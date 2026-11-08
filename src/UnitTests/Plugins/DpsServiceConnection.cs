using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetPluginService.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Plugins;

using CommandService = DuetPluginService.IPC.CommandService;
using DcsPluginService = DuetControlServer.IPC.Processors.PluginService;
using File = System.IO.File;

/// <summary>
/// The connection of DuetPluginService to DCS: CommandService (DuetPluginService/IPC/CommandService.cs), PluginServiceConnection (IPC/PluginServiceConnection.cs),
/// CommandFactory (Commands/CommandFactory.cs) and the start-up in Program.cs. Commands come from the PluginService processor of DCS through <see cref="DcsBridge"/>,
/// malformed ones are written to the plugin service connection directly. Also the system package commands, which run only as root
/// (Commands/InstallSystemPackage.cs:75-78, Commands/UninstallSystemPackage.cs:24-27)
/// </summary>
[TestFixture]
public class DpsServiceConnection : DpsTestBase
{
    private CommandService GetCommandService() => Dps!.Services.GetServices<IHostedService>().OfType<CommandService>().Single();

    private async Task<ErrorResponse> SendMalformedAsync(string json)
    {
        DuetControlServer.IPC.Connection connection = Dps!.Bridge!.PluginServiceConnection!;
        await connection.SendRawDataAsync(Encoding.UTF8.GetBytes(json));
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        BaseResponse response = await connection.ReceiveResponseAsync(cts.Token);
        Assert.That(response, Is.InstanceOf<ErrorResponse>(), json);
        return (ErrorResponse)response;
    }

    [Test]
    public async Task ServiceRegistersAsPluginServiceOfDcs()
    {
        // CommandService.StartAsync connects in PluginService mode (CommandService.cs:19-38, PluginServiceConnection.cs:30-34), DCS registers it
        // (DuetControlServer/IPC/Processors/PluginService.cs:191-215)
        await StartDpsAsync();
        Assert.That(DcsPluginService.IsConnected(false), Is.True);
        Assert.That(DcsPluginService.IsConnected(true), Is.False);
        Assert.That(DcsPluginService.ServicePid, Is.EqualTo(Environment.ProcessId));
        Assert.That(Dps!.Bridge!.Modes, Is.EqualTo(new[] { ConnectionMode.PluginService }));
    }

    [Test]
    public async Task CommandsOfDcsAreExecutedAndAnswered()
    {
        // ReloadPlugin is forwarded to both services by DCS (DuetControlServer/Commands/Plugins/ReloadPlugin.cs), here only the regular one is asked
        await StartDpsAsync();
        File.WriteAllText(Path.Combine(PluginDirectory, "Forwarded.json"), "{\"id\":\"Forwarded\",\"name\":\"Forwarded\"}");
        await DcsPluginService.PerformCommandAsync(new ReloadPlugin { Plugin = "Forwarded" }, false).WaitAsync(FakeFirmwareHost.DefaultTimeout);
        Assert.That(await Dps!.GetPluginAsync("Forwarded"), Is.Not.Null);

        // Errors go back as error responses and DCS raises them for the caller (CommandService.cs:74-96, PluginServiceConnection.cs:79-87)
        InternalServerException e = (await Assert.ThrowsAsync<InternalServerException>(async () =>
            await DcsPluginService.PerformCommandAsync(new StartPlugin { Plugin = "Missing" }, false).WaitAsync(FakeFirmwareHost.DefaultTimeout)))!;
        Assert.That(e.Message, Does.Contain("Plugin Missing not found by service"));
        Assert.That(Dps.Log.Contains("Failed to execute StartPlugin"), Is.True);

        // The service goes on
        await DcsPluginService.PerformCommandAsync(new ReloadPlugin { Plugin = "Forwarded" }, false).WaitAsync(FakeFirmwareHost.DefaultTimeout);
        Assert.That(GetCommandService().ExecuteTask!.IsCompleted, Is.False);
    }

    [Test]
    public async Task TypedResultsAreAnswered()
    {
        // ResolvePluginProcess returns a string, which is sent as result (PluginServiceConnection.cs:88-92), or null, sent as empty response (75-78)
        await StartDpsAsync();
        Assert.That(await DcsPluginService.PerformCommandAsync<string>(new DuetAPI.Commands.ResolvePluginProcess { Pid = Environment.ProcessId }, false).WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.Null);
    }

