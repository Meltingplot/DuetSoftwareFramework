using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// DSF loses the connection on the extended link while a trigger macro runs. RRF must notice that DSF exchanges the transfer again in a new session,
/// because DSF drops the macro then, and RRF would otherwise wait for its end and hold back every later trigger macro
/// </summary>
/// <remarks>
/// Needs the fault injection build of DCS on the extended link: run-lab-tests.sh --side-load --fault-injection --filter "FullyQualifiedName~SpiFaultLostConnection".
/// The fault corrupts every header of one transfer on its way to RRF, so DSF gives up after its retries (SPI.ExchangeHeader, SPI.PerformFullTransfer).
/// Its counter value picks the transfer: with 0, DSF used to leave the extended link with a sequence number that follows the last one of RRF (P4 of the
/// stop path tests, fix/extended-link-reset-sequence); 1 to 31 worked before. The fault is checked offline by UnitTests LinkRecovery.LabFaultLosesConnectionAndRrfStops,
/// keep both in line. Uses trigger 30 with sys/trigger30.g like TriggerMacros. Each case costs an emergency stop of RRF, which on the fork also switches
/// ATX off, and M999; when RRF does not notice, it costs a DCS restart and M999
/// </remarks>
[TestFixture, Category("FaultInjection"), Category("Trigger"), Explicit("Needs the fault injection build of DCS")]
public class SpiFaultLostConnection : LabTest
{
    private const string ControlFile = "/var/tmp/lab-tests/spi-fault";
    private const int TriggerNumber = 30;
    private const string TriggerMacro = "0:/sys/trigger30.g";
    private const string ResetMacro = $"{Lab.MacroDirectory}/lost-connection/reset.g";

    /// <summary>
    /// Iterations of the loop in the trigger macro. Each sets a global variable and waits for RRF to do it (KeywordHandler.cs:279), so the macro still
    /// runs when the fault is armed, which takes a few hundred ms after the macro has started
    /// </summary>
    private const int MacroIterations = 5000;

    [OneTimeSetUp]
    public async Task UploadMacrosAsync()
    {
        Lab.RequirePrinter();
        await using DuetHttpSession session = await Lab.ConnectAsync();
        if ((await session.GetFileListAsync("0:/sys")).Any(item => item.Filename == "trigger30.g"))
        {
            Assert.Ignore($"{TriggerMacro} exists on this printer, so the tests do not touch trigger {TriggerNumber}");
        }

        await Lab.UploadAsync(session, ResetMacro, """
            if !exists(global.labTestTriggerRuns)
              global labTestTriggerRuns = 0
            else
              set global.labTestTriggerRuns = 0
            if !exists(global.labTestTriggerLoop)
              global labTestTriggerLoop = 0
            else
              set global.labTestTriggerLoop = 0

            """);
        await Lab.UploadAsync(session, TriggerMacro, $"""
            set global.labTestTriggerRuns = global.labTestTriggerRuns + 1
            while iterations < {MacroIterations}
              set global.labTestTriggerLoop = iterations

            """);
    }

