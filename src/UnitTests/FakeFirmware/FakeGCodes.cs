using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.Link.Protocol.Shared;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Emulation of RRF's main task (GCodes::Spin and friends in src/GCodes/GCodes*.cpp) for codes that DSF sends in binary form.
/// It runs between two transfers, so it sees the codes the SBC task stored and its replies go out with the next transfer.
/// All members must be accessed with the lock of the <see cref="FakeFirmware"/> held
/// </summary>
/// <param name="firmware">Fake firmware this main task belongs to</param>
internal sealed class FakeGCodes(FakeFirmware firmware)
{
    /// <summary>
    /// Code numbers of macros started by RRF itself, see GCodes.h
    /// </summary>
    internal const int ToolChangeMacroCode = -1, SystemHelperMacroCode = -2, AsyncSystemMacroCode = -3;

    /// <summary>
    /// Maximum number of buffered SBC codes executed per spin, see GCodes.cpp
    /// </summary>
    private const int MaxSbcFileCodesPerSpin = 8;

    /// <summary>
    /// Macro call that waits for DSF to answer the macro request.
    /// In RRF the main task blocks inside GCodeBuffer::RequestMacroFile, the fake stores the rest of GCodes::DoFileMacro here
    /// </summary>
    internal sealed class MacroCall
    {
        /// <summary>
        /// Requested macro file
        /// </summary>
        public required string FileName { get; init; }

        /// <summary>
        /// Whether a missing file is reported
        /// </summary>
        public bool ReportMissing { get; init; }

        /// <summary>
        /// Code that runs the macro, see the codeRunning parameter of GCodes::DoFileMacro
        /// </summary>
        public int CodeRunning { get; init; }

        /// <summary>
        /// Macro parameters
        /// </summary>
        public Dictionary<string, object?> Parameters { get; init; } = [];

        /// <summary>
        /// Code that called the macro and finishes when the request has been resolved (M98), or null for system macros
        /// </summary>
        public FakeCode? Code { get; init; }

        /// <summary>
        /// Rest of the state machine step that requested the macro, called with the result of DoFileMacro once DSF has answered, or null
        /// </summary>
        public Action<bool>? Continuation { get; init; }

        /// <summary>
        /// Whether the main task holds the mutex of the G-code buffer while it waits. This is the case if the macro is requested by a code or by
        /// a step of the state machine, because GCodes::SpinGCodeBuffer locks the buffer around ActOnCode and RunStateMachine (GCodes.cpp:600)
        /// </summary>
        public bool HoldsMutex => Code is not null || Continuation is not null;
    }

    /// <summary>
    /// Message box opened by M291, see Platform/MessageBox.cpp
    /// </summary>
    /// <param name="Seq">Sequence number</param>
    /// <param name="Mode">Mode (S parameter)</param>
    /// <param name="ShouldAbort">Whether cancelling it aborts the calling macro</param>
    /// <param name="Message">Message text</param>
    /// <param name="Title">Title</param>
    internal sealed record MessageBox(uint Seq, int Mode, bool ShouldAbort, string Message, string Title)
    {
        /// <summary>
        /// Whether the message box blocks the code that opened it
        /// </summary>
        public bool IsBlocking => Mode >= 2;
    }

    /// <summary>
    /// Emulated G-code buffers, one per channel
    /// </summary>
    public FakeGCodeBuffer[] Buffers { get; } = [.. Enum.GetValues<CodeChannel>().Where(channel => channel != CodeChannel.Unknown).Select(channel => new FakeGCodeBuffer(channel))];

    /// <summary>
    /// Get the buffer of a channel
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <returns>G-code buffer</returns>
    public FakeGCodeBuffer this[CodeChannel channel] => Buffers[(int)channel];

    /// <summary>
    /// Whether the machine has been stopped by an emergency stop (GCodes::stopped)
    /// </summary>
    public bool Stopped { get; private set; }

    /// <summary>
    /// Buffer that owns the movement lock, see GCodes::resourceOwners. All movement systems are treated as one resource
    /// </summary>
    public FakeGCodeBuffer? MovementLockOwner { get; private set; }

    /// <summary>
    /// Open message boxes
    /// </summary>
    public List<MessageBox> MessageBoxes { get; } = [];

    /// <summary>
    /// Axes still to be homed by the G28 that runs, lowest first, see GCodes::toBeHomed. Like in RRF it is one for the whole machine
    /// </summary>
    private readonly List<char> _toBeHomed = [];

    /// <summary>
    /// Axes that are homed, see GCodes::axesHomed
    /// </summary>
    public HashSet<char> AxesHomed { get; } = [];

    /// <summary>
    /// Whether a tool change is in progress, see GCodes::doingToolChange
    /// </summary>
    public bool DoingToolChange { get; private set; }

    /// <summary>
    /// Whether GCodeBuffer::AbortFile clears macroJustStarted, as RRF does since 67e3c69f0. Only the Epoch model has that fix, V7 and RecordApply model
    /// firmware without it
    /// </summary>
    private bool AbortFileClearsJustStartedMacro => firmware.Model == FakeRrfModel.Epoch;

    /// <summary>
    /// Clean up the side effects of the state machine on every frame of a buffer before its files are aborted, see GCodes::AbortStateMachine
    /// (GCodes4.cpp:2028-2080). Like in RRF it does not change the state of a frame, so a code at the base level goes on with its next step. The
    /// probing, flashing and event states are not emulated
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    private void AbortStateMachine(FakeGCodeBuffer gb)
    {
        for (FakeMachineState? ms = gb.MachineState; ms is not null; ms = ms.Previous)
        {
            switch (ms.State)
            {
                case FakeMachineState.MachineStateKind.Homing1:
                case FakeMachineState.MachineStateKind.Homing2:
                    _toBeHomed.Clear();
                    break;

                case FakeMachineState.MachineStateKind.ToolChange0:
                case FakeMachineState.MachineStateKind.ToolChange1:
                case FakeMachineState.MachineStateKind.ToolChange2:
                case FakeMachineState.MachineStateKind.ToolChangeComplete:
                    DoingToolChange = false;
                    break;
            }
        }
    }

    /// <summary>
    /// Cancel the macros and the state machine of a buffer, see GCodes::AbortPrint (GCodes.cpp:3643-3660): PauseSequenceAborted (pausing is not
    /// emulated), AbortStateMachine and AbortFile(true). Stopping the print of a file channel is not emulated
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    private void AbortPrint(FakeGCodeBuffer gb)
    {
        AbortStateMachine(gb);
        gb.AbortFile(true, AbortFileClearsJustStartedMacro);
    }

    /// <summary>
    /// Last message box sequence number
    /// </summary>
    private uint _lastMessageBoxSeq;

    /// <summary>
    /// Emergency stop requested by the SBC task (requestsFromOtherTasks)
    /// </summary>
    private bool _emergencyStopRequested;

    /// <summary>
    /// System macros requested by tests, see <see cref="FakeFirmware.RequestMacro"/>
    /// </summary>
    private readonly Queue<(CodeChannel channel, string fileName, bool reportMissing)> _macroRequests = new();

    /// <summary>
    /// Number of triggers, see MaxTriggers in Pins_Duet3_MB6HC.h (the bitmap of pending triggers is a uint32_t)
    /// </summary>
    public const int MaxTriggers = 32;

    /// <summary>
    /// Bitmap of triggers that have fired but whose macros have not been started yet, see GCodes::triggersPending
    /// </summary>
    public uint TriggersPending { get; private set; }

    /// <summary>
    /// Mark a trigger as pending like GCodes::CheckTriggers does when TriggerItem::Check reports that it fired (GCodes.cpp:983-989).
    /// M582 S1 does the same (GCodes3.cpp:422-431). A trigger that is pending already stays pending only once
    /// </summary>
    /// <param name="number">Trigger number from 2 up, because trigger 0 (emergency stop) and trigger 1 (pause) are not emulated</param>
    public void SetTriggerPending(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(number, MaxTriggers);
        TriggersPending |= 1u << number;
    }

