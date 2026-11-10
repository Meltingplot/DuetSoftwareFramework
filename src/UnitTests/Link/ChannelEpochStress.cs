using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes.Handlers;
using DuetControlServer.Link;
using DuetControlServer.Link.Channel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using Code = DuetControlServer.Commands.Code;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Link;

/// <summary>
/// Stress test of channel aborts against the fake firmware: it drives DSF and the emulated RRF through the situations the channel epoch of
/// design/sbc-link-v8-requirements.md (draft 8) is for, with a fixed seed, and counts the symptoms of the positional catch code of the v7 link.
/// Every abort is a DSF abort of a whole channel (LinkInterface.AbortAllAsync, as the IPC command InvalidateChannel and the abort keyword outside
/// a file do), and after every abort a probe code with a reply of its own must complete on that channel. Situations:
/// <list type="bullet">
/// <item>the abort crosses the macro request of an M98, or comes while the M98 waits for its macro (the main task holds the buffer mutex)</item>
/// <item>the abort crosses the final reply of a code that RRF still runs</item>
/// <item>the abort crosses RRF's own abort of all files (AbortFile all, GCodes::AbortPrint)</item>
/// <item>the abort crosses a trigger macro request (GCodes::CheckTriggers) or a daemon.g request</item>
/// <item>the abort crosses the closal of a macro (MacroFileClosed)</item>
/// <item>the abort comes while a blocking message box is open, at the base level or in a macro</item>
/// <item>the abort comes while DSF runs a code that RRF passed on (DoCode), optionally followed by a second one</item>
/// </list>
/// RRF refuses packets for a busy buffer mutex in some aborts (FakeFirmware.FailBufferLocks). Counters, per run:
/// out-of-order replies DSF logs, InvalidateChannel resends, events dropped by DSF's positional windows, codes completed with a reply that is not
/// their own, stale events that act on another level, probe codes cancelled by an event of the aborted work, probes that do not complete within
/// a bounded number of transfers, and exceptions that stop the link thread of DCS. The numbers are written to the test output and, if the
/// environment variable DSF_EPOCH_STRESS_REPORT names a file, appended there.
/// <para>
/// Measured on 2026-11-10 with seed 20261109 and 2000 aborts (out-of-order, InvalidateChannel resends, window drops, wrong completions, stale events,
/// cancelled probes, blocks, stops of the link thread; then the drops of tagged packets by DSF):
/// <list type="bullet">
/// <item>DSF ab322905 (no epoch) and RRF 0dccb9efe (V7), the baseline: 534, 305, 502, 257, 120, 31, 43, 0</item>
/// <item>DSF ab322905 and the RRF of E1 (RecordApply): 424, 0, 474, 343, 0, 38, 0, 0</item>
/// <item>DSF with the epoch and RRF 0dccb9efe: 522, 305, 502, 256, 120, 35, 43, 0</item>
/// <item>DSF with the epoch and the RRF of E1, which ignores the tags: 413, 0, 474, 342, 0, 39, 0, 0</item>
/// <item>Epoch pair: 0, 0, 0, 0, 0, 0, 0, 0; 1252 dropped by their tag</item>
/// <item>Epoch pair without the catch code of E3 (DSF's windows, RRF #46): 0, 0, 0, 0, 0, 0, 0, 0; 1867 dropped by their tag</item>
/// </list>
/// </para>
/// </summary>
[TestFixture]
public class ChannelEpochStress
{
    /// <summary>
    /// Seed of the measurement runs
    /// </summary>
    public const int Seed = 20261109;

    /// <summary>
    /// Number of aborts of a measurement run
    /// </summary>
    public const int MeasurementAborts = 2000;

    /// <summary>
    /// Timeout of a single wait for the fake firmware
    /// </summary>
    private static readonly TimeSpan Timeout = FakeFirmwareHost.DefaultTimeout;

    /// <summary>
    /// Number of transfers within which a probe code must complete, else its channel counts as blocked
    /// </summary>
    private const int BlockTransfers = 300;

    /// <summary>
    /// Minimum time a probe gets before its channel counts as blocked, so that a slow machine does not count as a block
    /// </summary>
    private static readonly TimeSpan BlockTime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Situations an abort is placed into
    /// </summary>
    public enum Situation
    {
        /// <summary>The abort crosses the macro request of an M98 or comes while the M98 waits for its macro</summary>
        MacroRequest,
        /// <summary>The abort crosses the reply of a code that RRF still runs</summary>
        RunningCode,
        /// <summary>The abort crosses RRF's own abort of all files</summary>
        RrfAbortAll,
        /// <summary>The abort crosses a trigger macro request</summary>
        TriggerRequest,
        /// <summary>The abort crosses a daemon.g request</summary>
        DaemonRequest,
        /// <summary>The abort crosses the closal of a macro</summary>
        MacroFileClosed,
        /// <summary>The abort comes while a blocking message box is open</summary>
        MessageBox,
        /// <summary>The abort comes while DSF runs a code that RRF passed on</summary>
        DoCode
    }

