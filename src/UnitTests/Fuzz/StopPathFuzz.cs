using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using UnitTests.SPI;
using Flip = UnitTests.SPI.StopPath.Flip;
using Leg = UnitTests.SPI.StopPath.Leg;
using Restart = UnitTests.SPI.StopPath.Restart;
using Scenario = UnitTests.SPI.StopPath.Scenario;
using StopCase = UnitTests.SPI.StopPath.StopCase;
using StopMoment = UnitTests.SPI.StopPath.StopMoment;

namespace UnitTests.Fuzz;

/// <summary>
/// The stop path under random faults on the SPI link (<see cref="StopPath"/>): DSF requests an emergency stop, RRF requests a trigger macro, or both
/// at once, while bit errors hit the exchanges around them, both data lines of one exchange are disturbed, random bit errors hit every exchange,
/// or RRF restarts right before
/// </summary>
/// <remarks>
/// The properties are those the code guarantees (StopPath.RunAsync): the stop is never lost while the link is not stuck, it reaches RRF once unless a side
/// reconnected, M112 does not fail, and without a reconnect the macro starts once and its codes reach RRF once each and in order. That M112 replies only once
/// RRF has been stopped holds on a clean link (StopPath.EmergencyStopOnCleanLink) but not under every fault (StopPath.M112RepliesOnlyOnceRrfIsStopped), so it is
/// measured here. How long the stop and the start of the macro take is measured, not asserted, because nothing in the code bounds it: R10 of
/// design/sbc-link-requirements.md is not implemented (implementation status, R10-R14). <see cref="ExploreStopPath"/> reports the distributions and the cases
/// above the 100 ms of R10, <see cref="ExploreProperties"/> the properties over many seeds
/// </remarks>
[TestFixture]
public class StopPathFuzz
{
    #region Inputs
    /// <summary>
    /// Kind of faults of an input
    /// </summary>
    public enum FaultModel
    {
        /// <summary>
        /// No fault
        /// </summary>
        Clean,

        /// <summary>
        /// Two bit errors around the transfers of the scenario
        /// </summary>
        TwoFlips,

        /// <summary>
        /// Bit errors on both data lines of one exchange (SpiLinkFuzz.BothLines)
        /// </summary>
        BothLines,

        /// <summary>
        /// Random bit errors in every exchange while a part of the scenario runs
        /// </summary>
        Noise,

        /// <summary>
        /// RRF restarts right before the stop or the macro request
        /// </summary>
        Restart
    }

    /// <summary>
    /// Exchanges of each part of the scenario that faults hit: the transfer DSF or RRF has set up when the request comes, the one that carries it and
    /// the one after it (StopPath.Leg), and for the code part the transfer of the first code only, because what follows depends on how DSF batches the codes
    /// </summary>
    private static int Window(Leg leg) => (leg == Leg.Code) ? 4 : 10;

    /// <summary>
    /// Bits a fault may hit, taken modulo the length of the exchange: a multiple of the 32 bits of a response and the 128 bits of a header
    /// </summary>
    private const int BitRange = 256;

    /// <summary>
    /// Bit error rates of <see cref="FaultModel.Noise"/>, around the 1e-3 of LinkRecovery.ExtendedLinkSurvivesBitErrors
    /// </summary>
    private static readonly double[] BitErrorRates = [3e-4, 1e-3, 3e-3];

    /// <summary>
    /// Boot times of a restarting RRF, as in the census (LinkRecovery.RestartBootTimes)
    /// </summary>
    private static readonly double[] BootTimes = [100, 2000];

    /// <summary>
    /// Parts of the scenario that faults can hit
    /// </summary>
    private static Leg[] Legs(StopCase input) => input.Scenario switch
    {
        Scenario.Stop => [Leg.Stop],
        Scenario.Trigger => [Leg.Request, Leg.Code],
        _ => (input.StopAt == StopMoment.FirstCode) ? [Leg.Request, Leg.Code, Leg.Stop] : [Leg.Request, Leg.Stop]
    };

