using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using UnitTests.SPI;
using Outcome = UnitTests.SPI.LinkRecovery.Outcome;
using Variant = UnitTests.SPI.LinkRecovery.Variant;

namespace UnitTests.Fuzz;

/// <summary>
/// Bit errors on both data lines of the same SPI exchange at once, MOSI towards RRF and MISO towards DSF, as a disturbance that hits the
/// cable. A common disturbance hits the same SCLK edge, so half the inputs flip the same clocked bit on both lines, the other half independent
/// ones. Mostly one bit per line, sometimes two or three, sometimes a short burst of adjacent bits
/// </summary>
/// <remarks>
/// What the design promises: R1 of design/sbc-link-requirements.md forbids a reset because of any corrupted transfer or failed checksum, and R3 asks
/// for automatic recovery from bursts and ESD (criterion B). R2 and the census cover only single faults, and FakeRrfSpi counts a disturbance on both
/// lines as more than one (ExchangeDirection.Both), so the census leaves it out. The code repairs both sides at once where both see the error:
/// each answers a bad header with BadHeaderChecksum and exchanges the headers again (SPI.cs ExchangeHeader, DataTransfer.cpp:1167-1171), each
/// answers bad data with BadDataChecksum and RRF sends the data again when either side reports it (DataTransfer.cpp ExchangingDataResponse,
/// SPI.cs ExchangeData), and the extended response codes correct up to 3 bits in each direction on their own. One ESD event of the lab printer
/// hit both lines of a header (LinkRecovery.EsdEvents, 16:09:14) and is repaired. So on the extended link these tests require the repair, and
/// <see cref="ExploreBothLines"/> reports what does not hold. These tests rest on the port of RRF; a check of single faults on both lines with the
/// fault injection build of the lab printer (SpiFaultSpec direction Both) is pending
/// </remarks>
public partial class SpiLinkFuzz
{
    /// <summary>
    /// Draw bit errors on both lines of one exchange of the target transfer
    /// </summary>
    /// <param name="random">Random numbers of the input</param>
    /// <param name="scope">Transfers that get the errors</param>
    private static FlipCase GenerateBothLines(Random random, Scope scope)
    {
        FlipCase input = Generate(random, scope) with { Flips = [] };
        int exchange = random.Next(TargetExchanges(input)), bits = 8 * PlannedLength(input, exchange);

        // Positions of the errors on one line: mostly one bit, sometimes two or three, sometimes a burst of two to eight adjacent bits
        int[] Positions()
        {
            int kind = random.Next(10);
            if (kind < 7)
            {
                return [random.Next(bits)];
            }
            if (kind < 9)
            {
                return [.. Enumerable.Range(0, 2 + random.Next(2)).Select(_ => random.Next(bits)).Distinct()];
            }
            int start = random.Next(bits), length = 2 + random.Next(7);
            return [.. Enumerable.Range(start, Math.Min(length, bits - start))];
        }

        int[] toRrf = Positions();
        int[] toDsf = (random.Next(2) == 0) ? toRrf : Positions();
        return input with
        {
            Flips = [.. toRrf.Select(bit => new BitFlip(exchange, ExchangeDirection.ToRrf, bit)), .. toDsf.Select(bit => new BitFlip(exchange, ExchangeDirection.ToDsf, bit))]
        };
    }

    /// <summary>
    /// Simpler variants of a failing input that keep at least one error on each line: fewer errors, lower bits, the same bits on both lines,
    /// an earlier exchange and no polling
    /// </summary>
    private static IEnumerable<FlipCase> ShrinkBothLines(FlipCase input)
    {
        BitFlip[] toRrf = [.. input.Flips.Where(f => f.Direction == ExchangeDirection.ToRrf)], toDsf = [.. input.Flips.Where(f => f.Direction == ExchangeDirection.ToDsf)];
        FlipCase With(IEnumerable<BitFlip> rrf, IEnumerable<BitFlip> dsf) => input with { Flips = [.. rrf, .. dsf] };

        foreach (BitFlip[] line in new[] { toRrf, toDsf })
        {
            if (line.Length > 1)
            {
                foreach (BitFlip flip in line)
                {
                    yield return With(toRrf.Where(f => f != flip), toDsf.Where(f => f != flip));
                }
            }
        }
        if (toRrf.Length == toDsf.Length && !toRrf.Select(f => f.Bit).SequenceEqual(toDsf.Select(f => f.Bit)))
        {
            yield return With(toRrf, toRrf.Select(f => f with { Direction = ExchangeDirection.ToDsf }));
        }
        foreach (BitFlip flip in input.Flips)
        {
            foreach (int bit in new[] { flip.Bit / 2, flip.Bit - 1 }.Where(bit => bit >= 0 && !input.Flips.Contains(flip with { Bit = bit })))
            {
                yield return input with { Flips = [.. input.Flips.Select(f => (f == flip) ? f with { Bit = bit } : f)] };
            }
        }
        if (input.Flips[0].Exchange > 0)
        {
            yield return input with { Flips = [.. input.Flips.Select(f => f with { Exchange = f.Exchange - 1 })] };
        }
        if (input.Poll)
        {
            yield return input with { Poll = false };
        }
    }

