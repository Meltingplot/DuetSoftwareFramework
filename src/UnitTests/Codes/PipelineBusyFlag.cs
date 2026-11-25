using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DuetControlServer;
using DuetControlServer.Codes;
using DuetControlServer.Codes.Pipelines;
using DuetControlServer.Commands;
using DuetControlServer.Link.Channel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Codes;

/// <summary>
/// A pipeline stack item is busy while it holds a code (PipelineStackItem.Busy). Flushes wait for that (PipelineStackItem.FlushAsync,
/// ChannelProcessor.FlushAsync) and priority codes are routed by it (PipelineBase.IsIdle). The flag is reset by whoever takes the codes out:
/// the stage task after each code (PipelineStackItem.cs:91-96) and the link for the Firmware stage (StackState.SetBusy, Processor.cs:1227-1235).
/// A code that is written while the flag is reset must leave the stack item busy
/// </summary>
[TestFixture]
public class PipelineBusyFlag
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Stage whose codes are processed only when the test releases them
    /// </summary>
    private sealed class GatedPipeline(PipelineStage stage, ChannelProcessor channelProcessor, CodeProcessor codeProcessor, IHostApplicationLifetime lifetime, IOptions<Settings> settings)
        : PipelineBase(stage, channelProcessor, codeProcessor, lifetime, settings)
    {
        private readonly ConcurrentDictionary<Code, TaskCompletionSource> _started = new(ReferenceEqualityComparer.Instance), _released = new(ReferenceEqualityComparer.Instance);

        private static TaskCompletionSource Get(ConcurrentDictionary<Code, TaskCompletionSource> dictionary, Code code) =>
            dictionary.GetOrAdd(code, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public Task Started(Code code) => Get(_started, code).Task;

        public void Release(Code code) => Get(_released, code).TrySetResult();

        public override async ValueTask ProcessCodeAsync(PipelineStackItem stackItem, Code code)
        {
            Get(_started, code).TrySetResult();
            await Get(_released, code).Task;
        }
    }

    /// <summary>
    /// Channel whose writer stops before it stores a code until the gate is opened, so that the test can hold a writer between setting the
    /// busy flag and storing the code
    /// </summary>
    private sealed class GatedChannel : Channel<Code>
    {
        public readonly ManualResetEventSlim Gate = new(true);

        public readonly TaskCompletionSource WriterAtGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedChannel(Channel<Code> inner)
        {
            Reader = inner.Reader;
            Writer = new GatedWriter(this, inner.Writer);
        }

        private sealed class GatedWriter(GatedChannel channel, ChannelWriter<Code> inner) : ChannelWriter<Code>
        {
            private void Pass()
            {
                if (!channel.Gate.IsSet)
                {
                    channel.WriterAtGate.TrySetResult();
                    channel.Gate.Wait();
                }
            }

            public override bool TryWrite(Code item)
            {
                Pass();
                return inner.TryWrite(item);
            }

            public override async ValueTask WriteAsync(Code item, CancellationToken cancellationToken = default)
            {
                Pass();
                await inner.WriteAsync(item, cancellationToken);
            }

            public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => inner.WaitToWriteAsync(cancellationToken);

            public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);
        }
    }

    private FakeFirmwareHost? _host;

    [SetUp]
    public async Task SetUp() => _host = await FakeFirmwareHost.StartAsync();

    [TearDown]
    public async Task TearDown() => await _host!.DisposeAsync();

    private GatedPipeline CreatePipeline(PipelineStage stage)
    {
        CodeProcessor codeProcessor = _host!.Services.GetRequiredService<CodeProcessor>();
        return new GatedPipeline(stage, codeProcessor.Processors.Value[0], codeProcessor,
            _host.Services.GetRequiredService<IHostApplicationLifetime>(), _host.Services.GetRequiredService<IOptions<Settings>>());
    }

    private Code CreateCode() => _host!.Services.GetRequiredService<CodeFactory>().Create();

    /// <summary>
    /// A code is written while the stage task finishes the previous one. Before, the writer set the flag and stored the code only after it had
    /// released the lock (PipelineStackItem.WriteCodeAsync in upstream v3.7-dev), so the stage task could reset the flag in between because it
    /// did not see the code yet (PipelineStackItem.cs:91-96). The stage then processed the code while it reported idle, and a flush returned
    /// before the code had gone through the stage
    /// </summary>
    [Test]
    public async Task CodeWrittenWhileStageFinishesKeepsItBusy()
    {
        GatedPipeline pipeline = CreatePipeline(PipelineStage.Pre);
        PipelineStackItem item = pipeline.CurrentStackItem;
        GatedChannel channel = new(item.PendingCodes);
        typeof(PipelineStackItem).GetField(nameof(PipelineStackItem.PendingCodes))!.SetValue(item, channel);

        Code first = CreateCode(), second = CreateCode();
        Task writeSecond = Task.CompletedTask;
        try
        {
            await item.WriteCodeAsync(first);
            await pipeline.Started(first).WaitAsync(Timeout);

            // The writer of the second code stops after it has set the busy flag and before the code is stored
            channel.Gate.Reset();
            writeSecond = Task.Run(async () => await item.WriteCodeAsync(second));
            await channel.WriterAtGate.Task.WaitAsync(Timeout);

            // The stage task finishes the first code. Wait until it has reset the flag, at most 500 ms because it cannot do so while the
            // writer holds the stack item
            pipeline.Release(first);
            DateTime end = DateTime.UtcNow.AddMilliseconds(500);
            while (item.CodeBeingExecuted is not null && DateTime.UtcNow < end)
            {
                await Task.Delay(10);
            }

            channel.Gate.Set();
            await pipeline.Started(second).WaitAsync(Timeout);

            Assert.That(item.Busy, Is.True, "Stage reports idle while it processes a code");
            Task<bool> flush = item.FlushAsync().AsTask();
            await Task.Delay(100);
            Assert.That(flush.IsCompleted, Is.False, "Flush returned while the stage processes a code");

            pipeline.Release(second);
            Assert.That(await flush.WaitAsync(Timeout), Is.True);
            Assert.That(item.Busy, Is.False);
        }
        finally
        {
            channel.Gate.Set();
            pipeline.Release(first);
            pipeline.Release(second);
            await writeSecond.WaitAsync(Timeout);
        }
    }

    /// <summary>
    /// A code is written to the Firmware stage after the link has sent the pending codes and before it resets the flag because nothing is
    /// left to send (Processor.cs:1214-1235). Before, StackState.SetBusy reset the flag regardless, so the channel reported idle while it held
    /// a code and priority codes could be routed to it
    /// </summary>
    [Test]
    public async Task FirmwareStageKeepsBusyWhileCodeIsPending()
    {
        GatedPipeline pipeline = CreatePipeline(PipelineStage.Firmware);
        PipelineStackItem item = pipeline.CurrentStackItem;
        StackState state = new(item);

        await item.WriteCodeAsync(CreateCode());
        state.SetBusy(false);

        Assert.That(item.Busy, Is.True, "Firmware stage reports idle while it holds a code");
        Assert.That(pipeline.IsIdle(null), Is.False);

        // Once the code has been taken out, the flag is reset
        Assert.That(item.PendingCodes.Reader.TryRead(out _), Is.True);
        state.SetBusy(false);
        Assert.That(item.Busy, Is.False);
        Assert.That(await item.FlushAsync().AsTask().WaitAsync(Timeout), Is.True);
    }
}
