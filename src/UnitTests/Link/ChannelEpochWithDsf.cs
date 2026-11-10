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
    public async Task FirstTaggedPacketOfAConnectionCarriesTheMarker()
    {
        // CE-4: only the first tagged packet DSF writes after it started or ended a connection carries the first marker. A restart of RRF that DSF
        // notices ends the connection, resets the epochs and arms the marker again
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        await host.ExecuteAsync("G4 P1");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        await host.ExecuteAsync("G4 P2");
        Assert.That(host.Firmware.GetReceivedRequests().Count(request => (request.Tag & FakeTag.FirstMarker) != 0), Is.EqualTo(1));
        Assert.That(Epoch(host, CodeChannel.HTTP), Is.EqualTo(1));

        host.Firmware.SimulateReset();
        Assert.That(host.Firmware.WaitUntil(firmware => !firmware.IsResetPending, Timeout), Is.True, "DSF did not see the reset");
        await host.ExecuteAsync("G4 P3", CodeChannel.Telnet);
        Assert.That(Epoch(host, CodeChannel.HTTP), Is.Zero, "The epoch was not reset with the connection");
        FakeSbcRequest[] markers = [.. host.Firmware.GetReceivedRequests().Where(request => (request.Tag & FakeTag.FirstMarker) != 0)];
        Assert.That(markers, Has.Length.EqualTo(2));
        Assert.That(FakeTag.EpochOf(markers[1].Tag), Is.Zero);
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
