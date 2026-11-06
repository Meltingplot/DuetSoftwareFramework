using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
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
    /// How long the macro keeps its channel busy
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
              global labTestInput = -1
            else
              set global.labTestTriggerRuns = 0
              set global.labTestMacroRuns = 0
              set global.labTestRejected = 0
              set global.labTestParameters = ""
              set global.labTestInput = -1

            """);
        await Lab.UploadAsync(session, BlockingMacro, $"""
            set global.labTestMacroRuns = global.labTestMacroRuns + 1
            set global.labTestParameters = param.S ^ "," ^ param.A[1] ^ "," ^ param.B ^ "," ^ param.D
            set global.labTestInput = state.thisInput
            echo "labTest output of the macro"
            G4 S{MacroSeconds}

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
    /// The trigger keeps firing while the macro it started blocks the other channel. Further calls are refused through 'result', and the macro
    /// gets its parameters from the variables of the trigger macro with the same types as from M98
    /// </summary>
    /// <param name="number">Channel number of M118 P</param>
    /// <param name="channel">Expected code channel</param>
    /// <param name="outputReachesMessages">Whether DSF outputs the echo of the macro as a message, see Executed.cs</param>
    [TestCase(2, CodeChannel.Aux, false)]
    [TestCase(3, CodeChannel.HTTP, true)]
    [TestCase(4, CodeChannel.Telnet, true)]
    [TestCase(5, CodeChannel.Aux2, false)]
    public async Task TriggerKeepsFiringWhileMacroRuns(int number, CodeChannel channel, bool outputReachesMessages)
    {
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain("is not configured"), $"Trigger {TriggerNumber} is in use");
        await Lab.UploadAsync(Session, TriggerMacro, $$"""
            var x = 5
            set global.labTestTriggerRuns = global.labTestTriggerRuns + 1
            M98.{{number}} P"{{BlockingMacro}}" S{var.x} A{1,2} B{"5"} D{true}
            if result == 1
              set global.labTestRejected = global.labTestRejected + 1

            """);
        await SendCodeAsync($"M98 P\"{ResetMacro}\"");
        string cursor = await Lab.GetJournalCursorAsync();

        // Turns true twice per second. No string literals, they hang RRF 3.7 in M581.1
        Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} P\"mod(state.msUpTime, 500) < 250\" R0"), Does.Not.Contain("Error"));
        bool macroSeen, busySeen = false;
        Stopwatch deleteTime = new();
        try
        {
            macroSeen = await Lab.TryWaitForModelAsync(Session, model => model.Inputs[channel]?.StackDepth > 0, TimeSpan.FromSeconds(MacroSeconds / 2));
            busySeen = macroSeen && Lab.Read(Session, model => model.State.Status) == MachineStatus.Busy;
            await Task.Delay(TimeSpan.FromSeconds(MacroSeconds / 2));
        }
        finally
        {
            // Regular codes of the HTTP channel wait behind a macro on that channel, other channels do not get in their way
            deleteTime.Start();
            await SendCodeAsync($"M581 T{TriggerNumber} P-1", MacroSeconds * 3);
            deleteTime.Stop();
        }

        bool finished = await Lab.TryWaitForModelAsync(Session, model => model.Inputs[channel]?.StackDepth == 0, TimeSpan.FromSeconds(MacroSeconds * 3));
        await Task.Delay(TimeSpan.FromSeconds(2));
        int triggerRuns = (await QueryAsync("global.labTestTriggerRuns")).GetInt32();
        int macroRuns = (await QueryAsync("global.labTestMacroRuns")).GetInt32();
        int rejected = (await QueryAsync("global.labTestRejected")).GetInt32();
        int input = (await QueryAsync("global.labTestInput")).GetInt32();
        string? parameters = (await QueryAsync("global.labTestParameters")).GetString();
        string journal = await Lab.ReadJournalAsync(cursor);
        TestContext.Progress.WriteLine($"M98.{number} ({channel}): trigger ran {triggerRuns} times, macro {macroRuns} times, {rejected} calls were refused, input {input}, parameters {parameters}, " +
            $"seen in inputs[] {macroSeen}, busy {busySeen}, HTTP code took {deleteTime.Elapsed.TotalSeconds:F1} s, output in journal {journal.Contains("labTest output of the macro")}");

        Assert.Multiple(() =>
        {
            Assert.That(macroRuns, Is.GreaterThanOrEqualTo(1), "The macro did not start");
            Assert.That(input, Is.EqualTo((int)channel), "The macro ran on the wrong channel");
            Assert.That(macroSeen, Is.True, "The object model did not show the macro on the channel");
            Assert.That(busySeen, Is.True, "The machine did not report busy");
            Assert.That(finished, Is.True, "The stack depth of the channel did not return to 0");
            Assert.That(rejected, Is.GreaterThanOrEqualTo(MacroSeconds / 4), "The trigger stopped firing while the macro was running, or M98.n did not refuse the calls");
            Assert.That(triggerRuns, Is.EqualTo(macroRuns + rejected), "A call neither started the macro nor set result to 1");
            Assert.That(parameters, Is.EqualTo("5,2,5,true"));
            Assert.That(journal.Contains("labTest output of the macro"), Is.EqualTo(outputReachesMessages), "Unexpected route of the echo output");
            Assert.That(journal, Does.Not.Contain("Failed to start macro file"));
            Assert.That(journal, Does.Not.Contain("Out-of-order reply"));
            if (channel != CodeChannel.HTTP)
            {
                Assert.That(deleteTime.Elapsed.TotalSeconds, Is.LessThan(MacroSeconds / 2), "A code on the HTTP channel had to wait for a macro on another channel");
            }
        });
    }

    /// <summary>
    /// Calls that M98.n refuses without starting anything. USB and USB2 are left out, see design/open-findings.md
    /// </summary>
    [TestCase($"M98.1 P\"{BlockingMacro}\"", "Invalid code channel number 1")]
    [TestCase($"M98.6 P\"{BlockingMacro}\"", "Invalid code channel number 6")]
    [TestCase($"M98.7 P\"{BlockingMacro}\"", "Invalid code channel number 7")]
    [TestCase($"M98.3 P\"{BlockingMacro}\"", "Use M98 to start a macro on the same code channel")]
    [TestCase($"M98.2 P\"{Lab.MacroDirectory}/other-channel/missing.g\"", "not found")]
    public async Task CallIsRefused(string code, string error)
    {
        Assert.That(await SendCodeAsync(code), Does.Contain(error));
    }
}
