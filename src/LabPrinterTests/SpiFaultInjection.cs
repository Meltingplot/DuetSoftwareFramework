using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Single corrupted exchanges on the real SPI link, compared with the outcome the link census predicts for them
/// </summary>
/// <remarks>
/// Needs the fault injection build of DCS (run-lab-tests.sh --side-load --fault-injection) and a case file written by
/// UnitTests LinkRecovery.WriteHardwareCases for the firmware on the printer, whose path is in LAB_SPI_FAULT_CASES.
/// LAB_SPI_FAULT_SELECT picks the cases: "failures" (predicted to break the link), "responses" (every fault in a response word,
/// where the census finds all failing single bits), or "all"; the default is both of the first two. LAB_SPI_FAULT_MAX limits the number.
/// A case that breaks the link costs a DCS restart and M999, which Tim allows unattended
/// </remarks>
[TestFixture, Category("FaultInjection"), Explicit("Needs the fault injection build of DCS and a case file")]
public class SpiFaultInjection : LabTest
{
    private const string ControlFile = "/var/tmp/lab-tests/spi-fault";

    private sealed record HardwareCase(string Name, string Spec, string Expected, Dictionary<string, string> ExpectedByKind);

    private sealed record CaseFile(string Configuration, string Options, List<HardwareCase> Cases);

    [Test]
    public async Task SingleFaultsMatchCensus()
    {
        string? path = Environment.GetEnvironmentVariable("LAB_SPI_FAULT_CASES");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Assert.Ignore("LAB_SPI_FAULT_CASES does not name a case file");
        }
        CaseFile file = JsonSerializer.Deserialize<CaseFile>(await File.ReadAllTextAsync(path!))!;
        string select = Environment.GetEnvironmentVariable("LAB_SPI_FAULT_SELECT") ?? "failures,responses";
        List<HardwareCase> cases = [.. file.Cases.Where(c =>
            select.Contains("all") ||
            (select.Contains("failures") && c.ExpectedByKind.Values.Any(outcome => outcome != "Retry")) ||
            (select.Contains("responses") && c.Spec.Split(' ')[1] is "HeaderResponse" or "DataResponse"))];
        if (int.TryParse(Environment.GetEnvironmentVariable("LAB_SPI_FAULT_MAX"), out int max) && max < cases.Count)
        {
            // Every n-th case, so that each kind of fault stays represented
            cases = [.. cases.Where((_, index) => index % (cases.Count / max + 1) == 0).Take(max)];
        }
        TestContext.Progress.WriteLine($"{cases.Count} of {file.Cases.Count} cases for {file.Configuration} ({file.Options})");

        List<string> mismatches = [], notInjected = [];
        Dictionary<string, int> observed = [];
        foreach (HardwareCase hardwareCase in cases)
        {
            (string outcome, string? kind) = await InjectAsync(hardwareCase.Spec);
            if (kind is null)
            {
                notInjected.Add(hardwareCase.Name);
                continue;
            }

            string expected = hardwareCase.ExpectedByKind.GetValueOrDefault(kind, "unknown");
            observed[outcome] = observed.GetValueOrDefault(outcome) + 1;
            bool match = (outcome == "Retry") == (expected == "Retry");
            if (!match)
            {
                mismatches.Add($"{hardwareCase.Spec} hit {kind}: census {expected}, printer {outcome}");
            }
            if (!match || outcome != "Retry")
            {
                TestContext.Progress.WriteLine($"{hardwareCase.Spec} hit {kind}: census {expected}, printer {outcome}");
            }
        }

        TestContext.Progress.WriteLine($"Outcomes: {string.Join(", ", observed.Select(o => $"{o.Key} {o.Value}"))}, not injected {notInjected.Count}, " +
            $"{mismatches.Count} differ from the census");
        foreach (string name in notInjected)
        {
            TestContext.Progress.WriteLine($"Not injected: {name}");
        }
        Assert.That(mismatches, Is.Empty, string.Join("\n", mismatches));
    }

    /// <summary>
    /// Arm a fault, wait until it has been injected and see what the link did
    /// </summary>
    /// <returns>Outcome in the terms of the census, and the kind of transfer it hit or null if no transfer of the kind came along</returns>
    private async Task<(string Outcome, string? Kind)> InjectAsync(string spec)
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
            sudo -n journalctl -u duetcontrolserver --after-cursor="$cursor" -o cat --no-pager | grep -E 'SPI fault injected|Connection to controller has been reset|Lost connection to Duet|Duet Control Server v|Maximum number of SPI' | head -20
            """);

        Match injection = Regex.Match(report, @"SPI fault injected: .*, hit a (\w+) transfer");
        if (!injection.Success)
        {
            return ("NotInjected", null);
        }

        // Does the link still carry codes? A livelock never times out on its own
        string[] pids = report.Split('\n').First(line => line.StartsWith("PID ")).Split(' ');
        string outcome = (pids[1] != pids[2]) ? "DcsRestart"
            : (report.Contains("Connection to controller has been reset") || report.Contains("Lost connection to Duet")) ? "Reconnect"
            : "Retry";
        bool linkWorks;
        try
        {
            linkWorks = (await SendCodeAsync("M409 K\"state.upTime\" R1", 5)).Contains("\"result\":");
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
        return (outcome, injection.Groups[1].Value);
    }
}
