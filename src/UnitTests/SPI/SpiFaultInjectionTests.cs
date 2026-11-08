using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.Shared;
using DuetSharedLibrary;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace UnitTests.SPI;

/// <summary>
/// Fault specification and injector of the fault injection build for the lab printer
/// </summary>
[TestFixture]
public class SpiFaultInjectionTests
{
    [Test]
    public void SpecRoundTrip()
    {
        SpiFaultSpec fault = SpiFaultSpec.Parse(" BothData HeaderResponse ToDsf flip 2 ");
        Assert.That(fault, Is.EqualTo(new SpiFaultSpec(SpiTransferKind.BothData, SpiExchange.HeaderResponse, SpiFaultDirection.ToDsf, "flip 2")));
        Assert.That(SpiFaultSpec.Parse(fault.ToString()), Is.EqualTo(fault));
        Assert.That(SpiFaultSpec.Parse("Any Header Both flip 100"), Is.EqualTo(new SpiFaultSpec(SpiTransferKind.Any, SpiExchange.Header, SpiFaultDirection.Both, "flip 100")));
        Assert.That(() => SpiFaultSpec.Parse("BothData HeaderResponse 1 flip 2"), Throws.TypeOf<FormatException>());
        Assert.That(() => SpiFaultSpec.Parse("BothData HeaderResponse Sideways flip 2"), Throws.TypeOf<FormatException>());
        Assert.That(() => SpiFaultSpec.Parse("BothData HeaderResponse ToRrf twist 2"), Throws.TypeOf<FormatException>());
    }

    [Test]
    public void SpecOptionsRoundTrip()
    {
        SpiFaultSpec fault = SpiFaultSpec.Parse("Any Header ToRrf times=1000 counter=0 flip 96");
        Assert.That(fault, Is.EqualTo(new SpiFaultSpec(SpiTransferKind.Any, SpiExchange.Header, SpiFaultDirection.ToRrf, "flip 96", 1000, 0)));
        Assert.That(fault.ToString(), Is.EqualTo("Any Header ToRrf times=1000 counter=0 flip 96"));
        Assert.That(SpiFaultSpec.Parse("DsfData Data ToRrf counter=31 times=10 add 0 2 -1"), Is.EqualTo(new SpiFaultSpec(SpiTransferKind.DsfData, SpiExchange.Data, SpiFaultDirection.ToRrf, "add 0 2 -1", 10, 31)));

        // Without options the text form stays as it was, which the case files of the lab tests and the journal lines rely on
        Assert.That(new SpiFaultSpec(SpiTransferKind.NoData, SpiExchange.Header, SpiFaultDirection.ToDsf, "flip 3").ToString(), Is.EqualTo("NoData Header ToDsf flip 3"));

        Assert.That(() => SpiFaultSpec.Parse("Any Header ToRrf times=0 flip 96"), Throws.TypeOf<FormatException>());
        Assert.That(() => SpiFaultSpec.Parse("Any Header ToRrf counter=32 flip 96"), Throws.TypeOf<FormatException>());
        Assert.That(() => SpiFaultSpec.Parse("Any Header ToRrf counter=-1 flip 96"), Throws.TypeOf<FormatException>());
        Assert.That(() => SpiFaultSpec.Parse("Any Header ToRrf repeat=2 flip 96"), Throws.TypeOf<FormatException>());
        Assert.That(() => SpiFaultSpec.Parse("Any Header ToRrf times=2"), Throws.TypeOf<FormatException>());
    }

    /// <summary>
    /// Write a header of DSF with the given sequence number and exchange it with its response, as DSF does for every header retry
    /// </summary>
    private static void ExchangeHeader(FaultInjectingSpiDevice injector, ushort sequenceNumber)
    {
        byte[] header = new byte[16];
        header[0] = Consts.FormatCode;
        BitConverter.TryWriteBytes(header.AsSpan(4), sequenceNumber);
        injector.TransferFullDuplex(header, new byte[16]);
        injector.TransferFullDuplex(BitConverter.GetBytes(TransferResponse.Success), new byte[4]);
    }