    /// <summary>
    /// Channels used for the situations that run on a regular channel
    /// </summary>
    private static readonly CodeChannel[] ClientChannels = [CodeChannel.HTTP, CodeChannel.Telnet, CodeChannel.SBC];

    /// <summary>
    /// Channel of the codes RRF passes on to DSF. Aux is the channel of PanelDue, whose unknown codes RRF passes on
    /// </summary>
    private const CodeChannel DoCodeChannel = CodeChannel.Aux;

    /// <summary>
    /// Counters of a run
    /// </summary>
    public sealed class Counters
    {
        /// <summary>Aborts performed</summary>
        public int Aborts;
        /// <summary>Out-of-order replies logged by DSF (Processor.HandleReply and Processor.Spin)</summary>
        public int OutOfOrderReplies;
        /// <summary>Packets RRF asked DSF to send again, other than a movement lock</summary>
        public int Resends;
        /// <summary>InvalidateChannel packets RRF asked DSF to send again</summary>
        public int InvalidateChannelResends;
        /// <summary>Macro requests and closals dropped by the positional windows of DSF (#134, #158, #161)</summary>
        public int WindowDrops;
        /// <summary>Codes of clients or codes RRF passed on that completed with a reply that is not their own</summary>
        public int WrongCompletions;
        /// <summary>Events of aborted work that acted on another level: macros started that RRF did not request, closals and acknowledgements
        /// without a level, lock confirmations without a request, system macros refused for a busy stack, orphaned macros</summary>
        public int StaleEventEffects;
        /// <summary>Probe codes sent after the abort that were cancelled anyway</summary>
        public int ProbesCancelled;
        /// <summary>Probe codes that did not complete within the bound, so their channel was blocked</summary>
        public int Blocks;
        /// <summary>Exceptions that stopped the link thread of DCS, e.g. a stack underrun</summary>
        public int DcsStops;
        /// <summary>Tagged packets of another epoch that DSF dropped (CE-14)</summary>
        public int DsfEpochDrops;
        /// <summary>Complete, non-empty replies of work that DSF had ended, which DSF showed as messages instead of completing a code with them
        /// (decision of 2026-11-10). No symptom: their packets are among <see cref="DsfEpochDrops"/></summary>
        public int StaleRepliesShown;
        /// <summary>Tagged packets of another epoch, drops by the gate and ignored repeats in RRF (CE-10, CE-24, CE-7)</summary>
        public int RrfEpochDrops;
        /// <summary>Macro requests that RRF wrote without a tag. With the epoch pair there are none: RRF writes nothing of a channel before it has read
        /// DSF's first packet, which carries the marker (decisions of 2026-11-10)</summary>
        public int UntaggedMacroRequests;
        /// <summary>Host restarts the run needed to recover from a block or a stop</summary>
        public int Restarts;
        /// <summary>Details of the wrong completions and the stale events, by kind</summary>
        public SortedDictionary<string, int> Details { get; } = [];

        /// <summary>Count a detail</summary>
        public void Detail(string kind) => Details[kind] = Details.GetValueOrDefault(kind) + 1;

        /// <summary>Counters that the channel epoch is meant to bring to 0</summary>
        public IEnumerable<(string Name, int Value)> Symptoms =>
        [
            (nameof(OutOfOrderReplies), OutOfOrderReplies), (nameof(InvalidateChannelResends), InvalidateChannelResends), (nameof(WindowDrops), WindowDrops),
            (nameof(WrongCompletions), WrongCompletions), (nameof(StaleEventEffects), StaleEventEffects), (nameof(ProbesCancelled), ProbesCancelled),
            (nameof(Blocks), Blocks), (nameof(DcsStops), DcsStops)
        ];

        /// <inheritdoc />
        public override string ToString()
        {
            StringBuilder builder = new();
            builder.Append($"aborts={Aborts} outOfOrder={OutOfOrderReplies} resends={Resends} invalidateResends={InvalidateChannelResends} windowDrops={WindowDrops} ");
            builder.Append($"wrongCompletions={WrongCompletions} staleEffects={StaleEventEffects} probesCancelled={ProbesCancelled} blocks={Blocks} dcsStops={DcsStops} ");
            builder.Append($"dsfEpochDrops={DsfEpochDrops} staleRepliesShown={StaleRepliesShown} rrfEpochDrops={RrfEpochDrops} untaggedMacroRequests={UntaggedMacroRequests} restarts={Restarts}");
            foreach (KeyValuePair<string, int> detail in Details)
            {
                builder.Append($"\n  {detail.Key}: {detail.Value}");
            }
            return builder.ToString();
        }
    }

    /// <summary>
    /// Options of a run
    /// </summary>
    /// <param name="Name">Name of the run in the report</param>
    /// <param name="ConfigureFirmware">Optional setup of the fake firmware, called again after every restart of the host</param>
    /// <param name="ConfigureHost">Optional setup of DCS after the host started</param>
    /// <param name="Only">Optional situations to run instead of all, for a closer look at one of them</param>
    public sealed record Options(string Name, Action<FakeFirmware.FakeFirmware>? ConfigureFirmware = null, Action<FakeFirmwareHost>? ConfigureHost = null, Situation[]? Only = null);

