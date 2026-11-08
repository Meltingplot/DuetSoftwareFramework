using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetPluginService.PermissionManagers;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace UnitTests.Plugins;

using File = System.IO.File;
using Settings = DuetPluginService.Settings;

/// <summary>
/// AppArmor profiles that DuetPluginService generates for plugins without SuperUser (DuetPluginService/PermissionManagers/AppArmorPermissionManager.cs).
/// apparmor_parser is a <see cref="FakeTool"/>, so no profile reaches the kernel. Which rules each permission gives is listed in AppArmorPermissionManager.cs:42-144;
/// the expected rules below are taken from there, and PLUGINS.md:79-104 describes what the permissions are for
/// </summary>
[TestFixture]
public class DpsAppArmorProfiles
{
    private const string PluginDirectory = "/opt/dsf/plugins";
    private const string SdDirectory = "/opt/dsf/sd";

    private string _directory = null!;
    private Settings _settings = null!;
    private AppArmorPermissionManager _manager = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dsf-apparmor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_directory, "apparmor.d"));
        string template = Path.Combine(_directory, "template.conf");
        File.WriteAllText(template, "dir={pluginDirectory}\nincludes:\n{includes}rules:\n{rules}end\n");
        _settings = new Settings
        {
            AppArmorParser = FakeTool.Create(_directory, "apparmor_parser"),
            AppArmorTemplate = template,
            AppArmorProfileDirectory = Path.Combine(_directory, "apparmor.d")
        };
        _manager = new AppArmorPermissionManager(Options.Create(_settings));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private string ProfilePath(string id) => Path.Combine(_settings.AppArmorProfileDirectory, "dsf." + id);

    private async Task<string> InstallAsync(SbcPermissions permissions, string pluginDirectory = PluginDirectory, string sdDirectory = SdDirectory)
    {
        Plugin plugin = new() { Id = "Confined", Name = "Confined", SbcPermissions = permissions };
        await _manager.InstallProfileAsync(plugin, pluginDirectory, sdDirectory, CancellationToken.None);
        return await File.ReadAllTextAsync(ProfilePath("Confined"));
    }

    private static string Expected(IEnumerable<string> includes, IEnumerable<string> rules) =>
        $"dir={PluginDirectory}/Confined\nincludes:\n{string.Concat(includes.Select(line => line + Environment.NewLine))}rules:\n{string.Concat(rules.Select(line => line + Environment.NewLine))}end\n";

    /// <summary>
    /// Rules of each permission, in the words of AppArmorPermissionManager.cs:44-143
    /// </summary>
    private static IEnumerable<TestCaseData> PermissionRules()
    {
        // Enforced by DCS, no rule (AppArmorPermissionManager.cs:44-53), and not applicable (140-143)
        foreach (SbcPermissions permission in new[] { SbcPermissions.CodeInterceptionRead, SbcPermissions.CodeInterceptionReadWrite, SbcPermissions.CommandExecution,
            SbcPermissions.ManageUserSessions, SbcPermissions.ObjectModelRead, SbcPermissions.ObjectModelReadWrite, SbcPermissions.RegisterHttpEndpoints,
            SbcPermissions.ServicePlugins, SbcPermissions.SuperUser, SbcPermissions.None })
        {
            yield return new TestCaseData(permission, Array.Empty<string>(), Array.Empty<string>()).SetName($"RulesOf{permission}");
        }

        yield return new TestCaseData(SbcPermissions.ManagePlugins, Array.Empty<string>(), new[] { $"  {PluginDirectory}/ r,", $"  {PluginDirectory}/** rw," }).SetName("RulesOfManagePlugins");
        yield return new TestCaseData(SbcPermissions.FileSystemAccess, Array.Empty<string>(), new[] { "  / rw,", "  /** rw," }).SetName("RulesOfFileSystemAccess");
        yield return new TestCaseData(SbcPermissions.GpioAccess, Array.Empty<string>(), new[] { "  /dev/gpio* rwmlk,", "  /dev/i2c* rwmlk,", "  /dev/spidev* rwmlk," }).SetName("RulesOfGpioAccess");
        yield return new TestCaseData(SbcPermissions.LaunchProcesses, Array.Empty<string>(), new[] { "  /** mix," }).SetName("RulesOfLaunchProcesses");
        yield return new TestCaseData(SbcPermissions.NetworkAccess, new[] { "  #include <abstractions/nameservice>" },
            new[] { "  network,", "  /proc/net/dev r,", "  /proc/net/wireless r," }).SetName("RulesOfNetworkAccess");
        yield return new TestCaseData(SbcPermissions.WebcamAccess, Array.Empty<string>(), new[]
        {
            "  /dev/dma_heap/* rw,", "  /dev/media* rwmlk,", "  /dev/v4l-* rwmlk,", "  /dev/video* rwmlk,", "  /run/udev/data/** rwmlk,",
            "  /usr/bin/libcamerify rm,", "  /usr/libexec/libcamera/* rm,", "  /usr/share/libcamera/** r,", "  /usr/share/libpisp/** r,"
        }).SetName("RulesOfWebcamAccess");

        // Directories of the virtual SD card: read gives the directory and its content, write only the content (w and k, no r)
        foreach ((SbcPermissions read, SbcPermissions write, string directory) in new[]
        {
            (SbcPermissions.ReadFilaments, SbcPermissions.WriteFilaments, "filaments"),
            (SbcPermissions.ReadFirmware, SbcPermissions.WriteFirmware, "firmware"),
            (SbcPermissions.ReadGCodes, SbcPermissions.WriteGCodes, "gcodes"),
            (SbcPermissions.ReadMacros, SbcPermissions.WriteMacros, "macros"),
            (SbcPermissions.ReadMenu, SbcPermissions.WriteMenu, "menu"),
            (SbcPermissions.ReadSystem, SbcPermissions.WriteSystem, "sys"),
            (SbcPermissions.ReadWeb, SbcPermissions.WriteWeb, "www")
        })
        {
            yield return new TestCaseData(read, Array.Empty<string>(), new[] { $"  {SdDirectory}/{directory}/ r,", $"  {SdDirectory}/{directory}/** r," }).SetName($"RulesOf{read}");
            yield return new TestCaseData(write, Array.Empty<string>(), new[] { $"  {SdDirectory}/{directory}/** wk," }).SetName($"RulesOf{write}");
        }
    }

    [TestCaseSource(nameof(PermissionRules))]
    public async Task RulesOfAPermission(SbcPermissions permission, string[] includes, string[] rules)
    {
        Assert.That(await InstallAsync(permission), Is.EqualTo(Expected(includes, rules)));
    }

    [Test]
    public void EveryPermissionIsCovered()
    {
        // A new SbcPermissions value would fall through the switch of AppArmorPermissionManager.cs:42-144 without a rule; the cases above must list it
        HashSet<string> tested = [.. PermissionRules().Select(data => data.TestName!)];
        foreach (SbcPermissions permission in Enum.GetValues<SbcPermissions>())
        {
            Assert.That(tested, Does.Contain($"RulesOf{permission}"), $"No test case for {permission}");
        }
    }

    [Test]
    public async Task CombinedPermissionsAddTheirRulesInTheOrderOfTheEnum()
    {
        // Enum.GetValues returns the values sorted by value (AppArmorPermissionManager.cs:38): ReadGCodes 8192, WriteGCodes 16384, NetworkAccess 33554432
        string profile = await InstallAsync(SbcPermissions.NetworkAccess | SbcPermissions.WriteGCodes | SbcPermissions.ReadGCodes | SbcPermissions.ObjectModelRead);
        Assert.That(profile, Is.EqualTo(Expected(["  #include <abstractions/nameservice>"],
        [
            $"  {SdDirectory}/gcodes/ r,", $"  {SdDirectory}/gcodes/** r,", $"  {SdDirectory}/gcodes/** wk,",
            "  network,", "  /proc/net/dev r,", "  /proc/net/wireless r,"
        ])));
    }

    [Test]
    public async Task DirectoriesWithTrailingSeparators()
    {
        // ManagePlugins trims the separator of the plugin directory (AppArmorPermissionManager.cs:56-57), the SD paths go through Path.Combine
        string profile = await InstallAsync(SbcPermissions.ManagePlugins | SbcPermissions.ReadMacros, PluginDirectory + "/", SdDirectory + "/");
        Assert.That(profile, Does.Contain($"  {PluginDirectory}/ r,\n  {PluginDirectory}/** rw,\n"));
        Assert.That(profile, Does.Contain($"  {SdDirectory}/macros/ r,\n  {SdDirectory}/macros/** r,\n"));
        Assert.That(profile, Does.StartWith($"dir={PluginDirectory}/Confined\n"));
    }

    [Test]
    public async Task ShippedTemplateConfinesThePluginToItsDirectory()
    {
        // pkg/common/duetpluginservice/opt/dsf/conf/apparmor.conf: the profile attaches to the files below the plugin directory (line 4) and gives the
        // plugin its own directory (lines 26-27); all three placeholders are replaced
        _settings.AppArmorTemplate = DpsHost.ShippedFile("pkg/common/duetpluginservice/opt/dsf/conf/apparmor.conf");
        string profile = await InstallAsync(SbcPermissions.NetworkAccess | SbcPermissions.ReadSystem);
        Assert.That(profile, Does.Contain($"\n{PluginDirectory}/Confined/** {{\n"));
        Assert.That(profile, Does.Contain($"  owner {PluginDirectory}/Confined/ r,\n  owner {PluginDirectory}/Confined/** rwmlk,\n"));
        Assert.That(profile, Does.Contain("  #include <abstractions/base>\n  #include <abstractions/bash>\n  #include <abstractions/python>\n  #include <abstractions/user-tmp>\n  #include <abstractions/nameservice>\n"));
        Assert.That(profile, Does.Contain($"  {SdDirectory}/sys/ r,\n  {SdDirectory}/sys/** r,\n"));
        Assert.That(profile, Does.Contain("  signal (send) peer=@{profile_name},"));
        Assert.That(profile, Does.Not.Contain("{pluginDirectory}").And.Not.Contain("{includes}").And.Not.Contain("{rules}"));
        Assert.That(profile.TrimEnd(), Does.EndWith("}"));
    }

    [Test]
    public async Task ProfileIsWrittenAndLoaded()
    {
        await InstallAsync(SbcPermissions.ReadGCodes);
        Assert.That(File.Exists(ProfilePath("Confined")), Is.True);

        // apparmor_parser -r "<profile>" (AppArmorPermissionManager.cs:157) replaces a loaded profile or loads it
        Assert.That(FakeTool.Calls(_settings.AppArmorParser), Is.EqualTo(new[] { $"-r {ProfilePath("Confined")}" }));
    }

    [Test]
    public async Task ProfileIsDeletedWhenTheParserFails()
    {
        // StartPlugin only checks that the profile file exists (StartPlugin.cs:63-72), so a profile the kernel did not load must not stay behind
        // (AppArmorPermissionManager.cs:155-163)
        FakeTool.SetExitCode(_settings.AppArmorParser, 3);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await InstallAsync(SbcPermissions.ReadGCodes)))!;
        Assert.That(e.Message, Is.EqualTo("Failed to load AppArmor profile for plugin Confined (apparmor_parser exited with code 3)"));
        Assert.That(File.Exists(ProfilePath("Confined")), Is.False);
    }

    [Test]
    public async Task ReinstallingReplacesTheProfile()
    {
        await InstallAsync(SbcPermissions.ReadGCodes);
        string profile = await InstallAsync(SbcPermissions.WriteMacros);
        Assert.That(profile, Is.EqualTo(Expected([], [$"  {SdDirectory}/macros/** wk,"])));
        Assert.That(FakeTool.Calls(_settings.AppArmorParser), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task UninstallUnloadsAndDeletesTheProfile()
    {
        await InstallAsync(SbcPermissions.ReadGCodes);
        await _manager.UninstallProfileAsync(new Plugin { Id = "Confined", Name = "Confined" }, CancellationToken.None);

        // apparmor_parser -R "<profile>" unloads it (AppArmorPermissionManager.cs:179), then the file is deleted (183)
        Assert.That(FakeTool.Calls(_settings.AppArmorParser), Is.EqualTo(new[] { $"-r {ProfilePath("Confined")}", $"-R {ProfilePath("Confined")}" }));
        Assert.That(File.Exists(ProfilePath("Confined")), Is.False);
    }

    [Test]
    public async Task UninstallWithoutProfileDoesNothing()
    {
        await _manager.UninstallProfileAsync(new Plugin { Id = "Unconfined", Name = "Unconfined" }, CancellationToken.None);
        Assert.That(FakeTool.Calls(_settings.AppArmorParser), Is.Empty);
    }

    [Test]
    public async Task MissingTemplateIsReported()
    {
        _settings.AppArmorTemplate = Path.Combine(_directory, "missing.conf");
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await InstallAsync(SbcPermissions.ReadGCodes));
        Assert.That(File.Exists(ProfilePath("Confined")), Is.False);
        Assert.That(FakeTool.Calls(_settings.AppArmorParser), Is.Empty);
    }
}
