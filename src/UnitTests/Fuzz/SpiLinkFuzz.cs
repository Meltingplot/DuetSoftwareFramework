using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using NUnit.Framework;
using UnitTests.SPI;
using Outcome = UnitTests.SPI.LinkRecovery.Outcome;
using Variant = UnitTests.SPI.LinkRecovery.Variant;

namespace UnitTests.Fuzz;

/// <summary>
/// Two random bit errors in the SPI traffic between DSF and the SPI port of RRF (<see cref="FakeRrfSpi"/>), in the scenario of the
/// single-fault census (<see cref="LinkRecovery"/>). Each error flips one bit of one exchange in one direction. The two errors may hit
/// the same exchange, two exchanges of the same transfer or two nearby transfers
/// </summary>
/// <remarks>
/// Why two errors must be harmless: headers and data carry a CRC-32 (DSF: Link/Adapter/SPI.cs WriteCRC and ExchangeHeader/ExchangeData;
/// RRF: src/SBC/DataTransfer.cpp CalcCRC32 at the header and data checks). Both use the reflected polynomial 0xEDB88320 with an initial
/// value and final XOR of 0xFFFFFFFF (DSF Utility/CRC32.cs, RRF src/Storage/CRC32.cpp), and the header CRC covers the first 12 bytes
/// including the data CRC. A CRC with this primitive generator detects every error of one or two bits in a codeword shorter than
/// 2^32 - 1 bits, which <see cref="ChecksumsDetectEveryErrorOfUpToTwoBits"/> confirms for the exchanges of these scenarios.
/// On the extended link the response codes are 16 bits apart and corrected up to 3 bit errors (ExtendedLink.cs, tested by
/// ExtendedLinkCodes), while the regular codes of the RRF of today differ by single bits (TransferResponse.cs)
/// </remarks>
[TestFixture]
public partial class SpiLinkFuzz
{
    /// <summary>
    /// A bit error
    /// </summary>
    /// <param name="Exchange">SPI exchange that gets it, counted from the header of the target transfer. Retries shift the exchanges after an error</param>
    /// <param name="Direction">Direction that gets it</param>
    /// <param name="Bit">Bit that flips, counted from bit 0 of the first byte, modulo the length of the exchange</param>
    public sealed record BitFlip(int Exchange, ExchangeDirection Direction, int Bit);

    /// <summary>
    /// Input of the fuzz tests
    /// </summary>
    /// <param name="Capabilities">Capabilities RRF offers and DSF activates in the activation transfer</param>
    /// <param name="Variant">Kind of the target transfer</param>
    /// <param name="Target">Number of the target transfer, ignored for <see cref="Variant.Connect"/></param>
    /// <param name="Poll">Whether RRF polls while it is not connected</param>
    /// <param name="Flips">Bit errors</param>
    public sealed record FlipCase(LinkCapabilities Capabilities, Variant Variant, int Target, bool Poll, IReadOnlyList<BitFlip> Flips)
    {
        /// <summary>
        /// Text that <see cref="Parse"/> reads, for regression tests
        /// </summary>
        public override string ToString() =>
            $"{Capabilities} {Variant} {Target} {(Poll ? "poll" : "nopoll")}" + string.Concat(Flips.Select(f => $" | {f.Exchange} {f.Direction} {f.Bit}"));

        /// <summary>
        /// Read an input from its text
        /// </summary>
        public static FlipCase Parse(string text)
        {
            string[] parts = text.Split(" | ");
            string[] head = parts[0].Split(' ');
            return new(Enum.Parse<LinkCapabilities>(head[0]), Enum.Parse<Variant>(head[1]), int.Parse(head[2], CultureInfo.InvariantCulture), head[3] == "poll",
                [.. parts.Skip(1).Select(p => p.Split(' ')).Select(f => new BitFlip(int.Parse(f[0], CultureInfo.InvariantCulture), Enum.Parse<ExchangeDirection>(f[1]),
                    int.Parse(f[2], CultureInfo.InvariantCulture)))]);
        }

