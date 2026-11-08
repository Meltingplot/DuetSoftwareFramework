using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DuetControlServer.Link.Protocol.Shared;
using UnitTests.Utility;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Update of the main firmware: RRF loads the SBC IAP program that DSF sends and starts it, then the IAP program flashes the firmware DSF sends
/// and checks its CRC16.
/// <para>
/// RRF (3.7.0+1.mp.8, cde4e4fa1): the first WriteIap packet stops the machine and makes RRF wait for further chunks (SbcInterface.cpp:666-672,
/// RepRap.cpp:2122-2154). From then on RRF only copies WriteIap chunks to IAP_IMAGE_START and starts the IAP binary on StartIap; it ignores all
/// other packets (SbcInterface.cpp:1625-1690). The regular packet loop skips a StartIap that comes without a WriteIap before
/// (SbcInterface.cpp:1323-1330), so the IAP program does not start then.
/// </para>
/// <para>
/// SBC IAP program (Duet3D/DuetIAP 3.7-dev 9af72da4): it erases the whole firmware region before it writes anything (src/iap.cpp:370-410), writes
/// blocks of blockReadSize = 2048 bytes (src/iap.h:260, Consts.FirmwareSegmentSize) from the start of the flash (src/iap.cpp:437-535), and takes
/// the end of the transfer as the end of the firmware. Then it checks the CRC16 of the first firmwareLength bytes of the flash against the one DSF
/// sends: a length of 0 or beyond the flash makes it reset without an answer (src/iap.cpp:601-611), a match is answered 0x0C and followed by the
/// reboot into the new firmware (src/iap.cpp:612-627 and 751-759), a mismatch 0xFF (src/iap.cpp:628-645). After a mismatch it erases the flash and
/// expects the firmware again, at most MaxCrcRetries = 3 times, then it resets (src/iap.cpp:690-748, src/iap.h:264). A reset after the flash was
/// erased leaves the board in its bootloader (src/iap.cpp:772-789). When the IAP program does not answer, the SPI adapter runs into
/// Consts.IapTimeout while it waits for TfrRdy and throws an OperationCanceledException (SPI.cs:1741-1780), which the fake throws at once
/// </para>
/// </summary>
public sealed partial class FakeFirmware
{
    // RRF side
    private readonly List<byte[]> _iapSegments = [];
    private bool _loadingIap;
    private uint? _iapFirmwareLength;

    // IAP program
    private byte[]? _flash;
    private int _flashPosition, _blocksThisAttempt, _crcErrors;
    private bool _iapRunning, _iapRebooted;
    private readonly List<(long Length, ushort Crc16)> _flashVerifications = [];

    #region Test API
    /// <summary>
    /// Size of the firmware region of the flash (FirmwareFlashEnd - FirmwareFlashStart, src/iap.h:255-256), by default the IFLASH_SIZE of the SAME70Q20B
    /// of the MB6HC (Boards.cmake:44, CoreN2G same70q20.h:656)
    /// </summary>
    public int FlashSize { get; set; } = 0x100000;

    /// <summary>
    /// Number of the next flash attempts in which the IAP program writes one byte wrong, so that the CRC16 of the attempt does not match
    /// </summary>
    public int FlashAttemptsToCorrupt { get; set; }

    /// <summary>
    /// Number of firmware blocks after which the IAP program stops answering, or null if it keeps going
    /// </summary>
    public int? IapStopsAfterBlocks { get; set; }

    /// <summary>
    /// Chunks of the IAP binary in the order RRF received them
    /// </summary>
    public IReadOnlyList<byte[]> GetIapSegments()
    {
        lock (_lock)
        {
            return [.. _iapSegments.Select(segment => (byte[])segment.Clone())];
        }
    }

    /// <summary>
    /// Firmware length DSF passed when it started the IAP program, or null if RRF did not start it
    /// </summary>
    public uint? IapFirmwareLength
    {
        get
        {
            lock (_lock)
            {
                return _iapRunning || _iapRebooted || _flash is not null ? _iapFirmwareLength : null;
            }
        }
    }

    /// <summary>
    /// Content of the firmware region up to the last block the IAP program wrote, or null if it never ran
    /// </summary>
    public byte[]? GetFlashedFirmware()
    {
        lock (_lock)
        {
            return _flash?[.._flashPosition];
        }
    }

