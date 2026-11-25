using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// A second DuetControlServer process with a test configuration, next to the running one, for what Program.cs and Settings.cs do before the host runs
/// </summary>
/// <remarks>
/// The probe runs as dsf in a mount namespace of its own (unshare --mount --propagation private) with its files in <see cref="Directory"/>. In that namespace
/// /run/dsf, /opt/dsf/sd, /opt/dsf/plugins and /opt/dsf/conf are empty tmpfs mounts and an empty file hides every SPI, GPIO chip and ttyACM device node. So
/// even a probe that ignored its configuration could not reach the socket, the start error file, the configuration, the virtual SD card or the hardware of
/// the running DCS; the script checks that before it starts the probe. The configuration puts every path into the probe directory and names devices that do
/// not exist, as UnitTests/Startup/DcsProcess.cs does, so a start ends when the SPI adapter cannot open the GPIO chip (SPI.cs:109-111), with
/// "Failed to start: " and OsError 71 (Program.cs:246-253, ExitCode.cs:51). The test checks afterwards that the PID and the socket of the running DCS and
/// its /run/dsf/dcs.err are unchanged. 0 restarts, under a minute per case.
/// The fixes are in DuetControlServer.dll, which the side-load replaces; the apphost /opt/dsf/bin/DuetControlServer loads it
/// </remarks>
[TestFixture, Category("Approval"), Explicit("Needs Tim's approval for each run")]
public class ControlServerStartup : LabTest
{
    /// <summary>
    /// Directory of the probe, removed after each case
    /// </summary>
    private const string Directory = "/var/tmp/lab-tests/dcs-probe";

    private const string GpioChipDevice = $"{Directory}/no-gpiochip";
    private const string OtherGpioChipDevice = $"{Directory}/other-gpiochip";
    private const string MissingStartErrorFile = $"{Directory}/missing/dcs.err";

    /// <summary>
    /// Cases of <see cref="SecondInstanceReportsSettingsErrors"/>
    /// </summary>
    public enum Probe
    {
        LogLevelKeyInLowerCase,
        SettingOnTheCommandLine,
        StartErrorFileWithoutDirectory,
        InvalidConfigFile
    }

    /// <summary>
    /// Configuration of the probe whose paths all lie in its directory, as DcsProcess.WriteConfig in the unit tests (keys from Settings.cs:78-208)
    /// </summary>
    private static string Config(Action<JsonObject>? configure = null)
    {
        JsonObject config = new()
        {
            ["BaseDirectory"] = $"{Directory}/sd",
            ["SocketDirectory"] = $"{Directory}/run",
            ["StartErrorFile"] = $"{Directory}/run/dcs.err",
            ["PluginDirectory"] = $"{Directory}/plugins",
            ["PluginsFilename"] = $"{Directory}/plugins.txt",
            ["SpiDevice"] = $"{Directory}/no-spidev",
            ["GpioChipDevice"] = GpioChipDevice,
            ["UsbDevice"] = $"{Directory}/no-tty"
        };
        configure?.Invoke(config);
        return config.ToJsonString();
    }

    /// <summary>
    /// What the probe left behind
    /// </summary>
    private sealed record ProbeResult(int ExitCode, string Output, string ProbeStartErrorFile, string DefaultStartErrorFile, bool MissingStartErrorFileExists,
        string Before, string After);

