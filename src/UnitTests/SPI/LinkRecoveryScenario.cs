using System;
using System.Collections.Generic;
using System.Linq;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace UnitTests.SPI;

/// <summary>
/// Access to the scenario of the census for other fixtures, such as the fuzz tests of the link
/// </summary>
public partial class LinkRecovery
{
    /// <summary>
    /// How the link dealt with the faults of a scenario
    /// </summary>
    /// <param name="Outcome">Classification of the census</param>
    /// <param name="Label">Classification with the emergency stop of RRF and duplicates after a reconnect</param>
    /// <param name="Details">What happened</param>
    internal sealed record ScenarioResult(Outcome Outcome, string Label, string Details);

    /// <summary>
    /// Connect, perform clean transfers, corrupt the target transfer and the ones after it, and classify how the link recovered, like the census does.
    /// Call <see cref="SetUp"/> before and <see cref="TearDown"/> after
    /// </summary>
    /// <param name="variant">Kind of the target transfer</param>
    /// <param name="faults">Faults, each with its exchange counted from the start of the target transfer</param>
    /// <param name="options">Firmware end</param>
    /// <param name="trace">Trace of the exchanges and of what DSF logged, recorded if set</param>
    /// <returns>Outcome</returns>
    internal ScenarioResult RunScenario(Variant variant, IReadOnlyList<SpiFault> faults, RrfOptions options, List<string>? trace = null)
    {
        Result result = Run(variant, (faults.Count > 0) ? faults[0] : null, options, trace, moreFaults: (faults.Count > 1) ? [.. faults.Skip(1)] : null);
        return new(result.Outcome, result.Label, result.Details);
    }

    /// <summary>
    /// Faults on both data lines of one exchange for the lab printer, as exchange, direction and mutation of SpiFaultSpec. The fault injection
    /// build applies the mutation to both directions (SpiFaultInjection.cs, FaultInjectingSpiDevice.TransferFullDuplex), so the same bits flip
    /// on MOSI and MISO. The last one puts 8 bit errors into both response words, more than the extended codes correct
    /// </summary>
    private static readonly string[] BothLinesLabFaults =
    [
        "Header Both flip 32", "HeaderResponse Both flip 2", "Data Both flip 100", "DataResponse Both flip 0", "HeaderResponse Both ones 0 1"
    ];

    /// <summary>
    /// Write <see cref="BothLinesLabFaults"/> for the fault injection build on the lab printer, with the outcome the scenario of the census predicts
    /// for each kind of transfer, in the format of <see cref="WriteHardwareCases"/>. Run every case with LAB_SPI_FAULT_SELECT=all, because all are
    /// predicted to be repaired. The file goes to the path in LAB_SPI_FAULT_CASES, or next to the test assembly
    /// </summary>
    [Test, Explicit("Writes the cases for src/LabPrinterTests/SpiFaultInjection.cs")]
    public void WriteBothLinesCases()
    {
        static SpiTransferKind Kind(Variant variant) => variant switch
        {
            Variant.NoData => SpiTransferKind.NoData,
            Variant.BothData => SpiTransferKind.BothData,
            Variant.DsfData => SpiTransferKind.DsfData,
            _ => SpiTransferKind.RrfData
        };

        // The lab printer has an MB6HC, which polls while it is not connected
        RrfOptions options = new(PollWhileDisconnected: true, Capabilities: LinkCapabilities.All, TargetTransfer: ExtendedTransfer + 2);
        var cases = BothLinesLabFaults.Select(text =>
        {
            string[] parts = text.Split(' ', 3);
            SpiExchange exchange = Enum.Parse<SpiExchange>(parts[0]);
            SpiFault fault = new((int)exchange, ExchangeDirection.Both, data => SpiFaultSpec.Apply(parts[2], data), Mutation: parts[2]);
            Dictionary<string, string> expectedByKind = Enum.GetValues<Variant>()
                .Where(variant => variant != Variant.Connect && (variant != Variant.NoData || exchange is SpiExchange.Header or SpiExchange.HeaderResponse))
                .ToDictionary(variant => Kind(variant).ToString(), variant => Run(variant, fault, options).Outcome.ToString());
            return new { Name = $"Both lines: {text}", Spec = $"BothData {text}", Expected = expectedByKind[nameof(SpiTransferKind.BothData)], ExpectedByKind = expectedByKind };
        }).ToList();

        string path = Environment.GetEnvironmentVariable("LAB_SPI_FAULT_CASES") is { Length: > 0 } file ? file : System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "spi-fault-cases-both-lines.json");
        System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { Configuration = "both-lines", Options = options.ToString(), Cases = cases },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        TestContext.Out.WriteLine($"{cases.Count} cases written to {path}: {string.Join(", ", cases.Select(c => $"{c.Spec} -> {string.Join("/", c.ExpectedByKind.Select(k => $"{k.Key} {k.Value}"))}"))}");
    }
}
