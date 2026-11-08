using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetPluginService;
using DuetPluginService.Commands;
using DuetPluginService.IPC;
using DuetPluginService.PermissionManagers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Plugins;

using DcsPluginService = DuetControlServer.IPC.Processors.PluginService;
using File = System.IO.File;
using Settings = DuetPluginService.Settings;

/// <summary>
/// DuetPluginService (DPS) in-process, with the services wired like DuetPluginService/Program.cs:91-98 and started in the order of the generic host
/// (PluginService, then CommandService). DPS reaches DCS through a <see cref="DcsBridge"/> for both its plugin service connection and the command
/// connections it opens itself (ResolvePath, SetPluginProcess, WriteMessage, SetUpdateStatus).
/// The settings point to a temporary directory: AppArmor profiles go to a directory of their own, apparmor_parser, the Python package installer and the
/// package managers are replaced by <see cref="FakeTool"/> scripts that record their arguments, and the profile template is the one that DSF ships
/// (pkg/common/duetpluginservice/opt/dsf/conf/apparmor.conf). The tests run as a regular user, so Environment.IsPrivilegedProcess is false: this is the
/// service that runs as dsf user (duetpluginservice.service), the root code paths are not reached
/// </summary>
public sealed class DpsHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly List<IHostedService> _startedServices = [];

    private DpsHost(ServiceProvider provider, DpsLog log, string directory, Settings settings, DcsBridge? bridge)
    {
        _provider = provider;
        Log = log;
        Directory = directory;
        Settings = settings;
        Bridge = bridge;
    }

    /// <summary>
    /// Temporary directory of this instance (tools and AppArmor profiles)
    /// </summary>
    public string Directory { get; }

    /// <summary>
    /// Settings of DPS
    /// </summary>
    public Settings Settings { get; }

    /// <summary>
    /// Bridge to DCS or null if DPS runs without DCS
    /// </summary>
    public DcsBridge? Bridge { get; }

    /// <summary>
    /// Services of DPS
    /// </summary>
    public IServiceProvider Services => _provider;

    /// <summary>
    /// Plugins and processes known to DPS
    /// </summary>
    public PluginStore Store => _provider.GetRequiredService<PluginStore>();

    /// <summary>
    /// Application lifetime of DPS
    /// </summary>
    public ApplicationLifetime Lifetime => (ApplicationLifetime)_provider.GetRequiredService<IHostApplicationLifetime>();

    /// <summary>
    /// Log of DPS
    /// </summary>
    public DpsLog Log { get; }

    /// <summary>
    /// Create a DPS command like CommandService does for a command from DCS
    /// </summary>
    /// <typeparam name="T">Command type</typeparam>
    /// <returns>Command instance</returns>
    public T Create<T>() where T : DuetAPI.Commands.BaseCommand => _provider.GetRequiredService<CommandFactory>().Create<T>();

    /// <summary>
    /// Start DPS
    /// </summary>
    /// <param name="dcs">DCS host with IPC services or null to run DPS without DCS (SocketPath then points to a missing socket)</param>
    /// <param name="pluginDirectory">Plugin directory, shared with DCS (DCS Settings.PluginDirectory) for tests that go through DCS</param>
    /// <param name="configure">Optional callback to change the settings</param>
    /// <param name="register">Whether to start CommandService, which registers DPS as plugin service of DCS</param>
    /// <param name="asRoot">Whether DCS treats the connections of DPS as the ones of the root plugin service (DPS itself still runs as regular user)</param>
    /// <returns>Started DPS</returns>
    public static async Task<DpsHost> StartAsync(FakeFirmwareHost? dcs, string pluginDirectory, Action<Settings>? configure = null, bool register = true, bool asRoot = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), "dsf-dps-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path.Combine(directory, "apparmor.d"));
        System.IO.Directory.CreateDirectory(pluginDirectory);

        DcsBridge? bridge = (dcs is not null) ? DcsBridge.Start(dcs, asRoot) : null;
        Settings settings = new()
        {
            LogLevel = LogLevel.Trace,
            SocketPath = bridge?.SocketPath ?? Path.Combine(directory, "missing.sock"),
            PluginDirectory = pluginDirectory,
            AppArmorParser = FakeTool.Create(directory, "apparmor_parser"),
            AppArmorTemplate = ShippedFile("pkg/common/duetpluginservice/opt/dsf/conf/apparmor.conf"),
            AppArmorProfileDirectory = Path.Combine(directory, "apparmor.d"),
            PreinstallPackageCommand = FakeTool.Create(directory, "apt-update"),
            InstallPackageCommand = FakeTool.Create(directory, "apt-install"),
            InstallPythonPackageCommand = FakeTool.Create(directory, "pipInstall2.py"),
            InstallLocalPackageCommand = FakeTool.Create(directory, "dpkg-install"),
            UninstallLocalPackageCommand = FakeTool.Create(directory, "dpkg-remove")
        };
        configure?.Invoke(settings);

        DpsLog log = new();
        ServiceCollection services = new();
        services
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(log))
            .AddSingleton(Options.Create(settings))
            .AddSingleton<IHostApplicationLifetime, ApplicationLifetime>()
            // Program.cs:91-98
            .AddSingleton<CommandFactory>()
            .AddSingleton<IPermissionManager, AppArmorPermissionManager>()
            .AddSingleton<PluginStore>()
            .AddHostedService<PluginService>()
            .AddSingleton<PluginServiceConnection>()
            .AddHostedService<CommandService>();

        DpsHost host = new(services.BuildServiceProvider(), log, directory, settings, bridge);
        try
        {
            using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
            foreach (IHostedService service in host._provider.GetServices<IHostedService>())
            {
                if (service is CommandService && !register)
                {
                    continue;
                }
                await service.StartAsync(cts.Token);
                host._startedServices.Add(service);
            }
            host.Lifetime.NotifyStarted();

            if (register)
            {
                // CommandService.StartAsync returns once the connection is established, the processor of DCS registers the service right after
                while (!DcsPluginService.IsConnected(asRoot) && !host.Lifetime.ApplicationStopping.IsCancellationRequested)
                {
                    await Task.Delay(5, cts.Token);
                }
            }
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
        return host;
    }

    /// <summary>
    /// Get the path of a file in the repository
    /// </summary>
    /// <param name="relativePath">Path relative to the repository root</param>
    /// <returns>Absolute path</returns>
    public static string ShippedFile(string relativePath)
    {
        for (DirectoryInfo? dir = new(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException($"{relativePath} not found above {TestContext.CurrentContext.TestDirectory}");
    }

    /// <summary>
    /// Path of the AppArmor profile of a plugin, as AppArmorPermissionManager and StartPlugin build it
    /// </summary>
    /// <param name="pluginId">Plugin identifier</param>
    /// <returns>Profile path</returns>
    public string ProfilePath(string pluginId) => Path.Combine(Settings.AppArmorProfileDirectory, $"dsf.{pluginId}");

    /// <summary>
    /// Write a plugin manifest and the plugin executable to the plugin directory, like a completed installation leaves them behind,
    /// optionally with an AppArmor profile, which the root service would have written
    /// </summary>
    /// <param name="plugin">Plugin manifest</param>
    /// <param name="script">Shell script to use as the SBC executable or null for none</param>
    /// <param name="profile">Whether to write the AppArmor profile</param>
    /// <returns>Path of the executable or null</returns>
    public string? WritePlugin(Plugin plugin, string? script = null, bool profile = true)
    {
        string pluginBase = Path.Combine(Settings.PluginDirectory, plugin.Id);
        System.IO.Directory.CreateDirectory(Path.Combine(pluginBase, "dsf"));
        string? executable = null;
        if (script is not null && plugin.SbcExecutable is not null)
        {
            executable = Path.Combine(pluginBase, "dsf", plugin.SbcExecutable);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "#!/bin/sh\n" + script + "\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.WriteAllText(Path.Combine(Settings.PluginDirectory, plugin.Id + ".json"), JsonSerializer.Serialize(plugin, ObjectModelContext.Default.Plugin));
        if (profile)
        {
            File.WriteAllText(ProfilePath(plugin.Id), "# profile of " + plugin.Id);
        }
        return executable;
    }

    /// <summary>
    /// Add a plugin to the plugin store, like PluginService.StartAsync does for an installed plugin
    /// </summary>
    /// <param name="plugin">Plugin to add</param>
    public async Task AddToStoreAsync(Plugin plugin)
    {
        using (await Store.LockAsync(CancellationToken.None))
        {
            Store.Plugins.Add(plugin);
        }
    }

    /// <summary>
    /// Get a plugin from the store
    /// </summary>
    /// <param name="id">Plugin identifier</param>
    /// <returns>Plugin or null</returns>
    public async Task<Plugin?> GetPluginAsync(string id)
    {
        using (await Store.LockAsync(CancellationToken.None))
        {
            return Store.Plugins.FirstOrDefault(plugin => plugin.Id == id);
        }
    }

    /// <summary>
    /// Wait for a condition, checked every 10 ms
    /// </summary>
    /// <param name="condition">Condition to wait for</param>
    /// <param name="timeout">Timeout or null for the default timeout</param>
    /// <returns>True if the condition became true in time</returns>
    public static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        DateTime end = DateTime.UtcNow + (timeout ?? FakeFirmwareHost.DefaultTimeout);
        while (DateTime.UtcNow < end)
        {
            if (await condition())
            {
                return true;
            }
            await Task.Delay(10);
        }
        return await condition();
    }

    private bool _disposed;

    /// <summary>
    /// Stop DPS like the generic host does (services in reverse order), the bridge to DCS and delete the temporary directory
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        Lifetime.StopApplication();
        using (CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout))
        {
            for (int i = _startedServices.Count - 1; i >= 0; i--)
            {
                try
                {
                    await _startedServices[i].StopAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Stopping took too long, carry on with the next one
                }
            }
        }
        Lifetime.NotifyStopped();

        // Kill what may be left of the plugin processes
        using (await Store.LockAsync(CancellationToken.None))
        {
            foreach (System.Diagnostics.Process process in Store.Processes.Values)
            {
                try
                {
                    process.Kill(true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone
                }
            }
        }
        await _provider.DisposeAsync();

        if (Bridge is not null)
        {
            await Bridge.DisposeAsync();
        }
        try
        {
            System.IO.Directory.Delete(Directory, true);
        }
        catch (IOException)
        {
            // Left behind in the temporary directory
        }
    }
}

