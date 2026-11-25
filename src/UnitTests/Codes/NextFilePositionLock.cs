using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.Codes;
using DuetControlServer.Files;
using DuetControlServer.Link.Channel;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using Code = DuetControlServer.Commands.Code;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;

namespace UnitTests.Codes;

/// <summary>
/// Tests for the next file position of a code file (TP3 of the DSF error class census). The link task updates it when RRF starts a macro or a
/// message box for a buffered code (Processor.cs:1571, :1722). The reader of a job holds the lock of the file across the read from the file
/// (CodeFile.cs:397-418), so the update must not wait for that lock: a storage device that stalls for 4 s or more would stop all transfers,
/// RRF would report the SBC link as lost and the fork's RRF would perform an emergency stop
/// </summary>
[TestFixture]
public class NextFilePositionLock
{
    /// <summary>
    /// Time within which an update that does not wait for the file lock returns, far below the 4 s SBC timeout of RRF
    /// </summary>
    private static readonly TimeSpan PromptReturn = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Time the fake firmware gets to run a code while the job file is locked
    /// </summary>
    private static readonly TimeSpan WhileLocked = TimeSpan.FromSeconds(3);

    private const string JobContent = "G1 X1\nG1 X2\nG1 X3\n";

    private ServiceProvider? _provider;
    private string? _jobFile;
    private FakeFirmwareHost? _host;