    /// <summary>
    /// Run DuetControlServer once as the probe with the given configuration and arguments. Each line of the result starts with a marker
    /// </summary>
    private static async Task<ProbeResult> RunProbeAsync(string config, string args)
    {
        string report = await Lab.RunOnSbcAsync($$"""
            dir={{Directory}}
            for tool in unshare runuser; do command -v $tool > /dev/null || { echo "MISSING $tool"; exit 0; }; done
            [ -x /opt/dsf/bin/DuetControlServer ] || { echo "MISSING /opt/dsf/bin/DuetControlServer"; exit 0; }
            state() {
              err=$(sudo -n sha256sum /run/dsf/dcs.err 2> /dev/null) || err=none
              echo "pid $(systemctl show -p MainPID --value duetcontrolserver), socket $(sudo -n stat -c %i /run/dsf/dcs.sock 2> /dev/null || echo none), dcs.err ${err%% *}"
            }
            [ -d /var/tmp/lab-tests ] && parent=keep || parent=remove
            before=$(state)
            sudo -n rm -rf "$dir"
            sudo -n mkdir -p "$dir/sd" "$dir/run"
            sudo -n tee "$dir/config.json" > /dev/null <<'JSON'
            {{config}}
            JSON
            echo '{ "LogLevel": ' | sudo -n tee "$dir/invalid.json" > /dev/null
            sudo -n chown -R dsf:dsf "$dir"
            sudo -n unshare --mount --propagation private /bin/bash -s -- "$dir" {{args}} <<'PROBE'
            dir=$1
            shift
            mount -t tmpfs -o mode=2770,uid=$(id -u dsf),gid=$(id -g dsf) tmpfs /run/dsf || { echo "SETUP no tmpfs over /run/dsf"; exit 0; }
            for d in /opt/dsf/sd /opt/dsf/plugins /opt/dsf/conf; do
              [ -d "$d" ] || continue
              mount -t tmpfs -o mode=0755 tmpfs "$d" || { echo "SETUP no tmpfs over $d"; exit 0; }
            done
            : > "$dir/blank"
            for dev in /dev/spidev* /dev/gpiochip* /dev/ttyACM*; do
              [ -e "$dev" ] || continue
              mount --bind "$dir/blank" "$dev" || { echo "SETUP cannot hide $dev"; exit 0; }
            done
            for dev in /dev/spidev* /dev/gpiochip* /dev/ttyACM*; do
              [ -c "$dev" ] && { echo "SETUP $dev is still a device"; exit 0; }
            done
            [ -e /run/dsf/dcs.sock ] && { echo "SETUP the socket of the running DCS is visible"; exit 0; }
            cd "$dir"
            code=0
            runuser -u dsf -- env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin HOME="$dir" DOTNET_GCHeapHardLimit=0x10000000 DOTNET_EnableDiagnostics=0 \
              timeout 60 /opt/dsf/bin/DuetControlServer "$@" < /dev/null > "$dir/output" 2>&1 || code=$?
            echo "EXIT $code"
            echo "PROBE-ERRFILE $(head -c 2000 "$dir/run/dcs.err" 2> /dev/null | tr '\n' ' ')"
            echo "DEFAULT-ERRFILE $(head -c 2000 /run/dsf/dcs.err 2> /dev/null | tr '\n' ' ')"
            echo "MISSING-ERRFILE $([ -e "$dir/missing/dcs.err" ] && echo exists || echo absent)"
            echo "OUTPUT-BEGIN"
            head -c 20000 "$dir/output"
            echo
            echo "OUTPUT-END"
            PROBE
            echo "BEFORE $before"
            echo "AFTER $(state)"
            sudo -n rm -rf "$dir"
            [ $parent = keep ] || sudo -n rmdir /var/tmp/lab-tests 2> /dev/null || true
            """);

        string[] lines = report.Split('\n');
        string? missing = lines.FirstOrDefault(line => line.StartsWith("MISSING ") || line.StartsWith("SETUP "));
        if (missing is not null)
        {
            Assert.Ignore($"The probe did not run: {missing}");
        }

        string Value(string marker) => lines.FirstOrDefault(line => line.StartsWith(marker + " "))?[(marker.Length + 1)..].Trim() ?? string.Empty;
        int begin = Array.IndexOf(lines, "OUTPUT-BEGIN"), end = Array.IndexOf(lines, "OUTPUT-END");
        string output = (begin >= 0 && end > begin) ? RestartHelpers.StripColors(string.Join('\n', lines[(begin + 1)..end])) : string.Empty;
        return new ProbeResult(int.TryParse(Value("EXIT"), out int exitCode) ? exitCode : -1, output, Value("PROBE-ERRFILE"), Value("DEFAULT-ERRFILE"),
            Value("MISSING-ERRFILE") == "exists", Value("BEFORE"), Value("AFTER"));
    }

