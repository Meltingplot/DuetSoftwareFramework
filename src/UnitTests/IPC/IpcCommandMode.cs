using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Utility;
using DuetControlServer.IPC.Processors;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// Tests of the Command mode of the DCS IPC server (DuetControlServer/IPC/Processors/Command.cs, Connection.ReceiveCommandAsync)
/// with messages that DuetAPIClient does not send
/// </summary>
[TestFixture]
public class IpcCommandMode
{
    private FakeFirmwareHost _host = null!;
    private IpcLog _log = null!;
    private RawIpcClient _client = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await FakeFirmwareHost.StartAsync(configureSettings: settings =>
        {
            settings.PluginDirectory = Path.Combine(settings.BaseDirectory, "plugins");
            settings.PluginsFilename = Path.Combine(settings.BaseDirectory, "plugins.txt");
        }, ipc: true);
        _log = IpcLog.Attach(_host);
        _client = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Command\",\"version\":13}");
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [SetUp]
    public void ClearLog() => _log.Clear();

    [TearDown]
    public async Task CheckConnection()
    {
        // Command.ProcessAsync answers every message, so the connection stays usable after an error
        using JsonDocument response = await _client.RequestAsync("{\"command\":\"CheckPassword\",\"password\":\"\"}");
        Assert.That(response.RootElement.GetProperty("result").GetBoolean(), Is.True);
        Assert.That(_log.UnexpectedExceptions, Is.Empty, "The IPC server ran into an exception it did not expect");
    }

    private async Task<JsonElement> RequestAsync(string json)
    {
        using JsonDocument response = await _client.RequestAsync(json);
        return response.RootElement.Clone();
    }

    [TestCase("{\"command\":\"NoSuchCommand\"}", "unsupported command NoSuchCommand")]
    [TestCase("{\"command\":\"Acknowledge\"}", "unsupported command Acknowledge")]
    [TestCase("{\"command\":\"Resolve\",\"type\":\"Success\",\"content\":\"x\"}", "unsupported command Resolve")]
    [TestCase("{\"command\":\"ResolvePluginProcess\",\"pid\":1}", "unsupported command ResolvePluginProcess")]
    [TestCase("{\"command\":\"\"}", "unsupported command ")]
    [TestCase("{\"command\":1}", "command must be a string")]
    [TestCase("{\"command\":null}", "command must be a string")]
    [TestCase("{\"command\":{\"name\":\"SimpleCode\"}}", "command must be a string")]
    [TestCase("{}", "command type not found")]
    [TestCase("{\"code\":\"M115\",\"channel\":\"SBC\"}", "command type not found")]
    public async Task RefusesInvalidCommands(string json, string errorMessage)
    {
        // Connection.ReceiveCommandAsync throws an ArgumentException and Command.ProcessAsync sends it back
        JsonElement response = await RequestAsync(json);
        Assert.That(RawIpcClient.GetErrorType(response), Is.EqualTo(nameof(ArgumentException)));
        Assert.That(response.GetProperty("errorMessage").GetString(), Is.EqualTo(errorMessage));
    }

    /// <summary>
    /// The generated UpdateFromJsonReader methods (DuetAPI.SourceGenerators/Commands/UpdateFromJsonReader.cs) read scalar values with the
    /// Utf8JsonReader getters, which throw InvalidOperationException for another token type and FormatException for a number that does not fit,
    /// and enums with JsonSerializer, which throws JsonException. Malformed JSON makes Utf8JsonReader throw (JsonReaderException)
    /// </summary>
    [TestCase("{\"command\":\"ResolvePath\",\"path\":}", "JsonReaderException")]
    [TestCase("{\"command\":\"ResolvePath\",\"path\":\"0:/sys\",}", "JsonReaderException")]
    [TestCase("{\"command\":\"SimpleCode\",\"code\":\"M115\",\"channel\":\"NoSuchChannel\"}", nameof(JsonException))]
    [TestCase("{\"command\":\"Flush\",\"channel\":\"SBC\",\"syncFileStreams\":\"yes\"}", nameof(InvalidOperationException))]
    [TestCase("{\"command\":\"ResolvePath\",\"path\":42}", nameof(InvalidOperationException))]
    [TestCase("{\"command\":\"GetObjectModel\",\"filters\":\"state\"}", nameof(InvalidOperationException), Description = "Lists are read without checking for the start of the array")]
    [TestCase("{\"command\":\"RemoveUserSession\",\"id\":1.5}", nameof(FormatException))]
    [TestCase("{\"command\":\"RemoveUserSession\",\"id\":99999999999}", nameof(FormatException))]
    public async Task ReportsDeserializationErrors(string json, string errorType)
    {
        JsonElement response = await RequestAsync(json);
        Assert.That(RawIpcClient.GetErrorType(response), Is.EqualTo(errorType), response.ToString());
    }

    [Test]
    public async Task CommandNamesAreCaseInsensitive()
    {
        // Connection.ReceiveCommandAsync looks up the names with StringComparer.OrdinalIgnoreCase
        JsonElement response = await RequestAsync("{\"command\":\"rEsOlVePaTh\",\"path\":\"0:/sys/config.g\"}");
        Assert.That(response.GetProperty("result").GetString(), Is.EqualTo(Path.Combine(_host.BaseDirectory, "sys", "config.g")));
    }

    [Test]
    public async Task CommandIsFoundAfterOtherMembers()
    {
        // The command object is deserialized as a whole once the name is known, so the order of the members does not matter for
        // scalar members (members whose value is an object end the search, see IpcServer.ModeIsReadFromAnyPosition)
        JsonElement response = await RequestAsync("{\"path\":\"0:/macros/x.g\",\"list\":[1,{\"command\":\"x\"}],\"command\":\"ResolvePath\"}");
        Assert.That(response.GetProperty("result").GetString(), Is.EqualTo(Path.Combine(_host.BaseDirectory, "macros", "x.g")));
    }

    [Test]
    public async Task UnknownMembersAreIgnored()
    {
        JsonElement response = await RequestAsync("{\"command\":\"CheckPassword\",\"password\":\"\",\"unknown\":{\"a\":[1,2,3]},\"other\":null}");
        Assert.That(response.GetProperty("result").GetBoolean(), Is.True);
    }

    [Test]
    public async Task LegacyCommandNamesForOldClients()
    {
        // API version 8 and older still use GetMachineModel and PatchMachineModel (Connection._legacyCommandMapping)
        using (RawIpcClient legacy = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Command\",\"version\":8}"))
        {
            using JsonDocument response = await legacy.RequestAsync("{\"command\":\"GetMachineModel\"}");
            Assert.That(response.RootElement.GetProperty("result").GetProperty("state").GetProperty("status").GetString(), Is.Not.Empty);
        }

        // Newer clients do not get the mapping
        JsonElement current = await RequestAsync("{\"command\":\"GetMachineModel\"}");
        Assert.That(current.GetProperty("errorMessage").GetString(), Is.EqualTo("unsupported command GetMachineModel"));
    }

    [Test]
    public async Task RawJsonCommandsReturnTheirResult()
    {
        // GetObjectModel and QueryObjectModel write their JSON result directly into the response (Command.ProcessAsync, IRawJsonCommand)
        JsonElement model = await RequestAsync("{\"command\":\"GetObjectModel\",\"filters\":[\"state/status\",\"move/axes[0]/letter\"]}");
        Assert.That(model.GetProperty("result").GetProperty("state").GetProperty("status").GetString(), Is.EqualTo("idle"));
        Assert.That(model.GetProperty("result").GetProperty("move").GetProperty("axes")[0].GetProperty("letter").GetString(), Is.EqualTo("X"));

        JsonElement query = await RequestAsync("{\"command\":\"QueryObjectModel\",\"key\":\"move.axes\",\"flags\":\"d99v\"}");
        Assert.That(query.GetProperty("result").GetProperty("key").GetString(), Is.EqualTo("move.axes"));
        Assert.That(query.GetProperty("result").GetProperty("result").GetArrayLength(), Is.EqualTo(3));
    }

    [TestCase("{\"command\":\"GetObjectModel\",\"filters\":[\"heat/heaters[1,2]\"]}", Description = "Lists of indices are matched by the filter regex but cannot be parsed")]
    [TestCase("{\"command\":\"GetObjectModel\",\"filters\":[\"heat/heaters[99999999999]\"]}")]
    public async Task ReportsInvalidFilters(string json)
    {
        // Filter.ConvertFilter parses the index with int.Parse, which throws for these. The error is reported like any other
        JsonElement response = await RequestAsync(json);
        Assert.That(RawIpcClient.GetErrorType(response), Is.AnyOf(nameof(FormatException), nameof(OverflowException)));
    }

    [Test]
    public async Task CommandsWithResultTypes()
    {
        Assert.That((await RequestAsync("{\"command\":\"AddUserSession\",\"accessLevel\":\"ReadOnly\",\"sessionType\":\"HTTP\",\"origin\":\"test\"}")).GetProperty("result").GetInt32(), Is.GreaterThan(0));
        Assert.That((await RequestAsync("{\"command\":\"RemoveUserSession\",\"id\":-5}")).GetProperty("result").GetBoolean(), Is.False);
        Assert.That((await RequestAsync("{\"command\":\"Flush\",\"channel\":\"SBC\"}")).GetProperty("result").GetBoolean(), Is.True);
        Assert.That((await RequestAsync("{\"command\":\"EvaluateExpression\",\"channel\":\"SBC\",\"expression\":\"1 + 2\"}")).GetProperty("result").GetInt32(), Is.EqualTo(3));
        Assert.That((await RequestAsync("{\"command\":\"SimpleCode\",\"code\":\"echo 6 * 7\",\"channel\":\"SBC\"}")).GetProperty("result").GetString(), Is.EqualTo("42\n"));
        Assert.That((await RequestAsync("{\"command\":\"SyncObjectModel\"}")).TryGetProperty("result", out _), Is.False);
    }

    [Test]
    public async Task ReportsCommandErrorsWithTheirType()
    {
        // Command.ProcessAsync sends every exception back with its type name (ErrorResponse)
        Assert.That(RawIpcClient.GetErrorType(await RequestAsync("{\"command\":\"StartPlugin\",\"plugin\":\"NoSuchPlugin\"}")), Is.EqualTo(nameof(ArgumentException)));
        Assert.That(RawIpcClient.GetErrorType(await RequestAsync("{\"command\":\"AddHttpEndpoint\",\"endpointType\":\"GET\",\"namespace\":\"file\",\"path\":\"x\"}")), Is.EqualTo(nameof(ArgumentException)));
        Assert.That(RawIpcClient.GetErrorType(await RequestAsync("{\"command\":\"PatchObjectModel\",\"key\":\"state\",\"patch\":{}}")), Is.EqualTo(nameof(InvalidOperationException)));
        Assert.That(RawIpcClient.GetErrorType(await RequestAsync($"{{\"command\":\"InstallPlugin\",\"pluginFile\":\"{Path.Combine(_host.BaseDirectory, "missing.zip")}\"}}")), Is.EqualTo(nameof(FileNotFoundException)));
        Assert.That(RawIpcClient.GetErrorType(await RequestAsync($"{{\"command\":\"GetFileInfo\",\"fileName\":\"{Path.Combine(_host.BaseDirectory, "gcodes", "missing.gcode")}\"}}")), Is.EqualTo(nameof(FileNotFoundException)));
    }

    [Test]
    public async Task UnauthorizedAccessFromACommandIsReported()
    {
        // NotifyPluginStarted cannot find the plugin of an external program without DPS (Connection.ResolvePeerPluginIdAsync)
        JsonElement response = await RequestAsync("{\"command\":\"NotifyPluginStarted\"}");
        Assert.That(RawIpcClient.GetErrorType(response), Is.EqualTo(nameof(UnauthorizedAccessException)));
        Assert.That(_log.Entries.Any(entry => entry.Message.Contains("Cannot execute NotifyPluginStarted")), Is.True);
    }

    /// <summary>
    /// An external program gets every permission except SuperUser and ServicePlugins (Connection.GrantExternalPermissions), so commands
    /// that require one of them must be refused with an UnauthorizedAccessException before they are executed (Connection.CheckPermissions)
    /// </summary>
    [TestCase("{\"command\":\"UninstallSystemPackage\",\"package\":\"nano\"}", Description = "SuperUser")]
    [TestCase("{\"command\":\"InstallSystemPackage\",\"packageFile\":\"/tmp/does-not-exist.deb\"}", Description = "SuperUser")]
    [TestCase("{\"command\":\"SetPluginProcess\",\"plugin\":\"NoSuchPlugin\",\"pid\":1}", Description = "ServicePlugins")]
    [Explicit("Bug: RequiredPermissionsAttribute.Check grants every permission, so these commands are executed")]
    public async Task RefusesCommandsWithoutPermission(string json)
    {
        JsonElement response = await RequestAsync(json);
        Assert.That(RawIpcClient.GetErrorType(response), Is.EqualTo(nameof(UnauthorizedAccessException)), response.ToString());
        Assert.That(response.GetProperty("errorMessage").GetString(), Is.EqualTo("Insufficient permissions"));
    }

    [Test]
    [Explicit("Bug: RequiredPermissionsAttribute.Check grants every permission, so a plugin may run any command")]
    public async Task PluginIsRefusedCommandsItHasNoPermissionFor()
    {
        // A plugin that may only read the object model must not run codes (SimpleCode requires CommandExecution)
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        await IpcServer.AddPluginAsync(_host, "ReadOnlyPlugin", Environment.ProcessId, SbcPermissions.ObjectModelRead);
        try
        {
            using RawIpcClient plugin = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Command\",\"version\":13}");
            using JsonDocument allowed = await plugin.RequestAsync("{\"command\":\"GetObjectModel\"}");
            Assert.That(RawIpcClient.AssertResponse(allowed.RootElement), Is.True);
            using JsonDocument refused = await plugin.RequestAsync("{\"command\":\"SimpleCode\",\"code\":\"echo 1\",\"channel\":\"SBC\"}");
            Assert.That(RawIpcClient.GetErrorType(refused.RootElement), Is.EqualTo(nameof(UnauthorizedAccessException)), refused.RootElement.ToString());
        }
        finally
        {
            await IpcServer.RemovePluginAsync(_host, "ReadOnlyPlugin");
        }
    }

    [Test]
    [Explicit("Bug: RequiredPermissionsAttribute.Check grants every permission, so a plugin without permissions gets the Command mode")]
    public async Task PluginWithoutPermissionsIsRefusedTheCommandMode()
    {
        // Server.GetConnectionProcessorAsync refuses a mode if none of its commands may be executed (Connection.CheckCommandPermissions)
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        await IpcServer.AddPluginAsync(_host, "NoPermissionsPlugin", Environment.ProcessId, SbcPermissions.None);
        try
        {
            using RawIpcClient plugin = await RawIpcClient.ConnectAsync(_host.SocketPath!);
            using JsonDocument response = await plugin.RequestAsync("{\"mode\":\"Command\",\"version\":13}");
            Assert.That(RawIpcClient.GetErrorType(response.RootElement), Is.EqualTo(nameof(UnauthorizedAccessException)), response.RootElement.ToString());
        }
        finally
        {
            await IpcServer.RemovePluginAsync(_host, "NoPermissionsPlugin");
        }
    }

    [Test]
    public void EveryCommandModeCommandHasPermissions()
    {
        // Without the attribute Connection.CheckPermissions lets a command through unchecked
        foreach (SupportedCommand command in Command.SupportedCommands.Concat(CodeInterception.SupportedCommands).Concat(ModelSubscription.SupportedCommands))
        {
            bool hasAttribute = Attribute.IsDefined(command.Type, typeof(RequiredPermissionsAttribute), true);
            Assert.That(hasAttribute || command.Type == typeof(DuetControlServer.Commands.NotifyPluginStarted), Is.True, $"{command.Name} has no RequiredPermissions attribute");
        }
    }
}