    [TearDown]
    public async Task TearDown()
    {
        _provider?.Dispose();
        _provider = null;
        if (_jobFile is not null)
        {
            System.IO.File.Delete(_jobFile);
            _jobFile = null;
        }
        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }
    }

    /// <summary>
    /// Create the services without a link and a job file to read from
    /// </summary>
    private CodeFile CreateJobFile()
    {
        _provider = DcsServices.Build();
        _jobFile = Path.GetTempFileName();
        System.IO.File.WriteAllText(_jobFile, JobContent);
        return _provider.GetRequiredService<FileFactory>().Create("0:/gcodes/job.g", _jobFile, CodeChannel.File);
    }

    /// <summary>
    /// Make a code that the given file has read
    /// </summary>
    private Code MakeCode(CodeFile file, long filePosition, int length)
    {
        Code code = _provider!.GetRequiredService<CodeFactory>().Create();
        code.Channel = CodeChannel.File;
        code.Type = CodeType.GCode;
        code.MajorNumber = 1;
        code.File = file;
        code.FilePosition = filePosition;
        code.Length = length;
        return code;
    }

    /// <summary>
    /// Run an action on a dedicated thread like the link task and report whether it returned while the file lock is held
    /// </summary>
    private static async Task<bool> ReturnsWhileLockedAsync(CodeFile file, Action action)
    {
        // CodeFile.ReadCodeAsync holds this lock while ParseAsync reads from the file stream, a stalled read keeps it
        IDisposable readLock = await file.LockAsync(CancellationToken.None);
        Task task = Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        bool returned;
        try
        {
            returned = task.Wait(PromptReturn);
        }
        finally
        {
            readLock.Dispose();
        }

        // Without the fix the action goes on once the read has finished
        await task.WaitAsync(FakeFirmwareHost.DefaultTimeout);
        return returned;
    }

    [Test]
    public async Task UpdateDoesNotWaitForAFileRead()
    {
        using CodeFile file = CreateJobFile();
        Code code = MakeCode(file, 6, 6);

        Assert.That(await ReturnsWhileLockedAsync(file, code.UpdateNextFilePosition), Is.True,
            "Code.UpdateNextFilePosition waited for the lock that the reader holds across the read from the file");
        Assert.That(file.NextFilePosition, Is.EqualTo(12));
    }

    [Test]
    public async Task MessageBoxDoesNotWaitForAFileRead()
    {
        // RRF asks DSF to wait for the acknowledgement of an M291 in the job (LinkService.cs:1198-1210), the link task handles it under the channel lock
        using CodeFile file = CreateJobFile();
        Processor processor = ActivatorUtilities.CreateInstance<Processor>(_provider!, CodeChannel.File);
        Code m291 = MakeCode(file, 6, 6);
        m291.Type = CodeType.MCode;
        m291.MajorNumber = 291;
        processor.BufferedCodes.Add(m291);

        Assert.That(await ReturnsWhileLockedAsync(file, processor.WaitForAcknowledgement), Is.True,
            "Processor.WaitForAcknowledgement waited for the lock that the reader holds across the read from the file");
        Assert.That(processor.CurrentState.WaitingForAcknowledgement, Is.True);
        Assert.That(processor.CurrentState.StartCode, Is.SameAs(m291));
        Assert.That(file.NextFilePosition, Is.EqualTo(12), "A fork while the message box is open must not run the M291 again");
    }

    [Test]
    public void PositionSemanticsAreKept()
    {
        // The next file position only advances with codes (upstream 68182ff59), a seek sets it (CodeFile.Position) and a fork starts there (copy constructor)
        using CodeFile file = CreateJobFile();
        MakeCode(file, 6, 6).UpdateNextFilePosition();
        Assert.That(file.NextFilePosition, Is.EqualTo(12));

        MakeCode(file, 0, 6).UpdateNextFilePosition();
        Assert.That(file.NextFilePosition, Is.EqualTo(12), "An earlier code that finishes late must not move the position back");

        using (CodeFile copy = _provider!.GetRequiredService<FileFactory>().Create(file, CodeChannel.File2))
        {
            Assert.That(copy.Position, Is.EqualTo(12), "The fork does not start after the last processed code");
            Assert.That(copy.NextFilePosition, Is.EqualTo(12));
        }

        file.Position = 6;
        Assert.That(file.NextFilePosition, Is.EqualTo(6), "A seek, e.g. a rewind on resume, did not set the next file position");

        // A code without a file position (e.g. one from a code stream) leaves it alone
        Code noPosition = MakeCode(file, 0, 6);
        noPosition.FilePosition = null;
        noPosition.UpdateNextFilePosition();
        Assert.That(file.NextFilePosition, Is.EqualTo(6));
    }

    /// <summary>
    /// Start a job on the fake firmware that waits in its first code (M400) until the returned event is set, and lock the job file as the reader
    /// does across a stalled read once the firmware has the codes after it
    /// </summary>
    /// <param name="job">Job content, starting with M400</param>
    /// <param name="bufferedCode">Code after M400 that must have reached the firmware before the file is locked</param>
    /// <returns>Event to let M400 finish and the held lock of the job file</returns>
    private async Task<(ManualResetEventSlim release, IDisposable readLock)> StartJobAndLockItsFileAsync(string job, string bufferedCode)
    {
        _host = await FakeFirmwareHost.StartAsync();
        ManualResetEventSlim release = new();
        _host.Firmware.OnCode("M400", _ => release.IsSet ? FakeReply.Ok() : FakeReply.NotFinished);
        System.IO.File.WriteAllText(Path.Combine(_host.BaseDirectory, "gcodes", "job.g"), job);
        Assert.That(await _host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetReceivedCodes(CodeChannel.File).Any(code => code.Text == bufferedCode), FakeFirmwareHost.DefaultTimeout),
            Is.True, $"{bufferedCode} was not sent");

        JobProcessor jobs = _host.Services.GetRequiredService<JobProcessor>();
        CodeFile file = (CodeFile)typeof(JobProcessor).GetField("_file", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(jobs)!;
        IDisposable readLock = await file.LockAsync(CancellationToken.None);
        return (release, readLock);
    }

    /// <summary>
    /// Wait for the job loop to finish and dispose the job files
    /// </summary>
    private async Task WaitForJobEndAsync()
    {
        JobProcessor jobs = _host!.Services.GetRequiredService<JobProcessor>();
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        while (jobs.IsFileSelected)
        {
            await Task.Delay(10, cts.Token);
        }
    }

    [Test]
    public async Task MacroOfAJobCodeStartsWhileTheJobFileIsRead()
    {
        // A T-code, G28 or M600 in a job makes RRF ask for a macro with fromCode (FakeReply.CallMacro). The link task starts it for the first
        // buffered code of the job (Processor.DoMacroFile) while the reader of the job may be stuck in a read from a stalled storage device
        (ManualResetEventSlim release, IDisposable readLock) = await StartJobAndLockItsFileAsync("M400\nM42 P1\nG4 P2\n", "M42 P1");
        using (release)
        {
            _host!.WriteMacro("tp3.g", "G4 P7\n");
            _host.Firmware.OnCode("M42", _ => FakeReply.CallMacro("tp3.g"));
            bool macroRan;
            try
            {
                release.Set();
                macroRan = _host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.File).Any(code => code.Text == "G4 P7"), WhileLocked);
            }
            finally
            {
                readLock.Dispose();
            }
            Assert.That(macroRan, Is.True, "The link stopped until the read from the job file finished");
        }

        await WaitForJobEndAsync();
        Assert.That(_host.Firmware.GetExecutedCodes(CodeChannel.File).Select(code => code.Text), Is.EqualTo(new[] { "M400", "M42 P1", "G4 P7", "G4 P2", "M400" }));
    }

    [Test]
    public async Task MessageBoxOfAJobIsAcknowledgedWhileTheJobFileIsRead()
    {
        // M291 S2 in a job makes RRF ask DSF to wait for the acknowledgement (Processor.WaitForAcknowledgement). M292 from another channel can only
        // reach RRF if the link keeps running
        (ManualResetEventSlim release, IDisposable readLock) = await StartJobAndLockItsFileAsync("M400\nM291 P\"x\" S2\nG4 P2\n", "M291 P\"x\" S2");
        using (release)
        {
            bool acknowledged;
            try
            {
                release.Set();
                Assert.That(_host!.Firmware.WaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.Request == FirmwareRequest.WaitForAcknowledgement), FakeFirmwareHost.DefaultTimeout),
                    Is.True, "The message box did not open");
                acknowledged = await _host.StartCode("M292").WaitAsync(WhileLocked).ContinueWith(task => task.IsCompletedSuccessfully);
            }
            finally
            {
                readLock.Dispose();
            }
            Assert.That(acknowledged, Is.True, "The link stopped until the read from the job file finished");
        }

        await WaitForJobEndAsync();
        Assert.That(_host.Firmware.GetExecutedCodes(CodeChannel.File).Select(code => code.Text), Is.EqualTo(new[] { "M400", "M291 P\"x\" S2", "G4 P2", "M400" }));
    }
}