    /// <summary>
    /// Run the stress test
    /// </summary>
    /// <param name="options">Options of the run</param>
    /// <param name="seed">Seed of the random numbers</param>
    /// <param name="aborts">Number of aborts</param>
    /// <returns>Counters</returns>
    public static async Task<Counters> RunAsync(Options options, int seed, int aborts)
    {
        await using StressRun run = new(options, seed);
        await run.StartAsync();
        while (run.Counters.Aborts < aborts)
        {
            Situation situation = (Situation)run.Rng.Next(Enum.GetValues<Situation>().Length);
            if (options.Only is not null && !options.Only.Contains(situation))
            {
                continue;
            }
            await run.CycleAsync(situation);
        }
        run.Finish();

        string line = $"{DateTime.UtcNow:O} {options.Name} seed={seed} {run.Counters}";
        TestContext.Out.WriteLine(line);
        string? report = Environment.GetEnvironmentVariable("DSF_EPOCH_STRESS_REPORT");
        if (!string.IsNullOrEmpty(report))
        {
            System.IO.File.AppendAllText(report, line + Environment.NewLine);
        }
        return run.Counters;
    }

    /// <summary>
    /// Options of the epoch without the catch code that milestone E3 deletes (AC-1, AC-2): DSF's positional windows and RRF's codeReplySurplus (RRF #46)
    /// </summary>
    public static readonly Options WithoutCatchCode = new("Epoch without catch code", firmware =>
    {
        firmware.Model = FakeRrfModel.Epoch;
        firmware.CodeReplySurplus = false;
    }, host =>
    {
        foreach (Processor processor in host.Services.GetRequiredService<Manager>())
        {
            processor.UseAbortWindows = false;
        }
    });

    /// <summary>
    /// Measure the link with a model of RRF and DSF from milestone E2 (which tags), with the seed and number of aborts of the measurement. This only
    /// reports the numbers. <see cref="FakeRrfModel.V7"/> is a stock RRF of today (fork 0dccb9efe), <see cref="FakeRrfModel.RecordApply"/> milestone E1,
    /// which ignores the tags of DSF, <see cref="FakeRrfModel.Epoch"/> the epoch pair
    /// </summary>
    /// <param name="model">RRF that the fake firmware models</param>
    [Test, Explicit("Measurement, takes minutes: run with DSF_EPOCH_STRESS_REPORT set to collect the numbers")]
    public async Task Measure([Values] FakeRrfModel model)
    {
        Counters counters = await RunAsync(new Options(model.ToString(), firmware => firmware.Model = model), Seed, MeasurementAborts);
        Assert.That(counters.Aborts, Is.GreaterThanOrEqualTo(MeasurementAborts));
    }

    /// <summary>
    /// Measure the epoch pair without the catch code that E3 deletes
    /// </summary>
    [Test, Explicit("Measurement, takes minutes: run with DSF_EPOCH_STRESS_REPORT set to collect the numbers")]
    public async Task MeasureWithoutCatchCode()
    {
        Counters counters = await RunAsync(WithoutCatchCode, Seed, MeasurementAborts);
        Assert.That(counters.Aborts, Is.GreaterThanOrEqualTo(MeasurementAborts));
    }

    /// <summary>
    /// The epoch pair, with and without the catch code of E3, shows none of the symptoms: no out-of-order reply, no resend, no drop by a positional
    /// window, no wrong completion, no stale event, no cancelled probe, no block and no stop of the link thread. Fewer aborts than the measurement
    /// keep the suite short; the seed is the one of the measurement
    /// </summary>
    /// <param name="withoutCatchCode">Whether the catch code of E3 is switched off</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task EpochPairHasNoSymptoms(bool withoutCatchCode)
    {
        Counters counters = await RunAsync(withoutCatchCode ? WithoutCatchCode : new Options("Epoch"), Seed, 300);
        Assert.Multiple(() =>
        {
            foreach ((string name, int value) in counters.Symptoms)
            {
                Assert.That(value, Is.Zero, $"{name}: {counters}");
            }
            Assert.That(counters.Resends, Is.Zero, $"Resends: {counters}");
            Assert.That(counters.UntaggedMacroRequests, Is.Zero, $"UntaggedMacroRequests: {counters}");
        });
    }

    /// <summary>
    /// The daemon case alone, where the first daemon.g request of RRF crossed DSF's first abort of the channel untagged and fell into the window of #134
    /// while the first marker was on DSF's first channel packet only (1 drop with seed 20261109 and 100 aborts at 843ae3b5). With and without the catch
    /// code of E3, nothing of it is left
    /// </summary>
    /// <param name="withoutCatchCode">Whether the catch code of E3 is switched off</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task DaemonCaseHasNoSymptoms(bool withoutCatchCode)
    {
        Options options = withoutCatchCode ? WithoutCatchCode with { Name = "daemon without catch code", Only = [Situation.DaemonRequest] }
            : new Options("daemon", Only: [Situation.DaemonRequest]);
        Counters counters = await RunAsync(options, Seed, 100);
        Assert.Multiple(() =>
        {
            foreach ((string name, int value) in counters.Symptoms)
            {
                Assert.That(value, Is.Zero, $"{name}: {counters}");
            }
            Assert.That(counters.UntaggedMacroRequests, Is.Zero, $"UntaggedMacroRequests: {counters}");
        });
    }

