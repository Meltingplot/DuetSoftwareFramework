using System;
using System.Numerics;

namespace DuetControlServer.Link.Protocol.Shared;

/// <summary>
/// Capabilities of the SPI link that RRF offers in limits.sbcLinkCapabilities and DSF activates for the current connection
/// </summary>
/// <remarks>
/// Not part of upstream RRF. A connection reset or a restart of either side deactivates them
/// </remarks>
[Flags]
public enum LinkCapabilities : uint
{
    /// <summary>
    /// No capabilities
    /// </summary>
    None = 0,

    /// <summary>
    /// RRF goes back to the phase of DSF when what it receives shows that DSF is in another phase
    /// </summary>
    PhaseGuard = 1,

    /// <summary>
    /// RRF sends its last completed transfer again when DSF repeats its part of it
    /// </summary>
    SequenceGuard = 2,

    /// <summary>
    /// Both sides send sequence numbers with a session status and response codes that tolerate bit errors, see <see cref="ExtendedLink"/>
    /// </summary>
    ExtendedHeader = 4,

    /// <summary>
    /// Every capability DSF supports
    /// </summary>
    All = PhaseGuard | SequenceGuard | ExtendedHeader
}

/// <summary>
/// Sequence numbers and response codes of the extended link (<see cref="LinkCapabilities.ExtendedHeader"/>)
/// </summary>
/// <remarks>
/// Both sides start with regular 16-bit sequence numbers. A side switches to extended ones with the transfer after the one in which it knows
/// that the other side has activated them: RRF after processing the activation request, DSF after receiving the first extended header.
/// Until then each side accepts both formats. A side that leaves the extended link numbers from the counter on, so its status bits are 0 again,
/// which tells the other side that it restarted or reset the connection
/// </remarks>
public static class ExtendedLink
{
    /// <summary>
    /// Number of bits of the counter in an extended sequence number
    /// </summary>
    public const int CounterBits = 5;

    /// <summary>
    /// Mask of the counter in an extended sequence number
    /// </summary>
    public const ushort CounterMask = (1 << CounterBits) - 1;

    /// <summary>
    /// Status in the upper bits of every extended sequence number, 11 bits away from the 0 of a side that has left the extended link
    /// </summary>
    public const ushort ActiveStatus = 0x7FF;

    /// <summary>
    /// Highest regular sequence number of either side at which DSF may request the extended link. Regular numbers from 0xFFE0 on look like
    /// extended ones, so this leaves 224 transfers for the switch
    /// </summary>
    public const ushort LastSafeRegularNumber = 0xFF00;

    /// <summary>
    /// Check if a sequence number is an extended one
    /// </summary>
    /// <param name="sequenceNumber">Sequence number</param>
    /// <returns>Whether it has the status of the extended link</returns>
    public static bool IsExtended(ushort sequenceNumber) => (sequenceNumber >> CounterBits) == ActiveStatus;

    /// <summary>
    /// Get the sequence number of the next transfer
    /// </summary>
    /// <param name="sequenceNumber">Sequence number of the last transfer</param>
    /// <param name="extended">Whether the next transfer gets an extended sequence number</param>
    /// <returns>Next sequence number</returns>
    public static ushort Next(ushort sequenceNumber, bool extended) =>
        extended ? (ushort)((ActiveStatus << CounterBits) | ((sequenceNumber + 1) & CounterMask)) : (ushort)(sequenceNumber + 1);

    /// <summary>
    /// Get the sequence number of the transfer before, in the same format
    /// </summary>
    /// <param name="sequenceNumber">Sequence number</param>
    /// <returns>Previous sequence number</returns>
    public static ushort Previous(ushort sequenceNumber) =>
        IsExtended(sequenceNumber) ? (ushort)((ActiveStatus << CounterBits) | ((sequenceNumber - 1) & CounterMask)) : (ushort)(sequenceNumber - 1);

