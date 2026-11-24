using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DuetSharedLibrary;
using NUnit.Framework;

namespace UnitTests.Startup;

/// <summary>
/// The file capabilities of DuetControlServer. The packages gave it cap_sys_ptrace,cap_dac_read_search+ep (upstream 561d5c2ca, 77c3c748e), so both
/// were in the effective set of every thread for the whole run, and every file DCS opened for a code or an IPC client was opened with
/// CAP_DAC_READ_SEARCH, which bypasses file read permission checks (capabilities(7)). M36.2 returns the raw bytes of any file
/// (FileInfoParser.cs:754-882), and the drive numbers reach every mount (G3 in design/open-findings.md), so a client that may send codes could
/// read root-only files, e.g. the SSH host keys below the /etc/ssh mount of the CHX.
/// DCS needs the two capabilities only to read /proc/{pid}/exe and auxv of the DSF services that connect to it (Connection.cs:190, 216), because
/// these processes are started from files with capabilities and so are not dumpable (prctl(2), proc_pid(5), ptrace(2) "Ptrace access mode checking").
/// The packages now set them as permitted only (+p), and ProcessHelpers.cs:201 and 227 raise them into the effective set of the calling thread around
/// these two reads. The permitted file capabilities still set AT_SECURE (getauxval(3)), which the peer check needs for a second DCS process
/// </summary>
[TestFixture]
public class ControlServerCapabilities
{
    /// <summary>
    /// Root of the package files
    /// </summary>
    private static string PackageDirectory => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "pkg"));

    /// <summary>
    /// Every package format that installs DCS sets its file capabilities to exactly the two capabilities, permitted but not effective.
    /// The Arch build (pkg/PKGBUILD/build.sh) packages no programs and has no install script
    /// </summary>
    [Test]
    public void PackagesSetTheCapabilitiesAsPermittedOnly()
    {
        Regex setcap = new(@"\bsetcap\s+(\S+)\s+\S*/bin/DuetControlServer\b");
        Dictionary<string, List<string>> capabilities = [];
        foreach (string file in Directory.EnumerateFiles(PackageDirectory, "*", SearchOption.AllDirectories))
        {
            foreach (Match match in setcap.Matches(System.IO.File.ReadAllText(file)))
            {
                string name = Path.GetRelativePath(PackageDirectory, file);
                if (!capabilities.TryGetValue(name, out List<string>? list))
                {
                    list = [];
                    capabilities.Add(name, list);
                }
                list.Add(match.Groups[1].Value);
            }
        }

        Assert.That(capabilities.Keys, Is.EquivalentTo(new[] { "deb/duetcontrolserver/DEBIAN/postinst", "rpm/duetcontrolserver.spec" }));
        Assert.That(capabilities.Values.SelectMany(list => list), Has.All.EqualTo("cap_sys_ptrace,cap_dac_read_search+p"),
            "With the effective bit DCS opens every file with CAP_DAC_READ_SEARCH");
    }

    /// <summary>
    /// The thread capabilities of the process, as /proc/thread-self/status reports them
    /// </summary>
    private static (ulong Permitted, ulong Effective, ulong Inheritable) ReadThreadCapabilities()
    {
        string[] lines = System.IO.File.ReadAllLines("/proc/thread-self/status");
        ulong Read(string key) => Convert.ToUInt64(lines.Single(line => line.StartsWith(key + ":"))[(key.Length + 1)..].Trim(), 16);
        return (Read("CapPrm"), Read("CapEff"), Read("CapInh"));
    }

    /// <summary>
    /// The peer checks read the capability sets of the calling thread with capget(2) before they raise anything. If that call failed, DCS would read
    /// the /proc entries of DWS without the capabilities and refuse its connection. Without capabilities, as here, the checks work as before for
    /// processes that are dumpable, and they leave the sets of the thread as they were
    /// </summary>
    [Test]
    public void PeerChecksReadAndKeepTheCapabilitiesOfTheThread()
    {
        Assembly library = typeof(ProcessHelpers).Assembly;
        Type interop = library.GetType("Interop", true)!, headerType = library.GetType("cap_user_header", true)!, dataType = library.GetType("cap_user_data", true)!;
        object header = Activator.CreateInstance(headerType)!;
        headerType.GetField("version", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(header, interop.GetField("_LINUX_CAPABILITY_VERSION_3", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        object?[] arguments = [header, Activator.CreateInstance(dataType)];
        int result = (int)interop.GetMethod("capget", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments)!;
        ulong Field(string name) => (ulong)(uint)dataType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(arguments[1])!;

        (ulong Permitted, ulong Effective, ulong Inheritable) before = ReadThreadCapabilities();
        Assert.That(result, Is.Zero, "capget failed");
        Assert.That((Field("permittedHigh") << 32) | Field("permitted"), Is.EqualTo(before.Permitted));
        Assert.That((Field("effectiveHigh") << 32) | Field("effective"), Is.EqualTo(before.Effective));
        Assert.That((Field("inheritableHigh") << 32) | Field("inheritable"), Is.EqualTo(before.Inheritable));

        using Process child = Process.Start(new ProcessStartInfo("/bin/sleep", ["30"]))!;
        try
        {
            Assert.That(ProcessHelpers.GetExecutablePath(Environment.ProcessId), Is.EqualTo(Environment.ProcessPath));
            string? childPath = ProcessHelpers.GetExecutablePath(child.Id);
            Assert.That(Path.GetFileName(childPath), Is.EqualTo("sleep"));
            Assert.That(ProcessHelpers.IsExecSecure(child.Id), Is.False, "sleep has no file capabilities");
            Assert.That(ReadThreadCapabilities(), Is.EqualTo(before));
        }
        finally
        {
            child.Kill();
        }
    }
}