    /// <summary>
    /// Run one situation with the epoch pair, for a look at the log (DSF_FAKE_FIRMWARE_LOG, DSF_FAKE_FIRMWARE_LOG_FILE)
    /// </summary>
    /// <param name="situation">Situation</param>
    [Test, Explicit("Tool for a closer look at one situation")]
    public async Task RunOnly([Values] Situation situation) => await RunAsync(new Options($"only {situation}", Only: [situation]), Seed, 100);

    /// <summary>
    /// A short run that keeps the harness working: every situation is reached and the run ends
    /// </summary>
    [Test]
    public async Task HarnessRuns()
    {
        Counters counters = await RunAsync(new Options("harness"), Seed, 40);
        Assert.That(counters.Aborts, Is.GreaterThanOrEqualTo(40));
    }

    /// <summary>
    /// State of one run
    /// </summary>
    private sealed class StressRun(Options options, int seed) : IAsyncDisposable
    {
        public Counters Counters { get; } = new();
        public Random Rng { get; } = new(seed);

        private FakeFirmwareHost _host = null!;
        private readonly LogCounter _log = new();
        private readonly HashSet<int> _held = [];
        private int _nextId = 100000, _nextMacroId = MacroIdBase;

        /// <summary>
        /// Codes of macros have P values from here on and reply nothing, as most codes do. Client codes reply "r" and their P value
        /// </summary>
        private const int MacroIdBase = 500000;

        /// <summary>
        /// Gate of the codes that RRF passes on to DSF, see <see cref="HoldingMCodeHandler"/>. Null while they are not held
        /// </summary>
        private TaskCompletionSource? _doCodeGate;

        /// <summary>
        /// Counters of the host instances that have ended already
        /// </summary>
        private int _rrfStaleStarts;

        public async Task StartAsync()
        {
            _host = await FakeFirmwareHost.StartAsync(firmware =>
            {
                firmware.OnCode("G4", code =>
                {
                    int p = (int)code.GetParameter('P')!;
                    lock (_held)
                    {
                        if (_held.Contains(p))
                        {
                            return FakeReply.NotFinished;
                        }
                    }
                    return (p >= MacroIdBase) ? FakeReply.Ok() : FakeReply.Ok($"r{p}");
                });
                options.ConfigureFirmware?.Invoke(firmware);
            }, minimumLogLevel: LogLevel.Debug, configureServices: services =>
            {
                services.AddLogging(builder => builder.AddProvider(_log));
                services.AddKeyedSingleton<ICodeHandler>(Keys.MCodes, (provider, _) => new HoldingMCodeHandler(ActivatorUtilities.CreateInstance<MCodeHandler>(provider), this));
            });
            options.ConfigureHost?.Invoke(_host);
            _doCodesWritten = _doCodeResultsChecked = 0;
            _host.Firmware.HoldTransfers();
            Assert.That(_host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True, "The transfers were not held");
        }

        private async Task RestartAsync()
        {
            Counters.Restarts++;
            // The counters of the fake firmware start again with the new host, so keep the totals so far
            CollectFirmwareCounters();
            ReleaseHolds();
            _host.Firmware.ReleaseTransfers();
            await _host.DisposeAsync();
            await StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseHolds();
            _host.Firmware.ReleaseTransfers();
            await _host.DisposeAsync();
        }

        private void ReleaseHolds()
        {
            lock (_held)
            {
                _held.Clear();
            }
            Interlocked.Exchange(ref _doCodeGate, null)?.TrySetResult();
        }

        /// <summary>
        /// Gather the counters of the current host after the run
        /// </summary>
        public void Finish()
        {
            CollectFirmwareCounters();
            Counters.OutOfOrderReplies = _log.Count(LogCounter.OutOfOrderReply) + _log.Count(LogCounter.PendingOutOfOrderReply);
            Counters.WindowDrops = _log.Count(LogCounter.DiscardedMacroRequest) + _log.Count(LogCounter.DiscardedClosal);
            foreach (string format in LogCounter.StaleEventFormats)
            {
                int count = _log.Count(format);
                if (count > 0)
                {
                    Counters.StaleEventEffects += count;
                    Counters.Details[$"DSF: {format}"] = count;
                }
            }
            Counters.StaleEventEffects += _rrfStaleStarts;
        }

