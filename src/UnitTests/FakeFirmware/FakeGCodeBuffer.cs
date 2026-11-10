using System.Collections.Generic;
using DuetAPI;
using DuetControlServer.Link.Protocol.Shared;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Emulated G-code buffer of one channel, mirroring the SBC-related parts of RRF's GCodeBuffer (src/GCodes/GCodeBuffer/GCodeBuffer.cpp/.h)
/// </summary>
/// <param name="channel">Code channel</param>
internal sealed class FakeGCodeBuffer(CodeChannel channel)
{
    /// <summary>
    /// Maximum depth of the stack, see MaxStackDepth in RRF's Configuration.h
    /// </summary>
    private const int MaxStackDepth = 10;

    /// <summary>
    /// Code channel of this buffer
    /// </summary>
    public CodeChannel Channel { get; } = channel;

    /// <summary>
    /// Current (latest) machine state
    /// </summary>
    public FakeMachineState MachineState { get; private set; } = new();

    #region Code being executed
    /// <summary>
    /// Code being executed or null if the buffer is idle
    /// </summary>
    public FakeCode? CurrentCode { get; private set; }

    /// <summary>
    /// Whether the current code is still being executed (GCodeBufferState::executing)
    /// </summary>
    public bool IsExecuting { get; private set; }

    /// <summary>
    /// Macro call of the current code that waits for DSF, see <see cref="FakeGCodes"/>
    /// </summary>
    public FakeGCodes.MacroCall? PendingMacroCall { get; set; }

    /// <summary>
    /// Result of the last code, see GCodeBuffer::SetLastResult
    /// </summary>
    public CodeResult LastResult { get; set; } = CodeResult.Ok;
    #endregion

    #region SBC flags (see the HAS_SBC_INTERFACE members of GCodeBuffer)
    /// <summary>
    /// Whether the main task waits in RequestMacroFile for DSF to answer a macro request
    /// </summary>
    public bool IsWaitingForMacro { get; private set; }

    /// <summary>
    /// Macro file that is yet to be requested from DSF
    /// </summary>
    public string? RequestedMacroFile { get; private set; }

    /// <summary>
    /// Whether a macro has just been started and no code of it has been received yet
    /// </summary>
    public bool HasJustStartedMacro { get; private set; }

    /// <summary>
    /// Whether DSF could not start the requested macro
    /// </summary>
    public bool MacroFileError { get; private set; }

    /// <summary>
    /// Whether DSF reported the requested macro as empty
    /// </summary>
    public bool IsMacroEmpty { get; private set; }

    /// <summary>
    /// Whether RRF closed the macro and DSF has yet to be told (MacroFileClosed)
    /// </summary>
    public bool IsMacroFileClosed { get; private set; }

    /// <summary>
    /// Whether DSF has yet to be told about aborted files (AbortFile)
    /// </summary>
    public bool IsAbortRequested { get; private set; }

    /// <summary>
    /// Whether all files have been aborted
    /// </summary>
    public bool IsAbortAllRequested { get; private set; }

    /// <summary>
    /// Whether DSF has yet to be told about a blocking message box (WaitForAcknowledgement)
    /// </summary>
    public bool IsMessagePromptPending { get; private set; }

    /// <summary>
    /// Whether DSF has yet to be told about an acknowledged message box (MessageAcknowledged)
    /// </summary>
    public bool IsMessageAcknowledged { get; private set; }

    /// <summary>
    /// Whether the buffered codes of this channel are discarded at the next transfer (accessed only by the SBC task)
    /// </summary>
    public bool IsInvalidated { get; set; }

    /// <summary>
    /// Whether InvalidateChannel could not get the mutex of this buffer, so the next one is a resent copy (accessed only by the SBC task,
    /// RRF Meltingplot/RepRapFirmware#46)
    /// </summary>
    public bool IsInvalidationPending { get; set; }

    /// <summary>
    /// Whether DSF has dropped the code being executed when it invalidated the channel, so its replies are surplus (RRF Meltingplot/RepRapFirmware#46)
    /// </summary>
    public bool IsCodeReplySurplus { get; set; }

