using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Files;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Codes;

/// <summary>
/// Tests for job files that JobProcessor reads and DSF sends to the fake firmware: completion, abort, cancel, M26/M27,
/// pause and resume, simulation and selecting another file from a job
/// </summary>
[TestFixture]
public class JobFiles
{
    private FakeFirmwareHost? _host;

    private FakeFirmwareHost Host => _host!;

    /// <summary>
    /// Start the host. One buffered print code makes JobProcessor read a code only after the previous one finished,
    /// so that a held code stops the job at a known position
    /// </summary>
    private async Task<FakeFirmwareHost> StartAsync(int bufferedPrintCodes = 32) =>
        _host = await FakeFirmwareHost.StartAsync(configureSettings: settings => settings.BufferedPrintCodes = bufferedPrintCodes);

    [TearDown]
    public async Task TearDown()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }
    }

    private JobProcessor Jobs => Host.Services.GetRequiredService<JobProcessor>();

    private string WriteJob(string fileName, string content)
    {
        string path = Path.Combine(Host.BaseDirectory, "gcodes", fileName);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Codes the firmware executed on the File channel
    /// </summary>
    private string[] JobCodes() => [.. Host.Firmware.GetExecutedCodes(CodeChannel.File).Select(code => code.Text)];

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

    private bool WaitUntilExecuted(string code) =>
        Host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.File).Any(item => item.Text == code), FakeFirmwareHost.DefaultTimeout);

    private PrintStoppedReason[] StopReasons() =>
        [.. Host.Firmware.GetReceivedRequests().Where(request => request.Request == SbcRequest.PrintStopped).Select(request => request.PrintStoppedReason)];

    private async Task<(bool aborted, bool cancelled, bool simulated)> LastFileAsync()
    {
        using (await Host.Model.AccessReadOnlyAsync())
        {
            return (Host.Model.Job.LastFileAborted, Host.Model.Job.LastFileCancelled, Host.Model.Job.LastFileSimulated);
        }
    }

    [Test]
    public async Task CompleteJob()
    {
        await StartAsync();
        WriteJob("job.g", "G1 X1\n; comment\nG1 X2 F100\nM400\n");

        // M32 selects the file and lets RRF start it, the job starts when its reply comes back (MCodeHandler.cs:227-264, 1403-1413)
        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        await WaitForJobEndAsync();

        // JobProcessor waits for the moves of the job with a final M400 before it reports the end (JobProcessor.cs:586-612)
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G1 X1", "G1 X2 F100", "M400", "M400" }));
        Assert.That(StopReasons(), Is.EqualTo(new[] { PrintStoppedReason.NormalCompletion }));
        Assert.That(await LastFileAsync(), Is.EqualTo((false, false, false)));

        // The codes carry their file positions in the order of the file
        long?[] positions = [.. Host.Firmware.GetExecutedCodes(CodeChannel.File).Select(code => code.FilePosition).Where(position => position is not null)];
        Assert.That(positions, Is.EqualTo(new long?[] { 0, 16, 27 }));
    }

    [Test]
    public async Task AbortFromJob()
    {
        // abort terminates the print (wiki, Abort command). JobProcessor reports it as aborted to RRF (JobProcessor.cs:803-807)
        await StartAsync();
        WriteJob("job.g", "G1 X1\nabort \"stop\"\nG1 X2\n");
        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        await WaitForJobEndAsync();

        Assert.That(JobCodes(), Does.Not.Contain("G1 X2"));
        Assert.That((await LastFileAsync()).aborted, Is.True);
    }

    [Test]
    public async Task ErrorInJob()
    {
        // A read error aborts the job (JobProcessor.cs:511-528) and is reported with the file and line
        await StartAsync();
        WriteJob("job.g", "G1 X1\nelse\nG1 X2\n");
        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        await WaitForJobEndAsync();

        Assert.That(JobCodes(), Does.Not.Contain("G1 X2"));
        Assert.That(StopReasons(), Is.EqualTo(new[] { PrintStoppedReason.Abort }));
        using (await Host.Model.AccessReadOnlyAsync())
        {
            Assert.That(Host.Model.Messages.Select(message => message.ToString()), Has.Some.Match("^Error: in file job.g line [0-9]+: unexpected else$"));
            Assert.That(Host.Model.Job.LastFileAborted, Is.True);
        }
    }

    [Test, Explicit("A read error is reported with the line after the failing one: JobProcessor.cs:523 takes file.LineNumber, the line the parser has advanced to, because readCode is null")]
    public async Task ErrorInJobNamesTheFailingLine()
    {
        // The CodeParserException carries the code with its line number (CodeFile.cs:559)
        await StartAsync();
        WriteJob("job.g", "G1 X1\nelse\nG1 X2\n");
        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        await WaitForJobEndAsync();
        using (await Host.Model.AccessReadOnlyAsync())
        {
            Assert.That(Host.Model.Messages.Select(message => message.ToString()), Has.Some.EqualTo("Error: in file job.g line 2: unexpected else"));
        }
    }

    [Test]
    public async Task CodesBeforeLineThatCannotBeReadStillRun()
    {
        // CodeFile throws for an unexpected else while it reads the line (CodeFile.cs, "unexpected else"). RRF has executed the lines before it by then,
        // so DSF must not cancel those that it has read ahead and not finished yet. Codes keep the job's token since upstream d108e9767, so aborting
        // the job at once would cancel them. RRF holds M400 until the error is reported, so echo waits in its flush and the G-codes after it wait
        // behind echo while DSF reads the else
        await StartAsync();
        using ManualResetEventSlim release = new();
        Host.Firmware.OnCode("M400", _ => release.IsSet ? FakeReply.Ok() : FakeReply.NotFinished);
        WriteJob("job.g", "M400\necho \"before\"\nG4 P1\nG4 P2\nelse\nG4 P99\n");
        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));

        using (CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout))
        {
            while (!(await ErrorMessagesAsync()).Any(message => message.EndsWith(": unexpected else")))
            {
                await Task.Delay(10, cts.Token);
            }
        }
        release.Set();
        await WaitForJobEndAsync();

        // The firmware runs M400 again when the job ends only if it completes normally (JobProcessor.cs, waitForMoves)
        Assert.That(JobCodes(), Is.EqualTo(new[] { "M400", "G4 P1", "G4 P2" }));
        Assert.That((await LastFileAsync()).aborted, Is.True);
        Assert.That(StopReasons(), Is.EqualTo(new[] { PrintStoppedReason.Abort }));
        Assert.That(await ErrorMessagesAsync(), Has.Length.EqualTo(1));
    }

    private async Task<string[]> ErrorMessagesAsync()
    {
        using (await Host.Model.AccessReadOnlyAsync())
        {
            return [.. Host.Model.Messages.Where(message => message.Type == MessageType.Error).Select(message => message.Content)];
        }
    }

    [Test]
    public async Task StatusWhileRunning()
    {
        await StartAsync();
        using ManualResetEventSlim release = new();
        Host.Firmware.OnCode("G4", _ => release.IsSet ? FakeReply.Ok() : FakeReply.NotFinished);
        string content = "G1 X1\nG4 P1\nG1 X2\n";
        WriteJob("job.g", content);

        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        Assert.That(WaitUntilExecuted("G4 P1"), Is.True);

        // M27 reports the read position and the length (MCodeHandler.cs:324-346)
        Assert.That(await Host.ExecuteAsync("M27"), Does.Match($"^SD printing byte [0-9]+/{content.Length}\n$"));

        // M26 checks the position against the file length (MCodeHandler.cs:306-312)
        Assert.That(await Host.ExecuteAsync($"M26 S{content.Length + 1}"), Is.EqualTo("Error: M26: Position is out of range\n"));
        Assert.That(await Host.ExecuteAsync("M26 S-1"), Is.EqualTo("Error: M26: Position is out of range\n"));

        // A second file cannot be selected while one is printed (MCodeHandler.cs:247-250), and M0 needs a paused print (MCodeHandler.cs:100-103)
        WriteJob("other.g", "G1 Y1\n");
        Assert.That(await Host.ExecuteAsync("M32 \"other.g\""), Is.EqualTo("Error: M32: Cannot set file to print, because a file is already being printed\n"));
        Assert.That(await Host.ExecuteAsync("M37 P\"other.g\""), Is.EqualTo("Error: M37: Cannot set file to simulate, because a file is already being printed\n"));
        Assert.That(await Host.ExecuteAsync("M0"), Is.EqualTo("Error: M0: Pause the print before attempting to cancel it\n"));

        release.Set();
        await WaitForJobEndAsync();
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G1 X1", "G4 P1", "G1 X2", "M400" }));
    }

    /// <summary>
    /// Pause the job like LinkService.HandlePrintPaused does when RRF reports a pause (LinkService.cs:886-904).
    /// The fake firmware does not pause prints itself (FakeFirmware.ReadPrintPaused)
    /// </summary>
    private void Pause(long? filePosition, PrintPausedReason reason)
    {
        using (Jobs.Lock())
        {
            Jobs.Pause(filePosition, null, reason);
        }
    }

    private Task WaitForPausedAsync() => WaitForAsync(() => Jobs.IsPaused && !Jobs.IsProcessing);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task PauseAndResume(bool withPosition)
    {
        await StartAsync(bufferedPrintCodes: 1);
        using ManualResetEventSlim release = new();
        Host.Firmware.OnCode("G4", _ => release.IsSet ? FakeReply.Ok() : FakeReply.NotFinished);
        string content = "G1 X1\nG4 P1\nG1 X2\nG1 X3\n";
        WriteJob("job.g", content);

        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        Assert.That(WaitUntilExecuted("G4 P1"), Is.True);

        // RRF reports the position of the next code to execute. A pause requested by a code of the job comes without one,
        // then JobProcessor resumes after the last code it executed (LinkService.cs:901-903, JobProcessor.cs:652-657)
        long afterDwell = content.IndexOf("G1 X2", StringComparison.Ordinal);
        Pause(withPosition ? afterDwell : null, withPosition ? PrintPausedReason.User : PrintPausedReason.GCode);
        release.Set();
        await WaitForPausedAsync();

        Assert.That(await Host.ExecuteAsync("M27"), Is.EqualTo($"SD printing byte {afterDwell}/{content.Length}\n"));
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G1 X1", "G4 P1" }));

        // M24 resumes when RRF has processed it (MCodeHandler.cs:267-284 and 1403-1413)
        Assert.That(await Host.ExecuteAsync("M24"), Is.EqualTo("\n"));
        await WaitForJobEndAsync();
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G1 X1", "G4 P1", "G1 X2", "G1 X3", "M400" }));
        Assert.That(StopReasons(), Is.EqualTo(new[] { PrintStoppedReason.NormalCompletion }));
    }

    [Test]
    public async Task ResumeWhileDraining()
    {
        // A resume that arrives before the job task has reached the pause must not be lost (JobProcessor.cs:228-232, 659-664, 953-957)
        await StartAsync(bufferedPrintCodes: 1);
        using ManualResetEventSlim release = new();
        Host.Firmware.OnCode("G4", _ => release.IsSet ? FakeReply.Ok() : FakeReply.NotFinished);
        string content = "G1 X1\nG4 P1\nG1 X2\n";
        WriteJob("job.g", content);

        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        Assert.That(WaitUntilExecuted("G4 P1"), Is.True);

        Pause(content.IndexOf("G1 X2", StringComparison.Ordinal), PrintPausedReason.User);
        using (Jobs.Lock())
        {
            Jobs.Resume();
        }
        release.Set();

        await WaitForJobEndAsync();
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G1 X1", "G4 P1", "G1 X2", "M400" }));
    }

    [Test]
    public async Task CancelPausedJob()
    {
        await StartAsync(bufferedPrintCodes: 1);
        using ManualResetEventSlim release = new();
        Host.Firmware.OnCode("G4", _ => release.IsSet ? FakeReply.Ok() : FakeReply.NotFinished);
        WriteJob("job.g", "G1 X1\nG4 P1\nG1 X2\n");

        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        Assert.That(WaitUntilExecuted("G4 P1"), Is.True);
        Pause(null, PrintPausedReason.User);
        release.Set();
        await WaitForPausedAsync();

        // M0 cancels a paused print, which is then reported as cancelled and not aborted (MCodeHandler.cs:87-118, JobProcessor.cs:798-802, 970-985)
        Assert.That(await Host.ExecuteAsync("M0"), Is.EqualTo("\n"));
        await WaitForJobEndAsync();
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G1 X1", "G4 P1" }));
        Assert.That(StopReasons(), Is.Empty);
        Assert.That(await LastFileAsync(), Is.EqualTo((false, true, false)));
    }

    [Test]
    public async Task RewindIntoLoop()
    {
        // M26 inside a loop restores the loop of the read-ahead: the rewound lines run again with the iteration of that position,
        // and the loop goes on afterwards (CodeFile.Rewind, CodeFile.cs:176-234)
        await StartAsync(bufferedPrintCodes: 1);
        using ManualResetEventSlim release = new();
        Host.Firmware.OnCode("G4", _ => release.IsSet ? FakeReply.Ok() : FakeReply.NotFinished);
        string content = "G1 X0\nwhile iterations < 2\n  G1 Y{iterations}\n  G4 P1\nG1 Z0\n";
        WriteJob("job.g", content);

        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        Assert.That(WaitUntilExecuted("G4 P1"), Is.True);

        // Pause after the first dwell and resume at the first line of the loop body
        Pause(content.IndexOf("  G1 Y", StringComparison.Ordinal), PrintPausedReason.User);
        release.Set();
        await WaitForPausedAsync();
        Assert.That(await Host.ExecuteAsync("M24"), Is.EqualTo("\n"));
        await WaitForJobEndAsync();

        Assert.That(JobCodes(), Is.EqualTo(new[] { "G1 X0", "G1 Y{0}", "G4 P1", "G1 Y{0}", "G4 P1", "G1 Y{1}", "G4 P1", "G1 Z0", "M400" }));
    }

    [Test]
    public async Task SelectFileFromJob()
    {
        // A job may start another file with M32, which JobProcessor starts when the first one has ended (JobProcessor.cs:331-342, 879-893)
        await StartAsync();
        WriteJob("second.g", "G1 Y1\n");
        WriteJob("job.g", "G1 X1\nM32 \"second.g\"\nG1 X2\n");

        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        Assert.That(Host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.File).Any(code => code.Text == "G1 Y1"), FakeFirmwareHost.DefaultTimeout), Is.True);
        await WaitForJobEndAsync();

        string[] codes = JobCodes();
        Assert.That(codes.TakeWhile(code => code != "M32 \"second.g\""), Is.EqualTo(new[] { "G1 X1" }));
        Assert.That(codes.SkipWhile(code => code != "G1 Y1"), Is.EqualTo(new[] { "G1 Y1", "M400" }));
    }

    [Test]
    public async Task SelectFileFromJobStopsFirstJob()
    {
        // RRF chains to the new file (GCodes2.cpp:1106 "We now allow a file that is being printed to chain to another file"),
        // so the lines after M32 in the first file are not executed. JobProcessor.Cancel cancels the job's token (JobProcessor.cs:331-342),
        // which the codes read ahead keep since upstream d108e9767 (design/open-findings.md G9)
        await StartAsync();
        WriteJob("second.g", "G1 Y1\n");
        WriteJob("job.g", "G1 X1\nM32 \"second.g\"\nG1 X2\n");

        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        Assert.That(Host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.File).Any(code => code.Text == "G1 Y1"), FakeFirmwareHost.DefaultTimeout), Is.True);
        await WaitForJobEndAsync();
        Assert.That(JobCodes(), Does.Not.Contain("G1 X2"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Simulation(bool updateSimulatedTime)
    {
        await StartAsync();
        string content = "G1 X1\nM106 S1\nG1 X2\n" + new string(';', 64) + "\n";
        string path = WriteJob("job.g", content);

        // The simulated time is taken from job.lastDuration once the firmware reports it (JobProcessor.cs:829-858)
        Host.Firmware.ObjectModel.Set("job.lastDuration", 42);
        Assert.That(await Host.ExecuteAsync($"M37 P\"job.g\"{(updateSimulatedTime ? string.Empty : " F0")}"), Is.EqualTo("\n"));
        await WaitForJobEndAsync();

        // Most M-codes of a simulated file are not processed by DSF (MCodeHandler.cs:76-80), they still go to RRF
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G1 X1", "M106 S1", "G1 X2", "M400" }));
        Assert.That((await LastFileAsync()).simulated, Is.True);

        // M37 F0 does not write the simulated time back to the file (MCodeHandler.cs:518-519)
        string expected = updateSimulatedTime ? content + "\n; Simulated print time: 42\n" : content;
        Assert.That(await System.IO.File.ReadAllTextAsync(path), Is.EqualTo(expected));
    }

    [Test, Explicit("UpdateSimulatedTimeAsync seeks to the end only for files of at least 64 bytes (FileInfoParser.cs:948-964), so it overwrites the start of shorter files")]
    public async Task SimulationOfShortFile()
    {
        await StartAsync();
        const string content = "G1 X1\nG1 X2\n";
        string path = WriteJob("job.g", content);

        Host.Firmware.ObjectModel.Set("job.lastDuration", 42);
        Assert.That(await Host.ExecuteAsync("M37 P\"job.g\""), Is.EqualTo("\n"));
        await WaitForJobEndAsync();
        Assert.That(await System.IO.File.ReadAllTextAsync(path), Does.StartWith(content));
    }

    [Test]
    public async Task Diagnostics()
    {
        // M122 "DSF" includes the state of the job (JobProcessor.PrintDiagnosticsAsync)
        await StartAsync();
        using ManualResetEventSlim release = new();
        Host.Firmware.OnCode("G4", _ => release.IsSet ? FakeReply.Ok() : FakeReply.NotFinished);
        WriteJob("job.g", "G4 P1\n");

        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        Assert.That(WaitUntilExecuted("G4 P1"), Is.True);
        Assert.That(await Host.ExecuteAsync("M122 \"DSF\""), Does.Contain("File job.g is selected, processing"));

        release.Set();
        await WaitForJobEndAsync();
    }
}
