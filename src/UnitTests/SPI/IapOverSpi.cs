using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using DuetSharedLibrary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using UnitTests.Utility;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;
using SpiAdapter = DuetControlServer.Link.Adapter.SPI;

namespace UnitTests.SPI;

/// <summary>
/// A firmware update on the real SPI adapter from start to end: RRF (<see cref="FakeRrfSpi"/>) gets the IAP binary and starts it, then the SPI
/// adapter talks to the SBC IAP program (<see cref="IapSpi"/>), which knows nothing of RRF's transfer protocol. SPI.VerifyFirmwareChecksum and
/// SPI.WaitForIapReset wait in real time (Consts.FirmwareFinishedDelay, Consts.IapRebootDelay), so these tests take a few seconds
/// </summary>
[TestFixture]
public class IapOverSpi
{
    /// <summary>
    /// SPI side of the SBC IAP program of Duet3D/DuetIAP 3.7-dev 9af72da4 (src/iap_spi.cpp, src/iap.cpp). Every transfer the IAP program sets up toggles
    /// TfrRdy, starting from low (src/iap_spi.cpp:43 and 222-224). It first expects firmware blocks of blockReadSize = 2048 bytes (src/iap.h:260,
    /// src/iap_spi.cpp:388-418). When SpiTransferCompleteDelay passes without a block after at least one, the firmware is complete
    /// (src/iap_spi.cpp:398-404) and it sets up the 8-byte FlashVerifyRequest of firmwareLength and crc16 (src/iap.h:271-276, src/iap.cpp:537-560).
    /// It answers that with one byte, 0x0C for a matching CRC16 of the flashed bytes and 0xFF otherwise (src/iap.cpp:601-645, src/iap_spi.cpp:431-435).
    /// After 0x0C it reboots into the new firmware (src/iap.cpp:653-689 and 751-759), after 0xFF it erases the flash and expects the firmware again,
    /// at most three times (src/iap.cpp:690-748). A wait of the IAP program that nothing ends runs into its TransferTimeout and resets it
    /// (src/iap_spi.cpp:406-410, src/iap.cpp:575-597), so TfrRdy does not change any more and DSF times out. There is no real time here: when
    /// DSF waits for TfrRdy, the IAP program behaves as if DSF had waited long enough for whatever the IAP program waits for
    /// </summary>
    internal sealed class IapSpi : ISpiDevice, IInputGpioPin
    {
        private enum Transfer { None, Block, Verify, Response }

        private const int BlockSize = 2048;

        private readonly Queue<bool> _edges = new();
        private readonly byte[] _flash = new byte[0x100000];
        private bool _pin, _value, _dead;
        private Transfer _armed;
        private int _flashPosition, _blocksThisAttempt, _crcErrors;
        private byte _response;

        /// <summary>
        /// Sizes of the transfers DSF made with the IAP program
        /// </summary>
        public List<int> TransferSizes { get; } = [];

        /// <summary>
        /// Firmware length and CRC16 of every FlashVerifyRequest
        /// </summary>
        public List<(uint Length, ushort Crc16)> Verifications { get; } = [];

        /// <summary>
        /// Number of the next flash attempts in which one byte is written wrong
        /// </summary>
        public int AttemptsToCorrupt { get; set; }

        /// <summary>
        /// Number of blocks after which the IAP program stops answering, or null
        /// </summary>
        public int? StopAfterBlocks { get; set; }

        /// <summary>
        /// Whether the IAP program has rebooted into the new firmware
        /// </summary>
        public bool Rebooted { get; private set; }

        /// <summary>
        /// Flash content up to the last block DSF sent
        /// </summary>
        public byte[] Flashed => _flash[.._flashPosition];

        /// <summary>
        /// RRF has started the IAP program, which erases the flash and sets up the first block (src/iap.cpp:370-410, src/iap_spi.cpp:413-417)
        /// </summary>
        public void Start()
        {
            Array.Fill(_flash, (byte)0xFF);
            Arm(Transfer.Block);
        }

        private void Arm(Transfer transfer)
        {
            _armed = transfer;
            _pin = !_pin;
            _edges.Enqueue(_pin);
        }

        private void Die() => (_dead, _armed) = (true, Transfer.None);

        /// <summary>
        /// DSF waits for TfrRdy instead of starting the transfer the IAP program has set up
        /// </summary>
        private void Idle()
        {
            if (!_dead && _armed == Transfer.Block && _flashPosition != 0)
            {
                // The firmware is complete. The IAP program writes the 0xFF block it reads then, which changes nothing on erased flash
                Arm(Transfer.Verify);
            }
            else
            {
                Die();
            }
        }

        public int MissedEdges => 0;

        public bool Value => _value;

        public void FlushEvents()
        {
            while (_edges.TryDequeue(out bool level))
            {
                _value = level;
            }
        }

