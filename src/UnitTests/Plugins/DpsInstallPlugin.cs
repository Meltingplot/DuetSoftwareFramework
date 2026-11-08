using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using NUnit.Framework;

namespace UnitTests.Plugins;

using File = System.IO.File;

/// <summary>
/// Installation of plugin packages by the DuetPluginService instance that runs as dsf user (DuetPluginService/Commands/InstallPlugin.cs:80-264).
/// The command is created and run like CommandService does for a command from DCS, DCS resolves 0:/ for it.
/// The part of the root service (InstallPlugin.cs:54-79: the preinstall command, the installation of SbcPackageDependencies and the AppArmor profile at
/// installation time) is covered by <see cref="DpsRootService"/>, <see cref="DpsAppArmorProfiles"/> covers the profile itself
/// </summary>
[TestFixture]
public class DpsInstallPlugin : DpsTestBase
{
    private string Package(string? manifest, params (string Name, string Content)[] files) => PluginPackage.Create(Path.Combine(Dps!.Directory, "plugin.zip"), manifest, files);

    private static string Manifest(string id, Action<JsonObject>? configure = null)
    {
        JsonObject manifest = new() { ["id"] = id, ["name"] = id, ["version"] = "1.0.0" };
        configure?.Invoke(manifest);
        return manifest.ToJsonString();
    }

    private async Task InstallAsync(string package, bool upgrade = false)
    {
        DuetPluginService.IPC.InstallPlugin command = Dps!.Create<DuetPluginService.IPC.InstallPlugin>();
        command.PluginFile = package;
        command.Upgrade = upgrade;
        await command.ExecuteAsync();
    }

    private Plugin ReadManifest(string id)
    {
        using FileStream stream = File.OpenRead(Path.Combine(PluginDirectory, id + ".json"));
        using JsonDocument json = JsonDocument.Parse(stream);
        Plugin plugin = new();
        plugin.UpdateFromJson(json.RootElement, false);
        return plugin;
    }

    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;

