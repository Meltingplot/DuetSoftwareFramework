using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer.Link;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Channel;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using Host = UnitTests.SPI.LinkServiceOverSpi.Host;
using RrfPackets = UnitTests.SPI.LinkServiceOverSpi.RrfPackets;
using SbcPackets = UnitTests.SPI.LinkServiceOverSpi.SbcPackets;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;
using SpiAdapter = DuetControlServer.Link.Adapter.SPI;

namespace UnitTests.SPI;

/// <summary>
/// Tagged packets that RRF wrote before it read DSF's first marker of a connection (owner decision of 2026-11-10, EpochConnection.Received), with the
/// LinkService on the real SPI adapter. RRF knows the epochs of a connection from the first marker on (SbcInterface.cpp:334-357 at 78a997f74) and
/// sets up the data of a transfer before it reads what the SBC sends with it (:257-259, DataTransfer::StartNextTransfer), so such a packet is of
/// an older connection: DSF drops it. Lab finding of 2026-11-10: after 13 of 102 restarts of DCS while RRF ran, the transfer of the connect brought
/// the empty reply of a daemon.g code of the DCS before, which the new DCS logged as "Daemon: Out-of-order reply ''". What only repeats a
/// transfer of a running connection must never drop anything
/// </summary>
[TestFixture]
public class PacketsOfAnOlderConnection
{
    /// <summary>
    /// Number of bytes RRF has for buffered codes (SbcMessageFormats.h, SbcCodeBufferSize)
    /// </summary>
    private const int SbcCodeBufferSize = 4096;

    private static readonly ushort Epoch0 = ChannelEpoch.Tag(0);

    private static readonly byte[] EmptyDaemonReply = RrfPackets.Message(MessageTypeFlags.BinaryCodeReplyFlag | MessageTypeFlags.DaemonMessage, string.Empty);

    private static readonly byte[] EmptyTriggerReply = RrfPackets.Message(MessageTypeFlags.BinaryCodeReplyFlag | MessageTypeFlags.TriggerMessage, string.Empty);

    private static void WriteMacros(string baseDirectory)
    {
        System.IO.File.WriteAllText(Path.Combine(baseDirectory, "sys", "old.g"), "M400\n");
        System.IO.File.WriteAllText(Path.Combine(baseDirectory, "sys", "new.g"), "G4 S0\n");
    }

    private static int OutOfOrderReplies(Host host, CodeChannel channel)
    {
        Processor processor = host.Get<Manager>()[channel];
        using (processor.Lock())
        {
            return processor.OutOfOrderReplies;
        }
    }

    /// <summary>
    /// RRF asks for a macro on the Trigger channel with the given tag, DSF runs its one code and reports the end of the file, and RRF closes it
    /// (SbcInterface.cpp:1663-1667 at 78a997f74, DataTransfer::WriteMacroFileClosed). Returns once DSF has taken the closal, so that no transfer
    /// with data of this macro is still on its way
    /// </summary>
    private static void RunMacro(Host host, string fileName, (char Letter, int Major) code, ushort tag)
    {
        host.Rrf.SendPacket(FirmwareRequest.ExecuteMacro, RrfPackets.ExecuteMacro(CodeChannel.Trigger, false, fileName), tag);
        Assert.That(SbcPackets.Channel(host.WaitFor(SbcRequest.MacroStarted)), Is.EqualTo(CodeChannel.Trigger));
        Assert.That(SbcPackets.Code(host.WaitFor(SbcRequest.Code)), Is.EqualTo((CodeChannel.Trigger, code.Letter, code.Major)));
        host.Rrf.SendPacket(FirmwareRequest.Message, EmptyTriggerReply, tag);
        Assert.That(SbcPackets.MacroCompleted(host.WaitFor(SbcRequest.MacroCompleted)), Is.EqualTo((CodeChannel.Trigger, false)));
        host.Rrf.SendPacket(FirmwareRequest.MacroFileClosed, RrfPackets.Channel(CodeChannel.Trigger), tag);
        Processor processor = host.Get<Manager>()[CodeChannel.Trigger];
        host.WaitUntil(() =>
        {
            using (processor.Lock())
            {
                return processor.Stack.Count == 1;
            }
        }, "DSF to take the closal of the macro file");
    }