/// <summary>
/// Log of DPS
/// </summary>
public sealed class DpsLog : ILoggerProvider
{
    /// <summary>
    /// Log entry
    /// </summary>
    /// <param name="Category">Logger category</param>
    /// <param name="Level">Log level</param>
    /// <param name="Message">Formatted message</param>
    /// <param name="Exception">Logged exception</param>
    public sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception)
    {
        /// <inheritdoc />
        public override string ToString() => $"[{Level}] {Category}: {Message}" + ((Exception is not null) ? $" {Exception}" : string.Empty);
    }

    private readonly ConcurrentQueue<Entry> _entries = new();

    /// <summary>
    /// Entries so far
    /// </summary>
    public IReadOnlyList<Entry> Entries => [.. _entries];

    /// <summary>
    /// Check if a message containing the given text was logged
    /// </summary>
    /// <param name="text">Text to look for</param>
    /// <returns>Whether it was logged</returns>
    public bool Contains(string text) => _entries.Any(entry => entry.Message.Contains(text));

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    /// <inheritdoc />
    public void Dispose() { }

    private sealed class Logger(DpsLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            log._entries.Enqueue(new Entry(category, logLevel, formatter(state, exception), exception));
            TestContext.Progress.WriteLine($"DPS [{logLevel}] {category}: {formatter(state, exception)}" + ((exception is not null) ? $" {exception.Message}" : string.Empty));
        }
    }
}

