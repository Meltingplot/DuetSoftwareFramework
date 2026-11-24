using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using Host = UnitTests.SPI.LinkServiceOverSpi.Host;
using RrfPackets = UnitTests.SPI.LinkServiceOverSpi.RrfPackets;
using SbcPackets = UnitTests.SPI.LinkServiceOverSpi.SbcPackets;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.SPI;

/// <summary>
/// Files RRF writes on the SBC, such as config-override.g after M500 (GCodes.cpp:5333-5428) and resurrect.g (GCodes.cpp:1609-1736): RRF opens the file,
/// writes it in requests of at most SbcFileWriteBufLen bytes (FileWriteBuffer.h:16) and closes it (SbcInterface::OpenFile, WriteFile, WriteFileAsync,
/// CloseFile). The close gets no answer (SbcInterface.cpp:1599-1607), so FileStore::ForceClose returns only the result of the writes that flush the last data (FileStore.cpp:476-502, 695-743), and
/// RRF learns of a full disk only from the answer to a write. On the SD card f_write fails then (FileStore.cpp:576-581), M500 reports "Failed to write file"
/// and deletes the file (GCodes.cpp:5418-5428), and f_close syncs the file (ff.c:4773). DSF writes the file unbuffered for this and syncs it when RRF closes it
/// (LinkService.HandleOpenFile, HandleCloseFile). The sync itself is not visible to a test
/// </summary>
[TestFixture]
[Platform("Linux")]
public class FileWritesOverSpi
{
    /// <summary>
    /// Files DSF holds open for RRF, by handle
    /// </summary>
    private static Dictionary<uint, FileStream> OpenFiles(Host host) =>
        (Dictionary<uint, FileStream>)typeof(LinkService).GetField("_openFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host.Service)!;

    /// <summary>
    /// Wait until DSF has processed a close request, which has no answer, by waiting for the answer to a request sent after it
    /// </summary>
    private static void CloseFile(Host host, uint handle, string fileName)
    {
        host.Rrf.SendPacket(FirmwareRequest.CloseFile, RrfPackets.Handle(handle));
        host.Rrf.SendPacket(FirmwareRequest.CheckFileExists, RrfPackets.String(fileName));
        host.WaitFor(SbcRequest.CheckFileExistsResult);
    }

    /// <summary>
    /// A write the file system refuses, here because the disk is full (a symlink to /dev/full, whose every write fails with ENOSPC), is answered as failed,
    /// so that RRF takes its error path as on a full SD card. Before, DSF buffered up to FileBufferSize bytes and answered every such write with success,
    /// the error only came at the close, which has no answer, and the closed stream stayed in the list of open files
    /// </summary>
    [Test]
    public async Task WriteToFullDiskFails()
    {
        await using Host host = await Host.StartAsync();
        FakeRrfSpi rrf = host.Rrf;
        System.IO.File.CreateSymbolicLink(Path.Combine(host.BaseDirectory, "sys", "config-override.g"), "/dev/full");

        // M500 opens config-override.g with OpenMode::write and no pre-allocation (Platform::OpenSysFile, Platform.cpp:3935-3941)
        rrf.SendPacket(FirmwareRequest.OpenFile, RrfPackets.OpenFile("0:/sys/config-override.g", true, false, 0));
        (uint handle, _) = SbcPackets.OpenFileResult(host.WaitFor(SbcRequest.OpenFileResult));
        Assert.That(handle, Is.Not.EqualTo(Consts.NoFileHandle));

        rrf.SendPacket(FirmwareRequest.WriteFile, RrfPackets.WriteFile(handle, "; config-override.g file generated in response to M500\nM307 H1 R2.430 K0.560:0.000 D5.50 E1.35 S1.00 B0\n"));
        bool written = SbcPackets.Boolean(host.WaitFor(SbcRequest.FileWriteResult));
        CloseFile(host, handle, "0:/sys/config-override.g");

        Assert.Multiple(() =>
        {
            Assert.That(written, Is.False, "A write that does not reach the disk must fail");
            Assert.That(OpenFiles(host), Is.Empty, "The close must end the handle");
            Assert.That(host.Fault, Is.Null);
        });
    }

    /// <summary>
    /// A write that is answered as successful has reached the file system: the file has the data before RRF closes it. Opening it for writing
    /// replaces the old content, as FA_CREATE_ALWAYS does on the SD card (FileStore.cpp:131-134)
    /// </summary>
    [Test]
    public async Task WriteReachesTheFileBeforeItIsAnswered()
    {
        await using Host host = await Host.StartAsync();
        FakeRrfSpi rrf = host.Rrf;
        string path = Path.Combine(host.BaseDirectory, "sys", "config-override.g");
        System.IO.File.WriteAllText(path, "; old content that is longer than the new content of the file\n");

        rrf.SendPacket(FirmwareRequest.OpenFile, RrfPackets.OpenFile("0:/sys/config-override.g", true, false, 0));
        (uint handle, uint size) = SbcPackets.OpenFileResult(host.WaitFor(SbcRequest.OpenFileResult));
        Assert.That(handle, Is.Not.EqualTo(Consts.NoFileHandle));
        Assert.That(size, Is.Zero);

        const string first = "; config-override.g\n", second = "M307 H1 R2.430\n";
        rrf.SendPacket(FirmwareRequest.WriteFile, RrfPackets.WriteFile(handle, first));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileWriteResult)), Is.True);
        Assert.That(new FileInfo(path).Length, Is.EqualTo(first.Length), "The answered write must be in the file");
        rrf.SendPacket(FirmwareRequest.WriteFile, RrfPackets.WriteFile(handle, second));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileWriteResult)), Is.True);
        Assert.That(new FileInfo(path).Length, Is.EqualTo(first.Length + second.Length), "The answered write must be in the file");

        CloseFile(host, handle, "0:/sys/config-override.g");
        Assert.That(System.IO.File.ReadAllText(path), Is.EqualTo(first + second));
        Assert.That(OpenFiles(host), Is.Empty);
        Assert.That(host.Fault, Is.Null);
    }
}
