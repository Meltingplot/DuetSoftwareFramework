using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// IPC permissions of the installed plugins, of DuetWebServer and of other programs on the SBC
/// </summary>
/// <remarks>
/// Before #135 RequiredPermissionsAttribute.Check granted every command, because Enum.HasFlag(SbcPermissions.None) is always true. Now a plugin gets only
/// the commands and connection modes its manifest covers, and DCS refuses the rest with "Insufficient permissions" (Connection.cs:319-326, IPC/Server.cs:235-267).
/// PluginsStartWithTheirManifestPermissions and WebServerPassesPermissionCheckOfSystemPackages guard the fix: they fail if it refuses something that the
/// plugins on the lab printer or DuetWebServer need. They cannot fail without the fix, which refuses nothing. ExternalProgramIsRefusedPackageCommands
/// fails without it
/// </remarks>
[TestFixture]
public class PluginPermissions : LabTest
{
    /// <summary>
    /// Units whose log shows a refused IPC client: DCS logs the refusal (IPC/Server.cs:277, IPC/Processors/Command.cs:136-147), the plugin services log
    /// the output of the plugins (sbcOutputRedirected), and DuetWebServer logs a refused request
    /// </summary>
    private static readonly string[] Units = ["duetcontrolserver", "duetpluginservice", "duetpluginservice-root", "duetwebserver"];

    /// <summary>
    /// How long the plugins must keep their processes after they have started. CHX350 and QualityAssurance restart a crashed process (sbcAutoRestart)
    /// </summary>
    private static readonly TimeSpan StableTime = TimeSpan.FromSeconds(20);

    private static Dictionary<string, (int Pid, bool Started, SbcPermissions Permissions)> ReadPlugins(ObjectModel model) =>
        model.Plugins.Values.Where(plugin => !string.IsNullOrEmpty(plugin.SbcExecutable))
            .ToDictionary(plugin => plugin.Id, plugin => (plugin.Pid, plugin.Started, plugin.SbcPermissions));

    private static HashSet<string> ReadEndpoints(ObjectModel model) =>
        [.. model.SBC?.DSF.HttpEndpoints.Select(endpoint => $"{endpoint.EndpointType} /machine/{endpoint.Namespace}/{endpoint.Path}") ?? []];

    /// <summary>
    /// Restart DCS, which restarts the plugin services and so every plugin (PartOf=duetcontrolserver.service, see ControlServerRestart), and check that the
    /// plugins that ran before start again under this DCS, keep running, register their HTTP endpoints again and get no command refused
    /// </summary>
    [Test, Category("Restart")]
    public async Task PluginsStartWithTheirManifestPermissions()
    {
        Dictionary<string, (int Pid, bool Started, SbcPermissions Permissions)> before = Lab.Read(Session, ReadPlugins);
        HashSet<string> endpointsBefore = Lab.Read(Session, ReadEndpoints);
        string[] running = [.. before.Where(item => item.Value.Pid > 0).Select(item => item.Key)];
        TestContext.Progress.WriteLine($"SBC plugins: {string.Join(", ", before.Select(item => $"{item.Key} (pid {item.Value.Pid}, {item.Value.Permissions})"))}");
        if (running.Length == 0)
        {
            Assert.Ignore("No plugin with an SBC executable runs, so the test shows nothing");
        }

        string cursor = await Lab.GetJournalCursorAsync();
        try
        {
            await Lab.RunOnSbcAsync("sudo -n systemctl restart duetcontrolserver");
        }
        finally
        {
            await Lab.RunOnSbcAsync("sudo -n systemctl start duetpluginservice duetpluginservice-root");
            await Lab.ResetHaltedFirmwareAsync();
        }
        await ReconnectSessionAsync();

        bool AllStarted(ObjectModel model)
        {
            Dictionary<string, (int Pid, bool Started, SbcPermissions Permissions)> plugins = ReadPlugins(model);
            return running.All(id => plugins.TryGetValue(id, out var plugin) && plugin.Pid > 0 && plugin.Started);
        }
        bool started = await Lab.TryWaitForModelAsync(Session, AllStarted, TimeSpan.FromMinutes(2));
        Dictionary<string, (int Pid, bool Started, SbcPermissions Permissions)> afterStart = Lab.Read(Session, ReadPlugins);
        await Task.Delay(StableTime);
        Dictionary<string, (int Pid, bool Started, SbcPermissions Permissions)> afterStable = Lab.Read(Session, ReadPlugins);
        HashSet<string> endpointsAfter = Lab.Read(Session, ReadEndpoints);
        string journal = await Lab.ReadJournalAsync(cursor, Units);

        string[] refusals = [.. journal.Split('\n').Where(line => line.Contains("Insufficient permissions"))];
        TestContext.Progress.WriteLine($"After the restart: {string.Join(", ", afterStable.Select(item => $"{item.Key} (pid {item.Value.Pid}, started {item.Value.Started})"))}; " +
            $"{endpointsAfter.Count} HTTP endpoints, {endpointsBefore.Count} before; {refusals.Length} refusals in the journal");
        foreach (string line in refusals.Take(10))
        {
            TestContext.Progress.WriteLine(line);
        }

        Assert.Multiple(() =>
        {
            Assert.That(started, Is.True, $"Not every plugin started: {string.Join(", ", afterStart.Select(item => $"{item.Key} pid {item.Value.Pid} started {item.Value.Started}"))}");
            foreach (string id in running)
            {
                Assert.That(afterStable.TryGetValue(id, out var plugin) ? plugin.Pid : -1, Is.EqualTo(afterStart.GetValueOrDefault(id).Pid), $"{id} restarted its process");
                Assert.That(afterStable.GetValueOrDefault(id).Started, Is.True, $"{id} is not started");

                // The plugins on the lab printer register their endpoints under their ID: CHX350 (chx350-daemon.py:35), Vigil (vigil-daemon.py:349),
                // QualityAssurance (qa_api.py:39, qa_log.py:14)
                if (before[id].Permissions.HasFlag(SbcPermissions.RegisterHttpEndpoints))
                {
                    Assert.That(endpointsAfter.Any(endpoint => endpoint.Contains($" /machine/{id}/")), Is.True, $"{id} registered no HTTP endpoint");
                }
            }
            Assert.That(endpointsAfter, Is.SupersetOf(endpointsBefore), "HTTP endpoints are missing after the restart");
            Assert.That(refusals, Is.Empty, "DCS refused a command or connection mode");
        });
    }

