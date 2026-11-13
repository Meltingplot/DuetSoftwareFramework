using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer.Link;
using DuetControlServer.Link.Channel;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Link;

/// <summary>
/// An emergency stop of DSF ends the work of every channel as an abort of all files does, with an epoch change (decision of 2026-11-10):
/// LinkService.InvalidateCodes(true) arms every channel (Processor.Invalidate), so RRF's packets of the current epoch from before the stop are stale,
/// and every channel writes InvalidateChannel(e+1) after the stop, to every peer. A lost connection and a reset request arm nothing
/// </summary>
[TestFixture]
public class EmergencyStopEpoch
{
    private static readonly TimeSpan Timeout = FakeFirmwareHost.DefaultTimeout;

    private static readonly CodeChannel[] Channels = [.. Enum.GetValues<CodeChannel>().Where(channel => channel != CodeChannel.Unknown)];

    private static Processor Dsf(FakeFirmwareHost host, CodeChannel channel) => host.Services.GetRequiredService<Manager>()[channel];

    private static LinkInterface Link(FakeFirmwareHost host) => host.Services.GetRequiredService<LinkInterface>();

    private static byte Epoch(FakeFirmwareHost host, CodeChannel channel)
    {
        using (Dsf(host, channel).Lock())
        {
            return Dsf(host, channel).Epoch;
        }
    }

    private static string[] Messages(FakeFirmwareHost host)
    {
        using (host.Model.AccessReadOnly())
        {
            return [.. host.Model.Messages.Select(message => message.ToString())];
        }
    }

