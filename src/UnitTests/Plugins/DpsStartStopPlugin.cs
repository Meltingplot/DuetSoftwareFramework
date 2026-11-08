using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace UnitTests.Plugins;

using DcsPluginService = DuetControlServer.IPC.Processors.PluginService;
using File = System.IO.File;

/// <summary>
/// Plugin processes of the DuetPluginService instance that runs as dsf user: StartPlugin (DuetPluginService/Commands/StartPlugin.cs), StopPlugin
/// (Commands/StopPlugin.cs), ResolvePluginProcess (Commands/ResolvePluginProcess.cs) and the shutdown of the service (PluginService.StopAsync).
/// The plugins are shell scripts in a temporary plugin directory and run as the user of the test. DCS gets the PIDs via SetPluginProcess and the
/// plugin output via WriteMessage on the command connections of DPS
/// </summary>
[TestFixture]
public class DpsStartStopPlugin : DpsTestBase
{
    private async Task<Plugin> AddPluginAsync(string id, string? script, Action<Plugin>? configure = null, bool profile = true, bool inDcs = true)
    {
        Plugin plugin = new() { Id = id, Name = id, SbcExecutable = "plugin.sh", Pid = -1 };
        configure?.Invoke(plugin);
        Dps!.WritePlugin(plugin, script, profile);
        await Dps.AddToStoreAsync(plugin);
        if (inDcs)
        {
            await AddToDcsAsync(plugin);
        }
        return plugin;
    }

    private async Task StartAsync(string id)
    {
        DuetPluginService.IPC.StartPlugin command = Dps!.Create<DuetPluginService.IPC.StartPlugin>();
        command.Plugin = id;
        await command.ExecuteAsync();
    }

    private async Task StopAsync(string id)
    {
        DuetPluginService.IPC.StopPlugin command = Dps!.Create<DuetPluginService.IPC.StopPlugin>();
        command.Plugin = id;
        await command.ExecuteAsync();
    }

    private async Task<bool> WaitForStoppedAsync(Plugin plugin) => await DpsHost.WaitForAsync(async () =>
    {
        using (await Dps!.Store.LockAsync(default))
        {
            if (plugin.Pid != -1 || Dps.Store.Processes.ContainsKey(plugin.Id))
            {
                return false;
            }
        }
        return (await GetFromDcsAsync(plugin.Id))?.Pid == -1;
    });

    [Test]
    public async Task PluginIsStartedAndStopped()
    {
        await StartDpsAsync(register: false);
        Plugin plugin = await AddPluginAsync("Runner", "echo \"started in $(pwd)\"\necho \"socket $DSF_SOCKET_PATH\"\necho problem >&2\nexec sleep 30");
        await StartAsync("Runner");

        // The process is tracked and DCS got its PID before StartPlugin returned (StartPlugin.cs:147-155)
        int pid = Dps!.Store.Processes["Runner"].Id;
        Assert.That(pid, Is.GreaterThan(0));
        Assert.That(plugin.Pid, Is.EqualTo(pid));
        Assert.That((await GetFromDcsAsync("Runner"))!.Pid, Is.EqualTo(pid));

        // It runs in its dsf directory with the socket of DCS in DSF_SOCKET_PATH (StartPlugin.cs:126-130), and stdout/stderr become messages of DCS
        // prefixed with the plugin id (StartPlugin.cs:137-145, 209-231)
        Assert.That(await WaitForDcsMessageAsync($"[Runner]: started in {Path.Combine(PluginDirectory, "Runner", "dsf")}"), Is.True);
        Assert.That(await WaitForDcsMessageAsync($"[Runner]: socket {Dps.Settings.SocketPath}"), Is.True);
        Assert.That(await WaitForDcsMessageAsync("[Runner]: problem"), Is.True);
        Assert.That(DcsLog.Entries.Single(entry => entry.Message == "[Runner]: problem").Level, Is.EqualTo(LogLevel.Error), "stderr is an error message");
        Assert.That(DcsLog.Entries.Single(entry => entry.Message.StartsWith("[Runner]: started")).Level, Is.EqualTo(LogLevel.Information));

        // SIGTERM stops it, the PID is reset in DPS and DCS and the process is forgotten (StopPlugin.cs:56-71, StartPlugin.cs:157-199)
        await StopAsync("Runner");
        Assert.That(Dps.Log.Contains("Process stopped by SIGTERM"), Is.True);
        Assert.That(await WaitForStoppedAsync(plugin), Is.True);
    }

