using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Codes;

/// <summary>
/// Tests for M997. Only the paths that end before an update starts are used, because an update stops the plugins, flashes the
/// firmware and writes the start error file. The fake firmware does not emulate updates
/// </summary>
[TestFixture]
public class FirmwareUpdates
{
    private const string IapFile = "Duet3_SBCiap32_MB6HC.bin";

    // Board firmware file of the fake object model
    private const string FirmwareFile = "Duet3Firmware_MB6HC.bin";

    private FakeFirmwareHost? _host;

    [TearDown]
    public async Task TearDown()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    /// <summary>
    /// Start the host. The start error file is moved to the temporary directory in case an update is started after all
    /// </summary>
    /// <param name="iapFileName">IAP file name of the main board or null if RRF does not report one</param>
    private async Task<FakeFirmwareHost> StartAsync(string? iapFileName)
    {
        _host = await FakeFirmwareHost.StartAsync(
            firmware =>
            {
                if (iapFileName is not null)
                {
                    firmware.ObjectModel.Set("boards[0].iapFileNameSBC", iapFileName);
                }
            },
            settings => settings.StartErrorFile = Path.Combine(settings.BaseDirectory, "startError.txt"));
        return _host;
    }

    private string[] Received() => [.. _host!.Firmware.GetReceivedCodes().Select(code => code.Text)];

    [Test]
    public async Task UpdateWithoutIapFileName()
    {
        FakeFirmwareHost host = await StartAsync(null);

        Assert.That(await host.ExecuteAsync("M997"), Is.EqualTo("Error: M997: Cannot update firmware because IAP and firmware filenames are unknown\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task UpdateWithoutIapFile()
    {
        FakeFirmwareHost host = await StartAsync(IapFile);

        Assert.That(await host.ExecuteAsync("M997"), Is.EqualTo($"Error: M997: Failed to find IAP file {IapFile}\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task UpdateWithoutFirmwareFile()
    {
        FakeFirmwareHost host = await StartAsync(IapFile);
        System.IO.File.WriteAllText(Path.Combine(host.BaseDirectory, "firmware", IapFile), "iap");

        // The wiki: S0 (default) updates the main board firmware, which needs the IAP binary and the firmware on the SD card
        Assert.That(await host.ExecuteAsync("M997 S0"), Is.EqualTo($"Error: M997: Failed to find firmware file {FirmwareFile}\n"));
        Assert.That(await host.ExecuteAsync("M997 P\"custom.bin\""), Is.EqualTo("Error: M997: Failed to find firmware file custom.bin\n"));
        Assert.That(Received(), Is.Empty);
        Assert.That(System.IO.File.Exists(Path.Combine(host.BaseDirectory, "startError.txt")), Is.False);
    }

    [Test]
    public async Task UpdateWithIapFileInSystemDirectory()
    {
        // Before RRF 3.3 the update files were stored in 0:/sys (wiki), DCS still finds the IAP file there
        FakeFirmwareHost host = await StartAsync(IapFile);
        System.IO.File.WriteAllText(Path.Combine(host.BaseDirectory, "sys", IapFile), "iap");

        Assert.That(await host.ExecuteAsync("M997"), Is.EqualTo($"Error: M997: Failed to find firmware file {FirmwareFile}\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task UpdateDsfWithoutManagementPlugin()
    {
        FakeFirmwareHost host = await StartAsync(null);

        // The wiki: S2 updates the DSF packages, DuetPiManagementPlugin intercepts it
        Assert.That(await host.ExecuteAsync("M997 S2"), Is.EqualTo("Error: M997: Cannot update DSF because DuetPiManagementPlugin is not started\n"));
        Assert.That(Received(), Is.Empty);
    }

    [TestCase("M997 S1")]
    [TestCase("M997 B121")]
    [TestCase("M997 S3 B121")]
    public async Task UpdateOfOtherModule(string code)
    {
        FakeFirmwareHost host = await StartAsync(null);

        // Modules other than the main board firmware and DSF, and other boards, are updated by RRF
        Assert.That(await host.ExecuteAsync(code), Is.EqualTo("\n"));
        Assert.That(Received(), Is.EqualTo(new[] { code }));
    }
}
