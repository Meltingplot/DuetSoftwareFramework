using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Connection.InitMessages;
using DuetAPI.Utility;
using DuetControlServer.Commands;
using DuetControlServer.IPC;
using DuetControlServer.IPC.Processors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// Stand-in for DuetPluginService (DPS). The IPC server grants the PluginService mode only to DPS itself (Server.GetConnectionProcessorAsync
/// requires SbcPermissions.ServicePlugins, which Connection.AssignPermissionsAsync gives only to a process named DuetPluginService next to DCS),
/// so the test process cannot get it through the socket. Instead this class runs the real <see cref="PluginService"/> processor of DCS on one end
/// of a socket pair and answers the commands DCS forwards on the other end, like DPS does
/// </summary>
public sealed class FakePluginService : IAsyncDisposable
{
    private readonly Socket _listener, _dcsSocket, _dpsSocket;
    private readonly string _socketPath;
    private readonly CancellationTokenSource _cts = new();
    private Task? _processorTask, _responderTask;

    private FakePluginService(Socket listener, Socket dcsSocket, Socket dpsSocket, string socketPath, Connection connection)
    {
        _listener = listener;
        _dcsSocket = dcsSocket;
        _dpsSocket = dpsSocket;
        _socketPath = socketPath;
        Connection = connection;
    }

    /// <summary>
    /// DCS side of the connection
    /// </summary>
    public Connection Connection { get; }

    /// <summary>
    /// Whether this is the root plugin service
    /// </summary>
    public bool IsRoot => Connection.IsRoot;

    /// <summary>
    /// Commands DCS forwarded to this plugin service
    /// </summary>
    public ConcurrentQueue<JsonElement> ReceivedCommands { get; } = new();

    /// <summary>
    /// Function that creates the response to a forwarded command. By default every command succeeds and ResolvePluginProcess
    /// resolves to no plugin, which is what DPS replies for a PID it does not track
    /// </summary>
    public Func<JsonElement, Task<string>> Responder { get; set; } = command => Task.FromResult(DefaultResponse(command));

    /// <summary>
    /// Default response of DPS to a command
    /// </summary>
    public static string DefaultResponse(JsonElement command) =>
        (command.GetProperty("command").GetString() == nameof(DuetAPI.Commands.ResolvePluginProcess)) ? "{\"success\":true,\"result\":null}" : "{\"success\":true}";

    /// <summary>
    /// Register a plugin service for the given host
    /// </summary>
    /// <param name="host">Host with IPC server</param>
    /// <param name="asRoot">Whether to register the root plugin service. Connection.IsRoot is only set for peers with uid 0, so it is set via reflection here</param>
    /// <returns>Registered plugin service</returns>
    public static async Task<FakePluginService> StartAsync(FakeFirmwareHost host, bool asRoot = false)
    {
        string socketPath = Path.Combine(host.BaseDirectory, "run", $"dps-{Guid.NewGuid():N}"[..12] + ".sock");
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        Socket dpsSocket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await dpsSocket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        Socket dcsSocket = await listener.AcceptAsync();

        Connection connection = new(dcsSocket, host.Services.GetRequiredService<CommandFactory>(), host.Services.GetRequiredService<ILogger<Server>>());
        if (asRoot)
        {
            typeof(Connection).GetProperty(nameof(Connection.IsRoot))!.SetValue(connection, true);
        }

        FakePluginService service = new(listener, dcsSocket, dpsSocket, socketPath, connection);
        PluginService processor = host.Services.GetRequiredService<ProcessorFactory>().Create<PluginService>(connection, new PluginServiceInitMessage());
        service._processorTask = Task.Run(async () =>
        {
            try
            {
                await processor.ProcessAsync(service._cts.Token);
            }
            finally
            {
                processor.Dispose();
            }
        });
        service._responderTask = Task.Run(service.RespondAsync);

        // Wait for the registration (PluginService.ProcessAsync sets the flag before it waits for requests)
        using CancellationTokenSource timeout = new(FakeFirmwareHost.DefaultTimeout);
        while (!PluginService.IsConnected(asRoot) || !ReferenceEquals(GetRegisteredConnection(asRoot), connection) || service._processorTask.IsCompleted)
        {
            if (service._processorTask.IsCompleted)
            {
                // Registration failed, e.g. because another plugin service is still connected
                await service.DisposeAsync();
                throw service.ProcessorException ?? new InvalidOperationException("Plugin service processor terminated");
            }
            await Task.Delay(5, timeout.Token);
        }
        return service;
    }

