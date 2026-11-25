using System;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Restarts of DCS that follow a single corrupted SPI exchange, on the firmware that is on the printer
/// </summary>
/// <remarks>
/// Needs the fault injection build of DCS: run-lab-tests.sh --side-load --fault-injection --filter "FullyQualifiedName~SpiFaultRestarts".
/// Each test costs a DCS restart and M999, which Tim allows unattended
/// </remarks>
[TestFixture, Category("FaultInjection"), Explicit("Needs the fault injection build of DCS")]
public class SpiFaultRestarts : SpiFaultTest
{
    /// <summary>
    /// Leaves the link stuck when it hits the transfer that activates the link capabilities, see UnitTests LinkRecovery.LabFaultInActivationRestartsDcs
    /// </summary>
    private const string ActivationFault = "Activation DataResponse ToRrf word 0x00000006";

    /// <summary>
    /// Repaired by the protocol, but RRF then waits at its idle header with TfrRdy low, see UnitTests LinkRecovery.LabFaultLeavesTfrRdyLow
    /// </summary>
    private static readonly string[] TfrRdyLowFaults =
    [
        "RrfData HeaderResponse ToDsf zero 0 4",
        "RrfData HeaderResponse ToRrf zero 0 4",
        "RrfData Data ToDsf word 0xfefefefe"
    ];

    /// <summary>
    /// Arm a fault and wait until DCS has read it. A build without fault injection never does
    /// </summary>
    private static async Task ArmAsync(string fault)
    {
        try
        {
            await Lab.RunOnSbcAsync($"""
                cursor=$(sudo -n journalctl -u duetcontrolserver -n 1 --show-cursor -o cat --no-pager | sed -n 's/^-- cursor: //p')
                echo '{fault}' | sudo -n tee {ControlFile} > /dev/null
                for i in $(seq 50); do
                  sudo -n journalctl -u duetcontrolserver --after-cursor="$cursor" -o cat --no-pager | grep -qF 'SPI fault armed: {fault}' && exit 0
                  sleep 0.1
                done
                echo 'The fault was not armed' >&2
                exit 1
                """);
        }
        catch (InvalidOperationException e)
        {
            Assert.Ignore($"Is the fault injection build side-loaded? {e.Message}");
        }
    }