    /// <summary>
    /// Whether the SBC task has recorded an abort of this channel that the main task has yet to apply (abortRecorded, the byte of invalidationPending
    /// renamed, design/sbc-link-v8-requirements.md CE-7). The SBC task sets it, the main task clears it after the routine. Not used by
    /// <see cref="FakeRrfModel.V7"/>
    /// </summary>
    public bool IsAbortRecorded { get; set; }

    /// <summary>
    /// Epoch of the channel (6 bits), valid if <see cref="IsEpochKnown"/>. RRF keeps it in one byte per channel with the two flags below (CE-4, CE-24)
    /// </summary>
    public byte Epoch { get; set; }

    /// <summary>
    /// Whether RRF knows the epoch of the channel, from the first tagged DSF packet of the channel in this connection (adopt rule, CE-4)
    /// </summary>
    public bool IsEpochKnown { get; set; }

    /// <summary>
    /// Abort gate: set where RRF writes a tagged AbortFile(all), cleared by the record of the InvalidateChannel DSF answers it with (CE-24). While it is
    /// set, every DSF packet of the channel except InvalidateChannel is dropped
    /// </summary>
    public bool IsGated { get; set; }

    /// <summary>
    /// Forget the epoch of the channel, as after boot, InvalidateResources and the first marker (CE-4). The stamp of every frame becomes invalid
    /// (<see cref="FakeMachineState.CodeEpoch"/>), so that a code that survived the end of the connection is of no epoch of the next one
    /// </summary>
    public void ForgetEpoch()
    {
        IsEpochKnown = IsGated = false;
        Epoch = 0;
        for (FakeMachineState? ms = MachineState; ms is not null; ms = ms.Previous)
        {
            ms.CodeEpoch = null;
        }
    }

    /// <summary>
    /// Whether the buffer is completely idle, see GCodeBuffer::IsCompletelyIdle (GCodeBuffer.cpp:951-954): the state of the latest frame is normal and
    /// the buffer is neither ready nor executing. A text code that DSF executes (executingOnSbc) counts as idle, one that is yet to be sent does not,
    /// because RRF runs it again on every spin until it has gone out (GCodes2.cpp:603-606)
    /// </summary>
    public bool IsCompletelyIdle => MachineState.State == FakeMachineState.MachineStateKind.Normal && CurrentCode is null && !IsSendRequested;

    /// <summary>
    /// Whether a macro request has yet to be sent to DSF
    /// </summary>
    public bool IsMacroRequestPending => RequestedMacroFile is not null;

    /// <summary>
    /// Whether the last macro was requested by a code
    /// </summary>
    public bool IsMacroStartedByCode => MachineState.MacroStartedByCode;

    /// <summary>
    /// Called when the macro request has been sent
    /// </summary>
    public void MacroRequestSent() => RequestedMacroFile = null;

    /// <summary>
    /// Called when DSF has been told about the closed macro file
    /// </summary>
    public void MacroFileClosedSent() => IsMacroFileClosed = false;

    /// <summary>
    /// Called when DSF has been told about the aborted files
    /// </summary>
    public void FileAbortSent() => IsAbortRequested = IsAbortAllRequested = false;

    /// <summary>
    /// Called when DSF has been told about the blocking message box
    /// </summary>
    public void MessagePromptSent() => IsMessagePromptPending = false;

    /// <summary>
    /// Called when DSF has been told about the acknowledged message box
    /// </summary>
    public void MessageAcknowledgementSent() => IsMessageAcknowledged = false;

    /// <summary>
    /// Text code that RRF passes on to DSF (DoCode) because it cannot run it itself, see the default case of GCodes::HandleMcode
    /// (GCodes2.cpp:600-606). Null if there is none
    /// </summary>
    public string? SbcCode { get; private set; }

    /// <summary>
    /// Whether the text code is yet to be sent to DSF (sendToSbc, GCodeBuffer.h:416)
    /// </summary>
    public bool IsSendRequested { get; private set; }

    /// <summary>
    /// Whether DSF executes the text code (GCodeBufferState::executingOnSbc, GCodeBuffer::IsExecutingOnSbc)
    /// </summary>
    public bool IsExecutingOnSbc { get; private set; }

