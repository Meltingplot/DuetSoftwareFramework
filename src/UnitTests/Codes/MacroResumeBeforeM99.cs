using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Codes;

/// <summary>
/// M24 in a macro that returns with M99 right after it, as the filament error handler of the lab printer does after an automatic recovery
/// (lab print 2026-11-27, stalled twice for hours on DSF 3.7.1-rc.1+mp.1)
/// </summary>
/// <remarks>
/// <para>
/// DSF resumes a paused job when an M24 has been executed (MCodeHandler.CodeExecutedAsync, JobProcessor.Resume). Since upstream ae85dd5b5 it first
/// waits for a full object model update, to tell an M24 that resumed the print from one that only released a feed hold (MCodeHandler.cs:1479-1490).
/// That wait used the token of the code, and a code from a macro carries the token of its macro file (MacroFile.cs:346). RRF executes the M99 after
/// the M24 and asks DSF to abort the macro (Link/Channel/Processor.cs:816, :873), which cancels that token (MacroFile.cs:241) while the
/// M24 still waits. Resume was skipped: RRF printed on with nothing to print, and DSF kept the job paused.
/// </para>
/// <para>
/// Journal of the lab printer: 23:38:26.946 resume.g finished (the M24 completes after it), 23:38:27.040 file abort request for the last file,
/// 23:38:27.041 "Aborted macro file filament-error.g", 23:38:27.043 TaskCanceledException in ObjectModel.WaitForFullUpdateAsync called from
/// MCodeHandler.CodeExecutedAsync. The same again at 06:08:06.99 after the next recovery.
/// </para>
/// <para>
/// The test holds an update cycle so that the M24 is still waiting when DSF handles the abort of the macro, as on the printer. If DSF had handled the
/// abort only after the release, the old code would pass too: the timing can only hide the bug, it cannot fail the fixed code.
/// </para>
/// </remarks>
[TestFixture]
public class MacroResumeBeforeM99
{
    private FakeFirmwareHost? _host;

    private FakeFirmwareHost Host => _host!;

    private JobProcessor Jobs => Host.Services.GetRequiredService<JobProcessor>();

    [TearDown]
    public async Task TearDown()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }
    }

    private string[] JobCodes() => [.. Host.Firmware.GetExecutedCodes(CodeChannel.File).Select(code => code.Text)];

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + FakeFirmwareHost.DefaultTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }
            await Task.Delay(10);
        }
        return true;
    }

    /// <summary>
    /// Start a job and pause it after its dwell as RRF would report it (LinkService.cs:1037-1044), as FeedHoldResume does
    /// </summary>
    private async Task StartPausedJobAsync()
    {
        // One buffered print code, so that JobProcessor reads a code only after the previous one finished and the held dwell stops the job
        _host = await FakeFirmwareHost.StartAsync(configureSettings: settings => settings.BufferedPrintCodes = 1);
        using ManualResetEventSlim release = new();
        Host.Firmware.OnCode("G4", _ => release.IsSet ? FakeReply.Ok() : FakeReply.NotFinished);
        string content = "G1 X1\nG4 P1\nG1 X2\nG1 X3\n";
        System.IO.File.WriteAllText(Path.Combine(Host.BaseDirectory, "gcodes", "job.g"), content);

        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        Assert.That(Host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.File).Any(code => code.Text == "G4 P1"), FakeFirmwareHost.DefaultTimeout), Is.True);

        using (Jobs.Lock())
        {
            Jobs.Pause(content.IndexOf("G1 X2", StringComparison.Ordinal), null, PrintPausedReason.User);
        }
        release.Set();
        Assert.That(await WaitForAsync(() => Jobs.IsPaused && !Jobs.IsProcessing), Is.True, "The job did not pause");

        // RRF reports the print as paused until an M24 resumes it
        Host.Firmware.ObjectModel.AutoStatus = false;
        Host.Firmware.ObjectModel.Set("state.status", "paused");
        await Host.Model.WaitForFullUpdateAsync();
    }

    /// <summary>
    /// Run the macro "M24, M99" while an update cycle that started before it is held, and release the cycle once DSF has handled the abort of the macro
    /// </summary>
    /// <param name="statusAfterM24">Status RRF reports once it has executed the M24</param>
    private async Task RunResumeMacroAsync(string statusAfterM24)
    {
        Host.WriteMacro("recover.g", "M24\nM99\n");
        Host.Firmware.OnCode("M24", _ =>
        {
            Host.Firmware.ObjectModel.Set("state.status", statusAfterM24);
            return FakeReply.Ok();
        });

        // Hold the next update cycle, so the M24 waits for the one after it
        Host.Firmware.HoldObjectModelReplies("global");
        Host.Firmware.ObjectModel.IncrementSeq("global");
        Assert.That(Host.Firmware.WaitUntil(firmware => firmware.HeldObjectModelRequests > 0, FakeFirmwareHost.DefaultTimeout), Is.True, "The update cycle did not ask for global");

        Task<string> macro = Host.StartCode("M98 P\"recover.g\"");
        Assert.That(Host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M99"), FakeFirmwareHost.DefaultTimeout), Is.True, "M99 did not reach the firmware");

        // RRF sends the abort request for M99 with the next transfer, give DSF the time to abort the macro while the M24 waits
        await Task.Delay(500);
        Host.Firmware.ReleaseObjectModelReplies();
        Assert.That(await macro.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
    }

    [Test]
    public async Task M24FollowedByM99ResumesThePausedJob()
    {
        await StartPausedJobAsync();

        // RRF has resumed the print when it completes the M24 (RRF src/GCodes/GCodes4.cpp:750-751, :2018-2027)
        await RunResumeMacroAsync("processing");
        Assert.That(await WaitForAsync(() => !Jobs.IsPaused), Is.True, "DSF kept the job paused after the macro that resumed it returned with M99");

        Assert.That(await WaitForAsync(() => !Jobs.IsFileSelected), Is.True, "The job did not finish");
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G1 X1", "G4 P1", "G1 X2", "G1 X3", "M400" }));
    }

    [Test]
    public async Task M24ThatOnlyReleasesAHoldKeepsTheJobPausedWhenM99Follows()
    {
        await StartPausedJobAsync();

        // The M24 has only released a feed hold, RRF still reports the print as paused (upstream ae85dd5b5)
        await RunResumeMacroAsync("paused");
        await Task.Delay(500);
        Assert.That(Jobs.IsPaused, Is.True, "DSF resumed the paused job although RRF reports it paused after the M24");
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G1 X1", "G4 P1" }), "The job sent codes after the M24");
    }
}