    private static void Hold(FakeFirmwareHost host)
    {
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True, "The transfers were not held");
    }

    private static void Step(FakeFirmwareHost host)
    {
        int transfer = host.Firmware.TransferCount;
        host.Firmware.HoldTransfers(1);
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.TransferCount > transfer && firmware.IsTransferHeld, Timeout), Is.True, "No transfer");
    }

    /// <summary>
    /// Request an emergency stop while the transfers are held. LinkService takes it at the top of its next iteration
    /// </summary>
    private static Task RequestStop(FakeFirmwareHost host)
    {
        Task stop = Link(host).EmergencyStopAsync();
        Assert.That(SpinWait.SpinUntil(() => Link(host).FirmwareHaltRequest is not null, Timeout), Is.True, "The stop was not requested");
        return stop;
    }

    /// <summary>
    /// Check that the link thread still performs transfers. No code reaches RRF after a stop until it is reset (FakeFirmwareTests.CodeAfterEmergencyStopWaitsForReset)
    /// </summary>
    private static bool IsLinkAlive(FakeFirmwareHost host)
    {
        int transfer = host.Firmware.TransferCount;
        return host.Firmware.WaitUntil(firmware => firmware.TransferCount > transfer + 5, Timeout) &&
            host.Services.GetServices<IHostedService>().OfType<LinkService>().Single().ExecuteTask is { IsCompleted: false };
    }

    private static FakeSbcRequest StopRequest(FakeFirmwareHost host) => host.Firmware.GetReceivedRequests().Single(request => request.Request == SbcRequest.EmergencyStop);

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task.WaitAsync(Timeout);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the stop
        }
    }

    [TestCase(FakeRrfModel.Epoch, false)]
    [TestCase(FakeRrfModel.Epoch, true)]
    [TestCase(FakeRrfModel.RecordApply, false)]
    [TestCase(FakeRrfModel.RecordApply, true)]
    [TestCase(FakeRrfModel.V7, false)]
    [TestCase(FakeRrfModel.V7, true)]
    public async Task StopCrossingAMacroFileClosedKeepsTheLinkAlive(FakeRrfModel model, bool closalInTheTransferOfTheStop)
    {
        // Review finding: the stop pops every stack to the base level (Processor.Invalidate), then a MacroFileClosed that RRF wrote before the stop popped
        // again ("Stack underrun"), which faulted the link thread before the stop went out. The closal comes in the data pending at the stop or in the
        // data of the transfer that carries the stop. In the data pending at the stop it is processed before the codes are invalidated and pops the
        // macro it was sent for (#170). In the data of the transfer of the stop a tagged one is stale; an untagged one (V7, RecordApply) is dropped by
        // the window of #134, which the armed channel opens as after an abort
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware => firmware.Model = model);
        host.WriteMacro("short.g", "G4 P1\n");
        await host.ExecuteAsync("G4 P0");
        Hold(host);
        Task<string> m98 = host.StartCode("M98 P\"short.g\"");

        // RRF's main task has closed the macro once its stack is back at the base level; the SBC task writes the closal with the next transfer
        bool started = false;
        for (int i = 0; i < 200 && !((started |= host.Firmware.GetStackDepth(CodeChannel.HTTP) > 0) && host.Firmware.GetStackDepth(CodeChannel.HTTP) == 0); i++)
        {
            Step(host);
        }
        Assert.That(started && host.Firmware.GetStackDepth(CodeChannel.HTTP) == 0, Is.True, "RRF did not close the macro");
        Assert.That(host.Firmware.GetPendingPackets().Concat(host.Firmware.GetSentPackets()).Any(packet => packet.Request == FirmwareRequest.MacroFileClosed), Is.False,
            "RRF wrote the closal already");
        if (!closalInTheTransferOfTheStop)
        {
            // RRF writes the closal with the next transfer, which reaches DSF before LinkService takes the stop
            Step(host);
        }
        Task stop = RequestStop(host);
        host.Firmware.ReleaseTransfers();

        await stop.WaitAsync(Timeout);
        await IgnoreCancellation(m98);
        Assert.That(IsLinkAlive(host), Is.True, "The link thread ended");

        FakePacket closal = host.Firmware.GetSentPackets().Single(packet => packet.Request == FirmwareRequest.MacroFileClosed && packet.Channel == CodeChannel.HTTP);
        Assert.That(closal.Transfer, Is.EqualTo(StopRequest(host).Transfer - (closalInTheTransferOfTheStop ? 0 : 1)), "The closal is not where the test puts it");
        using (Dsf(host, CodeChannel.HTTP).Lock())
        {
            Assert.That(Dsf(host, CodeChannel.HTTP).Stack, Has.Count.EqualTo(1));
        }
    }

    [TestCase(FakeRrfModel.Epoch)]
    [TestCase(FakeRrfModel.V7)]
    public async Task EveryChannelWritesInvalidateChannelAfterTheStop(FakeRrfModel model)
    {
        // After the stop every channel writes InvalidateChannel with the next epoch, never before the stop and not in its transfer, whether the RRF tags
        // or not (one path for every peer). HTTP runs a macro and Telnet a code at the stop, the other channels are idle and write it as well
        bool release = false;
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware =>
        {
            firmware.Model = model;
            firmware.OnCode("G4", code => (code.Text is "G4 P5" or "G4 P6" && !release) ? FakeReply.NotFinished : null);
        });
        host.WriteMacro("held.g", "G4 P5\n");
        Task<string> m98 = host.StartCode("M98 P\"held.g\"");
        Task<string> g4 = host.StartCode("G4 P6", CodeChannel.Telnet);
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P5" && firmware.GetCurrentCode(CodeChannel.Telnet)?.Text == "G4 P6",
            Timeout), Is.True);
        Hold(host);
        Task stop = RequestStop(host);
        host.Firmware.ReleaseTransfers();
        await stop.WaitAsync(Timeout);
        release = true;
        await IgnoreCancellation(m98);
        await IgnoreCancellation(g4);

        int stopTransfer = StopRequest(host).Transfer;
        foreach (CodeChannel channel in Channels)
        {
            Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.InvalidateChannel && request.Channel == channel),
                Timeout), Is.True, $"{channel} wrote no InvalidateChannel after the stop");
            FakeSbcRequest[] invalidations = [.. host.Firmware.GetReceivedRequests().Where(request => request.Request == SbcRequest.InvalidateChannel && request.Channel == channel)];
            Assert.That(invalidations, Has.Length.EqualTo(1), $"{channel}");
            Assert.That(invalidations[0].Transfer, Is.GreaterThan(stopTransfer), $"{channel} wrote InvalidateChannel before the stop or with it");
            Assert.That(FakeTag.EpochOf(invalidations[0].Tag), Is.EqualTo(1), $"{channel}");
            Assert.That(Epoch(host, channel), Is.EqualTo(1), $"{channel}");
        }
    }

    [Test]
    public async Task ReplyOfACodeThatTheStopCancelledIsShownAsAMessage()
    {
        // A code that RRF still runs at the stop replies with its own epoch, which is stale once DSF has stopped: it is no out-of-order reply and is shown
        // as a message (the rule of a stale reply). The fake ends it with "Machine is halted", as its stopped main task does for a G-code
        // (FakeGCodes.ActOnCode). No code follows it, because none reaches RRF after a stop until it is reset (FakeFirmwareTests)
        bool release = false;
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware =>
            firmware.OnCode("G4", code => (code.Text == "G4 P5" && !release) ? FakeReply.NotFinished : null));
        Task<string> running = host.StartCode("G4 P5");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P5", Timeout), Is.True);
        await Link(host).EmergencyStopAsync().WaitAsync(Timeout);
        await IgnoreCancellation(running);
        release = true;

        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.IsCodeReply && packet.Text.Contains("Machine is halted")), Timeout), Is.True);
        Assert.That(SpinWait.SpinUntil(() => Messages(host).Contains("Error: G4: Machine is halted"), Timeout), Is.True, "The stale reply was not shown");
        using (Dsf(host, CodeChannel.HTTP).Lock())
        {
            Assert.That(Dsf(host, CodeChannel.HTTP).OutOfOrderReplies, Is.Zero);
            Assert.That(Dsf(host, CodeChannel.HTTP).ShownStaleReplies, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task StopKeepsAnUnwrittenInvalidateChannelOfRrfsAbort()
    {
        // Review finding: RRF's tagged abort of all files arms the channel and closes RRF's gate until DSF answers with InvalidateChannel(e+1) (CE-15,
        // CE-24). If that answer could not be written before the stop, the stop used to disarm the channel and the gate stayed closed. The fake has no
        // limit on the transfer buffer, so the unwritten answer is made by arming the channel the way LinkService does for that AbortFile
        // (Processor.FilesAborted) while the transfers are held, so that the next iteration of LinkService is the one of the stop
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        await host.ExecuteAsync("G4 P1");
        Hold(host);
        using (Dsf(host, CodeChannel.HTTP).Lock())
        {
            Dsf(host, CodeChannel.HTTP).FilesAborted(true, true);
            Assert.That(Dsf(host, CodeChannel.HTTP).IsAbortUnsent, Is.True);
        }
        Task stop = RequestStop(host);
        host.Firmware.ReleaseTransfers();
        await stop.WaitAsync(Timeout);

        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.InvalidateChannel && request.Channel == CodeChannel.HTTP),
            Timeout), Is.True, "The stop dropped the answer to RRF's abort of all files");
        FakeSbcRequest invalidation = host.Firmware.GetReceivedRequests().Single(request => request.Request == SbcRequest.InvalidateChannel && request.Channel == CodeChannel.HTTP);
        Assert.That(invalidation.Transfer, Is.GreaterThan(StopRequest(host).Transfer));
        Assert.That(FakeTag.EpochOf(invalidation.Tag), Is.EqualTo(1));
    }

    [Test]
    public async Task LostConnectionAfterTheStopArmsNothing()
    {
        // A lost connection resets the epochs to 0 (CE-4) and disarms every channel, also one that the stop has armed: no InvalidateChannel follows, and
        // the next first packet carries the marker
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        await host.ExecuteAsync("G4 P1");
        await Link(host).AbortAllAsync(CodeChannel.HTTP);
        await host.ExecuteAsync("G4 P2");
        Assert.That(Epoch(host, CodeChannel.HTTP), Is.EqualTo(1));
        Hold(host);
        Task stop = RequestStop(host);
        Step(host);
        Assert.That(host.Firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.EmergencyStop) ||
            host.Firmware.GetPendingRequests().Any(request => request.Request == SbcRequest.EmergencyStop), Is.True, "The stop was not written");
        int invalidations = host.Firmware.GetReceivedRequests().Count(request => request.Request == SbcRequest.InvalidateChannel);

        // RRF loses the connection before DSF has spun its channels again
        host.Firmware.SimulateConnectionLoss();
        host.Firmware.ReleaseTransfers();
        await IgnoreCancellation(stop);
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedRequests().Count(request => (request.Tag & FakeTag.FirstMarker) != 0) == 2, Timeout), Is.True,
            "No marker after the lost connection");
        Assert.That(IsLinkAlive(host), Is.True);

        Assert.That(host.Firmware.GetReceivedRequests().Count(request => request.Request == SbcRequest.InvalidateChannel), Is.EqualTo(invalidations),
            "An InvalidateChannel was written after the lost connection");
        foreach (CodeChannel channel in Channels)
        {
            Assert.That(Epoch(host, channel), Is.Zero, $"{channel}");
            using (Dsf(host, channel).Lock())
            {
                Assert.That(Dsf(host, channel).IsAbortUnsent, Is.False, $"{channel}");
            }
        }
    }

    [Test]
    public async Task ResetRequestAfterTheStopArmsNothing()
    {
        // A reset request invalidates every channel without arming it; the link ends with the reset and DCS starts a new connection with epoch 0 and
        // the marker. Nothing writes InvalidateChannel. The reset is handled before the stop in the same iteration, so the stop is not written at all
        // (LinkService.Execute, upstream 61df011e); RRF stops everything when it resets
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        await host.ExecuteAsync("G4 P1");
        Hold(host);
        _ = RequestStop(host);
        Task reset = Link(host).ResetFirmwareAsync();
        Assert.That(SpinWait.SpinUntil(() => Link(host).FirmwareResetRequest is not null, Timeout), Is.True);
        host.Firmware.ReleaseTransfers();
        await reset.WaitAsync(Timeout);

        Assert.That(host.Firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.Reset), Is.True);
        Assert.That(host.Firmware.GetReceivedRequests().Where(request => request.Request == SbcRequest.InvalidateChannel), Is.Empty);
        foreach (CodeChannel channel in Channels)
        {
            using (Dsf(host, channel).Lock())
            {
                Assert.That(Dsf(host, channel).IsAbortUnsent, Is.False, $"{channel}");
            }
        }
    }
}
