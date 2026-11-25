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
    /// The file info of a file whose footer has a line with two codes, such as "M104 S0 M140 S0" at the end of many slicer files, failed as a whole.
    /// FileInfoParser reads the footer line by line with the synchronous parser (FileInfoParser.cs:209-249), and since upstream 721e5df0 that parser
    /// dropped the first character of the next code on the same line, so the second call threw "Illegal parameter letter '1'" (PR #84). Fixed in
    /// DSF #84: Code.Parse peeks at the next character and leaves it in the reader when it starts the next code (DuetAPI Parser.cs:875-882). Unfixed,
    /// the exception escapes FileInfoParser.ParseAsync and M36 replies {"err":1,...} (MCodeHandler.cs:515-519), which the unit test
    /// SyncParserFollowingCodes showed for FileInfoParser on such a file before the fix. With the fix the scan goes on to the line before and takes
    /// the height from its G1 (FileInfoParser.cs:227-233). Deterministic, one M36 of a file in 0:/gcodes/lab-tests, which is never printed.
    /// Upstream v3.7-dev (dbf9b0447) still puts the character back into a local of Parse (Parser.cs:873-880 there)
    /// </summary>
    [Test]
    public async Task M36ReadsFooterWithTwoCodesOnOneLine()
    {
        string reply = await G2Helpers.GetFileInfoAsync(Session, "footer-two-codes.g", G2Helpers.JobFileHeader + "G1 Z0.4\nM104 S0 M140 S0\n");
        TestContext.Progress.WriteLine($"M36: {reply.Trim()}");
        using JsonDocument info = G2Helpers.ParseFileInfo(reply);
        Assert.That(info.RootElement.GetProperty("err").GetInt32(), Is.Zero, "The file info could not be read");
        Assert.That(info.RootElement.GetProperty("height").GetSingle(), Is.EqualTo(0.4F), "The footer scan did not get past the line with two codes");
    }

    /// <summary>
    /// The sizes were in "Gb" and the cluster size was missing (#82). The cluster size is f_frsize of statvfs (FileExtensions.GetFileSystemBlockSize,
    /// DuetSharedLibrary/FileExtensions.cs:33-41) on the path of volume 0 (MCodeHandler.cs:1561-1571), and PR #82 checked the layout of the statvfs struct
    /// only against the x86_64 headers. A wrong layout on the arm64 Pi would read another field, which is still above zero, so the test compares it with
    /// GNU stat on the same path: %S of "stat -f" is the fundamental block size, i.e. f_frsize (stat --help)
    /// </summary>
    [Test]
    public async Task M39ReportsSizesAndClusterSize()
    {
        Assert.That(await SendCodeAsync("M39"), Does.Contain("GB").And.Contain("cluster size").And.Not.Contain("Gb"));

        using JsonDocument reply = JsonDocument.Parse(await SendCodeAsync("M39 S2"));
        long clusterSize = reply.RootElement.GetProperty("SDinfo").GetProperty("clsize").GetInt64();
        Assert.That(clusterSize, Is.GreaterThan(0));

        // M39 P0 S2 reports volume 0 of the object model of DSF (MCodeHandler.cs:599-616), whose path DSF takes from DriveInfo (PeriodicUpdateService.cs:675)
        string? path = Lab.Read(Session, model => model.Volumes.Count > 0 ? model.Volumes[0].Path : null);
        Assert.That(path, Is.Not.Null.And.Not.Empty, "Volume 0 has no path");
        string blockSize = (await Lab.RunOnSbcAsync($"stat -f -c %S -- '{path!.Replace("'", "'\\''")}'")).Trim();
        Assert.That(clusterSize.ToString(), Is.EqualTo(blockSize), $"M39 S2 reports another cluster size than stat -f for {path}");
    }
}