    private static void RunBothLinesFuzzer(Scope scope, Action<FlipCase> check) =>
        Fuzzer.Run(random => GenerateBothLines(random, scope), check, input => input.ToString(), ShrinkBothLines, Inputs);

    /// <summary>
    /// With every capability active and in use by both sides, errors on both lines of one exchange are repaired through retries.
    /// The check on the hardware is pending (LinkRecovery.WriteBothLinesCases), so this rests on the port of RRF only
    /// </summary>
    [Test]
    public void ExtendedLinkRepairsErrorsOnBothLines() => RunBothLinesFuzzer(Scope.Extended, CheckRepaired);

    /// <summary>
    /// Without capabilities, and with the fork's RRF before the extended link is in use, errors on both lines of one exchange never corrupt,
    /// lose or duplicate a message unnoticed, and the link comes back
    /// </summary>
    [Test]
    public void PlainLinkStaysSafeWithErrorsOnBothLines() => RunBothLinesFuzzer(Scope.Plain, CheckSafe);

    /// <summary>
    /// In the first transfer after the activation, errors on both lines of one exchange stay safe
    /// </summary>
    [Test]
    public void RrfSwitchTransferStaysSafeWithErrorsOnBothLines() => RunBothLinesFuzzer(Scope.RrfSwitch, CheckSafe);

    /// <summary>
    /// Offsets of the census of errors on both lines in an exchange of the given length: the lowest and the highest bit of each header field,
    /// each byte of a response, and the first, middle and last byte of the data
    /// </summary>
    private static IEnumerable<int> CensusBits(int length) => length switch
    {
        16 => new[] { (0, 1), (1, 1), (2, 2), (4, 2), (6, 2), (8, 4), (12, 4) }.SelectMany(f => new[] { 8 * f.Item1, 8 * (f.Item1 + f.Item2) - 1 }),
        4 => [0, 8, 16, 24, 31],
        _ => [0, 4 * length, 8 * length - 1]
    };

    /// <summary>
    /// One flip on each line at the same offset, at every exchange of each kind of target transfer and at the offsets of <see cref="CensusBits"/>
    /// </summary>
    private static IEnumerable<FlipCase> BothLinesCensus(LinkCapabilities capabilities, int target, IEnumerable<Variant> variants) =>
        variants.SelectMany(variant =>
        {
            FlipCase input = new(capabilities, variant, target, false, []);
            return Enumerable.Range(0, TargetExchanges(input)).SelectMany(exchange => CensusBits(PlannedLength(input, exchange)).Select(bit =>
                input with { Flips = [new(exchange, ExchangeDirection.ToRrf, bit), new(exchange, ExchangeDirection.ToDsf, bit)] }));
        });

