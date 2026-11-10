using System;
using System.IO;
using System.Linq;
using DuetAPI;
using DuetControlServer;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;
using SpiAdapter = DuetControlServer.Link.Adapter.SPI;

namespace UnitTests.SPI;

/// <summary>
/// The tag of the channel epoch in the real SPI adapter (design/sbc-link-v8-requirements.md CE-5, CE-8): it goes into the header field ResendPacketId
/// of a packet, and a copy that RRF asks for keeps it without the first marker. The protocol version and the payloads stay as they are
/// </summary>
[TestFixture]
public class SpiAdapterEpochTags
{
    private ServiceProvider _provider = null!;
    private string _baseDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _baseDirectory = Path.Combine(Path.GetTempPath(), "dsf-spi-epoch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_baseDirectory, "sys"));
        _provider = DcsServices.Build(new Settings { BaseDirectory = _baseDirectory });
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        Directory.Delete(_baseDirectory, true);
    }

    private SpiAdapter CreateAdapter(FakeRrfSpi rrf) => new(_provider.GetRequiredService<EventLogger>(), _provider.GetRequiredService<DuetControlServer.Model.ObjectModel>(),
        NullLogger<SpiAdapter>.Instance, Options.Create(new Settings { BaseDirectory = _baseDirectory }), rrf, rrf, rrf.Clock);

    private static readonly ushort FirstTag = (ushort)(ChannelEpoch.Tag(1) | ChannelEpoch.FirstMarker), Tag2 = ChannelEpoch.Tag(2);

    [Test]
    public void HeaderCarriesTheTag()
    {
        FakeRrfSpi rrf = new(_ => false) { RecordPackets = true };
        SpiAdapter dsf = CreateAdapter(rrf);
        dsf.PerformFullTransfer(true);

        Assert.That(dsf.WriteInvalidateChannel(CodeChannel.Trigger, FirstTag), Is.True);
        Assert.That(dsf.WriteUnlock(CodeChannel.SBC, Tag2), Is.True);
        Assert.That(dsf.WriteGetObjectModel("state", "v", 0), Is.True);
        dsf.PerformFullTransfer();

        FakeRrfSpi.ProcessedPacket[] packets = [.. rrf.GetProcessedPackets()];
        Assert.That(packets.Single(packet => packet.Request == SbcRequest.InvalidateChannel).Tag, Is.EqualTo(FirstTag));
        Assert.That(packets.Single(packet => packet.Request == SbcRequest.Unlock).Tag, Is.EqualTo(Tag2));
        Assert.That(packets.Single(packet => packet.Request == SbcRequest.GetObjectModel).Tag, Is.Zero, "A packet without a channel carries no tag (CE-6)");

        // The payload is the one of v7: the channel only (Writer.WriteCodeChannel), so a stock RRF reads the packets as before
        Assert.That(packets.Single(packet => packet.Request == SbcRequest.InvalidateChannel).Payload, Has.Length.EqualTo(4));
    }

    [Test]
    public void PacketWithoutAChannelCarriesTheMarkerAloneAndItsCopyNone()
    {
        // CE-4, CE-8: DSF's first packet of a connection carries the first marker whatever its type, in production a GetObjectModel. The header field
        // then holds the marker alone, and a copy that RRF asks for holds 0
        bool refuse = true;
        FakeRrfSpi rrf = new(_ => false)
        {
            RecordPackets = true,
            Respond = (request, id, _) => (request == SbcRequest.GetObjectModel && refuse) ? [(FirmwareRequest.ResendPacket, [], id)] : []
        };
        SpiAdapter dsf = CreateAdapter(rrf);
        dsf.PerformFullTransfer(true);
        Assert.That(dsf.WriteGetObjectModel("state", "v", ChannelEpoch.FirstMarker), Is.True);
        dsf.PerformFullTransfer();
        refuse = false;
        dsf.PerformFullTransfer();

        for (int i = 0; i < dsf.PacketsToRead && dsf.ReadNextPacket() is PacketHeader packet; i++)
        {
            if ((FirmwareRequest)packet.Request == FirmwareRequest.ResendPacket)
            {
                dsf.ResendPacket(packet, out _, out _);
            }
        }
        dsf.PerformFullTransfer();

        FakeRrfSpi.ProcessedPacket[] copies = [.. rrf.GetProcessedPackets().Where(packet => packet.Request == SbcRequest.GetObjectModel)];
        Assert.That(copies.Select(packet => packet.Tag), Is.EqualTo(new[] { ChannelEpoch.FirstMarker, (ushort)0 }));
        Assert.That(copies[1].Payload, Is.EqualTo(copies[0].Payload));
    }

    [Test]
    public void ResentCopyKeepsTheTagWithoutTheMarker()
    {
        bool refuse = true;
        FakeRrfSpi rrf = new(_ => false)
        {
            RecordPackets = true,
            Respond = (request, id, _) => (request == SbcRequest.InvalidateChannel && refuse) ? [(FirmwareRequest.ResendPacket, [], id)] : []
        };
        SpiAdapter dsf = CreateAdapter(rrf);
        dsf.PerformFullTransfer(true);
        Assert.That(dsf.WriteInvalidateChannel(CodeChannel.Trigger, FirstTag), Is.True);
        dsf.PerformFullTransfer();
        refuse = false;
        dsf.PerformFullTransfer();

        for (int i = 0; i < dsf.PacketsToRead && dsf.ReadNextPacket() is PacketHeader packet; i++)
        {
            if ((FirmwareRequest)packet.Request == FirmwareRequest.ResendPacket)
            {
                dsf.ResendPacket(packet, out _, out _);
            }
        }
        dsf.PerformFullTransfer();

        FakeRrfSpi.ProcessedPacket[] copies = [.. rrf.GetProcessedPackets().Where(packet => packet.Request == SbcRequest.InvalidateChannel)];
        Assert.That(copies.Select(packet => packet.Tag), Is.EqualTo(new[] { FirstTag, ChannelEpoch.Tag(1) }));
        Assert.That(copies[1].Payload, Is.EqualTo(copies[0].Payload));
    }
}
