using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// DCS restarts and firmware resets
/// </summary>
[TestFixture, Category("Restart")]
public class ControlServerRestart : LabTest
{
    /// <summary>
    /// Text of the message box that waits while DCS stops. The machine opens boxes of its own, see MessageBoxes
    /// </summary>
    private const string BoxText = "Lab printer test, DCS stops while this box is open";

    /// <summary>
    /// Longest time from systemd's stop line to the end of DCS. With #149 the lab printer took 0.04 to 0.05 s (2026-11-08, design/lab-test-plan.md,
    /// section 14, Lauf 3). A service that does not stop is cut off by the host after 10 s (Program.cs:177, #149d), before that by systemd after 15 s
    /// (TimeoutStopSec, duetcontrolserver.service). 3 s as in the unit tests (UnitTests/Startup/ShutdownHang.cs, ExpectedStopTime)
    /// </summary>
    private const double MaxStopSeconds = 3;

    /// <summary>
    /// Longest time from "Resetting controller" to the end of DCS. DCS stops 1 s after the reply of M999 (MCodeHandler.cs:1429-1434); 1.04 to 1.07 s on
    /// 2026-11-08 (plan section 14, Lauf 3)
    /// </summary>
    private const double MaxResetStopSeconds = 4;

    /// <summary>
    /// Lines that no stop of DCS may log: a code in the pipeline that failed at the stop (PipelineStackItem.cs:85-88), a stop that waits for a firmware
    /// update (LinkService.cs:294) and systemd killing DCS
    /// </summary>
    private static readonly string[] ForbiddenStopLines =
    [
        "Failed to process code in stage", "Waiting for the firmware update to finish before stopping", "State 'stop-sigterm' timed out"
    ];

    /// <summary>
    /// Root IPC client on the SBC that sends M122 on the SBC channel, one after the other, until DCS stops. M122 waits in the Executed stage for the
    /// diagnostics of DSF (UnitTests/Startup/ExecutedStageShutdown.cs), so a stop usually finds one there
    /// </summary>
    private const string M122Loop = """
        ipc = Ipc()
        replies, end = 0, "time limit"
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            outcome, seconds = simple_code(ipc, "M122", "SBC", 30)
            if not outcome.startswith("reply"):
                end = outcome
                break
            replies += 1
        print(json.dumps({"replies": replies, "end": end}))
        """;

    /// <summary>
    /// Root IPC client on the SBC that sends M999 on HTTP and, the moment its reply is there, G4 P10 on the SBC channel of a second connection, as
    /// ShutdownHang.StopsAfterM999WhileAnotherCodeIsPending does in the unit tests
    /// </summary>
    private const string ResetThenCode = """
        resetting, other = Ipc(), Ipc()
        m999, m999_seconds = simple_code(resetting, "M999", "HTTP", 15)
        g4, g4_seconds = simple_code(other, "G4 P10", "SBC", 30)
        print(json.dumps({"m999": m999, "g4": g4, "g4Seconds": g4_seconds}))
        """;

