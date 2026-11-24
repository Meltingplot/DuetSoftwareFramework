using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Codes;

/// <summary>
/// Failures around a job that must neither stop the link nor end the job loop of <see cref="JobProcessor"/>: the variable requests of the rewind
/// after a pause that wait for RRF, are cancelled or are refused, and the file that a job selects for the next one when the link is reset
/// </summary>
[TestFixture]
public class JobProcessorFailures
{
    private FakeFirmwareHost? _host;

    private FakeFirmwareHost Host => _host!;

    /// <summary>
    /// Start the host. One buffered print code makes JobProcessor read a code only after the previous one finished,
    /// so that a held code stops the job at a known position
    /// </summary>
    private async Task StartAsync(int bufferedPrintCodes = 32) =>
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
        string path = System.IO.Path.Combine(Host.BaseDirectory, "gcodes", fileName);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private string[] JobCodes() => [.. Host.Firmware.GetExecutedCodes(CodeChannel.File).Select(code => code.Text)];

    private bool WaitUntilExecuted(string code) =>
        Host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.File).Any(item => item.Text == code), FakeFirmwareHost.DefaultTimeout);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    /// <summary>
    /// Wait for the job loop to finish the job and dispose its files. A job loop that has ended leaves the file selected for good
    /// </summary>
    private async Task WaitForJobEndAsync()
    {
        await WaitForAsync(() => !Jobs.IsFileSelected || Jobs.ExecuteTask is { IsCompleted: true });
        Assert.That(Jobs.ExecuteTask, Is.Not.Null);
        Assert.That(Jobs.ExecuteTask!.IsCompleted, Is.False, $"The job loop has ended ({Jobs.ExecuteTask.Status} {Jobs.ExecuteTask.Exception?.InnerException})");
    }

    private async Task<bool> LastFileAbortedAsync()
    {
        using (await Host.Model.AccessReadOnlyAsync())
        {
            return Host.Model.Job.LastFileAborted;
        }
    }

    /// <summary>
    /// Check that M32 does not wait for a job loop that has ended (JobProcessor.SelectFileAsync), where the machine is halted and RRF refuses the code
    /// </summary>
    private async Task M32RepliesAsync(string fileName)
    {
        WriteJob(fileName, "G1 Z1\n");
        Task<string> m32 = Host.StartCode($"M32 \"{fileName}\"");
        Assert.That(await Task.WhenAny(m32, Task.Delay(FakeFirmwareHost.DefaultTimeout)), Is.SameAs(m32), "M32 waits for the job loop");
        Assert.That(Jobs.ExecuteTask, Is.Not.Null);
        Assert.That(Jobs.ExecuteTask!.IsCompleted, Is.False, $"The job loop has ended ({Jobs.ExecuteTask.Status} {Jobs.ExecuteTask.Exception?.InnerException})");
    }

    /// <summary>
    /// Check that the job loop still starts jobs. A job loop that has ended lets M32 wait forever for the previous job (JobProcessor.SelectFileAsync)
    /// </summary>
    private async Task NextJobRunsAsync()
    {
        WriteJob("second.g", "G1 Y1\n");
        Assert.That(await Host.StartCode("M32 \"second.g\"").WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
        Assert.That(WaitUntilExecuted("G1 Y1"), Is.True, "The next job did not run");
        await WaitForJobEndAsync();
    }

    private async Task<string[]> ErrorMessagesAsync()
    {
        using (await Host.Model.AccessReadOnlyAsync())
        {
            return [.. Host.Model.Messages.Where(message => message.Type == MessageType.Error).Select(message => message.Content)];
        }
    }

    /// <summary>
    /// Pause the job like LinkService.HandlePrintPaused does when RRF reports a pause (LinkService.cs:1037-1043).
    /// The fake firmware does not pause prints itself (FakeFirmware.ReadPrintPaused)
    /// </summary>
    private void Pause(long filePosition)
    {
        using (Jobs.Lock())
        {
            Jobs.Pause(filePosition, null, PrintPausedReason.User);
        }
    }

    /// <summary>
    /// Hold the link before its next transfer
    /// </summary>
    private void HoldLink()
    {
        Host.Firmware.HoldTransfers();
        Assert.That(Host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, FakeFirmwareHost.DefaultTimeout), Is.True, "The link did not stop");
    }

    /// <summary>
    /// Let the link perform one transfer and process it, then hold it again
    /// </summary>
    private void Step()
    {
        int transfer = Host.Firmware.TransferCount;
        Host.Firmware.HoldTransfers(1);
        Assert.That(Host.Firmware.WaitUntil(firmware => firmware.TransferCount > transfer && firmware.IsTransferHeld, FakeFirmwareHost.DefaultTimeout), Is.True, "No transfer");
    }

    /// <summary>
    /// Job whose read-ahead leaves a block with a local variable before the job stops at the held dwell, so that the block is closed and its
    /// variable lives on under a hidden name (CodeFile.cs, EndCodeBlockAsync and HideLocalVariables)
    /// </summary>
    private const string BlockJob = "if true\n  var a = 1\n  echo var.a\nG4 P2\nG1 Z0\n";

    /// <summary>
    /// Name under which RRF keeps the variable of the closed block. The var line opens a block of its own (CodeFile.cs, ExpectLocalVariable),
    /// which is named after its file position (CodeFile.cs, GetHiddenVariableName)
    /// </summary>
    private static readonly string HiddenVariable = $"dsfClosedBlock{BlockJob.IndexOf("  var a", StringComparison.Ordinal)}x0";

    /// <summary>
    /// Position inside the closed block of <see cref="BlockJob"/>
    /// </summary>
    private static readonly long InsideBlock = BlockJob.IndexOf("  echo var.a", StringComparison.Ordinal);

    /// <summary>
    /// Run <see cref="BlockJob"/> up to its held dwell and pause it, by default inside the closed block as RRF reports the position of a move it still
    /// had queued. Then the rewind gives the variable of the block its name back with a SetVariable request (CodeFile.cs, RestoreLocalVariables)
    /// </summary>
    /// <param name="release">Event that releases the held dwell</param>
    /// <param name="beforePause">Action to perform when the job is held, before the pause</param>
    /// <param name="pausePosition">File position of the pause, or null for <see cref="InsideBlock"/></param>
    private async Task RunBlockJobAndPauseAsync(ManualResetEventSlim release, Action? beforePause = null, long? pausePosition = null)
    {
        Host.Firmware.OnCode("G4", code => (code.Text == "G4 P2" && !release.IsSet) ? FakeReply.NotFinished : null);
        WriteJob("job.g", BlockJob);

        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        Assert.That(Host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.File)?.Text == "G4 P2", FakeFirmwareHost.DefaultTimeout), Is.True);
        Assert.That(Host.Firmware.TryGetLocalVariable(CodeChannel.File, "a", out _), Is.False, "The block was not left");
        Assert.That(Host.Firmware.TryGetLocalVariable(CodeChannel.File, HiddenVariable, out object? hidden) ? hidden : null, Is.EqualTo(1), "The variable of the block was not hidden");

        beforePause?.Invoke();
        Pause(pausePosition ?? InsideBlock);
        release.Set();
    }

    /// <summary>
    /// Whether a request gives the variable of the closed block its name back (CodeFile.cs, RestoreLocalVariables)
    /// </summary>
    private static bool IsRestoreRequest(FakeSbcRequest request) =>
        request.Request == SbcRequest.SetVariable && request.Channel == CodeChannel.File && request.Flag && request.Text == "var.a" && request.Argument == "var." + HiddenVariable;

    /// <summary>
    /// Step the held link until DSF has written the SetVariable request of the rewind and RRF has yet to answer it
    /// </summary>
    private void StepUntilRestoreIsWritten()
    {
        for (int i = 0; i < 200 && !Host.Firmware.GetPendingRequests().Any(IsRestoreRequest); i++)
        {
            Step();
        }
        Assert.That(Host.Firmware.GetPendingRequests().Any(IsRestoreRequest), Is.True, "DSF did not write the variable request of the rewind");
    }

    [Test]
    public async Task RewindDoesNotHoldTheJobLockWhileItsVariableRequestsAreAnswered()
    {
        // The link task takes the job lock in its packet handling (Processor.FilesAborted, Processor.cs:904 and :943; LinkService.HandlePrintPaused,
        // LinkService.cs:1038) before it writes the variable requests and reads their answers (LinkService.cs:436-447, :585-612). A rewind that waits
        // for its variable requests while it holds the job lock stops the link for good when RRF reports an abort or a pause meanwhile. Here the link
        // is held while RRF has yet to answer the request, and the test takes the job lock the way the link task does
        await StartAsync(bufferedPrintCodes: 1);
        using ManualResetEventSlim release = new();
        await RunBlockJobAndPauseAsync(release, HoldLink);
        StepUntilRestoreIsWritten();

        Task jobLock = Task.Run(() =>
        {
            using (Jobs.Lock())
            {
                // The link task would abort or pause the job here
            }
        });
        Assert.That(await Task.WhenAny(jobLock, Task.Delay(FakeFirmwareHost.DefaultTimeout)), Is.SameAs(jobLock), "The rewind holds the job lock while it waits for RRF");

        // Resume before RRF has answered. The codes read after the rewind must not reach RRF before the variable they use exists again: DSF writes
        // the codes and evaluations of a transfer before its variable requests (LinkService.cs:487 and :585), so the job reads on only when they are done
        using (Jobs.Lock())
        {
            Jobs.Resume();
        }
        Host.Firmware.ReleaseTransfers();
        await WaitForJobEndAsync();

        FakeSbcRequest[] requests = [.. Host.Firmware.GetReceivedRequests()];
        int restore = Array.FindIndex(requests, IsRestoreRequest);
        int echoAfterRestore = Array.FindLastIndex(requests, request => request.Request == SbcRequest.EvaluateExpression && request.Channel == CodeChannel.File && request.Text.Contains("var.a"));
        Assert.That(echoAfterRestore, Is.GreaterThan(restore), "echo var.a went to RRF before var.a existed again");
        Assert.That(await ErrorMessagesAsync(), Is.Empty);
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G4 P2", "G4 P2", "G1 Z0", "M400" }));
    }

    [Test]
    public async Task M26DoesNotHoldTheJobLockWhileItsVariableRequestsAreAnswered()
    {
        // M26 S rewinds the paused job under the job lock as well (MCodeHandler.cs, case 26). The pause is after the block here, so the pause itself
        // restores nothing and M26 into the block does
        await StartAsync(bufferedPrintCodes: 1);
        using ManualResetEventSlim release = new();
        await RunBlockJobAndPauseAsync(release, pausePosition: BlockJob.IndexOf("G1 Z0", StringComparison.Ordinal));
        await WaitForAsync(() => Jobs.IsPaused && !Jobs.IsProcessing);
        Assert.That(Host.Firmware.GetReceivedRequests().Any(IsRestoreRequest), Is.False);

        HoldLink();
        Task<string> m26 = Host.StartCode($"M26 S{InsideBlock}");
        StepUntilRestoreIsWritten();

        Task jobLock = Task.Run(() =>
        {
            using (Jobs.Lock())
            {
                // The link task would abort or pause the job here
            }
        });
        Assert.That(await Task.WhenAny(jobLock, Task.Delay(FakeFirmwareHost.DefaultTimeout)), Is.SameAs(jobLock), "M26 holds the job lock while it waits for RRF");

        Host.Firmware.ReleaseTransfers();
        Assert.That(await m26.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
        Assert.That(await Host.ExecuteAsync("M24"), Is.EqualTo("\n"));
        await WaitForJobEndAsync();
        Assert.That(await ErrorMessagesAsync(), Is.Empty);
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G4 P2", "G4 P2", "G1 Z0", "M400" }));
    }

    [Test]
    public async Task RewindWhoseVariableRequestsAreCancelledEndsTheJobOnly()
    {
        // A reset of the link cancels the variable requests before it aborts the job (LinkService.InvalidateCodes, LinkInterface.InvalidateCodes).
        // The cancellation must end the job only, not the job loop: the loop ends only on its OperationCanceledException catch of the shutdown
        await StartAsync(bufferedPrintCodes: 1);
        using ManualResetEventSlim release = new();
        await RunBlockJobAndPauseAsync(release, HoldLink);
        StepUntilRestoreIsWritten();

        Host.Firmware.SimulateReset();
        Host.Firmware.ReleaseTransfers();
        await WaitForJobEndAsync();
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G4 P2" }));
        Assert.That(await LastFileAbortedAsync(), Is.True);


        // M32 selects its file before it goes to RRF (MCodeHandler.cs, case 32), so it would wait for the job loop. The fake firmware reads the
        // requests that DSF wrote before the reset after it and then ends the new connection as a lost one with an emergency stop (FakeFirmware,
        // ExchangeData and ReadFirstMarker), so RRF refuses the code here and only the reply of M32 is checked
        await M32RepliesAsync("second.g");
    }

    [Test]
    public async Task RewindWhoseVariableRequestIsRefusedAbortsTheJob()
    {
        // RRF refuses to create a variable that exists already (SbcInterface.cpp:1003-1008 at RRF cde4e4fa1) and DSF passes the error on to the
        // request (LinkService.HandleVariableResult). The local variables are then out of step with the file, so the job ends with an error as it
        // does on a read error, and the next job runs. Here the variable of the block exists again in RRF before the rewind restores it
        await StartAsync(bufferedPrintCodes: 1);
        using ManualResetEventSlim release = new();
        await RunBlockJobAndPauseAsync(release, () => Host.Firmware.SetLocalVariable(CodeChannel.File, "a", 5));
        await WaitForAsync(() => Jobs.IsPaused && !Jobs.IsProcessing);
        Assert.That(Host.Firmware.WaitUntil(firmware => firmware.GetReceivedRequests().Any(IsRestoreRequest), FakeFirmwareHost.DefaultTimeout), Is.True);

        Assert.That(await Host.ExecuteAsync("M24"), Is.EqualTo("\n"));
        await WaitForJobEndAsync();
        Assert.That(await ErrorMessagesAsync(), Is.EqualTo(new[] { "in file job.g: variable 'var.a' already exists" }));
        Assert.That(await LastFileAbortedAsync(), Is.True);
        Assert.That(JobCodes(), Is.EqualTo(new[] { "G4 P2" }), "The job read on after its rewind failed");

        await NextJobRunsAsync();
    }

    /// <summary>
    /// Job that selects the next file with M32, as RRF chains to it (GCodes2.cpp:1106 "We now allow a file that is being printed to chain to another file").
    /// JobProcessor starts that file once the job has ended (JobProcessor.SelectFileAsync with fromJobFile)
    /// </summary>
    private const string ChainingJob = "G1 X1\nM32 \"second.g\"\n";

    /// <summary>
    /// Run <see cref="ChainingJob"/> up to its M32, which the fake firmware holds, then hold the link and let it carry the reply of the M32 only.
    /// The job then ends without the link until it asks the link to tell RRF that it has stopped
    /// </summary>
    private async Task RunChainingJobUntilItStopsAsync()
    {
        using ManualResetEventSlim release = new();
        Host.Firmware.OnCode("M32", code => (code.Text == "M32 \"second.g\"" && !release.IsSet) ? FakeReply.NotFinished : null);
        WriteJob("second.g", "G1 Y1\n");
        WriteJob("job.g", ChainingJob);

        Assert.That(await Host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        Assert.That(Host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.File)?.Text == "M32 \"second.g\"", FakeFirmwareHost.DefaultTimeout), Is.True);

        HoldLink();
        release.Set();
        for (int i = 0; i < 20 && !Host.Firmware.GetSentPackets().Any(packet => packet.RelatedCode?.Text == "M32 \"second.g\""); i++)
        {
            Step();
        }
        Assert.That(Host.Firmware.GetSentPackets().Any(packet => packet.RelatedCode?.Text == "M32 \"second.g\""), Is.True, "RRF did not reply to the M32");
        await WaitForAsync(() => IsPending(link => link.StopPrintRequest));
    }

    /// <summary>
    /// Whether a request of the print state is waiting for the link to write it (LinkService.cs, print status in Execute)
    /// </summary>
    private bool IsPending(Func<LinkInterface, TaskCompletionSource?> request)
    {
        LinkInterface link = Host.Services.GetRequiredService<LinkInterface>();
        using (link.PrintStateLock.Lock())
        {
            return request(link) is not null;
        }
    }

    [Test]
    public async Task ResetBeforeRrfGetsTheChainedFileDeselectsIt()
    {
        // After the job, JobProcessor selects the file that the job asked for and waits for the link to tell RRF about it (SetPrintFileInfo). A reset
        // of the link cancels that (LinkInterface.InvalidateCodes) and aborts the job (LinkService.InvalidateCodes), and the cancellation must not end
        // the job loop, else no job can start until DCS restarts. The link is held here while the file waits for the link
        await StartAsync();
        await RunChainingJobUntilItStopsAsync();
        for (int i = 0; i < 20 && !Host.Firmware.GetPendingRequests().Any(request => request.Request == SbcRequest.PrintStopped); i++)
        {
            Step();
        }
        await WaitForAsync(() => IsPending(link => link.SetPrintInfoRequest));

        Host.Firmware.SimulateReset();
        Host.Firmware.ReleaseTransfers();
        await WaitForJobEndAsync();
        Assert.That(Host.Firmware.GetReceivedRequests().Count(request => request.Request == SbcRequest.SetPrintFileInfo), Is.EqualTo(1), "RRF got the chained file");
        Assert.That(JobCodes(), Does.Not.Contain("G1 Y1"), "The file that RRF did not get was printed");

        // The machine is halted here as in the reset test above, so only the reply of M32 is checked
        Assert.That(Host.Firmware.IsEmergencyStopped, Is.True);
        await M32RepliesAsync("third.g");
    }

    [Test]
    public async Task ResetBeforeRrfGetsTheEndOfAChainingJobDropsTheChainedFile()
    {
        // A reset of the link while the job that selected the next file waits for the link to tell RRF that it has stopped cancels that
        // (LinkInterface.InvalidateCodes) and aborts the job (LinkService.InvalidateCodes), which drops the next file (JobProcessor.Abort). The
        // M32 of the job has asked the loop to start the next file without waiting (JobProcessor.Resume), and that request must go with the file,
        // else the next pass of the loop finds no file at all. The test holds the object model until the link has aborted the job, because the
        // loop writes the end of the job to the object model before it takes the job lock again, and the abort of the link usually comes first
        await StartAsync();
        await RunChainingJobUntilItStopsAsync();

        using (await Host.Model.AccessReadOnlyAsync())
        {
            Host.Firmware.SimulateReset();
            Host.Firmware.ReleaseTransfers();
            await WaitForAsync(() => Jobs.IsAborted);
        }
        await WaitForJobEndAsync();
        Assert.That(Host.Firmware.GetReceivedRequests().Count(request => request.Request == SbcRequest.SetPrintFileInfo), Is.EqualTo(1), "RRF got the chained file");
        Assert.That(JobCodes(), Does.Not.Contain("G1 Y1"), "The chained file of the aborted job was printed");
        Assert.That(await LastFileAbortedAsync(), Is.True);

        // Whether the machine is halted here depends on whether DSF wrote anything before the reset (see the reset tests above), so only the reply
        // of M32 is checked
        await M32RepliesAsync("third.g");
    }
}
