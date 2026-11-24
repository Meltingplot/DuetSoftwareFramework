using System;
using System.Threading;

namespace UnitTests.Utility;

/// <summary>
/// Clock whose system time can be changed like NTP, a manual change or the switch to or from daylight saving time change it,
/// while its time stamps go on like the monotonic clock of the system
/// </summary>
public sealed class ShiftableClock : TimeProvider
{
    private long _offsetTicks;

    /// <summary>
    /// How far the system time of this clock is ahead of the real one
    /// </summary>
    public TimeSpan Offset
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
        set => Interlocked.Exchange(ref _offsetTicks, value.Ticks);
    }

    /// <summary>
    /// System time, which GetLocalNow converts to the local time zone
    /// </summary>
    /// <returns>Current UTC time plus the offset</returns>
    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
}