    /// <summary>
    /// Stopping DCS stops the plugin services (PartOf), and starting DCS again did not bring them back, so dsf-config.g never ran
    /// (fixed in #37 with WantedBy=duetcontrolserver.service). The same stop and start checks:
    /// <list type="bullet">
    /// <item>DSF #149b: a code of a client that waits when DCS stops was never completed, and DCS hung until the host gave up after 10 s (before #149d
    /// until systemd killed it after 15 s). The stages of the pipeline stopped reading at ApplicationStopping, before LinkService.StopAsync cancels the codes
    /// into them, and the IPC server waits for its connections until ApplicationStopped (IPC/Server.cs:122-123). Fixed: the stages read until the
    /// CodeProcessorService stops (PipelineStackItem.cs:63, CodeProcessor.cs:79, CodeProcessorService.cs:18-22). The test keeps M291 S2 from HTTP
    /// waiting, as a dialog that waits for the user, and measures the stop in the journal from systemd's stop line to the end of the process: fixed
    /// below <see cref="MaxStopSeconds"/>, unfixed 10 s or more. Deterministic by the mechanism; on hardware only the fixed build was measured</item>
    /// <item>DSF #149c: codes in the Executed stage failed at the stop with "Failed to process code in stage Executed" (seen twice on the lab printer),
    /// because AsyncLock gave up on a cancelled token although the lock was free (fix AsyncLock.cs:48-68, replaced by the same code of upstream 61df011e).
    /// An M122 loop of a root IPC client on the SBC channel runs across the stop. Statistical: the number of stops behind the two sightings was not
    /// recorded, so the rate per stop is not known</item>
    /// <item>DSF #154: a stop without a firmware update must not wait for one ("Waiting for the firmware update to finish before stopping",
    /// LinkService.cs:285-298, logged after 5 s). The flash side of #154 needs a real flash and is not tested</item>
    /// <item>RRF #21: after a DCS restart RRF carried on with the state of the old connection, idle. RRF ends the connection with an emergency stop
    /// (RRF SbcInterface.cpp:294-299), so DCS must report it halted until M999, its up time must go on (no reset, RRF #20 b: the stop from the SBC
    /// task is carried out by the main task, GCodes.cpp:1111-1116, :467-475) and a code gets "Machine is halted" (RRF GCodes2.cpp:194-198, :5156-5160).
    /// Unfixed: idle, G4 is carried out. Deterministic</item>
    /// <item>RRF #23: the emergency stop switches ATX power off (RRF RepRap.cpp:1057-1058, Platform.cpp:3561-3577). state.atxPower reads the pin back
    /// (RepRap.cpp:341, Platform.cpp:3500-3504), so it must turn false if it was true. The machine switches it on in e-stop.g, which config.g runs, and
    /// again after M999. Unfixed: it stays true. Deterministic; skipped where ATX power is not controlled (null)</item>
    /// </list>
    /// No extra restart: the stop and start were in the test before. A separate test for #149b would cost one more DCS restart and M999
    /// </summary>
    [Test]
    public async Task PluginServicesStartWithControlServer()
    {
        string cursor = await Lab.GetJournalCursorAsync();
        (int upTimeBefore, bool? atxBefore) = Lab.Read(Session, model => (model.State.UpTime, model.State.AtxPower));
        Stopwatch sinceUpTime = Stopwatch.StartNew();

        Task<string> messageBox = SendCodeAsync($"M291 P\"{BoxText}\" S2", 300);
        bool boxOpen = await Lab.TryWaitForModelAsync(Session, model => model.State.MessageBox is { Message: BoxText }, TimeSpan.FromSeconds(30));
        try
        {
            string loop = await Lab.RunOnSbcAsync($$"""
                out=$(mktemp)
                loop=
                if command -v python3 >/dev/null; then
                  sudo -n timeout 120 python3 - > "$out" 2>&1 <<'EOF' &
                {{RestartHelpers.IpcClient}}
                {{M122Loop}}
                EOF
                  loop=$!
                  sleep 2
                else
                  echo '{"error": "python3 is missing"}' > "$out"
                fi
                sudo -n systemctl stop duetcontrolserver
                [ -z "$loop" ] || wait $loop
                sudo -n systemctl start duetcontrolserver
                cat "$out"
                rm -f "$out"
                """);
            string boxOutcome = await OutcomeAsync(messageBox, TimeSpan.FromSeconds(10));

            // RRF before M999, seen by a new session
            MachineStatus status;
            int upTimeAfter;
            bool? atxAfter;
            string g4Reply;
            double elapsed;
            await using (DuetHttpSession after = await Lab.ReconnectAsync(TimeSpan.FromMinutes(2)))
            {
                await Lab.WaitForModelAsync(after, model => model.State.Status is MachineStatus.Halted or MachineStatus.Idle, TimeSpan.FromMinutes(2), "DCS to report the firmware state");
                (status, upTimeAfter, atxAfter) = Lab.Read(after, model => (model.State.Status, model.State.UpTime, model.State.AtxPower));
                elapsed = sinceUpTime.Elapsed.TotalSeconds;
                g4Reply = await Lab.SendCodeAsync(after, "G4 P1", TimeSpan.FromSeconds(10));
            }

            string states = await Lab.RunOnSbcAsync("""
                for i in $(seq 30); do
                  systemctl is-active --quiet duetpluginservice duetpluginservice-root && break
                  sleep 1
                done
                systemctl is-active duetpluginservice duetpluginservice-root || true
                """);

            RestartHelpers.JournalLine[] journal = await RestartHelpers.ReadTimedJournalAsync(cursor);
            double? stopping = RestartHelpers.FirstTime(journal, 0, "Stopping duetcontrolserver.service", "Stopping Duet Control Server");
            double? ended = (stopping is null) ? null : RestartHelpers.FirstTime(journal, stopping.Value, RestartHelpers.ControlServerEndedLines);
            TestContext.Progress.WriteLine($"Box {(boxOpen ? "open" : "not open")} at the stop, M291 ended with {boxOutcome}; M122 loop: {loop.Trim()}; " +
                $"stop {(ended - stopping)?.ToString("0.00") ?? "not found"} s; after the start {status}, up time {upTimeBefore} -> {upTimeAfter} s in {elapsed:0} s, " +
                $"ATX power {atxBefore?.ToString() ?? "not controlled"} -> {atxAfter?.ToString() ?? "not controlled"}, G4: {g4Reply.Trim()}");

            Assert.Multiple(() =>
            {
                Assert.That(states.Split('\n', StringSplitOptions.RemoveEmptyEntries), Is.EqualTo(new[] { "active", "active" }), "duetpluginservice and duetpluginservice-root");
                Assert.That(ended - stopping, Is.Not.Null.And.LessThan(MaxStopSeconds), "DCS did not stop in time while a code waited (#149b)");
                Assert.That(boxOutcome, Does.Not.StartWith("pending"), "The M291 that waited while DCS stopped got no answer (#149b)");
                foreach (string text in ForbiddenStopLines)
                {
                    Assert.That(RestartHelpers.Count(journal, text), Is.Zero, $"\"{text}\" in the journal");
                }
                Assert.That(status, Is.EqualTo(MachineStatus.Halted), "RRF was not halted after the DCS restart (RRF #21)");
                Assert.That(upTimeAfter, Is.GreaterThanOrEqualTo(upTimeBefore + (int)elapsed - 3), "RRF restarted instead of halting (RRF #20, #21)");
                Assert.That(g4Reply, Does.Contain("Machine is halted"), "RRF carried out a code before M999 (RRF #21)");
                if (atxBefore == true)
                {
                    Assert.That(atxAfter, Is.False, "The emergency stop left ATX power on (RRF #23)");
                }
            });
        }
        finally
        {
            await Lab.RunOnSbcAsync("sudo -n systemctl start duetpluginservice duetpluginservice-root");
            await Lab.ResetHaltedFirmwareAsync();
        }
    }

