using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetControlServer.IPC;
using DuetSharedLibrary;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// Tests of the connection set-up of the DCS IPC server: the init messages and what DuetControlServer/IPC/Server.cs and Connection.cs do with them
/// </summary>
[TestFixture]
public class IpcServer
{
    private FakeFirmwareHost _host = null!;
    private IpcLog _log = null!;

    /// <summary>
    /// Time to wait for the server to close a connection
    /// </summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await FakeFirmwareHost.StartAsync(ipc: true);
        _log = IpcLog.Attach(_host);
    }

    [OneTimeTearDown]
    public async Task StopHost() => await _host.DisposeAsync();

    [SetUp]
    public void ClearLog() => _log.Clear();

    [TearDown]
    public void CheckLog()
    {
        IpcStatics.RemoveLeakedCodeStreams();
        Assert.That(_log.UnexpectedExceptions, Is.Empty, "The IPC server ran into an exception it did not expect");
    }

    /// <summary>
    /// Errors from Utf8JsonReader are of the internal type JsonReaderException, which derives from JsonException
    /// </summary>
    public static bool IsJsonError(string? errorType) => errorType is nameof(JsonException) or "JsonReaderException";

    /// <summary>
    /// Expect an error response followed by the server closing the connection
    /// </summary>
    private static async Task<JsonElement> ExpectErrorAndCloseAsync(RawIpcClient client, string errorType)
    {
        (var messages, bool closed) = await client.ReceiveUntilClosedAsync(CloseTimeout);
        Assert.That(messages, Has.Count.EqualTo(1), "Expected exactly one response");
        Assert.That(RawIpcClient.GetErrorType(messages[0].RootElement), Is.EqualTo(errorType), messages[0].RootElement.ToString());
        Assert.That(closed, Is.True, "Server did not close the connection");
        return messages[0].RootElement;
    }

    [Test]
    public async Task ServerInitMessageHasVersionAndId()
    {
        // ServerInitMessage holds the protocol version and the connection ID (Server.ProcessConnectionAsync, Connection.Id)
        using RawIpcClient first = await RawIpcClient.ConnectAsync(_host.SocketPath!);
        using RawIpcClient second = await RawIpcClient.ConnectAsync(_host.SocketPath!);
        Assert.That(second.Id, Is.GreaterThan(first.Id));
    }

    [TestCase(Server.MinimumProtocolVersion)]
    [TestCase(8)]
    [TestCase(11)]
    [TestCase(Defaults.ProtocolVersion)]
    public async Task AcceptsSupportedProtocolVersions(int version)
    {
        // Server.GetConnectionProcessorAsync accepts MinimumProtocolVersion to Defaults.ProtocolVersion and only warns about older ones
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!, $"{{\"mode\":\"Command\",\"version\":{version}}}");
        using JsonDocument response = await client.RequestAsync("{\"command\":\"CheckPassword\",\"password\":\"\"}");
        Assert.That(response.RootElement.GetProperty("result").GetBoolean(), Is.True);
        if (version != Defaults.ProtocolVersion)
        {
            Assert.That(_log.Entries.Any(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning && entry.Message.Contains("outdated protocol version")), Is.True);
        }
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(Server.MinimumProtocolVersion - 1)]
    [TestCase(Defaults.ProtocolVersion + 1)]
    [TestCase(int.MaxValue)]
    public async Task RefusesUnsupportedProtocolVersions(int version)
    {
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!);
        await client.SendAsync($"{{\"mode\":\"Command\",\"version\":{version}}}");
        JsonElement response = await ExpectErrorAndCloseAsync(client, nameof(IncompatibleVersionException));
        Assert.That(response.GetProperty("errorMessage").GetString(), Is.EqualTo($"Incompatible protocol version (got {version}, need {Server.MinimumProtocolVersion} to {Defaults.ProtocolVersion})"));
    }

    [Test]
    public async Task MissingVersionIsUnsupported()
    {
        // ClientInitMessage.Version defaults to 0
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!);
        await client.SendAsync("{\"mode\":\"Command\"}");
        await ExpectErrorAndCloseAsync(client, nameof(IncompatibleVersionException));
    }

    [TestCase("{\"mode\":\"Command\",\"version\":}", Description = "Malformed JSON")]
    [TestCase("{\"mode\":\"NoSuchMode\",\"version\":13}", Description = "The enum converter does not know the mode")]
    [TestCase("{\"mode\":\"Command\",\"version\":\"13\"}", Description = "Number as string")]
    [TestCase("{\"mode\":\"Intercept\",\"version\":13,\"filters\":\"M1234\"}", Description = "String instead of list")]
    [TestCase("{\"mode\":\"CodeStream\",\"version\":13,\"channel\":\"NoSuchChannel\"}", Description = "Unknown channel")]
    [TestCase("{\"mode\":\"Subscribe\",\"version\":13,\"verbose\":1}", Description = "Number instead of bool")]
    public async Task ReportsJsonErrorsAndWaitsForAnotherInitMessage(string initMessage)
    {
        // Connection.ReceiveInitMessageAsync reports a JsonException and reads the next init message
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!);
        using (JsonDocument error = await client.RequestAsync(initMessage))
        {
            Assert.That(IsJsonError(RawIpcClient.GetErrorType(error.RootElement)), Is.True, error.RootElement.ToString());
        }
        using JsonDocument response = await client.RequestAsync("{\"mode\":\"Command\",\"version\":13}");
        Assert.That(RawIpcClient.AssertResponse(response.RootElement), Is.True);
    }

    [TestCase("{}", "missing connection mode")]
    [TestCase("{\"version\":13,\"channel\":\"SBC\"}", "missing connection mode")]
    [TestCase("{\"mode\":1,\"version\":13}", "mode must be a string")]
    [TestCase("{\"mode\":null,\"version\":13}", "mode must be a string")]
    [TestCase("{\"mode\":[\"Command\"],\"version\":13}", "mode must be a string")]
    [TestCase("{\"mode\":\"Unknown\",\"version\":13}", "Invalid connection mode")]
    public async Task RefusesInitMessagesWithoutValidMode(string initMessage, string errorMessage)
    {
        // Connection.ReceiveInitMessageAsync throws an ArgumentException, Server.GetConnectionProcessorAsync reports it and closes the connection
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!);
        await client.SendAsync(initMessage);
        JsonElement response = await ExpectErrorAndCloseAsync(client, nameof(ArgumentException));
        Assert.That(response.GetProperty("errorMessage").GetString(), Is.EqualTo(errorMessage));
    }

    [Test]
    public async Task ModeIsReadFromAnyPosition()
    {
        // The mode is looked up first, then the whole message is deserialized as the init message of that mode.
        // Connection.ReceiveInitMessageAsync skips other members. After a member whose value is an object, the reader stands on the end
        // of that object and the loop stops with "missing connection mode", so this test keeps such members out
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"version\":13,\"unknownKey\":[1,2,{\"mode\":\"Subscribe\"}],\"mode\":\"Command\"}");
        using JsonDocument response = await client.RequestAsync("{\"command\":\"CheckPassword\",\"password\":\"\"}");
        Assert.That(RawIpcClient.AssertResponse(response.RootElement), Is.True);
    }

    [Test]
    public async Task RefusesPluginServiceModeWithoutPermission()
    {
        // Only DPS gets SbcPermissions.ServicePlugins (Connection.AssignPermissionsAsync), Server.GetConnectionProcessorAsync checks it for this mode
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!);
        await client.SendAsync("{\"mode\":\"PluginService\",\"version\":13}");
        JsonElement response = await ExpectErrorAndCloseAsync(client, nameof(UnauthorizedAccessException));
        Assert.That(response.GetProperty("errorMessage").GetString(), Is.EqualTo("Insufficient permissions"));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(Defaults.MaxCodeBufferSize + 1)]
    public async Task RefusesCodeStreamBufferSizeOutOfRange(int bufferSize)
    {
        // The CodeStream constructor accepts 1 to Defaults.MaxCodeBufferSize
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!);
        await client.SendAsync($"{{\"mode\":\"CodeStream\",\"version\":13,\"bufferSize\":{bufferSize}}}");
        JsonElement response = await ExpectErrorAndCloseAsync(client, nameof(ArgumentException));
        Assert.That(response.GetProperty("errorMessage").GetString(), Is.EqualTo("BufferSize is out of range"));
    }

    [Test]
    public async Task SkipsBytesBeforeTheInitMessage()
    {
        // JsonHelper.ReceiveUtf8JsonAsync starts a message at the first opening brace
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!);
        using JsonDocument response = await client.RequestAsync("garbage\r\n\0\x01 ]] {\"mode\":\"Command\",\"version\":13}");
        Assert.That(RawIpcClient.AssertResponse(response.RootElement), Is.True);
    }

    [Test]
    public async Task ProcessesMessagesSentAtOnce()
    {
        // The server consumes only the bytes of the first message, so a command sent along with the init message is processed next
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!);
        await client.SendAsync("{\"mode\":\"Command\",\"version\":13}{\"command\":\"ResolvePath\",\"path\":\"0:/sys/config.g\"}{\"command\":\"CheckPassword\",\"password\":\"\"}");
        using JsonDocument initResponse = await client.ReceiveResponseAsync();
        using JsonDocument pathResponse = await client.ReceiveResponseAsync();
        using JsonDocument passwordResponse = await client.ReceiveResponseAsync();
        Assert.That(RawIpcClient.AssertResponse(initResponse.RootElement), Is.True);
        Assert.That(pathResponse.RootElement.GetProperty("result").GetString(), Is.EqualTo(Path.Combine(_host.BaseDirectory, "sys", "config.g")));
        Assert.That(passwordResponse.RootElement.GetProperty("result").GetBoolean(), Is.True);
    }

    [Test]
    public async Task BracesInStringsDoNotEndTheMessage()
    {
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!);
        using JsonDocument response = await client.RequestAsync("{\"mode\":\"Command\",\"version\":13,\"x\":\"}}{{\\\"}\"}");
        Assert.That(RawIpcClient.AssertResponse(response.RootElement), Is.True);
    }

    [Test]
    public async Task HugeInitMessage()
    {
        // The protocol has no size limit, the receiver buffers until the braces are balanced (JsonHelper.ReceiveUtf8JsonAsync)
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!);
        string padding = new('x', 4 * 1024 * 1024);
        using JsonDocument response = await client.RequestAsync($"{{\"mode\":\"Command\",\"version\":13,\"padding\":\"{padding}\"}}");
        Assert.That(RawIpcClient.AssertResponse(response.RootElement), Is.True);
    }

    [TestCase("{\"mode\":\"Comm")]
    [TestCase("{\"mode\":\"Command\",\"version\":13")]
    [TestCase("no message at all")]
    [TestCase("")]
    public async Task ClientClosesBeforeTheInitMessageIsComplete(string data)
    {
        // A closed connection ends the receive with a SocketException, which Server.ProcessConnectionAsync does not log as unexpected
        using (RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!))
        {
            await client.SendAsync(data);
            client.Socket.Shutdown(SocketShutdown.Send);
            (var messages, bool closed) = await client.ReceiveUntilClosedAsync(CloseTimeout);
            Assert.That(messages, Is.Empty);
            Assert.That(closed, Is.True);
        }

        // The server keeps accepting connections
        using CommandConnection connection = new();
        await connection.ConnectAsync(_host.SocketPath);
        Assert.That(await connection.CheckPasswordAsync(string.Empty), Is.True);
    }

    [Test]
    public async Task ClientClosesWithoutReadingTheResponse()
    {
        using (RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!, readServerInit: false))
        {
            await client.SendAsync("{\"mode\":\"Command\",\"version\":13}");
        }
        using CommandConnection connection = new();
        await connection.ConnectAsync(_host.SocketPath);
        Assert.That(await connection.CheckPasswordAsync(string.Empty), Is.True);
    }

    [Test]
    public async Task DuetApiClientConnectsInEveryMode()
    {
        using CommandConnection command = new();
        await command.ConnectAsync(_host.SocketPath);
        Assert.That(command.Id, Is.GreaterThan(0));

        using SubscribeConnection subscribe = new();
        await subscribe.ConnectAsync(SubscriptionMode.Patch, ["state/status"], _host.SocketPath);
        Assert.That(subscribe.Id, Is.GreaterThan(command.Id));

        using InterceptConnection intercept = new();
        await intercept.ConnectAsync(InterceptionMode.Pre, [DuetAPI.CodeChannel.SBC], ["M1234"], false, _host.SocketPath);
        Assert.That(intercept.Id, Is.GreaterThan(subscribe.Id));

        using CodeStreamConnection codeStream = new();
        await codeStream.ConnectAsync(8, DuetAPI.CodeChannel.SBC, _host.SocketPath);
        Assert.That(codeStream.Id, Is.GreaterThan(intercept.Id));
        Assert.That(codeStream.BufferSize, Is.EqualTo(8));
        Assert.That(codeStream.Channel, Is.EqualTo(DuetAPI.CodeChannel.SBC));

        // The synchronous variants do the same
        using CommandConnection syncCommand = new();
        syncCommand.Connect(_host.SocketPath);
        Assert.That(syncCommand.IsConnected, Is.True);
        syncCommand.Close();
        Assert.That(syncCommand.IsConnected, Is.False);
    }

    [Test]
    public async Task DuetApiClientReportsRefusedMode()
    {
        // BaseConnection.ConnectAsync turns the error response into an IOException naming the mode
        IOException? e = await Assert.ThrowsAsync<IOException>(async () =>
        {
            using CodeStreamConnection codeStream = new();
            await codeStream.ConnectAsync(0, DuetAPI.CodeChannel.SBC, _host.SocketPath);
        });
        Assert.That(e!.Message, Does.Contain("CodeStream").And.Contain("BufferSize is out of range"));

        e = Assert.Throws<IOException>(() =>
        {
            using CodeStreamConnection codeStream = new();
            codeStream.Connect(Defaults.MaxCodeBufferSize + 1, DuetAPI.CodeChannel.SBC, _host.SocketPath);
        });
        Assert.That(e!.Message, Does.Contain("BufferSize is out of range"));
    }

    [Test]
    public async Task RefusesUntrackedPeerWhileThePluginServiceIsConnected()
    {
        // With DPS connected, a peer with the uid of DCS must be a known plugin or a DSF service (Connection.AssignPermissionsAsync).
        // The test process is neither, so Server.ProcessConnectionAsync sends an error instead of its init message
        await using (FakePluginService service = await FakePluginService.StartAsync(_host))
        {
            using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!, readServerInit: false);
            JsonElement response = await ExpectErrorAndCloseAsync(client, nameof(UnauthorizedAccessException));
            Assert.That(response.GetProperty("errorMessage").GetString(), Is.EqualTo("Insufficient permissions"));
            Assert.That(_log.Entries.Any(entry => entry.Message.Contains("Rejecting untracked peer")), Is.True);
        }

        // Without DPS the test process is an external program again
        using RawIpcClient accepted = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Command\",\"version\":13}");
    }

    [Test]
    public async Task PluginProcessGetsThePermissionsOfItsPlugin()
    {
        // Connection.AssignPermissionsAsync gives a peer whose PID belongs to a plugin the permissions of that plugin
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        await AddPluginAsync(_host, "TestPlugin", Environment.ProcessId, SbcPermissions.ObjectModelReadWrite);
        try
        {
            // PluginService mode still requires ServicePlugins
            using (RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!))
            {
                await client.SendAsync("{\"mode\":\"PluginService\",\"version\":13}");
                await ExpectErrorAndCloseAsync(client, nameof(UnauthorizedAccessException));
            }

            // Plugin data of another plugin may only be changed by its owner or with ManagePlugins (SetPluginData.ExecuteAsync)
            using RawIpcClient plugin = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Command\",\"version\":13}");
            using (JsonDocument response = await plugin.RequestAsync("{\"command\":\"SetPluginData\",\"plugin\":\"OtherPlugin\",\"key\":\"x\",\"value\":1}"))
            {
                Assert.That(RawIpcClient.GetErrorType(response.RootElement), Is.EqualTo(nameof(UnauthorizedAccessException)));
            }

            // Its own data may be changed, the plugin is taken from the connection if it is omitted
            using (JsonDocument response = await plugin.RequestAsync("{\"command\":\"SetPluginData\",\"key\":\"value\",\"value\":42}"))
            {
                Assert.That(RawIpcClient.AssertResponse(response.RootElement), Is.True, response.RootElement.ToString());
            }
            using (await _host.Model.AccessReadOnlyAsync())
            {
                Assert.That(_host.Model.Plugins["TestPlugin"].Data["value"]!.Value.GetInt32(), Is.EqualTo(42));
            }

            // Unknown keys are refused
            using (JsonDocument response = await plugin.RequestAsync("{\"command\":\"SetPluginData\",\"key\":\"unknown\",\"value\":42}"))
            {
                Assert.That(RawIpcClient.GetErrorType(response.RootElement), Is.EqualTo(nameof(ArgumentException)));
            }

            // A plugin may notify that it has started
            using (JsonDocument response = await plugin.RequestAsync("{\"command\":\"NotifyPluginStarted\"}"))
            {
                Assert.That(RawIpcClient.AssertResponse(response.RootElement), Is.True, response.RootElement.ToString());
            }
            using (await _host.Model.AccessReadOnlyAsync())
            {
                Assert.That(_host.Model.Plugins["TestPlugin"].Started, Is.True);
            }
        }
        finally
        {
            await RemovePluginAsync(_host, "TestPlugin");
        }
    }

    [Test]
    public async Task ChildProcessGetsThePermissionsOfItsPlugin()
    {
        // Connection.AssignPermissionsAsync walks up the process tree, so a process started by a plugin is treated as that plugin.
        // The parent of the test process stands in for the plugin here
        int parentPid = ProcessHelpers.GetParentPid(Environment.ProcessId);
        Assume.That(parentPid, Is.GreaterThan(1));

        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        await AddPluginAsync(_host, "ParentPlugin", parentPid, SbcPermissions.ObjectModelReadWrite);
        try
        {
            using RawIpcClient plugin = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Command\",\"version\":13}");
            using JsonDocument response = await plugin.RequestAsync("{\"command\":\"SetPluginData\",\"key\":\"value\",\"value\":\"child\"}");
            Assert.That(RawIpcClient.AssertResponse(response.RootElement), Is.True, response.RootElement.ToString());
            using (await _host.Model.AccessReadOnlyAsync())
            {
                Assert.That(_host.Model.Plugins["ParentPlugin"].Data["value"]!.Value.GetString(), Is.EqualTo("child"));
            }
        }
        finally
        {
            await RemovePluginAsync(_host, "ParentPlugin");
        }
    }

    [Test]
    public async Task ExternalProgramAsksThePluginServiceForItsPlugin()
    {
        // An external program connected without DPS has no plugin ID. NotifyPluginStarted without a plugin then asks DPS
        // to resolve the peer (Connection.ResolvePeerPluginIdAsync) and fails if DPS is not there
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Command\",\"version\":13}");
        using (JsonDocument response = await client.RequestAsync("{\"command\":\"NotifyPluginStarted\"}"))
        {
            Assert.That(RawIpcClient.GetErrorType(response.RootElement), Is.EqualTo(nameof(UnauthorizedAccessException)));
            Assert.That(response.RootElement.GetProperty("errorMessage").GetString(), Is.EqualTo("Failed to determine plugin ID"));
        }

        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        await AddPluginAsync(_host, "ResolvedPlugin", -1, SbcPermissions.None);
        try
        {
            service.Responder = command => Task.FromResult(command.GetProperty("command").GetString() == "ResolvePluginProcess"
                ? (command.GetProperty("pid").GetInt32() == Environment.ProcessId ? "{\"success\":true,\"result\":\"ResolvedPlugin\"}" : "{\"success\":true,\"result\":null}")
                : FakePluginService.DefaultResponse(command));
            using (JsonDocument response = await client.RequestAsync("{\"command\":\"NotifyPluginStarted\"}"))
            {
                Assert.That(RawIpcClient.AssertResponse(response.RootElement), Is.True, response.RootElement.ToString());
            }
            using (await _host.Model.AccessReadOnlyAsync())
            {
                Assert.That(_host.Model.Plugins["ResolvedPlugin"].Started, Is.True);
            }

            // The resolved ID is kept, DPS is not asked again
            int resolveRequests = service.GetCommandNames().Count(name => name == "ResolvePluginProcess");
            using (JsonDocument response = await client.RequestAsync("{\"command\":\"NotifyPluginStarted\"}"))
            {
                Assert.That(RawIpcClient.AssertResponse(response.RootElement), Is.True);
            }
            Assert.That(service.GetCommandNames().Count(name => name == "ResolvePluginProcess"), Is.EqualTo(resolveRequests));
        }
        finally
        {
            await RemovePluginAsync(_host, "ResolvedPlugin");
        }
    }

    [Test]
    public async Task StopRemovesTheSocketFile()
    {
        FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(ipc: true);
        string socketPath = host.SocketPath!;
        try
        {
            Assert.That(System.IO.File.Exists(socketPath), Is.True);
        }
        finally
        {
            await host.DisposeAsync();
        }
        // Server.StopAsync closes the socket and deletes its file
        Assert.That(System.IO.File.Exists(socketPath), Is.False);
    }

    [Test]
    public async Task ReplacesStaleSocketFile()
    {
        // Server.StartAsync deletes a socket file left behind by a previous instance
        string baseDirectory = Path.Combine(Path.GetTempPath(), "dsf-ipc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(baseDirectory);
        try
        {
            string socketPath = Path.Combine(baseDirectory, Defaults.SocketFile);
            await System.IO.File.WriteAllTextAsync(socketPath, "stale");
            await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(configureSettings: settings => settings.SocketDirectory = baseDirectory, ipc: true);
            using CommandConnection connection = new();
            await connection.ConnectAsync(socketPath);
            Assert.That(await connection.CheckPasswordAsync(string.Empty), Is.True);
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }

    [Test]
    public async Task UpdateOnlyModeServesNoConnections()
    {
        // In update-only mode Server.ExecuteAsync does not accept connections, so a client never gets the init message
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(configureSettings: settings => settings.UpdateOnly = true, ipc: true);
        using RawIpcClient client = await RawIpcClient.ConnectAsync(host.SocketPath!, readServerInit: false);
        await Assert.ThrowsAsync<TimeoutException>(async () => await client.ReceiveAsync(TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>
    /// Add a plugin to the object model
    /// </summary>
    public static async Task AddPluginAsync(FakeFirmwareHost host, string id, int pid, SbcPermissions permissions)
    {
        using (await host.Model.AccessReadWriteAsync())
        {
            Plugin plugin = new() { Id = id, Name = id, Pid = pid, SbcPermissions = permissions };
            using JsonDocument value = JsonDocument.Parse("0");
            plugin.Data["value"] = value.RootElement.Clone();
            host.Model.Plugins.Add(id, plugin);
        }
    }

    /// <summary>
    /// Remove a plugin from the object model
    /// </summary>
    public static async Task RemovePluginAsync(FakeFirmwareHost host, string id)
    {
        using (await host.Model.AccessReadWriteAsync())
        {
            host.Model.Plugins.Remove(id);
        }
    }
}
