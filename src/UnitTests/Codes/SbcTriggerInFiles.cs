using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer.Files;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Codes;

/// <summary>
/// M581.1 with an expression that contains SBC fields, run from macro and job files. DSF clears the trigger slot in RRF with M581 T&lt;n&gt; P-1
/// and waits for that code before the M581.1 completes (SbcTriggerService.cs:136-145). The link sends only the codes of the stack level
/// on top (Processor.cs:1214-1225), and the pipeline puts a code on the stack level of its file (PipelineBase.cs:230-240), so the clear code
/// must come from the file of the M581.1. From the base level it waited for the macro, which waited for the M581.1, and on a file channel
/// with a job it found no stack level and was cancelled. Lock census finding DL1
/// </summary>
[TestFixture]
public class SbcTriggerInFiles
{
    private const string Expression = "sbc.cpu.avgLoad > 0.5";

    private FakeFirmwareHost _host = null!;

    [SetUp]
    public async Task SetUp()
    {
        _host = await FakeFirmwareHost.StartAsync();

        // PeriodicUpdateService, which sets the SBC fields in DCS, does not run in the fake firmware host
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.SBC!.CPU.AvgLoad = 0.1F;
        }
    }

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    private string[] Executed(CodeChannel channel) => [.. _host.Firmware.GetExecutedCodes(channel).Select(code => code.Text)];

    /// <summary>
    /// The clear code DSF sent for a trigger
    /// </summary>
    private FakeCode ClearCode(int trigger) => _host.Firmware.GetReceivedCodes().Single(code => code.Text == $"M581 T{trigger} P-1");

    /// <summary>
    /// Check that DSF registered the trigger, i.e. that the M581.1 completed. The report is DSF's (SbcTriggerService.cs:198-205)
    /// </summary>
    private async Task AssertRegisteredAsync(int trigger, string expression) =>
        Assert.That(await _host.ExecuteAsync($"M581.1 T{trigger}"), Is.EqualTo($"Trigger {trigger} fires when expression {{{expression}}} becomes true\n"));

    [Test]
    public async Task InMacro()
    {
        _host.WriteMacro("trig.g", $"M581.1 T20 P\"{Expression}\"\nG4 P20\n");

        Assert.That(await _host.ExecuteAsync("M98 P\"trig.g\""), Is.EqualTo("\n"), "The macro did not complete");

        // RRF gets the clear code as a code of the macro, before the code that follows the M581.1
        Assert.That(Executed(CodeChannel.HTTP), Is.EqualTo(new[] { "M98 P\"trig.g\"", "M581 T20 P-1", "G4 P20" }));
        FakeCode clearCode = ClearCode(20);
        Assert.That((clearCode.Channel, clearCode.IsFromMacro), Is.EqualTo((CodeChannel.HTTP, true)));
        Assert.That(_host.Firmware.GetStackDepth(CodeChannel.HTTP), Is.Zero);
        await AssertRegisteredAsync(20, Expression);
    }

    [Test]
    public async Task InNestedMacro()
    {
        _host.WriteMacro("outer.g", "M98 P\"inner.g\"\nG4 P21\n");
        _host.WriteMacro("inner.g", $"M581.1 T21 P\"{Expression}\"\nG4 P22\n");

        Assert.That(await _host.ExecuteAsync("M98 P\"outer.g\""), Is.EqualTo("\n"), "The macros did not complete");

        Assert.That(Executed(CodeChannel.HTTP), Is.EqualTo(new[] { "M98 P\"outer.g\"", "M98 P\"inner.g\"", "M581 T21 P-1", "G4 P22", "G4 P21" }));
        FakeCode clearCode = ClearCode(21);
        Assert.That((clearCode.Channel, clearCode.IsFromMacro), Is.EqualTo((CodeChannel.HTTP, true)));
        Assert.That(_host.Firmware.GetStackDepth(CodeChannel.HTTP), Is.Zero);
        await AssertRegisteredAsync(21, Expression);
    }

    [Test]
    public async Task InConfigFile()
    {
        // RRF runs config.g on the trigger channel (GCodes::RunConfigFile, GCodes.cpp:417 of the RRF pinned in firmware/), the request is the one
        // RRF sends at start-up. The expression also has a field that DSF does not evaluate, so the initial evaluation asks RRF on the trigger channel
        // (SbcTriggerService.cs:218-220, Expressions.cs:1033-1037) while config.g runs on that channel. That request does not depend on the stack
        // level (LinkService.cs:554-580), and RRF evaluates it under the mutex of the channel or refuses the packet so that DSF sends it again
        // (SbcInterface.cpp:761-842)
        const string expression = "state.status = \"processing\" && sbc.cpu.avgLoad > 0.5";
        _host.WriteMacro("config.g", "M581.1 T22 P\"state.status = \"\"processing\"\" && sbc.cpu.avgLoad > 0.5\"\nG4 P23\n");

        _host.Firmware.RequestMacro(CodeChannel.Trigger, "config.g");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Trigger).Any(code => code.Text == "G4 P23") &&
            firmware.GetStackDepth(CodeChannel.Trigger) == 0, FakeFirmwareHost.DefaultTimeout), Is.True, "config.g did not complete");

        // DSF sends M550 and M905 before the content of config.g (MacroFile.cs:303-330)
        Assert.That(Executed(CodeChannel.Trigger).TakeLast(2), Is.EqualTo(new[] { "M581 T22 P-1", "G4 P23" }));
        FakeCode clearCode = ClearCode(22);
        Assert.That((clearCode.Channel, clearCode.IsFromMacro), Is.EqualTo((CodeChannel.Trigger, true)));
        Assert.That(_host.Firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.EvaluateExpression && request.Channel == CodeChannel.Trigger),
            Is.True, "The initial state of the expression was not evaluated by RRF");
        await AssertRegisteredAsync(22, expression);
    }

    [Test]
    public async Task InJob()
    {
        System.IO.File.WriteAllText(Path.Combine(_host.BaseDirectory, "gcodes", "job.g"), $"M581.1 T23 P\"{Expression}\"\nG4 P24\n");

        Assert.That(await _host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        JobProcessor jobs = _host.Services.GetRequiredService<JobProcessor>();
        using (CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout))
        {
            while (jobs.IsFileSelected)
            {
                await Task.Delay(10, cts.Token);
            }
        }

        // The job file is the file of the base level on the File channel (PipelineBase.cs:285), there is no level without a file then.
        // JobProcessor waits for the moves of the job with a final M400 after PurgeSyncRequestsFor has taken the job file off the base level (JobProcessor.cs:586, 599-615)
        Assert.That(Executed(CodeChannel.File), Is.EqualTo(new[] { "M581 T23 P-1", "G4 P24", "M400" }));
        FakeCode clearCode = ClearCode(23);
        Assert.That((clearCode.Channel, clearCode.IsFromMacro), Is.EqualTo((CodeChannel.File, false)));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.Job.LastFileAborted, Is.False);
        }
        await AssertRegisteredAsync(23, Expression);
    }
}
