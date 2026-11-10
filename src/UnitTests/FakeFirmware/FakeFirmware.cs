using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Link adapter that emulates RepRapFirmware's side of the SBC protocol, so the real LinkService and the DCS code pipelines can run in-process.
/// <para>
/// Transfer model: Write* calls queue requests for the next transfer. <see cref="PerformFullTransfer"/> delivers the packets that the
/// emulated RRF wrote after the previous transfer, then lets the emulated SBC task process the new requests and write the packets for
/// the next transfer (SbcInterface::ExchangeData), and finally runs the emulated main task (GCodes::Spin), whose replies go out after
/// the following transfer. This is the order in which RRF handles a transfer, so replies, macro requests and abort requests reach DSF
/// in the same order and with the same latency as on real hardware with one interleaving of the two RRF tasks.
/// </para>
/// <para>
/// Ground truth is RRF 3.7.0+1.mp.5 (Meltingplot fork, 03a87245b): src/SBC/SbcInterface.cpp, src/GCodes/GCodes*.cpp and
/// src/GCodes/GCodeBuffer/GCodeBuffer.cpp. Packets are passed as objects, there is no binary encoding and the transfer buffer size is not limited
/// </para>
/// <para>
/// Channel aborts follow <see cref="Model"/>: the InvalidateChannel handler of fork RRF 0dccb9efe (<see cref="FakeRrfModel.V7"/>), the record by the SBC
/// task and the apply by the main task of design/sbc-link-v8-requirements.md (<see cref="FakeRrfModel.RecordApply"/>), or that plus the channel epoch
/// (<see cref="FakeRrfModel.Epoch"/>, the default), whose tags travel in the header field resendPacketId (<see cref="FakePacket.Tag"/>). The fake
/// has one loop for both RRF tasks, so the record happens while the SBC task reads the requests of a transfer and the apply at the start of the main
/// task's spin that follows; <see cref="ApplyDelay"/> puts transfers in between
/// </para>
/// <para>
/// Emulated: code buffer and buffer updates, code replies with RRF's flags, macro requests (M98, system macros), pending triggers and their
/// macros (GCodes::CheckTriggers, see <see cref="FireTrigger"/>), macro completion and M99 aborts, message boxes (M291/M292), M409, the result variables of M260/M261, expression evaluation, local and global variables,
/// movement locks, channel invalidation, emergency stop, reset, resend requests, the mutex hand-over between RRF's SBC and main tasks
/// while a code waits for a macro, text codes that RRF passes on to DSF (DoCode, see <see cref="PassCodeToSbc"/>), RRF's own abort of all files
/// (see <see cref="AbortAllFiles"/>) and main firmware updates through the SBC IAP program (FakeFirmwareIap.cs).
/// Not emulated: job files and pausing, file requests from RRF, timeouts, motion, heaters,
/// the binary protocol and its transfer buffer limits. Codes without a test handler succeed with an empty reply
/// </para>
/// </summary>
public sealed partial class FakeFirmware : ILinkAdapter
{
    /// <summary>
    /// Size of RRF's code buffer, see SbcCodeBufferSize in SbcMessageFormats.h
    /// </summary>
    private const int SbcCodeBufferSize = 4096;

    /// <summary>
    /// Delay of a transfer when nothing is going on. RRF waits up to SbcTransferDelay (25ms) then, the fake uses its burst mode delay to keep tests fast
    /// </summary>
    private static readonly TimeSpan IdleTransferDelay = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// Requests that SPI.cs still writes while a packet of the same type is being resent
    /// </summary>
    private static readonly HashSet<SbcRequest> RequestsWithoutResendCheck = [SbcRequest.EmergencyStop, SbcRequest.Reset, SbcRequest.LockAllMovementSystemsAndWaitForStandstill, SbcRequest.SetLastCodeResult, SbcRequest.ObjectModelKeyChanged];

    /// <summary>
    /// Lock around the emulated firmware. ILinkAdapter calls come from the LinkService thread, test calls from other threads
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// Maximum size of a binary code
    /// </summary>
    private readonly int _maxCodeLength;

    /// <summary>
    /// Emulated main task
    /// </summary>
    private readonly FakeGCodes _gcodes;

    /// <summary>
    /// Create a fake firmware with default settings
    /// </summary>
    public FakeFirmware() : this(Options.Create(new Settings())) { }

    /// <summary>
    /// Create a fake firmware
    /// </summary>
    /// <param name="settings">DCS settings</param>
    public FakeFirmware(IOptions<Settings> settings)
    {
        _maxCodeLength = settings.Value.MaxCodeBufferSize;
        _gcodes = new FakeGCodes(this);
    }

    #region Test API
    /// <summary>
    /// Logger for the packets exchanged with DSF (debug level)
    /// </summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>
    /// RRF that the fake models. Set it before DSF connects
    /// </summary>
    public FakeRrfModel Model { get; set; } = FakeRrfModel.Epoch;

    /// <summary>
    /// Number of transfers that RRF's main task needs after the SBC task recorded a channel abort to reach the top of its next spin, where it applies
    /// the abort (CE-7). The rest of a pass can be long, e.g. while the main task waits for an expansion board (Q38). 0 applies it in the transfer of the record
    /// </summary>
    public int ApplyDelay
    {
        get
        {
            lock (_lock)
            {
                return _applyDelay;
            }
        }
        set
        {
            lock (_lock)
            {
                _applyDelay = value;
            }
        }
    }
    private int _applyDelay;

    /// <summary>
    /// Whether the fork catch code of RRF #46 (codeReplySurplus, GCodeBuffer.h:405) suppresses the replies of a code that DSF dropped at its abort.
    /// It stays until milestone E3 (CE-7), switch it off to model E3
    /// </summary>
    public bool CodeReplySurplus { get; set; } = true;

    /// <summary>
    /// Whether the SBC task has recorded an abort of a channel that the main task has yet to apply
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <returns>Whether the abort is recorded but not applied</returns>
    public bool IsAbortRecorded(CodeChannel channel)
    {
        lock (_lock)
        {
            return _gcodes[channel].IsAbortRecorded;
        }
    }

    /// <summary>
    /// Longest time from the record of a channel abort to its apply, in transfers (CE-20 counts it in ms)
    /// </summary>
    public int MaxRecordToApply
    {
        get
        {
            lock (_lock)
            {
                return _gcodes.MaxRecordToApply;
            }
        }
    }

    /// <summary>
    /// Channel that holds the movement lock, or null if none does
    /// </summary>
    public CodeChannel? MovementLockOwner
    {
        get
        {
            lock (_lock)
            {
                return _gcodes.MovementLockOwner?.Channel;
            }
        }
    }

    /// <summary>
    /// Emulate a lost connection to the SBC that RRF notices while it keeps running, see the connection loss in SbcInterface::TaskLoop
    /// (SbcInterface.cpp:194-233): RRF stops the machine and runs InvalidateResources (:1717-1780). DSF sees the connection reset
    /// </summary>
    public void SimulateConnectionLoss()
    {
        lock (_lock)
        {
            _repRapStopped = true;
            _gcodes.RequestEmergencyStop();
            InvalidateResources();
            _hadReset = true;
        }
    }

    /// <summary>
    /// Object model of the fake firmware. Changes are reported to DSF via the sequence numbers
    /// </summary>
    public FakeObjectModel ObjectModel { get; } = new();

    /// <summary>
    /// Capabilities of the link the firmware offers in limits.sbcLinkCapabilities, or null for RRF without them
    /// </summary>
    public LinkCapabilities? OfferedLinkCapabilities
    {
        get => _offeredLinkCapabilities;
        set
        {
            _offeredLinkCapabilities = value;
            if (value is not null)
            {
                ObjectModel.Set("limits.sbcLinkCapabilities", JsonValue.Create((uint)value.Value));
                SetActiveLinkCapabilities(LinkCapabilities.None);
            }
        }
    }
    private LinkCapabilities? _offeredLinkCapabilities;

    /// <summary>
    /// Capabilities of the link that are active for the current connection
    /// </summary>
    public LinkCapabilities ActiveLinkCapabilities { get; private set; }

    /// <summary>
    /// Activation requests the firmware received
    /// </summary>
    public List<LinkCapabilities> LinkCapabilityRequests { get; } = [];

    private void SetActiveLinkCapabilities(LinkCapabilities capabilities)
    {
        ActiveLinkCapabilities = capabilities;
        if (_offeredLinkCapabilities is not null)
        {
            ObjectModel.Set("state.sbcLinkCapabilities", JsonValue.Create((uint)capabilities));
        }
    }

    /// <summary>
    /// Handlers for specific commands
    /// </summary>
    private readonly Dictionary<string, Func<FakeCode, FakeReply?>> _codeHandlers = [];

    /// <summary>
    /// Handler for all codes
    /// </summary>
    private Func<FakeCode, FakeReply?>? _anyCodeHandler;

    /// <summary>
    /// Register a handler for a command. It is called on the LinkService thread with the firmware locked, so it must not block.
    /// Return null to fall back to the emulated RRF behaviour, or <see cref="FakeReply.NotFinished"/> to be called again on the next spin
    /// </summary>
    /// <param name="command">Command without parameters, e.g. "G4" or "M581.1"</param>
    /// <param name="handler">Handler</param>
    public void OnCode(string command, Func<FakeCode, FakeReply?> handler)
    {
        lock (_lock)
        {
            _codeHandlers[command] = handler;
        }
    }