    /// <summary>
    /// Whether the main task is blocked in GCodeBuffer::RequestMacroFile
    /// </summary>
    public bool IsBlocked => Buffers.Any(gb => gb.IsWaitingForMacro);

    /// <summary>
    /// Whether a channel other than daemon and trigger runs a macro, see GCodes::DoingFileMacro
    /// </summary>
    public bool DoingFileMacro => Buffers.Any(gb => gb.Channel is not CodeChannel.Daemon and not CodeChannel.Trigger && gb.IsDoingFileMacro());

    /// <summary>
    /// Whether a channel waits for a message box, see GCodes::WaitingForAcknowledgement
    /// </summary>
    public bool WaitingForAcknowledgement => Buffers.Any(gb => gb.MachineState.WaitingForAcknowledgement);

    /// <summary>
    /// Queue a system macro, which is started when its channel is idle like GCodes::CheckTriggers starts trigger macros
    /// </summary>
    /// <param name="channel">Channel to run the macro on</param>
    /// <param name="fileName">Macro file</param>
    /// <param name="reportMissing">Whether a missing file is reported</param>
    public void QueueMacroRequest(CodeChannel channel, string fileName, bool reportMissing) => _macroRequests.Enqueue((channel, fileName, reportMissing));

    /// <summary>
    /// Text codes that a channel passes on to DSF once it is idle, see <see cref="FakeFirmware.PassCodeToSbc"/>
    /// </summary>
    private readonly Queue<(CodeChannel channel, string code)> _sbcCodes = new();

    /// <summary>
    /// Queue a text code that RRF cannot run itself, so it passes it on to DSF (GCodes2.cpp:600-606)
    /// </summary>
    /// <param name="channel">Channel of the code</param>
    /// <param name="code">Text of the code</param>
    public void QueueSbcCode(CodeChannel channel, string code) => _sbcCodes.Enqueue((channel, code));

    /// <summary>
    /// Whether a text code of a channel waits to be taken by the main task
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <returns>Whether one is queued</returns>
    public bool HasQueuedSbcCode(CodeChannel channel) => _sbcCodes.Any(item => item.channel == channel);

    /// <summary>
    /// Channels whose files the main task aborts on its next spin, see <see cref="FakeFirmware.AbortAllFiles"/>
    /// </summary>
    private readonly HashSet<CodeChannel> _abortAllFiles = [];

    /// <summary>
    /// Let the main task abort all files of a channel on its next spin, as GCodes::AbortPrint does after an error of the state machine
    /// (GCodes.cpp:3623-3640). GCodeBuffer::AbortFile(true) flags the abort for DSF, which the SBC task writes as AbortFile(all)
    /// </summary>
    /// <param name="channel">Code channel</param>
    public void QueueAbortAllFiles(CodeChannel channel) => _abortAllFiles.Add(channel);

    /// <summary>
    /// Called by the SBC task on an emergency stop. The main task carries it out on its next spin, see GCodes::EmergencyStop
    /// </summary>
    public void RequestEmergencyStop() => _emergencyStopRequested = true;

    /// <summary>
    /// Whether the SBC task has recorded a channel abort that the main task has yet to apply (the request bit of CE-7 in requestsFromOtherTasks,
    /// GCodes.cpp:69-76)
    /// </summary>
    private bool _applyRequested;

    /// <summary>
    /// Number of transfers the main task still needs to reach the top of its next spin, see <see cref="FakeFirmware.ApplyDelay"/>
    /// </summary>
    private int _applyCountdown;

    /// <summary>
    /// Transfer in which the oldest abort that is yet to be applied was recorded
    /// </summary>
    private int _recordTransfer;

    /// <summary>
    /// Longest time from the record of an abort to its apply in transfers (CE-20 keeps it in ms)
    /// </summary>
    public int MaxRecordToApply { get; private set; }

    /// <summary>
    /// Called by the SBC task when it has recorded a channel abort. The main task applies it at the top of its next spin (CE-7)
    /// </summary>
    /// <param name="delay">Number of transfers the main task needs to get there</param>
    /// <param name="transfer">Transfer of the record</param>
    public void RequestApply(int delay, int transfer)
    {
        if (!_applyRequested)
        {
            _applyRequested = true;
            _applyCountdown = delay;
            _recordTransfer = transfer;
        }
    }

    /// <summary>
    /// End the work of a channel: the routine of the apply of a channel abort, see SbcInterface::InvalidateChannel(GCodeBuffer&amp;) (SbcInterface.cpp:
    /// 1960-1984 at 4d687cec0), which the RRF of E1 also runs in InvalidateResources (design/sbc-link-v8-requirements.md CE-25). The caller has ended a
    /// macro wait before and has voided a closal that has not gone out and the invalidation (the take of the apply, :2051-2058). No "tell DSF" flag of
    /// the buffer is left set, so nothing of the old work is announced afterwards. A code that still runs at the base level survives and its state
    /// machine goes on with its next step: the routine does not clean up the state machine (owner decision D-A of 2026-11-10, RRF b1d3929a6; RRF's own
    /// abort does, <see cref="AbortPrint"/>), does not set the state of a frame back and does not reset the buffer
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    public void EndChannelWork(FakeGCodeBuffer gb)
    {
        // Unsent macro request (as RRF #43 and InvalidateResources, SbcInterface.cpp:893, :1766-1769)
        gb.MacroRequestSent();

        // A text code passed on to DSF, sent or not (SetFinished(true), as InvalidateResources does for a sent one, :1762-1765)
        if (gb.IsSendRequested || gb.IsExecutingOnSbc)
        {
            gb.SetFinished(true);
        }

        // PauseSequenceAborted comes before the files are aborted in RRF, but pausing is not emulated

        // File levels, without telling DSF, which has aborted them itself (:902-903, :1770-1771)
        gb.AbortFile(true, AbortFileClearsJustStartedMacro);
        gb.FileAbortSent();

        // A box left at the base level, closed as cancelled without the abort flag: with it, the main task would stop the print of the whole machine
        // (GCodes.cpp:593-616). Neither the prompt nor the acknowledgement is announced
        gb.MessageAcknowledged(true, false, 0);
        gb.MessagePromptSent();
        gb.MessageAcknowledgementSent();

        // Locks only of an idle buffer: UnlockAll under a running code would change it, e.g. clear toBeHomed of a G28 (FD-27, GCodes.cpp:5658-5663)
        if (gb.IsCompletelyIdle)
        {
            UnlockAll(gb);
        }
    }

    /// <summary>
    /// Reset everything after a firmware reset
    /// </summary>
    public void Reset()
    {
        foreach (FakeGCodeBuffer gb in Buffers)
        {
            gb.Reset();
        }
        Stopped = _emergencyStopRequested = _applyRequested = DoingToolChange = false;
        _applyCountdown = 0;
        _toBeHomed.Clear();
        AxesHomed.Clear();
        MovementLockOwner = null;
        MessageBoxes.Clear();
        _macroRequests.Clear();
        _sbcCodes.Clear();
        _abortAllFiles.Clear();
        TriggersPending = 0;
    }