        /// <summary>
        /// Whether DSF activates the capabilities in the target transfer, which then carries the activation request
        /// </summary>
        public bool IsActivation => Capabilities != LinkCapabilities.None && Variant != Variant.Connect && Target == LinkRecovery.ActivationTransfer;
    }

    private readonly LinkRecovery _recovery = new();

    [OneTimeSetUp]
    public void SetUp() => _recovery.SetUp();

    [OneTimeTearDown]
    public void TearDown() => _recovery.TearDown();

    #region Inputs
    /// <summary>
    /// Transfers that get the bit errors
    /// </summary>
    public enum Scope
    {
        /// <summary>
        /// Both sides use the extended link: a regular transfer and the first one in which DSF uses it (LinkRecovery.ExtendedTransfer)
        /// </summary>
        Extended,

        /// <summary>
        /// The first transfer after the activation, in which RRF uses the extended link and DSF still sends regular response codes,
        /// because it switches only once it has seen an extended header (ExtendedLink remarks, DataTransfer::SequenceDistance)
        /// </summary>
        RrfSwitch,

        /// <summary>
        /// The RRF of today without capabilities, and the fork's RRF before the extended link is in use: the connect transfer and the
        /// transfer that activates the capabilities
        /// </summary>
        Plain
    }

    /// <summary>
    /// Target transfers in which both sides use the extended link, see <see cref="Scope.Extended"/>
    /// </summary>
    private static readonly int[] ExtendedTargets = [LinkRecovery.ExtendedTransfer + 2, LinkRecovery.ExtendedTransfer];

    private static readonly Variant[] RegularVariants = [Variant.BothData, Variant.RrfData, Variant.DsfData, Variant.NoData];

    /// <summary>
    /// Bytes of the message packet of RRF ("rrf:N", DataTransfer pads only before a following packet) and of DSF ("dsf:N", padded by Writer.WriteMessage),
    /// and of the activation request (packet header and capabilities), see LinkRecovery.RrfDataSize and DsfDataSize
    /// </summary>
    private const int RrfDataSize = 21, DsfDataSize = 24, ActivationSize = 12;

    /// <summary>
    /// Number of exchanges of the target transfer without errors
    /// </summary>
    private static int TargetExchanges(FlipCase input) => (input.Variant == Variant.NoData && !input.IsActivation) ? 2 : 4;

    /// <summary>
    /// Length in bytes of an exchange if the errors before it cause no retry. The transfers after the target one have data in both directions
    /// </summary>
    private static int PlannedLength(FlipCase input, int exchange)
    {
        int targetExchanges = TargetExchanges(input);
        if (exchange >= targetExchanges)
        {
            return ((exchange - targetExchanges) % 4) switch { 0 => 16, 2 => DsfDataSize, _ => 4 };
        }
        bool rrfData = input.Variant is Variant.Connect or Variant.BothData or Variant.RrfData, dsfData = input.Variant is Variant.BothData or Variant.DsfData;
        return exchange switch
        {
            0 => 16,
            2 => Math.Max(rrfData ? RrfDataSize : 0, (dsfData ? DsfDataSize : 0) + (input.IsActivation ? ActivationSize : 0)),
            _ => 4
        };
    }