        /// <summary>
        /// Add what the fake firmware of the current host recorded: resends and the warnings RRF writes for a stale macro start. Called once per host,
        /// before it is disposed
        /// </summary>
        private void CollectFirmwareCounters()
        {
            FakeFirmware.FakeFirmware firmware = _host.Firmware;
            FakePacket[] packets = [.. firmware.GetSentPackets()];
            foreach (FakePacket packet in packets.Where(packet => packet.Request == FirmwareRequest.ResendPacket &&
                packet.ResendRequest?.Request is not SbcRequest.LockAllMovementSystemsAndWaitForStandstill))
            {
                Counters.Resends++;
                Counters.Detail($"resend: {packet.ResendRequest?.Request}");
                if (packet.ResendRequest?.Request == SbcRequest.InvalidateChannel)
                {
                    Counters.InvalidateChannelResends++;
                }
            }
            int started = packets.Count(packet => packet.Request == FirmwareRequest.Message && packet.Text.Contains("but none was requested"));
            if (started > 0)
            {
                _rrfStaleStarts += started;
                Counters.Details["RRF: Macro file has been started but none was requested"] = _rrfStaleStarts;
            }
            Counters.RrfEpochDrops += firmware.EpochCounterTotal;
            Counters.UntaggedMacroRequests += packets.Count(packet => packet.Request == FirmwareRequest.ExecuteMacro && !packet.IsTagged);
            foreach (Processor processor in _host.Services.GetRequiredService<Manager>())
            {
                using (processor.Lock())
                {
                    foreach (KeyValuePair<FirmwareRequest, int> drop in processor.StaleDrops)
                    {
                        Counters.DsfEpochDrops += drop.Value;
                        Counters.Details[$"DSF epoch drop: {drop.Key}"] = Counters.Details.GetValueOrDefault($"DSF epoch drop: {drop.Key}") + drop.Value;
                    }
                    Counters.DsfEpochDrops += processor.AheadDrops + processor.DroppedResults;
                    Counters.StaleRepliesShown += processor.ShownStaleReplies;
                    if (processor.AheadDrops > 0)
                    {
                        Counters.Details["DSF epoch drop: ahead"] = Counters.Details.GetValueOrDefault("DSF epoch drop: ahead") + processor.AheadDrops;
                    }
                    if (processor.DroppedResults > 0)
                    {
                        Counters.Details["DSF result of an older DoCode not sent"] = Counters.Details.GetValueOrDefault("DSF result of an older DoCode not sent") + processor.DroppedResults;
                    }
                }
            }
        }

        #region Steps
        /// <summary>
        /// Let the fake firmware perform one more transfer and wait until LinkService waits for the next one
        /// </summary>
        private void Step()
        {
            int transfer = _host.Firmware.TransferCount;
            _host.Firmware.HoldTransfers(1);
            if (!_host.Firmware.WaitUntil(firmware => firmware.TransferCount > transfer && firmware.IsTransferHeld, Timeout))
            {
                throw new LinkStoppedException();
            }
        }

        /// <summary>
        /// Perform transfers until a condition holds
        /// </summary>
        /// <param name="condition">Condition</param>
        /// <param name="maxTransfers">Maximum number of transfers</param>
        /// <returns>Whether the condition holds</returns>
        private bool StepUntil(Func<bool> condition, int maxTransfers)
        {
            for (int i = 0; i < maxTransfers; i++)
            {
                if (condition())
                {
                    return true;
                }
                Step();
            }
            return condition();
        }

        /// <summary>
        /// Perform a random number of transfers
        /// </summary>
        private void StepRandom(int max)
        {
            int steps = Rng.Next(max + 1);
            for (int i = 0; i < steps; i++)
            {
                Step();
            }
        }

        /// <summary>
        /// Wait until a started code has reached DSF's channel processor or RRF, so that its place in the order does not depend on the speed of the
        /// pipelines of DCS
        /// </summary>
        private void WaitUntilSent(string code, CodeChannel channel) =>
            StepUntil(() => _host.Firmware.GetPendingRequests().Any(request => request.Request == SbcRequest.Code && request.Channel == channel && request.Code!.Text == code) ||
                _host.Firmware.GetReceivedCodes(channel).Any(received => received.Text == code), 100);
        #endregion

        #region Helpers
        private int NewId() => _nextId++;

        /// <summary>
        /// Start a single code like a client does, but keep a cancellation visible: SimpleCode returns an empty reply for a cancelled code
        /// (SimpleCode.cs:181-184), the code itself throws OperationCanceledException
        /// </summary>
        private Task<Message?> StartCode(string text, CodeChannel channel)
        {
            DuetControlServer.Commands.SimpleCode simpleCode = _host.Services.GetRequiredService<DuetControlServer.Commands.CommandFactory>().Create<DuetControlServer.Commands.SimpleCode>();
            simpleCode.Code = text;
            simpleCode.Channel = channel;
            return Task.Run(async () =>
            {
                Code? parsed = null;
                await foreach (Code item in simpleCode.ParseAsync())
                {
                    parsed = item;
                }
                return await parsed!.ExecuteAsync();
            });
        }

        private int NewMacroId() => _nextMacroId++;

        private string Macro(string name, string content)
        {
            _host.WriteMacro(name, content);
            return name;
        }

        private Processor Dsf(CodeChannel channel) => _host.Services.GetRequiredService<Manager>()[channel];

        private int DsfStackDepth(CodeChannel channel)
        {
            Processor processor = Dsf(channel);
            using (processor.Lock())
            {
                return processor.Stack.Count;
            }
        }

