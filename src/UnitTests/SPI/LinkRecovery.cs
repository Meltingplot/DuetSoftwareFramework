using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DuetControlServer;
using DuetControlServer.Files;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using SpiAdapter = DuetControlServer.Link.Adapter.SPI;

namespace UnitTests.SPI;

/// <summary>
/// A single corrupted SPI exchange at every state of a transfer and in every field must not break the link.
/// It has to recover through the retries of the protocol, because a reconnect of either side or a restart of DCS stops a running print
/// </summary>
[TestFixture]
public class LinkRecovery
{
    /// <summary>
    /// Transfer that gets the corrupted exchange
    /// </summary>
    public enum Variant
    {
        /// <summary>
        /// First transfer, carrying data from RRF
        /// </summary>
        Connect,

        /// <summary>
        /// Regular transfer with data in both directions
        /// </summary>
        BothData,

        /// <summary>
        /// Regular transfer with data from RRF only
        /// </summary>
        RrfData,

        /// <summary>
        /// Regular transfer with data from DSF only
        /// </summary>
        DsfData,

        /// <summary>
        /// Regular transfer without data, so there are only the header exchanges
        /// </summary>
        NoData
    }

    /// <summary>
    /// How the link dealt with the fault
    /// </summary>
    public enum Outcome
    {
        /// <summary>
        /// Recovered through the retries of the protocol within the time limit
        /// </summary>
        Retry,

        /// <summary>
        /// Recovered through the retries of the protocol, but later than the time limit
        /// </summary>
        Slow,

        /// <summary>
        /// Recovered only through a reconnect of DSF or RRF, which stops a running print
        /// </summary>
        Reconnect,

        /// <summary>
        /// The first transfer failed, so DCS exits and connects again once systemd has restarted it, which stops a running print
        /// </summary>
        DcsRestart,

        /// <summary>
        /// DCS throws an error that stops it
        /// </summary>
        Fatal,

        /// <summary>
        /// The link does not deliver clean transfers again
        /// </summary>
        NoRecovery,

        /// <summary>
        /// A payload was delivered twice although neither side reconnected
        /// </summary>
        Duplicate,

        /// <summary>
        /// A payload was lost although neither side reconnected
        /// </summary>
        SilentLoss
    }

    private static bool IsFailure(Outcome outcome) => outcome != Outcome.Retry;

    /// <summary>
    /// Single fault and where it hits
    /// </summary>
    private sealed record Case(Variant Variant, string Phase, ExchangeDirection Direction, string Field, string Mutation, SpiFault Fault)
    {
        public override string ToString() => $"{Variant} / {Phase} / {Direction} / {Field} / {Mutation}";
    }

    /// <summary>
    /// Result of a scenario
    /// </summary>
    private sealed record Result(Outcome Outcome, bool RrfEmergencyStop, string Details, bool Duplicate = false)
    {
        public string Label => Outcome + (RrfEmergencyStop ? " (RRF e-stop)" : "") + (Duplicate ? " (duplicate)" : "");
    }

    /// <summary>
    /// RRF transfer that carries the corrupted exchange: 1 is the connect transfer, 2 and 3 are clean transfers before the target
    /// </summary>
    private const int TargetTransfer = 4;

    /// <summary>
    /// Number of clean transfers in a row that count as recovered
    /// </summary>
    private const int RecoveredTransfers = 3;

    /// <summary>
    /// Maximum time from the fault to the first of the clean transfers (R2 in design/sbc-link-requirements.md: half the shortest motion lookahead)
    /// </summary>
    private const double MaxRecoveryTime = 20;

    /// <summary>
    /// Number of times DCS is started again when it cannot connect, like systemd does
    /// </summary>
    private const int MaxDcsRestarts = 5;

    /// <summary>
    /// Time until systemd starts DCS again (RestartSec in pkg/common/duetcontrolserver/usr/lib/systemd/system/duetcontrolserver.service)
    /// </summary>
    private const double DcsRestartDelay = 5000;