        public bool WaitForEvent(int timeout)
        {
            if (_edges.Count == 0)
            {
                Idle();
            }
            if (_edges.Count == 0)
            {
                // DSF times out, as on FakeRrfSpi (FakeRrfSpi.cs:1812-1820)
                throw new OperationCanceledException();
            }
            _value = _edges.Dequeue();
            return _value;
        }

        public void TransferFullDuplex(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer)
        {
            // DSF may pass the same buffer for both directions (SPI.cs:1233 and 1238)
            byte[] data = writeBuffer.ToArray();
            TransferSizes.Add(data.Length);
            readBuffer.Fill(0x1A);          // content of writeData (src/iap_spi.cpp:385)
            switch (_armed)
            {
                case Transfer.Block:
                    data.AsSpan(0, Math.Min(data.Length, BlockSize)).CopyTo(_flash.AsSpan(_flashPosition));
                    if (_blocksThisAttempt++ == 0 && AttemptsToCorrupt > 0)
                    {
                        _flash[_flashPosition + 10] ^= 0x01;
                        AttemptsToCorrupt--;
                    }
                    _flashPosition += BlockSize;
                    if (StopAfterBlocks is int blocks && _flashPosition / BlockSize >= blocks)
                    {
                        Die();
                    }
                    else
                    {
                        Arm(Transfer.Block);
                    }
                    break;

                case Transfer.Verify:
                    uint length = BinaryPrimitives.ReadUInt32LittleEndian(data);
                    ushort crc16 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
                    Verifications.Add((length, crc16));
                    if (length == 0 || length > _flash.Length)
                    {
                        Die();
                    }
                    else
                    {
                        _response = (FirmwareImage.IapCrc16(_flash.AsSpan(0, (int)length)) == crc16) ? (byte)0x0C : (byte)0xFF;
                        Arm(Transfer.Response);
                    }
                    break;

                case Transfer.Response:
                    readBuffer[0] = _response;
                    if (_response == 0x0C)
                    {
                        Rebooted = true;
                        _armed = Transfer.None;
                    }
                    else if (++_crcErrors >= 3)
                    {
                        Die();
                    }
                    else
                    {
                        Array.Fill(_flash, (byte)0xFF);
                        _flashPosition = _blocksThisAttempt = 0;
                        Arm(Transfer.Block);
                    }
                    break;

                default:
                    // Nothing is set up, so nobody answers
                    readBuffer.Fill(0xFF);
                    break;
            }
        }
    }

    /// <summary>
    /// SPI device and TfrRdy pin that belong to RRF until RRF starts the IAP program, then to the IAP program
    /// </summary>
    private sealed class RrfThenIap(FakeRrfSpi rrf, IapSpi iap) : ISpiDevice, IInputGpioPin
    {
        public bool IapStarted { get; private set; }

        public void StartIap()
        {
            IapStarted = true;
            iap.Start();
        }

        private ISpiDevice Device => IapStarted ? iap : rrf;

        private IInputGpioPin Pin => IapStarted ? iap : rrf;

        public void TransferFullDuplex(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer) => Device.TransferFullDuplex(writeBuffer, readBuffer);

        public int MissedEdges => Pin.MissedEdges;

        public bool Value => Pin.Value;

        public void FlushEvents() => Pin.FlushEvents();

        public bool WaitForEvent(int timeout) => Pin.WaitForEvent(timeout);
    }

    /// <summary>
    /// SPI adapter whose StartIap hands the SPI device over to the IAP program once RRF has got the StartIap packet. Everything else is the real adapter
    /// </summary>
    private sealed class IapSpiAdapter(IServiceProvider services, RrfThenIap spi, IOptions<Settings> settings, TimeProvider clock) :
        SpiAdapter(services.GetRequiredService<EventLogger>(), services.GetRequiredService<DuetControlServer.Model.ObjectModel>(), NullLogger<SpiAdapter>.Instance, settings, spi, spi, clock), ILinkAdapter
    {
        void ILinkAdapter.StartIap(uint firmwareLength, CancellationToken cancellationToken)
        {
            StartIap(firmwareLength, cancellationToken);
            spi.StartIap();
        }
    }

    private static async Task<(LinkServiceOverSpi.Host Host, IapSpi Iap)> StartAsync(Action<IapSpi>? configure = null)
    {
        IapSpi iap = new();
        configure?.Invoke(iap);
        LinkServiceOverSpi.Host host = await LinkServiceOverSpi.Host.StartAsync(createAdapter: (services, rrf, settings) => new IapSpiAdapter(services, new RrfThenIap(rrf, iap), settings, rrf.Clock));
        return (host, iap);
    }

    private static readonly byte[] IapBinary = [.. Enumerable.Range(0, Consts.IapSegmentSize + 200).Select(i => (byte)(i * 7))];

