using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetPluginService.PermissionManagers;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using UnitTests.IPC;

namespace UnitTests.Plugins;

using File = System.IO.File;

/// <summary>
/// Plugin commands of a client (like DWS) that go through DCS to DuetPluginService: the regular service is the real DPS, the root service is
/// <see cref="FakePluginService"/>, which does what the root DPS does at installation and uninstallation (the AppArmor profile, InstallPlugin.cs:71-78 and
/// UninstallPlugin.cs:167-171) with the real AppArmorPermissionManager and a fake apparmor_parser
/// </summary>
[TestFixture]
public class DpsThroughDcs : DpsTestBase
{
    /// <summary>
    /// Connect a command connection. It must connect while no plugin service is registered, otherwise the test process is refused as untracked peer
    /// (DuetControlServer/IPC/Connection.cs:158-168)
    /// </summary>
    private async Task<CommandConnection> ConnectAsync()
    {
        CommandConnection connection = new();
        await connection.ConnectAsync(Dcs.SocketPath);
        return connection;
    }

    /// <summary>
    /// Start the root service that writes and removes the AppArmor profiles like the root DPS
    /// </summary>
    private async Task<FakePluginService> StartRootServiceAsync()
    {
        FakePluginService root = await FakePluginService.StartAsync(Dcs, asRoot: true);
        AppArmorPermissionManager permissionManager = new(Options.Create(Dps!.Settings));
        root.Responder = async command =>
        {
            switch (command.GetProperty("command").GetString())
            {
                case "InstallPlugin":
                    {
                        using ZipArchive archive = ZipFile.OpenRead(command.GetProperty("pluginFile").GetString()!);
                        await using Stream manifestStream = archive.GetEntry("plugin.json")!.Open();
                        using JsonDocument manifest = await JsonDocument.ParseAsync(manifestStream);
                        Plugin plugin = new();
                        plugin.UpdateFromJson(manifest.RootElement, false);
                        if (!plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser))
                        {
                            await permissionManager.InstallProfileAsync(plugin, PluginDirectory, SdDirectory, CancellationToken.None);
                        }
                        break;
                    }
                case "UninstallPlugin":
                    await permissionManager.UninstallProfileAsync(new Plugin { Id = command.GetProperty("plugin").GetString()! }, CancellationToken.None);
                    break;
            }
            return FakePluginService.DefaultResponse(command);
        };
        return root;
    }

    [Test]
    public async Task PluginIsInstalledStartedStoppedAndUninstalled()
    {
        using CommandConnection client = await ConnectAsync();
        await StartDpsAsync();
        await using FakePluginService root = await StartRootServiceAsync();

        // InstallPlugin: DCS checks the package and forwards it to the regular service, which installs the files, then to the root service, which writes the
        // profile; DCS loads the manifest that DPS wrote (DuetControlServer/Commands/Plugins/InstallPlugin.cs:148-168)
        string package = PluginPackage.Create(OutsidePath("Lifecycle.zip"), new JsonObject
        {
            ["id"] = "Lifecycle",
            ["name"] = "Lifecycle",
            ["sbcExecutable"] = "plugin.sh",
            ["sbcPermissions"] = new JsonArray("readGCodes")
        }.ToJsonString(), ("dsf/plugin.sh", "#!/bin/sh\necho hello\nexec sleep 30"), ("dwc/Lifecycle.js", "js"), ("sd/macros/lifecycle.g", "M117"));
        await client.InstallPluginAsync(package);
        Plugin installed = (await GetFromDcsAsync("Lifecycle"))!;
        Assert.That(installed.DsfFiles, Is.EqualTo(new[] { "plugin.sh" }));
        Assert.That(installed.SdFiles, Is.EqualTo(new[] { "macros/lifecycle.g" }));
        Assert.That(await Dps!.GetPluginAsync("Lifecycle"), Is.Not.Null);
        Assert.That(File.ReadAllText(Dps.ProfilePath("Lifecycle")), Does.Contain($"  {Path.Combine(SdDirectory, "gcodes")}/** r,"));

        // StartPlugin: the regular service starts it and reports the PID
        await client.StartPluginAsync("Lifecycle");
        Assert.That((await GetFromDcsAsync("Lifecycle"))!.Pid, Is.EqualTo(Dps.Store.Processes["Lifecycle"].Id));
        Assert.That(await WaitForDcsMessageAsync("[Lifecycle]: hello"), Is.True);

        // StopPlugin: DCS marks it as being stopped (Pid 0), DPS stops it and reports -1 when it has exited
        await client.StopPluginAsync("Lifecycle");
        Assert.That(await DpsHost.WaitForAsync(async () => (await GetFromDcsAsync("Lifecycle"))!.Pid == -1), Is.True);
        Assert.That(Dps.Store.Processes.ContainsKey("Lifecycle"), Is.False);

        // UninstallPlugin: both services remove their part
        await client.UninstallPluginAsync("Lifecycle");
        Assert.That(await GetFromDcsAsync("Lifecycle"), Is.Null);
        Assert.That(await Dps.GetPluginAsync("Lifecycle"), Is.Null);
        Assert.That(Directory.Exists(Path.Combine(PluginDirectory, "Lifecycle")), Is.False);
        Assert.That(File.Exists(Path.Combine(SdDirectory, "macros/lifecycle.g")), Is.False);
        Assert.That(File.Exists(Dps.ProfilePath("Lifecycle")), Is.False);
    }

    [Test]
    public async Task UpgradeKeepsTheConfigFiles()
    {
        // Installing a plugin again uninstalls it with ForUpgrade first (DuetControlServer/Commands/Plugins/InstallPlugin.cs:127-146), DPS keeps the config files
        using CommandConnection client = await ConnectAsync();
        await StartDpsAsync();
        await using FakePluginService root = await StartRootServiceAsync();
        string manifest = new JsonObject { ["id"] = "Upgraded", ["name"] = "Upgraded", ["sbcConfigFiles"] = new JsonArray("upgraded.conf") }.ToJsonString();
        await client.InstallPluginAsync(PluginPackage.Create(OutsidePath("v1.zip"), manifest, ("sd/sys/upgraded.conf", "v1"), ("dsf/v1", "v1")));
        File.WriteAllText(Path.Combine(SdDirectory, "sys/upgraded.conf"), "changed by the user");

        await client.InstallPluginAsync(PluginPackage.Create(OutsidePath("v2.zip"), manifest, ("sd/sys/upgraded.conf", "v2"), ("dsf/v2", "v2")));
        Assert.That(File.ReadAllText(Path.Combine(SdDirectory, "sys/upgraded.conf")), Is.EqualTo("changed by the user"));
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Upgraded/dsf/v1")), Is.False);
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Upgraded/dsf/v2")), Is.True);
        Assert.That((await GetFromDcsAsync("Upgraded"))!.DsfFiles, Is.EqualTo(new[] { "v2" }));
    }

    [Test]
    public async Task ErrorsOfDpsReachTheClient()
    {
        // DCS knows the plugin, DPS does not: the error of DPS is passed on (DuetControlServer/IPC/Processors/PluginService.cs:255-266)
        using CommandConnection client = await ConnectAsync();
        await StartDpsAsync();
        await AddToDcsAsync(new Plugin { Id = "OnlyInDcs", Name = "OnlyInDcs", SbcExecutable = "plugin.sh", Pid = -1 });
        InternalServerException e = (await Assert.ThrowsAsync<InternalServerException>(async () => await client.StartPluginAsync("OnlyInDcs")))!;
        Assert.That(e.Message, Does.Contain("Plugin OnlyInDcs not found by service"));
    }

    /// <summary>
    /// "By default root plugins are not allowed" (PLUGINS.md:15-16): DCS refuses to install a plugin with SuperUser unless RootPluginSupport is set
    /// (DuetControlServer/Commands/Plugins/InstallPlugin.cs:59-63). ReloadPlugin registers any manifest from the plugin directory
    /// (DuetControlServer/Commands/Plugins/ReloadPlugin.cs), StartPlugin sends a plugin with SuperUser to the root service (StartPlugin.cs:346-368 in DCS),
    /// and the root DPS starts it as root (DuetPluginService/Commands/StartPlugin.cs:57-61) - neither checks RootPluginSupport. The plugin directory belongs
    /// to the dsf user, and plugins with ManagePlugins may write it (AppArmorPermissionManager.cs:55-58), ManagePlugins is also what ReloadPlugin and
    /// StartPlugin require (DuetAPI/Commands/Plugins). DuetPiManagementPlugin is a root plugin that DCS loads from the same directory
    /// (Model/PeriodicUpdateService.cs:85-117), so where to check is a design question
    /// </summary>
    [Test]
    [Explicit("Bug: a SuperUser manifest written to the plugin directory and reloaded is started by the root service although RootPluginSupport is disabled")]
    public async Task RootPluginIsNotStartedWithoutRootPluginSupport()
    {
        using CommandConnection client = await ConnectAsync();
        await StartDpsAsync();
        await using FakePluginService root = await StartRootServiceAsync();
        Assert.That(DcsSettings.RootPluginSupport, Is.False);

        Directory.CreateDirectory(Path.Combine(PluginDirectory, "Escalated/dsf"));
        File.WriteAllText(Path.Combine(PluginDirectory, "Escalated.json"), "{\"id\":\"Escalated\",\"name\":\"Escalated\",\"sbcExecutable\":\"plugin.sh\",\"sbcPermissions\":[\"superUser\"]}");
        try
        {
            await client.ReloadPluginAsync("Escalated");
            await client.StartPluginAsync("Escalated");
        }
        catch (InternalServerException)
        {
            // Refusing it is fine
        }
        Assert.That(root.ReceivedCommands.Any(command => command.GetProperty("command").GetString() == "StartPlugin"), Is.False,
            "The root service was asked to start a root plugin");
    }
}
