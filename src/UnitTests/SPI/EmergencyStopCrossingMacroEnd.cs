using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer.Link;
using DuetControlServer.Link.Channel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using Host = UnitTests.SPI.LinkServiceOverSpi.Host;
using RrfPackets = UnitTests.SPI.LinkServiceOverSpi.RrfPackets;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.SPI;

/// <summary>
/// An emergency stop that DSF writes while the data RRF sent before it is still to be processed. The transfer thread writes the stop as the first
/// packet of its cycle, processes that data, invalidates the codes and transfers the stop at once (LinkService.Execute). RRF wrote that data before
/// it got the stop, e.g. the closal of a macro file that DSF still has on its stack (SbcInterface.cpp:1507-1511 at RRF ddec2d001), so it belongs to
/// the codes and macros before the invalidation. When DSF invalidated first, the closal popped the base level, Processor.Pop threw
/// "Stack underrun", and the transfer thread ended before the stop had gone out
/// </summary>
[TestFixture]
public class EmergencyStopCrossingMacroEnd
{
    private static readonly TimeSpan Timeout = FakeFirmwareHost.DefaultTimeout;

    /// <summary>
    /// Maximum number of transfers to step through until the macro has reached the wanted state
    /// </summary>
    private const int MaxSteps = 200;

    private static bool IsClosal(FakePacket packet) => packet.Request == FirmwareRequest.MacroFileClosed && packet.Channel == CodeChannel.HTTP;

    private static Task LinkTask(FakeFirmwareHost host) => host.Services.GetServices<IHostedService>().OfType<LinkService>().Single().ExecuteTask!;

    private static string LinkState(FakeFirmwareHost host) => $"transfer thread {LinkTask(host).Status} {LinkTask(host).Exception?.InnerException?.Message}";

    private static int StackDepth(Processor channel)
    {
        using (channel.Lock())
        {
            return channel.Stack.Count;
        }
    }

