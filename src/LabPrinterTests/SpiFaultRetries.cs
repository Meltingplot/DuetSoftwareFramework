using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Single faults that the retries of the SPI protocol must repair, without a restart of the full transfer or a lost connection
/// </summary>
/// <remarks>
/// Needs the fault injection build of DCS: run-lab-tests.sh --side-load --fault-injection --filter "FullyQualifiedName~SpiFaultRetries".
/// No case file. With the fixes in, no test costs a restart. A regression that breaks the link costs a DCS restart and M999, and on the fork the lost link
/// also costs an emergency stop with ATX off (Lab.RecoverAsync, which SpiFaultTest.InjectAsync calls for every case that does not end in Retry)
/// </remarks>
[TestFixture, Category("FaultInjection"), Explicit("Needs the fault injection build of DCS")]
public class SpiFaultRetries : SpiFaultTest
{
    /// <summary>
    /// Warning of DSF for each data exchange whose data from RRF has a bad checksum (SPI.ExchangeData, SPI.cs:2161)
    /// </summary>
    private const string BadDataLine = "Bad data CRC32";

    /// <summary>
    /// Start of every warning of DSF that restarts the full transfer (SPI.ExchangeHeader, SPI.ExchangeData, SPI.ExchangeDataResponse)
    /// </summary>
    private const string RestartLine = "Restarting full transfer because";

    /// <summary>
    /// One bit error in the data of RRF on its way to DSF, in a transfer in which only RRF has data. Bit 0 is in the packet header that starts
    /// every data of RRF (RRF SbcMessageFormats.h:101-107), so it is always inside the checksum; a later bit such as 128 can lie beyond data that
    /// carries only a code buffer update (12 bytes, RRF DataTransfer.cpp:1514, SbcMessageFormats.h:226-230) and then corrupts nothing
    /// </summary>
    private const string BadDataFault = "RrfData Data ToDsf flip 0";

    /// <summary>
    /// <see cref="BadDataFault"/> in nine data exchanges in a row of the same transfer, which are its first nine attempts
    /// </summary>
    private const string NineBadDataFaults = "RrfData Data ToDsf times=9 flip 0";

    /// <summary>
    /// One bit error in the data of RRF that only DSF got wrong: RRF got the (empty) data of DSF and answers Success. RRF sends its data again when
    /// either side reports a bad data checksum (RRF DataTransfer.cpp:1252-1257 at 0d4295065), so since DSF #105 DSF exchanges the data again on that
    /// Success as well (SPI.cs:2162-2182), which costs one data retry. Before #105 DSF answered the Success with BadResponse and restarted the full
    /// transfer, "Restarting full transfer because an unexpected response code has been received (code 0x00000001)" (SPI.cs:2177), on the extended link
    /// even twice: 4 of 5 SPI events of the ESD test on 2026-11-03 cost that (PR #105). Unfixed the link still recovers, so the outcome stays Retry and only
    /// the restart line and counter show the regression. Offline: UnitTests LinkRecovery.BadDataOnlyDsfGotIsExchangedAgain (every bit, both links) and
    /// RecoveryCounter("RrfData / Data / ToDsf / Payload / flip bit 128", extended). The F-1 run with the ESD case file passes with and without #105,
    /// because its outcome is Retry either way. Deterministic: the fault hits the next transfer with data of RRF only. Costs nothing, fixed or not
    /// </summary>
    [Test]
    public async Task BadRrfDataTakesOneDataRetry()
    {
        TestContext.Progress.WriteLine($"DCS uses the {await Lab.GetLinkModeAsync() ?? "unknown"} link");
        Dictionary<string, int> before = await ReadLinkCountersAsync();
        Injection injection = await InjectAsync(BadDataFault);
        if (injection.Kind is null)
        {
            Assert.Inconclusive("No transfer with data of RRF only came along within 10 s");
        }
        Assert.That(injection.Outcome, Is.EqualTo("Retry"), $"The link did not repair the bad data:\n{injection.Lines(RestartLine, "Lost connection to Duet", "Connection to controller has been reset")}");

        Dictionary<string, int> changes = Changes(before, await ReadLinkCountersAsync());
        TestContext.Progress.WriteLine($"{injection.Spec} hit {injection.Kind}: {Describe(changes)}");
        Assert.Multiple(() =>
        {
            Assert.That(injection.Count(BadDataLine), Is.EqualTo(1), $"DSF did not log one bad data checksum:\n{injection.Lines(BadDataLine, RestartLine)}");
            Assert.That(injection.Count(RestartLine), Is.Zero, $"The bad data cost a restart of the full transfer (#105):\n{injection.Lines(RestartLine)}");
            Assert.That(changes.GetValueOrDefault("data retries"), Is.EqualTo(1), $"M122 \"DSF\": {Describe(changes)}");
            Assert.That(changes.GetValueOrDefault("Full transfer restarts"), Is.Zero, $"M122 \"DSF\": {Describe(changes)}");
        });
    }