    /// <summary>
    /// A fault with times=N corrupts up to N exchanges of the transfer it hits first and ends with that transfer, which the injector tells by the
    /// sequence number DSF sends. Retries of the header keep the number, the next transfer and a new session after a lost connection do not
    /// </summary>
    [Test]
    public void TimesHitsTheExchangesOfOneTransfer()
    {
        FirmwareStub firmware = new();
        FaultInjectingSpiDevice injector = new(firmware, null, NullLogger.Instance);
        int hits = 0;
        injector.Injected += (_, _) => hits++;

        injector.Arm(SpiFaultSpec.Parse("Any Header ToRrf times=3 flip 96"));
        ExchangeHeader(injector, 7);
        ExchangeHeader(injector, 7);
        Assert.That(hits, Is.EqualTo(2));
        Assert.That(firmware.Received[^2][12], Is.EqualTo(1), "the header that reached the firmware");

        // The next transfer ends the fault before its third exchange
        ExchangeHeader(injector, 8);
        ExchangeHeader(injector, 8);
        Assert.That(hits, Is.EqualTo(2));
        Assert.That(firmware.Received[^2][12], Is.Zero);

        // Without a new transfer the fault ends after N exchanges
        injector.Arm(SpiFaultSpec.Parse("Any Header ToRrf times=3 flip 96"));
        for (int i = 0; i < 5; i++)
        {
            ExchangeHeader(injector, 9);
        }
        Assert.That(hits, Is.EqualTo(5));
    }

    /// <summary>
    /// A fault with counter=N waits for a header of DSF with an extended sequence number and that counter value. A regular number never matches,
    /// also not one whose low bits are the counter value
    /// </summary>
    [Test]
    public void CounterWaitsForTheExtendedCounterValue()
    {
        FirmwareStub firmware = new();
        FaultInjectingSpiDevice injector = new(firmware, null, NullLogger.Instance);
        List<ushort> hit = [];
        ushort current = 0;
        injector.Injected += (_, _) => hit.Add(current);
        injector.Arm(SpiFaultSpec.Parse("Any Header ToRrf times=2 counter=0 flip 96"));

        foreach (ushort sequenceNumber in (ushort[])[0x0000, 0x0020, 0xFFDF, ExtendedLink.Next(0xFFE0, true), 0xFFFF, 0xFFE0, 0xFFE0, 0xFFE0, 0x0000, 0xFFE0])
        {
            current = sequenceNumber;
            ExchangeHeader(injector, sequenceNumber);
        }
        Assert.That(hit, Is.EqualTo(new ushort[] { 0xFFE0, 0xFFE0 }));
    }

    [TestCase("flip 9", new byte[] { 0x01, 0x02, 0x03, 0x04 }, new byte[] { 0x01, 0x00, 0x03, 0x04 })]
    [TestCase("flip 40", new byte[] { 0x01, 0x02, 0x03, 0x04 }, new byte[] { 0x01, 0x02, 0x03, 0x04 })]
    [TestCase("zero 1 2", new byte[] { 0x01, 0x02, 0x03, 0x04 }, new byte[] { 0x01, 0x00, 0x00, 0x04 })]
    [TestCase("ones 3 4", new byte[] { 0x01, 0x02, 0x03, 0x04 }, new byte[] { 0x01, 0x02, 0x03, 0xFF })]
    [TestCase("add 0 2 1", new byte[] { 0xFF, 0x00, 0x03, 0x04 }, new byte[] { 0x00, 0x01, 0x03, 0x04 })]
    [TestCase("add 0 2 -1", new byte[] { 0x00, 0x00, 0x03, 0x04 }, new byte[] { 0xFF, 0xFF, 0x03, 0x04 })]
    [TestCase("word 0xFEFEFEFE", new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, new byte[] { 0xFE, 0xFE, 0xFE, 0xFE, 0x05 })]
    [TestCase("word 5", new byte[] { 0x01, 0x02 }, new byte[] { 0x01, 0x02 })]
    [TestCase("fill 0xFF", new byte[] { 0x01, 0x02 }, new byte[] { 0xFF, 0xFF })]
    public void Mutations(string mutation, byte[] data, byte[] expected)
    {
        SpiFaultSpec.Apply(mutation, data);
        Assert.That(data, Is.EqualTo(expected));
    }

