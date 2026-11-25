using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Steps of the SBC clock. DSF measured time-outs and the change of the system time with DateTime.Now, the wall clock, which jumps when NTP or someone
/// sets the time (fixed in DSF #215, not in 3.7.0+mp.15)
/// </summary>
/// <remarks>
/// The clock is stepped by python3 as root relative to CLOCK_MONOTONIC and set back the same way, so the restore does not depend on how long the test took.
/// NTP may correct it as well, the test prints whether the clock is synchronized afterwards. RRF gets its time from DSF with M905 when DSF notices a change
/// (PeriodicUpdateService.cs:186-207); the restore checks that RRF follows and warns if not, RRF gets the time again from config.g at its next start
/// (MacroFile.cs:320-329). The CHX config reads no wall-clock time (no state.time or datetime() in sys/ and macros/ of chx350-config 3100431); plugins may log
/// times that are off by the step for the length of a test
/// </remarks>
[TestFixture, Category("Approval"), Explicit("Needs Tim's approval for each run")]
public class SystemTime : LabTest
{
    /// <summary>
    /// How far RRF's clock (state.time, whole seconds, live update) may be off from the expected time
    /// </summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(5);

    [SetUp]
    public Task RequirePython3Async() => ApprovalHelpers.RequirePython3Async();

    /// <summary>
    /// DSF noticed a change of the system time only when the wait between two of its updates took longer than the interval plus 2 s, so it never noticed a
    /// backward step, and RRF (state.time) stayed ahead, e.g. by an hour after the end of daylight saving time. Fixed in DSF #215: each check compares how
    /// far the local time moved with the time that passed on the monotonic clock, a difference of more than 2 s in either direction logs
    /// "System time has been changed" and sends M905 (PeriodicUpdateService.cs:186-207, every HostUpdateInterval = 4 s, Settings.cs:136).
    /// Without the fix the step back of 60 s is not noticed: no journal line and RRF's clock goes on as before. Deterministic
    /// </summary>
    [Test]
    public async Task BackwardClockStepReachesFirmware()
    {
        TimeSpan step = TimeSpan.FromSeconds(-60);
        DateTime start = Lab.Read(Session, model => model.State.Time) ?? DateTime.MinValue;
        if (start == DateTime.MinValue)
        {
            Assert.Ignore("RRF reports no time (state.time)");
        }
        Stopwatch elapsed = Stopwatch.StartNew();
        if (!await IsFirmwareClockOfSbcAsync())
        {
            Assert.Inconclusive($"RRF's clock ({start:s}) is not the local time of the SBC before the step");
        }

        string cursor = await Lab.GetJournalCursorAsync();
        string offset = await StepClockAsync(step);
        bool noticed, followed;
        try
        {
            noticed = await WaitForJournalAsync(cursor, "System time has been changed", TimeSpan.FromSeconds(20));
            followed = await Lab.TryWaitForModelAsync(Session, model => IsNear(model.State.Time, start + elapsed.Elapsed + step), TimeSpan.FromSeconds(10));
        }
        finally
        {
            await RestoreClockAsync(offset, start, elapsed);
        }

        Assert.That(noticed, Is.True, "DCS did not notice that the clock was set back");
        Assert.That(followed, Is.True, $"RRF's clock was not set back: state.time {Lab.Read(Session, model => model.State.Time):s}");
    }

