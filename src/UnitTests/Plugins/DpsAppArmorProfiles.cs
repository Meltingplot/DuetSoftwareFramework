using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetPluginService;
using DuetPluginService.PermissionManagers;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Plugins;

using File = System.IO.File;
using Settings = DuetPluginService.Settings;

/// <summary>
/// AppArmor profiles that DuetPluginService generates for plugins without SuperUser (DuetPluginService/PermissionManagers/AppArmorPermissionManager.cs).
/// apparmor_parser is a <see cref="FakeTool"/>, so no profile reaches the kernel. Which rules each permission gives is listed in AppArmorPermissionManager.cs:51-158;
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
        // The content of the profiles does not depend on the privileges, DpsRootSettings checks the template of the root service
        _manager = new AppArmorPermissionManager(new ServicePrivileges { IsPrivileged = false }, Options.Create(_settings));
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
    /// Rules of each permission, in the words of AppArmorPermissionManager.cs:53-157
    /// </summary>
    private static IEnumerable<TestCaseData> PermissionRules()
    {
        // Enforced by DCS, no rule (AppArmorPermissionManager.cs:53-61), and not applicable (154-157)
        foreach (SbcPermissions permission in new[] { SbcPermissions.CodeInterceptionRead, SbcPermissions.CodeInterceptionReadWrite, SbcPermissions.CommandExecution,
            SbcPermissions.ManageUserSessions, SbcPermissions.ObjectModelRead, SbcPermissions.ObjectModelReadWrite, SbcPermissions.ServicePlugins,
            SbcPermissions.SuperUser, SbcPermissions.None })
        {
            yield return new TestCaseData(permission, Array.Empty<string>(), Array.Empty<string>()).SetName($"RulesOf{permission}");
        }

        // The sockets that DCS reserves for HTTP endpoints (AppArmorPermissionManager.cs:63-67)
        yield return new TestCaseData(SbcPermissions.RegisterHttpEndpoints, Array.Empty<string>(),
            new[] { "  /{,var/}run/dsf/**-{GET,POST,PUT,PATCH,TRACE,DELETE,OPTIONS,WebSocket}.sock rw," }).SetName("RulesOfRegisterHttpEndpoints");
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
        // A new SbcPermissions value would fall through the switch of AppArmorPermissionManager.cs:51-158 without a rule; the cases above must list it
        HashSet<string> tested = [.. PermissionRules().Select(data => data.TestName!)];
        foreach (SbcPermissions permission in Enum.GetValues<SbcPermissions>())
        {
            Assert.That(tested, Does.Contain($"RulesOf{permission}"), $"No test case for {permission}");
        }
    }

    [Test]
    public async Task CombinedPermissionsAddTheirRulesInTheOrderOfTheEnum()
    {
        // Enum.GetValues returns the values sorted by value (AppArmorPermissionManager.cs:47): ReadGCodes 8192, WriteGCodes 16384, NetworkAccess 33554432
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
        // ManagePlugins trims the separator of the plugin directory (AppArmorPermissionManager.cs:70-71), the SD paths go through Path.Combine
        string profile = await InstallAsync(SbcPermissions.ManagePlugins | SbcPermissions.ReadMacros, PluginDirectory + "/", SdDirectory + "/");
        Assert.That(profile, Does.Contain($"  {PluginDirectory}/ r,\n  {PluginDirectory}/** rw,\n"));
        Assert.That(profile, Does.Contain($"  {SdDirectory}/macros/ r,\n  {SdDirectory}/macros/** r,\n"));
        Assert.That(profile, Does.StartWith($"dir={PluginDirectory}/Confined\n"));
    }

    [Test]
    public async Task ShippedTemplateConfinesThePluginToItsDirectory()
    {
        // pkg/common/duetpluginservice/opt/dsf/conf/apparmor.conf: the profile is named dsf.<id>, the name DPS starts the plugin in (StartPlugin.cs:169-175),
        // and gives the plugin its own directory (lines 28-29); all placeholders are replaced. It has no attachment: a profile that attached to the files below
        // the plugin directory never confined bash and the interpreter of a Python plugin
        _settings.AppArmorTemplate = DpsHost.ShippedFile("pkg/common/duetpluginservice/opt/dsf/conf/apparmor.conf");
        string profile = await InstallAsync(SbcPermissions.NetworkAccess | SbcPermissions.ReadSystem);
        Assert.That(profile, Does.Contain("\nprofile dsf.Confined {\n"));
        Assert.That(profile.Split('\n').Count(line => line.TrimEnd().EndsWith('{')), Is.EqualTo(1), "The profile is the only block");
        Assert.That(profile, Does.Contain($"  owner {PluginDirectory}/Confined/ r,\n  owner {PluginDirectory}/Confined/** rwmlk,\n"));
        Assert.That(profile, Does.Contain("  #include <abstractions/base>\n  #include <abstractions/bash>\n  #include <abstractions/python>\n  #include <abstractions/user-tmp>\n  #include <abstractions/nameservice>\n"));
        Assert.That(profile, Does.Contain($"  {SdDirectory}/sys/ r,\n  {SdDirectory}/sys/** r,\n"));
        Assert.That(profile, Does.Contain("  signal (send) peer=@{profile_name},"));
        Assert.That(profile, Does.Not.Contain("{pluginDirectory}").And.Not.Contain("{pluginId}").And.Not.Contain("{includes}").And.Not.Contain("{rules}"));
        Assert.That(profile.TrimEnd(), Does.EndWith("}"));
    }

    /// <summary>
    /// Bash runs a Python plugin in the profile and executes the interpreter of the venv, which the kernel resolves to the interpreter the venv links to
    /// (pipInstall2.py creates the venv without --copies: venv/bin/python -> python3 -> /usr/bin/python3, which is python3.13 on Debian trixie and python3.11 on
    /// bookworm). The rule for it must give ix, so that the interpreter stays in the profile. It said python3.[0-9]+, but + is no glob character of AppArmor
    /// and stays a literal + (apparmor_parser -D rule-exprs: 3\.[0-9]\+), so it matched none of these
    /// </summary>
    [TestCase("/usr/bin/python3")]
    [TestCase("/usr/bin/python3.9")]
    [TestCase("/usr/bin/python3.11")]
    [TestCase("/usr/bin/python3.12")]
    [TestCase("/usr/bin/python3.13")]
    public async Task ShippedTemplateLetsBashExecutePythonInTheProfile(string interpreter)
    {
        _settings.AppArmorTemplate = DpsHost.ShippedFile("pkg/common/duetpluginservice/opt/dsf/conf/apparmor.conf");
        string profile = await InstallAsync(SbcPermissions.None);
        Assert.That(PermissionsOf(profile, interpreter), Does.Contain("i").And.Contain("x"));
        Assert.That(PermissionsOf(profile, "/usr/bin/perl"), Does.Not.Contain("x"), "Other programs are not executed");
    }

    /// <summary>
    /// In /run/dsf a plugin without permissions gets only the socket of DCS, which every plugin connects to (DSF_SOCKET_PATH, StartPlugin.cs:160-164). The rule
    /// /{,var/}run/dsf/** rw gave it every file there, so that it could create, replace and delete the start error file of DCS, the endpoint sockets of other
    /// plugins and anything else in the directory. dcs.sock keeps rw, which includes deleting it: a socket with a path is mediated by file rules (apparmor.d(5),
    /// "Unix socket rules"), w is required to unlink a file ("Access Modes Details"), and file rules have no mode of their own for deleting (ACCESS of FILE RULE)
    /// </summary>
    [TestCase("/run/dsf/dcs.sock", "rw")]
    [TestCase("/var/run/dsf/dcs.sock", "rw")]
    [TestCase("/run/dsf/dcs.err", "")]
    [TestCase("/run/dsf/dcs.sock.new", "")]
    [TestCase("/run/dsf/other.sock", "")]
    [TestCase("/run/dsf/Other/action-GET.sock", "")]
    public async Task ShippedTemplateGivesOnlyTheSocketOfDcsInRunDsf(string path, string permissions)
    {
        _settings.AppArmorTemplate = DpsHost.ShippedFile("pkg/common/duetpluginservice/opt/dsf/conf/apparmor.conf");
        Assert.That(PermissionsOf(await InstallAsync(SbcPermissions.None), path), Is.EqualTo(permissions));
    }

    /// <summary>
    /// A plugin with RegisterHttpEndpoints deletes, binds and makes group-writable the socket that DCS reserved for its endpoint (HttpEndpointUnixSocket.cs:64-76),
    /// which needs w on it. DCS names it &lt;socket directory&gt;/&lt;namespace&gt;/&lt;path&gt;-&lt;type&gt;.sock (DuetControlServer/Commands/HttpEndpoints/AddHttpEndpoint.cs:49-50),
    /// so an endpoint of every type is registered with DCS here and its socket path is moved from the socket directory of the test to /run/dsf
    /// </summary>
    [Test, UnsupportedOSPlatform("windows")]
    public async Task EndpointSocketsAreGivenOnlyWithRegisterHttpEndpoints()
    {
        _settings.AppArmorTemplate = DpsHost.ShippedFile("pkg/common/duetpluginservice/opt/dsf/conf/apparmor.conf");
        string withEndpoints = await InstallAsync(SbcPermissions.RegisterHttpEndpoints), withoutEndpoints = await InstallAsync(SbcPermissions.None);

        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(ipc: true);
        using CommandConnection connection = new();
        await connection.ConnectAsync(host.SocketPath!);
        string socketDirectory = Path.GetDirectoryName(host.SocketPath)!;
        foreach (HttpEndpointType type in Enum.GetValues<HttpEndpointType>())
        {
            // An endpoint path may contain a slash, for which DCS creates a subdirectory (AddHttpEndpoint.cs:51)
            foreach (string endpointPath in new[] { "action", "service/reset" })
            {
                using HttpEndpointUnixSocket endpoint = await connection.AddHttpEndpointAsync(type, "Endpoints", endpointPath);
                Assert.That(endpoint.SocketPath, Does.StartWith(socketDirectory + "/"));
                string path = "/run/dsf" + endpoint.SocketPath[socketDirectory.Length..];
                Assert.That(PermissionsOf(withEndpoints, path), Is.EqualTo("rw"), path);
                Assert.That(PermissionsOf(withoutEndpoints, path), Is.Empty, path);
            }
        }

        // The rule gives no other file in /run/dsf
        foreach (string path in new[] { "/run/dsf/dcs.err", "/run/dsf/other.sock", "/run/dsf/Endpoints/action.sock", "/run/dsf/Endpoints/action-HEAD.sock" })
        {
            Assert.That(PermissionsOf(withEndpoints, path), Is.Empty, path);
        }
        Assert.That(PermissionsOf(withEndpoints, "/run/dsf/dcs.sock"), Is.EqualTo("rw"));
    }

    /// <summary>
    /// Permissions that the file rules of a profile give for a path, as one string of mode characters. The globbing of apparmor.d(5) ("Globbing (AARE)": *, **,
    /// ?, [...], {a,b}) is converted like apparmor_parser converts it (apparmor_parser -D rule-exprs): * and ** that make up a whole path component, directly
    /// after a / and before a / or the end, need one character other than /, every other character is literal. Includes and rules with variables are not looked
    /// at, owner rules count as if the file belonged to the plugin
    /// </summary>
    /// <param name="profile">Content of the profile</param>
    /// <param name="path">Path to check</param>
    /// <returns>Mode characters, sorted</returns>
    private static string PermissionsOf(string profile, string path)
    {
        SortedSet<char> permissions = [];
        foreach (string line in profile.Split('\n'))
        {
            System.Text.RegularExpressions.Match rule = System.Text.RegularExpressions.Regex.Match(line, @"^\s*(owner\s+)?(?<path>/\S*)\s+(?<modes>[a-zA-Z]+),\s*$");
            if (rule.Success && !rule.Groups["path"].Value.Contains("@{") &&
                System.Text.RegularExpressions.Regex.IsMatch(path, "^" + AareToRegex(rule.Groups["path"].Value) + "$"))
            {
                permissions.UnionWith(rule.Groups["modes"].Value);
            }
        }
        return string.Concat(permissions);
    }

    private static string AareToRegex(string aare)
    {
        System.Text.StringBuilder regex = new();
        int braces = 0;
        for (int i = 0; i < aare.Length; i++)
        {
            switch (aare[i])
            {
                case '*' when i + 1 < aare.Length && aare[i + 1] == '*':
                    regex.Append(IsWholeComponent(aare, i, 2) ? "[^/].*" : ".*");
                    i++;
                    break;
                case '*':
                    regex.Append(IsWholeComponent(aare, i, 1) ? "[^/][^/]*" : "[^/]*");
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                case '[':
                    int end = aare.IndexOf(']', i + 1);
                    regex.Append(aare, i, end - i + 1);
                    i = end;
                    break;
                case '{':
                    braces++;
                    regex.Append('(');
                    break;
                case ',' when braces > 0:
                    regex.Append('|');
                    break;
                case '}' when braces > 0:
                    braces--;
                    regex.Append(')');
                    break;
                default:
                    regex.Append(System.Text.RegularExpressions.Regex.Escape(aare[i].ToString()));
                    break;
            }
        }
        return regex.ToString();
    }

    private static bool IsWholeComponent(string aare, int start, int length) =>
        start > 0 && aare[start - 1] == '/' && (start + length == aare.Length || aare[start + length] == '/');

    [Test]
    public async Task ProfileIsWrittenAndLoaded()
    {
        await InstallAsync(SbcPermissions.ReadGCodes);
        Assert.That(File.Exists(ProfilePath("Confined")), Is.True);

        // apparmor_parser -r "<profile>" (AppArmorPermissionManager.cs:171) replaces a loaded profile or loads it
        Assert.That(FakeTool.Calls(_settings.AppArmorParser), Is.EqualTo(new[] { $"-r {ProfilePath("Confined")}" }));
    }

    [Test]
    public async Task ProfileIsDeletedWhenTheParserFails()
    {
        // StartPlugin only checks that the profile file exists (StartPlugin.cs:63-72), so a profile the kernel did not load must not stay behind
        // (AppArmorPermissionManager.cs:169-177)
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

        // apparmor_parser -R "<profile>" unloads it (AppArmorPermissionManager.cs:194), then the file is deleted (198)
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