    /// <summary>
    /// Device that answers a header exchange with a valid header without data and anything else with Success
    /// </summary>
    private sealed class FirmwareStub : ISpiDevice
    {
        public List<byte[]> Received { get; } = [];

        public void TransferFullDuplex(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer)
        {
            Received.Add(writeBuffer.ToArray());
            if (writeBuffer.Length == 16)
            {
                byte[] header = [Consts.FormatCode, 0, (byte)Consts.ProtocolVersion, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
                BitConverter.TryWriteBytes(header.AsSpan(12), DuetControlServer.Utility.CRC32.Calculate(header.AsSpan(0, 12)));
                header.CopyTo(readBuffer);
            }
            else
            {
                BitConverter.TryWriteBytes(readBuffer, TransferResponse.Success);
            }
        }
    }

    [Test]
    public void ControlFileArmsOneFault()
    {
        string file = Path.GetTempFileName();
        try
        {
            FirmwareStub firmware = new();
            FaultInjectingSpiDevice injector = new(firmware, file, NullLogger.Instance);
            List<SpiTransferKind> hits = [];
            injector.Injected += (_, kind) => hits.Add(kind);
            byte[] header = new byte[16], response = new byte[4];
            header[0] = Consts.FormatCode;

            // Exchange the headers and the header response, returning the response DSF got
            uint Transfer()
            {
                injector.TransferFullDuplex(header, new byte[16]);
                byte[] rx = new byte[4];
                injector.TransferFullDuplex(response, rx);
                return BitConverter.ToUInt32(rx);
            }

            // A fault written before DCS started is not armed
            System.IO.File.WriteAllText(file, "NoData HeaderResponse ToDsf flip 3");
            Assert.That(Transfer(), Is.EqualTo(TransferResponse.Success));
            Assert.That(hits, Is.Empty);

            // Arm a fault in the response that DSF receives
            Thread.Sleep(60);
            System.IO.File.WriteAllText(file, "NoData HeaderResponse ToDsf flip 2");
            System.IO.File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));
            Assert.That(Transfer(), Is.EqualTo(TransferResponse.Success ^ 4));
            Assert.That(hits, Is.EqualTo(new[] { SpiTransferKind.NoData }));

            // The fault is injected once
            Thread.Sleep(60);
            Assert.That(Transfer(), Is.EqualTo(TransferResponse.Success));
            Assert.That(hits, Has.Count.EqualTo(1));

