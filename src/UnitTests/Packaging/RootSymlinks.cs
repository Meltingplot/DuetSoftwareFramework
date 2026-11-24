using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnitTests.Plugins;

namespace UnitTests.Packaging;

using File = System.IO.File;

/// <summary>
/// Programs that run as root must not write, read, chown or chmod a file in a directory where the dsf user, or anything that runs as dsf, may put a symlink
/// in place of the file: chown without -h, chmod, sed -i, shell redirections and FileMode.Create act on what the symlink points to, so dsf could make root
/// change or disclose any file. The packages of upstream give /opt/dsf/conf and /opt/dsf/sd with all files to dsf (duetcontrolserver postinst:
/// chown -R dsf:dsf /opt/dsf/conf, duetsd postinst: chown -R dsf:dsf /opt/dsf/sd), and the files checked here belong to dsf in any layout, so dsf may replace
/// each of them at any time. Below /opt/dsf/sd dsf may replace the directories as well, so there every path leads wherever dsf wants, -h or not. Only the
/// entries of root-owned directories stay where they are: /opt/dsf/conf and /opt/dsf/sd themselves are entries of /opt/dsf.
/// The scripts run against a tree in a temporary directory: /opt/dsf in the scripts is replaced by that tree, and the tools are stubs that record each file
/// they act on and then behave like the real tool. The stubs run as regular user and stand for root unless setpriv (also a stub) dropped to another user.
/// Files outside the tree stand for files of root: a stub that runs as another user may not open or change them, as the kernel would refuse it
/// </summary>
[TestFixture]
public class RootSymlinks
{
    private const string Root = "root", Dsf = "dsf";

    private const UnixFileMode Mode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode Mode644 = Mode600 | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode Mode660 = Mode600 | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
    private const UnixFileMode Mode755 = Mode644 | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private const string DpkgInfo = "/var/lib/dpkg/info";

    private string _directory = null!;

