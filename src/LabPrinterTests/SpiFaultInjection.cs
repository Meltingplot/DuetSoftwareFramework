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

    /// <summary>
    /// Warning of DSF when it goes on with the data because RRF answered its BadHeaderChecksum with BadDataChecksum (SPI.IsExchangingData, SPI.cs:2109)
    /// </summary>
    private const string ExchangingDataLine = "RepRapFirmware is exchanging the data, exchanging the data as well";

    /// <summary>
    /// Warning of DSF when the response of RRF to its header is no known code, e.g. a refusal (SPI.ExchangeHeader, SPI.cs:2074)
    /// </summary>
    private static string BadHeaderResponseLine(uint code) => $"Restarting full transfer because a bad header response was received (0x{code:x8})";

    /// <summary>
    /// Faults in the response of RRF to the header of DSF on the plain link, each with the warning of the repair DSF must take. The lines come from
    /// the census port, through the injector, on v3.7-dev 520765373; on the printer only the Retry outcome of the P1 faults has been seen (2026-11-08)
    /// </summary>
    private static readonly (string Spec, string Line)[] PlainLinkHeaderResponseFaults =
    [
        // P1 (#137): Success read as BadHeaderChecksum (bit 2 turns 1 into 5, or the whole word) or as BadResponse, in a transfer with data of DSF
        ("BothData HeaderResponse ToDsf flip 2", ExchangingDataLine),
        ("BothData HeaderResponse ToDsf word 0x00000005", ExchangingDataLine),
        ("BothData HeaderResponse ToDsf word 0xfefefefe", ExchangingDataLine),
        ("DsfData HeaderResponse ToDsf flip 2", ExchangingDataLine),
        ("DsfData HeaderResponse ToDsf word 0x00000005", ExchangingDataLine),
        ("DsfData HeaderResponse ToDsf word 0xfefefefe", ExchangingDataLine),

        // #91: Success read as a refusal, BadProtocolVersion (bit 1 turns 1 into 3), BadFormat (1 + 1) or BadDataLength, with and without data
        ("BothData HeaderResponse ToDsf flip 1", BadHeaderResponseLine(3)),
        ("NoData HeaderResponse ToDsf add 0 4 1", BadHeaderResponseLine(2)),
        ("DsfData HeaderResponse ToDsf word 0x00000004", BadHeaderResponseLine(4))
    ];

    /// <summary>
    /// One corrupted response of RRF to the header of DSF is repaired by retries on the plain link, which DSF uses with an RRF without link capabilities
    /// (run-lab-tests.sh --plain-link; on the extended link the response words are corrected, ExtendedLink.DecodeResponse). Needs no case file.
    /// - P1, DSF #137: RRF went on with the data after its Success, while DSF read another code and exchanged its header again. RRF took that header for
    ///   data and answered BadDataChecksum, from then on it exchanges the data again while either side reports BadDataChecksum (RRF DataTransfer.cpp:1229, :1252-1257 at 0d4295065). Before #137 DSF had
    ///   overwritten its copy of RRF's header with that data before the checksum check and kept exchanging headers, so neither side timed out and the link
    ///   stayed stuck until DCS stopped after SbcRestartTimeout (60 s, SPI.cs:327-332). The fix keeps the header until its checksum is valid (SPI.cs:1974) and
    ///   goes on with the data (SPI.cs:1940, :1963, :2102-2111). Unfixed, M409 ... R1 gets no answer in 5 s, so the case ends NoRecovery (all six broke on
    ///   2026-11-08 without #137, design/lab-test-plan.md:568; offline UnitTests LinkRecovery.HeaderResponseOfRrfWithDsfData)
    /// - DSF #91: the switch after DSF's Success threw "RepRapFirmware refused message format / protocol version / data length" for these codes, the exception
    ///   ended the transfer thread (LinkService.cs:182-228) and DCS stopped, so the case ends DcsRestart. Now they take the default branch: DSF logs the code,
    ///   sends BadResponse when data is involved and restarts the transfer (SPI.cs:2066-2085; offline UnitTests LinkRecovery.RefusalCodeInHeaderResponse)
    /// Each case is one fault in one exchange and costs nothing with the fixes. Unfixed, each costs a DCS restart and M999, and on the fork the lost link also
    /// costs an emergency stop with ATX off (Lab.RecoverAsync): 6 for #137, 3 for #91. Deterministic: each fault hits the next transfer of its kind
    /// </summary>
    [Test]
    public async Task PlainLinkHeaderResponseFaultsRecover()
    {
        await RequireLinkAsync("plain");
        List<string> failures = [], notInjected = [];
        foreach ((string spec, string line) in PlainLinkHeaderResponseFaults)
        {
            Injection injection = await InjectAsync(spec);
            if (injection.Kind is null)
            {
                notInjected.Add(spec);
                continue;
            }

            TestContext.Progress.WriteLine($"{spec} hit {injection.Kind}: {injection.Outcome}");
            if (injection.Outcome != "Retry")
            {
                failures.Add($"{spec}: {injection.Outcome}\n{injection.Lines("Restarting full transfer", "refused", "Lost connection to Duet", "Connection to controller has been reset", "Duet Control Server v")}");
            }
            else if (injection.Count(line) == 0)
            {
                failures.Add($"{spec}: repaired, but without \"{line}\"\n{injection.Lines("Restarting full transfer", "RepRapFirmware", "Bad header")}");
            }
        }

        Assert.That(failures, Is.Empty, "Faults in the header response that the plain link did not repair as expected:\n" + string.Join("\n", failures));
        if (notInjected.Count > 0)
        {
            Assert.Inconclusive($"No transfer of the kind came along for: {string.Join(", ", notInjected)}");
        }
    }
}