    /// <summary>
    /// Bit errors in nine data exchanges in a row of one transfer. SPI.ExchangeData makes MaxSbcRetries attempts (SPI.cs:2136) and, since #105, exchanges
    /// the data again after each of them, so with the limit of 10 the tenth exchange completes the transfer. Before DSF #98 the limit was 3
    /// (Settings.cs:193 and the packaged config.json:26 now say 10): the third bad exchange ended the attempts, "SPI connection reset because the number of
    /// maximum retries has been exceeded" (SPI.cs:2211) made DSF lose the connection, "Lost connection to Duet" (SPI.cs:475), and RRF stopped on the lost link
    /// (RRF SbcInterface.cpp:239, :294-299). Without #105 each bad exchange costs a full transfer restart (SPI.cs:2177) instead, which the restart line and
    /// counter show. Checked offline against the census port through the injector on v3.7-dev 520765373, with a probe that is not checked in: limit 10
    /// gives 9 data retries and no lost connection, limit 3 a lost connection after 3 exchanges.
    /// The limit comes from the image's /opt/dsf/conf/config.json, which the side-load does not replace (run-lab-tests.py:12), also under its old name
    /// MaxSpiRetries (Settings.cs:465, :527-531). The test is ignored when that file sets less than 10; without the key the default of the DCS under test applies.
    /// Deterministic: the fault hits the next transfer with data of RRF only and ends after nine exchanges (SpiFaultSpec.Times). Costs nothing with the
    /// fixes; a lost connection costs an emergency stop with ATX off, a DCS restart and M999
    /// </summary>
    [Test]
    public async Task NineBadDataExchangesKeepTheConnection()
    {
        int? configured = await ReadConfiguredMaxSbcRetriesAsync();
        if (configured < 10)
        {
            Assert.Ignore($"The image's config.json sets MaxSbcRetries {configured}, which the side-load does not change, so nine bad exchanges must lose the connection");
        }
        TestContext.Progress.WriteLine($"MaxSbcRetries {(configured is null ? "not set in config.json, default of the DCS under test" : configured.Value.ToString(CultureInfo.InvariantCulture))}, " +
            $"DCS uses the {await Lab.GetLinkModeAsync() ?? "unknown"} link");

        Dictionary<string, int> before = await ReadLinkCountersAsync();
        Injection injection = await InjectAsync(NineBadDataFaults);
        if (injection.Kind is null)
        {
            Assert.Inconclusive("No transfer with data of RRF only came along within 10 s");
        }
        Assert.That(injection.Outcome, Is.EqualTo("Retry"), "Nine bad data exchanges broke the link:\n" +
            injection.Lines("number of maximum retries", "Maximum number of SPI", "Lost connection to Duet", "Connection to controller has been reset", RestartLine));

        Dictionary<string, int> changes = Changes(before, await ReadLinkCountersAsync());
        TestContext.Progress.WriteLine($"{injection.Spec} hit {injection.Kind}: {Describe(changes)}");
        Assert.Multiple(() =>
        {
            Assert.That(injection.Count($"SPI fault injected: {NineBadDataFaults},"), Is.EqualTo(9), "The fault did not hit nine exchanges of the transfer");
            Assert.That(injection.Count(BadDataLine), Is.EqualTo(9), $"DSF did not log nine bad data checksums:\n{injection.Lines(BadDataLine, RestartLine)}");
            Assert.That(injection.Count(RestartLine), Is.Zero, $"The bad data cost restarts of the full transfer (#105):\n{injection.Lines(RestartLine)}");
            Assert.That(changes.GetValueOrDefault("data retries"), Is.EqualTo(9), $"M122 \"DSF\": {Describe(changes)}");
            Assert.That(changes.GetValueOrDefault("Full transfer restarts"), Is.Zero, $"M122 \"DSF\": {Describe(changes)}");
            Assert.That(changes.GetValueOrDefault("lost connections"), Is.Zero, $"M122 \"DSF\": {Describe(changes)}");
        });
    }

