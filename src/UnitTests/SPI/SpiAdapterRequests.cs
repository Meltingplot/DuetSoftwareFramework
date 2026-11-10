using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI;
using CodeType = DuetAPI.Commands.CodeType;
using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Commands;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;
using SpiAdapter = DuetControlServer.Link.Adapter.SPI;

namespace UnitTests.SPI;

/// <summary>
/// The write functions of the SPI adapter against the SPI port of RRF (<see cref="FakeRrfSpi"/>): what DSF refuses to write and why,
/// packets RRF asks for again, the diagnostics and the switch to the extended link
/// </summary>
[TestFixture]
public class SpiAdapterRequests
{
    private ServiceProvider _provider = null!;
    private string _baseDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _baseDirectory = Path.Combine(Path.GetTempPath(), "dsf-spi-adapter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_baseDirectory, "sys"));
        _provider = DcsServices.Build(new Settings { BaseDirectory = _baseDirectory });
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        Directory.Delete(_baseDirectory, true);
    }

    /// <summary>
    /// Logger that keeps the messages of DSF
    /// </summary>
    private sealed class ListLogger : ILogger<SpiAdapter>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private SpiAdapter CreateAdapter(FakeRrfSpi rrf, ListLogger? logger = null, Action<Settings>? configure = null)
    {
        Settings settings = new() { BaseDirectory = _baseDirectory };
        configure?.Invoke(settings);
        return new(_provider.GetRequiredService<EventLogger>(), _provider.GetRequiredService<DuetControlServer.Model.ObjectModel>(),
            (ILogger<SpiAdapter>?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SpiAdapter>.Instance, Options.Create(settings), rrf, rrf, rrf.Clock);
    }

    private static FakeRrfSpi CreateRrf(ushort firstSequenceNumber = 1) => new(_ => false, firstSequenceNumber: firstSequenceNumber) { RecordPackets = true };

    /// <summary>
    /// Read the packets of the last transfer
    /// </summary>
    private static List<PacketHeader> ReadPackets(SpiAdapter dsf)
    {
        List<PacketHeader> packets = [];
        for (int i = 0; i < dsf.PacketsToRead && dsf.ReadNextPacket() is PacketHeader packet; i++)
        {
            packets.Add(packet);
        }
        return packets;
    }

    private Code CreateCode()
    {
        Code code = _provider.GetRequiredService<CommandFactory>().Create<Code>();
        code.Channel = CodeChannel.SBC;
        code.Type = CodeType.GCode;
        code.MajorNumber = 4;
        return code;
    }

    /// <summary>
    /// Write one request of every kind that DSF does not send twice in a transfer, and return whether each could be written
    /// </summary>
    private Dictionary<SbcRequest, bool> WriteEveryRequest(SpiAdapter dsf) => new()
    {
        [SbcRequest.Code] = dsf.WriteCode(CreateCode(), 0),
        [SbcRequest.GetObjectModel] = dsf.WriteGetObjectModel("state", "v"),
        [SbcRequest.SetPrintFileInfo] = dsf.WritePrintFileInfo(new GCodeFileInfo { FileName = "0:/gcodes/print.gcode" }),
        [SbcRequest.PrintStopped] = dsf.WritePrintStopped(PrintStoppedReason.UserCancelled),
        [SbcRequest.MacroCompleted] = dsf.WriteMacroCompleted(CodeChannel.Trigger, false, 0),
        [SbcRequest.Unlock] = dsf.WriteUnlock(CodeChannel.SBC, 0),
        [SbcRequest.FileChunk] = dsf.WriteFileChunk([1, 2, 3], 3),
        [SbcRequest.EvaluateExpression] = dsf.WriteEvaluateExpression(CodeChannel.SBC, "1+1", 0),
        [SbcRequest.Message] = dsf.WriteMessage(MessageTypeFlags.UsbMessage, "hi", 0),
        [SbcRequest.MacroStarted] = dsf.WriteMacroStarted(CodeChannel.Trigger, 0),
        [SbcRequest.InvalidateChannel] = dsf.WriteInvalidateChannel(CodeChannel.Trigger, 0),
        [SbcRequest.SetVariable] = dsf.WriteSetVariable(CodeChannel.SBC, true, "global.x", "1", 0),
        [SbcRequest.DeleteLocalVariable] = dsf.WriteDeleteLocalVariable(CodeChannel.SBC, "x", 0),
        [SbcRequest.CheckFileExistsResult] = dsf.WriteCheckFileExistsResult(true),
        [SbcRequest.FileDeleteResult] = dsf.WriteFileDeleteResult(true),
        [SbcRequest.FileListResult] = dsf.WriteFileListResult([], true),
        [SbcRequest.OpenFileResult] = dsf.WriteOpenFileResult(1, 10),
        [SbcRequest.FileReadResult] = dsf.WriteFileReadResult([1, 2], 2),
        [SbcRequest.FileWriteResult] = dsf.WriteFileWriteResult(true),
        [SbcRequest.FileSeekResult] = dsf.WriteFileSeekResult(true),
        [SbcRequest.FileTruncateResult] = dsf.WriteFileTruncateResult(true)
    };

