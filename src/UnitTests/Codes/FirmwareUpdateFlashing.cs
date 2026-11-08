using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using UnitTests.Utility;

namespace UnitTests.Codes;

/// <summary>
/// M997 updates of the main board that get as far as flashing. DCS handles M997 S0 for board 0 itself: it looks for the IAP binary and the
/// firmware, stops the plugins, sends both to the link (MCodeHandler.cs:1203-1318) and the LinkService flashes the firmware through the IAP
/// program (LinkService.cs:62-156). The fake firmware emulates RRF and the IAP program for that (FakeFirmwareIap.cs). The paths that end before
/// an update starts are in <see cref="FirmwareUpdates"/>
/// </summary>
[TestFixture]
public class FirmwareUpdateFlashing
{
    private const string IapFile = "Duet3_SBCiap32_MB6HC.bin";

    // Board firmware file of the fake object model
    private const string FirmwareFile = "Duet3Firmware_MB6HC.bin";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private FakeFirmwareHost? _host;
    private byte[] _iap = null!, _firmware = null!;

    [SetUp]
    public void CreateBinaries()
    {
        _iap = new byte[2 * Consts.IapSegmentSize + 928];
        new Random(1).NextBytes(_iap);
        _firmware = FirmwareImage.Build("RepRapFirmware for Duet 3 MB6HC version 3.7.0+1.mp.9", length: 2 * Consts.FirmwareSegmentSize + 904, loadAddress: 0x00400000);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    /// <summary>
    /// Start the host with RRF reporting the IAP file of the MB6HC. The start error file is moved to the temporary directory
    /// </summary>
    private async Task<FakeFirmwareHost> StartAsync(Action<FakeFirmware.FakeFirmware>? configure = null)
    {
        _host = await FakeFirmwareHost.StartAsync(
            firmware =>
            {
                firmware.ObjectModel.Set("boards[0].iapFileNameSBC", IapFile);
                configure?.Invoke(firmware);
            },
            settings => settings.StartErrorFile = Path.Combine(settings.BaseDirectory, "startError.txt"));
        return _host;
    }

    private void WriteFile(string directory, string fileName, byte[] content) => System.IO.File.WriteAllBytes(Path.Combine(_host!.BaseDirectory, directory, fileName), content);

    private string StartErrorFile => Path.Combine(_host!.BaseDirectory, "startError.txt");

    private CancellationToken Stopping => _host!.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

    /// <summary>
    /// Wait until DCS is told to stop
    /// </summary>
    private async Task<bool> StopsAsync(TimeSpan timeout)
    {
        try
        {
            await Task.Delay(timeout, Stopping);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>
    /// Check that RRF got the IAP binary in segments of Consts.IapSegmentSize and started it, and that the IAP program got the firmware
    /// </summary>
    private void AssertFlashed(byte[] firmware)
    {
        FakeFirmware.FakeFirmware fake = _host!.Firmware;
        IReadOnlyList<byte[]> segments = fake.GetIapSegments();
        Assert.That(segments.Select(segment => segment.Length), Is.EqualTo(new[] { Consts.IapSegmentSize, Consts.IapSegmentSize, 928 }));
        Assert.That(segments.SelectMany(segment => segment), Is.EqualTo(_iap));
        Assert.That(fake.IapFirmwareLength, Is.EqualTo((uint)firmware.Length));

        // The IAP program writes whole blocks, DSF fills the last one with 0xFF (SPI.cs:1201-1205)
        Assert.That(fake.GetFlashedFirmware(), Is.EqualTo(FirmwareImage.Pad(firmware, Consts.FirmwareSegmentSize)));
        Assert.That(fake.GetFlashVerifications()[^1], Is.EqualTo(((long)firmware.Length, FirmwareImage.IapCrc16(firmware))));
        Assert.That(fake.IapRebooted, Is.True);
    }

    /// <summary>
    /// M997 without a P parameter flashes boards[0].firmwareFileName with boards[0].iapFileNameSBC, both from directories.firmware.
    /// The plugins are stopped first, the machine status is Updating from then on, and DCS stops a moment after the reply so that systemd starts it
    /// again for the new firmware (MCodeHandler.cs:1262-1316, LinkService.cs:64-67). The wiki: S0 is the default and B is 0 for the main board
    /// </summary>
    [TestCase("M997", true)]
    [TestCase("M997 S0", false)]
    [TestCase("M997 B0", false)]
    public async Task UpdateFlashesTheMainBoard(string code, bool checkStop)
    {
        FakeFirmwareHost host = await StartAsync();
        WriteFile("firmware", IapFile, _iap);
        WriteFile("firmware", FirmwareFile, _firmware);
        using (await host.Model.AccessReadWriteAsync())
        {
            host.Model.State.PluginsStarted = true;
        }

        Assert.That(await host.ExecuteAsync(code), Is.EqualTo("\n"));
        AssertFlashed(_firmware);
        Assert.That(host.Firmware.GetFlashVerifications(), Has.Count.EqualTo(1));
        Assert.That(host.Firmware.GetReceivedCodes().Select(item => item.Text), Is.Empty, "M997 S0 must not go to RRF");
        using (await host.Model.AccessReadOnlyAsync())
        {
            Assert.That(host.Model.State.Status, Is.EqualTo(MachineStatus.Updating));
            Assert.That(host.Model.State.PluginsStarted, Is.False);
        }
        if (checkStop)
        {
            Assert.That(await StopsAsync(StopTimeout), Is.True, "DCS did not stop after the update");
        }
        Assert.That(System.IO.File.Exists(StartErrorFile), Is.False);
    }

    /// <summary>
    /// The wiki: P is a file name in directories.firmware or an absolute path (Gcodes.md, M997, "P parameter")
    /// </summary>
    [TestCase("custom.bin", "firmware")]
    [TestCase("0:/sys/custom.bin", "sys")]
    public async Task UpdateWithFirmwareFromParameter(string parameter, string directory)
    {
        FakeFirmwareHost host = await StartAsync();
        WriteFile("firmware", IapFile, _iap);
        WriteFile("firmware", FirmwareFile, new byte[100]);
        WriteFile(directory, "custom.bin", _firmware);

        Assert.That(await host.ExecuteAsync($"M997 P\"{parameter}\""), Is.EqualTo("\n"));
        AssertFlashed(_firmware);
    }

    /// <summary>
    /// The Duet 3 Mini reports a .uf2 firmware file (Pins_Duet3Mini.h:18-29). DCS unpacks it and flashes the image (MCodeHandler.cs:1272-1276)
    /// </summary>
    [Test]
    public async Task UpdateWithUf2File()
    {
        FakeFirmwareHost host = await StartAsync(firmware =>
        {
            firmware.ObjectModel.Set("boards[0].firmwareFileName", "Duet3Firmware_Mini5plus.uf2");
            firmware.ObjectModel.Set("boards[0].iapFileNameSBC", "Duet3_SBCiap32_Mini5plus.bin");
        });
        byte[] image = FirmwareImage.Build("RepRapFirmware for Duet 3 Mini 5+ version 3.7.0+1.mp.9", length: 5000, loadAddress: 0);
        WriteFile("firmware", "Duet3_SBCiap32_Mini5plus.bin", _iap);
        WriteFile("firmware", "Duet3Firmware_Mini5plus.uf2", FirmwareImage.ToUf2(image, targetAddress: 0));

        Assert.That(await host.ExecuteAsync("M997"), Is.EqualTo("\n"));
        AssertFlashed(image);
    }

    /// <summary>
    /// The extension is compared without regard to case, like the SD card IAP program of the same release does (Duet3D/DuetIAP 3.7-dev
    /// src/iap_sd.cpp:133). Flashing the UF2 container itself would make the board reboot into an image whose first word is the UF2 magic
    /// instead of a stack pointer, so it would not start until it is recovered by hand
    /// </summary>
    [TestCase("Duet3Firmware_Mini5plus.UF2")]
    [TestCase("Duet3Firmware_Mini5plus.Uf2")]
    public async Task UpdateWithUf2FileInUpperCase(string fileName)
    {
        FakeFirmwareHost host = await StartAsync();
        byte[] image = FirmwareImage.Build("RepRapFirmware for Duet 3 Mini 5+ version 3.7.0+1.mp.9", length: 5000, loadAddress: 0);
        WriteFile("firmware", IapFile, _iap);
        WriteFile("firmware", fileName, FirmwareImage.ToUf2(image, targetAddress: 0));

        Assert.That(await host.ExecuteAsync($"M997 P\"{fileName}\""), Is.EqualTo("\n"));
        AssertFlashed(image);
    }

    /// <summary>
    /// When the CRC16 does not match, DCS sends the firmware again from the start, up to three attempts (LinkService.cs:101-143), as the IAP program
    /// expects after a mismatch (src/iap.cpp:690-748)
    /// </summary>
    [Test]
    public async Task UpdateRetriesAfterChecksumErrors()
    {
        FakeFirmwareHost host = await StartAsync(firmware => firmware.FlashAttemptsToCorrupt = 2);
        WriteFile("firmware", IapFile, _iap);
        WriteFile("firmware", FirmwareFile, _firmware);

        Assert.That(await host.ExecuteAsync("M997"), Is.EqualTo("\n"));
        AssertFlashed(_firmware);
        Assert.That(host.Firmware.GetFlashVerifications(), Has.Count.EqualTo(3));
        Assert.That(System.IO.File.Exists(StartErrorFile), Is.False);
    }

    /// <summary>
    /// After the third mismatch DCS gives up with an IOException (LinkService.cs:145-151). M997 reports it, writes it to the start error file for
    /// DWS and stops DCS (MCodeHandler.cs:1287-1313). The IAP program resets after its third mismatch as well (src/iap.cpp:720-727)
    /// </summary>
    [Test]
    public async Task UpdateFailsAfterThreeChecksumErrors()
    {
        FakeFirmwareHost host = await StartAsync(firmware => firmware.FlashAttemptsToCorrupt = 3);
        WriteFile("firmware", IapFile, _iap);
        WriteFile("firmware", FirmwareFile, _firmware);

        const string error = "Failed to update firmware: Checksum verification failed after 3 attempts";
        Assert.That(await host.ExecuteAsync("M997"), Is.EqualTo($"Error: M997: {error}\n"));
        Assert.That(host.Firmware.GetFlashVerifications(), Has.Count.EqualTo(3));
        Assert.That(host.Firmware.IapRebooted, Is.False);
        Assert.That(System.IO.File.ReadAllText(StartErrorFile), Is.EqualTo(error));
        Assert.That(await StopsAsync(StopTimeout), Is.True, "DCS did not stop after the failed update");
    }

    /// <summary>
    /// A start error file that cannot be written only costs a warning in the log, the error reply stays (MCodeHandler.cs:1291-1298)
    /// </summary>
    [Test]
    public async Task FailedUpdateWithoutStartErrorFile()
    {
        _host = await FakeFirmwareHost.StartAsync(
            firmware =>
            {
                firmware.ObjectModel.Set("boards[0].iapFileNameSBC", IapFile);
                firmware.FlashAttemptsToCorrupt = 3;
            },
            settings => settings.StartErrorFile = Path.Combine(settings.BaseDirectory, "missing", "startError.txt"));
        WriteFile("firmware", IapFile, _iap);
        WriteFile("firmware", FirmwareFile, _firmware);

        Assert.That(await _host.ExecuteAsync("M997"), Is.EqualTo("Error: M997: Failed to update firmware: Checksum verification failed after 3 attempts\n"));
        Assert.That(Directory.Exists(Path.Combine(_host.BaseDirectory, "missing")), Is.False);
    }

    /// <summary>
    /// When the IAP binary or the firmware is not in directories.firmware, DCS falls back to 0:/firmware and then to directories.system, where the
    /// files were kept before RRF 3.3 (MCodeHandler.cs:1230-1260, the wiki: Gcodes.md, M997 notes)
    /// </summary>
    [Test]
    public async Task UpdateWithFilesInFallbackDirectories()
    {
        FakeFirmwareHost host = await StartAsync(firmware => firmware.ObjectModel.Set("directories", System.Text.Json.Nodes.JsonNode.Parse("""{ "firmware": "0:/fw/" }""")));
        Directory.CreateDirectory(Path.Combine(host.BaseDirectory, "fw"));
        using (await host.Model.AccessReadOnlyAsync())
        {
            Assert.That(host.Model.Directories.Firmware, Is.EqualTo("0:/fw/"));
        }
        WriteFile("firmware", IapFile, _iap);
        WriteFile("sys", FirmwareFile, _firmware);

        Assert.That(await host.ExecuteAsync("M997"), Is.EqualTo("\n"));
        AssertFlashed(_firmware);
    }

    /// <summary>
    /// Without boards there is nothing to take the file names from (MCodeHandler.cs:1212-1215)
    /// </summary>
    [Test]
    public async Task UpdateWithoutBoards()
    {
        FakeFirmwareHost host = await StartAsync(firmware => firmware.ObjectModel.Set("boards", new System.Text.Json.Nodes.JsonArray()));
        Assert.That(await host.ExecuteAsync("M997"), Is.EqualTo("Error: M997: No boards have been detected\n"));
        Assert.That(host.Firmware.GetIapSegments(), Is.Empty);
    }

    /// <summary>
    /// A second update while the first one waits for the LinkService fails without stopping DCS (MCodeHandler.cs:1282-1286, LinkInterface.cs:523-526).
    /// The transfers are held so that the LinkService cannot take the first update, and the second M997 gets through its flush because its channel is
    /// idle (Channel/Processor.cs:521-528)
    /// </summary>
    [Test]
    public async Task UpdateWhileAnotherUpdateIsPending()
    {
        FakeFirmwareHost host = await StartAsync();
        WriteFile("firmware", IapFile, _iap);
        WriteFile("firmware", FirmwareFile, _firmware);

        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, FakeFirmwareHost.DefaultTimeout), Is.True);
        Task first = host.Services.GetRequiredService<DuetControlServer.Link.LinkInterface>().UpdateFirmware(new MemoryStream(_iap), new MemoryStream(_firmware));

        Assert.That(await host.ExecuteAsync("M997"), Is.EqualTo("Error: M997: Firmware is already being updated\n"));
        host.Firmware.ReleaseTransfers();
        await first.WaitAsync(FakeFirmwareHost.DefaultTimeout);
        AssertFlashed(_firmware);
        Assert.That(await StopsAsync(TimeSpan.FromSeconds(1.5)), Is.False, "The refused M997 must not stop DCS");
    }

    /// <summary>
    /// When the IAP program stops answering, the SPI adapter times out waiting for TfrRdy and throws an OperationCanceledException (SPI.cs:1207,
    /// 1741-1780). The LinkService hands it to M997 "which reports it and stops DCS" (LinkService.cs:319-324), but M997 catches every exception except
    /// OperationCanceledException (MCodeHandler.cs:1287), so the code ends as cancelled: no error reply, no start error file, and DCS keeps running
    /// although the transfer thread has ended (LinkService.cs:328) and the status stays Updating. By then the IAP program has erased the flash and
    /// resets into the bootloader (src/iap_spi.cpp:406-410, src/iap.cpp:772-789)
    /// </summary>
    [Test, Explicit("Bug: M997 does not report a TfrRdy timeout of the IAP program and DCS keeps running without a transfer thread")]
    public async Task UpdateFailsWhenTheIapProgramStopsAnswering()
    {
        FakeFirmwareHost host = await StartAsync(firmware => firmware.IapStopsAfterBlocks = 1);
        WriteFile("firmware", IapFile, _iap);
        WriteFile("firmware", FirmwareFile, _firmware);

        Assert.That(await host.ExecuteAsync("M997"), Does.StartWith("Error: M997: Failed to update firmware: "));
        Assert.That(System.IO.File.Exists(StartErrorFile), Is.True);
        Assert.That(await StopsAsync(StopTimeout), Is.True, "DCS did not stop after the failed update");
    }

    /// <summary>
    /// RRF refuses to start an update from a firmware file whose first word cannot be read or is not the initial stack pointer of the board
    /// (RepRap.cpp:2038-2083, called by GCodes3.cpp:832), so an empty file never reaches the IAP program. DCS checks only that the file exists and
    /// starts the IAP program, which erases the flash and then waits in vain for the first block until it resets into the bootloader
    /// (src/iap.cpp:370-410, src/iap_spi.cpp:388-410). An empty file is what a failed upload or a full SD card leaves behind
    /// </summary>
    [Test, Explicit("Bug: M997 starts the IAP program with an empty firmware file, which erases the flash and leaves the board unbootable")]
    public async Task UpdateRefusesEmptyFirmwareFile()
    {
        FakeFirmwareHost host = await StartAsync();
        WriteFile("firmware", IapFile, _iap);
        WriteFile("firmware", FirmwareFile, []);

        Assert.That(await host.ExecuteAsync("M997"), Does.StartWith("Error: M997: "));
        Assert.That(host.Firmware.GetIapSegments(), Is.Empty, "RRF must not get the IAP binary");
    }
}
