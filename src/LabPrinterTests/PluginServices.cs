using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Plugins on the SBC that are stopped and started while their plugin service keeps running
/// </summary>
/// <remarks>
/// The exit of DCS outside a stop job, after which the plugin services must register again (DSF #151, fix 6), is checked by
/// ControlServerRestart.ResetDoesNotHangShutdown on its M999, so it costs no emergency stop of its own
/// </remarks>
[TestFixture, Category("Approval"), Explicit("Needs Tim's approval for each run")]
public class PluginServices : LabTest
{
    /// <summary>
    /// The plugin that serves the HMI of the machine, stopped only if no other plugin has child processes
    /// </summary>
    private const string HmiPlugin = "CHX350";

    /// <summary>
    /// A process on the SBC: parent, start time in clock ticks after the boot (field 22 of /proc/[pid]/stat, as DuetSharedLibrary ProcessHelpers.cs:57-79
    /// reads it, so a reused PID is told apart) and state (field 3, Z for a zombie)
    /// </summary>
    private readonly record struct SbcProcess(int Pid, int ParentPid, ulong StartTime, string State);

    /// <summary>
    /// Read every process of the SBC from /proc, read-only
    /// </summary>
    private static async Task<Dictionary<int, SbcProcess>> ReadProcessesAsync()
    {
        string output = await Lab.RunOnSbcAsync("""
            sudo -n bash <<'EOF'
            for f in /proc/[0-9]*/stat; do
              s=$(cat "$f" 2> /dev/null) || continue
              [ -n "$s" ] || continue
              set -- ${s##*) }
              echo "${s%% *} $2 ${20} $1"
            done
            EOF
            """);
        Dictionary<int, SbcProcess> processes = [];
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(' ');
            if (parts.Length == 4 && int.TryParse(parts[0], out int pid) && int.TryParse(parts[1], out int parent) &&
                ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong startTime))
            {
                processes[pid] = new SbcProcess(pid, parent, startTime, parts[3]);
            }
        }
        return processes;
    }

    /// <summary>
    /// Processes below a process, as ProcessHelpers.GetDescendants (ProcessHelpers.cs:89-119) finds them
    /// </summary>
    private static List<SbcProcess> Descendants(Dictionary<int, SbcProcess> processes, int pid)
    {
        List<SbcProcess> result = [];
        Queue<int> parents = new([pid]);
        while (parents.TryDequeue(out int parent))
        {
            foreach (SbcProcess child in processes.Values.Where(process => process.ParentPid == parent && process.Pid != parent))
            {
                result.Add(child);
                parents.Enqueue(child.Pid);
            }
        }
        return result;
    }

    /// <summary>
    /// StopPlugin left the child processes of a plugin running: they are re-parented when the plugin exits, so Process.Kill(true) after the SIGTERM no
    /// longer finds them, and Terminate failed with ESRCH when the plugin had gone already (DSF #151, fix 9). Fixed: DPS collects the descendants before it
    /// sends SIGTERM and kills those still there afterwards, logging "Left-over child process {Pid} killed" (DuetPluginService Commands/StopPlugin.cs:62-91,
    /// DuetSharedLibrary ProcessHelpers.cs:32-43, :127). The plugin service keeps running, so its control group does not clean up (KillMode=control-group of the
    /// unit acts only when the service stops).
    /// The test reads the processes from /proc first and picks a running SBC plugin that has child processes and that no running plugin depends on
    /// (DCS refuses that, Commands/Plugins/StopPlugin.cs:41-50), CHX350 only if no other one qualifies. It records the children with their start times,
    /// stops the plugin with POST /machine/stopPlugin (DuetWebServer MachineEndpoints.cs:52, :684-706), checks that none of them is left after 10 s and starts the
    /// plugin again with POST /machine/startPlugin. Both requests save the list of started plugins (SaveState, DuetAPI Commands/Plugins/StopPlugin.cs:19), so
    /// plugins.txt holds the same plugins afterwards, perhaps in another order.
    /// Unfixed: the children keep running, re-parented. This catches a regression only if a child outlives the SIGTERM of its plugin; the log line shows
    /// whether this run had such a child. If no plugin has children the test is ignored. 0 restarts, the plugin is down for a few seconds.
    /// The plugin services come with the image, #151 is in 3.7.0+mp.14 and later
    /// </summary>
    [Test]
    public async Task StoppedPluginLeavesNoChildProcesses()
    {
        (string Id, int Pid, bool HasDependents)[] running = Lab.Read(Session, model => model.Plugins.Values
            .Where(plugin => !string.IsNullOrEmpty(plugin.SbcExecutable) && plugin.Pid > 0)
            .Select(plugin => (plugin.Id, plugin.Pid, model.Plugins.Values.Any(other => other.Id != plugin.Id && other.Pid > 0 && other.SbcPluginDependencies.Contains(plugin.Id))))
            .ToArray());
        Dictionary<int, SbcProcess> processes = await ReadProcessesAsync();
        (string Id, int Pid, List<SbcProcess> Children)[] candidates = [.. running
            .Where(plugin => !plugin.HasDependents && processes.ContainsKey(plugin.Pid))
            .Select(plugin => (plugin.Id, plugin.Pid, Children: Descendants(processes, plugin.Pid)))
            .Where(plugin => plugin.Children.Count > 0)
            .OrderBy(plugin => plugin.Id == HmiPlugin)];
        TestContext.Progress.WriteLine($"SBC plugins: {string.Join(", ", running.Select(plugin => $"{plugin.Id} (pid {plugin.Pid}, " +
            $"{(processes.ContainsKey(plugin.Pid) ? Descendants(processes, plugin.Pid).Count : 0)} child processes{(plugin.HasDependents ? ", others depend on it" : "")})"))}");
        if (candidates.Length == 0)
        {
            Assert.Ignore("No running SBC plugin has child processes, so a stop cannot leave any (#151)");
        }

        (string id, int pid, List<SbcProcess> children) = candidates[0];
        string cursor = await Lab.GetJournalCursorAsync();
        HttpStatusCode stopStatus, startStatus;
        string stopBody, startBody;
        SbcProcess[] left = [];
        bool restarted;
        try
        {
            (stopStatus, stopBody) = await RestartHelpers.PostMachineAsync("stopPlugin", id);
            for (int i = 0; i < 20; i++)
            {
                Dictionary<int, SbcProcess> after = await ReadProcessesAsync();
                left = [.. children.Where(child => after.TryGetValue(child.Pid, out SbcProcess process) && process.StartTime == child.StartTime && process.State != "Z")];
                if (left.Length == 0)
                {
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(0.5));
            }
        }
        finally
        {
            (startStatus, startBody) = await RestartHelpers.PostMachineAsync("startPlugin", id);
            restarted = await Lab.TryWaitForModelAsync(Session, model => model.Plugins.TryGetValue(id, out Plugin? plugin) && plugin.Pid > 0 && plugin.Started, TimeSpan.FromMinutes(2));
        }

        string journal = await Lab.ReadJournalAsync(cursor, "duetpluginservice", "duetpluginservice-root");
        int killed = journal.Split('\n').Count(line => line.Contains("Left-over child process"));
        TestContext.Progress.WriteLine($"Stopped {id} (pid {pid}) with {children.Count} child processes ({string.Join(", ", children.Select(child => child.Pid))}): " +
            $"stop {(int)stopStatus} '{stopBody}', start {(int)startStatus} '{startBody}', {left.Length} left, {killed} killed by the plugin service");

        Assert.Multiple(() =>
        {
            Assert.That(stopStatus, Is.EqualTo(HttpStatusCode.NoContent), $"POST /machine/stopPlugin {id}: {stopBody}");
            Assert.That(left.Select(child => child.Pid), Is.Empty, $"Child processes of {id} still run after it was stopped (#151)");
            Assert.That(startStatus, Is.EqualTo(HttpStatusCode.NoContent), $"POST /machine/startPlugin {id}: {startBody}");
            Assert.That(restarted, Is.True, $"{id} did not run again within 2 minutes");
        });
    }
}
