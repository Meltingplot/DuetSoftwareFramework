using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// RRF restarts while DCS keeps running
/// </summary>
/// <remarks>
/// - DSF 3.7.0+mp.8 never recovered in 4 of 5 watchdog resets, because every retry resent a TX header with a protocol version from a bad header (fixed in #59)
/// - After an SPI timeout, object model responses shifted by one query (fork check 4dc381d0, upstream 4a1ec34c)
/// - Each reset is also checked in the journal of DCS and in the file names of the main board, see <see cref="LinkRecoversAfterWatchdogReset"/>
/// </remarks>
[TestFixture, Category("Restart")]
public class FirmwareRestart : LabTest
{
    /// <summary>
    /// Number of watchdog resets, 5 unless LAB_WATCHDOG_RESTARTS sets another one, e.g. to look for a rare reboot race
    /// </summary>
    private static readonly int Restarts = int.TryParse(Environment.GetEnvironmentVariable("LAB_WATCHDOG_RESTARTS"), out int restarts) && restarts > 0 ? restarts : 5;

    /// <summary>
    /// Longest time on the extended link from the last "Restarting full transfer because" line to "Connection to controller has been reset". Fixed: 0.30 s
    /// after the 0xff header of the hung RRF (2026-11-21 journal, 10:54:13.046 to .351) or about 0.02 s after the 0x00000000 response of the booting one (trace
    /// in RRF PR #36). Without RRF #36 the 500 ms stall comes on top of the boot, about 0.78 s. The time includes the boot of RRF up to DataTransfer::Init
    /// (0.28 s in the trace), so a slower boot of a later RRF moves it as well, which is why every reset prints it
    /// </summary>
    private const double MaxDetectionSeconds = 0.6;

    /// <summary>
    /// Lines that no reset may cause: a lost connection (SPI.cs:475), a header that failed its checksum (SPI.cs:1956), a transfer sent again because RRF
    /// repeated a number (SPI.cs:2026) and RRF answering a header with a header (SPI.cs:2070)
    /// </summary>
    private static readonly string[] ForbiddenLines =
    [
        "Lost connection to Duet", "Bad header CRC16", "Sending transfer #", "RepRapFirmware sent a header instead of a header response"
    ];

    /// <summary>
    /// Verbose file names of the main board (RRF Platform.cpp:220-229) that M997 and DuetControlServer -u need
    /// </summary>
    private static readonly string[] FileNameKeys = ["firmwareFileName", "firmwareDate", "iapFileNameSBC", "iapFileNameSD"];

