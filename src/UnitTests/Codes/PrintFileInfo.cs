using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer.Files;
using DuetControlServer.Link;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Codes;

/// <summary>
/// The information about a selected job file that DSF sends to RRF with SetPrintFileInfo. JobProcessor.SelectFileAsync parses the file
/// (JobProcessor.cs:311), ApplyFileSelectionAsync copies the information into the object model and asks the link task to send it
/// (JobProcessor.cs:350-357, LinkInterface.SetPrintFileInfo), and the link task writes it into its next transfer (LinkService.cs:404-411)
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

    private void WriteJob(string fileName, string content) => System.IO.File.WriteAllText(Path.Combine(Host.BaseDirectory, "gcodes", fileName), content);

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
}
