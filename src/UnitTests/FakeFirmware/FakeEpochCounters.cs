using System.Collections.Generic;
using System.Linq;
using DuetAPI;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Counters of the channel epoch in the fake firmware, per channel and request type (design/sbc-link-v8-requirements.md CE-20).
/// Access them with the firmware locked or after the transfers have ended
/// </summary>
public sealed class FakeEpochCounters
{
    /// <summary>
    /// Tagged DSF packets whose epoch precedes the channel epoch (or is 32 away), dropped by the filter (CE-10)
    /// </summary>
    public Dictionary<(CodeChannel Channel, SbcRequest Request), int> StaleDrops { get; } = [];

    /// <summary>
    /// Tagged DSF packets whose epoch follows the channel epoch, dropped by the filter (CE-5)
    /// </summary>
    public Dictionary<(CodeChannel Channel, SbcRequest Request), int> AheadDrops { get; } = [];

    /// <summary>
    /// DSF packets dropped while the abort gate of their channel was set (CE-24)
    /// </summary>
    public Dictionary<(CodeChannel Channel, SbcRequest Request), int> GateDrops { get; } = [];

    /// <summary>
    /// InvalidateChannel packets whose tag did not follow the channel epoch, ignored as repeats (CE-7)
    /// </summary>
    public Dictionary<(CodeChannel Channel, SbcRequest Request), int> Repeats { get; } = [];

    /// <summary>
    /// Sum of all counters
    /// </summary>
    public int Total => StaleDrops.Values.Sum() + AheadDrops.Values.Sum() + GateDrops.Values.Sum() + Repeats.Values.Sum();

    /// <summary>
    /// Count an event
    /// </summary>
    internal void Count(Dictionary<(CodeChannel, SbcRequest), int> counter, CodeChannel channel, SbcRequest request) =>
        counter[(channel, request)] = counter.GetValueOrDefault((channel, request)) + 1;
}