    private static double? JournalTime(string report, string text)
    {
        Match line = Regex.Match(report, $@"^(\d+\.\d+) .*{Regex.Escape(text)}", RegexOptions.Multiline);
        return line.Success ? double.Parse(line.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// When both sides keep exchanging out of step, no timeout of either side ends that. DCS stops once no transfer has completed for
    /// SbcRestartTimeout (60 s), systemd starts it again, and the link comes back (#102). On the extended link, the census predicts this only in the
    /// transfer that activates the link capabilities. A watchdog reset makes DCS connect and activate again while the armed fault waits for it
    /// </summary>
    [Test]
    public async Task StuckLinkRestartsDcs()
    {
        string cursor = await Lab.GetJournalCursorAsync(), pid = await Lab.GetControlServerPidAsync();
        await ArmAsync(ActivationFault);
        try
        {
            await SendCodeAsync("M122 P1001");
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            // RRF may restart before it replies
        }

        string report = await Lab.RunOnSbcAsync($"""
            j="sudo -n journalctl -u duetcontrolserver --after-cursor={cursor} -o short-unix --no-pager"
            injected=0
            for i in $(seq 300); do
              if $j | grep -qF 'SPI fault injected: {ActivationFault},'; then injected=1; break; fi
              sleep 0.1
            done
            : | sudo -n tee {ControlFile} > /dev/null
            if [ $injected = 1 ]; then
              for i in $(seq 120); do
                p=$(systemctl show -p MainPID --value duetcontrolserver)
                if [ "$p" != 0 ] && [ "$p" != "{pid}" ]; then echo RESTARTED; break; fi
                sleep 1
              done
            fi
            $j | grep -F -m1 'SPI fault injected: {ActivationFault},'
            $j | sed -n '/SPI fault injected/,$p' | grep -F -m1 'Deactivated successfully'
            exit 0
            """);
        TestContext.Progress.WriteLine(report);

        // The stop comes from systemd's line and not from the message of DCS (SPI.cs). A stuck link logs so much that journald drops that message
        // when DCS writes all lines with one priority, as builds before the journal priorities do
        double? injectedAt = JournalTime(report, "SPI fault injected"), stoppedAt = JournalTime(report, "Deactivated successfully");
        if (injectedAt is null || !report.Contains("RESTARTED"))
        {
            await Lab.RecoverAsync();
            Assert.Fail(injectedAt is null ? "The fault did not hit the transfer that activates the link capabilities" : "DCS was not restarted, although the census predicts a stuck link");
        }

        await Lab.ResetHaltedFirmwareAsync();
        await ReconnectSessionAsync();
        string journal = await Lab.ReadJournalAsync(cursor);
        TestContext.Progress.WriteLine($"Injected at {injectedAt:F3}, DCS stopped {stoppedAt - injectedAt:F2} s later");

        Assert.Multiple(() =>
        {
            Assert.That(stoppedAt, Is.Not.Null, "systemd did not report that DCS stopped");
            Assert.That(stoppedAt - injectedAt, Is.InRange(55.0, 70.0), "DCS did not stop after SbcRestartTimeout (60 s)");
            Assert.That(Regex.Count(journal, "Link capabilities All are active"), Is.GreaterThanOrEqualTo(1), "The new DCS did not activate the link capabilities");
        });
    }

    /// <summary>
    /// Time the link stays stuck before DCS is stopped, as in the probe of 2026-11-08 (design/lab-test-plan.md:589). It is far below SbcRestartTimeout
    /// (60 s, Settings.cs:188), after which DCS would stop on its own
    /// </summary>
    private const int StuckSeconds = 5;

    /// <summary>
    /// Longest stop of DCS that counts as ending with the stop. The transfer ends at its next round or after a wait for TfrRdy in progress, which is at most
    /// SbcConnectionTimeout + SbcTransferTimeout (4.5 s, SPI.cs:1776, Settings.cs:178, :183); measured 0.03 s with the fix (design/lab-test-plan.md:592).
    /// A regression takes 10 s or 15 s
    /// </summary>
    private const double MaxStopSeconds = 6;

    /// <summary>
    /// A stop of DCS while the SPI link is stuck ends at once. The transfer loop used to wait for ApplicationStopped, which the host fires only after every
    /// service has stopped, so the stuck transfer kept LinkService from stopping: systemctl restart took 15.14 s and 15.18 s and ended with
    /// "State 'stop-sigterm' timed out. Killing." and SIGKILL (design/lab-test-plan.md:591). DSF #149 passes the stopping token of the service to
    /// PerformFullTransfer (LinkService.cs:336, :652), whose loop ends with it (SPI.cs:325). It also sets the shutdown timeout of the host to 10 s
    /// (Program.cs:177), below TimeoutStopSec=15 (duetcontrolserver.service:8), so a regression of the token alone ends after about 10 s without SIGKILL, and
    /// one of both after 15 s with SIGKILL. The test therefore asserts the time of the stop, not only the missing SIGKILL, and names which one it was.
    /// The link is made stuck as by <see cref="StuckLinkRestartsDcs"/>: the activation fault and a watchdog reset; it is stopped <see cref="StuckSeconds"/> s
    /// after the injection, long before DCS would stop on its own. The time is the one DCS spent deactivating, from ActiveExitTimestampMonotonic to
    /// InactiveEnterTimestampMonotonic of the unit; the time systemctl stop took is the fallback.
    /// StuckLinkRestartsDcs does not show this, because there DCS stops itself after SbcRestartTimeout (SPI.cs:329-332) before systemd stops it.
    /// Deterministic as far as the link gets stuck (2 of 2 on 2026-11-07, census LabFaultInActivationRestartsDcs); a run in which it does not is inconclusive.
    /// Costs a watchdog reset (M122 P1001), a DCS stop and start and M999, each lost link an emergency stop with ATX off on the fork: 2 in all
    /// </summary>
    [Test]
    public async Task StopWhileTheLinkIsStuck()
    {
        string cursor = await Lab.GetJournalCursorAsync(), pid = await Lab.GetControlServerPidAsync();
        await ArmAsync(ActivationFault);
        try
        {
            await SendCodeAsync("M122 P1001");
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            // RRF may restart before it replies
        }

        // One round trip: wait for the injection, let the link stay stuck, then time the stop and start DCS again. Stopping DCS stops the plugin services
        string report = await Lab.RunOnSbcAsync($"""
            j="sudo -n journalctl -u duetcontrolserver --after-cursor={cursor} -o cat --no-pager"
            injected=0
            for i in $(seq 300); do
              if $j | grep -qF 'SPI fault injected: {ActivationFault},'; then injected=1; break; fi
              sleep 0.1
            done
            : | sudo -n tee {ControlFile} > /dev/null
            if [ $injected != 1 ]; then echo NOTINJECTED; exit 0; fi
            sleep {StuckSeconds}
            echo "PID $(systemctl show -p MainPID --value duetcontrolserver)"
            $j | sed -n '/SPI fault injected/,$p' | grep -E 'Lost connection to Duet|Connection to Duet established|Link capabilities .* are active' | head -5
            start=$(date +%s.%N)
            sudo -n systemctl stop duetcontrolserver
            echo "STOPPED $start $(date +%s.%N)"
            systemctl show -p ActiveExitTimestampMonotonic -p InactiveEnterTimestampMonotonic duetcontrolserver
            $j | grep -E "stop-sigterm|SIGKILL" | head -5
            sudo -n systemctl start duetcontrolserver || echo STARTFAILED
            sudo -n systemctl start duetpluginservice duetpluginservice-root
            exit 0
            """);
        TestContext.Progress.WriteLine(report);

        if (report.Contains("NOTINJECTED"))
        {
            await Lab.RecoverAsync();
            Assert.Fail("The fault did not hit the transfer that activates the link capabilities");
        }
        await Lab.ResetHaltedFirmwareAsync();
        await ReconnectSessionAsync();

        // Stuck means: the same DCS, and after the injection it lost the connection and neither connected again nor activated the capabilities
        string pidBeforeStop = Regex.Match(report, @"^PID (\d+)", RegexOptions.Multiline).Groups[1].Value;
        string afterInjection = report[report.IndexOf("PID ", StringComparison.Ordinal)..];
        if (pidBeforeStop != pid || !afterInjection.Contains("Lost connection to Duet", StringComparison.Ordinal) ||
            afterInjection.Contains("Connection to Duet established", StringComparison.Ordinal) || Regex.IsMatch(afterInjection, "Link capabilities .* are active"))
        {
            Assert.Inconclusive($"The link was not stuck when DCS was stopped (PID {pid} before, {pidBeforeStop} at the stop), so this run shows nothing");
        }

        // The time DCS spent deactivating, from systemd's timestamps of the unit (microseconds). systemctl stop also waits for the plugin services,
        // which stop with DCS (PartOf=duetcontrolserver.service, duetpluginservice.service:3), so its own time is only the fallback
        Match stopped = Regex.Match(report, @"^STOPPED (\d+\.\d+) (\d+\.\d+)", RegexOptions.Multiline);
        Assert.That(stopped.Success, Is.True, "systemctl stop did not return");
        double wallSeconds = double.Parse(stopped.Groups[2].Value, CultureInfo.InvariantCulture) - double.Parse(stopped.Groups[1].Value, CultureInfo.InvariantCulture);
        long.TryParse(Regex.Match(report, @"^ActiveExitTimestampMonotonic=(\d+)", RegexOptions.Multiline).Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long activeExit);
        long.TryParse(Regex.Match(report, @"^InactiveEnterTimestampMonotonic=(\d+)", RegexOptions.Multiline).Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long inactiveEnter);
        double stopSeconds = (activeExit > 0 && inactiveEnter >= activeExit) ? (inactiveEnter - activeExit) / 1e6 : wallSeconds;
        bool killed = report.Contains("stop-sigterm", StringComparison.Ordinal) || report.Contains("SIGKILL", StringComparison.Ordinal);
        TestContext.Progress.WriteLine($"DCS stopped {stopSeconds:F2} s after systemctl stop while the link was stuck (systemctl stop returned after {wallSeconds:F2} s)" +
            (killed ? ", systemd killed it" : ""));
        Assert.Multiple(() =>
        {
            Assert.That(stopSeconds, Is.LessThanOrEqualTo(MaxStopSeconds),
                "DCS did not stop at once while the link was stuck, the transfer did not end with the stopping token (#149). " +
                (killed ? "systemd killed it, so the shutdown timeout of 10 s of the host (Program.cs:177) did not end it either"
                    : "About 10 s means that the shutdown timeout of the host ended it (Program.cs:177)"));
            Assert.That(killed, Is.False, "systemd killed DCS: State 'stop-sigterm' timed out or SIGKILL in the journal");
        });
    }

    /// <summary>
    /// A recovered fault can leave RRF at its idle header with TfrRdy low, but a new DCS expected it high and failed its first start after 500 ms.
    /// It now waits for the connection timeout of RRF (#99). The pin decides whether a fault shows that, so a fault that leaves it high is not a result
    /// </summary>
    [Test]
    public async Task DcsStartsWhileTfrRdyIsLow()
    {
        foreach (string fault in TfrRdyLowFaults)
        {
            string cursor = await Lab.GetJournalCursorAsync();
            await ArmAsync(fault);
            string report = await Lab.RunOnSbcAsync($"""
                j="sudo -n journalctl -u duetcontrolserver --after-cursor={cursor} -o cat --no-pager"
                for i in $(seq 100); do
                  if $j | grep -qF 'SPI fault injected: {fault},'; then echo INJECTED; break; fi
                  sleep 0.1
                done
                : | sudo -n tee {ControlFile} > /dev/null
                sleep 0.5
                sudo -n systemctl stop duetcontrolserver
                echo "LEVEL $(sudo -n gpioget --numeric -c gpiochip0 25)"
                after=$(sudo -n journalctl -u duetcontrolserver -n 1 --show-cursor -o cat --no-pager | sed -n 's/^-- cursor: //p')
                sudo -n systemctl start duetcontrolserver || echo STARTFAILED
                sudo -n journalctl -u duetcontrolserver --after-cursor="$after" -o short-unix --no-pager | grep -E 'Duet Control Server v|Connected to controller over SPI|timeout while waiting for TfrRdy' | head -6
                sudo -n systemctl start duetpluginservice duetpluginservice-root
                exit 0
                """);
            TestContext.Progress.WriteLine($"{fault}:\n{report}");

            await Lab.ResetHaltedFirmwareAsync();
            await ReconnectSessionAsync();

            if (!report.Contains("INJECTED") || !report.Contains("LEVEL 0"))
            {
                continue;
            }

            double? startedAt = JournalTime(report, "Duet Control Server v"), connectedAt = JournalTime(report, "Connected to controller over SPI");
            TestContext.Progress.WriteLine($"TfrRdy was low, the new DCS connected after {connectedAt - startedAt:F2} s");
            Assert.Multiple(() =>
            {
                Assert.That(report, Does.Not.Contain("STARTFAILED"), "systemctl reported a failed start");
                Assert.That(report, Does.Not.Contain("timeout while waiting for TfrRdy"));
                Assert.That(Regex.Count(report, "Duet Control Server v"), Is.EqualTo(1), "DCS needed a second start");
                Assert.That(connectedAt, Is.Not.Null, "The new DCS did not connect");
                Assert.That(connectedAt - startedAt, Is.LessThanOrEqualTo(6.0), "The first wait is at most SbcConnectionTimeout + SbcTransferTimeout, 4.5 s");
            });
            return;
        }
        Assert.Inconclusive("None of the faults left TfrRdy low, so this run shows nothing. The census predicts that they do");
    }
}
