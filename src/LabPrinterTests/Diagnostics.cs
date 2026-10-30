using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Repeated diagnostics requests
/// </summary>
/// <remarks>
/// RRF 3.7.0-rc.2+1-mp.3 hard-faulted in Flash::ReadUserSignature during M122 and aborted a print (fixed in RRF 3.7.0-rc.2+1-mp.4, CoreN2G#1).
/// The hammer test after the fix sent 3000 M122 without a reset
/// </remarks>
[TestFixture]
public class Diagnostics : LabTest
{
    private const int Count = 300;

    [Test]
    public async Task RepeatedM122()
    {
        string cursor = await Lab.GetJournalCursorAsync();
        int upTime = Lab.Read(Session, model => model.State.UpTime);

        for (int i = 1; i <= Count; i++)
        {
            Assert.That(await SendCodeAsync("M122", 30), Does.Contain("=== Diagnostics ==="), $"M122 #{i}");
        }

        Assert.That(Lab.Read(Session, model => model.State.UpTime), Is.GreaterThanOrEqualTo(upTime), "RRF restarted");
        Assert.That(await Lab.ReadJournalAsync(cursor), Does.Not.Contain("Connection to controller has been reset"));
    }
}
