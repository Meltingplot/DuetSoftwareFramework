using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetControlServer.IPC.Processors;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// Tests of the PluginService processor (DuetControlServer/IPC/Processors/PluginService.cs) and the plugin commands that DCS forwards
/// to DuetPluginService, with <see cref="FakePluginService"/> in place of DPS. No plugin process is started
/// </summary>
[TestFixture]
public class IpcPluginService
{
    private FakeFirmwareHost _host = null!;
    private IpcLog _log = null!;
    private string _pluginDirectory = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await FakeFirmwareHost.StartAsync(configureSettings: settings =>
        {
            settings.PluginDirectory = Path.Combine(settings.BaseDirectory, "plugins");
            settings.PluginsFilename = Path.Combine(settings.BaseDirectory, "plugins.txt");
        }, ipc: true);
        _log = IpcLog.Attach(_host);
        _pluginDirectory = Path.Combine(_host.BaseDirectory, "plugins");
        Directory.CreateDirectory(_pluginDirectory);
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
        System.IO.File.Delete(Path.Combine(_host.BaseDirectory, "plugins.txt"));
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

    private async Task AddPluginAsync(string id, int pid = -1, SbcPermissions permissions = SbcPermissions.None, string? executable = "plugin", Action<Plugin>? configure = null)
    {
        using (await _host.Model.AccessReadWriteAsync())
        {
            Plugin plugin = new() { Id = id, Name = id, Pid = pid, SbcPermissions = permissions, SbcExecutable = executable };
            configure?.Invoke(plugin);
            _host.Model.Plugins.Add(id, plugin);
        }
    }

    private async Task<Plugin?> GetPluginAsync(string id)
    {
        using (await _host.Model.AccessReadOnlyAsync())
        {
            return _host.Model.Plugins.TryGetValue(id, out Plugin? plugin) ? (Plugin)plugin.Clone() : null;
        }
    }

    [Test]
    public async Task PluginServiceRegistersAndUnregisters()
    {
        Assert.That(PluginService.IsConnected(false), Is.False);
        await using (FakePluginService service = await FakePluginService.StartAsync(_host))
        {
            Assert.That(PluginService.IsConnected(false), Is.True);
            Assert.That(PluginService.IsConnected(true), Is.False);
            Assert.That(PluginService.ServicePid, Is.EqualTo(Environment.ProcessId));
        }
        Assert.That(PluginService.IsConnected(false), Is.False);
        Assert.That(PluginService.ServicePid, Is.Zero);
    }

    [Test]
    public async Task SecondPluginServiceIsRefusedWhileTheFirstIsAlive()
    {
        await using FakePluginService first = await FakePluginService.StartAsync(_host);
        InvalidOperationException e = await Assert.ThrowsAsync<InvalidOperationException>(async () => await FakePluginService.StartAsync(_host))!;
        Assert.That(e.Message, Is.EqualTo("Plugin service is already connected"));
    }

    [Test]
    public async Task NewPluginServiceTakesOverFromACrashedOne()
    {
        // PluginService.TerminateIfGone polls the registered service and terminates its processor if the peer is gone
        await using FakePluginService crashed = await FakePluginService.StartAsync(_host);
        crashed.Crash();
        await using FakePluginService replacement = await FakePluginService.StartAsync(_host);
        Assert.That(await crashed.WaitForTerminationAsync(), Is.True);
        Assert.That(PluginService.IsConnected(false), Is.True, "The terminated processor must not unregister its replacement");
    }