    /// <summary>
    /// Every exchange of each kind of transfer on the extended link, with an error on both lines at the same offset, is repaired through retries.
    /// The check on the hardware is pending (LinkRecovery.WriteBothLinesCases), so this rests on the port of RRF only
    /// </summary>
    [Test]
    public void ExtendedLinkRepairsBothLinesCensus()
    {
        List<string> failures = [];
        foreach (FlipCase input in ExtendedTargets.SelectMany(target => BothLinesCensus(LinkCapabilities.All, target, RegularVariants)))
        {
            try
            {
                CheckRepaired(input);
            }
            catch (FuzzFailure e)
            {
                failures.Add(e.Message);
            }
        }
        Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(20)));
    }

    /// <summary>
    /// Every exchange of each kind of transfer on the link of today, and of the connect and activation transfers of the fork's RRF, with an error
    /// on both lines at the same offset, stays safe
    /// </summary>
    [Test]
    public void PlainLinkStaysSafeBothLinesCensus()
    {
        List<string> failures = [];
        foreach (FlipCase input in BothLinesCensus(LinkCapabilities.None, LinkRecovery.TargetTransfer, Enum.GetValues<Variant>())
            .Concat(BothLinesCensus(LinkCapabilities.All, LinkRecovery.ActivationTransfer, Enum.GetValues<Variant>())))
        {
            foreach (bool poll in new[] { false, true })
            {
                try
                {
                    CheckSafe(input with { Poll = poll });
                }
                catch (FuzzFailure e)
                {
                    failures.Add(e.Message);
                }
            }
        }
        Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(20)));
    }

    /// <summary>
    /// Run many inputs with errors on both lines per scope for several seeds without stopping at the first failure, like <see cref="ExploreTwoBitErrors"/>
    /// </summary>
    [Test, Explicit("Exploration for the report, takes a while")]
    public void ExploreBothLines() => Explore(GenerateBothLines, "errors on both lines");

    /// <summary>
    /// Run many inputs of a generator per scope for several seeds without stopping at the first failure, and print the outcomes and the inputs that
    /// violate the properties of the tests of the scope. DSF_FUZZ_ITERATIONS sets the inputs per seed and scope (default 5000), DSF_FUZZ_SEEDS the
    /// number of seeds from DSF_FUZZ_SEED on (default 3)
    /// </summary>
    private void Explore(Func<Random, Scope, FlipCase> generate, string title)
    {
        int seeds = int.TryParse(Environment.GetEnvironmentVariable("DSF_FUZZ_SEEDS"), out int value) ? value : 3;
        foreach (Scope scope in Enum.GetValues<Scope>())
        {
            Dictionary<string, int> outcomes = [];
            List<(string Property, string Message)> failures = [];
            for (int seed = Fuzzer.Seed; seed < Fuzzer.Seed + seeds; seed++)
            {
                for (int i = 0; i < Fuzzer.Iterations; i++)
                {
                    // Same inputs as Fuzzer.Run draws for this seed
                    FlipCase input = generate(new Random(unchecked(seed * 1_000_003 + i)), scope);
                    LinkRecovery.ScenarioResult result = Run(input);
                    string kind = (input.Capabilities == LinkCapabilities.None) ? "RRF of today" : (input.Variant == Variant.Connect) ? "fork, connect" :
                        input.IsActivation ? "fork, activation" : $"fork, transfer {input.Target}";
                    string label = result.Label;
                    if (result.Outcome == Outcome.NoRecovery)
                    {
                        label += $" ({result.Details[..Math.Min(result.Details.Length, 16)]}...), with restart timeout {Run(input, sbcRestartTimeout: StuckLinkRestartTimeout).Label}";
                    }
                    string key = $"{kind}: {label}";
                    outcomes[key] = outcomes.GetValueOrDefault(key) + 1;
                    foreach (Action<FlipCase> check in (scope == Scope.Plain) ? [CheckSafe] : new Action<FlipCase>[] { CheckRepaired, CheckSafe })
                    {
                        try
                        {
                            check(input);
                        }
                        catch (FuzzFailure e)
                        {
                            failures.Add((e.Property, $"seed {seed}, iteration {i}: {e.Message}"));
                        }
                    }
                }
            }

            StringBuilder report = new($"{title}, {scope}: {seeds} seeds x {Fuzzer.Iterations} inputs from seed {Fuzzer.Seed}, checked for {(scope == Scope.Plain ? "safety" : "repair and safety")}\n");
            foreach ((string key, int count) in outcomes.OrderBy(o => o.Key))
            {
                report.AppendLine($"  {key}: {count}");
            }
            report.AppendLine($"  {failures.Count} inputs violate the property");
            foreach (var group in failures.GroupBy(f => f.Property))
            {
                report.AppendLine($"  {group.Key}: {group.Count()}");
                foreach ((_, string message) in group.Take(12))
                {
                    report.AppendLine($"  - {message}");
                }
            }
            TestContext.Out.WriteLine(report.ToString());
        }
    }
}
