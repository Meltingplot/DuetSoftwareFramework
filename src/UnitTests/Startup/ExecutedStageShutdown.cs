using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using DuetControlServer.Codes.Handlers;
using DuetControlServer.Link.Channel;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using UnitTests.IPC;
using Code = DuetControlServer.Commands.Code;

namespace UnitTests.Startup;

/// <summary>
/// Codes that are in the Executed stage when DCS starts to stop, see the open finding "DCS logs 'Failed to process code in stage Executed' while it shuts down".
/// <list type="bullet">
/// <item>Once ApplicationStopping is cancelled, the tokens of the codes are cancelled as well, because their sources are linked to it (CodeProcessor.cs:310)</item>
/// <item>The DSF AsyncLock waited on a SemaphoreSlim, which gives up at once on a cancelled token even when the lock is free (SemaphoreSlim.cs:687-689 of
/// System.Private.CoreLib). Nito.AsyncEx.AsyncLock, which it replaced in bb5edd5e, takes a free lock in that case, see <see cref="Utility.AsyncLockCancelledToken"/></item>
/// <item>The Executed stage writes the reply to the event log with that token (Executed.cs:110-121 and 126-129, Logger.cs:258) and updates the file
/// position of a macro code with the token of the code (Executed.cs:81, Code.cs:181-185), both outside the try block of Executed.cs:164-240, which keeps
/// a cancellation during the shutdown quiet (Executed.cs:235)</item>
/// <item>The exception reaches the loop of the stage, which logged "Failed to process code in stage Executed" and left the code neither finished nor
/// cancelled, so whoever waits for it waited for good</item>
/// </list>
/// Fixed: the AsyncLock takes a free lock even with a cancelled token (AsyncLock.cs:50, 65), so the reply is still logged and returned, and the loop of the stage
/// completes a code whose stage throws, quietly for a cancellation while DCS stops (PipelineStackItem.cs:85-93).
/// The window: the code is in the Executed stage when the shutdown begins. A code handler holds it there in these tests, as the handlers of DCS do when
/// they wait, e.g. M122 for the diagnostics without a token (MCodeHandler.cs:1431-1436)
/// </summary>
[TestFixture]
public class ExecutedStageShutdown
{
    private static readonly TimeSpan Timeout = FakeFirmwareHost.DefaultTimeout;

    /// <summary>
    /// Time a code gets to finish once it has been released
    /// </summary>
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Hold for one code in <see cref="ICodeHandler.CodeExecutedAsync"/>
    /// </summary>
    /// <param name="match">Codes to hold, only the first one is held</param>
    private sealed class Hold(Func<Code, bool> match)
    {
        private int _taken;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously), _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _released.TrySetResult();

        /// <returns>Whether the code was held</returns>
        public async ValueTask<bool> WaitIfMatchAsync(Code code)
        {
            if (match(code) && Interlocked.Exchange(ref _taken, 1) == 0)
            {
                _entered.TrySetResult();
                await _released.Task;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The M-code handler of DCS, holding a code in <see cref="ICodeHandler.CodeExecutedAsync"/> before it is passed on
    /// </summary>
    /// <param name="handler">M-code handler of DCS</param>
    /// <param name="hold">Hold for the code</param>
    /// <param name="afterHold">Optional action for the held code once it is released, with the token the handler gets</param>
    private sealed class HoldingCodeHandler(ICodeHandler handler, Hold hold, Action<CancellationToken>? afterHold) : ICodeHandler
    {
        public ValueTask<Message?> ProcessAsync(Code code, CancellationToken cancellationToken) => handler.ProcessAsync(code, cancellationToken);

        public async ValueTask CodeExecutedAsync(Code code, CancellationToken cancellationToken)
        {
            if (await hold.WaitIfMatchAsync(code))
            {
                afterHold?.Invoke(cancellationToken);
            }
            await handler.CodeExecutedAsync(code, cancellationToken);
        }
    }

    /// <summary>
    /// Start the fake firmware host with the M-code handler wrapped in a <see cref="HoldingCodeHandler"/>
    /// </summary>
    private static Task<FakeFirmwareHost> StartAsync(Hold hold, Action<CancellationToken>? afterHold = null) => FakeFirmwareHost.StartAsync(configureServices: services =>
        services.AddKeyedSingleton<ICodeHandler>(Keys.MCodes, (provider, _) => new HoldingCodeHandler(ActivatorUtilities.CreateInstance<MCodeHandler>(provider), hold, afterHold)));

    private static bool IsM111(Code code) => code.Type == DuetAPI.Commands.CodeType.MCode && code.MajorNumber == 111;

    /// <summary>
    /// Entries of "Failed to process code in stage Executed" (PipelineStackItem.cs:90)
    /// </summary>
    private static List<IpcLog.Entry> StageFailures(IpcLog log) => [.. log.Entries.Where(entry => entry.Message == "Failed to process code in stage Executed")];

    /// <summary>
    /// Without a shutdown the held code goes on as usual: M111 P-1 is answered by DCS (MCodeHandler.cs:742)
    /// </summary>
    [Test]
    public async Task HeldCodeFinishesWhenReleased()
    {
        Hold hold = new(IsM111);
        await using FakeFirmwareHost host = await StartAsync(hold);
        IpcLog log = IpcLog.Attach(host.Services);

        Task<string> m111 = host.StartCode("M111 P-1");
        await hold.Entered.WaitAsync(Timeout);
        hold.Release();
        Assert.That(await m111.WaitAsync(Timeout), Does.StartWith("Current DCS log level: Information"));
        Assert.That(StageFailures(log), Is.Empty);
    }

    /// <summary>
    /// A reply of a code DSF answered itself goes to the event log (Executed.cs:119) with ApplicationStopping (Logger.cs:258), which is cancelled. The log
    /// write failed before the event log was even checked for being open, so this happened without M929 as well. The lock is free, so the reply is logged
    /// and returned as without the stop
    /// </summary>
    [Test]
    public async Task CodeInExecutedStageCompletesWhileDcsStops()
    {
        Hold hold = new(IsM111);
        await using FakeFirmwareHost host = await StartAsync(hold);
        IpcLog log = IpcLog.Attach(host.Services);

        Task<string> m111 = host.StartCode("M111 P-1");
        await hold.Entered.WaitAsync(Timeout);
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        hold.Release();

        bool completed = await Task.WhenAny(m111, Task.Delay(CompletionTimeout)) == m111;
        List<IpcLog.Entry> failures = StageFailures(log);
        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
            Assert.That(completed, Is.True, "The code was neither finished nor cancelled");
        });
        Assert.That(await m111, Does.StartWith("Current DCS log level: Information"));
    }