    /// <summary>
    /// Draw an input of the given kind
    /// </summary>
    /// <param name="random">Random numbers of the input</param>
    /// <param name="scenario">What happens on the link</param>
    /// <param name="capabilities">Extended or plain link</param>
    /// <param name="model">Kind of faults</param>
    /// <param name="paced">Whether RRF waits between transfers like an idle RRF with the default M576 S25 (only for the emergency stop)</param>
    internal static StopCase Generate(Random random, Scenario scenario, LinkCapabilities capabilities, FaultModel model, bool paced = false)
    {
        StopCase input = new(scenario, capabilities, [])
        {
            Macro = (random.Next(2) == 0) ? "trigger2.g" : "trigger4.g",
            StopAt = Enum.GetValues<StopMoment>()[random.Next(3)],
            Offset = random.Next(1 << ExtendedLink.CounterBits)
        };
        if (paced && scenario == Scenario.Stop)
        {
            input = input with { Delay = FakeRrfSpi.DefaultTransferDelay, Phase = random.Next(100) / 100.0 };
        }

        Leg[] legs = Legs(input);
        ExchangeDirection Direction() => (random.Next(2) == 0) ? ExchangeDirection.ToRrf : ExchangeDirection.ToDsf;
        switch (model)
        {
            case FaultModel.TwoFlips:
            {
                Leg leg = legs[random.Next(legs.Length)];
                Flip first = new(leg, random.Next(Window(leg)), Direction(), random.Next(BitRange));
                Flip second;
                do
                {
                    Leg secondLeg = (random.Next(3) == 0) ? legs[random.Next(legs.Length)] : leg;
                    second = (random.Next(4) == 0 && secondLeg == leg)
                        ? first with { Direction = Direction(), Bit = random.Next(BitRange) }
                        : new(secondLeg, random.Next((secondLeg == leg) ? first.Exchange : 0, Window(secondLeg)), Direction(), random.Next(BitRange));
                }
                while (second == first);
                return input with { Flips = [first, second] };
            }

            case FaultModel.BothLines:
            {
                // Mostly one bit per line, sometimes two or three, sometimes a burst of adjacent bits, half of the time the same on both lines
                Leg leg = legs[random.Next(legs.Length)];
                int exchange = random.Next(Window(leg));
                int[] Positions()
                {
                    int kind = random.Next(10);
                    if (kind < 7)
                    {
                        return [random.Next(BitRange / 2)];
                    }
                    if (kind < 9)
                    {
                        return [.. Enumerable.Range(0, 2 + random.Next(2)).Select(_ => random.Next(BitRange / 2)).Distinct()];
                    }
                    int start = random.Next(BitRange / 2);
                    return [.. Enumerable.Range(start, 2 + random.Next(7))];
                }
                int[] toRrf = Positions(), toDsf = (random.Next(2) == 0) ? toRrf : Positions();
                return input with
                {
                    Flips = [.. toRrf.Select(bit => new Flip(leg, exchange, ExchangeDirection.ToRrf, bit)), .. toDsf.Select(bit => new Flip(leg, exchange, ExchangeDirection.ToDsf, bit))]
                };
            }

            case FaultModel.Noise:
                return input with { BitErrorRate = BitErrorRates[random.Next(BitErrorRates.Length)], Seed = random.Next(1, int.MaxValue) };

            case FaultModel.Restart:
            {
                // Before the header of the transfer that is set up when the stop is requested (0) or of the one that carries it (2); before the header
                // of the transfer that carries the macro request (2) or MacroStarted (6)
                Leg leg = (scenario == Scenario.Trigger || (scenario == Scenario.Both && random.Next(2) == 0)) ? Leg.Request : Leg.Stop;
                int exchange = (leg == Leg.Request) ? ((random.Next(2) == 0) ? 2 : 6) : 2 * random.Next(2);
                return input with { Restart = new(leg, exchange, BootTimes[random.Next(BootTimes.Length)]) };
            }
        }
        return input;
    }

    /// <summary>
    /// Draw any input: a scenario, a link and a kind of faults, sometimes two kinds at once
    /// </summary>
    internal static StopCase GenerateAny(Random random)
    {
        Scenario scenario = Enum.GetValues<Scenario>()[random.Next(3)];
        LinkCapabilities capabilities = (random.Next(2) == 0) ? LinkCapabilities.All : LinkCapabilities.None;
        FaultModel model = Enum.GetValues<FaultModel>()[1 + random.Next(4)];
        StopCase input = Generate(random, scenario, capabilities, model, paced: random.Next(4) == 0);
        if (random.Next(4) == 0 && model != FaultModel.Noise)
        {
            // Random bit errors on top
            input = input with { BitErrorRate = BitErrorRates[random.Next(BitErrorRates.Length)], Seed = random.Next(1, int.MaxValue) };
        }
        return input;
    }

