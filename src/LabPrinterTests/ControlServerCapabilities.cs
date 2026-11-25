using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// The file capabilities of DCS. With cap_sys_ptrace,cap_dac_read_search+ep (packages up to this fix) every file DCS opened for a code was opened
/// with CAP_DAC_READ_SEARCH, so M36.2 returned root-only files from any volume, e.g. the SSH host keys of the /etc/ssh mount. The packages set
/// them as permitted only now, and DCS raises them only for the /proc reads of its IPC peer check (ProcessHelpers.cs). That check still has to
/// work, or DWS could not talk to DCS and no test here would get a reply.
/// A side-load does not change the file capabilities of /opt/dsf/bin/DuetControlServer, so these tests need a DCS package with the fix (DSF #232,
/// after 3.7.0+mp.15) and skip on an older one
/// </summary>
[TestFixture]
public class ControlServerCapabilities : LabTest
{
    /// <summary>
    /// Directory of the test files, below a volume on the CHX (/var) and away from the side-load directory
    /// </summary>
    private const string TestDirectory = "/var/tmp/lab-capabilities";

    /// <summary>
    /// CAP_DAC_READ_SEARCH (2) and CAP_SYS_PTRACE (19)
    /// </summary>
    private const ulong PermittedCapabilities = (1UL << 2) | (1UL << 19);

    /// <summary>
    /// Skip unless the installed package sets the capabilities as permitted only. The postinst of the packages up to 3.7.0+mp.15 runs
    /// setcap cap_sys_ptrace,cap_dac_read_search+ep (pkg/deb/duetcontrolserver/DEBIAN/postinst before #232). That the package scripts say +p
    /// is checked offline by UnitTests/Startup/ControlServerCapabilities.cs, so the skip hides no regression of the scripts; this test checks
    /// what the installed script gives the running DCS
    /// </summary>
    private static async Task IgnoreIfPackagePredatesTheFixAsync()
    {
        string setcap = (await Lab.RunOnSbcAsync("grep -oE 'setcap [^ ]+ /opt/dsf/bin/DuetControlServer' /var/lib/dpkg/info/duetcontrolserver.postinst || true")).Trim();
        if (setcap.Contains("+ep"))
        {
            Assert.Ignore($"The installed DCS package predates DSF #232, its postinst runs \"{setcap}\"");
        }
    }

    [Test]
    public async Task ControlServerRunsWithoutEffectiveCapabilities()
    {
        await IgnoreIfPackagePredatesTheFixAsync();
        string pid = await Lab.GetControlServerPidAsync();
        string status = await Lab.RunOnSbcAsync($"grep -E '^Cap(Prm|Eff):' /proc/{pid}/status");
        ulong Read(string key) => Convert.ToUInt64(status.Split('\n').Single(line => line.StartsWith(key + ":"))[(key.Length + 1)..].Trim(), 16);

        Assert.That(Read("CapPrm"), Is.EqualTo(PermittedCapabilities), status);
        Assert.That(Read("CapEff"), Is.Zero, "DCS opens files with these capabilities");
    }

    /// <summary>
    /// M36.2 reads a file below a volume with the rights of the dsf user: a file that only root may read gives err 1, a readable one its content
    /// </summary>
    [Test]
    public async Task FileReadsHaveTheRightsOfTheDsfUser()
    {
        await IgnoreIfPackagePredatesTheFixAsync();

        // The volume with the longest path that holds the directory. Drive 0 is the virtual SD card, whatever volumes[0] says
        List<string?> volumes = Lab.Read(Session, model => model.Volumes.Select(item => item.Path).ToList());
        int drive = -1;
        string? root = null;
        for (int i = 1; i < volumes.Count; i++)
        {
            string? path = volumes[i];
            if (path is not null && (path == "/" || TestDirectory.StartsWith(path.TrimEnd('/') + "/")) && (root is null || path.Length > root.Length))
            {
                drive = i;
                root = path;
            }
        }
        if (root is null)
        {
            Assert.Ignore($"No volume holds {TestDirectory}");
        }
        string drivePath = $"{drive}:/{TestDirectory[root.TrimEnd('/').Length..].TrimStart('/')}";

        await Lab.RunOnSbcAsync($"""
            set -e
            {SbcDirectories.CreateRootDirectoryScript(TestDirectory)}
            printf 'lab test: readable by dsf\n' | sudo -n tee {TestDirectory}/readable >/dev/null
            sudo -n chmod 0644 {TestDirectory}/readable
            printf 'lab test: root only\n' | sudo -n tee {TestDirectory}/root-only >/dev/null
            sudo -n chmod 0600 {TestDirectory}/root-only
            """);
        try
        {
            string reply = await SendCodeAsync($"M36.2 P\"{drivePath}/root-only\" S0");
            using JsonDocument rootOnly = JsonDocument.Parse(reply);
            Assert.That(rootOnly.RootElement.GetProperty("fragment").GetProperty("err").GetInt32(), Is.EqualTo(1), reply);
            Assert.That(reply, Does.Not.Contain("root only"));

            reply = await SendCodeAsync($"M36.2 P\"{drivePath}/readable\" S0");
            using JsonDocument readable = JsonDocument.Parse(reply);
            Assert.That(readable.RootElement.GetProperty("fragment").GetProperty("err").GetInt32(), Is.Zero, reply);
            Assert.That(readable.RootElement.GetProperty("fragment").GetProperty("data").GetString(), Is.EqualTo("lab test: readable by dsf\n"));
        }
        finally
        {
            await Lab.RunOnSbcAsync($"sudo -n rm -rf {TestDirectory}");
        }
    }
}