    /// <summary>
    /// Pass a text code on to DSF, see GCodeBuffer::SendToSbc. A text code clears lastCodeFromSbc (GCodeBuffer::Put, GCodeBuffer.cpp:294)
    /// </summary>
    /// <param name="code">Text of the code</param>
    public void SendToSbc(string code)
    {
        MachineState.LastCodeFromSbc = false;
        SbcCode = code;
        IsSendRequested = true;
    }

    /// <summary>
    /// The text code has been sent to DSF, see GCodeBuffer::SentToSbc (GCodeBuffer.h:447-452)
    /// </summary>
    public void SentToSbc()
    {
        IsSendRequested = false;
        IsExecutingOnSbc = true;
    }
    #endregion

    /// <summary>
    /// Whether this is one of the job file channels
    /// </summary>
    public bool IsFileChannel => Channel is CodeChannel.File or CodeChannel.File2;

    /// <summary>
    /// Get the original machine state before anything was pushed
    /// </summary>
    public FakeMachineState OriginalMachineState
    {
        get
        {
            FakeMachineState ms = MachineState;
            while (ms.Previous is not null)
            {
                ms = ms.Previous;
            }
            return ms;
        }
    }

    /// <summary>
    /// Frame holding the local variables, see GCodeBuffer::GetVariables
    /// </summary>
    public FakeMachineState VariablesFrame
    {
        get
        {
            FakeMachineState ms = MachineState;
            while (ms.LocalPush && ms.Previous is not null)
            {
                ms = ms.Previous;
            }
            return ms;
        }
    }

    /// <summary>
    /// Frame of the file being executed, see GCodeBuffer::CurrentFileMachineState
    /// </summary>
    public FakeMachineState CurrentFileMachineState => VariablesFrame;

    /// <summary>
    /// Get the response message type, see GCodeBuffer::GetResponseMessageType and the GCodeBuffer instances created in GCodes::GCodes
    /// </summary>
    public MessageTypeFlags ResponseMessageType
    {
        get
        {
            if (MachineState.LastCodeFromSbc)
            {
                // The epoch of the code goes into the free bits 18-23 and 26, which WriteCodeReply moves into the header (CE-11). It is the stamp of the
                // latest frame
                uint epochBits = (MachineState.CodeEpoch is byte epoch) ? ((uint)epoch << FakeTag.TypeEpochShift) | FakeTag.TypeEpochValid : 0;
                return (MessageTypeFlags)((1u << (int)Channel) | epochBits) | MessageTypeFlags.BinaryCodeReplyFlag;
            }
            return Channel switch
            {
                CodeChannel.HTTP => MessageTypeFlags.HttpMessage,
                CodeChannel.Telnet => MessageTypeFlags.TelnetMessage,
                CodeChannel.USB => MessageTypeFlags.UsbMessage,
                CodeChannel.USB2 => MessageTypeFlags.Usb2Message,
                CodeChannel.Aux => MessageTypeFlags.AuxMessage,
                CodeChannel.Aux2 => MessageTypeFlags.Aux2Message,
                CodeChannel.LCD => MessageTypeFlags.LcdMessage,
                _ => MessageTypeFlags.GenericMessage
            };
        }
    }

    /// <summary>
    /// Whether a file is being executed, see GCodeBuffer::IsDoingFile
    /// </summary>
    public bool IsDoingFile => MachineState.DoingFile || IsMacroRequestPending || IsMacroFileClosed;

    /// <summary>
    /// Whether a macro file is being executed, see GCodeBuffer::IsDoingFileMacro
    /// </summary>
    /// <param name="unlessClosed">Whether to ignore a file that has been closed internally</param>
    /// <returns>Whether a macro file is being executed</returns>
    public bool IsDoingFileMacro(bool unlessClosed = false) => MachineState.DoingFileMacro || IsMacroRequestPending || (IsMacroFileClosed && !unlessClosed);

    /// <summary>
    /// Whether the current file has finished, see GCodeBuffer::IsFileFinished
    /// </summary>
    public bool IsFileFinished => MachineState.FileFinished;

