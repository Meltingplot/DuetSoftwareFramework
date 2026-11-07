using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetControlServer.IPC;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// Permissions of plugin connections as Connection.AssignPermissionsAsync assigns them (DuetControlServer/IPC/Connection.cs) and the checks
/// that commands make themselves. The check of the RequiredPermissions attribute grants everything (see
/// <see cref="RequiredPermissions.RefusedWithoutRequiredFlag"/>), so refusals by the attribute are only covered by explicit tests
/// </summary>
[TestFixture]
public class IpcPluginPermissions
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
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Plugins.Clear();
        }
        Assert.That(_log.UnexpectedExceptions, Is.Empty, "The IPC server ran into an exception it did not expect");
    }

    private async Task AddPluginAsync(string id, int pid, SbcPermissions permissions)
    {
        using (await _host.Model.AccessReadWriteAsync())
        {
            Plugin plugin = new() { Id = id, Name = id, Pid = pid, SbcPermissions = permissions };
            plugin.Data["value"] = JsonSerializer.SerializeToElement(0);
            _host.Model.Plugins.Add(id, plugin);
        }
    }

    /// <summary>
    /// Init message of a mode that the given permissions allow, so that the connection is kept as a processor that the test can look up
    /// </summary>
    private static string InitMessageFor(SbcPermissions permissions)
    {
        // Acknowledge needs ObjectModelRead or ObjectModelReadWrite, Ignore needs CodeInterceptionRead or CodeInterceptionReadWrite,
        // Code needs CommandExecution (DuetAPI/Commands/*/RequiredPermissions attributes, Server.GetConnectionProcessorAsync)
        if ((permissions & (SbcPermissions.ObjectModelRead | SbcPermissions.ObjectModelReadWrite)) != 0)
        {
            return "{\"mode\":\"Subscribe\",\"version\":13,\"subscriptionMode\":\"Patch\",\"filters\":[\"state/status\"]}";
        }
        if ((permissions & (SbcPermissions.CodeInterceptionRead | SbcPermissions.CodeInterceptionReadWrite)) != 0)
        {
            return "{\"mode\":\"Intercept\",\"version\":13,\"interceptionMode\":\"Pre\",\"channels\":[\"SBC\"],\"filters\":[\"M7701\"]}";
        }
        return "{\"mode\":\"CodeStream\",\"version\":13,\"bufferSize\":1,\"channel\":\"SBC\"}";
    }

    [TestCase(SbcPermissions.ObjectModelRead)]
    [TestCase(SbcPermissions.ObjectModelReadWrite | SbcPermissions.CommandExecution | SbcPermissions.RegisterHttpEndpoints)]
    [TestCase(SbcPermissions.CodeInterceptionRead)]
    [TestCase(SbcPermissions.CodeInterceptionReadWrite | SbcPermissions.CommandExecution | SbcPermissions.ManageUserSessions)]
    [TestCase(SbcPermissions.CommandExecution | SbcPermissions.ReadGCodes | SbcPermissions.WriteGCodes | SbcPermissions.NetworkAccess | SbcPermissions.LaunchProcesses)]
    public async Task PluginConnectionHasExactlyTheManifestPermissions(SbcPermissions permissions)
    {
        // With DPS connected, a peer whose PID belongs to a plugin gets the permissions of that plugin and nothing else (Connection.cs:132-142)
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        await AddPluginAsync("Exact", Environment.ProcessId, permissions);
        using RawIpcClient plugin = await RawIpcClient.ConnectAsync(_host.SocketPath!, InitMessageFor(permissions));

        // Interceptors register in ProcessAsync, which starts after the success response (CodeInterception.cs:161-164, Server.cs:167-170)
        Connection? connection = null;
        await IpcStatics.WaitUntilAsync(() => (connection = IpcStatics.FindConnection(plugin.Id)) is not null, "the processor of the connection is registered");
        Assert.That(connection!.Permissions, Is.EqualTo(permissions));
        Assert.That(connection.PluginId, Is.EqualTo("Exact"));
        Assert.That(connection.IsRoot, Is.False);
    }

    [Test]
    public async Task ExternalProgramGetsEverythingButSuperUserAndServicePlugins()
    {
        // Without DPS, DCS cannot tell plugins apart and treats every peer as external program (Connection.cs:93-107, GrantExternalPermissions)
        SbcPermissions expected = Enum.GetValues<SbcPermissions>().Aggregate(SbcPermissions.None, (all, permission) => all | permission) & ~SbcPermissions.SuperUser & ~SbcPermissions.ServicePlugins;
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!, InitMessageFor(SbcPermissions.ObjectModelRead));
        Connection? connection = IpcStatics.FindConnection(client.Id);
        Assert.That(connection, Is.Not.Null);
        Assert.That(connection!.Permissions, Is.EqualTo(expected));
        Assert.That(connection.PluginId, Is.Null);
    }

    [Test]
    public async Task PluginMayOnlyNotifyItsOwnStart()
    {
        // NotifyPluginStarted for another plugin needs ManagePlugins (NotifyPluginStarted.cs:39-43)
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        await AddPluginAsync("Own", Environment.ProcessId, SbcPermissions.ObjectModelReadWrite);
        await AddPluginAsync("Other", -1, SbcPermissions.None);
        using RawIpcClient plugin = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Command\",\"version\":13}");
        using (JsonDocument response = await plugin.RequestAsync("{\"command\":\"NotifyPluginStarted\",\"plugin\":\"Other\"}"))
        {
            Assert.That(RawIpcClient.GetErrorType(response.RootElement), Is.EqualTo(nameof(UnauthorizedAccessException)));
            Assert.That(response.RootElement.GetProperty("errorMessage").GetString(), Is.EqualTo("Insufficient permissions"));
        }
        using (JsonDocument response = await plugin.RequestAsync("{\"command\":\"NotifyPluginStarted\",\"plugin\":\"Own\"}"))
        {
            Assert.That(RawIpcClient.AssertResponse(response.RootElement), Is.True, response.RootElement.ToString());
        }
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.Plugins["Own"].Started, Is.True);
            Assert.That(_host.Model.Plugins["Other"].Started, Is.False);
        }
    }

    [Test]
    public async Task ManagePluginsMayChangeOtherPlugins()
    {
        // A plugin with ManagePlugins may set the data of other plugins and notify their start (SetPluginData.cs:41, NotifyPluginStarted.cs:40)
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        await AddPluginAsync("Manager", Environment.ProcessId, SbcPermissions.ManagePlugins | SbcPermissions.ObjectModelReadWrite);
        await AddPluginAsync("Managed", -1, SbcPermissions.None);
        using RawIpcClient plugin = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Command\",\"version\":13}");
        using (JsonDocument response = await plugin.RequestAsync("{\"command\":\"SetPluginData\",\"plugin\":\"Managed\",\"key\":\"value\",\"value\":7}"))
        {
            Assert.That(RawIpcClient.AssertResponse(response.RootElement), Is.True, response.RootElement.ToString());
        }
        using (JsonDocument response = await plugin.RequestAsync("{\"command\":\"NotifyPluginStarted\",\"plugin\":\"Managed\"}"))
        {
            Assert.That(RawIpcClient.AssertResponse(response.RootElement), Is.True, response.RootElement.ToString());
        }
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.Plugins["Managed"].Data["value"]!.Value.GetInt32(), Is.EqualTo(7));
            Assert.That(_host.Model.Plugins["Managed"].Started, Is.True);
        }
    }

    /// <summary>
    /// A plugin must only get the connection modes of which it may run at least one command (Server.GetConnectionProcessorAsync with
    /// Connection.CheckCommandPermissions, Server.cs:243-261). RequiredPermissionsAttribute.Check grants everything, so every mode is granted
    /// </summary>
    [TestCase("{\"mode\":\"Intercept\",\"version\":13,\"interceptionMode\":\"Pre\",\"channels\":[\"SBC\"],\"filters\":[\"M7702\"]}", SbcPermissions.ObjectModelRead, Description = "Interception needs CodeInterceptionRead(Write)")]
    [TestCase("{\"mode\":\"Subscribe\",\"version\":13,\"subscriptionMode\":\"Patch\"}", SbcPermissions.CodeInterceptionRead, Description = "Subscriptions need ObjectModelRead(Write)")]
    [TestCase("{\"mode\":\"CodeStream\",\"version\":13,\"bufferSize\":1,\"channel\":\"SBC\"}", SbcPermissions.ObjectModelRead, Description = "Code streams need CommandExecution")]
    [Explicit("Bug: RequiredPermissionsAttribute.Check grants every permission, so a plugin gets connection modes it has no permission for")]
    public async Task PluginIsRefusedModesItHasNoPermissionFor(string initMessage, SbcPermissions permissions)
    {
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        await AddPluginAsync("Restricted", Environment.ProcessId, permissions);
        using RawIpcClient plugin = await RawIpcClient.ConnectAsync(_host.SocketPath!);
        using JsonDocument response = await plugin.RequestAsync(initMessage);
        Assert.That(RawIpcClient.GetErrorType(response.RootElement), Is.EqualTo(nameof(UnauthorizedAccessException)), response.RootElement.ToString());
        Assert.That(response.RootElement.GetProperty("errorMessage").GetString(), Is.EqualTo("Insufficient permissions"));
    }
}
