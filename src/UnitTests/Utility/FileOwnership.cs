using System;
using System.IO;
using System.Runtime.InteropServices;
using DuetSharedLibrary;
using NUnit.Framework;

namespace UnitTests.Utility;

using File = System.IO.File;

/// <summary>
/// FileExtensions.GetOwner and IsProtected (DuetSharedLibrary/FileExtensions.cs) and PluginManifestCheck.IsInstalledByPackage, which the root plugin service and DCS
/// use for the files of plugins with super-user permissions. GetOwner calls statx(2) with the structure of DuetSharedLibrary/Interop/Libc/Interop.stat.cs
/// </summary>
[TestFixture]
public class FileOwnership
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dsf-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    /// <summary>
    /// The offsets of struct statx in include/uapi/linux/stat.h of Linux, which the header states in comments (/* 0x00 */ etc.) and which are the same on
    /// every architecture: there is one definition for all of them, every member has a fixed size, and each starts at a multiple of its size, so no
    /// architecture inserts padding (statx_timestamp: __s64 tv_sec, __u32 tv_nsec, __s32 __reserved, 16 bytes). A sequential .NET layout follows the same
    /// rule for 32-bit ARM, ARM64 and x86-64, so checking the layout of the managed structure here holds for the SBCs too
    /// </summary>
    [TestCase("Mask", 0x00)]
    [TestCase("BlockSize", 0x04)]
    [TestCase("Attributes", 0x08)]
    [TestCase("HardLinks", 0x10)]
    [TestCase("UserID", 0x14)]
    [TestCase("GroupID", 0x18)]
    [TestCase("Mode", 0x1c)]
    [TestCase("Inode", 0x20)]
    [TestCase("Size", 0x28)]
    [TestCase("Blocks", 0x30)]
    [TestCase("AttributesMask", 0x38)]
    [TestCase("AccessTime", 0x40)]
    [TestCase("CreationTime", 0x50)]
    [TestCase("StatusChangeTime", 0x60)]
    [TestCase("LastModificationTime", 0x70)]
    [TestCase("RDevIdMajor", 0x80)]
    [TestCase("DevIdMajor", 0x88)]
    [TestCase("MountId", 0x90)]
    [TestCase("DioMemAlign", 0x98)]
    [TestCase("Padding04", 0xa0)]
    public void StatxStructureHasTheLayoutOfTheKernel(string field, int offset)
    {
        Type statx = typeof(FileExtensions).Assembly.GetType("statxbuf", true)!;
        Assert.That((int)Marshal.OffsetOf(statx, field), Is.EqualTo(offset));
        Assert.That(Marshal.SizeOf(statx), Is.EqualTo(0x100));
        Assert.That(Marshal.SizeOf(typeof(FileExtensions).Assembly.GetType("statx_timestamp", true)!), Is.EqualTo(16));
    }

    [Test]
    public void OwnerAndModeAreRead()
    {
        string file = Path.Combine(_directory, "file");
        File.WriteAllText(file, "x");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        (int userId, UnixFileMode mode, bool isSymlink) = FileExtensions.GetOwner(file);
        Assert.That(userId, Is.EqualTo(ProcessHelpers.GetEffectiveUserID()));
        Assert.That(mode, Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead));
        Assert.That(isSymlink, Is.False);

        // A symlink is not followed
        string link = Path.Combine(_directory, "link");
        File.CreateSymbolicLink(link, file);
        Assert.That(FileExtensions.GetOwner(link).IsSymlink, Is.True);

        // A sticky directory keeps its sticky bit in the mode
        File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.StickyBit);
        Assert.That(FileExtensions.GetOwner(_directory).Mode, Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.StickyBit));

        Assert.Throws<IOException>(() => FileExtensions.GetOwner(Path.Combine(_directory, "missing")));
    }

    [Test]
    public void ProtectedEntriesAreRecognized()
    {
        int me = ProcessHelpers.GetEffectiveUserID();
        string file = Path.Combine(_directory, "file");
        File.WriteAllText(file, "x");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Assert.That(FileExtensions.IsProtected(file, me), Is.True);
        Assert.That(FileExtensions.IsProtected(file, me + 1), Is.False, "Another owner");

        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.GroupWrite);
        Assert.That(FileExtensions.IsProtected(file, me), Is.False, "Writable by the group");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.OtherWrite);
        Assert.That(FileExtensions.IsProtected(file, me), Is.False, "Writable by others");

        File.SetUnixFileMode(file, UnixFileMode.UserRead);
        string link = Path.Combine(_directory, "link");
        File.CreateSymbolicLink(link, file);
        Assert.That(FileExtensions.IsProtected(link, me), Is.False, "A symlink");
        Assert.That(FileExtensions.IsProtected(Path.Combine(_directory, "missing"), me), Is.False);
    }

    [Test]
    public void ManifestOfASystemPackageIsRecognized()
    {
        // A system package leaves a manifest without write permission (pkg/deb/duetpimanagementplugin/DEBIAN/postinst: root:dsf 440)
        int me = ProcessHelpers.GetEffectiveUserID();
        string manifest = Path.Combine(_directory, "Packaged.json");
        File.WriteAllText(manifest, "{}");
        File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        Assert.That(PluginManifestCheck.IsInstalledByPackage(manifest, me), Is.True);
        Assert.That(PluginManifestCheck.IsInstalledByPackage(manifest, PluginManifestCheck.RootUserId), Is.EqualTo(me == 0));

        // A manifest that its owner may write was installed via API
        File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.That(PluginManifestCheck.IsInstalledByPackage(manifest, me), Is.False);
        File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        Assert.That(PluginManifestCheck.IsInstalledByPackage(manifest, me), Is.False);
    }
}
