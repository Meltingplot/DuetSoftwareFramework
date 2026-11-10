namespace UnitTests.FakeFirmware;

/// <summary>
/// RRF that the fake firmware models, see the milestones of design/sbc-link-v8-requirements.md (draft 8, section 7.2)
/// </summary>
public enum FakeRrfModel
{
    /// <summary>
    /// Fork RRF 0dccb9efe (v7 with RRF #43, #45 and #46): the SBC task carries InvalidateChannel out under the buffer mutex and refuses it while
    /// the main task holds that mutex (SbcInterface.cpp:868-925). It writes no tags and ignores those of DSF. This is the "stock RRF" of the tests
    /// </summary>
    V7,

    /// <summary>
    /// Milestone E1: the SBC task records InvalidateChannel and never refuses it, the main task applies it at the top of its next spin with the
    /// per-channel routine that a link loss runs as well (CE-7, CE-12, CE-25). It writes no tags and ignores those of DSF, so it is also the RRF of
    /// milestone E2 towards a DSF whose tags it does not see (a stock DSF, CE-18)
    /// </summary>
    RecordApply,

    /// <summary>
    /// Milestone E2: <see cref="RecordApply"/> plus the channel epoch: the adopt rule and the first marker (CE-4), the tags of its packets (CE-6, CE-9,
    /// CE-11, CE-12), the filter of stale DSF packets (CE-10), the receipt of InvalidateChannel by its tag (CE-7) and the abort gate (CE-24)
    /// </summary>
    Epoch
}

/// <summary>
/// Tag of the channel epoch as RRF writes and reads it in PacketHeader.resendPacketId (design/sbc-link-v8-requirements.md CE-5), and the bits of
/// RRF's MessageType that carry the epoch of a code reply until it is written (CE-11). Kept apart from DSF's own constants, so that a test of the fake
/// against DSF also checks the format
/// </summary>
internal static class FakeTag
{
    /// <summary>Bit 15: the packet is tagged</summary>
    public const ushort Tagged = 0x8000;

    /// <summary>Bit 14, DSF to RRF only: first tagged packet of a connection</summary>
    public const ushort FirstMarker = 0x4000;

    /// <summary>Bits 0-5: the epoch</summary>
    public const int EpochMask = 0x3F;

    /// <summary>MessageType bits 18-23 carry the epoch of a code reply (MessageType.h: destinations end at 0x3FFFF, :39)</summary>
    public const int TypeEpochShift = 18;

    /// <summary>MessageType bit 26 says that bits 18-23 hold an epoch (bit 26 is unused, MessageType.h:43-50)</summary>
    public const uint TypeEpochValid = 1u << 26;

    /// <summary>All bits of the epoch in a MessageType</summary>
    public const uint TypeEpochBits = ((uint)EpochMask << TypeEpochShift) | TypeEpochValid;

    /// <summary>Tag of an epoch</summary>
    public static ushort Of(int epoch) => (ushort)(Tagged | (epoch & EpochMask));

    /// <summary>Whether a header field carries a tag</summary>
    public static bool IsTagged(ushort tag) => (tag & Tagged) != 0;

    /// <summary>Epoch of a tag</summary>
    public static byte EpochOf(ushort tag) => (byte)(tag & EpochMask);

    /// <summary>Whether epoch a follows epoch b as an RFC 1982 serial number with SERIAL_BITS = 6. Distance 32 has no order and counts as not following</summary>
    public static bool Follows(int a, int b)
    {
        int distance = (a - b) & EpochMask;
        return distance is > 0 and < 32;
    }
}
