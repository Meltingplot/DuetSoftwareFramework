using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using NUnit.Framework;

namespace UnitTests.Plugins;

using File = System.IO.File;

/// <summary>
/// UninstallPlugin (DuetPluginService/Commands/UninstallPlugin.cs) and ReloadPlugin (Commands/ReloadPlugin.cs) of the DuetPluginService instance that runs as
/// dsf user, and the manifests that PluginService.StartAsync loads (PluginService.cs:34-59). The plugins are installed with the InstallPlugin command of DPS
/// first, so the file lists in their manifests are the ones DPS wrote. The part of the root service, the removal of the AppArmor profile
/// (UninstallPlugin.cs:167-171) and the regeneration of missing profiles at start (PluginService.cs:62-112), is covered by <see cref="DpsRootService"/>
/// </summary>
[TestFixture]
public class DpsUninstallReloadPlugin : DpsTestBase
{
    private async Task<Plugin> InstallAsync(string id, Action<JsonObject>? configure = null, params (string Name, string Content)[] files)
    {
        JsonObject manifest = new() { ["id"] = id, ["name"] = id };
        configure?.Invoke(manifest);
        DuetPluginService.IPC.InstallPlugin command = Dps!.Create<DuetPluginService.IPC.InstallPlugin>();
        command.PluginFile = PluginPackage.Create(OutsidePath(id + ".zip"), manifest.ToJsonString(), files);
        await command.ExecuteAsync();
        return (await Dps.GetPluginAsync(id))!;
    }

    private async Task UninstallAsync(string id, bool forUpgrade = false)
    {
        DuetPluginService.IPC.UninstallPlugin command = Dps!.Create<DuetPluginService.IPC.UninstallPlugin>();
        command.Plugin = id;
        command.ForUpgrade = forUpgrade;
        await command.ExecuteAsync();
    }

    private async Task ReloadAsync(string id)
    {
        DuetPluginService.IPC.ReloadPlugin command = Dps!.Create<DuetPluginService.IPC.ReloadPlugin>();
        command.Plugin = id;
        await command.ExecuteAsync();
    }

    private string Sd(string path) => Path.Combine(SdDirectory, path);

    [Test]
    public async Task UninstallRemovesThePlugin()
    {
        await StartDpsAsync(register: false);
        await InstallAsync("Gone", null, ("dsf/gone", "x"), ("dwc/Gone.js", "js"), ("dwc/gone/style.css", "css"), ("sd/macros/gone.g", "M117"), ("sd/sys/daemon.g", "M400"));
        await UninstallAsync("Gone");

        // Manifest, web files and their directory if empty, SD files and the plugin directory are removed (UninstallPlugin.cs:88-164)
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Gone.json")), Is.False);
        Assert.That(Directory.Exists(Path.Combine(PluginDirectory, "Gone")), Is.False);
        Assert.That(Path.Exists(Sd("www/Gone.js")), Is.False);
        Assert.That(Directory.Exists(Sd("www/gone")), Is.False);
        Assert.That(Directory.Exists(Sd("www")), Is.True, "0:/www is not empty");
        Assert.That(File.Exists(Sd("macros/gone.g")), Is.False);

        // daemon.g may still be open, so it is renamed (UninstallPlugin.cs:118-123)
        Assert.That(File.Exists(Sd("sys/daemon.g")), Is.False);
        Assert.That(File.ReadAllText(Sd("sys/daemon.g.bak")), Is.EqualTo("M400"));

        Assert.That(await Dps!.GetPluginAsync("Gone"), Is.Null);
        Assert.That(FakeTool.Calls(Dps.Settings.AppArmorParser), Is.Empty, "Only the root service removes profiles");
    }

