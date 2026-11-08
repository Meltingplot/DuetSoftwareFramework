using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Files;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using DcsModel = DuetControlServer.Model.ObjectModel;

namespace UnitTests.File;

/// <summary>
/// Tests for FilePathResolver with a temporary base directory and a second volume. The upstream tests in Path.cs are disabled since
/// FilePathResolver needs dependency injection; the cases of ToPhysical and ToVirtual below include the ones from there
/// </summary>
[TestFixture]
public class PathResolution
{
    private string _root = null!, _base = null!, _usb = null!;
    private ServiceProvider _services = null!;
    private FilePathResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "dsf-path-" + Guid.NewGuid().ToString("N"));
        _base = Path.Combine(_root, "sd");
        _usb = Path.Combine(_root, "usb");
        Directory.CreateDirectory(_base);
        Directory.CreateDirectory(_usb);

        _services = DcsServices.Build(new Settings { BaseDirectory = _base });
        _resolver = _services.GetRequiredService<FilePathResolver>();
        DcsModel model = _services.GetRequiredService<DcsModel>();
        using (model.AccessReadWrite())
        {
            model.Volumes.Clear();
            model.Volumes.Add(new Volume { Path = _base });
            model.Volumes.Add(new Volume { Path = _usb });
            model.Volumes.Add(new Volume());
        }
    }

    [TearDown]
    public void TearDown()
    {
        _services.Dispose();
        Directory.Delete(_root, true);
    }

    private string Base(string relative) => Path.Combine(_base, relative);

    [Test]
    public async Task ToPhysical()
    {
        // The cases of the disabled upstream test (Path.cs:9-34), with the base directory instead of /opt/dsf/sd
        Assert.That(await _resolver.ToPhysicalAsync("0:/sys"), Is.EqualTo(Base("sys")));
        Assert.That(await _resolver.ToPhysicalAsync("/www"), Is.EqualTo(Base("www")));
        Assert.That(await _resolver.ToPhysicalAsync("config.g", "sys"), Is.EqualTo(Base("sys/config.g")));
        Assert.That(await _resolver.ToPhysicalAsync("foobar/config.g", FileDirectory.Filaments), Is.EqualTo(Base("filaments/foobar/config.g")));
        Assert.That(await _resolver.ToPhysicalAsync("test.g", FileDirectory.GCodes), Is.EqualTo(Base("gcodes/test.g")));
        Assert.That(await _resolver.ToPhysicalAsync("test.g", FileDirectory.Macros), Is.EqualTo(Base("macros/test.g")));
        Assert.That(await _resolver.ToPhysicalAsync("test.g", FileDirectory.System), Is.EqualTo(Base("sys/test.g")));
        Assert.That(await _resolver.ToPhysicalAsync("index.html", FileDirectory.Web), Is.EqualTo(Base("www/index.html")));

        // The synchronous variants do the same
        Assert.That(_resolver.ToPhysical("0:/sys"), Is.EqualTo(Base("sys")));
        Assert.That(_resolver.ToPhysical("config.g", "sys"), Is.EqualTo(Base("sys/config.g")));
        Assert.That(_resolver.ToPhysical("a.g", FileDirectory.Firmware), Is.EqualTo(Base("firmware/a.g")));
        Assert.That(_resolver.ToPhysical("a.g", FileDirectory.Menu), Is.EqualTo(Base("menu/a.g")));
        Assert.That(_resolver.ToPhysical("a.g", FileDirectory.Filaments), Is.EqualTo(Base("filaments/a.g")));
        Assert.That(_resolver.ToPhysical("a.g", FileDirectory.GCodes), Is.EqualTo(Base("gcodes/a.g")));
        Assert.That(_resolver.ToPhysical("a.g", FileDirectory.Macros), Is.EqualTo(Base("macros/a.g")));
        Assert.That(_resolver.ToPhysical("a.g", FileDirectory.System), Is.EqualTo(Base("sys/a.g")));
        Assert.That(_resolver.ToPhysical("a.g", FileDirectory.Web), Is.EqualTo(Base("www/a.g")));

        // Unknown directories fall back to the system directory (FilePathResolver.cs:139, 163)
        Assert.That(_resolver.ToPhysical("a.g", (FileDirectory)99), Is.EqualTo(Base("sys/a.g")));
        Assert.That(await _resolver.ToPhysicalAsync("a.g", (FileDirectory)99), Is.EqualTo(Base("sys/a.g")));

        // Backslashes are separators, and a path with a drive or a leading slash ignores the directory (FilePathResolver.cs:197-217)
        Assert.That(_resolver.ToPhysical("sub\\a.g", "0:/gcodes"), Is.EqualTo(Base("gcodes/sub/a.g")));
        Assert.That(_resolver.ToPhysical("/sys/a.g", "0:/gcodes"), Is.EqualTo(Base("sys/a.g")));
        Assert.That(_resolver.ToPhysical("0:/sys/a.g", "1:/x"), Is.EqualTo(Base("sys/a.g")));
    }

    [Test]
    public async Task OtherDrives()
    {
        // n:/ is the path of volume n (FilePathResolver.cs:80-119), also for a relative path in a directory on that drive
        Assert.That(_resolver.ToPhysical("1:/a.g"), Is.EqualTo(Path.Combine(_usb, "a.g")));
        Assert.That(await _resolver.ToPhysicalAsync("1:/a.g"), Is.EqualTo(Path.Combine(_usb, "a.g")));
        Assert.That(_resolver.ToPhysical("a.g", "1:/sub"), Is.EqualTo(Path.Combine(_usb, "sub/a.g")));
        Assert.That(await _resolver.ToPhysicalAsync("a.g", "1:/sub"), Is.EqualTo(Path.Combine(_usb, "sub/a.g")));

        // A volume without a path and a drive that does not exist are invalid
        foreach (string path in new[] { "2:/a.g", "7:/a.g" })
        {
            Assert.That(() => _resolver.ToPhysical(path), Throws.ArgumentException.With.Message.EqualTo("Invalid drive index"));
            Assert.That(async () => await _resolver.ToPhysicalAsync(path), Throws.ArgumentException.With.Message.EqualTo("Invalid drive index"));
        }
        Assert.That(() => _resolver.ToPhysical("a.g", "7:/x"), Throws.ArgumentException);
        Assert.That(async () => await _resolver.ToPhysicalAsync("a.g", "7:/x"), Throws.ArgumentException);
    }

    [TestCase("../a.g", null)]
    [TestCase("0:/../a.g", null)]
    [TestCase("1:/../sd/a.g", null)]
    [TestCase("sub/../../../a.g", "0:/sys")]
    [TestCase("a.g", "0:/../..")]
    [TestCase("..\\..\\a.g", "/macros")]
    public void EscapingPath(string path, string? directory)
    {
        // The result must stay inside the root of its drive (FilePathResolver.cs:54-72)
        Assert.That(() => _resolver.ToPhysical(path, directory), Throws.ArgumentException.With.Message.Contains("escapes the volume root directory"));
        Assert.That(async () => await _resolver.ToPhysicalAsync(path, directory), Throws.ArgumentException.With.Message.Contains("escapes the volume root directory"));
    }

    [TestCase("0:/", null)]
    [TestCase("0:", null)]
    [TestCase("0:\\", null)]
    [TestCase("0://", null)]
    [TestCase("0:/.", null)]
    [TestCase("0:/sys/..", null)]
    [TestCase("0:/sys/../", null)]
    [TestCase("0:/./", null)]
    [TestCase("0:/../sd", null)]
    [TestCase("0:/../sd/", null)]
    [TestCase("/", null)]
    [TestCase("", null)]
    [TestCase("./", null)]
    [TestCase("1:/", null)]
    [TestCase("1:", null)]
    [TestCase("1:/sub/..", null)]
    [TestCase("1:/sub/../", null)]
    [TestCase("1:/./", null)]
    [TestCase("..", "0:/sys")]
    [TestCase("../", "0:/sys")]
    [TestCase("", "0:/")]
    [TestCase("./", "0:/")]
    [TestCase(".", "1:/")]
    [TestCase("./", "1:/")]
    [TestCase("../..", "sys/sub")]
    public async Task RootForDeletion(string path, string? directory)
    {
        // Every one of these names the root of its drive, which other requests like a directory listing may still resolve. Path.GetFullPath
        // keeps a trailing separator, so some of them resolve to the root with a slash at the end
        string root = Path.GetFullPath(path.StartsWith('1') || (directory?.StartsWith('1') ?? false) ? _usb : _base);
        Assert.That(_resolver.ToPhysical(path, directory), Is.EqualTo(root).Or.EqualTo(root + '/'));
        Assert.That(await _resolver.ToPhysicalAsync(path, directory), Is.EqualTo(root).Or.EqualTo(root + '/'));

        // RRF refuses to delete the root of a volume with this message (MassStorage.cpp:840-858), so a path that is going to be deleted may not
        // resolve to it, however it is written
        Assert.That(() => _resolver.ToPhysical(path, directory, forDeletion: true), Throws.ArgumentException.With.Message.EqualTo("Delete root folder is not allowed"));
        Assert.That(async () => await _resolver.ToPhysicalAsync(path, directory, forDeletion: true), Throws.ArgumentException.With.Message.EqualTo("Delete root folder is not allowed"));
    }

    [TestCase("0:/")]
    [TestCase("0:/sys/..")]
    [TestCase("0:/sys/../")]
    [TestCase("0:/.")]
    [TestCase("1:/")]
    [TestCase("1:/sub/..")]
    [TestCase("1:/sub/../")]
    public void RootWithTrailingSeparatorForDeletion(string path)
    {
        // The base directory and a volume path may end with a separator, which Path.GetFullPath keeps while it drops the one of a path that ends
        // with .. or ., so the root is the same with and without it
        using ServiceProvider services = DcsServices.Build(new Settings { BaseDirectory = _base + '/' });
        FilePathResolver resolver = services.GetRequiredService<FilePathResolver>();
        DcsModel model = services.GetRequiredService<DcsModel>();
        using (model.AccessReadWrite())
        {
            model.Volumes.Clear();
            model.Volumes.Add(new Volume { Path = _base + '/' });
            model.Volumes.Add(new Volume { Path = _usb + '/' });
        }

        string root = Path.GetFullPath(path.StartsWith('1') ? _usb : _base);
        Assert.That(resolver.ToPhysical(path), Is.EqualTo(root).Or.EqualTo(root + '/'));
        Assert.That(() => resolver.ToPhysical(path, forDeletion: true), Throws.ArgumentException.With.Message.EqualTo("Delete root folder is not allowed"));

        // Paths below the root stay what they were
        Assert.That(resolver.ToPhysical("0:/sys/../gcodes", forDeletion: true), Is.EqualTo(Base("gcodes")));
        Assert.That(resolver.ToPhysical("1:/sub/", forDeletion: true), Is.EqualTo(Path.Combine(_usb, "sub/")));
    }

    [Test]
    public async Task PathsBelowTheRootForDeletion()
    {
        // Everything below the root resolves as before
        Assert.That(_resolver.ToPhysical("0:/sys", forDeletion: true), Is.EqualTo(Base("sys")));
        Assert.That(await _resolver.ToPhysicalAsync("0:/sys/../gcodes/a.g", forDeletion: true), Is.EqualTo(Base("gcodes/a.g")));
        Assert.That(await _resolver.ToPhysicalAsync("0:/../sd/macros", forDeletion: true), Is.EqualTo(Base("macros")));
        Assert.That(_resolver.ToPhysical("a.g", "1:/sub", forDeletion: true), Is.EqualTo(Path.Combine(_usb, "sub/a.g")));
        Assert.That(await _resolver.ToPhysicalAsync("a.g", FileDirectory.GCodes, true), Is.EqualTo(Base("gcodes/a.g")));

        // A base directory works the same way, and a path that escapes is still reported as such
        Assert.That(async () => await _resolver.ToPhysicalAsync("..", FileDirectory.System, true), Throws.ArgumentException.With.Message.EqualTo("Delete root folder is not allowed"));
        Assert.That(await _resolver.ToPhysicalAsync("..", FileDirectory.System), Is.EqualTo(Path.GetFullPath(_base)));
        Assert.That(() => _resolver.ToPhysical("0:/../a.g", forDeletion: true), Throws.ArgumentException.With.Message.Contains("escapes the volume root directory"));
    }

    [Test]
    public async Task ToVirtual()
    {
        // The cases of the disabled upstream test (Path.cs:37-44)
        Assert.That(await _resolver.ToVirtualAsync(Base("sys")), Is.EqualTo("0:/sys"));
        Assert.That(await _resolver.ToVirtualAsync(Base("www")), Is.EqualTo("0:/www"));
        Assert.That(_resolver.ToVirtual(Base("sys")), Is.EqualTo("0:/sys"));

        // The root of a volume and paths on other volumes (FilePathResolver.cs:256-333)
        Assert.That(_resolver.ToVirtual(Path.GetFullPath(_base)), Is.EqualTo("0:/"));
        Assert.That(_resolver.ToVirtual(Path.Combine(_usb, "a.g")), Is.EqualTo("1:/a.g"));
        Assert.That(await _resolver.ToVirtualAsync(Path.Combine(_usb, "a.g")), Is.EqualTo("1:/a.g"));
        Assert.That(_resolver.ToVirtual(_usb), Is.EqualTo("1:/"));

        // A sibling whose name starts with the volume path is not on the volume
        Assert.That(_resolver.ToVirtual(_usb + "2/a.g"), Does.Not.StartWith("1:/"));
        Assert.That(await _resolver.ToVirtualAsync(_usb + "2/a.g"), Does.Not.StartWith("1:/"));
    }

    [Test, Explicit("A line feed after a drive prefix cuts the path short: the drive regex ^(\\d+):/*(.*) (FilePathResolver.cs:46) stops at the line feed, so another file is resolved")]
    public void LineFeedAfterDrive()
    {
        // Without a drive prefix the same name is kept (FilePathResolver.cs:216), so the prefix 0:/ must not change it either
        Assert.That(_resolver.ToPhysical("/gcodes/a\nb.g"), Is.EqualTo(Base("gcodes/a\nb.g")));
        Assert.That(_resolver.ToPhysical("0:/gcodes/a\nb.g"), Is.EqualTo(Base("gcodes/a\nb.g")));
    }
}
