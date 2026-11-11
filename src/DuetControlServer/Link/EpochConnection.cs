namespace DuetControlServer.Link;

/// <summary>
/// What DSF keeps about the channel epoch for one connection to RRF (design/sbc-link-v8-requirements.md, CE-4 and CE-13). LinkService creates a new
/// instance where a connection ends, at the same point where it resets the epochs of the channels, so both facts are forgotten with them.
/// Only the link thread uses it
/// </summary>
internal sealed class EpochConnection
{
    /// <summary>
    /// Whether the next packet DSF writes, whatever its type, is its first one of this connection and carries the first marker
    /// </summary>
    private bool _firstMarkerPending = true;

    /// <summary>
    /// Whether DSF's first packet of this connection has gone out with a transfer, and whether RRF has read it. RRF writes the data of a transfer
    /// before it reads what the SBC sends with it (SbcInterface::ExchangeData, on SPI and on USB), so the data that comes with the transfer of the
    /// first marker is still older than the marker
    /// </summary>
    private bool _firstMarkerTransferred, _firstMarkerRead;

    /// <summary>
    /// Whether RRF has written a tagged packet in this connection. Such an RRF records InvalidateChannel and never refuses it (CE-7), so DSF does not
    /// hold a channel until RRF has taken its InvalidateChannel (#161, CE-13)
    /// </summary>
    public bool PeerTags { get; private set; }

    /// <summary>
    /// Tag for a packet without a channel: the first marker alone on DSF's first packet of this connection, else 0. On it RRF sets every channel
    /// epoch to 0, which they are in DSF at that point (CE-4)
    /// </summary>
    /// <returns>Value of the header field</returns>
    public ushort Tag() => _firstMarkerPending ? ChannelEpoch.FirstMarker : (ushort)0;

    /// <summary>
    /// Tag for a packet of a channel, with the first marker if it is DSF's first packet of this connection
    /// </summary>
    /// <param name="epoch">Epoch of the channel</param>
    /// <returns>Tag</returns>
    public ushort Tag(byte epoch) => (ushort)(ChannelEpoch.Tag(epoch) | Tag());

    /// <summary>
    /// Tag for a packet whose tag was decided before, e.g. the result of a DoCode, which carries the tag of its DoCode (CE-6): the first marker is
    /// added if it is DSF's first packet of this connection
    /// </summary>
    /// <param name="tag">Tag of the packet without the first marker</param>
    /// <returns>Value of the header field</returns>
    public ushort Mark(ushort tag) => (ushort)(tag | Tag());

    /// <summary>
    /// Called when a packet has been written. A packet that could not be written did not carry the marker to RRF
    /// </summary>
    /// <param name="tag">Value of the header field of the written packet</param>
    public void Written(ushort tag)
    {
        if ((tag & ChannelEpoch.FirstMarker) != 0)
        {
            _firstMarkerPending = false;
        }
    }

    /// <summary>
    /// Called once after every full transfer, before the data of RRF that came with it is processed. What the adapter repeats to complete a transfer
    /// (a header or data that is exchanged again, a transfer that is sent or requested again, a packet that RRF asks for again) is part of that
    /// transfer or of a later one of this connection, so none of it starts the wait for the first marker again; only a new connection does
    /// </summary>
    public void Transferred()
    {
        _firstMarkerRead = _firstMarkerTransferred;
        _firstMarkerTransferred = !_firstMarkerPending;
    }

    /// <summary>
    /// Called for every packet from RRF except ResendPacket, whose header field is a packet ID. RRF tags only with an epoch that it knows, and it
    /// knows the epochs of this connection from the first marker on (CE-4). So a tagged packet that RRF wrote before it read the first marker carries
    /// the epoch of an older connection, which says nothing in this one, where the epochs start at 0 again: e.g. the reply of a code that DCS sent
    /// before it was restarted, which RRF wrote before it noticed the new connection. An untagged packet is processed as on v7
    /// </summary>
    /// <param name="tag">Header field of the packet</param>
    /// <returns>Whether the packet belongs to this connection, else it is dropped</returns>
    public bool Received(ushort tag)
    {
        if (ChannelEpoch.IsTagged(tag))
        {
            if (!_firstMarkerRead)
            {
                return false;
            }
            PeerTags = true;
        }
        return true;
    }
}
