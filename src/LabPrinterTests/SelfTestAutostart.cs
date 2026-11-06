using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// The machine configuration starts its self-test from the Trigger channel with M98.3, the first real user of M98.n (#108)
/// </summary>
/// <remarks>
/// The short tier runs after every boot: chx350-config sys/meltingplot/self-test/tick.g. It reads the object model and neither moves nor heats
/// </remarks>
[TestFixture, Category("Restart")]
public class SelfTestAutostart : LabTest
{
    private const string Flow = "0:/macros/meltingplot/maintenance/self-test";

    /// <summary>
    /// Outcome of the short tier since the boot, 0 until it has reported
    /// </summary>
    private static int QuickOutcome(ObjectModel model) =>
        model.Global.TryGetValue("self_test_quick_outcome", out JsonElement? value) && value?.ValueKind == JsonValueKind.Number ? value.Value.GetInt32() : 0;

    /// <summary>
    /// After a watchdog reset the flow starts on HTTP by itself. Codes from a client keep getting answers while it runs, and the machine tick goes on
    /// </summary>
    [Test]
    public async Task FlowStartsAfterBootAndCodesGetAnswers()
    {
        JsonElement canStart = await QueryAsync("global.self_test_can_start");
        if (canStart.ValueKind != JsonValueKind.True)
        {
            Assert.Ignore($"This machine does not start a self-test by itself (global.self_test_can_start is {canStart})");
        }

        Stopwatch restart = Stopwatch.StartNew();
        try
        {
            await SendCodeAsync("M122 P1001");
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            // RRF may restart before it replies
        }
        Assert.That(await Lab.TryWaitForModelAsync(Session, model => !Lab.IsIdle(model), TimeSpan.FromSeconds(30)), Is.True, "RRF did not restart");
        await Lab.WaitForModelAsync(Session, model => Lab.IsIdle(model) && model.State.UpTime <= restart.Elapsed.TotalSeconds + 5, TimeSpan.FromSeconds(90), "the printer to become idle after the reset");
        string cursor = await Lab.GetJournalCursorAsync();

        // M98.3 refuses to start the flow while a code runs on HTTP, so send no code before the flow is seen. It lasts about a second,
        // so the queries may come after its end. UnbufferedCodes covers M409 during a macro for certain
        Stopwatch waiting = Stopwatch.StartNew();
        Assert.That(await Lab.TryWaitForModelAsync(Session, model => model.Inputs[CodeChannel.HTTP]?.StackDepth > 0 || QuickOutcome(model) != 0, TimeSpan.FromSeconds(120)), Is.True, "The self-test did not start within 2 minutes");
        try
        {
            Assert.That(await SendCodeAsync("M409 K\"state.status\"", 20), Does.Contain("\"key\":\"state.status\""));
            Assert.That(await SendCodeAsync("M409 K\"state.messageBox.title\"", 20), Does.Contain("\"key\":\"state.messageBox.title\""));
        }
        catch (OperationCanceledException)
        {
            await Lab.RecoverAsync();
            Assert.Fail("A code got no answer while the self-test ran");
        }

        await Lab.WaitForModelAsync(Session, model => QuickOutcome(model) != 0, TimeSpan.FromSeconds(30), "the self-test to report");
        int outcome = Lab.Read(Session, QuickOutcome);
        bool finished = await Lab.TryWaitForModelAsync(Session, model => model.Inputs[CodeChannel.HTTP]?.StackDepth == 0, TimeSpan.FromSeconds(10));

        // The machine tick runs every 10 s
        await Task.Delay(TimeSpan.FromSeconds(15));
        string journal = await Lab.ReadJournalAsync(cursor);
        int flowEnd = journal.LastIndexOf($"Aborted macro file {Flow}", StringComparison.Ordinal);
        TestContext.Progress.WriteLine($"Outcome {outcome}, {waiting.Elapsed.TotalSeconds:F0} s after the printer became idle");

        Assert.Multiple(() =>
        {
            Assert.That(journal, Does.Contain($"Starting macro file {Flow} on channel HTTP"));
            Assert.That(journal, Does.Contain("Self-test: quick check passed"));
            Assert.That(finished, Is.True, "The stack depth of HTTP did not return to 0");
            Assert.That(journal, Does.Not.Contain("Failed to start macro file"));
            Assert.That(journal, Does.Not.Contain("Out-of-order reply"));
            Assert.That(flowEnd, Is.GreaterThanOrEqualTo(0), "The flow ends with M99, which the journal shows as an abort");
            Assert.That(flowEnd >= 0 && journal.IndexOf("Starting macro file trigger8.g", flowEnd, StringComparison.Ordinal) >= 0, Is.True, "The machine tick did not go on after the flow");
        });
    }
}
