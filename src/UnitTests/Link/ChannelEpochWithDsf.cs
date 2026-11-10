using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes.Handlers;
using DuetControlServer.Link;
using DuetControlServer.Link.Channel;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using Code = DuetControlServer.Commands.Code;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Link;

/// <summary>
/// DSF side of the channel epoch (milestone E2, design/sbc-link-v8-requirements.md draft 8) against the fake firmware that models the RRF side
/// (<see cref="FakeRrfModel.Epoch"/>): the tags DSF writes (CE-5, CE-6, CE-8, CE-13), its filter (CE-14), its answer to RRF's abort of all files
/// (CE-15), the end of the old epoch (CE-25) and the reset with the connection (CE-4)
/// </summary>
[TestFixture]
public class ChannelEpochWithDsf
{
    private static readonly TimeSpan Timeout = FakeFirmwareHost.DefaultTimeout;

    private static readonly SbcRequest[] ChannelRequests =
    [
        SbcRequest.Code, SbcRequest.MacroStarted, SbcRequest.MacroCompleted, SbcRequest.InvalidateChannel, SbcRequest.LockAllMovementSystemsAndWaitForStandstill,
        SbcRequest.Unlock, SbcRequest.EvaluateExpression, SbcRequest.SetVariable, SbcRequest.DeleteLocalVariable, SbcRequest.SetLastCodeResult
    ];

    private static Processor Dsf(FakeFirmwareHost host, CodeChannel channel) => host.Services.GetRequiredService<Manager>()[channel];

    private static byte Epoch(FakeFirmwareHost host, CodeChannel channel)
    {
        using (Dsf(host, channel).Lock())
        {
            return Dsf(host, channel).Epoch;
        }
    }

    private static int StaleDrops(FakeFirmwareHost host, CodeChannel channel, FirmwareRequest request)
    {
        using (Dsf(host, channel).Lock())
        {
            return Dsf(host, channel).StaleDrops.GetValueOrDefault(request);
        }
    }

