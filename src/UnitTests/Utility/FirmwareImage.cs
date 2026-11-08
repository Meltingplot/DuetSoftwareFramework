using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace UnitTests.Utility;

/// <summary>
/// Synthetic firmware files laid out like the ones RRF and the expansion board firmware are built into.
/// <para>
/// The vector table at the start of the image holds the address of VersionText in the entry at offset 0x20 and the address of the vector
/// table itself, which is the load address, in the entry at offset 0x24 (CoreN2G src/SAM4S_4E_E70/SAME70/Vectors.c:148-150 and
/// src/SAME5x_C21/SAMC21/Vectors.c:128-130). VersionText is FIRMWARE_NAME " version " VERSION in RRF (RepRap.cpp:91) and
/// "Duet " BOARD_TYPE_NAME " firmware version " VERSION in the expansion board firmware (Duet3Expansion src/RepRapFirmware.cpp:16).
/// The real Duet3Firmware_TOOL1LC.bin of 3.6.3+1 has 0x00024904 at 0x20, 0x00004000 at 0x24 and "Duet TOOL1LC firmware version 3.6.3+1" at offset 0x20904
/// </para>
/// </summary>
internal static class FirmwareImage
{
    /// <summary>
    /// Load address of the expansion board firmware behind its bootloader, as in Duet3Firmware_TOOL1LC.bin
    /// </summary>
    public const uint ExpansionLoadAddress = 0x4000;

    /// <summary>
    /// Build a firmware binary
    /// </summary>
    /// <param name="versionText">Version text stored in the image, null terminated</param>
    /// <param name="length">Length of the image</param>
    /// <param name="loadAddress">Address the image is linked for</param>
    /// <param name="versionTextOffset">Offset of the version text in the image</param>
    /// <param name="terminate">Whether to terminate the version text with a NUL character</param>
    /// <param name="seed">Seed of the random content of the rest of the image</param>
    /// <returns>Firmware binary</returns>
    public static byte[] Build(string versionText, int length = 6000, uint loadAddress = ExpansionLoadAddress, int versionTextOffset = 0x400, bool terminate = true, int seed = 1)
    {
        byte[] text = Encoding.UTF8.GetBytes(versionText);
        int textLength = text.Length + (terminate ? 1 : 0);
        if (versionTextOffset + textLength > length)
        {
            throw new ArgumentException("Version text does not fit", nameof(versionText));
        }

        // Random content keeps a wrong offset or a wrong unpacking from going unnoticed
        byte[] image = new byte[length];
        new Random(seed).NextBytes(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x20), loadAddress + (uint)versionTextOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x24), loadAddress);
        text.CopyTo(image, versionTextOffset);
        if (terminate)
        {
            image[versionTextOffset + text.Length] = 0;
        }
        return image;
    }

    /// <summary>
    /// Flags of a UF2 block whose payload Firmware.UnpackUF2Async leaves out (FlagNoFlash, Firmware.cs:33 and 79)
    /// </summary>
    public const uint Uf2NotMainFlash = 0x00000001;

    /// <summary>
    /// Pack an image into UF2 blocks of 512 bytes: the 32-byte header (magic start 0x0A324655 and 0x9E5D5157, flags, target address,
    /// payload size, block number, number of blocks, family ID), up to 476 bytes of payload and the magic end 0x0AB16F30 at offset 508,
    /// as Firmware.UnpackUF2Async reads them (Firmware.cs:14-33). RRF's .uf2 files carry 256 bytes per block (RepRap.cpp:2051-2052)
    /// </summary>
    /// <param name="image">Image to pack</param>
    /// <param name="targetAddress">Address of the first byte</param>
    /// <param name="payloadSize">Payload bytes per block</param>
    /// <param name="flags">Flags of every block</param>
    /// <param name="extraBlocks">Extra blocks with the given flags inserted before the block of the same index, e.g. comments</param>
    /// <returns>UF2 file content</returns>
    public static byte[] ToUf2(byte[] image, uint targetAddress = ExpansionLoadAddress, int payloadSize = 256, uint flags = 0,
        IReadOnlyDictionary<int, (uint Flags, byte[] Payload)>? extraBlocks = null)
    {
        List<(uint Flags, uint Address, byte[] Payload)> blocks = [];
        for (int offset = 0, index = 0; offset < image.Length; offset += payloadSize, index++)
        {
            if (extraBlocks is not null && extraBlocks.TryGetValue(index, out (uint Flags, byte[] Payload) extra))
            {
                blocks.Add((extra.Flags, 0, extra.Payload));
            }
            blocks.Add((flags, targetAddress + (uint)offset, image.AsSpan(offset, Math.Min(payloadSize, image.Length - offset)).ToArray()));
        }

        byte[] uf2 = new byte[blocks.Count * 512];
        for (int i = 0; i < blocks.Count; i++)
        {
            Span<byte> block = uf2.AsSpan(i * 512, 512);
            BinaryPrimitives.WriteUInt32LittleEndian(block[0..], 0x0A324655);
            BinaryPrimitives.WriteUInt32LittleEndian(block[4..], 0x9E5D5157);
            BinaryPrimitives.WriteUInt32LittleEndian(block[8..], blocks[i].Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(block[12..], blocks[i].Address);
            BinaryPrimitives.WriteUInt32LittleEndian(block[16..], (uint)blocks[i].Payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(block[20..], (uint)i);
            BinaryPrimitives.WriteUInt32LittleEndian(block[24..], (uint)blocks.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(block[28..], 0);
            blocks[i].Payload.CopyTo(block[32..]);
            BinaryPrimitives.WriteUInt32LittleEndian(block[508..], 0x0AB16F30);
        }
        return uf2;
    }

    /// <summary>
    /// CRC16 as the SBC IAP program calculates it over the flashed firmware (Duet3D/DuetIAP 3.7-dev 9af72da4, src/iap.cpp:177-222: table of the
    /// reflected polynomial 0xA001, start value 0xFFFF). Written bit by bit here, so it does not share code with DSF's table
    /// </summary>
    /// <param name="data">Data to check</param>
    /// <returns>CRC16</returns>
    public static ushort IapCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = ((crc & 1) != 0) ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }
        return crc;
    }

    /// <summary>
    /// Pad an image with 0xFF to a multiple of the given block size, as DSF sends the last firmware segment (SPI.cs:1201-1205)
    /// </summary>
    /// <param name="image">Image</param>
    /// <param name="blockSize">Block size</param>
    /// <returns>Padded image</returns>
    public static byte[] Pad(byte[] image, int blockSize)
    {
        byte[] padded = new byte[(image.Length + blockSize - 1) / blockSize * blockSize];
        Array.Fill(padded, (byte)0xFF);
        image.CopyTo(padded, 0);
        return padded;
    }
}