    /// <summary>
    /// DCS starts while RRF runs and has not noticed the end of the DCS before. RRF's data of the first transfer was written for the old
    /// connection, here the reply of a daemon.g code and a macro request, both with epoch 0 of that connection. In the new connection the
    /// epochs start at 0 again, so the tags alone look current. The same packets one transfer later come with DSF's first marker, which RRF reads
    /// only after it has sent them. DSF drops them in both cases: no out-of-order reply, old.g does not run. Once RRF has read the marker, its
    /// tagged packets belong to the connection: new.g runs
    /// </summary>
    /// <param name="withTheConnect">Whether the packets come with the transfer of the connect, else with the transfer of the first marker</param>
    [TestCase(true)]
    [TestCase(false)]
    public async Task TaggedPacketsOfAnOlderConnectionAreDropped(bool withTheConnect)
    {
        (FirmwareRequest Request, byte[] Payload, ushort Tag)[] oldPackets =
        [
            (FirmwareRequest.CodeBufferUpdate, RrfPackets.CodeBufferUpdate(SbcCodeBufferSize), 0),
            (FirmwareRequest.Message, EmptyDaemonReply, Epoch0),
            (FirmwareRequest.ExecuteMacro, RrfPackets.ExecuteMacro(CodeChannel.Trigger, false, "old.g"), Epoch0)
        ];
        await using Host host = await Host.StartAsync(firstPackets: withTheConnect ? oldPackets : null, configure: (rrf, baseDirectory) =>
        {
            WriteMacros(baseDirectory);
            if (!withTheConnect)
            {
                foreach ((FirmwareRequest request, byte[] payload, ushort tag) in oldPackets)
                {
                    rrf.SendPacket(request, payload, tag);
                }
            }
        });

        // StartAsync has waited for the query of the link capabilities, which is DSF's first packet and carries the marker
        FakeRrfSpi.ProcessedPacket marker = host.Rrf.GetProcessedPackets().First();
        Assert.That((marker.Request, marker.Tag), Is.EqualTo((SbcRequest.GetObjectModel, ChannelEpoch.FirstMarker)));
        host.WaitUntil(() => host.Rrf.AllPacketsSent, "the packets of the old connection");

        RunMacro(host, "new.g", ('G', 4), Epoch0);
        List<FakeRrfSpi.ProcessedPacket> packets = host.Rrf.GetProcessedPackets();
        Assert.That(packets.Count(packet => packet.Request == SbcRequest.MacroStarted), Is.EqualTo(1), "DSF ran the macro of the old connection");
        Assert.That(packets.Where(packet => packet.Request == SbcRequest.Code).Select(packet => SbcPackets.Code(packet.Payload)), Is.EqualTo(new[] { (CodeChannel.Trigger, 'G', 4) }));
        Assert.That(OutOfOrderReplies(host, CodeChannel.Daemon), Is.Zero, "DSF took the reply of the old connection for one of the new connection");
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// An RRF without the epoch writes no tag. What it sends with the connect is processed as before, here a macro request: such an RRF may still
    /// run the connection, and without the first marker nothing tells it otherwise
    /// </summary>
    [Test]
    public async Task UntaggedPacketsThatComeWithTheConnectAreProcessed()
    {
        await using Host host = await Host.StartAsync(configure: (_, baseDirectory) => WriteMacros(baseDirectory), firstPackets:
        [
            (FirmwareRequest.CodeBufferUpdate, RrfPackets.CodeBufferUpdate(SbcCodeBufferSize), 0),
            (FirmwareRequest.ExecuteMacro, RrfPackets.ExecuteMacro(CodeChannel.Trigger, false, "old.g"), 0)
        ]);
        Assert.That(SbcPackets.Channel(host.WaitFor(SbcRequest.MacroStarted)), Is.EqualTo(CodeChannel.Trigger));
        Assert.That(SbcPackets.Code(host.WaitFor(SbcRequest.Code)), Is.EqualTo((CodeChannel.Trigger, 'M', 400)));
        host.Rrf.SendPacket(FirmwareRequest.Message, EmptyTriggerReply);
        Assert.That(SbcPackets.MacroCompleted(host.WaitFor(SbcRequest.MacroCompleted)), Is.EqualTo((CodeChannel.Trigger, false)));
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// What repeats a transfer of a running connection starts nothing again: a header or data that is exchanged again, a transfer that RRF is
    /// asked for again or that DSF sends again. The fault hits the transfer that carries a tagged macro request of RRF (the only transfer with data
    /// at that time), the link repairs it without a new connection, and DSF runs the macro. A bad response to the data on the plain link is not
    /// among them: it ends the connection (link census, LinkRecovery)
    /// </summary>
    /// <param name="fault">Single fault, see <see cref="SpiFaultSpec.Parse"/></param>
    /// <param name="extended">Whether the link is extended</param>
    /// <param name="counter">Recovery counter of DSF that goes up</param>
    [TestCase("RrfData Header ToDsf flip 96", false, "header retries")]
    [TestCase("RrfData Data ToDsf flip 128", false, "data retries")]
    [TestCase("RrfData Data ToDsf flip 128", true, "data retries")]
    [TestCase("RrfData DataResponse ToDsf word 0xCCCCCCCC", true, "transfers requested again")]
    [TestCase("RrfData DataResponse ToRrf word 0xCCCCCCCC", true, "Transfers sent again")]
    public async Task RepeatedTransferOfARunningConnectionDropsNothing(string fault, bool extended, string counter)
    {
        FaultInjectingSpiDevice? injector = null;
        int hits = 0;
        await using Host host = await Host.StartAsync(extended ? LinkCapabilities.All : LinkCapabilities.None, configure: (_, baseDirectory) => WriteMacros(baseDirectory),
            createAdapter: (services, rrf, settings) => new SpiAdapter(services.GetRequiredService<EventLogger>(), services.GetRequiredService<DuetControlServer.Model.ObjectModel>(),
                NullLogger<SpiAdapter>.Instance, settings, rrf, injector = new FaultInjectingSpiDevice(rrf, null, NullLogger.Instance), rrf.Clock));
        injector!.Injected += (_, _) => hits++;
        if (extended)
        {
            host.WaitUntil(() => host.Rrf.DsfUsesExtendedLink, "the extended link");
        }

        // The connection runs: RRF has read the first marker and tags
        host.Rrf.SendPacket(FirmwareRequest.CodeBufferUpdate, RrfPackets.CodeBufferUpdate(SbcCodeBufferSize));
        RunMacro(host, "old.g", ('M', 400), Epoch0);

        injector.Arm(SpiFaultSpec.Parse(fault));
        RunMacro(host, "new.g", ('G', 4), Epoch0);
        Assert.That(hits, Is.EqualTo(1), "The fault was not injected");

        StringBuilder diagnostics = new();
        host.Adapter.PrintDiagnostics(diagnostics);
        int Counter(string name) => int.Parse(Regex.Match(diagnostics.ToString(), $@"{name}: (\d+)").Groups[1].Value);
        Assert.That(Counter(counter), Is.GreaterThan(0), diagnostics.ToString());
        Assert.That((Counter("lost connections"), Counter("connection resets")), Is.EqualTo((0, 0)), "The fault ended the connection: " + diagnostics);
        Assert.That(host.Rrf.GetProcessedPackets().Count(packet => packet.Request == SbcRequest.MacroStarted), Is.EqualTo(2));
        Assert.That(OutOfOrderReplies(host, CodeChannel.Trigger), Is.Zero);
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// A packet that RRF asks for again (ResendPacket) belongs to the running connection as well: the tagged macro request that comes in the
    /// same transfer as the ResendPacket is served
    /// </summary>
    [Test]
    public async Task ResentPacketOfARunningConnectionDropsNothing()
    {
        int resends = 0;
        await using Host host = await Host.StartAsync(configure: (_, baseDirectory) => WriteMacros(baseDirectory), respond: (request, id, _) =>
            (request == SbcRequest.Message && System.Threading.Interlocked.Increment(ref resends) == 1)
                ? [(FirmwareRequest.ResendPacket, [], id), (FirmwareRequest.ExecuteMacro, RrfPackets.ExecuteMacro(CodeChannel.Trigger, false, "new.g"), Epoch0)]
                : []);
        host.Rrf.SendPacket(FirmwareRequest.CodeBufferUpdate, RrfPackets.CodeBufferUpdate(SbcCodeBufferSize));
        RunMacro(host, "old.g", ('M', 400), Epoch0);

        host.Link.SendMessage(MessageTypeFlags.UsbMessage, "twice");
        Assert.That(SbcPackets.Channel(host.WaitFor(SbcRequest.MacroStarted)), Is.EqualTo(CodeChannel.Trigger));
        Assert.That(SbcPackets.Code(host.WaitFor(SbcRequest.Code)), Is.EqualTo((CodeChannel.Trigger, 'G', 4)));
        host.Rrf.SendPacket(FirmwareRequest.Message, EmptyTriggerReply, Epoch0);
        Assert.That(SbcPackets.MacroCompleted(host.WaitFor(SbcRequest.MacroCompleted)), Is.EqualTo((CodeChannel.Trigger, false)));
        host.Rrf.SendPacket(FirmwareRequest.MacroFileClosed, RrfPackets.Channel(CodeChannel.Trigger), Epoch0);
        Assert.That(host.Rrf.GetProcessedPackets().Count(packet => packet.Request == SbcRequest.Message && SbcPackets.Message(packet.Payload).Text == "twice"), Is.EqualTo(2));
        Assert.That(host.Fault, Is.Null);
    }
}
