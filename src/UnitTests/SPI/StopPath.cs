using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Link;
using DuetControlServer.Link.Channel;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using Host = UnitTests.SPI.LinkServiceOverSpi.Host;
using RrfPackets = UnitTests.SPI.LinkServiceOverSpi.RrfPackets;
using SbcPackets = UnitTests.SPI.LinkServiceOverSpi.SbcPackets;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.SPI;

/// <summary>
/// The stop path while the SPI link is disturbed (R10 of design/sbc-link-requirements.md): an emergency stop that DSF requests, and the start of a
/// trigger macro that RRF requests, with the real LinkService on the real SPI adapter against the SPI port of RRF (<see cref="FakeRrfSpi"/>)
/// </summary>
/// <remarks>
/// The emergency stop takes the path of M112: SimpleCode marks M112 as prioritized (SimpleCode.cs:110-116), MCodeHandler calls
/// LinkInterface.EmergencyStopAsync (MCodeHandler.cs:749-772), which sets FirmwareHaltRequest (LinkInterface.cs:339-350), the transfer thread
/// writes the EmergencyStop request into its next transfer (LinkService.cs:270-281) and completes the request once the transfer that carried it is
/// done and the data RRF sent with it is processed (LinkService.cs:398-400, 542). RRF performs the emergency stop as soon as it reads the
/// request (SbcInterface.cpp:336-338), and also whenever it loses an established connection (SbcInterface.cpp:194-230).
/// A trigger macro starts in RRF with DoFileMacro(..., AsyncSystemMacroCode) for trigger&lt;N&gt;.g (GCodes.cpp:1050-1052), which asks the SBC for
/// the file with fromCode false (GCodes.cpp:3699, GCodeBuffer.cpp:1243-1283) through WriteMacroRequest (DataTransfer.cpp:1543-1564). DSF starts the
/// file (LinkService.HandleMacroRequest, Processor.DoMacroFile), tells RRF with MacroStarted (Processor.cs:899-907), sends the codes and ends with
/// MacroCompleted (Processor.cs:909-914).
/// Time is virtual (<see cref="FakeRrfSpi.Now"/>). DSF is held at a header exchange while a test makes its request, so the request always
/// misses the transfer DSF has set up already, and the work DSF does in the background (opening the macro file, parsing its codes, the code
/// pipeline) takes no virtual time (<see cref="FakeRrfSpi.HoldWhile"/>), so the latencies are those of the link alone. On the SBC that work
/// takes real time on top. RRF arms the next transfer at once unless <see cref="StopCase.Delay"/> is set, see <see cref="FakeRrfSpi.TransferDelay"/>
/// </remarks>
[TestFixture]
public partial class StopPath
{
    private static readonly TimeSpan Timeout = LinkServiceOverSpi.Timeout;

    #region Input
    /// <summary>
    /// What happens on the link
    /// </summary>
    public enum Scenario
    {
        /// <summary>
        /// DSF requests an emergency stop
        /// </summary>
        Stop,

        /// <summary>
        /// RRF requests a trigger macro
        /// </summary>
        Trigger,

        /// <summary>
        /// RRF requests a trigger macro and DSF requests an emergency stop while it is being started
        /// </summary>
        Both
    }

    /// <summary>
    /// Part of the scenario from whose start the exchanges of a fault count
    /// </summary>
    public enum Leg
    {
        /// <summary>
        /// From the header of the transfer DSF has set up when the emergency stop is requested. The stop goes out in the transfer after it
        /// </summary>
        Stop,

        /// <summary>
        /// From the header of the transfer RRF has set up when the trigger fires. The macro request goes out in the transfer after it,
        /// MacroStarted in the transfer after that
        /// </summary>
        Request,

        /// <summary>
        /// From the header of the transfer that carries the first code of the macro
        /// </summary>
        Code
    }

    /// <summary>
    /// When DSF requests the emergency stop in <see cref="Scenario.Both"/>
    /// </summary>
    public enum StopMoment
    {
        /// <summary>
        /// When the trigger fires
        /// </summary>
        Trigger,

        /// <summary>
        /// At the header of the transfer that carries the macro request to DSF
        /// </summary>
        Request,

        /// <summary>
        /// At the header of the transfer that carries the first code of the macro to RRF
        /// </summary>
        FirstCode
    }

    /// <summary>
    /// A bit error
    /// </summary>
    /// <param name="Leg">Part of the scenario from whose start <paramref name="Exchange"/> counts</param>
    /// <param name="Exchange">SPI exchange that gets it. Retries shift the exchanges after an error</param>
    /// <param name="Direction">Direction that gets it</param>
    /// <param name="Bit">Bit that flips, counted from bit 0 of the first byte, modulo the length of the exchange</param>
    public sealed record Flip(Leg Leg, int Exchange, ExchangeDirection Direction, int Bit);

    /// <summary>
    /// RRF restarts right before an exchange
    /// </summary>
    /// <param name="Leg">Part of the scenario from whose start <paramref name="Exchange"/> counts</param>
    /// <param name="Exchange">Exchange before which RRF restarts</param>
    /// <param name="BootTime">Time until the restarted RRF has booted in ms</param>
    public sealed record Restart(Leg Leg, int Exchange, double BootTime);

    /// <summary>
    /// Input of the stop path tests
    /// </summary>
    /// <param name="Scenario">What happens on the link</param>
    /// <param name="Capabilities">Capabilities RRF offers and DSF activates: <see cref="LinkCapabilities.All"/> for the extended link of the fork,
    /// <see cref="LinkCapabilities.None"/> for the plain v7 link</param>
    /// <param name="Flips">Bit errors</param>
    public sealed record StopCase(Scenario Scenario, LinkCapabilities Capabilities, IReadOnlyList<Flip> Flips)
    {
        /// <summary>
        /// Trigger macro RRF requests: trigger2.g is the external emergency stop input, trigger4.g the door
        /// </summary>
        public string Macro { get; init; } = "trigger2.g";

        /// <summary>
        /// When DSF requests the emergency stop in <see cref="Scenario.Both"/>
        /// </summary>
        public StopMoment StopAt { get; init; }

        /// <summary>
        /// Number of idle transfers after the setup before the scenario starts. The extended counter of the sequence numbers has 32 values
        /// (ExtendedLink.CounterBits), so this puts the transfers of the scenario on each of them
        /// </summary>
        public int Offset { get; init; }

        /// <summary>
        /// Time RRF waits between two transfers, see <see cref="FakeRrfSpi.TransferDelay"/>, only for <see cref="Scenario.Stop"/>
        /// </summary>
        public double Delay { get; init; }

        /// <summary>
        /// Share of <see cref="Delay"/> that has passed when the emergency stop is requested, from 0 to 1
        /// </summary>
        public double Phase { get; init; }

        /// <summary>
        /// Probability of each bit to flip while a part of the scenario runs, see <see cref="FakeRrfSpi.BitErrorRate"/>
        /// </summary>
        public double BitErrorRate { get; init; }

        /// <summary>
        /// Seed of the random bit errors
        /// </summary>
        public int Seed { get; init; } = 1;

        /// <summary>
        /// Restart of RRF, if any
        /// </summary>
        public Restart? Restart { get; init; }