        private bool DsfIdle(CodeChannel channel)
        {
            Processor processor = Dsf(channel);
            using (processor.Lock())
            {
                return processor.Stack.Count == 1 && processor.BufferedCodes.Count == 0;
            }
        }

        private void FailLocksSometimes(CodeChannel channel)
        {
            int failures = Rng.Next(4) switch { 0 => 1, 1 => 2, _ => 0 };
            if (failures > 0)
            {
                _host.Firmware.FailBufferLocks(channel, failures);
            }
        }

        private async Task AbortAsync(CodeChannel channel)
        {
            Counters.Aborts++;
            await _host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(channel).WaitAsync(Timeout);
        }

        private void Hold(int id)
        {
            lock (_held)
            {
                _held.Add(id);
            }
        }

        private void Release(int id)
        {
            lock (_held)
            {
                _held.Remove(id);
            }
        }

        /// <summary>
        /// Check the result of a code that a client started, if it has completed
        /// </summary>
        /// <param name="task">Task of the code</param>
        /// <param name="expected">Expected replies</param>
        /// <param name="what">Description of the code</param>
        private void CheckClientCode(Task<Message?> task, string what, params string[] expected)
        {
            if (!task.IsCompleted)
            {
                return;
            }
            if (task.IsCanceled || task.Exception?.InnerException is OperationCanceledException)
            {
                return;
            }
            if (task.IsFaulted)
            {
                Counters.WrongCompletions++;
                Counters.Detail($"wrong: {what} failed with {task.Exception!.InnerException!.GetType().Name}");
                return;
            }
            string reply = task.Result?.ToString() ?? string.Empty;
            if (!expected.Contains(reply))
            {
                Counters.WrongCompletions++;
                Counters.Detail($"wrong: {what} got {Classify(reply)}");
            }
        }

        private static string Classify(string reply)
        {
            if (reply.StartsWith('r') && reply.Length > 1 && char.IsDigit(reply[1]))
            {
                return "the reply of another code";
            }
            if (reply.Contains("not found"))
            {
                return "'Macro file not found'";
            }
            if (reply.Contains("Awaiting input"))
            {
                return "'Awaiting input, command ignored'";
            }
            if (reply == "\n" || reply.Length == 0)
            {
                return "an empty reply";
            }
            return $"'{reply.Trim()}'";
        }
        #endregion

        /// <summary>
        /// Run one situation with its abort and probe
        /// </summary>
        public async Task CycleAsync(Situation situation)
        {
            try
            {
                await RunSituationAsync(situation);
            }
            catch (LinkStoppedException)
            {
                Counters.DcsStops++;
                Counters.Detail($"stop: link thread ended in {situation}");
                await RestartAsync();
            }
            if (IsLinkStopped())
            {
                Counters.DcsStops++;
                Counters.Detail($"stop: link thread ended in {situation}");
                await RestartAsync();
            }
        }

        private bool IsLinkStopped()
        {
            LinkService? link = _host.Services.GetServices<IHostedService>().OfType<LinkService>().FirstOrDefault();
            return link?.ExecuteTask is { IsCompleted: true };
        }