            // An empty file disarms a fault that never found its transfer
            System.IO.File.WriteAllText(file, "BothData Data ToRrf fill 0");
            System.IO.File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(2));
            Thread.Sleep(60);
            Transfer();
            System.IO.File.WriteAllText(file, string.Empty);
            System.IO.File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(3));
            Thread.Sleep(60);
            Transfer();
            injector.TransferFullDuplex(header, new byte[16]);
            Assert.That(hits, Has.Count.EqualTo(1));
        }
        finally
        {
            System.IO.File.Delete(file);
        }
    }

    [Test]
    public void BothDirectionsAreCorrupted()
    {
        FirmwareStub firmware = new();
        FaultInjectingSpiDevice injector = new(firmware, null, NullLogger.Instance);
        injector.Arm(SpiFaultSpec.Parse("Any HeaderResponse Both flip 2"));
        byte[] header = new byte[16];
        header[0] = Consts.FormatCode;
        injector.TransferFullDuplex(header, new byte[16]);

        byte[] rx = new byte[4];
        injector.TransferFullDuplex(BitConverter.GetBytes(TransferResponse.Success), rx);
        Assert.That(BitConverter.ToUInt32(firmware.Received[^1]), Is.EqualTo(TransferResponse.Success ^ 4), "sent to RRF");
        Assert.That(BitConverter.ToUInt32(rx), Is.EqualTo(TransferResponse.Success ^ 4), "received from RRF");
    }

    /// <summary>
    /// A fault of the kind Activation waits for the transfer whose packets contain the activation request. Its data does not count, and
    /// neither does a request code in the data of another packet
    /// </summary>
    [Test]
    public void ActivationKindHitsTheTransferThatActivates()
    {
        const ushort activation = (ushort)DuetControlServer.Link.Protocol.SbcRequests.Request.ActivateLinkCapabilities;
        FirmwareStub firmware = new();
        FaultInjectingSpiDevice injector = new(firmware, null, NullLogger.Instance);
        int hits = 0;
        injector.Injected += (_, _) => hits++;
        injector.Arm(SpiFaultSpec.Parse("Activation DataResponse ToRrf word 0x00000006"));

        // Perform a transfer with the given packets and return the data response that reached the firmware
        uint Transfer(params (ushort Request, byte[] Data)[] packets)
        {
            List<byte> data = [];
            foreach ((ushort request, byte[] packetData) in packets)
            {
                data.AddRange(BitConverter.GetBytes(request));
                data.AddRange(BitConverter.GetBytes((ushort)0));
                data.AddRange(BitConverter.GetBytes((ushort)packetData.Length));
                data.AddRange(BitConverter.GetBytes((ushort)0));
                data.AddRange(packetData);
                data.AddRange(new byte[(4 - packetData.Length % 4) % 4]);
            }

            byte[] header = new byte[16];
            header[0] = Consts.FormatCode;
            BitConverter.TryWriteBytes(header.AsSpan(6), (ushort)data.Count);
            injector.TransferFullDuplex(header, new byte[16]);
            injector.TransferFullDuplex(BitConverter.GetBytes(TransferResponse.Success), new byte[4]);
            injector.TransferFullDuplex(data.ToArray(), new byte[data.Count]);
            injector.TransferFullDuplex(BitConverter.GetBytes(TransferResponse.Success), new byte[4]);
            return BitConverter.ToUInt32(firmware.Received[^1]);
        }

        Assert.That(Transfer((1, [1, 2, 3])), Is.EqualTo(TransferResponse.Success), "another request");
        Assert.That(Transfer((1, BitConverter.GetBytes((uint)activation))), Is.EqualTo(TransferResponse.Success), "the request code in the data of a packet");
        Assert.That(hits, Is.Zero);

        Assert.That(Transfer((1, [1, 2, 3]), (activation, new byte[4])), Is.EqualTo(6u), "the request behind another packet");
        Assert.That(hits, Is.EqualTo(1));

        Assert.That(Transfer((activation, new byte[4])), Is.EqualTo(TransferResponse.Success), "the fault is injected once");
        Assert.That(hits, Is.EqualTo(1));
    }

    /// <summary>
    /// The transfer that activates is known only from its data, so a fault in an exchange before the data never hits it
    /// </summary>
    [Test]
    public void ActivationKindCannotHitTheHeaders()
    {
        const ushort activation = (ushort)DuetControlServer.Link.Protocol.SbcRequests.Request.ActivateLinkCapabilities;
        FirmwareStub firmware = new();
        FaultInjectingSpiDevice injector = new(firmware, null, NullLogger.Instance);
        int hits = 0;
        injector.Injected += (_, _) => hits++;
        injector.Arm(SpiFaultSpec.Parse("Activation HeaderResponse ToDsf flip 2"));

        byte[] data = new byte[12];
        BitConverter.TryWriteBytes(data, activation);
        BitConverter.TryWriteBytes(data.AsSpan(4), (ushort)4);
        byte[] header = new byte[16];
        header[0] = Consts.FormatCode;
        BitConverter.TryWriteBytes(header.AsSpan(6), (ushort)data.Length);
        for (int i = 0; i < 2; i++)
        {
            injector.TransferFullDuplex(header, new byte[16]);
            injector.TransferFullDuplex(BitConverter.GetBytes(TransferResponse.Success), new byte[4]);
            injector.TransferFullDuplex(data, new byte[data.Length]);
            injector.TransferFullDuplex(BitConverter.GetBytes(TransferResponse.Success), new byte[4]);
        }
        Assert.That(hits, Is.Zero);
    }
}
