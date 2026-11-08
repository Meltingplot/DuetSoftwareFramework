using System;
using System.Linq;
using System.Threading.Tasks;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.FakeFirmware;

/// <summary>
/// DCS queries limits.sbcLinkCapabilities after every connect and reset and activates what RRF offers and the adapter supports.
/// RRF without the capabilities reports null for the key and must never get the activation request
/// </summary>
[TestFixture]
public class LinkCapabilityHandshake
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

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (DateTime timeout = DateTime.UtcNow + FakeFirmwareHost.DefaultTimeout; !condition(); await Task.Delay(10))
        {
            if (DateTime.UtcNow > timeout)
            {
                Assert.Fail($"Timeout while waiting for {what}");
            }
        }
    }

    private int Queries(string key) => _host!.Firmware.GetReceivedRequests().Count(request => request.Request == SbcRequest.GetObjectModel && request.Text == key);

    [Test]
    public async Task FirmwareWithoutCapabilitiesGetsNoActivation()
    {
        _host = await FakeFirmwareHost.StartAsync();
        await WaitUntil(() => Queries("limits.sbcLinkCapabilities") > 0, "the query of limits.sbcLinkCapabilities");

        // A code goes through after the answer, so the answer has been processed
        Assert.That(await _host.ExecuteAsync("G4 P1"), Is.EqualTo("\n"));
        Assert.That(_host.Firmware.LinkCapabilityRequests, Is.Empty);
        Assert.That(Queries("state.sbcLinkCapabilities"), Is.Zero);
    }

    /// <summary>
    /// An adapter that supports no capabilities keeps the plain link with RRF that offers them, so DCS does not even query them. The fault injection
    /// build of the SPI adapter does that when /var/tmp/lab-tests/spi-plain-link exists, so that the lab tests can check the plain link
    /// </summary>
    [Test]
    public async Task AdapterWithoutCapabilitiesKeepsThePlainLink()
    {
        _host = await FakeFirmwareHost.StartAsync(firmware =>
        {
            firmware.OfferedLinkCapabilities = LinkCapabilities.All;
            firmware.SupportedLinkCapabilities = LinkCapabilities.None;
        });

        // A code goes through, so the transfers after the connect have been processed
        Assert.That(await _host.ExecuteAsync("G4 P1"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("G4 P1"), Is.EqualTo("\n"));
        Assert.That(Queries("limits.sbcLinkCapabilities"), Is.Zero);
        Assert.That(_host.Firmware.LinkCapabilityRequests, Is.Empty);
        Assert.That(_host.Firmware.ActiveLinkCapabilities, Is.EqualTo(LinkCapabilities.None));
    }

    [Test]
    public async Task OfferedCapabilitiesAreActivated()
    {
        _host = await FakeFirmwareHost.StartAsync(firmware => firmware.OfferedLinkCapabilities = LinkCapabilities.All);
        await WaitUntil(() => _host.Firmware.ActiveLinkCapabilities == LinkCapabilities.All, "the activation");
        await WaitUntil(() => Queries("state.sbcLinkCapabilities") > 0, "the check of state.sbcLinkCapabilities");
        Assert.That(_host.Firmware.LinkCapabilityRequests, Is.EqualTo(new[] { LinkCapabilities.All }));
    }

    [Test]
    public async Task UnknownCapabilitiesAreNotActivated()
    {
        // A later RRF may offer more than this DCS knows
        _host = await FakeFirmwareHost.StartAsync(firmware => firmware.OfferedLinkCapabilities = LinkCapabilities.All | (LinkCapabilities)0x100);
        await WaitUntil(() => _host.Firmware.LinkCapabilityRequests.Count > 0, "the activation");
        Assert.That(_host.Firmware.LinkCapabilityRequests, Is.EqualTo(new[] { LinkCapabilities.All }));
    }

    [Test]
    public async Task CapabilitiesAreActivatedAgainAfterRestart()
    {
        _host = await FakeFirmwareHost.StartAsync(firmware => firmware.OfferedLinkCapabilities = LinkCapabilities.ExtendedHeader);
        await WaitUntil(() => _host.Firmware.ActiveLinkCapabilities == LinkCapabilities.ExtendedHeader, "the activation");

        // A restart of RRF ends the capabilities, so DCS queries and activates them again
        _host.Firmware.SimulateReset();
        Assert.That(_host.Firmware.ActiveLinkCapabilities, Is.EqualTo(LinkCapabilities.None));
        await WaitUntil(() => _host.Firmware.LinkCapabilityRequests.Count == 2, "the second activation");
        Assert.That(Queries("limits.sbcLinkCapabilities"), Is.EqualTo(2));
        Assert.That(_host.Firmware.ActiveLinkCapabilities, Is.EqualTo(LinkCapabilities.ExtendedHeader));
    }
}