    /// <summary>
    /// Draw two bit errors: the first one in an exchange of the target transfer, the second one in the same exchange, a later one of the same
    /// transfer or one of the two transfers after it
    /// </summary>
    private static FlipCase Generate(Random random, Scope scope)
    {
        bool poll = random.Next(2) == 0;
        FlipCase input = scope switch
        {
            Scope.Extended => new(LinkCapabilities.All, RegularVariants[random.Next(RegularVariants.Length)], ExtendedTargets[random.Next(ExtendedTargets.Length)], poll, []),
            Scope.RrfSwitch => new(LinkCapabilities.All, RegularVariants[random.Next(RegularVariants.Length)], LinkRecovery.ActivationTransfer + 1, poll, []),
            // The RRF of today, or the fork's RRF in the connect transfer and in the transfer that activates the capabilities
            _ => (random.Next(4) != 0)
                ? new(LinkCapabilities.None, Enum.GetValues<Variant>()[random.Next(5)], LinkRecovery.TargetTransfer, poll, [])
                : new(LinkCapabilities.All, Enum.GetValues<Variant>()[random.Next(5)], LinkRecovery.ActivationTransfer, poll, [])
        };

        ExchangeDirection Direction() => (random.Next(2) == 0) ? ExchangeDirection.ToRrf : ExchangeDirection.ToDsf;
        int targetExchanges = TargetExchanges(input);
        int first = random.Next(targetExchanges);
        BitFlip firstFlip = new(first, Direction(), random.Next(8 * PlannedLength(input, first)));

        int second = (random.Next(4) == 0) ? first : random.Next(first + 1, targetExchanges + 8);
        BitFlip secondFlip;
        do
        {
            secondFlip = new(second, Direction(), random.Next(8 * PlannedLength(input, second)));
        }
        while (secondFlip == firstFlip);
        return input with { Flips = [firstFlip, secondFlip] };
    }

    /// <summary>
    /// Simpler variants of a failing input: one error alone, an error in an earlier exchange or a lower bit, and no polling
    /// </summary>
    private static IEnumerable<FlipCase> Shrink(FlipCase input)
    {
        if (input.Flips.Count > 1)
        {
            foreach (BitFlip flip in input.Flips)
            {
                yield return input with { Flips = [flip] };
            }
        }
        for (int i = 0; i < input.Flips.Count; i++)
        {
            BitFlip flip = input.Flips[i];
            if (flip.Exchange > 0)
            {
                yield return input with { Flips = [.. input.Flips.Select((f, j) => (i == j) ? f with { Exchange = f.Exchange - 1 } : f)] };
            }
            foreach (int bit in new[] { flip.Bit / 2, flip.Bit - 1 }.Where(bit => bit >= 0 && bit != flip.Bit))
            {
                yield return input with { Flips = [.. input.Flips.Select((f, j) => (i == j) ? f with { Bit = bit } : f)] };
            }
        }
        if (input.Poll)
        {
            yield return input with { Poll = false };
        }
    }
    #endregion

    #region Scenario
    /// <summary>
    /// Run the scenario of the census with the bit errors of the given input
    /// </summary>
    /// <param name="input">Bit errors and where they hit</param>
    /// <param name="trace">Trace of the exchanges, recorded if set</param>
    /// <param name="sbcRestartTimeout">Restart timeout of DCS in ms instead of the default of 60 s</param>
    private LinkRecovery.ScenarioResult Run(FlipCase input, List<string>? trace = null, int? sbcRestartTimeout = null)
    {
        List<SpiFault> faults = [.. input.Flips.Select(flip => new SpiFault(flip.Exchange, flip.Direction, data =>
        {
            if (data.Length > 0)
            {
                int bit = flip.Bit % (data.Length * 8);
                data[bit / 8] ^= (byte)(1 << (bit % 8));
            }
        }, Mutation: $"flip {flip.Bit}"))];
        LinkRecovery.RrfOptions options = new(PollWhileDisconnected: input.Poll, Capabilities: input.Capabilities, TargetTransfer: input.Target, SbcRestartTimeout: sbcRestartTimeout);
        return _recovery.RunScenario(input.Variant, faults, options, trace);
    }

    /// <summary>
    /// Outcomes that break the safety of the link: DCS stops with an error, a message is lost or duplicated without a reconnect that tells
    /// both sides about it, or a side takes in data the other side never sent, which counts even with a reconnect
    /// </summary>
    private static readonly Outcome[] UnsafeOutcomes = [Outcome.Fatal, Outcome.Duplicate, Outcome.SilentLoss, Outcome.Corrupted, Outcome.ResetMissed];