        private async Task RunSituationAsync(Situation situation)
        {
            CodeChannel channel = situation switch
            {
                Situation.TriggerRequest => CodeChannel.Trigger,
                Situation.DaemonRequest => CodeChannel.Daemon,
                Situation.DoCode => DoCodeChannel,
                _ => ClientChannels[Rng.Next(ClientChannels.Length)]
            };
            List<(Task<Message?> Task, string What, string[] Expected)> codes = [];
            List<int> heldIds = [];

            switch (situation)
            {
                case Situation.MacroRequest:
                    {
                        string macro = Macro($"stress-{channel}.g", $"G4 P{NewMacroId()}\n");
                        string m98 = $"M98 P\"{macro}\"";
                        int executed = _host.Firmware.GetExecutedCodes(channel).Count(code => code.Text == m98);
                        codes.Add((StartCode(m98, channel), "M98", ["\n"]));
                        StepUntil(() => _host.Firmware.GetExecutedCodes(channel).Count(code => code.Text == m98) > executed || codes[0].Task.IsCompleted, 50);
                        StepRandom(3);
                        FailLocksSometimes(channel);
                        await AbortAsync(channel);
                        break;
                    }

                case Situation.RunningCode:
                    {
                        int id = NewId();
                        Hold(id);
                        heldIds.Add(id);
                        string g4 = $"G4 P{id}";
                        codes.Add((StartCode(g4, channel), g4, [$"r{id}\n"]));
                        StepUntil(() => _host.Firmware.GetCurrentCode(channel)?.Text == g4, 50);
                        FailLocksSometimes(channel);
                        await AbortAsync(channel);
                        break;
                    }

                case Situation.RrfAbortAll:
                    {
                        int id = NewMacroId();
                        Hold(id);
                        heldIds.Add(id);
                        string macro = Macro($"stress-{channel}.g", $"G4 P{id}\nG4 P{NewMacroId()}\n");
                        string m98 = $"M98 P\"{macro}\"";
                        codes.Add((StartCode(m98, channel), "M98", ["\n"]));
                        StepUntil(() => _host.Firmware.GetCurrentCode(channel)?.Text == $"G4 P{id}", 50);
                        int before = Rng.Next(3);
                        if (before == 0)
                        {
                            await AbortAsync(channel);
                            _host.Firmware.AbortAllFiles(channel);
                        }
                        else
                        {
                            _host.Firmware.AbortAllFiles(channel);
                            StepRandom(before - 1);
                            await AbortAsync(channel);
                        }
                        break;
                    }

                case Situation.TriggerRequest:
                    {
                        int trigger = 2 + Rng.Next(8);
                        Macro($"trigger{trigger}.g", $"G4 P{NewMacroId()}\n");
                        _host.Firmware.FireTrigger(trigger);
                        StepRandom(4);
                        FailLocksSometimes(channel);
                        await AbortAsync(channel);
                        break;
                    }

                case Situation.DaemonRequest:
                    {
                        Macro("daemon.g", $"G4 P{NewMacroId()}\n");
                        _host.Firmware.RequestMacro(CodeChannel.Daemon, "daemon.g");
                        StepRandom(4);
                        FailLocksSometimes(channel);
                        await AbortAsync(channel);
                        break;
                    }

                case Situation.MacroFileClosed:
                    {
                        string macroCode = $"G4 P{NewMacroId()}";
                        string macro = Macro($"stress-{channel}.g", macroCode + "\n");
                        string m98 = $"M98 P\"{macro}\"";
                        codes.Add((StartCode(m98, channel), "M98", ["\n"]));
                        bool closedInRrf = Rng.Next(2) == 0;
                        StepUntil(() => closedInRrf
                            ? _host.Firmware.GetStackDepth(channel) == 0 && _host.Firmware.GetExecutedCodes(channel).Any(code => code.Text == macroCode)
                            : _host.Firmware.GetPendingRequests().Any(request => request.Request == SbcRequest.MacroCompleted && request.Channel == channel), 50);
                        StepRandom(1);
                        await AbortAsync(channel);
                        break;
                    }

                case Situation.MessageBox:
                    {
                        bool inMacro = Rng.Next(2) == 0;
                        string box = $"M291 P\"box {NewId()}\" S2";
                        if (inMacro)
                        {
                            string macro = Macro($"stress-{channel}.g", box + "\n");
                            codes.Add((StartCode($"M98 P\"{macro}\"", channel), "M98 with a box", ["\n"]));
                        }
                        else
                        {
                            codes.Add((StartCode(box, channel), "M291", ["\n"]));
                        }
                        StepUntil(() => Dsf(channel).IsWaitingForAcknowledgment, 50);
                        StepRandom(1);
                        await AbortAsync(channel);
                        break;
                    }

                case Situation.DoCode:
                    {
                        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _doCodeGate = gate;
                        _host.Firmware.PassCodeToSbc(channel, "M111 P-1");
                        int messagesBefore = _host.Firmware.GetReceivedMessages().Count;
                        StepUntil(() => _host.Firmware.GetSentPackets().Count(packet => packet.Request == FirmwareRequest.DoCode) > _doCodesWritten, 50);
                        _doCodesWritten = _host.Firmware.GetSentPackets().Count(packet => packet.Request == FirmwareRequest.DoCode);
                        StepRandom(2);
                        await AbortAsync(channel);
                        if (Rng.Next(2) == 0)
                        {
                            _host.Firmware.PassCodeToSbc(channel, "M929");
                        }
                        StepRandom(3);
                        Interlocked.Exchange(ref _doCodeGate, null)?.TrySetResult();

                        // M929 flushes its channel in DSF (MCodeHandler.cs:1150), so a probe buffered on that channel would wait for M929 while RRF holds
                        // the probe until M929 has its result. The probe goes out once RRF has nothing of the channel left, a channel that never gets
                        // there is blocked (K13)
                        DateTime idleTime = DateTime.UtcNow + BlockTime;
                        int idleTransfer = _host.Firmware.TransferCount;
                        while (!_host.Firmware.IsIdle(channel) && (_host.Firmware.TransferCount - idleTransfer < BlockTransfers || DateTime.UtcNow < idleTime))
                        {
                            Step();
                        }
                        if (!_host.Firmware.IsIdle(channel))
                        {
                            Counters.Blocks++;
                            Counters.Detail($"block: {situation} on {channel}");
                            await RestartAsync();
                            return;
                        }
                        break;
                    }
            }

            // The probe must get its own reply
            int probeId = NewId();
            string probe = $"G4 P{probeId}";
            Task<Message?> probeTask = StartCode(probe, channel);
            WaitUntilSent(probe, channel);

            // Let held codes of RRF finish at some point while the probe waits
            foreach (int id in heldIds)
            {
                StepRandom(3);
                Release(id);
            }
            Interlocked.Exchange(ref _doCodeGate, null)?.TrySetResult();

            DateTime blockTime = DateTime.UtcNow + BlockTime;
            int transfer = _host.Firmware.TransferCount;
            while (!probeTask.IsCompleted && (_host.Firmware.TransferCount - transfer < BlockTransfers || DateTime.UtcNow < blockTime))
            {
                Step();
            }

            if (!probeTask.IsCompleted)
            {
                Counters.Blocks++;
                Counters.Detail($"block: {situation} on {channel}");
                await RestartAsync();
                return;
            }
            if (probeTask.IsCanceled || probeTask.Exception?.InnerException is OperationCanceledException)
            {
                Counters.ProbesCancelled++;
                Counters.Detail($"probe cancelled: {situation}");
            }
            else
            {
                CheckClientCode(probeTask, $"probe of {situation}", $"r{probeId}\n");
            }

            // Bring both sides back to an idle channel. A message box that the abort left open in RRF is closed as a user would do it
            if (!StepUntil(() => _host.Firmware.IsIdle(channel) && DsfIdle(channel) && codes.All(code => code.Task.IsCompleted), 100))
            {
                if (_host.Firmware.MessageBoxCount > 0)
                {
                    Task<Message?> m292 = StartCode("M292 P1", channel);
                    StepUntil(() => m292.IsCompleted, 50);
                }
                if (!StepUntil(() => _host.Firmware.IsIdle(channel) && DsfIdle(channel) && codes.All(code => code.Task.IsCompleted), 200))
                {
                    Counters.Blocks++;
                    Counters.Detail($"block after the probe: {situation} on {channel}");
                    await RestartAsync();
                    return;
                }
            }
            foreach ((Task<Message?> task, string what, string[] expected) in codes)
            {
                CheckClientCode(task, what, expected);
            }
            CheckDoCodeResults();
        }

