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
}