    /// <summary>
    /// Restart timeout of DCS that ends a stuck link in the scenario, as in LinkRecovery.StuckLinkRestartsDcs instead of the default of 60 s
    /// </summary>
    private const int StuckLinkRestartTimeout = 200;

    /// <summary>
    /// Property of the extended link: every error of two bits is repaired by the retries of the protocol within the time limit, without a reconnect
    /// of either side and without losing, duplicating or corrupting a message
    /// </summary>
    private void CheckRepaired(FlipCase input)
    {
        LinkRecovery.ScenarioResult result = Run(input);
        if (result.Outcome != Outcome.Retry)
        {
            throw new FuzzFailure($"not repaired: {result.Outcome}", $"{input}: {result.Label}, {result.Details}");
        }
    }

    /// <summary>
    /// Property of the link without the extended capabilities: two bit errors may need a reconnect, as single ones do (27 of 4142 in the census,
    /// design/sbc-link-requirements.md R1/R2), but they must never corrupt, lose or duplicate a message unnoticed, nor stop DCS with an error.
    /// A link that does not recover within the budget of the scenario is stuck in a way no timeout of RRF ends. DCS then stops after
    /// Settings.SbcRestartTimeout (SPI.PerformFullTransfer) and systemd starts it again, and that must bring the link back.
    /// This is checked for an RRF that polls while it is not connected, as the builds for the MB6HC, MB6XD and Mini 5+ do: they support SBC
    /// over USB (Pins_Duet3_MB6HC.h:43, Pins_Duet3_MB6XD.h:28, Pins_Duet3Mini.h:42 with CoreN2G Core.h:82) and then wait at most
    /// SbcConnectionTimeout while not connected (SbcInterface.cpp:162-169). Only SAME70 boards offer link capabilities
    /// (design/sbc-link-requirements.md C3), so every RRF with capabilities polls. The SBC build for the Duet 2 does not poll
    /// (Pins_DuetNG.h:42-45), and there two bit errors can stop the link for good, see <see cref="StuckWithoutPolling"/>
    /// </summary>
    /// <param name="input">Bit errors and where they hit</param>
    private void CheckSafe(FlipCase input) => CheckSafe(input, input.Poll);

    /// <summary>
    /// See <see cref="CheckSafe(FlipCase)"/>
    /// </summary>
    /// <param name="input">Bit errors and where they hit</param>
    /// <param name="mustComeBack">Whether a stuck link must come back through a restart of DCS</param>
    private void CheckSafe(FlipCase input, bool mustComeBack)
    {
        LinkRecovery.ScenarioResult result = Run(input);
        if (UnsafeOutcomes.Contains(result.Outcome))
        {
            throw new FuzzFailure($"unsafe: {result.Outcome}", $"{input}: {result.Label}, {result.Details}");
        }
        if (result.Outcome == Outcome.NoRecovery && mustComeBack)
        {
            LinkRecovery.ScenarioResult restarted = Run(input, sbcRestartTimeout: StuckLinkRestartTimeout);
            if (restarted.Outcome == Outcome.NoRecovery || UnsafeOutcomes.Contains(restarted.Outcome))
            {
                throw new FuzzFailure($"stuck: {restarted.Outcome}", $"{input}: {result.Details}; with a restart timeout of {StuckLinkRestartTimeout} ms: {restarted.Label}, {restarted.Details}");
            }
        }
    }
    #endregion

    /// <summary>
    /// Number of inputs per property at the default of 5000 iterations
    /// </summary>
    private static int Inputs => Fuzzer.Scale(4000);

    private static void RunFuzzer(Scope scope, Action<FlipCase> check) => Fuzzer.Run(random => Generate(random, scope), check, input => input.ToString(), Shrink, Inputs);

    /// <summary>
    /// With every capability active and in use by both sides, two bit errors anywhere in one or two nearby transfers are repaired through retries
    /// </summary>
    [Test]
    public void ExtendedLinkRepairsTwoBitErrors() => RunFuzzer(Scope.Extended, CheckRepaired);

