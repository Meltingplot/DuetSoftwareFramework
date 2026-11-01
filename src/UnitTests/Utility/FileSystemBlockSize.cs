using System.Diagnostics;
using System.IO;
using DuetSharedLibrary;
using NUnit.Framework;

namespace UnitTests.Utility;

/// <summary>
/// Tests for <see cref="FileExtensions.GetFileSystemBlockSize(string)"/>, which reads struct statvfs from libc
/// </summary>
public class FileSystemBlockSize
{
    [Test]
    public void MatchesStat()
    {
        // stat -f %S prints the fundamental block size (f_frsize) as well
        using Process stat = Process.Start(new ProcessStartInfo("stat", ["-f", "-c", "%S", Path.GetTempPath()]) { RedirectStandardOutput = true })!;
        long expected = long.Parse(stat.StandardOutput.ReadToEnd().Trim());
        stat.WaitForExit();

        Assert.That(FileExtensions.GetFileSystemBlockSize(Path.GetTempPath()), Is.EqualTo(expected));
    }

    [Test]
    public void MissingPath()
    {
        Assert.That(() => FileExtensions.GetFileSystemBlockSize(Path.Combine(Path.GetTempPath(), "dsf-missing", "path")), Throws.TypeOf<IOException>());
    }
}
