using DuetAPI;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Packet the fake firmware sends to DSF (a FirmwareRequest in RRF terms).
/// Packets are passed as objects, there is no binary encoding
/// </summary>
public sealed class FakePacket
{
    /// <summary>
    /// Type of the packet
    /// </summary>
    public Request Request { get; init; }

    /// <summary>
    /// Packet ID within its transfer
    /// </summary>
    public ushort Id { get; internal set; }

    /// <summary>
    /// Number of the transfer that delivered this packet to DSF
    /// </summary>
    public int Transfer { get; internal set; }

    /// <summary>
    /// <see cref="System.Diagnostics.Stopwatch"/> timestamp of the transfer that delivered this packet to DSF
    /// </summary>
    public long Timestamp { get; internal set; }

    /// <summary>
    /// Code channel of the packet, if any
    /// </summary>
    public CodeChannel Channel { get; init; }

    /// <summary>
    /// Flags of a message or code reply
    /// </summary>
    public MessageTypeFlags MessageType { get; init; }

    /// <summary>
    /// Text of a message, name of a requested macro file, evaluated expression or variable, or object model JSON
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Value of an evaluation or variable result. Errors are passed as <see cref="CodeParserException"/>
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// Whether a requested macro file is started by a code (ExecuteMacro)
    /// </summary>
    public bool IsFromCode { get; init; }

    /// <summary>
    /// Whether all files are aborted (AbortFile)
    /// </summary>
    public bool AbortAll { get; init; }

    /// <summary>
    /// Free code buffer space (CodeBufferUpdate)
    /// </summary>
    public ushort BufferSpace { get; init; }

    /// <summary>
    /// Code that RRF attributes a code reply to, if known
    /// </summary>
    public FakeCode? RelatedCode { get; init; }

    /// <summary>
    /// Request that DSF is asked to send again (ResendPacket)
    /// </summary>
    internal FakeSbcRequest? ResendRequest { get; init; }

    /// <summary>
    /// Whether this is a code reply
    /// </summary>
    public bool IsCodeReply => Request == Request.Message && MessageType.HasFlag(MessageTypeFlags.BinaryCodeReplyFlag);

    /// <inheritdoc />
    public override string ToString() => Request switch
    {
        Request.Message => $"#{Transfer} Message [{MessageType}] '{Text}'" + ((RelatedCode is not null) ? $" for {RelatedCode}" : string.Empty),
        Request.ExecuteMacro => $"#{Transfer} ExecuteMacro {Channel} '{Text}' fromCode={IsFromCode}",
        Request.AbortFile => $"#{Transfer} AbortFile {Channel} abortAll={AbortAll}",
        Request.CodeBufferUpdate => $"#{Transfer} CodeBufferUpdate {BufferSpace}",
        Request.EvaluationResult or Request.VariableResult => $"#{Transfer} {Request} {Channel} {Text} = {Value}",
        Request.ObjectModel => $"#{Transfer} ObjectModel {Text}",
        _ => $"#{Transfer} {Request} {Channel}"
    };
}