    /// <summary>
    /// Each watchdog reset (M122 P1001, RRF Platform.cpp:1650-1657) must end with the printer idle again and the object model answering, and the journal of DCS
    /// must show one clean restart of RRF (design/lab-test-plan.md, section 14: 40 resets on 2026-11-07 all looked like this):
    /// <list type="bullet">
    /// <item>exactly one "Connection to controller has been reset" (LinkService.cs:383) and, on the extended link, one "RepRapFirmware has left the extended
    /// link" (SPI.cs:2014-2022), which is how DCS notices a restarted RRF there because it numbers its transfers anew (#93b)</item>
    /// <item>none of <see cref="ForbiddenLines"/></item>
    /// <item>on the extended link, the reset line at most <see cref="MaxDetectionSeconds"/> after the last "Restarting full transfer because" line, which
    /// DCS logs when it reads the hung or booting RRF (SPI.cs:1910, :2074)</item>
    /// <item>5 s after the printer is idle, the verbose file names of the main board are back in the object model of DCS, unchanged (#115)</item>
    /// </list>
    /// What the builds without the fixes show:
    /// <list type="bullet">
    /// <item>RRF #36 (SbcBootArmDelay, RRF SbcInterface.cpp:46, :137): without the 10 ms delay DCS answers the falling TfrRdy edge of DataTransfer::Init
    /// 20-30 µs before RRF arms its first header. Either both sides wait for RRF's 500 ms transfer timeout, so the reset comes 0.5 s later than above, or
    /// the exchanges run out of step until the 4 s connection timeout of DCS, which logs "Lost connection to Duet" and a second reset. Each of the 10 reboots
    /// recorded on the unfixed RRF (PR #36, 2026-11-02) showed one of the two; the census of DSF #100, which also places the answer of DCS away from the edge,
    /// shows a stall in about half. So 5 resets catch a regression with at least 1 - 0.5^5 = 97 %</item>
    /// <item>RRF #41 (DecodeResponse while not connected, RRF DataTransfer.cpp:906-915): about one restart in 40 stayed one exchange out of step, with
    /// "Bad header CRC16" until "Maximum number of SPI transfer retries exceeded" and "Lost connection to Duet". 5 resets catch it with 12 %,
    /// LAB_WATCHDOG_RESTARTS=40 with 64 %</item>
    /// <item>DSF #115: ConnectionLost cleared the sequence numbers while the update service could still start a query, so it read the model without the
    /// verbose flag (fix: ObjectModel.cs:887-896, UpdateService.cs:239-249). The names were gone after 9 of 10 resets (PR #115), so 5 resets miss it with
    /// about 1e-5. A session that subscribes with verbose=true (DuetWebServer WebSocketEndpoint.cs:47) makes DCS read them again and hides the bug; the
    /// sessions of these tests do not (DuetHttpClient RestConnector.cs:199)</item>
    /// <item>DSF #93b and #92b are only guarded here. #93b missed a restart right after RRF's 16-bit sequence number wrapped, once per 65,536 transfers;
    /// #92b took a restarted RRF that numbers from 1 for a repeat (SPI.cs:2011-2012), which RRF did before RRF #36 when its connection timeout restarted
    /// the link at 1 a second time. Neither can be aimed at, a regression shows only if a reset happens to hit the case</item>
    /// <item>DSF #59: a regression shows only after a lost connection that followed a header with a bad protocol version (SPI.cs:1916-1921, :463-464),
    /// which today's reboots do not cause. The link then does not recover and the test fails after 90 s</item>
    /// </list>
    /// A run collects the findings of every reset and fails at its end
    /// </summary>
    [Test]
    public async Task LinkRecoversAfterWatchdogReset()
    {
        string? linkMode = await Lab.GetLinkModeAsync();
        Dictionary<string, string?> fileNames = await QueryMainBoardFileNamesAsync();
        TestContext.Progress.WriteLine($"Link: {linkMode ?? "unknown"}, main board: {string.Join(", ", fileNames.Select(item => $"{item.Key} {item.Value ?? "missing"}"))}");
        Assert.That(fileNames.Values, Has.All.Not.Null.And.Not.Empty, "DCS lacks file names of the main board before the first reset");

        List<string> findings = [];
        for (int run = 1; run <= Restarts; run++)
        {
            string cursor = await Lab.GetJournalCursorAsync();
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                // Watchdog reset, see M122 in the G-code dictionary
                await SendCodeAsync("M122 P1001");
            }
            catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
            {
                // RRF may restart before it replies
            }

            // DCS loses the link while RRF boots, then reports it idle again with a fresh up time
            bool lost = await Lab.TryWaitForModelAsync(Session, model => !Lab.IsIdle(model), TimeSpan.FromSeconds(30));
            bool recovered = lost && await Lab.TryWaitForModelAsync(Session, model => Lab.IsIdle(model) && model.State.UpTime <= stopwatch.Elapsed.TotalSeconds + 5, TimeSpan.FromSeconds(90));
            if (!recovered)
            {
                string journal = await Lab.ReadJournalAsync(cursor);
                await Lab.RecoverAsync();
                Assert.Fail($"Run {run}: {(lost ? "the link did not recover within 90 s" : "RRF did not restart")} " +
                            $"({Regex.Count(journal, "bad header checksum")} bad header checksum reports in the journal)");
            }
            TestContext.Progress.WriteLine($"Run {run}: idle again after {stopwatch.Elapsed.TotalSeconds:0.0} s");
            Stopwatch sinceIdle = Stopwatch.StartNew();

            CheckRebootJournal(run, await RestartHelpers.ReadTimedJournalAsync(cursor), linkMode, findings);
            await CheckObjectModelKeysAsync(run);

            // PR #115 read the names 5 s after the printer was idle again
            TimeSpan wait = TimeSpan.FromSeconds(5) - sinceIdle.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait);
            }
            Dictionary<string, string?> afterReset = await QueryMainBoardFileNamesAsync();
            foreach ((string key, string? value) in fileNames)
            {
                if (afterReset[key] != value)
                {
                    findings.Add($"Run {run}: boards[0].{key} is {(afterReset[key] is null ? "missing" : $"'{afterReset[key]}'")} instead of '{value}' (#115)");
                }
            }
        }
        Assert.That(findings, Is.Empty, string.Join("\n", findings));
    }

    /// <summary>
    /// Check the journal lines of one reset, see <see cref="LinkRecoversAfterWatchdogReset"/>
    /// </summary>
    private static void CheckRebootJournal(int run, RestartHelpers.JournalLine[] lines, string? linkMode, List<string> findings)
    {
        int resets = RestartHelpers.Count(lines, "Connection to controller has been reset"), left = RestartHelpers.Count(lines, "RepRapFirmware has left the extended link");
        if (resets != 1)
        {
            findings.Add($"Run {run}: {resets} times \"Connection to controller has been reset\" instead of once");
        }
        if (linkMode == "extended" && left != 1)
        {
            findings.Add($"Run {run}: {left} times \"RepRapFirmware has left the extended link\" instead of once (#93b)");
        }
        foreach (string text in ForbiddenLines)
        {
            int count = RestartHelpers.Count(lines, text);
            if (count > 0)
            {
                findings.Add($"Run {run}: {count} times \"{text}\"");
            }
        }

        // Time from the reaction of DCS to the restart to its detection
        int reset = Array.FindIndex(lines, line => line.Text.Contains("Connection to controller has been reset", StringComparison.Ordinal));
        int restart = (reset < 0) ? -1 : Array.FindLastIndex(lines, reset, line => line.Text.Contains("Restarting full transfer because", StringComparison.Ordinal));
        if (restart < 0)
        {
            TestContext.Progress.WriteLine($"Run {run}: no \"Restarting full transfer because\" line before the reset, detection time not measured");
            return;
        }
        double detection = lines[reset].Time - lines[restart].Time;
        TestContext.Progress.WriteLine($"Run {run}: reset detected {detection:0.000} s after \"{lines[restart].Text[(lines[restart].Text.IndexOf("Restarting", StringComparison.Ordinal))..]}\"");
        if (linkMode == "extended" && detection > MaxDetectionSeconds)
        {
            findings.Add($"Run {run}: the reset was detected {detection:0.000} s after the last full transfer restart, more than {MaxDetectionSeconds} s (RRF #36)");
        }
    }

    /// <summary>
    /// Read the file names of the main board from the object model of DCS. M409 without an index in the key and with the flag v is answered by DSF from its
    /// own model and includes the verbose fields (MCodeHandler.cs:844-880, Filter.cs QueryFlags 'v'); a key with an index would go to RRF (:853)
    /// </summary>
    /// <returns>Value of each of <see cref="FileNameKeys"/>, null if it is missing or empty</returns>
    private async Task<Dictionary<string, string?>> QueryMainBoardFileNamesAsync()
    {
        using JsonDocument reply = JsonDocument.Parse(await SendCodeAsync("M409 K\"boards\" F\"v\""));
        JsonElement mainBoard = reply.RootElement.GetProperty("result")[0];
        return FileNameKeys.ToDictionary(key => key, key =>
            mainBoard.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null);
    }

    /// <summary>
    /// Every query must get the answer to its own key, and the answer must match the object model DCS reports
    /// </summary>
    /// <remarks>
    /// DSF forwards M409 with an index in the key to RRF
    /// </remarks>
    private async Task CheckObjectModelKeysAsync(int run)
    {
        char[] letters = Lab.Read(Session, model => model.Move.Axes.Select(axis => axis.Letter).ToArray());
        Assert.That(letters, Is.Not.Empty, $"Run {run}: DCS reports no axes");

        for (int i = 0; i < 50; i++)
        {
            int axis = i % letters.Length;
            string key = $"move.axes[{axis}].letter";
            Assert.That(await SendCodeAsync($"M409 K\"{key}\""), Does.Contain($"\"key\":\"{key}\"").And.Contain($"\"result\":\"{letters[axis]}\""), $"Run {run}, query {i + 1}");
        }
    }
}