    /// <summary>
    /// Simpler variants of an input: without the random bit errors, the restart or the wait of RRF, fewer bit errors, earlier exchanges and lower bits,
    /// the shorter macro
    /// </summary>
    internal static IEnumerable<StopCase> Shrink(StopCase input)
    {
        if (input.BitErrorRate > 0)
        {
            // The same run with the bit errors of the noise as single flips, which can be shrunk further
            if (StopPath.RunAsync(input).GetAwaiter().GetResult().NoiseAsFlips is StopCase flips)
            {
                yield return flips;
            }
            yield return input with { BitErrorRate = 0, Seed = 1 };
        }
        if (input.Restart is Restart restart)
        {
            yield return input with { Restart = null };
            if (restart.BootTime > BootTimes[0])
            {
                yield return input with { Restart = restart with { BootTime = BootTimes[0] } };
            }
            if (restart.Exchange > 0)
            {
                yield return input with { Restart = restart with { Exchange = restart.Exchange - 2 } };
            }
        }
        if (input.Delay > 0)
        {
            yield return input with { Delay = 0, Phase = 0 };
            if (input.Phase > 0)
            {
                yield return input with { Phase = 0 };
            }
        }
        if (input.Macro != "trigger2.g")
        {
            yield return input with { Macro = "trigger2.g" };
        }
        if (input.Offset > 0)
        {
            yield return input with { Offset = 0 };
        }
        for (int i = 0; i < input.Flips.Count; i++)
        {
            if (input.Flips.Count > 1)
            {
                yield return input with { Flips = [.. input.Flips.Where((_, j) => j != i)] };
            }
        }
        for (int i = 0; i < input.Flips.Count; i++)
        {
            Flip flip = input.Flips[i];
            List<Flip> candidates = [];
            if (flip.Exchange > 0)
            {
                candidates.Add(flip with { Exchange = flip.Exchange - 1 });
            }
            candidates.AddRange(new[] { flip.Bit / 2, flip.Bit - 1 }.Where(bit => bit >= 0 && bit != flip.Bit).Select(bit => flip with { Bit = bit }));
            foreach (Flip candidate in candidates.Where(c => !input.Flips.Contains(c)))
            {
                yield return input with { Flips = [.. input.Flips.Select((f, j) => (i == j) ? candidate : f)] };
            }
        }
    }
    #endregion

    #region Properties
    /// <summary>
    /// Run an input and fail with the first property it violates
    /// </summary>
    internal static void Check(StopCase input)
    {
        StopPath.StopResult result = StopPath.RunAsync(input).GetAwaiter().GetResult();
        if (result.Violations.Count > 0)
        {
            (string property, string message) = result.Violations[0];
            throw new FuzzFailure(property, $"{input}: {message}; {result.Details}");
        }
    }

    /// <summary>
    /// Run a fuzz test
    /// </summary>
    /// <param name="generate">Generator of the inputs</param>
    /// <param name="inputs">Number of inputs at the default of 5000 iterations, which keeps the three tests at about 10 s</param>
    private static void RunFuzzer(Func<Random, StopCase> generate, int inputs) =>
        Fuzzer.Run(generate, Check, input => input.ToString(), Shrink, Fuzzer.Scale(inputs), TimeSpan.FromSeconds(30));

    /// <summary>
    /// An emergency stop under random faults on both kinds of link: it is never lost, reaches RRF once without a reconnect, and M112 does not fail
    /// </summary>
    [Test]
    public void EmergencyStopUnderFaults() => RunFuzzer(random =>
        Generate(random, Scenario.Stop, (random.Next(2) == 0) ? LinkCapabilities.All : LinkCapabilities.None, Enum.GetValues<FaultModel>()[1 + random.Next(4)], random.Next(4) == 0), 400);

    /// <summary>
    /// The start of a trigger macro under random faults on both kinds of link: without a reconnect, the macro starts once and its codes reach RRF once
    /// each, in order and completely
    /// </summary>
    [Test]
    public void TriggerMacroUnderFaults() => RunFuzzer(random =>
        Generate(random, Scenario.Trigger, (random.Next(2) == 0) ? LinkCapabilities.All : LinkCapabilities.None, Enum.GetValues<FaultModel>()[1 + random.Next(4)]), 200);

    /// <summary>
    /// Any of the scenarios with any of the faults, including an emergency stop while a trigger macro starts
    /// </summary>
    [Test]
    public void StopPathUnderFaults() => RunFuzzer(GenerateAny, 300);
    #endregion