    /// <summary>
    /// Hold the transfers and let them go on one by one until the condition is met after a transfer. The transfer after it is held then
    /// </summary>
    private static void StepUntil(FakeFirmwareHost host, Func<bool> condition, string what)
    {
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True, "The transfers were not held");
        for (int i = 0; i < MaxSteps; i++)
        {
            int transfer = host.Firmware.TransferCount;
            host.Firmware.HoldTransfers(1);
            Assert.That(host.Firmware.WaitUntil(firmware => firmware.TransferCount > transfer && firmware.IsTransferHeld, Timeout), Is.True, "The transfer was not held");
            if (condition())
            {
                return;
            }
        }
        Assert.Fail(what);
    }

    /// <summary>
    /// M112 while RRF's closal of a finished macro is in the data DSF processes in the cycle that writes the stop. DSF processes the closal before
    /// it invalidates the codes, so the closal pops the macro and the code that started it gets RRF's final reply, and the stop goes out in the next
    /// transfer as its first packet. M112 completes once the data RRF sent with that transfer has been processed
    /// </summary>
    [Test]
    public async Task EmergencyStopWhileTheClosalOfAMacroIsPending()
    {
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        LinkInterface link = host.Services.GetRequiredService<LinkInterface>();
        Processor http = host.Services.GetRequiredService<Manager>()[CodeChannel.HTTP];
        host.WriteMacro("short.g", "G4 P1\n");

        // Run the macro until RRF has closed it and written the closal into its next transfer. DSF has not got it yet, so the macro is still on its stack
        Task<string> m98 = host.StartCode("M98 P\"short.g\"");
        StepUntil(host, () => host.Firmware.GetPendingPackets().Any(IsClosal), "RRF did not close the macro");
        Assert.That(StackDepth(http), Is.EqualTo(2), "DSF does not have the macro on its stack");

        // DSF writes the stop in the cycle after the next transfer, which delivers the closal
        Task<string> m112 = host.StartCode("M112");
        Assert.That(SpinWait.SpinUntil(() => link.FirmwareHaltRequest is not null, Timeout), Is.True, "M112 did not request the emergency stop");
        host.Firmware.ReleaseTransfers();

        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsEmergencyStopped, Timeout), Is.True, $"RRF did not get the emergency stop, {LinkState(host)}");
        FakePacket closal = host.Firmware.GetSentPackets().Single(IsClosal);
        FakeSbcRequest stop = host.Firmware.GetReceivedRequests().Single(request => request.Request == SbcRequest.EmergencyStop);
        Assert.That(stop.Transfer, Is.EqualTo(closal.Transfer + 1), "The stop did not go out in the transfer after the closal");
        Assert.That(stop.Id, Is.Zero, "The stop was not the first packet of its transfer");

        // A cancelled code gives an empty string (SimpleCode.cs:183-186), a code with RRF's empty final reply a newline
        Assert.That(await m112.WaitAsync(Timeout), Is.EqualTo("\n"));
        Assert.That(await m98.WaitAsync(Timeout), Is.EqualTo("\n"), "M98 did not get RRF's final reply");
        Assert.That(StackDepth(http), Is.EqualTo(1));
        Assert.That(LinkTask(host).IsCompleted, Is.False, LinkState(host));
    }

    /// <summary>
    /// M112 while RRF's main task has closed the macro but its SBC task writes the closal only into the transfer that carries the stop
    /// (FakeFirmware.ExchangeData, SbcInterface.cpp:1507-1511 at RRF ddec2d001). DSF has invalidated the codes by the time it processes that closal.
    /// Whatever DSF does with it, the stop has reached RRF before. With the channel epoch the invalidation of the stop has armed every channel, so the
    /// closal, which carries the epoch of the macro, is stale: it pops nothing and the transfer thread keeps running (see also EmergencyStopEpoch)
    /// </summary>
    [Test]
    public async Task EmergencyStopWhenRrfClosesAMacroInTheTransferOfTheStop()
    {
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        LinkInterface link = host.Services.GetRequiredService<LinkInterface>();
        Processor http = host.Services.GetRequiredService<Manager>()[CodeChannel.HTTP];
        host.WriteMacro("short.g", "G4 P1\n");

        // Run the macro until RRF's main task has closed it and popped its stack. The SBC task writes the closal in the next transfer, which delivers
        // it with the one after
        bool started = false;
        _ = host.StartCode("M98 P\"short.g\"");
        StepUntil(host, () => (started |= host.Firmware.GetStackDepth(CodeChannel.HTTP) > 0) && host.Firmware.GetStackDepth(CodeChannel.HTTP) == 0, "RRF did not close the macro");
        Assert.That(host.Firmware.GetPendingPackets().Any(IsClosal) || host.Firmware.GetSentPackets().Any(IsClosal), Is.False, "RRF wrote the closal already");
        Assert.That(StackDepth(http), Is.EqualTo(2), "DSF does not have the macro on its stack");

        // DSF writes the stop in the cycle after the next transfer, and the closal comes with the transfer that carries the stop
        using CancellationTokenSource cts = new();
        _ = link.EmergencyStopAsync(cts.Token);
        Assert.That(SpinWait.SpinUntil(() => link.FirmwareHaltRequest is not null, Timeout), Is.True, "The emergency stop was not requested");
        host.Firmware.ReleaseTransfers();

        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsEmergencyStopped, Timeout), Is.True, $"RRF did not get the emergency stop, {LinkState(host)}");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetSentPackets().Any(IsClosal), Timeout), Is.True, "RRF did not send the closal");
        FakePacket closal = host.Firmware.GetSentPackets().Single(IsClosal);
        FakeSbcRequest stop = host.Firmware.GetReceivedRequests().Single(request => request.Request == SbcRequest.EmergencyStop);
        Assert.That(closal.Transfer, Is.EqualTo(stop.Transfer), "The closal did not come with the transfer of the stop");

        // DSF has processed the closal once a later transfer is through. No code reaches RRF after the stop until it is reset, so the transfers show
        // that the transfer thread is alive
        int transfer = host.Firmware.TransferCount;
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.TransferCount > transfer + 5, Timeout), Is.True, $"No transfers after the closal, {LinkState(host)}");
        Assert.That(LinkTask(host).IsCompleted, Is.False, LinkState(host));
        Assert.That(StackDepth(http), Is.EqualTo(1));
        cts.Cancel();
    }

    /// <summary>
    /// An emergency stop is written into a cycle whose data from RRF cannot be processed, here a packet that runs past the data of its transfer
    /// (ArgumentOutOfRangeException, see LinkServiceOverSpi.MalformedPacketStopsTheTransferThread). The stop still goes out as the first packet of the
    /// next transfer before the error ends the transfer thread. Real SPI adapter against the SPI port of RRF (<see cref="FakeRrfSpi"/>)
    /// </summary>
    [Test, Explicit("Upstream 61df011e writes the stop after RRF's data has been processed, so an error in that data ends the transfer thread before the stop is written (LinkService.Execute); see design/open-findings.md")]
    public async Task EmergencyStopWhenTheDataBeforeItCannotBeProcessed()
    {
        await using Host host = await Host.StartAsync();
        FakeRrfSpi rrf = host.Rrf;

        // RRF has set up the transfer DSF is held at, so the malformed packet goes out with the transfer after it (FakeRrfSpi.SendPacket)
        rrf.HoldBeforeHeader(rrf.Exchanges);
        Assert.That(rrf.WaitUntilHeld(Timeout), Is.True, "DSF was not held");
        rrf.SendPacket(FirmwareRequest.CodeBufferUpdate, RrfPackets.CodeBufferUpdate(100), declaredLength: 1000);
        rrf.HoldBeforeHeader(rrf.Exchanges + 1);
        Assert.That(rrf.WaitUntilHeld(Timeout), Is.True, "DSF did not reach the transfer of the malformed packet");

        // DSF writes the stop in the cycle that processes the malformed packet
        Task stop = host.Link.EmergencyStopAsync();
        Assert.That(SpinWait.SpinUntil(() => host.Link.FirmwareHaltRequest is not null, Timeout), Is.True, "The emergency stop was not requested");
        rrf.Release();

        await host.Service.ExecuteTask!.ContinueWith(_ => { }).WaitAsync(Timeout);
        Assert.That(host.Fault?.InnerException, Is.InstanceOf<ArgumentOutOfRangeException>());
        FakeRrfSpi.ProcessedPacket? stopPacket = rrf.GetProcessedPackets().SingleOrDefault(packet => packet.Request == SbcRequest.EmergencyStop);
        Assert.That(stopPacket, Is.Not.Null, "RRF did not get the emergency stop");
        Assert.That(stopPacket!.Id, Is.Zero, "The stop was not the first packet of its transfer");
        Assert.That(stop.IsCompleted, Is.False, "The stop completed although the data RRF sent with it was not processed");
    }
}
