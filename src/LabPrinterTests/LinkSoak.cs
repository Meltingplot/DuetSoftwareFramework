using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// SPI link while the printer is idle
/// </summary>
/// <remarks>
/// With DSF 3.7.0-rc.1 the link timed out every 15 to 30 minutes, because the TfrRdy level and its edge event raced (fixed upstream 46886f5d)
/// </remarks>
[TestFixture, Explicit("Runs for 30 minutes")]
public class LinkSoak : LabTest
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(30);

    [Test]
    public async Task NoLostConnectionWhileIdle()
    {
        string cursor = await Lab.GetJournalCursorAsync();
        int upTime = Lab.Read(Session, model => model.State.UpTime);

        await Task.Delay(Duration);

        Assert.That(await Lab.ReadJournalAsync(cursor), Does.Not.Contain("Lost connection to Duet"));
        Assert.That(Lab.Read(Session, model => model.State.UpTime), Is.GreaterThan(upTime), "RRF restarted");
    }
}
