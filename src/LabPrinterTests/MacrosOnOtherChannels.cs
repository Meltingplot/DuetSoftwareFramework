using System;
using System.Linq;
using System.Threading.Tasks;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// M98.n starts a macro on another code channel, so that a trigger macro can start something blocking without holding the trigger channel
/// </summary>
/// <remarks>
/// Uses a temporary trigger that the machine configuration does not use and deletes it again. The macro only waits, nothing moves or heats
/// </remarks>
[TestFixture, Category("Trigger")]
public class MacrosOnOtherChannels : LabTest
{
    private const int TriggerNumber = 30;
    private const string TriggerMacro = "0:/sys/trigger30.g";
    private const string ResetMacro = $"{Lab.MacroDirectory}/other-channel/reset.g";
    private const string BlockingMacro = $"{Lab.MacroDirectory}/other-channel/blocking.g";

    /// <summary>
    /// How long the macro keeps the HTTP channel busy
    /// </summary>
    private const int MacroSeconds = 10;

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
              global labTestMacroRuns = 0
              global labTestRejected = 0
              global labTestParameters = ""
            else
              set global.labTestTriggerRuns = 0
              set global.labTestMacroRuns = 0
              set global.labTestRejected = 0
              set global.labTestParameters = ""

            """);
        await Lab.UploadAsync(session, BlockingMacro, $"""
            set global.labTestMacroRuns = global.labTestMacroRuns + 1
            set global.labTestParameters = param.S ^ "," ^ param.A[1] ^ "," ^ param.B ^ "," ^ param.D
            G4 S{MacroSeconds}

            """);
        await Lab.UploadAsync(session, TriggerMacro, $$"""
            var x = 5
            set global.labTestTriggerRuns = global.labTestTriggerRuns + 1
            M98.3 P"{{BlockingMacro}}" S{var.x} A{1,2} B{"5"} D{true}
            if result == 1
              set global.labTestRejected = global.labTestRejected + 1

            """);
    }

    [OneTimeTearDown]
    public async Task DeleteMacrosAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, TriggerMacro);
            await Lab.DeleteAsync(session, BlockingMacro);
            await Lab.DeleteAsync(session, ResetMacro);
            await Lab.DeleteAsync(session, $"{Lab.MacroDirectory}/other-channel");
        }
    }

    /// <summary>
    /// The trigger keeps firing while the macro it started blocks the HTTP channel. Further calls are refused through 'result', and the macro
    /// gets its parameters from the variables of the trigger macro with the same types as from M98
    /// </summary>
    [Test]
    public async Task TriggerKeepsFiringWhileMacroRuns()
    {
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain("is not configured"), $"Trigger {TriggerNumber} is in use");
        await SendCodeAsync($"M98 P\"{ResetMacro}\"");
        string cursor = await Lab.GetJournalCursorAsync();

        // Turns true twice per second. No string literals, they hang RRF 3.7 in M581.1
        Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} P\"mod(state.msUpTime, 500) < 250\" R0"), Does.Not.Contain("Error"));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(MacroSeconds / 2));
        }
        finally
        {
            // Regular codes of the HTTP channel wait behind the macro, so this returns once it has finished
            await SendCodeAsync($"M581 T{TriggerNumber} P-1", MacroSeconds * 3);
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
        int triggerRuns = (await QueryAsync("global.labTestTriggerRuns")).GetInt32();
        int macroRuns = (await QueryAsync("global.labTestMacroRuns")).GetInt32();
        int rejected = (await QueryAsync("global.labTestRejected")).GetInt32();
        string? parameters = (await QueryAsync("global.labTestParameters")).GetString();
        string journal = await Lab.ReadJournalAsync(cursor);
        TestContext.Progress.WriteLine($"Trigger ran {triggerRuns} times, macro {macroRuns} times, {rejected} calls were refused, parameters {parameters}");

        Assert.Multiple(() =>
        {
            Assert.That(macroRuns, Is.GreaterThanOrEqualTo(1), "The macro did not start");
            Assert.That(rejected, Is.GreaterThanOrEqualTo(MacroSeconds / 4), "The trigger stopped firing while the macro was running, or M98.3 did not refuse the calls");
            Assert.That(triggerRuns, Is.EqualTo(macroRuns + rejected), "A call neither started the macro nor set result to 1");
            Assert.That(parameters, Is.EqualTo("5,2,5,true"));
            Assert.That(journal, Does.Not.Contain("Failed to start macro file"));
            Assert.That(journal, Does.Not.Contain("Out-of-order reply"));
        });
    }
}
