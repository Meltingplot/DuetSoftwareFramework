using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using DuetControlServer.Files;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Link.Requests;
using DuetSharedLibrary;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Link.Channel;

/// <summary>
/// Class used to process data on a single code channel
/// </summary>
/// <remarks>
/// This class should be merged with Codes.Pipelines.Firmware at some point
/// </remarks>
public sealed class Processor
{
    /// <summary>
    /// What code channel this class is about
    /// </summary>
    public CodeChannel Channel { get; }

    // Private variables
    private readonly Commands.CommandFactory _commandFactory;
    private readonly CodeProcessor _codeProcessor;
    private readonly FilePathResolver _filePathResolver;
    private readonly ILinkAdapter _linkAdapter;
    private readonly LinkInterface _linkInterface;
    private readonly JobProcessor _jobProcessor;

    private readonly Model.ObjectModel _model;
    private readonly FileFactory _macroFileFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger _logger;
    private readonly Settings _settings;

    /// <summary>
    /// Constructor of a code channel processor
    /// </summary>
    /// <param name="channel">Code channel of this instance</param>
    /// <param name="commandFactory">Command factory</param>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="filePathResolver">File path resolver</param>
    /// <param name="linkAdapter">Link adapter</param>
    /// <param name="linkInterface">Link interface</param>
    /// <param name="jobProcessor">Job processor</param>
    /// <param name="macroFileFactory">Macro file factory</param>
    /// <param name="model">Object model</param>
    /// <param name="lifetime">Host application lifetime</param>
    /// <param name="loggerFactory">Logger factory</param>
    /// <param name="settings">Settings</param>
    public Processor(
        CodeChannel channel,
        Commands.CommandFactory commandFactory,
        CodeProcessor codeProcessor,
        FilePathResolver filePathResolver,
        ILinkAdapter linkAdapter,
        LinkInterface linkInterface,
        JobProcessor jobProcessor,
        FileFactory macroFileFactory,
        Model.ObjectModel model,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory,
        IOptions<Settings> settings)
    {
        Channel = channel;
        _commandFactory = commandFactory;
        _codeProcessor = codeProcessor;
        _filePathResolver = filePathResolver;
        _jobProcessor = jobProcessor;
        _linkAdapter = linkAdapter;
        _linkInterface = linkInterface;
        _macroFileFactory = macroFileFactory;
        _model = model;
        _lifetime = lifetime;
        _logger = loggerFactory.CreateLogger(channel.ToString());
        _settings = settings.Value;

        BaseState = CurrentState = new StackState(codeProcessor.GetFirmwareState(channel));
        Stack.Push(CurrentState);
    }

    /// <summary>
    /// Lock used when accessing this instance
    /// </summary>
    private readonly AsyncLock _lock = new();

    /// <summary>
    /// Lock access to this code channel
    /// </summary>
    /// <returns>Disposable lock</returns>
    public IDisposable Lock(CancellationToken cancellationToken = default) => _lock.Lock(cancellationToken);

    /// <summary>
    /// Lock access to this code channel asynchronously
    /// </summary>
    /// <returns>Disposable lock</returns>
    public async ValueTask<IDisposable> LockAsync(CancellationToken cancellationToken = default) => await _lock.LockAsync(cancellationToken);

    /// <summary>
    /// This is set to true if all the files have been aborted and RRF has to be notified
    /// </summary>
    private bool _allFilesAborted;

    /// <summary>
    /// This is set to true if the notification about aborted files went out with the last transfer. RRF wrote the data of that transfer
    /// before it read the notification, which ends a pending macro request with an error, so macro requests in that data are void.
    /// So are closals of macro files, because the abort has removed all macro files from the stack
    /// </summary>
    private bool _allFilesAbortedSent;

    /// <summary>
    /// This is set to true while RRF has not answered the notification about aborted files yet. It answers with the data of the transfer
    /// after the one that brought the notification, by asking for it again if it could not process it (SbcInterface.cpp:1338-1341).
    /// Nothing else goes out on this channel until then, because a copy that RRF processes later drops the codes it got before (InvalidateBufferedCodes)
    /// </summary>
    private bool _allFilesAbortUnconfirmed;

    /// <summary>
    /// This is set to true if RRF asked for the notification about aborted files again in the data being processed. The copy goes out with the next transfer
    /// </summary>
    private bool _allFilesAbortResent;

    /// <summary>
    /// Whether the positional windows above act on untagged packets of RRF (design/sbc-link-v8-requirements.md CE-13). They stay until milestone E3,
    /// a test switches them off to measure the link without them (AC-1, AC-2)
    /// </summary>
    internal bool UseAbortWindows { get; set; } = true;

    /// <summary>
    /// Epoch of this channel (design/sbc-link-v8-requirements.md CE-13). DSF is the only writer of an epoch: it increments it where it writes
    /// InvalidateChannel, and LinkService resets it where a connection ends (CE-4). Both happen on the link thread, which tags the packets of the
    /// channel with it and drops RRF's packets of other epochs (CE-14). <see cref="Invalidate"/> does not reset it, because it runs on every
    /// emergency stop as well, while RRF keeps its epochs across a stop
    /// </summary>
    public byte Epoch { get; private set; }

    /// <summary>
    /// Tagged packets of RRF that this channel dropped because their epoch was not the current one, per request (CE-20). Packets of the current epoch
    /// that arrive after DSF aborted the channel and before it wrote InvalidateChannel count as well. Access it with the channel locked
    /// </summary>
    internal Dictionary<Protocol.FirmwareRequests.Request, int> StaleDrops { get; } = [];

    /// <summary>
    /// Tagged packets of RRF whose epoch follows the current one, which cannot happen on an intact link (CE-5). They are dropped too and counted here
    /// </summary>
    internal int AheadDrops { get; private set; }

    /// <summary>
    /// Out-of-order replies that are not of an older epoch (CE-20, evidence gate EG-1)
    /// </summary>
    internal int OutOfOrderReplies { get; private set; }

    /// <summary>
    /// Complete replies of work that DSF has ended that this channel showed as messages (decision of 2026-11-10). Their packets are counted in
    /// <see cref="StaleDrops"/> as well, because they complete no code
    /// </summary>
    internal int ShownStaleReplies { get; private set; }

    /// <summary>
    /// Check if a packet of RRF belongs to work of this channel that DSF has ended, and count it then (CE-14). A tagged packet is current only if its
    /// epoch is the current one and DSF has not aborted all files since: from that abort until the write of InvalidateChannel(e+1) RRF still writes
    /// epoch e, but DSF has removed e's work from its stack. An untagged packet is decided by the v7 windows (CE-13).
    /// Call it with the channel locked, because the abort happens under that lock
    /// </summary>
    /// <param name="tag">Header field ResendPacketId of the packet</param>
    /// <param name="request">Type of the packet</param>
    /// <returns>Whether the packet is dropped</returns>
    internal bool IsStale(ushort tag, Protocol.FirmwareRequests.Request request)
    {
        if (!ChannelEpoch.IsTagged(tag))
        {
            return false;
        }

        byte epoch = ChannelEpoch.EpochOf(tag);
        if (epoch == Epoch && !_allFilesAborted)
        {
            return false;
        }

        if (IsAhead(epoch))
        {
            AheadDrops++;
            _logger.LogWarning("Dropped {Request} of epoch {Epoch}, which is ahead of epoch {Current}", request, epoch, Epoch);
        }
        else
        {
            StaleDrops[request] = StaleDrops.GetValueOrDefault(request) + 1;
            _logger.LogDebug("Dropped {Request} of epoch {Epoch}, current epoch is {Current}", request, epoch, Epoch);
        }
        return true;
    }