    [Test]
    public async Task UninstallForUpgradeKeepsConfigAndCustomFiles()
    {
        await StartDpsAsync(register: false);
        await InstallAsync("Kept", manifest => manifest["sbcConfigFiles"] = new JsonArray("kept.conf"),
            ("dsf/kept", "x"), ("dwc/Kept.js", "js"), ("sd/sys/kept.conf", "conf"), ("sd/macros/kept.g", "M117"));
        string custom = Path.Combine(PluginDirectory, "Kept/dsf/settings.local");
        File.WriteAllText(custom, "written by the plugin");
        await UninstallAsync("Kept", forUpgrade: true);

        // Only the installed files go, config files and files the plugin made itself stay (UninstallPlugin.cs:112-154)
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Kept.json")), Is.False);
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Kept/dsf/kept")), Is.False);
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Kept/dwc/Kept.js")), Is.False);
        Assert.That(File.Exists(custom), Is.True);
        Assert.That(File.ReadAllText(Sd("sys/kept.conf")), Is.EqualTo("conf"));
        Assert.That(File.Exists(Sd("macros/kept.g")), Is.False);
        Assert.That(await Dps!.GetPluginAsync("Kept"), Is.Null);
    }

    [Test]
    public async Task RunningPluginIsNotUninstalled()
    {
        await StartDpsAsync(register: false);
        Plugin plugin = await InstallAsync("Busy", null, ("dsf/busy", "x"));
        plugin.Pid = 1234;
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await UninstallAsync("Busy")))!;
        Assert.That(e.Message, Is.EqualTo("Plugin must be stopped before it can be uninstalled"));
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Busy.json")), Is.True);
    }

    [Test]
    public async Task UnknownPluginIsNotUninstalled()
    {
        await StartDpsAsync(register: false);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await UninstallAsync("Unknown")))!;
        Assert.That(e.Message, Is.EqualTo("Plugin Unknown not found by service"));
    }

    [Test]
    public async Task PluginWithReadOnlyManifestIsNotUninstalled()
    {
        // A manifest without write permission marks a plugin that came with a system package (UninstallPlugin.cs:71-76)
        await StartDpsAsync(register: false);
        await InstallAsync("System", null, ("dsf/system", "x"));
        string manifest = Path.Combine(PluginDirectory, "System.json");
        File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await UninstallAsync("System")))!;
        Assert.That(e.Message, Is.EqualTo("Plugin cannot be uninstalled via API"));
        Assert.That(File.Exists(manifest), Is.True);
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "System/dsf/system")), Is.True);
        Assert.That(await Dps!.GetPluginAsync("System"), Is.Not.Null);
    }

    [Test]
    public async Task FilesOfRootPluginsAreSplitBetweenTheServices()
    {
        // Each service removes what it installed: the regular service the SD files and the links to the web files, the root service the manifest and the
        // plugin directory of a plugin with SuperUser (UninstallPlugin.cs), which it installed itself
        await StartDpsAsync(register: false);
        Plugin plugin = await InstallAsync("RootFiles", manifest => manifest["sbcPermissions"] = new JsonArray("superUser"),
            ("dsf/root", "x"), ("dwc/RootFiles.js", "js"), ("sd/macros/root.g", "M117"));
        Dps!.WritePlugin(plugin, "exit 0", profile: false);
        await UninstallAsync("RootFiles");
        Assert.That(File.Exists(Sd("macros/root.g")), Is.False);
        Assert.That(Path.Exists(Sd("www/RootFiles.js")), Is.False);
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "RootFiles.json")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(PluginDirectory, "RootFiles/dsf")), Is.True);
        Assert.That(await Dps.GetPluginAsync("RootFiles"), Is.Null);
    }

    [Test]
    public async Task RootPluginOfASystemPackageIsNotUninstalledByEitherService()
    {
        // The read-only manifest is checked by both services before either deletes anything, here by the regular service that comes first
        await StartDpsAsync(register: false);
        Plugin plugin = await InstallAsync("Packaged", manifest => manifest["sbcPermissions"] = new JsonArray("superUser"), ("sd/macros/packaged.g", "M117"));
        Dps!.WritePlugin(plugin, "exit 0", profile: false);
        File.SetUnixFileMode(Path.Combine(PluginDirectory, "Packaged.json"), UnixFileMode.UserRead | UnixFileMode.GroupRead);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await UninstallAsync("Packaged")))!;
        Assert.That(e.Message, Is.EqualTo("Plugin cannot be uninstalled via API"));
        Assert.That(File.Exists(Sd("macros/packaged.g")), Is.True);
        Assert.That(await Dps.GetPluginAsync("Packaged"), Is.Not.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SymlinkedPluginDirectoryLosesTheLinkOnly(bool forUpgrade)
    {
        // A plugin with ManagePlugins or FileSystemAccess may replace its plugin directory by a symlink. The checks of UninstallPlugin resolve symlinks
        // with the plugin directory as base, so nothing behind such a link is deleted, only the link
        await StartDpsAsync(register: false);
        await InstallAsync("Replaced", null, ("dsf/replaced", "x"));
        string elsewhere = OutsidePath("elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "dsf"));
        File.WriteAllText(Path.Combine(elsewhere, "dsf", "replaced"), "keep me");
        Directory.Delete(Path.Combine(PluginDirectory, "Replaced"), true);
        Directory.CreateSymbolicLink(Path.Combine(PluginDirectory, "Replaced"), elsewhere);

        await UninstallAsync("Replaced", forUpgrade);
        Assert.That(Path.Exists(Path.Combine(PluginDirectory, "Replaced")), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(elsewhere, "dsf", "replaced")), Is.EqualTo("keep me"));
        Assert.That(Dps!.Log.Contains($"Plugin directory {Path.Combine(PluginDirectory, "Replaced")} is a symlink, removing the link only"), Is.True);
    }

    /// <summary>
    /// UninstallPlugin deletes the manifest only if it exists, and it checks the file mode of the manifest only then as well, so a plugin whose manifest is
    /// gone is still uninstalled
    /// </summary>
    [Test]
    public async Task PluginWithoutManifestIsUninstalled()
    {
        await StartDpsAsync(register: false);
        await InstallAsync("Orphan", null, ("dsf/orphan", "x"));
        File.Delete(Path.Combine(PluginDirectory, "Orphan.json"));
        await UninstallAsync("Orphan");
        Assert.That(Directory.Exists(Path.Combine(PluginDirectory, "Orphan")), Is.False);
        Assert.That(await Dps!.GetPluginAsync("Orphan"), Is.Null);
    }

    /// <summary>
    /// InstallPlugin keeps every installed file inside its directory (zip-slip guard), UninstallPlugin deletes the files that the manifest lists with the same
    /// check (Path.IsInside, DuetSharedLibrary/PathExtensions.cs). The manifest is a file of the dsf user that plugins with ManagePlugins or FileSystemAccess
    /// may write (AppArmorPermissionManager.cs:55-64). For a plugin with SuperUser the root service runs this code, so it would delete as root whatever such a
    /// manifest lists. A listed file outside its directory is skipped with a warning, the rest of the plugin is uninstalled
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task UninstallDeletesOnlyFilesInsideTheSdDirectory(bool forUpgrade)
    {
        await StartDpsAsync(register: false);
        string outside = OutsidePath("precious.txt");
        File.WriteAllText(outside, "keep me");
        Plugin plugin = await InstallAsync("Tampered", null, ("dsf/tampered", "x"), ("sd/macros/tampered.g", "M117"));

        // The manifest lists a file outside 0:/, outside 0:/www and outside the plugin directory
        plugin.SdFiles.Add(Path.GetRelativePath(SdDirectory, outside));
        plugin.DwcFiles.Add(Path.GetRelativePath(Path.Combine(SdDirectory, "www"), outside));
        plugin.DsfFiles.Add(Path.GetRelativePath(Path.Combine(PluginDirectory, "Tampered", "dsf"), outside));
        File.WriteAllText(Path.Combine(PluginDirectory, "Tampered.json"), JsonSerializer.Serialize(plugin, ObjectModelContext.Default.Plugin));
        await ReloadAsync("Tampered");

        await UninstallAsync("Tampered", forUpgrade);
        Assert.That(File.Exists(outside), Is.True, "A file outside its directory was deleted");
        Assert.That(Dps!.Log.Contains($"Not deleting {Path.Combine(SdDirectory, Path.GetRelativePath(SdDirectory, outside))}, it is outside of {SdDirectory}"), Is.True);
        Assert.That(File.Exists(Sd("macros/tampered.g")), Is.False, "The files inside 0:/ are still uninstalled");
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Tampered/dsf/tampered")), Is.False);
        Assert.That(await Dps.GetPluginAsync("Tampered"), Is.Null);
    }

    [Test]
    public async Task ReloadReadsTheManifestAgain()
    {
        // ReloadPlugin.cs:41-64: a new manifest adds the plugin, a changed one updates it, a missing one removes it
        await StartDpsAsync(register: false);
        File.WriteAllText(Path.Combine(PluginDirectory, "Reloaded.json"), "{\"id\":\"Reloaded\",\"name\":\"Reloaded\",\"version\":\"1.0\"}");
        await ReloadAsync("Reloaded");
        Plugin plugin = (await Dps!.GetPluginAsync("Reloaded"))!;
        Assert.That(plugin.Version, Is.EqualTo("1.0"));
        Assert.That(plugin.Pid, Is.EqualTo(-1));

        File.WriteAllText(Path.Combine(PluginDirectory, "Reloaded.json"), "{\"id\":\"Reloaded\",\"name\":\"Reloaded\",\"version\":\"2.0\"}");
        plugin.Pid = 77;
        await ReloadAsync("Reloaded");
        Assert.That(await Dps.GetPluginAsync("Reloaded"), Is.SameAs(plugin));
        Assert.That(plugin.Version, Is.EqualTo("2.0"));
        Assert.That(plugin.Pid, Is.EqualTo(-1));

        File.Delete(Path.Combine(PluginDirectory, "Reloaded.json"));
        await ReloadAsync("Reloaded");
        Assert.That(await Dps.GetPluginAsync("Reloaded"), Is.Null);

        // A plugin that is neither installed nor known is ignored
        await ReloadAsync("Unknown");
        Assert.That(Dps.Store.Plugins, Is.Empty);
    }

    [Test]
    public async Task InstalledPluginsAreLoadedAtStart()
    {
        // PluginService.StartAsync loads every *.json file of the plugin directory and skips the ones that cannot be read (PluginService.cs:37-59)
        File.WriteAllText(Path.Combine(PluginDirectory, "First.json"), "{\"id\":\"First\",\"name\":\"First\",\"pid\":1234}");
        File.WriteAllText(Path.Combine(PluginDirectory, "Second.json"), "{\"id\":\"Second\",\"name\":\"Second\",\"sbcPermissions\":[\"superUser\"]}");
        File.WriteAllText(Path.Combine(PluginDirectory, "Broken.json"), "{\"id\":");
        File.WriteAllText(Path.Combine(PluginDirectory, "notes.txt"), "{\"id\":\"Notes\",\"name\":\"Notes\"}");
        await StartDpsAsync(register: false);

        Assert.That(Dps!.Store.Plugins.ConvertAll(plugin => plugin.Id), Is.EquivalentTo(new[] { "First", "Second" }));
        Assert.That((await Dps.GetPluginAsync("First"))!.Pid, Is.EqualTo(-1));
        Assert.That(Dps.Log.Contains("Plugin First loaded"), Is.True);
        Assert.That(Dps.Log.Contains("Failed to load plugin manifest Broken.json"), Is.True);

        // A manifest with an id that the setter refuses (PluginManifest.cs:32-38) is not checked here: whether UpdateFromJson throws depends on the static
        // ObjectModel.OnDeserializationFailed handler (DuetAPI/ObjectModel/ObjectModel.cs:218-226), which every DCS model registers in this process
        // (DuetControlServer/Model/ObjectModel.cs:83) but which DPS does not have
    }

    /// <summary>
    /// DCS skips manifests without id or name at start (DuetControlServer/Model/PeriodicUpdateService.cs), DPS does the same with the same check
    /// (DuetSharedLibrary/PluginManifestCheck.cs). Otherwise the root service would regenerate the missing profile "dsf." for it (PluginService.cs:62-112),
    /// whose attachment is the whole plugin directory, Path.Combine(pluginDirectory, "") (AppArmorPermissionManager.cs:34)
    /// </summary>
    [Test]
    public async Task ManifestWithoutIdIsNotLoadedAtStart()
    {
        File.WriteAllText(Path.Combine(PluginDirectory, "Anonymous.json"), "{\"name\":\"Anonymous\",\"sbcPermissions\":[\"fileSystemAccess\"]}");
        File.WriteAllText(Path.Combine(PluginDirectory, "Valid.json"), "{\"id\":\"Valid\",\"name\":\"Valid\"}");
        await StartDpsAsync(register: false, privileged: true);
        Assert.That(Dps!.Store.Plugins.ConvertAll(plugin => plugin.Id), Is.EqualTo(new[] { "Valid" }));
        Assert.That(Dps.Log.Contains("Skipping plugin manifest Anonymous.json: Plugin manifest must provide an id and a name"), Is.True);
        Assert.That(File.Exists(Dps.ProfilePath("")), Is.False, "A profile for the whole plugin directory was generated");
        Assert.That(File.Exists(Dps.ProfilePath("Valid")), Is.True);
    }

    [Test]
    public async Task ReloadRefusesAManifestWithoutId()
    {
        // ReloadPlugin makes the check of the installation before it changes anything
        await StartDpsAsync(register: false);
        string file = Path.Combine(PluginDirectory, "Changed.json");
        File.WriteAllText(file, "{\"id\":\"Changed\",\"name\":\"Changed\",\"version\":\"1.0\"}");
        await ReloadAsync("Changed");

        File.WriteAllText(file, "{\"name\":\"Changed\",\"version\":\"2.0\"}");
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await ReloadAsync("Changed")))!;
        Assert.That(e.Message, Is.EqualTo("Plugin manifest must provide an id and a name"));
        Assert.That((await Dps!.GetPluginAsync("Changed"))!.Version, Is.EqualTo("1.0"), "The plugin was changed");

        File.WriteAllText(Path.Combine(PluginDirectory, "New.json"), "{\"name\":\"New\"}");
        Assert.ThrowsAsync<ArgumentException>(async () => await ReloadAsync("New"));
        Assert.That(Dps.Store.Plugins.ConvertAll(item => item.Id), Is.EqualTo(new[] { "Changed" }), "An empty plugin was added");
    }

    /// <summary>
    /// ServicePlugins "is reserved for internal purposes" and InstallPlugin refuses manifests with it. ReloadPlugin and the start of DPS and DCS read the
    /// manifests on disk again, all of them make that check of the installation (DuetSharedLibrary/PluginManifestCheck.cs).
    /// DCS grants a plugin connection the permissions of its manifest (DuetControlServer/IPC/Connection.cs:121-124, 136-140), and ServicePlugins is enough for
    /// Install/UninstallSystemPackage (RequiredPermissions SuperUser | ServicePlugins, any of them). A plugin with ManagePlugins or FileSystemAccess may write the
    /// manifests (AppArmorPermissionManager.cs:55-64)
    /// </summary>
    [Test]
    public async Task ReloadRefusesTheReservedPermission()
    {
        await StartDpsAsync(register: false);
        File.WriteAllText(Path.Combine(PluginDirectory, "Elevated.json"), "{\"id\":\"Elevated\",\"name\":\"Elevated\",\"sbcPermissions\":[\"servicePlugins\"]}");
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await ReloadAsync("Elevated")))!;
        Assert.That(e.Message, Is.EqualTo("ServicePlugins permission is reserved for internal purposes"));
        Assert.That(await Dps!.GetPluginAsync("Elevated"), Is.Null);
    }

    /// <summary>
    /// Same as <see cref="ReloadRefusesTheReservedPermission"/> for the manifests that PluginService.StartAsync loads (PluginService.cs:43-54)
    /// </summary>
    [Test]
    public async Task ReservedPermissionIsNotLoadedAtStart()
    {
        File.WriteAllText(Path.Combine(PluginDirectory, "Elevated.json"), "{\"id\":\"Elevated\",\"name\":\"Elevated\",\"sbcPermissions\":[\"servicePlugins\"]}");
        await StartDpsAsync(register: false);
        Assert.That(await Dps!.GetPluginAsync("Elevated"), Is.Null);
        Assert.That(Dps.Log.Contains("Skipping plugin manifest Elevated.json: ServicePlugins permission is reserved for internal purposes"), Is.True);
    }
}
