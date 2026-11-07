using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// Every call of the DuetAPIClient command connection, synchronous and asynchronous, against the DCS IPC server.
/// The expected results follow from the command implementations in DuetControlServer/Commands
/// </summary>
[TestFixture]
public class IpcClientApi
{
    private FakeFirmwareHost _host = null!;
    private IpcLog _log = null!;
    private CommandConnection _connection = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await FakeFirmwareHost.StartAsync(configureSettings: settings =>
        {
            settings.PluginDirectory = Path.Combine(settings.BaseDirectory, "plugins");
            settings.PluginsFilename = Path.Combine(settings.BaseDirectory, "plugins.txt");
        }, ipc: true);
        _log = IpcLog.Attach(_host);
        _connection = new CommandConnection();
        await _connection.ConnectAsync(_host.SocketPath);
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        _connection.Dispose();
        await _host.DisposeAsync();
    }

    [TearDown]
    public void CheckLog() => Assert.That(_log.UnexpectedExceptions, Is.Empty, "The IPC server ran into an exception it did not expect");

    [Test]
    public async Task Codes()
    {
        Assert.That(_connection.PerformSimpleCode("echo 1 + 1", CodeChannel.SBC), Is.EqualTo("2\n"));
        Assert.That(await _connection.PerformSimpleCodeAsync("echo 2 + 2", CodeChannel.SBC), Is.EqualTo("4\n"));
        Assert.That(_connection.PerformSimpleCode("echo 3", CodeChannel.SBC, false), Is.EqualTo("3\n"));
        Assert.That(await _connection.PerformSimpleCodeAsync("echo 4", CodeChannel.SBC, false), Is.EqualTo("4\n"));

        // A parse error is reported in the reply, not as an exception (SimpleCode.ExecuteAsync)
        Assert.That(await _connection.PerformSimpleCodeAsync("G1 X{", CodeChannel.SBC), Does.StartWith("Error: "));

        Message result = _connection.PerformCode(new Code("echo 5") { Channel = CodeChannel.SBC });
        Assert.That(result.Content, Is.EqualTo("5\n"));
        result = await _connection.PerformCodeAsync(new Code("echo 6") { Channel = CodeChannel.SBC });
        Assert.That(result.Content, Is.EqualTo("6\n"));

        Assert.That(_connection.Flush(CodeChannel.SBC), Is.True);
        Assert.That(await _connection.FlushAsync(CodeChannel.SBC), Is.True);

        Assert.That(_connection.EvaluateExpression("1 + 2").GetInt32(), Is.EqualTo(3));
        Assert.That((await _connection.EvaluateExpressionAsync("move.axes[0].letter")).GetString(), Is.EqualTo("X"));
    }

    [Test]
    public async Task ObjectModel()
    {
        ObjectModel model = _connection.GetObjectModel();
        Assert.That(model.Move.Axes, Has.Count.EqualTo(3));
        model = await _connection.GetObjectModelAsync();
        Assert.That(model.State.Status, Is.EqualTo(MachineStatus.Idle));

        model = _connection.GetObjectModel("move/axes[1]/letter", "state/status");
        Assert.That(model.Move.Axes[1].Letter, Is.EqualTo('Y'));
        model = await _connection.GetObjectModelAsync("move/axes[2]/letter");
        Assert.That(model.Move.Axes[2].Letter, Is.EqualTo('Z'));
        model = await _connection.GetObjectModelAsync(["state/status"]);
        Assert.That(model.State.Status, Is.EqualTo(MachineStatus.Idle));

        using JsonDocument serialized = JsonDocument.Parse(_connection.GetSerializedObjectModel());
        Assert.That(serialized.RootElement.GetProperty("move").GetProperty("axes").GetArrayLength(), Is.EqualTo(3));
        using JsonDocument serializedAsync = JsonDocument.Parse(await _connection.GetSerializedObjectModelAsync());
        Assert.That(serializedAsync.RootElement.GetProperty("state").GetProperty("status").GetString(), Is.EqualTo("idle"));

        Assert.That(_connection.QueryObjectModel("state.status", "").GetProperty("result").GetString(), Is.EqualTo("idle"));
        Assert.That((await _connection.QueryObjectModelAsync("move.axes", "f")).GetProperty("key").GetString(), Is.EqualTo("move.axes"));

        _connection.SyncObjectModel();
        await _connection.SyncObjectModelAsync();

        // PatchObjectModel is refused unless custom patches are allowed (PatchObjectModel.ExecuteAsync)
        using JsonDocument patch = JsonDocument.Parse("{\"status\":\"busy\"}");
        Assert.Throws<InternalServerException>(() => _connection.PatchObjectModel("state", patch.RootElement));
        await Assert.ThrowsAsync<InternalServerException>(() => _connection.PatchObjectModelAsync("state", patch.RootElement));

        _connection.SetWifiCountry("DE");
        await _connection.SetWifiCountryAsync(null);
    }

    [Test]
    public async Task UpdateStatus()
    {
        try
        {
            _connection.SetUpdateStatus(true);
            await _connection.SetUpdateStatusAsync("Updating", 0.5F);
            using (await _host.Model.AccessReadOnlyAsync())
            {
                Assert.That(_host.Model.SBC!.Upgrade!.Message, Is.EqualTo("Updating"));
                Assert.That(_host.Model.SBC.Upgrade.Progress, Is.EqualTo(0.5F));
            }
            _connection.SetUpdateStatus("Almost done", 0.9F);
        }
        finally
        {
            await _connection.SetUpdateStatusAsync(false);
        }
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.SBC!.Upgrade, Is.Null);
        }
    }

    [Test]
    public async Task MessagesAndPassword()
    {
        _connection.WriteMessage(MessageType.Success, "sync message");
        await _connection.WriteMessageAsync(MessageType.Warning, "async message", false, EventLogLevel.Off);
        _connection.WriteMessage(new Message(MessageType.Error, "message object"), true, EventLogLevel.Warn);
        await _connection.WriteMessageAsync(new Message(MessageType.Success, "async message object"));
        Assert.That(_log.Entries.Any(entry => entry.Message.Contains("async message")), Is.True, "A message that is neither output nor logged goes to the DCS log");

        Assert.That(_connection.CheckPassword("anything"), Is.True, "No password is set");
        Assert.That(await _connection.CheckPasswordAsync("anything"), Is.True);
    }

    [Test]
    public async Task Files()
    {
        Assert.That(_connection.ResolvePath("0:/sys/config.g"), Is.EqualTo(Path.Combine(_host.BaseDirectory, "sys", "config.g")));
        Assert.That(await _connection.ResolvePathAsync("0:/macros/a.g"), Is.EqualTo(Path.Combine(_host.BaseDirectory, "macros", "a.g")));
        Assert.That(_connection.ResolvePath("b.g", FileDirectory.Macros), Is.EqualTo(Path.Combine(_host.BaseDirectory, "macros", "b.g")));
        Assert.That(await _connection.ResolvePathAsync("c.g", FileDirectory.GCodes), Is.EqualTo(Path.Combine(_host.BaseDirectory, "gcodes", "c.g")));

        // GetFileInfo takes a physical path (FileInfoParser.ParseAsync opens it as is)
        string file = Path.Combine(_host.BaseDirectory, "gcodes", "info.gcode");
        await System.IO.File.WriteAllTextAsync(file, ";Generated with Cura_SteamEngine 5.0\n;LAYER_COUNT:2\nG1 Z0.2\nG1 Z0.4\n");
        GCodeFileInfo info = _connection.GetFileInfo(file);
        Assert.That(info.Size, Is.GreaterThan(0));
        info = await _connection.GetFileInfoAsync(file);
        Assert.That(info.FileName, Is.EqualTo("0:/gcodes/info.gcode"));
        info = _connection.GetFileInfo(file, true);
        Assert.That(info.Thumbnails, Is.Empty);
        info = await _connection.GetFileInfoAsync(file, false);
        Assert.That(info.Size, Is.GreaterThan(0));

        InternalServerException e = Assert.Throws<InternalServerException>(() => _connection.GetFileInfo(Path.Combine(_host.BaseDirectory, "gcodes", "missing.gcode")))!;
        Assert.That(e.Type, Is.EqualTo(nameof(FileNotFoundException)));
        Assert.That(e.Command, Is.EqualTo(nameof(GetFileInfo)));
    }

    [Test]
    public async Task UserSessions()
    {
        int first = _connection.AddUserSession(AccessLevel.ReadWrite, SessionType.HTTP, "1.2.3.4");
        int second = await _connection.AddUserSessionAsync(AccessLevel.ReadOnly, SessionType.Telnet);
        Assert.That(second, Is.GreaterThan(first));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.SBC!.DSF.UserSessions.Single(session => session.Id == second).Origin, Is.EqualTo(Environment.ProcessId.ToString()));
        }
        Assert.That(_connection.RemoveUserSession(first), Is.True);
        Assert.That(await _connection.RemoveUserSessionAsync(second), Is.True);
        Assert.That(_connection.RemoveUserSession(second), Is.False);
    }

    [Test]
    public async Task HttpEndpoints()
    {
        using (HttpEndpointUnixSocket endpoint = _connection.AddHttpEndpoint(HttpEndpointType.GET, "ipctest", "sync"))
        {
            Assert.That(endpoint.SocketPath, Is.EqualTo(Path.Combine(_host.BaseDirectory, "run", "ipctest", "sync-GET.sock")));
            Assert.That(System.IO.File.Exists(endpoint.SocketPath), Is.True);

            // AddHttpEndpoint refuses an endpoint that is registered and served
            Assert.Throws<InternalServerException>(() => _connection.AddHttpEndpoint(HttpEndpointType.GET, "ipctest", "sync"));
        }

        using (HttpEndpointUnixSocket endpoint = await _connection.AddHttpEndpointAsync(HttpEndpointType.POST, "ipctest", "async", true))
        {
            Assert.That(endpoint.EndpointType, Is.EqualTo(HttpEndpointType.POST));
            Assert.That(endpoint.Namespace, Is.EqualTo("ipctest"));
            Assert.That(endpoint.EndpointPath, Is.EqualTo("async"));
        }

        // An endpoint whose socket is no longer served may be registered again and gets the same socket
        using (HttpEndpointUnixSocket endpoint = _connection.AddHttpEndpoint(HttpEndpointType.GET, "ipctest", "sync"))
        {
            Assert.That(endpoint.SocketPath, Does.EndWith("sync-GET.sock"));
        }

        Assert.That(_connection.RemoveHttpEndpoint(HttpEndpointType.GET, "ipctest", "sync"), Is.True);
        Assert.That(await _connection.RemoveHttpEndpointAsync(HttpEndpointType.POST, "ipctest", "async"), Is.True);
        Assert.That(await _connection.RemoveHttpEndpointAsync(HttpEndpointType.POST, "ipctest", "async"), Is.False);

        // Some namespaces are reserved by DWS
        await Assert.ThrowsAsync<InternalServerException>(() => _connection.AddHttpEndpointAsync(HttpEndpointType.GET, "fileinfo", "x"));
    }

    [Test]
    public async Task PluginsWithoutPluginService()
    {
        // Without DPS the plugin commands fail once they need it, or before when the plugin does not exist
        Assert.Throws<InternalServerException>(() => _connection.StartPlugin("NoSuchPlugin"));
        await Assert.ThrowsAsync<InternalServerException>(() => _connection.StartPluginAsync("NoSuchPlugin"));
        Assert.Throws<InternalServerException>(() => _connection.StopPlugin("NoSuchPlugin"));
        await Assert.ThrowsAsync<InternalServerException>(() => _connection.StopPluginAsync("NoSuchPlugin"));
        Assert.Throws<InternalServerException>(() => _connection.UninstallPlugin("NoSuchPlugin"));
        await Assert.ThrowsAsync<InternalServerException>(() => _connection.UninstallPluginAsync("NoSuchPlugin"));
        Assert.Throws<InternalServerException>(() => _connection.InstallPlugin(Path.Combine(_host.BaseDirectory, "missing.zip")));
        await Assert.ThrowsAsync<InternalServerException>(() => _connection.InstallPluginAsync(Path.Combine(_host.BaseDirectory, "missing.zip")));
        Assert.Throws<InternalServerException>(() => _connection.NotifyPluginStarted());
        await Assert.ThrowsAsync<InternalServerException>(() => _connection.NotifyPluginStartedAsync("NoSuchPlugin"));
        using JsonDocument value = JsonDocument.Parse("1");
        Assert.Throws<InternalServerException>(() => _connection.SetPluginData("key", value.RootElement, "NoSuchPlugin"));
        await Assert.ThrowsAsync<InternalServerException>(() => _connection.SetPluginDataAsync("key", value.RootElement, "NoSuchPlugin"));

        // ReloadPlugin does nothing for a plugin that is neither installed nor has a manifest (ReloadPlugin.ExecuteAsync)
        _connection.ReloadPlugin("NoSuchPlugin");
        await _connection.ReloadPluginAsync("NoSuchPlugin");

        // Root plugin support is off by default, so system packages cannot be handled
        Assert.Throws<InternalServerException>(() => _connection.InstallSystemPackage("/tmp/does-not-exist.deb"));
        await Assert.ThrowsAsync<InternalServerException>(() => _connection.InstallSystemPackageAsync("/tmp/does-not-exist.deb"));
        Assert.Throws<InternalServerException>(() => _connection.UninstallSystemPackage("does-not-exist"));
        await Assert.ThrowsAsync<InternalServerException>(() => _connection.UninstallSystemPackageAsync("does-not-exist"));
    }

    /// <summary>
    /// DuetAPIClient offers InvalidateChannel ("Invalidate all pending codes and files on the given channel") and DCS implements it
    /// (DuetControlServer/Commands/Generic/InvalidateChannel.cs, added as "new IPC command to invalidate codes and files" in 3.4-b3),
    /// but no IPC processor lists it, so the server refuses it as "unsupported command InvalidateChannel"
    /// </summary>
    [Test]
    [Explicit("Bug: InvalidateChannel is missing from Command.SupportedCommands, so the DuetAPIClient calls always fail")]
    public async Task InvalidateChannel()
    {
        _connection.InvalidateChannel(CodeChannel.Telnet);
        await _connection.InvalidateChannelAsync(CodeChannel.Telnet);
    }

    [Test]
    public void ConnectionState()
    {
        using CommandConnection connection = new();
        connection.Connect(_host.SocketPath);
        Assert.That(connection.IsConnected, Is.True);
        connection.Poll();
        connection.Dispose();
        Assert.That(connection.IsConnected, Is.False);
        Assert.Throws<ObjectDisposedException>(() => connection.Poll());
    }

    [Test]
    public async Task DisposedConnectionThrows()
    {
                using CommandConnection connection = new();
        connection.Connect(_host.SocketPath);
        connection.Close();
        Assert.Throws<ObjectDisposedException>(() => connection.CheckPassword(string.Empty));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.CheckPasswordAsync(string.Empty));
    }

    [Test]
    public async Task CancelledCodeIsReportedAsTaskCanceledException()
    {
        // BaseConnection.PerformCommandAsync turns an error response of type OperationCanceledException into a TaskCanceledException.
        // A code that an interceptor cancels fails with OperationCanceledException (CodeInterception.InterceptAsync)
        using InterceptConnection interceptor = new();
        await interceptor.ConnectAsync(InterceptionMode.Pre, [CodeChannel.SBC], ["M7001"], false, _host.SocketPath);
        Task<Message> code = _connection.PerformCodeAsync(new Code("M7001") { Channel = CodeChannel.SBC });
        Code intercepted = await interceptor.ReceiveCodeAsync().AsTask().WaitAsync(FakeFirmwareHost.DefaultTimeout);
        Assert.That(intercepted.MajorNumber, Is.EqualTo(7001));
        await interceptor.CancelCodeAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await code.WaitAsync(FakeFirmwareHost.DefaultTimeout));

        Task<Message> syncCode = Task.Run(() => _connection.PerformCode(new Code("M7001") { Channel = CodeChannel.SBC }));
        intercepted = await interceptor.ReceiveCodeAsync().AsTask().WaitAsync(FakeFirmwareHost.DefaultTimeout);
        interceptor.CancelCode();
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await syncCode.WaitAsync(FakeFirmwareHost.DefaultTimeout));
    }
}
