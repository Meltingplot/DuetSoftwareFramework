namespace DuetControlServer.Link;

/// <summary>
/// What DSF keeps about the channel epoch for one connection to RRF (design/sbc-link-v8-requirements.md, CE-4 and CE-13). LinkService creates a new
/// instance where a connection ends, at the same point where it resets the epochs of the channels, so both facts are forgotten with them.
/// Only the link thread uses it
/// </summary>
internal sealed class EpochConnection
{
    /// <summary>
    /// Whether the next tagged packet is DSF's first one of this connection and carries the first marker
    /// </summary>
    private bool _firstMarkerPending = true;

    /// <summary>
    /// Whether RRF has written a tagged packet in this connection. Such an RRF records InvalidateChannel and never refuses it (CE-7), so DSF does not
    /// hold a channel until RRF has taken its InvalidateChannel (#161, CE-13)
    /// </summary>
    public bool PeerTags { get; private set; }

    /// <summary>
    /// Tag for a packet of a channel, with the first marker if no tagged packet was written in this connection yet
    /// </summary>
    /// <param name="epoch">Epoch of the channel</param>
    /// <returns>Tag</returns>
    public ushort Tag(byte epoch) => (ushort)(ChannelEpoch.Tag(epoch) | (_firstMarkerPending ? ChannelEpoch.FirstMarker : 0));

    /// <summary>
    /// Called when a tagged packet has been written. A packet that could not be written did not carry the marker to RRF
    /// </summary>
    /// <param name="tag">Tag of the written packet</param>
    public void Written(ushort tag)
    {
        if ((tag & ChannelEpoch.FirstMarker) != 0)
        {
            _firstMarkerPending = false;
        }
    }

    /// <summary>
    /// Called for every packet from RRF except ResendPacket, whose header field is a packet ID
    /// </summary>
    /// <param name="tag">Header field of the packet</param>
    public void Received(ushort tag)
    {
        if (ChannelEpoch.IsTagged(tag))
        {
            PeerTags = true;
        }
    }
}
