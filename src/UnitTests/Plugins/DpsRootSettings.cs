using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace UnitTests.Plugins;

using File = System.IO.File;

/// <summary>
/// The root plugin service runs the commands of its settings file (plugins.json: package managers, systemd-run, the Python launcher, apparmor_parser) and
/// loads profiles made from the AppArmor template (apparmor.conf) as root, so it does that only if nobody but root may change these files
/// (DuetPluginService/RootSettingsFiles.cs, ServicePrivileges.ForSettingsFile). DPS takes the code paths of the root service here while the test runs as
/// regular user, so the files belong to the user of the test (<see cref="DpsHost"/>)
/// </summary>
[TestFixture]
public class DpsRootSettings : DpsTestBase
{
    private const UnixFileMode SettingsMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode DirectoryMode = SettingsMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>
    /// Ways in which others than root may change the settings file or replace it, like the dsf user could before (owner dsf:dsf, mode 0664 in the
    /// directory /opt/dsf/conf of dsf) or when an entry was replaced before the upgrade
    /// </summary>
    private static readonly Dictionary<string, Action<string>> Tampering = new()
    {
        ["writable by the group"] = file => File.SetUnixFileMode(file, SettingsMode | UnixFileMode.GroupWrite),
        ["writable by others"] = file => File.SetUnixFileMode(file, SettingsMode | UnixFileMode.OtherWrite),
        ["symlink"] = file =>
        {
            // The target is a file that nobody else may write, but its directory could be one where others may replace it
            string target = Path.Combine(Path.GetDirectoryName(file)!, "target.json");
            File.Move(file, target);
            File.CreateSymbolicLink(file, target);
        },
        ["missing"] = File.Delete,
        ["directory"] = file =>
        {
            File.Delete(file);
            Directory.CreateDirectory(file);
            File.SetUnixFileMode(file, DirectoryMode);
        },
        ["in a directory writable by the group without sticky bit"] = file => File.SetUnixFileMode(Path.GetDirectoryName(file)!, DirectoryMode | UnixFileMode.GroupWrite),
        ["in a directory writable by others"] = file => File.SetUnixFileMode(Path.GetDirectoryName(file)!, DirectoryMode | UnixFileMode.OtherWrite | UnixFileMode.StickyBit),
        ["in a symlinked directory"] = file =>
        {
            string directory = Path.GetDirectoryName(file)!;
            Directory.Move(directory, directory + ".real");
            File.CreateSymbolicLink(directory, directory + ".real");
        }
    };

    private static string Error(string file) => $"{file} must be a regular file of the root service that nobody else may write, in a directory of the root service " +
        "where nobody else may replace it (root:root 0644 in /opt/dsf/conf, root:dsf 1775)";

    private string SettingsError => "Refusing to run commands from the settings: " + Error(Dps!.SettingsFile);

    private string Package(string id, Action<JsonObject>? configure = null, params (string Name, string Content)[] files)
    {
        JsonObject manifest = new() { ["id"] = id, ["name"] = id, ["version"] = "1.0.0" };
        configure?.Invoke(manifest);
        return PluginPackage.Create(OutsidePath(id + ".zip"), manifest.ToJsonString(), files);
    }

    private async Task InstallAsync(string package)
    {
        DuetPluginService.IPC.InstallPlugin command = Dps!.Create<DuetPluginService.IPC.InstallPlugin>();
        command.PluginFile = package;
        await command.ExecuteAsync();
    }

    private async Task UninstallAsync(string id)
    {
        DuetPluginService.IPC.UninstallPlugin command = Dps!.Create<DuetPluginService.IPC.UninstallPlugin>();
        command.Plugin = id;
        await command.ExecuteAsync();
    }

    private async Task StartAsync(string id)
    {
        DuetPluginService.IPC.StartPlugin command = Dps!.Create<DuetPluginService.IPC.StartPlugin>();
        command.Plugin = id;
        await command.ExecuteAsync();
    }

    /// <summary>
    /// None of the tools of the settings ran
    /// </summary>
    private void AssertNoToolRan()
    {
        DuetPluginService.Settings settings = Dps!.Settings;
        foreach (string tool in new[] { settings.PreinstallPackageCommand, settings.InstallPackageCommand, settings.InstallPythonPackageCommand, settings.InstallLocalPackageCommand,
            settings.UninstallLocalPackageCommand, settings.SystemdRunCommand, settings.AppArmorParser })
        {
            Assert.That(FakeTool.Calls(tool), Is.Empty, Path.GetFileName(tool));
        }
    }