    #region Exploration
    /// <summary>
    /// Kinds of input of the measurement: every scenario on both links with each kind of fault, and the emergency stop with an idle RRF that waits
    /// between transfers
    /// </summary>
    private static IEnumerable<(string Name, Func<Random, StopCase> Generate)> Variants()
    {
        foreach (Scenario scenario in new[] { Scenario.Stop, Scenario.Trigger })
        {
            foreach (LinkCapabilities capabilities in new[] { LinkCapabilities.All, LinkCapabilities.None })
            {
                string link = (capabilities == LinkCapabilities.All) ? "extended" : "plain";
                foreach (FaultModel model in Enum.GetValues<FaultModel>())
                {
                    yield return ($"{scenario}, {link}, {model}", random => Generate(random, scenario, capabilities, model));
                    if (scenario == Scenario.Stop)
                    {
                        yield return ($"{scenario}, {link}, {model}, RRF waits {FakeRrfSpi.DefaultTransferDelay} ms", random => Generate(random, scenario, capabilities, model, true));
                    }
                }
            }
        }
        foreach (LinkCapabilities capabilities in new[] { LinkCapabilities.All, LinkCapabilities.None })
        {
            yield return ($"Both, {((capabilities == LinkCapabilities.All) ? "extended" : "plain")}, any fault", random =>
            {
                StopCase input = Generate(random, Scenario.Both, capabilities, Enum.GetValues<FaultModel>()[random.Next(5)]);
                return input;
            });
        }
    }

    /// <summary>
    /// Limit of R10 for the stop (design/sbc-link-requirements.md), also the process safety time of the door (same document, SB2 and the decision log)
    /// </summary>
    private const double R10Limit = 100;

