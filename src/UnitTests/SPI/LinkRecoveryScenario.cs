using System.Collections.Generic;
using System.Linq;

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
}
