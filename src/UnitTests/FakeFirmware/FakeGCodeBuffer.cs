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
                return (MessageTypeFlags)(1u << (int)Channel) | MessageTypeFlags.BinaryCodeReplyFlag;
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
            CurrentCode = null;
            IsExecuting = false;
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
        IsAbortRequested = IsAbortAllRequested = IsMessagePromptPending = IsMessageAcknowledged = IsInvalidated = false;
        RequestedMacroFile = null;
    }

    /// <summary>
    /// Get the variables of the current file frame
    /// </summary>
    /// <returns>Local variables</returns>
    public Dictionary<string, object?> GetVariables() => VariablesFrame.Variables;
}