    [Test]
    public async Task ExitOfThePluginIsReported()
    {
        await StartDpsAsync(register: false);
        Plugin plugin = await AddPluginAsync("Exiting", "exit 3");
        await StartAsync("Exiting");
        Assert.That(await WaitForStoppedAsync(plugin), Is.True);
        Assert.That(Dps!.Log.Contains("Process stopped with exit code 3"), Is.True);

        // It can be started again
        await StartAsync("Exiting");
        Assert.That(await WaitForStoppedAsync(plugin), Is.True);
        Assert.That(Dps.Log.Entries.Count(entry => entry.Message.Contains("Process stopped with exit code 3")), Is.EqualTo(2));
    }

    [Test]
    public async Task PluginIsStartedOnlyOnce()
    {
        await StartDpsAsync(register: false);
        await AddPluginAsync("Once", "exec sleep 30");
        await StartAsync("Once");
        int pid = Dps!.Store.Processes["Once"].Id;
        await StartAsync("Once");
        Assert.That(Dps.Store.Processes["Once"].Id, Is.EqualTo(pid));
        Assert.That(Dps.Log.Entries.Count(entry => entry.Message.StartsWith("Launching")), Is.EqualTo(1));
    }

    [Test]
    public async Task PluginThatIgnoresSigtermIsKilled()
    {
        // After StopTimeout the process tree is killed (StopPlugin.cs:65-77)
        await StartDpsAsync(settings => settings.StopTimeout = 300, register: false);
        Plugin plugin = await AddPluginAsync("Stubborn", "trap '' TERM\necho ready\nwhile true; do sleep 0.1; done");
        await StartAsync("Stubborn");
        Assert.That(await WaitForDcsMessageAsync("[Stubborn]: ready"), Is.True);
        await StopAsync("Stubborn");
        Assert.That(Dps!.Log.Contains("Process killed"), Is.True);
        Assert.That(await WaitForStoppedAsync(plugin), Is.True);
    }

    [Test]
    public async Task StoppingAPluginThatIsNotRunningDoesNothing()
    {
        await StartDpsAsync(register: false);
        await AddPluginAsync("Idle", "exec sleep 30");
        await StopAsync("Idle");
        Assert.That(Dps!.Log.Contains("Attempting to stop process"), Is.False);
    }

