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
/// A pause that RRF reports while the job already waits to be resumed (lab print 2026-11-27: the job ended as "finished" 2.4 minutes after it was
/// resumed, without a single move)
/// </summary>
/// <remarks>
/// <para>
/// JobProcessor.Pause cancels the cancellation token source of the job and creates a new one (JobProcessor.cs:964-966). The print task takes the new
/// token when it sees the pause before it reads the next code (JobProcessor.cs:495-497), rewinds the file and waits to be resumed
/// (JobProcessor.cs:697). A second Pause while it waits cancels that token, too. After the resume the task read the rest of the file with it: every
/// code was cancelled before it reached RRF, the task got to the end of the file and finished the job.
/// </para>
/// <para>
/// Journal of the lab printer: DSF had kept the job paused after an M24 in a macro (MacroResumeBeforeM99), RRF was printing. 06:14:38 the M25 of the
/// operator: "Resume state saved", but no "Job on File has been paused" because the task already waited. 06:14:51 the M24 of the operator, resume.g
/// finished 06:15:00. M122 "DSF" 06:15:15: "File ... is selected, processing", job.filePosition 1470 bytes past the pause, no move in the QA record
/// until 06:17:24 "Finished job file" and stop.g. The file had 116.9 MB left.
/// </para>
/// </remarks>
[TestFixture]
public class PauseWhileWaitingForResume
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

    [Test]
    public async Task JobPausedAgainWhileWaitingRunsOnAfterTheResume()
    {
        // One buffered print code, so that JobProcessor reads a code only after the previous one finished and the held dwell stops the job
        _host = await FakeFirmwareHost.StartAsync(configureSettings: settings => settings.BufferedPrintCodes = 1);
        using ManualResetEventSlim release = new();
        Host.Firmware.OnCode("G4", _ => release.IsSet ? FakeReply.Ok() : FakeReply.NotFinished);
        string content = "G1 X1\nG4 P1\nG1 X2\nG1 X3\n";
        long pausePosition = content.IndexOf("G1 X2", StringComparison.Ordinal);
        System.IO.File.WriteAllText(Path.Combine(Host.BaseDirectory, "gcodes", "job.g"), content);

        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        Assert.That(Host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.File).Any(code => code.Text == "G4 P1"), FakeFirmwareHost.DefaultTimeout), Is.True);

        // Pause as RRF reports it (LinkService.cs:1037-1044) and let the task rewind and wait
        using (Jobs.Lock())
        {
            Jobs.Pause(pausePosition, null, PrintPausedReason.User);
        }
        release.Set();
        Assert.That(await WaitForAsync(() => Jobs.IsPaused && !Jobs.IsProcessing), Is.True, "The job did not pause");

        // RRF reports a pause again while the task waits
        using (Jobs.Lock())
        {
            Jobs.Pause(pausePosition, null, PrintPausedReason.User);
        }

        // The M24 of the operator resumes the print
        Host.Firmware.ObjectModel.AutoStatus = false;
        Host.Firmware.ObjectModel.Set("state.status", "processing");
        Assert.That(await Host.ExecuteAsync("M24"), Is.EqualTo("\n"));
        Assert.That(Jobs.IsPaused, Is.False, "The M24 did not resume the job");

        Assert.That(await WaitForAsync(() => !Jobs.IsFileSelected), Is.True, "The job did not finish");
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G1 X1", "G4 P1", "G1 X2", "G1 X3", "M400" }), "The job did not send the codes after the pause position");
    }
}
