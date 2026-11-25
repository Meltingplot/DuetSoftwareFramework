using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Owners and modes of what the installed DSF packages put on the SBC. These fixes reach the printer only with an image: the side-load replaces
/// DuetAPI.dll, DuetControlServer.dll and DuetSharedLibrary.dll only (run-lab-tests.py:36). Each test reads the installed state, changes nothing,
/// and is skipped when the installed package predates its fix
/// </summary>
[TestFixture]
public class Packaging : LabTest
{
    /// <summary>
    /// Packages that pkg/deb/build.sh builds with the DSF version (build.sh:19-57, :79-99). duetsd and duetwebcontrol carry versions of their own
    /// (build.sh:61-73), which do not tell whether their package was built with #30, and their trees go to dsf anyway (see <see cref="DsfTrees"/>)
    /// </summary>
    private static readonly string[] DsfPackages = ["duetcontrolserver", "duetwebserver", "duetpluginservice", "duettools", "duetruntime",
        "duetpimanagementplugin", "duetsoftwareframework"];

    /// <summary>
    /// Trees that the maintainer scripts give to dsf as a whole (pkg/deb/duetsd/DEBIAN/postinst:3, pkg/deb/duetwebcontrol/DEBIAN/postinst:3), and whose
    /// files DWC, plugins and the machine configuration change at run time. Owners there tell nothing about the package build
    /// </summary>
    private static readonly string[] DsfTrees = ["/opt/dsf/sd", "/opt/dsf/dwc"];

    /// <summary>
    /// First fork release with #30: v3.7.0-rc.2+mp.9 is the first tag that contains its merge commit 4cc175a6c, as package version with '~' for '-'
    /// (pkg/deb/build.sh:25). Upstream packages never have it, their versions have no "+mp."
    /// </summary>
    private const string FirstReleaseWithRootOwner = "3.7.0~rc.2+mp.9";

    /// <summary>
    /// List every entry of the installed DSF packages built with #30 (dpkg -L) and report those not owned by root or dsf. Bind mounts are skipped: a
    /// side-loaded DLL shows the owner of its source. So are entries that no longer exist
    /// </summary>
    private const string OwnerScript = """
        installed, built = {}, []
        for package in ARGS["packages"]:
            r = subprocess.run(["dpkg-query", "-W", "-f", "${db:Status-Status} ${Version}", package], capture_output=True, text=True)
            if r.returncode == 0 and r.stdout.startswith("installed "):
                version = r.stdout.split(" ", 1)[1]
                installed[package] = version
                if "+mp." in version and subprocess.run(["dpkg", "--compare-versions", version, "ge", ARGS["firstFixed"]]).returncode == 0:
                    built.append(package)
        mounts = set(subprocess.run(["findmnt", "-rn", "-o", "TARGET"], capture_output=True, text=True).stdout.split("\n"))
        dsf = pwd.getpwnam("dsf").pw_uid
        entries = set()
        for package in built:
            listing = subprocess.run(["dpkg", "-L", package], capture_output=True, text=True).stdout
            entries.update(line for line in listing.split("\n") if line.startswith("/"))
        checked, foreign = 0, []
        for entry in sorted(entries):
            if entry in mounts or any(entry == tree or entry.startswith(tree + "/") for tree in ARGS["trees"]):
                continue
            try:
                st = os.lstat(entry)
            except FileNotFoundError:
                continue
            checked += 1
            if st.st_uid not in (0, dsf):
                try:
                    owner = pwd.getpwuid(st.st_uid).pw_name
                except KeyError:
                    owner = str(st.st_uid)
                foreign.append("%s (uid %d) %s" % (owner, st.st_uid, entry))
        finish({"installed": installed, "built": built, "checked": checked, "foreignCount": len(foreign), "foreign": foreign[:40]})
        """;

