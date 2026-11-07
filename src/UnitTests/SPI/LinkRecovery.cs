using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DuetControlServer;
using DuetControlServer.Files;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using DuetSharedLibrary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
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
        SilentLoss,

        /// <summary>
        /// RRF restarted and DSF detected it (R6), so it discards pending work
        /// </summary>
        ResetDetected,

        /// <summary>
        /// RRF restarted but DSF did not detect it
        /// </summary>
        ResetMissed
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
    /// Transfer in which DSF requests the capabilities RRF offers. RRF uses the extended link from the next transfer on, DSF from the one after
    /// </summary>
    private const int ActivationTransfer = 2;

    /// <summary>
    /// First transfer in which both sides use the extended link
    /// </summary>
    private const int ExtendedTransfer = ActivationTransfer + 2;

    /// <summary>
    /// Target transfer at which the regular sequence numbers of RRF wrap long after the activation: with the restarts after seq 1 and after seq 65535,
    /// RRF numbers transfer 1 with 0xFE00 or 0xFE02, which leaves the activation in transfer 2 below LastSafeRegularNumber
    /// </summary>
    private const int RegularWrapTransfer = 0x10001 - 0xFE00;

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

    [Test, Explicit("35 of 4142 single faults still break the link with the RRF of today")]
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
        bool RrfFirstOnTie = true, byte UnderrunByte = 0, bool PhaseGuard = false, bool SequenceGuard = false,
        LinkCapabilities Capabilities = LinkCapabilities.None, int TargetTransfer = TargetTransfer, RrfRule DisabledRules = RrfRule.None, bool InjectThroughDsf = false,
        bool DcsRestartAfterRecovery = false, double? BootArmDelay = null, byte BootWindowMiso = 0x00, int? SbcRestartTimeout = null, bool InjectIntoActivation = false,
        bool HarnessBootDelay = false);

    private static IEnumerable<TestCaseData> OtherOptions =>
    [
        new TestCaseData(new RrfOptions(RrfFirstOnTie: false)).SetName("SingleCorruptedExchange_DsfFirstOnTie"),
        new TestCaseData(new RrfOptions(PollWhileDisconnected: true)).SetName("SingleCorruptedExchange_PollWhileDisconnected"),
        new TestCaseData(new RrfOptions(UnderrunByte: 0xFF)).SetName("SingleCorruptedExchange_UnderrunByteFF"),
        new TestCaseData(new RrfOptions(SequenceMismatchHandling.Ignore)).SetName("SingleCorruptedExchange_IgnoreSequenceMismatch"),
        new TestCaseData(new RrfOptions(PhaseGuard: true)).SetName("SingleCorruptedExchange_PhaseGuard"),
        new TestCaseData(new RrfOptions(SequenceGuard: true)).SetName("SingleCorruptedExchange_SequenceGuard"),
        new TestCaseData(new RrfOptions(PhaseGuard: true, SequenceGuard: true)).SetName("SingleCorruptedExchange_PhaseAndSequenceGuard"),
        new TestCaseData(new RrfOptions(PollWhileDisconnected: true, PhaseGuard: true, SequenceGuard: true)).SetName("SingleCorruptedExchange_PhaseAndSequenceGuardPolling"),
        new TestCaseData(new RrfOptions(Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2)).SetName("SingleCorruptedExchange_Extended"),
        new TestCaseData(new RrfOptions(PollWhileDisconnected: true, Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2)).SetName("SingleCorruptedExchange_ExtendedPolling"),
        new TestCaseData(new RrfOptions(Capabilities: LinkCapabilities.ExtendedHeader, TargetTransfer: ExtendedTransfer + 2)).SetName("SingleCorruptedExchange_ExtendedWithoutGuards"),
        new TestCaseData(new RrfOptions(Capabilities: LinkCapabilities.ExtendedHeader | LinkCapabilities.SequenceGuard, TargetTransfer: ExtendedTransfer + 2)).SetName("SingleCorruptedExchange_ExtendedWithSequenceGuard"),
        new TestCaseData(new RrfOptions(Capabilities: LinkCapabilities.All, TargetTransfer: ActivationTransfer)).SetName("SingleCorruptedExchange_ExtendedActivationTransfer"),
        new TestCaseData(new RrfOptions(Capabilities: LinkCapabilities.All, TargetTransfer: ActivationTransfer + 1)).SetName("SingleCorruptedExchange_ExtendedRrfSwitchTransfer"),
        new TestCaseData(new RrfOptions(Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer)).SetName("SingleCorruptedExchange_ExtendedDsfSwitchTransfer")
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
    /// The fault injection build for the lab printer injects through the SPI device of DSF and has to recognise the exchanges on its own.
    /// Every single fault injected that way must have the same outcome as injected by the census in RRF
    /// </summary>
    [TestCase(false), TestCase(true)]
    public void InjectorMatchesCensus(bool extended)
    {
        RrfOptions inRrf = extended ? new(Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2) : new();
        RrfOptions throughDsf = inRrf with { InjectThroughDsf = true };
        List<string> differences = [.. GetCases()
            .Select(c => (Case: c, InRrf: Run(c.Variant, c.Fault, inRrf), ThroughDsf: Run(c.Variant, c.Fault, throughDsf)))
            .Where(r => r.InRrf.Label != r.ThroughDsf.Label)
            .Select(r => $"- {r.Case}: census {r.InRrf.Label}, injector {r.ThroughDsf.Label} ({r.ThroughDsf.Details})")];
        Assert.That(differences, Is.Empty, string.Join("\n", differences.Take(20)));
    }

    private static IEnumerable<TestCaseData> HardwareCaseOptions =>
    [
        new TestCaseData("rrf-of-today", new RrfOptions(PollWhileDisconnected: true)).SetName("WriteHardwareCases_RrfOfToday"),
        new TestCaseData("extended", new RrfOptions(PollWhileDisconnected: true, Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2)).SetName("WriteHardwareCases_Extended")
    ];

    /// <summary>
    /// Write the single faults for the fault injection build on the lab printer, with the outcome the census predicts for each kind of transfer.
    /// The lab printer hits whatever transfer of the requested kind comes next, so the connect transfer is left out.
    /// The file goes to the path in LAB_SPI_FAULT_CASES, or next to the test assembly
    /// </summary>
    /// <param name="name">Name of the configuration</param>
    /// <param name="options">Firmware end, as on the lab printer: its MB6HC polls while disconnected</param>
    [TestCaseSource(nameof(HardwareCaseOptions)), Explicit("Writes the cases for src/LabPrinterTests/SpiFaultInjection.cs")]
    public void WriteHardwareCases(string name, RrfOptions options)
    {
        static SpiTransferKind Kind(Variant variant) => variant switch
        {
            Variant.NoData => SpiTransferKind.NoData,
            Variant.BothData => SpiTransferKind.BothData,
            Variant.DsfData => SpiTransferKind.DsfData,
            _ => SpiTransferKind.RrfData
        };

        List<(Case Case, Result Result)> results = [.. GetCases().Where(c => c.Variant != Variant.Connect).Select(c => (c, Run(c.Variant, c.Fault, options)))];
        var cases = results.Select(r => new
        {
            Name = r.Case.ToString(),
            Spec = new SpiFaultSpec(Kind(r.Case.Variant), (SpiExchange)r.Case.Fault.Exchange, Enum.Parse<SpiFaultDirection>(r.Case.Fault.Direction.ToString()), r.Case.Fault.Mutation!).ToString(),
            Expected = r.Result.Outcome.ToString(),
            ExpectedByKind = results
                .Where(o => o.Case.Phase == r.Case.Phase && o.Case.Direction == r.Case.Direction && o.Case.Field == r.Case.Field && o.Case.Mutation == r.Case.Mutation)
                .ToDictionary(o => Kind(o.Case.Variant).ToString(), o => o.Result.Outcome.ToString())
        });

        string path = Environment.GetEnvironmentVariable("LAB_SPI_FAULT_CASES") is { Length: > 0 } file ? file : System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, $"spi-fault-cases-{name}.json");
        System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { Configuration = name, Options = options.ToString(), Cases = cases },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        TestContext.Out.WriteLine($"{results.Count} cases, {results.Count(r => r.Result.Outcome != Outcome.Retry)} predicted to break the link, written to {path}");
    }

    private static IEnumerable<TestCaseData> ExtendedOptions =>
    [
        new TestCaseData(ExtendedTransfer + 2, false).SetName("ExtendedLinkRecoversFromEverySingleFault"),
        new TestCaseData(ExtendedTransfer + 2, true).SetName("ExtendedLinkRecoversFromEverySingleFault_Polling"),
        new TestCaseData(ActivationTransfer + 1, false).SetName("ExtendedLinkRecoversFromEverySingleFault_RrfSwitchTransfer"),
        new TestCaseData(ExtendedTransfer, false).SetName("ExtendedLinkRecoversFromEverySingleFault_DsfSwitchTransfer")
    ];

    /// <summary>
    /// With every capability active, each single fault after the activation transfer recovers through retries within the time limit.
    /// The connect transfer comes before any activation, so it is left out
    /// </summary>
    [TestCaseSource(nameof(ExtendedOptions))]
    public void ExtendedLinkRecoversFromEverySingleFault(int target, bool poll)
    {
        RrfOptions options = new(PollWhileDisconnected: poll, Capabilities: LinkCapabilities.All, TargetTransfer: target);
        List<string> failures = [.. GetCases().Where(c => c.Variant != Variant.Connect)
            .Select(c => (Case: c, Result: Run(c.Variant, c.Fault, options)))
            .Where(r => r.Result.Outcome != Outcome.Retry)
            .Select(r => $"- {r.Case}: {r.Result.Outcome}, {r.Result.Details}")];
        Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(20)));
    }

    /// <summary>
    /// Restarts on the extended link: at every exchange, right after the extended counter wrapped, and right after the regular
    /// numbers RRF used before the switch would have wrapped, where a restart goes unnoticed on the regular link
    /// </summary>
    private static IEnumerable<Case> GetExtendedRestartCases() =>
        GetRestartCases(ExtendedTransfer + 2, true)
            .Concat(GetRestartCases(RegularWrapTransfer).Where(c => c.Mutation.StartsWith("RRF restart after seq")).Select(c => c with { Field = "RegularWrap" }));

    /// <summary>
    /// On the extended link, DSF notices every restart of RRF at once, also right after a wrap of either counter
    /// </summary>
    [TestCase(false), TestCase(true)]
    public void ExtendedLinkNoticesEveryRestart(bool poll)
    {
        List<string> failures = [.. GetExtendedRestartCases()
            .Select(c => (Case: c, Result: Run(c.Variant, c.Fault, ExtendedRestartOptions(c, poll, RrfRule.None))))
            .Where(r => r.Result.Outcome != Outcome.ResetDetected)
            .Select(r => $"- {r.Case}: {r.Result.Outcome}, {r.Result.Details}")];
        Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(20)));
    }

    /// <summary>
    /// RRF restarts after it has completed an exchange and before it sets up the next one. DSF takes the falling edge of DataTransfer::Init for
    /// its next exchange, reads 0 from the disabled SPI and, with data in the transfer, answers with an extended BadResponse. That reaches the first
    /// header of the booted RRF 10 ms later (SbcBootArmDelay). The restart must be noticed without losing the connection, except when RRF boots for
    /// longer than the transfer timeout of DSF
    /// </summary>
    [TestCase(false, true), TestCase(true, true), TestCase(false, false), TestCase(true, false)]
    public void RestartBeforeArmKeepsTheConnection(bool poll, bool extended)
    {
        List<string> failures = [.. GetRestartBeforeArmCases()
            .Select(c => (Case: c, Result: Run(c.Variant, c.Fault, extended ? ExtendedRestartOptions(c, poll, RrfRule.None) : new(PollWhileDisconnected: poll))))
            .Where(r => !KeepsConnectionThroughRestart(r.Case, r.Result))
            .Select(r => $"- {r.Case}: {r.Result.Label}, {r.Result.Details}")];
        Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(20)));
    }

    /// <summary>
    /// The restart of the lab printer on 2026-11-07 (design/open-findings.md). Without DecodeHeaderWordUnconnected, RRF answers the extended
    /// BadResponse in its first header with BadHeaderChecksum, DSF reads that as a header and both sides stay one exchange apart until DSF has
    /// used up its retries. DSF logs the same lines as the printer did
    /// </summary>
    [Test]
    public void RestartBeforeArmMatchesLabPrinter()
    {
        Case c = GetRestartBeforeArmCases().Single(c => c.ToString() == "BothData / Header / ToRrf / Restart / RRF restart before the next arm, boot 100 ms");
        List<string> trace = [];
        Result result = Run(c.Variant, c.Fault, ExtendedRestartOptions(c, false, RrfRule.DecodeHeaderWordUnconnected), trace);
        Assert.Multiple(() =>
        {
            Assert.That(trace, Has.One.Contains("Restarting full transfer because a bad header response was received (0x00000000)"));
            Assert.That(trace.Count(line => line.Contains("Bad header CRC16 (expected 0x0000, got 0x1760)")), Is.EqualTo(99));
            Assert.That(result.Details, Does.Contain("DSF lost connection 1x"));
        });
    }

    /// <summary>
    /// Whether DSF noticed a restart before the next arm without losing the connection, which it cannot keep if RRF boots for longer than its transfer timeout
    /// </summary>
    private static bool KeepsConnectionThroughRestart(Case c, Result result) =>
        result.Outcome == Outcome.ResetDetected && (c.Fault.RestartBootTime >= new Settings().SbcTransferTimeout || result.Details.Contains("DSF lost connection 0x"));

    /// <summary>
    /// With the fork's RRF, DSF notices a restart within 50 ms of the boot. Without the delay before the first header, DSF and the booted RRF
    /// waited for each other until a timeout of RRF after 500 ms or 4 s (lab printer, 2026-11-02). Restarts in the connect transfer have no
    /// connection to notice, and for a second restart the time is taken from the first boot, so both are left out
    /// </summary>
    [TestCase(false), TestCase(true)]
    public void ExtendedLinkNoticesEveryRebootQuickly(bool poll)
    {
        System.Text.RegularExpressions.Regex noticed = new(@"noticed \[([^,\]]*)");
        List<string> failures = [.. GetExtendedRestartCases()
            .Where(c => c.Variant != Variant.Connect && !c.Mutation.Contains("twice"))
            .Select(c => (Case: c, Delay: noticed.Match(Run(c.Variant, c.Fault, ExtendedRestartOptions(c, poll, RrfRule.None)).Details).Groups[1].Value))
            .Where(r => !double.TryParse(r.Delay, System.Globalization.CultureInfo.InvariantCulture, out double ms) || ms >= 50)
            .Select(r => $"- {r.Case}: noticed {r.Delay} ms after the boot")];
        Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(20)));
    }

    /// <summary>
    /// Random bit errors in both directions, far more than a real link has. With every capability active, DSF retries each transfer within
    /// its retry limit until it gets through, so the link neither resets nor loses or duplicates a message. With the former limit of 3,
    /// these runs reset in transfer 49 and 5
    /// </summary>
    /// <param name="bitErrorRate">Probability of each bit to flip</param>
    /// <param name="padding">Filler characters per message: 0 gives about 850 bits per transfer, 1000 about 17,000</param>
    [TestCase(1e-3, 0), TestCase(3e-5, 1000)]
    public void ExtendedLinkSurvivesBitErrors(double bitErrorRate, int padding)
    {
        Assert.That(RunWithBitErrors(LinkCapabilities.All, bitErrorRate, padding, BitErrorTransfers), Is.Null);
    }

    /// <summary>
    /// Number of transfers with bit errors in a run
    /// </summary>
    private const int BitErrorTransfers = 20000;

    /// <summary>
    /// Connect, activate the capabilities, then perform transfers with a message in both directions while bit errors hit every exchange
    /// </summary>
    /// <param name="capabilities">Capabilities RRF offers and DSF activates</param>
    /// <param name="bitErrorRate">Probability of each bit to flip</param>
    /// <param name="padding">Filler characters per message</param>
    /// <param name="transfers">Number of transfers with bit errors</param>
    /// <param name="maxSbcRetries">Retry limit of DSF, or null for the default</param>
    /// <returns>Why the link failed, or null if it did not</returns>
    private string? RunWithBitErrors(LinkCapabilities capabilities, double bitErrorRate, int padding, int transfers, int? maxSbcRetries = null)
    {
        FakeRrfSpi rrf = new(_ => true, capabilities: capabilities) { PayloadPadding = padding };
        SpiAdapter dsf = CreateSpiAdapter(rrf, null, maxSbcRetries: maxSbcRetries);
        List<string> dsfSent = [], dsfReceived = [];
        int dsfConnectionLosses = 0, dsfId = 0;
        void OnConnectionLost(object? sender, EventArgs e) => dsfConnectionLosses++;

        // Perform a full transfer and read the messages RRF sent. Returns why the link failed, or null
        string? Transfer(bool dsfData, bool connecting = false, bool activation = false)
        {
            // A transfer that does not complete within this budget is stuck
            rrf.ExchangeBudget = rrf.Exchanges + 5000;
            rrf.TimeBudget = rrf.Now + 10000;

            if (activation && !dsf.WriteActivateLinkCapabilities(capabilities))
            {
                return "DSF did not request the capabilities";
            }
            if (dsfData)
            {
                // The fake RRF records only the "dsf:N" part of a message
                string payload = $"dsf:{++dsfId}";
                if (dsf.WriteMessage(MessageTypeFlags.UsbMessage, payload + new string('y', padding)))
                {
                    dsfSent.Add(payload);
                }
            }

            int lossesBefore = dsfConnectionLosses, rrfResetsBefore = rrf.ConnectionResets;
            dsf.PerformFullTransfer(connecting, rrf.Budget.Token);
            if (rrf.Budget.IsCancellationRequested)
            {
                return $"no transfer within {rrf.Exchanges} exchanges";
            }
            bool hadReset = dsf.HadReset();
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
            return (dsfConnectionLosses > lossesBefore) ? "DSF lost the connection" : hadReset ? "DSF noticed a reset" :
                (rrf.ConnectionResets > rrfResetsBefore) ? "RRF reset the connection" : null;
        }

        // Every message must arrive once and in order. What arrived is dropped, so that long runs keep their memory
        string? CheckMessages(bool complete)
        {
            int rrfGot = rrf.ReceivedPayloads.Count;
            if (rrfGot > dsfSent.Count || !rrf.ReceivedPayloads.SequenceEqual(dsfSent.Take(rrfGot)) || (complete && rrfGot != dsfSent.Count))
            {
                return "messages of DSF lost or duplicated";
            }
            if (dsfReceived.Count > rrf.SentPayloads.Count || !dsfReceived.SequenceEqual(rrf.SentPayloads.Take(dsfReceived.Count)) ||
                (complete && dsfReceived.Count < rrf.SentPayloads.Count - 1))
            {
                return "messages of RRF lost or duplicated";
            }
            dsfSent.RemoveRange(0, rrfGot);
            rrf.ReceivedPayloads.Clear();
            rrf.SentPayloads.RemoveRange(0, dsfReceived.Count);
            dsfReceived.Clear();
            return null;
        }

        _model.OnConnectionLost += OnConnectionLost;
        try
        {
            string? failure = Transfer(false, true) ?? Transfer(true, activation: true) ?? Transfer(true) ?? Transfer(true);
            if (failure is not null || rrf.ActiveCapabilities != capabilities)
            {
                return $"no activation: {failure ?? rrf.ActiveCapabilities.ToString()}";
            }

            rrf.BitErrorRate = bitErrorRate;
            for (int transfer = 1; transfer <= transfers; transfer++)
            {
                failure = Transfer(true) ?? ((transfer % 1000 == 0) ? CheckMessages(false) : null);
                if (failure is not null)
                {
                    return $"transfer {transfer}: {failure}";
                }
            }

            // Let the last messages arrive
            rrf.BitErrorRate = 0;
            for (int transfer = 0; transfer < RecoveredTransfers; transfer++)
            {
                failure = Transfer(false);
                if (failure is not null)
                {
                    return $"after the bit errors: {failure}";
                }
            }
            return CheckMessages(true);
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
        finally
        {
            _model.OnConnectionLost -= OnConnectionLost;
        }
    }

    /// <summary>
    /// Switch off one rule of the proposed RRF changes at a time and list the single faults and restarts that then break the link,
    /// so that each rule is backed by the cases it is needed for
    /// </summary>
    [Test, Explicit("Analysis for the RRF changes")]
    public void RrfRuleAblation()
    {
        // The extended sequence numbers cannot be switched off alone, because DSF switches when it sees them. Their cases are the restarts
        // after a wrap of the regular numbers, which go unnoticed without the extended link
        foreach (LinkCapabilities capabilities in new[] { LinkCapabilities.None, LinkCapabilities.All })
        {
            List<string> missed = [.. GetExtendedRestartCases().Where(c => c.Field == "RegularWrap")
                .Select(c => (Case: c, Result: Run(c.Variant, c.Fault, new(Capabilities: capabilities, TargetTransfer: RegularWrapTransfer))))
                .Where(r => r.Result.Outcome != Outcome.ResetDetected)
                .Select(r => $"{r.Case}: {r.Result.Label}")];
            TestContext.Out.WriteLine($"Restarts after a regular wrap with capabilities {capabilities}: {missed.Count} fail");
            foreach (string failure in missed)
            {
                TestContext.Out.WriteLine($"  {failure}");
            }
        }

        // How often each rule takes effect with every rule active
        Dictionary<RrfRule, int> hits = [];
        foreach (int target in new[] { ExtendedTransfer + 2, ActivationTransfer + 1, ExtendedTransfer })
        {
            foreach (Case c in GetCases().Where(c => c.Variant != Variant.Connect))
            {
                Run(c.Variant, c.Fault, new(Capabilities: LinkCapabilities.All, TargetTransfer: target), ruleHits: hits);
            }
        }
        foreach (bool poll in new[] { false, true })
        {
            foreach (Case c in GetExtendedRestartCases().Concat(GetRestartBeforeArmCases()))
            {
                Run(c.Variant, c.Fault, ExtendedRestartOptions(c, poll, RrfRule.None), ruleHits: hits);
            }
            foreach (Case c in GetDcsRestartCases())
            {
                Run(c.Variant, c.Fault, new(PollWhileDisconnected: poll, Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2), ruleHits: hits);
            }
        }
        TestContext.Out.WriteLine("Rules taking effect with every rule active: " + string.Join(", ", Enum.GetValues<RrfRule>().Skip(1).Select(rule => $"{rule} {hits.GetValueOrDefault(rule)}")));

        foreach (RrfRule rule in Enum.GetValues<RrfRule>())
        {
            List<string> failures = [];
            foreach (int target in new[] { ExtendedTransfer + 2, ActivationTransfer + 1, ExtendedTransfer })
            {
                RrfOptions options = new(Capabilities: LinkCapabilities.All, TargetTransfer: target, DisabledRules: rule);
                failures.AddRange(GetCases().Where(c => c.Variant != Variant.Connect)
                    .Select(c => (Case: c, Result: Run(c.Variant, c.Fault, options)))
                    .Where(r => r.Result.Outcome != Outcome.Retry)
                    .Select(r => $"single fault in transfer {target}: {r.Case}: {r.Result.Label}"));
            }
            foreach (bool poll in new[] { false, true })
            {
                failures.AddRange(GetExtendedRestartCases()
                    .Select(c => (Case: c, Result: Run(c.Variant, c.Fault, ExtendedRestartOptions(c, poll, rule))))
                    .Where(r => r.Result.Outcome != Outcome.ResetDetected)
                    .Select(r => $"restart{(poll ? ", polling" : "")}: {r.Case}: {r.Result.Label}"));
                failures.AddRange(GetRestartBeforeArmCases()
                    .Select(c => (Case: c, Result: Run(c.Variant, c.Fault, ExtendedRestartOptions(c, poll, rule))))
                    .Where(r => !KeepsConnectionThroughRestart(r.Case, r.Result))
                    .Select(r => $"restart before the next arm{(poll ? ", polling" : "")}: {r.Case}: {r.Result.Label}"));
                RrfOptions dcsOptions = new(PollWhileDisconnected: poll, Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2, DisabledRules: rule);
                failures.AddRange(GetDcsRestartCases()
                    .Select(c => (Case: c, Result: Run(c.Variant, c.Fault, dcsOptions)))
                    .Where(r => r.Result.Outcome != Outcome.ResetDetected)
                    .Select(r => $"DCS restart{(poll ? ", polling" : "")}: {r.Case}: {r.Result.Label}"));
            }

            TestContext.Out.WriteLine($"{rule} off: {failures.Count} cases fail");
            foreach (string failure in failures)
            {
                TestContext.Out.WriteLine($"  {failure}");
            }
        }
    }

    private static RrfOptions ExtendedRestartOptions(Case c, bool poll, RrfRule disabledRules) =>
        new(PollWhileDisconnected: poll, Capabilities: LinkCapabilities.All, TargetTransfer: (c.Field == "RegularWrap") ? RegularWrapTransfer : ExtendedTransfer + 2, DisabledRules: disabledRules);

    /// <summary>
    /// Get every restart of DCS: it dies right before each exchange of the target transfer, and a new instance starts after a short time,
    /// after a time just under the connection timeout of RRF, or when systemd starts it again
    /// </summary>
    private static IEnumerable<Case> GetDcsRestartCases() =>
        GetExchanges().Where(e => e.Variant != Variant.Connect).SelectMany(e => DcsRestartDelays.Select(delay =>
            new Case(e.Variant, e.Phase, ExchangeDirection.ToRrf, "DcsRestart", $"DCS restart after {delay} ms", new SpiFault(e.Exchange, ExchangeDirection.ToRrf, _ => { }, DcsRestartDelay: delay))));

    private static readonly double[] DcsRestartDelays = [100, 2000, DcsRestartDelay];

    /// <summary>
    /// A fault in the transfer that activates the link capabilities can leave both sides answering each other out of step, because the guards
    /// only start with that transfer. DSF keeps exchanging, so no timeout of RRF ends that. DCS stops after SbcRestartTimeout without a complete
    /// transfer, RRF times out meanwhile, and systemd starts a new instance. The census uses 200 ms instead of the default of 60 s
    /// </summary>
    [Test]
    public void StuckLinkRestartsDcs()
    {
        RrfOptions options = new(Capabilities: LinkCapabilities.All, TargetTransfer: ActivationTransfer, SbcRestartTimeout: 200);
        List<(Case Case, Result Result)> results = [.. GetCases().Where(c => c.Variant != Variant.Connect).Select(c => (c, Run(c.Variant, c.Fault, options)))];
        TestContext.Out.WriteLine(string.Join(", ", results.GroupBy(r => r.Result.Outcome).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
        List<string> stuck = [.. results.Where(r => r.Result.Outcome == Outcome.NoRecovery).Select(r => $"- {r.Case}: {r.Result.Details}")];
        Assert.That(stuck, Is.Empty, $"{stuck.Count} faults leave the link stuck:\n" + string.Join("\n", stuck.Take(20)));
    }

    /// <summary>
    /// The fault of the lab test SpiFaultRestarts.StuckLinkRestartsDcs, through the injector and into the transfer that activates the capabilities.
    /// Keep it in line with the lab test. A transfer with DSF data is the only kind the activation request can be in
    /// </summary>
    /// <param name="variant">Kind of the transfer that activates</param>
    /// <param name="poll">Whether RRF polls while disconnected, as the MB6HC of the lab printer does</param>
    [TestCase(Variant.BothData, false), TestCase(Variant.BothData, true), TestCase(Variant.DsfData, false), TestCase(Variant.DsfData, true)]
    public void LabFaultInActivationRestartsDcs(Variant variant, bool poll)
    {
        RrfOptions options = new(PollWhileDisconnected: poll, Capabilities: LinkCapabilities.All, TargetTransfer: ActivationTransfer, SbcRestartTimeout: 200, InjectThroughDsf: true, InjectIntoActivation: true);
        Case fault = GetCases().First(c => c.Variant == variant && (SpiExchange)c.Fault.Exchange == SpiExchange.DataResponse && c.Fault.Direction == ExchangeDirection.ToRrf && c.Fault.Mutation == "word 0x00000006");
        Result result = Run(fault.Variant, fault.Fault, options);
        Assert.That(result.Outcome, Is.EqualTo(Outcome.DcsRestart), result.Details);
    }

    /// <summary>
    /// The faults of the lab test SpiFaultRestarts.DcsStartsWhileTfrRdyIsLow. Each is repaired by the protocol but leaves RRF at its idle header with
    /// TfrRdy low, so a new DCS must wait for the connection timeout of RRF. Which faults do that depends on the recovery paths of DSF, so this
    /// test tells when the list in the lab test has to change. Keep it in line with the lab test
    /// </summary>
    /// <param name="variant">Kind of transfer</param>
    /// <param name="exchange">Exchange that is corrupted</param>
    /// <param name="direction">Direction that is corrupted</param>
    /// <param name="mutation">Mutation of the exchange</param>
    [TestCase(Variant.RrfData, SpiExchange.HeaderResponse, ExchangeDirection.ToDsf, "zero 0 4")]
    [TestCase(Variant.RrfData, SpiExchange.HeaderResponse, ExchangeDirection.ToRrf, "zero 0 4")]
    [TestCase(Variant.RrfData, SpiExchange.Data, ExchangeDirection.ToDsf, "word 0xfefefefe")]
    public void LabFaultLeavesTfrRdyLow(Variant variant, SpiExchange exchange, ExchangeDirection direction, string mutation)
    {
        RrfOptions options = new(PollWhileDisconnected: true, Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2, DcsRestartAfterRecovery: true);
        Case fault = GetCases().First(c => c.Variant == variant && (SpiExchange)c.Fault.Exchange == exchange && c.Fault.Direction == direction && c.Fault.Mutation == mutation);
        Result result = Run(fault.Variant, fault.Fault, options);
        Assert.That(result.Outcome, Is.EqualTo(Outcome.Retry), result.Details);
        Assert.That(result.Details, Does.Contain("connected after 4000.0 ms"));
    }

    private static IEnumerable<TestCaseData> DcsStartAfterFaultOptions =>
    [
        new TestCaseData(new RrfOptions(PollWhileDisconnected: true, DcsRestartAfterRecovery: true)).SetName("DcsStartsAfterRecoveredFault"),
        new TestCaseData(new RrfOptions(PollWhileDisconnected: true, Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2, DcsRestartAfterRecovery: true))
            .SetName("DcsStartsAfterRecoveredFault_Extended")
    ];

    /// <summary>
    /// After every single fault that the link recovers from, DCS stops and a new instance starts at once, while RRF still waits for the next
    /// transfer. RRF arms that header with TfrRdy low when it armed an odd number of extra exchanges since its last reset, which about one
    /// recovery in five does, but a new DCS waits for TfrRdy to be high. It must connect anyway, at the latest once RRF's connection timeout
    /// has reset it. On the lab printer the first start failed after 500 ms, as predicted (2026-11-02)
    /// </summary>
    /// <param name="options">Firmware end</param>
    [TestCaseSource(nameof(DcsStartAfterFaultOptions))]
    public void DcsStartsAfterRecoveredFault(RrfOptions options)
    {
        List<string> failures = [.. GetCases().Where(c => c.Variant != Variant.Connect)
            .Select(c => (Case: c, Result: Run(c.Variant, c.Fault, options)))
            .Where(r => r.Result.Outcome == Outcome.DcsRestart)
            .Select(r => $"- {r.Case}: {r.Result.Details}")];
        Assert.That(failures, Is.Empty, $"{failures.Count} new DCS instances cannot connect:\n" + string.Join("\n", failures.Take(20)));
    }

    private static IEnumerable<TestCaseData> DcsRestartOptions =>
    [
        new TestCaseData(new RrfOptions()).SetName("DcsRestart_RrfOfToday").Explicit("2 of 42 DCS restarts need a second start of DCS with the RRF of today"),
        new TestCaseData(new RrfOptions(PollWhileDisconnected: true)).SetName("DcsRestart_PollWhileDisconnected").Explicit("2 of 42 DCS restarts need a second start of DCS with the RRF of today"),
        new TestCaseData(new RrfOptions(Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2)).SetName("DcsRestart_Extended"),
        new TestCaseData(new RrfOptions(PollWhileDisconnected: true, Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2)).SetName("DcsRestart_ExtendedPolling"),
        new TestCaseData(new RrfOptions(Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2, DisabledRules: RrfRule.LeaveWhenDsfLeft))
            .SetName("DcsRestart_ExtendedWithoutLeaveWhenDsfLeft").Explicit("Shows why RRF leaves the extended link when DSF left: 6 of 42 DCS restarts need a second start of DCS without it")
    ];

    /// <summary>
    /// DCS restarts at every exchange of a transfer. RRF must notice it (R6), the link must come back, and nothing DCS sent may reach RRF twice
    /// </summary>
    [TestCaseSource(nameof(DcsRestartOptions))]
    public void DcsRestart(RrfOptions options)
    {
        List<(Case Case, Result Result)> results = [.. GetDcsRestartCases().Select(c => (c, Run(c.Variant, c.Fault, options)))];
        TestContext.Out.WriteLine($"{results.Count} DCS restarts, RRF options {options}");
        foreach (var group in results.GroupBy(r => r.Case.Mutation))
        {
            TestContext.Out.WriteLine($"{group.Key}: " + string.Join(", ", group.GroupBy(r => r.Result.Label).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
        }

        List<(Case Case, Result Result)> failures = [.. results.Where(r => r.Result.Outcome != Outcome.ResetDetected)];
        Assert.That(failures, Is.Empty, $"{failures.Count} of {results.Count} DCS restarts break the link:\n" + string.Join("\n", failures.Select(r => $"- {r.Case}: {r.Result.Outcome}, {r.Result.Details}")));
    }

    /// <summary>
    /// Time from the boot of a restarted RRF until DSF notices the restart, for every restart of the census. On the lab printer, DSF took
    /// 500 ms or 4000 ms after a watchdog reset, until a timeout of RRF ended a wait of both sides (2026-11-02)
    /// </summary>
    /// <param name="options">Firmware end</param>
    [TestCaseSource(nameof(RestartOptions)), Explicit("Analysis")]
    public void RebootDetectionTime(RrfOptions options)
    {
        System.Text.RegularExpressions.Regex noticed = new(@"noticed \[([^\]]*)\] ms after the boot");
        List<(Case Case, string Delay)> results = [.. GetRestartCases(options.TargetTransfer, options.Capabilities.HasFlag(LinkCapabilities.ExtendedHeader))
            .Select(c => (c, noticed.Match(Run(c.Variant, c.Fault, options).Details).Groups[1].Value.Split(", ")[0]))];
        static string Bucket(string delay) => !double.TryParse(delay, System.Globalization.CultureInfo.InvariantCulture, out double ms) ? delay :
            (ms < 50) ? "< 50 ms" : (ms < 400) ? "50-400 ms" : (ms < 700) ? "about 500 ms" : (ms < 3500) ? "0.7-3.5 s" : "about 4 s";
        foreach (var group in results.GroupBy(r => r.Case.Mutation.Contains("DSF before the first arm") ? "DSF before the first arm" : "RRF arms first"))
        {
            TestContext.Out.WriteLine($"{group.Key}: " + string.Join(", ", group.GroupBy(r => Bucket(r.Delay)).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
        }
        foreach ((Case c, string delay) in results)
        {
            TestContext.Out.WriteLine($"- {c}: {delay} ms");
        }
    }

    private static IEnumerable<TestCaseData> RestartOptions =>
    [
        new TestCaseData(new RrfOptions()).SetName("RrfRestart"),
        new TestCaseData(new RrfOptions(PollWhileDisconnected: true)).SetName("RrfRestart_PollWhileDisconnected"),
        new TestCaseData(new RrfOptions(Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2)).SetName("RrfRestart_Extended"),
        new TestCaseData(new RrfOptions(PollWhileDisconnected: true, Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2)).SetName("RrfRestart_ExtendedPolling"),
        new TestCaseData(new RrfOptions(PollWhileDisconnected: true, Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2, BootArmDelay: 0))
            .SetName("RrfRestart_ExtendedWithoutBootArmDelay"),
        new TestCaseData(new RrfOptions(PollWhileDisconnected: true, BootArmDelay: FakeRrfSpi.ForkBootArmDelay)).SetName("RrfRestart_BootArmDelay"),
        new TestCaseData(new RrfOptions(PollWhileDisconnected: true, Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2, BootWindowMiso: 0xFF))
            .SetName("RrfRestart_ExtendedBootWindowFF"),
        new TestCaseData(new RrfOptions(PollWhileDisconnected: true, Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2, BootWindowMiso: Consts.FormatCode))
            .SetName("RrfRestart_ExtendedBootWindow5F")
    ];

    private static IEnumerable<TestCaseData> RestartsAtFirstNumber => GetRestartCases()
        .Where(c => c.Mutation.StartsWith("RRF restart twice") || c.Mutation.StartsWith("RRF restart after seq 1,"))
        .Select(c => new TestCaseData(c.ToString()).SetName($"RestartIsNotTakenForRepeat({c.Variant}, {c.Phase}, {c.Mutation})"));

    /// <summary>
    /// A restarted RRF numbers its transfers from 1 again. When DSF saw 1 last, which it does after every reset, the restart repeats
    /// that number, and the sequence guard of DSF must not take it for a transfer RRF did not complete
    /// </summary>
    [TestCaseSource(nameof(RestartsAtFirstNumber))]
    public void RestartIsNotTakenForRepeat(string name)
    {
        Case c = GetRestartCases().Single(c => c.ToString() == name);
        Result result = Run(c.Variant, c.Fault, new(PollWhileDisconnected: true));
        Assert.That(result.Outcome, Is.EqualTo(Outcome.ResetDetected), result.Details);
    }

    /// <summary>
    /// RRF restarts at every exchange of a transfer. DSF must notice it (R6), the link must come back, and nothing DSF sent may reach RRF twice
    /// </summary>
    [TestCaseSource(nameof(RestartOptions)), Explicit("5 of 256 restarts stay unnoticed when RRF of today restarts right after its sequence number wrapped")]
    public void RrfRestart(RrfOptions options)
    {
        List<(Case Case, Result Result)> results = [.. GetRestartCases(options.TargetTransfer, options.Capabilities.HasFlag(LinkCapabilities.ExtendedHeader)).Select(c => (c, Run(c.Variant, c.Fault, options)))];
        TestContext.Out.WriteLine($"{results.Count} restarts, RRF options {options}");
        foreach (var group in results.GroupBy(r => r.Case.Mutation[..r.Case.Mutation.IndexOf(',')]))
        {
            TestContext.Out.WriteLine($"{group.Key}: " + string.Join(", ", group.GroupBy(r => r.Result.Label).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
        }

        List<(Case Case, Result Result)> failures = [.. results.Where(r => r.Result.Outcome != Outcome.ResetDetected)];
        Assert.That(failures, Is.Empty, $"{failures.Count} of {results.Count} restarts break the link:\n" + string.Join("\n", failures.Select(r => $"- {r.Case}: {r.Result.Outcome}, {r.Result.Details}")));
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
    [TestCase("BothData / Header / ToRrf / Restart / RRF restart twice, boot 100 ms", SequenceMismatchHandling.ConnectionReset, false, true)]
    [TestCase("BothData / DataResponse / ToRrf / Restart / RRF restart after seq 65535, boot 100 ms")]
    [TestCase("BothData / Header / ToRrf / Restart / RRF restart, boot 100 ms", SequenceMismatchHandling.ConnectionReset, false, false, true)]
    [TestCase("NoData / Header / ToDsf / Exchange / BadResponse in first word", SequenceMismatchHandling.ConnectionReset, false, false, true, ActivationTransfer)]
    [TestCase("BothData / Data / ToRrf / DcsRestart / DCS restart after 100 ms")]
    [TestCase("BothData / Header / ToRrf / Restart / RRF restart before the next arm, boot 100 ms", SequenceMismatchHandling.ConnectionReset, false, false, true)]
    [TestCase("BothData / Data / ToRrf / Restart / RRF restart before the next arm, boot 100 ms", SequenceMismatchHandling.ConnectionReset, false, false, true)]
    [Explicit("Analysis")]
    public void TraceCase(string name, SequenceMismatchHandling sequenceMismatchHandling = SequenceMismatchHandling.ConnectionReset, bool guards = false, bool poll = false,
        bool extended = false, int target = ExtendedTransfer + 2)
    {
        Case c = GetCases().Concat(GetRestartCases()).Concat(GetRestartCases(ExtendedTransfer + 2, true)).Concat(GetRestartBeforeArmCases()).Concat(GetDcsRestartCases())
            .First(c => c.ToString() == name);
        List<string> trace = [];
        Result result = Run(c.Variant, c.Fault, new(sequenceMismatchHandling, PollWhileDisconnected: poll, PhaseGuard: guards, SequenceGuard: guards,
            Capabilities: extended ? LinkCapabilities.All : LinkCapabilities.None, TargetTransfer: extended ? target : TargetTransfer), trace);
        TestContext.Out.WriteLine($"{c}: {result}");
        foreach (string line in trace.Take(200))
        {
            TestContext.Out.WriteLine(line);
        }
    }

    private static IEnumerable<TestCaseData> HarnessOptions =>
    [
        new TestCaseData(LinkCapabilities.None, TargetTransfer).SetName("PortMatchesRealFirmware"),
        new TestCaseData(LinkCapabilities.All, ExtendedTransfer + 2).SetName("PortMatchesRealFirmware_Extended"),
        new TestCaseData(LinkCapabilities.All, ActivationTransfer).SetName("PortMatchesRealFirmware_ExtendedActivationTransfer"),
        new TestCaseData(LinkCapabilities.All, ActivationTransfer + 1).SetName("PortMatchesRealFirmware_ExtendedRrfSwitchTransfer"),
        new TestCaseData(LinkCapabilities.All, ExtendedTransfer).SetName("PortMatchesRealFirmware_ExtendedDsfSwitchTransfer")
    ];

    /// <summary>
    /// Replay every single fault and every DCS restart against the real DataTransfer and SbcInterface of RRF, built as a host program whose path
    /// is in RRF_HOST_HARNESS, and compare its answers with those of the port. Inputs are replayed open-loop, so only the first difference of a case counts.
    /// The restarts before the next arm have a test of their own, because the host program needs -b for them
    /// </summary>
    /// <param name="capabilities">Capabilities DSF activates, which the host build must offer</param>
    /// <param name="target">Transfer with the fault</param>
    [TestCaseSource(nameof(HarnessOptions)), Explicit("Needs the host build of RRF's DataTransfer and SbcInterface")]
    public void PortMatchesRealFirmware(LinkCapabilities capabilities, int target)
    {
        RrfOptions options = new(PollWhileDisconnected: Environment.GetEnvironmentVariable("RRF_HOST_POLL") == "1", Capabilities: capabilities, TargetTransfer: target);
        CompareWithHarness(string.Empty, capabilities.HasFlag(LinkCapabilities.ExtendedHeader),
            Enum.GetValues<Variant>().Select(v => (v, (SpiFault?)null, $"{v} / clean", options))
                .Concat(GetCases().Concat(GetDcsRestartCases()).Select(c => (c.Variant, (SpiFault?)c.Fault, c.ToString(), options))));
    }

    /// <summary>
    /// Replay every restart before the next arm against the host build of RRF, started with -b so that the boot arm delay waits for T.
    /// RRF_HOST_RULE=1 tells that the build reads an extended BadResponse in the first header while it is not connected
    /// (<see cref="RrfRule.DecodeHeaderWordUnconnected"/>), otherwise the port runs without that rule
    /// </summary>
    [Test, Explicit("Needs the host build of RRF's DataTransfer and SbcInterface")]
    public void PortMatchesRealFirmwareAfterRestartBeforeArm()
    {
        bool poll = Environment.GetEnvironmentVariable("RRF_HOST_POLL") == "1";
        RrfRule disabledRules = (Environment.GetEnvironmentVariable("RRF_HOST_RULE") == "1") ? RrfRule.None : RrfRule.DecodeHeaderWordUnconnected;
        CompareWithHarness("-b", true, GetRestartBeforeArmCases().Select(c => (c.Variant, (SpiFault?)c.Fault, c.ToString(), ExtendedRestartOptions(c, poll, disabledRules) with { HarnessBootDelay = true })));
    }

    /// <summary>
    /// Replay the given cases against the host program in RRF_HOST_HARNESS and compare its answers with those of the port
    /// </summary>
    /// <param name="arguments">Arguments of the host program</param>
    /// <param name="extended">Whether RRF must send extended codes in some case</param>
    /// <param name="cases">Cases to replay</param>
    private void CompareWithHarness(string arguments, bool extended, IEnumerable<(Variant Variant, SpiFault? Fault, string Name, RrfOptions Options)> cases)
    {
        string? harness = Environment.GetEnvironmentVariable("RRF_HOST_HARNESS");
        if (string.IsNullOrEmpty(harness))
        {
            Assert.Ignore("RRF_HOST_HARNESS is not set");
        }

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(harness!, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;

        List<string> differences = [];
        int caseCount = 0, events = 0, extendedCases = 0;
        string extendedSuccess = Convert.ToHexStringLower(BitConverter.GetBytes(ExtendedLink.Success));
        foreach ((Variant variant, SpiFault? fault, string name, RrfOptions options) in cases)
        {
            List<(string Input, string? Output)> replay = [];
            Run(variant, fault, options, replay: replay);
            caseCount++;
            if (replay.Any(r => r.Output?.StartsWith($"R {extendedSuccess}", StringComparison.Ordinal) == true))
            {
                extendedCases++;
            }
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

        TestContext.Out.WriteLine($"{caseCount} cases, {events} events compared, {differences.Count} cases differ, {extendedCases} cases with extended codes from RRF");
        Assert.That(differences, Is.Empty, string.Join("\n", differences.Take(20)));
        if (extended)
        {
            Assert.That(extendedCases, Is.GreaterThan(0), "RRF never sent an extended code");
        }
    }

    #region Diagnostics
    /// <summary>
    /// Counters of the recoveries that DSF reports in M122
    /// </summary>
    private static readonly string[] RecoveryCounters =
    [
        "Full transfer restarts", "header retries", "data retries", "Transfers sent again", "transfers requested again", "extended link left by RRF", "lost connections", "connection resets"
    ];

    private static Dictionary<string, int> ReadRecoveryCounters(StringBuilder diagnostics) =>
        RecoveryCounters.ToDictionary(name => name, name => int.Parse(System.Text.RegularExpressions.Regex.Match(diagnostics.ToString(), $@"{name}: (\d+)").Groups[1].Value));

    /// <summary>
    /// Warnings of DSF in the trace, see <see cref="TraceLogger"/>
    /// </summary>
    private static bool IsDsfWarning(string line) => line.StartsWith("   ") && line.Contains(" ms: DSF ") && !line.Contains(" ms: DSF timeout after ");

    private static IEnumerable<TestCaseData> RecoveryCounterOptions =>
    [
        new TestCaseData(new RrfOptions()).SetName("{m}"),
        new TestCaseData(new RrfOptions(Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2)).SetName("{m}_Extended")
    ];

    /// <summary>
    /// Without a fault, M122 of DSF reports no recovery
    /// </summary>
    [TestCaseSource(nameof(RecoveryCounterOptions))]
    public void RecoveryCountersStayZeroWithoutFaults(RrfOptions options)
    {
        foreach (Variant variant in Enum.GetValues<Variant>())
        {
            StringBuilder diagnostics = new();
            Result result = Run(variant, null, options, diagnostics: diagnostics);
            Assert.That(result.Outcome, Is.EqualTo(Outcome.Retry), $"{variant}: {result.Details}");
            Assert.That(ReadRecoveryCounters(diagnostics).Values, Is.All.Zero, $"{variant}: {diagnostics}");
        }
    }

    /// <summary>
    /// Every single fault that DSF had to deal with shows in M122 of DSF. When the link recovered through the retries of the protocol and DSF
    /// logged a warning, a counter went up, but none of a reconnect. When DSF noticed a restart of RRF, the connection resets went up.
    /// A fault in the first transfer is left out, because DCS exits then and a new instance starts with new counters
    /// </summary>
    [TestCaseSource(nameof(RecoveryCounterOptions))]
    public void RecoveryCountersShowEveryFault(RrfOptions options)
    {
        List<string> failures = [];
        foreach (Case c in GetCases().Concat(GetRestartCases(options.TargetTransfer, options.Capabilities.HasFlag(LinkCapabilities.ExtendedHeader))).Where(c => c.Variant != Variant.Connect))
        {
            List<string> trace = [];
            StringBuilder diagnostics = new();
            Result result = Run(c.Variant, c.Fault, options, trace, diagnostics: diagnostics);
            Dictionary<string, int> counters = ReadRecoveryCounters(diagnostics);
            bool counted = result.Outcome switch
            {
                Outcome.Retry or Outcome.Slow => (counters.Values.Sum() > 0 || !trace.Any(IsDsfWarning)) && counters["lost connections"] == 0 && counters["connection resets"] == 0,
                Outcome.ResetDetected => counters["connection resets"] > 0,
                _ => true
            };
            if (!counted)
            {
                failures.Add($"- {c}: {result.Outcome}, {string.Join(", ", counters.Select(counter => $"{counter.Key}: {counter.Value}"))}");
            }
        }
        Assert.That(failures, Is.Empty, $"{failures.Count} faults do not show in M122 of DSF:\n" + string.Join("\n", failures.Take(20)));
    }

    /// <summary>
    /// Each counter goes up for the recovery it is named after, and no other counter does
    /// </summary>
    /// <param name="name">Single fault</param>
    /// <param name="extended">Whether the link is extended</param>
    /// <param name="expected">Counters that are not zero</param>
    [TestCase("BothData / Header / ToDsf / FormatCode / zero", false, "Full transfer restarts: 1")]
    [TestCase("BothData / HeaderResponse / ToDsf / Response / zero", false, "Full transfer restarts: 1")]
    [TestCase("NoData / HeaderResponse / ToDsf / Response / zero", false, "Full transfer restarts: 1")]
    [TestCase("BothData / Header / ToDsf / ChecksumHeader32 / flip bit 96", false, "header retries: 1")]
    [TestCase("BothData / Data / ToRrf / Payload / flip bit 128", false, "data retries: 1")]
    [TestCase("RrfData / Data / ToDsf / Payload / flip bit 128", true, "data retries: 1")]
    [TestCase("BothData / DataResponse / ToDsf / Response / flip bit 0", false, "data retries: 1")]
    [TestCase("BothData / DataResponse / ToRrf / Response / BadResponse", false, "header retries: 1, Transfers sent again: 1")]
    [TestCase("BothData / DataResponse / ToDsf / Response / BadResponse", true, "Full transfer restarts: 1, header retries: 1, transfers requested again: 1")]
    [TestCase("BothData / Header / ToRrf / Restart / RRF restart, boot 100 ms", true, "Full transfer restarts: 1, extended link left by RRF: 1, connection resets: 1")]
    [TestCase("BothData / Header / ToRrf / Restart / RRF restart, boot 100 ms", false, "Full transfer restarts: 1, connection resets: 1")]
    [TestCase("BothData / Data / ToDsf / Exchange / BadResponse in first word", false, "Full transfer restarts: 11, header retries: 1, lost connections: 1, connection resets: 1")]
    public void RecoveryCounter(string name, bool extended, string expected)
    {
        RrfOptions options = extended ? new(Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2) : new();
        Case c = GetCases().Concat(GetRestartCases(options.TargetTransfer, extended)).First(c => c.ToString() == name);
        StringBuilder diagnostics = new();
        Run(c.Variant, c.Fault, options, diagnostics: diagnostics);
        Dictionary<string, int> expectedCounters = RecoveryCounters.ToDictionary(counter => counter, counter => 0);
        foreach (string counter in expected.Split(", "))
        {
            expectedCounters[counter[..counter.IndexOf(':')]] = int.Parse(counter[(counter.IndexOf(':') + 2)..]);
        }
        Assert.That(ReadRecoveryCounters(diagnostics), Is.EqualTo(expectedCounters), diagnostics.ToString());
    }

    /// <summary>
    /// A bit error in the data of RRF that only DSF got wrong is repaired by exchanging the data again, which RRF does on a bad data checksum
    /// of either side, instead of restarting the full transfer. On the extended link that restart even took a second one
    /// </summary>
    [TestCaseSource(nameof(RecoveryCounterOptions))]
    public void BadDataOnlyDsfGotIsExchangedAgain(RrfOptions options)
    {
        List<string> failures = [];
        foreach (Case c in GetCases().Where(c => c.Variant is Variant.BothData or Variant.RrfData && c.Phase == "Data" && c.Direction == ExchangeDirection.ToDsf && c.Mutation.StartsWith("flip bit")))
        {
            StringBuilder diagnostics = new();
            Result result = Run(c.Variant, c.Fault, options, diagnostics: diagnostics);
            Dictionary<string, int> counters = ReadRecoveryCounters(diagnostics);
            if (result.Outcome != Outcome.Retry || counters.Any(counter => counter.Key != "data retries" && counter.Value != 0) || counters["data retries"] > 1)
            {
                failures.Add($"- {c}: {result.Outcome}, {string.Join(", ", counters.Where(counter => counter.Value != 0).Select(counter => $"{counter.Key}: {counter.Value}"))}");
            }
        }
        Assert.That(failures, Is.Empty, $"{failures.Count} bit errors in the data of RRF are not repaired by a single data retry:\n" + string.Join("\n", failures.Take(20)));
    }
    #endregion

    #region ESD events
    /// <summary>
    /// SPI events of the piezo ESD test on the lab printer (2026-11-03) as they reached the link, with the counters they leave in M122 of DSF.
    /// No single wrong bit or byte gives the CRCs DSF logged for the data events, so these corrupt two bytes
    /// </summary>
    private static readonly (string Time, Variant Variant, string Phase, int Exchange, ExchangeDirection Direction, string Mutation, string Expected)[] EsdEvents =
    [
        ("14:13:00, 14:15:12, 16:09:28", Variant.RrfData, "Data", 2, ExchangeDirection.ToDsf, "ones 4 2", "data retries: 1"),
        ("16:10:18", Variant.BothData, "Data", 2, ExchangeDirection.ToDsf, "ones 4 2", "data retries: 1"),
        ("16:09:14", Variant.BothData, "Header", 0, ExchangeDirection.Both, "flip 100", "header retries: 1")
    ];

    private static SpiFault EsdFault(ExchangeDirection direction, int exchange, string mutation) =>
        new(exchange, direction, data => SpiFaultSpec.Apply(mutation, data), Mutation: mutation);

    private static IEnumerable<TestCaseData> EsdEventOptions =>
        EsdEvents.SelectMany(e => new[] { false, true }.Select(extended => new TestCaseData(e.Time, extended).SetName($"EsdEventIsRepaired({e.Time}{(extended ? ", extended" : "")})")));

    /// <summary>
    /// Each SPI event of the ESD test is repaired by the retries of the protocol, without a restart of the full transfer
    /// </summary>
    /// <param name="time">Time of the event on the lab printer</param>
    /// <param name="extended">Whether the link is extended</param>
    [TestCaseSource(nameof(EsdEventOptions))]
    public void EsdEventIsRepaired(string time, bool extended)
    {
        var e = EsdEvents.Single(e => e.Time == time);
        RrfOptions options = extended ? new(Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2) : new();
        StringBuilder diagnostics = new();
        Result result = Run(e.Variant, EsdFault(e.Direction, e.Exchange, e.Mutation), options, diagnostics: diagnostics);
        Assert.That(result.Outcome, Is.EqualTo(Outcome.Retry), result.Details);

        Dictionary<string, int> expectedCounters = RecoveryCounters.ToDictionary(counter => counter, counter => 0);
        foreach (string counter in e.Expected.Split(", "))
        {
            expectedCounters[counter[..counter.IndexOf(':')]] = int.Parse(counter[(counter.IndexOf(':') + 2)..]);
        }
        Assert.That(ReadRecoveryCounters(diagnostics), Is.EqualTo(expectedCounters), diagnostics.ToString());
    }

    /// <summary>
    /// Write the SPI events of the ESD test for the fault injection build on the lab printer, each repeated LAB_ESD_REPEATS times (default 5),
    /// in the format of <see cref="WriteHardwareCases"/>
    /// </summary>
    [Test, Explicit("Writes the cases for src/LabPrinterTests/SpiFaultInjection.cs")]
    public void WriteEsdCases()
    {
        static SpiTransferKind Kind(Variant variant) => variant switch
        {
            Variant.NoData => SpiTransferKind.NoData,
            Variant.BothData => SpiTransferKind.BothData,
            Variant.DsfData => SpiTransferKind.DsfData,
            _ => SpiTransferKind.RrfData
        };

        RrfOptions options = new(PollWhileDisconnected: true, Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2);
        int repeats = int.TryParse(Environment.GetEnvironmentVariable("LAB_ESD_REPEATS"), out int value) ? value : 5;
        var cases = EsdEvents.SelectMany(e =>
        {
            SpiFault fault = EsdFault(e.Direction, e.Exchange, e.Mutation);
            Dictionary<string, string> expectedByKind = Enum.GetValues<Variant>().Where(variant => variant != Variant.Connect)
                .ToDictionary(variant => Kind(variant).ToString(), variant => Run(variant, fault, options).Outcome.ToString());
            string spec = new SpiFaultSpec(Kind(e.Variant), (SpiExchange)e.Exchange, Enum.Parse<SpiFaultDirection>(e.Direction.ToString()), e.Mutation).ToString();
            return Enumerable.Range(1, repeats).Select(n => new { Name = $"ESD {e.Time} #{n}", Spec = spec, Expected = expectedByKind[Kind(e.Variant).ToString()], ExpectedByKind = expectedByKind });
        }).ToList();

        string path = Environment.GetEnvironmentVariable("LAB_SPI_FAULT_CASES") is { Length: > 0 } file ? file : System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "spi-fault-cases-esd.json");
        System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { Configuration = "esd", Options = options.ToString(), Cases = cases },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        TestContext.Out.WriteLine($"{cases.Count} cases written to {path}");
    }
    #endregion

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
                exchanges.Add(("Data", (variant is Variant.BothData or Variant.DsfData) ? DsfDataSize : RrfDataSize, DataFields));
                exchanges.Add(("DataResponse", 4, ResponseFields));
            }

            for (int exchange = 0; exchange < exchanges.Count; exchange++)
            {
                (string phase, int size, var fields) = exchanges[exchange];
                foreach (ExchangeDirection direction in (ExchangeDirection[])[ExchangeDirection.ToRrf, ExchangeDirection.ToDsf])
                {
                    // The mutations are written as SpiFaultSpec text, so that the fault injection build applies the same ones on the real link
                    Case Make(string field, string mutation, string spec) =>
                        new(variant, phase, direction, field, mutation, new SpiFault(exchange, direction, data => SpiFaultSpec.Apply(spec, data), Mutation: spec));

                    // Every bit, named after the field it is in
                    for (int bit = 0; bit < size * 8; bit++)
                    {
                        int b = bit;
                        string field = fields.FirstOrDefault(f => b / 8 >= f.Offset && b / 8 < f.Offset + f.Size).Name ?? "Payload";
                        yield return Make(field, $"flip bit {bit}", $"flip {bit}");
                    }

                    // Special values of each field
                    foreach ((string name, int offset, int fieldSize) in fields)
                    {
                        yield return Make(name, "zero", $"zero {offset} {fieldSize}");
                        yield return Make(name, "all ones", $"ones {offset} {fieldSize}");
                        yield return Make(name, "plus one", $"add {offset} {fieldSize} 1");
                        yield return Make(name, "minus one", $"add {offset} {fieldSize} -1");
                    }
                    if (fields == ResponseFields)
                    {
                        foreach ((string name, uint value) in ResponseCodes)
                        {
                            yield return Make("Response", name, $"word 0x{value:x8}");
                        }
                    }

                    // Whole exchange
                    yield return Make("Exchange", "all 0x00", "fill 0x00");
                    yield return Make("Exchange", "all 0xFF", "fill 0xFF");
                    yield return Make("Exchange", "BadResponse in first word", $"word 0x{TransferResponse.BadResponse:x8}");
                }

            }
        }
    }

    /// <summary>
    /// Exchanges of each variant
    /// </summary>
    private static IEnumerable<(Variant Variant, int Exchange, string Phase)> GetExchanges() =>
        Enum.GetValues<Variant>().SelectMany(variant => ((variant == Variant.NoData) ? new[] { "Header", "HeaderResponse" } : ["Header", "HeaderResponse", "Data", "DataResponse"])
            .Select((phase, exchange) => (variant, exchange, phase)));

    /// <summary>
    /// Get every restart of RRF: right before each exchange of the target transfer, booting quickly or slowly. RRF may restart again
    /// after its first transfer, as a reconnect timeout does, or restart when its sequence number has just wrapped, so that it starts
    /// again at the number DSF saw last or at that plus two
    /// </summary>
    /// <param name="target">Target transfer</param>
    /// <param name="extended">Whether the target transfer uses the extended link, whose counter wraps after 31</param>
    private static IEnumerable<Case> GetRestartCases(int target = TargetTransfer, bool extended = false)
    {
        int last = extended ? ExtendedLink.CounterMask : ushort.MaxValue;
        foreach ((Variant variant, int exchange, string phase) in GetExchanges())
        {
            foreach (double bootTime in RestartBootTimes)
            {
                // At the boot, DSF may exchange right after the falling edge of DataTransfer::Init, before RRF raises TfrRdy for its first header
                foreach (bool dsfFirst in new[] { false, true })
                {
                    string race = dsfFirst ? ", DSF before the first arm" : "";
                    Case Make(string kind, bool again = false, ushort firstSequenceNumber = 1) =>
                        new(variant, phase, ExchangeDirection.ToRrf, "Restart", $"RRF restart{kind}, boot {bootTime} ms{race}",
                            new SpiFault(exchange, ExchangeDirection.ToRrf, _ => { }, bootTime, again, firstSequenceNumber, DsfBeforeFirstArm: dsfFirst));
                    yield return Make("");
                    yield return Make(" twice", again: true);
                    if (variant != Variant.Connect)
                    {
                        // Transfer 1 of RRF has this number, so the one before the target transfer has 1 or the last number before the wrap
                        yield return Make(" after seq 1", firstSequenceNumber: (ushort)((1 - (target - 2)) & last));
                        yield return Make($" after seq {last}", firstSequenceNumber: (ushort)((last - (target - 2)) & last));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Get every restart of RRF that happens after RRF has completed an exchange of the target transfer and before it sets up the next one,
    /// see <see cref="SpiFault.RestartBeforeArm"/>
    /// </summary>
    private static IEnumerable<Case> GetRestartBeforeArmCases() =>
        GetExchanges().SelectMany(e => RestartBootTimes.Select(bootTime =>
            new Case(e.Variant, e.Phase, ExchangeDirection.ToRrf, "Restart", $"RRF restart before the next arm, boot {bootTime} ms",
                new SpiFault(e.Exchange, ExchangeDirection.ToRrf, _ => { }, bootTime, RestartBeforeArm: true))));

    /// <summary>
    /// Boot times of a restarting RRF
    /// </summary>
    private static readonly double[] RestartBootTimes = [100, 2000];

    /// <summary>
    /// Size of RRF's data: one message packet "rrf:N" without padding, because DataTransfer pads only before a following packet
    /// </summary>
    private const int RrfDataSize = 21;

    /// <summary>
    /// Size of DSF's data: one message packet "dsf:N", padded to 4 bytes by Writer.WriteMessage. The data exchange covers the longer of both
    /// </summary>
    private const int DsfDataSize = 24;

    #endregion

    #region Scenario
    private sealed class BudgetExceededException : Exception;

    private sealed class DcsCannotConnectException : Exception;

    /// <summary>
    /// Connect, perform clean transfers, inject the fault into the target transfer and check whether clean transfers follow
    /// </summary>
    private Result Run(Variant variant, SpiFault? fault, RrfOptions options, List<string>? trace = null, List<(string Input, string? Output)>? replay = null,
        Dictionary<RrfRule, int>? ruleHits = null, StringBuilder? diagnostics = null)
    {
        int target = (variant == Variant.Connect) ? 1 : options.TargetTransfer;
        bool activate = options.Capabilities != LinkCapabilities.None && variant != Variant.Connect;
        bool rrfDataInTarget = variant is Variant.Connect or Variant.BothData or Variant.RrfData;
        bool dsfDataInTarget = variant is Variant.BothData or Variant.DsfData;
        replay?.Add(($"NEW {(rrfDataInTarget ? 0 : target)} {options.UnderrunByte:X2}", null));
        FakeRrfSpi rrf = new(n => n != target || rrfDataInTarget, options.SequenceMismatchHandling, options.PollWhileDisconnected, options.RrfFirstOnTie, options.UnderrunByte, options.PhaseGuard, options.SequenceGuard,
            fault?.RrfFirstSequenceNumber ?? 1, options.Capabilities, options.DisabledRules)
        {
            Trace = trace,
            Replay = replay,
            RuleHits = ruleHits,
            // A livelock has to run until the restart timeout of DSF ends it
            ExchangeBudget = (options.SbcRestartTimeout is int restartTimeout) ? 2000 + 8 * target + 250 * restartTimeout : 2000 + 8 * target,
            // The fork's RRF, which offers link capabilities, delays its first header after a boot
            BootArmDelay = options.BootArmDelay ?? ((options.Capabilities != LinkCapabilities.None) ? FakeRrfSpi.ForkBootArmDelay : 0),
            BootWindowMiso = options.BootWindowMiso
        };
        if (options.HarnessBootDelay)
        {
            // A host program started with -b waits in the boot arm delay of every NEW until T, also at the start of a case
            replay?.Add(("T", $"T {rrf.ReplayStatusNow}"));
        }

        int dsfConnectionLosses = 0, dcsRestarts = 0, dsfId = 0, dsfResets = 0, restartTimeouts = 0;
        double? recoveredAt = null;

        // Numbers of exchanges after which DSF completed a transfer and noticed a reset of either side
        List<int> dsfTransfers = [], dsfDetections = [];
        List<double> dsfDetectionTimes = [];
        void OnConnectionLost(object? sender, EventArgs e)
        {
            dsfConnectionLosses++;
            dsfDetections.Add(rrf.Exchanges);
            dsfDetectionTimes.Add(rrf.Now);
        }
        List<string> dsfSent = [], dsfReceived = [];
        // The fault injection build injects through the SPI device of DSF instead of in RRF
        FaultInjectingSpiDevice? injector = (options.InjectThroughDsf && fault?.Mutation is not null) ? new(rrf, null, NullLogger.Instance) : null;
        bool injected = false;
        double injectedAt = 0;
        if (injector is not null)
        {
            injector.Injected += (_, _) =>
            {
                injected = true;
                injectedAt = rrf.Now;
            };
        }
        void ArmFault(SpiFault armed)
        {
            if (injector is null)
            {
                rrf.Arm(armed);
                return;
            }
            SpiExchange exchange = (SpiExchange)armed.Exchange;
            SpiTransferKind kind = options.InjectIntoActivation ? SpiTransferKind.Activation : variant switch
            {
                Variant.NoData => SpiTransferKind.NoData,
                Variant.BothData => SpiTransferKind.BothData,
                Variant.DsfData => SpiTransferKind.DsfData,
                _ => SpiTransferKind.RrfData
            };
            injector.Arm(new SpiFaultSpec(kind, exchange, Enum.Parse<SpiFaultDirection>(armed.Direction.ToString()), armed.Mutation!));
        }
        SpiAdapter CreateAdapter(FakeRrfSpi rrf, List<string>? trace) => CreateSpiAdapter(rrf, trace, injector, sbcRestartTimeout: options.SbcRestartTimeout);
        SpiAdapter dsf = CreateAdapter(rrf, trace);

        // Perform a full transfer and read the payloads RRF sent. Returns whether the transfer was clean
        bool Transfer(bool dsfData, bool connecting = false, bool activation = false)
        {
            if (activation && !dsf.WriteActivateLinkCapabilities(options.Capabilities))
            {
                throw new InvalidOperationException("DSF did not request the capabilities");
            }
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
                dsfDetections.Add(rrf.Exchanges);
            dsfDetectionTimes.Add(rrf.Now);
            }
            dsfTransfers.Add(rrf.Exchanges);

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

        // A DCS that died before an exchange is started again after the delay, and the new instance connects like the first one did
        int rrfResetsAtDcsRestart = -1;
        bool TransferOrRestartDcs(bool dsfData, bool activation = false)
        {
            try
            {
                return Transfer(dsfData, activation: activation);
            }
            catch (Exception e) when (e is DcsRestartException || (e is TimeoutException && !rrf.Budget.IsCancellationRequested))
            {
                // DCS stops after SbcRestartTimeout without a complete transfer, and systemd starts it again
                if (e is TimeoutException)
                {
                    restartTimeouts++;
                    trace?.Add($"   {rrf.Now,9:F3} ms: DCS stops after {options.SbcRestartTimeout} ms without a complete transfer");
                }
                rrfResetsAtDcsRestart = rrf.ConnectionResets;
                rrf.Wait((e as DcsRestartException)?.Delay ?? DcsRestartDelay);
                for (dsf = CreateAdapter(rrf, trace); ; dsf = CreateAdapter(rrf, trace))
                {
                    try
                    {
                        Transfer(false, true);
                        return false;
                    }
                    catch (Exception connectError) when (!rrf.Budget.IsCancellationRequested && connectError is not DcsRestartException)
                    {
                        if (++dcsRestarts > MaxDcsRestarts)
                        {
                            throw new DcsCannotConnectException();
                        }
                        rrf.Wait(DcsRestartDelay);
                    }
                }
            }
        }

        _model.OnConnectionLost += OnConnectionLost;
        try
        {
            // Connect. If that fails, DCS exits and systemd starts it again
            if (variant == Variant.Connect && fault is not null)
            {
                ArmFault(fault);
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
                    dsfDetections.Add(rrf.Exchanges);
            dsfDetectionTimes.Add(rrf.Now);
                    rrf.Wait(DcsRestartDelay);
                    dsf = CreateAdapter(rrf, trace);
                }
            }

            // Clean transfers, then the target transfer
            if (variant != Variant.Connect)
            {
                for (int transfer = 2; transfer < target; transfer++)
                {
                    Transfer(true, activation: activate && transfer == ActivationTransfer);
                }
                if (activate && target >= ExtendedTransfer && rrf.ActiveCapabilities != options.Capabilities)
                {
                    return new(Outcome.NoRecovery, false, $"RRF has activated {rrf.ActiveCapabilities} instead of {options.Capabilities} before the fault");
                }
                if (fault is not null)
                {
                    ArmFault(fault);
                }
                targetClean = TransferOrRestartDcs(dsfDataInTarget, activate && target == ActivationTransfer);
            }

            // The link must deliver clean transfers again. The transfer that got the fault counts if it completed cleanly through retries
            int cleanInARow = targetClean ? 1 : 0;
            recoveredAt = targetClean ? rrf.Now : null;
            for (int i = 0; i < MaxTransfersAfterFault && cleanInARow < RecoveredTransfers; i++)
            {
                if (TransferOrRestartDcs(true))
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

            // DCS stops after the recovery and a new instance starts at once, while RRF still waits for the next transfer
            if (options.DcsRestartAfterRecovery)
            {
                int resetsBefore = rrf.ConnectionResets;
                double stoppedAt = rrf.Now;
                dsf = CreateAdapter(rrf, trace);
                try
                {
                    Transfer(false, true);
                }
                catch (Exception e) when (!rrf.Budget.IsCancellationRequested)
                {
                    return new(Outcome.DcsRestart, rrf.Disconnects > 0, $"the new DCS cannot connect: {e.Message}");
                }
                return new(Outcome.Retry, rrf.Disconnects > 0, $"the new DCS connected after {rrf.Now - stoppedAt:F1} ms, RRF reset {rrf.ConnectionResets - resetsBefore}x meanwhile");
            }
        }
        catch (BudgetExceededException)
        {
            return new(Outcome.NoRecovery, rrf.Disconnects > 0, $"no clean transfer within {rrf.Now:F0} ms and {rrf.Exchanges} exchanges");
        }
        catch (DcsCannotConnectException)
        {
            return new(Outcome.NoRecovery, rrf.Disconnects > 0, $"the new DCS cannot connect after {MaxDcsRestarts} restarts");
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
            if (diagnostics is not null)
            {
                dsf.PrintDiagnostics(diagnostics);
            }
        }

        if (fault is not null && !(injector is null ? rrf.FaultApplied : injected))
        {
            return new(Outcome.NoRecovery, false, "the fault was never applied, the scenario does not reach the exchange");
        }

        // After a restart of DCS, RRF must notice it, because the new DCS knows nothing of what the old one left in RRF (R6), and nothing may reach RRF twice
        if (fault?.DcsRestartDelay is not null)
        {
            string dcsDetails = $"RRF reset {rrf.ConnectionResets}x ({rrfResetsAtDcsRestart}x before the restart), RRF disconnect {rrf.Disconnects}x, DCS restart {dcsRestarts}x, " +
                $"RRF got [{string.Join(", ", rrf.ReceivedPayloads)}] of [{string.Join(", ", dsfSent)}]";
            if (HasDuplicate(rrf.ReceivedPayloads))
            {
                return new(Outcome.Duplicate, rrf.Disconnects > 0, dcsDetails);
            }
            if (dcsRestarts > 0)
            {
                // The new DCS must connect at once. Failing to start makes systemctl report an error and costs another restart of DCS
                return new(Outcome.DcsRestart, rrf.Disconnects > 0, dcsDetails);
            }
            return new((rrf.ConnectionResets > rrfResetsAtDcsRestart) ? Outcome.ResetDetected : Outcome.ResetMissed, rrf.Disconnects > 0, dcsDetails);
        }

        // After a restart of RRF, nothing DSF sent before may reach the new session again, and DSF must notice every restart that ends
        // a session it completed a transfer in, because RRF forgot what DSF waits for (R6). Noticing needs an exchange after the restart
        if (fault?.RestartBootTime is not null)
        {
            string restartDetails = $"DSF lost connection {dsfConnectionLosses}x, DSF reset {dsfResets}x, DCS restart {dcsRestarts}x, " +
                $"RRF restarts in exchange [{string.Join(", ", rrf.Restarts)}], DSF noticed after [{string.Join(", ", dsfDetections)}], " +
                $"noticed [{string.Join(", ", rrf.Boots.Select(boot => dsfDetectionTimes.Where(t => t >= boot).Select(t => $"{t - boot:F1}").FirstOrDefault("never")))}] ms after the boot, " +
                $"RRF got [{string.Join(", ", rrf.ReceivedPayloads)}] of [{string.Join(", ", dsfSent)}]";
            if (HasDuplicate(rrf.ReceivedPayloads))
            {
                return new(Outcome.Duplicate, rrf.Disconnects > 0, restartDetails);
            }
            bool missed = rrf.Restarts.Any(restart => dsfTransfers.Any(t => t < restart) && !dsfDetections.Any(t => t > restart));
            return new(missed ? Outcome.ResetMissed : Outcome.ResetDetected, rrf.Disconnects > 0, restartDetails);
        }

        // DCS got out of a stuck link only by stopping after SbcRestartTimeout
        if (restartTimeouts > 0)
        {
            return new(Outcome.DcsRestart, rrf.Disconnects > 0, $"DCS stopped {restartTimeouts}x after {options.SbcRestartTimeout} ms without a complete transfer, " +
                $"and the new instance connected; RRF reset {rrf.ConnectionResets}x, RRF disconnect {rrf.Disconnects}x");
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
        double recoveryTime = (fault is null || recoveredAt is null) ? 0 : recoveredAt.Value - (injector is null ? rrf.FaultTime : injectedAt);
        return (recoveryTime > MaxRecoveryTime)
            ? new(Outcome.Slow, false, $"recovered after {recoveryTime:F1} ms")
            : new(Outcome.Retry, false, $"recovered after {recoveryTime:F2} ms");
    }

    private static bool HasDuplicate(List<string> payloads) => payloads.Count != payloads.Distinct().Count();

    private SpiAdapter CreateSpiAdapter(FakeRrfSpi rrf, List<string>? trace, ISpiDevice? device = null, int? maxSbcRetries = null, int? sbcRestartTimeout = null)
    {
        Settings settings = new();
        settings.MaxSbcRetries = maxSbcRetries ?? settings.MaxSbcRetries;
        settings.SbcRestartTimeout = sbcRestartTimeout ?? settings.SbcRestartTimeout;
        return new(_eventLogger, _model, (trace is null) ? NullLogger<SpiAdapter>.Instance : new TraceLogger(rrf, trace), Options.Create(settings), rrf, device ?? rrf, rrf.Clock);
    }

    /// <summary>
    /// Writes the information, warnings and errors of DSF into the trace
    /// </summary>
    private sealed class TraceLogger(FakeRrfSpi rrf, List<string> trace) : ILogger<SpiAdapter>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                trace.Add($"   {rrf.Now,9:F3} ms: DSF {formatter(state, exception)}");
            }
        }
    }
    #endregion
}
