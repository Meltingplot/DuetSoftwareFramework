using System;
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
/// DSF against the RRF of milestone E1 (design/sbc-link-v8-requirements.md, draft 8): RRF records InvalidateChannel and never refuses it, applies it
/// with the routine of a link loss and writes no tag (<see cref="FakeRrfModel.RecordApply"/>), so DSF runs its v7 path. Each test runs against the
/// model of today's RRF as well where the result differs, to show the old behaviour first (AC-18)
/// </summary>
[TestFixture]
public class RecordApplyWithDsf
{
    private static readonly TimeSpan Timeout = FakeFirmwareHost.DefaultTimeout;

    private static void Step(FakeFirmwareHost host)
    {
        int transfer = host.Firmware.TransferCount;
        host.Firmware.HoldTransfers(1);
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.TransferCount > transfer && firmware.IsTransferHeld, Timeout), Is.True, "No transfer");
    }

    private static void HoldTransfers(FakeFirmwareHost host)
    {
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True, "The transfers were not held");
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
    public async Task NextCodeGetsItsOwnReplyWhenTheMutexIsBusyAtTheAbort()
    {
        // As TriggerFuzz.NextCodeGetsItsOwnReplyWhenRrfAsksForTheAbortAgain, but RRF records the abort without the buffer mutex (CE-7), so it never
        // asks for the InvalidateChannel again and DSF's catch code #161 has nothing to wait for
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        host.WriteMacro("http-macro.g", "G4 P90001\n");
        host.Firmware.OnCode("G4", code => (code.Text == "G4 P2") ? FakeReply.Ok("reply of G4 P2") : null);
        HoldTransfers(host);
        Task<string> m98 = host.StartCode("M98 P\"http-macro.g\"");
        for (int i = 0; i < 100 && !host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M98 P\"http-macro.g\""); i++)
        {
            Step(host);
        }
        Step(host);
        host.Firmware.FailBufferLocks(CodeChannel.HTTP, 5);
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        Task<string> next = host.StartCode("G4 P2");
        host.Firmware.ReleaseTransfers();
        await IgnoreCancellation(m98);

        Assert.That(await next.WaitAsync(Timeout), Is.EqualTo("reply of G4 P2\n"));
        Assert.That(host.Firmware.GetSentPackets().Where(packet => packet.Request == FirmwareRequest.ResendPacket && packet.ResendRequest?.Request == SbcRequest.InvalidateChannel),
            Is.Empty, "RRF asked for the InvalidateChannel again");
        Assert.That(host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text), Has.None.EqualTo("G4 P90001"));
    }

    /// <summary>
    /// M-code handler of DCS that holds M111 coming from the firmware until released, so that DSF still runs a code RRF passed on when its channel
    /// is aborted
    /// </summary>
    private sealed class HoldingMCodeHandler(ICodeHandler handler, Task release) : ICodeHandler
    {
        public async ValueTask<Message?> ProcessAsync(Code code, CancellationToken cancellationToken)
        {
            if (code.Flags.HasFlag(CodeFlags.IsFromFirmware) && code.Type == CodeType.MCode && code.MajorNumber == 111)
            {
                await release.WaitAsync(cancellationToken);
            }
            return await handler.ProcessAsync(code, cancellationToken);
        }

        public ValueTask CodeExecutedAsync(Code code, CancellationToken cancellationToken) => handler.CodeExecutedAsync(code, cancellationToken);
    }

    [TestCase(FakeRrfModel.V7, false)]
    [TestCase(FakeRrfModel.RecordApply, true)]
    public async Task ChannelIsFreeAfterAnAbortWhileDsfRunsACodePassedOn(FakeRrfModel model, bool free)
    {
        // AC-18, CE-25 (b), K13: today InvalidateChannel leaves a code that RRF passed on to DSF alone, so the buffer of its channel stays executing on
        // the SBC until a result comes, and none comes when DSF cancels the code (Executed.cs:224-228). The routine of the apply ends it. DSF's abort of
        // the channel does not cancel the code here (it runs in DSF's own pipeline, not in the buffers of the channel), so the test holds it in DSF
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware => firmware.Model = model, configureServices: services =>
            services.AddKeyedSingleton<ICodeHandler>(Keys.MCodes, (provider, _) => new HoldingMCodeHandler(ActivatorUtilities.CreateInstance<MCodeHandler>(provider), release.Task)));
        host.Firmware.PassCodeToSbc(CodeChannel.Aux, "M111 P-1");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.Request == FirmwareRequest.DoCode), Timeout), Is.True);
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.Aux);
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.InvalidateChannel), Timeout), Is.True);

        bool idle = host.Firmware.WaitUntil(firmware => firmware.IsIdle(CodeChannel.Aux), TimeSpan.FromSeconds(free ? 10 : 1));
        Assert.That(idle, Is.EqualTo(free));

        // The late result is printed to its target and finishes nothing from E1 on (SbcInterface.cpp:774-803). DSF runs the next code of the channel
        // only after the held one
        release.SetResult();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedMessages().Count == 1, Timeout), Is.True, "DSF did not send the late result");
        Assert.That(host.Firmware.GetFinishedSbcCodes(), free ? Is.Empty : Has.Count.EqualTo(1));
        Assert.That(await host.ExecuteAsync("G4 P5", CodeChannel.Aux), Is.EqualTo("\n"));
    }

    [TestCase(FakeRrfModel.V7, "Warning: G4: Awaiting input, command ignored\n")]
    [TestCase(FakeRrfModel.RecordApply, "\n")]
    public async Task NextCodeRunsAfterTheAbortOfABoxAtTheBaseLevel(FakeRrfModel model, string reply)
    {
        // AC-18, CE-25 (c), OF-T10: today a box at the base level survives InvalidateChannel, so RRF ignores the next code. The routine closes it as
        // cancelled. The final reply of M291 then comes right after the abort and, untagged, reaches a DSF without the epoch as the reply of its next
        // code (requirements section 4.5, second table, Q37): here the empty reply that G4 P5 gets, its own would be "reply of G4 P5"
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware => firmware.Model = model);
        host.Firmware.OnCode("G4", code => FakeReply.Ok($"reply of {code.Text}"));
        Task<string> box = host.StartCode("M291 P\"box\" S2");
        Processor http = host.Services.GetRequiredService<Manager>()[CodeChannel.HTTP];
        Assert.That(host.Firmware.WaitUntil(_ => http.IsWaitingForAcknowledgment, Timeout), Is.True, "The box was not opened");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        await IgnoreCancellation(box);

        Assert.That(await host.ExecuteAsync("G4 P5"), Is.EqualTo(reply));
        Assert.That(host.Firmware.GetSentPackets().Where(packet => packet.Request == FirmwareRequest.MessageAcknowledged), Is.Empty);
    }

    [TestCase(FakeRrfModel.V7, false)]
    [TestCase(FakeRrfModel.RecordApply, true)]
    public async Task DaemonChannelWorksAfterAnAbortCrossingADaemonRequest(FakeRrfModel model, bool works)
    {
        // OF-T11 (A5): RRF requests daemon.g without waiting (GCodeBuffer.cpp:1258-1261). If InvalidateChannel arrives before the request has gone out,
        // today's RRF closes the file but still sends the request. DSF starts daemon.g on a level that RRF no longer has, waits for its closal and holds
        // every later code of the channel behind it, because RRF has no file to close (seen in the fake: ExecuteMacro in the transfer after
        // InvalidateChannel, after the window of #134). From E1 nothing is written between record and apply (CE-12) and the routine voids the request.
        // The main task asks for daemon.g in the transfer before the one that brings InvalidateChannel
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware => firmware.Model = model);
        host.WriteMacro("daemon.g", "G4 P1\n");
        HoldTransfers(host);
        host.Firmware.RequestMacro(CodeChannel.Daemon, "daemon.g");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.Daemon);
        host.Firmware.ReleaseTransfers();

        Task<string> probe = host.StartCode("G4 P2", CodeChannel.Daemon);
        bool done = await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(works ? 10 : 2))) == probe;
        Assert.That(done, Is.EqualTo(works));
    }
}
