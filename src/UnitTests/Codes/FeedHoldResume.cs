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
/// M24 on a paused job when it only releases a feed hold (upstream ae85dd5b5, "M24 that only releases a feed hold keeps a paused job
/// paused", with RRF 2092f4148).
/// </summary>
/// <remarks>
/// <para>
/// DSF resumes a paused job only when an M24 has been executed: MCodeHandler.CodeExecutedAsync calls JobProcessor.Resume
/// (MCodeHandler.cs:1467-1500, JobProcessor.cs:975-997).
/// </para>
/// <para>
/// Since RRF 2092f4148, an M24 during a feed hold only releases the hold, and a paused print stays paused until an M24 of its own.
/// So when the job is paused, DSF first waits for a full object model update. It leaves the job paused while RRF reports
/// held, resuming, pausing or paused (MCodeHandler.cs:1474-1485). Before ae85dd5b5, every successful M24 resumed the job.
/// </para>
/// <para>
/// The fake firmware does not pause prints itself, so the test pauses the job as LinkService.HandlePrintPaused does. It then holds
/// state.status at the value RRF would report after the M24 (FakeObjectModel.AutoStatus off). CodeExecutedAsync runs before the
/// reply of the code is returned, so the job state right after the reply is the one DSF has decided on.
/// </para>
/// </remarks>
[TestFixture]
public class FeedHoldResume
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

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    /// <summary>
    /// Start a job and pause it after its dwell as RRF would report it (LinkService.cs:886-904, as JobFiles.PauseAndResume does)
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
        await WaitForAsync(() => Jobs.IsPaused && !Jobs.IsProcessing);
    }

    /// <summary>
    /// Report the given machine status from now on
    /// </summary>
    private void ReportStatus(string status)
    {
        Host.Firmware.ObjectModel.AutoStatus = false;
        Host.Firmware.ObjectModel.Set("state.status", status);
    }

    [TestCase("held")]
    [TestCase("paused")]
    [TestCase("pausing")]
    [TestCase("resuming")]
    public async Task M24ThatOnlyReleasesAHoldKeepsThePausedJobPaused(string statusAfterM24)
    {
        await StartPausedJobAsync();

        // RRF still reports the print as held or paused after the M24, so the M24 has only released the hold
        ReportStatus(statusAfterM24);
        Assert.That(await Host.ExecuteAsync("M24"), Is.EqualTo("\n"));
        Assert.That(Jobs.IsPaused, Is.True, $"DSF resumed the paused job although RRF reports {statusAfterM24} after the M24");

        await Task.Delay(200);
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G1 X1", "G4 P1" }), "The job sent codes after the M24");
        using (await Host.Model.AccessReadOnlyAsync())
        {
            Assert.That(Host.Model.State.Status.ToString(), Is.EqualTo(statusAfterM24).IgnoreCase, "DSF decided on a status other than the one the fake firmware reported");
        }
    }

    [Test]
    public async Task SecondM24ResumesThePausedJob()
    {
        await StartPausedJobAsync();

        // First M24: the hold is released and the print stays paused
        ReportStatus("paused");
        Assert.That(await Host.ExecuteAsync("M24"), Is.EqualTo("\n"));
        Assert.That(Jobs.IsPaused, Is.True, "The first M24 resumed the paused job");

        // Second M24: RRF has resumed the print by the time it completes the code. A resuming M24 runs resume.g and completes in state
        // resuming3, which sets pauseState back to notPaused (RRF src/GCodes/GCodes4.cpp:750-751) before the reply goes out once the
        // state is normal again (GCodes4.cpp:2018-2027). So DSF sees the print running and resumes the job
        ReportStatus("processing");
        Assert.That(await Host.ExecuteAsync("M24"), Is.EqualTo("\n"));
        Assert.That(Jobs.IsPaused, Is.False, "The second M24 did not resume the paused job");

        await WaitForAsync(() => !Jobs.IsFileSelected);
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G1 X1", "G4 P1", "G1 X2", "G1 X3", "M400" }));
    }
}
