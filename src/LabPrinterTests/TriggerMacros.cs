using System;
using System.Linq;
using System.Text.Json;
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

    /// <summary>
    /// Content of <see cref="TriggerMacro"/> for every test but <see cref="TriggerAfterM99GetsItsMacro"/>
    /// </summary>
    private const string TriggerMacroContent = $"M98 P\"{CalledMacro}\"\n";

    /// <summary>
    /// Macro that sets up a trigger which DSF evaluates, for <see cref="SbcTriggerConfiguredFromMacro"/>
    /// </summary>
    private const string ConfiguringMacro = $"{Lab.MacroDirectory}/trigger/configure-sbc-trigger.g";

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
        await Lab.UploadAsync(session, TriggerMacro, TriggerMacroContent);
        await Lab.UploadAsync(session, ConfiguringMacro,
            G2Helpers.Global("labTestTriggerConfigured", "false") +
            $"M581.1 T{TriggerNumber} P\"{FiringSbcExpression}\" R0\n" +
            "set global.labTestTriggerConfigured = true\n");
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
            await Lab.DeleteAsync(session, ConfiguringMacro);
            await Lab.DeleteAsync(session, $"{Lab.MacroDirectory}/trigger");
        }
    }

    /// <summary>
    /// Make sure the configuration does not use the test trigger
    /// </summary>
    /// <remarks>
    /// Plain M581, because DSF 3.7.0+mp.8 and older refuse M581.1 without P
    /// </remarks>
    private async Task RequireFreeTriggerAsync() =>
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain("is not configured"), $"Trigger {TriggerNumber} is in use");

    /// <summary>
    /// Delete the test trigger. Plain M581, because before #61 DSF did not pass M581.1 P-1 on to RRF
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
    /// The G-code dictionary documents M581.1 T# as a report and M581.1 T# R&lt;n&gt; as a change of the enable condition, but DSF refused both
    /// without a P parameter (fixed in #63)
    /// </summary>
    [Test]
    public async Task ReportFirmwareTriggerWithM581Point1()
    {
        await RequireFreeTriggerAsync();

        // Never true, so the trigger never fires
        Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} P\"state.upTime < 0\" R0"), Does.Not.Contain("Error"));
        try
        {
            Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber}"), Does.Contain("fires when expression {state.upTime < 0} becomes true"));
            Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} R-1"), Does.Not.Contain("Error"));
            Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain("if enabled would fire"), "M581.1 R-1 did not reach RRF");
        }
        finally
        {
            await DeleteTriggerAsync();
        }
    }

    /// <summary>
    /// DSF evaluates a trigger with SBC fields itself, and plain M581 T# deleted such a trigger even when it only asked for a report (fixed in #63)
    /// </summary>
    [Test]
    public async Task ReportSbcTriggerWithM581()
    {
        await RequireFreeTriggerAsync();

        // Contains an SBC field, so DSF evaluates it, and it is never true
        const string expression = "sbc.dsf.pluginSupport && state.upTime < 0";
        Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} P\"{expression}\" R0"), Does.Not.Contain("Error"));
        try
        {
            string report = $"Trigger {TriggerNumber} fires when expression {{{expression}}} becomes true";
            Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain(report));
            Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber}"), Does.Contain(report), "plain M581 deleted the trigger");
        }
        finally
        {
            await SendCodeAsync($"M581.1 T{TriggerNumber} P-1");
        }
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain("is not configured"));
    }

    /// <summary>
    /// Contains an SBC field, so DSF evaluates it, and it turns true every 2 s
    /// </summary>
    private const string FiringSbcExpression = "sbc.dsf.pluginSupport && mod(state.upTime, 2) == 0";

    /// <summary>
    /// Wait until the test trigger has run, or fail
    /// </summary>
    private async Task WaitForTriggerRunAsync()
    {
        for (int i = 0; i < 20; i++)
        {
            if ((await QueryAsync("global.labTestTriggerRuns")).GetInt32() > 0)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        Assert.Fail("The trigger did not fire within 20 s");
    }

    /// <summary>
    /// An M581.1 expression that RRF evaluates must replace a trigger that DSF evaluates, otherwise both run the trigger macro (#67)
    /// </summary>
    [Test]
    public async Task RrfExpressionReplacesSbcTrigger()
    {
        await RequireFreeTriggerAsync();
        await SendCodeAsync($"M98 P\"{ResetMacro}\"");
        Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} P\"{FiringSbcExpression}\" R0"), Does.Not.Contain("Error"));
        try
        {
            await WaitForTriggerRunAsync();

            // Never true, so RRF never fires the trigger
            Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} P\"state.upTime < 0\" R0"), Does.Not.Contain("Error"));
            Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain("fires when expression {state.upTime < 0} becomes true"));

            // Let a trigger macro that is on its way finish, then the count must stay
            await Task.Delay(TimeSpan.FromSeconds(3));
            int runs = (await QueryAsync("global.labTestTriggerRuns")).GetInt32();
            await Task.Delay(TimeSpan.FromSeconds(8));
            Assert.That((await QueryAsync("global.labTestTriggerRuns")).GetInt32(), Is.EqualTo(runs), "DSF kept firing the old trigger");
        }
        finally
        {
            await DeleteTriggerAsync();
        }
    }

    /// <summary>
    /// A trigger that DSF evaluates fired with R-1 whenever the printer was not printing, but R-1 means disabled (#63)
    /// </summary>
    [Test]
    public async Task DisabledSbcTriggerDoesNotFire()
    {
        await RequireFreeTriggerAsync();
        await SendCodeAsync($"M98 P\"{ResetMacro}\"");
        Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} P\"{FiringSbcExpression}\" R-1"), Does.Not.Contain("Error"));
        try
        {
            Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain("if enabled would fire"));
            await Task.Delay(TimeSpan.FromSeconds(8));
            Assert.That((await QueryAsync("global.labTestTriggerRuns")).GetInt32(), Is.Zero, "The disabled trigger fired");

            // The expression does fire once the trigger is enabled
            Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} R0"), Does.Not.Contain("Error"));
            await WaitForTriggerRunAsync();
        }
        finally
        {
            await SendCodeAsync($"M581.1 T{TriggerNumber} P-1");
        }
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain("is not configured"));
    }

    /// <summary>
    /// M581.1 with an SBC field hung when a macro ran it: DSF clears the slot in RRF with an internal M581 T# P-1 before it registers the trigger,
    /// and that code had no file, so it waited on the base level of the channel behind the macro, which waited for the M581.1 (PR #216, lock census
    /// finding DL1, upstream 4229f5527). Fixed in DSF #216: the clear code takes the file, the channel and IsFromMacro of the M581.1
    /// (SbcTriggerService.cs:136-145), so it is sent as a code of the macro. Unfixed, the M98 never replies and the macro and its channel hang until an
    /// abort, an emergency stop or a reset; the unit test SbcTriggerInFiles.InMacro timed out after 10 s in M98 (PR #216). Here the test restarts DCS and
    /// resets RRF before it fails. Deterministic. The job file case of #216 (the clear code was cancelled and no trigger created) needs a job and is not
    /// tested here, and config.g is covered only by the unit test SbcTriggerInFiles.InConfigFile
    /// </summary>
    [Test]
    public async Task SbcTriggerConfiguredFromMacro()
    {
        await G3Helpers.RequireControlServerWithNewFixesAsync("#216");
        await RequireFreeTriggerAsync();
        await SendCodeAsync($"M98 P\"{ResetMacro}\"");
        try
        {
            string reply;
            try
            {
                reply = await SendCodeAsync($"M98 P\"{ConfiguringMacro}\"", 15);
            }
            catch (OperationCanceledException)
            {
                await Lab.RecoverAsync();
                await ReconnectSessionAsync();
                Assert.Fail("A macro with M581.1 and an SBC field did not end within 15 s");
                throw;
            }
            Assert.That(reply, Does.Not.Contain("Error"));
            Assert.That((await QueryAsync("global.labTestTriggerConfigured")).ValueKind, Is.EqualTo(JsonValueKind.True), "The macro did not get past M581.1");

            // SbcTriggerService.cs:198-205 reports a trigger that DSF evaluates
            Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber}"), Does.Contain($"Trigger {TriggerNumber} fires when expression {{{FiringSbcExpression}}} becomes true"));
            await WaitForTriggerRunAsync();
        }
        finally
        {
            await SendCodeAsync($"M581.1 T{TriggerNumber} P-1");
        }
    }

    /// <summary>
    /// Content of <see cref="TriggerMacro"/> for <see cref="TriggerAfterM99GetsItsMacro"/>. The dwell makes it run longer than the 200 ms period of
    /// its expression, so the trigger is pending again whenever M99 ends the macro
    /// </summary>
    private const string TriggerMacroWithM99 = $"M98 P\"{CalledMacro}\"\nG4 P300\nM99\n";

    /// <summary>
    /// When a trigger macro ended with M99 while the trigger was pending again, RRF requested the next trigger30.g before it had sent DSF the abort of
    /// the old one: M99 calls GCodeBuffer::AbortFile in SBC mode (GCodes.cpp:3852-3860, GCodeBuffer.cpp:1200-1228), which only sets abortFile, and
    /// IsTriggerBusy did not count a pending abort. DSF still had the old macro on the stack of the Trigger channel and refused the request with
    /// "System macro ... is requested but the stack is not empty. Discarding request." (Processor.cs:1670-1676, the request of a trigger is not
    /// from a code, GCodes.cpp:1079 and 3742), and RRF reported "Macro file trigger30.g not found" (GCodes.cpp:3742-3751). Fixed in RRF fork PR #13,
    /// part 1 (e1559cded, upstream 185ebb3de): IsTriggerBusy is true while the abort is pending (GCodes.cpp:432). Unfixed, the trigger is pending at
    /// nearly every M99, because its expression turns true every 200 ms (TriggerItem.cpp:115-133 and the pending check of GCodes.cpp:1010-1016 go on
    /// while the macro runs), so on an unfixed RRF nearly every run would end with the DSF warning; PR #13 argued this from the code and was not run
    /// on hardware, so the rate on the printer is not measured. The same journal checks as <see cref="TriggerMacroCallingMacro"/> cover RRF fork PR #12
    /// (a garbled macro name, about once per 1100 trigger runs), which this test catches statistically: about 2 runs per second, i.e. about 240 in
    /// 2 min, give about a 20 % chance per run. TriggerMacroCallingMacro has no M99, so it does not cover part 1
    /// </summary>
    [Test]
    public async Task TriggerAfterM99GetsItsMacro()
    {
        await RequireFreeTriggerAsync();
        await SendCodeAsync($"M98 P\"{ResetMacro}\"");
        await Lab.UploadAsync(Session, TriggerMacro, TriggerMacroWithM99);
        try
        {
            string cursor = await Lab.GetJournalCursorAsync();

            // Turns true five times per second (state.msUpTime is the millisecond part of the up time, RepRap.cpp:361). No string literals, see below
            Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} P\"mod(state.msUpTime, 200) < 100\" R0"), Does.Not.Contain("Error"));
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
                Assert.That(journal, Does.Not.Contain("is requested but the stack is not empty"), "RRF requested the next trigger macro before the abort of the last one");
                Assert.That(journal, Does.Not.Match("Macro file .* not found"));
                Assert.That(journal, Does.Not.Contain("Failed to start macro file"));
                Assert.That(journal, Does.Not.Contain("Out-of-order reply"));

                // A run takes at least the 300 ms dwell, so about two runs per second are possible
                Assert.That(runs, Is.GreaterThanOrEqualTo(Duration.TotalSeconds / 2), "The trigger macro ran less than once per 2 s");
            });
        }
        finally
        {
            await Lab.UploadAsync(Session, TriggerMacro, TriggerMacroContent);
        }
    }

    /// <summary>
    /// A trigger action (M581.1 U, a fork addition of RRF fork PR #25, design/rrf-local-trigger-actions.md:51-70, not in the G-code dictionary) runs in
    /// RRF alone, but one that called fileexists() or fileread() depended on DSF, which evaluates both in SBC mode (PR #115). Fixed in RRF fork PR
    /// #115: TriggerItem::IsAllowedAction refuses an action that calls either anywhere on its line (TriggerItem.cpp:464-468,
    /// StringParser::CallsFileFunction at StringParser.cpp:1296), and M581.1 replies "not allowed in an action, put it in trigger30.g" before it
    /// changes anything (TriggerItem.cpp:201-214). DSF passes the code on unchanged: its expression has no SBC field (SbcTriggerService.cs:126-130),
    /// and U is a quoted string, not an expression. Unfixed, set passes IsAllowedAction, the trigger is created with the action and is locked until
    /// the next restart (TriggerItem.cpp:342, 163-174), so trigger 30 cannot be deleted any more and the test restarts DCS and resets RRF before it
    /// fails. The expression is never true, so the action never runs. Deterministic. There is no positive control, because an allowed action
    /// would lock trigger 30 until the next restart as well
    /// </summary>
    [TestCase("fileexists(\"0:/sys/config.g\")")]
    [TestCase("fileread(\"0:/sys/config.g\", 0, 1, ',')")]
    public async Task ActionCallingFileFunctionIsRefused(string call)
    {
        G7Helpers.RequireMainBoardFixes(Session, G7Helpers.Rrf115);
        await RequireFreeTriggerAsync();

        // The quotes of the action are doubled in the G-code string
        string action = $"set global.labTestTriggerAction = {call}";
        string reply = await SendCodeAsync($"M581.1 T{TriggerNumber} P\"state.upTime < 0\" U\"{action.Replace("\"", "\"\"")}\" R0");
        TestContext.Progress.WriteLine($"M581.1 replied \"{reply.Trim()}\"");

        string report = await SendCodeAsync($"M581 T{TriggerNumber}");
        if (!report.Contains("is not configured"))
        {
            // A trigger with an action refuses P-1 until the next restart
            await DeleteTriggerAsync();
            if (!(await SendCodeAsync($"M581 T{TriggerNumber}")).Contains("is not configured"))
            {
                await Lab.RecoverAsync();
                await ReconnectSessionAsync();
            }
            Assert.Fail($"M581.1 accepted an action that calls a file function: \"{report.Trim()}\"");
        }
        Assert.That(reply, Does.Contain($"not allowed in an action, put it in trigger{TriggerNumber}.g"));
    }

    /// <summary>
    /// RRF 3.7 parsed a trigger expression under the heap read lock, and a string literal in it took the write lock, so M581.1 hung RRF
    /// until its watchdog reset it with StuckInSpinLoop. Fixed in RRF 59a04159b (2026-09-24), which is in 3.7.0+1.mp.1 and later
    /// </summary>
    [Test]
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