    private static void Step(FakeFirmwareHost host)
    {
        int transfer = host.Firmware.TransferCount;
        host.Firmware.HoldTransfers(1);
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.TransferCount > transfer && firmware.IsTransferHeld, Timeout), Is.True, "No transfer");
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task.WaitAsync(Timeout);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the abort
        }
    }

    [Test]
    public async Task DsfTagsEveryChannelPacketWithTheEpochOfItsChannel()
    {
        // CE-5, CE-6, CE-13: every channel packet carries the epoch of its channel, InvalidateChannel the new one, which becomes the current epoch
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        host.WriteMacro("epoch.g", "G4 P1\necho global.epochTest\nset global.epochTest = 1\n");
        await host.ExecuteAsync("global epochTest = 0");
        await host.ExecuteAsync("M98 P\"epoch.g\"");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        await host.ExecuteAsync("M98 P\"epoch.g\"");

        FakeSbcRequest[] requests = [.. host.Firmware.GetReceivedRequests().Where(request => request.Channel == CodeChannel.HTTP && ChannelRequests.Contains(request.Request))];
        Assert.That(requests.Select(request => request.Request), Is.SupersetOf(new[] { SbcRequest.Code, SbcRequest.MacroStarted, SbcRequest.MacroCompleted, SbcRequest.InvalidateChannel,
            SbcRequest.EvaluateExpression, SbcRequest.SetVariable }));
        Assert.That(requests.All(request => FakeTag.IsTagged(request.Tag)), Is.True, "An untagged channel packet: " + string.Join(", ", requests.Where(request => !FakeTag.IsTagged(request.Tag))));

        int invalidate = Array.FindIndex(requests, request => request.Request == SbcRequest.InvalidateChannel);
        Assert.That(requests[..invalidate].Select(request => FakeTag.EpochOf(request.Tag)), Is.All.EqualTo(0));
        Assert.That(requests[invalidate..].Select(request => FakeTag.EpochOf(request.Tag)), Is.All.EqualTo(1));
        Assert.That(Epoch(host, CodeChannel.HTTP), Is.EqualTo(1));
    }

    [Test]
    public async Task FirstPacketOfAConnectionCarriesTheMarker()
    {
        // CE-4 (decision of 2026-11-10): the first packet DSF writes after it started or ended a connection carries the first marker, whatever its type.
        // Here, as in production, that is a GetObjectModel (the query of the link capabilities, LinkService.UpdateLinkCapabilities), which has no
        // channel and carries the marker alone. A restart of RRF that DSF notices ends the connection, resets the epochs and arms the marker again
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        await host.ExecuteAsync("G4 P1");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        await host.ExecuteAsync("G4 P2");
        FakeSbcRequest first = host.Firmware.GetReceivedRequests()[0];
        Assert.That(first.Request, Is.EqualTo(SbcRequest.GetObjectModel));
        Assert.That(first.Tag, Is.EqualTo(FakeTag.FirstMarker), "The first packet does not carry the marker alone");
        Assert.That(host.Firmware.GetReceivedRequests().Count(request => (request.Tag & FakeTag.FirstMarker) != 0), Is.EqualTo(1));
        Assert.That(host.Firmware.GetReceivedRequests().Where(request => ChannelRequests.Contains(request.Request)).Select(request => request.Tag & FakeTag.FirstMarker),
            Is.All.Zero, "A channel packet carries the marker although it was not the first packet");
        Assert.That(Epoch(host, CodeChannel.HTTP), Is.EqualTo(1));

        // DSF resets the epochs before it writes anything of the new connection, so once the second marker is there they are 0. A code started while
        // DSF handles the reset may be cancelled, so the code goes after it
        host.Firmware.SimulateReset();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedRequests().Count(request => (request.Tag & FakeTag.FirstMarker) != 0) == 2, Timeout), Is.True,
            "DSF wrote no marker after the reset");
        await host.ExecuteAsync("G4 P3", CodeChannel.Telnet);
        Assert.That(Epoch(host, CodeChannel.HTTP), Is.Zero, "The epoch was not reset with the connection");
        FakeSbcRequest[] markers = [.. host.Firmware.GetReceivedRequests().Where(request => (request.Tag & FakeTag.FirstMarker) != 0)];
        Assert.That(markers, Has.Length.EqualTo(2));
        Assert.That(markers[1].Id, Is.Zero, "The marker of the second connection is not on the first packet of its transfer");
        Assert.That(markers[1].Tag, Is.EqualTo(FakeTag.FirstMarker));
    }

    [Test]
    public async Task RrfRequestBeforeDsfsFirstPacketCarriesAnEpoch()
    {
        // Decision of 2026-11-10: RRF writes nothing of a channel before it has read DSF's first packet of the connection, so a macro request that RRF
        // makes at boot goes out with epoch 0 of DSF's marker, not untagged. Without the query of the link capabilities DSF's first packet is the first
        // poll of the object model (UpdateService). The fake runs its main task after the SBC task, so the request is written one transfer after the
        // connect and DSF's poll is there by then; this test therefore also passes without the hold, which FakeFirmwareEpoch checks at the adapter
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware =>
        {
            firmware.SupportedLinkCapabilities = DuetControlServer.Link.Protocol.Shared.LinkCapabilities.None;
            firmware.RequestMacro(CodeChannel.Trigger, "boot.g");
        });
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.Request == FirmwareRequest.ExecuteMacro), Timeout), Is.True,
            "boot.g was not requested");
        FakePacket request = host.Firmware.GetSentPackets().First(packet => packet.Request == FirmwareRequest.ExecuteMacro);
        FakeSbcRequest first = host.Firmware.GetReceivedRequests()[0];
        Assert.That(request.Tag, Is.EqualTo(FakeTag.Of(0)));
        Assert.That(first.Tag & FakeTag.FirstMarker, Is.Not.Zero);
        Assert.That(request.Transfer, Is.GreaterThan(first.Transfer), "RRF wrote the request before it had read DSF's first packet");
    }

    [Test]
    public void MarkerGoesOnTheFirstPacketThatIsWritten()
    {
        // CE-4: every kind of packet takes the marker while it is pending: one without a channel alone, a channel packet with its epoch, a packet whose
        // tag was decided before (a DoCode result) with that tag. Only a packet that was written clears it, one that could not be written leaves it
        EpochConnection connection = new();
        Assert.That(connection.Tag(), Is.EqualTo(ChannelEpoch.FirstMarker));
        Assert.That(connection.Tag((byte)3), Is.EqualTo(ChannelEpoch.Tag(3) | ChannelEpoch.FirstMarker));
        Assert.That(connection.Mark(ChannelEpoch.Tag(5)), Is.EqualTo(ChannelEpoch.Tag(5) | ChannelEpoch.FirstMarker));

        ushort notWritten = connection.Tag();
        Assert.That(connection.Tag(), Is.EqualTo(notWritten), "Asking for the tag cleared the marker");
        connection.Written(connection.Tag());
        Assert.That(connection.Tag(), Is.Zero);
        Assert.That(connection.Tag((byte)3), Is.EqualTo(ChannelEpoch.Tag(3)));
        Assert.That(connection.Mark(ChannelEpoch.Tag(5)), Is.EqualTo(ChannelEpoch.Tag(5)));
    }

    [Test]
    public async Task ResentCopyKeepsTheTagWithoutTheMarker()
    {
        // CE-8: RRF refuses the evaluation for a busy buffer, and the copy that DSF writes carries the tag of the original
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        await host.ExecuteAsync("G4 P1");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        await host.ExecuteAsync("global resent = 2");
        int before = host.Firmware.GetReceivedRequests().Count(request => request.Request == SbcRequest.EvaluateExpression);
        host.Firmware.FailBufferLocks(CodeChannel.HTTP, 1);
        Assert.That(await host.ExecuteAsync("echo global.resent + 3"), Is.EqualTo("5\n"));
        FakeSbcRequest[] evaluations = [.. host.Firmware.GetReceivedRequests().Where(request => request.Request == SbcRequest.EvaluateExpression).Skip(before)];
        Assert.That(evaluations, Has.Length.EqualTo(2), "RRF did not refuse the evaluation");
        Assert.That(evaluations.Select(request => request.Tag), Is.All.EqualTo(FakeTag.Of(1)));
    }

    [Test]
    public async Task ReplyOfACodeThatRanIntoTheAbortIsShownAsAMessage()
    {
        // DF-T10 (CE-11, CE-14): the code RRF still runs at the abort replies with its own epoch, which completes no code in DSF, so the next code gets
        // its own reply. The reply is shown as a message that belongs to no code (decision of 2026-11-10). RRF #46 is switched off as in E3, so the
        // reply does go out; with it, the reply is suppressed in RRF
        bool release = false;
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware =>
        {
            firmware.CodeReplySurplus = false;
            firmware.OnCode("G4", code => (code.Text == "G4 P5" && !release) ? FakeReply.NotFinished : FakeReply.Ok($"reply of {code.Text}"));
        });
        Task<string> running = host.StartCode("G4 P5");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P5", Timeout), Is.True);
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        await IgnoreCancellation(running);
        Task<string> next = host.StartCode("G4 P6");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedCodes(CodeChannel.HTTP).Any(code => code.Text == "G4 P6"), Timeout), Is.True);
        release = true;

        Assert.That(await next.WaitAsync(Timeout), Is.EqualTo("reply of G4 P6\n"));
        Assert.That(host.Firmware.GetSentPackets().Any(packet => packet.IsCodeReply && packet.Text == "reply of G4 P5" && packet.Epoch == 0), Is.True);
        Assert.That(StaleDrops(host, CodeChannel.HTTP, FirmwareRequest.Message), Is.EqualTo(1));
        Assert.That(Messages(host), Has.One.EqualTo("reply of G4 P5"));
        using (Dsf(host, CodeChannel.HTTP).Lock())
        {
            Assert.That(Dsf(host, CodeChannel.HTTP).OutOfOrderReplies, Is.Zero);
            Assert.That(Dsf(host, CodeChannel.HTTP).ShownStaleReplies, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// Let a reply of epoch 0 wait in <see cref="Processor.PendingReplies"/>, as a reply does that arrives while a message box is open (PV 3,
    /// Processor.HandleReply). The calls are the ones LinkService makes for the packets WaitForAcknowledgement and Message
    /// </summary>
    private static void ParkReply(Processor processor, string text)
    {
        MessageTypeFlags http = (MessageTypeFlags)(1 << (int)CodeChannel.HTTP) | MessageTypeFlags.BinaryCodeReplyFlag;
        using (processor.Lock())
        {
            processor.WaitForAcknowledgement();
            Assert.That(processor.HandleReply(http, text, FakeTag.Of(0)), Is.True);
            Assert.That(processor.PendingReplies, Has.Count.EqualTo(1));
        }
    }

    [TestCase(300)]
    [TestCase(1)]
    public async Task WaitingReplyOfAnAbortedEpochCompletesNoCodeOfTheNextOne(int codes)
    {
        // A reply that waits in PendingReplies when DSF aborts the channel belongs to the old epoch. It keeps its tag, so when it is taken after
        // InvalidateChannel(e+1) it is stale and shown as a message, whether it is taken for the first buffered code of the new epoch (300 codes: they
        // do not fit into one Spin, which returns before the queue is emptied) or at the end of a Spin (1 code), where it is no out-of-order reply
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        await host.ExecuteAsync("G4 P1");
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True);
        Processor processor = Dsf(host, CodeChannel.HTTP);
        ParkReply(processor, "old reply of epoch 0");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);

        List<Task<string>> tasks = [.. Enumerable.Range(0, codes).Select(i => host.StartCode($"G4 P{i} A\"{new string('x', 150)}\""))];
        for (int i = 0; i < 100; i++)
        {
            using (processor.Lock())
            {
                if (processor.CurrentState.PendingCodes.Reader.Count >= Math.Min(codes, 30))
                {
                    break;
                }
            }
            await Task.Delay(50);
        }
        host.Firmware.ReleaseTransfers();

        string[] replies = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(replies, Is.All.EqualTo("\n"), "A code of epoch 1 got another reply");
        Assert.That(Messages(host), Has.One.EqualTo("old reply of epoch 0"));
        using (processor.Lock())
        {
            Assert.That(processor.PendingReplies, Is.Empty);
            Assert.That(processor.OutOfOrderReplies, Is.Zero);
            Assert.That(processor.ShownStaleReplies, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task WaitingReplyEndsWithTheConnection()
    {
        // Where a connection ends the epochs start at 0 again (CE-4), so a waiting reply of epoch 0 of the old connection would look current in the new
        // one. It is discarded with the connection. The transfers are held, so that no Spin takes the reply meanwhile
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True);
        Processor processor = Dsf(host, CodeChannel.HTTP);
        ParkReply(processor, "reply of the old connection");
        using (processor.Lock())
        {
            processor.ResetEpoch();
            Assert.That(processor.PendingReplies, Is.Empty);
        }
        host.Firmware.ReleaseTransfers();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StaleReplyIsShownWithTheHeadThatCameBeforeTheAbort(bool tailAfterInvalidateChannel)
    {
        // The head (PushFlag) of the reply of the current code waits in DSF when DSF aborts the channel; the tail of the same epoch comes stale, before
        // InvalidateChannel because DSF has aborted all files, or after it because the epoch has changed. The head moves to the stale reply at the abort,
        // so the reply is shown complete as one message
        bool release = false;
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware =>
            firmware.OnCode("G4", code => (code.Text == "G4 P5" && !release) ? FakeReply.NotFinished : null));
        await host.ExecuteAsync("G4 P1");
        Task<string> running = host.StartCode("G4 P5");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P5", Timeout), Is.True);
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True);

        Processor processor = Dsf(host, CodeChannel.HTTP);
        MessageTypeFlags http = (MessageTypeFlags)(1 << (int)CodeChannel.HTTP) | MessageTypeFlags.BinaryCodeReplyFlag;
        using (processor.Lock())
        {
            Assert.That(processor.BufferedCodes, Has.Count.EqualTo(1));
            Assert.That(processor.HandleReply(http | MessageTypeFlags.PushFlag | MessageTypeFlags.WarningMessageFlag, "head of the reply ", FakeTag.Of(0)), Is.True);
        }
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        if (tailAfterInvalidateChannel)
        {
            for (int i = 0; i < 20 && Epoch(host, CodeChannel.HTTP) == 0; i++)
            {
                Step(host);
            }
            Assert.That(Epoch(host, CodeChannel.HTTP), Is.EqualTo(1), "InvalidateChannel was not written");
        }
        using (processor.Lock())
        {
            Assert.That(processor.HandleReply(http, "tail of the reply", FakeTag.Of(0)), Is.True);
        }
        host.Firmware.ReleaseTransfers();
        release = true;
        await IgnoreCancellation(running);

        Assert.That(Messages(host), Has.One.EqualTo("Warning: head of the reply tail of the reply"));
        Assert.That(Messages(host), Has.None.EqualTo("tail of the reply"));
        Assert.That(await host.ExecuteAsync("G4 P6"), Is.EqualTo("\n"), "The head went to the next code");
    }

    [Test]
    public void SameTagIgnoresTheMarkerAndTheFreeBits()
    {
        // CE-5: an answer is matched to its request by tagged and epoch only, as the filter of stale packets compares them
        Assert.That(ChannelEpoch.IsSameTag(ChannelEpoch.Tag(3), ChannelEpoch.Tag(3)), Is.True);
        Assert.That(ChannelEpoch.IsSameTag((ushort)(ChannelEpoch.Tag(3) | ChannelEpoch.FirstMarker), ChannelEpoch.Tag(3)), Is.True);
        Assert.That(ChannelEpoch.IsSameTag((ushort)(ChannelEpoch.Tag(3) | 0x0FC0), ChannelEpoch.Tag(3)), Is.True);
        Assert.That(ChannelEpoch.IsSameTag(ChannelEpoch.Tag(3), ChannelEpoch.Tag(4)), Is.False);
        Assert.That(ChannelEpoch.IsSameTag(ChannelEpoch.Tag(0), 0), Is.False, "An untagged field is not epoch 0");
        Assert.That(ChannelEpoch.IsSameTag(ChannelEpoch.FirstMarker, 0), Is.True);
    }

    [Test]
    public async Task EvaluationThatIsTheFirstPacketOfAConnectionIsAnswered()
    {
        // CE-4, CE-6: when an EvaluateExpression is DSF's first packet of a connection it carries the first marker, and the request keeps the header field
        // it was written with. RRF answers with the tag without the marker (SbcInterface.cpp:768 in the RRF branch), and the answer completes the request.
        // Without the query of the link capabilities and with a long poll interval nothing else is written after the reset
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(
            firmware => firmware.SupportedLinkCapabilities = LinkCapabilities.None, settings => settings.ModelUpdateInterval = 60000);
        await host.ExecuteAsync("G4 P1");
        host.Firmware.SimulateReset();
        Assert.That(host.Firmware.WaitUntil(firmware => !firmware.IsResetPending, Timeout), Is.True, "DSF did not see the reset");
        int transfer = host.Firmware.TransferCount;
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.TransferCount > transfer + 1, Timeout), Is.True);
        int before = host.Firmware.GetReceivedRequests().Count;
        Assert.That(host.Firmware.GetReceivedRequests().Count(request => (request.Tag & FakeTag.FirstMarker) != 0), Is.EqualTo(1), "Something else was written after the reset");

        object? result = await host.Services.GetRequiredService<LinkInterface>().EvaluateExpressionAsync(CodeChannel.HTTP, "1+1").WaitAsync(Timeout);
        Assert.That(result, Is.EqualTo(2));
        FakeSbcRequest first = host.Firmware.GetReceivedRequests()[before];
        Assert.That(first.Request, Is.EqualTo(SbcRequest.EvaluateExpression));
        Assert.That(first.Tag, Is.EqualTo((ushort)(FakeTag.Of(0) | FakeTag.FirstMarker)));
        Assert.That(host.Firmware.GetSentPackets().Last(packet => packet.Request == FirmwareRequest.EvaluationResult).Tag, Is.EqualTo(FakeTag.Of(0)));
    }

    [Test]
    public async Task PartsOfAStaleReplyAreJoinedApartFromTheReplyOfTheCurrentCode()
    {
        // Decision of 2026-11-10: the parts (PushFlag) of a stale reply are joined apart from the incomplete reply of the current code, and only with parts
        // of the same epoch: a part that never completes is discarded by the stale reply of another epoch and does not leak into it. The joined reply is
        // shown with the type of its flags
        bool release = false;
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware =>
            firmware.OnCode("G4", code => (code.Text == "G4 P7" && !release) ? FakeReply.NotFinished : FakeReply.Ok("end")));
        await host.ExecuteAsync("G4 P1");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        Task<string> held = host.StartCode("G4 P7");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P7", Timeout), Is.True);
        Assert.That(Epoch(host, CodeChannel.HTTP), Is.EqualTo(1));

        // Epoch 1 is current, 0 and 63 are older (63 is 2 behind 1 as a 6-bit serial number)
        MessageTypeFlags http = (MessageTypeFlags)(1 << (int)CodeChannel.HTTP) | MessageTypeFlags.BinaryCodeReplyFlag;
        Processor processor = Dsf(host, CodeChannel.HTTP);
        using (processor.Lock())
        {
            Assert.That(processor.HandleReply(http | MessageTypeFlags.PushFlag, "current ", FakeTag.Of(1)), Is.True);
            Assert.That(processor.HandleReply(http | MessageTypeFlags.PushFlag, "dangling ", FakeTag.Of(63)), Is.True);
            Assert.That(processor.HandleReply(http | MessageTypeFlags.WarningMessageFlag | MessageTypeFlags.PushFlag, "stale ", FakeTag.Of(0)), Is.True);
            Assert.That(processor.HandleReply(http, "reply", FakeTag.Of(0)), Is.True);
            Assert.That(processor.HandleReply(http, string.Empty, FakeTag.Of(0)), Is.True);
        }
        release = true;

        Assert.That(await held.WaitAsync(Timeout), Is.EqualTo("current end\n"));
        Assert.That(Messages(host), Has.One.EqualTo("Warning: stale reply"));
        Assert.That(Messages(host), Has.None.Contains("dangling"));
        using (processor.Lock())
        {
            Assert.That(processor.ShownStaleReplies, Is.EqualTo(1));
            Assert.That(processor.OutOfOrderReplies, Is.Zero);
        }
    }

    [Test]
    public async Task IncompleteStaleReplyEndsWithTheConnection()
    {
        // Decision of 2026-11-10: the part of a stale reply does not survive the end of the connection, after which the epochs start at 0 again
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        await host.ExecuteAsync("G4 P1");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        await host.ExecuteAsync("G4 P2");
        MessageTypeFlags http = (MessageTypeFlags)(1 << (int)CodeChannel.HTTP) | MessageTypeFlags.BinaryCodeReplyFlag;
        Processor processor = Dsf(host, CodeChannel.HTTP);
        using (processor.Lock())
        {
            Assert.That(processor.HandleReply(http | MessageTypeFlags.PushFlag, "before the reset ", FakeTag.Of(0)), Is.True);
            processor.ResetEpoch();
            Assert.That(processor.Epoch, Is.Zero);
            Assert.That(processor.HandleReply(http, "after the abort", FakeTag.Of(63)), Is.True);
        }
        Assert.That(Messages(host), Has.One.EqualTo("after the abort"));
    }

    private static string[] Messages(FakeFirmwareHost host)
    {
        using (host.Model.AccessReadOnly())
        {
            return [.. host.Model.Messages.Select(message => message.ToString())];
        }
    }

    private static string[] ExecutedTexts(FakeFirmwareHost host, CodeChannel channel) => [.. host.Firmware.GetExecutedCodes(channel).Select(code => code.Text)];

    private static int OutOfOrderReplies(FakeFirmwareHost host, CodeChannel channel)
    {
        using (Dsf(host, channel).Lock())
        {
            return Dsf(host, channel).OutOfOrderReplies;
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ToolChangeThatSurvivesAnAbortRunsItsOtherMacros(bool codeReplySurplus)
    {
        // Decision of 2026-11-10 on LA-25 (C): a T-code at the base level survives an abort inside tfree, as in standalone RRF, and RRF asks for tpre and
        // tpost as system macros with the new epoch. DSF runs them in order. G4 P1, which ran in tfree at the abort, goes on in RRF and ends on the
        // level of tpre. A code sent after the abort runs after the T-code has ended and gets its own reply: RRF holds it in its ring while the state
        // machine runs and drops it with the next macro request, DSF suspends it under the system macro and sends it again (v7). No reply completes
        // another code. The late reply of G4 P1 carries the old epoch, so RRF #46 (codeReplySurplus) does not suppress it and DSF decides by its tag
        bool release = false;
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware =>
        {
            firmware.CodeReplySurplus = codeReplySurplus;
            firmware.OnCode("T1", _ => FakeReply.ToolChange("tfree0.g", "tpre1.g", "tpost1.g"));
            firmware.OnCode("G4", code => (code.Text == "G4 P1" && !release) ? FakeReply.NotFinished : FakeReply.Ok($"reply of {code.Text}"));
        });
        host.WriteMacro("tfree0.g", "G4 P1\nG4 P2\n");
        host.WriteMacro("tpre1.g", "G4 P3\n");
        host.WriteMacro("tpost1.g", "G4 P4\n");
        Task<string> t1 = host.StartCode("T1");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P1", Timeout), Is.True, "tfree0.g did not run");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        Task<string> next = host.StartCode("G4 P5");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetSentPackets().Count(packet => packet.Request == FirmwareRequest.ExecuteMacro) == 2, Timeout), Is.True,
            "T1 did not go on with tpre1.g");
        release = true;

        Assert.That(await next.WaitAsync(Timeout), Is.EqualTo("reply of G4 P5\n"));
        Assert.That(ExecutedTexts(host, CodeChannel.HTTP), Is.EqualTo(new[] { "T1", "G4 P1", "G4 P3", "G4 P4", "G4 P5" }));
        FakePacket[] requests = [.. host.Firmware.GetSentPackets().Where(packet => packet.Request == FirmwareRequest.ExecuteMacro && packet.Channel == CodeChannel.HTTP)];
        Assert.That(requests.Select(packet => (packet.Text, packet.IsFromCode, packet.Epoch)),
            Is.EqualTo(new[] { ("tfree0.g", true, 0), ("tpre1.g", false, 1), ("tpost1.g", false, 1) }));
        Assert.That(host.Firmware.GetSentPackets().Any(packet => packet.IsCodeReply && packet.Text.Length == 0 && packet.RelatedCode?.Text == "T1" && packet.Epoch == 0), Is.True,
            "T1 did not end in RRF with a reply of its own epoch");

        // T1 ended in DSF with the abort, not with a reply of another code
        string? t1Reply = null;
        try
        {
            t1Reply = await t1.WaitAsync(Timeout);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the abort
        }
        Assert.That(t1Reply ?? string.Empty, Does.Not.Contain("reply of"));
        Assert.That(OutOfOrderReplies(host, CodeChannel.HTTP), Is.Zero);

        // The late reply of G4 P1 is of the old epoch and is shown as a message, with and without RRF #46, the empty final reply of T1 is not shown. The
        // replies of the codes of tpre and tpost are shown as well, as DSF shows the output of every system macro
        Assert.That(Messages(host).Count(message => message == "reply of G4 P1"), Is.EqualTo(1));
        Assert.That(Messages(host).Where(message => message != "reply of G4 P1"), Is.EquivalentTo(new[] { "reply of G4 P3", "reply of G4 P4" }));
        Assert.That(host.Firmware.MovementLockOwner, Is.Null);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task HomingCodeThatSurvivesAnAbortWithoutHomingAnAxisEndsWithAnError(bool codeReplySurplus)
    {
        // Owner decision D-A: the routine of the abort leaves the state machine alone, so a G28 whose homing file the abort popped goes on in homing2
        // (GCodes4.cpp:394-413). homex.g did not home X before the abort, so G28 ends with "Failed to home axes XY", a reply of the old epoch. DSF shows
        // it as an error message that belongs to no code, and the next code gets its own reply. RRF #46 does not suppress it, because it carries a tag
        bool release = false;
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware =>
        {
            firmware.CodeReplySurplus = codeReplySurplus;
            firmware.OnCode("G28", _ => FakeReply.Home('X', 'Y'));
            firmware.OnCode("G4", code => (code.Text == "G4 P1" && !release) ? FakeReply.NotFinished : FakeReply.Ok());
        });
        host.WriteMacro("homex.g", "G4 P1\nG1 H1 X-300\n");
        host.WriteMacro("homey.g", "G1 H1 Y-300\n");
        Task<string> g28 = host.StartCode("G28");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P1", Timeout), Is.True, "homex.g did not run");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        await IgnoreCancellation(g28);
        Task<string> next = host.StartCode("M115");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedCodes(CodeChannel.HTTP).Any(code => code.Text == "M115"), Timeout), Is.True);
        release = true;

        Assert.That(await next.WaitAsync(Timeout), Does.Not.Contain("home"));
        FakePacket error = host.Firmware.GetSentPackets().Single(packet => packet.IsCodeReply && packet.Text.StartsWith("Failed to home axes"));
        Assert.That(error.Epoch, Is.Zero);
        Assert.That(Messages(host), Has.One.EqualTo("Error: Failed to home axes XY"), "The error of the old G28 did not reach the user");
        Assert.That(host.Firmware.GetSentPackets().Count(packet => packet.Request == FirmwareRequest.ExecuteMacro), Is.EqualTo(1), "G28 asked for homey.g");
        Assert.That(OutOfOrderReplies(host, CodeChannel.HTTP), Is.Zero);
    }

    [Test]
    public async Task HomingCodeThatSurvivesAnAbortAfterHomingAnAxisGoesOn()
    {
        // Owner decision D-A: homex.g homed X before the abort, so G28 goes on in homing2 and asks for homey.g, as a system macro request of the new
        // epoch (C2). DSF runs homey.g, G28 ends with an empty reply of the old epoch, which DSF drops, and the code sent after the abort gets its own
        // reply. No error is shown
        bool release = false;
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware =>
        {
            firmware.OnCode("G28", _ => FakeReply.Home('X', 'Y'));
            firmware.OnCode("G4", code => (code.Text == "G4 P1" && !release) ? FakeReply.NotFinished : FakeReply.Ok());
        });
        host.WriteMacro("homex.g", "G1 H1 X-300\nG4 P1\n");
        host.WriteMacro("homey.g", "G1 H1 Y-300\n");
        Task<string> g28 = host.StartCode("G28");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P1", Timeout), Is.True, "homex.g did not run");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        await IgnoreCancellation(g28);
        Task<string> next = host.StartCode("M115");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetSentPackets().Count(packet => packet.Request == FirmwareRequest.ExecuteMacro) == 2, Timeout), Is.True,
            "G28 did not go on with homey.g");
        release = true;

        Assert.That(await next.WaitAsync(Timeout), Is.EqualTo("\n"));
        FakePacket[] requests = [.. host.Firmware.GetSentPackets().Where(packet => packet.Request == FirmwareRequest.ExecuteMacro)];
        Assert.That(requests.Select(packet => (packet.Text, packet.IsFromCode, packet.Epoch)), Is.EqualTo(new[] { ("homex.g", true, 0), ("homey.g", false, 1) }));
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.AxesHomed.Count == 2, Timeout), Is.True, "homey.g did not home Y");
        Assert.That(host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text), Has.Member("G1 H1 Y-300"));
        Assert.That(Messages(host), Has.None.Contains("home"));
        Assert.That(OutOfOrderReplies(host, CodeChannel.HTTP), Is.Zero);
    }

    [Test]
    public async Task HomingCodeAbortedBeforeTheFirstCodeOfItsFileEndsWithOneErrorMessage()
    {
        // As HomingCodeThatSurvivesAnAbortWithoutHomingAnAxisEndsWithAnError, but DSF aborts the channel after it has written MacroStarted for homex.g and before the first
        // code of homex.g. RRF clears macroJustStarted when the abort pops the macro (RRF 67e3c69f0), so the error is a final reply and reaches the user
        // complete as one message, not as the head of a push message that never ends
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware => firmware.OnCode("G28", _ => FakeReply.Home('X', 'Y')));
        host.WriteMacro("homex.g", "G1 H1 X-300\n");
        host.WriteMacro("homey.g", "G1 H1 Y-300\n");
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True);
        Task<string> g28 = host.StartCode("G28");
        for (int i = 0; i < 200 && !host.Firmware.GetPendingRequests().Any(request => request.Request == SbcRequest.MacroStarted && request.Channel == CodeChannel.HTTP); i++)
        {
            Step(host);
        }
        Assert.That(host.Firmware.GetPendingRequests().Any(request => request.Request == SbcRequest.MacroStarted && request.Channel == CodeChannel.HTTP), Is.True,
            "DSF did not start homex.g");
        Assert.That(host.Firmware.GetPendingRequests().Any(request => request.Request == SbcRequest.Code), Is.False);
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        host.Firmware.ReleaseTransfers();
        await IgnoreCancellation(g28);

        Assert.That(await host.ExecuteAsync("M115"), Does.Not.Contain("home"));
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.IsCodeReply && packet.Text.StartsWith("Failed to home axes")), Timeout),
            Is.True, "G28 did not end with an error");
        Assert.That(host.Firmware.GetReceivedCodes(CodeChannel.HTTP).Select(code => code.Text), Has.None.EqualTo("G1 H1 X-300"), "A code of homex.g reached RRF");
        FakePacket error = host.Firmware.GetSentPackets().Single(packet => packet.IsCodeReply && packet.Text.StartsWith("Failed to home axes"));
        Assert.That(error.MessageType.HasFlag(MessageTypeFlags.PushFlag), Is.False);
        Assert.That(Messages(host), Has.One.EqualTo("Error: Failed to home axes XY"));
    }

    [Test]
    public async Task TaggedAbortOfAllFilesIsAnsweredWithInvalidateChannel()
    {
        // CE-15, CE-24: RRF's own abort of all files of the current epoch closes its gate; DSF answers it with InvalidateChannel(e+1), which opens it
        bool release = false;
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware => firmware.OnCode("G4", code => (code.Text == "G4 P1" && !release) ? FakeReply.NotFinished : null));
        host.WriteMacro("held.g", "G4 P1\nG4 P2\n");
        Task<string> m98 = host.StartCode("M98 P\"held.g\"");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P1", Timeout), Is.True);
        host.Firmware.AbortAllFiles(CodeChannel.HTTP);
        await IgnoreCancellation(m98);
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.InvalidateChannel), Timeout), Is.True,
            "DSF did not answer the abort of all files");
        Assert.That(FakeTag.EpochOf(host.Firmware.GetReceivedRequests().Single(request => request.Request == SbcRequest.InvalidateChannel).Tag), Is.EqualTo(1));

        // G4 P1 survives at the base level, as a code that runs does (CE-25), and the next code waits behind it
        release = true;
        Assert.That(await host.ExecuteAsync("G4 P3"), Is.EqualTo("\n"));
    }

    [Test]
    public async Task EpochChangeFailsThePendingRequestsOfTheChannel()
    {
        // CE-25: RRF drops a request of an older epoch without an answer (CE-10), so DSF fails the evaluations, variables and code results of the
        // channel where it writes InvalidateChannel(e+1). Here RRF refuses the evaluation while the abort comes
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        await host.ExecuteAsync("G4 P1");
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True);
        Task<object?> evaluation = host.Services.GetRequiredService<LinkInterface>().EvaluateExpressionAsync(CodeChannel.HTTP, "1+1");
        host.Firmware.FailBufferLocks(CodeChannel.HTTP, 1000);
        Step(host);
        Step(host);
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        Step(host);
        Assert.That(async () => await evaluation.WaitAsync(Timeout), Throws.InstanceOf<OperationCanceledException>());
        host.Firmware.FailBufferLocks(CodeChannel.HTTP, 0);
        host.Firmware.ReleaseTransfers();
        Assert.That(await host.ExecuteAsync("echo 2+2"), Is.EqualTo("4\n"));
    }

    [Test]
    public async Task LockQueuedAfterAnAbortGoesOutAfterInvalidateChannel()
    {
        // CE-13, CE-14: a lock that is requested between DSF's abort and its write of InvalidateChannel (M505 does that when its flush ends with the
        // abort) must not go out first. RRF would answer it with the old epoch, DSF drops that answer as stale, and the channel would wait for it
        // without ever writing InvalidateChannel, while RRF holds the movement lock. After RRF's own abort of all files the same order would leave
        // the lock behind RRF's gate (CE-24), which only that InvalidateChannel opens
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        LinkInterface link = host.Services.GetRequiredService<LinkInterface>();
        await host.ExecuteAsync("G4 P1");
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True);
        await link.AbortAllAsync(CodeChannel.HTTP);
        Task<IAsyncDisposable> lockTask = link.LockAllMovementSystemsAndWaitForStandstill(CodeChannel.HTTP);
        host.Firmware.ReleaseTransfers();

        await using (await lockTask.WaitAsync(Timeout))
        {
            Assert.That(host.Firmware.MovementLockOwner, Is.EqualTo(CodeChannel.HTTP));
        }
        FakeSbcRequest[] requests = [.. host.Firmware.GetReceivedRequests().Where(request => request.Channel == CodeChannel.HTTP &&
            request.Request is SbcRequest.InvalidateChannel or SbcRequest.LockAllMovementSystemsAndWaitForStandstill)];
        Assert.That(requests.Select(request => request.Request), Is.EqualTo(new[] { SbcRequest.InvalidateChannel, SbcRequest.LockAllMovementSystemsAndWaitForStandstill }));
        Assert.That(requests.Select(request => FakeTag.EpochOf(request.Tag)), Is.All.EqualTo(1));
        Assert.That(await host.ExecuteAsync("echo 2+2"), Is.EqualTo("4\n"));
        Assert.That(host.Firmware.MovementLockOwner, Is.Null);
    }

    [Test]
    public async Task CodesAfterAnAbortGoOutWithoutWaitingForRrf()
    {
        // CE-13: an RRF that tags never refuses InvalidateChannel, so DSF does not hold the channel until RRF has taken it (#161): the next code goes out
        // in the transfer after the one with InvalidateChannel, and RRF keeps it in its ring until the apply (CE-7)
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        await host.ExecuteAsync("G4 P1");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        await host.ExecuteAsync("G4 P2");
        int invalidate = host.Firmware.GetReceivedRequests().Single(request => request.Request == SbcRequest.InvalidateChannel).Transfer;
        int code = host.Firmware.GetReceivedRequests().Single(request => request.Request == SbcRequest.Code && request.Code!.Text == "G4 P2").Transfer;
        Assert.That(code, Is.LessThanOrEqualTo(invalidate + 1));
    }

    /// <summary>
    /// M-code handler of DCS that holds M111 coming from the firmware until released
    /// </summary>
    private sealed class HoldingMCodeHandler(ICodeHandler handler, Task release, TaskCompletionSource entered) : ICodeHandler
    {
        public async ValueTask<Message?> ProcessAsync(Code code, CancellationToken cancellationToken)
        {
            if (code.Flags.HasFlag(CodeFlags.IsFromFirmware) && code.Type == CodeType.MCode && code.MajorNumber == 111)
            {
                entered.TrySetResult();
                await release.WaitAsync(cancellationToken);
            }
            return await handler.ProcessAsync(code, cancellationToken);
        }

        public ValueTask CodeExecutedAsync(Code code, CancellationToken cancellationToken) => handler.CodeExecutedAsync(code, cancellationToken);
    }

    [Test]
    public async Task ResultOfADoCodeOfAnOlderEpochIsNotSent()
    {
        // AC-14, CE-25 (e): DSF does not send the result of a DoCode whose channel it aborted while it ran, and RRF has ended that DoCode at the apply.
        // So no late result can finish a newer DoCode, which is what a DSF without tags does (RecordApplyWithDsf, Q37)
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously), entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(configureServices: services =>
            services.AddKeyedSingleton<ICodeHandler>(Keys.MCodes, (provider, _) => new HoldingMCodeHandler(ActivatorUtilities.CreateInstance<MCodeHandler>(provider), release.Task, entered)));
        await host.ExecuteAsync("G4 P1", CodeChannel.Aux);
        host.Firmware.PassCodeToSbc(CodeChannel.Aux, "M111 P-1");
        await entered.Task.WaitAsync(Timeout);
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.Aux);
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsIdle(CodeChannel.Aux), Timeout), Is.True, "RRF did not end the DoCode");
        host.Firmware.PassCodeToSbc(CodeChannel.Aux, "M929");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetSentPackets().Count(packet => packet.Request == FirmwareRequest.DoCode) == 2, Timeout), Is.True);
        release.SetResult();

        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetFinishedSbcCodes().Count == 1 && firmware.IsIdle(CodeChannel.Aux), Timeout), Is.True, "M929 did not finish");
        Assert.That(host.Firmware.GetFinishedSbcCodes()[0], Is.EqualTo(("M929", "Event logging is disabled\n")));
        Assert.That(host.Firmware.GetReceivedMessages().Select(message => message.Text), Has.None.StartsWith("Current DCS log level"));
        using (Dsf(host, CodeChannel.Aux).Lock())
        {
            Assert.That(Dsf(host, CodeChannel.Aux).DroppedResults, Is.EqualTo(1));
        }
    }

    private static int Markers(FakeFirmwareHost host) => host.Firmware.GetReceivedRequests().Count(request => (request.Tag & FakeTag.FirstMarker) != 0);

    private static MachineStatus Status(FakeFirmwareHost host)
    {
        using (host.Model.AccessReadOnly())
        {
            return host.Model.State.Status;
        }
    }

    [Test]
    public async Task ConnectionThatDsfEndsWhileRrfRunsItEndsAsALostOne()
    {
        // Owner decision D-B: DSF ends the connection while RRF still runs it and notices nothing, e.g. after a stall that is longer than DSF waits but
        // shorter than SbcConnectionTimeout. DSF's first packet of the new connection carries the first marker, which RRF reads on a connection whose
        // packets it has read, so it ends that connection as a lost one before it handles the packet as the first one of the new connection
        // (SbcInterface.cpp:333-357 at 4d687cec0): it stops the machine (EndConnection, :282-298) and drops what DSF sent before (InvalidateResources),
        // so no code of old.g that waited in RRF's buffer runs. The code that still ran belongs to the ended connection, so none of its replies goes to
        // DSF (owner decision of 2026-11-10, option 1); CodeThatOutlivesAConnectionDsfEndedCompletesNoCodeOfTheNextOne checks this while a code of the
        // new connection is out
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware =>
        {
            firmware.OnCode("G4", code => (code.Text == "G4 P5") ? FakeReply.NotFinished : FakeReply.Ok());
            firmware.OnCode("M115", _ => FakeReply.Ok("FIRMWARE_NAME: fake"));
        });
        host.WriteMacro("old.g", "G4 P5\nG4 P6\nG4 P7\n");
        Task<string> macro = host.StartCode("M98 P\"old.g\"");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P5" &&
            firmware.GetReceivedCodes(CodeChannel.HTTP).Any(code => code.Text == "G4 P7"), Timeout), Is.True, "old.g did not run");
        Assert.That(Markers(host), Is.EqualTo(1));

        host.Firmware.SimulateDsfConnectionEnd();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsEmergencyStopped, Timeout), Is.True, "The marker on a running connection did not end it");
        Assert.That(Markers(host), Is.EqualTo(2));
        await IgnoreCancellation(macro);

        // Nothing of the old connection that waited in RRF's buffer runs, the code that still ran ends without a reply to DSF
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.HTTP) is null, Timeout), Is.True, "G4 P5 did not end");
        Assert.That(host.Firmware.GetDiscardedCodes(CodeChannel.HTTP).Select(code => code.Text), Is.EquivalentTo(new[] { "G4 P6", "G4 P7" }));
        Assert.That(ExecutedTexts(host, CodeChannel.HTTP), Has.None.EqualTo("G4 P6").And.None.EqualTo("G4 P7"));

        // DSF sees the machine halted and goes on with the new connection: a status request gets its own reply
        Assert.That(await host.ExecuteAsync("M115"), Is.EqualTo("FIRMWARE_NAME: fake\n"));
        Assert.That(host.Firmware.GetSentPackets().Where(packet => packet.IsCodeReply && packet.RelatedCode?.Text == "G4 P5"), Is.Empty,
            "A reply of the ended connection reached DSF");
        Assert.That(Messages(host), Has.None.Contains("Machine is halted"));
        for (int i = 0; i < 100 && Status(host) != MachineStatus.Halted; i++)
        {
            await Task.Delay(50);
        }
        Assert.That(Status(host), Is.EqualTo(MachineStatus.Halted));
        Assert.That(OutOfOrderReplies(host, CodeChannel.HTTP), Is.Zero);
        Assert.That(Epoch(host, CodeChannel.HTTP), Is.Zero);
    }

    private static async Task WaitForPendingCode(FakeFirmwareHost host, CodeChannel channel)
    {
        for (int i = 0; i < 100; i++)
        {
            using (Dsf(host, channel).Lock())
            {
                if (Dsf(host, channel).CurrentState.PendingCodes.Reader.Count > 0)
                {
                    return;
                }
            }
            await Task.Delay(50);
        }
        Assert.Fail("The code did not reach the channel");
    }

    [Test]
    public async Task CodeThatOutlivesAConnectionDsfEndedCompletesNoCodeOfTheNextOne()
    {
        // Owner decision of 2026-11-10 on the open finding of D-B (option 1): a code from the SBC that was taken in a connection and is still being
        // executed when the connection ends belongs to that connection, and none of its replies goes to the SBC, whatever connection is up by then.
        // Here DSF ends the connection while RRF still runs it, and M115 of the new connection is out when G4 P5 of the old one ends ("Machine is
        // halted" after the stop of EndConnection). Without the rule that untagged reply completed M115, and M115's own reply was shown as a message
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware =>
        {
            firmware.OnCode("G4", code => (code.Text == "G4 P5") ? FakeReply.NotFinished : FakeReply.Ok());
            firmware.OnCode("M115", _ => FakeReply.Ok("FIRMWARE_NAME: fake"));
        });
        host.WriteMacro("old.g", "G4 P5\nG4 P6\nG4 P7\n");
        Task<string> macro = host.StartCode("M98 P\"old.g\"");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P5" &&
            firmware.GetReceivedCodes(CodeChannel.HTTP).Any(code => code.Text == "G4 P7"), Timeout), Is.True, "old.g did not run");

        // DSF ends the connection and writes the marker of the next one, then M115 is queued before that marker reaches RRF
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True);
        host.Firmware.SimulateDsfConnectionEnd();
        Step(host);
        Assert.That(host.Firmware.GetPendingRequests().Any(request => (request.Tag & FakeTag.FirstMarker) != 0), Is.True, "DSF did not write the marker");
        Task<string> m115 = host.StartCode("M115");
        await WaitForPendingCode(host, CodeChannel.HTTP);
        host.Firmware.ReleaseTransfers();
        await IgnoreCancellation(macro);

        Assert.That(await m115.WaitAsync(Timeout), Is.EqualTo("FIRMWARE_NAME: fake\n"), "M115 got a reply of the ended connection");
        Assert.That(host.Firmware.IsEmergencyStopped, Is.True);
        Assert.That(host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text), Has.Member("G4 P5").And.None.EqualTo("G4 P6"));
        Assert.That(host.Firmware.GetSentPackets().Where(packet => packet.IsCodeReply && packet.RelatedCode?.Text == "G4 P5"), Is.Empty,
            "A reply of the ended connection reached DSF");
        Assert.That(OutOfOrderReplies(host, CodeChannel.HTTP), Is.Zero);
        Assert.That(Messages(host), Has.None.Contains("Machine is halted").And.None.Contains("FIRMWARE_NAME"));
    }

    [Test]
    public async Task CodeThatOutlivesALossRrfNoticedCompletesNoCodeOfTheNextOne()
    {
        // As CodeThatOutlivesAConnectionDsfEndedCompletesNoCodeOfTheNextOne, for a loss that RRF notices. A reply made while RRF is not connected is
        // dropped anyway (SbcInterface.cpp:2673-2676), so the old code is a status request, which runs on after the stop (GCodes2.cpp:617-627) and
        // ends only after the reconnect, while M115 of the new connection waits behind it in RRF's buffer. Its final reply does not go to the SBC
        bool release = false;
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware =>
        {
            firmware.OnCode("M114", _ => release ? FakeReply.Ok("position of the old connection") : FakeReply.NotFinished);
            firmware.OnCode("M115", _ => FakeReply.Ok("FIRMWARE_NAME: fake"));
        });
        Task<string> m114 = host.StartCode("M114");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "M114", Timeout), Is.True, "M114 did not start");

        host.Firmware.SimulateConnectionLoss();
        await IgnoreCancellation(m114);
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedRequests().Count(request => (request.Tag & FakeTag.FirstMarker) != 0) == 2, Timeout), Is.True,
            "DSF did not start a new connection");
        Task<string> m115 = host.StartCode("M115");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedCodes(CodeChannel.HTTP).Any(code => code.Text == "M115"), Timeout), Is.True,
            "M115 did not reach RRF");
        Assert.That(host.Firmware.GetCurrentCode(CodeChannel.HTTP)?.Text, Is.EqualTo("M114"), "M114 did not outlive the connection");
        Assert.That(host.Firmware.IsConnected, Is.True);
        release = true;

        Assert.That(await m115.WaitAsync(Timeout), Is.EqualTo("FIRMWARE_NAME: fake\n"), "M115 got a reply of the ended connection");
        Assert.That(host.Firmware.GetSentPackets().Where(packet => packet.IsCodeReply && packet.RelatedCode?.Text == "M114"), Is.Empty,
            "A reply of the ended connection reached DSF");
        Assert.That(OutOfOrderReplies(host, CodeChannel.HTTP), Is.Zero);
        Assert.That(Messages(host), Has.None.Contains("old connection").And.None.Contains("FIRMWARE_NAME"));
    }

    [Test]
    public async Task MarkerOfAConnectionToARestartedRrfStopsNothing()
    {
        // Owner decision D-B: a first marker on the first packet that RRF reads in a connection only starts it. Here RRF restarts while DSF runs, as
        // at boot: DSF's first packet after the restart carries the marker, RRF does not stop the machine, asks for config.g once it has read that
        // packet (with epoch 0, SbcInterface.cpp:1635) and DSF serves it
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware => firmware.OnCode("M115", _ => FakeReply.Ok("FIRMWARE_NAME: fake")));
        Assert.That(Markers(host), Is.EqualTo(1));
        Assert.That(host.Firmware.IsEmergencyStopped, Is.False, "The marker of the first connection stopped the machine");
        host.WriteMacro("config.g", "G4 P9\n");

        host.Firmware.SimulateReset();
        host.Firmware.RequestMacro(CodeChannel.Trigger, "config.g");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Trigger).Any(code => code.Text == "G4 P9"), Timeout), Is.True,
            "config.g did not run");
        Assert.That(host.Firmware.IsEmergencyStopped, Is.False, "The marker after the restart stopped the machine");
        Assert.That(Markers(host), Is.EqualTo(2));
        FakeSbcRequest marker = host.Firmware.GetReceivedRequests().Last(request => (request.Tag & FakeTag.FirstMarker) != 0);
        FakePacket request = host.Firmware.GetSentPackets().Single(packet => packet.Request == FirmwareRequest.ExecuteMacro && packet.Text == "config.g");
        Assert.That((request.Channel, request.IsFromCode, request.Tag), Is.EqualTo((CodeChannel.Trigger, false, FakeTag.Of(0))));
        Assert.That(request.Transfer, Is.GreaterThan(marker.Transfer), "RRF asked for config.g before it had read DSF's first packet");
        Assert.That(await host.ExecuteAsync("M115"), Is.EqualTo("FIRMWARE_NAME: fake\n"));
        Assert.That(Status(host), Is.Not.EqualTo(MachineStatus.Halted));
    }

    [TestCase(FakeRrfModel.V7)]
    [TestCase(FakeRrfModel.RecordApply)]
    public async Task DsfWorksWithAnRrfThatDoesNotTag(FakeRrfModel model)
    {
        // CE-18, AC-7: towards an RRF without the epoch DSF tags anyway, the RRF ignores the field, writes no tag, and DSF runs its v7 path with the
        // catch code. The stress run of these models covers the aborts, this checks that nothing of the epoch gets in the way
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware => firmware.Model = model);
        host.WriteMacro("plain.g", "G4 P1\nset global.plain = 1\n");
        await host.ExecuteAsync("global plain = 0");
        await host.ExecuteAsync("M98 P\"plain.g\"");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        Assert.That(await host.ExecuteAsync("echo global.plain + 5"), Is.EqualTo("6\n"));
        Assert.That(host.Firmware.GetSentPackets().Any(packet => packet.IsTagged), Is.False);
        Assert.That(Epoch(host, CodeChannel.HTTP), Is.EqualTo(1));
    }
}