    /// <summary>
    /// Get the distance from the sequence number of the last transfer to a received one
    /// </summary>
    /// <param name="last">Sequence number of the last transfer</param>
    /// <param name="received">Received sequence number</param>
    /// <param name="extendedRequested">Whether the extended link has been requested, so that extended sequence numbers may arrive</param>
    /// <returns>1 for the next transfer, 0 for a repeated one, 2 if one is missing, -1 if the other side left the extended link</returns>
    public static int Distance(ushort last, ushort received, bool extendedRequested)
    {
        if (extendedRequested)
        {
            if (IsExtended(received))
            {
                return (received - last) & CounterMask;
            }
            if (IsExtended(last))
            {
                return -1;
            }
        }
        return (ushort)(received - last);
    }

    // Response codes are Walsh codes. They are 16 bits apart from each other and at least 10 bits apart from the regular codes,
    // from 0 and 0xFFFFFFFF, from the first word of a header and from the first word of a packet header. Their low bytes are
    // at least 4 bits apart from the format code, so that a corrected code is never taken for the start of a header

    /// <summary>
    /// Extended code of <see cref="TransferResponse.Success"/>
    /// </summary>
    public const uint Success = 0xFF00FF00;

    /// <summary>
    /// Extended code of <see cref="TransferResponse.BadHeaderChecksum"/>
    /// </summary>
    public const uint BadHeaderChecksum = 0xF0F0F0F0;

    /// <summary>
    /// Extended code of <see cref="TransferResponse.BadDataChecksum"/>
    /// </summary>
    public const uint BadDataChecksum = 0xF00F0FF0;

    /// <summary>
    /// Extended code of <see cref="TransferResponse.BadResponse"/>
    /// </summary>
    public const uint BadResponse = 0xCCCCCCCC;

    /// <summary>
    /// Extended code of <see cref="TransferResponse.BadFormat"/>
    /// </summary>
    public const uint BadFormat = 0xCC3333CC;

    /// <summary>
    /// Extended code of <see cref="TransferResponse.BadProtocolVersion"/>
    /// </summary>
    public const uint BadProtocolVersion = 0xC3C3C3C3;

    /// <summary>
    /// Extended code of <see cref="TransferResponse.BadDataLength"/>
    /// </summary>
    public const uint BadDataLength = 0xC33C3CC3;

    /// <summary>
    /// Number of bit errors in an extended response code that are corrected
    /// </summary>
    public const int CorrectableBits = 3;

    private static readonly (uint Regular, uint Extended)[] ResponseCodes =
    [
        (TransferResponse.Success, Success), (TransferResponse.BadHeaderChecksum, BadHeaderChecksum), (TransferResponse.BadDataChecksum, BadDataChecksum),
        (TransferResponse.BadResponse, BadResponse), (TransferResponse.BadFormat, BadFormat), (TransferResponse.BadProtocolVersion, BadProtocolVersion),
        (TransferResponse.BadDataLength, BadDataLength)
    ];

    /// <summary>
    /// Get the extended code of a response
    /// </summary>
    /// <param name="response">Regular response code</param>
    /// <returns>Extended response code</returns>
    public static uint EncodeResponse(uint response)
    {
        foreach ((uint regular, uint extended) in ResponseCodes)
        {
            if (regular == response)
            {
                return extended;
            }
        }
        return response;
    }

    /// <summary>
    /// Get the regular code of a received response, which may be an extended one with up to <see cref="CorrectableBits"/> bit errors
    /// </summary>
    /// <param name="word">Received word</param>
    /// <returns>Regular response code, or the word itself if it is no extended code</returns>
    public static uint DecodeResponse(uint word)
    {
        foreach ((uint regular, uint extended) in ResponseCodes)
        {
            if (BitOperations.PopCount(word ^ extended) <= CorrectableBits)
            {
                return regular;
            }
        }
        return word;
    }
}
