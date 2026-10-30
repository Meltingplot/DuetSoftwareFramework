using System;
using System.Net.Http;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// DCS restarts and firmware resets
/// </summary>
[TestFixture, Category("Restart")]
public class ControlServerRestart : LabTest
{
    /// <summary>
    /// Stopping DCS stops the plugin services (PartOf), and starting DCS again did not bring them back, so dsf-config.g never ran
    /// (fixed in #37 with WantedBy=duetcontrolserver.service)
    /// </summary>
    [Test]
    public async Task PluginServicesStartWithControlServer()
    {
        try
        {
            await Lab.RunOnSbcAsync("""
                sudo -n systemctl stop duetcontrolserver
                sudo -n systemctl start duetcontrolserver
                """);
            string states = await Lab.RunOnSbcAsync("""
                for i in $(seq 30); do
                  systemctl is-active --quiet duetpluginservice duetpluginservice-root && break
                  sleep 1
                done
                systemctl is-active duetpluginservice duetpluginservice-root || true
                """);
            Assert.That(states.Split('\n', StringSplitOptions.RemoveEmptyEntries), Is.EqualTo(new[] { "active", "active" }), "duetpluginservice and duetpluginservice-root");
        }
        finally
        {
            await Lab.RunOnSbcAsync("sudo -n systemctl start duetpluginservice duetpluginservice-root");
            await Lab.ResetHaltedFirmwareAsync();
        }
    }

    /// <summary>
    /// After M999, DCS exits and systemd starts it again. On 3.7.0+mp.6 to mp.8 it hung in its shutdown until systemd killed it 15 s later
    /// (not fixed yet). So far it hung after the M999 a client sends at boot, while an M999 on a running printer restarted DCS within 15 s,
    /// so a pass here does not prove the hang is gone. A systemctl restart while the SPI link was stuck also hung
    /// </summary>
    [Test]
    public async Task ResetDoesNotHangShutdown()
    {
        string cursor = await Lab.GetJournalCursorAsync(), pid = await Lab.GetControlServerPidAsync();
        try
        {
            await SendCodeAsync("M999");
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            // DCS exits after sending the reset
        }
        await Lab.WaitForControlServerRestartAsync(pid);
        await Lab.ResetHaltedFirmwareAsync();

        string journal = await Lab.ReadJournalAsync(cursor);
        Assert.That(journal, Does.Contain("Resetting controller"));
        Assert.That(journal, Does.Not.Contain("State 'stop-sigterm' timed out"), "DCS hung in its shutdown after M999 until systemd killed it");
    }
}