    /// <summary>
    /// Store a binary code from DSF, see GCodeBuffer::PutBinary
    /// </summary>
    /// <param name="code">Code to execute</param>
    public void PutBinary(FakeCode code)
    {
        MachineState.LastCodeFromSbc = true;
        HasJustStartedMacro = false;
        IsCodeReplySurplus = false;     // the dropped code has finished, or this code belongs to a macro that the dropped code has started since
        MachineState.CodeEpoch = code.Stamp;    // FillBuffer carries the stamp of the ring over to the latest frame (CE-9)
        CurrentCode = code;
        IsExecuting = false;
    }

    /// <summary>
    /// Mark the current code as (not) finished, see GCodeBuffer::SetFinished
    /// </summary>
    /// <param name="finished">Whether the code has finished</param>
    public void SetFinished(bool finished)
    {
        if (finished)
        {
            // This ends a text code passed on to DSF as well (sendToSbc = false and the parser's SetFinished, GCodeBuffer.cpp:957-966)
            CurrentCode = null;
            IsExecuting = false;
            SbcCode = null;
            IsSendRequested = IsExecutingOnSbc = false;
        }
        else
        {
            IsExecuting = true;
        }
    }

    /// <summary>
    /// Push a new frame, see GCodeBuffer::PushState
    /// </summary>
    /// <param name="withinSameFile">Whether this is a local push</param>
    /// <returns>Whether the frame could be pushed</returns>
    public bool PushState(bool withinSameFile)
    {
        int depth = 0;
        for (FakeMachineState ms = MachineState; ms.Previous is not null; ms = ms.Previous)
        {
            depth++;
        }
        if (depth >= MaxStackDepth)
        {
            return false;
        }

        MachineState = new FakeMachineState(MachineState, withinSameFile);
        if (!withinSameFile)
        {
            MachineState.InvokingCode = CurrentCode;
        }
        return true;
    }

    /// <summary>
    /// Pop the current frame, see GCodeBuffer::PopState
    /// </summary>
    /// <param name="withinSameFile">Whether only a local frame is popped</param>
    /// <returns>Whether a frame could be popped</returns>
    public bool PopState(bool withinSameFile)
    {
        bool poppedFileState;
        do
        {
            FakeMachineState ms = MachineState;
            if (ms.Previous is null)
            {
                ms.MessageAcknowledged = false;
                ms.WaitingForAcknowledgement = false;
                return false;
            }

            poppedFileState = !ms.LocalPush;
            MachineState = ms.Previous;
        }
        while (!withinSameFile && !poppedFileState);
        return true;
    }

    /// <summary>
    /// Abort the files being executed and flag the abort for DSF, see GCodeBuffer::AbortFile
    /// </summary>
    /// <param name="abortAll">Whether all files are aborted or only the last one</param>
    public void AbortFile(bool abortAll)
    {
        if (MachineState.DoingFile)
        {
            do
            {
                if (MachineState.DoingFile)
                {
                    MachineState.CloseFile();
                }
            }
            while (PopState(false) && abortAll);

            IsAbortRequested = true;
            IsAbortAllRequested = abortAll;
        }
    }

