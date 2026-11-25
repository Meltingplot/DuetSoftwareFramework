using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Base of the tests that corrupt single SPI exchanges with the fault injection build of DCS (run-lab-tests.sh --side-load --fault-injection).
/// A fault is armed by writing its text form (SpiFaultSpec in DuetControlServer/Link/Adapter/SpiFaultInjection.cs) to the control file, and the
/// build logs "SPI fault armed: ..." when it reads it and "SPI fault injected: ..., hit a ... transfer" for every exchange it corrupts
/// </summary>
public abstract class SpiFaultTest : LabTest
{
    /// <summary>
    /// File the fault injection build watches for faults (SPI.FaultControlFile)
    /// </summary>
    protected const string ControlFile = "/var/tmp/lab-tests/spi-fault";

    /// <summary>
    /// Recovery counters that DSF reports in M122 "DSF" (SPI.PrintDiagnostics, SPI.cs:288-290), as UnitTests LinkRecovery.RecoveryCounters reads them
    /// </summary>
    private static readonly string[] LinkCounters =
    [
        "Full transfer restarts", "header retries", "data retries", "Transfers sent again", "transfers requested again", "extended link left by RRF", "lost connections", "connection resets"
    ];

    /// <summary>
    /// What one armed fault did
    /// </summary>
    /// <param name="Spec">Fault</param>
    /// <param name="Kind">Kind of transfer it hit first, or null if no transfer of the requested kind came along within 10 s</param>
    /// <param name="Outcome">In the terms of the census: Retry (repaired by the protocol), Reconnect, DcsRestart or NoRecovery (no answer to
    /// M409 ... R1 in 5 s), or NotInjected</param>
    /// <param name="Journal">DCS log from arming the fault until 2 s after it was injected</param>
    protected sealed record Injection(string Spec, string? Kind, string Outcome, string Journal)
    {
        /// <summary>
        /// Number of lines of the journal that contain the text
        /// </summary>
        public int Count(string text) => Journal.Split('\n').Count(line => line.Contains(text, StringComparison.Ordinal));

        /// <summary>
        /// Lines of the journal that contain one of the texts, for a failure message
        /// </summary>
        public string Lines(params string[] texts) =>
            string.Join("\n", Journal.Split('\n').Where(line => texts.Any(text => line.Contains(text, StringComparison.Ordinal))).Take(20));
    }

    /// <summary>
    /// Arm a fault, wait until it has been injected and see what the link did. A case that does not end in Retry, or leaves the printer busy,
    /// restarts DCS and resets the firmware before it returns, so that the next case starts from a working link
    /// </summary>
    /// <param name="spec">Fault in its text form</param>
    /// <returns>Outcome and log of the fault</returns>
    protected async Task<Injection> InjectAsync(string spec)
    {
        // One round trip: arm, wait for the injection in the journal, empty the control file, give the link 2 s to recover, then report
        string report = await Lab.RunOnSbcAsync($"""
            cursor=$(sudo -n journalctl -u duetcontrolserver -n 1 --show-cursor -o cat --no-pager | sed -n 's/^-- cursor: //p')
            pid=$(systemctl show -p MainPID --value duetcontrolserver)
            echo '{spec}' | sudo -n tee {ControlFile} > /dev/null
            injected=0
            for i in $(seq 100); do
              if sudo -n journalctl -u duetcontrolserver --after-cursor="$cursor" -o cat --no-pager | grep -qF 'SPI fault injected: {spec},'; then injected=1; break; fi
              sleep 0.1
            done
            : | sudo -n tee {ControlFile} > /dev/null
            [ $injected = 1 ] && sleep 2
            echo "PID $pid $(systemctl show -p MainPID --value duetcontrolserver)"
            sudo -n journalctl -u duetcontrolserver --after-cursor="$cursor" -o cat --no-pager | head -n 2000
            """);
        int pidLine = report.IndexOf("PID ", StringComparison.Ordinal);
        string[] pids = report[pidLine..].Split('\n')[0].Split(' ');
        string journal = report[(report.IndexOf('\n', pidLine) + 1)..];

        Match injection = Regex.Match(journal, $@"SPI fault injected: {Regex.Escape(spec)}, hit a (\w+) transfer");
        if (!injection.Success)
        {
            if (!journal.Contains($"SPI fault armed: {spec}", StringComparison.Ordinal))
            {
                Assert.Ignore($"DCS did not arm {spec}. Is the fault injection build side-loaded (run-lab-tests.sh --side-load --fault-injection)?");
            }
            return new(spec, null, "NotInjected", journal);
        }

        // Does the link still carry codes? A livelock never times out on its own
        string outcome = (pids[1] != pids[2]) ? "DcsRestart"
            : (journal.Contains("Connection to controller has been reset", StringComparison.Ordinal) || journal.Contains("Lost connection to Duet", StringComparison.Ordinal)) ? "Reconnect"
            : "Retry";
        bool linkWorks;
        try
        {
            linkWorks = (await SendCodeAsync("M409 K\"state.upTime\" R1", 5)).Contains("\"result\":", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            linkWorks = false;
        }
        if (!linkWorks && outcome == "Retry")
        {
            outcome = "NoRecovery";
        }

        // Bring the printer back for the next case
        if (outcome != "Retry" || !Lab.IsIdle(Lab.Read(Session, model => model)))
        {
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
        }
        return new(spec, injection.Groups[1].Value, outcome, journal);
    }

    /// <summary>
    /// Read the recovery counters of the link from M122 "DSF", which DSF answers itself (MCodeHandler.cs:836-840; wiki: G-code dictionary, M122 "DSF").
    /// They count from the start of DCS (SPI.PrintDiagnostics does not reset them), so a test compares two readings
    /// </summary>
    protected async Task<Dictionary<string, int>> ReadLinkCountersAsync()
    {
        string diagnostics = await SendCodeAsync("M122 \"DSF\"");
        Dictionary<string, int> counters = [];
        foreach (string name in LinkCounters)
        {
            Match match = Regex.Match(diagnostics, $@"{name}: (\d+)");
            if (!match.Success)
            {
                Assert.Fail($"M122 \"DSF\" shows no \"{name}\": {diagnostics}");
            }
            counters[name] = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return counters;
    }

    /// <summary>
    /// Changes of the recovery counters between two readings, only those that changed
    /// </summary>
    protected static Dictionary<string, int> Changes(Dictionary<string, int> before, Dictionary<string, int> after) =>
        after.Where(counter => counter.Value != before[counter.Key]).ToDictionary(counter => counter.Key, counter => counter.Value - before[counter.Key]);

    /// <summary>
    /// Text form of counter changes for messages, e.g. "data retries +1"
    /// </summary>
    protected static string Describe(Dictionary<string, int> changes) =>
        (changes.Count == 0) ? "no counter changed" : string.Join(", ", changes.Select(counter => $"{counter.Key} {counter.Value:+0;-0}"));

    /// <summary>
    /// Skip the test unless DCS uses the given link: "plain" with run-lab-tests.sh --plain-link, else "extended" (Lab.GetLinkModeAsync)
    /// </summary>
    protected static async Task RequireLinkAsync(string link)
    {
        string? actual = await Lab.GetLinkModeAsync();
        if (actual != link)
        {
            Assert.Ignore($"Needs the {link} link, but DCS uses the {actual ?? "unknown"} link. Run it {(link == "plain" ? "with" : "without")} run-lab-tests.sh --plain-link");
        }
    }
}