    /// <summary>
    /// Maximum number of transfers after the fault
    /// </summary>
    private const int MaxTransfersAfterFault = 15;

    private ServiceProvider _provider = null!;
    private EventLogger _eventLogger = null!;
    private ObjectModel _model = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        ServiceCollection services = new();
        services
            .AddLogging()
            .AddSingleton(Options.Create(new Settings()))
            .AddSingleton<IHostApplicationLifetime, ApplicationLifetime>()
            .AddFiles()
            .AddModel()
            .AddUtility();
        _provider = services.BuildServiceProvider();
        _eventLogger = _provider.GetRequiredService<EventLogger>();
        _model = _provider.GetRequiredService<ObjectModel>();
    }

    [OneTimeTearDown]
    public void TearDown() => _provider.Dispose();

    [Test]
    public void CleanTransfers()
    {
        foreach (Variant variant in Enum.GetValues<Variant>())
        {
            Result result = Run(variant, null, new());
            Assert.That(result.Outcome, Is.EqualTo(Outcome.Retry), $"{variant}: {result.Details}");
        }
    }

    [Test, Explicit("32 of 4238 single faults still break the link with the RRF of today")]
    public void SingleCorruptedExchange() => RunCensus(new());

    private static IEnumerable<TestCaseData> RefusalCases => GetCases()
        .Where(c => c.Phase == "HeaderResponse" && c.Direction == ExchangeDirection.ToDsf && c.Mutation is "flip bit 1" or "plus one" or "BadFormat" or "BadProtocolVersion" or "BadDataLength")
        .Select(c => new TestCaseData(c.ToString()).SetName($"RefusalCodeInHeaderResponse({c.Variant}, {c.Mutation})"));

    [TestCaseSource(nameof(RefusalCases))]
    public void RefusalCodeInHeaderResponse(string name)
    {
        // A single corrupted Success reads as BadProtocolVersion (bit 1) or BadFormat (plus one), so DCS must retry instead of giving up
        Case c = GetCases().Single(c => c.ToString() == name);
        Result result = Run(c.Variant, c.Fault, new());
        Assert.That(result.Outcome, Is.EqualTo(Outcome.Retry), result.Details);
    }

    /// <summary>
    /// Settings of the firmware end that are not known for sure or that are being considered
    /// </summary>
    public sealed record RrfOptions(SequenceMismatchHandling SequenceMismatchHandling = SequenceMismatchHandling.ConnectionReset, bool PollWhileDisconnected = false,
        bool RrfFirstOnTie = true, byte UnderrunByte = 0, bool PhaseGuard = false, bool SequenceGuard = false);

    private static IEnumerable<TestCaseData> OtherOptions =>
    [
        new TestCaseData(new RrfOptions(RrfFirstOnTie: false)).SetName("SingleCorruptedExchange_DsfFirstOnTie"),
        new TestCaseData(new RrfOptions(PollWhileDisconnected: true)).SetName("SingleCorruptedExchange_PollWhileDisconnected"),
        new TestCaseData(new RrfOptions(UnderrunByte: 0xFF)).SetName("SingleCorruptedExchange_UnderrunByteFF"),
        new TestCaseData(new RrfOptions(SequenceMismatchHandling.Ignore)).SetName("SingleCorruptedExchange_IgnoreSequenceMismatch"),
        new TestCaseData(new RrfOptions(PhaseGuard: true)).SetName("SingleCorruptedExchange_PhaseGuard"),
        new TestCaseData(new RrfOptions(SequenceGuard: true)).SetName("SingleCorruptedExchange_SequenceGuard"),
        new TestCaseData(new RrfOptions(PhaseGuard: true, SequenceGuard: true)).SetName("SingleCorruptedExchange_PhaseAndSequenceGuard")
    ];

    [TestCaseSource(nameof(OtherOptions)), Explicit("Firmware variants and proposals, run on demand")]
    public void SingleCorruptedExchangeWith(RrfOptions options) => RunCensus(options);

    /// <summary>
    /// Run every single fault and fail with a list of those the link does not recover from
    /// </summary>
    private void RunCensus(RrfOptions options)
    {
        List<(Case Case, Result Result)> results = [.. GetCases().Select(c => (c, Run(c.Variant, c.Fault, options)))];

        // Census of all outcomes
        StringBuilder census = new();
        census.AppendLine($"{results.Count} single faults, RRF options {options}");
        foreach (var group in results.GroupBy(r => (r.Case.Variant, r.Case.Phase, r.Case.Direction)))
        {
            census.AppendLine($"{group.Key.Variant} / {group.Key.Phase} / {group.Key.Direction}: " +
                string.Join(", ", group.GroupBy(r => r.Result.Label).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
        }

        // Single-bit errors are the only faults with a meaningful probability: every bit of an exchange is one case
        census.AppendLine("Single-bit errors that break the link:");
        foreach (var group in results.Where(r => r.Case.Mutation.StartsWith("flip bit")).GroupBy(r => (r.Case.Variant, r.Case.Phase, r.Case.Direction)))
        {
            int failed = group.Count(r => IsFailure(r.Result.Outcome));
            census.AppendLine($"{group.Key.Variant} / {group.Key.Phase} / {group.Key.Direction}: {failed} of {group.Count()} bits" +
                (failed > 0 ? $" ({string.Join(", ", group.Where(r => IsFailure(r.Result.Outcome)).Select(r => $"{r.Case.Mutation[5..]} {r.Result.Outcome}"))})" : ""));
        }
        TestContext.Out.WriteLine(census.ToString());

        // Failures grouped by field and outcome, with a few example mutations each
        List<(Case Case, Result Result)> failures = [.. results.Where(r => IsFailure(r.Result.Outcome))];
        if (failures.Count > 0)
        {
            StringBuilder report = new();
            report.AppendLine($"{failures.Count} of {results.Count} single faults break the link:");
            foreach (var group in failures.GroupBy(r => (r.Case.Variant, r.Case.Phase, r.Case.Direction, r.Case.Field, r.Result.Outcome)))
            {
                report.AppendLine($"- {group.Key.Variant} / {group.Key.Phase} / {group.Key.Direction} / {group.Key.Field}: {group.Key.Outcome} for {group.Count()} " +
                    $"(e.g. {string.Join("; ", group.Take(3).Select(r => $"{r.Case.Mutation}: {r.Result.Details}"))})");
            }
            Assert.Fail(report.ToString());
        }
    }

    /// <summary>
    /// Print the exchanges of a single case, for analysing a finding
    /// </summary>
    [TestCase("BothData / HeaderResponse / ToDsf / Response / BadHeaderChecksum")]
    [TestCase("BothData / Header / ToDsf / Exchange / BadResponse in first word")]
    [TestCase("BothData / DataResponse / ToRrf / Response / BadDataChecksum")]
    [TestCase("Connect / Data / ToRrf / Exchange / BadResponse in first word")]
    [TestCase("Connect / DataResponse / ToDsf / Response / BadDataChecksum", SequenceMismatchHandling.Ignore)]
    [TestCase("NoData / HeaderResponse / ToRrf / Response / flip bit 0")]
    [TestCase("BothData / DataResponse / ToDsf / Response / BadDataChecksum")]
    [TestCase("BothData / Data / ToRrf / Exchange / BadResponse in first word")]
    [TestCase("DsfData / HeaderResponse / ToRrf / Response / BadHeaderChecksum")]
    [TestCase("BothData / Data / ToRrf / Exchange / BadResponse in first word", SequenceMismatchHandling.ConnectionReset, true)]
    [TestCase("Connect / Header / ToDsf / Exchange / BadResponse in first word", SequenceMismatchHandling.ConnectionReset, true)]
    [Explicit("Analysis")]
    public void TraceCase(string name, SequenceMismatchHandling sequenceMismatchHandling = SequenceMismatchHandling.ConnectionReset, bool guards = false)
    {
        Case c = GetCases().Single(c => c.ToString() == name);
        List<string> trace = [];
        Result result = Run(c.Variant, c.Fault, new(sequenceMismatchHandling, PhaseGuard: guards, SequenceGuard: guards), trace);
        TestContext.Out.WriteLine($"{c}: {result}");
        foreach (string line in trace.Take(80))
        {
            TestContext.Out.WriteLine(line);
        }
    }

    /// <summary>
    /// Replay every case against the real DataTransfer and SbcInterface of RRF, built as a host program whose path is in RRF_HOST_HARNESS,
    /// and compare its answers with those of the port. Inputs are replayed open-loop, so only the first difference of a case counts
    /// </summary>
    [Test, Explicit("Needs the host build of RRF's DataTransfer and SbcInterface")]
    public void PortMatchesRealFirmware()
    {
        string? harness = Environment.GetEnvironmentVariable("RRF_HOST_HARNESS");
        if (string.IsNullOrEmpty(harness))
        {
            Assert.Ignore("RRF_HOST_HARNESS is not set");
        }

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(harness!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;

        List<string> differences = [];
        int cases = 0, events = 0;
        foreach ((Variant variant, SpiFault? fault, string name) in Enum.GetValues<Variant>().Select(v => (v, (SpiFault?)null, $"{v} / clean")).Concat(GetCases().Select(c => (c.Variant, (SpiFault?)c.Fault, c.ToString()))))
        {
            List<(string Input, string? Output)> replay = [];
            Run(variant, fault, new(PollWhileDisconnected: Environment.GetEnvironmentVariable("RRF_HOST_POLL") == "1"), replay: replay);
            cases++;
            for (int i = 0; i < replay.Count; i++)
            {
                (string input, string? expected) = replay[i];
                process.StandardInput.WriteLine(input);
                process.StandardInput.Flush();
                if (expected is not null)
                {
                    events++;
                    string? actual = process.StandardOutput.ReadLine();
                    if (actual != expected)
                    {
                        differences.Add($"{name}, event {i} ({input}):\n  port: {expected}\n  RRF:  {actual}");
                        break;
                    }
                }
            }
            process.StandardInput.WriteLine("END");
        }
        process.StandardInput.Close();
        process.WaitForExit();

        TestContext.Out.WriteLine($"{cases} cases, {events} events compared, {differences.Count} cases differ");
        Assert.That(differences, Is.Empty, string.Join("\n", differences.Take(20)));
    }

    #region Faults
    private static readonly (string Name, int Offset, int Size)[] HeaderFields =
    [
        ("FormatCode", 0, 1), ("NumPackets", 1, 1), ("ProtocolVersion", 2, 2), ("SequenceNumber", 4, 2), ("DataLength", 6, 2), ("ChecksumData32", 8, 4), ("ChecksumHeader32", 12, 4)
    ];

    private static readonly (string Name, int Offset, int Size)[] ResponseFields = [("Response", 0, 4)];

    /// <summary>
    /// Fields of a message packet as both sides write them
    /// </summary>
    private static readonly (string Name, int Offset, int Size)[] DataFields =
    [
        ("Packet.Request", 0, 2), ("Packet.Id", 2, 2), ("Packet.Length", 4, 2), ("Packet.ResendPacketId", 6, 2), ("Message.Type", 8, 4), ("Message.Length", 12, 2), ("Message.Padding", 14, 2)
    ];

    private static readonly (string Name, uint Value)[] ResponseCodes =
    [
        ("Success", TransferResponse.Success), ("BadFormat", TransferResponse.BadFormat), ("BadProtocolVersion", TransferResponse.BadProtocolVersion),
        ("BadDataLength", TransferResponse.BadDataLength), ("BadHeaderChecksum", TransferResponse.BadHeaderChecksum), ("BadDataChecksum", TransferResponse.BadDataChecksum),
        ("BadResponse", TransferResponse.BadResponse), ("HighPin", TransferResponse.HighPin), ("Zero", 0)
    ];

    /// <summary>
    /// Get every single fault: each bit of each exchange of the target transfer, special values of each field and whole-exchange patterns, in both directions
    /// </summary>
    private static IEnumerable<Case> GetCases()
    {
        foreach (Variant variant in Enum.GetValues<Variant>())
        {
            List<(string Phase, int Size, (string Name, int Offset, int Size)[] Fields)> exchanges =
            [
                ("Header", 16, HeaderFields),
                ("HeaderResponse", 4, ResponseFields)
            ];
            if (variant != Variant.NoData)
            {
                exchanges.Add(("Data", DataSize, DataFields));
                exchanges.Add(("DataResponse", 4, ResponseFields));
            }

            for (int exchange = 0; exchange < exchanges.Count; exchange++)
            {
                (string phase, int size, var fields) = exchanges[exchange];
                foreach (ExchangeDirection direction in Enum.GetValues<ExchangeDirection>())
                {
                    Case Make(string field, string mutation, SpiMutation mutate) => new(variant, phase, direction, field, mutation, new SpiFault(exchange, direction, mutate));

                    // Every bit, named after the field it is in
                    for (int bit = 0; bit < size * 8; bit++)
                    {
                        int b = bit;
                        string field = fields.FirstOrDefault(f => b / 8 >= f.Offset && b / 8 < f.Offset + f.Size).Name ?? "Payload";
                        yield return Make(field, $"flip bit {bit}", data => { if (b / 8 < data.Length) data[b / 8] ^= (byte)(1 << (b % 8)); });
                    }

                    // Special values of each field
                    foreach ((string name, int offset, int fieldSize) in fields)
                    {
                        yield return Make(name, "zero", data => Field(data, offset, fieldSize).Clear());
                        yield return Make(name, "all ones", data => Field(data, offset, fieldSize).Fill(0xFF));
                        yield return Make(name, "plus one", data => AddToField(Field(data, offset, fieldSize), 1));
                        yield return Make(name, "minus one", data => AddToField(Field(data, offset, fieldSize), -1));
                    }
                    if (fields == ResponseFields)
                    {
                        foreach ((string name, uint value) in ResponseCodes)
                        {
                            yield return Make("Response", name, data => BitConverter.TryWriteBytes(data, value));
                        }
                    }

                    // Whole exchange
                    yield return Make("Exchange", "all 0x00", data => data.Clear());
                    yield return Make("Exchange", "all 0xFF", data => data.Fill(0xFF));
                    yield return Make("Exchange", "BadResponse in first word", data => BitConverter.TryWriteBytes(data, TransferResponse.BadResponse));
                }
            }
        }
    }

    /// <summary>
    /// Size of the data exchange: a message packet of either side, see FakeRrfSpi.WritePayload and Writer.WriteMessage
    /// </summary>
    private const int DataSize = 24;

    /// <summary>
    /// Get a field of an exchange, or the part of it the exchange holds
    /// </summary>
    private static Span<byte> Field(Span<byte> data, int offset, int size) => (offset < data.Length) ? data[offset..Math.Min(offset + size, data.Length)] : [];

    private static void AddToField(Span<byte> field, int delta)
    {
        ulong value = 0;
        for (int i = field.Length - 1; i >= 0; i--)
        {
            value = (value << 8) | field[i];
        }
        value = unchecked(value + (ulong)delta);
        for (int i = 0; i < field.Length; i++)
        {
            field[i] = (byte)(value >> (8 * i));
        }
    }
    #endregion

    #region Scenario
    private sealed class BudgetExceededException : Exception;

    /// <summary>
    /// Connect, perform clean transfers, inject the fault into the target transfer and check whether clean transfers follow
    /// </summary>
    private Result Run(Variant variant, SpiFault? fault, RrfOptions options, List<string>? trace = null, List<(string Input, string? Output)>? replay = null)
    {
        int target = (variant == Variant.Connect) ? 1 : TargetTransfer;
        bool rrfDataInTarget = variant is Variant.Connect or Variant.BothData or Variant.RrfData;
        bool dsfDataInTarget = variant is Variant.BothData or Variant.DsfData;
        replay?.Add(($"NEW {(rrfDataInTarget ? 0 : target)} {options.UnderrunByte:X2}", null));
        FakeRrfSpi rrf = new(n => n != target || rrfDataInTarget, options.SequenceMismatchHandling, options.PollWhileDisconnected, options.RrfFirstOnTie, options.UnderrunByte, options.PhaseGuard, options.SequenceGuard)
        {
            Trace = trace,
            Replay = replay
        };

        int dsfConnectionLosses = 0, dcsRestarts = 0, dsfId = 0, dsfResets = 0;
        double? recoveredAt = null;
        void OnConnectionLost(object? sender, EventArgs e) => dsfConnectionLosses++;
        List<string> dsfSent = [], dsfReceived = [];
        SpiAdapter dsf = CreateAdapter(rrf);

        // Perform a full transfer and read the payloads RRF sent. Returns whether the transfer was clean
        bool Transfer(bool dsfData, bool connecting = false)
        {
            if (dsfData)
            {
                string payload = $"dsf:{++dsfId}";
                if (dsf.WriteMessage(MessageTypeFlags.UsbMessage, payload))
                {
                    dsfSent.Add(payload);
                }
            }

            int lossesBefore = dsfConnectionLosses, rrfResetsBefore = rrf.ConnectionResets;
            dsf.PerformFullTransfer(connecting, rrf.Budget.Token);
            if (rrf.Budget.IsCancellationRequested)
            {
                throw new BudgetExceededException();
            }
            bool hadReset = dsf.HadReset();
            if (hadReset)
            {
                dsfResets++;
            }

            for (int i = 0; i < dsf.PacketsToRead; i++)
            {
                PacketHeader? packet = dsf.ReadNextPacket();
                if (packet is null)
                {
                    break;
                }
                if (packet.Value.Request == (ushort)FirmwareRequest.Message)
                {
                    dsf.ReadMessage(out _, out string text);
                    dsfReceived.Add(text);
                }
            }
            return !hadReset && lossesBefore == dsfConnectionLosses && rrfResetsBefore == rrf.ConnectionResets && dsf.ProtocolVersion == Consts.ProtocolVersion;
        }

        _model.OnConnectionLost += OnConnectionLost;
        try
        {
            // Connect. If that fails, DCS exits and systemd starts it again
            if (variant == Variant.Connect && fault is not null)
            {
                rrf.Arm(fault);
            }
            bool targetClean = false;
            while (true)
            {
                try
                {
                    targetClean = Transfer(false, true);
                    break;
                }
                catch (Exception e) when (!rrf.Budget.IsCancellationRequested)
                {
                    if (++dcsRestarts > MaxDcsRestarts)
                    {
                        return new(Outcome.NoRecovery, rrf.Disconnects > 0, $"DCS cannot connect again after {MaxDcsRestarts} restarts ({e.Message})");
                    }
                    rrf.Wait(DcsRestartDelay);
                    dsf = CreateAdapter(rrf);
                }
            }

            // Clean transfers, then the target transfer
            if (variant != Variant.Connect)
            {
                Transfer(true);
                Transfer(true);
                if (fault is not null)
                {
                    rrf.Arm(fault);
                }
                targetClean = Transfer(dsfDataInTarget);
            }

            // The link must deliver clean transfers again. The transfer that got the fault counts if it completed cleanly through retries
            int cleanInARow = targetClean ? 1 : 0;
            recoveredAt = targetClean ? rrf.Now : null;
            for (int i = 0; i < MaxTransfersAfterFault && cleanInARow < RecoveredTransfers; i++)
            {
                if (Transfer(true))
                {
                    if (cleanInARow++ == 0)
                    {
                        recoveredAt = rrf.Now;
                    }
                }
                else
                {
                    cleanInARow = 0;
                }
            }
            if (cleanInARow < RecoveredTransfers)
            {
                return new(Outcome.NoRecovery, rrf.Disconnects > 0, $"no {RecoveredTransfers} clean transfers in a row within {MaxTransfersAfterFault}");
            }
        }
        catch (BudgetExceededException)
        {
            return new(Outcome.NoRecovery, rrf.Disconnects > 0, $"no clean transfer within {rrf.Now:F0} ms and {rrf.Exchanges} exchanges");
        }
        catch (OperationCanceledException) when (rrf.Budget.IsCancellationRequested)
        {
            return new(Outcome.NoRecovery, rrf.Disconnects > 0, $"no clean transfer within {rrf.Now:F0} ms and {rrf.Exchanges} exchanges");
        }
        catch (Exception e)
        {
            return new(Outcome.Fatal, rrf.Disconnects > 0, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            _model.OnConnectionLost -= OnConnectionLost;
        }

        if (fault is not null && !rrf.FaultApplied)
        {
            return new(Outcome.NoRecovery, false, "the fault was never applied, the scenario does not reach the exchange");
        }

        // Every payload at most once and in order, and without a reconnect nothing may be lost
        bool reconnected = dsfConnectionLosses > 0 || dsfResets > 0 || rrf.ConnectionResets > 0 || dcsRestarts > 0;
        string details = $"DSF lost connection {dsfConnectionLosses}x, DSF reset {dsfResets}x, RRF reset {rrf.ConnectionResets}x, RRF disconnect {rrf.Disconnects}x, DCS restart {dcsRestarts}x";
        bool duplicate = HasDuplicate(dsfReceived) || HasDuplicate(rrf.ReceivedPayloads);
        if (duplicate && !reconnected)
        {
            return new(Outcome.Duplicate, rrf.Disconnects > 0, $"DSF got [{string.Join(", ", dsfReceived)}] of [{string.Join(", ", rrf.SentPayloads)}], RRF got [{string.Join(", ", rrf.ReceivedPayloads)}] of [{string.Join(", ", dsfSent)}]; {details}");
        }
        if (!reconnected && (!dsfReceived.SequenceEqual(rrf.SentPayloads.Take(dsfReceived.Count)) || dsfReceived.Count < rrf.SentPayloads.Count - 1 || !rrf.ReceivedPayloads.SequenceEqual(dsfSent)))
        {
            return new(Outcome.SilentLoss, rrf.Disconnects > 0, $"DSF got [{string.Join(", ", dsfReceived)}] of [{string.Join(", ", rrf.SentPayloads)}], RRF got [{string.Join(", ", rrf.ReceivedPayloads)}] of [{string.Join(", ", dsfSent)}]");
        }
        if (dcsRestarts > 0 || reconnected)
        {
            return new(dcsRestarts > 0 ? Outcome.DcsRestart : Outcome.Reconnect, rrf.Disconnects > 0, details, duplicate);
        }

        // The repair must not take longer than the limit
        double recoveryTime = (fault is null || recoveredAt is null) ? 0 : recoveredAt.Value - rrf.FaultTime;
        return (recoveryTime > MaxRecoveryTime)
            ? new(Outcome.Slow, false, $"recovered after {recoveryTime:F1} ms")
            : new(Outcome.Retry, false, $"recovered after {recoveryTime:F2} ms");
    }

    private static bool HasDuplicate(List<string> payloads) => payloads.Count != payloads.Distinct().Count();

    private SpiAdapter CreateAdapter(FakeRrfSpi rrf) => new(_eventLogger, _model, NullLogger<SpiAdapter>.Instance, Options.Create(new Settings()), rrf, rrf);
    #endregion
}