    /// <summary>
    /// Length and CRC16 of every check the IAP program got from DSF
    /// </summary>
    public IReadOnlyList<(long Length, ushort Crc16)> GetFlashVerifications()
    {
        lock (_lock)
        {
            return [.. _flashVerifications];
        }
    }

    /// <summary>
    /// Whether the IAP program has rebooted into the new firmware
    /// </summary>
    public bool IapRebooted
    {
        get
        {
            lock (_lock)
            {
                return _iapRebooted;
            }
        }
    }
    #endregion

    /// <summary>
    /// Exception the SPI adapter throws when the IAP program does not raise TfrRdy within Consts.IapTimeout (SPI.cs:1779)
    /// </summary>
    private static OperationCanceledException IapTimeout() => new("Transfer timeout while waiting for TfrRdy pin (fake IAP program does not answer)");

    #region ILinkAdapter
    /// <inheritdoc />
    public bool WriteIapSegment(Stream stream, CancellationToken cancellationToken = default)
    {
        // Same segments as SPI.WriteIapSegment (SPI.cs:1149-1162)
        byte[] segment = new byte[Consts.IapSegmentSize];
        int bytesRead = stream.Read(segment);
        if (bytesRead <= 0)
        {
            return false;
        }

        lock (_lock)
        {
            _iapSegments.Add(segment[..bytesRead]);
            _loadingIap = true;
        }
        return true;
    }

    /// <inheritdoc />
    public void StartIap(uint firmwareLength, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _iapFirmwareLength = firmwareLength;
            if (_loadingIap)
            {
                _flash = new byte[FlashSize];
                Array.Fill(_flash, (byte)0xFF);
                _flashPosition = _blocksThisAttempt = _crcErrors = 0;
                _iapRunning = true;
            }
        }
    }

    /// <inheritdoc />
    public bool FlashFirmwareSegment(Stream stream)
    {
        // Same segments as SPI.FlashFirmwareSegment: the last one is padded with 0xFF (SPI.cs:1190-1210)
        byte[] block = new byte[Consts.FirmwareSegmentSize];
        int bytesRead = stream.Read(block);
        if (bytesRead <= 0)
        {
            return false;
        }
        block.AsSpan(bytesRead).Fill(0xFF);

        lock (_lock)
        {
            if (!_iapRunning || (IapStopsAfterBlocks is int blocks && _flashPosition / Consts.FirmwareSegmentSize >= blocks))
            {
                _iapRunning = false;
                throw IapTimeout();
            }

            if (_flashPosition + block.Length <= _flash!.Length)
            {
                block.CopyTo(_flash, _flashPosition);
                if (_blocksThisAttempt == 0 && FlashAttemptsToCorrupt > 0)
                {
                    _flash[_flashPosition + 100] ^= 0x10;
                    FlashAttemptsToCorrupt--;
                }
                _flashPosition += block.Length;
            }
            _blocksThisAttempt++;
        }
        return true;
    }

    /// <inheritdoc />
    public bool VerifyFirmwareChecksum(long firmwareLength, ushort crc16)
    {
        lock (_lock)
        {
            if (!_iapRunning || _blocksThisAttempt == 0)
            {
                // Without a single block the IAP program waits for the firmware until it times out (src/iap_spi.cpp:388-410)
                _iapRunning = false;
                throw IapTimeout();
            }

            _flashVerifications.Add((firmwareLength, crc16));
            if (firmwareLength == 0 || firmwareLength > _flash!.Length)
            {
                _iapRunning = false;
                throw IapTimeout();
            }

            if (FirmwareImage.IapCrc16(_flash.AsSpan(0, (int)firmwareLength)) == crc16)
            {
                return true;
            }

            if (++_crcErrors >= 3)
            {
                // The answer still goes out, then the IAP program resets
                _iapRunning = false;
            }
            else
            {
                Array.Fill(_flash, (byte)0xFF);
                _flashPosition = _blocksThisAttempt = 0;
            }
            return false;
        }
    }

    /// <inheritdoc />
    public void WaitForIapReset()
    {
        lock (_lock)
        {
            if (!_iapRunning)
            {
                throw new InvalidOperationException("The IAP program does not run");
            }

            // The new firmware starts like after a reset
            _iapRunning = _loadingIap = false;
            _iapRebooted = true;
            ResetFirmware();
        }
    }
    #endregion
}