    [Test]
    public async Task UnknownPluginsAreRefused()
    {
        await StartDpsAsync(register: false);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await StartAsync("Unknown")))!;
        Assert.That(e.Message, Is.EqualTo("Plugin Unknown not found by service"));
        e = (await Assert.ThrowsAsync<ArgumentException>(async () => await StopAsync("Unknown")))!;
        Assert.That(e.Message, Is.EqualTo("Plugin Unknown not found by service"));
    }

    [Test]
    public async Task RootPluginsAreLeftToTheRootService()
    {
        // The service starts and stops only the plugins whose SuperUser flag matches its own privileges (StartPlugin.cs:57-61, StopPlugin.cs:50-54)
        await StartDpsAsync(register: false);
        await AddPluginAsync("Root", "exec sleep 30", plugin => plugin.SbcPermissions = SbcPermissions.SuperUser | SbcPermissions.CommandExecution);
        InvalidOperationException e = (await Assert.ThrowsAsync<InvalidOperationException>(async () => await StartAsync("Root")))!;
        Assert.That(e.Message, Is.EqualTo("Wrong plugin service to start this plugin"));
        Assert.That(Dps!.Store.Processes, Is.Empty);

        // StopPlugin uses the same message (StopPlugin.cs:53)
        e = (await Assert.ThrowsAsync<InvalidOperationException>(async () => await StopAsync("Root")))!;
        Assert.That(e.Message, Is.EqualTo("Wrong plugin service to start this plugin"));
    }

    [Test]
    public async Task PluginWithoutProfileIsNotStarted()
    {
        // StartPlugin.cs:63-72: a plugin must not run unconfined because its profile is missing
        await StartDpsAsync(register: false);
        await AddPluginAsync("Unconfined", "exec sleep 30", profile: false);
        InvalidOperationException e = (await Assert.ThrowsAsync<InvalidOperationException>(async () => await StartAsync("Unconfined")))!;
        Assert.That(e.Message, Is.EqualTo($"AppArmor profile for plugin Unconfined is missing at {Dps!.ProfilePath("Unconfined")}; refusing to start"));
        Assert.That(Dps.Store.Processes, Is.Empty);
    }

    [Test]
    public async Task PluginWithoutProfileIsStartedWhenAppArmorIsDisabled()
    {
        await StartDpsAsync(settings => settings.DisableAppArmor = true, register: false);
        await AddPluginAsync("NoAppArmor", "exec sleep 30", profile: false);
        await StartAsync("NoAppArmor");
        Assert.That(Dps!.Store.Processes.ContainsKey("NoAppArmor"), Is.True);
    }

    [Test]
    public async Task MissingExecutableIsReported()
    {
        await StartDpsAsync(register: false);
        await AddPluginAsync("NoExe", null);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await StartAsync("NoExe")))!;
        Assert.That(e.Message, Is.EqualTo($"Cannot find executable {Path.Combine(PluginDirectory, "NoExe", "dsf", "plugin.sh")}"));
    }

    [Test]
    public async Task SymlinkedExecutableIsNotStarted()
    {
        // StartPlugin.cs:94-100: the plugin may write its own directory, a symlink to another binary would escape its profile
        await StartDpsAsync(register: false);
        await AddPluginAsync("Linked", null);
        string executable = Path.Combine(PluginDirectory, "Linked", "dsf", "plugin.sh");
        File.CreateSymbolicLink(executable, "/bin/sh");
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await StartAsync("Linked")))!;
        Assert.That(e.Message, Is.EqualTo($"Refusing to launch plugin Linked: executable {executable} is a symlink"));
        Assert.That(Dps!.Store.Processes, Is.Empty);
    }

    [Test]
    public async Task ExecutableOfTheArchitectureComesFirst()
    {
        await StartDpsAsync(register: false);
        await AddPluginAsync("Arch", "echo generic\nexec sleep 30");
        string archExecutable = Path.Combine(PluginDirectory, "Arch", "dsf", Architecture, "plugin.sh");
        Directory.CreateDirectory(Path.GetDirectoryName(archExecutable)!);
        File.WriteAllText(archExecutable, "#!/bin/sh\necho \"arch in $(pwd)\"\nexec sleep 30\n");
        File.SetUnixFileMode(archExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await StartAsync("Arch");
        Assert.That(await WaitForDcsMessageAsync($"[Arch]: arch in {Path.GetDirectoryName(archExecutable)}"), Is.True);
        Assert.That(DcsLog.Entries.Any(entry => entry.Message == "[Arch]: generic"), Is.False);
    }

    [Test]
    public async Task ArgumentsArePassed()
    {
        await StartDpsAsync(register: false);
        await AddPluginAsync("Args", "echo \"$#:$1:$2\"\nexec sleep 30", plugin => plugin.SbcExecutableArguments = "--port 8080");
        await StartAsync("Args");
        Assert.That(await WaitForDcsMessageAsync("[Args]: 2:--port:8080"), Is.True);
    }

    [Test]
    public async Task OutputIsNotForwardedUnlessRedirected()
    {
        await StartDpsAsync(register: false);
        Plugin plugin = await AddPluginAsync("Quiet", "echo quiet", plugin => plugin.SbcOutputRedirected = false);
        await StartAsync("Quiet");
        Assert.That(await WaitForStoppedAsync(plugin), Is.True);
        Assert.That(DcsLog.Entries.Any(entry => entry.Message.Contains("quiet")), Is.False);
    }

    [Test]
    public async Task OutputIsDroppedWhileDcsIsUnreachable()
    {
        // Every output line opens a new connection to DCS (StartPlugin.cs:218-229); without DCS the line is dropped with a warning, as seen in the
        // journal of the lab printer while DCS was stopped
        await StartDpsAsync(register: false);
        await AddPluginAsync("Talker", "sleep 1\necho late\nexec sleep 30");
        await StartAsync("Talker");
        await Dps!.Bridge!.DisposeAsync();
        Assert.That(await WaitForAsync(() => Dps.Log.Contains("Failed to send console message to DCS")), Is.True);
        Assert.That(Dps.Store.Processes.ContainsKey("Talker"), Is.True, "The plugin keeps running");
    }

    [Test]
    public async Task PythonPluginIsLaunchedThroughItsVirtualEnvironment()
    {
        // PythonLaunchCommand and PythonLaunchArguments (Settings.cs:112-117, defaults /bin/bash -c "{pluginDir}/venv/bin/python {command} {args}"), StartPlugin.cs:119-125
        await StartDpsAsync(register: false);
        Plugin plugin = await AddPluginAsync("Python", "print()", plugin =>
        {
            plugin.SbcExecutable = "main.py";
            plugin.SbcExecutableArguments = "--flag value";
            plugin.SbcPythonDependencies.Add("requests");
        });
        string python = Path.Combine(PluginDirectory, "Python", "venv", "bin", "python");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "#!/bin/sh\necho \"python $*\"\n");
        File.SetUnixFileMode(python, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await StartAsync("Python");
        Assert.That(await WaitForDcsMessageAsync($"[Python]: python {Path.Combine(PluginDirectory, "Python", "dsf", "main.py")} --flag value"), Is.True);
        Assert.That(Dps!.Log.Contains($"Launching /bin/bash -c \"{Path.Combine(PluginDirectory, "Python")}/venv/bin/python"), Is.True);
        Assert.That(await WaitForStoppedAsync(plugin), Is.True);
    }

    [Test]
    public async Task PythonPluginWithShellMetacharactersInItsPathIsNotStarted()
    {
        // StartPlugin.cs:102-108
        await StartDpsAsync(register: false);
        await AddPluginAsync("Spaced", "print()", plugin =>
        {
            plugin.SbcExecutable = "my main.py";
            plugin.SbcPythonDependencies.Add("requests");
        });
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await StartAsync("Spaced")))!;
        Assert.That(e.Message, Is.EqualTo("Refusing to launch plugin Spaced: executable path contains shell metacharacters"));
    }

    [Test]
    public async Task ChildProcessesAreResolvedToTheirPlugin()
    {
        // ResolvePluginProcess walks up the process tree (ResolvePluginProcess.cs:21-37), DCS asks for it while a plugin is starting
        // (DuetControlServer/IPC/Connection.cs:109-128). The result goes through DCS like there
        await StartDpsAsync();
        await AddPluginAsync("Tree", "sleep 30 &\necho \"child $!\"\nwait");
        await StartAsync("Tree");
        Assert.That(await WaitForDcsMessageAsync("[Tree]: child "), Is.True);
        int child = int.Parse(DcsLog.Entries.First(entry => entry.Message.StartsWith("[Tree]: child ")).Message["[Tree]: child ".Length..]);
        int pid = Dps!.Store.Processes["Tree"].Id;

        async Task<string?> ResolveAsync(int processId) =>
            await DcsPluginService.PerformCommandAsync<string>(new DuetAPI.Commands.ResolvePluginProcess { Pid = processId }, false).WaitAsync(UnitTests.FakeFirmware.FakeFirmwareHost.DefaultTimeout);

        Assert.That(await ResolveAsync(child), Is.EqualTo("Tree"));
        Assert.That(await ResolveAsync(pid), Is.EqualTo("Tree"));
        Assert.That(await ResolveAsync(Environment.ProcessId), Is.Null);
        Assert.That(await ResolveAsync(1), Is.Null);
        Assert.That(await ResolveAsync(int.MaxValue), Is.Null, "A process that does not exist has no parent (ProcessHelpers.GetParentPid returns 0)");

        // Let the plugin end by itself, see ChildProcessesAreStoppedWithThePlugin
        System.Diagnostics.Process.GetProcessById(child).Kill();
    }

    /// <summary>
    /// StopPlugin sends SIGTERM to the plugin process only (ProcessHelpers.Terminate), so the plugin can stop its children itself. A shell plugin that waits for
    /// a child exits on SIGTERM and its child is re-parented, so Kill(true) no longer finds it below the exited plugin. StopPlugin looks the children up before
    /// SIGTERM and kills the ones that are still left afterwards (StopPlugin.cs). With redirected output the child holds the pipes, so WaitForExitAsync returns
    /// only after StopTimeout
    /// </summary>
    [Test]
    public async Task ChildProcessesAreStoppedWithThePlugin()
    {
        await StartDpsAsync(settings => settings.StopTimeout = 500, register: false);
        await AddPluginAsync("Parent", "sleep 30 &\necho \"child $!\"\nwait");
        await StartAsync("Parent");
        Assert.That(await WaitForDcsMessageAsync("[Parent]: child "), Is.True);
        int child = int.Parse(DcsLog.Entries.First(entry => entry.Message.StartsWith("[Parent]: child ")).Message["[Parent]: child ".Length..]);
        try
        {
            await StopAsync("Parent");
            Assert.That(await WaitForAsync(() => !File.Exists($"/proc/{child}/stat"), TimeSpan.FromSeconds(2)), Is.True, $"Child process {child} still runs");
            Assert.That(Dps!.Log.Contains($"Left-over child process {child} killed"), Is.True);
        }
        finally
        {
            try
            {
                System.Diagnostics.Process.GetProcessById(child).Kill();
            }
            catch (ArgumentException)
            {
                // Not running
            }
        }
    }

    [Test]
    public async Task ServiceStopStopsThePlugins()
    {
        // PluginService.StopAsync stops every plugin with a process (PluginService.cs:120-138)
        await StartDpsAsync(register: false);
        await AddPluginAsync("First", "exec sleep 30");
        await AddPluginAsync("Second", "exec sleep 30");
        await StartAsync("First");
        await StartAsync("Second");
        int[] pids = [Dps!.Store.Processes["First"].Id, Dps.Store.Processes["Second"].Id];

        await Dps.DisposeAsync();
        Assert.That(Dps.Log.Entries.Count(entry => entry.Message == "Process stopped by SIGTERM"), Is.EqualTo(2));
        Assert.That(Dps.Log.Contains("Plugins stopped"), Is.True);
        foreach (int pid in pids)
        {
            Assert.That(await WaitForAsync(() => !File.Exists($"/proc/{pid}/stat") || File.ReadAllText($"/proc/{pid}/stat").Split(' ')[2] == "Z"), Is.True, $"Process {pid} still runs");
        }
    }

    /// <summary>
    /// SbcExecutableArguments of a Python plugin go into the command string of bash -c (Settings.cs:117, StartPlugin.cs:119-125). InstallPlugin
    /// refuses the characters that end the double quotes of that string (InstallPlugin.cs:300-306: ", $, `, \, newline), but bash runs the whole string as a
    /// command line, so ;, |, &amp;, &gt; and &lt; still start other commands. bash and anything it starts besides the plugin are not below the plugin
    /// directory, which the AppArmor profile attaches to (apparmor.conf:4), so whether they are confined could not be checked here. The same holds for an
    /// SbcExecutable with ; in its name, StartPlugin.cs:105 checks only ", $, `, \, newline and space
    /// </summary>
    [Test]
    [Explicit("Bug: ; in SbcExecutableArguments of a Python plugin runs a second command via bash -c")]
    public async Task PythonPluginArgumentsDoNotRunShellCommands()
    {
        await StartDpsAsync(register: false);
        string marker = OutsidePath("injected");
        string package = PluginPackage.Create(OutsidePath("injection.zip"), new JsonObject
        {
            ["id"] = "Injection",
            ["name"] = "Injection",
            ["sbcExecutable"] = "main.py",
            ["sbcExecutableArguments"] = $"--flag; touch {marker}",
            ["sbcPythonDependencies"] = new JsonArray("requests")
        }.ToJsonString(), ("dsf/main.py", "print()"));

        // The arguments pass the check of InstallPlugin
        DuetPluginService.IPC.InstallPlugin install = Dps!.Create<DuetPluginService.IPC.InstallPlugin>();
        install.PluginFile = package;
        await install.ExecuteAsync();
        Plugin plugin = (await Dps.GetPluginAsync("Injection"))!;
        await AddToDcsAsync(plugin);
        File.WriteAllText(Dps.ProfilePath("Injection"), "# profile");
        string python = Path.Combine(PluginDirectory, "Injection", "venv", "bin", "python");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(python, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await StartAsync("Injection");
        Assert.That(await WaitForStoppedAsync(plugin), Is.True);
        Assert.That(File.Exists(marker), Is.False, "The arguments started another command");
    }

    /// <summary>
    /// StartPlugin reports the PID to DCS. If SetPluginProcess fails, e.g. because DCS does not know the plugin (DuetControlServer/Commands/Plugins/SetPluginProcess.cs
    /// throws "Plugin ... not found"), DCS cannot track or stop the process, so StartPlugin kills it again. The task that waits for the exit is set up before, so it
    /// forgets the process and the next StartPlugin starts the plugin again
    /// </summary>
    [Test]
    public async Task FailedRegistrationAtDcsDoesNotBlockTheNextStart()
    {
        await StartDpsAsync(register: false);
        Plugin plugin = await AddPluginAsync("Unknown2Dcs", "exec sleep 30", inDcs: false);
        await Assert.ThrowsAsync<InternalServerException>(async () => await StartAsync("Unknown2Dcs"));
        Assert.That(Dps!.Log.Contains("Failed to report the process to DCS, killing it"), Is.True);
        Assert.That(await DpsHost.WaitForAsync(async () =>
        {
            using (await Dps.Store.LockAsync(default))
            {
                return !Dps.Store.Processes.ContainsKey("Unknown2Dcs") && plugin.Pid == -1;
            }
        }, TimeSpan.FromSeconds(5)), Is.True, "The exited process is still registered");

        await AddToDcsAsync(plugin);
        await StartAsync("Unknown2Dcs");
        Assert.That((await GetFromDcsAsync("Unknown2Dcs"))!.Pid, Is.EqualTo(Dps.Store.Processes["Unknown2Dcs"].Id));
    }

    [Test]
    public async Task ExitedProcessIsForgottenBeforeDcsIsTold()
    {
        // The plugin can be started again as soon as DCS knows that it has stopped
        await StartDpsAsync(register: false);
        Plugin plugin = await AddPluginAsync("Quick", "exit 0");
        for (int i = 0; i < 3; i++)
        {
            await StartAsync("Quick");
            Assert.That(await DpsHost.WaitForAsync(async () => (await GetFromDcsAsync("Quick"))!.Pid == -1), Is.True);
            using (await Dps!.Store.LockAsync(default))
            {
                Assert.That(Dps.Store.Processes.ContainsKey("Quick"), Is.False);
            }
        }
        Assert.That(Dps!.Log.Entries.Count(entry => entry.Message.StartsWith("Launching")), Is.EqualTo(3));
    }
}
