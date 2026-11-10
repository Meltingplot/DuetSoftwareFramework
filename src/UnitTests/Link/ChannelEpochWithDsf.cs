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
    public async Task ReplyOfACodeThatRanIntoTheAbortIsDroppedByItsEpoch()
    {
        // DF-T10 (CE-11, CE-14): the code RRF still runs at the abort replies with its own epoch, which DSF drops, so the next code gets its own reply.
        // RRF #46 is switched off as in E3, so the reply does go out; with it, the reply is suppressed in RRF
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
        using (Dsf(host, CodeChannel.HTTP).Lock())
        {
            Assert.That(Dsf(host, CodeChannel.HTTP).OutOfOrderReplies, Is.Zero);
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
        // another code. With RRF #46 (codeReplySurplus) the late reply of G4 P1 is suppressed in RRF, without it DSF drops it by its epoch
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
        Assert.That(host.Firmware.MovementLockOwner, Is.Null);
    }

    [Test]
    public async Task HomingCodeThatSurvivesAnAbortEndsWithAnError()
    {
        // Decision of 2026-11-10 on LA-25 (C1): the routine of the abort clears toBeHomed as AbortPrint does, so a G28 whose homing file the abort popped
        // ends with "Failed to home axes", a reply of the old epoch. The next code gets its own reply
        bool release = false;
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware =>
        {
            firmware.CodeReplySurplus = false;
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
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedCodes(CodeChannel.HTTP).Any(code => code.Text == "M115"), Timeout), Is.True);
        release = true;

        Assert.That(await next.WaitAsync(Timeout), Does.Not.Contain("home"));
        FakePacket error = host.Firmware.GetSentPackets().Single(packet => packet.IsCodeReply && packet.Text.StartsWith("Failed to home axes"));
        Assert.That(error.Epoch, Is.Zero);
        Assert.That(host.Firmware.GetSentPackets().Count(packet => packet.Request == FirmwareRequest.ExecuteMacro), Is.EqualTo(1), "G28 asked for homey.g");
        Assert.That(OutOfOrderReplies(host, CodeChannel.HTTP), Is.Zero);
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