    /// <summary>
    /// Measure every kind of input for several seeds and print, per kind, the distribution of the time until RRF is stopped and until the first code
    /// of the macro reaches it, the properties that fail, and the slowest inputs, shrunk to the simplest one that still takes longer than the limit of R10.
    /// DSF_FUZZ_ITERATIONS sets the inputs per seed and kind (default 5000), DSF_FUZZ_SEEDS the number of seeds from DSF_FUZZ_SEED on (default 1),
    /// DSF_STOP_VARIANT a part of the name of the kinds to run
    /// </summary>
    [Test, Explicit("Measurement for the report, takes a while")]
    public void ExploreStopPath()
    {
        int seeds = int.TryParse(Environment.GetEnvironmentVariable("DSF_FUZZ_SEEDS"), out int value) ? value : 1;
        string? filter = Environment.GetEnvironmentVariable("DSF_STOP_VARIANT");
        StringBuilder report = new($"{seeds} seeds x {Fuzzer.Iterations} inputs per kind from seed {Fuzzer.Seed}\n");
        int total = 0;
        foreach ((string name, Func<Random, StopCase> generate) in Variants().Where(v => filter is null || v.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            // Without faults every input of a kind is the same but for the phase of the wait, so fewer of them show that the scenario is deterministic
            List<(StopCase Input, StopPath.StopResult Result)> results = [];
            int iterations = (name.Contains(nameof(FaultModel.Clean)) && !name.Contains("waits")) ? Math.Min(Fuzzer.Iterations, 20) : Fuzzer.Iterations;
            for (int seed = Fuzzer.Seed; seed < Fuzzer.Seed + seeds; seed++)
            {
                for (int i = 0; i < iterations; i++)
                {
                    StopCase input = generate(new Random(unchecked(seed * 1_000_003 + i)));
                    results.Add((input, StopPath.RunAsync(input).GetAwaiter().GetResult()));
                }
            }
            total += results.Count;
            report.AppendLine($"{name}: {results.Count} inputs, {results.Count(r => r.Result.Reconnected)} with a reconnect, {results.Count(r => r.Result.Stuck)} stuck");
            if (results.Any(r => r.Result.Reconnected))
            {
                report.AppendLine("  reconnects: " + string.Join(", ", results.Where(r => r.Result.Reconnected).GroupBy(r => r.Result.ReconnectKind).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
            }
            if (results.Any(r => r.Result.TriggerOutcome.Length > 0))
            {
                report.AppendLine("  trigger macro: " + string.Join(", ", results.GroupBy(r => r.Result.TriggerOutcome + (r.Result.Stuck ? " (stuck)" : "")).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
                foreach ((StopCase input, StopPath.StopResult result) in results.Where(r => r.Result.TriggerOutcome == "lost" && !r.Result.Stuck).Take(2))
                {
                    report.AppendLine($"  LOST: {input}; {result.Details}");
                    report.AppendLine($"    shrunk: {Shrunk(input, r => r.TriggerOutcome == "lost" && !r.Stuck)}");
                }
            }
            if (results.Any(r => r.Result.HaltLatency is not null))
            {
                List<double> leads = [.. results.Where(r => r.Result.HaltBeforeStop).Select(r => (r.Result.StopLatency ?? double.PositiveInfinity) - r.Result.HaltLatency!.Value)];
                report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  M112 completes before RRF is stopped: {leads.Count}{(leads.Count > 0 ? $", by up to {leads.Max():F3} ms" : "")}, " +
                    $"before RRF had the request with a valid checksum: {results.Count(r => r.Result.HaltBeforeData)}"));
                if (leads.Count > 0)
                {
                    var early = results.Where(r => r.Result.HaltBeforeStop).MaxBy(r => (r.Result.StopLatency ?? double.PositiveInfinity) - r.Result.HaltLatency!.Value);
                    report.AppendLine($"  - {early.Input}: {early.Result.Details}");
                    report.AppendLine($"    shrunk: {Shrunk(early.Input, r => r.HaltBeforeStop)}");
                }
                if (results.FirstOrDefault(r => r.Result.HaltBeforeData) is var beforeData && beforeData.Input is not null)
                {
                    report.AppendLine($"  - {beforeData.Input}: {beforeData.Result.Details}");
                    report.AppendLine($"    shrunk: {Shrunk(beforeData.Input, r => r.HaltBeforeData)}");
                }
            }
            Summarize(report, "  stop", results, r => r.StopLatency, r => r.StoppedBy);
            Summarize(report, "  stop request", results, r => r.PacketLatency, null);
            Summarize(report, "  macro started", results, r => r.MacroStartedLatency, null);
            Summarize(report, "  first code", results, r => r.FirstCodeLatency, null);
            Summarize(report, "  M112 reply", results, r => r.HaltLatency, null);
            foreach (var group in results.SelectMany(r => r.Result.Violations.Select(v => (r.Input, r.Result, v.Property, v.Message))).GroupBy(v => v.Property))
            {
                report.AppendLine($"  VIOLATED {group.Key}: {group.Count()}");
                foreach (var violation in group.Take(5))
                {
                    report.AppendLine($"  - {violation.Input}: {violation.Message}; {violation.Result.Details}");
                }
            }

            // Stuck links, which only a restart of DCS ends
            foreach ((StopCase input, StopPath.StopResult result) in results.Where(r => r.Result.Stuck).Take(3))
            {
                report.AppendLine($"  STUCK: {input}; {result.Details}");
                report.AppendLine($"    shrunk: {Shrunk(input, r => r.Stuck)}");
            }

            // The slowest inputs above the limit, each shrunk to the simplest one that stays above it
            foreach (Func<StopPath.StopResult, double?> latency in new Func<StopPath.StopResult, double?>[] { r => r.StopLatency, r => r.FirstCodeLatency })
            {
                foreach ((StopCase input, StopPath.StopResult result) in results.Where(r => latency(r.Result) > R10Limit).OrderByDescending(r => latency(r.Result)).Take(3))
                {
                    report.AppendLine($"  SLOW {latency(result):F1} ms: {input}; {result.Details}");
                    report.AppendLine($"    shrunk: {Shrunk(input, r => latency(r) > R10Limit)}");
                }
            }
            TestContext.Out.WriteLine(report.ToString());
            report.Clear();
        }
        TestContext.Out.WriteLine($"{total} inputs in all");
    }

    /// <summary>
    /// Print the median, the 99th percentile and the maximum of a latency, how many inputs exceed the limit of R10 and how many never got there
    /// </summary>
    private static void Summarize(StringBuilder report, string what, List<(StopCase Input, StopPath.StopResult Result)> results, Func<StopPath.StopResult, double?> latency,
        Func<StopPath.StopResult, string>? cause)
    {
        List<(StopPath.StopResult Result, double? Value)> values = [.. results.Select(r => (r.Result, latency(r.Result)))];
        List<double> reached = [.. values.Where(v => v.Value is not null).Select(v => v.Value!.Value).Order()];
        if (reached.Count == 0 && values.All(v => v.Value is null) && what != "  stop")
        {
            return;
        }
        if (reached.Count == 0)
        {
            report.AppendLine($"{what}: never in {values.Count}");
            return;
        }
        double Percentile(double p) => reached[Math.Min(reached.Count - 1, (int)Math.Ceiling(p * reached.Count) - 1)];
        string causes = (cause is null) ? "" : ", by " + string.Join(", ", values.Where(v => v.Value is not null).GroupBy(v => cause(v.Result)).Select(g => $"{g.Key} {g.Count()}"));
        report.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{what}: median {Percentile(0.5):F3} ms, p99 {Percentile(0.99):F3} ms, max {reached[^1]:F3} ms, > {R10Limit} ms {reached.Count(v => v > R10Limit)}, never {values.Count - reached.Count}{causes}"));
    }

    /// <summary>
    /// Shrink an input and describe it with what happened
    /// </summary>
    private static string Shrunk(StopCase input, Func<StopPath.StopResult, bool> keeps)
    {
        StopCase shrunk = ShrinkWhile(input, keeps);
        return $"{shrunk}; {StopPath.RunAsync(shrunk).GetAwaiter().GetResult().Details}";
    }

    /// <summary>
    /// Shrink an input greedily while the result keeps the given property
    /// </summary>
    private static StopCase ShrinkWhile(StopCase input, Func<StopPath.StopResult, bool> keeps)
    {
        for (int budget = 200; budget > 0;)
        {
            StopCase? next = null;
            foreach (StopCase candidate in Shrink(input))
            {
                if (budget-- <= 0)
                {
                    break;
                }
                if (keeps(StopPath.RunAsync(candidate).GetAwaiter().GetResult()))
                {
                    next = candidate;
                    break;
                }
            }
            if (next is null)
            {
                break;
            }
            input = next;
        }
        return input;
    }

    /// <summary>
    /// Run many inputs of <see cref="GenerateAny"/> for several seeds without stopping at the first failure, and print the violated properties with
    /// their shrunk inputs. DSF_FUZZ_ITERATIONS sets the inputs per seed (default 5000), DSF_FUZZ_SEEDS the number of seeds from DSF_FUZZ_SEED on (default 1)
    /// </summary>
    [Test, Explicit("Exploration for the report, takes a while")]
    public void ExploreProperties()
    {
        int seeds = int.TryParse(Environment.GetEnvironmentVariable("DSF_FUZZ_SEEDS"), out int value) ? value : 1;
        Dictionary<string, int> kinds = [];
        List<(string Property, StopCase Input, string Message)> failures = [];
        List<(StopCase Input, long Milliseconds, string Details)> slowest = [];
        for (int seed = Fuzzer.Seed; seed < Fuzzer.Seed + seeds; seed++)
        {
            for (int i = 0; i < Fuzzer.Iterations; i++)
            {
                // Same inputs as StopPathUnderFaults draws for this seed
                StopCase input = GenerateAny(new Random(unchecked(seed * 1_000_003 + i)));
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                StopPath.StopResult result = StopPath.RunAsync(input).GetAwaiter().GetResult();
                slowest.Add((input, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, result.Details));
                string kind = $"{input.Scenario} {input.Capabilities}{(result.Reconnected ? ", reconnected" : "")}{(result.Stuck ? ", stuck" : "")}";
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                failures.AddRange(result.Violations.Select(v => (v.Property, input, $"seed {seed}, iteration {i}: {v.Message}; {result.Details}")));
            }
        }

        StringBuilder report = new($"{seeds} seeds x {Fuzzer.Iterations} inputs from seed {Fuzzer.Seed}\n");
        foreach ((StopCase input, long ms, string details) in slowest.OrderByDescending(s => s.Milliseconds).Take(10))
        {
            report.AppendLine($"  {ms} ms real time: {input}; {details}");
        }
        foreach ((string kind, int count) in kinds.OrderBy(k => k.Key))
        {
            report.AppendLine($"  {kind}: {count}");
        }
        report.AppendLine($"  {failures.Count} violations");
        foreach (var group in failures.GroupBy(f => f.Property))
        {
            report.AppendLine($"  {group.Key}: {group.Count()}");
            foreach ((_, StopCase input, string message) in group.Take(8))
            {
                StopCase shrunk = ShrinkWhile(input, r => r.Violations.Any(v => v.Property == group.Key));
                report.AppendLine($"  - {input}: {message}");
                report.AppendLine($"    shrunk: {shrunk}");
            }
        }
        TestContext.Out.WriteLine(report.ToString());
    }
    #endregion
}