    [Test]
    public async Task FilesAreInstalledByDirectory()
    {
        await StartDpsAsync(register: false);
        string package = Package(Manifest("Demo", manifest =>
        {
            manifest["sbcExecutable"] = "demo";
            manifest["sbcExtraExecutables"] = new JsonArray("tool");
        }),
            ("dsf/demo", "binary"), ("dsf/tool", "tool"), ("dsf/settings.json", "{}"), ("dsf/sub/data.txt", "data"), ("dsf/empty/", ""),
            ("dwc/Demo.js", "js"), ("dwc/css/Demo.css", "css"),
            ("sd/macros/demo.g", "M117 demo"), ("sd/sys/demo.conf", "conf"),
            ("README.md", "readme"));
        await InstallAsync(package);

        // dsf/ and dwc/ go to <PluginDirectory>/<id>, sd/ to 0:/, everything else is skipped (InstallPlugin.cs:132-158)
        string pluginBase = Path.Combine(PluginDirectory, "Demo");
        Assert.That(File.ReadAllText(Path.Combine(pluginBase, "dsf/demo")), Is.EqualTo("binary"));
        Assert.That(File.ReadAllText(Path.Combine(pluginBase, "dsf/sub/data.txt")), Is.EqualTo("data"));
        Assert.That(File.ReadAllText(Path.Combine(pluginBase, "dwc/css/Demo.css")), Is.EqualTo("css"));
        Assert.That(File.ReadAllText(Path.Combine(SdDirectory, "macros/demo.g")), Is.EqualTo("M117 demo"));
        Assert.That(File.ReadAllText(Path.Combine(SdDirectory, "sys/demo.conf")), Is.EqualTo("conf"));
        Assert.That(Directory.EnumerateFileSystemEntries(pluginBase, "README.md", SearchOption.AllDirectories), Is.Empty);
        Assert.That(File.Exists(Path.Combine(SdDirectory, "README.md")), Is.False);
        Assert.That(Dps!.Log.Contains("Skipping installation of file README.md"), Is.True);

        // plugin.json is not extracted, the refreshed manifest is written next to the plugin directory instead (InstallPlugin.cs:123-128, 250-256)
        Assert.That(File.Exists(Path.Combine(pluginBase, "plugin.json")), Is.False);
        Plugin manifest = ReadManifest("Demo");
        Assert.That(manifest.DsfFiles, Is.EqualTo(new[] { "demo", "tool", "settings.json", "sub/data.txt" }));
        Assert.That(manifest.DwcFiles, Is.EqualTo(new[] { "Demo.js", "css/Demo.css" }));
        Assert.That(manifest.SdFiles, Is.EqualTo(new[] { "macros/demo.g", "sys/demo.conf" }));

        // The executable and the extra executables get mode 770, other files keep the default (InstallPlugin.cs:189-198)
        Assert.That(File.GetUnixFileMode(Path.Combine(pluginBase, "dsf/demo")), Is.EqualTo(Executable));
        Assert.That(File.GetUnixFileMode(Path.Combine(pluginBase, "dsf/tool")), Is.EqualTo(Executable));
        Assert.That(File.GetUnixFileMode(Path.Combine(pluginBase, "dsf/settings.json")).HasFlag(UnixFileMode.UserExecute), Is.False);

        // Web files are symlinked from 0:/www to the plugin directory (InstallPlugin.cs:217-248). PLUGINS.md:27 says they go to 0:/www/<PluginName>,
        // the code puts them directly below 0:/www (Path.Combine(sdPath, "www", dwcFile), InstallPlugin.cs:221)
        Assert.That(new FileInfo(Path.Combine(SdDirectory, "www/Demo.js")).LinkTarget, Is.EqualTo(Path.Combine(pluginBase, "dwc/Demo.js")));
        Assert.That(new FileInfo(Path.Combine(SdDirectory, "www/css/Demo.css")).LinkTarget, Is.EqualTo(Path.Combine(pluginBase, "dwc/css/Demo.css")));

        // The plugin is registered, not started
        Plugin? plugin = await Dps.GetPluginAsync("Demo");
        Assert.That(plugin, Is.Not.Null);
        Assert.That(plugin!.Pid, Is.EqualTo(-1));

        // The regular service neither writes an AppArmor profile nor runs a package manager, that is up to the root service (InstallPlugin.cs:54-79)
        Assert.That(File.Exists(Dps.ProfilePath("Demo")), Is.False);
        Assert.That(FakeTool.Calls(Dps.Settings.AppArmorParser), Is.Empty);
        Assert.That(FakeTool.Calls(Dps.Settings.InstallPackageCommand), Is.Empty);

        // 0:/ was resolved through DCS
        Assert.That(Dps.Bridge!.Modes, Is.EqualTo(new[] { ConnectionMode.Command }));
    }

    [Test]
    public async Task ExecutableOfTheArchitectureIsFound()
    {
        await StartDpsAsync(register: false);
        await InstallAsync(Package(Manifest("Arch", manifest => manifest["sbcExecutable"] = "demo"), ($"dsf/{Architecture}/demo", "binary"), ("dsf/other/demo", "other")));
        Assert.That(File.GetUnixFileMode(Path.Combine(PluginDirectory, "Arch/dsf", Architecture, "demo")), Is.EqualTo(Executable));
        Assert.That(File.GetUnixFileMode(Path.Combine(PluginDirectory, "Arch/dsf/other/demo")).HasFlag(UnixFileMode.UserExecute), Is.False);
    }

