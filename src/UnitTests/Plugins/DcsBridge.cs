using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.Utility;
using DuetControlServer.Commands;
using DuetControlServer.IPC;
using DuetControlServer.IPC.Processors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UnitTests.FakeFirmware;

namespace UnitTests.Plugins;

/// <summary>
/// IPC endpoint of DCS as DuetPluginService (DPS) sees it. It accepts the connections of DPS on its own UNIX socket and serves them with the real
/// Command and PluginService processors of the given DCS host, in the steps of Server.ProcessConnectionAsync (DuetControlServer/IPC/Server.cs:148-198):
/// server init message, client init message, success response, processor. Only Connection.AssignPermissionsAsync is replaced, because it grants the
/// internal ServicePlugins permission only to the DuetPluginService executable next to DCS (Connection.cs:97-106 and 162-176 via IsDsfService), which
/// the test process is not. The bridge assigns what AssignPermissionsAsync gives DPS instead: ServicePlugins plus the external program permissions
/// (Connection.cs:103-105 and 173-176, GrantExternalPermissions at Connection.cs:230-240) for the regular service, everything and IsRoot for a peer
/// with uid 0 (Connection.cs:84-91)
/// </summary>
public sealed class DcsBridge : IAsyncDisposable
{
    private readonly FakeFirmwareHost _host;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connections = [];
    private readonly Task _acceptTask;

    private DcsBridge(FakeFirmwareHost host, Socket listener, string socketPath, bool asRoot)
    {
        _host = host;
        _listener = listener;
        SocketPath = socketPath;
        AsRoot = asRoot;
        _acceptTask = Task.Run(AcceptAsync);
    }

    /// <summary>
    /// Path of the UNIX socket that DPS connects to (Settings.SocketPath of DPS)
    /// </summary>
    public string SocketPath { get; }

    /// <summary>
    /// Whether the connections get the permissions of the root plugin service
    /// </summary>
    public bool AsRoot { get; }

    /// <summary>
    /// Modes of the connections that DPS made, in order
    /// </summary>
    public ConcurrentQueue<ConnectionMode> Modes { get; } = new();

    /// <summary>
    /// Unexpected exceptions of the served connections
    /// </summary>
    public ConcurrentQueue<Exception> Exceptions { get; } = new();

    /// <summary>
    /// DCS side of the plugin service connection, once DPS has registered
    /// </summary>
    public Connection? PluginServiceConnection { get; private set; }

    /// <summary>
    /// Start a bridge for the given DCS host, which must run the IPC services (FakeFirmwareHost.StartAsync with ipc: true)
    /// </summary>
    /// <param name="host">DCS host</param>
    /// <param name="asRoot">Whether the connections get the permissions of the root plugin service</param>
    /// <returns>Listening bridge</returns>
    public static DcsBridge Start(FakeFirmwareHost host, bool asRoot = false)
    {
        // The socket path must stay below the 108 characters of sockaddr_un
        string socketPath = Path.Combine(host.BaseDirectory, "run", $"bridge-{Guid.NewGuid():N}"[..15] + ".sock");
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(8);
        return new DcsBridge(host, listener, socketPath, asRoot);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                Socket socket = await _listener.AcceptAsync(_cts.Token);
                lock (_connections)
                {
                    _connections.Add(Task.Run(() => ServeAsync(socket)));
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Bridge is shutting down
        }
    }

    private async Task ServeAsync(Socket socket)
    {
        using Connection connection = new(socket, _host.Services.GetRequiredService<CommandFactory>(), _host.Services.GetRequiredService<ILogger<Server>>());
        try
        {
            SbcPermissions permissions = SbcPermissions.ServicePlugins;
            foreach (SbcPermissions permission in Enum.GetValues<SbcPermissions>())
            {
                if (permission != SbcPermissions.SuperUser || AsRoot)
                {
                    permissions |= permission;
                }
            }
            typeof(Connection).GetProperty(nameof(Connection.Permissions))!.SetValue(connection, permissions);
            if (AsRoot)
            {
                typeof(Connection).GetProperty(nameof(Connection.IsRoot))!.SetValue(connection, true);
            }

            await connection.SendInitMessageAsync(new ServerInitMessage { Id = connection.Id });
            ClientInitMessage initMessage = await connection.ReceiveInitMessageAsync(_cts.Token);
            connection.ApiVersion = initMessage.Version;
            ProcessorFactory processorFactory = _host.Services.GetRequiredService<ProcessorFactory>();
            IProcessor processor = initMessage.Mode switch
            {
                ConnectionMode.Command => processorFactory.Create<Command>(connection, initMessage),
                ConnectionMode.PluginService => processorFactory.Create<PluginService>(connection, initMessage),
                _ => throw new ArgumentException($"DPS does not use connection mode {initMessage.Mode}")
            };
            Modes.Enqueue(initMessage.Mode);
            if (initMessage.Mode == ConnectionMode.PluginService)
            {
                PluginServiceConnection = connection;
            }

            await connection.SendResponseAsync();
            try
            {
                await processor.ProcessAsync(_cts.Token);
            }
            finally
            {
                (processor as IDisposable)?.Dispose();
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException)
        {
            // Connection closed or bridge shutting down
        }
        catch (Exception e)
        {
            Exceptions.Enqueue(e);
        }
    }

    private bool _disposed;

    /// <summary>
    /// Stop accepting connections and terminate the served ones. The PluginService processor unregisters the plugin service when it ends
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await _cts.CancelAsync();
        _listener.Dispose();
        await _acceptTask.WaitAsync(FakeFirmwareHost.DefaultTimeout);
        Task[] connections;
        lock (_connections)
        {
            connections = [.. _connections];
        }
        try
        {
            await Task.WhenAll(connections).WaitAsync(FakeFirmwareHost.DefaultTimeout);
        }
        finally
        {
            System.IO.File.Delete(SocketPath);
            _cts.Dispose();
        }
    }
}
