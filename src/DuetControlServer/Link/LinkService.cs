using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using DuetSharedLibrary;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuetControlServer.Link;

/// <summary>
/// This class accesses RepRapFirmware via SPI and deals with general communication
/// </summary>
/// <param name="channels">Channel manager</param>
/// <param name="eventLogger">Event logger</param>
/// <param name="filePathResolver">File path resolver</param>
/// <param name="jobProcessor">Job processor</param>
/// <param name="linkAdapter">Firmware link adapter</param>
/// <param name="linkInterface">Link interface</param>
/// <param name="model">Object model</param>
/// <param name="lifetime">Host application lifetime</param>
/// <param name="logger">Logger</param>
/// <param name="settings">Settings</param>
/// <param name="systemdNotifier">Notifier of systemd, registered by UseSystemd only when DCS runs as a systemd service</param>
public sealed class LinkService(
    Channel.Manager channels,
    EventLogger eventLogger,
    FilePathResolver filePathResolver,
    JobProcessor jobProcessor,
    ILinkAdapter linkAdapter,
    LinkInterface linkInterface,
    Model.ObjectModel model,
    IHostApplicationLifetime lifetime,
    ILogger<LinkService> logger,
    IOptions<Settings> settings,
    ISystemdNotifier? systemdNotifier = null) : BackgroundService
{
    /// <summary>
    /// Open files requested by the firmware
    /// </summary>
    private readonly Dictionary<uint, FileStream> _openFiles = [];

    /// <summary>
    /// Handle counter for open files
    /// </summary>
    public uint _openFileHandleCounter = Consts.NoFileHandle;

    /// <summary>
    /// Perform the firmware update internally
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    private void PerformFirmwareUpdate(CancellationToken cancellationToken = default)
    {
        using (model.AccessReadWrite(cancellationToken))
        {
            model.State.Status = MachineStatus.Updating;
        }

        // Get the CRC16 checksum of the firmware binary
        ushort crc16 = CRC16.Calculate(linkInterface.FirmwareStream!);

        // Send the IAP binary to the firmware. Cancellation is safe at this stage
        logger.LogInformation("Sending IAP binary");
        bool dataSent;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            dataSent = linkAdapter.WriteIapSegment(linkInterface.IapStream!, cancellationToken);
            if (logger.IsEnabled(LogLevel.Debug))
            {
                Console.Write('.');
            }
        }
        while (dataSent);
        if (logger.IsEnabled(LogLevel.Debug))
        {
            Console.WriteLine();
        }

        // Start the IAP binary. This is the point of no return -- after this,
        // the board is running IAP and we must complete the firmware transfer
        // or the board will need manual recovery
        // The firmware length is sent as part of the USB handshake so IAP knows
        // exactly how many bytes to expect (SPI ignores this)
        uint firmwareLength = (uint)linkInterface.FirmwareStream!.Length;
        linkAdapter.StartIap(firmwareLength, cancellationToken);

        // From here on, do not honor the cancellation token for data transfer
        // Interrupting a flash-in-progress would brick the board
        // Only check cancellation between CRC retries as a last resort
        int numRetries = 0;
        do
        {
            if (numRetries != 0)
            {
                // Check cancellation between retries -- if DSF is being shut down
                // and the board is unresponsive, there's no point in retrying
                if (cancellationToken.IsCancellationRequested)
                {
                    eventLogger.LogOutput(MessageType.Error, "Firmware update cancelled during retry. The board may need manual recovery.");
                    logger.LogError("Firmware update cancelled during CRC retry");
                    throw new OperationCanceledException("Firmware update cancelled during retry");
                }
                logger.LogError("Firmware checksum verification failed");
            }

            logger.LogInformation("Updating RepRapFirmware");
            linkInterface.FirmwareStream!.Seek(0, SeekOrigin.Begin);

            try
            {
                while (linkAdapter.FlashFirmwareSegment(linkInterface.FirmwareStream))
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        Console.Write('.');
                    }
                }
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    Console.WriteLine();
                }
            }
            catch (Exception e)
            {
                eventLogger.LogOutput(MessageType.Error, "Failed to update firmware. Please install it manually.");
                logger.LogError(e, "Failed to update firmware");
                throw;
            }

            logger.LogInformation("Verifying checksum");
        }
        while (!linkAdapter.VerifyFirmwareChecksum(linkInterface.FirmwareStream.Length, crc16) && ++numRetries < 3);

        if (numRetries == 3)
        {
            // Failed to flash the firmware. Do not throw an OperationCanceledException here,
            // it would be treated as a code cancellation and the reason would be lost
            eventLogger.LogOutput(MessageType.Error, "Could not update firmware after 3 attempts. Please install it manually.");
            throw new IOException("Checksum verification failed after 3 attempts");
        }

        // Wait for the IAP binary to restart the controller
        linkAdapter.WaitForIapReset();
        logger.LogInformation("Firmware update successful");
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Initialize the link interface
        linkAdapter.Connect(cancellationToken);

        // Run this service
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Start a thread that performs the communication with the firmware
    /// </summary>
    /// <remarks>
    /// This effectively starts a thread with higher priority in order to ensure
    /// that the communication with the controller is not blocked by other tasks
    /// </remarks>
    /// <param name="stoppingToken">Cancellation token</param>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread wrapper = new(() =>
        {
            try
            {
                Execute(stoppingToken);
                tcs.SetResult();
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    if (ae.InnerException is OperationCanceledException)
                    {
                        if (stoppingToken.IsCancellationRequested)
                        {
                            tcs.SetResult();
                        }
                        else
                        {
                            tcs.SetCanceled();
                        }
                    }
                    else
                    {
                        tcs.SetException(ae.InnerException!);
                    }
                }
                else if (e is OperationCanceledException)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        tcs.SetResult();
                    }
                    else
                    {
                        tcs.SetCanceled();
                    }
                }
                else
                {
                    tcs.SetException(e);
                }
            }
        })
        {
            Priority = ThreadPriority.Highest,
            IsBackground = true
        };
        wrapper.Start();
        return tcs.Task;
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        // A firmware update must not be cut off, else the board may be left without firmware. Let a running one finish first, regardless of the
        // shutdown timeout of the host, and keep the lock until the transfer thread has ended, so that no update starts meanwhile
        using (await WaitForFirmwareUpdateAsync())
        {
            // Cancel the file being printed
            using (await jobProcessor.LockAsync(stoppingToken))
            {
                jobProcessor.Abort();
            }

            // Shut down the link subsystem
            await linkInterface.InvalidateAsync(stoppingToken);

            // Shut down this service. This terminates the transfer thread, which may still be serving file
            // requests, so the open files must not be closed before this call
            await base.StopAsync(stoppingToken);
        }

        // Close all the files
        foreach (var kv in _openFiles)
        {
            await kv.Value.DisposeAsync();
        }
        _openFiles.Clear();
    }

    /// <summary>
    /// Maximum time a stop waits for a firmware update to finish. A flash of the main board took 11 s on the lab printer, and an IAP program
    /// that stops answering ends the update after Consts.IapTimeout, so this is only reached if the update hangs in DCS itself.
    /// Internal because no user has to change it and a setting would add a config.json key for a test
    /// </summary>
    internal TimeSpan FirmwareUpdateStopTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Interval in which DCS asks systemd for more time while a stop waits for a firmware update, and the time it asks for each time.
    /// systemd kills DCS TimeoutStopSec (15 s) after the stop began unless it gets EXTEND_TIMEOUT_USEC within the interval it asked for
    /// (sd_notify(3) since systemd 236, Debian bookworm has 252 and trixie 257; Type=notify makes NotifyAccess=main)
    /// </summary>
    private static readonly TimeSpan SystemdExtendInterval = TimeSpan.FromSeconds(5), SystemdExtendTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Wait for a firmware update to finish, at most <see cref="FirmwareUpdateStopTimeout"/>, and ask systemd for more time while it runs.
    /// The transfer thread holds the lock while it updates the firmware and otherwise only to check for an update request (see <see cref="Execute"/>)
    /// </summary>
    /// <returns>Lock of the firmware updates, or null if the update did not finish in time</returns>
    private async Task<AsyncLock.Releaser?> WaitForFirmwareUpdateAsync()
    {
        using CancellationTokenSource timeoutCts = new(FirmwareUpdateStopTimeout);
        Task<AsyncLock.Releaser> lockTask = linkInterface.FirmwareUpdateLock.LockAsync(timeoutCts.Token).AsTask();
        bool waiting = false;
        while (!lockTask.IsCompleted && await Task.WhenAny(lockTask, Task.Delay(SystemdExtendInterval)) != lockTask)
        {
            if (!waiting)
            {
                logger.LogWarning("Waiting for the firmware update to finish before stopping");
                waiting = true;
            }
            systemdNotifier?.Notify(new ServiceState($"EXTEND_TIMEOUT_USEC={(long)SystemdExtendTimeout.TotalMicroseconds}"));
        }

        try
        {
            return await lockTask;
        }
        catch (OperationCanceledException)
        {
            logger.LogError("The firmware update did not finish within {Timeout}, stopping anyway", FirmwareUpdateStopTimeout);
            return null;
        }
    }

    /// <summary>
    /// Perform communication with the RepRapFirmware controller over SPI
    /// </summary>
    /// <param name="stoppingToken">Cancellation token</param>
    private void Execute(CancellationToken stoppingToken)
    {
        // Emergency stop that went out with the last transfer. RRF carries it out as soon as it reads it, but the data RRF sent
        // with the same transfer was written before, so the stop is complete only once that data has been processed
        TaskCompletionSource? transferredHaltRequest = null;
        do
        {
            // A full transfer has completed, the one of Connect at first, and its data of RRF is processed below
            _connection.Transferred();

            bool skipChannels = false;
            TaskCompletionSource? writtenHaltRequest = null;
            using (linkInterface.FirmwareActionLock.Lock(stoppingToken))
            {
                // Check if an emergency stop has been requested. It is written first and transferred as soon as the data RRF sent before it has been processed
                if (linkInterface.FirmwareHaltRequest is not null)
                {
                    ushort tag = _connection.Tag();
                    if (linkAdapter.WriteEmergencyStop(tag))
                    {
                        _connection.Written(tag);
                        logger.LogWarning("Emergency stop");
                        writtenHaltRequest = linkInterface.FirmwareHaltRequest;
                        linkInterface.FirmwareHaltRequest = null;
                    }
                    skipChannels = true;
                }

                // Check if a firmware reset has been requested
                if (linkInterface.FirmwareResetRequest is not null)
                {
                    Invalidate();
                    ushort tag = _connection.Tag();
                    if (linkAdapter.WriteReset(tag))
                    {
                        _connection.Written(tag);
                        logger.LogWarning("Resetting controller");
                        linkAdapter.PerformFullTransfer(cancellationToken: stoppingToken);
                        linkInterface.FirmwareResetRequest.SetResult();
                        linkInterface.FirmwareResetRequest = null;

                        // RRF performs an emergency stop before it resets
                        transferredHaltRequest?.SetResult();
                        writtenHaltRequest?.SetResult();
                        break;
                    }
                    skipChannels = true;
                }
            }

            // Check if a firmware update is supposed to be performed
            using (linkInterface.FirmwareUpdateLock.Lock(stoppingToken))
            {
                if (linkInterface.IapStream is not null && linkInterface.FirmwareStream is not null)
                {
                    Invalidate();

                    // RRF performs an emergency stop when it receives the first IAP segment
                    transferredHaltRequest?.SetResult();
                    writtenHaltRequest?.SetResult();

                    try
                    {
                        PerformFirmwareUpdate(lifetime.ApplicationStopped);
                        linkInterface.FirmwareUpdateRequest?.SetResult();
                    }
                    catch (Exception e)
                    {
                        // Pass the failure to the M997 handler, which reports it and stops DCS.
                        // Rethrowing would stop the host before the error reply could go out.
                        // The adapters report timeouts as OperationCanceledException, which M997 would take for a
                        // cancellation of its own and drop the reason, so only a stopping DCS is passed on as one
                        linkInterface.FirmwareUpdateRequest?.SetException((e is OperationCanceledException && !lifetime.ApplicationStopped.IsCancellationRequested) ? new IOException(e.Message, e) : e);
                    }
                    linkInterface.FirmwareUpdateRequest = null;

                    linkInterface.IapStream = linkInterface.FirmwareStream = null;
                    break;
                }
            }

            // Invalidate data if a controller reset has been performed
            if (linkAdapter.HadReset())
            {
                Invalidate();
                eventLogger.LogOutput(MessageType.Warning, "Connection to controller has been reset");

                // The capabilities of the link end with the connection, and so do the epochs of the channels. The first packet of the next connection,
                // whatever its type, carries the first marker, on which RRF sets its epochs to 0 as well (CE-4)
                _queryLinkCapabilities = true;
                _connection = new EpochConnection();
                foreach (Channel.Processor channel in channels)
                {
                    using (channel.Lock())
                    {
                        channel.ResetEpoch();
                    }
                }
            }

            // Activate the capabilities of the link that RRF offers
            UpdateLinkCapabilities();

            // Check for changes of the print status
            bool printStopped = false;
            using (linkInterface.PrintStateLock.Lock(stoppingToken))
            {
                ushort tag = _connection.Tag();
                if (linkInterface.SetPrintInfoRequest is not null && linkAdapter.WritePrintFileInfo(model.Job.File, tag))
                {
                    // The packet providing file info has be sent first because it includes a time_t value that must reside on a 64-bit boundary!
                    _connection.Written(tag);
                    linkInterface.SetPrintInfoRequest.SetResult();
                    linkInterface.SetPrintInfoRequest = null;
                }
                else
                {
                    if (linkInterface.StopPrintRequest is not null && linkAdapter.WritePrintStopped(linkInterface.StopPrintReason, tag))
                    {
                        _connection.Written(tag);
                        // Only a print that really ran to the end makes RRF execute stop.g and reply once more. A simulation exits
                        // early in RRF's StopPrint, and after M0/M1/M2 it has run stop.g for that code long before this notification
                        printStopped = (linkInterface.StopPrintReason == PrintStoppedReason.NormalCompletion) && !jobProcessor.IsSimulating && !jobProcessor.IsStoppedByCode;
                        linkInterface.StopPrintRequest.SetResult();
                        linkInterface.StopPrintRequest = null;
                    }
                }
            }

            if (printStopped)
            {
                // Only the primary file channel runs stop.g, forked streams exit early in RRF's StopPrint
                using (channels[CodeChannel.File].Lock())
                {
                    channels[CodeChannel.File].WaitForPrintCompletion();
                }
            }

            // Process incoming packets
            try
            {
                for (int i = 0; i < linkAdapter.PacketsToRead; i++)
                {
                    try
                    {
                        PacketHeader? packet = linkAdapter.ReadNextPacket();
                        if (packet is null)
                        {
                            logger.LogError("Read invalid packet");
                            break;
                        }
                        ProcessPacket(packet.Value);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        linkAdapter.DumpMalformedPacket();
                        throw;
                    }
                }
            }
            catch when (writtenHaltRequest is not null)
            {
                // The emergency stop must reach RRF even if the data RRF sent before it cannot be processed
                linkAdapter.PerformFullTransfer(cancellationToken: stoppingToken);
                throw;
            }
            linkInterface.BytesReserved = 0;

            // RRF has carried out the emergency stop that went out with the last transfer, and the data it sent before has been processed
            transferredHaltRequest?.SetResult();
            transferredHaltRequest = null;

            // Transfer an emergency stop at once, like a reset. The codes are invalidated only now, because the data RRF sent before the stop
            // belongs to them, e.g. the closal of a macro file that DSF still has on the stack. The stop ends the work of every channel as an abort of
            // all files does: RRF's packets of the current epoch are stale from here on, also those that come with the transfer of the stop, and each
            // channel writes InvalidateChannel(e+1) in a later Spin, never before the stop
            if (writtenHaltRequest is not null)
            {
                InvalidateCodes(true);
                linkAdapter.PerformFullTransfer(cancellationToken: stoppingToken);
                transferredHaltRequest = writtenHaltRequest;
                continue;
            }

            // Process pending codes, macro files and requests for resource locks/unlocks as well as flush requests
            if (!skipChannels)
            {
                channels.Spin(_connection);
            }

            // Request object model updates
            if (linkAdapter.ProtocolVersion == 1)
            {
                throw new Exception("Unsupported firmware version. Upgrade your firmware manually");
            }

            // Only allow one object model query per transfer because the response is usually quite large
            lock (linkInterface.ModelQueryRequests)
            {
                ushort tag = _connection.Tag();
                if (linkInterface.ModelQueryRequests.TryPeek(out ModelQueryRequest? request) &&
                    !request.QuerySent && linkAdapter.WriteGetObjectModel(request.Key, request.Flags, tag))
                {
                    _connection.Written(tag);
                    request.QuerySent = true;
                }
            }

            // Notify RRF about changed object model keys
            lock (linkInterface.UpdatedObjectModelKeys)
            {
                foreach (string key in linkInterface.UpdatedObjectModelKeys.ToList())
                {
                    ushort tag = _connection.Tag();
                    if (linkAdapter.WriteObjectModelKeyChanged(key, tag))
                    {
                        _connection.Written(tag);
                        linkInterface.UpdatedObjectModelKeys.Remove(key);
                    }
                    else
                    {
                        // Cannot write any more object model key changes, stop here
                        break;
                    }
                }
            }

            // Update code result(s) where needed
            lock (linkInterface.SetLastCodeResultRequests)
            {
                foreach (SetLastCodeResultRequest request in linkInterface.SetLastCodeResultRequests.ToList())
                {
                    if (channels[request.Channel].IsAbortUnsent)
                    {
                        // Nothing of the channel goes out before its InvalidateChannel, which fails this request (CE-25 (f))
                        continue;
                    }

                    ushort tag = _connection.Tag(channels[request.Channel].Epoch);
                    if (linkAdapter.WriteSetLastCodeResult(request.Channel, request.Result, tag))
                    {
                        _connection.Written(tag);
                        request.SetResult();
                        linkInterface.SetLastCodeResultRequests.Remove(request);
                    }
                    else
                    {
                        // Cannot write any more code result requests, stop here
                        break;
                    }
                }
            }

            {
                // Ask for expressions to be evaluated
                int numEvaluationsSent = 0;

                lock (linkInterface.EvaluateExpressionRequests)
                {
                    foreach (EvaluateExpressionRequest request in linkInterface.EvaluateExpressionRequests)
                    {
                        if (!request.Written && !channels[request.Channel].IsAbortUnsent)
                        {
                            ushort tag = _connection.Tag(channels[request.Channel].Epoch);
                            if (linkAdapter.WriteEvaluateExpression(request.Channel, request.Expression, tag))
                            {
                                _connection.Written(tag);
                                request.Written = true;
                                request.Tag = tag;

                                numEvaluationsSent++;
                                if (numEvaluationsSent >= Consts.MaxEvaluationRequestsPerTransfer)
                                {
                                    break;
                                }
                            }
                            else
                            {
                                // Cannot write any more evaluation requests, stop here
                                break;
                            }
                        }
                    }
                }

                // Perform variable updates
                lock (linkInterface.VariableRequests)
                {
                    foreach (VariableRequest request in linkInterface.VariableRequests.ToList())
                    {
                        if (!request.Written && !channels[request.Channel].IsAbortUnsent)
                        {
                            ushort tag = _connection.Tag(channels[request.Channel].Epoch);
                            if ((request.Expression is not null && linkAdapter.WriteSetVariable(request.Channel, request.CreateVariable, request.VariableName, request.Expression, tag)) ||
                                (request.Expression is null && linkAdapter.WriteDeleteLocalVariable(request.Channel, request.VariableName, tag)))
                            {
                                _connection.Written(tag);
                                request.Tag = tag;
                                if (request.Expression is null)
                                {
                                    request.SetResult(null);
                                    linkInterface.VariableRequests.Remove(request);
                                }
                                else
                                {
                                    request.Written = true;
                                }

                                numEvaluationsSent++;
                                if (numEvaluationsSent >= Consts.MaxEvaluationRequestsPerTransfer)
                                {
                                    break;
                                }
                            }
                            else
                            {
                                // Don't attempt to write any more variable requests, else we risk getting out of order
                                break;
                            }
                        }
                    }
                }
            }

            // Send pending messages
            lock (linkInterface.MessagesToSend)
            {
                while (linkInterface.MessagesToSend.TryPeek(out (MessageTypeFlags Flags, string Message, ushort Tag) message))
                {
                    // The result of a DoCode of an older epoch is not sent: RRF has ended that DoCode at the abort and drops the chunks (CE-25 (e), CE-10)
                    if (ChannelEpoch.IsTagged(message.Tag) && TryGetDestinationChannel(message.Flags, out CodeChannel destination) &&
                        !channels[destination].IsResultOfCurrentEpoch(message.Tag))
                    {
                        linkInterface.MessagesToSend.Dequeue();
                        continue;
                    }

                    ushort tag = _connection.Mark(message.Tag);
                    if (linkAdapter.WriteMessage(message.Flags, message.Message, tag))
                    {
                        _connection.Written(tag);
                        linkInterface.MessagesToSend.Dequeue();
                    }
                    else
                    {
                        break;
                    }
                }
            }

            // Do another full SPI transfer. It ends with this service, like the loop around it, else a transfer that does not complete keeps the
            // service from stopping until the shutdown timeout
            linkAdapter.PerformFullTransfer(cancellationToken: stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested);
    }

    /// <summary>
    /// Process a packet from RepRapFirmware
    /// </summary>
    /// <param name="packet">Received packet</param>
    /// <returns>Asynchronous task</returns>
    private void ProcessPacket(PacketHeader packet)
    {
        // Outside ResendPacket the header field carries the tag of the channel epoch (CE-5). A packet of another epoch is dropped by its handler,
        // with the channel locked, because DSF aborts a channel under that lock (Processor.IsStale)
        ushort tag = ((Request)packet.Request != Request.ResendPacket) ? packet.ResendPacketId : (ushort)0;
        if (!_connection.Received(tag))
        {
            // RRF tagged this packet before it read DSF's first packet of this connection, so it belongs to an older connection, whose work both
            // sides have ended: DSF has no code or request left that it could belong to, and RRF ends that connection as a lost one at the latest
            // when it reads the first marker
            logger.LogDebug("Dropped {Request} of an older connection", (Request)packet.Request);
            return;
        }

        switch ((Request)packet.Request)
        {
            case Request.ResendPacket:
                linkAdapter.ResendPacket(packet, out Protocol.SbcRequests.Request sbcRequest, out CodeChannel? invalidatedChannel);
                if (sbcRequest != Protocol.SbcRequests.Request.LockAllMovementSystemsAndWaitForStandstill)
                {
                    // It's expected that RRF will need a moment to lock the movement but report other resend requests
                    logger.LogWarning("Resending packet #{Id} (request {Request})", packet.Id, sbcRequest);
                }
                if (invalidatedChannel is not null && invalidatedChannel.Value < CodeChannel.Unknown)
                {
                    using (channels[invalidatedChannel.Value].Lock())
                    {
                        channels[invalidatedChannel.Value].AllFilesAbortResent();
                    }
                }
                break;
            case Request.ObjectModel:
                HandleObjectModel();
                break;
            case Request.CodeBufferUpdate:
                HandleCodeBufferUpdate();
                break;
            case Request.Message:
                HandleMessage(tag);
                break;
            case Request.ExecuteMacro:
                HandleMacroRequest(tag);
                break;
            case Request.AbortFile:
                HandleAbortFileRequest(tag);
                break;
            case Request.PrintPaused:
                HandlePrintPaused(tag);
                break;
            case Request.Locked:
                HandleResourceLocked(tag);
                break;
            case Request.FileChunk:
                HandleFileChunkRequest();
                break;
            case Request.EvaluationResult:
                HandleEvaluationResult(tag);
                break;
            case Request.DoCode:
                HandleDoCode(tag);
                break;
            case Request.WaitForAcknowledgement:
                HandleWaitForAcknowledgement(tag);
                break;
            case Request.MacroFileClosed:
                HandleMacroFileClosed(tag);
                break;
            case Request.MessageAcknowledged:
                HandleMessageAcknowledgement(tag);
                break;
            case Request.VariableResult:
                HandleVariableResult(tag);
                break;
            case Request.CheckFileExists:
                HandleCheckFileExists();
                break;
            case Request.DeleteFileOrDirectory:
            case Request.DeleteFileOrDirectoryRecursively:
                HandleDeleteFileOrDirectory((Request)packet.Request == Request.DeleteFileOrDirectoryRecursively);
                break;
            case Request.SecureDeleteFile:
                HandleSecureDeleteFile();
                break;
            case Request.GetFileList:
                HandleGetFileList();
                break;
            case Request.OpenFile:
                HandleOpenFile();
                break;
            case Request.ReadFile:
                HandleReadFile();
                break;
            case Request.WriteFile:
                HandleWriteFile();
                break;
            case Request.SeekFile:
                HandleSeekFile();
                break;
            case Request.TruncateFile:
                HandleTruncateFile();
                break;
            case Request.CloseFile:
                HandleCloseFile();
                break;
        }
    }

    /// <summary>
    /// Process an object model response
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private void HandleObjectModel()
    {
        logger.LogTrace("Received object model");
        linkAdapter.ReadObjectModel(out ReadOnlySpan<byte> json);
        lock (linkInterface.ModelQueryRequests)
        {
            if (linkInterface.ModelQueryRequests.TryPeek(out ModelQueryRequest? query) && query.QuerySent)
            {
                // Responses are matched to queries by order only. A response to a query that was
                // invalidated (e.g. after a transfer timeout) may still arrive once the link is back
                // and would then be applied to the wrong key, shifting every following response by one
                if (Protocol.Reader.TryReadObjectModelKey(json, out string responseKey) && responseKey != query.Key)
                {
                    logger.LogWarning("Discarding object model response for key '{ResponseKey}' because key '{RequestedKey}' was requested", responseKey, query.Key);
                    return;
                }

                linkInterface.ModelQueryRequests.Dequeue();
                if (json.IsEmpty)
                {
                    query.Tcs.SetException(new ArgumentException("Object model response was too big"));
                }
                else
                {
                    query.Tcs.SetResult(json.ToArray());
                }
            }
            else
            {
                logger.LogWarning("Failed to find query for object model response");
            }
        }
    }

    /// <summary>
    /// Update the amount of buffer space
    /// </summary>
    private void HandleCodeBufferUpdate()
    {
        linkAdapter.ReadCodeBufferUpdate(out ushort bufferSpace);
        if (bufferSpace > linkInterface.MaxReportedBufferSpace)
        {
            linkInterface.MaxReportedBufferSpace = bufferSpace;
        }
        linkInterface.BufferSpace = bufferSpace - linkInterface.BytesReserved;
        logger.LogTrace("Buffer space available: {BufferSpace}", linkInterface.BufferSpace);
    }

    /// <summary>
    /// Buffer for truncated log messages
    /// </summary>
    private string? _partialLogMessage;

    /// <summary>
    /// Process an incoming message
    /// </summary>
    private void HandleMessage(ushort tag)
    {
        linkAdapter.ReadMessage(out MessageTypeFlags flags, out string reply);
        logger.LogTrace("Received message [{Flags}] {Message}", flags, reply);

        // Deal with log messages
        if ((flags & MessageTypeFlags.LogOff) != MessageTypeFlags.LogOff)
        {
            _partialLogMessage += reply;
            if (!flags.HasFlag(MessageTypeFlags.PushFlag))
            {
                if (!string.IsNullOrWhiteSpace(_partialLogMessage))
                {
                    MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                                        : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                                            : MessageType.Success;
                    EventLogLevel level = flags.HasFlag(MessageTypeFlags.LogOff) ? EventLogLevel.Off
                                        : flags.HasFlag(MessageTypeFlags.LogWarn) ? EventLogLevel.Warn
                                            : flags.HasFlag(MessageTypeFlags.LogInfo) ? EventLogLevel.Info
                                                : EventLogLevel.Debug;
                    eventLogger.Log(level, type, _partialLogMessage.TrimEnd());
                }
                _partialLogMessage = null;
            }
        }

        // Check if this is a code reply
        if (flags.HasFlag(MessageTypeFlags.BinaryCodeReplyFlag))
        {
            if (!channels.HandleReply(flags, reply, tag))
            {
                // Must be a left-over error message...
                OutputGenericMessage(flags, reply);
            }
        }
        else if ((flags & MessageTypeFlags.GenericMessage) == MessageTypeFlags.GenericMessage)
        {
            // Generic messages to the main object model
            OutputGenericMessage(flags, reply);
        }
        else
        {
            // Targeted messages are handled by the IPC processors
            MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                    : MessageType.Success;
            IPC.Processors.CodeStream.RecordMessage(flags, new Message(type, reply));
            IPC.Processors.ModelSubscription.RecordMessage(flags, new Message(type, reply));
        }
    }


    /// <summary>
    /// Partial incoming message (if any)
    /// </summary>
    private static string? _partialGenericMessage;

    /// <summary>
    /// Output a generic message
    /// </summary>
    /// <param name="flags">Message flags</param>
    /// <param name="reply">Message content</param>
    private void OutputGenericMessage(MessageTypeFlags flags, string reply)
    {
        _partialGenericMessage += reply;
        if (!flags.HasFlag(MessageTypeFlags.PushFlag))
        {
            if (!string.IsNullOrWhiteSpace(_partialGenericMessage))
            {
                MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                                    : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                                        : MessageType.Success;
                model.Output(type, _partialGenericMessage.TrimEnd());
            }
            _partialGenericMessage = null;
        }
    }

    /// <summary>
    /// Handle a macro request
    /// </summary>
    private void HandleMacroRequest(ushort tag)
    {
        linkAdapter.ReadMacroRequest(out CodeChannel channel, out bool fromCode, out string filename);
        logger.LogTrace("Received macro request for file {File} on channel {Channel}", filename, channel);

        if (channel < CodeChannel.Unknown)
        {
            using (channels[channel].Lock())
            {
                if (!channels[channel].IsStale(tag, Request.ExecuteMacro))
                {
                    channels[channel].DoMacroFile(filename, fromCode, ChannelEpoch.IsTagged(tag), _connection);
                }
            }
        }
        else if (!settings.Value.UpdateOnly)
        {
            logger.LogError("Received macro request for file {File} on invalid channel {Channel}", filename, channel);
        }
    }

    /// <summary>
    /// Handle a file abort request
    /// </summary>
    private void HandleAbortFileRequest(ushort tag)
    {
        linkAdapter.ReadAbortFile(out CodeChannel channel, out bool abortAll);
        if (channel != CodeChannel.Daemon)
        {
            logger.LogInformation("Received file abort request on channel {Channel} for {FileType}", channel, abortAll ? "all files" : "the last file");
        }
        else
        {
            logger.LogDebug("Received file abort request on channel {Channel} for {FileType}", channel, abortAll ? "all files" : "the last file");
        }

        if (channel < CodeChannel.Unknown)
        {
            using (channels[channel].Lock())
            {
                if (!channels[channel].IsStale(tag, Request.AbortFile))
                {
                    channels[channel].FilesAborted(abortAll, ChannelEpoch.IsTagged(tag));
                }
            }
        }
        else if (!settings.Value.UpdateOnly)
        {
            logger.LogError("Received file abort request on channel {Channel} for {FileType}", channel, abortAll ? "all files" : "the last file");
        }
    }

    /// <summary>
    /// Deal with paused print events
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private void HandlePrintPaused(ushort tag)
    {
        linkAdapter.ReadPrintPaused(out uint filePosition, out uint filePosition2, out PrintPausedReason pauseReason);
        using (channels[CodeChannel.File].Lock())
        {
            // PrintPaused carries the epoch of File (CE-6). Here the check and the pause below do not share one lock, as an abort of File meanwhile
            // would void the pause anyway (the job is aborted with it)
            if (channels[CodeChannel.File].IsStale(tag, Request.PrintPaused))
            {
                return;
            }
        }
        logger.LogDebug("Received print pause notification for file position {Offset}/{Offset2}, reason {PauseReason}", (filePosition == Consts.NoFilePosition) ? "(none)" : filePosition.ToString(), (filePosition2 == Consts.NoFilePosition) ? "(none)" : filePosition2.ToString(), pauseReason);

        // Update the object model
        using (model.AccessReadWrite())
        {
            model.State.Status = MachineStatus.Paused;
        }

        // Pause the print
        using (jobProcessor.Lock())
        {
            // Do NOT supply a file position if this is a pause request initiated from G-code because that would lead to an endless loop
            bool filePositionValid = (filePosition != Consts.NoFilePosition) && (pauseReason != PrintPausedReason.GCode) && (pauseReason != PrintPausedReason.FilamentChange);
            bool filePosition2Valid = (filePosition2 != Consts.NoFilePosition) && (pauseReason != PrintPausedReason.GCode) && (pauseReason != PrintPausedReason.FilamentChange);
            jobProcessor.Pause(filePositionValid ? filePosition : null, filePosition2Valid ? filePosition2 : null, pauseReason);
        }

        // Resolve pending and buffered codes on the file channels
        using (channels[CodeChannel.File].Lock())
        {
            channels[CodeChannel.File].PrintPaused();
        }

        using (channels[CodeChannel.File2].Lock())
        {
            channels[CodeChannel.File2].PrintPaused();
        }
    }

    /// <summary>
    /// Deal with the confirmation that a resource has been locked
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private void HandleResourceLocked(ushort tag)
    {
        linkAdapter.ReadCodeChannel(out CodeChannel channel);
        logger.LogTrace("Received resource locked notification for channel {Channel}", channel);

        if (channel < CodeChannel.Unknown)
        {
            using (channels[channel].Lock())
            {
                if (!channels[channel].IsStale(tag, Request.Locked))
                {
                    channels[channel].ResourceLocked();
                }
            }
        }
        else if (!settings.Value.UpdateOnly)
        {
            logger.LogError("Received resource locked notification for invalid channel {Channel}", channel);
        }
    }

    /// <summary>
    /// Process a request for a chunk of a given file
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private void HandleFileChunkRequest()
    {
        ushort tag = _connection.Tag();
        linkAdapter.ReadFileChunkRequest(out string filename, out uint offset, out int maxLength);
        logger.LogDebug("Received file chunk request for {File}, offset {Offset}, maxLength {MaxLength}", filename, offset, maxLength);

        try
        {
            string filePath;
            if (filename.EndsWith(".bin") || filename.EndsWith(".uf2"))
            {
                filePath = filePathResolver.ToPhysical(filename, FileDirectory.Firmware);
                if (!File.Exists(filePath))
                {
                    filePath = filePathResolver.ToPhysical(filename, FileDirectory.System);
                }
            }
            else
            {
                filePath = filePathResolver.ToPhysical(filename, FileDirectory.System);
            }

            using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, settings.Value.FileBufferSize)
            {
                Position = offset
            };

            // The requested length comes from the wire and can never legitimately exceed the transfer
            // buffer size, so clamp it before allocating the chunk on the stack
            maxLength = Math.Clamp(maxLength, 0, settings.Value.SbcBufferSize);
            Span<byte> buffer = stackalloc byte[maxLength];
            int bytesRead = fs.Read(buffer);

            Answered(linkAdapter.WriteFileChunk((bytesRead > 0) ? buffer[..bytesRead] : [], fs.Length, tag), tag);
        }
        catch (Exception e)
        {
            if (!settings.Value.UpdateOnly)
            {
                logger.LogError(e, "Failed to send requested file chunk of {File}", filename);
            }
            Answered(linkAdapter.WriteFileChunk(null, 0, tag), tag);
        }
    }

    /// <summary>
    /// Handle the result of an evaluated expression
    /// </summary>
    private void HandleEvaluationResult(ushort tag)
    {
        linkAdapter.ReadEvaluationResult(out CodeChannel? channel, out string expression, out object? result);
        logger.LogDebug("Received evaluation result for expression via {Channel}: {Expression} = {Result}", channel, expression, result);
        if (IsStaleAnswer(channel, tag, Request.EvaluationResult))
        {
            return;
        }

        lock (linkInterface.EvaluateExpressionRequests)
        {
            foreach (EvaluateExpressionRequest request in linkInterface.EvaluateExpressionRequests)
            {
                if (request.Written && (channel == null || request.Channel == channel) && request.Expression == expression && (!ChannelEpoch.IsTagged(tag) || ChannelEpoch.IsSameTag(request.Tag, tag)))
                {
                    if (result is Exception exception)
                    {
                        request.SetException(exception);
                    }
                    else
                    {
                        request.SetResult(result);
                    }
                    linkInterface.EvaluateExpressionRequests.Remove(request);
                    return;
                }
            }
        }

        if (!settings.Value.UpdateOnly)
        {
            logger.LogWarning("Unresolved evaluation result for expression {Expression} = {Result}", expression, result);
        }
    }

    /// <summary>
    /// Handle a firmware request to perform a G/M/T-code in DSF
    /// </summary>
    private void HandleDoCode(ushort tag)
    {
        linkAdapter.ReadDoCode(out CodeChannel channel, out string code);
        logger.LogTrace("Received firmware code request on channel {Channel} => {Code}", channel, code);

        if (channel < CodeChannel.Unknown)
        {
            using (channels[channel].Lock())
            {
                // A DoCode of an older epoch is not run, RRF has ended it at the abort (CE-14, CE-25)
                if (!channels[channel].IsStale(tag, Request.DoCode))
                {
                    channels[channel].DoFirmwareCode(code, tag);
                }
            }
        }
        else if (!settings.Value.UpdateOnly)
        {
            logger.LogError("Received firmware code request for invalid channel {Channel} => {Code}", channel, code);
        }
    }

    /// <summary>
    /// Handle a firmware request to wait for a message to be acknowledged
    /// </summary>
    private void HandleWaitForAcknowledgement(ushort tag)
    {
        linkAdapter.ReadCodeChannel(out CodeChannel channel);
        logger.LogTrace("Received wait for message acknowledgement on channel {Channel}", channel);

        if (channel < CodeChannel.Unknown)
        {
            using (channels[channel].Lock())
            {
                if (!channels[channel].IsStale(tag, Request.WaitForAcknowledgement))
                {
                    channels[channel].WaitForAcknowledgement();
                }
            }
        }
        else if (!settings.Value.UpdateOnly)
        {
            logger.LogError("Received wait for message acknowledgement on invalid channel {Channel}", channel);
        }
    }

    /// <summary>
    /// Handle a firmware request that is sent when RRF has internally closed a macro file
    /// </summary>
    private void HandleMacroFileClosed(ushort tag)
    {
        linkAdapter.ReadCodeChannel(out CodeChannel channel);
        logger.LogTrace("Received file closal on channel {Channel}", channel);

        if (channel < CodeChannel.Unknown)
        {
            using (channels[channel].Lock())
            {
                if (!channels[channel].IsStale(tag, Request.MacroFileClosed))
                {
                    channels[channel].MacroFileClosed(ChannelEpoch.IsTagged(tag));
                }
            }
        }
        else if (!settings.Value.UpdateOnly)
        {
            logger.LogError("Received file closal on invalid channel {Channel}", channel);
        }
    }

    /// <summary>
    /// Handle a firmware request that is sent when RRF has successfully acknowledged a blocking message
    /// </summary>
    private void HandleMessageAcknowledgement(ushort tag)
    {
        linkAdapter.ReadCodeChannel(out CodeChannel channel);
        logger.LogTrace("Received message acknowledgement on channel {Channel}", channel);

        if (channel < CodeChannel.Unknown)
        {
            using (channels[channel].Lock())
            {
                if (!channels[channel].IsStale(tag, Request.MessageAcknowledged))
                {
                    channels[channel].MessageAcknowledged();
                }
            }
        }
        else if (!settings.Value.UpdateOnly)
        {
            logger.LogError("Received message acknowledgement on invalid channel {Channel}", channel);
        }
    }

    /// <summary>
    /// Handle the result of a variable assignment
    /// </summary>
    private void HandleVariableResult(ushort tag)
    {
        linkAdapter.ReadEvaluationResult(out CodeChannel? channel, out string varName, out object? result);
        logger.LogTrace("Received variable assignment result for {Channel}: {Variable} = {Result}", channel, varName, result);
        if (IsStaleAnswer(channel, tag, Request.VariableResult))
        {
            return;
        }

        lock (linkInterface.VariableRequests)
        {
            foreach (VariableRequest request in linkInterface.VariableRequests)
            {
                if (request.VariableName == varName && (channel == null || request.Channel == channel) && (!ChannelEpoch.IsTagged(tag) || ChannelEpoch.IsSameTag(request.Tag, tag)))
                {
                    if (result is Exception exception)
                    {
                        request.SetException(exception);
                    }
                    else
                    {
                        request.SetResult(result);
                    }
                    linkInterface.VariableRequests.Remove(request);
                    return;
                }
            }
        }

        if (!settings.Value.UpdateOnly)
        {
            logger.LogWarning("Unresolved variable set result for {Channel}: {Variable} = {Result}", channel, varName, result);
        }
    }

    /// <summary>
    /// Check if a file exists
    /// </summary>
    private void HandleCheckFileExists()
    {
        ushort tag = _connection.Tag();
        linkAdapter.ReadCheckFileExists(out string filename);
        logger.LogDebug("Checking if file {File} exists", filename);

        try
        {
            string physicalFile = filePathResolver.ToPhysical(filename);
            bool exists = File.Exists(physicalFile);
            Answered(linkAdapter.WriteCheckFileExistsResult(exists, tag), tag);
        }
        catch (Exception e)
        {
            if (!settings.Value.UpdateOnly)
            {
                logger.LogError(e, "Failed to check if file {File} exists", filename);
            }
            Answered(linkAdapter.WriteCheckFileExistsResult(false, tag), tag);
        }
    }

    /// <summary>
    /// Delete a file or directory
    /// </summary>
    /// <param name="recursive">Delete file or directory recursively</param>
    private void HandleDeleteFileOrDirectory(bool recursive)
    {
        ushort tag = _connection.Tag();
        linkAdapter.ReadDeleteFileOrDirectory(out string filename);
        logger.LogDebug("Attempting to delete {File}", filename);

        try
        {
            string physicalFile = filePathResolver.ToPhysical(filename, forDeletion: true);
            if (Directory.Exists(physicalFile))
            {
                Directory.Delete(physicalFile, recursive);
            }
            else
            {
                File.Delete(physicalFile);
            }
            Answered(linkAdapter.WriteFileDeleteResult(true, tag), tag);
        }
        catch (Exception e)
        {
            if (!settings.Value.UpdateOnly)
            {
                logger.LogError(e, "Failed to delete file or directory {File}", filename);
            }
            Answered(linkAdapter.WriteFileDeleteResult(false, tag), tag);
        }
    }

    /// <summary>
    /// Securely delete a file: overwrite its contents with zeros and fsync before unlinking.
    /// One zero-overwrite pass is appropriate for SD/eMMC; multi-pass wipes don't help on flash
    /// because of wear-leveling indirection. Mirrors MassStorage::SecureDelete on the RRF side.
    /// Directories and non-existent files are rejected (the firmware uses this only for files).
    /// </summary>
    private void HandleSecureDeleteFile()
    {
        ushort tag = _connection.Tag();
        linkAdapter.ReadDeleteFileOrDirectory(out string filename);
        logger.LogDebug("Attempting to securely delete {File}", filename);

        try
        {
            string physicalFile = filePathResolver.ToPhysical(filename);
            if (Directory.Exists(physicalFile))
            {
                throw new IOException("SecureDelete is not supported for directories");
            }

            if (File.Exists(physicalFile))
            {
                using FileStream fs = new(physicalFile, FileMode.Open, FileAccess.Write, FileShare.None, settings.Value.FileBufferSize);
                long length = fs.Length;
                if (length > 0)
                {
                    byte[] zeros = new byte[Math.Min(settings.Value.FileBufferSize, length)];
                    fs.Seek(0, SeekOrigin.Begin);
                    long remaining = length;
                    while (remaining > 0)
                    {
                        int toWrite = (int)Math.Min(zeros.Length, remaining);
                        fs.Write(zeros, 0, toWrite);
                        remaining -= toWrite;
                    }
                    fs.Flush(true);     // fsync to media
                }
            }
            // File.Delete is a no-op if the file is missing, so it is safe outside the exists-check;
            // placed after the if-block so the using declaration has disposed fs before the unlink
            File.Delete(physicalFile);
            Answered(linkAdapter.WriteFileDeleteResult(true, tag), tag);
        }
        catch (Exception e)
        {
            if (!settings.Value.UpdateOnly)
            {
                logger.LogError(e, "Failed to securely delete {File}", filename);
            }
            Answered(linkAdapter.WriteFileDeleteResult(false, tag), tag);
        }
    }

    /// <summary>
    /// Send back part of a directory listing.
    /// The firmware pages through the listing by entry index, so the order must be the same for every
    /// request of the same directory - hence the explicit sort instead of the order the filesystem returns
    /// </summary>
    private void HandleGetFileList()
    {
        ushort tag = _connection.Tag();
        linkAdapter.ReadGetFileList(out string directory, out uint startIndex, out uint maxLength);
        logger.LogDebug("Listing {Directory} from entry {StartIndex}", directory, startIndex);

        List<FileSystemInfo> entries = [];
        bool endOfList = true;
        try
        {
            // The requested length comes from the wire and can never legitimately exceed the transfer buffer size
            maxLength = Math.Min(maxLength, (uint)settings.Value.SbcBufferSize);

            DirectoryInfo directoryInfo = new(filePathResolver.ToPhysical(directory));
            if (directoryInfo.Exists)
            {
                FileSystemInfo[] allEntries = directoryInfo.GetFileSystemInfos();
                Array.Sort(allEntries, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                int bytesUsed = 0;
                for (int i = (int)startIndex; i < allEntries.Length; i++)
                {
                    int entrySize = Protocol.Writer.GetFileListEntrySize(allEntries[i].Name);
                    if (bytesUsed + entrySize > maxLength)
                    {
                        endOfList = false;
                        break;
                    }

                    bytesUsed += entrySize;
                    entries.Add(allEntries[i]);
                }
            }
        }
        catch (Exception e)
        {
            if (!settings.Value.UpdateOnly)
            {
                logger.LogError(e, "Failed to list {Directory}", directory);
            }
            entries.Clear();
            endOfList = true;
        }
        Answered(linkAdapter.WriteFileListResult(entries, endOfList, tag), tag);
    }

    /// <summary>
    /// Try to open a file
    /// </summary>
    private void HandleOpenFile()
    {
        ushort tag = _connection.Tag();
        linkAdapter.ReadOpenFile(out string filename, out bool forWriting, out bool append, out long preAllocSize);
        logger.LogDebug("Opening {File} for {Operation} ({NotAppending}appending), prealloc {Prealloc}", filename, forWriting ? "writing" : "reading", append ? string.Empty : "not ", preAllocSize);

        try
        {
            // Resolve the path and create the parent directory if necessary
            string physicalFile = filePathResolver.ToPhysical(filename), parentDirectory = Path.GetDirectoryName(physicalFile)!;
            if (!Directory.Exists(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            // Try to open the file as requested
            FileMode fsMode = forWriting ? (append ? FileMode.Append : FileMode.Create) : FileMode.Open;
            FileAccess faMode = forWriting ? FileAccess.Write : FileAccess.Read;
            FileStream fs = new(physicalFile, fsMode, faMode, FileShare.Read, settings.Value.FileBufferSize);
            if (forWriting && !append && preAllocSize > 0)
            {
                fs.SetLength(preAllocSize);
            }

            // Register a handle and send it back
            _openFileHandleCounter++;
            if (_openFileHandleCounter == Consts.NoFileHandle)
            {
                _openFileHandleCounter++;
            }
            _openFiles.Add(_openFileHandleCounter, fs);

            logger.LogDebug("File {File} opened with handle #{Handle}", filename, _openFileHandleCounter);
            Answered(linkAdapter.WriteOpenFileResult(_openFileHandleCounter, fs.Length, tag), tag);
        }
        catch (Exception e)
        {
            if (!settings.Value.UpdateOnly)
            {
                logger.LogError(e, "Failed to open {File} for {Operation}", filename, forWriting ? "writing" : "reading");
            }
            Answered(linkAdapter.WriteOpenFileResult(Consts.NoFileHandle, 0, tag), tag);
        }
    }

    /// <summary>
    /// Read more from a given file
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private void HandleReadFile()
    {
        ushort tag = _connection.Tag();
        linkAdapter.ReadFileRequest(out uint handle, out int maxLength);
        logger.LogTrace("Reading up to {MaxLength} bytes from file #{Handle}", maxLength, handle);

        try
        {
            // Read file content as requested. The requested length comes from the wire and can never
            // legitimately exceed the transfer buffer size, so clamp it before allocating on the stack
            maxLength = Math.Clamp(maxLength, 0, settings.Value.SbcBufferSize);
            FileStream fs = _openFiles[handle];
            Span<byte> data = stackalloc byte[maxLength];
            int bytesRead = fs.Read(data);

            // Send it back
            Answered(linkAdapter.WriteFileReadResult((bytesRead > 0) ? data[..bytesRead] : [], bytesRead, tag), tag);
        }
        catch (Exception e)
        {
            if (!settings.Value.UpdateOnly)
            {
                logger.LogError(e, "Failed to read {MaxLength} bytes from file #{Handle}", maxLength, handle);
            }
            Answered(linkAdapter.WriteFileReadResult([], -1, tag), tag);
        }
    }

    /// <summary>
    /// Write more to a given file
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private void HandleWriteFile()
    {
        ushort tag = _connection.Tag();
        linkAdapter.ReadWriteRequest(out uint handle, out ReadOnlySpan<byte> data);
        logger.LogTrace("Writing {DataLength} bytes to file #{Handle}", data.Length, handle);

        try
        {
            // Write file content as requested
            FileStream fs = _openFiles[handle];
            fs.Write(data);

            // Send it back
            Answered(linkAdapter.WriteFileWriteResult(true, tag), tag);
        }
        catch (Exception e)
        {
            if (!settings.Value.UpdateOnly)
            {
                logger.LogError(e, "Failed to write {DataLength} bytes to file #{Handle}", data.Length, handle);
            }
            Answered(linkAdapter.WriteFileWriteResult(false, tag), tag);
        }
    }

    /// <summary>
    /// Go to a specific position in a file
    /// </summary>
    private void HandleSeekFile()
    {
        ushort tag = _connection.Tag();
        linkAdapter.ReadSeekFile(out uint handle, out long offset);
        logger.LogTrace("Seeking to position {Offset} in file #{Handle}", offset, handle);

        try
        {
            // Go to the file position as requested
            FileStream fs = _openFiles[handle];
            fs.Seek(offset, SeekOrigin.Begin);

            // Send it back
            Answered(linkAdapter.WriteFileSeekResult(true, tag), tag);
        }
        catch (Exception e)
        {
            if (!settings.Value.UpdateOnly)
            {
                logger.LogError(e, "Failed to go to position {Offset} in file #{Handle}", offset, handle);
            }
            Answered(linkAdapter.WriteFileSeekResult(false, tag), tag);
        }
    }

    /// <summary>
    /// Go to a specific position in a file
    /// </summary>
    private void HandleTruncateFile()
    {
        ushort tag = _connection.Tag();
        linkAdapter.ReadTruncateFile(out uint handle);
        logger.LogDebug("Truncating file #{Handle}", handle);

        try
        {
            // Go to the file position as requested
            FileStream fs = _openFiles[handle];
            fs.SetLength(fs.Position);
            logger.LogDebug("Truncated file #{Handle} at byte {Length}", handle, fs.Length);

            // Send it back
            Answered(linkAdapter.WriteFileTruncateResult(true, tag), tag);
        }
        catch (Exception e)
        {
            if (!settings.Value.UpdateOnly)
            {
                logger.LogError(e, "Failed to truncate file #{Handle}", handle);
            }
            Answered(linkAdapter.WriteFileTruncateResult(false, tag), tag);
        }
    }

    /// <summary>
    /// Check if a file exists
    /// </summary>
    private void HandleCloseFile()
    {
        linkAdapter.ReadCloseFile(out uint handle);
        logger.LogDebug("Closing file #{Handle}", handle);

        try
        {
            // Close the file stream
            FileStream fs = _openFiles[handle];
            fs.Close();

            // Remove it again from the list of open files
            _openFiles.Remove(handle);

            // RRF doesn't expect a response for this...
        }
        catch (Exception e)
        {
            if (!settings.Value.UpdateOnly)
            {
                logger.LogError(e, "Failed to close file #{Handle}", handle);
            }
        }
    }
    
    /// <summary>
    /// State of the channel epoch for the current connection (CE-4). Replaced where a connection ends
    /// </summary>
    private EpochConnection _connection = new();

    /// <summary>
    /// Check if an answer to a request of DSF (EvaluationResult, VariableResult) is of another epoch than the current one of its channel (CE-14).
    /// The answer is matched outside the channel lock, so it also only answers a request that was written with its tag
    /// </summary>
    /// <param name="channel">Channel of the answer</param>
    /// <param name="tag">Tag of the answer</param>
    /// <param name="request">Type of the answer</param>
    /// <returns>Whether the answer is dropped</returns>
    private bool IsStaleAnswer(CodeChannel? channel, ushort tag, Request request)
    {
        if (channel is null || channel >= CodeChannel.Unknown || !ChannelEpoch.IsTagged(tag))
        {
            return false;
        }
        using (channels[channel.Value].Lock())
        {
            return channels[channel.Value].IsStale(tag, request);
        }
    }

    /// <summary>
    /// Keep track of an answer to a file request of RRF for the first marker (CE-4). The answer is lost if it could not be written, as before
    /// </summary>
    /// <param name="written">Whether the answer was written</param>
    /// <param name="tag">Tag it was written with, from <see cref="EpochConnection.Tag()"/></param>
    private void Answered(bool written, ushort tag)
    {
        if (written)
        {
            _connection.Written(tag);
        }
    }

    /// <summary>
    /// Get the channel of a message whose only destination is one code channel, as the result of a DoCode is (Executed.cs:177)
    /// </summary>
    /// <param name="flags">Message flags</param>
    /// <param name="channel">Destination channel</param>
    /// <returns>Whether there is exactly one destination channel</returns>
    private static bool TryGetDestinationChannel(MessageTypeFlags flags, out CodeChannel channel)
    {
        uint destinations = (uint)flags & ((1u << (int)CodeChannel.Unknown) - 1);
        if (destinations != 0 && (destinations & (destinations - 1)) == 0)
        {
            channel = (CodeChannel)System.Numerics.BitOperations.TrailingZeroCount(destinations);
            return true;
        }
        channel = CodeChannel.Unknown;
        return false;
    }

    /// <summary>
    /// Invalidate pending codes and code-relevant requests due to an emergency stop, a lost connection or a reset
    /// </summary>
    /// <param name="emergencyStop">Whether this is for an emergency stop, see <see cref="Channel.Processor.Invalidate"/></param>
    private void InvalidateCodes(bool emergencyStop)
    {
        // Invalidate pending codes and code-relevant requests
        linkInterface.InvalidateCodes(emergencyStop);

        // Cancel the file being printed (if any)
        using (jobProcessor.Lock())
        {
            jobProcessor.Abort();
        }

        // Resolve pending macros, unbuffered (system) codes and flush requests
        foreach (Channel.Processor channel in channels)
        {
            using (channel.Lock())
            {
                channel.Invalidate(emergencyStop);
            }
        }
    }

    /// <summary>
    /// Whether the capabilities of the link have to be queried, which is the case after every connect and reset
    /// </summary>
    private bool _queryLinkCapabilities = true;

    /// <summary>
    /// Query of limits.sbcLinkCapabilities or state.sbcLinkCapabilities
    /// </summary>
    private Task<byte[]>? _linkCapabilitiesQuery;

    /// <summary>
    /// Capabilities of the link that RRF offers and DSF has yet to activate, then the ones it has activated
    /// </summary>
    private LinkCapabilities _linkCapabilitiesToActivate, _activatedLinkCapabilities;

    /// <summary>
    /// Query the capabilities of the link that RRF offers, activate the ones the adapter supports and check that RRF uses them.
    /// RRF without them reports null for the key, and only RRF that offers them may get the activation request
    /// </summary>
    private void UpdateLinkCapabilities()
    {
        if (_queryLinkCapabilities)
        {
            _queryLinkCapabilities = false;
            _linkCapabilitiesToActivate = _activatedLinkCapabilities = LinkCapabilities.None;
            // Like every limit, the key is verbose
            _linkCapabilitiesQuery = (linkAdapter.SupportedLinkCapabilities != LinkCapabilities.None) ? linkInterface.RequestObjectModel("limits.sbcLinkCapabilities", "v") : null;
        }
        else if (_linkCapabilitiesQuery is { IsCompleted: true })
        {
            LinkCapabilities capabilities = LinkCapabilities.None;
            bool valid = _linkCapabilitiesQuery.IsCompletedSuccessfully && TryReadLinkCapabilities(_linkCapabilitiesQuery.Result, out capabilities);
            _linkCapabilitiesQuery = null;
            if (_activatedLinkCapabilities == LinkCapabilities.None)
            {
                // Answer to limits.sbcLinkCapabilities
                _linkCapabilitiesToActivate = valid ? capabilities & linkAdapter.SupportedLinkCapabilities : LinkCapabilities.None;
            }
            else if (valid && capabilities == _activatedLinkCapabilities)
            {
                // Answer to state.sbcLinkCapabilities
                logger.LogInformation("Link capabilities {Capabilities} are active", capabilities);
            }
            else
            {
                logger.LogWarning("RepRapFirmware reports other link capabilities than {Capabilities} as active", _activatedLinkCapabilities);
            }
        }

        ushort tag = _connection.Tag();
        if (_linkCapabilitiesToActivate != LinkCapabilities.None && linkAdapter.WriteActivateLinkCapabilities(_linkCapabilitiesToActivate, tag))
        {
            _connection.Written(tag);
            _activatedLinkCapabilities = _linkCapabilitiesToActivate;
            _linkCapabilitiesToActivate = LinkCapabilities.None;
            _linkCapabilitiesQuery = linkInterface.RequestObjectModel("state.sbcLinkCapabilities", string.Empty);
        }
    }

    /// <summary>
    /// Read the capabilities of the link from the response to an object model query
    /// </summary>
    /// <param name="json">Response</param>
    /// <param name="capabilities">Capabilities</param>
    /// <returns>Whether the result was a number</returns>
    private static bool TryReadLinkCapabilities(byte[] json, out LinkCapabilities capabilities)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Number && result.TryGetUInt32(out uint value))
            {
                capabilities = (LinkCapabilities)value;
                return true;
            }
        }
        catch (JsonException)
        {
            // Not a valid response
        }
        capabilities = LinkCapabilities.None;
        return false;
    }

    /// <summary>
    /// Invalidate every resource due to a disconnect or reset
    /// </summary>
    private void Invalidate()
    {
        // Invalidate codes and code-relevant requests
        InvalidateCodes(false);

        // Invalidate remaining link interface requests
        linkInterface.Invalidate();

        // Close all the files
        foreach (var kv in _openFiles)
        {
            kv.Value.Dispose();
        }
        _openFiles.Clear();

        // Notify the updater task about the lost connection
        model.ConnectionLost();
    }
}