    /// <summary>
    /// Whether an epoch follows the current one, which cannot happen on an intact link (CE-5)
    /// </summary>
    /// <param name="epoch">Epoch of a packet</param>
    /// <returns>Whether it is ahead</returns>
    private bool IsAhead(byte epoch) => epoch != Epoch && ChannelEpoch.Follows(epoch, Epoch);

    /// <summary>
    /// Whether all files of this channel are aborted and InvalidateChannel is not written yet. Nothing of the channel goes out until then: the codes
    /// wait in Spin, and LinkService holds the requests of the channel, which the new epoch fails (CE-25 (f)). Read on the link thread without the lock;
    /// a request that is written in the same instant as the abort carries the old epoch and is failed the same way
    /// </summary>
    internal bool IsAbortUnsent => _allFilesAborted;

    /// <summary>
    /// Results of a DoCode of an older epoch that were not sent (CE-25 (e))
    /// </summary>
    internal int DroppedResults { get; private set; }

    /// <summary>
    /// Check if the result of a code that RRF passed on (DoCode) may be sent: one of an older epoch is not, because RRF has ended that DoCode at the
    /// abort and drops its chunks (CE-25 (e), CE-10). Called on the link thread, which is the only writer of <see cref="Epoch"/>
    /// </summary>
    /// <param name="tag">Tag of the DoCode request</param>
    /// <returns>Whether the result is sent</returns>
    internal bool IsResultOfCurrentEpoch(ushort tag)
    {
        if (ChannelEpoch.IsTagged(tag) && (ChannelEpoch.EpochOf(tag) != Epoch || _allFilesAborted))
        {
            DroppedResults++;
            _logger.LogDebug("Not sending the result of a firmware code of epoch {Epoch}, current epoch is {Current}", ChannelEpoch.EpochOf(tag), Epoch);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Start a new epoch of this channel where InvalidateChannel(e+1) has been written: the partial reply of the old epoch must not be prepended to
    /// the next reply, and the requests of the old epoch are failed, because RRF drops them (CE-13, CE-25)
    /// </summary>
    /// <param name="epoch">New epoch</param>
    private void ChangeEpoch(byte epoch)
    {
        Epoch = epoch;
        DiscardPartialReply();
        _linkInterface.FailChannelRequests(Channel);
    }

    /// <summary>
    /// Reset the epoch where a connection ends (CE-4). RRF sets its epochs to 0 at the first marker of the next connection
    /// </summary>
    public void ResetEpoch()
    {
        Epoch = 0;
        DiscardPartialReply();
        _stalePartialReply = null;

        // The epochs start at 0 again, so a reply of the old connection that waits here would look current
        if (PendingReplies.Count > 0)
        {
            _logger.LogDebug("Discarding {Count} pending replies of the old connection", PendingReplies.Count);
            PendingReplies.Clear();
        }
    }

    /// <summary>
    /// Discard the incomplete reply of the current code (K11 for an epoch change)
    /// </summary>
    private void DiscardPartialReply()
    {
        _lastPartialMessage = null;
        _lastPartialMessageType = MessageTypeFlags.NoDestinationMessage;
    }

    /// <summary>
    /// Stack of the different channel states
    /// </summary>
    public Stack<StackState> Stack { get; } = new();

    /// <summary>
    /// First item on the stack
    /// </summary>
    public StackState BaseState { get; }

    /// <summary>
    /// Get the current state from the stack
    /// </summary>
    public StackState CurrentState { get; private set; }

    /// <summary>
    /// Push a new state on the stack
    /// </summary>
    /// <param name="file">Optional file being executed</param>
    /// <returns>New state</returns>
    public StackState Push(CodeFile? file = null)
    {
        // Push a new element on the stack
        StackState state = new(_codeProcessor.Push(Channel, file));

        // Dequeue already suspended codes first so the correct order is maintained
        Queue<Code> alreadySuspendedCodes = new(CurrentState.SuspendedCodes.Count);
        while (CurrentState.SuspendedCodes.TryDequeue(out Code? suspendedCode))
        {
            alreadySuspendedCodes.Enqueue(suspendedCode);
        }

        // Suspend the already buffered codes
        foreach (Code bufferedCode in BufferedCodes)
        {
            _logger.LogDebug("Suspending code {Code}", bufferedCode);
            CurrentState.SuspendedCodes.Enqueue(bufferedCode);
        }
        BytesBuffered = 0;
        BufferedCodes.Clear();

        // Add back any codes that were previously suspended
        while (alreadySuspendedCodes.TryDequeue(out Code? suspendedCode))
        {
            CurrentState.SuspendedCodes.Enqueue(suspendedCode);
        }

        // Done
        Stack.Push(state);
        CurrentState = state;
        return state;
    }

    /// <summary>
    /// Pop the last state from the stack
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public void Pop()
    {
        // There must be at least one item on the stack...
        if (Stack.Count == 1)
        {
            throw new InvalidOperationException($"Stack underrun on channel {Channel}");
        }

        // Pop the stack
        _codeProcessor.Pop(Channel);
        StackState oldState = Stack.Pop();
        CurrentState = Stack.Peek();

        // Restore the message box state
        _isWaitingForAcknowledgment = CurrentState.WaitingForAcknowledgement;

        // Invalidate obsolete lock requests and supended codes
        while (oldState.LockRequests.TryDequeue(out LockMovementRequest? lockRequest))
        {
            lockRequest.Resolve(false);
        }

        while (oldState.SuspendedCodes.TryDequeue(out Code? suspendedCode))
        {
            _codeProcessor.CancelCode(suspendedCode);
        }

        // Deal with macro files
        if (oldState.File is MacroFile macro)
        {
            using (macro.Lock(_lifetime.ApplicationStopped))
            {
                if (macro.IsExecuting)
                {
                    if (!macro.IsAborted)
                    {
                        if (!_lifetime.ApplicationStopping.IsCancellationRequested)
                        {
                            _logger.LogWarning("Aborting orphaned macro file {File}", macro.FilePath.Virtual);
                        }
                        macro.Abort();
                    }
                }
                else
                {
                    if (Channel != CodeChannel.Daemon)
                    {
                        _logger.LogDebug("Disposing macro file {File}", macro.FilePath.Virtual);
                    }
                    else
                    {
                        _logger.LogTrace("Disposing macro file {File}", macro.FilePath.Virtual);
                    }
                    macro.Dispose();
                }
            }
        }

        // Invalidate macro start codes, pending codes, and flush requests
        if (oldState.StartCode is not null)
        {
            _logger.LogWarning("==> Cancelling unfinished starting code: {Code}", oldState.StartCode);
            _codeProcessor.CancelCode(oldState.StartCode);
        }

        while (oldState.PendingCodes.Reader.TryRead(out Code? pendingCode))
        {
            pendingCode.Stage = PipelineStage.Firmware;
            _codeProcessor.CancelCode(pendingCode);
        }

        while (oldState.FlushRequests.TryDequeue(out TaskCompletionSource<bool>? source))
        {
            source.SetResult(false);
        }
        oldState.SetBusy(false);
    }

    /// <summary>
    /// Pop the last state from the stack
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public async Task PopAsync()
    {
        // There must be at least one item on the stack...
        if (Stack.Count == 1)
        {
            throw new InvalidOperationException($"Stack underrun on channel {Channel}");
        }

        // Pop the stack
        _codeProcessor.Pop(Channel);
        StackState oldState = Stack.Pop();
        CurrentState = Stack.Peek();

        // Restore the message box state
        _isWaitingForAcknowledgment = CurrentState.WaitingForAcknowledgement;

        // Invalidate obsolete lock requests and supended codes
        while (oldState.LockRequests.TryDequeue(out LockMovementRequest? lockRequest))
        {
            lockRequest.Resolve(false);
        }

        while (oldState.SuspendedCodes.TryDequeue(out Code? suspendedCode))
        {
            _codeProcessor.CancelCode(suspendedCode);
        }

        // Deal with macro files
        if (oldState.File is MacroFile macro)
        {
            using (await macro.LockAsync(_lifetime.ApplicationStopping))
            {
                if (macro.IsExecuting)
                {
                    if (!macro.IsAborted)
                    {
                        if (!_lifetime.ApplicationStopping.IsCancellationRequested)
                        {
                            _logger.LogWarning("Aborting orphaned macro file {File}", macro.FilePath.Virtual);
                        }
                        macro.Abort();
                    }
                }
                else
                {
                    if (Channel != CodeChannel.Daemon)
                    {
                        _logger.LogDebug("Disposing macro file {File}", macro.FilePath.Virtual);
                    }
                    else
                    {
                        _logger.LogTrace("Disposing macro file {File}", macro.FilePath.Virtual);
                    }
                    macro.Dispose();
                }
            }
        }

        // Invalidate macro start codes, pending codes, and flush requests
        if (oldState.StartCode is not null)
        {
            _logger.LogWarning("==> Cancelling unfinished starting code: {Code}", oldState.StartCode);
            _codeProcessor.CancelCode(oldState.StartCode);
        }

        while (oldState.PendingCodes.Reader.TryRead(out Code? pendingCode))
        {
            pendingCode.Stage = PipelineStage.Firmware;
            _codeProcessor.CancelCode(pendingCode);
        }

        while (oldState.FlushRequests.TryDequeue(out TaskCompletionSource<bool>? source))
        {
            source.SetResult(false);
        }
        oldState.SetBusy(false);
    }

    /// <summary>
    /// Block file macro calls if the state is being copied
    /// </summary>
    private static readonly List<MacroFile> _macrosToStart = [];

    /// <summary>
    /// Copy the state from another channel processor
    /// </summary>
    /// <param name="from">Source</param>
    /// <returns>Asynchronous task</returns>
    public void CopyState(Processor from)
    {
        if (Stack.Count != 1)
        {
            throw new ArgumentException("Cannot copy state because the stack is not empty");
        }

        // Create macro/state copies but don't start the macros yet. Some may need to wait before they can start execution
        StackState baseItem = from.Stack.Last();
        foreach (StackState item in from.Stack.Reverse())
        {
            if (item != baseItem)
            {
                if (item.File is MacroFile macro)
                {
                    MacroFile copy = _macroFileFactory.CreateMacro(macro, Channel);
                    Push(copy);
                    lock (_macrosToStart)
                    {
                        _macrosToStart.Add(copy);
                    }
                }
                else
                {
                    Push();
                    CurrentState.WaitingForAcknowledgement = item.WaitingForAcknowledgement;
                }
            }
        }
    }

    /// <summary>
    /// Start copied macros. This must happen later to avoid race conditions
    /// </summary>
    public static void StartCopiedMacros()
    {
        lock (_macrosToStart)
        {
            foreach (MacroFile file in _macrosToStart)
            {
                file.Start(false);
            }
            _macrosToStart.Clear();
        }
    }

    /// <summary>
    /// Discard a state copied by <see cref="CopyState(Processor)"/> that was never started,
    /// e.g. because the job ended before the firmware forked it
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public async Task DiscardCopiedStateAsync()
    {
        lock (_macrosToStart)
        {
            _macrosToStart.RemoveAll(macro => macro.Channel == Channel);
        }

        while (Stack.Count > 1)
        {
            if (CurrentState.File is MacroFile macro)
            {
                using (await macro.LockAsync(_lifetime.ApplicationStopping))
                {
                    macro.Abort();
                }
            }
            await PopAsync();
        }
    }

    /// <summary>
    /// List of buffered G/M/T-codes that are being processed by the firmware
    /// </summary>
    public List<Code> BufferedCodes { get; } = [];

    /// <summary>
    /// Occupied space for buffered codes in bytes
    /// </summary>
    public int BytesBuffered { get; private set; }

    /// <summary>
    /// Queue of code replies for codes that pushed the stack (e.g. macro files or blocking messages).
    /// Replies must be consumed in arrival order, else multi-chunk replies are reassembled wrongly.
    /// Each reply keeps the tag of its packet (CE-11), so that one of work that DSF has ended while it waited is decided by its tag when it is taken
    /// </summary>
    public Queue<(MessageTypeFlags Flags, string Reply, ushort Tag)> PendingReplies { get; } = new();

    /// <summary>
    /// Print diagnostics of this class
    /// </summary>
    /// <param name="builder">String builder to print to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async ValueTask PrintDiagnosticsAsync(StringBuilder builder, CancellationToken cancellationToken)
    {
        StringBuilder channelDiagostics = new();

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IDisposable? lockObject = null;
        try
        {
            cts.CancelAfter(2000);
            lockObject = await _lock.LockAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            channelDiagostics.AppendLine($"Failed to lock {Channel} processor within 2 seconds");
        }

        foreach (Code bufferedCode in BufferedCodes)
        {
            channelDiagostics.AppendLine($"Buffered code: {bufferedCode}");
        }
        if (BytesBuffered != 0)
        {
            channelDiagostics.AppendLine($"Buffered codes: {BytesBuffered} bytes total");
        }
        int staleDrops = StaleDrops.Values.Sum();
        if (Epoch != 0 || staleDrops != 0 || AheadDrops != 0 || OutOfOrderReplies != 0)
        {
            channelDiagostics.AppendLine($"Epoch {Epoch}, stale drops {staleDrops}, ahead drops {AheadDrops}, out-of-order replies {OutOfOrderReplies}, stale replies shown {ShownStaleReplies}");
        }

        string prefix = ">";
        foreach (StackState state in Stack.Reverse())
        {
            if (state.WaitingForAcknowledgement)
            {
                channelDiagostics.AppendLine($"{prefix} Waiting for acknowledgement, requested by {((state.StartCode is null) ? "system" : state.StartCode.ToString())}");
            }
            if (state.LockRequests.Count > 0)
            {
                channelDiagostics.AppendLine($"{prefix} Number of lock/unlock requests: {state.LockRequests.Count(item => item.IsLockRequest)}/{state.LockRequests.Count(item => !item.IsLockRequest)}");
            }
            if (state.File is MacroFile macro)
            {
                channelDiagostics.AppendLine($"{prefix} {(macro.IsExecuting ? "Doing" : "Finishing")} macro {state.File.FilePath.Virtual}, started by {((state.StartCode is null) ? "system" : state.StartCode.ToString())}");
            }
            foreach (Code suspendedCode in state.SuspendedCodes)
            {
                channelDiagostics.AppendLine($"{prefix} Suspended code: {suspendedCode}");
            }
            if (state.FlushRequests.Count > 0)
            {
                channelDiagostics.AppendLine($"{prefix} Number of flush requests: {state.FlushRequests.Count}");
            }
            prefix += '>';
        }

        if (channelDiagostics.Length != 0)
        {
            builder.AppendLine($"{Channel}:");
            builder.Append(channelDiagostics);
        }
        lockObject?.Dispose();
    }

    /// <summary>
    /// Checks if this channel is waiting for acknowledgement
    /// </summary>
    /// <remarks>
    /// This is volatile to allow fast access without locking this instance first
    /// </remarks>
    public bool IsWaitingForAcknowledgment => _isWaitingForAcknowledgment;
    private volatile bool _isWaitingForAcknowledgment;

    /// <summary>
    /// Get a flush task
    /// </summary>
    /// <param name="state">Stack item</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    private ValueTask<bool> GetFlushTask(StackState state, CancellationToken cancellationToken = default)
    {
        // Check if we can resolve the flush request immediately if nothing is being done
        if (state == CurrentState &&
            BufferedCodes.Count == 0 && state.LockRequests.Count == 0 && !_allFilesAborted &&
            (state.File is not MacroFile macro || (!macro.JustStarted && macro.IsExecuting)) && !state.MacroCompleted &&
            state.SuspendedCodes.Count == 0 && !state.PendingCodes.Reader.TryPeek(out _))
        {
            return ValueTask.FromResult(true);
        }

        // Need to wait for the SPI connector to finish other operations first
        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        state.FlushRequests.Enqueue(tcs);
        return new ValueTask<bool>(tcs.Task.WaitAsync(cancellationToken));
    }

    /// <summary>
    /// Flush pending codes and return true on success or false on failure.
    /// This method may be deprecated; in theory it should suffice to flush the pipeline only (with stricter Busy conditions)
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes could be flushed</returns>
    public ValueTask<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        // Need to find the correct state for a flush request first.
        // Generic flush requests are not meant for temporary macro states
        foreach (StackState state in Stack)
        {
            if ((state.File is not MacroFile macro || !macro.WasStarted || macro.IsExecuting) && !state.MacroCompleted)
            {
                return GetFlushTask(state, cancellationToken);
            }
        }

        // Fallback, should not happen
        _logger.LogWarning("Failed to find suitable stack level for flush request, falling back to current one");
        return GetFlushTask(CurrentState, cancellationToken);
    }

    /// <summary>
    /// Flush pending codes and return true on success or false on failure.
    /// This method may be deprecated; in theory it should suffice to flush the pipeline only (with stricter Busy conditions)
    /// </summary>
    /// <param name="file">Optional code file for the flush target</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes could be flushed</returns>
    public ValueTask<bool> FlushAsync(CodeFile file, CancellationToken cancellationToken = default)
    {
        // Need to find the correct state for a flush request first.
        // Generic flush requests are not meant for temporary macro states
        foreach (StackState state in Stack)
        {
            if (state.File == file)
            {
                return GetFlushTask(state, cancellationToken);
            }
        }

        // Fallback, should not happen
        _logger.LogWarning("Failed to find suitable stack level for flush request, falling back to current one");
        return GetFlushTask(CurrentState, cancellationToken);
    }

    /// <summary>
    /// Flush all pending codes and return true on success or false on failure.
    /// This method may be deprecated; in theory it should suffice to flush the pipeline only (with stricter Busy conditions)
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes could be flushed</returns>
    public ValueTask<bool> FlushAllAsync(CancellationToken cancellationToken = default) => GetFlushTask(BaseState, cancellationToken);

    /// <summary>
    /// Lock all movement systems and wait for standstill
    /// </summary>
    /// <returns>Whether the movement systems could be locked</returns>
    public Task<bool> LockAllMovementSystemsAndWaitForStandstill()
    {
        LockMovementRequest request = new(true);
        CurrentState.LockRequests.Enqueue(request);
        return request.Task;
    }

    /// <summary>
    /// Unlock all resources occupied by the given channel
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public Task UnlockAll()
    {
        LockMovementRequest request = new(false);
        CurrentState.LockRequests.Enqueue(request);
        return request.Task;
    }

    /// <summary>
    /// Flag the currently executing macro file as (not) pausable
    /// </summary>
    /// <param name="isPausable">Whether the macro is pausable or not</param>
    /// <returns>Asynchronous task</returns>
    public async Task SetMacroPausable(bool isPausable)
    {
        if (CurrentState.File is MacroFile macro)
        {
            using (await macro.LockAsync(_lifetime.ApplicationStopping))
            {
                macro.IsPausable = isPausable;
            }
        }
    }

    /// <summary>
    /// Called when the last or all files have been aborted by the firmware
    /// </summary>
    /// <param name="abortAll">Whether to abort all files</param>
    /// <param name="tagged">Whether RRF tagged the request, which it does once it knows the epoch of the channel</param>
    public void FilesAborted(bool abortAll, bool tagged)
    {
        bool macroAborted = false;

        // If only the last macro is aborted, we may have a pending reply for e.g. M99
        if (!abortAll)
        {
            ResolvePendingReplies();

            // The M99 that requested this abort has been executed, so complete it here. RRF sends no reply for it because that reply
            // could overtake the abort request and then complete the code that started the macro being aborted
            if (BufferedCodes.Count > 0 && BufferedCodes[0].Type == CodeType.MCode && BufferedCodes[0].MajorNumber == 99)
            {
                Code code = BufferedCodes[0];
                BytesBuffered -= code.BinarySize;
                BufferedCodes.RemoveAt(0);

                code.Result = new Message();
                _codeProcessor.CodeCompleted(code);
            }
        }

        // Clean up the stack
        Code? startCode = null;
        while (CurrentState.WaitingForAcknowledgement || CurrentState.File is MacroFile)
        {
            if (CurrentState.StartCode is not null)
            {
                startCode = CurrentState.StartCode;
                CurrentState.StartCode = null;
            }

            if (CurrentState.File is MacroFile macro)
            {
                using (macro.Lock(_lifetime.ApplicationStopping))
                {
                    if (startCode is not null && abortAll)
                    {
                        // Wait for the macro to be fully cancelled and then cancel the code that started it.
                        // Copy the code to a local first because startCode is shared across loop iterations
                        // and must be reset here so lower stack levels cannot complete it a second time
                        Code codeToComplete = startCode;
                        _ = macro.WaitForFinishAsync().ContinueWith(async task =>
                        {
                            try
                            {
                                await task;
                            }
                            finally
                            {
                                _codeProcessor.CodeCompleted(codeToComplete);
                            }
                        }, TaskContinuationOptions.RunContinuationsAsynchronously);
                        startCode = null;
                    }

                    // Abort the macro file
                    macro.Abort();
                }
                macroAborted = true;
            }
            else if (startCode is not null)
            {
                // This is a message prompt. Cancel the code that started it
                _codeProcessor.CancelCode(startCode);
                startCode = null;
            }

            // Pop the stack
            Pop();
            if (startCode is not null && abortAll)
            {
                _logger.LogDebug("==> Unfinished starting code: {Code}", startCode);
            }

            // Stop if only a single file is supposed to be aborted
            if (!abortAll && macroAborted)
            {
                break;
            }
        }

        if (abortAll)
        {
            // Cancel pending codes and requests
            InvalidateRegular();

            // RRF's own abort of all files of the current epoch is answered with InvalidateChannel(e+1), which also opens RRF's gate (CE-15, CE-24).
            // An untagged one is handled as in v7, without an answer (V7-46)
            if (tagged)
            {
                _allFilesAborted = true;
            }
        }
        else
        {
            // Invalidate remaining buffered codes from the last macro file
            foreach (Code bufferedCode in BufferedCodes)
            {
                _codeProcessor.CancelCode(bufferedCode);
            }
            BufferedCodes.Clear();
            BytesBuffered = 0;

            // If only the last file was closed (e.g. from M99), carry on with the execution of the code that started it
            if (startCode is not null)
            {
                BytesBuffered += startCode.BinarySize;
                BufferedCodes.Insert(0, startCode);
                _logger.LogDebug("==> Resuming unfinished starting code: {Code}", startCode);
            }
        }

        // Abort the file print if necessary
        if ((Channel is CodeChannel.File or CodeChannel.File2) && (abortAll || !macroAborted))
        {
            using (_jobProcessor.Lock())
            {
                _jobProcessor.Abort();
            }
        }
    }

    /// <summary>
    /// Abort all files asynchronously
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public async Task AbortAllFilesAsync()
    {
        // Clean up the stack
        Code? startCode = null;
        while (CurrentState.WaitingForAcknowledgement || CurrentState.File is MacroFile)
        {
            if (CurrentState.StartCode is not null)
            {
                startCode = CurrentState.StartCode;
                CurrentState.StartCode = null;
            }

            if (CurrentState.File is MacroFile macro)
            {
                using (await macro.LockAsync(_lifetime.ApplicationStopping))
                {
                    // Resolve potential start codes when the macro file finishes. Copy the code to a local
                    // first because startCode is shared across loop iterations and must be reset here so
                    // lower stack levels cannot complete it a second time
                    if (startCode is not null)
                    {
                        Code codeToComplete = startCode;
                        _ = macro.WaitForFinishAsync().ContinueWith(async task =>
                        {
                            try
                            {
                                await task;
                            }
                            finally
                            {
                                _codeProcessor.CodeCompleted(codeToComplete);
                            }
                        }, TaskContinuationOptions.RunContinuationsAsynchronously);
                        startCode = null;
                    }

                    // Abort the macro file
                    macro.Abort();
                }
            }
            else if (startCode is not null)
            {
                // Cancel the code that started the blocking message prompt
                _codeProcessor.CancelCode(startCode);
                startCode = null;
            }

            // Pop the stack
            await PopAsync();
            if (startCode is not null)
            {
                _logger.LogDebug("==> Unfinished starting code: {Code}", startCode);
            }
        }

        // Cancel pending codes and requests
        _allFilesAborted = _linkAdapter.ProtocolVersion >= 3;
        InvalidateRegular();

        // Abort the job files if necessary
        if (Channel is CodeChannel.File or CodeChannel.File2)
        {
            using (await _jobProcessor.LockAsync())
            {
                _jobProcessor.Abort();
            }
        }
    }

    /// <summary>
    /// Called when a resource has been locked
    /// </summary>
    public void ResourceLocked()
    {
        foreach (StackState state in Stack)
        {
            if (state.LockRequests.TryDequeue(out LockMovementRequest? item))
            {
                item.Resolve(true);
                return;
            }
        }
        _logger.LogError("Received a lock confirmation for a non-existent request!");
    }

    /// <summary>
    /// Resolve pending comment codes
    /// </summary>
    private void ResolveCommentCodes()
    {
        while (BufferedCodes.Count > 0 && BufferedCodes[0].Type == CodeType.Comment)
        {
            Code code = BufferedCodes[0];
            BytesBuffered -= code.BinarySize;
            BufferedCodes.RemoveAt(0);

            code.Result = new Message();
            _codeProcessor.CodeCompleted(code);
        }
    }

    /// <summary>
    /// Process code replies that could not be interpreted immediately
    /// </summary>
    private void ResolvePendingReplies()
    {
        while (BufferedCodes.Count > 0 && PendingReplies.TryDequeue(out (MessageTypeFlags Flags, string Reply, ushort Tag) reply))
        {
            HandleReply(reply.Flags, reply.Reply, reply.Tag);
        }
    }

    /// <summary>
    /// Process pending requests on this channel
    /// </summary>
    /// <param name="connection">State of the channel epoch for the current connection</param>
    internal void Spin(EpochConnection connection)
    {
        if (_allFilesAbortResent)
        {
            // The copy of the notification about aborted files goes out with the next transfer, whose data RRF writes before it reads the copy
            _allFilesAbortResent = false;
        }
        else if (_allFilesAbortedSent)
        {
            // The data RRF sent with the notification about aborted files has been processed, its answer comes with the next transfer
            _allFilesAbortedSent = false;
        }
        else
        {
            // RRF has processed the notification about aborted files
            _allFilesAbortUnconfirmed = false;
        }

        // 1. Whole line comments and pending replies. An RRF that tags never refuses InvalidateChannel (CE-7), so nothing waits for its confirmation
        ResolveCommentCodes();
        ResolvePendingReplies();
        if (_allFilesAbortUnconfirmed && !connection.PeerTags && UseAbortWindows)
        {
            return;
        }

        // 2. Abort requests. InvalidateChannel carries the new epoch, which is the current one once it is written (CE-13). Nothing of the channel goes
        // out before it: the abort has failed the lock requests of the old epoch, so one that is queued here came after the abort. Written first, RRF
        // would drop it behind its gate (CE-24) or answer it with a tag that is stale already, and this channel would wait for the answer forever
        if (_allFilesAborted)
        {
            byte epoch = ChannelEpoch.Next(Epoch);
            ushort tag = connection.Tag(epoch);
            if (_linkAdapter.WriteInvalidateChannel(Channel, tag))
            {
                connection.Written(tag);
                ChangeEpoch(epoch);
                _allFilesAbortedSent = _allFilesAbortUnconfirmed = true;
                _allFilesAborted = false;
            }
            return;
        }

        // 3. Lock/Unlock requests
        if (CurrentState.LockRequests.TryPeek(out LockMovementRequest? lockRequest))
        {
            ushort tag = connection.Tag(Epoch);
            if (lockRequest.IsLockRequest)
            {
                if (!lockRequest.IsLockRequested && _linkAdapter.WriteLockAllMovementSystemsAndWaitForStandstill(Channel, tag))
                {
                    connection.Written(tag);
                    lockRequest.IsLockRequested = true;
                }
                return;
            }

            if (_linkAdapter.WriteUnlock(Channel, tag))
            {
                connection.Written(tag);
                lockRequest.Resolve(true);
                CurrentState.LockRequests.Dequeue();
                // Resources unlocked; carry on
            }
            else
            {
                return;
            }
        }

        // 4. Macro files (must come before any other code unless the stack state is being cloned)
        if (CurrentState.File is MacroFile || CurrentState.MacroError)
        {
            // Tell RRF as quickly as possible about the new macro being started
            if (CurrentState.File is MacroFile macro && macro.JustStarted)
            {
                ushort tag = connection.Tag(Epoch);
                macro.JustStarted = (_linkAdapter.ProtocolVersion >= 3) && !_linkAdapter.WriteMacroStarted(Channel, tag);
                if (!macro.JustStarted)
                {
                    connection.Written(tag);
                }
                return;
            }

            // Check if the macro file has finished
            if (CurrentState.File is MacroFile { WasStarted: true, IsExecuting: false } || CurrentState.MacroError)
            {
                ushort tag = connection.Tag(Epoch);
                if (!CurrentState.MacroCompleted && _linkAdapter.WriteMacroCompleted(Channel, CurrentState.MacroError, tag))
                {
                    connection.Written(tag);
                    CurrentState.MacroCompleted = true;
                    if (_linkAdapter.ProtocolVersion >= 3)
                    {
                        if (CurrentState.MacroError)
                        {
                            // In newer protocol versions we don't expect a response because RRF will be waiting in a semaphore
                            Code? startCode = CurrentState.StartCode;
                            if (startCode is not null)
                            {
                                BytesBuffered += startCode.BinarySize;
                                BufferedCodes.Insert(0, startCode);
                                CurrentState.StartCode = null;
                                ResolvePendingReplies();
                            }

                            // Macro has finished, pop the stack
                            Pop();
                            if (startCode is not null)
                            {
                                _logger.LogDebug("==> Unfinished starting code: {Code}", startCode);
                            }
                        }
                    }
                    else
                    {
                        // Wait for a response first if an older firmware version is used, then pop the stack
                        return;
                    }
                }
                else
                {
                    // Still waiting for acknowledgement or failed to write macro complete message, try again ASAP
                    return;
                }
            }
        }

        // 5. Suspended codes being resumed (may include priority and macro codes)
        while (CurrentState.SuspendedCodes.TryPeek(out Code? suspendedCode))
        {
            if (BufferCode(suspendedCode, connection))
            {
                _logger.LogDebug("-> Resumed suspended code");
                CurrentState.SuspendedCodes.Dequeue();
            }
            else
            {
                return;
            }
        }

        // 6. Pending codes
        while (CurrentState.PendingCodes.Reader.TryPeek(out Code? pendingCode))
        {
            if (BufferCode(pendingCode, connection))
            {
                CurrentState.PendingCodes.Reader.TryRead(out _);
            }
            else
            {
                return;
            }
        }

        // 7. Flush requests
        if (BufferedCodes.Count == 0)
        {
            if (CurrentState.FlushRequests.TryDequeue(out TaskCompletionSource<bool>? flushRequest))
            {
                flushRequest.SetResult(true);
                return;
            }
            CurrentState.SetBusy(false);
        }

        // Log untracked code replies. One of work that DSF has ended since it was queued is no out-of-order reply
        while (PendingReplies.TryDequeue(out (MessageTypeFlags Flags, string Reply, ushort Tag) reply))
        {
            if (!HandleStaleReply(reply.Flags, reply.Reply, reply.Tag))
            {
                _logger.LogWarning("Pending out-of-order reply: '{Reply}'", reply.Reply);
            }
        }
    }

    /// <summary>
    /// Perform a regular code that was requested from the firmware
    /// </summary>
    /// <param name="code">Code to perform</param>
    /// <param name="tag">Tag of the DoCode request, which the chunks of the result carry (CE-6)</param>
    public void DoFirmwareCode(string code, ushort tag)
    {
        _logger.LogDebug("Running code from firmware '{Code}'", code);

        Commands.SimpleCode simpleCode = _commandFactory.Create<Commands.SimpleCode>();
        simpleCode.Code = code;
        simpleCode.Channel = Channel;
        simpleCode.IsFromFirmware = true;
        simpleCode.FirmwareTag = tag;
        simpleCode.ExecuteAsynchronously = true;
        _ = simpleCode.ExecuteAsync();
    }

    /// <summary>
    /// Store a pending code for transmission to RepRapFirmware
    /// </summary>
    /// <param name="pendingCode">Code to transfer</param>
    /// <param name="connection">State of the channel epoch for the current connection</param>
    /// <returns>True if the code could be buffered</returns>
    private bool BufferCode(Code pendingCode, EpochConnection connection)
    {
        try
        {
            // Figure out how much space this code needs
            if (pendingCode.Stage != PipelineStage.Firmware)
            {
                pendingCode.BinarySize = Consts.BufferedCodeHeaderSize + Protocol.Writer.GetCodeSize(pendingCode, _settings.MaxCodeBufferSize, _linkAdapter.ProtocolVersion);

                pendingCode.Stage = PipelineStage.Firmware;
            }

            // Don't send cancelled codes to the firmware
            if (pendingCode.CancellationToken.IsCancellationRequested)
            {
                _codeProcessor.CancelCode(pendingCode);
                return true;
            }

            // Try to send it to RepRapFirmware. Only the next code of a channel without buffered codes may use the spare room
            int maxBufferSpace = _linkInterface.GetMaxBufferSpacePerChannel(_jobProcessor.NumJobStreams);
            ushort tag = connection.Tag(Epoch);
            if ((BytesBuffered == 0 || BytesBuffered + pendingCode.BinarySize <= maxBufferSpace) &&
                _linkInterface.SendCode(pendingCode, pendingCode.BinarySize, BytesBuffered == 0, tag))
            {
                connection.Written(tag);
                BytesBuffered += pendingCode.BinarySize;
                BufferedCodes.Add(pendingCode);
                _logger.LogDebug("Sent {Code}, remaining space {BytesRemaining}, needed {BytesNeeded}", pendingCode, maxBufferSpace - BytesBuffered, pendingCode.BinarySize);
                return true;
            }
            return false;
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Failed to buffer code {Code}", pendingCode);
            _codeProcessor.CancelCode(pendingCode, e);
            return true;
        }
    }

    /// <summary>
    /// Handle a G-code reply
    /// </summary>
    /// <param name="flags">Message flags</param>
    /// <param name="reply">Code reply</param>
    /// <param name="tag">Tag of the reply, the epoch of the code that produced it (CE-11). A reply of work that DSF has ended completes no code; it is
    /// shown as a message instead (decision of 2026-11-10, <see cref="ShowStaleReply"/>), one whose epoch is ahead is dropped (CE-14)</param>
    /// <returns>Whether the reply could be processed</returns>
    public bool HandleReply(MessageTypeFlags flags, string reply, ushort tag = 0)
    {
        if (HandleStaleReply(flags, reply, tag))
        {
            return true;
        }

        // Replies are not meant for comment codes, resolve them separately
        ResolveCommentCodes();

        // Deal with codes being executed
        if (BufferedCodes.Count > 0)
        {
            int codeSize = BufferedCodes[0].BinarySize;
            if (HandleCodeReply(BufferedCodes[0], flags, reply))
            {
                BytesBuffered -= codeSize;
                BufferedCodes.RemoveAt(0);
            }
            return true;
        }

        // Check for a final empty reply for the current macro file being closed
        if (CurrentState.MacroCompleted)
        {
            if (_linkAdapter.ProtocolVersion < 3 && string.IsNullOrEmpty(reply))
            {
                MacroFileClosed();
                return true;
            }
            else if (_linkAdapter.ProtocolVersion >= 3)
            {
                if (_waitingForPrintCompletion && string.IsNullOrEmpty(reply) && CurrentState.StartCode is null)
                {
                    // RRF writes the final reply before the request closing stop.g, so it may arrive first
                    _waitingForPrintCompletion = false;
                    return true;
                }
                PendingReplies.Enqueue((flags, reply, tag));
                return true;
            }
        }

        // Check for message boxes being closed
        if (CurrentState.WaitingForAcknowledgement)
        {
            if (_linkAdapter.ProtocolVersion < 3 && string.IsNullOrEmpty(reply))
            {
                MessageAcknowledged();
                return true;
            }
            else if (_linkAdapter.ProtocolVersion >= 3)
            {
                PendingReplies.Enqueue((flags, reply, tag));
                return true;
            }
        }

        // Check for the final reply confirming that the print has been stopped
        if (_waitingForPrintCompletion && string.IsNullOrEmpty(reply))
        {
            _waitingForPrintCompletion = false;
            return true;
        }

        // Unless this message comes from the file or code queue it is out-of-order...
        if (Channel != CodeChannel.Queue)
        {
            OutOfOrderReplies++;
            _logger.LogWarning("Out-of-order reply (flags {Flags}): '{Reply}'", flags, reply);
        }
        return false;
    }

    /// <summary>
    /// Check if a reply belongs to work that DSF has ended (CE-14) and show it as a message then, unless its epoch is ahead
    /// </summary>
    /// <param name="flags">Message flags</param>
    /// <param name="reply">Reply</param>
    /// <param name="tag">Tag of the reply</param>
    /// <returns>Whether the reply is stale and has been dealt with</returns>
    private bool HandleStaleReply(MessageTypeFlags flags, string reply, ushort tag)
    {
        if (!IsStale(tag, Protocol.FirmwareRequests.Request.Message))
        {
            return false;
        }
        if (!IsAhead(ChannelEpoch.EpochOf(tag)))
        {
            ShowStaleReply(flags, reply, tag);
        }
        return true;
    }

    /// <summary>
    /// Incomplete reply of work that DSF has ended (PushFlag), with its flags and its tag. It is kept apart from the incomplete reply of the current code
    /// (<see cref="_lastPartialMessage"/>)
    /// </summary>
    private string? _stalePartialReply;
    private MessageTypeFlags _stalePartialReplyType;
    private ushort _stalePartialReplyTag;

    /// <summary>
    /// Show a reply of work that DSF has ended, e.g. of a code that survived the abort at the base level in RRF, as a message that belongs to no code,
    /// with the type of its flags, as a generic message is shown (LinkService.OutputGenericMessage). An empty one is dropped. Its parts are assembled
    /// apart from the reply of the current code, and only parts with the same tag are joined: the part of a reply that never completes is discarded
    /// by the next stale reply of another epoch or by the end of the connection
    /// </summary>
    /// <param name="flags">Message flags</param>
    /// <param name="reply">Reply</param>
    /// <param name="tag">Tag of the reply</param>
    private void ShowStaleReply(MessageTypeFlags flags, string reply, ushort tag)
    {
        if (_stalePartialReply is not null)
        {
            if (_stalePartialReplyTag == tag)
            {
                reply = _stalePartialReply + reply;
                flags |= _stalePartialReplyType & ~MessageTypeFlags.PushFlag;
            }
            else
            {
                _logger.LogDebug("Discarding incomplete reply of epoch {Epoch}: '{Reply}'", ChannelEpoch.EpochOf(_stalePartialReplyTag), _stalePartialReply);
            }
            _stalePartialReply = null;
        }

        if (flags.HasFlag(MessageTypeFlags.PushFlag))
        {
            _stalePartialReply = reply;
            _stalePartialReplyType = flags;
            _stalePartialReplyTag = tag;
            return;
        }

        if (!string.IsNullOrWhiteSpace(reply))
        {
            ShownStaleReplies++;
            MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                : MessageType.Success;
            _model.Output(type, reply.TrimEnd());
        }
    }

    /// <summary>
    /// Indicates if the firmware still has to confirm the end of the last print.
    /// RRF replies once more when it has run stop.g, and no code of ours is waiting for that reply
    /// </summary>
    private bool _waitingForPrintCompletion;

    /// <summary>
    /// Called when the print stopped notification has been sent to the firmware
    /// </summary>
    public void WaitForPrintCompletion() => _waitingForPrintCompletion = true;

    /// <summary>
    /// Hold the flags of the last incomplete code reply
    /// </summary>
    private MessageTypeFlags _lastPartialMessageType = MessageTypeFlags.NoDestinationMessage;

    /// <summary>
    /// Holds the last incomplete code reply
    /// </summary>
    private string? _lastPartialMessage;


    /// <summary>
    /// Process a firmware code reply
    /// </summary>
    /// <param name="code">Destination code</param>
    /// <param name="flags">Reply flags</param>
    /// <param name="reply">Reply</param>
    /// <returns>Whether the code has finished</returns>
    private bool HandleCodeReply(Code code, MessageTypeFlags flags, string reply)
    {
        if (!string.IsNullOrEmpty(_lastPartialMessage))
        {
            // Deal with incomplete replies
            reply = _lastPartialMessage + reply;
            flags |= _lastPartialMessageType & ~MessageTypeFlags.PushFlag;
            _lastPartialMessageType = MessageTypeFlags.NoDestinationMessage;
            _lastPartialMessage = null;
        }

        if (flags.HasFlag(MessageTypeFlags.PushFlag))
        {
            // Code reply is not complete yet
            _lastPartialMessageType |= flags;
            _lastPartialMessage = reply;
            return false;
        }

        if (code is not null)
        {
            // Code reply is complete, resolve the code
            MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                : MessageType.Success;
            if (code.Result is null)
            {
                code.Result = new Message(type, reply);
            }
            else
            {
                code.Result.Append(type, reply);
            }

            // Preserve the log level from the firmware reply flags
            code.ReplyLogLevel = (flags & MessageTypeFlags.LogOff) == MessageTypeFlags.LogOff ? EventLogLevel.Off
                : flags.HasFlag(MessageTypeFlags.LogWarn) ? EventLogLevel.Warn
                : flags.HasFlag(MessageTypeFlags.LogInfo) ? EventLogLevel.Info
                : EventLogLevel.Debug;

            _codeProcessor.CodeCompleted(code);
        }
        else
        {
            // Final output from a system macro
            MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                        : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                        : MessageType.Success;
            _model.Output(type, reply);
        }
        return true;
    }

    /// <summary>
    /// Wait for a message to be acknowledged
    /// </summary>
    public void WaitForAcknowledgement()
    {
        // Message box requests are not meant for comment codes, resolve them separately
        ResolveCommentCodes();

        // Figure out which code requested the message box
        if (!CurrentState.WaitingForAcknowledgement)
        {
            _logger.LogDebug("Waiting for acknowledgement");

            Code? startCode = null;
            if (BufferedCodes.Count > 0)
            {
                startCode = BufferedCodes[0];
                startCode.UpdateNextFilePosition();
                BytesBuffered -= startCode.BinarySize;
                BufferedCodes.RemoveAt(0);
            }

            StackState newState = Push();
            newState.StartCode = startCode;
            newState.WaitingForAcknowledgement = true;
            _isWaitingForAcknowledgment = true;
        }
    }

    /// <summary>
    /// Called when RepRapFirmware has closed the last macro file internally
    /// </summary>
    /// <param name="tagged">Whether RRF tagged the closal. A tagged one is decided by its tag (<see cref="IsStale"/>), the windows are for untagged ones (CE-13)</param>
    public void MacroFileClosed(bool tagged = false)
    {
        // The abort of all files has removed the macro from the stack already, and RRF does not report the files it closes for the abort
        if (!tagged && UseAbortWindows && (_allFilesAborted || _allFilesAbortedSent))
        {
            _logger.LogDebug("Macro file was closed before RRF got the abort of all files. Discarding closal.");
            return;
        }

        Code? startCode = CurrentState.StartCode;
        if (startCode is not null)
        {
            _logger.LogDebug("==> Unfinished starting code: {Code}", startCode);

            // Code has not finished yet, need a separate response for it
            BytesBuffered += startCode.BinarySize;
            BufferedCodes.Insert(0, startCode);
            CurrentState.StartCode = null;
            ResolvePendingReplies();
        }

        Pop();
    }

    /// <summary>
    /// Called when RRF could not process the notification about aborted files and the link adapter sends it again. RRF wrote the rest of
    /// this data after it had given up the pending macro request (SbcInterface.cpp:880-886), and it writes the data of the next transfer before
    /// it reads the copy, so macro requests and closals in both are void as well
    /// </summary>
    public void AllFilesAbortResent()
    {
        _allFilesAbortedSent = _allFilesAbortUnconfirmed = _allFilesAbortResent = true;
    }

    /// <summary>
    /// Called when a message has been acknowledged
    /// </summary>
    public void MessageAcknowledged()
    {
        if (CurrentState.WaitingForAcknowledgement)
        {
            _logger.LogDebug("Message acknowledged");

            Code? startCode = CurrentState.StartCode;
            if (startCode is not null)
            {
                BytesBuffered += CurrentState.StartCode!.BinarySize;
                BufferedCodes.Insert(0, CurrentState.StartCode);
                CurrentState.StartCode = null;
                ResolvePendingReplies();
            }

            Pop();
            if (startCode is not null)
            {
                _logger.LogDebug("==> Unfinished starting code: {Code}", startCode);
            }
        }
        else
        {
            _logger.LogError("Tried to acknowledge a message, but no acknowledgement is requested!");
        }
    }

    /// <summary>
    /// Attempt to start a file macro
    /// </summary>
    /// <param name="virtualFile">Requested name of the macro file</param>
    /// <param name="fromCode">Request comes from a real G/M/T-code</param>
    /// <param name="tagged">Whether RRF tagged the request. A tagged one is decided by its tag (<see cref="IsStale"/>), the windows are for untagged ones (CE-13)</param>
    /// <param name="connection">State of the channel epoch for the current connection, for the answer that refuses the request</param>
    internal void DoMacroFile(string virtualFile, bool fromCode, bool tagged, EpochConnection connection)
    {
        // Macro requests are not meant for comment codes, resolve them separately
        ResolveCommentCodes();

        // RRF ends a pending macro request with an error when it is notified about aborted files, so a request it sent before is void
        if (!tagged && UseAbortWindows && (_allFilesAborted || _allFilesAbortedSent))
        {
            _logger.LogDebug("Macro file {File} was requested before RRF got the abort of all files. Discarding request.", virtualFile);
            return;
        }

        // Cannot start system macro if something is still busy
        if (!fromCode && Stack.Count > 1)
        {
            _logger.LogWarning("System macro {File} is requested but the stack is not empty. Discarding request.", virtualFile);
            RefuseMacroRequest(connection);
            return;
        }

        // Figure out which code started the macro file
        Code? startCode = null;
        if (fromCode)
        {
            if (CurrentState.MacroCompleted)
            {
                _logger.LogInformation("Finished intermediate macro file {File}", CurrentState.File!.FilePath.Virtual);
                startCode = CurrentState.StartCode;
                CurrentState.StartCode = null;     // don't add it back to the buffered codes because it's about to be pushed on the stack again
                Pop();
            }
            else if (BufferedCodes.Count > 0)
            {
                startCode = BufferedCodes[0];
                BytesBuffered -= startCode.BinarySize;
                BufferedCodes.RemoveAt(0);
            }
        }
        else if (Stack.Count > 1)
        {
            _logger.LogWarning("System macro {File} is requested but the stack is not empty. Discarding request.", virtualFile);
            RefuseMacroRequest(connection);
            return;
        }

        // Try to locate the macro file. A path that cannot be resolved (it leaves its volume or names an unknown drive) cannot be opened either
        MacroFile? macro = null;
        try
        {
            string physicalFile = _filePathResolver.ToPhysical(virtualFile, FileDirectory.System);
            macro = _macroFileFactory.CreateMacro(virtualFile, physicalFile, Channel, startCode, startCode?.SourceConnection ?? 0);
        }
        catch (ArgumentException e)
        {
            _logger.LogError(e, "Failed to start macro file {File}", virtualFile);
        }

        StackState newState = Push(macro);
        newState.StartCode = startCode;
        if (macro is not null)
        {
            // Start it
            if (startCode is not null)
            {
                startCode.UpdateNextFilePosition();
                _logger.LogDebug("==> Starting code {Code}", startCode);
            }
            macro.Start();
        }
        else
        {
            // Report back to RRF that the file could not be opened
            newState.MacroError = true;
        }
    }

    /// <summary>
    /// Tell RRF that a requested system macro cannot be started
    /// </summary>
    /// <param name="connection">State of the channel epoch for the current connection</param>
    private void RefuseMacroRequest(EpochConnection connection)
    {
        ushort tag = connection.Tag(Epoch);
        if (_linkAdapter.WriteMacroCompleted(Channel, true, tag))
        {
            connection.Written(tag);
        }
    }

    /// <summary>
    /// Called when the print has been paused on the file channel
    /// </summary>
    public void PrintPaused()
    {
        // Invalidate pending requests
        InvalidateRegular();

        // Clear macros. When we get here, RRF has done the same
        while (CurrentState.File is MacroFile)
        {
            Pop();
        }

        // Invalidate everything else
        InvalidateRegular();
    }

    /// <summary>
    /// Invalidate buffered and regular codes + requests
    /// </summary>
    public void InvalidateRegular()
    {
        foreach (Code bufferedCode in BufferedCodes)
        {
            _codeProcessor.CancelCode(bufferedCode);
        }
        BufferedCodes.Clear();
        BytesBuffered = 0;

        foreach (StackState state in Stack)
        {
            if (!state.WaitingForAcknowledgement && (state.File is not MacroFile macro || macro.IsPausable))
            {
                while (state.LockRequests.TryDequeue(out LockMovementRequest? lockRequest))
                {
                    lockRequest.Resolve(false);
                }

                while (state.SuspendedCodes.TryDequeue(out Code? suspendedCode))
                {
                    _codeProcessor.CancelCode(suspendedCode);
                }

                while (state.PendingCodes.Reader.TryRead(out Code? pendingCode))
                {
                    pendingCode.Stage = PipelineStage.Firmware;
                    _codeProcessor.CancelCode(pendingCode);
                }

                while (state.FlushRequests.TryDequeue(out TaskCompletionSource<bool>? source))
                {
                    source.SetResult(false);
                }
                state.SetBusy(false);
            }
        }
    }

    /// <summary>
    /// Invalidate every request and buffered code on this channel
    /// </summary>
    /// <returns>If any resource has been invalidated</returns>
    public void Invalidate()
    {
        // Invalidate the stack
        do
        {
            while (CurrentState.LockRequests.TryDequeue(out LockMovementRequest? lockRequest))
            {
                lockRequest.Resolve(false);
            }

            while (CurrentState.SuspendedCodes.TryDequeue(out Code? suspendedCode))
            {
                _codeProcessor.CancelCode(suspendedCode);
            }

            while (CurrentState.PendingCodes.Reader.TryRead(out Code? pendingCode))
            {
                pendingCode.Stage = PipelineStage.Firmware;
                _codeProcessor.CancelCode(pendingCode);
            }

            while (CurrentState.FlushRequests.TryDequeue(out TaskCompletionSource<bool>? source))
            {
                source.SetResult(false);
            }
            CurrentState.SetBusy(false);

            if (Stack.Count == 1)
            {
                break;
            }
            Pop();
        }
        while (true);

        // Clear codes being processed
        foreach (Code bufferedCode in BufferedCodes)
        {
            _codeProcessor.CancelCode(bufferedCode);
        }
        BufferedCodes.Clear();
        BytesBuffered = 0;
        _waitingForPrintCompletion = false;

        // Clear codes that are still pending but have not been fed into the SPI interface yet
        _codeProcessor.CancelPending(Channel);
        _allFilesAborted = _allFilesAbortedSent = _allFilesAbortUnconfirmed = _allFilesAbortResent = false;
    }
}