    /// <summary>
    /// DuetWebServer let a session without a WebSocket and without a running request expire when DateTime.Now was more than SessionTimeout past its last
    /// request, so a forward step of the clock ended all such sessions at the next maintenance (every second, Services/SessionExpiry.cs:28-29). Fixed in
    /// DSF #215: SessionStorage measures with the monotonic time stamps of its TimeProvider (SessionStorage.cs:403). The session of this test has no
    /// WebSocket (GET /machine/connect, then GET /machine/noop, MachineEndpoints.cs:35-36, 96). Without the fix the noop after the step is refused with
    /// 401, because the key is no longer known (SessionKeyAuthenticationHandler.cs:38-55, :83), and DWS logs "Session ... expired".
    /// The fix is in DuetWebServer, which reaches the printer by image or by a side-load that includes it. Deterministic as long as the requests of the
    /// test stay within SessionTimeout (8 s by default, Settings.cs:46), else it is inconclusive
    /// </summary>
    [Test]
    public async Task SessionSurvivesClockStep()
    {
        // Read as dsf, the user of DuetWebServer (duetwebserver.service:14), not as root: dsf may replace files in /opt/dsf/conf (duetcontrolserver postinst:18-24)
        await ApprovalHelpers.RequireSudoAsDsfAsync();
        TimeSpan sessionTimeout = TimeSpan.FromMilliseconds(double.Parse((await Lab.RunOnSbcAsync($"""
            {ApprovalHelpers.AsDsf} python3 -I -c 'import json; print(json.load(open("/opt/dsf/conf/http.json")).get("SessionTimeout", 8000))'
            """)).Trim(), System.Globalization.CultureInfo.InvariantCulture));
        TimeSpan step = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(60).Ticks, 4 * sessionTimeout.Ticks));
        // RRF's clock is only checked after the restore if DCS had set it to the time of the SBC
        DateTime? start = Lab.Read(Session, model => model.State.Time);
        Stopwatch elapsed = Stopwatch.StartNew();
        if (start is not null && !await IsFirmwareClockOfSbcAsync())
        {
            start = null;
        }

        string cursor = await Lab.GetJournalCursorAsync();
        await using RawMachineSession http = await RawMachineSession.ConnectAsync();
        (HttpStatusCode before, _) = await http.SendAsync(HttpMethod.Get, "machine/noop");
        Assert.That(before, Is.EqualTo(HttpStatusCode.NoContent), "The noop before the step failed");
        Stopwatch sinceRequest = Stopwatch.StartNew();

        string offset = await StepClockAsync(step);
        HttpStatusCode after;
        TimeSpan idle;
        try
        {
            // Let the maintenance run at least twice after the step
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            idle = sinceRequest.Elapsed;
            (after, _) = await http.SendAsync(HttpMethod.Get, "machine/noop");
        }
        finally
        {
            await RestoreClockAsync(offset, start, elapsed);
        }

        if (idle >= sessionTimeout - TimeSpan.FromSeconds(1))
        {
            Assert.Inconclusive($"{idle.TotalSeconds:0.0} s passed between the two requests, the session may have expired without the step (SessionTimeout {sessionTimeout.TotalSeconds:0.#} s)");
        }
        string[] expired = [.. (await Lab.ReadJournalAsync(cursor, "duetwebserver")).Split('\n').Where(line => line.Contains("expired"))];
        Assert.That(after, Is.EqualTo(HttpStatusCode.NoContent), $"The session did not survive a step of {step.TotalSeconds:0} s: {string.Join(" | ", expired)}");
    }

    /// <summary>
    /// Step the clock of the SBC
    /// </summary>
    /// <returns>Offset of CLOCK_REALTIME from CLOCK_MONOTONIC before the step, to set the clock back with</returns>
    private static async Task<string> StepClockAsync(TimeSpan step)
    {
        string offset = (await Lab.RunOnSbcAsync($$"""
            sudo -n python3 -I - {{step.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}} <<'EOF'
            import sys, time
            offset = time.clock_gettime(time.CLOCK_REALTIME) - time.clock_gettime(time.CLOCK_MONOTONIC)
            time.clock_settime(time.CLOCK_REALTIME, time.clock_gettime(time.CLOCK_REALTIME) + float(sys.argv[1]))
            print(repr(offset))
            EOF
            """)).Trim();
        TestContext.Progress.WriteLine($"Stepped the SBC clock by {step.TotalSeconds:0} s");
        return offset;
    }

    /// <summary>
    /// Set the clock of the SBC back to its offset from CLOCK_MONOTONIC before the step and check that RRF follows
    /// </summary>
    private async Task RestoreClockAsync(string offset, DateTime? firmwareStart, Stopwatch elapsed)
    {
        await Lab.RunOnSbcAsync($$"""
            sudo -n python3 -I - {{offset}} <<'EOF'
            import sys, time
            time.clock_settime(time.CLOCK_REALTIME, time.clock_gettime(time.CLOCK_MONOTONIC) + float(sys.argv[1]))
            EOF
            """);
        string ntp = (await Lab.RunOnSbcAsync("timedatectl show -p NTPSynchronized --value 2>/dev/null || echo unknown")).Trim();
        TestContext.Progress.WriteLine($"Set the SBC clock back, NTP synchronized: {ntp}");

        if (firmwareStart is not null &&
            !await Lab.TryWaitForModelAsync(Session, model => IsNear(model.State.Time, firmwareStart.Value + elapsed.Elapsed), TimeSpan.FromSeconds(20)))
        {
            Assert.Warn($"RRF's clock did not follow the restore (state.time {Lab.Read(Session, model => model.State.Time):s}), it gets the time again from config.g at its next start");
        }
    }

    /// <summary>
    /// The checks of RRF's clock compare it with where it was before the step. That works only if DCS has set it to the local time of the SBC before,
    /// because M905 sets it to that time (PeriodicUpdateService.cs:197-205)
    /// </summary>
    private async Task<bool> IsFirmwareClockOfSbcAsync()
    {
        DateTime sbc = DateTime.Parse((await Lab.RunOnSbcAsync("date '+%Y-%m-%dT%H:%M:%S'")).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        DateTime? firmware = Lab.Read(Session, model => model.State.Time);
        TestContext.Progress.WriteLine($"Before the step: RRF {firmware:s}, SBC {sbc:s}");
        return IsNear(firmware, sbc);
    }

    private static bool IsNear(DateTime? time, DateTime expected) => time is not null && (time.Value - expected).Duration() <= Tolerance;

    /// <summary>
    /// Wait until the DCS log has a line with the given text since a cursor
    /// </summary>
    private static async Task<bool> WaitForJournalAsync(string cursor, string text, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        do
        {
            if ((await Lab.ReadJournalAsync(cursor)).Contains(text))
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        while (stopwatch.Elapsed < timeout);
        return false;
    }
}