        private int _doCodesWritten, _doCodeResultsChecked;

        /// <summary>
        /// Every result of a code RRF passed on must finish that code. The fake records which code a result finished
        /// </summary>
        private void CheckDoCodeResults()
        {
            IReadOnlyList<(string Code, string Result)> finished = _host.Firmware.GetFinishedSbcCodes();
            for (; _doCodeResultsChecked < finished.Count; _doCodeResultsChecked++)
            {
                (string code, string result) = finished[_doCodeResultsChecked];
                bool matches = code switch
                {
                    "M111 P-1" => result.StartsWith("Current DCS log level"),
                    "M929" => result.StartsWith("Event logging"),
                    _ => true
                };
                if (!matches)
                {
                    Counters.WrongCompletions++;
                    Counters.Detail($"wrong: {code} passed on by RRF was finished by the result '{result.Trim()}'");
                }
            }
        }

        /// <summary>
        /// M-code handler of DCS that holds the codes RRF passes on while the gate of the run is closed
        /// </summary>
        private sealed class HoldingMCodeHandler(ICodeHandler handler, StressRun run) : ICodeHandler
        {
            public async ValueTask<Message?> ProcessAsync(Code code, CancellationToken cancellationToken)
            {
                if (code.Flags.HasFlag(CodeFlags.IsFromFirmware) && code.Type == CodeType.MCode && code.MajorNumber == 111)
                {
                    Task? gate = run._doCodeGate?.Task;
                    if (gate is not null)
                    {
                        await gate.WaitAsync(cancellationToken);
                    }
                }
                return await handler.ProcessAsync(code, cancellationToken);
            }

            public ValueTask CodeExecutedAsync(Code code, CancellationToken cancellationToken) => handler.CodeExecutedAsync(code, cancellationToken);
        }
    }

    /// <summary>
    /// Raised when the link thread of DCS no longer performs transfers
    /// </summary>
    private sealed class LinkStoppedException : Exception;

    /// <summary>
    /// Logger provider that counts the log entries of DCS by their message template
    /// </summary>
    private sealed class LogCounter : ILoggerProvider
    {
        public const string OutOfOrderReply = "Out-of-order reply (flags {Flags}): '{Reply}'";
        public const string PendingOutOfOrderReply = "Pending out-of-order reply: '{Reply}'";
        public const string DiscardedMacroRequest = "Macro file {File} was requested before RRF got the abort of all files. Discarding request.";
        public const string DiscardedClosal = "Macro file was closed before RRF got the abort of all files. Discarding closal.";

        /// <summary>
        /// Log entries that show an event of aborted work acting on another level
        /// </summary>
        public static readonly string[] StaleEventFormats =
        [
            "Tried to acknowledge a message, but no acknowledgement is requested!",
            "Received a lock confirmation for a non-existent request!",
            "System macro {File} is requested but the stack is not empty. Discarding request.",
            "Aborting orphaned macro file {File}",
            "Unresolved evaluation result for expression {Expression} = {Result}",
            "Unresolved variable set result for {Channel}: {Variable} = {Result}"
        ];

        private readonly ConcurrentDictionary<string, int> _counts = new();

        public int Count(string format) => _counts.GetValueOrDefault(format);

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose() { }

        private sealed class Logger(LogCounter counter) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (state is IReadOnlyList<KeyValuePair<string, object?>> values && values.Count > 0 && values[^1].Key == "{OriginalFormat}" && values[^1].Value is string format)
                {
                    counter._counts.AddOrUpdate(format, 1, (_, count) => count + 1);
                }
            }
        }
    }
}
