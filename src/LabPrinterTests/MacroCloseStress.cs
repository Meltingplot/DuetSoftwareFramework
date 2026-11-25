using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Macro files that close on the trigger channel while DSF reads the object model
/// </summary>
/// <remarks>
/// RRF 3.7.0+1.mp.9 and older reset while idle with a MemoryProtectionFault in Heap::DeleteSlot, about once per 40,000 macro closes on the
/// trigger channel (six resets on the lab printer from 2026-11-10 to 2026-11-21 at about 30,000 closes a day). A closing macro deletes its
/// file name (GCodeMachineState::CloseFile), and StringHandle::Delete released the heap slot before it cleared the handle. The SBC task
/// serialises inputs[].currentFile into every live object model response that DSF requests (every ModelUpdateInterval, 100 ms by default),
/// so it could copy the handle in that window and revive the released slot. The code is upstream (d232aa0b3, 3.7.0). Fixed in RRF fork
/// PR #50, first in the test build 3.7.0+1.mp.10-dev.1, and covered offline by Tools/heap-test in RRF
///
/// The test runs trigger 30 back to back. Its macro nests four more, up to three levels deep, like the idle tick of the machine (trigger8.g
/// with the fan/ and self-test/ macros), only much more often. Triggers with lower numbers, such as those of the doors, still run first
/// (GCodes::CheckTriggers), and a trigger whose action is M112 fires as trigger 0 at once
///
/// It also reads the journal of DCS for the lines that TriggerMacros.TriggerMacroCallingMacro checks. RRF before 3.7.0-rc.2+1-mp.3 copied the macro name of
/// a trigger request without holding the GB mutex, so the SBC task could send the new name over the tail of the previous request ("Macro file trigger8.g
/// not found", fixed in RRF fork PR #12 and #13, upstream as Duet3D 6c53cc748, GCodeBuffer.cpp:1333). That happened about once per 1100 trigger runs;
/// 60 minutes are 7,200 runs at the lowest rate the test accepts (10 closes a second) and 18,000 at five runs a second, so a regression would show
/// 6 to 16 times. "Failed to start macro file" is the error
/// of DCS for a macro request it cannot serve (Processor.cs:1712), "Out-of-order reply" a reply that reached a channel without a code for it (Processor.cs:1390)
/// </remarks>
[TestFixture, Category("Trigger"), Explicit("Runs for 60 minutes")]
public class MacroCloseStress : LabTest
{
    private const int TriggerNumber = 30;
    private const string TriggerMacro = "0:/sys/trigger30.g";
    private const string MacroFolder = $"{Lab.MacroDirectory}/macro-close";
    private const string ResetMacro = $"{MacroFolder}/reset-counter.g";

    // Long paths like those of the machine's own macros, e.g. 0:/sys/meltingplot/self-test/check-update.g
    private const string FirstMacro = $"{MacroFolder}/level-1-first-of-two.g";
    private const string SecondMacro = $"{MacroFolder}/level-1-second-of-two.g";
    private const string NestedMacro = $"{MacroFolder}/level-2-called-by-the-second.g";
    private const string DeepestMacro = $"{MacroFolder}/level-3-called-by-level-2.g";

    /// <summary>
    /// Macros that run per trigger run: trigger30.g and the four it nests
    /// </summary>
    private const int MacrosPerRun = 5;

    /// <summary>
    /// Lowest rate of macro closes that counts as a stress. The idle tick of the machine closes about 0.35 macros a second
    /// </summary>
    private const double MinimumClosesPerSecond = 10;

    /// <summary>
    /// Turns true every 200 ms, so trigger 30 runs at most five times a second. No string literals, they hung RRF 3.7 before 59a04159b
    /// </summary>
    private const string Expression = "mod(state.msUpTime, 200) < 100";

    private const string Count = "set global.labTestMacroCloses = global.labTestMacroCloses + 1\n";

