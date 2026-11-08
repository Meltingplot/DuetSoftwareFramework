using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer.Files;
using DuetControlServer.Link;
using DuetControlServer.Link.Channel;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Codes;

/// <summary>
/// Tests for jobs that fork the input reader with M606 S1
/// </summary>
[TestFixture]
public class ForkedJobs
{
    private FakeFirmwareHost _host = null!;

    [SetUp]
    public async Task SetUp() => _host = await FakeFirmwareHost.StartAsync();

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    private JobProcessor Jobs => _host.Services.GetRequiredService<JobProcessor>();

    private int StackDepth(CodeChannel channel) => _host.Services.GetRequiredService<Manager>()[channel].Stack.Count;

    private void WriteJob(string fileName, string content) => System.IO.File.WriteAllText(Path.Combine(_host.BaseDirectory, "gcodes", fileName), content);

    /// <summary>
    /// Wait for the job loop to finish and dispose the job files
    /// </summary>
    private async Task WaitForJobEndAsync()
    {
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        while (Jobs.IsFileSelected)
        {
            await Task.Delay(10, cts.Token);
        }
    }

    [Test]
    public async Task ForkAfterAbortWhileForkInMacroIsPending()
    {
        // The wiki (Gcodes.md:8101) allows M606 S1 in a macro invoked by a job
        WriteJob("job.g", "M98 P\"fork.g\"\nG1 X2\n");
        _host.WriteMacro("fork.g", "M606 S1\nG1 X1\n");

        // Hold RRF's reply to M606 S1, so that the job is aborted after MCodeHandler prepared the fork but before RRF forked.
        // JobProcessor.ForkAsync copies the File stack with the macro to File2 (Processor.CopyState); only a successful reply starts the copies
        using ManualResetEventSlim release = new();
        _host.Firmware.OnCode("M606", _ => release.IsSet ? FakeReply.Ok() : FakeReply.NotFinished);
        Assert.That(await _host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetReceivedCodes(CodeChannel.File).Any(code => code.Text == "M606 S1"), FakeFirmwareHost.DefaultTimeout), Is.True);

        // InvalidateChannel and the abort keyword abort a job this way
        await _host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.File);
        await WaitForJobEndAsync();

        // RRF never forked, so it sends no abort request for File2 (SbcInterface.cpp:1511). JobProcessor discards the copied state when the job ends,
        // like RRF closes File2 in StopPrint (GCodes.cpp:4886). Before, only a reset cleared it (LinkService.cs:1482)
        Assert.That(StackDepth(CodeChannel.File2), Is.EqualTo(1));

        // The next job must be able to fork. With the copied stack left over, Processor.CopyState threw "Cannot copy state because the stack is not empty".
        // Release the held code first so that the fake can go on; RRF would have discarded it with the print (SbcInterface.cpp:507-509)
        release.Set();
        WriteJob("job2.g", "M606 S1\nG1 X3\n");
        Assert.That(await _host.ExecuteAsync("M32 \"job2.g\""), Is.EqualTo("\n"));
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetReceivedCodes(CodeChannel.File).Count(code => code.Text == "M606 S1") == 2, FakeFirmwareHost.DefaultTimeout), Is.True);

        // The fake does not emulate forked input streams, so the forked job cannot finish. Abort it as well, which also discards the state of a fork that RRF made
        await _host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.File);
        await WaitForJobEndAsync();
        Assert.That(StackDepth(CodeChannel.File2), Is.EqualTo(1));
    }
}
