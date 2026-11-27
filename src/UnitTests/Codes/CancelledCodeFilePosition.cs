using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using DuetControlServer.Codes.Handlers;
using DuetControlServer.Files;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using UnitTests.IPC;
using Code = DuetControlServer.Commands.Code;

namespace UnitTests.Codes;

/// <summary>
/// The file position of a code whose token is cancelled when it reaches the Executed stage, the case of upstream 4f333e8cc ("Fixed M99 in a nested macro
/// logging a TaskCanceledException").
/// <list type="bullet">
/// <item>RRF closes the active macro file on M99 and goes up one stack level (wiki Gcodes.md "M99: Return from Macro/Subprogram"), in SBC mode by
/// AbortFile(false) (GCodes.cpp:3864-3869 of the fork's RRF 3.7-dev 323b5b185, emulated by FakeGCodes.cs:1339-1348)</item>
/// <item>DSF completes the M99 with an empty result (Processor.cs:827-834, CodeProcessor.cs:339) and then aborts the macro under its lock
/// (Processor.cs:850-873), which cancels the token that all codes of the macro carry (MacroFile.cs:234-241, 346). The Executed stage runs on its own task
/// (PipelineStackItem.cs:58-97), so the M99 gets there with that token cancelled</item>
/// <item>Before 4f333e8cc the stage updated the file position of every code with a result under the file lock with the token of the code
/// (Executed.cs:81 and Code.cs:208-221 of 4f333e8cc~1). The AsyncLock takes a free lock even with a cancelled token, but gives up on a held one
/// (DuetSharedLibrary/AsyncLock.cs:63-70, <see cref="Utility.AsyncLockCancelledToken"/>), so the M99 failed with a TaskCanceledException whenever the file lock
/// was held at that moment, e.g. by the link task while it aborts the macro (Processor.cs:850-873), by the reader while it reads a code (CodeFile.cs:397-418)
/// or by the reader while it aborts the macro after a cancelled code (MacroFile.cs:462-465). The update was outside the try block of the stage, so the loop of
/// the stage logged "Failed to process code in stage Executed" and completed the M99 with the exception (PipelineStackItem.cs:85-89)</item>
/// <item>The fork had replaced that update with one that takes neither the file lock nor a token (Code.cs:191-197, CodeFile.cs:159-168, TP3 of #228), which
/// could not throw. The merge of 4f333e8cc keeps it behind upstream's guard (Executed.cs:80-84), so a code whose token is cancelled no longer advances the
/// next file position, as in upstream</item>
/// </list>
/// </summary>
[TestFixture]
public class CancelledCodeFilePosition
{
    private static readonly TimeSpan Timeout = FakeFirmwareHost.DefaultTimeout;

    private const string InnerMacro = "M42 P0 S1\nM99\nG4 P22\n";

    // "M99\n" starts after "M42 P0 S1\n"
    private const long M99Position = 10;
    private const int M99Length = 4;

    private ServiceProvider? _provider;
    private string? _macroFile;
    private FakeFirmwareHost? _host;

    [SetUp]
    public void SetUp()
    {
        // NUnit runs all tests of a fixture on one instance
        _m99 = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _heldM42 = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [TearDown]
    public async Task TearDown()
    {
        _release.TrySetResult();
        _provider?.Dispose();
        _provider = null;
        if (_macroFile is not null)
        {
            System.IO.File.Delete(_macroFile);
            _macroFile = null;
        }
        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }
    }

