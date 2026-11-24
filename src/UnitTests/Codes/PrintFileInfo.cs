using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Files.Parser;
using DuetControlServer.Link;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Codes;

/// <summary>
/// The information about a selected job file that DSF sends to RRF with SetPrintFileInfo. JobProcessor.SelectFileAsync parses the file
/// (JobProcessor.cs:312), ApplyFileSelectionAsync copies the information into the object model and asks the link task to send it
/// (JobProcessor.cs:360-367, LinkInterface.SetPrintFileInfo), and the link task writes it into its next transfer (LinkService.cs:404-411)
/// </summary>
[TestFixture]
public class PrintFileInfo
{
    private FakeFirmwareHost? _host;

    private FakeFirmwareHost Host => _host!;

    [TearDown]
    public async Task TearDown()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }
    }

    private JobProcessor Jobs => Host.Services.GetRequiredService<JobProcessor>();

    private string WriteJob(string fileName, string content)
    {
        string path = Path.Combine(Host.BaseDirectory, "gcodes", fileName);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private Task<GCodeFileInfo> ParseAsync(string path) => Host.Services.GetRequiredService<FileInfoParser>().ParseAsync(path, false);

    /// <summary>
    /// Wait for the job loop to finish
    /// </summary>
    private async Task WaitForJobEndAsync()
    {
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        while (Jobs.IsFileSelected)
        {
            await Task.Delay(10, cts.Token);
        }
    }

    /// <summary>
    /// File names in the SetPrintFileInfo requests the firmware received
    /// </summary>
    private string[] SentFileInfos() =>
        [.. Host.Firmware.GetReceivedRequests().Where(request => request.Request == SbcRequest.SetPrintFileInfo).Select(request => request.Text)];

    /// <summary>
    /// UpdateService requests the job key of RRF when seqs.job changes (UpdateService.cs:186-195) and applies it to the object model on its
    /// own task. RRF reports job.file from the information of the file it prints (PrintMonitor.cpp:87-99), which is still the previous file
    /// until it has read the new one. The link task must send the information of the selected file, not what the object model holds when it
    /// gets to write the request
    /// </summary>
    [Test]
    public async Task SentInfoIsThatOfTheSelectedFile()
    {
        _host = await FakeFirmwareHost.StartAsync();
        WriteJob("next.g", "G1 X1\n");
        LinkInterface link = Host.Services.GetRequiredService<LinkInterface>();

        // Hold the link task in a transfer, so that the request goes out with the next one
        Host.Firmware.HoldTransfers();
        Assert.That(Host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, FakeFirmwareHost.DefaultTimeout), Is.True, "The transfers were not held");
        Task<string> m32 = Host.StartCode("M32 \"next.g\"");
        Assert.That(SpinWait.SpinUntil(() => link.SetPrintInfoRequest is not null, FakeFirmwareHost.DefaultTimeout), Is.True, "M32 did not request the print file info");

        // A job update of RRF arrives before the link task writes the request
        using (await Host.Model.AccessReadWriteAsync())
        {
            Host.Model.Job.File.FileName = "0:/gcodes/previous.g";
            Host.Model.Job.File.Size = 1;
        }
        Host.Firmware.ReleaseTransfers();

        Assert.That(await m32.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
        await WaitForJobEndAsync();
        Assert.That(SentFileInfos(), Is.EqualTo(new[] { "0:/gcodes/next.g" }));
    }

    /// <summary>
    /// IdeaMaker writes the filament usage of each extruder with its index (";Material#1 Used: 868.0", Settings.cs:369). FileInfoParser
    /// grows the filament list up to the index from the comment (FileInfoParser.cs:473-480), so a large index made it allocate and fill that
    /// many floats, and removing the leading zeros again (FileInfoParser.cs:116-120) took quadratic time. RRF ignores an index of MaxFilaments
    /// or more (FileInfoParser.cpp:778)
    /// </summary>
    [Test]
    public async Task FilamentIndexIsBounded()
    {
        _host = await FakeFirmwareHost.StartAsync();
        GCodeFileInfo info = await ParseAsync(WriteJob("job.g", ";Material#0 Used: 5.0\n;Material#100000 Used: 1.0\nG1 X1\n"));
        Assert.That(info.Filament, Is.EqualTo(new[] { 5F }));
    }

    /// <summary>
    /// A list of filament usages in one comment (PrusaSlicer, Settings.cs:368) or one comment per filament adds as many filaments as there are
    /// values. RRF keeps MaxFilaments of them (FileInfoParser.cpp:799-804)
    /// </summary>
    [Test]
    public async Task FilamentCountIsBounded()
    {
        _host = await FakeFirmwareHost.StartAsync();
        string values = string.Join(", ", Enumerable.Range(1, 30).Select(i => $"{i}.0"));
        GCodeFileInfo info = await ParseAsync(WriteJob("job.g", $"; filament used [mm] = {values}\nG1 X1\n"));
        Assert.That(info.Filament, Is.EqualTo(Enumerable.Range(1, 20).Select(i => (float)i)));

        string lines = string.Concat(Enumerable.Range(1, 30).Select(i => $";Estimated Build Volume: {i}.0\n"));
        info = await ParseAsync(WriteJob("job.g", lines + "G1 X1\n"));
        Assert.That(info.Filament, Has.Count.EqualTo(20));
    }

    /// <summary>
    /// The SetPrintFileInfo request takes a file name of 254 UTF-8 bytes (Writer.WritePrintFileInfo). M32 of a file with a longer virtual path
    /// made the Writer throw on the link task, so DCS stopped and the caller got no reply. Now M32 replies with an error and selects nothing
    /// </summary>
    [TestCase(244, false), TestCase(245, true)]
    public async Task LongFileNameIsRefused(int nameLength, bool refused)
    {
        _host = await FakeFirmwareHost.StartAsync();
        string name = new string('a', nameLength - 2) + ".g";
        WriteJob(name, "G1 X1\n");

        string reply = await Host.ExecuteAsync($"M32 \"{name}\"");
        if (refused)
        {
            Assert.That(reply, Does.StartWith("Error: Filename too long"));
            Assert.That(Jobs.IsFileSelected, Is.False);
            Assert.That(SentFileInfos(), Is.Empty);
        }
        else
        {
            Assert.That(reply, Is.EqualTo("\n"));
            await WaitForJobEndAsync();
            Assert.That(SentFileInfos(), Is.EqualTo(new[] { "0:/gcodes/" + name }));
        }
    }

    /// <summary>
    /// File positions are 32-bit values in the SBC protocol and in RRF (CodeHeader.FilePosition, Writer.cs:103; FilePosition in RepRapFirmware.h:726).
    /// RRF reports the position of a pause back (PrintPaused), and JobProcessor rewinds the file to it on resume (JobProcessor.cs:677-679), so in a
    /// file of 4 GiB or more a pause past 4 GiB resumed near the start of the file. RRF does not take such files either (FAT32 only, FF_FS_EXFAT 0 in
    /// ffconf.h:244), and M560 refuses them (CodeStream.cs:636-640). The file is sparse, with valid codes where FileInfoParser reads
    /// </summary>
    [TestCase("M32 \"big.g\"")]
    [TestCase("M37 P\"big.g\"")]
    public async Task FileOf4GiBIsRefused(string code)
    {
        _host = await FakeFirmwareHost.StartAsync();
        byte[] codes = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("G1 X1\n", 100000)));
        using (FileStream stream = new(Path.Combine(Host.BaseDirectory, "gcodes", "big.g"), FileMode.Create))
        {
            stream.Write(codes);
            stream.SetLength((long)uint.MaxValue + 1);
            stream.Seek(-codes.Length, SeekOrigin.End);
            stream.Write(codes);
        }

        Assert.That(await Host.ExecuteAsync(code), Does.StartWith("Error: File too big"));
        Assert.That(Jobs.IsFileSelected, Is.False);
        Assert.That(SentFileInfos(), Is.Empty);
    }
}