    /// <summary>
    /// Read MaxSbcRetries from the image's config.json, where DCS reads it (Settings.DefaultConfigFile, Program.cs:165; the unit passes no arguments,
    /// duetcontrolserver.service:7). The current key wins over the old one, and keys are not case-sensitive (Settings.cs:495-531)
    /// </summary>
    /// <returns>Value the file sets, or null if it sets none</returns>
    private static async Task<int?> ReadConfiguredMaxSbcRetriesAsync()
    {
        string output = (await Lab.RunOnSbcAsync("""
            command -v python3 >/dev/null || { echo 'python3 is missing'; exit 0; }
            sudo -n python3 - <<'EOF'
            import json
            with open('/opt/dsf/conf/config.json') as f:
                settings = {key.lower(): value for key, value in json.load(f).items()}
            print(settings.get('maxsbcretries', settings.get('maxspiretries', '')))
            EOF
            """)).Trim();
        if (output == "python3 is missing")
        {
            Assert.Ignore("The SBC has no python3 to read config.json");
        }
        if (output.Length == 0)
        {
            return null;
        }
        if (!int.TryParse(output, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            Assert.Ignore($"MaxSbcRetries in config.json is no number: {output}");
        }
        return value;
    }

    /// <summary>
    /// Response words that read as a refusal of DSF's header, on the extended link, which DCS uses with the RRF on the printer
    /// </summary>
    private static readonly (string Spec, uint Code)[] RefusalWords =
    [
        ("BothData HeaderResponse ToDsf word 0x00000002", 2),
        ("DsfData HeaderResponse ToDsf word 0x00000003", 3),
        ("RrfData HeaderResponse ToDsf word 0x00000004", 4),
        ("NoData HeaderResponse ToDsf word 0x00000003", 3)
    ];

    /// <summary>
    /// A response of RRF to DSF's header that reads as BadFormat, BadProtocolVersion or BadDataLength (2, 3, 4, TransferResponse.cs:19-29) is retried.
    /// On the extended link the response words are Walsh codes and up to 3 bit errors are corrected (ExtendedLink.cs:162, :193-203), so only a fault of the
    /// whole word gets there; a word that is no extended code passes as it is. Before DSF #91 the switch after DSF's Success threw "RepRapFirmware refused
    /// message format / protocol version / data length" for these codes, the exception ended the transfer thread (LinkService.cs:182-228), DCS stopped
    /// and systemd started it again (duetcontrolserver.service:9-10), and RRF stopped on the lost link (RRF SbcInterface.cpp:239, :294-299). Now they take the
    /// default branch, "Restarting full transfer because a bad header response was received (0x0000000N)" (SPI.cs:2074), which goes on with the next transfer
    /// without data (:2075-2081) and else sends BadResponse and restarts the transfer (:2083-2085). Offline: UnitTests LinkRecovery.ExtendedLinkRecoversFromEverySingleFault
    /// has these words for the response of every kind of transfer (LinkRecovery.cs:1240-1244, :1290-1296); the line was checked through the injector.
    /// The plain-link half is SpiFaultInjection.PlainLinkHeaderResponseFaultsRecover. Deterministic: each fault hits the next transfer of its kind.
    /// Costs nothing with the fix; unfixed each case costs a DCS restart, an emergency stop with ATX off and M999 (4 in all)
    /// </summary>
    [Test]
    public async Task RefusalWordInHeaderResponseIsRetried()
    {
        await RequireLinkAsync("extended");
        List<string> failures = [], notInjected = [];
        foreach ((string spec, uint code) in RefusalWords)
        {
            Injection injection = await InjectAsync(spec);
            if (injection.Kind is null)
            {
                notInjected.Add(spec);
                continue;
            }

            string line = $"Restarting full transfer because a bad header response was received (0x{code:x8})";
            TestContext.Progress.WriteLine($"{spec} hit {injection.Kind}: {injection.Outcome}");
            if (injection.Outcome != "Retry")
            {
                failures.Add($"{spec}: {injection.Outcome}\n{injection.Lines("refused", "Lost connection to Duet", "Connection to controller has been reset", "Duet Control Server v")}");
            }
            else if (injection.Count(line) == 0)
            {
                failures.Add($"{spec}: repaired, but without \"{line}\"\n{injection.Lines(RestartLine, "RepRapFirmware")}");
            }
        }

        Assert.That(failures, Is.Empty, "Refusal words in the header response that the link did not retry:\n" + string.Join("\n", failures));
        if (notInjected.Count > 0)
        {
            Assert.Inconclusive($"No transfer of the kind came along for: {string.Join(", ", notInjected)}");
        }
    }
}