    /// <summary>
    /// Two bit errors in the first transfer after the activation are not all repaired, see <see cref="TwoBitErrorsInRrfSwitchTransfer"/>
    /// </summary>
    [Test, Explicit("1 in 1000 pairs of bit errors in the first transfer after the activation needs a reconnect and an e-stop of RRF")]
    public void RrfSwitchTransferRepairsTwoBitErrors() => RunFuzzer(Scope.RrfSwitch, CheckRepaired);

    /// <summary>
    /// The first transfer after the activation stays safe with two bit errors, although it does not always repair them
    /// </summary>
    [Test]
    public void RrfSwitchTransferStaysSafeWithTwoBitErrors() => RunFuzzer(Scope.RrfSwitch, CheckSafe);

    /// <summary>
    /// Without capabilities, and with the fork's RRF before the extended link is in use, two bit errors never corrupt, lose or duplicate a message
    /// unnoticed, and the link always comes back
    /// </summary>
    [Test]
    public void PlainLinkStaysSafeWithTwoBitErrors() => RunFuzzer(Scope.Plain, CheckSafe);

    /// <summary>
    /// Two bit errors in the first transfer after the activation: a bit error in RRF's header makes DSF answer with the regular code
    /// BadHeaderChecksum, because DSF uses extended codes only once it has seen an extended header (SPI.cs ExchangeResponse, _extendedTx).
    /// The second error turns that 5 into a 4 (BadDataLength), which RRF answers by restarting the transfer with BadResponse
    /// (DataTransfer.cpp:1173, RestartTransfer). DSF reads it in place of the next header, repeats the data response, gets a header
    /// and gives up (SPI.cs:1832-1836), so it loses the connection and leaves the extended link (EndSession). It never sent an extended
    /// sequence number, so RRF cannot tell (DataTransfer::SequenceDistance) and keeps sending extended codes that DSF no longer decodes,
    /// until the connection timeout of RRF ends the transfer after 4 s with an emergency stop. Without data in the transfer, RRF continues
    /// with the next header instead of sending BadResponse (DataTransfer::RestartTransfer), and the errors are repaired
    /// </summary>
    /// <param name="text">Input as the fuzz tests print it</param>
    [TestCase("All RrfData 3 nopoll | 0 ToDsf 0 | 1 ToRrf 0")]
    [TestCase("All BothData 3 nopoll | 0 ToDsf 0 | 1 ToRrf 0")]
    [TestCase("All DsfData 3 nopoll | 0 ToDsf 0 | 1 ToRrf 0")]
    [Explicit("Two bit errors in the first transfer after the activation need a reconnect and an e-stop of RRF")]
    public void TwoBitErrorsInRrfSwitchTransfer(string text) => CheckRepaired(FlipCase.Parse(text));

    /// <summary>
    /// Two bit errors that leave the link stuck for good if RRF does not poll while it is not connected, on the link of today and in the first
    /// transfer after the activation. The first error turns the regular Success of DSF into BadHeaderChecksum (1 to 5), so RRF repeats its header
    /// while DSF exchanges its data. DSF answers the result with BadResponse, which RRF reads in the header phase as a request to repeat the
    /// data response (DataTransfer.cpp:1087-1095 and 1033-1035). The second error hits the format code of the next header of DSF, so RRF
    /// ends ExchangingDataResponseRetry with a connection reset (DataTransfer.cpp:1258), which keeps the level of TfrRdy (DataTransfer.cpp:1434-1438,
    /// SbcInterface.cpp:287). The sequence number of RRF has skipped one from the view of DSF, so DSF waits for a resend (SPI.cs:1969-1975) that
    /// RRF, without a sequence guard after the reset, never sends, until DSF gives up and loses the connection. From then on DSF waits for TfrRdy
    /// to be high, as for every first transfer (SPI.cs:1693-1711), while RRF waits with TfrRdy low and without a timeout, because it is not
    /// connected (SbcInterface.cpp:171). No restart of DCS changes that, only a restart of RRF. With polling, the same errors end in a reconnect
    /// </summary>
    /// <param name="text">Input as the fuzz tests print it</param>
    [TestCase("None BothData 4 nopoll | 1 ToRrf 2 | 5 ToRrf 3")]
    [TestCase("All BothData 3 nopoll | 1 ToRrf 2 | 5 ToRrf 3")]
    [Explicit("Two bit errors leave a non-polling RRF (Duet 2 SBC build) waiting for good with TfrRdy low")]
    public void StuckWithoutPolling(string text) => CheckSafe(FlipCase.Parse(text), true);