    [Test]
    public async Task StartAndStopPlugin()
    {
        using CommandConnection connection = await ConnectAsync();
        await AddPluginAsync("StartStop");
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        service.Responder = async command =>
        {
            if (command.GetProperty("command").GetString() == "StartPlugin")
            {
                // DPS reports the PID of the started plugin with SetPluginProcess before it answers
                using (await _host.Model.AccessReadWriteAsync())
                {
                    _host.Model.Plugins[command.GetProperty("plugin").GetString()!].Pid = 4242;
                }
            }
            return FakePluginService.DefaultResponse(command);
        };

        // StartPlugin forwards the command to the plugin service and saves the started plugins (StartPlugin.ExecuteAsync)
        await connection.StartPluginAsync("StartStop");
        JsonElement start = await service.WaitForCommandAsync("StartPlugin");
        Assert.That(start.GetProperty("plugin").GetString(), Is.EqualTo("StartStop"));
        Assert.That(await System.IO.File.ReadAllTextAsync(Path.Combine(_host.BaseDirectory, "plugins.txt")), Is.EqualTo("StartStop\n"));

        // StopPlugin is forwarded only for a running plugin and marks it as being shut down (Pid 0)
        connection.StopPlugin("StartStop");
        await service.WaitForCommandAsync("StopPlugin");
        Assert.That((await GetPluginAsync("StartStop"))!.Pid, Is.Zero);

        // A running plugin is not started again
        int startCommands = service.GetCommandNames().Count(name => name == "StartPlugin");
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Plugins["StartStop"].Pid = 4242;
        }
        await connection.StartPluginAsync("StartStop");
        Assert.That(service.GetCommandNames().Count(name => name == "StartPlugin"), Is.EqualTo(startCommands));
    }

    [Test]
    public async Task StartPluginStartsDependenciesFirst()
    {
        using CommandConnection connection = await ConnectAsync();
        await AddPluginAsync("Base");
        await AddPluginAsync("Dependent", configure: plugin => plugin.SbcPluginDependencies.Add("Base"));
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        await connection.StartPluginAsync("Dependent");
        Assert.That(service.ReceivedCommands.Where(command => command.GetProperty("command").GetString() == "StartPlugin").Select(command => command.GetProperty("plugin").GetString()),
                    Is.EqualTo(new[] { "Base", "Dependent" }));

        // A missing dependency is reported
        await AddPluginAsync("Broken", configure: plugin => plugin.SbcPluginDependencies.Add("Missing"));
        InternalServerException e = await Assert.ThrowsAsync<InternalServerException>(async () => await connection.StartPluginAsync("Broken"))!;
        Assert.That(e.Message, Does.Contain("Dependency Missing of plugin Broken not found"));

        // Incompatible versions are refused before anything is started
        await AddPluginAsync("Old", configure: plugin => plugin.SbcDsfVersion = "1.0");
        e = await Assert.ThrowsAsync<InternalServerException>(async () => await connection.StartPluginAsync("Old"))!;
        Assert.That(e.Message, Does.Contain("Incompatible DSF version"));
        await AddPluginAsync("OldRrf", configure: plugin => plugin.RrfVersion = "2.0");
        e = await Assert.ThrowsAsync<InternalServerException>(async () => await connection.StartPluginAsync("OldRrf"))!;
        Assert.That(e.Message, Does.Contain("Incompatible RRF version"));
    }

    [Test]
    public async Task ErrorOfThePluginServiceIsPassedOn()
    {
        // PluginService.ProcessAsync turns an error response into an InternalServerException for the waiting command
        using CommandConnection connection = await ConnectAsync();
        await AddPluginAsync("Failing");
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        service.Responder = _ => Task.FromResult("{\"success\":false,\"errorType\":\"IOException\",\"errorMessage\":\"cannot start\"}");
        InternalServerException e = await Assert.ThrowsAsync<InternalServerException>(async () => await connection.StartPluginAsync("Failing"))!;
        Assert.That(e.Message, Does.Contain("cannot start"));
    }

    [Test]
    public async Task PluginCommandsFailWithoutPluginService()
    {
        // PluginService.PerformCommandAsync refuses commands while no plugin service is registered
        using CommandConnection connection = await ConnectAsync();
        await AddPluginAsync("Lonely");
        InternalServerException e = await Assert.ThrowsAsync<InternalServerException>(async () => await connection.StartPluginAsync("Lonely"))!;
        Assert.That(e.Message, Does.Contain("plugin service is not started"));
    }

    [Test]
    public async Task BothServicesStartThePluginsAndStopThemAgain()
    {
        // When both plugin services are registered, PluginService.ProcessAsync starts the plugins listed in the plugins file (StartPlugins)
        await System.IO.File.WriteAllTextAsync(Path.Combine(_host.BaseDirectory, "plugins.txt"), "AutoStart\nUnknownPlugin\n");
        await AddPluginAsync("AutoStart");
        await AddPluginAsync("RootPlugin", pid: 77, permissions: SbcPermissions.SuperUser);

        FakePluginService service = await FakePluginService.StartAsync(_host);
        FakePluginService rootService = await FakePluginService.StartAsync(_host, asRoot: true);
        try
        {
            Assert.That(PluginService.IsConnected(true), Is.True);
            JsonElement start = await service.WaitForCommandAsync("StartPlugin");
            Assert.That(start.GetProperty("plugin").GetString(), Is.EqualTo("AutoStart"));
            using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
            while (true)
            {
                using (await _host.Model.AccessReadOnlyAsync())
                {
                    if (_host.Model.State.PluginsStarted)
                    {
                        break;
                    }
                }
                await Task.Delay(10, cts.Token);
            }

            // The unknown plugin is reported, StartPlugins goes on
            Assert.That(_log.Entries.Any(entry => entry.Message.Contains("Failed to start plugin UnknownPlugin")), Is.True);

            using (await _host.Model.AccessReadWriteAsync())
            {
                _host.Model.Plugins["AutoStart"].Pid = 4711;
            }
        }
        finally
        {
            // The root service goes first, its plugins are no longer running
            await rootService.DisposeAsync();
        }
        try
        {
            Assert.That((await GetPluginAsync("RootPlugin"))!.Pid, Is.LessThanOrEqualTo(0), "Plugins of the root service are no longer running");
        }
        finally
        {
            await service.DisposeAsync();
        }
        Assert.That((await GetPluginAsync("AutoStart"))!.Pid, Is.LessThanOrEqualTo(0), "The plugins of a terminated service are no longer running");
    }

    /// <summary>
    /// When one of the two plugin services terminates, PluginService.ProcessAsync wants to stop the remaining plugins ("Stop the remaining plugins
    /// again unless they are already stopped"). It starts StopPlugins with Task.Run and the cancellation token of the processor, but the processor
    /// only ends when that token is cancelled (the request loop catches the socket errors), so Task.Run never runs the command. The comment above
    /// the cleanup says the token "is already cancelled on application shutdown and the cleanup must still run to completion"
    /// </summary>
    [Test]
    [Explicit("Bug: StopPlugins is started with the already cancelled token of the terminated plugin service, so it never runs")]
    public async Task RemainingPluginsAreStoppedWhenAServiceTerminates()
    {
        await AddPluginAsync("Remaining", pid: 4711);
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        FakePluginService rootService = await FakePluginService.StartAsync(_host, asRoot: true);
        using (CancellationTokenSource started = new(FakeFirmwareHost.DefaultTimeout))
        {
            while (true)
            {
                using (await _host.Model.AccessReadOnlyAsync())
                {
                    if (_host.Model.State.PluginsStarted)
                    {
                        break;
                    }
                }
                await Task.Delay(10, started.Token);
            }
        }
        await rootService.DisposeAsync();
        JsonElement stop = await service.WaitForCommandAsync("StopPlugin");
        Assert.That(stop.GetProperty("plugin").GetString(), Is.EqualTo("Remaining"));
    }

    /// <summary>
    /// Plugin.Pid is "-1 if not started. It is set to 0 while the plugin is being shut down" (DuetAPI/ObjectModel/Plugins/Plugin.cs).
    /// PluginService.ProcessAsync sets the plugins of a terminated service to 0 first and to -1 afterwards, but the second loop only
    /// changes plugins with a PID above 0, which the first one has just set to 0, so they stay at 0 (being shut down) for good
    /// </summary>
    [Test]
    [Explicit("Bug: the plugins of a terminated plugin service keep Pid 0 (being shut down) instead of -1 (not started)")]
    public async Task PluginsOfATerminatedServiceAreNotStarted()
    {
        await AddPluginAsync("Orphan", pid: 4711);
        FakePluginService service = await FakePluginService.StartAsync(_host);
        await service.DisposeAsync();
        Assert.That((await GetPluginAsync("Orphan"))!.Pid, Is.EqualTo(-1));
    }

    [Test]
    public async Task UninstallPlugin()
    {
        using CommandConnection connection = await ConnectAsync();
        using RawIpcClient raw = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Command\",\"version\":13}");
        await AddPluginAsync("ToRemove", pid: 1000);
        await AddPluginAsync("Keeper", pid: 1001);
        await AddPluginAsync("Needy", configure: plugin => plugin.SbcPluginDependencies.Add("Keeper"));
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        await using FakePluginService rootService = await FakePluginService.StartAsync(_host, asRoot: true);

        // A plugin another one depends on cannot be uninstalled (UninstallPlugin.ExecuteAsync)
        InternalServerException e = await Assert.ThrowsAsync<InternalServerException>(async () => await connection.UninstallPluginAsync("Keeper"))!;
        Assert.That(e.Message, Does.Contain("plugin Needy depends on it"));

        // Otherwise it is stopped, removed from the plugins file and uninstalled by both services
        await connection.UninstallPluginAsync("ToRemove");
        Assert.That(service.GetCommandNames(), Does.Contain("StopPlugin").And.Contain("UninstallPlugin"));
        Assert.That(rootService.GetCommandNames(), Does.Contain("UninstallPlugin"));
        Assert.That(await GetPluginAsync("ToRemove"), Is.Null);
        Assert.That(await System.IO.File.ReadAllLinesAsync(Path.Combine(_host.BaseDirectory, "plugins.txt")), Does.Contain("Keeper").And.Not.Contain("ToRemove"));

        // ForUpgrade is reserved for DPS
        using JsonDocument response = await raw.RequestAsync("{\"command\":\"UninstallPlugin\",\"plugin\":\"Keeper\",\"forUpgrade\":true}");
        Assert.That(response.RootElement.GetProperty("errorMessage").GetString(), Does.Contain("ForUpgrade switch must not be used by third-party applications"));
    }

    [Test]
    public async Task ReloadPlugin()
    {
        using CommandConnection connection = await ConnectAsync();
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        await using FakePluginService rootService = await FakePluginService.StartAsync(_host, asRoot: true);

        // A manifest in the plugin directory is (re)loaded and both services are told (ReloadPlugin.ExecuteAsync)
        await System.IO.File.WriteAllTextAsync(Path.Combine(_pluginDirectory, "Reloaded.json"), "{\"id\":\"Reloaded\",\"name\":\"Reloaded plugin\",\"version\":\"1.2.3\"}");
        await connection.ReloadPluginAsync("Reloaded");
        Plugin plugin = (await GetPluginAsync("Reloaded"))!;
        Assert.That(plugin.Version, Is.EqualTo("1.2.3"));
        Assert.That(plugin.Pid, Is.EqualTo(-1));
        Assert.That(rootService.GetCommandNames(), Does.Contain("ReloadPlugin"));
        Assert.That(service.GetCommandNames(), Does.Contain("ReloadPlugin"));

        // A running plugin must be stopped first
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Plugins["Reloaded"].Pid = 99;
        }
        await Assert.ThrowsAsync<InternalServerException>(async () => await connection.ReloadPluginAsync("Reloaded"));

        // Without a manifest the plugin is removed
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Plugins["Reloaded"].Pid = -1;
        }
        System.IO.File.Delete(Path.Combine(_pluginDirectory, "Reloaded.json"));
        await connection.ReloadPluginAsync("Reloaded");
        Assert.That(await GetPluginAsync("Reloaded"), Is.Null);
    }

    /// <summary>
    /// Create a plugin package
    /// </summary>
    private string CreatePackage(string name, string? manifest, params string[] files)
    {
        string path = Path.Combine(_host.BaseDirectory, name + ".zip");
        System.IO.File.Delete(path);
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        if (manifest is not null)
        {
            using StreamWriter writer = new(archive.CreateEntry("plugin.json").Open());
            writer.Write(manifest);
        }
        foreach (string file in files)
        {
            using StreamWriter writer = new(archive.CreateEntry(file).Open());
            writer.Write("content");
        }
        return path;
    }

    [Test]
    public async Task InstallPlugin()
    {
        using CommandConnection connection = await ConnectAsync();
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        await using FakePluginService rootService = await FakePluginService.StartAsync(_host, asRoot: true);

        // The package is checked by DCS, installed by both services and its manifest is loaded from the plugin directory (InstallPlugin.ExecuteAsync)
        string package = CreatePackage("Installed", "{\"id\":\"Installed\",\"name\":\"Installed plugin\",\"version\":\"1.0\"}", "dwc/Installed.js", "sd/macros/installed.g");
        service.Responder = async command =>
        {
            if (command.GetProperty("command").GetString() == "InstallPlugin")
            {
                await System.IO.File.WriteAllTextAsync(Path.Combine(_pluginDirectory, "Installed.json"), "{\"id\":\"Installed\",\"name\":\"Installed plugin\",\"version\":\"1.0\",\"dwcFiles\":[\"Installed.js\"]}");
            }
            return FakePluginService.DefaultResponse(command);
        };
        await connection.InstallPluginAsync(package);
        Assert.That(service.GetCommandNames(), Does.Contain("InstallPlugin"));
        Assert.That(rootService.GetCommandNames(), Does.Contain("InstallPlugin"));
        Plugin installed = (await GetPluginAsync("Installed"))!;
        Assert.That(installed.DwcFiles, Is.EqualTo(new[] { "Installed.js" }));

        // Installing it again is an upgrade, the old version is uninstalled first
        await connection.InstallPluginAsync(package);
        Assert.That(service.GetCommandNames().Count(name => name == "UninstallPlugin"), Is.EqualTo(1));
        System.IO.File.Delete(Path.Combine(_pluginDirectory, "Installed.json"));
    }

    [TestCase(null, new string[0], "plugin.json not found in the ZIP file")]
    [TestCase("{\"name\":\"No id\"}", new string[0], "Plugin manifest must provide an id and a name")]
    [TestCase("{\"id\":\"Evil\",\"name\":\"Evil\"}", new[] { "sd/sys/config.g" }, "Illegal filename sd/sys/config.g")]
    [TestCase("{\"id\":\"Evil\",\"name\":\"Evil\"}", new[] { "sd/../../etc/passwd" }, "Illegal filename")]
    [TestCase("{\"id\":\"Evil\",\"name\":\"Evil\"}", new[] { "sd/firmware/Duet3Firmware_MB6HC.bin" }, "Illegal filename")]
    [TestCase("{\"id\":\"Evil\",\"name\":\"Evil\",\"sbcPermissions\":[\"servicePlugins\"]}", new string[0], "ServicePlugins permission is reserved")]
    [TestCase("{\"id\":\"Root\",\"name\":\"Root\",\"sbcPermissions\":[\"superUser\"]}", new string[0], "Plugins with super-user permissions are not allowed")]
    [TestCase("{\"id\":\"Args\",\"name\":\"Args\",\"sbcExecutableArguments\":\"`id`\"}", new string[0], "SbcExecutableArguments must not contain shell metacharacters")]
    [TestCase("{\"id\":\"Absolute\",\"name\":\"Absolute\",\"sbcExecutable\":\"/bin/bash\"}", new string[0], "Executable must be a relative path")]
    [TestCase("{\"id\":\"Old\",\"name\":\"Old\",\"sbcDsfVersion\":\"1.0\"}", new string[0], "Incompatible DSF version")]
    [TestCase("{\"id\":\"OldRrf\",\"name\":\"OldRrf\",\"rrfVersion\":\"2.0\"}", new string[0], "Incompatible RRF version")]
    [TestCase("{\"id\":\"Needy\",\"name\":\"Needy\",\"sbcPluginDependencies\":[\"Missing\"]}", new string[0], "Missing plugin dependency Missing")]
    [TestCase("{\"id\":\"Inject\",\"name\":\"Inject\",\"sbcPackageDependencies\":[\"nano; rm -rf /\"]}", new string[0], "Illegal characters in required package")]
    [TestCase("{\"id\":\"InjectPy\",\"name\":\"InjectPy\",\"sbcPythonDependencies\":[\"requests; rm\"]}", new string[0], "Illegal characters in required Python package")]
    public async Task InstallPluginRefusesInvalidPackages(string? manifest, string[] files, string error)
    {
        using CommandConnection connection = await ConnectAsync();
        await using FakePluginService service = await FakePluginService.StartAsync(_host);
        string package = CreatePackage("Invalid", manifest, files);
        InternalServerException e = await Assert.ThrowsAsync<InternalServerException>(async () => await connection.InstallPluginAsync(package))!;
        Assert.That(e.Message, Does.Contain(error));
        Assert.That(service.GetCommandNames(), Does.Not.Contain("InstallPlugin"), "Nothing may be installed");
    }
}