    /// <summary>
    /// Register a handler that is called for every code without a command handler, see <see cref="OnCode"/>
    /// </summary>
    /// <param name="handler">Handler</param>
    public void OnAnyCode(Func<FakeCode, FakeReply?> handler)
    {
        lock (_lock)
        {
            _anyCodeHandler = handler;
        }
    }

    /// <summary>
    /// Ask DSF to run a macro file as RRF does for config.g and trigger macros (DoFileMacro with AsyncSystemMacroCode).
    /// The request is started on the next spin of the main task once the channel is idle
    /// </summary>
    /// <param name="channel">Channel to run the macro on, e.g. Trigger</param>
    /// <param name="fileName">Macro file name as RRF requests it, e.g. "trigger5.g"</param>
    /// <param name="reportMissing">Whether RRF reports a missing file (true for trigger macros, false for config.g)</param>
    public void RequestMacro(CodeChannel channel, string fileName, bool reportMissing = false)
    {
        lock (_lock)
        {
            _gcodes.QueueMacroRequest(channel, fileName, reportMissing);
        }
    }

    /// <summary>
    /// Let a trigger fire as if its input had changed or its expression had become true and its enable condition was met
    /// (TriggerItem::Check). RRF marks the trigger as pending and the main task asks DSF for sys/triggerN.g on the trigger channel once that
    /// channel is free, the lowest pending trigger first. A trigger that fires again before its macro has been requested runs only once
    /// (GCodes::CheckTriggers, GCodes.cpp:981-1062)
    /// </summary>
    /// <param name="number">Trigger number from 2 up. Trigger 0 (emergency stop) and trigger 1 (pause) are not emulated</param>
    public void FireTrigger(int number)
    {
        lock (_lock)
        {
            _gcodes.SetTriggerPending(number);
        }
    }

    /// <summary>
    /// Let a channel pass a text code on to DSF, as RRF does for a code from PanelDue, USB or a trigger action that it cannot run itself
    /// (default case of GCodes::HandleMcode, GCodes2.cpp:600-606). The main task takes the code once the channel is idle, and the SBC task writes it
    /// as DoCode (SbcInterface.cpp:1613-1634). The channel stays busy until DSF sends a message to that channel alone, which is the result
    /// (SbcInterface.cpp:774-793)
    /// </summary>
    /// <param name="channel">Channel of the code</param>
    /// <param name="code">Text of the code</param>
    public void PassCodeToSbc(CodeChannel channel, string code)
    {
        lock (_lock)
        {
            _gcodes.QueueSbcCode(channel, code);
        }
    }

    /// <summary>
    /// Let the main task abort all files of a channel on its next spin, as GCodes::AbortPrint does after an error of the state machine
    /// (GCodes.cpp:3623-3640). The SBC task tells DSF with AbortFile(all) (SbcInterface.cpp:1541-1577). A channel without a file is left alone,
    /// see GCodeBuffer::AbortFile
    /// </summary>
    /// <param name="channel">Code channel</param>
    public void AbortAllFiles(CodeChannel channel)
    {
        lock (_lock)
        {
            _gcodes.QueueAbortAllFiles(channel);
        }
    }

    /// <summary>
    /// Get the messages DSF sent to the firmware, e.g. the results of the text codes RRF passed on
    /// </summary>
    /// <returns>Messages in order</returns>
    public IReadOnlyList<FakeSbcRequest> GetReceivedMessages()
    {
        lock (_lock)
        {
            return [.. _receivedMessages];
        }
    }

    /// <summary>
    /// Get the text codes that RRF passed on to DSF and that a message of DSF finished, with the text of that message
    /// </summary>
    /// <returns>Finished codes and the results that finished them, in order</returns>
    public IReadOnlyList<(string Code, string Result)> GetFinishedSbcCodes()
    {
        lock (_lock)
        {
            return [.. _finishedSbcCodes];
        }
    }

    /// <summary>
    /// Bitmap of the triggers that have fired and whose macros have not been requested yet (GCodes::triggersPending)
    /// </summary>
    public uint TriggersPending
    {
        get
        {
            lock (_lock)
            {
                return _gcodes.TriggersPending;
            }
        }
    }

    /// <summary>
    /// Send a message to DSF like Platform::Message does in SBC mode
    /// </summary>
    /// <param name="type">Message type flags</param>
    /// <param name="message">Message content</param>
    public void SendMessage(MessageTypeFlags type, string message)
    {
        lock (_lock)
        {
            PlatformMessage(type, message, null);
        }
    }

    /// <summary>
    /// Emulate a restart of RRF (e.g. by its watchdog) while DSF keeps running
    /// </summary>
    public void SimulateReset()
    {
        lock (_lock)
        {
            ResetFirmware();
        }
    }

    /// <summary>
    /// Number of transfers performed so far
    /// </summary>
    public int TransferCount
    {
        get
        {
            lock (_lock)
            {
                return _transferNumber;
            }
        }
    }

    /// <summary>
    /// Let the next attempts of the SBC task to lock the buffer of a channel fail, as when the main task does not release the buffer mutex within
    /// SbcYieldTimeout (SbcInterface.cpp:45, :888-900). RRF then asks DSF to send the request again (:1338-1341). While an M98 waits for its macro,
    /// the main task holds the mutex for the whole spin (GCodes.cpp:590, GCodeBuffer::RequestMacroFile releases only its own level)
    /// </summary>
    /// <param name="channel">Channel of the buffer</param>
    /// <param name="count">Number of attempts that fail</param>
    public void FailBufferLocks(CodeChannel channel, int count)
    {
        lock (_lock)
        {
            _failedBufferLocks[channel] = count;
        }
    }

