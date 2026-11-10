using System;
using System.Linq;
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

    /// <summary>
    /// Number of restarts of <see cref="NoReplyOfTheOldConnectionAfterRestart"/>, LAB_DCS_RESTARTS overrides it
    /// </summary>
    private static int Restarts => int.TryParse(Environment.GetEnvironmentVariable("LAB_DCS_RESTARTS"), out int restarts) ? restarts : 20;

    /// <summary>
    /// After a restart of DCS no reply of a code of the old connection may reach the new one. Since 2026-10-29 the journal showed
    /// "Daemon: Out-of-order reply ''" within 2 s of a DCS start in about 18 of 430 starts (design/lab-test-plan.md, section 14): RRF wrote the reply
    /// of a code of daemon.g, which it had taken in the old connection, to the new one. With no code waiting there it only gave that line; a code
    /// waiting on that channel would have been completed with it. RRF with the channel epoch writes no reply of a code of an ended connection
    /// (design/sbc-link-v8-requirements.md, section 10.9). The case is rare, so the test restarts DCS many times and reads the journal;
    /// one run without the line proves little, use LAB_DCS_RESTARTS=100 to compare with the rate above
    /// </summary>
    [Test, Explicit("Restarts DCS and resets the firmware many times, each restart is an emergency stop of RRF that switches ATX off on the fork")]
    public async Task NoReplyOfTheOldConnectionAfterRestart()
    {
        string cursor = await Lab.GetJournalCursorAsync();
        for (int i = 0; i < Restarts; i++)
        {
            // daemon.g of the machine runs in a loop, give it time to have a code in RRF again
            await Task.Delay(TimeSpan.FromSeconds(3));
            await Lab.RecoverAsync();
        }
        await ReconnectSessionAsync();

        string[] lines = (await Lab.ReadJournalAsync(cursor)).Split('\n');
        string[] outOfOrder = [.. lines.Where(line => line.Contains("Out-of-order reply"))];
        TestContext.Progress.WriteLine($"{Restarts} restarts, {lines.Count(line => line.Contains("Duet Control Server v"))} DCS starts in the journal, {outOfOrder.Length} out-of-order replies");
        Assert.That(outOfOrder, Is.Empty, $"A reply reached a connection that has no code for it:\n{string.Join("\n", outOfOrder)}");
    }
}
