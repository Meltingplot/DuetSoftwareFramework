using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Utility;

/// <summary>
/// The event log (M929) on a disk that becomes full while logging is on. A failing write must stop logging and must not throw to the code that logs,
/// which is the link thread for messages of RRF (LinkService.HandleMessage), the code pipeline for code replies (Executed.cs:115-128) and the job.
/// RRF closes its log file without writing to it again when a write fails (Logger::InternalLogMessage, Logger.cpp:182-186), and state.logFile is null
/// then (Logger::GetFileName). Before, the catch of EventLogger.Log wrote "Event logging stopped" to the same file, that write threw out of the catch,
/// the writer stayed open, and the next logged message threw again; on the link thread that ended the transfer thread and with it DCS
/// </summary>
/// <remarks>
/// The disk becomes full only for the open log file: its descriptor is replaced by one of /dev/full, on which every write fails with ENOSPC
/// </remarks>
[TestFixture]
[Platform("Linux")]
public class EventLogDiskFull
{
    private const int O_WRONLY = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldFd, int newFd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    private FakeFirmwareHost _host = null!;

    private string LogFile => Path.Combine(_host.BaseDirectory, "sys", "eventlog.txt");

    [SetUp]
    public async Task SetUp()
    {
        _host = await FakeFirmwareHost.StartAsync(firmware => firmware.OnCode("M115", _ => FakeReply.Ok("FIRMWARE_NAME: fake")));
        Assert.That(await _host.ExecuteAsync("M929 P\"eventlog.txt\" S3"), Is.EqualTo("\n"));
    }

    [TearDown]
    public async Task TearDown()
    {
        try
        {
            await _host.ExecuteAsync("M929 S0");
        }
        finally
        {
            await _host.DisposeAsync();
        }
    }

    /// <summary>
    /// Let every further write to the open log file fail as on a full disk
    /// </summary>
    private void FillDisk()
    {
        int full = open("/dev/full", O_WRONLY);
        Assert.That(full, Is.GreaterThanOrEqualTo(0), "Cannot open /dev/full");
        int replaced = 0;
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
            {
                string? target;
                try
                {
                    target = new FileInfo(entry).LinkTarget;
                }
                catch (IOException)
                {
                    continue;   // closed while enumerating
                }
                if (target == LogFile && int.TryParse(Path.GetFileName(entry), out int fd))
                {
                    Assert.That(dup2(full, fd), Is.EqualTo(fd), "dup2 failed");
                    replaced++;
                }
            }
        }
        finally
        {
            close(full);
        }
        Assert.That(replaced, Is.EqualTo(1), "The log file is not open once");
    }

    private Task TransferThread => _host.Services.GetServices<IHostedService>().OfType<LinkService>().Single().ExecuteTask!;

    private (string? File, EventLogLevel Level) LogState()
    {
        using (_host.Model.AccessReadOnly())
        {
            return (_host.Model.State.LogFile, _host.Model.State.LogLevel);
        }
    }

    /// <summary>
    /// After a write has failed, logging is reported as stopped and DCS goes on: codes reach RRF and come back, the object model is updated, the transfer
    /// thread runs and the application is not stopping. Logging can be started again once the disk has space
    /// </summary>
    private async Task AssertLoggingStoppedAndDcsRuns()
    {
        Assert.That(TransferThread.IsCompleted, Is.False, () => $"The transfer thread must still run, it ended with {TransferThread.Exception?.InnerException}");
        Assert.That(LogState(), Is.EqualTo(((string?)null, EventLogLevel.Off)), "Logging must be reported as stopped");
        Assert.That(await _host.ExecuteAsync("M929"), Is.EqualTo("Event logging is disabled\n"));

        Assert.That(await _host.ExecuteAsync("M115"), Is.EqualTo("FIRMWARE_NAME: fake\n"));
        _host.Firmware.SendMessage(MessageTypeFlags.LoggedGenericMessage, "Finished printing file\n");
        using (CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout))
        {
            await _host.Model.WaitForFullUpdateAsync(cts.Token);
        }
        Assert.That(TransferThread.IsCompleted, Is.False, "The transfer thread must still run");
        Assert.That(_host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested, Is.False, "DCS must not stop");

        Assert.That(await _host.ExecuteAsync("M929 P\"eventlog.txt\" S3"), Is.EqualTo("\n"));
        Assert.That(LogState(), Is.EqualTo(("eventlog.txt", EventLogLevel.Debug)));
        Assert.That(System.IO.File.ReadAllLines(LogFile).Count(line => line.EndsWith("Event logging started")), Is.EqualTo(2));
    }

    /// <summary>
    /// A message of RRF that is logged on the transfer thread (LinkService.HandleMessage, EventLogger.Log), e.g. "Resume state saved" while printing
    /// </summary>
    [Test]
    public async Task MessageOfRrf()
    {
        FillDisk();
        _host.Firmware.SendMessage(MessageTypeFlags.LoggedGenericMessage, "Resume state saved\n");

        // Logging stops when DSF has processed the message
        using (CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout))
        {
            while (LogState().File is not null && !TransferThread.IsCompleted)
            {
                await Task.Delay(10, cts.Token);
            }
        }
        await AssertLoggingStoppedAndDcsRuns();
    }

    /// <summary>
    /// The reply of a code that DSF handles, logged by the code pipeline (Executed.cs:115-119, EventLogger.LogAsync). The code gets its reply
    /// </summary>
    [Test]
    public async Task ReplyOfCode()
    {
        FillDisk();
        Assert.That(await _host.ExecuteAsync("M929"), Is.EqualTo("Event logging is enabled at log level debug\n"));
        await AssertLoggingStoppedAndDcsRuns();
    }
}
