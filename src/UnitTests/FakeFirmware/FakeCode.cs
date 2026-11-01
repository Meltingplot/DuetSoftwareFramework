using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.Link.Protocol;
using DuetControlServer.Link.Protocol.Shared;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Snapshot of a G/M/T-code the fake firmware received from DSF.
/// DSF keeps changing its code instances after they have been sent, so only a copy is kept
/// </summary>
public sealed class FakeCode
{
    /// <summary>
    /// Take a snapshot of a code that DSF is about to send
    /// </summary>
    /// <param name="code">Code from DSF</param>
    /// <param name="maxCodeLength">Maximum length of a binary code</param>
    internal FakeCode(DuetAPI.Commands.Code code, int maxCodeLength)
    {
        Channel = code.Channel;
        Type = code.Type;
        MajorNumber = code.MajorNumber;
        MinorNumber = (code.MinorNumber >= 0) ? code.MinorNumber : null;
        Flags = code.Flags;
        LineNumber = code.LineNumber;
        FilePosition = code.FilePosition;
        Parameters = [.. code.Parameters];
        Text = code.ToString();

        // Same length that the SPI adapter writes and that RRF stores in its code buffer
        BinaryLength = Writer.GetCodeSize(code, maxCodeLength, Consts.ProtocolVersion);
    }

    /// <summary>
    /// Channel of the code
    /// </summary>
    public CodeChannel Channel { get; }

    /// <summary>
    /// Type of the code. Whole-line comments are sent as Q0 in binary form, see <see cref="CodeType.Comment"/>
    /// </summary>
    public CodeType Type { get; }

    /// <summary>
    /// Major code number
    /// </summary>
    public int? MajorNumber { get; }

    /// <summary>
    /// Minor code number or null if there is none
    /// </summary>
    public int? MinorNumber { get; }

    /// <summary>
    /// Code flags as set by DSF
    /// </summary>
    public CodeFlags Flags { get; }

    /// <summary>
    /// Line number of the code
    /// </summary>
    public long? LineNumber { get; }

    /// <summary>
    /// File position of the code
    /// </summary>
    public long? FilePosition { get; }

    /// <summary>
    /// Parameters of the code
    /// </summary>
    public IReadOnlyList<CodeParameter> Parameters { get; }

    /// <summary>
    /// Text representation of the code when it was sent
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Length of the binary code in bytes
    /// </summary>
    public int BinaryLength { get; }

    /// <summary>
    /// Whether this code comes from a macro file
    /// </summary>
    public bool IsFromMacro => Flags.HasFlag(CodeFlags.IsFromMacro);

    /// <summary>
    /// Command without parameters, e.g. G4 or M581.1. This is what code handlers are registered for
    /// </summary>
    public string Command => (Type == CodeType.Comment) ? "Q0" : $"{(char)Type}{MajorNumber}" + ((MinorNumber is not null) ? $".{MinorNumber}" : string.Empty);

    /// <summary>
    /// Check if a parameter is present
    /// </summary>
    /// <param name="letter">Parameter letter</param>
    /// <returns>Whether the parameter is present</returns>
    public bool HasParameter(char letter) => Parameters.Any(parameter => parameter.Letter == letter);

    /// <summary>
    /// Get a parameter
    /// </summary>
    /// <param name="letter">Parameter letter</param>
    /// <returns>Parameter or null if not present</returns>
    public CodeParameter? GetParameter(char letter) => Parameters.FirstOrDefault(parameter => parameter.Letter == letter);

    /// <summary>
    /// Command as printed by RRF in front of error messages, see BinaryParser::PrintCommand
    /// </summary>
    internal string PrintCommand => (MajorNumber is not null) ? $"{(char)Type}{MajorNumber.Value.ToString(CultureInfo.InvariantCulture)}" + ((MinorNumber is not null) ? $".{MinorNumber}" : string.Empty) : string.Empty;

    /// <inheritdoc />
    public override string ToString() => Text;
}