    /// <summary>
    /// The data of a transfer is limited to the transfer buffer of RRF (SbcTransferBufferSize in SbcMessageFormats.h, Settings.SbcBufferSize),
    /// so once it is full DSF refuses every further request (SPI.CanWritePacket), the emergency stop and the reset as well, until the next transfer
    /// </summary>
    [Test]
    public void FullBufferRefusesEveryRequest()
    {
        FakeRrfSpi rrf = CreateRrf();
        SpiAdapter dsf = CreateAdapter(rrf);
        dsf.PerformFullTransfer(true);

        // A message packet takes the packet header, the message header and the text: 8 + 8 + 8172 bytes leave 4 bytes, too few for any packet
        string text = new('x', Consts.BufferSize - 2 * Marshal.SizeOf<PacketHeader>() - 4);
        Assert.That(dsf.WriteMessage(MessageTypeFlags.UsbMessage, text, 0), Is.True);

        Assert.Multiple(() =>
        {
            foreach ((SbcRequest request, bool written) in WriteEveryRequest(dsf))
            {
                Assert.That(written, Is.False, request.ToString());
            }
            Assert.That(dsf.WriteEmergencyStop(), Is.False, "EmergencyStop");
            Assert.That(dsf.WriteReset(), Is.False, "Reset");
            Assert.That(dsf.WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel.SBC, 0), Is.False, "LockAllMovementSystemsAndWaitForStandstill");
            Assert.That(dsf.WriteActivateLinkCapabilities(LinkCapabilities.All), Is.False, "ActivateLinkCapabilities");
            Assert.That(dsf.WriteSetLastCodeResult(CodeChannel.SBC, CodeResult.Ok, 0), Is.False, "SetLastCodeResult");
            Assert.That(dsf.WriteObjectModelKeyChanged("network"), Is.False, "ObjectModelKeyChanged");
        });

        // RRF gets the message in one piece (DataTransfer::ReadMessage)
        dsf.PerformFullTransfer();
        List<(SbcRequest Request, ushort Id, byte[] Payload)> packets = rrf.GetReceivedPackets();
        Assert.That(packets.Select(p => p.Request), Is.EqualTo(new[] { SbcRequest.Message }));
        Assert.That(Encoding.UTF8.GetString(packets[0].Payload, 8, BitConverter.ToUInt16(packets[0].Payload, 4)), Is.EqualTo(text));

        // The next transfer has room again
        Assert.That(dsf.WriteEmergencyStop(), Is.True);
    }