    /// <summary>
    /// A package name that no package can have: Debian package names consist of lower case letters, digits, '+', '-' and '.' (Debian Policy Manual,
    /// control fields Source and Package), so '_' cannot occur.
    /// It is a single word without a leading '-', because DPS puts it into the arguments of dpkg -r as it is (DuetPluginService/Settings.cs:108-113,
    /// Commands/UninstallSystemPackage.cs)
    /// </summary>
    private const string NoSuchPackage = "labtest_no_such_package";

    /// <summary>
    /// DuetWebServer forwards DELETE /machine/systemPackage as UninstallSystemPackage (MachineEndpoints.cs:754-792), which needs SuperUser or ServicePlugins
    /// since #135. DWS gets ServicePlugins as a DSF service (Connection.cs), so the command must get past the permission check and fail only where DCS or DPS
    /// handle it. DWS replies 500 with the message of the error (EndpointHelper.cs:145-146), or 204 if dpkg succeeds
    /// </summary>
    [Test]
    public async Task WebServerPassesPermissionCheckOfSystemPackages()
    {
        bool rootPluginSupport = Lab.Read(Session, model => model.SBC?.DSF.RootPluginSupport ?? false);
        (HttpStatusCode status, string body) = await UninstallSystemPackageAsync(NoSuchPackage);
        TestContext.Progress.WriteLine($"DELETE /machine/systemPackage {NoSuchPackage} (root plugin support {rootPluginSupport}): {(int)status} '{body}'");

        Assert.That(body, Does.Not.Contain("Insufficient permissions"), "DCS refused UninstallSystemPackage to DuetWebServer");
        if (!rootPluginSupport)
        {
            // DCS refuses it before it reaches the root plugin service (DuetControlServer/Commands/Packages/UninstallSystemPackage.cs:23-26)
            Assert.That((status, body), Is.EqualTo((HttpStatusCode.InternalServerError, "Root plugin support has been disabled")));
        }
        else if (status != HttpStatusCode.NoContent)
        {
            // dpkg failed (DuetPluginService/Commands/UninstallSystemPackage.cs), or the root plugin service is not running (IPC/Processors/PluginService.cs:110-113)
            Assert.That(status, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(body, Does.StartWith("Failed to uninstall system package (exit code").Or.EqualTo("Cannot perform command because the plugin service (root) is not started"));
        }
    }

    /// <summary>
    /// IPC client that runs as root only to drop its rights: it becomes nobody with nobody's group, and dsf as its only supplementary group. That group
    /// lets it reach the socket: /run/dsf is 2770 dsf:dsf (pkg/common/duetcontrolserver/usr/lib/tmpfiles.d/duetcontrolserver.conf:1) and DCS creates
    /// the socket there with UMask=0002 (duetcontrolserver.service:14). DCS takes uid and gid of the peer from SO_PEERCRED (Connection.cs:81), which are
    /// neither root (:84) nor dsf (:162), and the process is no plugin and has no plugin as ancestor (:130-156), so it gets the permissions of an external
    /// program: all but SuperUser and ServicePlugins (:176, :230-239). ResolvePath needs CommandExecution or FileSystemAccess
    /// (DuetAPI/Commands/Files/ResolvePath.cs:8), so it shows that the client is connected as such a program
    /// </summary>
    private const string ExternalProgramScript = """
        dsf = grp.getgrnam("dsf").gr_gid
        nobody = pwd.getpwnam("nobody")
        os.setgroups([dsf])
        os.setgid(nobody.pw_gid)
        os.setuid(nobody.pw_uid)
        ids = {"uid": os.getuid(), "gid": os.getgid(), "groups": os.getgroups(), "dsfGid": dsf}
        ipc = Ipc()
        control = ipc.request({"command": "ResolvePath", "path": "0:/sys"}, 10)
        refused = ipc.request({"command": "UninstallSystemPackage", "package": ARGS["package"]}, 60)
        finish({"ids": ids, "control": control, "refused": refused})
        """;

    /// <summary>
    /// An external program, i.e. an IPC client that is neither root, nor dsf, nor a plugin, is refused UninstallSystemPackage, which needs SuperUser or
    /// ServicePlugins (DuetAPI/Commands/Packages/UninstallSystemPackage.cs:8). DCS checks the permission before it creates the command
    /// (IPC/Connection.cs:670, :320-326) and answers <c>UnauthorizedAccessException</c> "Insufficient permissions"; it logs "IPC#n: Failed to receive
    /// command" as an error (IPC/Processors/Command.cs:150).
    /// Without #135 <c>RequiredPermissionsAttribute.Check</c> looped over every SbcPermissions value including None and returned true at None
    /// (PR #135 diff of RequiredPermissionsAttribute.cs; :25 now), so the command ran: with root plugin support off, as on the CHX, DCS answered
    /// <c>NotSupportedException</c> "Root plugin support has been disabled" (Commands/Packages/UninstallSystemPackage.cs:23-26); with it on, the root
    /// plugin service ran dpkg -r with a name no package can have (see <see cref="NoSuchPackage"/>). Nothing changes on the machine either way.
    /// Upstream still has the loop. The plugin path of the same check cannot be reached from a test, it would have to run inside a plugin's process tree.
    /// Deterministic, one run catches it
    /// </summary>
    [Test]
    public async Task ExternalProgramIsRefusedPackageCommands()
    {
        JsonElement result = await G3Helpers.RunPythonAsync(ExternalProgramScript, new { package = NoSuchPackage }, 120);
        TestContext.Progress.WriteLine($"Script: {result}");

        JsonElement ids = result.GetProperty("ids"), control = result.GetProperty("control"), refused = result.GetProperty("refused");
        Assert.That(ids.GetProperty("uid").GetInt32(), Is.Not.Zero, "The client did not drop root");
        Assert.That(ids.GetProperty("gid").GetInt32(), Is.Not.Zero.And.Not.EqualTo(ids.GetProperty("dsfGid").GetInt32()), "The client runs with group root or dsf");
        Assert.That(G3Helpers.IsSuccess(control), Is.True, $"ResolvePath of an external program: {control}");
        Assert.Multiple(() =>
        {
            Assert.That(G3Helpers.IsSuccess(refused), Is.False, $"UninstallSystemPackage of an external program succeeded: {refused}");
            Assert.That((G3Helpers.GetString(refused, "errorType"), G3Helpers.GetString(refused, "errorMessage")),
                Is.EqualTo(("UnauthorizedAccessException", "Insufficient permissions")), $"UninstallSystemPackage of an external program: {refused}");
        });
    }

    /// <summary>
    /// Send DELETE /machine/systemPackage with its own HTTP session, DuetHttpClient has no call for it
    /// </summary>
    private static async Task<(HttpStatusCode Status, string Body)> UninstallSystemPackageAsync(string package)
    {
        using HttpClient client = new() { BaseAddress = Lab.BaseUri, Timeout = TimeSpan.FromSeconds(60) };
        string sessionKey;
        using (HttpResponseMessage connect = await client.GetAsync($"machine/connect?password={HttpUtility.UrlEncode(DuetAPI.Connection.Defaults.Password)}"))
        {
            connect.EnsureSuccessStatusCode();
            using JsonDocument json = JsonDocument.Parse(await connect.Content.ReadAsStringAsync());
            sessionKey = json.RootElement.GetProperty("sessionKey").GetString()!;
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Delete, "machine/systemPackage") { Content = new StringContent(package, Encoding.UTF8, "text/plain") };
            request.Headers.Add("X-Session-Key", sessionKey);
            using HttpResponseMessage response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            using HttpRequestMessage disconnect = new(HttpMethod.Get, "machine/disconnect");
            disconnect.Headers.Add("X-Session-Key", sessionKey);
            using HttpResponseMessage _ = await client.SendAsync(disconnect);
        }
    }
}
