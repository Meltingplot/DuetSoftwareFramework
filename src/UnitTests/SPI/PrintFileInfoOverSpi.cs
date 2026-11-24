using System;
using System.IO;
using System.Threading.Tasks;
using DuetControlServer.Files;
using NUnit.Framework;
using Host = UnitTests.SPI.LinkServiceOverSpi.Host;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.SPI;

/// <summary>
/// The SetPrintFileInfo request for a selected job file on the real SPI adapter. SPI.WritePrintFileInfo serialises the information with
/// Writer.WritePrintFileInfo into a span of the transfer buffer (SPI.cs:1039-1061) on the link task (LinkService.cs:404-411), which has no
/// catch around it. An exception there ends the link task, and DCS stops (BackgroundServiceExceptionBehavior.StopHost)
/// </summary>
[TestFixture]
public class PrintFileInfoOverSpi
{
    private static readonly TimeSpan Timeout = LinkServiceOverSpi.Timeout;

    /// <summary>
    /// Select a job file as M32 does (MCodeHandler.cs:270-278) and wait until the link task has sent its information or stopped
    /// </summary>
    private static async Task SelectAsync(Host host, string fileName, string content)
    {
        string physicalFile = Path.Combine(host.BaseDirectory, "gcodes", fileName);
        System.IO.File.WriteAllText(physicalFile, content);

        JobProcessor jobs = host.Get<JobProcessor>();
        Task fileInfoSent;
        using (await jobs.LockAsync())
        {
            fileInfoSent = await jobs.SelectFileAsync("0:/gcodes/" + fileName, physicalFile);
        }
        await Task.WhenAny(fileInfoSent, host.Service.ExecuteTask!).WaitAsync(Timeout);
    }

    /// <summary>
    /// Number of filaments in a SetPrintFileInfo packet (PrintStartedHeader.NumFilaments)
    /// </summary>
    private static uint NumFilaments(byte[] packet) => BitConverter.ToUInt32(packet, 4);

    /// <summary>
    /// A filament index from a comment made FileInfoParser add that many filaments, and 40 + 4 * 2101 bytes do not fit into the 8184 bytes of the
    /// packet span, so the Writer threw ArgumentOutOfRangeException on the link task
    /// </summary>
    [Test]
    public async Task FilamentIndexDoesNotStopTheLink()
    {
        await using Host host = await Host.StartAsync();
        await SelectAsync(host, "job.g", ";Material#0 Used: 5.0\n;Material#2100 Used: 1.0\nG1 X1\n");
        Assert.That(host.Fault, Is.Null);
        Assert.That(NumFilaments(host.WaitFor(SbcRequest.SetPrintFileInfo)), Is.EqualTo(1));
    }
}