    [Test]
    public async Task MissingFilePermissionsAreReported()
    {
        // UnauthorizedAccessException is logged without stack trace (CommandService.cs:81-84) and sent back like any other error
        await StartDpsAsync();
        string package = PluginPackage.Create(OutsidePath("denied.zip"), "{\"id\":\"Denied\",\"name\":\"Denied\"}", ("dsf/file", "x"));
        File.SetUnixFileMode(PluginDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            InternalServerException e = (await Assert.ThrowsAsync<InternalServerException>(async () =>
                await DcsPluginService.PerformCommandAsync(new InstallPlugin { PluginFile = package }, false).WaitAsync(FakeFirmwareHost.DefaultTimeout)))!;
            Assert.That(e.Message, Does.Contain("Access to the path"));
            Assert.That(Dps!.Log.Entries.Any(entry => entry.Message.StartsWith("Cannot execute InstallPlugin: Access to the path") && entry.Exception is null), Is.True);
        }
        finally
        {
            File.SetUnixFileMode(PluginDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [TestCase("{\"plugin\":\"Demo\"}", "ArgumentException", "Command type not found")]
    [TestCase("{\"command\":5}", "ArgumentException", "Command type must be a string")]
    [TestCase("{\"command\":\"GetObjectModel\"}", "ArgumentException", "Unsupported command GetObjectModel")]
    [TestCase("{\"command\":\"SetPluginProcess\",\"plugin\":\"Demo\",\"pid\":1}", "ArgumentException", "Unsupported command SetPluginProcess")]
    public async Task MalformedCommandsAreAnswered(string json, string errorType, string errorMessage)
    {
        // PluginServiceConnection.ReceiveCommandAsync (PluginServiceConnection.cs:43-62) and CommandFactory.Create (CommandFactory.cs:67-79)
        await StartDpsAsync();
        ErrorResponse response = await SendMalformedAsync(json);
        Assert.That(response.ErrorType, Is.EqualTo(errorType));
        Assert.That(response.ErrorMessage, Is.EqualTo(errorMessage));
        Assert.That(GetCommandService().ExecuteTask!.IsCompleted, Is.False, "The service goes on");
    }

    [Test]
    public async Task CommandNamesAreCaseInsensitive()
    {
        // CommandFactory.cs:71
        await StartDpsAsync();
        File.WriteAllText(Path.Combine(PluginDirectory, "Cased.json"), "{\"id\":\"Cased\",\"name\":\"Cased\"}");
        DuetControlServer.IPC.Connection connection = Dps!.Bridge!.PluginServiceConnection!;
        await connection.SendRawDataAsync("{\"command\":\"reloadplugin\",\"plugin\":\"Cased\"}"u8.ToArray());
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        Assert.That((await connection.ReceiveResponseAsync(cts.Token)).Success, Is.True);
        Assert.That(await Dps.GetPluginAsync("Cased"), Is.Not.Null);
    }

    [Test]
    public async Task CommandFactoryCreatesTheSupportedCommands()
    {
        await StartDpsAsync(register: false);
        CommandFactory factory = Dps!.Services.GetRequiredService<CommandFactory>();

        // CommandFactory.cs:30-40
        (string Name, Type Type)[] supported =
        [
            ("InstallPlugin", typeof(DuetPluginService.IPC.InstallPlugin)), ("ReloadPlugin", typeof(DuetPluginService.IPC.ReloadPlugin)),
            ("ResolvePluginProcess", typeof(DuetPluginService.IPC.ResolvePluginProcess)), ("StartPlugin", typeof(DuetPluginService.IPC.StartPlugin)),
            ("StopPlugin", typeof(DuetPluginService.IPC.StopPlugin)), ("UninstallPlugin", typeof(DuetPluginService.IPC.UninstallPlugin)),
            ("InstallSystemPackage", typeof(DuetPluginService.IPC.InstallSystemPackage)), ("UninstallSystemPackage", typeof(DuetPluginService.IPC.UninstallSystemPackage))
        ];
        foreach ((string name, Type type) in supported)
        {
            using JsonDocument json = JsonDocument.Parse($"{{\"command\":\"{name}\"}}");
            Assert.That(factory.Create(name, json.RootElement), Is.InstanceOf(type), name);
        }

        using JsonDocument start = JsonDocument.Parse("{\"command\":\"StartPlugin\",\"plugin\":\"Demo\",\"saveState\":false}");
        DuetPluginService.IPC.StartPlugin startPlugin = (DuetPluginService.IPC.StartPlugin)factory.Create("StartPlugin", start.RootElement);
        Assert.That(startPlugin.Plugin, Is.EqualTo("Demo"));
        Assert.That(startPlugin.SaveState, Is.False);

        // Commands of DCS that DPS does not implement are refused (CommandFactory.cs:48-58)
        ArgumentException e = Assert.Throws<ArgumentException>(() => factory.Create<GetObjectModel>())!;
        Assert.That(e.Message, Is.EqualTo("Unsupported command GetObjectModel"));
    }

    [Test]
    public async Task ServiceStopsWhenDcsIsNotRunning()
    {
        // CommandService.cs:26-33: DCS is not there yet, the service stops so that systemd starts it again
        await using DpsHost dps = await DpsHost.StartAsync(null, PluginDirectory);
        Assert.That(dps.Lifetime.ApplicationStopping.IsCancellationRequested, Is.True);
        Assert.That(dps.Log.Contains("Failed to connect to DCS"), Is.True);
        Assert.That(DcsPluginService.IsConnected(false), Is.False);
    }

    [Test]
    public async Task DcsRefusesAProcessThatIsNotDps()
    {
        // The real IPC server of DCS gives the PluginService mode only to the DuetPluginService executable next to it (DuetControlServer/IPC/Server.cs:264-269,
        // Connection.cs:97-106 and IsDsfService), so DPS fails to start with the connection error (BaseConnection.ConnectAsync throws IOException,
        // CommandService.StartAsync catches only SocketException)
        IOException e = (await Assert.ThrowsAsync<IOException>(async () =>
            await DpsHost.StartAsync(Dcs, PluginDirectory, settings => settings.SocketPath = Dcs.SocketPath!)))!;
        Assert.That(e.Message, Is.EqualTo("Could not set connection type PluginService (UnauthorizedAccessException: Insufficient permissions)"));
        Assert.That(DcsPluginService.IsConnected(false), Is.False);
    }

    [Test]
    public async Task ServiceEndsItsLoopWhenDcsClosesTheConnection()
    {
        // CommandService.cs:69-73: a closed connection ends the command loop
        await StartDpsAsync();
        Dps!.Bridge!.PluginServiceConnection!.Close();
        Assert.That(await WaitForAsync(() => GetCommandService().ExecuteTask!.IsCompleted), Is.True);
        Assert.That(GetCommandService().ExecuteTask!.IsFaulted, Is.False);
    }

    /// <summary>
    /// When DCS is not there at start-up, CommandService stops the application "and let systemd restart us to retry" (CommandService.cs:26-33). It does the same
    /// when DCS closes the connection later, so that DPS registers with the next DCS (Restart=always in
    /// pkg/common/duetpluginservice/usr/lib/systemd/system/duetpluginservice*.service)
    /// </summary>
    [Test]
    public async Task ServiceStopsWhenDcsClosesTheConnection()
    {
        await StartDpsAsync();
        Dps!.Bridge!.PluginServiceConnection!.Close();
        Assert.That(await WaitForAsync(() => Dps.Lifetime.ApplicationStopping.IsCancellationRequested, TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(Dps.Log.Contains("Connection to DCS has been closed"), Is.True);
    }

    /// <summary>
    /// The received command belongs to one pass of the command loop, so when receiving the next command fails, the error is not logged as failure of the
    /// previous command (CommandService.cs)
    /// </summary>
    [Test]
    public async Task InvalidCommandIsNotLoggedAsThePreviousOne()
    {
        await StartDpsAsync();
        await DcsPluginService.PerformCommandAsync(new ReloadPlugin { Plugin = "Previous" }, false).WaitAsync(FakeFirmwareHost.DefaultTimeout);
        await SendMalformedAsync("{\"command\":5}");
        Assert.That(Dps!.Log.Contains("Failed to execute ReloadPlugin"), Is.False);
        Assert.That(Dps.Log.Entries.Any(entry => entry.Message == "Failed to execute command" && entry.Exception?.Message == "Command type must be a string"), Is.True);
    }

    [Test]
    public async Task SystemPackagesNeedRoot()
    {
        // Both commands refuse to run unless the service is privileged (InstallSystemPackage.cs:75-78, UninstallSystemPackage.cs:24-27)
        await StartDpsAsync(register: false);
        string package = PluginPackage.Create(OutsidePath("package.zip"), null, ("update.sh", "#!/bin/sh"));
        DuetPluginService.IPC.InstallSystemPackage install = Dps!.Create<DuetPluginService.IPC.InstallSystemPackage>();
        install.PackageFile = package;
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await install.ExecuteAsync()))!;
        Assert.That(e.Message, Is.EqualTo("Unable to manage system packages without root privileges"));

        DuetPluginService.IPC.UninstallSystemPackage uninstall = Dps.Create<DuetPluginService.IPC.UninstallSystemPackage>();
        uninstall.Package = "nano";
        e = (await Assert.ThrowsAsync<ArgumentException>(async () => await uninstall.ExecuteAsync()))!;
        Assert.That(e.Message, Is.EqualTo("Unable to manage system packages without root privileges"));

        Assert.That(FakeTool.Calls(Dps.Settings.InstallLocalPackageCommand), Is.Empty);
        Assert.That(FakeTool.Calls(Dps.Settings.UninstallLocalPackageCommand), Is.Empty);
        Assert.That(Dps.Bridge!.Modes, Is.Empty, "No update status was reported to DCS");
    }

    [Test]
    public async Task SystemPackagesGoToTheRootServiceOnly()
    {
        // DCS forwards the system package commands to the root service only and only with RootPluginSupport (DuetControlServer/Commands/Packages/*.cs).
        // Here DCS treats DPS as root service; DPS itself is not privileged, so it refuses them
        await StartDpsAsync(asRoot: true);
        string package = PluginPackage.Create(OutsidePath("package.deb"), null);
        DuetControlServer.Commands.CommandFactory dcsCommands = Dcs.Services.GetRequiredService<DuetControlServer.Commands.CommandFactory>();

        DuetControlServer.Commands.InstallSystemPackage install = dcsCommands.Create<DuetControlServer.Commands.InstallSystemPackage>();
        install.PackageFile = package;
        NotSupportedException disabled = (await Assert.ThrowsAsync<NotSupportedException>(async () => await install.ExecuteAsync()))!;
        Assert.That(disabled.Message, Is.EqualTo("Root plugin support has been disabled"));

        DcsSettings.RootPluginSupport = true;
        InternalServerException e = (await Assert.ThrowsAsync<InternalServerException>(async () => await install.ExecuteAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout)))!;
        Assert.That(e.Message, Does.Contain("Unable to manage system packages without root privileges"));

        DuetControlServer.Commands.UninstallSystemPackage uninstall = dcsCommands.Create<DuetControlServer.Commands.UninstallSystemPackage>();
        uninstall.Package = "nano";
        e = (await Assert.ThrowsAsync<InternalServerException>(async () => await uninstall.ExecuteAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout)))!;
        Assert.That(e.Message, Does.Contain("Unable to manage system packages without root privileges"));
        Assert.That(FakeTool.Calls(Dps!.Settings.InstallLocalPackageCommand), Is.Empty);
    }

    [TestCase("--log-level", "debug")]
    [TestCase(null, null)]
    public async Task ProgramStopsWhenDcsIsNotRunning(string? option, string? value)
    {
        // Program.cs: configuration file via -c, log level from the command line or the file (aliases like "warn" via LogLevelHelper), then the host runs
        // until CommandService stops it because DCS is missing (CommandService.cs:26-33)
        string directory = Path.Combine(Path.GetTempPath(), "dsf-dps-program-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "plugins"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "plugins", "Listed.json"), "{\"id\":\"Listed\",\"name\":\"Listed\"}");
            string config = Path.Combine(directory, "plugins.json");
            File.WriteAllText(config, JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, object>
            {
                ["LogLevel"] = "warn",
                ["SocketPath"] = Path.Combine(directory, "missing.sock"),
                ["PluginDirectory"] = Path.Combine(directory, "plugins"),
                ["StopTimeout"] = 1000
            }));
            string[] args = (option is null) ? ["-c", config] : ["-c", config, option, value!];
            MethodInfo entryPoint = typeof(DuetPluginService.PluginService).Assembly.EntryPoint!;
            object? exitCode = await Task.Run(() => entryPoint.Invoke(null, [args])).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.That(exitCode, Is.EqualTo(0));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