    /// <summary>
    /// Duration of the run, 60 minutes unless LAB_MACRO_CLOSE_MINUTES sets another one
    /// </summary>
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(
        int.TryParse(Environment.GetEnvironmentVariable("LAB_MACRO_CLOSE_MINUTES"), out int minutes) && minutes > 0 ? minutes : 60);

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5), CountInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Journal lines of DCS that RRF restarted, as in PER_REBOOT of run-lab-tests.py
    /// </summary>
    private static readonly string[] RestartLines = ["Connection to controller has been reset", "RepRapFirmware has left the extended link", "Restarting full transfer because"];

    [OneTimeSetUp]
    public async Task UploadMacrosAsync()
    {
        Lab.RequirePrinter();
        await using DuetHttpSession session = await Lab.ConnectAsync();
        if ((await session.GetFileListAsync("0:/sys")).Any(item => item.Filename == "trigger30.g"))
        {
            Assert.Ignore($"{TriggerMacro} exists on this printer, so the test does not touch trigger {TriggerNumber}");
        }

        await Lab.UploadAsync(session, ResetMacro, """
            if !exists(global.labTestMacroCloses)
              global labTestMacroCloses = 0
            else
              set global.labTestMacroCloses = 0

            """);
        await Lab.UploadAsync(session, FirstMacro, Count);
        await Lab.UploadAsync(session, SecondMacro, $"M98 P\"{NestedMacro}\"\n{Count}");
        await Lab.UploadAsync(session, NestedMacro, $"M98 P\"{DeepestMacro}\"\n{Count}");
        await Lab.UploadAsync(session, DeepestMacro, Count);
        await Lab.UploadAsync(session, TriggerMacro, $"M98 P\"{FirstMacro}\"\nM98 P\"{SecondMacro}\"\n{Count}");
    }

    [OneTimeTearDown]
    public async Task DeleteMacrosAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, TriggerMacro);
            foreach (string macro in new[] { FirstMacro, SecondMacro, NestedMacro, DeepestMacro, ResetMacro })
            {
                await Lab.DeleteAsync(session, macro);
            }
            await Lab.DeleteAsync(session, MacroFolder);
        }
    }

    [Test]
    public async Task NoResetWhileMacrosCloseOnTheTriggerChannel()
    {
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain("is not configured"), $"Trigger {TriggerNumber} is in use");
        await SendCodeAsync($"M98 P\"{ResetMacro}\"");
        string resetBefore = SoftwareReset(await SendCodeAsync("M122", 30));
        string cursor = await Lab.GetJournalCursorAsync();
        int upTime = Lab.Read(Session, model => model.State.UpTime);

        int closes = 0;
        bool restarted = false;
        Stopwatch stopwatch = Stopwatch.StartNew();
        TimeSpan nextCount = CountInterval;
        Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} P\"{Expression}\" R0"), Does.Not.Contain("Error"));
        try
        {
            // A reset of RRF ends the run at once. The up time of the object model shows it within a few seconds
            while (stopwatch.Elapsed < Duration)
            {
                await Task.Delay(CheckInterval);
                int now = Lab.Read(Session, model => model.State.UpTime);
                if (now < upTime)
                {
                    restarted = true;
                    break;
                }
                upTime = now;

                if (stopwatch.Elapsed >= nextCount)
                {
                    closes = await CountClosesAsync() ?? closes;
                    TestContext.Progress.WriteLine($"{stopwatch.Elapsed.TotalMinutes:0} min: {closes} macro closes, {closes / stopwatch.Elapsed.TotalSeconds:0.0} per second");
                    nextCount += CountInterval;
                }
            }
        }
        finally
        {
            await DeleteTriggerAsync();
        }
        TimeSpan elapsed = stopwatch.Elapsed;

        if (restarted && !await Lab.TryWaitForModelAsync(Session, Lab.IsIdle, TimeSpan.FromMinutes(2)))
        {
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
        }

        // Let the last trigger macro finish. After a reset the counter is gone, so the last count stays
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (!restarted)
        {
            closes = await CountClosesAsync() ?? closes;
        }
        string m122 = await SendCodeAsync("M122", 30);
        string resetAfter = SoftwareReset(m122);
        string journal = await Lab.ReadJournalAsync(cursor);
        double perSecond = closes / elapsed.TotalSeconds;
        TestContext.Progress.WriteLine($"Trigger {TriggerNumber} ran {closes / MacrosPerRun} times and closed {closes} macros in {elapsed.TotalMinutes:0.0} min, {perSecond:0.0} per second");

        string where = $"after {elapsed.TotalMinutes:0.0} min and at least {closes} macro closes";
        Assert.Multiple(() =>
        {
            Assert.That(restarted, Is.False, $"RRF restarted {where}. M122:\n{resetAfter}");
            Assert.That(resetAfter, Is.EqualTo(resetBefore), $"M122 reports a new software reset {where}");
            Assert.That(m122, Does.Contain("Heap OK"), "M122 finds the string heap damaged");
            foreach (string line in RestartLines)
            {
                Assert.That(journal, Does.Not.Contain(line), $"The journal of DCS shows a restart of RRF {where}");
            }
            Assert.That(perSecond, Is.GreaterThanOrEqualTo(MinimumClosesPerSecond), $"Only {perSecond:0.0} macro closes a second");
            Assert.That(journal, Does.Not.Contain("Failed to start macro file"));
            Assert.That(journal, Does.Not.Match("Macro file .* not found"), "A macro request of RRF named a file that does not exist (RRF #12)");
            Assert.That(journal, Does.Not.Contain("Out-of-order reply"));
        });
    }

    /// <summary>
    /// Delete the test trigger. Plain M581, because before #61 DSF did not pass M581.1 P-1 on to RRF. RRF may still restart
    /// </summary>
    private async Task DeleteTriggerAsync()
    {
        try
        {
            await SendCodeAsync($"M581 T{TriggerNumber} P-1", 30);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            // A reset deletes the trigger as well, config.g does not create it
        }
    }

    /// <summary>
    /// Number of macros that have run to their end, or null when the counter is gone because RRF restarted
    /// </summary>
    private async Task<int?> CountClosesAsync()
    {
        JsonElement value = await QueryAsync("global.labTestMacroCloses");
        return (value.ValueKind == JsonValueKind.Number) ? value.GetInt32() : null;
    }

    /// <summary>
    /// The lines of M122 about the last software reset of RRF: the time and reason, the reset code and the stack
    /// </summary>
    private static string SoftwareReset(string m122) =>
        string.Join('\n', m122.Split('\n').Select(line => line.Trim()).Where(line =>
            line.StartsWith("Last software reset") || line.StartsWith("Software reset code") || line.StartsWith("Stack:")));
}