    /// <summary>
    /// RRF asks for a packet again when it could not take it (FirmwareRequest.ResendPacket with the id in PacketHeader.resendPacketId). DSF copies the
    /// packet from the transfer RRF read it in into the next one with a new id (SPI.ResendPacket), and refuses to write another request of the same kind
    /// meanwhile, so that the requests stay in order. Movement locks are expected to be asked for again and are left out
    /// </summary>
    [Test]
    public void ResentPacketsKeepTheirOrder()
    {
        // RRF asks for every packet of the first transfer with data again, in the transfer after it
        bool resend = true;
        FakeRrfSpi rrf = new(_ => false)
        {
            RecordPackets = true,
            Respond = (_, id, _) => resend ? [(FirmwareRequest.ResendPacket, [], id)] : []
        };
        SpiAdapter dsf = CreateAdapter(rrf);
        dsf.PerformFullTransfer(true);

        Dictionary<SbcRequest, bool> written = WriteEveryRequest(dsf);
        Assert.That(written.Values, Is.All.True);
        Assert.That(dsf.WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel.SBC, 0), Is.True);
        dsf.PerformFullTransfer();
        resend = false;
        List<(SbcRequest Request, ushort Id, byte[] Payload)> sent = rrf.GetReceivedPackets();
        Assert.That(sent, Has.Count.EqualTo(written.Count + 1));

        dsf.PerformFullTransfer();
        List<PacketHeader> requests = ReadPackets(dsf);
        Assert.That(requests, Has.Count.EqualTo(sent.Count));
        List<SbcRequest> resent = [];
        foreach (PacketHeader request in requests)
        {
            Assert.That((FirmwareRequest)request.Request, Is.EqualTo(FirmwareRequest.ResendPacket));
            dsf.ResendPacket(request, out SbcRequest kind, out CodeChannel? invalidatedChannel);
            resent.Add(kind);

            // LinkService reopens the abort window of that channel (Processor.AllFilesAbortResent)
            Assert.That(invalidatedChannel, Is.EqualTo((kind == SbcRequest.InvalidateChannel) ? CodeChannel.Trigger : null), kind.ToString());
        }
        Assert.That(resent, Is.EqualTo(sent.Select(p => p.Request)));