    private string TreeRoot => Path.Combine(_directory, "tree");
    private string Stubs => Path.Combine(_directory, "stubs");
    private string Outside => Path.Combine(_directory, "outside");
    private string CallLog => Path.Combine(_directory, "calls");

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dsf-root-symlinks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TreeRoot);
        Directory.CreateDirectory(Outside);
        CreateStubs();
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    #region Tree, stubs and scripts

    private string Physical(string path) => TreeRoot + path;

    private void AddFile(string path, string content, UnixFileMode mode = Mode644)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Physical(path))!);
        File.WriteAllText(Physical(path), content);
        File.SetUnixFileMode(Physical(path), mode);
    }

    /// <summary>
    /// Create a file of root outside the tree
    /// </summary>
    private string AddFileOfRoot(string name, string content)
    {
        string path = Path.Combine(Outside, name);
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, Mode600);
        return path;
    }

    /// <summary>
    /// Let dsf put a symlink in place of an entry of the tree
    /// </summary>
    private void PlantSymlink(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Physical(path))!);
        File.CreateSymbolicLink(Physical(path), target);
    }

    private static string RealTool(string name) => File.Exists("/usr/bin/" + name) ? "/usr/bin/" + name : "/bin/" + name;

    private void WriteStub(string name, string body)
    {
        string path = Path.Combine(Stubs, name);
        File.WriteAllText(path,
            "#!/bin/sh\n" +
            "user=${RUN_AS:-root}\n" +
            "TOOL=" + name + "\n" +
            "# One line per file: tool, user, 1 if the call acts on what a symlink in place of the file points to, file\n" +
            "record() { printf '%s\\t%s\\t%s\\t%s\\n' \"$TOOL\" \"$user\" \"$1\" \"$2\" >> \"$CALL_LOG\"; }\n" +
            "# Files outside the tree stand for files of root, which no other user may open or change\n" +
            $"denied() {{ [ \"$user\" != root ] && case \"$({RealTool("readlink")} -f -- \"$1\")\" in \"$OUTSIDE\"/*) return 0 ;; esac; return 1; }}\n" +
            body);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void CreateStubs()
    {
        Directory.CreateDirectory(Stubs);

        // chown records the owner with the tool. Without -h and -R it changes what a symlink points to (coreutils: --dereference is the default when not
        // operating recursively, -P when operating recursively). It changes nothing here, the tests run as regular user
        WriteStub("chown",
            "deref=1\n" +
            "while [ $# -gt 0 ]; do case \"$1\" in -*h*|--no-dereference|-*R*|--recursive) deref=0 ;; -*) ;; *) break ;; esac; shift; done\n" +
            "TOOL=\"chown $1\"; shift\n" +
            "for f in \"$@\"; do record $deref \"$f\"; done\n");

        // chmod changes what a symlink on its command line points to (coreutils manual, chmod invocation)
        WriteStub("chmod",
            "mode=0\n" +
            "for a in \"$@\"; do\n" +
            "  case \"$a\" in -*) [ $mode -eq 0 ] && continue ;; esac\n" +
            "  if [ $mode -eq 0 ]; then mode=1; continue; fi\n" +
            "  record 1 \"$a\"\n" +
            "  if denied \"$a\"; then echo \"chmod: changing permissions of '$a': Operation not permitted\" >&2; exit 1; fi\n" +
            "done\n" +
            $"exec {RealTool("chmod")} \"$@\"\n");

        // tee, grep, sed and touch open what a symlink points to. sed -i reads it and puts a copy in place of the symlink
        WriteStub("tee",
            "for a in \"$@\"; do case \"$a\" in -*) ;; *) record 1 \"$a\"\n" +
            $"  if denied \"$a\"; then {RealTool("cat")} > /dev/null; echo \"tee: $a: Permission denied\" >&2; exit 1; fi ;; esac; done\n" +
            $"exec {RealTool("tee")} \"$@\"\n");
        foreach ((string tool, int exitCode) in new[] { ("grep", 2), ("sed", 4) })
        {
            WriteStub(tool,
                "skip=0\n" +
                "for a in \"$@\"; do\n" +
                "  if [ $skip -eq 1 ]; then skip=0; continue; fi\n" +
                "  case \"$a\" in -e|-f) skip=1 ;; -*) ;; *) record 1 \"$a\"\n" +
                $"    if denied \"$a\"; then echo \"{tool}: $a: Permission denied\" >&2; exit {exitCode}; fi ;; esac\n" +
                "done\n" +
                $"exec {RealTool(tool)} \"$@\"\n");
        }
        WriteStub("touch",
            "for a in \"$@\"; do case \"$a\" in -*) ;; *) record 1 \"$a\"\n" +
            "  if denied \"$a\"; then echo \"touch: cannot touch '$a': Permission denied\" >&2; exit 1; fi ;; esac; done\n" +
            $"exec {RealTool("touch")} \"$@\"\n");

        // openssl writes the files named by -out and -keyout and reads the ones named by -in and -inkey
        WriteStub("openssl",
            "next=\n" +
            "for a in \"$@\"; do\n" +
            "  case \"$next\" in\n" +
            "    out) record 1 \"$a\"; if denied \"$a\"; then echo \"Can't open \\\"$a\\\" for writing, Permission denied\" >&2; exit 1; fi; echo \"PEM of the stub\" > \"$a\" ;;\n" +
            "    in) record 1 \"$a\"; if denied \"$a\"; then echo \"Could not open $a: Permission denied\" >&2; exit 1; fi ;;\n" +
            "  esac\n" +
            "  next=\n" +
            "  case \"$a\" in -out|-keyout) next=out ;; -in|-inkey|--inkey) next=in ;; esac\n" +
            "done\n");

        // setpriv runs a program as the user given by --reuid, only root may do that
        WriteStub("setpriv",
            "record 0 \"$*\"\n" +
            "[ \"$user\" = root ] || { echo \"setpriv: setresuid failed: Operation not permitted\" >&2; exit 1; }\n" +
            "while [ $# -gt 0 ]; do case \"$1\" in --reuid=*) RUN_AS=${1#--reuid=} ;; --) shift; break ;; -*) ;; *) break ;; esac; shift; done\n" +
            "export RUN_AS\n" +
            "exec \"$@\"\n");

        // cp opens what a symlink in place of the source or the destination points to. mv renames the source and replaces the destination entry itself
        WriteStub("cp",
            "for a in \"$@\"; do case \"$a\" in -*) ;; *) record 1 \"$a\"\n" +
            "  if denied \"$a\"; then echo \"cp: cannot open '$a': Permission denied\" >&2; exit 1; fi ;; esac; done\n" +
            $"exec {RealTool("cp")} \"$@\"\n");
        WriteStub("mv",
            "for a in \"$@\"; do case \"$a\" in -*) ;; *) record 0 \"$a\"\n" +
            "  if denied \"$a\"; then echo \"mv: cannot move '$a': Permission denied\" >&2; exit 1; fi ;; esac; done\n" +
            $"exec {RealTool("mv")} \"$@\"\n");

        // dpkg-query -L lists the files of an installed package from the database of dpkg, which only root may change
        WriteStub("dpkg-query",
            "[ \"$1\" = -L ] || exit 1\n" +
            $"exec {RealTool("cat")} \"{Physical(DpkgInfo)}/$2.list\"\n");

        WriteStub("id", "if [ \"$user\" = root ]; then echo 0; else echo 999; fi\n");
        WriteStub("hostname", "echo duet3\n");
        WriteStub("systemctl", "exit 1\n");                 // no unit is active or enabled
        WriteStub("rm", $"exec {RealTool("rm")} \"$@\"\n");  // rm removes a symlink itself
        foreach (string tool in new[] { "setcap", "sleep", "sync" })
        {
            WriteStub(tool, "exit 0\n");
        }
    }

    /// <summary>
    /// Run a script as root would, against the tree
    /// </summary>
    /// <param name="script">Text of the script</param>
    /// <param name="path">Where to put the script in the tree</param>
    /// <param name="arguments">Arguments of the script</param>
    private void RunScript(string script, string path, params string[] arguments)
    {
        string physical = Physical(path);
        Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
        File.WriteAllText(physical, script.Replace("/opt/dsf", Physical("/opt/dsf")));
        File.SetUnixFileMode(physical, Mode755);

        // dpkg and the plugin execute the scripts themselves, so the interpreter comes from their first line
        ProcessStartInfo startInfo = new(physical) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(physical) };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["PATH"] = Stubs;
        startInfo.Environment["CALL_LOG"] = CallLog;
        startInfo.Environment["OUTSIDE"] = Outside;
        startInfo.Environment.Remove("RUN_AS");
        using Process process = Process.Start(startInfo)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd() + error.Result;
        process.WaitForExit();

        // Every program the scripts call is a stub, the shell reports any other one as not found
        Assert.That(output, Does.Not.Contain("not found"), output);
    }

    private void RunShippedScript(string shippedPath, string path, params string[] arguments) =>
        RunScript(File.ReadAllText(DpsHost.ShippedFile(shippedPath)), path, arguments);

    private sealed record Call(string Tool, string User, bool Dereferences, string Path)
    {
        public override string ToString() => $"{Tool} as {User}{(Dereferences ? " through" : string.Empty)} {Path}";
    }

    /// <summary>
    /// Calls of the stubs so far, with the paths in the tree as on the system
    /// </summary>
    private List<Call> Calls() => File.Exists(CallLog)
        ? [.. File.ReadAllLines(CallLog).Select(line => line.Split('\t')).Select(fields => new Call(fields[0], fields[1], fields[2] == "1", fields[3].Replace(TreeRoot, string.Empty)))]
        : [];

    /// <summary>
    /// Whether dsf may put a symlink in place of an entry at any time. See the summary of the class
    /// </summary>
    private static bool DsfMayReplace(string path) => Path.GetDirectoryName(path) == "/opt/dsf/conf" || path.StartsWith("/opt/dsf/sd/");

    /// <summary>
    /// Whether dsf may put a symlink in place of a directory on the way to an entry, so that the path leads into another directory
    /// </summary>
    private static bool DsfMayRedirect(string path)
    {
        for (string? directory = Path.GetDirectoryName(path); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            if (DsfMayReplace(directory))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Check that root acted on nothing through an entry that dsf may replace, symlink or not: dsf may put one there between a check and the call.
    /// Neither may root act on an entry in a directory that dsf may replace, which -h does not help against
    /// </summary>
    private void AssertRootActsOnNothingDsfMayReplace()
    {
        List<string> calls = [.. Calls().Where(call => call.User == Root && ((call.Dereferences && DsfMayReplace(call.Path)) || DsfMayRedirect(call.Path))).Select(call => call.ToString())];
        Assert.That(calls, Is.Empty, "calls of root that act on what dsf may put a symlink in place of");
    }

    private void AssertUnchanged(string fileOfRoot, string content)
    {
        Assert.That(File.Exists(fileOfRoot), Is.True, $"{fileOfRoot} is gone");
        Assert.That(File.ReadAllText(fileOfRoot), Is.EqualTo(content), $"content of {fileOfRoot}");
        Assert.That(File.GetUnixFileMode(fileOfRoot), Is.EqualTo(Mode600), $"mode of {fileOfRoot}");
    }

    #endregion

    #region HTTPS certificate of the DuetPi management plugin

    private const string CertificateScript = "/opt/dsf/plugins/DuetPiManagementPlugin/dsf/gen-https-cert.sh";

    /// <summary>
    /// The DuetPi management plugin runs as root (superUser in DuetPiManagementPlugin.json) and runs gen-https-cert.sh when HTTPS is enabled and
    /// /opt/dsf/conf/https.pfx does not exist (HTTP.cs:73-77). The certificate files end up belonging to dsf, so the script makes them with the rights of
    /// dsf, and nothing in /opt/dsf/conf is opened, chowned or chmodded by root
    /// </summary>
    [Test]
    public void CertificateIsMadeWithTheRightsOfDsf()
    {
        Directory.CreateDirectory(Physical("/opt/dsf/conf"));
        RunShippedScript("pkg/common/duetpimanagementplugin" + CertificateScript, CertificateScript);

        List<Call> calls = Calls();
        Assert.Multiple(() =>
        {
            Assert.That(calls.Where(call => call.Tool == "setpriv").Select(call => call.Path), Has.One.Contains("--reuid=dsf").And.Contains("--regid=dsf"));
            Assert.That(calls.Where(call => call.Path.StartsWith("/opt/dsf/conf/")).Select(call => call.ToString()), Is.Not.Empty.And.All.Contains(" as dsf"));
            foreach (string file in new[] { "https.key", "https.crt", "https.pfx" })
            {
                string path = Physical("/opt/dsf/conf/" + file);
                Assert.That(File.Exists(path), Is.True, $"{file} was not made");
                Assert.That(File.Exists(path) ? File.GetUnixFileMode(path) : default, Is.EqualTo(Mode660), $"mode of {file}");
            }
        });
    }

    /// <summary>
    /// File.Exists counts a symlink as existing even if it points nowhere, so dsf leaves https.pfx out to make the plugin run the script, and puts symlinks to
    /// files of root in place of https.key and https.crt. root must neither write those files nor change their owner or mode
    /// </summary>
    [Test]
    public void CertificateScriptChangesNoFileOfRootThroughASymlink()
    {
        string existingFile = AddFileOfRoot("existing", "file of root\n"), newFile = Path.Combine(Outside, "new");
        PlantSymlink("/opt/dsf/conf/https.key", newFile);
        PlantSymlink("/opt/dsf/conf/https.crt", existingFile);

        RunShippedScript("pkg/common/duetpimanagementplugin" + CertificateScript, CertificateScript);

        Assert.Multiple(() =>
        {
            AssertRootActsOnNothingDsfMayReplace();
            AssertUnchanged(existingFile, "file of root\n");
            Assert.That(File.Exists(newFile), Is.False, "root made a file through a symlink");
        });
    }

    #endregion

    #region reprapfirmware package

    private const string FirmwarePostinst = "pkg/deb/reprapfirmware/DEBIAN/postinst";

    /// <summary>
    /// Let dpkg install a package: list its files in the database of dpkg, as dpkg-query -L reads them
    /// </summary>
    private void AddPackageList(string package, params string[] paths)
    {
        Directory.CreateDirectory(Physical(DpkgInfo));
        File.WriteAllLines(Physical($"{DpkgInfo}/{package}.list"), paths.Select(Physical));
    }

    /// <summary>
    /// The files of the package in the layout of this template (sys) and in the layout of the reprapfirmware package of Duet3D (firmware, *.bin and *.uf2)
    /// </summary>
    private static readonly string[] FirmwareFiles = ["/opt/dsf/sd/sys/Duet3Firmware_MB6HC.bin", "/opt/dsf/sd/firmware/Duet3Firmware_EXP3HC.uf2"];

    /// <summary>
    /// The reprapfirmware postinst gives the firmware files to dsf: dpkg installs them as root, and DWS (rr_upload) and DCS (M28, echo, files written by RRF)
    /// overwrite files in place. dsf owns /opt/dsf/sd (duetsd postinst) and may put a symlink in place of sys, so that /opt/dsf/sd/sys/*.bin names files of
    /// root, as the files of the package that dpkg put through that symlink. chown -h does not help there, its last component is no symlink. root must
    /// neither give those files to dsf nor change them
    /// </summary>
    [Test]
    public void FirmwarePackageChangesNoFileOfRootThroughASymlinkInPlaceOfItsDirectory()
    {
        string fileOfRoot = AddFileOfRoot("brcmfmac.bin", "firmware of root\n");
        string packagedFile = AddFileOfRoot("Duet3Firmware_MB6HC.bin", "firmware of the package\n");
        PlantSymlink("/opt/dsf/sd/sys", Outside);
        AddPackageList("reprapfirmware", ["/opt/dsf", "/opt/dsf/sd", "/opt/dsf/sd/sys", "/opt/dsf/sd/sys/Duet3Firmware_MB6HC.bin"]);

        RunShippedScript(FirmwarePostinst, DpkgInfo + "/reprapfirmware.postinst", "configure");

        Assert.Multiple(() =>
        {
            AssertRootActsOnNothingDsfMayReplace();
            AssertUnchanged(fileOfRoot, "firmware of root\n");
            AssertUnchanged(packagedFile, "firmware of the package\n");
            Assert.That(Directory.GetFiles(Outside).Select(Path.GetFileName), Is.EquivalentTo(new[] { "brcmfmac.bin", "Duet3Firmware_MB6HC.bin" }), "files made through the symlink");
        });
    }

    /// <summary>
    /// dsf takes over the firmware files of the package with its own rights: it copies each of them and puts the copy in its place. Other files stay as they are
    /// </summary>
    [Test]
    public void FirmwarePackageGivesItsFilesToDsf()
    {
        foreach (string file in FirmwareFiles)
        {
            AddFile(file, "firmware " + Path.GetFileName(file));
        }
        AddFile("/opt/dsf/sd/sys/Uploaded.bin", "file of dsf");
        AddPackageList("reprapfirmware", ["/opt/dsf", "/opt/dsf/sd", "/opt/dsf/sd/firmware", "/opt/dsf/sd/sys", .. FirmwareFiles]);

        RunShippedScript(FirmwarePostinst, DpkgInfo + "/reprapfirmware.postinst", "configure");

        Assert.Multiple(() =>
        {
            AssertRootActsOnNothingDsfMayReplace();
            Assert.That(Calls().Where(call => call.Tool == "mv").Select(call => $"{call.User} {call.Path}"),
                Is.EquivalentTo(FirmwareFiles.SelectMany(file => new[] { $"{Dsf} {file}.new", $"{Dsf} {file}" })), "the firmware files no longer go to dsf");
            foreach (string file in FirmwareFiles)
            {
                Assert.That(File.ReadAllText(Physical(file)), Is.EqualTo("firmware " + Path.GetFileName(file)), $"content of {file}");
                Assert.That(File.Exists(Physical(file + ".new")), Is.False, $"{file}.new is left over");
            }
            Assert.That(Calls().Where(call => call.Path == "/opt/dsf/sd/sys/Uploaded.bin"), Is.Empty, "a file of dsf was copied");
        });
    }

    #endregion
}
