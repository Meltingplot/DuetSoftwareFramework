using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Replies of codes that only read, which DSF answers itself
/// </summary>
[TestFixture]
public class CodeReplies : LabTest
{
    /// <summary>
    /// The reply was invalid JSON (#72)
    /// </summary>
    [Test]
    public async Task M36MissingFileIsValidJson()
    {
        using JsonDocument reply = JsonDocument.Parse(await SendCodeAsync("M36 \"0:/gcodes/lab-test-missing.g\""));
        Assert.That(reply.RootElement.GetProperty("err").GetInt32(), Is.EqualTo(1));
        Assert.That(reply.RootElement.GetProperty("fileName").GetString(), Is.EqualTo("0:/gcodes/lab-test-missing.g"));
    }

    /// <summary>
    /// The sizes were in "Gb" and the cluster size was missing (#82)
    /// </summary>
    [Test]
    public async Task M39ReportsSizesAndClusterSize()
    {
        Assert.That(await SendCodeAsync("M39"), Does.Contain("GB").And.Contain("cluster size").And.Not.Contain("Gb"));

        using JsonDocument reply = JsonDocument.Parse(await SendCodeAsync("M39 S2"));
        Assert.That(reply.RootElement.GetProperty("SDinfo").GetProperty("clsize").GetInt64(), Is.GreaterThan(0));
    }
}