    /// <summary>
    /// Every entry that the DSF packages install outside the dsf trees belongs to root or dsf.
    /// Without #30 dpkg-deb stored the owner of the staged files, the GitHub runner user 1001:1001, and dpkg kept it on install, also for directories it
    /// took over. On the CHX image UID 1001 is the admin account mpadmin, which owned 435 entries of the read-only root such as /opt/dsf, /opt/dsf/bin,
    /// the systemd units and /usr/bin/launch-dwc (PR #30). The fix is --root-owner-group on every dpkg-deb call (pkg/deb/build.sh:27-99,
    /// Makefile:398); the maintainer scripts give dsf only what it needs (pkg/deb/*/DEBIAN/postinst). Upstream still builds without the flag.
    /// Only packages of a fork release with #30 are checked, each by its own version; the test is skipped when duetcontrolserver predates it.
    /// Read-only and deterministic. In DSF 3.7.0+mp.15
    /// </summary>
    [Test]
    public async Task DsfFilesAreNotOwnedByTheAdminUser()
    {
        JsonElement result = await G3Helpers.RunPythonAsync(OwnerScript, new { packages = DsfPackages, trees = DsfTrees, firstFixed = FirstReleaseWithRootOwner }, 300);
        Dictionary<string, string> installed = result.GetProperty("installed").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!);
        string[] built = [.. result.GetProperty("built").EnumerateArray().Select(item => item.GetString()!)];
        TestContext.Progress.WriteLine($"Installed: {string.Join(", ", installed.Select(item => $"{item.Key} {item.Value}"))}; checked {string.Join(", ", built)}: " +
            $"{result.GetProperty("checked").GetInt32()} entries, {result.GetProperty("foreignCount").GetInt32()} not owned by root or dsf");
        if (!installed.TryGetValue("duetcontrolserver", out string? version))
        {
            Assert.Ignore("duetcontrolserver is not installed as a package");
        }
        if (!built.Contains("duetcontrolserver"))
        {
            Assert.Ignore($"duetcontrolserver {version} predates #30, which is in fork releases from {FirstReleaseWithRootOwner} on");
        }

