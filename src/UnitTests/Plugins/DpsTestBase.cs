using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using UnitTests.IPC;

namespace UnitTests.Plugins;

using DcsPluginService = DuetControlServer.IPC.Processors.PluginService;
using File = System.IO.File;
using Settings = DuetPluginService.Settings;

/// <summary>
/// Base of the DuetPluginService fixtures: one DCS host with IPC per fixture (its base directory is the virtual SD card, 0:/), a fresh DPS per test
/// (<see cref="StartDpsAsync"/>) and a fresh plugin directory, which DCS and DPS share like /opt/dsf/plugins on the SBC
/// </summary>
public abstract class DpsTestBase
{
    /// <summary>
    /// DCS host
    /// </summary>
    protected FakeFirmwareHost Dcs { get; private set; } = null!;

    /// <summary>
    /// Log of DCS
    /// </summary>
    protected IpcLog DcsLog { get; private set; } = null!;

    /// <summary>
    /// DPS of the current test, if started
    /// </summary>
    protected DpsHost? Dps { get; private set; }

    /// <summary>
    /// Plugin directory of DCS and DPS
    /// </summary>
    protected string PluginDirectory => Path.Combine(Dcs.BaseDirectory, "plugins");

    /// <summary>
    /// Physical directory of 0:/
    /// </summary>
    protected string SdDirectory => Dcs.BaseDirectory;

    /// <summary>
    /// Settings of DCS
    /// </summary>
    protected DuetControlServer.Settings DcsSettings => Dcs.Services.GetRequiredService<IOptions<DuetControlServer.Settings>>().Value;

