using System.Collections.Generic;
using System.Linq;
using DuetControlServer.Link.Protocol.Shared;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Result of a code handled by a test handler of the fake firmware.
/// The fake treats it like the GCodeResult and reply text of an RRF code handler, see GCodes::HandleResult
/// </summary>
public sealed class FakeReply
{
    /// <summary>
    /// Code result
    /// </summary>
    public CodeResult Result { get; }

    /// <summary>
    /// Reply text. RRF prefixes warnings and errors with the command (e.g. "G1: "), the fake does the same
    /// </summary>
    public string Text { get; }

    private FakeReply(CodeResult result, string text)
    {
        Result = result;
        Text = text;
    }

    /// <summary>
    /// The code has finished successfully
    /// </summary>
    /// <param name="text">Optional reply text</param>
    /// <returns>Reply</returns>
    public static FakeReply Ok(string text = "") => new(CodeResult.Ok, text);

    /// <summary>
    /// The code has finished with a warning
    /// </summary>
    /// <param name="text">Warning text</param>
    /// <returns>Reply</returns>
    public static FakeReply Warning(string text) => new(CodeResult.Warning, text);

    /// <summary>
    /// The code has failed
    /// </summary>
    /// <param name="text">Error text</param>
    /// <returns>Reply</returns>
    public static FakeReply Error(string text) => new(CodeResult.Error, text);

    /// <summary>
    /// The code has not finished yet. RRF calls the code handler again on the next spin, so the fake calls the test handler again
    /// </summary>
    public static readonly FakeReply NotFinished = new(CodeResult.NotFinished, string.Empty);

    /// <summary>
    /// Whether the code calls the macro named by <see cref="Text"/>, see <see cref="CallMacro"/>
    /// </summary>
    public bool IsMacroCall { get; private init; }

    /// <summary>
    /// The code calls a macro now, as G28 does for its homing files (GCodes4.cpp:358-413): RRF asks DSF for it with fromCode set, like M98, and the
    /// code finishes with an error if DSF cannot start it. A code that ran into an abort can do this after the abort (OF-T10); with the channel epoch
    /// the request then goes out as a system macro request (decision of 2026-11-10, replaces CE-12 (b))
    /// </summary>
    /// <param name="fileName">Macro file</param>
    /// <returns>Reply</returns>
    public static FakeReply CallMacro(string fileName) => new(CodeResult.Ok, fileName) { IsMacroCall = true };

    /// <summary>
    /// Axes that a G28-like code homes, see <see cref="Home"/>
    /// </summary>
    public IReadOnlyList<char> HomedAxes { get; private init; } = [];

    /// <summary>
    /// Macros that a T-like code runs, see <see cref="ToolChange"/>
    /// </summary>
    public IReadOnlyList<string?> ToolChangeMacros { get; private init; } = [];

    /// <summary>
    /// Whether the code goes on in the state machine of its frame (<see cref="Home"/>, <see cref="ToolChange"/>)
    /// </summary>
    public bool IsStateMachine => HomedAxes.Count > 0 || ToolChangeMacros.Count > 0;

    /// <summary>
    /// The code homes axes as G28 does: it takes the movement lock (GCodes2.cpp:389-395), sets the axes to be homed, marks them as not homed and sets
    /// the state homing1 (DoHome, GCodes.cpp:3857-3884; a G28 while axes are still to be homed fails), and the state machine requests home&lt;axis&gt;.g of the lowest axis still to be homed (fromCode, SystemHelperMacroCode) and
    /// checks after it that the axis was homed (GCodes4.cpp:358-413). An axis counts as homed when a G1 H1 of that axis ran, the fake has no endstops.
    /// The code ends with an error reply if a homing file is not found or did not home its axis. homeall.g and the order of Z homing with a probe
    /// (Kinematics::GetHomingFileName) are not modelled
    /// </summary>
    /// <param name="axes">Axes to home, lowest first</param>
    /// <returns>Reply</returns>
    public static FakeReply Home(params char[] axes) => new(CodeResult.Ok, string.Empty) { HomedAxes = [.. axes.Select(char.ToUpperInvariant)] };

    /// <summary>
    /// The code changes the tool as a T-code does: it takes the movement lock and sets the state toolChange0 without a reply (HandleTcode,
    /// GCodes2.cpp:4930-4964; StartToolChange, GCodes.cpp:5542-5546), and the state machine runs tfree, tpre and tpost one after the other, each one after the previous one has
    /// ended (fromCode, ToolChangeMacroCode, without reporting a missing file), and then ends the code with its reply (GCodes4.cpp:415-542). The fallback
    /// of RRF from tfreeN.g to tfree.g when the first one is missing is not modelled
    /// </summary>
    /// <param name="tfree">Macro of the old tool or null if there is none</param>
    /// <param name="tpre">Macro before the new tool is selected or null</param>
    /// <param name="tpost">Macro after the new tool is selected or null</param>
    /// <returns>Reply</returns>
    public static FakeReply ToolChange(string? tfree, string? tpre, string? tpost) => new(CodeResult.Ok, string.Empty) { ToolChangeMacros = [tfree, tpre, tpost] };

    /// <inheritdoc />
    public override string ToString() => $"{Result}: {Text}";
}