    [OneTimeTearDown]
    public async Task DeleteMacrosAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, TriggerMacro);
            await Lab.DeleteAsync(session, ResetMacro);
            await Lab.DeleteAsync(session, $"{Lab.MacroDirectory}/lost-connection");
        }
    }

    /// <summary>
    /// The trigger fires twice per second while its macro is not running (TriggerMacros.TriggerMacroCallingMacro). Once the macro runs, the fault is armed,
    /// and it hits the next transfer with the counter value. RRF then has to stop: it resets the connection and performs an emergency stop
    /// (DataTransfer.cpp:1125-1131, DataTransfer.h:212-215, SbcInterface.cpp:194-230). If it does not, the trigger macro DSF dropped
    /// (LinkService.cs:333-340) stays open in RRF and holds back every later trigger macro (GCodes.cpp:423-433 and 1000 of RRF 3.7.0+1.mp.8), which this test reports
    /// </summary>
    /// <param name="counter">Extended counter value of the transfer that gets the fault</param>
    [TestCase(0), TestCase(16)]
    public async Task RrfStopsWhenDsfLosesTheConnectionInATriggerMacro(int counter)
    {
        string? link = await Lab.GetLinkModeAsync();
        if (link != "extended")
        {
            Assert.Ignore($"Needs the extended link, but DCS uses the {link ?? "unknown"} link. Run it without --plain-link");
        }
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain("is not configured"), $"Trigger {TriggerNumber} is in use");
        await SendCodeAsync($"M98 P\"{ResetMacro}\"");

        string fault = $"Any Header ToRrf times=1000 counter={counter} flip 96";
        string cursor = await Lab.GetJournalCursorAsync();
        bool halted = false;
        try
        {
            Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} P\"mod(state.msUpTime, 500) < 250\" R0"), Does.Not.Contain("Error"));

            // One round trip: wait for the macro, arm, wait until DSF is connected again, then report in the order of the journal
            string report = await Lab.RunOnSbcAsync($"""
                j="sudo -n journalctl -u duetcontrolserver --after-cursor={cursor} -o cat --no-pager"
                for i in $(seq 100); do
                  $j | grep -qE 'Starting macro file trigger30\.g on channel Trigger' && break
                  sleep 0.1
                done
                echo '{fault}' | sudo -n tee {ControlFile} > /dev/null
                for i in $(seq 100); do
                  $j | grep -qF 'Connection to controller has been reset' && break
                  sleep 0.1
                done
                : | sudo -n tee {ControlFile} > /dev/null
                sleep 1
                $j | grep -E 'trigger30\.g|SPI fault (armed|done)|SPI fault injected: .*\(1 of|Lost connection to Duet|Connection to Duet established|Connection to controller has been reset|Link capabilities' | head -40
                """);
            TestContext.Progress.WriteLine(report);

            string[] lines = report.Split('\n');
            int injectedAt = Array.FindIndex(lines, line => line.Contains($"SPI fault injected: {fault},"));
            if (injectedAt < 0 || !report.Contains("Lost connection to Duet"))
            {
                Assert.Fail(injectedAt < 0 ? "The fault was not injected" : "The fault did not make DSF lose the connection");
            }
            int startedAt = Array.FindLastIndex(lines, injectedAt, line => line.Contains("Starting macro file trigger30.g"));
            if (startedAt < 0 || lines[startedAt..injectedAt].Any(line => line.Contains("Finished macro file trigger30.g")))
            {
                Assert.Inconclusive("The trigger macro was not running when the fault hit, so this run shows nothing");
            }

            halted = await Lab.TryWaitForModelAsync(Session, model => model.State.Status == MachineStatus.Halted, TimeSpan.FromSeconds(15));
            if (!halted)
            {
                // RRF kept the session. A trigger macro that is still open in RRF holds back the next ones, so neither global changes any more
                (int runs, int loop) = ((await QueryAsync("global.labTestTriggerRuns")).GetInt32(), (await QueryAsync("global.labTestTriggerLoop")).GetInt32());
                await Task.Delay(TimeSpan.FromSeconds(10));
                (int laterRuns, int laterLoop) = ((await QueryAsync("global.labTestTriggerRuns")).GetInt32(), (await QueryAsync("global.labTestTriggerLoop")).GetInt32());
                string journal = await Lab.ReadJournalAsync(cursor);
                Assert.Fail($"RRF did not stop after DSF lost the connection: DSF {(journal.Contains("Aborted macro file trigger30.g") ? "aborted" : "did not abort")} the trigger macro, " +
                    $"trigger {TriggerNumber} started {laterRuns - runs}x in the 10 s after it and its loop {(laterLoop == loop ? "stood still" : "went on")} " +
                    $"(the trigger fires twice per second unless its macro is still open in RRF), status {Lab.Read(Session, model => model.State.Status)}");
            }
            TestContext.Progress.WriteLine($"RRF stopped, DSF {(Regex.IsMatch(report, "Aborted macro file trigger30\\.g") ? "aborted" : "did not abort")} the trigger macro");
        }
        finally
        {
            if (halted)
            {
                // The reset of RRF also ends the trigger
                await Lab.ResetHaltedFirmwareAsync();
            }
            else
            {
                // RRF may still hold the trigger macro that DSF dropped, which only a reset ends
                try
                {
                    await SendCodeAsync($"M581 T{TriggerNumber} P-1");
                }
                catch (Exception e) when (e is OperationCanceledException or System.Net.Http.HttpRequestException)
                {
                    // The reset below ends the trigger as well
                }
                await Lab.RecoverAsync();
            }
            await ReconnectSessionAsync();
        }
    }
}