    /// <summary>
    /// A code handler that is cancelled while DCS stops, e.g. because it waits with the cancelled token of the code for a lock that is held: the loop of the
    /// stage completes the code with the cancellation and does not log it. SimpleCode answers a cancelled code with an empty reply (SimpleCode.cs:183-186)
    /// </summary>
    [Test]
    public async Task CodeWhoseHandlerIsCancelledWhileDcsStopsIsCompleted()
    {
        Hold hold = new(IsM111);
        await using FakeFirmwareHost host = await StartAsync(hold, afterHold: token => token.ThrowIfCancellationRequested());
        IpcLog log = IpcLog.Attach(host.Services);

        Task<string> m111 = host.StartCode("M111 P-1");
        await hold.Entered.WaitAsync(Timeout);
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        hold.Release();

        Assert.That(await m111.WaitAsync(CompletionTimeout), Is.Empty);
        Assert.That(StageFailures(log), Is.Empty);
    }

    /// <summary>
    /// A code handler that fails without a stop: the failure is logged as before, and the code is completed with it instead of being left unanswered
    /// </summary>
    [Test]
    public async Task CodeWhoseHandlerFailsIsCompleted()
    {
        Hold hold = new(IsM111);
        await using FakeFirmwareHost host = await StartAsync(hold, afterHold: _ => throw new InvalidOperationException("Handler failed"));
        IpcLog log = IpcLog.Attach(host.Services);

        Task<string> m111 = host.StartCode("M111 P-1");
        await hold.Entered.WaitAsync(Timeout);
        hold.Release();

        InvalidOperationException? e = await Assert.CatchAsync<InvalidOperationException>(async () => await m111.WaitAsync(CompletionTimeout));
        Assert.That(e?.Message, Is.EqualTo("Handler failed"));
        Assert.That(StageFailures(log).Select(entry => entry.Exception?.Message), Is.EqualTo(new[] { "Handler failed" }));
    }

    /// <summary>
    /// Codes behind the held one in the Executed queue are read after the cancellation as well. A macro code then failed earlier, when its file position is
    /// updated with its token (Executed.cs:81, Code.cs:181-185, CodeFile.cs:64), which is linked to ApplicationStopping as well (MacroFile.cs:166, 340):
    /// the same log line, but EventLogger.LogAsync is not in the stack, as in the second occurrence of the open finding. The held code is an M42 that RRF
    /// answers, so the G4 behind it is answered and queued meanwhile (an M111 P-1 would flush the channel first, MCodeHandler.cs:695). The M42 itself failed
    /// in the event log like the code above, through Executed.cs:123-129, because every reply of RRF gets a log level of at least Debug
    /// (Channel/Processor.cs:1202-1206)
    /// </summary>
    [Test]
    public async Task MacroCodeQueuedInExecutedStageCompletesWhileDcsStops()
    {
        Hold hold = new(code => code.Type == DuetAPI.Commands.CodeType.MCode && code.MajorNumber == 42);
        await using FakeFirmwareHost host = await StartAsync(hold);
        IpcLog log = IpcLog.Attach(host.Services);
        host.WriteMacro("stopping.g", "M42 P0 S1\nG4 P1\n");

        Task<string> m98 = host.StartCode("M98 P\"stopping.g\"");
        await hold.Entered.WaitAsync(Timeout);
        await WaitForAnsweredG4Async(host);
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        hold.Release();

        await Task.WhenAny(m98, Task.Delay(CompletionTimeout));
        List<IpcLog.Entry> failures = StageFailures(log);
        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
            Assert.That(failures.Where(entry => entry.Exception?.StackTrace?.Contains("EventLogger") == false), Is.Empty, "Failure without EventLogger in the stack");
            Assert.That(m98.IsCompleted, Is.True, "M98 was neither finished nor cancelled");
        });
    }

    /// <summary>
    /// Wait until RRF has answered the G4 and DSF has taken it off the codes it waits for (Channel/Processor.cs:426), which puts it into the Executed queue
    /// </summary>
    private static async Task WaitForAnsweredG4Async(FakeFirmwareHost host)
    {
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(DuetAPI.CodeChannel.HTTP).Any(code => code.Text == "G4 P1"), Timeout), Is.True, "RRF did not run the G4");
        Manager channels = host.Services.GetRequiredService<Manager>();
        DateTime end = DateTime.UtcNow + Timeout;
        while (true)
        {
            using (channels[DuetAPI.CodeChannel.HTTP].Lock())
            {
                if (!channels[DuetAPI.CodeChannel.HTTP].BufferedCodes.Any(code => code.Type == DuetAPI.Commands.CodeType.GCode && code.MajorNumber == 4))
                {
                    return;
                }
            }
            Assert.That(DateTime.UtcNow, Is.LessThan(end), "DSF did not take the answer of the G4");
            await Task.Delay(10);
        }
    }
}
