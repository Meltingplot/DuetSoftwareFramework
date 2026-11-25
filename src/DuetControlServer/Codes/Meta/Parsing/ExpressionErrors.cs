namespace DuetControlServer.Codes.Meta.Parsing;

/// <summary>
/// Error message texts that are also emitted by the firmware expression parser, kept identical here so that an
/// expression rejected on the SBC reports the same wording as it would if the firmware had rejected it
/// </summary>
internal static class ExpressionErrors
{
    /// <summary>
    /// An array was indexed out of bounds
    /// </summary>
    public const string ArrayIndexOutOfRange = "array index out of bounds";

    /// <summary>
    /// exists() was applied to something that is not a variable or object model path
    /// </summary>
    public const string InvalidExists = "invalid 'exists' expression";

    /// <summary>
    /// A non-negative integer was required
    /// </summary>
    public const string ExpectedNonNegativeInt = "expected non-negative integer";

    /// <summary>
    /// An array does not fit into one heap block of the firmware (RRF src/Platform/ArrayHandle.cpp, ArrayHandle::Allocate)
    /// </summary>
    public const string ArrayTooLarge = "Array too large";

    /// <summary>
    /// An array literal has more elements than the firmware parses (RRF src/GCodes/GCodeBuffer/ExpressionParser.cpp, ParseGeneralArray)
    /// </summary>
    public const string TooManyArrayElements = "too many array elements";
}
