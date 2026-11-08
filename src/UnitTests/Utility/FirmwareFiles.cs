using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DuetControlServer.Utility;
using NUnit.Framework;

namespace UnitTests.Utility;

/// <summary>
/// Reading the firmware version from a firmware file and unpacking UF2 files (Firmware.cs). DCS -u compares the version of each board with
/// the version in its firmware file to decide which boards to update (FirmwareUpdater.cs:56-90), and M997 unpacks a .uf2 file before it
/// flashes it (MCodeHandler.cs:1272-1276). The files are built by <see cref="FirmwareImage"/>
/// </summary>
[TestFixture]
public class FirmwareFiles
{
    private string _directory = null!;

    [SetUp]
    public void CreateDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dsf-firmware-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void DeleteDirectory() => Directory.Delete(_directory, true);

    private string Write(string fileName, byte[] content)
    {
        string path = Path.Combine(_directory, fileName);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    private static Task<string?> GetVersionAsync(string path) => Firmware.GetFirmwareVersionAsync(path, 4096);

    #region Firmware version
    /// <summary>
    /// The version is the last space-delimited item of the version text. RRF reports the same VERSION as boards[].firmwareVersion
    /// (Platform.cpp:222), whose comment says it must not contain spaces (Version.h:14). The suffix of a USE_CAN0 build is part of it (Version.h:16-23)
    /// </summary>
    [TestCase("RepRapFirmware for Duet 3 MB6HC version 3.7.0+1.mp.8", 0x00400000u, ExpectedResult = "3.7.0+1.mp.8")]
    [TestCase("Duet TOOL1LC firmware version 3.6.3+1", FirmwareImage.ExpansionLoadAddress, ExpectedResult = "3.6.3+1")]
    [TestCase("RepRapFirmware for Duet 3 Mini 5+ version 3.7.0+1.mp.8(CAN0)", 0x00000000u, ExpectedResult = "3.7.0+1.mp.8(CAN0)")]
    public async Task<string?> VersionOfFirmwareBinary(string versionText, uint loadAddress)
    {
        // A load address of 0 is not special: only the offset of the text from the load address counts
        string path = Write("firmware.bin", FirmwareImage.Build(versionText, loadAddress: loadAddress));
        return await GetVersionAsync(path);
    }

    /// <summary>
    /// The text may end with the file. Reading stops there instead of failing (Firmware.cs:147)
    /// </summary>
    [Test]
    public async Task VersionTextAtTheEndOfTheFile()
    {
        const string text = "Duet EXP3HC firmware version 3.7.0";
        string path = Write("firmware.bin", FirmwareImage.Build(text, length: 0x400 + text.Length, terminate: false));
        Assert.That(await GetVersionAsync(path), Is.EqualTo("3.7.0"));
    }

    /// <summary>
    /// 128 characters is the longest text DSF accepts, a longer one is treated as garbage (MaxFirmwareStringLength, Firmware.cs:103 and 155-159)
    /// </summary>
    [TestCase(128, true)]
    [TestCase(129, false)]
    public async Task LongestVersionText(int length, bool accepted)
    {
        string text = new('v', length);
        string path = Write("firmware.bin", FirmwareImage.Build(text));
        Assert.That(await GetVersionAsync(path), accepted ? Is.EqualTo(text) : Is.Null);
    }

    private static IEnumerable<TestCaseData> FilesWithoutVersion()
    {
        // Too short to hold the two words at 0x20 and 0x24 (Firmware.cs:128-131)
        yield return new TestCaseData(new byte[0x27]).SetName("{m}(TooShort)");

        // A file of zeros, e.g. a firmware whose vector table leaves the reserved entries empty (CoreN2G SAME70/Vectors.c:151-154): the text address is not above the load address
        yield return new TestCaseData(new byte[0x1000]).SetName("{m}(Zeros)");

        // The text address points behind the end of the file (Firmware.cs:141)
        byte[] beyondTheEnd = FirmwareImage.Build("Duet TOOL1LC firmware version 3.7.0");
        BinaryPrimitives.WriteUInt32LittleEndian(beyondTheEnd.AsSpan(0x20), FirmwareImage.ExpansionLoadAddress + (uint)beyondTheEnd.Length);
        yield return new TestCaseData(beyondTheEnd).SetName("{m}(TextBeyondTheEnd)");

        // An empty text and a text that ends with a space have no last item (Firmware.cs:160-170)
        yield return new TestCaseData(FirmwareImage.Build(string.Empty)).SetName("{m}(EmptyText)");
        yield return new TestCaseData(FirmwareImage.Build("RepRapFirmware version ")).SetName("{m}(TrailingSpace)");
    }

    [TestCaseSource(nameof(FilesWithoutVersion))]
    public async Task NoVersion(byte[] content)
    {
        string path = Write("firmware.bin", content);
        Assert.That(await GetVersionAsync(path), Is.Null);
    }

    /// <summary>
    /// The version of a .uf2 file is read from its unpacked image. The Duet 3 Mini uses one (IAP_FIRMWARE_FILE, Pins_Duet3Mini.h:18-27), and so do
    /// expansion boards that ask for it (ExpansionManager.cpp:57-60 and 479-480). The extension is compared without regard to case, like the
    /// SD card IAP program does (Duet3D/DuetIAP 3.7-dev src/iap_sd.cpp:133)
    /// </summary>
    [TestCase("Duet3Firmware_Mini5plus.uf2")]
    [TestCase("Duet3Firmware_Mini5plus.UF2")]
    public async Task VersionOfUf2File(string fileName)
    {
        byte[] image = FirmwareImage.Build("RepRapFirmware for Duet 3 Mini 5+ version 3.7.0+1.mp.8", length: 3000, loadAddress: 0);
        string path = Write(fileName, FirmwareImage.ToUf2(image, targetAddress: 0));
        Assert.That(await GetVersionAsync(path), Is.EqualTo("3.7.0+1.mp.8"));
    }

    /// <summary>
    /// A missing file is reported as such, only FirmwareUpdater checks for its existence first (FirmwareUpdater.cs:60-68)
    /// </summary>
    [Test]
    public void MissingFile()
    {
        Assert.ThatAsync(() => GetVersionAsync(Path.Combine(_directory, "missing.bin")), Throws.InstanceOf<FileNotFoundException>());
    }
    #endregion

    #region UF2
    private static async Task<byte[]> UnpackAsync(byte[] uf2)
    {
        using MemoryStream stream = new(uf2);
        await using MemoryStream unpacked = await Firmware.UnpackUF2Async(stream);
        Assert.That(unpacked.Position, Is.Zero, "The unpacked stream must start at the beginning");
        return unpacked.ToArray();
    }

    /// <summary>
    /// The payloads of the blocks are concatenated in the order of the blocks, so a partly filled last block adds only its payload
    /// </summary>
    [TestCase(256, 3000)]
    [TestCase(476, 476 * 3)]
    [TestCase(1, 5)]
    public async Task UnpackUf2(int payloadSize, int length)
    {
        byte[] image = FirmwareImage.Build("Duet EXP1HCL firmware version 3.7.0", length: length < 0x500 ? 0x500 : length);
        image = image[..length];
        Assert.That(await UnpackAsync(FirmwareImage.ToUf2(image, payloadSize: payloadSize)), Is.EqualTo(image));
    }

    /// <summary>
    /// Blocks flagged as not for the main flash are left out (FlagNoFlash, Firmware.cs:79-82)
    /// </summary>
    [Test]
    public async Task UnpackUf2WithoutBlocksForOtherMemory()
    {
        byte[] image = FirmwareImage.Build("Duet EXP1HCL firmware version 3.7.0", length: 2000);
        Dictionary<int, (uint, byte[])> comments = new()
        {
            [0] = (FirmwareImage.Uf2NotMainFlash, Encoding.ASCII.GetBytes("comment before the first block")),
            [3] = (FirmwareImage.Uf2NotMainFlash, Encoding.ASCII.GetBytes("comment in between"))
        };
        Assert.That(await UnpackAsync(FirmwareImage.ToUf2(image, extraBlocks: comments)), Is.EqualTo(image));
    }

    private static IEnumerable<TestCaseData> InvalidUf2Files()
    {
        byte[] uf2 = FirmwareImage.ToUf2(FirmwareImage.Build("Duet EXP1HCL firmware version 3.7.0", length: 1024, versionTextOffset: 0x100));

        yield return new TestCaseData(uf2[..^1], "UF2 file size must be a multiple of 512 bytes").SetName("{m}(Size)");

        byte[] badStart = (byte[])uf2.Clone();
        badStart[512 + 4] ^= 1;
        yield return new TestCaseData(badStart, "Invalid magic start in UF2 block").SetName("{m}(MagicStart)");

        byte[] badEnd = (byte[])uf2.Clone();
        badEnd[508] ^= 1;
        yield return new TestCaseData(badEnd, "Invalid magic end in UF2 block").SetName("{m}(MagicEnd)");

        byte[] badPayload = (byte[])uf2.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badPayload.AsSpan(16), 477);
        yield return new TestCaseData(badPayload, "Invalid payload size in UF2 block").SetName("{m}(PayloadSize)");

        // The blocks claim one block more than the file has
        byte[] truncated = (byte[])uf2.Clone();
        for (int offset = 0; offset < truncated.Length; offset += 512)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(offset + 24), (uint)(truncated.Length / 512 + 1));
        }
        yield return new TestCaseData(truncated, "Unexpected end in UF2 file").SetName("{m}(Truncated)");
    }

    /// <summary>
    /// An invalid UF2 file throws an IOException (Firmware.cs:40-76)
    /// </summary>
    [TestCaseSource(nameof(InvalidUf2Files))]
    public void InvalidUf2(byte[] uf2, string message)
    {
        Assert.ThatAsync(() => UnpackAsync(uf2), Throws.InstanceOf<IOException>().With.Message.EqualTo(message));
    }

    /// <summary>
    /// GetFirmwareVersionAsync does not catch it, so an invalid .uf2 file fails the version check instead of giving no version
    /// </summary>
    [Test]
    public void VersionOfInvalidUf2File()
    {
        string path = Write("Duet3Firmware_Mini5plus.uf2", new byte[513]);
        Assert.ThatAsync(() => GetVersionAsync(path), Throws.InstanceOf<IOException>());
    }

    /// <summary>
    /// The extension decides whether a file is unpacked (Firmware.cs:117), so UF2 content in a file named otherwise is read as a binary,
    /// whose words at 0x20 and 0x24 are then the first payload bytes of the first block
    /// </summary>
    [Test]
    public async Task Uf2ContentNotUnpacked()
    {
        byte[] image = FirmwareImage.Build("RepRapFirmware for Duet 3 Mini 5+ version 3.7.0", length: 3000, loadAddress: 0);
        string path = Write("Duet3Firmware_Mini5plus.bin", FirmwareImage.ToUf2(image, targetAddress: 0));
        Assert.That(await GetVersionAsync(path), Is.Not.EqualTo("3.7.0"));
    }
    #endregion
}
