namespace DuetControlServer.Link;

/// <summary>
/// Tag of the channel epoch, which both sides write into <see cref="Protocol.Shared.PacketHeader.ResendPacketId"/> of every packet of a channel's work
/// (design/sbc-link-v8-requirements.md, CE-5). A peer without the epoch writes 0 there and reads the field only in ResendPacket
/// (SPI.cs:831, USB.cs:729; RRF writes it only in WritePacketHeader, DataTransfer.cpp:2290-2303), so the tag needs no negotiation (FD-37)
/// </summary>
internal static class ChannelEpoch
{
    /// <summary>
    /// Bit 15: the packet is tagged. 0 means untagged, as in v7
    /// </summary>
    public const ushort Tagged = 0x8000;

    /// <summary>
    /// Bit 14, from DSF to RRF only: the first tagged packet DSF wrote in a connection. RRF forgets every channel epoch on it (CE-4)
    /// </summary>
    public const ushort FirstMarker = 0x4000;

    /// <summary>
    /// Bits 0-5: the epoch. It has 6 bits because RRF's reply path carries it in the free bits 18-23 of its MessageType (CE-11, FD-33)
    /// </summary>
    public const byte EpochMask = 0x3F;

    /// <summary>
    /// Tag of an epoch
    /// </summary>
    /// <param name="epoch">Epoch</param>
    /// <returns>Tag without the first marker</returns>
    public static ushort Tag(byte epoch) => (ushort)(Tagged | (epoch & EpochMask));

    /// <summary>
    /// Whether a header field carries a tag
    /// </summary>
    /// <param name="tag">Value of the header field</param>
    /// <returns>Whether bit 15 is set</returns>
    public static bool IsTagged(ushort tag) => (tag & Tagged) != 0;

    /// <summary>
    /// Epoch of a tag
    /// </summary>
    /// <param name="tag">Tag</param>
    /// <returns>Epoch</returns>
    public static byte EpochOf(ushort tag) => (byte)(tag & EpochMask);

    /// <summary>
    /// Epoch that follows an epoch
    /// </summary>
    /// <param name="epoch">Epoch</param>
    /// <returns>Next epoch</returns>
    public static byte Next(byte epoch) => (byte)((epoch + 1) & EpochMask);

    /// <summary>
    /// Whether an epoch follows another one as an RFC 1982 serial number with SERIAL_BITS = 6 (RFC 1982 section 3.2). The pair at distance 32 has
    /// no order there and counts as not following
    /// </summary>
    /// <param name="a">Epoch that may follow</param>
    /// <param name="b">Reference epoch</param>
    /// <returns>Whether a follows b</returns>
    public static bool Follows(byte a, byte b)
    {
        int distance = (a - b) & EpochMask;
        return distance is > 0 and < 32;
    }
}
