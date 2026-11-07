using System;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Machine;

/// <summary>
/// RRF reports the firmware and IAP file names of the main board only in verbose responses (Platform.cpp:220-229), and DSF asks for verbose
/// fields only in the first query of a connection. A reset of RRF must therefore make DSF query verbose again, else M997 and DCS -u cannot
/// update the main board any more (lab printer, 2026-11-07: firmwareFileName '' after a few watchdog resets)
/// </summary>
[TestFixture]
public class VerboseFieldsAfterReset
{
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
    /// Wait until the main board of DSF's model meets a condition
    /// </summary>
    private async Task<bool> WaitForMainBoardAsync(Func<MainBoard, bool> condition)
    {
        DateTime end = DateTime.UtcNow + FakeFirmwareHost.DefaultTimeout;
        while (DateTime.UtcNow < end)
        {
            using (await _host!.Model.AccessReadOnlyAsync())
            {
                if (_host.Model.Boards.Count > 0 && _host.Model.Boards[0] is MainBoard mainBoard && condition(mainBoard))
                {
                    return true;
                }
            }
            await Task.Delay(20);
        }
        return false;
    }

    /// <summary>
    /// The update service may start its next query between the reset and the moment DSF forgets the model of the old connection. Holding the
    /// model lock over the reset makes it do that every time: the lost connection waits for the lock, the next query does not
    /// </summary>
    [Test]
    public async Task ResetKeepsTheFirmwareFileNames()
    {
        _host = await FakeFirmwareHost.StartAsync(firmware => firmware.ObjectModel.Set("boards[0].iapFileNameSBC", "Duet3_SBCiap32_MB6HC.bin"));
        Assert.That(await WaitForMainBoardAsync(board => board.FirmwareFileName == "Duet3Firmware_MB6HC.bin" && board.IapFileNameSBC is not null),
            Is.True, "DSF did not read the verbose fields at the start");

        using (await _host.Model.AccessReadOnlyAsync())
        {
            _host.Firmware.SimulateReset();
            await Task.Delay(1000);
        }

        Assert.That(await WaitForMainBoardAsync(board => board.FirmwareFileName == "Duet3Firmware_MB6HC.bin" && board.IapFileNameSBC == "Duet3_SBCiap32_MB6HC.bin"),
            Is.True, "DSF did not read the verbose fields again after the reset");
    }
}