/// <summary>
/// Shell script that stands in for an external program of DPS (apparmor_parser, apt, dpkg, pipInstall2.py). It appends its arguments to
/// &lt;tool&gt;.calls and the value of DEBIAN_FRONTEND to &lt;tool&gt;.env and exits with the code in &lt;tool&gt;.exit (0 if there is none)
/// </summary>
public static class FakeTool
{
    /// <summary>
    /// Create a fake tool
    /// </summary>
    /// <param name="directory">Directory to create it in</param>
    /// <param name="name">Name of the tool</param>
    /// <returns>Path of the tool</returns>
    public static string Create(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$0.calls\"\nprintf '%s\\n' \"$DEBIAN_FRONTEND\" >> \"$0.env\"\nif [ -f \"$0.exit\" ]; then exit $(cat \"$0.exit\"); fi\nexit 0\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>
    /// Arguments of each call so far
    /// </summary>
    /// <param name="path">Path of the tool</param>
    /// <returns>One line per call</returns>
    public static string[] Calls(string path) => File.Exists(path + ".calls") ? File.ReadAllLines(path + ".calls") : [];

    /// <summary>
    /// Value of DEBIAN_FRONTEND in each call so far
    /// </summary>
    /// <param name="path">Path of the tool</param>
    /// <returns>One line per call</returns>
    public static string[] Environment(string path) => File.Exists(path + ".env") ? File.ReadAllLines(path + ".env") : [];

    /// <summary>
    /// Set the exit code of the tool
    /// </summary>
    /// <param name="path">Path of the tool</param>
    /// <param name="exitCode">Exit code</param>
    public static void SetExitCode(string path, int exitCode) => File.WriteAllText(path + ".exit", exitCode.ToString());
}

/// <summary>
/// Plugin package (ZIP file) as DWC uploads it
/// </summary>
public static class PluginPackage
{
    /// <summary>
    /// Create a plugin package
    /// </summary>
    /// <param name="path">Path of the ZIP file</param>
    /// <param name="manifest">Content of plugin.json or null for none</param>
    /// <param name="files">Entries other than plugin.json with their content</param>
    /// <returns>Path of the ZIP file</returns>
    public static string Create(string path, string? manifest, params (string Name, string Content)[] files)
    {
        File.Delete(path);
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        if (manifest is not null)
        {
            using StreamWriter writer = new(archive.CreateEntry("plugin.json").Open());
            writer.Write(manifest);
        }
        foreach ((string name, string content) in files)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            if (!name.EndsWith('/'))
            {
                using StreamWriter writer = new(entry.Open());
                writer.Write(content);
            }
        }
        return path;
    }
}