    /// <summary>
    /// Flag the current macro file as finished, see GCodeBuffer::SetFileFinished
    /// </summary>
    public void SetFileFinished()
    {
        int macroFileId = 0, printFileId = OriginalMachineState.FileId;
        for (FakeMachineState? ms = MachineState; ms is not null; ms = ms.Previous)
        {
            if (macroFileId == 0 && ms.FileId != 0 && ms.FileId != printFileId && !ms.FileFinished)
            {
                macroFileId = ms.FileId;
            }

            if (macroFileId != 0)
            {
                if (ms.FileId == macroFileId)
                {
                    ms.FileFinished = true;
                }
                else
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Request a macro file from DSF, see GCodeBuffer::RequestMacroFile. Unlike RRF this does not block,
    /// the caller must check <see cref="IsWaitingForMacro"/> and continue once the request has been resolved
    /// </summary>
    /// <param name="fileName">Macro file to request</param>
    /// <param name="fromCode">Whether the macro is started by a code</param>
    /// <returns>True if the request blocks until DSF has answered it</returns>
    public bool RequestMacroFile(string fileName, bool fromCode)
    {
        HasJustStartedMacro = MacroFileError = IsMacroEmpty = false;
        MachineState.MacroStartedByCode = fromCode;
        RequestedMacroFile = fileName;

        // There is no need to block the main task if daemon.g is requested
        if (Channel != CodeChannel.Daemon || MachineState.DoingFileMacro)
        {
            IsWaitingForMacro = true;
            return true;
        }
        HasJustStartedMacro = true;
        return false;
    }

    /// <summary>
    /// Finish a macro request that has been resolved, see the end of GCodeBuffer::RequestMacroFile
    /// </summary>
    /// <returns>Whether the macro file could be started</returns>
    public bool FinishMacroRequest()
    {
        if (!MacroFileError)
        {
            HasJustStartedMacro = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Resolve the request for a macro file, see GCodeBuffer::ResolveMacroRequest
    /// </summary>
    /// <param name="hadError">Whether the macro could not be started</param>
    /// <param name="isEmpty">Whether the macro file is empty</param>
    public void ResolveMacroRequest(bool hadError, bool isEmpty)
    {
        MacroFileError = hadError;
        IsMacroEmpty = !hadError && isEmpty;
        IsWaitingForMacro = false;
    }

    /// <summary>
    /// Start a new file on the current frame, see GCodeBuffer::StartNewFile
    /// </summary>
    /// <param name="fileName">File name</param>
    public void StartNewFile(string fileName)
    {
        MachineState.FileName = fileName;
        MachineState.SetFileExecuting();
    }

    /// <summary>
    /// The current macro file has been closed by RRF and DSF needs to be told, see GCodeBuffer::MacroFileClosed
    /// </summary>
    public void MacroFileClosed()
    {
        MachineState.CloseFile();
        HasJustStartedMacro = false;
        IsMacroFileClosed = true;
    }

    /// <summary>
    /// Wait for a message box to be acknowledged, see GCodeBuffer::WaitForAcknowledgement
    /// </summary>
    /// <param name="seq">Sequence number of the message box</param>
    public void WaitForAcknowledgement(uint seq)
    {
        MachineState.MessageBoxSeq = seq;
        MachineState.WaitingForAcknowledgement = true;
        IsMessagePromptPending = true;
    }

    /// <summary>
    /// A message box has been acknowledged, see GCodeBuffer::MessageAcknowledged
    /// </summary>
    /// <param name="cancelled">Whether the message box was cancelled</param>
    /// <param name="shouldAbort">Whether the macro is supposed to be aborted on cancellation</param>
    /// <param name="seq">Sequence number of the message box or 0</param>
    public void MessageAcknowledged(bool cancelled, bool shouldAbort, uint seq)
    {
        for (FakeMachineState? ms = MachineState; ms is not null; ms = ms.Previous)
        {
            if (ms.WaitingForAcknowledgement && (seq == ms.MessageBoxSeq || seq == 0 || ms.MessageBoxSeq == 0))
            {
                ms.WaitingForAcknowledgement = false;
                ms.MessageAcknowledged = true;
                ms.MessageShouldAbort = cancelled && shouldAbort;
                if (cancelled)
                {
                    LastResult = CodeResult.M291Cancelled;
                }
                IsMessageAcknowledged = !(cancelled && shouldAbort) || !ms.DoingFile;
            }
        }
    }

    /// <summary>
    /// Reset this buffer after a firmware reset
    /// </summary>
    public void Reset()
    {
        MachineState = new FakeMachineState();
        CurrentCode = null;
        IsExecuting = false;
        PendingMacroCall = null;
        LastResult = CodeResult.Ok;
        IsWaitingForMacro = HasJustStartedMacro = MacroFileError = IsMacroEmpty = IsMacroFileClosed = false;
        IsAbortRequested = IsAbortAllRequested = IsMessagePromptPending = IsMessageAcknowledged = IsInvalidated = IsInvalidationPending = IsCodeReplySurplus = false;
        IsAbortRecorded = false;
        ForgetEpoch();
        RequestedMacroFile = null;
        SbcCode = null;
        IsSendRequested = IsExecutingOnSbc = false;
    }

    /// <summary>
    /// Get the variables of the current file frame
    /// </summary>
    /// <returns>Local variables</returns>
    public Dictionary<string, object?> GetVariables() => VariablesFrame.Variables;
}
