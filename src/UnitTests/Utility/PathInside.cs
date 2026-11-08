using System;
using System.IO;
using DuetSharedLibrary;
using NUnit.Framework;

namespace UnitTests.Utility;

/// <summary>
/// Path.IsInside of DuetSharedLibrary/PathExtensions.cs, which InstallPlugin and UninstallPlugin of DuetPluginService use for the files of a plugin
/// </summary>
[TestFixture]
public class PathInside
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dsf-inside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_directory, "base/sub"));
        Directory.CreateDirectory(Path.Combine(_directory, "outside"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private string Base => Path.Combine(_directory, "base");

    [TestCase("file", true)]
    [TestCase("sub/file", true)]
    [TestCase("missing/deeper/file", true)]
    [TestCase("sub/../file", true)]
    [TestCase("", true)]
    [TestCase("../outside/file", false)]
    [TestCase("../base2/file", false)]
    [TestCase("sub/../../file", false)]
    [TestCase("/etc/passwd", false)]
    public void PathsAreCheckedLexically(string relativePath, bool inside)
    {
        Assert.That(Path.IsInside(Path.Combine(Base, relativePath), Base), Is.EqualTo(inside));
    }

    [Test]
    public void SymlinkedDirectoriesAreResolved()
    {
        Directory.CreateSymbolicLink(Path.Combine(Base, "out"), Path.Combine(_directory, "outside"));
        Directory.CreateSymbolicLink(Path.Combine(Base, "in"), Path.Combine(Base, "sub"));
        Directory.CreateSymbolicLink(Path.Combine(Base, "dangling"), Path.Combine(_directory, "missing"));

        Assert.That(Path.IsInside(Path.Combine(Base, "out/file"), Base), Is.False);
        Assert.That(Path.IsInside(Path.Combine(Base, "out/missing/file"), Base), Is.False);
        Assert.That(Path.IsInside(Path.Combine(Base, "in/file"), Base), Is.True);
        Assert.That(Path.IsInside(Path.Combine(Base, "dangling/file"), Base), Is.False);

        // The entry itself is not followed, whoever deletes or writes it acts on the link
        Assert.That(Path.IsInside(Path.Combine(Base, "out"), Base), Is.True);
        Assert.That(Path.IsInside(Path.Combine(Base, "dangling"), Base), Is.True);
    }

    [Test]
    public void SymlinkedBaseIsResolved()
    {
        string link = Path.Combine(_directory, "link");
        Directory.CreateSymbolicLink(link, Base);
        Assert.That(Path.IsInside(Path.Combine(link, "sub/file"), link), Is.True);
        Assert.That(Path.IsInside(Path.Combine(Base, "sub/file"), link), Is.False, "The lexical check comes first");
    }
}
