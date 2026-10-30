using System;
using System.Linq;
using System.Threading.Tasks;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Expression triggers whose macros call other macros
/// </summary>
/// <remarks>
/// Every test uses a temporary trigger that the machine configuration does not use, and deletes it again
/// </remarks>
[TestFixture, Category("Trigger")]
public class TriggerMacros : LabTest
{
    private const int TriggerNumber = 30;
    private const string TriggerMacro = "0:/sys/trigger30.g";
    private const string ResetMacro = $"{Lab.MacroDirectory}/trigger/reset-counter.g";

    // A long path, so that a garbled request for trigger30.g still contains a tail of it
    private const string CalledMacro = $"{Lab.MacroDirectory}/trigger/called-from-the-trigger-macro.g";

    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(2);

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

            """);
        await Lab.UploadAsync(session, CalledMacro, "set global.labTestTriggerRuns = global.labTestTriggerRuns + 1\n");
        await Lab.UploadAsync(session, TriggerMacro, $"M98 P\"{CalledMacro}\"\n");
    }

    [OneTimeTearDown]
    public async Task DeleteMacrosAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, TriggerMacro);
            await Lab.DeleteAsync(session, CalledMacro);
            await Lab.DeleteAsync(session, ResetMacro);
            await Lab.DeleteAsync(session, $"{Lab.MacroDirectory}/trigger");
        }
    }

    /// <summary>
    /// Make sure the configuration does not use the test trigger
    /// </summary>
    /// <remarks>
    /// DSF requires a P parameter for M581.1, so plain M581 reports the trigger
    /// </remarks>
    private async Task RequireFreeTriggerAsync() =>
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain("is not configured"), $"Trigger {TriggerNumber} is in use");

    /// <summary>
    /// Delete the test trigger. DSF passes only plain M581 on to RRF, M581.1 P-1 deletes just a trigger that DSF evaluates itself
    /// </summary>
    private Task DeleteTriggerAsync() => SendCodeAsync($"M581 T{TriggerNumber} P-1");

    /// <summary>
    /// RRF before 3.7.0-rc.2+1-mp.3 copied the macro name of a trigger request without holding the GB mutex, so the SBC task could send the new
    /// name over the tail of the previous request ("Macro file trigger8.g not found", fixed in RRF fork PR #12 and #13). It happened about once
    /// per 1100 trigger runs, so a clean run of this length proves little on its own
    /// </summary>
    [Test]
    public async Task TriggerMacroCallingMacro()
    {
        await RequireFreeTriggerAsync();
        await SendCodeAsync($"M98 P\"{ResetMacro}\"");
        string cursor = await Lab.GetJournalCursorAsync();

        // Turns true twice per second. No string literals, they hang RRF 3.7 in M581.1 (see below)
        Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} P\"mod(state.msUpTime, 500) < 250\" R0"), Does.Not.Contain("Error"));
        try
        {
            await Task.Delay(Duration);
        }
        finally
        {
            await DeleteTriggerAsync();
        }

        // Let the last trigger macro finish
        await Task.Delay(TimeSpan.FromSeconds(2));
        int runs = (await QueryAsync("global.labTestTriggerRuns")).GetInt32();
        string journal = await Lab.ReadJournalAsync(cursor);
        TestContext.Progress.WriteLine($"Trigger {TriggerNumber} ran {runs} times");

        Assert.Multiple(() =>
        {
            Assert.That(journal, Does.Not.Contain("Failed to start macro file"));
            Assert.That(journal, Does.Not.Match("Macro file .* not found"));
            Assert.That(journal, Does.Not.Contain("Out-of-order reply"));
            Assert.That(runs, Is.GreaterThanOrEqualTo(Duration.TotalSeconds / 2), "The trigger fired on fewer than a quarter of its edges");
        });
    }

    /// <summary>
    /// The G-code dictionary says M581.1 T# P-1 deletes a trigger, but DSF answers it without passing it on to RRF, so a trigger that RRF
    /// evaluates keeps running (upstream SbcTriggerService.ConfigureAsync, found while writing these tests, fixed in #61)
    /// </summary>
    [Test]
    public async Task DeleteFirmwareTriggerWithM581Point1()
    {
        await RequireFreeTriggerAsync();

        // Never true, so the trigger never fires
        Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} P\"state.upTime < 0\" R0"), Does.Not.Contain("Error"));
        try
        {
            await SendCodeAsync($"M581.1 T{TriggerNumber} P-1");
            Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain("is not configured"), "M581.1 P-1 did not delete the trigger in RRF");
        }
        finally
        {
            await DeleteTriggerAsync();
        }
    }

    /// <summary>
    /// RRF 3.7 parses a trigger expression under the heap read lock, and a string literal in it takes the write lock, so M581.1 hangs RRF
    /// until its watchdog resets it with StuckInSpinLoop (not fixed)
    /// </summary>
    [Test, Explicit("Hangs RRF 3.7 until its watchdog resets it")]
    public async Task StringLiteralInExpression()
    {
        await RequireFreeTriggerAsync();

        // "a" == "b" is false, so the trigger never fires
        string reply = string.Empty;
        try
        {
            reply = await SendCodeAsync($"M581.1 T{TriggerNumber} P\"\"\"a\"\" == \"\"b\"\"\" R0");
            await DeleteTriggerAsync();
        }
        catch (OperationCanceledException)
        {
            if (!await Lab.TryWaitForModelAsync(Session, Lab.IsIdle, TimeSpan.FromMinutes(2)))
            {
                await Lab.RecoverAsync();
            }
            Assert.Fail("M581.1 with a string literal in its expression hung RRF");
        }
        Assert.That(reply, Does.Not.Contain("Error"));
    }
}