    /// <summary>
    /// Answer the commands DCS forwards
    /// </summary>
    private async Task RespondAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await using MemoryStream json = await JsonHelper.ReceiveUtf8JsonAsync(_dpsSocket, _cts.Token);
                using JsonDocument command = JsonDocument.Parse(json);
                JsonElement commandElement = command.RootElement.Clone();
                ReceivedCommands.Enqueue(commandElement);
                string response = await Responder(commandElement);
                await _dpsSocket.SendAsync(Encoding.UTF8.GetBytes(response), SocketFlags.None, _cts.Token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Plugin service is shutting down
        }
    }

    /// <summary>
    /// Wait for a forwarded command
    /// </summary>
    /// <param name="command">Name of the command</param>
    /// <returns>Received command</returns>
    public async Task<JsonElement> WaitForCommandAsync(string command)
    {
        using CancellationTokenSource timeout = new(FakeFirmwareHost.DefaultTimeout);
        while (true)
        {
            foreach (JsonElement item in ReceivedCommands)
            {
                if (item.GetProperty("command").GetString() == command)
                {
                    return item;
                }
            }
            await Task.Delay(5, timeout.Token);
        }
    }

    /// <summary>
    /// Get the names of the forwarded commands
    /// </summary>
    public List<string> GetCommandNames()
    {
        List<string> names = [];
        foreach (JsonElement item in ReceivedCommands)
        {
            names.Add(item.GetProperty("command").GetString()!);
        }
        return names;
    }

    /// <summary>
    /// Close the DPS end of the connection like a crashed DPS, without stopping the processor in DCS
    /// </summary>
    public void Crash() => _dpsSocket.Dispose();

    /// <summary>
    /// Wait for the processor in DCS to terminate on its own, e.g. after another plugin service took over
    /// </summary>
    /// <returns>True if it terminated in time</returns>
    public async Task<bool> WaitForTerminationAsync()
    {
        Task finished = await Task.WhenAny(_processorTask!, Task.Delay(FakeFirmwareHost.DefaultTimeout));
        return finished == _processorTask;
    }

    /// <summary>
    /// Exception the processor terminated with, other than its cancellation
    /// </summary>
    public Exception? ProcessorException { get; private set; }

    private bool _disposed;

    /// <summary>
    /// Unregister the plugin service and wait for the processor to clean up
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await _cts.CancelAsync();
        try
        {
            if (_processorTask is not null)
            {
                await _processorTask.WaitAsync(FakeFirmwareHost.DefaultTimeout);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the processor is cancelled
        }
        catch (Exception e)
        {
            ProcessorException = e;
        }
        try
        {
            if (_responderTask is not null)
            {
                await _responderTask.WaitAsync(FakeFirmwareHost.DefaultTimeout);
            }
        }
        catch (Exception e)
        {
            ProcessorException ??= e;
        }
        Connection.Dispose();
        _dpsSocket.Dispose();
        _dcsSocket.Dispose();
        _listener.Dispose();
        System.IO.File.Delete(_socketPath);
        _cts.Dispose();
        Assert.That(PluginService.IsConnected(IsRoot) && ReferenceEquals(GetRegisteredConnection(IsRoot), Connection), Is.False, "Plugin service is still registered");
    }

    /// <summary>
    /// Get the connection of the registered plugin service (private static field of PluginService)
    /// </summary>
    private static Connection? GetRegisteredConnection(bool asRoot)
    {
        object? service = typeof(PluginService).GetField(asRoot ? "_rootService" : "_service", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        return (service as PluginService)?.Connection;
    }
}