    /// <summary>
    /// Settings and start errors of DCS (DSF #150, all four still upstream bugs, see the PR). Each case runs the probe once, deterministically:
    /// <list type="bullet">
    /// <item>#150a, LogLevelKeyInLowerCase: config.json with "logLevel": "info". The key was taken out of the binding case-sensitively, so the binder got
    /// "info" for the enum LogLevel and DCS stopped with "Failed to initialize environment" (Program.cs:203-207, also exit 71). Fixed: Settings.cs:497
    /// compares with OrdinalIgnoreCase, the probe gets to the GPIO chip and its start error file starts with "Failed to start: " and names that path
    /// (UnitTests SettingsParsing.LogLevelKeyIsCaseInsensitive)</item>
    /// <item>#150d, SettingOnTheCommandLine: --GpioChipDevice=... after -c. System.CommandLine refused it with "Unrecognized command or argument" and DCS
    /// never ran its action. Fixed: TreatUnmatchedTokensAsErrors = false (Program.cs:256-257), the token reaches the configuration (Program.cs:166), and the
    /// start error file names the other path (DcsProcess.SettingsCanBeGivenOnTheCommandLine)</item>
    /// <item>#150e, StartErrorFileWithoutDirectory: a start error file in a directory that does not exist. The exception of File.WriteAllText left
    /// Terminate and the process ended with exit code 1 (PR #150). Fixed: Program.cs:46-55 says "Failed to write the start error file ..." on stderr and
    /// still exits 71 (DcsProcess.StartFailureIsReportedWithoutTheDirectoryOfTheStartErrorFile)</item>
    /// <item>#150f, InvalidConfigFile: a config file that is no valid JSON. It was reported as "Failed to initialize environment" with 71. Fixed:
    /// Program.cs:197-202 catches the InvalidDataException of the build and exits 78 (ExitCode.cs:86) with "Failed to load settings: " and the message of
    /// the JSON parser (DcsProcess.InvalidConfigFileIsAConfigurationError). At that point the start error file is still /run/dsf/dcs.err (Program.cs:22),
    /// which in the namespace of the probe is its own tmpfs</item>
    /// </list>
    /// </summary>
    [TestCase(Probe.LogLevelKeyInLowerCase)]
    [TestCase(Probe.SettingOnTheCommandLine)]
    [TestCase(Probe.StartErrorFileWithoutDirectory)]
    [TestCase(Probe.InvalidConfigFile)]
    public async Task SecondInstanceReportsSettingsErrors(Probe probe)
    {
        ProbeResult result = probe switch
        {
            Probe.LogLevelKeyInLowerCase => await RunProbeAsync(Config(config => config["logLevel"] = "info"), "-c \"$dir/config.json\""),
            Probe.SettingOnTheCommandLine => await RunProbeAsync(Config(), $"-c \"$dir/config.json\" --GpioChipDevice={OtherGpioChipDevice}"),
            Probe.StartErrorFileWithoutDirectory => await RunProbeAsync(Config(config => config["StartErrorFile"] = MissingStartErrorFile), "-c \"$dir/config.json\""),
            _ => await RunProbeAsync(Config(), "-c \"$dir/invalid.json\"")
        };
        TestContext.Progress.WriteLine($"{probe}: exit {result.ExitCode}, probe start error file '{result.ProbeStartErrorFile}', " +
            $"default start error file '{result.DefaultStartErrorFile}'; before: {result.Before}; after: {result.After}");
        TestContext.Progress.WriteLine(string.Join('\n', result.Output.Split('\n').Where(line => line.Contains("fatal", StringComparison.OrdinalIgnoreCase) || line.Contains("Failed")).Take(10)));

        Assert.Multiple(() =>
        {
            Assert.That(result.After, Is.EqualTo(result.Before), "The probe changed the running DCS, its socket or its start error file");
            Assert.That(result.Output, Does.Not.Contain("Unrecognized command or argument"));
            Assert.That(result.Output, Does.Not.Contain("Failed to initialize environment"));
            switch (probe)
            {
                case Probe.LogLevelKeyInLowerCase:
                    Assert.That(result.ExitCode, Is.EqualTo(71), "#150a");
                    Assert.That(result.ProbeStartErrorFile, Does.StartWith("Failed to start: ").And.Contain(GpioChipDevice), "#150a");
                    break;
                case Probe.SettingOnTheCommandLine:
                    Assert.That(result.ExitCode, Is.EqualTo(71), "#150d");
                    Assert.That(result.ProbeStartErrorFile, Does.Contain(OtherGpioChipDevice), "#150d");
                    break;
                case Probe.StartErrorFileWithoutDirectory:
                    Assert.That(result.ExitCode, Is.EqualTo(71), "#150e");
                    Assert.That(result.Output, Does.Contain("Failed to start: ").And.Contain($"Failed to write the start error file {MissingStartErrorFile}"), "#150e");
                    Assert.That(result.MissingStartErrorFileExists, Is.False, "#150e");
                    break;
                case Probe.InvalidConfigFile:
                    Assert.That(result.ExitCode, Is.EqualTo(78), "#150f");
                    Assert.That(result.Output, Does.Contain("Failed to load settings: ").And.Contain("BytePositionInLine"), "#150f");
                    Assert.That(result.DefaultStartErrorFile, Does.StartWith("Failed to load settings: "), "#150f");
                    break;
            }
        });
    }
}
