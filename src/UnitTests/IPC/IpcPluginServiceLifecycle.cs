using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetControlServer.IPC;
using DuetControlServer.IPC.Processors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// Requests that DCS forwards to DuetPluginService (DuetControlServer/IPC/Processors/PluginService.cs) when the service is missing, refused,
/// slow or gone, with <see cref="FakePluginService"/> in place of DPS. Every forwarded request must end with a response to the client that
/// started it. <see cref="IpcPluginService"/> covers the plugin commands themselves
/// </summary>
[TestFixture]
public class IpcPluginServiceLifecycle
{
    private FakeFirmwareHost _host = null!;
    private IpcLog _log = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await FakeFirmwareHost.StartAsync(configureSettings: settings =>
        {
            settings.PluginDirectory = Path.Combine(settings.BaseDirectory, "plugins");
            settings.PluginsFilename = Path.Combine(settings.BaseDirectory, "plugins.txt");
        }, ipc: true);
        _log = IpcLog.Attach(_host);
    }

    [OneTimeTearDown]
    public async Task StopHost() => await _host.DisposeAsync();

    [SetUp]
    public void ClearLog() => _log.Clear();

    [TearDown]
    public async Task CleanUp()
    {
        Assert.That(PluginService.IsConnected(false) || PluginService.IsConnected(true), Is.False, "A plugin service was left registered");
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Plugins.Clear();
            _host.Model.State.PluginsStarted = false;
        }
        Assert.That(_log.UnexpectedExceptions, Is.Empty, "The IPC server ran into an exception it did not expect");
    }

    /// <summary>
    /// Connect a command connection. It must connect while no plugin service is registered, otherwise the test process is refused as untracked peer
    /// </summary>
    private async Task<CommandConnection> ConnectAsync()
    {
        CommandConnection connection = new();
        await connection.ConnectAsync(_host.SocketPath);
        return connection;
    }

    private async Task AddPluginAsync(string id, int pid = -1, SbcPermissions permissions = SbcPermissions.None, string? executable = "plugin")
    {
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Plugins.Add(id, new Plugin { Id = id, Name = id, Pid = pid, SbcPermissions = permissions, SbcExecutable = executable });
        }
    }

    [Test]
    public async Task RootPluginNeedsTheRootService()
    {
        // A plugin with SuperUser is started by the root plugin service (StartPlugin.cs: rootPlugin), which refuses while it is not there (PluginService.cs:109-116).
        // Root plugins need root plugin support for that
        DuetControlServer.Settings settings = _host.Services.GetRequiredService<IOptions<DuetControlServer.Settings>>().Value;
        settings.RootPluginSupport = true;
        try
        {
            using CommandConnection connection = await ConnectAsync();
            await AddPluginAsync("RootOnly", permissions: SbcPermissions.SuperUser);
            await using FakePluginService service = await FakePluginService.StartAsync(_host);
            InternalServerException e = (await Assert.ThrowsAsync<InternalServerException>(async () => await connection.StartPluginAsync("RootOnly")))!;
            Assert.That(e.Message, Does.Contain("Cannot perform command because the plugin service (root) is not started"));
            Assert.That(service.GetCommandNames(), Does.Not.Contain("StartPlugin"));
        }
        finally
        {
            settings.RootPluginSupport = false;
        }
    }

    [Test]
    public async Task PluginWithoutSbcExecutableIsNotStarted()
    {
        // DWC-only plugins have no SBC executable, StartPlugin does nothing for them (StartPlugin.cs: string.IsNullOrEmpty(plugin.SbcExecutable))
        using CommandConnection connection = await ConnectAsync();
        await AddPluginAsync("DwcOnly", executable: null);
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        await connection.StartPluginAsync("DwcOnly");
        Assert.That(service.GetCommandNames(), Does.Not.Contain("StartPlugin"));
    }

    [Test]
    public async Task SecondRootServiceIsRefusedWhileTheFirstIsAlive()
    {
        // PluginService.cs:195-203
        await using FakePluginService first = await FakePluginService.StartAsync(_host, asRoot: true);
        InvalidOperationException e = (await Assert.ThrowsAsync<InvalidOperationException>(async () => await FakePluginService.StartAsync(_host, asRoot: true)))!;
        Assert.That(e.Message, Is.EqualTo("Plugin service (root) is already connected"));
    }

    /// <summary>
    /// The second plugin service waits for a full model update before DCS starts the plugins (PluginService.ProcessAsync). A service that goes away during that
    /// wait must not stay registered, else DCS sends its commands to a processor that no longer reads them. The update lock of the model is held here, so the
    /// wait cannot end before the service is closed
    /// </summary>
    [Test]
    public async Task ServiceClosedBeforeThePluginsAreStartedIsUnregistered()
    {
        Nito.AsyncEx.AsyncLock updateLock = (Nito.AsyncEx.AsyncLock)typeof(DuetControlServer.Model.ObjectModel)
            .GetField("_updateLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_host.Model)!;
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        using (await updateLock.LockAsync())
        {
            FakePluginService rootService = await FakePluginService.StartAsync(_host, asRoot: true);
            Assert.That(PluginService.IsConnected(true), Is.True);

            // FakePluginService.DisposeAsync checks that the processor unregistered the service
            await rootService.DisposeAsync();
            Assert.That(rootService.ProcessorException, Is.Null);
            Assert.That(PluginService.IsConnected(true), Is.False, "The closed root service is still registered");
        }
    }

    [Test]
    public async Task PluginServiceIsRefusedWithoutPluginSupport()
    {
        // PluginService.cs:182-185
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(configureSettings: settings => settings.PluginSupport = false, ipc: true);
        NotSupportedException e = (await Assert.ThrowsAsync<NotSupportedException>(async () => await FakePluginService.StartAsync(host)))!;
        Assert.That(e.Message, Is.EqualTo("Plugin support has been disabled"));
        Assert.That(PluginService.IsConnected(false), Is.False);
    }

    /// <summary>
    /// Start a plugin and let the plugin service crash while it handles the request
    /// </summary>
    /// <returns>Task of the StartPlugin call</returns>
    private async Task<Task> StartPluginAndCrashAsync(CommandConnection connection, FakePluginService service, TaskCompletionSource release)
    {
        TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Responder = async command =>
        {
            received.TrySetResult();
            await release.Task;
            return FakePluginService.DefaultResponse(command);
        };
        Task start = connection.StartPluginAsync("Crashing");
        await received.Task.WaitAsync(FakeFirmwareHost.DefaultTimeout);
        service.Crash();
        return start;
    }

    [Test]
    public async Task CommandDoesNotHangWhenThePluginServiceDiesWhileRunningIt()
    {
        // The receive of the response fails with a SocketException, which is passed on to the waiting command (PluginService.cs:269-280)
        using CommandConnection connection = await ConnectAsync();
        await AddPluginAsync("Crashing");
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task start = await StartPluginAndCrashAsync(connection, service, release);
            Assert.That(async () => await start.WaitAsync(FakeFirmwareHost.DefaultTimeout), Throws.Exception.Not.TypeOf<TimeoutException>());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>
    /// PluginService.ProcessAsync passes the SocketException of the lost plugin service connection to the waiting command (PluginService.cs:269-280,
    /// "Unexpected exception"), but Command.ProcessAsync takes every SocketException for the loss of its own client connection (Command.cs:126-130,
    /// "Connection has been terminated") and closes it without a response. The client should get the error like for any other failed command
    /// and keep its connection
    /// </summary>
    [Test]
    [Explicit("Bug: a client whose plugin command was in flight when the plugin service died loses its connection instead of getting an error response")]
    public async Task CommandFailsWithAnErrorWhenThePluginServiceDiesWhileRunningIt()
    {
        using CommandConnection connection = await ConnectAsync();
        await AddPluginAsync("Crashing");
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task start = await StartPluginAndCrashAsync(connection, service, release);
            InternalServerException e = (await Assert.ThrowsAsync<InternalServerException>(async () => await start.WaitAsync(FakeFirmwareHost.DefaultTimeout)))!;
            Assert.That(e.Type, Is.EqualTo("SocketException"));
            Assert.That(await connection.CheckPasswordAsync(string.Empty), Is.True, "The connection is still usable");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Test]
    public async Task PendingRequestsAreCancelledWhenThePluginServiceUnregisters()
    {
        // The processor forwards one request at a time. When it ends, the request in flight fails with its cancellation and the queued ones
        // are cancelled (PluginService.cs:282-286, 326-330), so both clients get a response
        using CommandConnection starter = await ConnectAsync();
        using CommandConnection notifier = await ConnectAsync();
        await AddPluginAsync("Slow");
        FakePluginService service = await FakePluginService.StartAsync(_host);
        TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Responder = async command =>
        {
            received.TrySetResult();
            await release.Task;
            return FakePluginService.DefaultResponse(command);
        };
        Task disposal = Task.CompletedTask;
        try
        {
            Task start = starter.StartPluginAsync("Slow");
            await received.Task.WaitAsync(FakeFirmwareHost.DefaultTimeout);

            // An external program without plugin ID asks DPS to resolve its PID (Connection.ResolvePeerPluginIdAsync), which has to wait
            Task notify = notifier.NotifyPluginStartedAsync();
            await IpcStatics.WaitUntilAsync(() => IpcStatics.GetPendingPluginServiceCommands(false) == 1, "the second request is queued");

            disposal = service.DisposeAsync().AsTask();
            Assert.That(async () => await start.WaitAsync(FakeFirmwareHost.DefaultTimeout), Throws.InstanceOf<TaskCanceledException>(), "Request in flight");
            Assert.That(async () => await notify.WaitAsync(FakeFirmwareHost.DefaultTimeout), Throws.InstanceOf<TaskCanceledException>(), "Queued request");
            Assert.That(IpcStatics.GetPendingPluginServiceCommands(false), Is.Zero);
        }
        finally
        {
            release.TrySetResult();
            await disposal;
            await service.DisposeAsync();
        }
    }

    [Test]
    public async Task PluginConnectingWhileItIsStartedGetsItsPermissionsFromThePluginService()
    {
        // While StartPlugin runs, the new plugin process may connect before DPS has reported its PID with SetPluginProcess. Connection.AssignPermissionsAsync
        // asks DPS then (Connection.cs:109-128). DPS and the plugin are separate processes in a real system; here both are the test process, so the PID of
        // the plugin service is changed to one that is not the test process, else DCS would treat the plugin connection as one of DPS itself
        using CommandConnection connection = await ConnectAsync();
        SbcPermissions permissions = SbcPermissions.ObjectModelRead | SbcPermissions.CommandExecution;
        await AddPluginAsync("Starting", pid: -1, permissions: permissions);
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        PropertyInfo servicePid = typeof(PluginService).GetProperty(nameof(PluginService.ServicePid), BindingFlags.Public | BindingFlags.Static)!;
        servicePid.SetValue(null, 1);

        Task<RawIpcClient>? pluginConnect = null;
        service.Responder = async command =>
        {
            switch (command.GetProperty("command").GetString())
            {
                case "StartPlugin":
                    // The plugin connects right after it has been spawned and DCS asks DPS to resolve it, which waits behind StartPlugin
                    pluginConnect = RawIpcClient.ConnectAsync(_host.SocketPath!);
                    await IpcStatics.WaitUntilAsync(() => IpcStatics.GetPendingPluginServiceCommands(false) == 1, "DCS asks the plugin service to resolve the plugin");
                    return FakePluginService.DefaultResponse(command);
                case "ResolvePluginProcess":
                    return (command.GetProperty("pid").GetInt32() == Environment.ProcessId) ? "{\"success\":true,\"result\":\"Starting\"}" : "{\"success\":true,\"result\":null}";
                default:
                    return FakePluginService.DefaultResponse(command);
            }
        };

        await connection.StartPluginAsync("Starting").WaitAsync(FakeFirmwareHost.DefaultTimeout);
        Assert.That(pluginConnect, Is.Not.Null);
        using RawIpcClient plugin = await pluginConnect!.WaitAsync(FakeFirmwareHost.DefaultTimeout);
        using (JsonDocument response = await plugin.RequestAsync("{\"mode\":\"Subscribe\",\"version\":13,\"subscriptionMode\":\"Patch\",\"filters\":[\"state/status\"]}"))
        {
            Assert.That(RawIpcClient.AssertResponse(response.RootElement), Is.True, response.RootElement.ToString());
        }
        Connection? pluginConnection = IpcStatics.FindConnection(plugin.Id);
        Assert.That(pluginConnection, Is.Not.Null);
        Assert.That(pluginConnection!.PluginId, Is.EqualTo("Starting"));
        Assert.That(pluginConnection.Permissions, Is.EqualTo(permissions));
        Assert.That(service.ReceivedCommands.Count(command => command.GetProperty("command").GetString() == "ResolvePluginProcess"), Is.EqualTo(1));
    }

    [Test]
    public async Task ResponsesOfThePluginServiceMayListTheirMembersInAnyOrder()
    {
        // JSON members have no order, and DCS reads the responses of DPS accordingly: the result may come before success (Connection.PerformCommandAsync,
        // Connection.cs:477-479 and 493-501) and other members may come first (Connection.ReceiveResponseAsync, Connection.cs:417-420).
        // A member whose value is an object or array must not come before success: after Skip the reader stands on the end of that value and the
        // loop stops with "missing success key", the same limitation as for init messages (see IpcServer.ModeIsReadFromAnyPosition)
        using CommandConnection external = await ConnectAsync();
        using CommandConnection connection = await ConnectAsync();
        await AddPluginAsync("Ordered");
        await AddPluginAsync("Resolved");
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        service.Responder = command => Task.FromResult(command.GetProperty("command").GetString() switch
        {
            "ResolvePluginProcess" => "{\"result\":\"Resolved\",\"success\":true}",
            _ => "{\"extra\":\"success\",\"other\":null,\"success\":true}"
        });

        await connection.StartPluginAsync("Ordered");
        await external.NotifyPluginStartedAsync();
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.Plugins["Resolved"].Started, Is.True);
        }

        // An error response for ResolvePluginProcess counts as an unknown process (Connection.ResolvePluginViaServiceAsync logs it and returns null)
        using CommandConnection another = new();
        await using (FakePluginService replacement = await ReplaceAsync(service, another))
        {
            replacement.Responder = _ => Task.FromResult("{\"errorType\":\"IOException\",\"errorMessage\":\"broken\",\"success\":false}");
            InternalServerException e = (await Assert.ThrowsAsync<InternalServerException>(async () => await another.NotifyPluginStartedAsync()))!;
            Assert.That(e.Message, Does.Contain("Failed to determine plugin ID"));
            Assert.That(_log.Entries.Any(entry => entry.Message.Contains("Failed to query plugin service")), Is.True);
        }
    }

    /// <summary>
    /// Unregister a plugin service, connect a command connection while none is registered and register a new one
    /// </summary>
    private async Task<FakePluginService> ReplaceAsync(FakePluginService service, CommandConnection connection)
    {
        await service.DisposeAsync();
        await connection.ConnectAsync(_host.SocketPath);
        return await FakePluginService.StartAsync(_host);
    }

    [Test]
    public async Task StopPluginsStopsEveryRunningPlugin()
    {
        // StopPlugins forwards StopPlugin for every plugin with a PID and clears State.PluginsStarted (StopPlugins.cs)
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Command\",\"version\":13}");
        await AddPluginAsync("RunningA", pid: 100);
        await AddPluginAsync("RunningB", pid: 101);
        await AddPluginAsync("Stopped", pid: -1);
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.State.PluginsStarted = true;
        }
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        using (JsonDocument response = await client.RequestAsync("{\"command\":\"StopPlugins\"}"))
        {
            Assert.That(RawIpcClient.AssertResponse(response.RootElement), Is.True, response.RootElement.ToString());
        }
        Assert.That(service.ReceivedCommands.Where(command => command.GetProperty("command").GetString() == "StopPlugin").Select(command => command.GetProperty("plugin").GetString()),
                    Is.EquivalentTo(new[] { "RunningA", "RunningB" }));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.State.PluginsStarted, Is.False);
            Assert.That(_host.Model.Plugins["RunningA"].Pid, Is.Zero, "Being shut down (StopPlugin.cs), DPS reports -1 with SetPluginProcess");
        }
    }
}
