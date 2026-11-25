using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Single corrupted exchanges on the real SPI link, checked against the target outcome of the lab test plan and compared with the outcome
/// the link census predicts for them
/// </summary>
/// <remarks>
/// Needs the fault injection build of DCS (run-lab-tests.sh --side-load --fault-injection). SingleFaultsMatchCensus needs a case file written by
/// UnitTests LinkRecovery.WriteHardwareCases or WriteEsdCases, whose path is in LAB_SPI_FAULT_CASES. The cases of WriteHardwareCases_RrfOfToday are for the plain link,
/// which DCS keeps with the RRF on the printer only with --plain-link, those of WriteHardwareCases_Extended and WriteEsdCases for the extended link.
/// LAB_SPI_FAULT_SELECT picks the cases: "failures" (predicted to break the link), "responses" (every fault in a response word,
/// where the census finds all failing single bits), or "all"; the default is both of the first two. LAB_SPI_FAULT_MAX limits the number.
/// A case that breaks the link costs a DCS restart and M999, which Tim allows unattended
/// </remarks>
[TestFixture, Category("FaultInjection"), Explicit("Needs the fault injection build of DCS")]
public class SpiFaultInjection : SpiFaultTest
{
    private sealed record HardwareCase(string Name, string Spec, string Expected, Dictionary<string, string> ExpectedByKind);

    private sealed record CaseFile(string Configuration, string Options, List<HardwareCase> Cases);

    /// <summary>
    /// Single faults that break the plain link with the fixes in, as (fault without its kind, kind of the transfer hit). These are the 20 of 3234 cases
    /// that UnitTests LinkRecovery.WriteHardwareCases_RrfOfToday predicts on v3.7-dev 520765373, the number design/lab-test-plan.md:244 gives for the
    /// plain link with #137. Several cases share a fault and kind, so there are 16 entries. Each needs a change of RRF, which the plain link stands for
    /// (an RRF without link capabilities). A later fix may repair one, so a Retry is always within the target
    /// </summary>
    private static readonly HashSet<(string Fault, string Kind)> PlainLinkBreaks =
    [
        ("Header ToDsf word 0xfefefefe", "BothData"), ("Header ToDsf word 0xfefefefe", "DsfData"), ("Header ToDsf word 0xfefefefe", "NoData"),
        ("HeaderResponse ToDsf flip 2", "NoData"), ("HeaderResponse ToDsf word 0x00000005", "NoData"), ("HeaderResponse ToDsf word 0xfefefefe", "NoData"),
        ("Data ToDsf word 0xfefefefe", "BothData"), ("Data ToDsf word 0xfefefefe", "DsfData"), ("Data ToDsf word 0xfefefefe", "RrfData"),
        ("DataResponse ToDsf word 0x00000006", "BothData"), ("DataResponse ToDsf word 0x00000006", "RrfData"),
        ("DataResponse ToDsf word 0xfefefefe", "BothData"), ("DataResponse ToDsf word 0xfefefefe", "DsfData"), ("DataResponse ToDsf word 0xfefefefe", "RrfData"),
        ("DataResponse ToRrf word 0x00000006", "BothData"), ("DataResponse ToRrf word 0x00000006", "DsfData")
    ];

    /// <summary>
    /// Every hit must meet the target outcome of design/lab-test-plan.md, and its outcome must agree with the census.
    /// The census is computed from the tree under test (WriteHardwareCases runs that tree's SPI.cs against the fake RRF), so a regression of DSF changes
    /// the prediction and the printer alike, and the comparison alone passed: F-3 without #137 ended "Reconnect 8, Retry 4, 0 Abweichungen" on 2026-11-08
    /// (design/lab-test-plan.md:568). The target does not depend on the tree:
    /// - Extended link and ESD events: every hit is repaired by retries (F-2 and F-1, design/lab-test-plan.md:219 and :231; offline
    ///   UnitTests LinkRecovery.ExtendedLinkRecoversFromEverySingleFault and EsdEventIsRepaired).
    /// - Plain link: every hit is repaired by retries except those of <see cref="PlainLinkBreaks"/>. Without #137, its six P1 faults (BothData and DsfData
    ///   HeaderResponse ToDsf flip 2, word 0x00000005, word 0xfefefefe) leave the link stuck or reconnecting, which is outside the target, so this fails.
    /// The target was chosen over reference case files from a known good tree: it needs no regeneration when a fix changes the census, a repaired case
    /// still passes, and only a case that breaks the link where it must not fails. A case the census predicts wrongly still fails as before, as a
    /// difference from the census
    /// </summary>
    [Test]
    public async Task SingleFaultsMatchCensus()
    {
        string? path = Environment.GetEnvironmentVariable("LAB_SPI_FAULT_CASES");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Assert.Ignore("LAB_SPI_FAULT_CASES does not name a case file");
        }
        CaseFile file = JsonSerializer.Deserialize<CaseFile>(await File.ReadAllTextAsync(path!))!;
        if (file.Configuration is not ("extended" or "esd" or "rrf-of-today"))
        {
            Assert.Fail($"No target outcome for the case file configuration {file.Configuration}, known are extended, esd (both WriteHardwareCases_Extended, WriteEsdCases) " +
                "and rrf-of-today (WriteHardwareCases_RrfOfToday)");
        }

