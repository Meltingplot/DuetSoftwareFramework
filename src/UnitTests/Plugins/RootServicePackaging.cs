using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace UnitTests.Plugins;

using File = System.IO.File;

/// <summary>
/// What the packages leave below /opt/dsf for the root plugin service (duetpluginservice-root.service has no User=). As root it runs the commands configured in
/// /opt/dsf/conf/plugins.json (InstallPlugin.cs:58-70, :364-433, InstallSystemPackage.cs:104-150, UninstallSystemPackage.cs:32-33,
/// AppArmorPermissionManager.cs:166, :189), writes and loads AppArmor profiles made from /opt/dsf/conf/apparmor.conf (AppArmorPermissionManager.cs:42-166),
/// and runs on the files in /opt/dsf/bin. The dsf user, and everything that runs as dsf, must be able to change none of it, else it makes root run its code.
/// The Debian maintainer scripts and the RPM scriptlets run with /bin/sh against a tree in a temporary directory: /opt/dsf in the scripts is replaced by
/// that tree, chmod runs for real inside it, and chown and the other tools are stubs. The test runs as regular user, so the chown stub records its calls and
/// the test applies them to a model of the owners. chown and chmod record the modes of the directories in the tree at each call as well, so that the test can
/// tell whether dsf could have replaced an entry by a symlink when a call followed it
/// </summary>
[TestFixture]
public class RootServicePackaging
{
    private const string Root = "root", Dsf = "dsf";

    private const UnixFileMode Mode644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode Mode664 = Mode644 | UnixFileMode.GroupWrite;
    private const UnixFileMode Mode755 = Mode644 | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
    private const UnixFileMode Mode775 = Mode755 | UnixFileMode.GroupWrite;
    private const UnixFileMode Mode1775 = Mode775 | UnixFileMode.StickyBit;

    private string _directory = null!;
    private readonly Dictionary<string, (string User, string Group)> _owners = [];
    private readonly List<string> _callsThroughReplaceableEntries = [];