    /// <summary>
    /// Hold the transfers after the given number of further transfers, so that a test can check what DSF does before its requests reach RRF
    /// </summary>
    /// <param name="transfers">Number of transfers to perform before holding</param>
    public void HoldTransfers(int transfers = 0)
    {
        lock (_lock)
        {
            _transfersAllowed = transfers;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Let held transfers continue
    /// </summary>
    public void ReleaseTransfers()
    {
        lock (_lock)
        {
            _transfersAllowed = null;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Whether LinkService waits for a held transfer
    /// </summary>
    public bool IsTransferHeld
    {
        get
        {
            lock (_lock)
            {
                return _transferHeld;
            }
        }
    }

    /// <summary>
    /// Get the requests DSF wrote for the next transfer
    /// </summary>
    /// <returns>Requests in order</returns>
    public IReadOnlyList<FakeSbcRequest> GetPendingRequests()
    {
        lock (_lock)
        {
            return [.. _incoming];
        }
    }

    /// <summary>
    /// Whether a reset has happened that LinkService has not seen yet via <see cref="HadReset"/>
    /// </summary>
    public bool IsResetPending
    {
        get
        {
            lock (_lock)
            {
                return _hadReset;
            }
        }
    }

    /// <summary>
    /// Whether an emergency stop has been performed since the last reset
    /// </summary>
    public bool IsEmergencyStopped
    {
        get
        {
            lock (_lock)
            {
                return _repRapStopped;
            }
        }
    }

    /// <summary>
    /// Get the codes that DSF sent, including those RRF discarded
    /// </summary>
    /// <param name="channel">Optional channel to filter by</param>
    /// <returns>Received codes in order</returns>
    public IReadOnlyList<FakeCode> GetReceivedCodes(CodeChannel? channel = null)
    {
        lock (_lock)
        {
            return [.. _receivedCodes.Where(code => channel is null || code.Channel == channel)];
        }
    }

    /// <summary>
    /// Get the codes that the emulated main task executed
    /// </summary>
    /// <param name="channel">Optional channel to filter by</param>
    /// <returns>Executed codes in order</returns>
    public IReadOnlyList<FakeCode> GetExecutedCodes(CodeChannel? channel = null)
    {
        lock (_lock)
        {
            return [.. _executedCodes.Where(code => channel is null || code.Channel == channel)];
        }
    }

    /// <summary>
    /// Get the codes that RRF discarded because the channel was invalidated
    /// </summary>
    /// <param name="channel">Optional channel to filter by</param>
    /// <returns>Discarded codes in order</returns>
    public IReadOnlyList<FakeCode> GetDiscardedCodes(CodeChannel? channel = null)
    {
        lock (_lock)
        {
            return [.. _discardedCodes.Where(code => channel is null || code.Channel == channel)];
        }
    }

    /// <summary>
    /// Get the packets that have been delivered to DSF
    /// </summary>
    /// <returns>Packets in order</returns>
    public IReadOnlyList<FakePacket> GetSentPackets()
    {
        lock (_lock)
        {
            return [.. _sentPackets];
        }
    }

    /// <summary>
    /// Get the requests that DSF sent
    /// </summary>
    /// <returns>Requests in order</returns>
    public IReadOnlyList<FakeSbcRequest> GetReceivedRequests()
    {
        lock (_lock)
        {
            return [.. _receivedRequests];
        }
    }

    /// <summary>
    /// Get the situations the fake ran into but does not emulate. Tests can check this is empty
    /// </summary>
    /// <returns>Descriptions</returns>
    public IReadOnlyList<string> GetUnemulated()
    {
        lock (_lock)
        {
            return [.. _unemulated];
        }
    }

    /// <summary>
    /// Get a local variable of the current file frame of a channel
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <param name="name">Name of the variable without var prefix</param>
    /// <param name="value">Value of the variable</param>
    /// <returns>Whether the variable exists</returns>
    public bool TryGetLocalVariable(CodeChannel channel, string name, out object? value)
    {
        lock (_lock)
        {
            return _gcodes[channel].GetVariables().TryGetValue(name, out value);
        }
    }

    /// <summary>
    /// Get the stack depth of a channel
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <returns>Number of frames above the base frame</returns>
    public int GetStackDepth(CodeChannel channel)
    {
        lock (_lock)
        {
            int depth = 0;
            for (FakeMachineState ms = _gcodes[channel].MachineState; ms.Previous is not null; ms = ms.Previous)
            {
                depth++;
            }
            return depth;
        }
    }

    /// <summary>
    /// Whether a channel has nothing to do: no file on its stack, no macro request, no code being executed, no message box and no file abort
    /// that DSF has yet to be told about. For the trigger channel this means that the next pending trigger macro can be requested
    /// (GCodes::IsTriggerBusy, GCodes.cpp:423-433)
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <returns>Whether the channel is idle</returns>
    public bool IsIdle(CodeChannel channel)
    {
        lock (_lock)
        {
            FakeGCodeBuffer gb = _gcodes[channel];
            return !gb.IsDoingFile && !gb.IsWaitingForMacro && gb.PendingMacroCall is null && gb.CurrentCode is null &&
                !gb.MachineState.WaitingForAcknowledgement && !gb.IsAbortRequested && gb.MachineState.Previous is null && gb.SbcCode is null &&
                !gb.IsAbortRecorded && !_gcodes.HasQueuedSbcCode(channel);
        }
    }

    /// <summary>
    /// Get the code that the emulated main task is executing on a channel
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <returns>Code being executed or null if there is none</returns>
    public FakeCode? GetCurrentCode(CodeChannel channel)
    {
        lock (_lock)
        {
            return _gcodes[channel].CurrentCode;
        }
    }

    /// <summary>
    /// Number of open message boxes
    /// </summary>
    public int MessageBoxCount
    {
        get
        {
            lock (_lock)
            {
                return _gcodes.MessageBoxes.Count;
            }
        }
    }

    /// <summary>
    /// Wait until a condition is met. The condition is checked after every transfer with the firmware locked
    /// </summary>
    /// <param name="condition">Condition to check</param>
    /// <param name="timeout">Maximum time to wait</param>
    /// <returns>Whether the condition was met in time</returns>
    public bool WaitUntil(Func<FakeFirmware, bool> condition, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow + timeout;
        lock (_lock)
        {
            while (!condition(this))
            {
                TimeSpan remaining = end - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }
                Monitor.Wait(_lock, remaining);
            }
            return true;
        }
    }
    #endregion

    #region Firmware state
    // Transfer state
    private int _transferNumber;
    private int? _transfersAllowed;
    private bool _transferHeld;
    private List<FakeSbcRequest> _incoming = [];
    private List<FakePacket> _txPackets = [];
    private List<FakePacket> _rxPackets = [];
    private int _rxIndex;
    private FakePacket? _currentPacket;
    private readonly HashSet<SbcRequest> _requestsBeingResent = [];

    /// <summary>
    /// Number of further attempts of the SBC task to lock the buffer of a channel that fail
    /// </summary>
    private readonly Dictionary<CodeChannel, int> _failedBufferLocks = [];
    private bool _hadReset;

    // SBC interface state
    private readonly List<FakeCode> _codeBuffer = [];
    private int _codeBufferUsed;
    private bool _sendBufferUpdate = true;
    private readonly List<(MessageTypeFlags type, StringBuilder text, FakeCode? code, bool isOutputBuffer)> _gcodeReply = [];
    private bool _repRapStopped;

    // Logs
    private readonly List<FakeCode> _receivedCodes = [], _executedCodes = [], _discardedCodes = [];
    private readonly List<FakePacket> _sentPackets = [];
    private readonly List<FakeSbcRequest> _receivedRequests = [];
    private readonly List<FakeSbcRequest> _receivedMessages = [];
    private readonly List<(string code, string result)> _finishedSbcCodes = [];
    private readonly List<string> _unemulated = [];

    /// <summary>
    /// Emulate a software reset of RRF: everything is lost except the seeded object model
    /// </summary>
    private void ResetFirmware()
    {
        _gcodes.Reset();
        _codeBuffer.Clear();
        _codeBufferUsed = 0;
        _gcodeReply.Clear();
        _txPackets.Clear();
        _sendBufferUpdate = true;
        _repRapStopped = false;
        ObjectModel.ClearGlobals();
        SetActiveLinkCapabilities(LinkCapabilities.None);
        _hadReset = true;
    }
    #endregion

    #region Transfer
    /// <inheritdoc />
    public void Connect(CancellationToken cancellationToken = default) => PerformFullTransfer(true, cancellationToken);

    /// <inheritdoc />
    public int ProtocolVersion => Consts.ProtocolVersion;

    /// <inheritdoc />
    public void PerformFullTransfer(bool connecting = false, CancellationToken cancellationToken = default)
    {
        bool busy;
        lock (_lock)
        {
            // Wait while a test holds the transfers
            while (_transfersAllowed == 0 && !cancellationToken.IsCancellationRequested)
            {
                _transferHeld = true;
                Monitor.PulseAll(_lock);
                Monitor.Wait(_lock, 100);
            }
            _transferHeld = false;
            if (_transfersAllowed > 0)
            {
                _transfersAllowed--;
            }

            _transferNumber++;
            long timestamp = Stopwatch.GetTimestamp();
            _requestsBeingResent.Clear();

            // Deliver what the SBC task wrote after the previous transfer
            _rxPackets = _txPackets;
            _txPackets = [];
            _rxIndex = 0;
            _currentPacket = null;
            foreach (FakePacket packet in _rxPackets)
            {
                packet.Transfer = _transferNumber;
                packet.Timestamp = timestamp;
                if (packet.Request is not FirmwareRequest.ObjectModel and not FirmwareRequest.CodeBufferUpdate)
                {
                    Logger.LogDebug("RRF -> DSF: {Packet}", packet);
                }
            }
            _sentPackets.AddRange(_rxPackets);

            // Take the requests that DSF wrote since the previous transfer
            List<FakeSbcRequest> received = _incoming;
            _incoming = [];
            foreach (FakeSbcRequest request in received)
            {
                request.Transfer = _transferNumber;
                request.Timestamp = timestamp;
                if (request.Request is not SbcRequest.GetObjectModel)
                {
                    Logger.LogDebug("DSF -> RRF: {Request}", request);
                }
            }
            _receivedRequests.AddRange(received);

            // SBC task (SbcInterface::ExchangeData), then the main task until the next transfer (GCodes::Spin)
            ExchangeData(received);
            _gcodes.Spin();
            UpdateStatus();

            busy = received.Count > 0 || _rxPackets.Count > 0 || _txPackets.Count > 0 || _gcodeReply.Count > 0;
            Monitor.PulseAll(_lock);
        }

        if (!busy && !connecting)
        {
            cancellationToken.WaitHandle.WaitOne(IdleTransferDelay);
        }
    }

    /// <inheritdoc />
    public double GetMaxFullTransferDelay() => 0;

    /// <inheritdoc />
    public bool HadReset()
    {
        lock (_lock)
        {
            bool result = _hadReset;
            _hadReset = false;
            return result;
        }
    }

    /// <summary>
    /// Update state.status, see RepRap::GetStatusIndex
    /// </summary>
    private void UpdateStatus()
    {
        string status = _repRapStopped ? "halted" : (_gcodes.DoingFileMacro || _gcodes.WaitingForAcknowledgement) ? "busy" : "idle";
        ObjectModel.SetComputedStatus(status);
    }
    #endregion

    #region SBC task
    /// <summary>
    /// Process the requests of a transfer and write the packets for the next one, see SbcInterface::ExchangeData
    /// </summary>
    /// <param name="received">Requests from DSF</param>
    private void ExchangeData(List<FakeSbcRequest> received)
    {
        bool codeBufferAvailable = true;
        foreach (FakeSbcRequest request in received)
        {
            bool acknowledged = true;
            FakeGCodeBuffer? gb = (request.Channel < CodeChannel.Unknown) ? _gcodes[request.Channel] : null;

            // Only the RRF of milestone E2 reads the tags of DSF (CE-5), the others see 0 there
            ushort tag = (Model == FakeRrfModel.Epoch) ? request.Tag : (ushort)0;
            if ((tag & FakeTag.FirstMarker) != 0)
            {
                // DSF's first tagged packet after it started or ended a connection (CE-4). DSF writes it where all its epochs are 0, so every channel
                // epoch is 0 from here on, not unknown as CE-4 says: then RRF tags the packets of a channel before DSF has sent anything on it, and a
                // request that crosses DSF's first abort of that channel is dropped by its tag instead of passing untagged (Q34). An InvalidateChannel
                // in this packet then moves its channel on as usual
                foreach (FakeGCodeBuffer buffer in _gcodes.Buffers)
                {
                    buffer.ForgetEpoch();
                    buffer.IsEpochKnown = true;
                }
            }
            if (DropByEpoch(request, gb, tag))
            {
                if (request.Request == SbcRequest.Code)
                {
                    _receivedCodes.Add(request.Code!);
                    _discardedCodes.Add(request.Code!);
                }
                continue;
            }

            switch (request.Request)
            {
                case SbcRequest.EmergencyStop:
                    // RepRap::EmergencyStop stops at once, GCodes::EmergencyStop is carried out by the main task
                    _repRapStopped = true;
                    _gcodes.RequestEmergencyStop();
                    break;

                case SbcRequest.Reset:
                    // RRF performs a software reset, so nothing else is processed or sent
                    ResetFirmware();
                    return;

                case SbcRequest.Code:
                    acknowledged = StoreCode(request.Code!, gb!, tag, ref codeBufferAvailable);
                    break;

                case SbcRequest.GetObjectModel:
                    // state.status is a live function in RRF (RepRap::GetStatusIndex), e.g. an emergency stop in the same transfer reports halted
                    UpdateStatus();
                    WritePacket(new FakePacket { Request = FirmwareRequest.ObjectModel, Text = ObjectModel.GetModelResponse(request.Text, request.Argument) });
                    break;

                case SbcRequest.SetPrintFileInfo:
                    // Print monitoring is not emulated
                    break;

                case SbcRequest.PrintStopped:
                    if (request.PrintStoppedReason == PrintStoppedReason.Abort)
                    {
                        InvalidateBufferedCodes(CodeChannel.File);
                        InvalidateBufferedCodes(CodeChannel.File2);
                    }
                    // Marking the print files as finished is not emulated because job files are not emulated
                    break;

                case SbcRequest.MacroCompleted:
                    if (gb!.IsWaitingForMacro && !gb.IsMacroRequestPending)
                    {
                        gb.ResolveMacroRequest(request.Flag, true);
                    }
                    else if (TryLockBuffer(gb))
                    {
                        if (request.Flag)
                        {
                            gb.CurrentFileMachineState.CloseFile();
                            gb.PopState(false);
                        }
                        else
                        {
                            gb.SetFileFinished();
                        }
                    }
                    else
                    {
                        acknowledged = false;
                    }
                    break;

                case SbcRequest.LockAllMovementSystemsAndWaitForStandstill:
                    if (TryLockBuffer(gb!) && _gcodes.LockAllMovementSystemsAndWaitForStandstill(gb!))
                    {
                        WritePacket(new FakePacket { Request = FirmwareRequest.Locked, Channel = request.Channel, Tag = AnswerTag(tag) });
                    }
                    else
                    {
                        acknowledged = false;
                    }
                    break;

                case SbcRequest.Unlock:
                    if (TryLockBuffer(gb!))
                    {
                        _gcodes.UnlockAll(gb!);
                    }
                    else
                    {
                        acknowledged = false;
                    }
                    break;

                case SbcRequest.EvaluateExpression:
                    if (TryLockBuffer(gb!))
                    {
                        object? result;
                        try
                        {
                            result = _gcodes.Evaluate(gb!, request.Text);
                        }
                        catch (FakeExpressionException e)
                        {
                            // WriteEvaluationError sends the message as an expression, which DSF turns into an exception
                            result = new CodeParserException(e.Message);
                        }
                        WritePacket(new FakePacket { Request = FirmwareRequest.EvaluationResult, Channel = request.Channel, Text = request.Text, Value = result, Tag = AnswerTag(tag) });
                    }
                    else
                    {
                        acknowledged = false;
                    }
                    break;

                case SbcRequest.Message:
                    // A message to a single channel whose text code DSF executes is the result of that code and finishes it (SbcInterface.cpp:774-793).
                    // Output to other devices is not emulated, the fake records the message instead
                    if (!request.MessageFlags.HasFlag(MessageTypeFlags.PushFlag) && TryGetSingleChannel(request.MessageFlags, out CodeChannel destination))
                    {
                        FakeGCodeBuffer target = _gcodes[destination];
                        if (target.IsExecutingOnSbc)
                        {
                            if (!TryLockBuffer(target))
                            {
                                acknowledged = false;
                                break;
                            }
                            _finishedSbcCodes.Add((target.SbcCode!, request.Text));
                            target.SetFinished(true);
                        }
                    }
                    _receivedMessages.Add(request);
                    break;

                case SbcRequest.MacroStarted:
                    if (gb!.IsWaitingForMacro && !gb.IsMacroRequestPending)
                    {
                        gb.ResolveMacroRequest(false, false);
                    }
                    else if (request.Channel != CodeChannel.Daemon)
                    {
                        PlatformMessage(MessageTypeFlags.GenericMessage | MessageTypeFlags.LogWarn | MessageTypeFlags.WarningMessageFlag, $"Macro file has been started on channel {request.Channel} but none was requested\n", null);
                    }
                    break;

                case SbcRequest.InvalidateChannel when Model != FakeRrfModel.V7:
                    RecordAbort(gb!, tag);
                    break;

                case SbcRequest.InvalidateChannel:
                    // DSF drops every code it has sent on this channel, so the replies of a code that is still being executed are surplus. Only the
                    // first copy of the request flags that code, a resent copy would flag a code that has started since (RRF Meltingplot/RepRapFirmware#46)
                    bool firstCopy = !gb!.IsInvalidationPending;
                    if (gb.IsWaitingForMacro)
                    {
                        if (firstCopy && gb.IsMacroStartedByCode)
                        {
                            gb.IsCodeReplySurplus = true;
                        }

                        // A request that has not gone out yet is void as well (RRF Meltingplot/RepRapFirmware#43, as GCodeBuffer::Reset)
                        gb.MacroRequestSent();
                        gb.ResolveMacroRequest(true, false);
                    }
                    if (TryLockBuffer(gb))
                    {
                        gb.AbortFile(true);
                        gb.FileAbortSent();
                        // DSF has removed all its macro files, so a closal that has not gone out yet is void (RRF Meltingplot/RepRapFirmware#45, as GCodeBuffer::Reset)
                        gb.MacroFileClosedSent();
                        InvalidateBufferedCodes(request.Channel);

                        // A code that is still being executed replies on the level that AbortFile has left, see GCodeBuffer::IsCompletelyIdle
                        if (firstCopy && gb.MachineState.LastCodeFromSbc && (gb.CurrentCode is not null || gb.MachineState.State != FakeMachineState.MachineStateKind.Normal))
                        {
                            gb.IsCodeReplySurplus = true;
                        }
                        gb.IsInvalidationPending = false;
                    }
                    else
                    {
                        gb.IsInvalidationPending = true;
                        acknowledged = false;
                    }
                    break;

                case SbcRequest.SetVariable:
                    if (TryLockBuffer(gb!))
                    {
                        SetVariable(gb!, request.Flag, request.Text, request.Argument, AnswerTag(tag));
                    }
                    else
                    {
                        acknowledged = false;
                    }
                    break;

                case SbcRequest.DeleteLocalVariable:
                    if (TryLockBuffer(gb!))
                    {
                        gb!.GetVariables().Remove(request.Text);
                    }
                    else
                    {
                        acknowledged = false;
                    }
                    break;

                case SbcRequest.SetLastCodeResult:
                    if (TryLockBuffer(gb!))
                    {
                        gb!.LastResult = request.CodeResult;
                    }
                    else
                    {
                        acknowledged = false;
                    }
                    break;

                case SbcRequest.ObjectModelKeyChanged:
                    // See RepRap::NetworkUpdated and RepRap::VolumesUpdated
                    if (request.Text.Equals("network", StringComparison.OrdinalIgnoreCase))
                    {
                        ObjectModel.IncrementSeq("network");
                    }
                    else if (request.Text.Equals("volumes", StringComparison.OrdinalIgnoreCase))
                    {
                        ObjectModel.IncrementSeq("volumes");
                    }
                    break;

                case SbcRequest.ActivateLinkCapabilities:
                    // RRF without link capabilities reports an internal error for an unknown request (SbcInterface.cpp), which the tests check for
                    LinkCapabilityRequests.Add(request.LinkCapabilities);
                    if (_offeredLinkCapabilities is not null)
                    {
                        SetActiveLinkCapabilities(request.LinkCapabilities & _offeredLinkCapabilities.Value);
                    }
                    break;

                default:
                    // Results of file operations: the fake never requests file operations
                    break;
            }

            // Request the packet again if no response could be sent back
            if (!acknowledged)
            {
                WritePacket(new FakePacket { Request = FirmwareRequest.ResendPacket, ResendRequest = request });
            }
        }

        // Send code replies and generic messages
        WriteQueuedCodeReplies();

        // Notify DSF about the available buffer space
        if (!codeBufferAvailable || _sendBufferUpdate)
        {
            WritePacket(new FakePacket { Request = FirmwareRequest.CodeBufferUpdate, BufferSpace = (ushort)(SbcCodeBufferSize - _codeBufferUsed) });
            _sendBufferUpdate = false;
        }

        // Deal with code channel requests
        foreach (FakeGCodeBuffer gb in _gcodes.Buffers)
        {
            // Nothing of a buffer whose abort is recorded is written before the main task has applied it (CE-12). A macro wait that began after the
            // record ends here with an error instead of a request, as the record ends one (SbcInterface.cpp:893-894)
            if (gb.IsAbortRecorded)
            {
                if (gb.IsWaitingForMacro)
                {
                    gb.MacroRequestSent();
                    gb.ResolveMacroRequest(true, false);
                }
                continue;
            }

            // Invalidate buffered codes if required
            if (gb.IsInvalidated)
            {
                InvalidateBufferedCodes(gb.Channel);
                gb.IsInvalidated = false;
            }

            // Deal with macro files being closed
            if (gb.IsMacroFileClosed)
            {
                WritePacket(new FakePacket { Request = FirmwareRequest.MacroFileClosed, Channel = gb.Channel, Tag = ChannelTag(gb) });
                gb.MacroFileClosedSent();
            }

            // Handle blocking macro requests. One of a code of an older epoch is not written but ends the wait with an error at once, e.g. a G28 that
            // survived the abort and asks for its next homing file (CE-12 (b))
            if (gb.IsWaitingForMacro && gb.IsMacroRequestPending)
            {
                if (Model == FakeRrfModel.Epoch && gb.IsMacroStartedByCode && gb.IsEpochKnown && gb.CodeEpoch is byte codeEpoch && codeEpoch != gb.Epoch)
                {
                    gb.MacroRequestSent();
                    gb.ResolveMacroRequest(true, false);
                }
                else
                {
                    WritePacket(new FakePacket { Request = FirmwareRequest.ExecuteMacro, Channel = gb.Channel, Text = gb.RequestedMacroFile!, IsFromCode = gb.IsMacroStartedByCode,
                        Tag = gb.IsMacroStartedByCode ? CodeTag(gb) : ChannelTag(gb) });
                    gb.MacroRequestSent();
                    gb.IsInvalidated = true;
                }
            }

            // Deal with other requests unless we are still waiting in a semaphore
            if (!gb.IsWaitingForMacro && TryLockBuffer(gb))
            {
                // Handle file abort requests. All queued replies must go out first because DSF attributes a single file abort to the first code it has buffered
                if (gb.IsAbortRequested)
                {
                    if (!gb.IsAbortAllRequested)
                    {
                        WriteQueuedCodeReplies();
                    }

                    bool abortAll = gb.IsAbortAllRequested;
                    ushort abortTag = ChannelTag(gb);
                    WritePacket(new FakePacket { Request = FirmwareRequest.AbortFile, Channel = gb.Channel, AbortAll = abortAll, Tag = abortTag });
                    if (abortAll && FakeTag.IsTagged(abortTag))
                    {
                        // A tagged abort of all files closes the gate until DSF answers it with InvalidateChannel(e+1) (CE-24)
                        gb.IsGated = true;
                    }

                    // Send back a final code reply for codes that started the last macros, else the corresponding code will never finish
                    if (!abortAll && gb.MachineState.State == FakeMachineState.MachineStateKind.Normal &&
                        (!gb.MachineState.LastCodeFromSbc || gb.MachineState.MacroStartedByCode))
                    {
                        WriteCodeReply(gb.ResponseMessageType, string.Empty, null, gb.Channel);
                    }
                    gb.FileAbortSent();
                    gb.IsInvalidated = true;
                }

                // Handle blocking messages and their results
                if (gb.MachineState.WaitingForAcknowledgement && gb.IsMessagePromptPending)
                {
                    WriteQueuedCodeReplies();
                    WritePacket(new FakePacket { Request = FirmwareRequest.WaitForAcknowledgement, Channel = gb.Channel, Tag = ChannelTag(gb) });
                    gb.MessagePromptSent();
                    gb.IsInvalidated = true;
                }
                else if (gb.IsMessageAcknowledged)
                {
                    WritePacket(new FakePacket { Request = FirmwareRequest.MessageAcknowledged, Channel = gb.Channel, Tag = ChannelTag(gb) });
                    gb.MessageAcknowledgementSent();
                }

                // Handle non-blocking macro requests (e.g. daemon.g)
                if (gb.IsMacroRequestPending)
                {
                    WritePacket(new FakePacket { Request = FirmwareRequest.ExecuteMacro, Channel = gb.Channel, Text = gb.RequestedMacroFile!, IsFromCode = gb.IsMacroStartedByCode,
                        Tag = gb.IsMacroStartedByCode ? CodeTag(gb) : ChannelTag(gb) });
                    gb.MacroRequestSent();
                    gb.IsInvalidated = true;
                }

                // Send pending firmware codes (SbcInterface.cpp:1613-1634). Explicit line numbers are not emulated
                if (gb.IsSendRequested)
                {
                    WritePacket(new FakePacket { Request = FirmwareRequest.DoCode, Channel = gb.Channel, Text = gb.SbcCode!, Tag = ChannelTag(gb) });
                    gb.SentToSbc();
                }
            }
        }

        // Print pause notifications are not emulated
    }

    /// <summary>
    /// Counters of the channel epoch (CE-20). Read them with <see cref="EpochCounterTotal"/> while DSF is connected
    /// </summary>
    public FakeEpochCounters EpochCounters { get; } = new();

    /// <summary>
    /// Sum of the counters of the channel epoch
    /// </summary>
    public int EpochCounterTotal
    {
        get
        {
            lock (_lock)
            {
                return EpochCounters.Total;
            }
        }
    }

    /// <summary>
    /// Requests whose tag the filter checks, in addition to the chunks of a DoCode result (CE-10). InvalidateChannel is decided by its record
    /// </summary>
    private static readonly HashSet<SbcRequest> FilteredRequests =
    [
        SbcRequest.Code, SbcRequest.MacroStarted, SbcRequest.MacroCompleted, SbcRequest.LockAllMovementSystemsAndWaitForStandstill, SbcRequest.Unlock,
        SbcRequest.EvaluateExpression, SbcRequest.SetVariable, SbcRequest.DeleteLocalVariable, SbcRequest.SetLastCodeResult
    ];

    /// <summary>
    /// Apply the adopt rule (CE-4), the gate (CE-24) and the filter of stale DSF packets (CE-10) to a request, before any side effect of it
    /// </summary>
    /// <param name="request">Request from DSF</param>
    /// <param name="gb">Buffer of the request's channel, if any</param>
    /// <param name="tag">Tag of the request as RRF reads it</param>
    /// <returns>Whether the request is dropped. A dropped request gets no answer and is not asked for again</returns>
    private bool DropByEpoch(FakeSbcRequest request, FakeGCodeBuffer? gb, ushort tag)
    {
        if (Model != FakeRrfModel.Epoch)
        {
            return false;
        }

        FakeGCodeBuffer? target = null;
        if (FilteredRequests.Contains(request.Request))
        {
            target = gb;
        }
        else if (request.Request == SbcRequest.Message && FakeTag.IsTagged(tag) && TryGetSingleChannel(request.MessageFlags, out CodeChannel destination))
        {
            // The chunks of a DoCode result carry the tag of their DoCode, other messages are untagged and not bound to a channel
            target = _gcodes[destination];
        }
        if (target is null)
        {
            return false;
        }

        // While the epoch of the channel is unknown, the first tagged packet of the channel sets it, whatever its type
        if (FakeTag.IsTagged(tag) && !target.IsEpochKnown)
        {
            target.Epoch = FakeTag.EpochOf(tag);
            target.IsEpochKnown = true;
        }

        if (target.IsGated)
        {
            EpochCounters.Count(EpochCounters.GateDrops, target.Channel, request.Request);
            return true;
        }
        if (FakeTag.IsTagged(tag) && FakeTag.EpochOf(tag) != target.Epoch)
        {
            EpochCounters.Count(FakeTag.Follows(FakeTag.EpochOf(tag), target.Epoch) ? EpochCounters.AheadDrops : EpochCounters.StaleDrops, target.Channel, request.Request);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Tag of an answer (Locked, EvaluationResult, VariableResult): the tag of the request it answers, without the first marker (CE-6). It is passed
    /// as a parameter, because each answer is written inside the handler of its request
    /// </summary>
    /// <param name="requestTag">Tag of the request as RRF read it</param>
    /// <returns>Tag of the answer</returns>
    private static ushort AnswerTag(ushort requestTag) => FakeTag.IsTagged(requestTag) ? FakeTag.Of(FakeTag.EpochOf(requestTag)) : (ushort)0;

    /// <summary>
    /// Tag source (2) of CE-9: the epoch of the channel when the SBC task writes the packet, none while the epoch is unknown
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <returns>Tag</returns>
    private ushort ChannelTag(FakeGCodeBuffer gb) => (Model == FakeRrfModel.Epoch && gb.IsEpochKnown) ? FakeTag.Of(gb.Epoch) : (ushort)0;

    /// <summary>
    /// Tag source (1) of CE-9: the epoch of the code that the packet belongs to (codeEpoch), none if it is invalid or the channel epoch unknown
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <returns>Tag</returns>
    private ushort CodeTag(FakeGCodeBuffer gb) => (Model == FakeRrfModel.Epoch && gb.IsEpochKnown && gb.CodeEpoch is byte epoch) ? FakeTag.Of(epoch) : (ushort)0;

    /// <summary>
    /// Record an abort of a channel on the SBC task (design/sbc-link-v8-requirements.md CE-7, "Record"). It takes no buffer mutex, so it is never
    /// refused. The main task applies it at the top of its next spin with <see cref="FakeGCodes.EndChannelWork"/>
    /// </summary>
    /// <param name="gb">G-code buffer of the channel</param>
    /// <param name="tag">Tag of the InvalidateChannel, the new epoch of the channel</param>
    private void RecordAbort(FakeGCodeBuffer gb, ushort tag)
    {
        // A tagged InvalidateChannel acts only if the epoch is unknown or the tag follows it (RFC 1982). An equal or preceding one is a repeat, e.g. of a
        // transfer that RRF processes twice, and is counted and ignored (CE-5, CE-7, FD-28). Its tag becomes the epoch and opens the gate (CE-24)
        if (FakeTag.IsTagged(tag))
        {
            byte epoch = FakeTag.EpochOf(tag);
            if (gb.IsEpochKnown && !FakeTag.Follows(epoch, gb.Epoch))
            {
                EpochCounters.Count(EpochCounters.Repeats, gb.Channel, SbcRequest.InvalidateChannel);
                return;
            }
            gb.Epoch = epoch;
            gb.IsEpochKnown = true;
        }
        gb.IsGated = false;

        // Purge the codes of the channel in the ring and clear invalidated, the step the write phase does today (SbcInterface.cpp:1497-1504). A code of
        // the new work is then stored as usual and not dropped by the read-ahead rule (:376-380), whose cause the abort has voided
        InvalidateBufferedCodes(gb.Channel);
        gb.IsInvalidated = false;

        // Until E3 the fork catch code of RRF #46 flags a code that waits for its macro (:886-889)
        if (CodeReplySurplus && gb.IsWaitingForMacro && gb.IsMacroStartedByCode)
        {
            gb.IsCodeReplySurplus = true;
        }

        // Flag the abort for the main task before the next packet is read
        gb.IsAbortRecorded = true;
        _gcodes.RequestApply(_applyDelay, _transferNumber);

        // End a macro wait with an error, so that a main task blocked in it returns (:893-894)
        if (gb.IsWaitingForMacro)
        {
            gb.MacroRequestSent();
            gb.ResolveMacroRequest(true, false);
        }
    }

    /// <summary>
    /// Invalidate the resources of the SBC interface after a lost connection, see SbcInterface::InvalidateResources (SbcInterface.cpp:1717-1780).
    /// From milestone E1 it runs the routine of a channel abort on every buffer (CE-25), which also fixes K5
    /// </summary>
    private void InvalidateResources()
    {
        _codeBuffer.Clear();
        _codeBufferUsed = 0;
        _sendBufferUpdate = true;
        _gcodeReply.Clear();
        _txPackets.Clear();
        foreach (FakeGCodeBuffer gb in _gcodes.Buffers)
        {
            if (gb.IsWaitingForMacro)
            {
                gb.ResolveMacroRequest(true, false);
            }

            // RRF takes the buffer mutex without a timeout here, and the main task finishes a code that waited for its macro before it releases it
            if (gb.PendingMacroCall is not null)
            {
                _gcodes.ContinueMacroCall(gb);
            }

            if (Model == FakeRrfModel.V7)
            {
                if (gb.IsExecutingOnSbc)
                {
                    gb.SetFinished(true);
                }
                gb.MacroRequestSent();
                gb.AbortFile(true);
                gb.FileAbortSent();
                gb.MessageAcknowledged(true, true, 0);
            }
            else
            {
                _gcodes.EndChannelWork(gb);
                gb.IsAbortRecorded = false;
                gb.IsCodeReplySurplus = false;
            }
            gb.ForgetEpoch();
        }
    }

    /// <summary>
    /// Try to take the mutex of a G-code buffer from the SBC task.
    /// While the main task waits in RequestMacroFile for a macro started by a code, it holds the mutex and the SBC task gives up
    /// after SbcYieldTimeout. Once the request has been resolved, the main task finishes that code before it releases the mutex.
    /// From milestone E1 the SBC task keeps off a buffer whose abort it has recorded until the main task has applied it (CE-7 (d)),
    /// so the packets that need the mutex are refused as for a busy one
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <returns>Whether the mutex could be taken</returns>
    private bool TryLockBuffer(FakeGCodeBuffer gb)
    {
        if (gb.IsAbortRecorded)
        {
            return false;
        }
        if (_failedBufferLocks.TryGetValue(gb.Channel, out int failures) && failures > 0)
        {
            _failedBufferLocks[gb.Channel] = failures - 1;
            return false;
        }
        if (gb.PendingMacroCall is { HoldsMutex: true })
        {
            if (gb.IsWaitingForMacro)
            {
                return false;
            }
            _gcodes.ContinueMacroCall(gb);
        }
        return true;
    }

    /// <summary>
    /// Store a code in the code buffer, see the Code case in SbcInterface::ExchangeData
    /// </summary>
    /// <param name="code">Code to store</param>
    /// <param name="gb">G-code buffer of the code's channel</param>
    /// <param name="codeBufferAvailable">Whether codes can still be stored in this transfer</param>
    /// <returns>Whether the packet has been acknowledged</returns>
    /// <param name="tag">Tag of the Code packet, which passed the filter</param>
    private bool StoreCode(FakeCode code, FakeGCodeBuffer gb, ushort tag, ref bool codeBufferAvailable)
    {
        _receivedCodes.Add(code);

        // Don't deal with codes that will be thrown away
        if (gb.IsInvalidated)
        {
            _discardedCodes.Add(code);
            return true;
        }

        // Check if a GB is waiting for a macro file to be started
        if (gb.IsWaitingForMacro && !gb.IsMacroRequestPending)
        {
            gb.ResolveMacroRequest(false, false);
        }

        // Don't process any more codes if we failed to store them last time
        if (!codeBufferAvailable)
        {
            _receivedCodes.Remove(code);
            return false;
        }

        // Make sure no existing codes are overwritten. The ring buffer and its fragmentation are not emulated
        int bufferedCodeSize = Consts.BufferedCodeHeaderSize + code.BinaryLength;
        if (bufferedCodeSize > SbcCodeBufferSize - _codeBufferUsed)
        {
            _receivedCodes.Remove(code);
            codeBufferAvailable = false;
            return false;
        }

        // Stamp the code with the epoch of its packet in the free padding of its header (CE-9)
        code.Stamp = FakeTag.IsTagged(tag) ? FakeTag.EpochOf(tag) : null;
        _codeBuffer.Add(code);
        _codeBufferUsed += bufferedCodeSize;
        _sendBufferUpdate = true;
        return true;
    }

    /// <summary>
    /// Fetch the next buffered code of a channel, see SbcInterface::FillBuffer
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <returns>Whether a code has been fetched</returns>
    internal bool FillBuffer(FakeGCodeBuffer gb)
    {
        if (gb.IsInvalidated || gb.IsMacroFileClosed || gb.IsMessageAcknowledged || gb.IsAbortRequested ||
            (gb.MachineState.WaitingForAcknowledgement && gb.IsMessagePromptPending) || gb.IsAbortRecorded)
        {
            // Don't process codes that are supposed to be suspended. A recorded abort holds the codes as well, because within one spin the main task
            // fetches up to 8 codes without passing the guard of SpinGCodeBuffer again (CE-7 (b), GCodes.cpp:808-828)
            return false;
        }

        int index = _codeBuffer.FindIndex(code => code.Channel == gb.Channel);
        if (index < 0)
        {
            return false;
        }

        FakeCode code = _codeBuffer[index];
        _codeBuffer.RemoveAt(index);
        _codeBufferUsed -= Consts.BufferedCodeHeaderSize + code.BinaryLength;
        _sendBufferUpdate = true;
        gb.PutBinary(code);
        return true;
    }

    /// <summary>
    /// Discard the buffered codes of a channel, see SbcInterface::InvalidateBufferedCodes
    /// </summary>
    /// <param name="channel">Code channel</param>
    private void InvalidateBufferedCodes(CodeChannel channel)
    {
        foreach (FakeCode code in _codeBuffer.Where(code => code.Channel == channel).ToList())
        {
            _codeBuffer.Remove(code);
            _codeBufferUsed -= Consts.BufferedCodeHeaderSize + code.BinaryLength;
            _discardedCodes.Add(code);
            _sendBufferUpdate = true;
        }
    }

    /// <summary>
    /// Set a variable, see the SetVariable case in SbcInterface::ExchangeData. Array indices are not emulated
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <param name="createVariable">Whether the variable is created</param>
    /// <param name="varName">Name of the variable including global or var prefix</param>
    /// <param name="expression">Expression to assign</param>
    /// <param name="tag">Tag of the answer</param>
    private void SetVariable(FakeGCodeBuffer gb, bool createVariable, string varName, string expression, ushort tag)
    {
        void writeResult(object? value) => WritePacket(new FakePacket { Request = FirmwareRequest.VariableResult, Channel = gb.Channel, Text = varName, Value = value, Tag = tag });

        bool isGlobal = varName.StartsWith("global.");
        if (!isGlobal && !varName.StartsWith("var."))
        {
            writeResult(new CodeParserException("expected a global or local variable"));
            return;
        }

        string shortVarName = varName[(isGlobal ? "global.".Length : "var.".Length)..];
        if (!createVariable && shortVarName.Contains('['))
        {
            writeResult(new CodeParserException($"FakeFirmware: array indices are not emulated ('{varName}')"));
            _unemulated.Add($"Indexed assignment of {varName}");
            return;
        }

        Dictionary<string, object?> localVariables = gb.GetVariables();
        bool exists = isGlobal ? ObjectModel.TryGetGlobal(shortVarName, out _) : localVariables.ContainsKey(shortVarName);
        if (createVariable && exists)
        {
            writeResult(new CodeParserException($"variable '{varName}' already exists"));
            return;
        }
        if (!createVariable && !exists)
        {
            writeResult(new CodeParserException($"unknown variable '{varName}'"));
            return;
        }

        object? value;
        try
        {
            value = _gcodes.Evaluate(gb, expression);
        }
        catch (FakeExpressionException e)
        {
            writeResult(new CodeParserException(e.Message));
            return;
        }

        if (isGlobal)
        {
            // This increments the global sequence number, see RepRap::GlobalUpdated
            ObjectModel.SetGlobal(shortVarName, value);
        }
        else
        {
            localVariables[shortVarName] = value;
        }
        writeResult(value);
    }

    /// <summary>
    /// Write the queued code replies and messages, see SbcInterface::WriteQueuedCodeReplies
    /// </summary>
    private void WriteQueuedCodeReplies()
    {
        foreach (var (type, text, code, _) in _gcodeReply)
        {
            WriteCodeReply(type, text.ToString(), code, code?.Channel ?? CodeChannel.Unknown);
        }
        _gcodeReply.Clear();
    }

    /// <summary>
    /// Write a message, see DataTransfer::WriteCodeReply: the epoch of a code reply moves from the free MessageType bits into the header, and the bits
    /// are cleared in the type that goes out (CE-11)
    /// </summary>
    /// <param name="type">Message type, possibly with the epoch bits of GetResponseMessageType</param>
    /// <param name="text">Message text</param>
    /// <param name="code">Code the reply belongs to, if known</param>
    /// <param name="channel">Channel of the packet log</param>
    private void WriteCodeReply(MessageTypeFlags type, string text, FakeCode? code, CodeChannel channel)
    {
        uint bits = (uint)type & FakeTag.TypeEpochBits;
        ushort tag = ((bits & FakeTag.TypeEpochValid) != 0) ? FakeTag.Of((int)(bits >> FakeTag.TypeEpochShift)) : (ushort)0;
        WritePacket(new FakePacket { Request = FirmwareRequest.Message, MessageType = (MessageTypeFlags)((uint)type & ~FakeTag.TypeEpochBits), Text = text, RelatedCode = code, Channel = channel, Tag = tag });
    }

    /// <summary>
    /// Write a packet for the next transfer
    /// </summary>
    /// <param name="packet">Packet to write</param>
    private void WritePacket(FakePacket packet)
    {
        packet.Id = (ushort)_txPackets.Count;
        _txPackets.Add(packet);
    }

    /// <summary>
    /// Get the channel of a message whose only destination is one code channel, as SbcInterface.cpp:774-793 checks with
    /// Bitmap::IsOnlyBitSet over the destination bits
    /// </summary>
    /// <param name="flags">Message flags</param>
    /// <param name="channel">Channel of the destination</param>
    /// <returns>Whether exactly one channel is the destination</returns>
    private static bool TryGetSingleChannel(MessageTypeFlags flags, out CodeChannel channel)
    {
        uint destinations = (uint)flags & ((1u << (int)CodeChannel.Unknown) - 1);
        if (destinations != 0 && (destinations & (destinations - 1)) == 0)
        {
            channel = (CodeChannel)System.Numerics.BitOperations.TrailingZeroCount(destinations);
            return true;
        }
        channel = CodeChannel.Unknown;
        return false;
    }
    #endregion

    #region Interface for the emulated main task
    /// <summary>
    /// Output a message, see Platform::Message and SbcInterface::HandleGCodeReply. Only messages that go to the SBC are emulated
    /// </summary>
    /// <param name="type">Message type</param>
    /// <param name="message">Message content</param>
    /// <param name="relatedCode">Code that the message belongs to, if any</param>
    /// <param name="isOutputBuffer">Whether the message comes in an output buffer, which is never combined with a previous one</param>
    internal void PlatformMessage(MessageTypeFlags type, string message, FakeCode? relatedCode, bool isOutputBuffer = false)
    {
        if (!type.HasFlag(MessageTypeFlags.BinaryCodeReplyFlag) && (type & MessageTypeFlags.GenericMessage) != MessageTypeFlags.GenericMessage &&
            (type & MessageTypeFlags.LogOff) == MessageTypeFlags.LogOff)
        {
            // Not for the SBC
            return;
        }

        if (!isOutputBuffer && _gcodeReply.Count > 0)
        {
            var last = _gcodeReply[^1];
            if (!last.isOutputBuffer && last.text.Length > 0 && last.type == type && type.HasFlag(MessageTypeFlags.PushFlag))
            {
                // Try to save some space by combining segments that have the Push flag set
                last.text.Append(message);
                return;
            }
        }
        _gcodeReply.Add((type, new StringBuilder(message), relatedCode, isOutputBuffer));
    }

    /// <summary>
    /// Run the test handler of a code
    /// </summary>
    /// <param name="code">Code to handle</param>
    /// <returns>Reply or null if there is no handler or it falls back to the emulated behaviour</returns>
    internal FakeReply? RunCodeHandler(FakeCode code)
    {
        if (_codeHandlers.TryGetValue(code.Command, out Func<FakeCode, FakeReply?>? handler))
        {
            return handler(code);
        }
        return _anyCodeHandler?.Invoke(code);
    }

    /// <summary>
    /// Record that the main task started a code
    /// </summary>
    /// <param name="code">Code being executed</param>
    internal void LogExecutedCode(FakeCode code) => _executedCodes.Add(code);

    /// <summary>
    /// Record something the fake does not emulate
    /// </summary>
    /// <param name="description">Description</param>
    internal void LogUnemulated(string description) => _unemulated.Add(description);
    #endregion

    #region Read functions
    /// <inheritdoc />
    public int PacketsToRead
    {
        get
        {
            lock (_lock)
            {
                return _rxPackets.Count;
            }
        }
    }

    /// <inheritdoc />
    public PacketHeader? ReadNextPacket()
    {
        lock (_lock)
        {
            if (_rxIndex >= _rxPackets.Count)
            {
                return null;
            }

            _currentPacket = _rxPackets[_rxIndex++];
            return new PacketHeader
            {
                Request = (ushort)_currentPacket.Request,
                Id = _currentPacket.Id,
                ResendPacketId = _currentPacket.ResendRequest?.Id ?? _currentPacket.Tag
            };
        }
    }

    /// <summary>
    /// Get the packet being read and check its type
    /// </summary>
    /// <param name="requests">Expected packet types</param>
    /// <returns>Current packet</returns>
    private FakePacket CurrentPacket(params FirmwareRequest[] requests)
    {
        lock (_lock)
        {
            if (_currentPacket is null || !requests.Contains(_currentPacket.Request))
            {
                throw new InvalidOperationException($"Expected a {string.Join('/', requests)} packet but got {_currentPacket?.Request.ToString() ?? "none"}");
            }
            return _currentPacket;
        }
    }

    /// <inheritdoc />
    public void ReadObjectModel(out ReadOnlySpan<byte> json) => json = Encoding.UTF8.GetBytes(CurrentPacket(FirmwareRequest.ObjectModel).Text);

    /// <inheritdoc />
    public void ReadCodeBufferUpdate(out ushort bufferSpace) => bufferSpace = CurrentPacket(FirmwareRequest.CodeBufferUpdate).BufferSpace;

    /// <inheritdoc />
    public void ReadMessage(out MessageTypeFlags messageType, out string reply)
    {
        FakePacket packet = CurrentPacket(FirmwareRequest.Message);
        messageType = packet.MessageType;
        reply = packet.Text;
    }

    /// <inheritdoc />
    public void ReadMacroRequest(out CodeChannel channel, out bool isSystemMacro, out string filename)
    {
        // LinkService passes this flag on as fromCode, which is what RRF writes into the packet
        FakePacket packet = CurrentPacket(FirmwareRequest.ExecuteMacro);
        channel = packet.Channel;
        isSystemMacro = packet.IsFromCode;
        filename = packet.Text;
    }

    /// <inheritdoc />
    public void ReadAbortFile(out CodeChannel channel, out bool abortAll)
    {
        FakePacket packet = CurrentPacket(FirmwareRequest.AbortFile);
        channel = packet.Channel;
        abortAll = packet.AbortAll;
    }

    /// <inheritdoc />
    public void ReadPrintPaused(out uint filePosition, out uint filePosition2, out PrintPausedReason reason) => throw new NotSupportedException("The fake firmware does not pause prints");

    /// <inheritdoc />
    public void ReadCodeChannel(out CodeChannel channel) => channel = CurrentPacket(FirmwareRequest.Locked, FirmwareRequest.WaitForAcknowledgement, FirmwareRequest.MacroFileClosed, FirmwareRequest.MessageAcknowledged).Channel;

    /// <inheritdoc />
    public void ReadFileChunkRequest(out string filename, out uint offset, out int maxLength) => throw new NotSupportedException("The fake firmware does not request files");

    /// <inheritdoc />
    public void ReadEvaluationResult(out CodeChannel? channel, out string expression, out object? result)
    {
        FakePacket packet = CurrentPacket(FirmwareRequest.EvaluationResult, FirmwareRequest.VariableResult);
        channel = packet.Channel;
        expression = packet.Text;
        result = packet.Value;
    }

    /// <inheritdoc />
    public void ReadDoCode(out CodeChannel channel, out string code)
    {
        FakePacket packet = CurrentPacket(FirmwareRequest.DoCode);
        channel = packet.Channel;
        code = packet.Text;
    }

    /// <inheritdoc />
    public void ReadCheckFileExists(out string filename) => throw new NotSupportedException("The fake firmware does not request files");

    /// <inheritdoc />
    public void ReadDeleteFileOrDirectory(out string filename) => throw new NotSupportedException("The fake firmware does not request files");

    /// <inheritdoc />
    public void ReadGetFileList(out string directory, out uint startIndex, out uint maxLength) => throw new NotSupportedException("The fake firmware does not request files");

    /// <inheritdoc />
    public void ReadOpenFile(out string filename, out bool forWriting, out bool append, out long preAllocSize) => throw new NotSupportedException("The fake firmware does not request files");

    /// <inheritdoc />
    public void ReadSeekFile(out uint handle, out long offset) => throw new NotSupportedException("The fake firmware does not request files");

    /// <inheritdoc />
    public void ReadTruncateFile(out uint handle) => throw new NotSupportedException("The fake firmware does not request files");

    /// <inheritdoc />
    public void ReadFileRequest(out uint handle, out int maxLength) => throw new NotSupportedException("The fake firmware does not request files");

    /// <inheritdoc />
    public void ReadWriteRequest(out uint handle, out ReadOnlySpan<byte> data) => throw new NotSupportedException("The fake firmware does not request files");

    /// <inheritdoc />
    public void ReadCloseFile(out uint handle) => throw new NotSupportedException("The fake firmware does not request files");

    /// <inheritdoc />
    public void DumpMalformedPacket() { }

    /// <inheritdoc />
    public void ResendPacket(PacketHeader packet, out DuetControlServer.Link.Protocol.SbcRequests.Request sbcRequest, out CodeChannel? invalidatedChannel)
    {
        lock (_lock)
        {
            FakeSbcRequest original = CurrentPacket(FirmwareRequest.ResendPacket).ResendRequest ?? throw new ArgumentException($"Firmware requested resend for invalid packet #{packet.ResendPacketId}");
            AddRequest(original.CloneForResend());
            if (original.Request != SbcRequest.LockAllMovementSystemsAndWaitForStandstill)
            {
                _requestsBeingResent.Add(original.Request);
            }
            sbcRequest = original.Request;
            invalidatedChannel = (original.Request == SbcRequest.InvalidateChannel) ? original.Channel : null;
        }
    }
    #endregion

    #region Write functions
    /// <summary>
    /// Queue a request for the next transfer
    /// </summary>
    /// <param name="request">Request to queue</param>
    /// <returns>Whether the request could be written</returns>
    private bool AddRequest(FakeSbcRequest request)
    {
        lock (_lock)
        {
            // Don't send a new request if another one of the same type is still pending, see SPI.cs
            if (_requestsBeingResent.Contains(request.Request) && !RequestsWithoutResendCheck.Contains(request.Request))
            {
                return false;
            }

            request.Id = (ushort)_incoming.Count;
            _incoming.Add(request);
            return true;
        }
    }

    /// <inheritdoc />
    public bool WriteEmergencyStop() => AddRequest(new FakeSbcRequest { Request = SbcRequest.EmergencyStop });

    /// <inheritdoc />
    public bool WriteReset()
    {
        lock (_lock)
        {
            // SPI.cs discards the other packets of this transfer
            _incoming.Clear();
            return AddRequest(new FakeSbcRequest { Request = SbcRequest.Reset });
        }
    }

    /// <inheritdoc />
    public bool WriteCode(DuetControlServer.Commands.Code code, ushort tag)
    {
        lock (_lock)
        {
            if (_requestsBeingResent.Contains(SbcRequest.Code))
            {
                return false;
            }
            return AddRequest(new FakeSbcRequest { Request = SbcRequest.Code, Channel = code.Channel, Code = new FakeCode(code, _maxCodeLength), Tag = tag });
        }
    }

    /// <inheritdoc />
    public bool WriteGetObjectModel(string key, string flags) => AddRequest(new FakeSbcRequest { Request = SbcRequest.GetObjectModel, Text = key, Argument = flags });

    /// <inheritdoc />
    public LinkCapabilities SupportedLinkCapabilities { get; set; } = LinkCapabilities.All;

    /// <inheritdoc />
    public bool WriteActivateLinkCapabilities(LinkCapabilities capabilities) => AddRequest(new FakeSbcRequest { Request = SbcRequest.ActivateLinkCapabilities, LinkCapabilities = capabilities });

    /// <inheritdoc />
    public bool WritePrintFileInfo(GCodeFileInfo info) => AddRequest(new FakeSbcRequest { Request = SbcRequest.SetPrintFileInfo, Text = info.FileName });

    /// <inheritdoc />
    public bool WritePrintStopped(PrintStoppedReason reason) => AddRequest(new FakeSbcRequest { Request = SbcRequest.PrintStopped, PrintStoppedReason = reason });

    /// <inheritdoc />
    public bool WriteMacroCompleted(CodeChannel channel, bool error, ushort tag) => AddRequest(new FakeSbcRequest { Request = SbcRequest.MacroCompleted, Channel = channel, Flag = error, Tag = tag });

    /// <inheritdoc />
    public bool WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel channel, ushort tag) => AddRequest(new FakeSbcRequest { Request = SbcRequest.LockAllMovementSystemsAndWaitForStandstill, Channel = channel, Tag = tag });

    /// <inheritdoc />
    public bool WriteUnlock(CodeChannel channel, ushort tag) => AddRequest(new FakeSbcRequest { Request = SbcRequest.Unlock, Channel = channel, Tag = tag });

    /// <inheritdoc />
    public bool WriteFileChunk(Span<byte> data, long fileLength) => AddRequest(new FakeSbcRequest { Request = SbcRequest.FileChunk });

    /// <inheritdoc />
    public bool WriteEvaluateExpression(CodeChannel channel, string expression, ushort tag) => AddRequest(new FakeSbcRequest { Request = SbcRequest.EvaluateExpression, Channel = channel, Text = expression, Tag = tag });

    /// <inheritdoc />
    public bool WriteMessage(MessageTypeFlags flags, string message, ushort tag) => AddRequest(new FakeSbcRequest { Request = SbcRequest.Message, Channel = CodeChannel.Unknown, MessageFlags = flags, Text = message, Tag = tag });

    /// <inheritdoc />
    public bool WriteMacroStarted(CodeChannel channel, ushort tag) => AddRequest(new FakeSbcRequest { Request = SbcRequest.MacroStarted, Channel = channel, Tag = tag });

    /// <inheritdoc />
    public bool WriteInvalidateChannel(CodeChannel channel, ushort tag) => AddRequest(new FakeSbcRequest { Request = SbcRequest.InvalidateChannel, Channel = channel, Tag = tag });

    /// <inheritdoc />
    public bool WriteSetVariable(CodeChannel channel, bool createVariable, string varName, string expression, ushort tag) => AddRequest(new FakeSbcRequest { Request = SbcRequest.SetVariable, Channel = channel, Flag = createVariable, Text = varName, Argument = expression, Tag = tag });

    /// <inheritdoc />
    public bool WriteDeleteLocalVariable(CodeChannel channel, string varName, ushort tag) => AddRequest(new FakeSbcRequest { Request = SbcRequest.DeleteLocalVariable, Channel = channel, Text = varName, Tag = tag });

    /// <inheritdoc />
    public bool WriteCheckFileExistsResult(bool exists) => AddRequest(new FakeSbcRequest { Request = SbcRequest.CheckFileExistsResult, Flag = exists });

    /// <inheritdoc />
    public bool WriteFileDeleteResult(bool success) => AddRequest(new FakeSbcRequest { Request = SbcRequest.FileDeleteResult, Flag = success });

    /// <inheritdoc />
    public bool WriteFileListResult(IList<FileSystemInfo> entries, bool endOfList) => AddRequest(new FakeSbcRequest { Request = SbcRequest.FileListResult, Flag = endOfList });

    /// <inheritdoc />
    public bool WriteOpenFileResult(uint fileHandle, long length) => AddRequest(new FakeSbcRequest { Request = SbcRequest.OpenFileResult });

    /// <inheritdoc />
    public bool WriteFileReadResult(Span<byte> data, int bytesRead) => AddRequest(new FakeSbcRequest { Request = SbcRequest.FileReadResult });

    /// <inheritdoc />
    public bool WriteFileWriteResult(bool success) => AddRequest(new FakeSbcRequest { Request = SbcRequest.FileWriteResult, Flag = success });

    /// <inheritdoc />
    public bool WriteFileSeekResult(bool success) => AddRequest(new FakeSbcRequest { Request = SbcRequest.FileSeekResult, Flag = success });

    /// <inheritdoc />
    public bool WriteFileTruncateResult(bool success) => AddRequest(new FakeSbcRequest { Request = SbcRequest.FileTruncateResult, Flag = success });

    /// <inheritdoc />
    public bool WriteSetLastCodeResult(CodeChannel channel, CodeResult result, ushort tag) => AddRequest(new FakeSbcRequest { Request = SbcRequest.SetLastCodeResult, Channel = channel, CodeResult = result, Tag = tag });

    /// <inheritdoc />
    public bool WriteObjectModelKeyChanged(string key) => AddRequest(new FakeSbcRequest { Request = SbcRequest.ObjectModelKeyChanged, Text = key });
    #endregion
}