    [TestCase("writable by the group")]
    [TestCase("writable by others")]
    [TestCase("symlink")]
    [TestCase("missing")]
    [TestCase("directory")]
    [TestCase("in a directory writable by the group without sticky bit")]
    [TestCase("in a directory writable by others")]
    [TestCase("in a symlinked directory")]
    public async Task SettingsThatOthersMayChangeAreRefused(string tampering)
    {
        // The root service decides at start, before it reads the file, and says why in the journal. The service keeps running, the commands that would
        // use the settings are refused with the same message
        await StartDpsAsync(register: false, privileged: true, changeSettingsFile: Tampering[tampering]);
        Assert.That(Dps!.Privileges.SettingsError, Is.EqualTo(SettingsError));
        Assert.That(Dps.Log.Entries.Any(entry => entry.Level == LogLevel.Error && entry.Message == SettingsError), Is.True);

        string package = OutsidePath("single.deb");
        File.WriteAllText(package, "not a ZIP file");
        DuetPluginService.IPC.InstallSystemPackage command = Dps.Create<DuetPluginService.IPC.InstallSystemPackage>();
        command.PackageFile = package;
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await command.ExecuteAsync()))!;
        Assert.That(e.Message, Is.EqualTo(SettingsError));
        AssertNoToolRan();
    }

    [Test]
    public async Task PluginsAreNotInstalledWithRefusedSettings()
    {
        await StartDpsAsync(register: false, privileged: true, changeSettingsFile: Tampering["writable by the group"]);

        // Packages of a regular plugin: neither the preinstall command nor the package manager
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await InstallAsync(Package("Packages", manifest =>
            manifest["sbcPackageDependencies"] = new JsonArray("nano"), ("dsf/file", "x")))))!;
        Assert.That(e.Message, Is.EqualTo(SettingsError));

        // Security profile of a regular plugin: neither the template nor apparmor_parser
        e = (await Assert.ThrowsAsync<ArgumentException>(async () => await InstallAsync(Package("Confined", null, ("dsf/file", "x")))))!;
        Assert.That(e.Message, Is.EqualTo(SettingsError));
        Assert.That(File.Exists(Dps!.ProfilePath("Confined")), Is.False);

        // Files of a root plugin in the plugin directory of the settings, and its Python packages
        e = (await Assert.ThrowsAsync<ArgumentException>(async () => await InstallAsync(Package("Root", manifest =>
        {
            manifest["sbcExecutable"] = "root";
            manifest["sbcPermissions"] = new JsonArray("superUser");
            manifest["sbcPythonDependencies"] = new JsonArray("requests");
        }, ("dsf/root", "#!/bin/sh\n")))))!;
        Assert.That(e.Message, Is.EqualTo(SettingsError));
        Assert.That(Path.Exists(Path.Combine(PluginDirectory, "Root")), Is.False);
        Assert.That(Path.Exists(Path.Combine(PluginDirectory, "Root.json")), Is.False);

        AssertNoToolRan();
        Assert.That(Dps.Store.Plugins, Is.Empty);
    }

    [Test]
    public async Task PluginsAreNotUninstalledOrStartedWithRefusedSettings()
    {
        await StartDpsAsync(register: false, privileged: true, changeSettingsFile: Tampering["writable by the group"]);
        await EnableRootPluginSupportAsync();

        // Security profile of a regular plugin
        Plugin confined = new() { Id = "Confined", Name = "Confined", SbcExecutable = "confined", Pid = -1 };
        Dps!.WritePlugin(confined, "exit 0");
        await Dps.AddToStoreAsync(confined);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await UninstallAsync("Confined")))!;
        Assert.That(e.Message, Is.EqualTo(SettingsError));
        Assert.That(File.Exists(Dps.ProfilePath("Confined")), Is.True);

        // Files of a root plugin
        Plugin root = new() { Id = "Root", Name = "Root", SbcExecutable = "plugin.sh", SbcPermissions = SbcPermissions.SuperUser, Pid = -1 };
        root.DsfFiles.Add("plugin.sh");
        Dps.WritePlugin(root, "exec sleep 30", profile: false);
        await Dps.AddToStoreAsync(root);
        await AddToDcsAsync(root);
        e = (await Assert.ThrowsAsync<ArgumentException>(async () => await UninstallAsync("Root")))!;
        Assert.That(e.Message, Is.EqualTo(SettingsError));
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Root/dsf/plugin.sh")), Is.True);
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Root.json")), Is.True);

        // A root plugin is not started from the plugin directory of the settings
        e = (await Assert.ThrowsAsync<ArgumentException>(async () => await StartAsync("Root")))!;
        Assert.That(e.Message, Is.EqualTo(SettingsError));
        Assert.That(Dps.Store.Processes, Is.Empty);

        // Nor are system packages removed
        DuetPluginService.IPC.UninstallSystemPackage command = Dps.Create<DuetPluginService.IPC.UninstallSystemPackage>();
        command.Package = "nano";
        e = (await Assert.ThrowsAsync<ArgumentException>(async () => await command.ExecuteAsync()))!;
        Assert.That(e.Message, Is.EqualTo(SettingsError));

        AssertNoToolRan();
    }

    [Test]
    public async Task ProfilesAreNotRegeneratedWithRefusedSettings()
    {
        File.WriteAllText(Path.Combine(PluginDirectory, "Missing.json"), "{\"id\":\"Missing\",\"name\":\"Missing\"}");
        await StartDpsAsync(register: false, privileged: true, changeSettingsFile: Tampering["writable by the group"]);
        Assert.That(Dps!.Log.Entries.Any(entry => entry.Message == "Failed to regenerate AppArmor profile for plugin Missing" && entry.Exception?.Message == SettingsError), Is.True);
        Assert.That(File.Exists(Dps.ProfilePath("Missing")), Is.False);
        AssertNoToolRan();
    }

    [Test]
    public async Task PluginsWithoutWorkForTheRootServiceAreInstalledWithRefusedSettings()
    {
        // Like the CHX image, which leaves plugins.json to dsf and disables AppArmor there: the root service has nothing to run for a regular plugin
        // without packages, so the installation and the uninstallation that DCS sends to both services go on
        await StartDpsAsync(settings => settings.DisableAppArmor = true, register: false, privileged: true, changeSettingsFile: Tampering["writable by the group"]);
        await InstallAsync(Package("Web", null, ("dwc/Web.js", "js")));
        Assert.That(await Dps!.GetPluginAsync("Web"), Is.Not.Null);
        await UninstallAsync("Web");
        Assert.That(await Dps.GetPluginAsync("Web"), Is.Null);
        AssertNoToolRan();
    }

    [TestCase("writable by the group")]
    [TestCase("symlink")]
    [TestCase("missing")]
    public async Task TemplateThatOthersMayChangeIsRefused(string tampering)
    {
        // The profile is loaded as root, so the template must be as safe as the settings, also when the settings name another file
        await StartDpsAsync(settings => Tampering[tampering](settings.AppArmorTemplate), register: false, privileged: true);
        Assert.That(Dps!.Privileges.SettingsError, Is.Null);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await InstallAsync(Package("Confined", null, ("dsf/file", "x")))))!;
        Assert.That(e.Message, Is.EqualTo($"Refusing to install the security profile of plugin Confined: {Error(Dps.Settings.AppArmorTemplate)}"));
        Assert.That(File.Exists(Dps.ProfilePath("Confined")), Is.False);
        AssertNoToolRan();
    }

    [Test]
    public async Task SettingsAreCheckedOnlyByTheRootService()
    {
        // The packages install the files so that the root service accepts them
        await StartDpsAsync(register: false, privileged: true);
        Assert.That(Dps!.Privileges.SettingsError, Is.Null);
        Assert.That(Dps.Log.Entries.Any(entry => entry.Level == LogLevel.Error), Is.False);
        await Dps.DisposeAsync();

        // The regular service runs as dsf and runs nothing as root, so its settings may be changed by dsf as before
        await using DpsHost regular = await DpsHost.StartAsync(Dcs, PluginDirectory, register: false, changeSettingsFile: Tampering["writable by the group"]);
        Assert.That(regular.Privileges.SettingsError, Is.Null);
        Assert.That(regular.Log.Entries.Any(entry => entry.Level == LogLevel.Error), Is.False);
    }
}