        // The census predicts the plain link for RRF without capabilities. The RRF on the printer offers them, so DCS keeps the plain link only with --plain-link
        string expectedLink = Regex.Match(file.Options, @"Capabilities = (\w+)").Groups[1].Value is "" or "None" ? "plain" : "extended";
        string? link = await Lab.GetLinkModeAsync();
        if (link != expectedLink)
        {
            Assert.Fail($"The cases of {file.Configuration} are for the {expectedLink} link, but DCS uses the {link ?? "unknown"} link. " +
                $"Run them {(expectedLink == "plain" ? "with" : "without")} run-lab-tests.sh --plain-link");
        }

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
        if (cases.Count == 0)
        {
            // The ESD file has neither predicted failures nor response faults (design/lab-test-plan.md:222)
            Assert.Inconclusive($"LAB_SPI_FAULT_SELECT={select} selects no case of {file.Configuration}, use LAB_SPI_FAULT_SELECT=all");
        }

        List<string> belowTarget = [], mismatches = [], notInjected = [];
        Dictionary<string, int> observed = [];
        foreach (HardwareCase hardwareCase in cases)
        {
            Injection injection = await InjectAsync(hardwareCase.Spec);
            if (injection.Kind is not string kind)
            {
                notInjected.Add(hardwareCase.Name);
                continue;
            }

            string outcome = injection.Outcome, expected = hardwareCase.ExpectedByKind.GetValueOrDefault(kind, "unknown");
            observed[outcome] = observed.GetValueOrDefault(outcome) + 1;
            bool meetsTarget = outcome == "Retry" || (file.Configuration == "rrf-of-today" && PlainLinkBreaks.Contains((hardwareCase.Spec[(hardwareCase.Spec.IndexOf(' ') + 1)..], kind)));
            bool match = (outcome == "Retry") == (expected == "Retry");
            if (!meetsTarget)
            {
                belowTarget.Add($"{hardwareCase.Spec} hit {kind}: printer {outcome}, target Retry (census {expected})");
            }
            if (!match)
            {
                mismatches.Add($"{hardwareCase.Spec} hit {kind}: census {expected}, printer {outcome}");
            }
            if (!meetsTarget || !match || outcome != "Retry")
            {
                TestContext.Progress.WriteLine($"{hardwareCase.Spec} hit {kind}: census {expected}, printer {outcome}{(meetsTarget ? "" : ", below the target")}");
            }
        }

        TestContext.Progress.WriteLine($"Outcomes: {string.Join(", ", observed.Select(o => $"{o.Key} {o.Value}"))}, not injected {notInjected.Count}, " +
            $"{belowTarget.Count} below the target, {mismatches.Count} differ from the census");
        foreach (string name in notInjected)
        {
            TestContext.Progress.WriteLine($"Not injected: {name}");
        }
        Assert.Multiple(() =>
        {
            Assert.That(belowTarget, Is.Empty, "Faults that broke the link where the lab test plan requires a repair by retries:\n" + string.Join("\n", belowTarget));
            Assert.That(mismatches, Is.Empty, "Faults whose outcome differs from the census:\n" + string.Join("\n", mismatches));
        });
        if (observed.Count == 0)
        {
            Assert.Inconclusive($"None of the {cases.Count} faults found a transfer of its kind");
        }
    }
}