        string[] foreign = [.. result.GetProperty("foreign").EnumerateArray().Select(item => item.GetString()!)];
        Assert.That(result.GetProperty("checked").GetInt32(), Is.Positive, "No entry of the packages was checked");
        Assert.That(foreign, Is.Empty, $"{result.GetProperty("foreignCount").GetInt32()} entries of the DSF packages belong to neither root nor dsf, the first ones");
    }

    /// <summary>
    /// Find out whether the installed DPS has #234 and read the state the root plugin service runs with. The fix added the text "Refusing to run commands
    /// from the settings" (DuetPluginService/ServicePrivileges.cs:37), which no earlier DPS has, and .NET keeps string literals as UTF-16 in the binary
    /// </summary>
    private const string RootServiceScript = """
        import stat

        def run(*command):
            return subprocess.run(command, capture_output=True, text=True).stdout.strip()

        def describe(path):
            try:
                st = os.lstat(path)
            except FileNotFoundError:
                return None
            kind = "symlink" if stat.S_ISLNK(st.st_mode) else "directory" if stat.S_ISDIR(st.st_mode) else "file" if stat.S_ISREG(st.st_mode) else "other"
            try:
                owner = pwd.getpwuid(st.st_uid).pw_name
            except KeyError:
                owner = str(st.st_uid)
            try:
                group = grp.getgrgid(st.st_gid).gr_name
            except KeyError:
                group = str(st.st_gid)
            return "%s %s:%s %o" % (kind, owner, group, stat.S_IMODE(st.st_mode))

        version = run("dpkg-query", "-W", "-f", "${Version}", "duetpluginservice")
        newer = bool(version) and subprocess.run(["dpkg", "--compare-versions", version, "gt", ARGS["release"]]).returncode == 0
        marker = ARGS["marker"].encode("utf-16-le")
        markerFound = False
        for binary in ("/opt/dsf/bin/DuetPluginService.dll", "/opt/dsf/bin/DuetPluginService"):
            try:
                with open(binary, "rb") as f:
                    markerFound = markerFound or marker in f.read()
            except FileNotFoundError:
                pass
        pid = int(run("systemctl", "show", "-p", "MainPID", "--value", "duetpluginservice-root") or 0)
        refusals = None
        if pid > 0:
            journal = run("journalctl", "-u", "duetpluginservice-root", "_PID=%d" % pid, "-o", "cat", "--no-pager")
            refusals = [line for line in journal.split("\n") if ARGS["marker"] in line]
        finish({
            "version": version or None, "newer": newer, "markerFound": markerFound, "pid": pid, "refusals": refusals,
            "workingDirectory": run("systemctl", "show", "-p", "WorkingDirectory", "--value", "duetpluginservice-root"),
            "files": {path: describe(path) for path in ARGS["paths"]}})
        """;

    /// <summary>
    /// Message the root DPS logs at its start when it refuses its settings (DuetPluginService/ServicePrivileges.cs:37, PluginService.cs:38-41)
    /// </summary>
    private const string RefusalMessage = "Refusing to run commands from the settings";

    /// <summary>
    /// What #234 makes of the files the root plugin service reads or runs: /opt/dsf/conf root:dsf 1775 (pkg/deb/duetcontrolserver/DEBIAN/postinst:23-24),
    /// plugins.json and apparmor.conf root:root 0644, pipInstall2.py root:root 0755 (pkg/deb/duetpluginservice/DEBIAN/postinst:7-15)
    /// </summary>
    private static readonly Dictionary<string, string> RootServiceFiles = new()
    {
        ["/opt/dsf/conf"] = "directory root:dsf 1775",
        ["/opt/dsf/conf/plugins.json"] = "file root:root 644",
        ["/opt/dsf/conf/apparmor.conf"] = "file root:root 644",
        ["/opt/dsf/bin/pipInstall2.py"] = "file root:root 755"
    };

    /// <summary>
    /// The root plugin service runs commands only from settings that root alone may change, in a directory dsf cannot write to.
    /// Without #234 the packages gave /opt/dsf/conf and plugins.json to dsf, so code running as dsf could choose the commands that the root DPS runs as
    /// root (FS4), and the root DPS worked in /opt/dsf/plugins, where dsf may create files that it reads as configuration or imports as Python modules (LX2).
    /// The fix: the owners above, <c>WorkingDirectory=/opt/dsf/bin</c> (duetpluginservice-root.service:9), and a root DPS that refuses settings others may
    /// change and logs <see cref="RefusalMessage"/> at its start.
    /// The test tells whether the installed DPS has #234 from its package version (newer than <see cref="G3Helpers.LastReleaseBeforeNewFixes"/>) or from
    /// that message in its binary, and is skipped otherwise. Per PR #234 the CHX image's mp-dsf-configure gives /opt/dsf/conf and its files to dsf
    /// (2770/0660) after the packages are configured, so until the image follows the PR's recommendation this test fails on the owners and on the
    /// refusal in the journal. Read-only and deterministic. Not in DSF 3.7.0+mp.15
    /// </summary>
    [Test]
    public async Task RootPluginServiceSettingsBelongToRoot()
    {
        JsonElement result = await G3Helpers.RunPythonAsync(RootServiceScript,
            new { release = G3Helpers.LastReleaseBeforeNewFixes, marker = RefusalMessage, paths = RootServiceFiles.Keys.ToArray() }, 120);
        TestContext.Progress.WriteLine($"Root plugin service: {result}");

        string? version = G3Helpers.GetString(result, "version");
        if (version is null)
        {
            Assert.Ignore("duetpluginservice is not installed as a package");
        }
        if (!result.GetProperty("newer").GetBoolean() && !result.GetProperty("markerFound").GetBoolean())
        {
            Assert.Ignore($"duetpluginservice {version} predates #234: it is not newer than {G3Helpers.LastReleaseBeforeNewFixes} and its DuetPluginService " +
                $"has no '{RefusalMessage}'");
        }

        JsonElement files = result.GetProperty("files"), refusals = result.GetProperty("refusals");
        Assert.Multiple(() =>
        {
            foreach ((string path, string expected) in RootServiceFiles)
            {
                string? actual = G3Helpers.GetString(files, path);
                if (actual is null && path.EndsWith("apparmor.conf"))
                {
                    // The postinst leaves a missing template missing (duetpluginservice postinst:8-13), DPS needs it only with AppArmor
                    TestContext.Progress.WriteLine($"{path} does not exist");
                    continue;
                }
                Assert.That(actual, Is.EqualTo(expected), $"{path}. The CHX image sets /opt/dsf/conf to dsf 2770/0660 after the packages (PR #234: mp-dsf-configure)");
            }
            Assert.That(G3Helpers.GetString(result, "workingDirectory"), Is.EqualTo("/opt/dsf/bin"), "WorkingDirectory of duetpluginservice-root");
            if (refusals.ValueKind == JsonValueKind.Array)
            {
                Assert.That(refusals.EnumerateArray().Select(line => line.GetString()), Is.Empty, "The running root plugin service refuses its settings");
            }
            else
            {
                TestContext.Progress.WriteLine("duetpluginservice-root is not running, its journal is not checked");
            }
        });
    }
}