    [Test]
    public async Task MissingExecutableIsRefused()
    {
        await StartDpsAsync(register: false);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await InstallAsync(Package(Manifest("NoExe", manifest => manifest["sbcExecutable"] = "demo"), ("dsf/other", "x")))))!;
        Assert.That(e.Message, Is.EqualTo("SBC executable demo not found"));
        Assert.That(await Dps!.GetPluginAsync("NoExe"), Is.Null);
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "NoExe.json")), Is.False);
    }

    [TestCase(null, "plugin.json not found in the ZIP file")]
    [TestCase("{\"name\":\"No id\"}", "Plugin manifest must provide an id and a name")]
    [TestCase("{\"id\":\"NoName\"}", "Plugin manifest must provide an id and a name")]
    [TestCase("{\"id\":\"Reserved\",\"name\":\"Reserved\",\"sbcPermissions\":[\"servicePlugins\"]}", "ServicePlugins permission is reserved for internal purposes")]
    [TestCase("{\"id\":\"Reserved\",\"name\":\"Reserved\",\"sbcPermissions\":[\"superUser\",\"servicePlugins\"]}", "ServicePlugins permission is reserved for internal purposes")]
    [TestCase("{\"id\":\"Quote\",\"name\":\"Quote\",\"sbcExecutableArguments\":\"a\\\"b\"}", "SbcExecutableArguments must not contain shell metacharacters")]
    [TestCase("{\"id\":\"Dollar\",\"name\":\"Dollar\",\"sbcExecutableArguments\":\"$HOME\"}", "SbcExecutableArguments must not contain shell metacharacters")]
    [TestCase("{\"id\":\"Backtick\",\"name\":\"Backtick\",\"sbcExecutableArguments\":\"`id`\"}", "SbcExecutableArguments must not contain shell metacharacters")]
    [TestCase("{\"id\":\"Backslash\",\"name\":\"Backslash\",\"sbcExecutableArguments\":\"a\\\\b\"}", "SbcExecutableArguments must not contain shell metacharacters")]
    [TestCase("{\"id\":\"Newline\",\"name\":\"Newline\",\"sbcExecutableArguments\":\"a\\nb\"}", "SbcExecutableArguments must not contain shell metacharacters")]
    [TestCase("{\"id\":\"Return\",\"name\":\"Return\",\"sbcExecutableArguments\":\"a\\rb\"}", "SbcExecutableArguments must not contain shell metacharacters")]
    public async Task ManifestIsChecked(string? manifest, string error)
    {
        // InstallPlugin.ExtractManifest (InstallPlugin.cs:282-310) runs before anything is installed or DCS is asked for 0:/
        await StartDpsAsync(register: false);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await InstallAsync(Package(manifest, ("dsf/file", "x")))))!;
        Assert.That(e.Message, Does.StartWith(error));
        Assert.That(Directory.EnumerateFileSystemEntries(PluginDirectory), Is.Empty);
        Assert.That(Dps!.Bridge!.Modes, Is.Empty);
        Assert.That(Dps.Store.Plugins, Is.Empty);
    }

    [Test]
    public async Task ArgumentsWithoutShellMetacharactersAreAccepted()
    {
        await StartDpsAsync(register: false);
        await InstallAsync(Package(Manifest("Args", manifest => manifest["sbcExecutableArguments"] = "--port 8080 -v 'quoted' name=value")));
        Assert.That(ReadManifest("Args").SbcExecutableArguments, Is.EqualTo("--port 8080 -v 'quoted' name=value"));
    }

    [TestCase("dsf/../../Escaped/file")]
    [TestCase("dwc/../../Escaped/file")]
    [TestCase("dsf/../../Escaped.json")]
    [TestCase("sd/../Escaped/file")]
    [TestCase("sd//tmp/Escaped/file")]
    public async Task EntriesOutsideTheirDirectoryAreRefused(string entry)
    {
        // Zip-slip guard (InstallPlugin.cs:160-167). DCS refuses names with ".." before (DuetControlServer/Commands/Plugins/InstallPlugin.cs:47-56)
        await StartDpsAsync(register: false);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await InstallAsync(Package(Manifest("Slip"), (entry, "escaped")))))!;
        Assert.That(e.Message, Is.EqualTo($"Refusing to install entry {entry}: path escapes plugin directory"));
        Assert.That(Path.Exists(Path.Combine(PluginDirectory, "Escaped")) || File.Exists(Path.Combine(PluginDirectory, "Escaped.json")), Is.False);
        Assert.That(Path.Exists(Path.Combine(Path.GetDirectoryName(SdDirectory.TrimEnd('/'))!, "Escaped")), Is.False);
        Assert.That(Path.Exists("/tmp/Escaped"), Is.False);
        Assert.That(await Dps!.GetPluginAsync("Slip"), Is.Null);
    }

    [Test]
    public async Task EntriesThatStayInsideAreAccepted()
    {
        // ".." that resolves inside the allowed base passes the guard
        await StartDpsAsync(register: false);
        await InstallAsync(Package(Manifest("Inside"), ("dsf/sub/../file", "x"), ("sd/macros/../macros/inside.g", "y")));
        Assert.That(File.ReadAllText(Path.Combine(PluginDirectory, "Inside/dsf/file")), Is.EqualTo("x"));
        Assert.That(File.ReadAllText(Path.Combine(SdDirectory, "macros/inside.g")), Is.EqualTo("y"));
    }

    [Test]
    public async Task ConfigFilesAreNotOverwritten()
    {
        // SbcConfigFiles may be given relative to 0:/sys or to 0:/ (PluginManifest.cs:229-236, InstallPlugin.cs:178)
        await StartDpsAsync(register: false);
        File.WriteAllText(Path.Combine(SdDirectory, "sys/kept.conf"), "user");
        File.WriteAllText(Path.Combine(SdDirectory, "macros/kept.g"), "user");
        File.WriteAllText(Path.Combine(SdDirectory, "sys/plain.conf"), "user");
        await InstallAsync(Package(Manifest("Config", manifest => manifest["sbcConfigFiles"] = new JsonArray("kept.conf", "macros/kept.g", "new.conf")),
            ("sd/sys/kept.conf", "plugin"), ("sd/macros/kept.g", "plugin"), ("sd/sys/new.conf", "plugin"), ("sd/sys/plain.conf", "plugin")));

        Assert.That(File.ReadAllText(Path.Combine(SdDirectory, "sys/kept.conf")), Is.EqualTo("user"));
        Assert.That(File.ReadAllText(Path.Combine(SdDirectory, "macros/kept.g")), Is.EqualTo("user"));
        Assert.That(File.ReadAllText(Path.Combine(SdDirectory, "sys/new.conf")), Is.EqualTo("plugin"), "A config file that does not exist yet is installed");
        Assert.That(File.ReadAllText(Path.Combine(SdDirectory, "sys/plain.conf")), Is.EqualTo("plugin"), "Files that are no config files are overwritten");

        // The kept files still belong to the plugin
        Assert.That(ReadManifest("Config").SdFiles, Is.EquivalentTo(new[] { "sys/kept.conf", "macros/kept.g", "sys/new.conf", "sys/plain.conf" }));
    }

    [Test]
    public async Task PreviousInstallationIsDeletedUnlessItIsAnUpgrade()
    {
        // InstallPlugin.cs:82-96
        await StartDpsAsync(register: false);
        string leftOver = Path.Combine(PluginDirectory, "Again/dsf/left-over");
        Directory.CreateDirectory(Path.GetDirectoryName(leftOver)!);
        File.WriteAllText(leftOver, "old");

        await InstallAsync(Package(Manifest("Again"), ("dsf/new", "new")), upgrade: true);
        Assert.That(File.Exists(leftOver), Is.True, "An upgrade keeps the files that the uninstallation for the upgrade left behind");

        await InstallAsync(Package(Manifest("Again"), ("dsf/new", "new")));
        Assert.That(File.Exists(leftOver), Is.False);
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Again/dsf/new")), Is.True);
        Assert.That(Dps!.Log.Contains("Deleting previous installation directory"), Is.True);
    }

    [Test]
    public async Task PreviousInstallationThatCannotBeDeletedIsReported()
    {
        // InstallPlugin.cs:86-95
        await StartDpsAsync(register: false);
        string locked = Path.Combine(PluginDirectory, "Locked/dsf");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "file"), "old");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await InstallAsync(Package(Manifest("Locked"), ("dsf/new", "new")))))!;
            Assert.That(e.Message, Is.EqualTo($"Failed to remove previous installation directory {Path.Combine(PluginDirectory, "Locked")}"));
            Assert.That(await Dps!.GetPluginAsync("Locked"), Is.Null);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task StaleWebFilesAreReplaced()
    {
        // A left-over file or a dead symlink is deleted before the new symlink is created (InstallPlugin.cs:230-235)
        await StartDpsAsync(register: false);
        File.WriteAllText(Path.Combine(SdDirectory, "www/File.js"), "stale");
        File.CreateSymbolicLink(Path.Combine(SdDirectory, "www/Dead.js"), Path.Combine(PluginDirectory, "Gone/dwc/Dead.js"));
        await InstallAsync(Package(Manifest("Web"), ("dwc/File.js", "file"), ("dwc/Dead.js", "dead")));
        Assert.That(File.ReadAllText(Path.Combine(SdDirectory, "www/File.js")), Is.EqualTo("file"));
        Assert.That(File.ReadAllText(Path.Combine(SdDirectory, "www/Dead.js")), Is.EqualTo("dead"));
        Assert.That(new FileInfo(Path.Combine(SdDirectory, "www/Dead.js")).LinkTarget, Is.EqualTo(Path.Combine(PluginDirectory, "Web/dwc/Dead.js")));
    }

    [Test]
    public async Task PythonPackagesAreInstalledLast()
    {
        // InstallPythonPackages (InstallPlugin.cs:357-388) runs the installer as regular user after the manifest has been written (InstallPlugin.cs:258-263),
        // with the variables of InstallPackageEnvironment added to its environment (DEBIAN_FRONTEND=noninteractive by default, Settings.cs:84-87). A test
        // variable is used here, the test process may have DEBIAN_FRONTEND already, which EnvironmentVariables.Add (InstallPlugin.cs:373-376) refuses
        string installer = "";
        await StartDpsAsync(settings =>
        {
            // The installer checks that the manifest exists when it runs
            installer = Path.Combine(Path.GetDirectoryName(settings.InstallPythonPackageCommand)!, "pip-check");
            File.WriteAllText(installer, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$0.calls\"\n[ -f \"$2\" ] && echo manifest >> \"$0.calls\"\necho \"$DSF_TEST_FRONTEND\" >> \"$0.calls\"\n");
            File.SetUnixFileMode(installer, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            settings.InstallPythonPackageCommand = installer;
            settings.InstallPackageEnvironment = new() { ["DSF_TEST_FRONTEND"] = "noninteractive" };
        }, register: false);
        await InstallAsync(Package(Manifest("Python", manifest => manifest["sbcPythonDependencies"] = new JsonArray("requests>=2.0")), ("dsf/main.py", "print()")));
        Assert.That(FakeTool.Calls(installer), Is.EqualTo(new[]
        {
            $"-m {Path.Combine(PluginDirectory, "Python.json")} -p {Path.Combine(PluginDirectory, "Python")}",
            "manifest",
            "noninteractive"
        }));
        Assert.That(await Dps!.GetPluginAsync("Python"), Is.Not.Null);
    }

    [Test]
    public async Task FailingPythonInstallationIsReported()
    {
        await StartDpsAsync(register: false);
        FakeTool.SetExitCode(Dps!.Settings.InstallPythonPackageCommand, 2);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () =>
            await InstallAsync(Package(Manifest("PyFail", manifest => manifest["sbcPythonDependencies"] = new JsonArray("requests")), ("dsf/main.py", "print()")))))!;
        Assert.That(e.Message, Is.EqualTo("Failed to install packages for plugin PyFail, package manager exited with code 2"));
        Assert.That(await Dps.GetPluginAsync("PyFail"), Is.Null);
    }

    [Test]
    public async Task PackageDependenciesAreLeftToTheRootService()
    {
        // The regular service skips SbcPackageDependencies (InstallPlugin.cs:54-69), DCS forwards InstallPlugin to the root service as well
        // (DuetControlServer/Commands/Plugins/InstallPlugin.cs:148-152)
        await StartDpsAsync(register: false);
        await InstallAsync(Package(Manifest("Packages", manifest => manifest["sbcPackageDependencies"] = new JsonArray("nano")), ("dsf/file", "x")));
        Assert.That(FakeTool.Calls(Dps!.Settings.PreinstallPackageCommand), Is.Empty);
        Assert.That(FakeTool.Calls(Dps.Settings.InstallPackageCommand), Is.Empty);
        Assert.That(await Dps.GetPluginAsync("Packages"), Is.Not.Null);
    }

    [Test]
    public async Task FilesOfRootPluginsAreInstalledByTheRegularService()
    {
        // InstallPlugin.cs:80-264 runs for every plugin in the service that is not privileged, also for plugins with SuperUser, so the files of a root plugin
        // belong to the dsf user. The root service only installs packages and profiles (InstallPlugin.cs:54-79) and starts the plugin from this directory
        // (StartPlugin.cs:84-92)
        await StartDpsAsync(register: false);
        await InstallAsync(Package(Manifest("Root", manifest =>
        {
            manifest["sbcExecutable"] = "root";
            manifest["sbcPermissions"] = new JsonArray("superUser");
        }), ("dsf/root", "binary")));
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Root/dsf/root")), Is.True);
        Assert.That(ReadManifest("Root").SbcPermissions, Is.EqualTo(SbcPermissions.SuperUser));
        Assert.That(File.Exists(Dps!.ProfilePath("Root")), Is.False);
    }

    /// <summary>
    /// A plugin can write below its own directory (owner {pluginDirectory}/** rwmlk, apparmor.conf:27), so it can replace its dsf directory with a
    /// symlink. When the same plugin is upgraded, DCS uninstalls it with ForUpgrade (DuetControlServer/Commands/Plugins/InstallPlugin.cs:139-146), which
    /// deletes only the files listed in the manifest (UninstallPlugin.cs:132-154) and keeps the symlink, and InstallPlugin keeps the plugin directory
    /// (Upgrade, InstallPlugin.cs:84). The zip-slip guard compares Path.GetFullPath, which does not resolve symlinks (InstallPlugin.cs:162-167), and the
    /// file is created through the symlink (InstallPlugin.cs:185) by DPS, which runs without AppArmor profile. So the package of the upgrade writes
    /// wherever the dsf user may write, e.g. 0:/sys/config.g, which DCS refuses as sd/ entry (InstallPlugin.cs:49-52 in DCS), or other plugins
    /// </summary>
    [Test]
    [Explicit("Bug: an upgrade writes through a symlink that the plugin left in its directory, outside the plugin directory")]
    public async Task UpgradeDoesNotWriteThroughSymlinks()
    {
        await StartDpsAsync(register: false);
        string outside = OutsidePath("outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(PluginDirectory, "Linked"));
        Directory.CreateSymbolicLink(Path.Combine(PluginDirectory, "Linked/dsf"), outside);

        try
        {
            await InstallAsync(Package(Manifest("Linked"), ("dsf/config.g", "M999")), upgrade: true);
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            // Refusing the package is fine too
        }
        Assert.That(File.Exists(Path.Combine(outside, "config.g")), Is.False, "The package was written outside the plugin directory");
    }

    /// <summary>
    /// The id is optional in PluginManifest (PluginManifest.cs:25-26). With an empty id the plugin base would be the plugin directory itself
    /// (InstallPlugin.cs:83), which would be deleted with all installed plugins (InstallPlugin.cs:84-96). DPS refuses such a manifest with the check that
    /// DCS makes before it forwards InstallPlugin (DuetSharedLibrary/PluginManifestCheck.cs, DuetControlServer/Commands/Plugins/InstallPlugin.cs)
    /// </summary>
    [Test]
    public async Task ManifestWithoutIdIsRefused()
    {
        await StartDpsAsync(register: false);
        File.WriteAllText(Path.Combine(PluginDirectory, "Other.json"), "{\"id\":\"Other\",\"name\":\"Other\"}");
        Directory.CreateDirectory(Path.Combine(PluginDirectory, "Other/dsf"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await InstallAsync(Package("{\"name\":\"No id\"}", ("dsf/file", "x"))));
        Assert.That(File.Exists(Path.Combine(PluginDirectory, "Other.json")), Is.True, "Another plugin was deleted");
    }
}
