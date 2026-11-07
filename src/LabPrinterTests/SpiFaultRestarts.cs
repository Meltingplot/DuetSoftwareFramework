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
public class SpiFaultRestarts : LabTest
{
    private const string ControlFile = "/var/tmp/lab-tests/spi-fault";

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

        // The stop comes from systemd's line and not from the message of DCS (SPI.cs). A stuck link logs so much that journald drops that message,
        // see design/open-findings.md
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
