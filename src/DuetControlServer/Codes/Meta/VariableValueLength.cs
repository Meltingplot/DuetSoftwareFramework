using DuetAPI.Utility;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DuetControlServer.Codes.Meta;

/// <summary>
/// Lower bounds of the JSON that RepRapFirmware writes for a variable value, to refuse a value whose JSON can never reach DSF
/// before the code goes to the firmware (Meltingplot)
/// </summary>
/// <remarks>
/// The firmware writes a heap array as JSON in the reply to SetVariable and in the "global" object model key, and both have to fit
/// into one transfer. A bigger value used to make the firmware ask for the SetVariable request again after it had assigned the value,
/// so that the second attempt failed with "variable already exists", and the "global" key could no longer be fetched at all.
/// The bounds never exceed the real length, so DSF refuses only what the firmware cannot send either
/// </remarks>
public static class VariableValueLength
{
    /// <summary>
    /// Error message, the same as the firmware's for a SetVariable reply that can never be sent
    /// </summary>
    public const string TooLongMessage = "value too long to send to the SBC";

    /// <summary>
    /// Longest JSON of a VariableResult packet. RRF DataTransfer::WriteSetVariableResult writes the packet only if
    /// CanWritePacket(sizeof(EvaluationResultHeader) + strlen(varName) + json->Length()) holds (src/SBC/DataTransfer.cpp), which needs
    /// FreeTxSpace() >= sizeof(PacketHeader) + that length (src/SBC/DataTransfer.h). FreeTxSpace is at most SbcTransferBufferSize
    /// (src/SBC/SbcMessageFormats.h) in an empty transmit buffer, and less once anything else is in it
    /// </summary>
    /// <param name="varName">Name of the variable as it goes to the firmware, e.g. global.m or global.m[1]</param>
    /// <returns>Longest JSON that the reply can carry</returns>
    public static int MaxVariableResultJsonLength(string varName) =>
        Consts.BufferSize - Marshal.SizeOf<PacketHeader>() - Marshal.SizeOf<EvaluationResultHeader>() - Encoding.UTF8.GetByteCount(varName);

    /// <summary>
    /// Longest object model response. The GetObjectModel case of RRF SbcInterface::ExchangeData (src/SBC/SbcInterface.cpp) discards a
    /// response longer than SbcTransferBufferSize - sizeof(PacketHeader) - sizeof(StringHeader). The "global" key holds every global
    /// variable whole: GlobalVariables::ReportAsJson writes each value after its name inside braces (src/ObjectModel/GlobalVariables.cpp),
    /// so no array in it starts at the initial buffer offset of the query (ObjectModel::ReportAsJson, src/ObjectModel/ObjectModel.cpp),
    /// and only such a root array is sent in parts (ReportHeapArrayAsJson)
    /// </summary>
    public static readonly int MaxObjectModelResponseLength = Consts.BufferSize - Marshal.SizeOf<PacketHeader>() - Marshal.SizeOf<StringHeader>();

    /// <summary>
    /// Check if the firmware can never send a value back to DSF
    /// </summary>
    /// <param name="value">Value as the SBC evaluated it</param>
    /// <param name="varName">Name of the variable as it goes to the firmware</param>
    /// <param name="isGlobal">Whether the value becomes (part of) a global variable</param>
    /// <returns>True if the value is too long</returns>
    public static bool IsTooLong(object? value, string varName, bool isGlobal)
    {
        // The SetVariable reply. Only an array is written as JSON (SbcInterface.cpp, SetVariable case), other values go in binary form.
        // ReportHeapArrayAsJson (ObjectModel.cpp) may stop after any element of a root array once half of the output buffers is used,
        // but it always writes the brackets and the first element whole
        if (value is object?[] array)
        {
            int maxReplyLength = MaxVariableResultJsonLength(varName);
            long replyLength = 2 + ((array.Length > 0) ? MinimumJsonLength(array[0], maxReplyLength) : 0);
            if (replyLength > maxReplyLength)
            {
                return true;
            }
        }

        // A global variable also goes whole into the "global" object model key, which DSF fetches in one response
        return isGlobal && MinimumJsonLength(value, MaxObjectModelResponseLength) > MaxObjectModelResponseLength;
    }

    /// <summary>
    /// Get a lower bound of the length of the JSON that ObjectModel::ReportItemAsJsonFull (RRF src/ObjectModel/ObjectModel.cpp) writes
    /// for a value. The result is exact for null, booleans, 32-bit integers and arrays of them
    /// </summary>
    /// <param name="value">Value as the SBC evaluated it</param>
    /// <param name="limit">Length beyond which the exact bound is of no interest</param>
    /// <returns>Lower bound of the length, or a number greater than the limit</returns>
    public static long MinimumJsonLength(object? value, long limit) => MinimumJsonLength(value, limit, new Dictionary<object?[], long>(ReferenceEqualityComparer.Instance));

    private static long MinimumJsonLength(object? value, long limit, Dictionary<object?[], long> knownArrays)
    {
        switch (value)
        {
            case null:
                return 4;                                   // "null" (TypeCode::None)
            case bool b:
                return b ? 4 : 5;                           // "true" or "false"
            case int i:
                return i.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;     // %d (TypeCode::Int32)
            case uint u:
                return u.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;     // %u (TypeCode::Uint32)
            case float or double or long:
                return 1;                                   // ReportFloat writes "0", "null" or a %f format; a 64-bit value is stored in 56 bits
            case char:
                return 2;                                   // the quotes around EncodeChar
            case string s:
                return 2 + s.Length;                        // "%.s" in quotes escapes the text, which only adds characters, and UTF-8 has
                                                            // at least as many bytes as UTF-16 has code units
            case DateTime:
                return 4;                                   // ReportDateTime writes 21 characters, a zero time is "null"
            case DriverId:
                return 3;                                   // "%u" or "%u.%u" in quotes
            case object?[] array:
                // vector() fills its array with one reference (MetaExpressionParser, vector), so work out each array only once
                if (!knownArrays.TryGetValue(array, out long length))
                {
                    length = 2 + Math.Max(array.Length - 1, 0);     // brackets and commas
                    foreach (object? element in array)
                    {
                        length += MinimumJsonLength(element, limit, knownArrays);
                        if (length > limit)
                        {
                            break;                          // the caller only needs to know that the limit is exceeded
                        }
                    }
                    knownArrays[array] = length;
                }
                return length;
            default:
                return 0;
        }
    }
}