        /// <summary>
        /// Text that <see cref="Parse"/> reads, for regression tests
        /// </summary>
        public override string ToString()
        {
            StringBuilder text = new($"{Scenario} {Capabilities}");
            if (Scenario != Scenario.Stop)
            {
                text.Append($" macro={Macro}");
            }
            if (Scenario == Scenario.Both)
            {
                text.Append($" at={StopAt}");
            }
            if (Offset > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $" offset={Offset}");
            }
            if (Delay > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $" delay={Delay} phase={Phase}");
            }
            if (BitErrorRate > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $" ber={BitErrorRate} seed={Seed}");
            }
            if (Restart is Restart restart)
            {
                text.Append(CultureInfo.InvariantCulture, $" restart={restart.Leg}:{restart.Exchange}:{restart.BootTime}");
            }
            foreach (Flip flip in Flips)
            {
                text.Append($" | {flip.Leg} {flip.Exchange} {flip.Direction} {flip.Bit}");
            }
            return text.ToString();
        }

        /// <summary>
        /// Read an input from its text
        /// </summary>
        public static StopCase Parse(string text)
        {
            string[] parts = text.Split(" | ");
            string[] head = parts[0].Split(' ');
            StopCase input = new(Enum.Parse<Scenario>(head[0]), Enum.Parse<LinkCapabilities>(head[1]),
                [.. parts.Skip(1).Select(p => p.Split(' ')).Select(f => new Flip(Enum.Parse<Leg>(f[0]), int.Parse(f[1], CultureInfo.InvariantCulture),
                    Enum.Parse<ExchangeDirection>(f[2]), int.Parse(f[3], CultureInfo.InvariantCulture)))]);
            foreach (string option in head.Skip(2))
            {
                string[] pair = option.Split('=');
                input = pair[0] switch
                {
                    "macro" => input with { Macro = pair[1] },
                    "at" => input with { StopAt = Enum.Parse<StopMoment>(pair[1]) },
                    "offset" => input with { Offset = int.Parse(pair[1], CultureInfo.InvariantCulture) },
                    "delay" => input with { Delay = double.Parse(pair[1], CultureInfo.InvariantCulture) },
                    "phase" => input with { Phase = double.Parse(pair[1], CultureInfo.InvariantCulture) },
                    "ber" => input with { BitErrorRate = double.Parse(pair[1], CultureInfo.InvariantCulture) },
                    "seed" => input with { Seed = int.Parse(pair[1], CultureInfo.InvariantCulture) },
                    "restart" => input with
                    {
                        Restart = new(Enum.Parse<Leg>(pair[1].Split(':')[0]), int.Parse(pair[1].Split(':')[1], CultureInfo.InvariantCulture),
                            double.Parse(pair[1].Split(':')[2], CultureInfo.InvariantCulture))
                    },
                    _ => throw new ArgumentException($"Unknown option {option}", nameof(text))
                };
            }
            return input;
        }

        /// <summary>
        /// Whether DSF requests an emergency stop
        /// </summary>
        public bool HasStop => Scenario != Scenario.Trigger;

        /// <summary>
        /// Whether RRF requests a trigger macro
        /// </summary>
        public bool HasTrigger => Scenario != Scenario.Stop;
    }
    #endregion

    #region Result
    /// <summary>
    /// What happened in a scenario. Times are virtual and in ms, counted from the request of the stop or the trigger, null if it did not happen
    /// </summary>
    internal sealed record StopResult
    {
        /// <summary>
        /// Time until RRF was stopped by the EmergencyStop request, by an emergency stop because it lost the connection, or by a restart
        /// </summary>
        public double? StopLatency { get; init; }

        /// <summary>
        /// Time until RRF processed the EmergencyStop request
        /// </summary>
        public double? PacketLatency { get; init; }

        /// <summary>
        /// Number of EmergencyStop requests RRF processed
        /// </summary>
        public int StopPackets { get; init; }

        /// <summary>
        /// Time until DSF completed the emergency stop, which lets M112 reply
        /// </summary>
        public double? HaltLatency { get; init; }

        /// <summary>
        /// Whether DSF completed the emergency stop before RRF was stopped
        /// </summary>
        public bool HaltBeforeStop { get; init; }

        /// <summary>
        /// Whether DSF completed the emergency stop even before RRF had accepted the data that carries it with a valid checksum
        /// </summary>
        public bool HaltBeforeData { get; init; }

        /// <summary>
        /// How RRF was stopped first: "packet", "disconnect", "restart" or "never"
        /// </summary>
        public string StoppedBy { get; init; } = "never";

        /// <summary>
        /// Time until RRF processed MacroStarted
        /// </summary>
        public double? MacroStartedLatency { get; init; }

        /// <summary>
        /// Time until RRF processed the first code of the macro
        /// </summary>
        public double? FirstCodeLatency { get; init; }

        /// <summary>
        /// Time until RRF processed MacroCompleted
        /// </summary>
        public double? MacroCompletedLatency { get; init; }

        /// <summary>
        /// Lines of the macro codes RRF processed, in order
        /// </summary>
        public IReadOnlyList<int> CodeLines { get; init; } = [];

        /// <summary>
        /// Number of times RRF processed MacroStarted and MacroCompleted for the trigger channel
        /// </summary>
        public int MacroStarts { get; init; }

        /// <inheritdoc cref="MacroStarts"/>
        public int MacroCompletions { get; init; }

        /// <summary>
        /// Number of macro codes RRF processed after the EmergencyStop request
        /// </summary>
        public int CodesAfterStop { get; init; }

        /// <summary>
        /// Status of the machine in the object model of DSF at the end
        /// </summary>
        public MachineStatus Status { get; init; }

        /// <summary>
        /// What a reconnect looked like: whether RRF lost the connection (and stopped), reset it without having one, restarted, and whether DSF lost it or noticed a reset
        /// </summary>
        public string ReconnectKind { get; init; } = string.Empty;

        /// <summary>
        /// How the trigger macro ended: "completed", "RRF stopped" if RRF processed an emergency stop, lost the connection or restarted instead, "lost" if none
        /// of these, or empty without a trigger
        /// </summary>
        public string TriggerOutcome { get; init; } = string.Empty;

        /// <summary>
        /// The input with the bit errors of <see cref="StopCase.BitErrorRate"/> as single flips, which repeats the run and can be shrunk, or null without them
        /// </summary>
        public StopCase? NoiseAsFlips { get; init; }

        /// <summary>
        /// Whether either side lost or reset the connection or RRF restarted
        /// </summary>
        public bool Reconnected { get; init; }

        /// <summary>
        /// Whether both sides kept exchanging without RRF completing a transfer, which only the restart of DCS after Settings.SbcRestartTimeout ends
        /// </summary>
        public bool Stuck { get; init; }

        /// <summary>
        /// Properties the scenario violated, as (property, description)
        /// </summary>
        public IReadOnlyList<(string Property, string Message)> Violations { get; init; } = [];

        /// <summary>
        /// What happened
        /// </summary>
        public string Details { get; init; } = string.Empty;
    }
    #endregion

    #region Scenario
    /// <summary>
    /// Number of exchanges of the setup: connect, query and activate the capabilities, switch to the extended link. DSF is held at the first header after it
    /// </summary>
    private const int SetupExchanges = 64;

    /// <summary>
    /// Exchanges after the end of a scenario in which a repeated request would still show
    /// </summary>
    private const int TailExchanges = 24;

    /// <summary>
    /// Number of exchanges without a transfer that RRF completes after which the link counts as stuck. A recovery takes at most a few times the retries of
    /// DSF (Settings.MaxSbcRetries) per phase, and after that DSF waits for the transfer ready pin without exchanging (SPI.WaitForTransfer)
    /// </summary>
    private const int StuckExchanges = 10000;

    /// <summary>
    /// Virtual time after which a scenario ends without its requests done. Longer than the connection timeouts of both sides (4 s, SbcMessageFormats.h:45 and
    /// Settings.SbcConnectionTimeout) and the reconnect wait of DSF (4.5 s, SPI.WaitForTransfer), so that a lost connection comes back within it
    /// </summary>
    private const double VirtualTimeLimit = 15000;

    /// <summary>
    /// Number of bytes RRF has for buffered codes (SbcMessageFormats.h:47, SbcCodeBufferSize)
    /// </summary>
    private const int SbcCodeBufferSize = 4096;

    /// <summary>
    /// Trigger macros of the scenarios. Their content is made up: plain codes that DSF passes on to RRF (GCodeHandler has no case of its own), one per line,
    /// so that the line numbers in the code headers tell the order. Fewer lines than Settings.BufferedMacroCodes, so that DSF reads all of them at once
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, int> Macros = new Dictionary<string, int> { ["trigger2.g"] = 3, ["trigger4.g"] = 5 };

    private static string MacroContent(int lines) => string.Concat(Enumerable.Range(1, lines).Select(line => $"G4 P{line}\n"));

    /// <summary>
    /// Observes the scenario from the thread of DSF: what RRF processed (<see cref="FakeRrfSpi.Respond"/>) and the state before each exchange
    /// (<see cref="FakeRrfSpi.BeforeExchange"/>). Every member is used under the lock of the probe
    /// </summary>
    private sealed class Probe(StopCase input)
    {
        public FakeRrfSpi Rrf = null!;
        public Processor? Trigger;
        public readonly StopCase Input = input;

        // Stop
        public double? StopRequestedAt, StopPacketAt, StopDataAcceptedAt, HaltSeenAt;
        public bool HaltBeforeStop, HaltBeforeData;
        public int StopPackets, CodesAfterStop;
        public Task? HaltTask;

        // Trigger
        public double? TriggerAt, MacroStartedAt, FirstCodeAt, MacroCompletedAt;
        public bool CodeLegStarted, HoldAtFirstCode, FirstCodeHoldRequested, MacroError;
        public int MacroStarts, MacroCompletions;
        public readonly List<int> CodeLines = [];

        // Both sides
        public double StartTime;
        public int StartDisconnects, StartConnectionResets, StartRestarts, DsfConnectionLosses;
        public bool TailStarted, TimedOut, Stuck;
        public int LastCompletedTransfers = -1, ExchangesAtLastCompletion;

        // Exchange from which the faults of each part count
        public readonly Dictionary<Leg, int> LegStarts = [];
        public readonly List<(string Property, string Message)> Violations = [];

        /// <summary>
        /// Codes of the macro
        /// </summary>
        public int MacroLines => Macros[Input.Macro];

        /// <summary>
        /// When RRF was stopped first, and how
        /// </summary>
        public (double? Time, string How) Stopped
        {
            get
            {
                List<(double Time, string How)> stops = [];
                if (StopPacketAt is double packet)
                {
                    stops.Add((packet, "packet"));
                }
                if (Rrf.DisconnectTimes.Skip(StartDisconnects).FirstOrDefault(t => t >= StopRequestedAt, double.NaN) is double disconnect && !double.IsNaN(disconnect))
                {
                    stops.Add((disconnect, "disconnect"));
                }
                if (Rrf.RestartTimes.Skip(StartRestarts).FirstOrDefault(t => t >= StopRequestedAt, double.NaN) is double restart && !double.IsNaN(restart))
                {
                    stops.Add((restart, "restart"));
                }
                return (stops.Count > 0) ? stops.MinBy(s => s.Time) : (null, "never");
            }
        }

        /// <summary>
        /// Whether either side lost or reset the connection or RRF restarted since the start
        /// </summary>
        public bool Reconnected => DsfConnectionLosses > 0 || Rrf.Disconnects > StartDisconnects || Rrf.ConnectionResets > StartConnectionResets || Rrf.RestartTimes.Count > StartRestarts;

        /// <summary>
        /// Whether the requests of the scenario are done, so that the exchanges after it can start: DSF has completed the emergency stop, which
        /// ends the macro too, or RRF has processed the end of the macro, or the macro has gone with a reconnect
        /// </summary>
        private bool Done
        {
            get
            {
                bool triggerDone = MacroCompletedAt is not null || (Reconnected && Trigger?.CurrentState.File is not MacroFile);
                return Input.Scenario switch
                {
                    Scenario.Stop => HaltSeenAt is not null,
                    Scenario.Trigger => triggerDone,
                    _ => HaltSeenAt is not null || (StopRequestedAt is null && triggerDone)
                } || Stuck;
            }
        }

        /// <summary>
        /// Whether a part of the scenario runs, during which the random bit errors are on
        /// </summary>
        private bool LegActive =>
            !TailStarted && ((StopRequestedAt is not null && Stopped.Time is null) ||
                (TriggerAt is not null && MacroStartedAt is null && !Reconnected) ||
                (CodeLegStarted && FirstCodeAt is null && !Reconnected));

        /// <summary>
        /// Answers of RRF, written into the next transfer like SbcInterface::ExchangeData writes them while it processes a transfer
        /// </summary>
        public IEnumerable<(FirmwareRequest Request, byte[] Payload, ushort ResendPacketId)> Respond(SbcRequest request, ushort id, byte[] payload)
        {
            lock (this)
            {
                switch (request)
                {
                    case SbcRequest.GetObjectModel:
                        // RRF answers with the key, the flags and the result (SbcInterface.cpp:443-494). It offers the capabilities in limits.sbcLinkCapabilities,
                        // null if it has none, and reports the active ones in state.sbcLinkCapabilities (design/sbc-link-requirements.md, C1)
                        (string key, string flags) = SbcPackets.GetObjectModel(payload);
                        string? result = key switch
                        {
                            "limits.sbcLinkCapabilities" => (Input.Capabilities == LinkCapabilities.None) ? "null" : ((uint)Input.Capabilities).ToString(CultureInfo.InvariantCulture),
                            "state.sbcLinkCapabilities" => ((uint)Rrf.ActiveCapabilities).ToString(CultureInfo.InvariantCulture),
                            _ => null
                        };
                        if (result is not null)
                        {
                            return [(FirmwareRequest.ObjectModel, RrfPackets.String($"{{\"key\":\"{key}\",\"flags\":\"{flags}\",\"result\":{result}}}"), 0)];
                        }
                        break;

                    case SbcRequest.EmergencyStop:
                        if (StopRequestedAt is not null)
                        {
                            StopPackets++;
                            if (StopPacketAt is null)
                            {
                                StopPacketAt = Rrf.Now;
                            }
                        }
                        break;

                    case SbcRequest.MacroStarted:
                        if (SbcPackets.Channel(payload) == CodeChannel.Trigger && TriggerAt is not null)
                        {
                            MacroStarts++;
                            MacroStartedAt ??= Rrf.Now;
                        }
                        break;

                    case SbcRequest.Code:
                        if (SbcPackets.Code(payload).Channel == CodeChannel.Trigger && TriggerAt is not null)
                        {
                            // CodeHeader is followed by the line number (BinaryCodeLayout, CodeHeaderSize)
                            CodeLines.Add(BitConverter.ToInt32(payload, 16));
                            FirstCodeAt ??= Rrf.Now;
                            if (StopPacketAt is not null)
                            {
                                CodesAfterStop++;
                            }

                            // RRF replies to every code, here with an empty reply for its channel (DataTransfer::WriteCodeReply), and reports the space of its
                            // code buffer after storing a code (SbcInterface.cpp:433 and 1370-1380)
                            return [(FirmwareRequest.Message, RrfPackets.Message(MessageTypeFlags.BinaryCodeReplyFlag | MessageTypeFlags.TriggerMessage, string.Empty), 0),
                                (FirmwareRequest.CodeBufferUpdate, RrfPackets.CodeBufferUpdate(SbcCodeBufferSize), 0)];
                        }
                        break;

                    case SbcRequest.MacroCompleted:
                        (CodeChannel channel, bool error) = SbcPackets.MacroCompleted(payload);
                        if (channel == CodeChannel.Trigger && TriggerAt is not null)
                        {
                            MacroCompletions++;
                            MacroError |= error;
                            MacroCompletedAt ??= Rrf.Now;

                            // RRF finishes the file (SbcInterface.cpp:585) and tells DSF once it is closed (SbcInterface.cpp:1486-1490), here with the next transfer
                            if (!error)
                            {
                                return [(FirmwareRequest.MacroFileClosed, RrfPackets.Channel(channel), 0)];
                            }
                        }
                        break;
                }
                return [];
            }
        }

        /// <summary>
        /// Called on the thread of DSF before every exchange
        /// </summary>
        public void BeforeExchange()
        {
            lock (this)
            {
                // Whether DSF completed the emergency stop before RRF was stopped. RRF does its part within an exchange and DSF completes the request
                // between two, so the state at the start of an exchange shows the order
                if (HaltTask is { IsCompleted: true } && HaltSeenAt is null)
                {
                    HaltSeenAt = Rrf.Now;
                    if (Stopped.Time is null)
                    {
                        // DSF completes the request once it has the Success of RRF for the data response (LinkService.cs:398-400, 542). RRF processes the
                        // data only once it has the Success of DSF for it (DataTransfer.cpp ExchangingDataResponse), which nothing confirms to DSF
                        HaltBeforeStop = true;
                        HaltBeforeData = StopDataAcceptedAt is null;
                    }
                }

                // The transfer that carries the first code of the macro: DSF has written it into this transfer in the last cycle (Processor.Spin)
                if (TriggerAt is not null && !CodeLegStarted && MacroStartedAt is not null && Trigger?.BufferedCodes.Count > 0)
                {
                    CodeLegStarted = true;
                    LegStarts[Leg.Code] = Rrf.Exchanges;
                    List<SpiFault> faults = [.. Faults(Input, Leg.Code)];
                    if (faults.Count > 0)
                    {
                        Rrf.ArmAlso(faults);
                    }
                    if (HoldAtFirstCode)
                    {
                        FirstCodeHoldRequested = true;
                        Rrf.HoldBeforeHeader(Rrf.Exchanges);
                    }
                }

                // Both sides keep exchanging without RRF completing a transfer, so no timeout of RRF ends it. Only DCS stops after
                // Settings.SbcRestartTimeout without a complete transfer (SPI.PerformFullTransfer)
                if (Rrf.CompletedTransfers != LastCompletedTransfers)
                {
                    LastCompletedTransfers = Rrf.CompletedTransfers;
                    ExchangesAtLastCompletion = Rrf.Exchanges;
                }
                else if (!Stuck && StartTime > 0 && Rrf.Exchanges - ExchangesAtLastCompletion > StuckExchanges)
                {
                    Stuck = true;
                }

                // A few more exchanges after the end of the scenario, without faults, in which a repeated request would still show
                if (!TailStarted && Done)
                {
                    StartTail();
                }

                Rrf.BitErrorRate = LegActive ? Input.BitErrorRate : 0;
            }
        }

        /// <summary>
        /// Hold DSF after the exchanges that follow the scenario. Call this with the lock of the probe taken
        /// </summary>
        public void StartTail()
        {
            TailStarted = true;
            if (Stuck || TimedOut)
            {
                // A stuck link may not exchange a header any more
                Rrf.HoldBeforeExchange(Rrf.Exchanges);
            }
            else
            {
                Rrf.HoldBeforeHeader(Rrf.Exchanges + TailExchanges);
            }
        }

        /// <summary>
        /// Called on the thread of DSF when RRF has accepted data of DSF with a valid checksum, before it processes it
        /// </summary>
        public void DataAccepted(byte[] data)
        {
            lock (this)
            {
                if (StopRequestedAt is null || StopDataAcceptedAt is not null)
                {
                    return;
                }

                // Packets as DataTransfer::ReadPacket reads them: PacketHeader and the data padded to a dword
                for (int offset = 0; offset + 8 <= data.Length; offset += 8 + (BitConverter.ToUInt16(data, offset + 4) + 3) / 4 * 4)
                {
                    if (BitConverter.ToUInt16(data, offset) == (ushort)SbcRequest.EmergencyStop)
                    {
                        StopDataAcceptedAt = Rrf.Now;
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Whether DSF works on the macro in the background without needing the link: the macro task reads its codes and passes them through the
        /// code pipeline (MacroFile.RunAsync), or it finishes after the last code. DSF is held meanwhile, see <see cref="FakeRrfSpi.HoldWhile"/>
        /// </summary>
        public bool Preparing()
        {
            lock (this)
            {
                if (TriggerAt is null || Trigger is null)
                {
                    return false;
                }
                StackState state = Trigger.CurrentState;
                if (state.File is not MacroFile { IsExecuting: true, JustStarted: false, IsAborted: false } ||
                    state.FlushRequests.Count > 0 || Trigger.BufferedCodes.Count > 0 || state.SuspendedCodes.Count > 0 || state.LockRequests.Count > 0)
                {
                    // Nothing in the background, or the link has to go on: a flush is resolved, codes are buffered and RRF has to reply to them
                    return false;
                }

                // The macro task reads every code before it waits for the first one (Settings.BufferedMacroCodes), so wait for all of them
                int remaining = MacroLines - CodeLines.Count;
                return (remaining > 0) ? state.PendingCodes.Reader.Count < remaining : state.PendingCodes.Reader.Count == 0;
            }
        }

        public void OnConnectionLost(object? sender, EventArgs e)
        {
            lock (this)
            {
                DsfConnectionLosses++;
            }
        }
    }

    /// <summary>
    /// Faults of the given part of the scenario
    /// </summary>
    private static IEnumerable<SpiFault> Faults(StopCase input, Leg leg)
    {
        if (input.Restart is Restart restart && restart.Leg == leg)
        {
            yield return new SpiFault(restart.Exchange, ExchangeDirection.ToRrf, _ => { }, restart.BootTime);
        }
        foreach (Flip flip in input.Flips.Where(f => f.Leg == leg))
        {
            yield return new SpiFault(flip.Exchange, flip.Direction, data =>
            {
                if (data.Length > 0)
                {
                    int bit = flip.Bit % (data.Length * 8);
                    data[bit / 8] ^= (byte)(1 << (bit % 8));
                }
            }, Mutation: $"flip {flip.Bit}");
        }
    }

    /// <summary>
    /// Run a scenario and check the properties that the code guarantees
    /// </summary>
    /// <param name="input">Scenario and faults</param>
    /// <param name="trace">Trace of the exchanges, recorded if set</param>
    /// <returns>What happened</returns>
    internal static async Task<StopResult> RunAsync(StopCase input, List<string>? trace = null)
    {
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Probe probe = new(input);
        await using Host host = await Host.StartAsync(input.Capabilities, respond: probe.Respond, answerCapabilities: false,
            // The fork's RRF, which offers link capabilities, delays its first header after a boot (SbcInterface.cpp:123-127), as in LinkRecovery
            bootArmDelay: (input.Capabilities != LinkCapabilities.None) ? FakeRrfSpi.ForkBootArmDelay : 0, transferDelay: input.Delay, noiseSeed: input.Seed,
            configure: (rrf, directory) =>
            {
                foreach ((string name, int lines) in Macros)
                {
                    System.IO.File.WriteAllText(Path.Combine(directory, "sys", name), MacroContent(lines));
                }
                rrf.Trace = trace;
                rrf.NoiseLog = (input.BitErrorRate > 0) ? [] : null;
                probe.Rrf = rrf;
                rrf.BeforeExchange = probe.BeforeExchange;
                rrf.DataAccepted = probe.DataAccepted;

                // RRF reports the space of its code buffer after it started (SbcInterface.cpp:59 and 1374-1378)
                rrf.SendPacket(FirmwareRequest.CodeBufferUpdate, RrfPackets.CodeBufferUpdate(SbcCodeBufferSize));
                // Every idle transfer after the setup takes a header and a response exchange
                rrf.HoldBeforeHeader(SetupExchanges + 2 * input.Offset);
            });
        FakeRrfSpi rrf = host.Rrf;
        LinkInterface link = host.Link;
        lock (probe)
        {
            probe.Trigger = host.Get<Manager>()[CodeChannel.Trigger];
        }
        host.Model.OnConnectionLost += probe.OnConnectionLost;

        // Setup
        if (!rrf.WaitUntilHeld(Timeout))
        {
            throw new TimeoutException($"DSF did not complete the setup: {host.Fault}");
        }
        if (input.Delay > 0)
        {
            // DSF waits for its next transfer while RRF waits between two transfers. Let the given share of that time pass
            rrf.HoldWhenPacing();
            rrf.HoldBeforeHeader(int.MaxValue);
            if (!rrf.WaitUntilHeld(Timeout))
            {
                throw new TimeoutException("DSF did not wait for RRF");
            }
            rrf.Wait(input.Phase * input.Delay);
        }
        if (rrf.ActiveCapabilities != input.Capabilities || (input.Capabilities != LinkCapabilities.None && !rrf.DsfUsesExtendedLink))
        {
            throw new InvalidOperationException($"Setup failed: RRF has activated {rrf.ActiveCapabilities}, DSF uses the extended link: {rrf.DsfUsesExtendedLink}");
        }

        string adapterCounters = Counters(host);
        lock (probe)
        {
            probe.StartTime = rrf.Now;
            probe.StartDisconnects = rrf.Disconnects;
            probe.StartConnectionResets = rrf.ConnectionResets;
            probe.StartRestarts = rrf.RestartTimes.Count;
        }

        // Request the emergency stop like M112 does: SimpleCode makes M112 a prioritized code (SimpleCode.cs:110-116) and MCodeHandler waits for the stop
        // (MCodeHandler.cs:749-772). DSF is held, so it writes the request into its next transfer
        Task<Message?>? m112 = null;
        void RequestStop()
        {
            DuetControlServer.Commands.Code code = host.Get<DuetControlServer.Commands.CommandFactory>().Create<DuetControlServer.Commands.Code>();
            code.Channel = CodeChannel.HTTP;
            code.Type = DuetAPI.Commands.CodeType.MCode;
            code.MajorNumber = 112;
            code.Flags |= DuetAPI.Commands.CodeFlags.IsPrioritized;
            m112 = code.ExecuteAsync();
            if (!SpinWait.SpinUntil(() => link.FirmwareHaltRequest is not null || m112.IsCompleted, Timeout) || link.FirmwareHaltRequest is null)
            {
                throw new TimeoutException($"M112 did not request the emergency stop: {(m112.IsFaulted ? m112.Exception : m112.Status)}");
            }
            lock (probe)
            {
                probe.StopRequestedAt = rrf.Now;
                probe.HaltTask = link.FirmwareHaltRequest.Task;
                probe.LegStarts[Leg.Stop] = rrf.Exchanges;
            }
            rrf.ArmAlso([.. Faults(input, Leg.Stop)]);
        }

        // Trigger the macro: RRF writes the request into the transfer after the one it has set up (FakeRrfSpi.SendPacket)
        if (input.HasTrigger)
        {
            lock (probe)
            {
                probe.TriggerAt = rrf.Now;
                probe.HoldAtFirstCode = input.Scenario == Scenario.Both && input.StopAt == StopMoment.FirstCode;
                probe.LegStarts[Leg.Request] = rrf.Exchanges;
            }
            rrf.SendPacket(FirmwareRequest.ExecuteMacro, RrfPackets.ExecuteMacro(CodeChannel.Trigger, false, input.Macro));
            rrf.ArmAlso([.. Faults(input, Leg.Request)]);
            rrf.HoldWhile = probe.Preparing;
        }
        if (input.Scenario == Scenario.Stop || (input.Scenario == Scenario.Both && input.StopAt == StopMoment.Trigger))
        {
            RequestStop();
        }

        bool OutOfTime() => rrf.Now - probe.StartTime > VirtualTimeLimit || host.Fault is not null;

        if (input.Scenario == Scenario.Both && input.StopAt == StopMoment.Request)
        {
            // Hold DSF again at the header of the next transfer, which RRF has set up with the macro request
            rrf.HoldBeforeHeader(rrf.Exchanges + 1);
            if (!rrf.WaitUntilHeld(Timeout))
            {
                throw new TimeoutException("DSF did not reach the transfer of the macro request");
            }
            RequestStop();
            rrf.Release();
        }
        else if (input.Scenario == Scenario.Both && input.StopAt == StopMoment.FirstCode)
        {
            // The probe holds DSF at the transfer of the first code, or for the exchanges after the scenario if the macro ended without one
            rrf.Release();
            DateTime end = DateTime.UtcNow + Timeout;
            while (!rrf.WaitUntilHeld(TimeSpan.FromMilliseconds(10)) && !OutOfTime() && DateTime.UtcNow < end)
            {
                // Wait for the transfer of the first code
            }
            bool held;
            lock (probe)
            {
                held = probe.FirstCodeHoldRequested && !probe.TailStarted && rrf.IsHeld;
            }
            if (held)
            {
                RequestStop();
                rrf.Release();
            }
        }
        else
        {
            rrf.Release();
        }

        // Wait until the probe holds DSF after the exchanges that follow the scenario, or end it if it takes too long
        bool tailHeld;
        {
            DateTime end = DateTime.UtcNow + Timeout;
            while (!(tailHeld = rrf.WaitUntilHeld(TimeSpan.FromMilliseconds(10))))
            {
                if (OutOfTime() || DateTime.UtcNow > end)
                {
                    lock (probe)
                    {
                        if (!probe.TailStarted)
                        {
                            probe.TimedOut = true;
                            probe.StartTail();
                        }
                        else
                        {
                            // The link has not reached the end of the tail
                            rrf.HoldBeforeExchange(rrf.Exchanges);
                        }
                    }
                    tailHeld = rrf.WaitUntilHeld(Timeout);
                    break;
                }
            }
        }

        // Evaluate while DSF is held. M112 completes in the background once DSF has completed the emergency stop
        bool haltCompleted;
        lock (probe)
        {
            haltCompleted = probe.HaltTask is { IsCompleted: true };
        }
        if (haltCompleted)
        {
            SpinWait.SpinUntil(() => m112!.IsCompleted, Timeout);
        }
        Message? m112Result = null;
        string? m112Error = null;
        if (m112 is { IsCompleted: true })
        {
            if (m112.IsCompletedSuccessfully)
            {
                m112Result = m112.Result;
            }
            else
            {
                m112Error = m112.Exception?.InnerException?.Message ?? m112.Status.ToString();
            }
        }
        MachineStatus status;
        using (host.Model.AccessReadOnly())
        {
            status = host.Model.State.Status;
        }
        StopResult result;
        lock (probe)
        {
            List<(string Property, string Message)> violations = [.. probe.Violations];
            (double? stopTime, string stoppedBy) = (probe.StopRequestedAt is not null) ? probe.Stopped : (null, "never");
            bool reconnected = probe.Reconnected;
            string context = $"{(probe.TimedOut ? "TIMED OUT, " : "")}{(probe.Stuck ? "STUCK, " : "")}{(reconnected ? "reconnected" : "no reconnect")}, RRF disconnects {rrf.Disconnects - probe.StartDisconnects}, " +
                $"RRF connection resets {rrf.ConnectionResets - probe.StartConnectionResets}, RRF restarts {rrf.RestartTimes.Count - probe.StartRestarts}, " +
                $"DSF connection losses {probe.DsfConnectionLosses}, DSF before {adapterCounters}, after {Counters(host)}";

            if (host.Fault is Exception fault)
            {
                violations.Add(("DCS stopped", $"the transfer thread stopped: {fault.InnerException?.Message ?? fault.Message}"));
            }
            if (!tailHeld && host.Fault is null)
            {
                violations.Add(("no tail", "the link did not perform the exchanges after the scenario"));
            }
            if (rrf.HoldWhileTimeouts > 0)
            {
                violations.Add(("background stuck", $"DSF did not finish its background work on the macro {rrf.HoldWhileTimeouts}x"));
            }

            if (input.HasStop && probe.StopRequestedAt is not null)
            {
                // The stop is never lost: RRF processes the request, or it performs an emergency stop because it lost the connection, or it restarted.
                // A stuck link is no longer alive, it is reported and measured instead
                if (stopTime is null && !probe.Stuck)
                {
                    violations.Add(("stop lost", "RRF was never stopped"));
                }

                // Without a reconnect, the request reaches RRF once. After a reconnect DSF may send it again, which only repeats the stop (RepRap.cpp:1049-1104)
                if (probe.StopPackets > 1 && !reconnected)
                {
                    violations.Add(("stop twice", $"RRF processed the emergency stop {probe.StopPackets}x without a reconnect"));
                }

                // M112 replies once RRF has been stopped, and not with the timeout of MCodeHandler
                if (m112Error is not null)
                {
                    violations.Add(("M112 failed", m112Error));
                }
                else if (m112Result is { Type: MessageType.Error })
                {
                    violations.Add(("M112 failed", m112Result.Content));
                }
                else if (m112 is not { IsCompleted: true } && stopTime is not null && host.Fault is null && !probe.Stuck)
                {
                    violations.Add(("M112 pending", "M112 did not complete although RRF was stopped"));
                }
            }

            if (input.HasTrigger && probe.TriggerAt is not null)
            {
                // Without a reconnect, the macro starts once and its codes reach RRF once each and in order. With the emergency stop in between, only a part may
                List<int> expected = [.. Enumerable.Range(1, probe.CodeLines.Count)];
                if (!reconnected)
                {
                    if (probe.MacroStarts > 1)
                    {
                        violations.Add(("macro twice", $"RRF processed MacroStarted {probe.MacroStarts}x"));
                    }
                    if (!probe.CodeLines.SequenceEqual(expected) || probe.CodeLines.Count > probe.MacroLines)
                    {
                        violations.Add(("codes out of order", $"RRF got the codes of lines [{string.Join(", ", probe.CodeLines)}]"));
                    }
                    if (probe.MacroCompletions > 1)
                    {
                        violations.Add(("macro completed twice", $"RRF processed MacroCompleted {probe.MacroCompletions}x"));
                    }
                    if (input.Scenario == Scenario.Trigger && !probe.Stuck)
                    {
                        if (probe.MacroStarts != 1 || probe.CodeLines.Count != probe.MacroLines || probe.MacroCompletions != 1 || probe.MacroError)
                        {
                            violations.Add(("macro incomplete", $"RRF processed MacroStarted {probe.MacroStarts}x, the codes of lines [{string.Join(", ", probe.CodeLines)}] " +
                                $"of {probe.MacroLines}, MacroCompleted {probe.MacroCompletions}x{(probe.MacroError ? " with an error" : "")}"));
                        }
                    }
                }
            }

            double origin = probe.StartTime;
            double? Since(double? time, double? from) => (time is double t && from is double f) ? t - f : null;
            result = new StopResult
            {
                StopLatency = Since(stopTime, probe.StopRequestedAt),
                PacketLatency = Since(probe.StopPacketAt, probe.StopRequestedAt),
                StopPackets = probe.StopPackets,
                HaltLatency = Since(probe.HaltSeenAt, probe.StopRequestedAt),
                HaltBeforeStop = probe.HaltBeforeStop,
                HaltBeforeData = probe.HaltBeforeData,
                StoppedBy = stoppedBy,
                MacroStartedLatency = Since(probe.MacroStartedAt, probe.TriggerAt),
                FirstCodeLatency = Since(probe.FirstCodeAt, probe.TriggerAt),
                MacroCompletedLatency = Since(probe.MacroCompletedAt, probe.TriggerAt),
                CodeLines = [.. probe.CodeLines],
                MacroStarts = probe.MacroStarts,
                MacroCompletions = probe.MacroCompletions,
                CodesAfterStop = probe.CodesAfterStop,
                Status = status,
                ReconnectKind = !reconnected ? string.Empty : string.Join("+", new[]
                {
                    (rrf.Disconnects > probe.StartDisconnects) ? "RRF disconnect" : null,
                    (rrf.ConnectionResets > probe.StartConnectionResets) ? "RRF reset" : null,
                    (rrf.RestartTimes.Count > probe.StartRestarts) ? "RRF restart" : null,
                    (probe.DsfConnectionLosses > 0) ? "DSF loss" : null
                }.Where(kind => kind is not null)),
                TriggerOutcome = (probe.TriggerAt is null) ? string.Empty : (probe.MacroCompletedAt is not null && !probe.MacroError) ? "completed" :
                    (probe.StopPacketAt is not null || rrf.Disconnects > probe.StartDisconnects || rrf.RestartTimes.Count > probe.StartRestarts) ? "RRF stopped" : "lost",
                Reconnected = reconnected,
                Stuck = probe.Stuck,
                NoiseAsFlips = (rrf.NoiseLog is null) ? null : input with
                {
                    BitErrorRate = 0,
                    Seed = 1,
                    Flips = [.. input.Flips, .. rrf.NoiseLog.Select(flip =>
                    {
                        // The part that started last before the exchange
                        KeyValuePair<Leg, int> leg = probe.LegStarts.Where(l => l.Value <= flip.Exchange).MaxBy(l => l.Value);
                        return new Flip(leg.Key, flip.Exchange - leg.Value, flip.Direction, flip.Bit);
                    })]
                },
                Violations = violations,
                Details = $"stop {Format(Since(stopTime, probe.StopRequestedAt))} ms by {stoppedBy} (request {Format(Since(probe.StopPacketAt, probe.StopRequestedAt))} ms, " +
                    $"{probe.StopPackets}x, halt {Format(Since(probe.HaltSeenAt, probe.StopRequestedAt))} ms{(probe.HaltBeforeData ? " BEFORE RRF HAD THE DATA" : probe.HaltBeforeStop ? " BEFORE THE STOP" : "")}), macro started {Format(Since(probe.MacroStartedAt, probe.TriggerAt))} ms " +
                    $"({probe.MacroStarts}x), first code {Format(Since(probe.FirstCodeAt, probe.TriggerAt))} ms, lines [{string.Join(", ", probe.CodeLines)}], " +
                    $"completed {Format(Since(probe.MacroCompletedAt, probe.TriggerAt))} ms ({probe.MacroCompletions}x), codes after the stop {probe.CodesAfterStop}; " +
                    $"{context}; {rrf.Now - origin:F1} ms, {rrf.Exchanges} exchanges, {stopwatch.ElapsedMilliseconds} ms real time"
            };
        }

        // A stuck link would keep DCS in its transfer until the host times out
        rrf.Close();

        // The object model outlives the host (it is still reachable after the host is disposed), so do not let it keep the probe and with it the services
        host.Model.OnConnectionLost -= probe.OnConnectionLost;
        return result;
    }

    private static string Format(double? ms) => (ms is double value) ? value.ToString("F3", CultureInfo.InvariantCulture) : "-";

    #region Tests
    /// <summary>
    /// On a clean link the emergency stop reaches RRF once, in the transfer after the one DSF had set up, and M112 replies only after RRF has processed it,
    /// with the machine halted (MCodeHandler.cs:749-772, LinkService.cs:398-400 and 542)
    /// </summary>
    /// <param name="capabilities">Extended or plain link</param>
    [TestCase(LinkCapabilities.All), TestCase(LinkCapabilities.None)]
    public async Task EmergencyStopOnCleanLink(LinkCapabilities capabilities)
    {
        StopResult result = await RunAsync(new(Scenario.Stop, capabilities, []));
        Assert.Multiple(() =>
        {
            Assert.That(result.Violations, Is.Empty, result.Details);
            Assert.That(result.StoppedBy, Is.EqualTo("packet"), result.Details);
            Assert.That(result.StopPackets, Is.EqualTo(1), result.Details);
            Assert.That(result.HaltBeforeStop, Is.False, result.Details);
            Assert.That(result.Reconnected, Is.False, result.Details);
            Assert.That(result.Status, Is.EqualTo(MachineStatus.Halted), result.Details);
        });
    }

    /// <summary>
    /// With an idle RRF that waits between transfers (<see cref="FakeRrfSpi.TransferDelay"/>), the emergency stop still reaches RRF once and M112 replies
    /// only after RRF has processed it, wherever in the wait the stop is requested
    /// </summary>
    /// <param name="phase">Share of the wait that has passed when the stop is requested</param>
    [TestCase(0.0), TestCase(0.5), TestCase(0.99)]
    public async Task EmergencyStopWithIdleRrf(double phase)
    {
        StopResult result = await RunAsync(new(Scenario.Stop, LinkCapabilities.All, []) { Delay = FakeRrfSpi.DefaultTransferDelay, Phase = phase });
        Assert.Multiple(() =>
        {
            Assert.That(result.Violations, Is.Empty, result.Details);
            Assert.That(result.StoppedBy, Is.EqualTo("packet"), result.Details);
            Assert.That(result.StopPackets, Is.EqualTo(1), result.Details);
            Assert.That(result.HaltBeforeStop, Is.False, result.Details);
        });
    }

    /// <summary>
    /// On a clean link the trigger macro starts once and its codes reach RRF once each, in order, followed by the end of the macro
    /// </summary>
    /// <param name="capabilities">Extended or plain link</param>
    /// <param name="macro">Trigger macro</param>
    [TestCase(LinkCapabilities.All, "trigger2.g"), TestCase(LinkCapabilities.All, "trigger4.g"), TestCase(LinkCapabilities.None, "trigger2.g"), TestCase(LinkCapabilities.None, "trigger4.g")]
    public async Task TriggerMacroOnCleanLink(LinkCapabilities capabilities, string macro)
    {
        StopResult result = await RunAsync(new(Scenario.Trigger, capabilities, []) { Macro = macro });
        Assert.Multiple(() =>
        {
            Assert.That(result.Violations, Is.Empty, result.Details);
            Assert.That(result.MacroStarts, Is.EqualTo(1), result.Details);
            Assert.That(result.CodeLines, Is.EqualTo(Enumerable.Range(1, Macros[macro])), result.Details);
            Assert.That(result.MacroCompletions, Is.EqualTo(1), result.Details);
            Assert.That(result.Reconnected, Is.False, result.Details);
        });
    }

    /// <summary>
    /// An emergency stop while a trigger macro starts reaches RRF once, and the macro starts at most once, its codes reaching RRF in order
    /// </summary>
    /// <param name="capabilities">Extended or plain link</param>
    /// <param name="at">When DSF requests the stop</param>
    [TestCase(LinkCapabilities.All, StopMoment.Trigger), TestCase(LinkCapabilities.All, StopMoment.Request), TestCase(LinkCapabilities.All, StopMoment.FirstCode)]
    [TestCase(LinkCapabilities.None, StopMoment.Trigger), TestCase(LinkCapabilities.None, StopMoment.Request), TestCase(LinkCapabilities.None, StopMoment.FirstCode)]
    public async Task EmergencyStopWhileTriggerMacroStarts(LinkCapabilities capabilities, StopMoment at)
    {
        StopResult result = await RunAsync(new(Scenario.Both, capabilities, []) { Macro = "trigger4.g", StopAt = at });
        TestContext.Out.WriteLine(result.Details);
        Assert.Multiple(() =>
        {
            Assert.That(result.Violations, Is.Empty, result.Details);
            Assert.That(result.StoppedBy, Is.EqualTo("packet"), result.Details);
            Assert.That(result.StopPackets, Is.EqualTo(1), result.Details);
            Assert.That(result.HaltBeforeStop, Is.False, result.Details);
            Assert.That(result.MacroStarts, Is.LessThanOrEqualTo(1), result.Details);
        });
    }

    /// <summary>
    /// R10 of design/sbc-link-requirements.md: the emergency stop reaches RRF within 100 ms while the link is disturbed but alive. On the plain link a single bit
    /// error in the Success of RRF for the header of the transfer that carries the stop (Success 1 read as BadHeaderChecksum 5) makes DSF exchange its header again
    /// while RRF goes on with the data. RRF takes the header as the 8 bytes of data and answers BadDataChecksum, after which it exchanges the data again whatever DSF
    /// answers (DataTransfer.cpp:1218-1222). DSF goes on with the data as well (SPI.IsExchangingData), so the stop reaches RRF in the same transfer. Before, DSF kept
    /// exchanging its header, so no timeout of RRF ended it and only DCS stopped after Settings.SbcRestartTimeout (60 s, SPI.PerformFullTransfer). The last input has
    /// a full transfer restart before the same error, after which RRF used to stop only by its 500 ms transfer timeout
    /// </summary>
    /// <param name="text">Input as the tests print it</param>
    [TestCase("Stop None | Stop 3 ToDsf 130")]
    [TestCase("Stop None | Stop 1 ToRrf 0 | Stop 7 ToDsf 130")]
    [TestCase("Stop None | Stop 3 ToDsf 0 | Stop 6 ToDsf 2")]
    public async Task EmergencyStopReachesRrfWithin100Ms(string text)
    {
        StopResult result = await RunAsync(StopCase.Parse(text));
        Assert.That(result.StopLatency, Is.Not.Null.And.LessThanOrEqualTo(100), result.Details);
    }

    /// <summary>
    /// M112 replies once DSF has completed the transfer that carried the stop (LinkService.cs:398-400, 542, PR #88), which tells only that DSF got the Success of RRF
    /// for its data. RRF processes the data once it has the Success of DSF for it (DataTransfer.cpp:1202-1216), and nothing confirms that one to DSF. A lost Success
    /// of DSF (one bit error on the plain link, four on the extended one, whose codes correct three) makes M112 reply one exchange before RRF stops. A single bit error
    /// in the Success of DSF for the header (read as BadHeaderChecksum) shifts the phases: DSF completes the transfer on the data response RRF repeats for the transfer
    /// before, and its sequence guard sends the stop again in the next transfer (SPI.cs, _resending), so M112 replies even before RRF has the request
    /// </summary>
    /// <param name="text">Input as the tests print it</param>
    [TestCase("Stop None | Stop 3 ToRrf 2")]
    [TestCase("Stop None | Stop 5 ToRrf 0")]
    [TestCase("Stop All | Stop 5 ToRrf 0 | Stop 5 ToRrf 1 | Stop 5 ToRrf 2 | Stop 5 ToRrf 3")]
    [Explicit("M112 replies up to one exchange before RRF has processed the stop when the last response of DSF is lost or the phases shift")]
    public async Task M112RepliesOnlyOnceRrfIsStopped(string text)
    {
        StopResult result = await RunAsync(StopCase.Parse(text));
        Assert.That(result.HaltBeforeStop, Is.False, result.Details);
    }

    /// <summary>
    /// Ten bit errors in the data of the transfer that carries the codes of the macro use up the retries of DSF (Settings.MaxSbcRetries), so DSF loses the
    /// connection, leaves the extended link (SPI.EndSession) and drops the macro once it is connected again (SPI.PerformFullTransfer sets _hadReset,
    /// LinkService.Invalidate). RRF has got the codes by then, so it must notice the end of the session and stop, because the macro will never end.
    /// It does: DSF exchanges the transfer again with the number after its counter and status 0 (SPI.PerformFullTransfer), which never follows an extended
    /// number of RRF, so RRF resets the connection when the transfer ends (DataTransfer.cpp:1125-1131 and 852, DataTransfer.h:212-215) and performs an
    /// emergency stop (SbcInterface.cpp:194-230). Before, DSF used the counter itself, and with counter value 0 its 0x0000 followed the 0xFFFF of RRF
    /// </summary>
    /// <param name="offset">Idle transfers before the scenario, which puts the transfer of the codes on counter value <paramref name="offset"/></param>
    [Test]
    public async Task RrfStopsWhenDsfDropsTheTriggerMacro([Range(0, ExtendedLink.CounterMask)] int offset)
    {
        StopResult result = await RunAsync(StopCase.Parse($"Trigger All offset={offset} | Code 2 ToRrf 0 | Code 4 ToRrf 0 | Code 6 ToRrf 0 | Code 8 ToRrf 0 | Code 10 ToRrf 0 | Code 12 ToRrf 0 | Code 14 ToRrf 0 | Code 16 ToRrf 0 | Code 18 ToRrf 0 | Code 20 ToRrf 0"));
        Assert.That(result.TriggerOutcome, Is.EqualTo("RRF stopped"), result.Details);
    }

    /// <summary>
    /// The trigger macro is never lost: it completes, or RRF stops because the connection was reset. The first input are the faults of
    /// <see cref="RrfStopsWhenDsfDropsTheTriggerMacro"/> when the transfer of the codes has counter value 0. DSF used to leave the extended link with sequence
    /// number 0x0000 there, which follows the 0xFFFF RRF saw last as a regular number, so SequenceDistance was 1 once RRF had left the extended link as well
    /// (DataTransfer.cpp:852) and RRF did not reset the connection. RRF processed the codes and waited for the end of the macro that DSF had dropped, and it
    /// kept running. On RRF the trigger file then stays open, so IsTriggerBusy (GCodes.cpp:423-431) would hold back the next trigger macros (GCodes.cpp:999);
    /// the port does not model that part of RRF. On the plain link two bit errors around the header of the transfer of the codes are enough: DSF loses the
    /// connection (SPI.ExchangeHeader, "SPI data transfer failed"), counts a reset (SPI.PerformFullTransfer sets _hadReset after every lost connection) and drops
    /// the macro (LinkService.cs:333-340), while RRF had not completed the transfer, so it takes the same transfer with the same number in sequence, processes
    /// the codes and counts neither a disconnect nor a reset. That is case 2 of fork PR #57, which waits for upstream
    /// </summary>
    /// <param name="text">Input as the tests print it</param>
    [TestCase("Trigger All | Code 2 ToRrf 0 | Code 4 ToRrf 0 | Code 6 ToRrf 0 | Code 8 ToRrf 0 | Code 10 ToRrf 0 | Code 12 ToRrf 0 | Code 14 ToRrf 0 | Code 16 ToRrf 0 | Code 18 ToRrf 0 | Code 20 ToRrf 0")]
    [TestCase("Trigger None | Code 0 ToDsf 0 | Code 1 ToRrf 0", Explicit = true,
        Reason = "Plain link: DSF drops the macro after a lost connection that RRF does not see, because RRF takes the repeated transfer in sequence (fork PR #57, case 2)")]
    public async Task TriggerMacroRunsOrRrfStops(string text)
    {
        StopResult result = await RunAsync(StopCase.Parse(text));
        Assert.That(result.TriggerOutcome, Is.Not.EqualTo("lost"), result.Details);
    }
    #endregion

    /// <summary>
    /// Print the exchanges of a single input, for analysing a finding
    /// </summary>
    /// <param name="text">Input as the tests print it</param>
    [TestCase("Stop All"), TestCase("Trigger All macro=trigger4.g"), TestCase("Both All macro=trigger4.g at=FirstCode"), TestCase("Trigger None | Code 0 ToDsf 0 | Code 1 ToRrf 0"), Explicit("Analysis")]
    public async Task TraceInput(string text)
    {
        List<string> trace = [];
        StopResult result = await RunAsync(StopCase.Parse(text), trace);
        TestContext.Out.WriteLine($"{text}: {result.Details}");
        foreach ((string property, string message) in result.Violations)
        {
            TestContext.Out.WriteLine($"VIOLATION {property}: {message}");
        }
        if (result.NoiseAsFlips is StopCase flips)
        {
            TestContext.Out.WriteLine($"As flips: {flips}: {(await RunAsync(flips)).Details}");
        }
        // From shortly before the end of the setup on
        int start = trace.FindIndex(line => line.StartsWith($"#{SetupExchanges - 4} ", StringComparison.Ordinal));
        foreach (string line in trace.Skip(Math.Max(0, start)).Take(300))
        {
            TestContext.Out.WriteLine(line);
        }
    }

    /// <summary>
    /// Recovery counters of DSF from its M122 (SPI.PrintDiagnostics)
    /// </summary>
    private static string Counters(Host host)
    {
        StringBuilder diagnostics = new();
        host.Adapter.PrintDiagnostics(diagnostics);
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(diagnostics.ToString(), @"lost connections: (\d+), connection resets: (\d+)");
        return $"{match.Groups[1].Value}/{match.Groups[2].Value}";
    }
    #endregion
}
