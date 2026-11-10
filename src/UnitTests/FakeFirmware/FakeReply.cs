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
    /// code finishes with an error if DSF cannot start it. A code that ran into an abort can do this after the abort (OF-T10, CE-12 (b))
    /// </summary>
    /// <param name="fileName">Macro file</param>
    /// <returns>Reply</returns>
    public static FakeReply CallMacro(string fileName) => new(CodeResult.Ok, fileName) { IsMacroCall = true };

    /// <inheritdoc />
    public override string ToString() => $"{Result}: {Text}";
}
