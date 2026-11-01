using System.Collections.Generic;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Stack frame of an emulated G-code buffer, mirroring the parts of RRF's GCodeMachineState that matter in SBC mode
/// </summary>
internal sealed class FakeMachineState
{
    /// <summary>
    /// States of the per-frame state machine that the fake emulates
    /// </summary>
    internal enum MachineStateKind
    {
        /// <summary>
        /// GCodeState::normal
        /// </summary>
        Normal,

        /// <summary>
        /// GCodeState::waitingForAcknowledgement, set by M291 in SBC mode before the message box frame is pushed
        /// </summary>
        WaitingForAcknowledgement
    }

    /// <summary>
    /// Create the base frame of a G-code buffer
    /// </summary>
    public FakeMachineState() { }

    /// <summary>
    /// Push a new frame, see the GCodeMachineState(prev, withinSameFile) constructor
    /// </summary>
    /// <param name="previous">Previous frame</param>
    /// <param name="withinSameFile">Whether this is a local push (M120, message boxes)</param>
    public FakeMachineState(FakeMachineState previous, bool withinSameFile)
    {
        Previous = previous;
        LocalPush = withinSameFile;
        FileId = previous.FileId;
        FileName = previous.FileName;
        DoingFileMacro = previous.DoingFileMacro;
        LastCodeFromSbc = previous.LastCodeFromSbc;
        MacroStartedByCode = previous.MacroStartedByCode;
        FileFinished = previous.FileFinished;
        LockedMovement = previous.LockedMovement;
    }

    /// <summary>
    /// Previous frame or null if this is the base frame
    /// </summary>
    public FakeMachineState? Previous { get; }

    /// <summary>
    /// True if this frame was pushed within the same file, so it uses the variables of its parent
    /// </summary>
    public bool LocalPush { get; }

    /// <summary>
    /// ID of the file being executed or 0 (NoFileId) if none
    /// </summary>
    public int FileId { get; set; }

    /// <summary>
    /// Name of the file being executed
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Whether this frame runs a macro file
    /// </summary>
    public bool DoingFileMacro { get; set; }

    /// <summary>
    /// Whether the last code on this frame came from DSF in binary form
    /// </summary>
    public bool LastCodeFromSbc { get; set; }

    /// <summary>
    /// Whether the last macro requested on this frame was started by a code
    /// </summary>
    public bool MacroStartedByCode { get; set; }

    /// <summary>
    /// Whether DSF has finished the file of this frame (MacroCompleted)
    /// </summary>
    public bool FileFinished { get; set; }

    /// <summary>
    /// Whether a blocking message box is waiting for acknowledgement on this frame
    /// </summary>
    public bool WaitingForAcknowledgement { get; set; }

    /// <summary>
    /// Whether the message box of this frame has been acknowledged
    /// </summary>
    public bool MessageAcknowledged { get; set; }

    /// <summary>
    /// Whether the message box was cancelled and the macro must be aborted
    /// </summary>
    public bool MessageShouldAbort { get; set; }

    /// <summary>
    /// Sequence number of the message box this frame waits for
    /// </summary>
    public uint MessageBoxSeq { get; set; }

    /// <summary>
    /// State of the state machine
    /// </summary>
    public MachineStateKind State { get; set; } = MachineStateKind.Normal;

    /// <summary>
    /// Whether this frame holds the movement lock (lockedResources)
    /// </summary>
    public bool LockedMovement { get; set; }

    /// <summary>
    /// Code that pushed this frame, used to attribute the final macro reply in the packet log
    /// </summary>
    public FakeCode? InvokingCode { get; set; }

    /// <summary>
    /// Code that put this frame into a non-normal state, used to attribute the final reply in the packet log
    /// </summary>
    public FakeCode? StateCode { get; set; }

    /// <summary>
    /// Local variables of this frame
    /// </summary>
    public Dictionary<string, object?> Variables { get; } = [];

    /// <summary>
    /// Macro parameters of this frame
    /// </summary>
    public Dictionary<string, object?> Parameters { get; } = [];

    /// <summary>
    /// Whether a file is being executed, see GCodeMachineState::DoingFile
    /// </summary>
    public bool DoingFile => FileId != 0;

    /// <summary>
    /// Mark a new file as executing, see GCodeMachineState::SetFileExecuting
    /// </summary>
    public void SetFileExecuting()
    {
        FileId = (Previous?.FileId ?? 0) + 1;
        FileFinished = false;
    }

    /// <summary>
    /// Close the file of this frame and of the frames below that run the same file, see GCodeMachineState::CloseFile
    /// </summary>
    public void CloseFile()
    {
        if (FileId != 0)
        {
            int lastFileId = FileId;
            for (FakeMachineState? ms = this; ms is not null; ms = ms.Previous)
            {
                if (ms.FileId == lastFileId)
                {
                    ms.FileId = 0;
                    ms.FileFinished = false;
                    ms.FileName = null;
                }
            }
        }
    }
}