    /// <summary>
    /// Wait for a task and return how it failed, or null if it completed
    /// </summary>
    private static async Task<Exception?> FailureOfAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Timeout);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>
    /// Log entries of a cancellation, which is what 4f333e8cc fixed, or of any failure of the Executed stage: the loop of the stage (PipelineStackItem.cs:87)
    /// and the catch block of the stage (Executed.cs:242-248)
    /// </summary>
    private static List<IpcLog.Entry> CancellationsAndStageFailures(IpcLog log) => [.. log.Entries.Where(entry =>
        entry.Exception is OperationCanceledException ||
        entry.Message.StartsWith("Failed to process code in stage") ||
        entry.Message.StartsWith("Executed interceptor threw an exception"))];

    #region Executed stage

    /// <summary>
    /// Create the services without a link and a code file that stands in for the nested macro
    /// </summary>
    private CodeFile CreateMacroFile()
    {
        _provider = DcsServices.Build();
        _macroFile = Path.GetTempFileName();
        System.IO.File.WriteAllText(_macroFile, InnerMacro);
        return _provider.GetRequiredService<FileFactory>().Create("0:/sys/inner.g", _macroFile, CodeChannel.HTTP);
    }

    /// <summary>
    /// Make the M99 of the macro as FilesAborted completes it (Processor.cs:827-834): with the flags of a macro code that has been post-processed
    /// (MacroFile.cs:347-350, Post.cs:29) and an empty result
    /// </summary>
    private Code MakeM99(CodeFile file, CancellationToken cancellationToken)
    {
        Code code = _provider!.GetRequiredService<CodeFactory>().Create();
        code.Channel = CodeChannel.HTTP;
        code.Type = CodeType.MCode;
        code.MajorNumber = 99;
        code.File = file;
        code.FilePosition = M99Position;
        code.Length = M99Length;
        code.CancellationToken = cancellationToken;
        code.Flags = CodeFlags.Asynchronous | CodeFlags.IsFromMacro | CodeFlags.IsNestedMacro | CodeFlags.IsPostProcessed;
        code.Result = new Message();
        return code;
    }

    /// <summary>
    /// Pass a code with a result to the Executed stage the way FilesAborted does (Processor.cs:834, CodeProcessor.cs:339), so that the loop of the stage
    /// handles what the stage throws (PipelineStackItem.cs:85-89), and wait for it
    /// </summary>
    /// <returns>How the code failed, or null if it was finished</returns>
    private async Task<Exception?> CompleteAsync(Code code)
    {
        Task<Message?> task = code.Task;
        _provider!.GetRequiredService<CodeProcessor>().CodeCompleted(code);
        return await FailureOfAsync(task);
    }

    /// <summary>
    /// A code with a cancelled token is finished and leaves the next file position alone (Executed.cs:81). Without the guard the update advances it
    /// </summary>
    [Test]
    public async Task CancelledCodeDoesNotAdvanceTheNextFilePosition()
    {
        using CodeFile file = CreateMacroFile();
        IpcLog log = IpcLog.Attach(_provider!);
        long before = file.NextFilePosition;

        Exception? failure = await CompleteAsync(MakeM99(file, new CancellationToken(true)));

        List<IpcLog.Entry> entries = CancellationsAndStageFailures(log);
        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Null, "The M99 with a cancelled token was not finished");
            Assert.That(entries, Is.Empty, string.Join("\n", entries));
            Assert.That(file.NextFilePosition, Is.EqualTo(before), "A code with a cancelled token advanced the next file position");
        });
    }

    /// <summary>
    /// The case of 4f333e8cc: the file lock is held while the M99 with its cancelled token is in the Executed stage, as the link task holds it while it aborts
    /// the macro (Processor.cs:850-873). Before 4f333e8cc the update gave up on the held lock with a TaskCanceledException (AsyncLock.cs:65-67), which the loop
    /// of the stage logged and set on the M99 (PipelineStackItem.cs:85-89)
    /// </summary>
    [Test]
    public async Task CancelledCodeIsFinishedWhileItsFileIsLocked()
    {
        using CodeFile file = CreateMacroFile();
        IpcLog log = IpcLog.Attach(_provider!);
        long before = file.NextFilePosition;

        Exception? failure;
        using (await file.LockAsync(CancellationToken.None))
        {
            failure = await CompleteAsync(MakeM99(file, new CancellationToken(true)));
        }

        List<IpcLog.Entry> entries = CancellationsAndStageFailures(log);
        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Null, "The M99 with a cancelled token failed while its file was locked");
            Assert.That(entries, Is.Empty, string.Join("\n", entries));
            Assert.That(file.NextFilePosition, Is.EqualTo(before), "A code with a cancelled token advanced the next file position");
        });
    }

    /// <summary>
    /// A code whose token is not cancelled advances the next file position to its end (Code.cs:191-197), which a fork of the file starts from (CodeFile.cs:321)
    /// </summary>
    [Test]
    public async Task CodeAdvancesTheNextFilePosition()
    {
        using CodeFile file = CreateMacroFile();
        IpcLog log = IpcLog.Attach(_provider!);

        Exception? failure = await CompleteAsync(MakeM99(file, CancellationToken.None));

        List<IpcLog.Entry> entries = CancellationsAndStageFailures(log);
        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Null, "The M99 was not finished");
            Assert.That(entries, Is.Empty, string.Join("\n", entries));
            Assert.That(file.NextFilePosition, Is.EqualTo(M99Position + M99Length));
        });
    }

    #endregion

    #region Nested macro on the fake firmware

    /// <summary>
    /// Task of the M99, taken when it is processed internally (Code.cs:256-258), so a test can tell how the Executed stage completed it
    /// </summary>
    private TaskCompletionSource<Task<Message?>> _m99 = null!;

    /// <summary>
    /// M42 of the nested macro once it is held in the Executed stage
    /// </summary>
    private TaskCompletionSource<Code> _heldM42 = null!;

    /// <summary>
    /// Lets the held M42 go on
    /// </summary>
    private TaskCompletionSource _release = null!;

    /// <summary>
    /// The M-code handler of DCS, taking the task of the M99 and optionally holding the M42 in <see cref="ICodeHandler.CodeExecutedAsync"/>, which the
    /// Executed stage calls after it has dealt with the file position (Executed.cs:78-98)
    /// </summary>
    private sealed class WatchingMCodeHandler(ICodeHandler handler, CancelledCodeFilePosition test, bool holdM42) : ICodeHandler
    {
        public ValueTask<Message?> ProcessAsync(Code code, CancellationToken cancellationToken)
        {
            if (code.MajorNumber == 99)
            {
                test._m99.TrySetResult(code.Task);
            }
            return handler.ProcessAsync(code, cancellationToken);
        }

        public async ValueTask CodeExecutedAsync(Code code, CancellationToken cancellationToken)
        {
            if (holdM42 && code.MajorNumber == 42 && test._heldM42.TrySetResult(code))
            {
                await test._release.Task;
            }
            await handler.CodeExecutedAsync(code, cancellationToken);
        }
    }

    /// <summary>
    /// Start the fake firmware host with the M-code handler wrapped and write the macros: the outer one calls the nested one and goes on after it, the nested
    /// one returns with M99 before G4 P22
    /// </summary>
    private async Task<IpcLog> StartHostAsync(bool holdM42)
    {
        _host = await FakeFirmwareHost.StartAsync(configureServices: services =>
            services.AddKeyedSingleton<ICodeHandler>(Keys.MCodes, (provider, _) => new WatchingMCodeHandler(ActivatorUtilities.CreateInstance<MCodeHandler>(provider), this, holdM42)));
        _host.WriteMacro("inner.g", InnerMacro);
        _host.WriteMacro("outer.g", "M98 P\"inner.g\"\nG4 P20\n");
        return IpcLog.Attach(_host.Services);
    }

    /// <summary>
    /// Check that the outer macro went on after the M99 of the nested one, that the code after the M99 did not run and that the M99 was finished without a
    /// cancellation being logged
    /// </summary>
    private async Task AssertNestedMacroReturnedAsync(Task<string> m98, Exception? m99Failure, IpcLog log)
    {
        Assert.That(await m98.WaitAsync(Timeout), Is.EqualTo("\n"));
        string[] executed = [.. _host!.Firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text)];
        List<IpcLog.Entry> entries = CancellationsAndStageFailures(log);
        Assert.Multiple(() =>
        {
            Assert.That(m99Failure, Is.Null, "The Executed stage did not finish the M99");
            Assert.That(entries, Is.Empty, string.Join("\n", entries));
            Assert.That(executed, Is.EqualTo(new[] { "M98 P\"outer.g\"", "M98 P\"inner.g\"", "M42 P0 S1", "M99", "G4 P20" }));
            Assert.That(executed, Does.Not.Contain("G4 P22"), "The code after M99 ran");
        });
    }

    /// <summary>
    /// M99 in a nested macro as it happens on its own. Whether the file lock is held when the M99 reaches the Executed stage depends on timing: with the update
    /// of 4f333e8cc~1 put back this failed in 2 of 11 runs on the development machine, see <see cref="M99InNestedMacroWhileItsFileIsLocked"/> for the case
    /// that does not depend on timing
    /// </summary>
    [Test]
    public async Task M99InNestedMacro()
    {
        IpcLog log = await StartHostAsync(holdM42: false);

        Task<string> m98 = _host!.StartCode("M98 P\"outer.g\"");
        Task<Message?> m99 = await _m99.Task.WaitAsync(Timeout);
        await AssertNestedMacroReturnedAsync(m98, await FailureOfAsync(m99), log);
    }

    /// <summary>
    /// M99 in a nested macro with the file lock held when the M99 gets to the Executed stage. The M42 before it is held in the Executed stage until DSF has
    /// aborted the macro, so the M99 queues behind it with its token cancelled (Processor.cs:827-873), and the test takes the lock of the macro like its holders
    /// do (see the class summary) before the M42 goes on. Before 4f333e8cc the M99 failed with the TaskCanceledException and the loop of the stage logged it
    /// (PipelineStackItem.cs:85-89)
    /// </summary>
    [Test]
    public async Task M99InNestedMacroWhileItsFileIsLocked()
    {
        IpcLog log = await StartHostAsync(holdM42: true);

        Task<string> m98 = _host!.StartCode("M98 P\"outer.g\"");
        Code m42 = await _heldM42.Task.WaitAsync(Timeout);
        MacroFile inner = (MacroFile)m42.File!;
        long m42End = m42.FilePosition!.Value + (m42.Length ?? 0);
        Task<Message?> m99 = await _m99.Task.WaitAsync(Timeout);

        // RRF has run the M99 and DSF has aborted the macro, which cancelled the token of the M99 (MacroFile.cs:234-241)
        using (CancellationTokenSource cts = new(Timeout))
        {
            while (!inner.IsAborted)
            {
                await Task.Delay(10, cts.Token);
            }
        }
        Assert.That(m99.IsCompleted, Is.False, "The M99 left the Executed stage before the M42 that is held there");

        Exception? m99Failure;
        using (await inner.LockAsync(CancellationToken.None))
        {
            _release.TrySetResult();
            m99Failure = await FailureOfAsync(m99);
        }

        // The M42 often gets to the stage only after the abort as well, then it does not advance the position either (Executed.cs:81). Either way the M99 must not
        await AssertNestedMacroReturnedAsync(m98, m99Failure, log);
        Assert.That(inner.NextFilePosition, Is.LessThanOrEqualTo(m42End), "The M99 with its cancelled token advanced the next file position of the macro");
    }

    #endregion
}