    /// <summary>
    /// Architecture directory that InstallPlugin.cs:112-119 and StartPlugin.cs:75-82 look for first
    /// </summary>
    protected static string Architecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm => "arm",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.X64 => "x86_64",
        _ => "unknown"
    };

    [OneTimeSetUp]
    public async Task StartDcs()
    {
        Dcs = await FakeFirmwareHost.StartAsync(configureSettings: settings =>
        {
            settings.PluginDirectory = Path.Combine(settings.BaseDirectory, "plugins");
            settings.PluginsFilename = Path.Combine(settings.BaseDirectory, "plugins.txt");
        }, ipc: true);
        DcsLog = IpcLog.Attach(Dcs);
        _initialEntries = [.. Directory.EnumerateFileSystemEntries(SdDirectory, "*", SearchOption.AllDirectories)];
    }

    /// <summary>
    /// Entries of the SD directory before the first test, the ones that tests add are deleted again
    /// </summary>
    private System.Collections.Generic.HashSet<string> _initialEntries = [];

    [OneTimeTearDown]
    public async Task StopDcs() => await Dcs.DisposeAsync();

    [SetUp]
    public void PrepareTest()
    {
        DcsLog.Clear();
        Directory.CreateDirectory(PluginDirectory);
    }

    [TearDown]
    public async Task CleanUp()
    {
        try
        {
            if (Dps is not null)
            {
                await Dps.DisposeAsync();
                Assert.That(Dps.Bridge?.Exceptions, Is.Null.Or.Empty, "The bridge to DCS ran into an exception");
                Dps = null;
            }
            Assert.That(DcsPluginService.IsConnected(false) || DcsPluginService.IsConnected(true), Is.False, "A plugin service was left registered");
            Assert.That(DcsLog.UnexpectedExceptions, Is.Empty, "The IPC server ran into an exception it did not expect");
        }
        finally
        {
            using (await Dcs.Model.AccessReadWriteAsync())
            {
                Dcs.Model.Plugins.Clear();
                Dcs.Model.State.PluginsStarted = false;
                Dcs.Model.SBC!.DSF.RootPluginSupport = false;
            }
            DcsSettings.RootPluginSupport = false;
            File.Delete(DcsSettings.PluginsFilename);
            Directory.Delete(PluginDirectory, true);
            foreach (string entry in Directory.EnumerateFileSystemEntries(SdDirectory, "*", SearchOption.TopDirectoryOnly).Concat(
                Directory.EnumerateDirectories(SdDirectory).Where(dir => new DirectoryInfo(dir).LinkTarget is null).SelectMany(dir => Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.TopDirectoryOnly))).ToList())
            {
                if (_initialEntries.Contains(entry) || entry == PluginDirectory || !Path.Exists(entry))
                {
                    continue;
                }
                if (Directory.Exists(entry) && new DirectoryInfo(entry).LinkTarget is null)
                {
                    Directory.Delete(entry, true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }
    }

    /// <summary>
    /// Start DPS for the current test
    /// </summary>
    /// <param name="configure">Optional callback to change the settings</param>
    /// <param name="register">Whether to register DPS as plugin service of DCS</param>
    /// <param name="asRoot">Whether DCS treats DPS as root plugin service</param>
    /// <param name="privileged">Whether DPS takes the code paths of the root plugin service</param>
    /// <param name="userId">User ID that DPS assumes to run as or null for the one of the test</param>
    /// <returns>Started DPS</returns>
    protected async Task<DpsHost> StartDpsAsync(Action<Settings>? configure = null, bool register = true, bool asRoot = false, bool privileged = false, int? userId = null)
    {
        Dps = await DpsHost.StartAsync(Dcs, PluginDirectory, configure, register, asRoot, privileged, userId);
        return Dps;
    }

    /// <summary>
    /// Wait until DCS has started the plugins, which it does in the background once both plugin services are registered and the object model has been
    /// updated (DuetControlServer/IPC/Processors/PluginService.cs, ProcessAsync), so that this does not run into the test
    /// </summary>
    protected async Task WaitForPluginsStartedAsync()
    {
        Assert.That(await DpsHost.WaitForAsync(async () =>
        {
            using (await Dcs.Model.AccessReadOnlyAsync())
            {
                return Dcs.Model.State.PluginsStarted;
            }
        }), Is.True, "DCS did not start the plugins");
    }

    /// <summary>
    /// Enable root plugin support in DCS like RootPluginSupport in config.json does: the setting and sbc.dsf.rootPluginSupport, which DCS sets from it
    /// when the object model is created (DuetControlServer/Model/ObjectModel.cs)
    /// </summary>
    protected async Task EnableRootPluginSupportAsync()
    {
        DcsSettings.RootPluginSupport = true;
        using (await Dcs.Model.AccessReadWriteAsync())
        {
            Dcs.Model.SBC!.DSF.RootPluginSupport = true;
        }
    }

    /// <summary>
    /// Add a plugin to the object model of DCS, which SetPluginProcess requires (DuetControlServer/Commands/Plugins/SetPluginProcess.cs)
    /// </summary>
    /// <param name="plugin">Plugin to add</param>
    protected async Task AddToDcsAsync(Plugin plugin)
    {
        using (await Dcs.Model.AccessReadWriteAsync())
        {
            Dcs.Model.Plugins.Add(plugin.Id, (Plugin)plugin.Clone());
        }
    }

    /// <summary>
    /// Get a copy of a plugin from the object model of DCS
    /// </summary>
    /// <param name="id">Plugin identifier</param>
    /// <returns>Plugin or null</returns>
    protected async Task<Plugin?> GetFromDcsAsync(string id)
    {
        using (await Dcs.Model.AccessReadOnlyAsync())
        {
            return Dcs.Model.Plugins.TryGetValue(id, out Plugin? plugin) ? (Plugin)plugin.Clone() : null;
        }
    }

    /// <summary>
    /// Wait until DCS logged a message containing the given text
    /// </summary>
    /// <param name="text">Text to wait for</param>
    /// <returns>True if it was logged in time</returns>
    protected Task<bool> WaitForDcsMessageAsync(string text) =>
        DpsHost.WaitForAsync(() => Task.FromResult(DcsLog.Entries.Any(entry => entry.Message.Contains(text))));

    /// <summary>
    /// Path of a file in a temporary directory of the current test that is neither the plugin nor the SD directory
    /// </summary>
    /// <param name="name">File name</param>
    /// <returns>Path in the DPS directory</returns>
    protected string OutsidePath(string name) => Path.Combine(Dps!.Directory, name);

    /// <summary>
    /// Wait for a condition
    /// </summary>
    protected static Task<bool> WaitForAsync(Func<bool> condition, TimeSpan? timeout = null) => DpsHost.WaitForAsync(() => Task.FromResult(condition()), timeout);
}