        // Until the resent packets have gone out, DSF writes no other request of the same kinds
        Assert.Multiple(() =>
        {
            foreach ((SbcRequest request, bool again) in WriteEveryRequest(dsf))
            {
                Assert.That(again, Is.False, request.ToString());
            }
        });
        Assert.That(dsf.WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel.SBC, 0), Is.True);

        // RRF gets the same packets again, with new ids
        int before = rrf.GetReceivedPackets().Count;
        dsf.PerformFullTransfer();
        List<(SbcRequest Request, ushort Id, byte[] Payload)> again = rrf.GetReceivedPackets()[before..];
        Assert.That(again.Select(p => p.Request), Is.EqualTo(sent.Select(p => p.Request).Append(SbcRequest.LockAllMovementSystemsAndWaitForStandstill)));
        for (int i = 0; i < sent.Count; i++)
        {
            Assert.That(again[i].Payload, Is.EqualTo(sent[i].Payload), sent[i].Request.ToString());
        }

        // Afterwards every kind can be written again
        Assert.That(WriteEveryRequest(dsf).Values, Is.All.True);
    }

    /// <summary>
    /// M122 of DSF reports the transfers and codes per second and the longest data in each direction (SPI.PrintDiagnostics), and nothing when DCS
    /// uses another link
    /// </summary>
    [Test]
    public void Diagnostics()
    {
        FakeRrfSpi rrf = CreateRrf();
        SpiAdapter dsf = CreateAdapter(rrf);
        dsf.PerformFullTransfer(true);
        Assert.That(dsf.WriteCode(CreateCode(), 0), Is.True);
        dsf.PerformFullTransfer();

        StringBuilder diagnostics = new();
        dsf.PrintDiagnostics(diagnostics);
        Assert.That(diagnostics.ToString(), Does.Match(@"Codes per second: [1-9]"));
        Assert.That(diagnostics.ToString(), Does.Match(@"Full transfers per second: [1-9]"));
        Assert.That(diagnostics.ToString(), Does.Contain("Maximum length of RX/TX data transfers: 0/"));

        // The counters start again
        diagnostics.Clear();
        dsf.PrintDiagnostics(diagnostics);
        Assert.That(diagnostics.ToString(), Does.Contain("Codes per second: 0.00"));

        StringBuilder usb = new();
        CreateAdapter(CreateRrf(), configure: settings => settings.CommunicationMethod = CommunicationMethod.USB).PrintDiagnostics(usb);
        Assert.That(usb.ToString(), Is.Empty);
    }

    /// <summary>
    /// DSF uses the extended link only after RRF has sent an extended sequence number. When RRF does not switch, for instance because it does
    /// not offer the capability, DSF gives up after 16 transfers (SPI.MaxExtendedSwitchTransfers) and stays on the regular link
    /// </summary>
    [Test]
    public void RrfThatDoesNotSwitchToTheExtendedLink()
    {
        FakeRrfSpi rrf = CreateRrf();
        ListLogger logger = new();
        SpiAdapter dsf = CreateAdapter(rrf, logger);
        dsf.PerformFullTransfer(true);
        Assert.That(dsf.WriteActivateLinkCapabilities(LinkCapabilities.All), Is.True);
        for (int i = 0; i < 17; i++)
        {
            dsf.PerformFullTransfer();
            Assert.That(dsf.HadReset(), Is.False);
        }
        Assert.That(logger.Lines, Has.One.EqualTo("Warning: RepRapFirmware did not switch to the extended link"));
        Assert.That(rrf.ActiveCapabilities, Is.EqualTo(LinkCapabilities.None));
        Assert.That(rrf.ConnectionResets, Is.Zero);
    }

    /// <summary>
    /// Regular sequence numbers from 0xFFE0 on look like extended ones, so DSF does not ask for the extended link while either side numbers
    /// above ExtendedLink.LastSafeRegularNumber, and waits for the numbers to wrap (SPI.WriteActivateLinkCapabilities)
    /// </summary>
    [Test]
    public void NoActivationCloseToTheWrap()
    {
        FakeRrfSpi rrf = CreateRrf(firstSequenceNumber: ExtendedLink.LastSafeRegularNumber + 1);
        SpiAdapter dsf = CreateAdapter(rrf);
        dsf.PerformFullTransfer(true);
        Assert.That(dsf.WriteActivateLinkCapabilities(LinkCapabilities.All), Is.False);
        Assert.That(dsf.WriteActivateLinkCapabilities(LinkCapabilities.PhaseGuard | LinkCapabilities.SequenceGuard), Is.True, "only the extended link depends on the numbers");
    }

    /// <summary>
    /// For a malformed packet, DSF writes the data of the transfer to sys/transferDump.bin and logs the packet (SPI.DumpMalformedPacket), which
    /// LinkService does before it stops on the error
    /// </summary>
    [Test]
    public void DumpMalformedPacket()
    {
        FakeRrfSpi rrf = CreateRrf();
        ListLogger logger = new();
        SpiAdapter dsf = CreateAdapter(rrf, logger);
        dsf.PerformFullTransfer(true);
        rrf.SendPacket(FirmwareRequest.Message, [8, 0, 0, 0, 2, 0, 0, 0, (byte)'o', (byte)'k']);

        // RRF has set up the next transfer already, so the packet goes out with the one after it
        dsf.PerformFullTransfer();
        dsf.PerformFullTransfer();
        Assert.That(ReadPackets(dsf), Has.Count.EqualTo(1));
        dsf.DumpMalformedPacket();

        byte[] dump = System.IO.File.ReadAllBytes(Path.Combine(_baseDirectory, "sys", "transferDump.bin"));
        Assert.That(dump, Has.Length.EqualTo(Marshal.SizeOf<PacketHeader>() + 10));
        Assert.That(dump[^2..], Is.EqualTo("ok"u8.ToArray()));
        Assert.That(logger.Lines, Has.One.StartsWith("Error: Received malformed packet: === Packet #0 from offset 20 request 3 (length 10) ==="));
    }
}
