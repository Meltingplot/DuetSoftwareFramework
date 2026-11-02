using DuetAPI;
using DuetControlServer.Link.Protocol.SbcRequests;
using DuetControlServer.Link.Protocol.Shared;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Request that DSF sent to the fake firmware (an SbcRequest in RRF terms)
/// </summary>
public sealed class FakeSbcRequest
{
    /// <summary>
    /// Type of the request
    /// </summary>
    public Request Request { get; init; }

    /// <summary>
    /// Packet ID within its transfer
    /// </summary>
    public ushort Id { get; internal set; }

    /// <summary>
    /// Number of the transfer that delivered this request to the firmware
    /// </summary>
    public int Transfer { get; internal set; }

    /// <summary>
    /// Code channel of the request, if any
    /// </summary>
    public CodeChannel Channel { get; init; }

    /// <summary>
    /// Code to execute (Code)
    /// </summary>
    public FakeCode? Code { get; init; }

    /// <summary>
    /// Object model key (GetObjectModel, ObjectModelKeyChanged), expression (EvaluateExpression),
    /// variable name (SetVariable, DeleteLocalVariable), message (Message) or file name (SetPrintFileInfo)
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Object model flags (GetObjectModel) or expression of a variable (SetVariable)
    /// </summary>
    public string Argument { get; init; } = string.Empty;

    /// <summary>
    /// Whether a variable is created (SetVariable) or a macro failed (MacroCompleted)
    /// </summary>
    public bool Flag { get; init; }

    /// <summary>
    /// Message flags (Message)
    /// </summary>
    public MessageTypeFlags MessageFlags { get; init; }

    /// <summary>
    /// Code result (SetLastCodeResult)
    /// </summary>
    public CodeResult CodeResult { get; init; }

    /// <summary>
    /// Reason why a print was stopped (PrintStopped)
    /// </summary>
    public PrintStoppedReason PrintStoppedReason { get; init; }

    /// <summary>
    /// Capabilities of the link to activate (ActivateLinkCapabilities)
    /// </summary>
    public LinkCapabilities LinkCapabilities { get; init; }

    /// <summary>
    /// Copy this request because DSF sends it again
    /// </summary>
    /// <returns>Copy of this request</returns>
    internal FakeSbcRequest CloneForResend() => (FakeSbcRequest)MemberwiseClone();

    /// <inheritdoc />
    public override string ToString() => Request switch
    {
        Request.Code => $"#{Transfer} Code {Code}",
        Request.MacroCompleted => $"#{Transfer} MacroCompleted {Channel} error={Flag}",
        Request.GetObjectModel => $"#{Transfer} GetObjectModel '{Text}' '{Argument}'",
        Request.EvaluateExpression => $"#{Transfer} EvaluateExpression {Channel} {Text}",
        Request.SetVariable => $"#{Transfer} SetVariable {Channel} {Text} = {Argument} create={Flag}",
        Request.DeleteLocalVariable => $"#{Transfer} DeleteLocalVariable {Channel} {Text}",
        _ => $"#{Transfer} {Request} {Channel}"
    };
}
