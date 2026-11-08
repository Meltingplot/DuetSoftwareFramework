using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer;
using DuetSharedLibrary;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Startup;

/// <summary>
/// DuetControlServer as a process, for what Program.cs does around the host: the options (Program.cs:58-91), the start error file and the exit codes
/// (Program.cs:24-56, 203-207, 244-253) and the update-only mode (Program.cs:102-106, 209-226). There is no hardware, so a normal start always ends with the
/// link adapter failing; every config file points the devices to paths that do not exist, so the process never reaches real hardware either
/// </summary>
[TestFixture]
public class DcsProcess
{
    /// <summary>
    /// Maximum time a run may take, including the start of the runtime and the instrumentation of a coverage run
    /// </summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    private string _directory = null!;

    [SetUp]
    public void CreateDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dsf-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_directory, "run"));
        Directory.CreateDirectory(Path.Combine(_directory, "sd"));
    }

    [TearDown]
    public void DeleteDirectory() => Directory.Delete(_directory, true);

    private string StartErrorFile => Path.Combine(_directory, "run", "dcs.err");

    private string SocketFile => Path.Combine(_directory, "run", "dcs.sock");

    private string GpioChipDevice => Path.Combine(_directory, "no-gpiochip");

    /// <summary>
    /// Write a config file whose paths all lie in the test directory
    /// </summary>
    private string WriteConfig(Action<JsonObject>? configure = null)
    {
        JsonObject config = new()
        {
            [nameof(Settings.BaseDirectory)] = Path.Combine(_directory, "sd"),
            [nameof(Settings.SocketDirectory)] = Path.Combine(_directory, "run"),
            [nameof(Settings.StartErrorFile)] = StartErrorFile,
            [nameof(Settings.PluginDirectory)] = Path.Combine(_directory, "plugins"),
            [nameof(Settings.PluginsFilename)] = Path.Combine(_directory, "plugins.txt"),
            [nameof(Settings.SpiDevice)] = Path.Combine(_directory, "no-spidev"),
            [nameof(Settings.GpioChipDevice)] = GpioChipDevice,
            [nameof(Settings.UsbDevice)] = Path.Combine(_directory, "no-tty")
        };
        configure?.Invoke(config);
        string configFile = Path.Combine(_directory, "config.json");
        System.IO.File.WriteAllText(configFile, config.ToJsonString());
        return configFile;
    }

    /// <summary>
    /// DuetControlServer.dll in the output directory of its project. The copy next to the tests cannot run on its own, because the tests take the
    /// Microsoft.Extensions assemblies from the ASP.NET Core framework
    /// </summary>
    private static string DcsAssembly
    {
        get
        {
            DirectoryInfo testDirectory = new(TestContext.CurrentContext.TestDirectory);
            string configuration = testDirectory.Parent!.Name, framework = testDirectory.Name;
            return Path.GetFullPath(Path.Combine(testDirectory.FullName, "..", "..", "..", "..", "DuetControlServer", "bin", configuration, framework, "DuetControlServer.dll"));
        }
    }

    /// <summary>
    /// Run DuetControlServer with the given arguments until it exits
    /// </summary>
    /// <returns>Exit code and the output of stdout and stderr</returns>
    private async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        Assert.That(System.IO.File.Exists(DcsAssembly), Is.True, $"{DcsAssembly} not found");
        string dotnet = (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") ? Environment.ProcessPath! : "dotnet";
        ProcessStartInfo startInfo = new(dotnet)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _directory
        };
        startInfo.ArgumentList.Add(DcsAssembly);
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        startInfo.Environment["DOTNET_GCHeapHardLimit"] = "0x80000000";

        using Process process = Process.Start(startInfo)!;
        process.StandardInput.Close();      // FirmwareUpdater asks for confirmation unless the input is redirected (FirmwareUpdater.cs:336-340)
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource cts = new(RunTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail($"DuetControlServer did not exit within {RunTimeout}:\n{await output}{await error}");
        }
        return (process.ExitCode, await output + await error);
    }

    [Test]
    public async Task HelpListsTheOptions()
    {
        (int exitCode, string output) = await RunAsync("--help");
        Assert.That(exitCode, Is.EqualTo(ExitCode.Success));
        foreach (string option in new[] { "-u, --update", "-c, --config", "-l, --log-level", "-S, --socket-directory", "-s, --socket-file", "-b, --base-directory" })
        {
            Assert.That(output, Does.Contain(option));
        }
    }

    /// <summary>
    /// The host resolves every hosted service before it starts the first one, so the SPI adapter fails in its constructor (SPI.cs:108-110) before the IPC
    /// server has created its socket. host.Run() has disposed the logger factory by then, so Terminate writes the reason to stderr (Program.cs:35-45),
    /// saves it in the start error file and exits with OsError (Program.cs:246-253)
    /// </summary>
    [Test]
    public async Task StartFailureIsSavedInTheStartErrorFile()
    {
        (int exitCode, string output) = await RunAsync("-c", WriteConfig());

        Assert.That(exitCode, Is.EqualTo(ExitCode.OsError), output);
        Assert.That(output, Does.Contain("Duet Control Server v"), "Banner of the regular mode (Program.cs:113-117)");
        Assert.That(output, Does.Contain("Hosting failed to start"), "Error of the host at the default log level");
        Assert.That(output, Does.Contain("[fatal] Failed to start: "));
        Assert.That(System.IO.File.ReadAllText(StartErrorFile), Does.StartWith("Failed to start: ").And.Contain(GpioChipDevice));
        Assert.That(System.IO.File.Exists(SocketFile), Is.False);
    }

    /// <summary>
    /// A start error file in a directory that does not exist, as /run/dsf/dcs.err without the tmpfiles.d entry of the package
    /// (pkg/common/duetcontrolserver/usr/lib/tmpfiles.d/duetcontrolserver.conf): Terminate cannot save the reason, says so on stderr and still exits with
    /// the exit code of the failure (Program.cs:46-55). Before, the exception of File.WriteAllText left Terminate, and the process ended with its own exit code
    /// </summary>
    [Test]
    public async Task StartFailureIsReportedWithoutTheDirectoryOfTheStartErrorFile()
    {
        string startErrorFile = Path.Combine(_directory, "missing", "dcs.err");
        (int exitCode, string output) = await RunAsync("-c", WriteConfig(config => config[nameof(Settings.StartErrorFile)] = startErrorFile));

        Assert.That(exitCode, Is.EqualTo(ExitCode.OsError), output);
        Assert.That(output, Does.Contain("[fatal] Failed to start: "));
        Assert.That(output, Does.Contain($"Failed to write the start error file {startErrorFile}"));
        Assert.That(System.IO.File.Exists(startErrorFile), Is.False);
    }

    /// <summary>
    /// -l sets the level of the log (Program.cs:97, 135-159); the reason of a start failure still goes to stderr
    /// </summary>
    [Test]
    public async Task LogLevelOptionFiltersTheLog()
    {
        (int exitCode, string output) = await RunAsync("-c", WriteConfig(), "-l", "fatal");

        Assert.That(exitCode, Is.EqualTo(ExitCode.OsError), output);
        Assert.That(output, Does.Not.Contain("Hosting failed to start"));
        Assert.That(output, Does.Contain("[fatal] Failed to start: "));
    }

    /// <summary>
    /// -u asks a running DCS for the object model and updates the boards through it (FirmwareUpdater.TryRemoteFirmwareUpdateAsync), then exits
    /// (Program.cs:210-220). The config file points to another socket, so the update reaches the running instance only through -s
    /// </summary>
    [Test]
    public async Task UpdateOnlyGoesThroughTheRunningInstance()
    {
        await using FakeFirmwareHost running = await FakeFirmwareHost.StartAsync(ipc: true, configureFirmware: firmware => firmware.ObjectModel.Set("boards[0].firmwareFileName", ""));
        (int exitCode, string output) = await RunAsync("-u", "-c", WriteConfig(), "-s", running.SocketPath!);

        Assert.That(exitCode, Is.EqualTo(ExitCode.Success), output);
        Assert.That(output, Does.Contain("All boards are up-to-date!"));
        Assert.That(output, Does.Not.Contain("Duet Control Server v"), "No banner in update-only mode (Program.cs:102-106)");
        Assert.That(System.IO.File.Exists(StartErrorFile), Is.False);
    }

    /// <summary>
    /// -S sets the directory of the socket, whose name stays dcs.sock
    /// </summary>
    [Test]
    public async Task SocketDirectoryOptionIsUsed()
    {
        await using FakeFirmwareHost running = await FakeFirmwareHost.StartAsync(ipc: true, configureFirmware: firmware => firmware.ObjectModel.Set("boards[0].firmwareFileName", ""));
        Assert.That(Path.GetFileName(running.SocketPath), Is.EqualTo("dcs.sock"));
        (int exitCode, string output) = await RunAsync("-u", "-c", WriteConfig(), "-S", Path.GetDirectoryName(running.SocketPath)!);

        Assert.That(exitCode, Is.EqualTo(ExitCode.Success), output);
        Assert.That(output, Does.Contain("All boards are up-to-date!"));
    }

    /// <summary>
    /// Without a running instance, -u starts DCS in update-only mode to update the firmware itself (Program.cs:209-226 falls through to host.Run)
    /// </summary>
    [Test]
    public async Task UpdateOnlyWithoutRunningInstanceStartsItself()
    {
        (int exitCode, string output) = await RunAsync("-u", "-c", WriteConfig());

        Assert.That(exitCode, Is.EqualTo(ExitCode.OsError), output);
        Assert.That(output, Does.Not.Contain("Duet Control Server v"));
        Assert.That(System.IO.File.ReadAllText(StartErrorFile), Does.StartWith("Failed to start: "));
    }

    /// <summary>
    /// Upstream 9ca5be78 "Pass unmatched params in DCS to host builder" hands the tokens that are no option to the configuration
    /// (Program.cs:166), so that a setting can be given as --Key=value, as DWS takes them (DuetWebServer/Program.cs:46). System.CommandLine rejected
    /// them first ("Unrecognized command or argument"), because a command treats unmatched tokens as errors by default, so the action of Program.cs:92
    /// never ran. The root command no longer does (Program.cs:256-257)
    /// </summary>
    [Test]
    public async Task SettingsCanBeGivenOnTheCommandLine()
    {
        string otherDevice = Path.Combine(_directory, "other-gpiochip");
        (int exitCode, string output) = await RunAsync("-c", WriteConfig(), $"--{nameof(Settings.GpioChipDevice)}={otherDevice}");

        Assert.That(output, Does.Not.Contain("Unrecognized command or argument"));
        Assert.That(exitCode, Is.EqualTo(ExitCode.OsError), output);
        Assert.That(System.IO.File.ReadAllText(StartErrorFile), Does.Contain(otherDevice));
    }

    /// <summary>
    /// The other side of <see cref="SettingsCanBeGivenOnTheCommandLine"/>: an argument that is neither an option nor a setting is no longer refused, the
    /// configuration binder ignores the unknown key, and DCS starts as without it
    /// </summary>
    [Test]
    public async Task UnknownArgumentIsIgnored()
    {
        (int exitCode, string output) = await RunAsync("-c", WriteConfig(), "--NoSuchSetting=1");

        Assert.That(output, Does.Not.Contain("Unrecognized command or argument"));
        Assert.That(exitCode, Is.EqualTo(ExitCode.OsError), output);
        Assert.That(System.IO.File.ReadAllText(StartErrorFile), Does.Contain(GpioChipDevice));
    }

    /// <summary>
    /// An invalid config file stops the start before the settings exist, see <see cref="InvalidConfigFileIsAConfigurationError"/> for how it is reported
    /// </summary>
    [Test]
    public async Task InvalidConfigFileStopsTheStart()
    {
        Assume.That(Directory.Exists("/run/dsf"), Is.False, "Would write /run/dsf/dcs.err");
        string configFile = Path.Combine(_directory, "config.json");
        System.IO.File.WriteAllText(configFile, "{ \"LogLevel\": ");
        (int exitCode, string output) = await RunAsync("-c", configFile);

        Assert.That(exitCode, Is.Not.EqualTo(ExitCode.Success), output);
        Assert.That(output, Does.Contain($"Failed to load configuration from file '{configFile}'"));
        Assert.That(System.IO.File.Exists(StartErrorFile), Is.False, "The start error file of the config file is not known yet");
    }

    /// <summary>
    /// An invalid config file is reported as "Failed to load settings" with the message of the JSON parser and ExitCode.Configuration. Program.cs meant to do
    /// that where it adds the file to the configuration, but AddJsonFile only adds the source; the file is parsed in HostBuilder.Build (Program.cs:190), which
    /// throws InvalidDataException (FileConfigurationProvider.cs:107-108 of Microsoft.Extensions.Configuration.FileExtensions), so it was reported as
    /// "Failed to initialize environment" with OsError. Now that is caught at the build (Program.cs:197-202). At that point the start error file is still
    /// /run/dsf/dcs.err (Program.cs:22), because AddSettings has not run; where /run/dsf does not exist, Terminate reports that it cannot write it
    /// (<see cref="StartFailureIsReportedWithoutTheDirectoryOfTheStartErrorFile"/>). The test does not run where /run/dsf exists, so that it never writes there
    /// </summary>
    [Test]
    public async Task InvalidConfigFileIsAConfigurationError()
    {
        Assume.That(Directory.Exists("/run/dsf"), Is.False, "Would write /run/dsf/dcs.err");
        string configFile = Path.Combine(_directory, "config.json");
        System.IO.File.WriteAllText(configFile, "{ \"LogLevel\": ");
        (int exitCode, string output) = await RunAsync("-c", configFile);

        Assert.That(output, Does.Contain("[fatal] Failed to load settings: ").And.Contain("BytePositionInLine"));
        Assert.That(exitCode, Is.EqualTo(ExitCode.Configuration), output);
    }
}