    private string TreeRoot => Path.Combine(_directory, "tree");
    private string Stubs => Path.Combine(_directory, "stubs");
    private string CallLog => Path.Combine(_directory, "calls");

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dsf-packaging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TreeRoot);
        CreateStubs();
        _owners.Clear();
        _callsThroughReplaceableEntries.Clear();
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    #region Tree and scripts

    private string Physical(string path) => TreeRoot + path;

    private void Add(string path, string user, string group, UnixFileMode mode, bool isDirectory = false)
    {
        string physical = Physical(path);
        if (isDirectory)
        {
            Directory.CreateDirectory(physical);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
            File.WriteAllText(physical, string.Empty);
        }
        File.SetUnixFileMode(physical, mode);
        _owners[path] = (user, group);
    }

    /// <summary>
    /// The files of the DSF packages after dpkg unpacked them on a new system: dpkg-deb --root-owner-group (pkg/deb/build.sh:27-57) makes root the owner of
    /// every file, and the modes are the ones of the build tree, here those of the git checkout (pipInstall2.py 644, apparmor.conf 755)
    /// </summary>
    private void UnpackNewInstallation()
    {
        foreach (string directory in new[] { "/opt/dsf", "/opt/dsf/bin", "/opt/dsf/conf", "/opt/dsf/plugins", "/opt/dsf/plugins/DuetPiManagementPlugin",
                                             "/opt/dsf/plugins/DuetPiManagementPlugin/dsf" })
        {
            Add(directory, Root, Root, Mode755, true);
        }

        // duetcontrolserver, duetwebserver, duetpluginservice, duetruntime, duetpimanagementplugin
        Add("/opt/dsf/bin/DuetControlServer", Root, Root, Mode755);
        Add("/opt/dsf/bin/DuetControlServer.dll", Root, Root, Mode644);
        Add("/opt/dsf/conf/config.json", Root, Root, Mode644);
        Add("/opt/dsf/bin/DuetWebServer", Root, Root, Mode755);
        Add("/opt/dsf/bin/DuetWebServer.dll", Root, Root, Mode644);
        Add("/opt/dsf/bin/appsettings.json", Root, Root, Mode644);
        Add("/opt/dsf/bin/appsettings.Development.json", Root, Root, Mode644);
        Add("/opt/dsf/conf/http.json", Root, Root, Mode644);
        Add("/opt/dsf/bin/DuetPluginService", Root, Root, Mode755);
        Add("/opt/dsf/bin/DuetPluginService.dll", Root, Root, Mode644);
        Add("/opt/dsf/bin/pipInstall2.py", Root, Root, Mode644);
        Add("/opt/dsf/conf/plugins.json", Root, Root, Mode644);
        Add("/opt/dsf/conf/apparmor.conf", Root, Root, Mode755);
        Add("/opt/dsf/bin/DuetAPI.dll", Root, Root, Mode644);
        Add("/opt/dsf/bin/libcoreclr.so", Root, Root, Mode644);
        Add("/opt/dsf/plugins/DuetPiManagementPlugin.json", Root, Root, Mode644);
        Add("/opt/dsf/plugins/DuetPiManagementPlugin/dsf/DuetPiManagementPlugin", Root, Root, Mode755);
    }

    /// <summary>
    /// The files after dpkg unpacked a new version over a system that the scripts of upstream set up: its duetcontrolserver postinst gave /opt/dsf/conf with
    /// all files to dsf and made them group-writable (chown -R dsf:dsf /opt/dsf/conf, chmod -R g+wX /opt/dsf/conf), its duetpluginservice postinst gave
    /// plugins* there to dsf again. dpkg replaces the files that are no conffiles, so apparmor.conf belongs to root again, but it keeps a changed conffile with
    /// owner and mode (DSF upgrades with --force-confold), as plugins.json with a changed setting
    /// </summary>
    private void UnpackUpgrade()
    {
        UnpackNewInstallation();
        Add("/opt/dsf/conf", Dsf, Dsf, Mode775, true);
        Add("/opt/dsf/conf/config.json", Dsf, Dsf, Mode664);
        Add("/opt/dsf/conf/plugins.txt", Dsf, Dsf, Mode664);
        Add("/opt/dsf/conf/http.json", Dsf, Dsf, Mode664);
        Add("/opt/dsf/conf/plugins.json", Dsf, Dsf, Mode664);
        Add("/opt/dsf/plugins", Dsf, Dsf, Mode755, true);
    }

    private void WriteStub(string name, string body)
    {
        string path = Path.Combine(Stubs, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void CreateStubs()
    {
        Directory.CreateDirectory(Stubs);
        string chmod = File.Exists("/usr/bin/chmod") ? "/usr/bin/chmod" : "/bin/chmod";
        string find = File.Exists("/usr/bin/find") ? "/usr/bin/find" : "/bin/find";

        // One line with the modes of each directory in the tree, then one line with the command and its arguments
        string logCall(string command) => $"{find} \"$TREE\" -type d -printf 'mode\\t%p\\t%m\\n' >> \"$CALL_LOG\"\n" +
            $"printf '{command}' >> \"$CALL_LOG\"; for arg in \"$@\"; do printf '\\t%s' \"$arg\" >> \"$CALL_LOG\"; done; printf '\\n' >> \"$CALL_LOG\"\n";
        WriteStub("chown", logCall("chown"));
        WriteStub("chmod", "for arg in \"$@\"; do case \"$arg\" in /*) case \"$arg\" in \"$TREE\"/*) ;; *) echo \"chmod outside the tree: $arg\" >&2; exit 1 ;; esac ;; esac; done\n" +
            logCall("chmod") + $"exec {chmod} \"$@\"\n");
        WriteStub("systemctl", "exit 1\n");                 // no unit is enabled or active
        WriteStub("getent", "exit 2\n");                    // no such group
        foreach (string tool in new[] { "setcap", "systemd-sysusers", "systemd-tmpfiles", "udevadm", "usermod", "rm", "apparmor_parser" })
        {
            WriteStub(tool, "exit 0\n");
        }
    }

    /// <summary>
    /// Run a maintainer script or scriptlet as root would, against the tree
    /// </summary>
    /// <param name="script">Text of the script</param>
    /// <param name="arguments">Arguments of the script</param>
    private void RunScript(string script, params string[] arguments)
    {
        string path = Path.Combine(_directory, "script");
        File.WriteAllText(path, script.Replace("/opt/dsf", Physical("/opt/dsf")));

        ProcessStartInfo startInfo = new("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        startInfo.ArgumentList.Add(path);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["PATH"] = Stubs;
        startInfo.Environment["TREE"] = TreeRoot;
        startInfo.Environment["CALL_LOG"] = CallLog;
        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        // Every program a script calls is a stub, the shell reports any other one as not found
        Assert.That(output, Does.Not.Contain("not found").And.Not.Contain("outside the tree"), output);
        ReplayCalls();
    }

    private void RunDebianScript(string package, string script, params string[] arguments) =>
        RunScript(File.ReadAllText(DpsHost.ShippedFile($"pkg/deb/{package}/DEBIAN/{script}")), arguments);

    /// <summary>
    /// dpkg configures a package after the packages it depends on: duetwebserver and duetpluginservice depend on duetcontrolserver, duetpimanagementplugin on
    /// duetpluginservice (pkg/deb/*/DEBIAN/control)
    /// </summary>
    private void ConfigurePackages(params string[] arguments)
    {
        foreach (string package in new[] { "duetcontrolserver", "duetwebserver", "duetpluginservice", "duetpimanagementplugin" })
        {
            RunDebianScript(package, "postinst", ["configure", .. arguments]);
        }
    }

    /// <summary>
    /// Replay the calls of chown and chmod in their order: note each call that follows a symlink (chmod and chown without -h, except below the arguments of
    /// -R) on an entry that dsf could have replaced at that moment, and apply chown to the model of the owners
    /// </summary>
    private void ReplayCalls()
    {
        if (!File.Exists(CallLog))
        {
            return;
        }

        Dictionary<string, UnixFileMode> modes = [];
        foreach (string line in File.ReadAllLines(CallLog))
        {
            List<string> args = [.. line.Split('\t', StringSplitOptions.RemoveEmptyEntries)];
            string command = args[0];
            args.RemoveAt(0);
            if (command == "mode")
            {
                modes[args[0][TreeRoot.Length..]] = (UnixFileMode)Convert.ToInt32(args[1], 8);
                continue;
            }

            string call = $"{command} {string.Join(' ', args)}".Replace(TreeRoot, string.Empty);
            bool recursive = false, noDereference = false;
            while (args[0].StartsWith('-'))
            {
                recursive |= args[0].Contains('R');
                noDereference |= args[0].Contains('h');
                args.RemoveAt(0);
            }

            // chown -R does not follow symlinks (-P is its default), chmod follows the ones it is given
            if (command == "chmod" || !(recursive || noDereference))
            {
                foreach (string physical in args.Skip(1).Where(arg => arg.StartsWith(TreeRoot + "/")))
                {
                    if (DsfMayReplace(physical[TreeRoot.Length..], path => modes.TryGetValue(path, out UnixFileMode mode) ? mode : Mode(path)))
                    {
                        _callsThroughReplaceableEntries.Add(call);
                    }
                }
            }
            if (command != "chown")
            {
                continue;
            }

            string[] owner = args[0].Split(':', '.');
            foreach (string physical in args.Skip(1))
            {
                Assert.That(physical, Does.StartWith(TreeRoot + "/"), "chown outside the tree");
                if (!File.Exists(physical) && !Directory.Exists(physical))
                {
                    continue;
                }

                IEnumerable<string> entries = (recursive && Directory.Exists(physical)) ? Directory.EnumerateFileSystemEntries(physical, "*", SearchOption.AllDirectories).Prepend(physical) : [physical];
                foreach (string entry in entries)
                {
                    string path = entry[TreeRoot.Length..];
                    _owners[path] = (owner[0], (owner.Length > 1 && owner[1] != string.Empty) ? owner[1] : Owner(path).Group);
                }
            }
        }
        File.Delete(CallLog);
    }

    #endregion

    #region Model

    /// <summary>
    /// Owner of an entry. The scripts run as root, so an entry that a script created belongs to root
    /// </summary>
    private (string User, string Group) Owner(string path) => _owners.TryGetValue(path, out (string User, string Group) owner) ? owner : (Root, Root);

    private UnixFileMode Mode(string path) => File.GetUnixFileMode(Physical(path));

    /// <summary>
    /// Whether dsf may write the file, or create and remove entries in the directory. The owner may change the mode, so dsf may write whatever it owns
    /// </summary>
    /// <param name="path">Path of the entry</param>
    /// <param name="modeOf">Modes of the entries or null for the ones in the tree</param>
    private bool DsfMayWrite(string path, Func<string, UnixFileMode>? modeOf = null)
    {
        (string user, string group) = Owner(path);
        UnixFileMode mode = (modeOf ?? Mode)(path);
        return user == Dsf || (group == Dsf && mode.HasFlag(UnixFileMode.GroupWrite)) || mode.HasFlag(UnixFileMode.OtherWrite);
    }

    /// <summary>
    /// Whether dsf may rename or remove the entry, or one of the directories above it, and put something else in its place. In a sticky directory only the
    /// owner of the entry and the owner of the directory may do that
    /// </summary>
    /// <param name="path">Path of the entry</param>
    /// <param name="modeOf">Modes of the entries or null for the ones in the tree</param>
    private bool DsfMayReplace(string path, Func<string, UnixFileMode>? modeOf = null)
    {
        if (path == "/opt/dsf")
        {
            // Created by dpkg and changed by no package script
            return false;
        }

        string parent = Path.GetDirectoryName(path)!;
        if (DsfMayWrite(parent, modeOf) && (!(modeOf ?? Mode)(parent).HasFlag(UnixFileMode.StickyBit) || Owner(path).User == Dsf || Owner(parent).User == Dsf))
        {
            return true;
        }
        return DsfMayReplace(parent, modeOf);
    }

    private bool DsfMayChange(string path) => DsfMayWrite(path) || DsfMayReplace(path);

    /// <summary>
    /// Check that dsf may change none of the files that the root service runs on or runs commands from, and that DCS may still write its own files
    /// </summary>
    private void AssertRootServiceFilesBelongToRoot()
    {
        List<string> rootFiles = ["/opt/dsf/bin", "/opt/dsf/conf/plugins.json", "/opt/dsf/conf/apparmor.conf"];
        rootFiles.AddRange(Directory.GetFileSystemEntries(Physical("/opt/dsf/bin")).Select(entry => entry[TreeRoot.Length..]));
        Assert.Multiple(() =>
        {
            foreach (string path in rootFiles)
            {
                Assert.That(DsfMayChange(path), Is.False, $"dsf may change {path} ({Owner(path).User}:{Owner(path).Group} {Mode(path)})");
            }
            Assert.That(Mode("/opt/dsf/bin/pipInstall2.py").HasFlag(UnixFileMode.UserExecute), Is.True, "pipInstall2.py is not executable");

            // DCS writes these and creates them when they are missing (DuetControlServer/Settings.cs:431-446, DuetControlServer/Commands/Plugins/StartPlugin.cs:65)
            Assert.That(DsfMayWrite("/opt/dsf/conf"), Is.True, "dsf may not create files in /opt/dsf/conf");
            foreach (string path in new[] { "/opt/dsf/conf/config.json", "/opt/dsf/conf/plugins.txt" }.Where(path => File.Exists(Physical(path))))
            {
                Assert.That(DsfMayWrite(path), Is.True, $"dsf may not write {path}");
            }
        });
    }

    #endregion

    #region RPM

    private sealed record RpmEntry(string Spec, string Path, string User, string Group, string Mode, bool IsDirectory);

    private static readonly Dictionary<string, string> RpmMacros = new()
    {
        ["%{dsfoptdir}"] = "/opt/dsf",
        ["%{_unitdir}"] = "/usr/lib/systemd/system",
        ["%{_sysconfdir}"] = "/etc",
        ["%{_exec_prefix}"] = "/usr"
    };

    private static string ExpandRpmMacros(string text, string name)
    {
        foreach ((string macro, string value) in RpmMacros)
        {
            text = text.Replace(macro, value);
        }
        return text.Replace("%{name}", name);
    }

    /// <summary>
    /// Entries of the %files section of a spec with the owner and mode that %defattr and %attr give them ("-" keeps the mode of the build tree)
    /// </summary>
    private static List<RpmEntry> RpmFiles(string spec)
    {
        string[] lines = File.ReadAllLines(DpsHost.ShippedFile($"pkg/rpm/{spec}.spec"));
        int start = Array.FindIndex(lines, line => line.Trim() == "%files");
        Assert.That(start, Is.Not.Negative, $"{spec}.spec has no %files section");

        string[] defattr = ["-", Root, Root, "-"];
        List<RpmEntry> entries = [];
        foreach (string rawLine in lines.Skip(start + 1))
        {
            string line = rawLine.Trim();
            if (line == string.Empty || line.StartsWith('#'))
            {
                continue;
            }

            Match match = Regex.Match(line, @"^%defattr\(([^)]*)\)");
            if (match.Success)
            {
                defattr = [.. match.Groups[1].Value.Split(',').Select(value => value.Trim())];
                continue;
            }

            match = Regex.Match(line, @"^((?:%(?:dir|config\([^)]*\)|attr\([^)]*\))\s+)*)(\S+)$");
            Assert.That(match.Success, Is.True, $"Unexpected line in {spec}.spec: {line}");
            string prefix = match.Groups[1].Value, path = ExpandRpmMacros(match.Groups[2].Value, spec);
            bool isDirectory = prefix.Contains("%dir");
            string[] attr = ["-", "-", "-"];
            Match attrMatch = Regex.Match(prefix, @"%attr\(([^)]*)\)");
            if (attrMatch.Success)
            {
                attr = [.. attrMatch.Groups[1].Value.Split(',').Select(value => value.Trim())];
            }
            entries.Add(new RpmEntry(spec, path,
                (attr[1] != "-") ? attr[1] : defattr[1],
                (attr[2] != "-") ? attr[2] : defattr[2],
                (attr[0] != "-") ? attr[0] : (isDirectory ? defattr[3] : defattr[0]),
                isDirectory));
        }
        return entries;
    }

    private static readonly string[] RpmSpecs = ["duetcontrolserver", "duetwebserver", "duetpluginservice", "duettools", "duetruntime", "duetsd", "duetsoftwareframework"];

    private static string RpmScriptlet(string spec, string section)
    {
        string[] lines = File.ReadAllLines(DpsHost.ShippedFile($"pkg/rpm/{spec}.spec"));
        int start = Array.FindIndex(lines, line => line.Trim() == section);
        Assert.That(start, Is.Not.Negative, $"{spec}.spec has no {section} scriptlet");
        IEnumerable<string> body = lines.Skip(start + 1).TakeWhile(line => !Regex.IsMatch(line, @"^%(pre|post|preun|postun|files|build|install|description)\b"));
        return ExpandRpmMacros(string.Join('\n', body) + "\n", spec);
    }

    private static bool IsWritableByOthers(string octalMode) =>
        octalMode != "-" && (Convert.ToInt32(octalMode, 8) & (Convert.ToInt32("020", 8) | Convert.ToInt32("002", 8))) != 0;

    #endregion

    /// <summary>
    /// On a new system the packages leave the files of the root service to root. The configuration directory belongs to root and has the sticky bit, so dsf
    /// may still create its files there but cannot replace the ones of root
    /// </summary>
    [Test]
    public void InstallationLeavesTheFilesOfTheRootServiceToRoot()
    {
        UnpackNewInstallation();
        ConfigurePackages();
        AssertRootServiceFilesBelongToRoot();
    }

    /// <summary>
    /// An upgrade takes plugins.json and the configuration directory back from dsf, which the packages of upstream gave them to
    /// </summary>
    [Test]
    public void UpgradeTakesTheFilesOfTheRootServiceBackFromDsf()
    {
        UnpackUpgrade();
        ConfigurePackages("3.6.0");
        AssertRootServiceFilesBelongToRoot();
        Assert.That(Owner("/opt/dsf/conf/plugins.txt").User, Is.EqualTo(Dsf));
    }

    /// <summary>
    /// chmod, and chown without -h, follow a symlink, and dsf may put one in place of each entry that it may rename or remove: its own files in the
    /// configuration directory at any time. So the scripts that run on an upgrade give such a file to root before they call chmod on it or chown without -h,
    /// else root changes the target of a symlink that dsf put there between the test for a symlink and the call. The duetwebserver postinst, which gives
    /// http.json to dsf with chown dsf:dsf, is not checked here
    /// </summary>
    [Test]
    public void UpgradeFollowsNoSymlinkThatDsfMayPutInPlace()
    {
        UnpackUpgrade();
        foreach (string package in new[] { "duetcontrolserver", "duetpluginservice", "duetpimanagementplugin" })
        {
            RunDebianScript(package, "postinst", "configure", "3.6.0");
        }
        Assert.That(_callsThroughReplaceableEntries, Is.Empty);
        AssertRootServiceFilesBelongToRoot();
    }

    /// <summary>
    /// duetcontrolserver may be configured on its own, e.g. by apt-get install --reinstall. Its postinst must not give the settings of the root service to dsf
    /// </summary>
    [Test]
    public void ConfiguringDcsAloneLeavesTheFilesOfTheRootServiceToRoot()
    {
        UnpackNewInstallation();
        ConfigurePackages();
        RunDebianScript("duetcontrolserver", "postinst", "configure", "3.7.0");
        AssertRootServiceFilesBelongToRoot();
    }

    /// <summary>
    /// The RPM packages give the binaries of the root service, the runtime it runs on and the files it takes commands from to root, as dpkg-deb
    /// --root-owner-group does for the Debian packages, and the configuration directory is set up as by the duetcontrolserver postinst
    /// </summary>
    [Test]
    public void RpmPackagesGiveTheFilesOfTheRootServiceToRoot()
    {
        List<RpmEntry> entries = [.. RpmSpecs.SelectMany(RpmFiles)];
        List<RpmEntry> rootFiles = [.. entries.Where(entry =>
            entry.Spec == "duetruntime" ||
            entry.Path.StartsWith("/opt/dsf/bin/DuetPluginService") ||
            entry.Path is "/opt/dsf/bin/pipInstall2.py" or "/opt/dsf/conf/plugins.json" or "/opt/dsf/conf/apparmor.conf")];
        Assert.That(rootFiles.Select(entry => entry.Path), Is.SupersetOf(new[] { "/opt/dsf/bin/DuetPluginService", "/opt/dsf/bin/pipInstall2.py", "/opt/dsf/conf/plugins.json",
            "/opt/dsf/conf/apparmor.conf", "/opt/dsf/bin/*" }));

        Assert.Multiple(() =>
        {
            foreach (RpmEntry entry in rootFiles)
            {
                Assert.That(entry.User, Is.EqualTo(Root), $"owner of {entry.Path} in {entry.Spec}.spec");
                Assert.That(entry.Group, Is.Not.EqualTo(Dsf), $"group of {entry.Path} in {entry.Spec}.spec");
                Assert.That(IsWritableByOthers(entry.Mode), Is.False, $"mode of {entry.Path} in {entry.Spec}.spec");
            }
            Assert.That(rootFiles.Single(entry => entry.Path == "/opt/dsf/bin/pipInstall2.py").Mode, Is.EqualTo("0755"), "pipInstall2.py is not executable");

            RpmEntry? conf = entries.SingleOrDefault(entry => entry.IsDirectory && entry.Path == "/opt/dsf/conf");
            Assert.That(conf, Is.Not.Null, "no spec owns /opt/dsf/conf");
            Assert.That(conf?.User, Is.EqualTo(Root), "owner of /opt/dsf/conf");
            Assert.That(conf?.Mode, Is.EqualTo("1775"), "mode of /opt/dsf/conf");
        });
    }

    /// <summary>
    /// rpm keeps a changed config file on upgrade (%config(noreplace)), so the %post scriptlet of duetpluginservice takes plugins.json and apparmor.conf back
    /// from dsf, which older packages gave them to
    /// </summary>
    [Test]
    public void RpmUpgradeTakesTheSettingsOfTheRootServiceBackFromDsf()
    {
        UnpackNewInstallation();
        Add("/opt/dsf/conf", Root, Dsf, Mode1775, true);
        Add("/opt/dsf/conf/plugins.json", Dsf, Dsf, Mode664);
        Add("/opt/dsf/conf/apparmor.conf", Dsf, Dsf, Mode664);

        RunScript(RpmScriptlet("duetpluginservice", "%post"), "2");
        Assert.That(_callsThroughReplaceableEntries, Is.Empty);
        Assert.Multiple(() =>
        {
            foreach (string path in new[] { "/opt/dsf/conf/plugins.json", "/opt/dsf/conf/apparmor.conf" })
            {
                Assert.That(DsfMayChange(path), Is.False, $"dsf may change {path} ({Owner(path).User}:{Owner(path).Group} {Mode(path)})");
            }
        });
    }
}
