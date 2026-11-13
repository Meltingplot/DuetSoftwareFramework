using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer.Link;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Link;

/// <summary>
/// An emergency stop and a firmware reset that are both pending when the transfer thread starts its next cycle. The thread handles the reset
/// first and ends the link with it (LinkService.Execute). RRF performs an emergency stop before it resets (SbcInterface.cpp:377-380 at
/// Meltingplot/RepRapFirmware a7199f29f, :332-335 at Duet3D 3.7-dev 612b365aa), so the reset completes the stop as well. Without that the stop
/// stays pending after the link has ended, and M112 replies "Halt timed out, stopping DCS" after 4.5 s (MCodeHandler.cs, M112)
/// </summary>
[TestFixture]
public class EmergencyStopWithReset
{
    private static readonly TimeSpan Timeout = FakeFirmwareHost.DefaultTimeout;

    [Test]
    public async Task ResetCompletesThePendingStop()
    {
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        LinkInterface link = host.Services.GetRequiredService<LinkInterface>();
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True, "The transfers were not held");

        Task stop = link.EmergencyStopAsync();
        Assert.That(SpinWait.SpinUntil(() => link.FirmwareHaltRequest is not null, Timeout), Is.True, "The stop was not requested");
        Task reset = link.ResetFirmwareAsync();
        Assert.That(SpinWait.SpinUntil(() => link.FirmwareResetRequest is not null, Timeout), Is.True, "The reset was not requested");
        host.Firmware.ReleaseTransfers();

        await reset.WaitAsync(Timeout);
        Assert.That(host.Firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.Reset), Is.True, "RRF did not get the reset");
        Assert.That(async () => await stop.WaitAsync(Timeout), Throws.Nothing, "The reset did not complete the stop");
        Assert.That(link.FirmwareHaltRequest, Is.Null);
    }
}