    /// <summary>
    /// Run the main task until no channel can make progress, see GCodes::Spin
    /// </summary>
    public void Spin()
    {
        // A code waiting in RequestMacroFile continues as soon as the SBC task has resolved the request
        foreach (FakeGCodeBuffer gb in Buffers)
        {
            if (gb.PendingMacroCall is not null && !gb.IsWaitingForMacro)
            {
                ContinueMacroCall(gb);
            }
        }
        if (IsBlocked)
        {
            return;
        }

        // Carry out requests from other tasks first, see GCodes::Spin and GCodes::EmergencyStop
        if (_emergencyStopRequested)
        {
            _emergencyStopRequested = false;
            foreach (FakeGCodeBuffer gb in Buffers)
            {
                AbortPrint(gb);
            }
            Stopped = true;
        }

        // Apply the channel aborts that the SBC task recorded, after the stop and reset requests and before CheckTriggers, under the buffer mutex
        // (CE-7, GCodes.cpp:463-487). While a request is pending, SpinGCodeBuffer starts nothing (GCodes.cpp:581-584), so with a delay the main task
        // does no work at all until it gets here
        if (_applyRequested)
        {
            if (_applyCountdown > 0)
            {
                _applyCountdown--;
                return;
            }
            _applyRequested = false;
            MaxRecordToApply = Math.Max(MaxRecordToApply, firmware.TransferCount - _recordTransfer);
            foreach (FakeGCodeBuffer gb in Buffers)
            {
                // Take both records before the work, see SbcInterface::ApplyRecordedAborts (SbcInterface.cpp:2030-2089 at 4d687cec0): the abort and the
                // end of the connection that InvalidateResources records together are applied together. A closal that has not gone out and the
                // invalidation are voided in the take (the pause report is not emulated)
                bool aborted = gb.IsAbortRecorded, connectionLost = gb.IsConnectionEndRecorded;
                gb.IsAbortRecorded = gb.IsConnectionEndRecorded = false;
                if (aborted)
                {
                    gb.MacroFileClosedSent();
                    gb.IsInvalidated = false;
                    EndChannelWork(gb);

                    // Until E3 the fork catch code of RRF #46 stays: a code that still runs has replies that DSF does not expect. It is flagged here, on
                    // the level that AbortFile has left, unless its replies are tagged or the connection has been lost, whose codes reply to nothing anyway
                    if (!connectionLost && firmware.CodeReplySurplus && gb.MachineState.LastCodeFromSbc && !gb.IsCompletelyIdle)
                    {
                        gb.MarkCodeReplySurplus();
                    }
                }

                // After a lost connection the codes on the stack belong to the connection that has ended: their stamps are void, and none of their replies
                // goes to DSF, whatever connection is up when they reply (owner decision of 2026-11-10, RRF 78a997f74, SbcInterface.cpp:2085-2089). That
                // is checked where a reply is queued (FakeFirmware.PlatformMessage), not with the flag of RRF #46, which the loss leaves alone. It also
                // decides where an InvalidateChannel is applied with the loss, in either order
                if (connectionLost)
                {
                    gb.EndConnectionOfCodes();
                }
            }
        }

        // RRF's own abort of all files, e.g. AbortPrint after an error of the state machine
        foreach (CodeChannel channel in _abortAllFiles)
        {
            AbortPrint(this[channel]);
        }
        _abortAllFiles.Clear();

        // Start requested system macros when their channels are idle
        while (_macroRequests.TryPeek(out var request))
        {
            FakeGCodeBuffer gb = this[request.channel];
            if (gb.CurrentCode is not null || gb.PendingMacroCall is not null || gb.SbcCode is not null)
            {
                break;
            }
            _macroRequests.Dequeue();

            DoFileMacro(gb, request.fileName, request.reportMissing, AsyncSystemMacroCode, [], null);
            if (IsBlocked)
            {
                return;
            }
        }

        // Pass text codes on to DSF when their channels are idle. RRF reads a text code into the buffer of its channel, so that channel is busy
        // until DSF has sent the result (GCodeBufferState::executingOnSbc)
        int sbcCodes = _sbcCodes.Count;
        for (int i = 0; i < sbcCodes; i++)
        {
            (CodeChannel channel, string code) = _sbcCodes.Dequeue();
            FakeGCodeBuffer gb = this[channel];
            if (gb.CurrentCode is null && gb.PendingMacroCall is null && gb.SbcCode is null && !gb.IsDoingFile && !gb.MachineState.WaitingForAcknowledgement)
            {
                gb.SendToSbc(code);
            }
            else
            {
                _sbcCodes.Enqueue((channel, code));
            }
        }

        // Spin the channels until none of them can make progress. RRF checks the triggers on every spin before it spins a channel (GCodes.cpp:517)
        for (int i = 0; i < 1000; i++)
        {
            bool progress = CheckTriggers();
            if (IsBlocked)
            {
                return;
            }
            foreach (FakeGCodeBuffer gb in Buffers)
            {
                progress |= SpinGCodeBuffer(gb);
                if (IsBlocked)
                {
                    return;
                }
            }
            if (!progress)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Whether the trigger channel is busy, see GCodes::IsTriggerBusy (GCodes.cpp:423-433)
    /// </summary>
    private bool IsTriggerBusy
    {
        get
        {
            FakeGCodeBuffer gb = this[CodeChannel.Trigger];
            // A recorded abort counts like an unsent one, so a trigger stays pending until the apply (CE-7 (c))
            return gb.IsDoingFile || gb.CurrentCode is not null || gb.MachineState.WaitingForAcknowledgement || gb.IsAbortRequested ||
                gb.IsSendRequested || gb.IsExecutingOnSbc || gb.IsAbortRecorded;
        }
    }

    /// <summary>
    /// Start the macro of the lowest pending trigger once the trigger channel is free, see GCodes::CheckTriggers (GCodes.cpp:991-1056).
    /// Trigger 0 (emergency stop), trigger 1 (pause) and the one-line actions of M581.1 U are not emulated
    /// </summary>
    /// <returns>Whether a trigger macro has been started</returns>
    private bool CheckTriggers()
    {
        if (TriggersPending == 0)
        {
            return false;
        }

        int lowestTriggerPending = System.Numerics.BitOperations.TrailingZeroCount(TriggersPending);
        FakeGCodeBuffer gb = this[CodeChannel.Trigger];
        if (IsTriggerBusy || gb.MachineState.State != FakeMachineState.MachineStateKind.Normal || gb.PendingMacroCall is not null)
        {
            return false;
        }

        TriggersPending &= ~(1u << lowestTriggerPending);
        DoFileMacro(gb, $"trigger{lowestTriggerPending}.g", true, AsyncSystemMacroCode, [], null);
        return true;
    }

    /// <summary>
    /// Do some work on a channel, see GCodes::SpinGCodeBuffer
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <returns>Whether the state of the channel has changed</returns>
    private bool SpinGCodeBuffer(FakeGCodeBuffer gb)
    {
        if (gb.PendingMacroCall is not null)
        {
            return false;
        }

        if (gb.MachineState.State == FakeMachineState.MachineStateKind.Normal)
        {
            if (gb.MachineState.MessageAcknowledged)
            {
                bool shouldAbort = gb.MachineState.MessageShouldAbort;
                gb.PopState(true);
                if (shouldAbort)
                {
                    if (gb.MachineState.Previous is null)
                    {
                        // StopPrint is not emulated
                        gb.AbortFile(false, AbortFileClearsJustStartedMacro);
                    }
                    else
                    {
                        FileMacroCyclesReturn(gb);
                    }
                }
                return true;
            }
            return StartNextGCode(gb);
        }
        return RunStateMachine(gb);
    }

    /// <summary>
    /// Start a new code or continue the current one, see GCodes::StartNextGCode
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <returns>Whether the state of the channel has changed</returns>
    private bool StartNextGCode(FakeGCodeBuffer gb)
    {
        // Pausing, autopause events and daemon.g are not emulated
        if (gb.CurrentCode is not null)
        {
            return ExecuteCurrentCode(gb);
        }

        if (gb.IsDoingFile)
        {
            return DoFilePrint(gb);
        }

        if (gb.SbcCode is not null)
        {
            // A text code is with DSF or waits to be sent there, the buffer of the channel holds it until DSF has sent the result
            return false;
        }

        if (firmware.FillBuffer(gb))
        {
            ExecuteCurrentCode(gb);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Run the current code and update the buffer state, see the ActOnCode calls in GCodes::StartNextGCode
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <returns>Whether the state of the channel has changed</returns>
    private bool ExecuteCurrentCode(FakeGCodeBuffer gb)
    {
        bool firstCall = !gb.IsExecuting;
        switch (ActOnCode(gb))
        {
            case ActResult.Finished:
                gb.SetFinished(true);
                return true;

            case ActResult.Blocked:
                gb.SetFinished(false);
                return true;

            default:
                gb.SetFinished(false);
                return firstCall;
        }
    }

    /// <summary>
    /// Continue a file or macro, see GCodes::DoFilePrint
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <returns>Whether the state of the channel has changed</returns>
    private bool DoFilePrint(FakeGCodeBuffer gb)
    {
        if (gb.IsFileFinished)
        {
            if (gb.IsFileChannel && gb.MachineState.Previous is null)
            {
                // Finished printing a job file, StopPrint is not emulated
                return false;
            }

            if (!gb.IsMacroFileClosed)
            {
                // Finished a macro, pop the stack and notify the SBC that we have closed the file
                FakeCode? startCode = gb.CurrentFileMachineState.InvokingCode;
                gb.MacroFileClosed();
                Pop(gb, false);

                // Send a final code response
                if (gb.MachineState.State == FakeMachineState.MachineStateKind.Normal)
                {
                    UnlockAll(gb);
                    if (!gb.MachineState.LastCodeFromSbc || gb.MachineState.MacroStartedByCode)
                    {
                        HandleReply(gb, CodeResult.Ok, string.Empty, startCode);
                    }
                }
                return true;
            }
            return false;
        }

        // Execute the next buffered codes
        bool didWork = false;
        for (int codesDone = 0; codesDone < MaxSbcFileCodesPerSpin; codesDone++)
        {
            if (!firmware.FillBuffer(gb))
            {
                break;
            }
            didWork = true;

            ActResult result = ActOnCode(gb);
            gb.SetFinished(result == ActResult.Finished);
            if (result != ActResult.Finished || gb.MachineState.State != FakeMachineState.MachineStateKind.Normal)
            {
                break;
            }
        }
        return didWork;
    }

    /// <summary>
    /// Run the state machine, see GCodes::RunStateMachine. The states waitingForAcknowledgement, homing1, homing2 and the tool change states are
    /// emulated, motion is not: a step that waits for standstill only waits for the movement lock
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <returns>Whether the state of the channel has changed</returns>
    private bool RunStateMachine(FakeGCodeBuffer gb)
    {
        // Wait for the G-code replies and abort requests to go before anything else is done in the state machine
        if (gb.IsAbortRequested || gb.IsAbortAllRequested)
        {
            return false;
        }

        FakeMachineState ms = gb.MachineState;
        switch (ms.State)
        {
            case FakeMachineState.MachineStateKind.WaitingForAcknowledgement:
                // Finished M291 and the SBC expects a response next
                ms.State = FakeMachineState.MachineStateKind.Normal;
                EndStateMachineStep(gb, ms, gb.LastResult, string.Empty);
                return true;

            case FakeMachineState.MachineStateKind.Homing1:
                // GCodes4.cpp:358-392. Only one G28 runs at a time, so toBeHomed belongs to this one
                if (_toBeHomed.Count == 0)
                {
                    ms.State = FakeMachineState.MachineStateKind.Normal;
                    EndStateMachineStep(gb, ms, CodeResult.Ok, string.Empty);
                }
                else
                {
                    string homingFile = $"home{char.ToLowerInvariant(_toBeHomed[0])}.g";
                    ms.State = FakeMachineState.MachineStateKind.Homing2;
                    RunStateMacro(gb, ms, homingFile, SystemHelperMacroCode, found =>
                    {
                        if (found)
                        {
                            return (CodeResult.Ok, string.Empty);
                        }
                        _toBeHomed.Clear();
                        ms.State = FakeMachineState.MachineStateKind.Normal;
                        return (CodeResult.Error, $"Homing file {homingFile} not found");
                    });
                }
                return true;

            case FakeMachineState.MachineStateKind.Homing2:
                // GCodes4.cpp:394-413. If the homing file that the abort popped homed an axis, G28 goes on with the next one; else, and with an empty
                // toBeHomed after the emergency stop's AbortStateMachine, it fails here
                if (!LockAllMovementSystemsAndWaitForStandstill(gb))
                {
                    return false;
                }
                if (!_toBeHomed.Any(AxesHomed.Contains))
                {
                    string reply = "Failed to home axes " + string.Concat(_toBeHomed);
                    _toBeHomed.Clear();
                    ms.State = FakeMachineState.MachineStateKind.Normal;
                    EndStateMachineStep(gb, ms, CodeResult.Error, reply);
                }
                else
                {
                    _toBeHomed.RemoveAll(AxesHomed.Contains);
                    ms.State = (_toBeHomed.Count == 0) ? FakeMachineState.MachineStateKind.Normal : FakeMachineState.MachineStateKind.Homing1;
                    EndStateMachineStep(gb, ms, CodeResult.Ok, string.Empty);
                }
                return true;

            case FakeMachineState.MachineStateKind.ToolChange0:
                // tfree of the old tool (GCodes4.cpp:415-441). The T-code has the movement lock already
                DoingToolChange = true;
                ms.State = FakeMachineState.MachineStateKind.ToolChange1;
                RunStateMacro(gb, ms, ms.ToolChangeMacros[0], ToolChangeMacroCode, _ => (CodeResult.Ok, string.Empty));
                return true;

            case FakeMachineState.MachineStateKind.ToolChange1:
                // tpre of the new tool once tfree has ended (GCodes4.cpp:443-469)
                if (!LockAllMovementSystemsAndWaitForStandstill(gb))
                {
                    return false;
                }
                ms.State = FakeMachineState.MachineStateKind.ToolChange2;
                RunStateMacro(gb, ms, ms.ToolChangeMacros[1], ToolChangeMacroCode, _ => (CodeResult.Ok, string.Empty));
                return true;

            case FakeMachineState.MachineStateKind.ToolChange2:
                // Select the new tool and run its tpost once tpre has ended (GCodes4.cpp:471-522). Tools are not emulated
                if (!LockAllMovementSystemsAndWaitForStandstill(gb))
                {
                    return false;
                }
                ms.State = FakeMachineState.MachineStateKind.ToolChangeComplete;
                RunStateMacro(gb, ms, ms.ToolChangeMacros[2], ToolChangeMacroCode, _ => (CodeResult.Ok, string.Empty));
                return true;

            case FakeMachineState.MachineStateKind.ToolChangeComplete:
                // tpost has ended, the T-code ends (GCodes4.cpp:524-542)
                if (!LockAllMovementSystemsAndWaitForStandstill(gb))
                {
                    return false;
                }
                DoingToolChange = false;
                ms.State = FakeMachineState.MachineStateKind.Normal;
                EndStateMachineStep(gb, ms, CodeResult.Ok, string.Empty);
                return true;
        }
        return false;
    }

    /// <summary>
    /// Run the macro of a state machine step with DoFileMacro, then the rest of the step. While the request waits for DSF, the rest follows when DSF has
    /// answered, as RRF's main task goes on in RequestMacroFile then. A step without a macro (e.g. no old tool) only ends
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <param name="ms">Frame of the step</param>
    /// <param name="fileName">Macro file, or null if the step runs none</param>
    /// <param name="codeRunning">Code that runs the macro, see DoFileMacro</param>
    /// <param name="rest">Rest of the step, called with whether DoFileMacro found the file. It returns the result and the reply of the step</param>
    private void RunStateMacro(FakeGCodeBuffer gb, FakeMachineState ms, string? fileName, int codeRunning, Func<bool, (CodeResult Result, string Reply)> rest)
    {
        void finish(bool found)
        {
            (CodeResult result, string reply) = rest(found);
            EndStateMachineStep(gb, ms, result, reply);
        }

        if (fileName is null)
        {
            EndStateMachineStep(gb, ms, CodeResult.Ok, string.Empty);
            return;
        }
        bool? started = DoFileMacro(gb, fileName, false, codeRunning, [], null, finish);
        if (started is not null)
        {
            finish(started.Value);
        }
    }

    /// <summary>
    /// End of a step of the state machine (GCodes4.cpp:1998-2010): if the latest frame is in the normal state, the locks are released and the reply is
    /// sent. After a macro has been started that is the frame of the macro, whose reply is an empty push message and is not sent
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <param name="ms">Frame of the step</param>
    /// <param name="result">Result of the step</param>
    /// <param name="reply">Reply of the step</param>
    private void EndStateMachineStep(FakeGCodeBuffer gb, FakeMachineState ms, CodeResult result, string reply)
    {
        if (gb.MachineState.State == FakeMachineState.MachineStateKind.Normal)
        {
            FakeCode? stateCode = ms.StateCode;
            if (ms.State == FakeMachineState.MachineStateKind.Normal)
            {
                ms.StateCode = null;
            }
            UnlockAll(gb);
            HandleReply(gb, result, reply, stateCode);
        }
    }

    #region Code execution
    /// <summary>
    /// Result of <see cref="ActOnCode"/>
    /// </summary>
    private enum ActResult
    {
        /// <summary>
        /// The code has finished
        /// </summary>
        Finished,

        /// <summary>
        /// The code has to be called again
        /// </summary>
        NotFinished,

        /// <summary>
        /// The code waits for DSF to answer a macro request
        /// </summary>
        Blocked
    }

    /// <summary>
    /// Execute the current code, see GCodes::ActOnCode
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <returns>Result</returns>
    private ActResult ActOnCode(FakeGCodeBuffer gb)
    {
        FakeCode code = gb.CurrentCode!;
        if (!gb.IsExecuting)
        {
            firmware.LogExecutedCode(code);
        }

        try
        {
            switch (code.Type)
            {
                case CodeType.Comment:
                    // Q0 is a whole-line comment and returns without a reply, see GCodes::HandleQcode
                    return ActResult.Finished;

                case CodeType.GCode when code.MajorNumber is not null:
                    if (Stopped)
                    {
                        return HandleResult(gb, CodeResult.Stopped, string.Empty);
                    }
                    if (code.MajorNumber is not 1 and not 90 and not 91 && gb.MachineState.WaitingForAcknowledgement)
                    {
                        return HandleResult(gb, CodeResult.WaitingForAckSoIgnored, string.Empty);
                    }
                    return HandleCode(gb, code);

                case CodeType.MCode when code.MajorNumber is not null:
                    {
                        int number = code.MajorNumber.Value;
                        bool isStatusRequest = number is 105 or 109 or 114 or 115 or 122 or 409;
                        if (Stopped && !isStatusRequest && number is not 112 and not 999)
                        {
                            return HandleResult(gb, CodeResult.Stopped, string.Empty);
                        }
                        if (gb.MachineState.WaitingForAcknowledgement && !isStatusRequest && number is not 120 and not 121 and not 292)
                        {
                            return HandleResult(gb, CodeResult.WaitingForAckSoIgnored, string.Empty);
                        }
                        return HandleCode(gb, code);
                    }

                case CodeType.TCode:
                    if (Stopped)
                    {
                        return HandleResult(gb, CodeResult.Stopped, string.Empty);
                    }
                    if (gb.MachineState.WaitingForAcknowledgement)
                    {
                        return HandleResult(gb, CodeResult.WaitingForAckSoIgnored, string.Empty);
                    }
                    return HandleCode(gb, code);
            }
        }
        catch (FakeExpressionException e)
        {
            UnlockAll(gb);
            HandleReply(gb, CodeResult.Error, FormatException(gb, code, e.Message), code);
            return ActResult.Finished;
        }
        catch (Exception e)
        {
            // A failing test handler or a parameter of an unexpected type must not stop the LinkService thread
            firmware.LogUnemulated($"Exception while executing {code}: {e}");
            UnlockAll(gb);
            HandleReply(gb, CodeResult.Error, $"FakeFirmware: {e.Message}", code);
            return ActResult.Finished;
        }

        // If we get here then we didn't see a command that was worth parsing
        HandleReply(gb, CodeResult.Error, "Bad command: " + code.Text, code);
        return ActResult.Finished;
    }

    /// <summary>
    /// Run a test handler or the emulated RRF handler of a code
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <param name="code">Code to run</param>
    /// <returns>Result</returns>
    private ActResult HandleCode(FakeGCodeBuffer gb, FakeCode code)
    {
        FakeReply? reply = firmware.RunCodeHandler(code);
        if (reply is { IsStateMachine: true })
        {
            return StartStateMachine(gb, code, reply);
        }
        if (reply is { IsMacroCall: true })
        {
            bool? started = DoFileMacro(gb, reply.Text, false, code.MajorNumber ?? 0, [], code);
            if (started is null)
            {
                return ActResult.Blocked;
            }
            return started.Value ? HandleResult(gb, CodeResult.Ok, string.Empty) : HandleResult(gb, CodeResult.Error, $"Macro file {reply.Text} not found");
        }
        if (reply is not null)
        {
            return HandleResult(gb, reply.Result, reply.Text);
        }

        if (code.Type == CodeType.GCode && code.MajorNumber is 0 or 1 && code.HasParameter('H') && (int)code.GetParameter('H')! == 1)
        {
            // A homing move homes the axes it names, see the waitingForSpecialMoveToComplete state (GCodes4.cpp:62-100). There are no endstops, they
            // count as triggered at once
            foreach (CodeParameter parameter in code.Parameters)
            {
                if (parameter.Letter is >= 'A' and <= 'Z' and not 'E' and not 'F' and not 'H')
                {
                    AxesHomed.Add(parameter.Letter);
                }
            }
            return HandleResult(gb, CodeResult.Ok, string.Empty);
        }

        if (code.Type == CodeType.MCode)
        {
            switch (code.MajorNumber)
            {
                // Call Macro/Subprogram, see the M98 case in GCodes::HandleMcode
                case 98:
                    if (code.HasParameter('P'))
                    {
                        string fileName = GetString(gb, code, 'P');
                        Dictionary<string, object?> parameters = AddParameters(gb, code, 98);
                        bool? started = DoFileMacro(gb, fileName, false, 98, parameters, code);
                        if (started is null)
                        {
                            return ActResult.Blocked;
                        }
                        return started.Value ? HandleResult(gb, CodeResult.Ok, string.Empty) : HandleResult(gb, CodeResult.Error, $"Macro file {fileName} not found");
                    }
                    if (code.HasParameter('R'))
                    {
                        // Restartable macros are not emulated
                        return HandleResult(gb, CodeResult.Ok, string.Empty);
                    }
                    return HandleResult(gb, CodeResult.Error, "No P or R parameter");

                // Return from Macro/Subprogram, see GCodes::FileMacroCyclesReturn
                case 99:
                    FileMacroCyclesReturn(gb);
                    return HandleResult(gb, CodeResult.Ok, string.Empty);

                // Display message, optionally wait for acknowledgement
                case 291:
                    return DoMessageBox(gb, code);

                // Acknowledge message
                case 292:
                    return AcknowledgeMessage(gb, code);

                // Get object model values in JSON format
                case 409:
                    return GetObjectModel(gb, code);

                // Check external trigger, see GCodes::CheckTrigger (GCodes3.cpp:422-431). The trigger slots of RRF are not emulated, so the level check
                // finds nothing and only S1 makes a trigger pending. Like SetTriggerPending, triggers 0 (emergency stop) and 1 (pause) are not emulated
                case 582:
                    {
                        int number = (int)code.GetParameter('T')!;
                        if (number < 0 || number >= MaxTriggers)
                        {
                            return HandleResult(gb, CodeResult.Error, "parameter 'T' too high");
                        }
                        if (number >= 2 && code.HasParameter('S') && (int)code.GetParameter('S')! == 1)
                        {
                            TriggersPending |= 1u << number;
                        }
                        return HandleResult(gb, CodeResult.Ok, string.Empty);
                    }

                // I2C or Modbus send and receive, see Platform::SendI2cOrModbus and Platform::ReceiveI2cOrModbus
                case 260:
                case 261:
                    CreateResultVariable(gb, code);
                    return HandleResult(gb, CodeResult.Ok, string.Empty);
            }
        }

        // Everything else succeeds without a reply
        return HandleResult(gb, CodeResult.Ok, string.Empty);
    }

    /// <summary>
    /// Start a code that goes on in the state machine of its frame, see <see cref="FakeReply.Home"/> and <see cref="FakeReply.ToolChange"/>. Both take
    /// the movement lock first and wait for it like the codes of RRF (GCodes2.cpp:390-393, :4930-4933)
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <param name="code">Code</param>
    /// <param name="reply">Reply of the test handler</param>
    /// <returns>Result</returns>
    private ActResult StartStateMachine(FakeGCodeBuffer gb, FakeCode code, FakeReply reply)
    {
        if (!LockAllMovementSystemsAndWaitForStandstill(gb))
        {
            return ActResult.NotFinished;
        }

        FakeMachineState ms = gb.MachineState;
        if (reply.HomedAxes.Count > 0)
        {
            // DoHome (GCodes.cpp:3857-3884)
            if (_toBeHomed.Count > 0)
            {
                return HandleResult(gb, CodeResult.Error, "G28 may not be used within a homing file");
            }
            _toBeHomed.AddRange(reply.HomedAxes);
            AxesHomed.ExceptWith(reply.HomedAxes);
            ms.State = FakeMachineState.MachineStateKind.Homing1;
        }
        else
        {
            // StartToolChange (GCodes.cpp:5542-5546). The T-code returns without a reply and without unlocking (GCodes2.cpp:4961-4964)
            ms.ToolChangeMacros = [.. reply.ToolChangeMacros];
            ms.State = FakeMachineState.MachineStateKind.ToolChange0;
        }
        ms.StateCode = code;
        return HandleResult(gb, CodeResult.Ok, string.Empty);
    }

    /// <summary>
    /// Create the variable named by the V parameter of M260 or M261, see GetResultVariable in Platform.cpp.
    /// The I2C or Modbus transfer is not emulated, so the variable keeps the null value it is created with
    /// </summary>
    private void CreateResultVariable(FakeGCodeBuffer gb, FakeCode code)
    {
        if (code.HasParameter('V'))
        {
            string varName = GetString(gb, code, 'V');
            Dictionary<string, object?> variables = gb.GetVariables();
            if (variables.ContainsKey(varName))
            {
                throw new FakeExpressionException($"variable '{varName}' already exists");
            }
            variables.Add(varName, null);
        }
    }

    /// <summary>
    /// Run M291, see GCodes::DoMessageBox in GCodes7.cpp and RepRap::SendAlert. Only the message, title, mode, timeout and J parameters
    /// are evaluated. Timeouts, the object model entry state.messageBox and the extra output to USB, Telnet and aux channels are not emulated
    /// </summary>
    private ActResult DoMessageBox(FakeGCodeBuffer gb, FakeCode code)
    {
        if (!code.HasParameter('P'))
        {
            throw new FakeExpressionException("missing parameter 'P'");
        }
        string message = GetString(gb, code, 'P');
        string title = code.HasParameter('R') ? GetString(gb, code, 'R') : string.Empty;
        int mode = code.HasParameter('S') ? (int)code.GetParameter('S')! : 1;
        float timeout = code.HasParameter('T') ? (float)code.GetParameter('T')! : ((mode <= 1) ? 10F : 0F);     // DefaultMessageTimeout is 10s
        bool shouldAbort = !code.HasParameter('J') || (int)code.GetParameter('J')! < 2;

        if (mode == 0 && timeout <= 0F)
        {
            return HandleResult(gb, CodeResult.Error, "Attempt to create a message box that cannot be dismissed");
        }

        MessageBox messageBox = new(++_lastMessageBoxSeq, mode, shouldAbort, message, title);
        MessageBoxes.Add(messageBox);
        firmware.PlatformMessage(MessageTypeFlags.LogInfo, $"M291: - {((title.Length > 0) ? title : "[no title]")} - {message}", null);
        if (messageBox.IsBlocking)
        {
            gb.MachineState.State = FakeMachineState.MachineStateKind.WaitingForAcknowledgement;
            gb.MachineState.StateCode = code;
            if (Push(gb, true))
            {
                gb.WaitForAcknowledgement(messageBox.Seq);
            }
        }
        return HandleResult(gb, CodeResult.Ok, string.Empty);
    }

    /// <summary>
    /// Run M292, see GCodes::AcknowledgeMessage and GCodes::MessageBoxClosed in GCodes7.cpp
    /// </summary>
    private ActResult AcknowledgeMessage(FakeGCodeBuffer gb, FakeCode code)
    {
        uint seq = code.HasParameter('S') ? (uint)code.GetParameter('S')! : 0;
        bool cancelled = code.HasParameter('P') && (int)code.GetParameter('P')! == 1;

        // MessageBox::Acknowledge leaves shouldAbort unset if no message box is open, the fake uses the default of MessageBoxLimits
        bool shouldAbort = true;
        MessageBox? messageBox = MessageBoxes.FirstOrDefault(item => seq == 0 || item.Seq == seq);
        if (messageBox is not null)
        {
            MessageBoxes.Remove(messageBox);
            shouldAbort = messageBox.ShouldAbort;
            if (messageBox.IsBlocking)
            {
                firmware.PlatformMessage(MessageTypeFlags.LogInfo, $"M292: cancelled={(cancelled ? "true" : "false")} shouldAbort={(shouldAbort ? "true" : "false")}", null);
                foreach (FakeGCodeBuffer target in Buffers)
                {
                    target.MessageAcknowledged(cancelled, shouldAbort, seq);
                }
            }
        }
        return HandleResult(gb, (cancelled && !shouldAbort) ? CodeResult.M291Cancelled : CodeResult.Ok, string.Empty);
    }

    /// <summary>
    /// Run M409, see the M409 case in GCodes::HandleMcode and RepRap::GetModelResponse
    /// </summary>
    private ActResult GetObjectModel(FakeGCodeBuffer gb, FakeCode code)
    {
        string key = code.HasParameter('K') ? GetString(gb, code, 'K') : string.Empty;
        if (code.HasParameter('I') && (int)code.GetParameter('I')! > 0)
        {
            // Allow DSF to increment sequence numbers of its provided OM keys via K"key" I1
            if (key is "network" or "volumes")
            {
                firmware.ObjectModel.IncrementSeq(key);
            }
            return HandleResult(gb, CodeResult.Ok, string.Empty);
        }

        string flags = code.HasParameter('F') ? GetString(gb, code, 'F') : string.Empty;
        string json = firmware.ObjectModel.GetModelResponse(key, flags);
        if (code.Flags.HasFlag(CodeFlags.HasExplicitLineNumber) && code.LineNumber is not null)
        {
            // See RepRap::StartJsonResponse
            json = $"{{\"line\":{code.LineNumber.Value.ToString(CultureInfo.InvariantCulture)}," + json[1..];
        }

        // The reply comes in an output buffer, see GCodes::HandleResult and GCodes::HandleReply(gb, OutputBuffer)
        UnlockAll(gb);
        gb.LastResult = CodeResult.Ok;
        if (gb.MachineState.LastCodeFromSbc)
        {
            if (gb.IsCodeReplySurplus)
            {
                // DSF has dropped this code when it invalidated the channel, so this final reply is surplus (RRF Meltingplot/RepRapFirmware#46)
                gb.IsCodeReplySurplus = false;
            }
            else
            {
                firmware.PlatformMessage(gb.ResponseMessageType, json, code, true);
            }
        }
        else
        {
            firmware.LogUnemulated($"Reply to {code} from a non-SBC code");
        }
        return ActResult.Finished;
    }
    #endregion

    #region Macro files
    /// <summary>
    /// Run a macro file, see GCodes::DoFileMacro
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <param name="fileName">Macro file</param>
    /// <param name="reportMissing">Whether a missing file is reported</param>
    /// <param name="codeRunning">Code that runs the macro</param>
    /// <param name="parameters">Macro parameters</param>
    /// <param name="code">Code that calls the macro or null for system macros</param>
    /// <param name="continuation">Rest of the state machine step that calls the macro, if the request waits for DSF</param>
    /// <returns>True if the file was found or it wasn't and that was reported, false if it was not found, null if the request is pending</returns>
    private bool? DoFileMacro(FakeGCodeBuffer gb, string fileName, bool reportMissing, int codeRunning, Dictionary<string, object?> parameters, FakeCode? code,
        Action<bool>? continuation = null)
    {
        MacroCall call = new()
        {
            FileName = fileName,
            ReportMissing = reportMissing,
            CodeRunning = codeRunning,
            Parameters = parameters,
            Code = code,
            Continuation = continuation
        };

        // RequestMacroFile fails at once while no SBC is connected (GCodeBuffer.cpp:1298-1302)
        if (!firmware.IsConnectedForMainTask)
        {
            return FinishDoFileMacro(gb, call, false);
        }
        if (gb.RequestMacroFile(fileName, gb.MachineState.LastCodeFromSbc && codeRunning != AsyncSystemMacroCode))
        {
            // The main task waits in RequestMacroFile until DSF has answered
            gb.PendingMacroCall = call;
            return null;
        }
        return FinishDoFileMacro(gb, call, true);
    }

    /// <summary>
    /// Continue a macro call after DSF has answered the macro request. The SBC task calls this as well when it needs the
    /// mutex of a buffer whose code waited for the macro, because the main task finishes that code before it releases the mutex
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    public void ContinueMacroCall(FakeGCodeBuffer gb)
    {
        MacroCall call = gb.PendingMacroCall!;
        gb.PendingMacroCall = null;

        bool started = FinishDoFileMacro(gb, call, gb.FinishMacroRequest());
        if (call.Code is not null)
        {
            // Rest of the M98 case in GCodes::HandleMcode
            ActResult result = started ? HandleResult(gb, CodeResult.Ok, string.Empty) : HandleResult(gb, CodeResult.Error, $"Macro file {call.FileName} not found");
            gb.SetFinished(result == ActResult.Finished);
        }
        call.Continuation?.Invoke(started);
    }

    /// <summary>
    /// Rest of GCodes::DoFileMacro after GCodeBuffer::RequestMacroFile has returned
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <param name="call">Macro call</param>
    /// <param name="success">Whether DSF could start the macro</param>
    /// <returns>True if the file was found or it wasn't and that was reported, false if it was not found</returns>
    private bool FinishDoFileMacro(FakeGCodeBuffer gb, MacroCall call, bool success)
    {
        if (!success)
        {
            if (call.ReportMissing)
            {
                // A part of the reply of a code that DSF has dropped is surplus (RRF Meltingplot/RepRapFirmware#46)
                bool isCodeReply = gb.MachineState.LastCodeFromSbc && call.CodeRunning != SystemHelperMacroCode;
                if (!isCodeReply || !gb.IsCodeReplySurplus)
                {
                    MessageTypeFlags mt = isCodeReply
                        ? gb.ResponseMessageType | MessageTypeFlags.WarningMessageFlag | MessageTypeFlags.PushFlag
                        : MessageTypeFlags.GenericMessage | MessageTypeFlags.LogWarn | MessageTypeFlags.WarningMessageFlag;
                    firmware.PlatformMessage(mt, $"Macro file {call.FileName} not found\n", call.Code);
                }
                return true;
            }
            return false;
        }

        if (!Push(gb, false))
        {
            gb.AbortFile(false, AbortFileClearsJustStartedMacro);
            return true;
        }

        FakeMachineState frame = gb.VariablesFrame;
        foreach (KeyValuePair<string, object?> parameter in call.Parameters)
        {
            frame.Parameters[parameter.Key] = parameter.Value;
        }
        gb.StartNewFile(call.FileName);
        if (gb.IsMacroEmpty)
        {
            gb.SetFileFinished();
        }
        gb.MachineState.DoingFileMacro = true;
        gb.MachineState.State = FakeMachineState.MachineStateKind.Normal;
        return true;
    }

    /// <summary>
    /// Return from a macro file (M99), see GCodes::FileMacroCyclesReturn
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    private void FileMacroCyclesReturn(FakeGCodeBuffer gb)
    {
        if (gb.IsDoingFileMacro())
        {
            gb.AbortFile(false, AbortFileClearsJustStartedMacro);
        }
    }

    /// <summary>
    /// Push a frame, see GCodes::Push
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <param name="withinSameFile">Whether this is a local push</param>
    /// <returns>Whether the frame could be pushed</returns>
    private bool Push(FakeGCodeBuffer gb, bool withinSameFile)
    {
        if (!gb.PushState(withinSameFile))
        {
            firmware.PlatformMessage(MessageTypeFlags.GenericMessage | MessageTypeFlags.LogWarn | MessageTypeFlags.ErrorMessageFlag, $"Push(): stack overflow on {gb.Channel}\n", null);
            gb.AbortFile(true, AbortFileClearsJustStartedMacro);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Pop a frame, see GCodes::Pop
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <param name="withinSameFile">Whether only a local frame is popped</param>
    private void Pop(FakeGCodeBuffer gb, bool withinSameFile)
    {
        if (!gb.PopState(withinSameFile))
        {
            firmware.PlatformMessage(MessageTypeFlags.GenericMessage | MessageTypeFlags.LogWarn | MessageTypeFlags.ErrorMessageFlag, $"Pop(): stack underflow on {gb.Channel}\n", null);
        }
    }
    #endregion

    #region Movement lock
    /// <summary>
    /// Lock all movement systems, see GCodes::LockAllMovementSystemsAndWaitForStandstill. There are no moves to wait for
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <returns>Whether the lock could be acquired</returns>
    public bool LockAllMovementSystemsAndWaitForStandstill(FakeGCodeBuffer gb)
    {
        if (MovementLockOwner == gb)
        {
            return true;
        }
        if (MovementLockOwner is null)
        {
            MovementLockOwner = gb;
            gb.MachineState.LockedMovement = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Release the locks of a channel that are not held by the frame below, see GCodes::UnlockAll
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    public void UnlockAll(FakeGCodeBuffer gb)
    {
        bool keep = gb.MachineState.Previous?.LockedMovement ?? false;
        if (MovementLockOwner == gb && !keep)
        {
            MovementLockOwner = null;
            gb.MachineState.LockedMovement = false;
        }
    }
    #endregion

    #region Replies
    /// <summary>
    /// Deal with the result of a code, see GCodes::HandleResult
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <param name="result">Code result</param>
    /// <param name="reply">Reply text</param>
    /// <returns>Whether the code has finished</returns>
    private ActResult HandleResult(FakeGCodeBuffer gb, CodeResult result, string reply)
    {
        FakeCode? code = gb.CurrentCode;
        string command = code?.PrintCommand ?? string.Empty;

        // The other results that are converted to errors or warnings below print the command themselves
        bool addCommandPrefix = result is CodeResult.Error or CodeResult.Warning;
        switch (result)
        {
            case CodeResult.NotFinished:
                return ActResult.NotFinished;

            case CodeResult.WarningNotSupported:
                reply = command + ": Command is not supported";
                result = CodeResult.Warning;
                break;

            case CodeResult.ErrorNotSupported:
                reply = command + ": Command is not supported";
                result = CodeResult.Error;
                break;

            case CodeResult.BadOrMissingParameter:
                reply = command + ": Bad or missing parameter";
                result = CodeResult.Error;
                break;

            case CodeResult.Stopped:
                reply = command + ": Machine is halted";
                result = CodeResult.Error;
                break;

            case CodeResult.WaitingForAckSoIgnored:
                reply = command + ": Awaiting input, command ignored";
                result = CodeResult.Warning;
                break;
        }

        // Binary codes are never local files, so the command is always printed
        if (addCommandPrefix)
        {
            reply = command + ": " + reply;
        }

        if (gb.MachineState.State == FakeMachineState.MachineStateKind.Normal)
        {
            UnlockAll(gb);
            HandleReply(gb, result, reply, code);
        }
        return ActResult.Finished;
    }

    /// <summary>
    /// Send a reply and update the last result, see GCodes::HandleReply
    /// </summary>
    private void HandleReply(FakeGCodeBuffer gb, CodeResult result, string reply, FakeCode? relatedCode)
    {
        gb.LastResult = result;
        HandleReplyPreserveResult(gb, result, reply, relatedCode);
    }

    /// <summary>
    /// Send a reply, see GCodes::HandleReplyPreserveResult
    /// </summary>
    private void HandleReplyPreserveResult(FakeGCodeBuffer gb, CodeResult result, string reply, FakeCode? relatedCode)
    {
        if (gb.MachineState.LastCodeFromSbc)
        {
            bool isPushMessage = result == CodeResult.NotFinished || gb.HasJustStartedMacro || (gb.MachineState.WaitingForAcknowledgement && gb.IsMessagePromptPending);
            if (gb.IsCodeReplySurplus)
            {
                // DSF has dropped this code when it invalidated the channel, so this reply is surplus. Otherwise it would complete the next code
                // (RRF Meltingplot/RepRapFirmware#46)
                if (!isPushMessage)
                {
                    gb.IsCodeReplySurplus = false;
                }
                return;
            }

            if (reply.Length == 0 && gb.IsAbortRequested && !gb.IsAbortAllRequested)
            {
                // The SBC resolves the code that requested a single file abort itself, so this reply is surplus (RRF 4d80f4663)
                return;
            }

            MessageTypeFlags type = gb.ResponseMessageType;
            if (isPushMessage)
            {
                if (reply.Length == 0)
                {
                    // Don't send empty push messages
                    return;
                }
                type |= MessageTypeFlags.PushFlag;
            }

            if (result == CodeResult.Warning)
            {
                type = AddLogWarn(type | MessageTypeFlags.WarningMessageFlag);
            }
            else if (result == CodeResult.Error)
            {
                type = AddLogWarn(type | MessageTypeFlags.ErrorMessageFlag);
            }
            firmware.PlatformMessage(type, reply, relatedCode);
            return;
        }

        // Don't report empty responses if a file or macro is being processed or if the code was queued
        if (reply.Length == 0 &&
            (gb.Channel is CodeChannel.File or CodeChannel.Queue or CodeChannel.Trigger or CodeChannel.Autopause or CodeChannel.Daemon or CodeChannel.File2 or CodeChannel.Queue2 ||
             gb.IsDoingFileMacro(true)))
        {
            return;
        }

        // Replies to codes that did not come from DSF depend on the compatibility mode of the channel and are not emulated
        firmware.LogUnemulated($"{result} reply '{reply}' on {gb.Channel} for a non-SBC code");
    }

    /// <summary>
    /// Replace the log level of a message type with LogWarn, see AddLogWarn in MessageType.h
    /// </summary>
    private static MessageTypeFlags AddLogWarn(MessageTypeFlags type) => (type & ~MessageTypeFlags.LogOff) | MessageTypeFlags.LogWarn;

    /// <summary>
    /// Format an error message, see GCodeException::GetMessage
    /// </summary>
    private static string FormatException(FakeGCodeBuffer gb, FakeCode code, string message)
    {
        string prefix = string.Empty;
        if ((gb.IsDoingFileMacro() || gb.IsDoingFile) && gb.MachineState.FileName is not null)
        {
            prefix = $"in file {gb.MachineState.FileName} line {code.LineNumber ?? 0}: ";
        }
        if (code.Type is CodeType.GCode or CodeType.MCode or CodeType.TCode && code.MajorNumber is not null)
        {
            prefix += code.PrintCommand + ": ";
        }
        return prefix + message;
    }
    #endregion

    #region Expressions and parameters
    /// <summary>
    /// Evaluate an expression in the context of a channel, see ExpressionParser
    /// </summary>
    /// <param name="gb">G-code buffer</param>
    /// <param name="expression">Expression</param>
    /// <returns>Result</returns>
    /// <exception cref="FakeExpressionException">Failed to evaluate the expression</exception>
    public object? Evaluate(FakeGCodeBuffer gb, string expression)
    {
        FakeExpressionEvaluator evaluator = new(id => Resolve(gb, id), id => Exists(gb, id));
        return evaluator.Evaluate(expression);
    }

    /// <summary>
    /// Resolve an identifier, see the end of ExpressionParser::ParseIdentifierExpression
    /// </summary>
    private object? Resolve(FakeGCodeBuffer gb, string id)
    {
        if (id.StartsWith("param."))
        {
            string name = id["param.".Length..];
            return gb.VariablesFrame.Parameters.TryGetValue(name, out object? value) ? value : throw new FakeExpressionException($"unknown parameter '{name}'");
        }
        if (id.StartsWith("global."))
        {
            string name = id["global.".Length..];
            return firmware.ObjectModel.TryGetGlobal(name, out object? value) ? value : throw new FakeExpressionException($"unknown variable '{name}'");
        }
        if (id.StartsWith("var."))
        {
            string name = id["var.".Length..];
            return gb.GetVariables().TryGetValue(name, out object? value) ? value : throw new FakeExpressionException($"unknown variable '{name}'");
        }

        // The error text of RRF for unknown object model values has not been checked, see RepRap::GetObjectValueUsingTableNumber
        return firmware.ObjectModel.TryGetValue(id, out object? omValue) ? omValue : throw new FakeExpressionException($"unknown value '{id}'");
    }

    /// <summary>
    /// Check if an identifier exists, see the handling of exists() in ExpressionParser
    /// </summary>
    private bool Exists(FakeGCodeBuffer gb, string id)
    {
        if (id is "param" or "var" or "global")
        {
            return true;
        }

        try
        {
            Resolve(gb, id);
            return true;
        }
        catch (FakeExpressionException)
        {
            return false;
        }
    }

    /// <summary>
    /// Get a string parameter, evaluating an expression if needed
    /// </summary>
    private string GetString(FakeGCodeBuffer gb, FakeCode code, char letter)
    {
        CodeParameter parameter = code.GetParameter(letter)!;
        if (parameter.IsExpression)
        {
            object? value = Evaluate(gb, (string)parameter!);
            return value?.ToString() ?? string.Empty;
        }
        return (string)parameter!;
    }

    /// <summary>
    /// Convert the parameters of a macro call to macro parameters, see BinaryParser::AddParameters.
    /// Arrays are not emulated
    /// </summary>
    private Dictionary<string, object?> AddParameters(FakeGCodeBuffer gb, FakeCode code, int codeRunning)
    {
        Dictionary<string, object?> result = [];
        foreach (CodeParameter parameter in code.Parameters)
        {
            if (parameter.Letter != 'P' || codeRunning != 98)
            {
                object? value = null;
                if (parameter.IsExpression)
                {
                    value = Evaluate(gb, (string)parameter!);
                }
                else if (parameter.Type == typeof(string))
                {
                    value = (string)parameter!;
                }
                else if (parameter.Type == typeof(float))
                {
                    value = (float)parameter;
                }
                else if (parameter.Type == typeof(int))
                {
                    value = (int)parameter;
                }
                else if (parameter.Type == typeof(uint))
                {
                    value = (int)(uint)parameter;
                }

                if (value is not null)
                {
                    result[parameter.Letter.ToString()] = value;
                }
            }
        }
        return result;
    }
    #endregion
}