    private static readonly byte[] FirmwareBinary = FirmwareImage.Build("RepRapFirmware for Duet 3 MB6HC version 3.7.0+1.mp.9", length: 2 * Consts.FirmwareSegmentSize + 300, loadAddress: 0x00400000);

    /// <summary>
    /// DSF sends the firmware in blocks of the IAP program's blockReadSize, the last one filled up with 0xFF (SPI.cs:1186-1210), then the
    /// FlashVerifyRequest with the length of the firmware and its CRC16 (SPI.cs:1213-1240). A mismatch makes DSF send the firmware again
    /// (LinkService.cs:101-143), a match ends the update with the reboot of the board (SPI.cs:1245-1255)
    /// </summary>
    [Test]
    public async Task FirmwareUpdateWithOneChecksumError()
    {
        (LinkServiceOverSpi.Host host, IapSpi iap) = await StartAsync(iap => iap.AttemptsToCorrupt = 1);
        await using (host)
        {
            await host.Link.UpdateFirmware(new MemoryStream(IapBinary), new MemoryStream(FirmwareBinary)).WaitAsync(LinkServiceOverSpi.Timeout);

            List<(SbcRequest Request, ushort Id, byte[] Payload)> packets = host.Rrf.GetReceivedPackets();
            Assert.That(packets.Where(p => p.Request == SbcRequest.WriteIap).SelectMany(p => p.Payload), Is.EqualTo(IapBinary));
            Assert.That(packets.Count(p => p.Request == SbcRequest.StartIap), Is.EqualTo(1));

            int[] attempt = [Consts.FirmwareSegmentSize, Consts.FirmwareSegmentSize, Consts.FirmwareSegmentSize, 8, 1];
            Assert.That(iap.TransferSizes, Is.EqualTo(attempt.Concat(attempt)));
            Assert.That(iap.Verifications, Is.EqualTo(Enumerable.Repeat(((uint)FirmwareBinary.Length, FirmwareImage.IapCrc16(FirmwareBinary)), 2)));
            Assert.That(iap.Flashed, Is.EqualTo(FirmwareImage.Pad(FirmwareBinary, Consts.FirmwareSegmentSize)));
            Assert.That(iap.Rebooted, Is.True);

            await host.Service.ExecuteTask!.WaitAsync(LinkServiceOverSpi.Timeout);
            Assert.That(host.Fault, Is.Null);
            using (host.Model.AccessReadOnly())
            {
                Assert.That(host.Model.State.Status, Is.EqualTo(MachineStatus.Updating));
            }
        }
    }

    /// <summary>
    /// When the IAP program stops raising TfrRdy, DSF times out after Consts.IapTimeout with an OperationCanceledException that names no transfer
    /// phase, since the transfer is not one of RRF's (SPI.cs:1741-1780). The update fails with it as an IOException, because M997 would take an
    /// OperationCanceledException for its own cancellation and drop the reason (LinkService.cs:134-139 and 319-326, MCodeHandler.cs:1287)
    /// </summary>
    [Test]
    public async Task IapProgramStopsAnswering()
    {
        (LinkServiceOverSpi.Host host, IapSpi iap) = await StartAsync(iap => iap.StopAfterBlocks = 1);
        await using (host)
        {
            await Assert.ThatAsync(() => host.Link.UpdateFirmware(new MemoryStream(IapBinary), new MemoryStream(FirmwareBinary)).WaitAsync(LinkServiceOverSpi.Timeout),
                Throws.InstanceOf<IOException>().With.Message.StartsWith("Transfer timeout while waiting for TfrRdy pin (waited ")
                    .And.InnerException.InstanceOf<OperationCanceledException>());
            Assert.That(iap.TransferSizes, Is.EqualTo(new[] { Consts.FirmwareSegmentSize }));
            Assert.That(iap.Rebooted, Is.False);
        }
    }

    /// <summary>
    /// With an empty firmware file DSF sends no block and goes straight to the check, but the IAP program still waits for the first block, which it
    /// does not take as the end of the firmware (src/iap_spi.cpp:398), so DSF times out before it sends the FlashVerifyRequest (SPI.cs:1218-1233).
    /// This is the transfer the fake firmware reports as a TfrRdy timeout (FakeFirmwareIap.cs)
    /// </summary>
    [Test]
    public async Task EmptyFirmware()
    {
        (LinkServiceOverSpi.Host host, IapSpi iap) = await StartAsync();
        await using (host)
        {
            await Assert.ThatAsync(() => host.Link.UpdateFirmware(new MemoryStream(IapBinary), new MemoryStream()).WaitAsync(LinkServiceOverSpi.Timeout),
                Throws.InstanceOf<IOException>().With.InnerException.InstanceOf<OperationCanceledException>());
            Assert.That(iap.TransferSizes, Is.Empty);
            Assert.That(iap.Verifications, Is.Empty);
        }
    }
}
