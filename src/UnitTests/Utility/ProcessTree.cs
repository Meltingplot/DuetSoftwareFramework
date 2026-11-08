using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using DuetSharedLibrary;
using NUnit.Framework;

namespace UnitTests.Utility;

/// <summary>
/// Process tree helpers of DuetSharedLibrary/ProcessHelpers.cs that StopPlugin of DuetPluginService uses for the child processes of a plugin
/// </summary>
[TestFixture]
public class ProcessTree
{
    [Test]
    public void DescendantsAreFoundAndKilledOnlyWithTheirStartTime()
    {
        using Process shell = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "sleep 30 & wait"]))!;
        try
        {
            (int Pid, ulong StartTime) sleep = default;
            Assert.That(SpinWait.SpinUntil(() =>
            {
                sleep = ProcessHelpers.GetDescendants(shell.Id).FirstOrDefault();
                return sleep.Pid != 0;
            }, TimeSpan.FromSeconds(5)), Is.True, "The child of the shell was not found");
            Assert.That(ProcessHelpers.GetDescendants(Environment.ProcessId).Select(item => item.Pid), Does.Contain(shell.Id).And.Contain(sleep.Pid), "Grandchildren are descendants too");
            Assert.That(ProcessHelpers.GetParentPid(sleep.Pid), Is.EqualTo(shell.Id));

            // A PID with another start time belongs to another process by now
            Assert.That(ProcessHelpers.Kill(sleep.Pid, sleep.StartTime + 1), Is.False);
            Assert.That(ProcessHelpers.GetParentPid(sleep.Pid), Is.EqualTo(shell.Id), "The process was killed");

            Assert.That(ProcessHelpers.Kill(sleep.Pid, sleep.StartTime), Is.True);
            Assert.That(shell.WaitForExit(5000), Is.True, "The shell did not see its child exit");
            Assert.That(ProcessHelpers.Kill(sleep.Pid, sleep.StartTime), Is.False, "A process that is gone cannot be killed");
        }
        finally
        {
            shell.Kill(true);
        }
    }

    [Test]
    public void ProcessThatHasGoneIsTerminatedWithoutError()
    {
        // StopPlugin checks HasExited and sends SIGTERM afterwards, the plugin may exit in between
        using Process process = Process.Start(new ProcessStartInfo("/bin/true"))!;
        process.WaitForExit();
        Assert.DoesNotThrow(process.Terminate);
    }

    [Test]
    public void ProcessThatDoesNotExistHasNoDescendants()
    {
        Assert.That(ProcessHelpers.GetDescendants(int.MaxValue), Is.Empty);
        Assert.That(ProcessHelpers.GetParentPid(int.MaxValue), Is.Zero);
    }
}