    /// <summary>
    /// The errors of <see cref="StuckWithoutPolling"/> with an RRF that polls while it is not connected
    /// </summary>
    /// <param name="text">Input as the fuzz tests print it</param>
    [TestCase("None BothData 4 poll | 1 ToRrf 2 | 5 ToRrf 3")]
    [TestCase("All BothData 3 poll | 1 ToRrf 2 | 5 ToRrf 3")]
    public void StuckWithoutPollingComesBackWithPolling(string text) => CheckSafe(FlipCase.Parse(text), true);

    /// <summary>
    /// The CRC-32 of headers and data detects every error of up to two bits in the exchanges of these scenarios, which is what makes
    /// <see cref="ExtendedLinkRepairsTwoBitErrors"/> a requirement and not a matter of luck
    /// </summary>
    [Test]
    public void ChecksumsDetectEveryErrorOfUpToTwoBits()
    {
        Random random = new(1);

        // Header: the CRC covers the first 12 bytes and sits in the last 4 (TransferHeader.ChecksumHeader32). Data: the CRC sits in the header,
        // which a single bit error there makes fail on its own, so the data CRC only needs to catch errors that leave the header intact
        foreach (int length in new[] { 12, RrfDataSize, DsfDataSize, DsfDataSize + ActivationSize })
        {
            byte[] codeword = new byte[length + sizeof(uint)];
            random.NextBytes(codeword.AsSpan(0, length));
            BitConverter.TryWriteBytes(codeword.AsSpan(length), CRC32.Calculate(codeword.AsSpan(0, length)));
            int bits = codeword.Length * 8;
            for (int a = 0; a < bits; a++)
            {
                for (int b = a; b < bits; b++)
                {
                    codeword[a / 8] ^= (byte)(1 << (a % 8));
                    codeword[b / 8] ^= (byte)((b != a) ? 1 << (b % 8) : 0);
                    bool detected = CRC32.Calculate(codeword.AsSpan(0, length)) != BitConverter.ToUInt32(codeword, length);
                    codeword[a / 8] ^= (byte)(1 << (a % 8));
                    codeword[b / 8] ^= (byte)((b != a) ? 1 << (b % 8) : 0);
                    if (!detected)
                    {
                        Assert.Fail($"Bits {a} and {b} of a {length}-byte block with its CRC are not detected");
                    }
                }
            }
        }
    }

    #region Exploration
    /// <summary>
    /// Run many inputs with two bit errors per scope for several seeds without stopping at the first failure, and print the outcomes and the inputs
    /// that violate the properties of the tests of the scope, see <see cref="Explore"/>
    /// </summary>
    [Test, Explicit("Exploration for the report, takes a while")]
    public void ExploreTwoBitErrors() => Explore(Generate, "two bit errors");


    /// <summary>
    /// Print the exchanges of a single input, for analysing a failure
    /// </summary>
    /// <param name="text">Input as the fuzz tests print it</param>
    [TestCase("All BothData 3 nopoll | 1 ToRrf 2 | 5 ToRrf 3"), Explicit("Analysis")]
    public void TraceInput(string text)
    {
        List<string> trace = [];
        LinkRecovery.ScenarioResult result = Run(FlipCase.Parse(text), trace);
        TestContext.Out.WriteLine($"{text}: {result.Label}, {result.Details}");
        foreach (string line in trace.Take(300))
        {
            TestContext.Out.WriteLine(line);
        }
    }
    #endregion
}
