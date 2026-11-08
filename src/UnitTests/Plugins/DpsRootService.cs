using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using NUnit.Framework;

namespace UnitTests.Plugins;

using File = System.IO.File;

/// <summary>
/// Code paths of the DuetPluginService instance that runs as root (duetpluginservice-root.service): DPS takes them when
/// <see cref="DuetPluginService.ServicePrivileges.IsPrivileged"/> is set, here while the test runs as regular user. apparmor_parser, apt, dpkg, the Python
/// installer and systemd-run are <see cref="FakeTool"/> scripts, so nothing reaches the package manager or the kernel
/// </summary>
[TestFixture]
public class DpsRootService : DpsTestBase
{
    private string Package(string manifest, params (string Name, string Content)[] files) => PluginPackage.Create(OutsidePath("plugin.zip"), manifest, files);

    private static string Manifest(string id, Action<JsonObject>? configure = null)
    {
        JsonObject manifest = new() { ["id"] = id, ["name"] = id, ["version"] = "1.0.0" };
        configure?.Invoke(manifest);
        return manifest.ToJsonString();
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

    private async Task StopAsync(string id)
    {
        DuetPluginService.IPC.StopPlugin command = Dps!.Create<DuetPluginService.IPC.StopPlugin>();
        command.Plugin = id;
        await command.ExecuteAsync();
    }

    /// <summary>
    /// Replace a fake tool by one that also records the value of DSF_TEST_FRONTEND. The test process may have DEBIAN_FRONTEND already, which
    /// EnvironmentVariables.Add (InstallPlugin.cs:336-339) refuses, so the tests pass their own variable via InstallPackageEnvironment
    /// </summary>
    private static string EnvironmentTool(string path)
    {
        File.WriteAllText(path, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$0.calls\"\nprintf '%s\\n' \"$DSF_TEST_FRONTEND\" >> \"$0.env\"\nif [ -f \"$0.exit\" ]; then exit $(cat \"$0.exit\"); fi\nexit 0\n");
        return path;
    }

    private Task StartRootDpsAsync(Action<DuetPluginService.Settings>? configure = null) => StartDpsAsync(settings =>
    {
        settings.InstallPackageEnvironment = new() { ["DSF_TEST_FRONTEND"] = "noninteractive" };
        EnvironmentTool(settings.PreinstallPackageCommand);
        EnvironmentTool(settings.InstallPackageCommand);
        configure?.Invoke(settings);
    }, register: false, privileged: true);

    [Test]
    public async Task PackagesAndProfileAreInstalledByTheRootService()
    {
        // InstallPlugin.cs:54-79: the preinstall command once, then each package with InstallPackageEnvironment, then the profile
        await StartRootDpsAsync();
        await InstallAsync(Package(Manifest("Confined", manifest =>
        {
            manifest["sbcExecutable"] = "confined";
            manifest["sbcPermissions"] = new JsonArray("readGCodes");
            manifest["sbcPackageDependencies"] = new JsonArray("nano", "git");
            manifest["sbcPythonDependencies"] = new JsonArray("requests");
        }), ("dsf/confined", "binary"), ("sd/macros/confined.g", "M117")));

        Assert.That(FakeTool.Calls(Dps!.Settings.PreinstallPackageCommand), Is.EqualTo(new[] { "update" }));
        Assert.That(FakeTool.Calls(Dps.Settings.InstallPackageCommand), Is.EqualTo(new[] { "install -y nano", "install -y git" }));
        Assert.That(FakeTool.Environment(Dps.Settings.InstallPackageCommand), Is.EqualTo(new[] { "noninteractive", "noninteractive" }));
        Assert.That(File.ReadAllText(Dps.ProfilePath("Confined")), Does.Contain($"  {Path.Combine(SdDirectory, "gcodes")}/** r,"));
        Assert.That(FakeTool.Calls(Dps.Settings.AppArmorParser), Is.EqualTo(new[] { $"-r {Dps.ProfilePath("Confined")}" }));
        Assert.That(Dps.Log.Contains("Security profile installed"), Is.True);

        // The files, the manifest and the Python packages are up to the regular service (InstallPlugin.cs:80-264), the root service only registers the plugin
        Assert.That(Directory.Exists(Path.Combine(PluginDirectory, "Confined")), Is.False);
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Confined.json")), Is.False);
        Assert.That(File.Exists(Path.Combine(SdDirectory, "macros/confined.g")), Is.False);
        Assert.That(FakeTool.Calls(Dps.Settings.InstallPythonPackageCommand), Is.Empty);
        Assert.That(await Dps.GetPluginAsync("Confined"), Is.Not.Null);
    }

    [Test]
    public async Task NoPreinstallCommandWithoutPackages()
    {
        await StartRootDpsAsync();
        await InstallAsync(Package(Manifest("NoPackages"), ("dsf/file", "x")));
        Assert.That(FakeTool.Calls(Dps!.Settings.PreinstallPackageCommand), Is.Empty);
        Assert.That(FakeTool.Calls(Dps.Settings.InstallPackageCommand), Is.Empty);
        Assert.That(File.Exists(Dps.ProfilePath("NoPackages")), Is.True);
    }

    [Test]
    public async Task RootPluginsGetNoProfile()
    {
        // "Apply security profile for this plugin unless it gets root permissions anyway" (InstallPlugin.cs:71-78)
        await StartRootDpsAsync();
        await InstallAsync(Package(Manifest("Root", manifest => manifest["sbcPermissions"] = new JsonArray("superUser")), ("dsf/root", "x")));
        Assert.That(File.Exists(Dps!.ProfilePath("Root")), Is.False);
        Assert.That(FakeTool.Calls(Dps.Settings.AppArmorParser), Is.Empty);
        Assert.That(await Dps.GetPluginAsync("Root"), Is.Not.Null);
    }

    [Test]
    public async Task NoProfileWhenAppArmorIsDisabled()
    {
        await StartRootDpsAsync(settings => settings.DisableAppArmor = true);
        await InstallAsync(Package(Manifest("Unconfined"), ("dsf/file", "x")));
        Assert.That(File.Exists(Dps!.ProfilePath("Unconfined")), Is.False);
        Assert.That(FakeTool.Calls(Dps.Settings.AppArmorParser), Is.Empty);
    }

    [Test]
    public async Task FailingPackageInstallationIsReported()
    {
        // InstallPlugin.cs:341-349
        await StartRootDpsAsync();
        FakeTool.SetExitCode(Dps!.Settings.InstallPackageCommand, 100);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () =>
            await InstallAsync(Package(Manifest("NoPackage", manifest => manifest["sbcPackageDependencies"] = new JsonArray("missing")), ("dsf/file", "x")))))!;
        Assert.That(e.Message, Is.EqualTo("Failed to install package missing, package manager exited with code 100"));
        Assert.That(File.Exists(Dps.ProfilePath("NoPackage")), Is.False);
        Assert.That(await Dps.GetPluginAsync("NoPackage"), Is.Null);
    }

    [Test]
    public async Task ProfileThatCannotBeLoadedIsRemoved()
    {
        // AppArmorPermissionManager.cs:155-163: StartPlugin checks only that the profile exists, so a profile that the kernel refused must not stay
        await StartRootDpsAsync();
        FakeTool.SetExitCode(Dps!.Settings.AppArmorParser, 1);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await InstallAsync(Package(Manifest("Refused"), ("dsf/file", "x")))))!;
        Assert.That(e.Message, Is.EqualTo("Failed to load AppArmor profile for plugin Refused (apparmor_parser exited with code 1)"));
        Assert.That(File.Exists(Dps.ProfilePath("Refused")), Is.False);
        Assert.That(await Dps.GetPluginAsync("Refused"), Is.Null);
    }

    [Test]
    public async Task MissingProfilesAreRegeneratedAtStart()
    {
        // PluginService.cs:62-112: the DSF package removes the dsf.* profiles on upgrade, the root service writes the missing ones again at start
        Directory.CreateDirectory(PluginDirectory);
        File.WriteAllText(Path.Combine(PluginDirectory, "Missing.json"), "{\"id\":\"Missing\",\"name\":\"Missing\",\"sbcPermissions\":[\"readMacros\"]}");
        File.WriteAllText(Path.Combine(PluginDirectory, "Present.json"), "{\"id\":\"Present\",\"name\":\"Present\"}");
        File.WriteAllText(Path.Combine(PluginDirectory, "Root.json"), "{\"id\":\"Root\",\"name\":\"Root\",\"sbcPermissions\":[\"superUser\"]}");
        string? present = null;
        await StartDpsAsync(settings =>
        {
            present = Path.Combine(settings.AppArmorProfileDirectory, "dsf.Present");
            File.WriteAllText(present, "# kept");
        }, register: false, privileged: true);

        Assert.That(File.ReadAllText(Dps!.ProfilePath("Missing")), Does.Contain($"  {Path.Combine(SdDirectory, "macros")}/** r,"));
        Assert.That(File.ReadAllText(present!), Is.EqualTo("# kept"));
        Assert.That(File.Exists(Dps.ProfilePath("Root")), Is.False);
        Assert.That(FakeTool.Calls(Dps.Settings.AppArmorParser), Is.EqualTo(new[] { $"-r {Dps.ProfilePath("Missing")}" }));
        Assert.That(Dps.Log.Contains("Regenerated AppArmor profile for plugin Missing"), Is.True);

        // 0:/ was resolved through DCS
        Assert.That(Dps.Bridge!.Modes, Is.EqualTo(new[] { DuetAPI.Connection.ConnectionMode.Command }));
    }

    [Test]
    public async Task ProfileRegenerationGoesOnAfterAFailure()
    {
        // PluginService.cs:105-110: StartPlugin refuses the plugin whose profile is still missing, the other plugins get theirs
        File.WriteAllText(Path.Combine(PluginDirectory, "First.json"), "{\"id\":\"First\",\"name\":\"First\"}");
        File.WriteAllText(Path.Combine(PluginDirectory, "Second.json"), "{\"id\":\"Second\",\"name\":\"Second\"}");
        await StartDpsAsync(settings => FakeTool.SetExitCode(settings.AppArmorParser, 1), register: false, privileged: true);
        Assert.That(Dps!.Log.Contains("Failed to regenerate AppArmor profile for plugin First"), Is.True);
        Assert.That(Dps.Log.Contains("Failed to regenerate AppArmor profile for plugin Second"), Is.True);
        Assert.That(File.Exists(Dps.ProfilePath("First")) || File.Exists(Dps.ProfilePath("Second")), Is.False);
        Assert.That(Dps.Store.Plugins, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task ProfileRegenerationIsSkippedWithoutDcs()
    {
        // PluginService.cs:85-96
        File.WriteAllText(Path.Combine(PluginDirectory, "Waiting.json"), "{\"id\":\"Waiting\",\"name\":\"Waiting\"}");
        await using DpsHost dps = await DpsHost.StartAsync(null, PluginDirectory, register: false, privileged: true);
        Assert.That(dps.Log.Contains("Could not resolve SD path; skipping AppArmor profile regeneration"), Is.True);
        Assert.That(File.Exists(dps.ProfilePath("Waiting")), Is.False);
        Assert.That(dps.Store.Plugins, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ProfilesAreNotRegeneratedByTheRegularServiceOrWithoutAppArmor()
    {
        File.WriteAllText(Path.Combine(PluginDirectory, "Plain.json"), "{\"id\":\"Plain\",\"name\":\"Plain\"}");
        await using (DpsHost regular = await DpsHost.StartAsync(Dcs, PluginDirectory, register: false))
        {
            Assert.That(FakeTool.Calls(regular.Settings.AppArmorParser), Is.Empty);
            Assert.That(regular.Bridge!.Modes, Is.Empty);
        }
        await using DpsHost disabled = await DpsHost.StartAsync(Dcs, PluginDirectory, settings => settings.DisableAppArmor = true, register: false, privileged: true);
        Assert.That(FakeTool.Calls(disabled.Settings.AppArmorParser), Is.Empty);
        Assert.That(File.Exists(disabled.ProfilePath("Plain")), Is.False);
    }

    [Test]
    public async Task RootPluginIsStartedAndStoppedByTheRootService()
    {
        // StartPlugin.cs: the root service starts the plugins with SuperUser, without AppArmor profile, if DCS has root plugin support
        await StartDpsAsync(register: false, privileged: true);
        await EnableRootPluginSupportAsync();
        Plugin plugin = new() { Id = "Root", Name = "Root", SbcExecutable = "plugin.sh", SbcPermissions = SbcPermissions.SuperUser, Pid = -1 };
        Dps!.WritePlugin(plugin, "echo root\nexec sleep 30", profile: false);
        await Dps.AddToStoreAsync(plugin);
        await AddToDcsAsync(plugin);

        await StartAsync("Root");
        int pid = Dps.Store.Processes["Root"].Id;
        Assert.That((await GetFromDcsAsync("Root"))!.Pid, Is.EqualTo(pid));
        Assert.That(await WaitForDcsMessageAsync("[Root]: root"), Is.True);

        await StopAsync("Root");
        Assert.That(Dps.Log.Contains("Process stopped by SIGTERM"), Is.True);
        Assert.That(await DpsHost.WaitForAsync(async () => (await GetFromDcsAsync("Root"))!.Pid == -1), Is.True);
    }

    [Test]
    public async Task RootPluginIsNotStartedWithoutRootPluginSupportInDcs()
    {
        // DCS refuses to send StartPlugin then, the root service asks DCS for sbc.dsf.rootPluginSupport again before it starts a root plugin
        await StartDpsAsync(register: false, privileged: true);
        Plugin plugin = new() { Id = "Root", Name = "Root", SbcExecutable = "plugin.sh", SbcPermissions = SbcPermissions.SuperUser, Pid = -1 };
        Dps!.WritePlugin(plugin, "exec sleep 30", profile: false);
        await Dps.AddToStoreAsync(plugin);
        await AddToDcsAsync(plugin);

        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await StartAsync("Root")))!;
        Assert.That(e.Message, Is.EqualTo("Plugins with super-user permissions are not allowed"));
        Assert.That(Dps.Store.Processes, Is.Empty);
        Assert.That(plugin.Pid, Is.EqualTo(-1));
    }

    [Test]
    public async Task DuetPiManagementPluginIsStartedWithRootPluginSupport()
    {
        // The manifest that the duetpimanagementplugin package ships, the root service loads it at start like DCS
        string manifest = File.ReadAllText(DpsHost.ShippedFile("pkg/common/duetpimanagementplugin/opt/dsf/plugins/DuetPiManagementPlugin.json"))
            .Replace("DSFVER", DuetSharedLibrary.VersionHelper.GetVersion());
        File.WriteAllText(Path.Combine(PluginDirectory, "DuetPiManagementPlugin.json"), manifest);
        string executable = Path.Combine(PluginDirectory, "DuetPiManagementPlugin", "dsf", "DuetPiManagementPlugin");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "#!/bin/sh\nexec sleep 30\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await StartDpsAsync(register: false, privileged: true);
        await EnableRootPluginSupportAsync();
        Plugin plugin = (await Dps!.GetPluginAsync("DuetPiManagementPlugin"))!;
        await AddToDcsAsync(plugin);

        await StartAsync("DuetPiManagementPlugin");
        Assert.That((await GetFromDcsAsync("DuetPiManagementPlugin"))!.Pid, Is.EqualTo(Dps.Store.Processes["DuetPiManagementPlugin"].Id));
        await StopAsync("DuetPiManagementPlugin");
        Assert.That(await DpsHost.WaitForAsync(async () => (await GetFromDcsAsync("DuetPiManagementPlugin"))!.Pid == -1), Is.True);
    }

    [Test]
    public async Task RegularPluginsAreLeftToTheRegularService()
    {
        // StartPlugin.cs:57-61, StopPlugin.cs:50-54
        await StartDpsAsync(register: false, privileged: true);
        Plugin plugin = new() { Id = "Regular", Name = "Regular", SbcExecutable = "plugin.sh", Pid = -1 };
        Dps!.WritePlugin(plugin, "exec sleep 30");
        await Dps.AddToStoreAsync(plugin);
        InvalidOperationException e = (await Assert.ThrowsAsync<InvalidOperationException>(async () => await StartAsync("Regular")))!;
        Assert.That(e.Message, Is.EqualTo("Wrong plugin service to start this plugin"));
        e = (await Assert.ThrowsAsync<InvalidOperationException>(async () => await StopAsync("Regular")))!;
        Assert.That(e.Message, Is.EqualTo("Wrong plugin service to start this plugin"));
        Assert.That(Dps.Store.Processes, Is.Empty);

        ArgumentException unknown = (await Assert.ThrowsAsync<ArgumentException>(async () => await StartAsync("Unknown")))!;
        Assert.That(unknown.Message, Is.EqualTo("Plugin Unknown not found by root service"));
    }

    [Test]
    public async Task RootServiceUninstallsTheFilesOfRootPlugins()
    {
        // "Root plugins are deleted by the root service" (UninstallPlugin.cs:66-67)
        await StartDpsAsync(register: false, privileged: true);
        Plugin plugin = new() { Id = "RootFiles", Name = "RootFiles", SbcExecutable = "root", SbcPermissions = SbcPermissions.SuperUser, Pid = -1 };
        plugin.DsfFiles.Add("root");
        plugin.SdFiles.Add("macros/root.g");
        Dps!.WritePlugin(plugin, "exit 0", profile: false);
        File.WriteAllText(Path.Combine(SdDirectory, "macros/root.g"), "M117");
        await Dps.AddToStoreAsync(plugin);

        await UninstallAsync("RootFiles");
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "RootFiles.json")), Is.False);
        Assert.That(Directory.Exists(Path.Combine(PluginDirectory, "RootFiles")), Is.False);
        Assert.That(File.Exists(Path.Combine(SdDirectory, "macros/root.g")), Is.False);
        Assert.That(FakeTool.Calls(Dps.Settings.AppArmorParser), Is.Empty, "A root plugin has no profile to remove");
        Assert.That(await Dps.GetPluginAsync("RootFiles"), Is.Null);
    }

    [Test]
    public async Task RootServiceRemovesOnlyTheProfileOfRegularPlugins()
    {
        // UninstallPlugin.cs:167-171
        await StartDpsAsync(register: false, privileged: true);
        Plugin plugin = new() { Id = "Confined", Name = "Confined", SbcExecutable = "confined", Pid = -1 };
        plugin.DsfFiles.Add("confined");
        Dps!.WritePlugin(plugin, "exit 0");
        await Dps.AddToStoreAsync(plugin);

        await UninstallAsync("Confined");
        Assert.That(File.Exists(Dps.ProfilePath("Confined")), Is.False);
        Assert.That(FakeTool.Calls(Dps.Settings.AppArmorParser), Is.EqualTo(new[] { $"-R {Dps.ProfilePath("Confined")}" }));
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Confined.json")), Is.True, "The files are up to the regular service");
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Confined/dsf/confined")), Is.True);
        Assert.That(await Dps.GetPluginAsync("Confined"), Is.Null);
    }

    [Test]
    public async Task SystemPackageIsInstalledInATransientUnit()
    {
        // InstallSystemPackage.cs:105-133: a single package is passed to dpkg in the transient unit dsf-package, without update status
        await StartDpsAsync(register: false, privileged: true);
        string package = OutsidePath("single.deb");
        File.WriteAllText(package, "not a ZIP file");
        DuetPluginService.IPC.InstallSystemPackage command = Dps!.Create<DuetPluginService.IPC.InstallSystemPackage>();
        command.PackageFile = package;
        await command.ExecuteAsync();

        Assert.That(FakeTool.Calls(Dps.Settings.SystemdRunCommand), Is.EqualTo(new[]
        {
            $"--collect --wait --pipe --quiet --unit=dsf-package {Dps.Settings.InstallLocalPackageCommand} --force-confold -i {package}"
        }));
        Assert.That(Dps.Bridge!.Modes, Is.Empty, "No update status was reported");
    }

    [Test]
    public async Task CombinedPackageIsUnpackedInstalledAndItsUpdateScriptRun()
    {
        // InstallSystemPackage.cs:89-104, 135-164 and 172-182
        await StartDpsAsync(register: false, privileged: true);
        string name = "combined-" + Guid.NewGuid().ToString("N");
        string package = OutsidePath(name + ".zip");
        using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            foreach (string entry in new[] { "first.deb", "second.deb", "update.sh" })
            {
                using StreamWriter writer = new(archive.CreateEntry(entry).Open());
                writer.Write("#!/bin/sh\n");
            }
        }
        string unpacked = Path.Combine(Path.GetTempPath(), name);
        DuetPluginService.IPC.InstallSystemPackage command = Dps!.Create<DuetPluginService.IPC.InstallSystemPackage>();
        command.PackageFile = package;
        await command.ExecuteAsync();

        string[] calls = FakeTool.Calls(Dps.Settings.SystemdRunCommand);
        Assert.That(calls, Has.Length.EqualTo(2));
        string prefix = $"--collect --wait --pipe --quiet --unit=dsf-package {Dps.Settings.InstallLocalPackageCommand} --force-confold -i ";
        Assert.That(calls[0], Does.StartWith(prefix));
        Assert.That(calls[0][prefix.Length..].Split(' '), Is.EquivalentTo(new[] { Path.Combine(unpacked, "first.deb"), Path.Combine(unpacked, "second.deb") }));
        Assert.That(calls[1], Is.EqualTo($"--collect --wait --pipe --quiet --unit=dsf-package {Path.Combine(unpacked, "update.sh")}"));
        Assert.That(Directory.Exists(unpacked), Is.False, "The unpacked package is deleted");

        // Unpacking, installing, update script, end of the update
        Assert.That(Dps.Bridge!.Modes.Count(mode => mode == DuetAPI.Connection.ConnectionMode.Command), Is.EqualTo(4));
        using (await Dcs.Model.AccessReadOnlyAsync())
        {
            Assert.That(Dcs.Model.SBC?.Upgrade, Is.Null);
        }
    }

    [Test]
    public async Task FailingSystemPackageInstallationIsReported()
    {
        await StartDpsAsync(register: false, privileged: true);
        FakeTool.SetExitCode(Dps!.Settings.SystemdRunCommand, 3);
        string package = OutsidePath("failing.deb");
        File.WriteAllText(package, "not a ZIP file");
        DuetPluginService.IPC.InstallSystemPackage command = Dps.Create<DuetPluginService.IPC.InstallSystemPackage>();
        command.PackageFile = package;
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await command.ExecuteAsync()))!;
        Assert.That(e.Message, Is.EqualTo("Failed to install system package (exit code 3)"));
    }

    [Test]
    public async Task SystemPackageIsUninstalled()
    {
        // UninstallSystemPackage.cs
        await StartDpsAsync(register: false, privileged: true);
        DuetPluginService.IPC.UninstallSystemPackage command = Dps!.Create<DuetPluginService.IPC.UninstallSystemPackage>();
        command.Package = "nano";
        await command.ExecuteAsync();
        Assert.That(FakeTool.Calls(Dps.Settings.UninstallLocalPackageCommand), Is.EqualTo(new[] { "-r nano" }));

        FakeTool.SetExitCode(Dps.Settings.UninstallLocalPackageCommand, 1);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await command.ExecuteAsync()))!;
        Assert.That(e.Message, Is.EqualTo("Failed to uninstall system package (exit code 1)"));
    }
}