    /// <summary>
    /// What became of a code: "reply ..." or the exception that ended it, or "pending" if it has not ended within the given time
    /// </summary>
    private static async Task<string> OutcomeAsync(Task<string> code, TimeSpan timeout)
    {
        if (await Task.WhenAny(code, Task.Delay(timeout)) != code)
        {
            return "pending";
        }
        try
        {
            return $"reply '{(await code).Trim()}'";
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>
    /// After M999, DCS exits and systemd starts it again. On 3.7.0+mp.6 to mp.8 it hung in its shutdown until systemd killed it 15 s later. So far it hung
    /// after the M999 a client sends at boot, when other clients are busy: M999 ends the transfer thread (LinkService.cs:327-346), a code that another client
    /// sends in the second before DCS stops (MCodeHandler.cs:1429-1434) waits in the Firmware stage until LinkService.StopAsync cancels it, and the
    /// pipeline no longer took cancelled codes (case 2 of UnitTests/Startup/ShutdownHang.cs). Fixed in DSF #149b, see
    /// <see cref="PluginServicesStartWithControlServer"/>; since #149d the host gives up after 10 s (Program.cs:177), so the hang no longer ends with
    /// "State 'stop-sigterm' timed out" and the test measures the stop instead.
    /// A root IPC client on the SBC sends M999 and, as soon as its reply is there, G4 P10 on the SBC channel, which RRF never gets. Fixed: G4 gets an empty
    /// reply when DCS stops and DCS ends within <see cref="MaxResetStopSeconds"/> of "Resetting controller" (1.04 to 1.07 s on 2026-11-08). Unfixed: both
    /// take about 10 s. Deterministic by the mechanism, the window of 1 s is wide enough for a client on the SBC.
    ///
    /// DCS ends itself after M999, outside a stop job of systemd, so PartOf=duetcontrolserver.service of the plugin service units does not stop them
    /// (pkg/common/duetpluginservice/usr/lib/systemd/system/*.service). Before DSF #151 (fix 6) a plugin service only left its command loop when DCS closed
    /// the connection and stayed up without a connection, so its plugins were not started under the new DCS (upstream IPC/CommandService.cs only breaks the
    /// loop). Fixed: it logs "Connection to DCS has been closed" and stops (DuetPluginService IPC/CommandService.cs:66-71), systemd starts it again
    /// (Restart=always) and it registers with the new DCS. The test samples the main PIDs of DCS and both plugin services every 0.1 s and requires each plugin
    /// service to have a new process at least 2 s before the new DCS (systemd starts DCS RestartSec=5 s after it ended), the line in the log of each
    /// service and every SBC plugin that ran before to run again under the new DCS. Unfixed: no line, and the plugin services keep their PIDs at least until
    /// systemd restarts DCS (whether systemd passes that restart on to the PartOf units was not checked; the 2 s rule does not depend on it).
    /// Deterministic. The plugin services come with the image, #151 is in 3.7.0+mp.14 and later. This check replaces a separate test with
    /// systemctl kill, which would have cost one more emergency stop and M999
    /// </summary>
    [Test]
    public async Task ResetDoesNotHangShutdown()
    {
        string[] runningPlugins = Lab.Read(Session, model => model.Plugins.Values.Where(plugin => !string.IsNullOrEmpty(plugin.SbcExecutable) && plugin.Pid > 0).Select(plugin => plugin.Id).ToArray());
        string cursor = await Lab.GetJournalCursorAsync();
        string output = await Lab.RunOnSbcAsync($$"""
            command -v python3 > /dev/null || { echo '{"error": "python3 is missing"}'; exit 0; }
            units="{{string.Join(' ', ServiceUnits)}}"
            echo "BEFORE $(systemctl show -p MainPID --value $units | tr '\n' ' ')"
            old=$(systemctl show -p MainPID --value duetcontrolserver)
            sudo -n timeout 60 python3 - 2>&1 <<'EOF' || echo '{"error": "the IPC client failed"}'
            {{RestartHelpers.IpcClient}}
            {{ResetThenCode}}
            EOF
            for i in $(seq 300); do
              set -- $(systemctl show -p MainPID --value $units)
              echo "PIDS $(date +%s.%N) $*"
              [ "$1" != 0 ] && [ "$1" != "$old" ] && break
              sleep 0.1
            done
            exit 0
            """);
        TestContext.Progress.WriteLine(output.Trim());
        if (output.Contains("python3 is missing"))
        {
            Assert.Ignore("python3 is missing on the SBC, the IPC client needs it");
        }
        string? result = output.Split('\n').LastOrDefault(line => line.StartsWith('{'));
        using JsonDocument report = JsonDocument.Parse(result ?? "{}");
        (double Time, string[] Pids)[] samples = ReadPidSamples(output);
        string[] before = output.Split('\n').FirstOrDefault(line => line.StartsWith("BEFORE "))?["BEFORE ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        double? newControlServer = samples.Where(sample => before.Length == ServiceUnits.Length && sample.Pids[0] != "0" && sample.Pids[0] != before[0])
            .Select(sample => (double?)sample.Time).FirstOrDefault();
        if (newControlServer is null)
        {
            await Lab.RecoverAsync();
            Assert.Fail($"systemd did not start a new DCS after M999 ({result})");
        }
        await Lab.ResetHaltedFirmwareAsync();
        await ReconnectSessionAsync();
        bool pluginsBack = await Lab.TryWaitForModelAsync(Session, model => runningPlugins.All(id => model.Plugins.TryGetValue(id, out Plugin? plugin) && plugin.Pid > 0 && plugin.Started),
            TimeSpan.FromMinutes(2));

        RestartHelpers.JournalLine[] journal = await RestartHelpers.ReadTimedJournalAsync(cursor);
        double? reset = RestartHelpers.FirstTime(journal, 0, "Resetting controller");
        double? ended = (reset is null) ? null : RestartHelpers.FirstTime(journal, reset.Value, RestartHelpers.ControlServerEndedLines);
        string[] pluginServiceLogs = await Task.WhenAll(ServiceUnits[1..].Select(unit => Lab.ReadJournalAsync(cursor, unit)));
        TestContext.Progress.WriteLine($"DCS ended {(ended - reset)?.ToString("0.00") ?? "?"} s after the reset, plugins {string.Join(", ", runningPlugins)} " +
            $"{(pluginsBack ? "back" : "not back")}");

        Assert.Multiple(() =>
        {
            Assert.That(reset, Is.Not.Null, "\"Resetting controller\" is not in the journal");
            Assert.That(ended - reset, Is.Not.Null.And.LessThanOrEqualTo(MaxResetStopSeconds), "DCS did not stop in time after M999 (#149b)");
            foreach (string text in ForbiddenStopLines)
            {
                Assert.That(RestartHelpers.Count(journal, text), Is.Zero, $"\"{text}\" in the journal");
            }
            Assert.That(report.RootElement.TryGetProperty("m999", out JsonElement m999) ? m999.GetString() : result, Is.EqualTo("empty reply"), "M999 from the IPC client");
            if (report.RootElement.TryGetProperty("g4", out JsonElement g4))
            {
                Assert.That(g4.GetString(), Is.EqualTo("empty reply").Or.StartWith("closed"), "G4 after M999");
                Assert.That(report.RootElement.GetProperty("g4Seconds").GetDouble(), Is.LessThanOrEqualTo(MaxResetStopSeconds), "G4 after M999 waited for the stop of the host (#149b)");
            }
            for (int i = 1; i < ServiceUnits.Length; i++)
            {
                double? changed = samples.Where(sample => before.Length == ServiceUnits.Length && sample.Pids[i] != before[i]).Select(sample => (double?)sample.Time).FirstOrDefault();
                Assert.That(newControlServer - changed, Is.Not.Null.And.GreaterThanOrEqualTo(2.0), $"{ServiceUnits[i]} did not restart when DCS closed its connection (#151)");
                Assert.That(pluginServiceLogs[i - 1], Does.Contain("Connection to DCS has been closed"), $"{ServiceUnits[i]} (#151)");
            }
            Assert.That(pluginsBack, Is.True, "Not every plugin that ran before runs again under the new DCS (#151)");
        });
    }

    /// <summary>
    /// Units whose main PIDs <see cref="ResetDoesNotHangShutdown"/> samples, DCS first
    /// </summary>
    private static readonly string[] ServiceUnits = ["duetcontrolserver", "duetpluginservice", "duetpluginservice-root"];

    /// <summary>
    /// Read the lines "PIDS time pid..." of the sampling loop
    /// </summary>
    private static (double Time, string[] Pids)[] ReadPidSamples(string output) =>
        [.. output.Split('\n').Where(line => line.StartsWith("PIDS ")).Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 + ServiceUnits.Length)
            .Select(parts => (double.Parse(parts[1], CultureInfo.InvariantCulture), parts[2..]))];

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
