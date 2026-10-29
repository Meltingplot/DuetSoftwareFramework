using DuetAPI.Commands;
using DuetControlServer.Link.Channel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines;

/// <summary>
/// Initial pipeline element for codes being started
/// </summary>
/// <param name="channelManager">Channel manager</param>
/// <param name="channelProcessor">Channel processor</param>
/// <param name="codeProcessor">Code processor</param>
/// <param name="lifetime">Application lifetime</param>
/// <param name="settings">Settings</param>
public sealed class Start(Manager channelManager, ChannelProcessor channelProcessor, CodeProcessor codeProcessor, IHostApplicationLifetime lifetime, IOptions<Settings> settings)
    : PipelineBase(PipelineStage.Start, channelProcessor, codeProcessor, lifetime, settings)
{
    /// <summary>
    /// Counters for unbuffered codes per stack level
    /// </summary>
    private readonly ConditionalWeakTable<PipelineStackItem, AsyncCountdownEvent> _unbufferedCodesCounters = new();

    /// <inheritdoc />
    public override async ValueTask ProcessCodeAsync(Commands.Code code)
    {
        try
        {
            // Wait for pending unbuffered codes on the same or a deeper stack level to finish first unless we're dealing with a priority code.
            // Codes on underlying levels are executed after this code (e.g. a client code waiting for a macro), so waiting for them would deadlock
            if (!code.Flags.HasFlag(CodeFlags.IsPrioritized))
            {
                Task[] pendingCodes;
                while ((pendingCodes = GetPendingUnbufferedCodes(code)).Length > 0)
                {
                    await Task.WhenAll(pendingCodes);
                }
            }

            // Make sure other codes wait for this code to complete first if it is marked "Unbuffered"
            if (code.Flags.HasFlag(CodeFlags.Unbuffered) && GetStackItem(code) is PipelineStackItem stackItem)
            {
                AsyncCountdownEvent counter = _unbufferedCodesCounters.GetValue(stackItem, _ => new AsyncCountdownEvent(0));
                counter.AddCount(1);
                _ = code.Task.ContinueWith(_ => counter.Signal(), TaskContinuationOptions.ExecuteSynchronously);
            }

            // Log it
            if (code.Flags.HasFlag(CodeFlags.IsPrioritized))
            {
                ChannelProcessor.Logger.LogDebug("Starting code {Code} (prioritized)", code);
            }
            else if (code.Flags.HasFlag(CodeFlags.IsFromMacro))
            {
                ChannelProcessor.Logger.LogDebug("Starting code {Code} (macro code)", code);
            }
            else if (channelManager.IsWaitingForAcknowledgment(code.Channel))
            {
                ChannelProcessor.Logger.LogDebug("Starting code {Code} (acknowledgment)", code);
            }
            else
            {
                ChannelProcessor.Logger.LogDebug("Starting code {Code}", code);
            }

            // Code execution may begin, send it to the Pre stage
            await ChannelProcessor.WriteCodeAsync(code, PipelineStage.Pre);
        }
        catch (Exception e)
        {
            CodeProcessor.CancelCode(code, e);
        }
    }

    /// <summary>
    /// Get the stack item that is processing the given code
    /// </summary>
    /// <param name="code">Code being processed</param>
    /// <returns>Stack item or null if it has been popped already</returns>
    private PipelineStackItem? GetStackItem(Commands.Code code)
    {
        lock (_stack)
        {
            // Codes without a file may be processed on a lower level than the one a lookup by file finds (e.g. when a message box level is pushed)
            foreach (PipelineStackItem stackItem in _stack)
            {
                if (stackItem.CodeBeingExecuted == code)
                {
                    return stackItem;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Get tasks that wait for the pending unbuffered codes on the stack level of the given code and the levels above it
    /// </summary>
    /// <param name="code">Code being processed</param>
    /// <returns>Tasks to wait for</returns>
    private Task[] GetPendingUnbufferedCodes(Commands.Code code)
    {
        List<AsyncCountdownEvent> counters = [];
        lock (_stack)
        {
            // The stack is enumerated from the deepest level downwards
            foreach (PipelineStackItem stackItem in _stack)
            {
                if (_unbufferedCodesCounters.TryGetValue(stackItem, out AsyncCountdownEvent? counter) && counter.CurrentCount > 0)
                {
                    counters.Add(counter);
                }

                if (stackItem.CodeBeingExecuted == code)
                {
                    return [.. counters.Select(counter => counter.WaitAsync(code.CancellationToken))];
                }
            }
        }

        // The stack level of this code has been popped, so no other code on it is left to be held back
        return [];
    }
}
