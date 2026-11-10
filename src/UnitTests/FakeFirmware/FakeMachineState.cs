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
        WaitingForAcknowledgement,

        /// <summary>
        /// GCodeState::homing1: request the homing file of the lowest axis still to be homed (GCodes4.cpp:358-392)
        /// </summary>
        Homing1,

        /// <summary>
        /// GCodeState::homing2: the homing file has ended, check that it homed its axis (GCodes4.cpp:394-413)
        /// </summary>
        Homing2,

        /// <summary>
        /// GCodeState::toolChange0: run tfree of the old tool (GCodes4.cpp:415-441)
        /// </summary>
        ToolChange0,

        /// <summary>
        /// GCodeState::toolChange1: tfree has ended, run tpre of the new tool (GCodes4.cpp:443-469)
        /// </summary>
        ToolChange1,

        /// <summary>
        /// GCodeState::toolChange2: tpre has ended, select the new tool and run its tpost (GCodes4.cpp:471-522)
        /// </summary>
        ToolChange2,

        /// <summary>
        /// GCodeState::toolChangeComplete: tpost has ended, the T-code ends (GCodes4.cpp:524-542)
        /// </summary>
        ToolChangeComplete
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
        CodeEpoch = previous.CodeEpoch;
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
    /// Epoch of the code from DSF that this frame runs or ran last (codeEpoch, CE-9), null if invalid. FillBuffer carries it over from the stamp of
    /// the code in the ring (PutBinary), a new frame takes it from the frame below as it takes <see cref="LastCodeFromSbc"/>. The replies of the
    /// frame and the requests of its codes carry it. It lives on the frame and not on the buffer (decision of 2026-11-10), so that the codes of a
    /// macro that a code of an older epoch starts after an abort do not overwrite the stamp of that code, which waits on the frame below: when the
    /// macro has ended, its next request and its final reply carry its own epoch again
    /// </summary>
    public byte? CodeEpoch { get; set; }

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
    /// Macros of the tool change that this frame runs, tfree, tpre and tpost, null for one that is not run. RRF derives them from the tool numbers of
    /// the movement state (GCodes4.cpp:431-438, :459-466, :512-519)
    /// </summary>
    public string?[] ToolChangeMacros { get; set; } = [];

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
