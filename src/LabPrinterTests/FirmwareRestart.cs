using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
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
/// </remarks>
[TestFixture, Category("Restart")]
public class FirmwareRestart : LabTest
{
    private const int Restarts = 5;

    [Test]
    public async Task LinkRecoversAfterWatchdogReset()
    {
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

            await CheckObjectModelKeysAsync(run);
        }
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
