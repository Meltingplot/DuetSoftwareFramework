using System.Collections.Generic;
using System.IO;
using System.Linq;
using DuetAPI;
using DuetControlServer.Codes;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Code = DuetControlServer.Commands.Code;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Rules of the RRF side of the channel epoch (milestone E2, design/sbc-link-v8-requirements.md draft 8) as the fake firmware models them, driven
/// directly like LinkService does, with tags written by the test: the adopt rule and the first marker (CE-4), the tag sources (CE-9, CE-11, CE-12),
/// the filter of stale DSF packets (CE-10), the receipt of InvalidateChannel by its tag (CE-7) and the abort gate (CE-24)
/// </summary>
[TestFixture]
public class FakeFirmwareEpoch
{
    private ServiceProvider _provider = null!;
    private FakeFirmware _firmware = null!;
    private readonly List<FakePacket> _delivered = [];

    [SetUp]
    public void SetUp()
    {
        _provider = DcsServices.Build();
        _firmware = new FakeFirmware { Model = FakeRrfModel.Epoch };
        _firmware.Connect();
        _delivered.Clear();
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    #region Helpers
    private static ushort T(int epoch) => FakeTag.Of(epoch);

    private static ushort Marker(int epoch) => (ushort)(FakeTag.Of(epoch) | FakeTag.FirstMarker);

    private Code MakeCode(CodeChannel channel, string text)
    {
        Code code = _provider.GetRequiredService<CodeFactory>().Create();
        code.Channel = channel;
        using StringReader reader = new(text);
        DuetAPI.Commands.Code.Parse(reader, code);
        return code;
    }

    private List<FakePacket> Transfer()
    {
        _firmware.PerformFullTransfer();
        List<FakePacket> packets = [.. _firmware.GetSentPackets().Where(packet => packet.Transfer == _firmware.TransferCount && packet.Request != Request.CodeBufferUpdate)];
        for (int i = 0; i < _firmware.PacketsToRead; i++)
        {
            PacketHeader header = _firmware.ReadNextPacket()!.Value;
            if ((Request)header.Request == Request.ResendPacket)
            {
                _firmware.ResendPacket(header, out _, out _);
            }
        }
        _delivered.AddRange(packets);
        return packets;
    }

    private void TransferUntil(System.Func<bool> condition, string description, int maxTransfers = 10)
    {
        for (int i = 0; i < maxTransfers && !condition(); i++)
        {
            Transfer();
        }
        Assert.That(condition(), Is.True, description);
    }

    private void Transfers(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Transfer();
        }
    }

    private FakePacket[] Delivered(Request request, CodeChannel channel) => [.. _delivered.Where(packet => packet.Request == request && packet.Channel == channel)];

    private FakePacket[] Replies(string text) => [.. _delivered.Where(packet => packet.IsCodeReply && packet.Text == text)];

    private static int Sum(Dictionary<(CodeChannel Channel, SbcRequest Request), int> counter, CodeChannel channel, SbcRequest request) =>
        counter.GetValueOrDefault((channel, request));

    /// <summary>
    /// Let the channel know an epoch, from an InvalidateChannel while the epoch is unknown
    /// </summary>
    private void KnowEpoch(CodeChannel channel, int epoch)
    {
        Assert.That(_firmware.WriteInvalidateChannel(channel, T(epoch)), Is.True);
        Transfer();
    }
    #endregion

    [Test]
    public void AdoptsTheEpochOfTheFirstTaggedPacketOfAChannel()
    {
        // CE-4: while the epoch of a channel is unknown, the first tagged DSF packet of the channel sets it, whatever its type. Then RRF tags
        _firmware.OnCode("G4", code => FakeReply.Ok($"reply of {code.Text}"));
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P1"), T(5)), Is.True);
        TransferUntil(() => Replies("reply of G4 P1").Length == 1, "G4 P1 did not reply");
        Assert.That(Replies("reply of G4 P1")[0].Tag, Is.EqualTo(T(5)), "The reply does not carry the epoch of its code (CE-11)");
        Assert.That((uint)Replies("reply of G4 P1")[0].MessageType & FakeTag.TypeEpochBits, Is.Zero, "The epoch bits of MessageType went out (CE-11)");
    }

    [Test]
    public void WritesNoTagWhileTheEpochIsUnknown()
    {
        // CE-4: towards an SBC whose first packet carries no marker (a stock DSF, or a DSF that did not notice the restart of RRF), a request before any
        // DSF packet of the channel (config.g at boot, RepRap.cpp:627-637) is untagged, not tagged with a start value
        _firmware.RequestMacro(CodeChannel.Trigger, "config.g");
        Assert.That(_firmware.WriteGetObjectModel("state", "v", 0), Is.True);
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.Trigger).Length == 1, "config.g was not requested");
        Assert.That(Delivered(Request.ExecuteMacro, CodeChannel.Trigger)[0].IsTagged, Is.False);
    }

    [Test]
    public void HoldsChannelPacketsUntilTheFirstSbcPacket()
    {
        // Decision of 2026-11-10: until RRF has read DSF's first packet of the connection, it writes nothing that belongs to a channel. config.g, which
        // RRF requests as soon as the SBC is connected (RepRap.cpp:627-637), waits for it and then carries epoch 0 of DSF's first marker
        _firmware.RequestMacro(CodeChannel.Trigger, "config.g");
        Transfers(3);
        Assert.That(Delivered(Request.ExecuteMacro, CodeChannel.Trigger), Is.Empty, "config.g was requested before DSF's first packet");
        Assert.That(_firmware.IsIdle(CodeChannel.Trigger), Is.False, "The request was dropped");

        Assert.That(_firmware.WriteGetObjectModel("state", "v", FakeTag.FirstMarker), Is.True);
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.Trigger).Length == 1, "config.g was not requested after DSF's first packet");
        Assert.That(Delivered(Request.ExecuteMacro, CodeChannel.Trigger)[0].Tag, Is.EqualTo(T(0)));
    }

    [Test]
    public void HoldsChannelPacketsAgainAfterALostConnection()
    {
        // The flag ends with the connection (InvalidateResources). A system macro that RRF asks for after the loss is requested once DSF's first packet
        // of the next connection has been read, with the epoch of its marker. Packets without a channel, such as the buffer update, are not held
        Assert.That(_firmware.WriteGetObjectModel("state", "v", FakeTag.FirstMarker), Is.True);
        Transfer();
        _firmware.SimulateConnectionLoss();
        int lossTransfer = _firmware.TransferCount;
        _firmware.RequestMacro(CodeChannel.Trigger, "trigger3.g", true);
        Transfers(3);
        Assert.That(Delivered(Request.ExecuteMacro, CodeChannel.Trigger), Is.Empty, "trigger3.g was requested before DSF's first packet");
        Assert.That(_firmware.GetSentPackets().Any(packet => packet.Request == Request.CodeBufferUpdate && packet.Transfer > lossTransfer), Is.True,
            "The buffer update was held");

        Assert.That(_firmware.WriteGetObjectModel("state", "v", FakeTag.FirstMarker), Is.True);
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.Trigger).Length == 1, "trigger3.g was not requested after DSF's first packet");
        Assert.That(Delivered(Request.ExecuteMacro, CodeChannel.Trigger)[0].Tag, Is.EqualTo(T(0)));
    }

    [Test]
    public void FirstMarkerMakesEveryEpochZero()
    {
        // CE-4 with the decision of this implementation: DSF writes the first marker where all its epochs are 0, so RRF takes 0 for every channel instead
        // of forgetting them. An old epoch of a DSF that restarted unnoticed is gone, and the trigger channel, on which DSF sent nothing yet, tags with 0
        _firmware.OnCode("G4", code => FakeReply.Ok($"reply of {code.Text}"));
        KnowEpoch(CodeChannel.HTTP, 5);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.Telnet, "G4 P1"), Marker(0)), Is.True);
        Transfer();
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P2"), T(5)), Is.True);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P3"), T(0)), Is.True);
        Transfers(3);
        Assert.That(_firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text), Is.EqualTo(new[] { "G4 P3" }));
        Assert.That(Sum(_firmware.EpochCounters.AheadDrops, CodeChannel.HTTP, SbcRequest.Code) + Sum(_firmware.EpochCounters.StaleDrops, CodeChannel.HTTP, SbcRequest.Code), Is.EqualTo(1));

        _firmware.FireTrigger(5);
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.Trigger).Length == 1, "trigger5.g was not requested");
        Assert.That(Delivered(Request.ExecuteMacro, CodeChannel.Trigger)[0].Tag, Is.EqualTo(T(0)));
    }

    [Test]
    public void DropsStaleAndAheadCodes()
    {
        // CE-10: a Code of another epoch, older or ahead, is dropped before it is stored and counted by its class (CE-5, CE-20)
        KnowEpoch(CodeChannel.HTTP, 3);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P2"), T(2)), Is.True);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P4"), T(4)), Is.True);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P3"), T(3)), Is.True);
        Transfers(3);
        Assert.That(_firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text), Is.EqualTo(new[] { "G4 P3" }));
        Assert.That(Sum(_firmware.EpochCounters.StaleDrops, CodeChannel.HTTP, SbcRequest.Code), Is.EqualTo(1));
        Assert.That(Sum(_firmware.EpochCounters.AheadDrops, CodeChannel.HTTP, SbcRequest.Code), Is.EqualTo(1));
        Assert.That(_delivered.Where(packet => packet.Request == Request.ResendPacket), Is.Empty, "A dropped packet was asked for again");
    }

    [Test]
    public void StaleMacroStartedDoesNotEndAMacroWait()
    {
        // CE-10: MacroStarted resolves a wait (SbcInterface.cpp:839-842), so a stale one must be dropped before that side effect
        KnowEpoch(CodeChannel.HTTP, 1);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "M98 P\"wait.g\""), T(1)), Is.True);
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.HTTP).Length == 1, "M98 did not request its macro");
        Assert.That(_firmware.WriteMacroStarted(CodeChannel.HTTP, T(0)), Is.True);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P1"), T(0)), Is.True);
        Transfers(2);
        Assert.That(_firmware.IsIdle(CodeChannel.HTTP), Is.False);
        Assert.That(_firmware.GetCurrentCode(CodeChannel.HTTP)?.Text, Is.EqualTo("M98 P\"wait.g\""), "The wait of M98 ended");
    }

    [Test]
    public void StaleRequestsGetNoAnswer()
    {
        // CE-10: Lock, EvaluateExpression and SetVariable of an older epoch act on nothing and get no answer; DSF has failed them (CE-25)
        KnowEpoch(CodeChannel.HTTP, 2);
        Assert.That(_firmware.WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel.HTTP, T(1)), Is.True);
        Assert.That(_firmware.WriteEvaluateExpression(CodeChannel.HTTP, "1+1", T(1)), Is.True);
        Assert.That(_firmware.WriteSetVariable(CodeChannel.HTTP, true, "global.stale", "1", T(1)), Is.True);
        Transfers(2);
        Assert.That(_delivered.Where(packet => packet.Request is Request.Locked or Request.EvaluationResult or Request.VariableResult), Is.Empty);
        Assert.That(_firmware.MovementLockOwner, Is.Null);
        Assert.That(_firmware.ObjectModel.TryGetGlobal("stale", out _), Is.False);
    }

    [Test]
    public void AnswersCarryTheTagOfTheirRequest()
    {
        // CE-6: Locked, EvaluationResult and VariableResult carry the tag of the request they answer, without the first marker
        Assert.That(_firmware.WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel.HTTP, Marker(0)), Is.True);
        Assert.That(_firmware.WriteEvaluateExpression(CodeChannel.HTTP, "1+1", T(0)), Is.True);
        Assert.That(_firmware.WriteSetVariable(CodeChannel.HTTP, true, "global.fresh", "1", T(0)), Is.True);
        Transfers(2);
        FakePacket[] answers = [.. _delivered.Where(packet => packet.Request is Request.Locked or Request.EvaluationResult or Request.VariableResult)];
        Assert.That(answers, Has.Length.EqualTo(3));
        Assert.That(answers.Select(packet => packet.Tag), Is.All.EqualTo(T(0)));
    }

    [Test]
    public void RepeatedInvalidateChannelIsIgnored()
    {
        // AC-15 (CE-7, FD-28): a transfer that RRF processes twice delivers the InvalidateChannel(e+1) twice. The second record is counted and ignored:
        // the code stored after the first stays and a macro wait begun after it is not ended
        KnowEpoch(CodeChannel.HTTP, 0);
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, T(1)), Is.True);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "M98 P\"wait.g\""), T(1)), Is.True);
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.HTTP).Length == 1, "M98 did not request its macro");
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, T(1)), Is.True);
        Transfers(2);
        Assert.That(Sum(_firmware.EpochCounters.Repeats, CodeChannel.HTTP, SbcRequest.InvalidateChannel), Is.EqualTo(1));
        Assert.That(_firmware.GetCurrentCode(CodeChannel.HTTP)?.Text, Is.EqualTo("M98 P\"wait.g\""), "The repeat ended the macro wait");
        Assert.That(_firmware.IsAbortRecorded(CodeChannel.HTTP), Is.False);
    }

    [Test]
    public void ReplyOfACodeThatSurvivedTheAbortKeepsItsEpoch()
    {
        // CE-9, CE-11: a code that still runs at the base level survives the abort; its reply carries its own epoch, so that DSF drops it. RRF #46 is
        // switched off here, as in E3, else the reply would not go out at all
        _firmware.CodeReplySurplus = false;
        bool release = false;
        _firmware.OnCode("G4", code => (code.Text == "G4 P5" && !release) ? FakeReply.NotFinished : FakeReply.Ok($"reply of {code.Text}"));
        KnowEpoch(CodeChannel.HTTP, 1);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P5"), T(1)), Is.True);
        TransferUntil(() => _firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P5", "G4 P5 did not start");
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, T(2)), Is.True);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P6"), T(2)), Is.True);
        Transfer();
        release = true;
        TransferUntil(() => Replies("reply of G4 P6").Length == 1, "G4 P6 did not reply");
        Assert.That(Replies("reply of G4 P5").Single().Tag, Is.EqualTo(T(1)));
        Assert.That(Replies("reply of G4 P6").Single().Tag, Is.EqualTo(T(2)));
    }

    [Test]
    public void MacroRequestOfACodeOfAnOlderEpochIsASystemMacroRequest()
    {
        // Decision of 2026-11-10, replaces CE-12 (b): a code that survived the abort at the base level asks for a macro. The request goes out as one
        // without a code (fromCode false) with the channel epoch, so that DSF starts it as a system macro; the final reply of the code carries its own
        // old epoch
        _firmware.CodeReplySurplus = false;
        bool release = false;
        _firmware.OnCode("G28", _ => release ? FakeReply.CallMacro("homey.g") : FakeReply.NotFinished);
        KnowEpoch(CodeChannel.HTTP, 1);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G28"), T(1)), Is.True);
        TransferUntil(() => _firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G28", "G28 did not start");
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, T(2)), Is.True);
        Transfer();
        release = true;
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.HTTP).Length == 1, "G28 did not request its macro");
        FakePacket request = Delivered(Request.ExecuteMacro, CodeChannel.HTTP)[0];
        Assert.That(request.IsFromCode, Is.False, "The request of the old code was written as one of a code");
        Assert.That(request.Tag, Is.EqualTo(T(2)));

        // DSF finds the macro empty; the code ends with its own epoch
        Assert.That(_firmware.WriteMacroCompleted(CodeChannel.HTTP, false, T(2)), Is.True);
        TransferUntil(() => _delivered.Any(packet => packet.IsCodeReply), "G28 did not end");
        Assert.That(_firmware.IsIdle(CodeChannel.HTTP), Is.True);
        Transfer();
        FakePacket[] replies = [.. _delivered.Where(packet => packet.IsCodeReply)];
        Assert.That(replies, Has.Length.EqualTo(1));
        Assert.That(replies[0].Tag, Is.EqualTo(T(1)));
    }

    [Test]
    public void ToolChangeGoesOnAfterAnAbortWithSystemMacros()
    {
        // Decision of 2026-11-10 on LA-25 (C1-C3): the routine of the abort cleans up the state machine as AbortPrint does (doingToolChange false) but
        // leaves the state of the base level, so a T-code whose tfree the abort popped goes on as in standalone RRF and asks for tpre and tpost. Both
        // go out as system macro requests with the new epoch. The codes of tpre carry the new epoch and do not overwrite the stamp of the T-code on
        // the level below, so the tpost request after tpre is still a system macro request and the final reply of the T-code carries its old epoch
        _firmware.CodeReplySurplus = false;
        _firmware.OnCode("T1", _ => FakeReply.ToolChange("tfree0.g", "tpre1.g", "tpost1.g"));
        _firmware.OnCode("G4", code => FakeReply.Ok($"reply of {code.Text}"));
        KnowEpoch(CodeChannel.HTTP, 1);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "T1"), T(1)), Is.True);
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.HTTP).Length == 1, "T1 did not request tfree0.g");
        FakePacket tfree = Delivered(Request.ExecuteMacro, CodeChannel.HTTP)[0];
        Assert.That((tfree.Text, tfree.IsFromCode, tfree.Tag), Is.EqualTo(("tfree0.g", true, T(1))));
        Assert.That(_firmware.WriteMacroStarted(CodeChannel.HTTP, T(1)), Is.True);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P1"), T(1)), Is.True);
        TransferUntil(() => Replies("reply of G4 P1").Length == 1, "tfree0.g did not run");
        Assert.That(_firmware.IsDoingToolChange, Is.True);

        // Abort inside tfree0.g
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, T(2)), Is.True);
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.HTTP).Length == 2, "T1 did not go on with tpre1.g");
        Assert.That(_firmware.IsDoingToolChange, Is.False, "The routine did not clean up the state machine (AbortStateMachine)");
        FakePacket tpre = Delivered(Request.ExecuteMacro, CodeChannel.HTTP)[1];
        Assert.That((tpre.Text, tpre.IsFromCode, tpre.Tag), Is.EqualTo(("tpre1.g", false, T(2))));

        // tpre1.g runs a code of the new epoch
        Assert.That(_firmware.WriteMacroStarted(CodeChannel.HTTP, T(2)), Is.True);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P2"), T(2)), Is.True);
        TransferUntil(() => Replies("reply of G4 P2").Length == 1, "tpre1.g did not run");
        Assert.That(Replies("reply of G4 P2")[0].Tag, Is.EqualTo(T(2)));
        Assert.That(_firmware.WriteMacroCompleted(CodeChannel.HTTP, false, T(2)), Is.True);
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.HTTP).Length == 3, "T1 did not go on with tpost1.g");
        FakePacket tpost = Delivered(Request.ExecuteMacro, CodeChannel.HTTP)[2];
        Assert.That((tpost.Text, tpost.IsFromCode, tpost.Tag), Is.EqualTo(("tpost1.g", false, T(2))), "The codes of tpre1.g overwrote the stamp of T1");

        // tpost1.g is empty, then T1 ends with a reply of its own epoch and releases the movement lock
        int replies = _delivered.Count(packet => packet.IsCodeReply);
        Assert.That(_firmware.WriteMacroCompleted(CodeChannel.HTTP, false, T(2)), Is.True);
        TransferUntil(() => _delivered.Count(packet => packet.IsCodeReply) > replies, "T1 did not end");
        Assert.That(_firmware.IsIdle(CodeChannel.HTTP), Is.True);
        FakePacket final = _delivered.Last(packet => packet.IsCodeReply);
        Assert.That((final.Text, final.Tag), Is.EqualTo((string.Empty, T(1))));
        Assert.That(_firmware.MovementLockOwner, Is.Null);
    }

    [Test]
    public void HomingCodeEndsWithAnErrorAfterAnAbort()
    {
        // Decision of 2026-11-10 on LA-25 (C1): AbortStateMachine clears toBeHomed, the G28 at the base level goes on in homing2 and ends with "Failed
        // to home axes", as G28 does after AbortPrint in standalone RRF. The reply carries the old epoch, DSF shows it as a message. The homing file
        // ran a code before the abort; without one the reply would be a push message, because AbortFile leaves macroJustStarted set
        _firmware.CodeReplySurplus = false;
        _firmware.OnCode("G28", _ => FakeReply.Home('X', 'Y'));
        KnowEpoch(CodeChannel.HTTP, 1);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G28"), T(1)), Is.True);
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.HTTP).Length == 1, "G28 did not request homex.g");
        FakePacket homex = Delivered(Request.ExecuteMacro, CodeChannel.HTTP)[0];
        Assert.That((homex.Text, homex.IsFromCode, homex.Tag), Is.EqualTo(("homex.g", true, T(1))));
        Assert.That(_firmware.WriteMacroStarted(CodeChannel.HTTP, T(1)), Is.True);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G91"), T(1)), Is.True);
        Transfers(2);

        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, T(2)), Is.True);
        TransferUntil(() => _delivered.Any(packet => packet.IsCodeReply && packet.Text.StartsWith("Failed to home axes")), "G28 did not end with an error");
        Assert.That(_firmware.IsIdle(CodeChannel.HTTP), Is.True);
        FakePacket reply = _delivered.Single(packet => packet.IsCodeReply && packet.Text.StartsWith("Failed to home axes"));
        Assert.That(reply.Tag, Is.EqualTo(T(1)));
        Assert.That(reply.MessageType.HasFlag(MessageTypeFlags.ErrorMessageFlag) && !reply.MessageType.HasFlag(MessageTypeFlags.PushFlag), Is.True);
        Assert.That(Delivered(Request.ExecuteMacro, CodeChannel.HTTP), Has.Length.EqualTo(1), "G28 asked for another homing file");
        Assert.That(_firmware.MovementLockOwner, Is.Null);
    }

    [Test]
    public void HomingCodeHomesItsAxesWithoutAnAbort()
    {
        // The model of G28 without an abort: home<axis>.g of each axis in turn, the G1 H1 in it homes the axis, the code ends with an empty reply
        _firmware.OnCode("G28", _ => FakeReply.Home('X', 'Y'));
        KnowEpoch(CodeChannel.HTTP, 1);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G28"), T(1)), Is.True);
        foreach ((string file, string move, int count) in new[] { ("homex.g", "G1 H1 X-300", 1), ("homey.g", "G1 H1 Y-300", 2) })
        {
            TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.HTTP).Length == count, $"G28 did not request {file}");
            Assert.That(Delivered(Request.ExecuteMacro, CodeChannel.HTTP)[count - 1].Text, Is.EqualTo(file));
            Assert.That(_firmware.WriteMacroStarted(CodeChannel.HTTP, T(1)), Is.True);
            Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, move), T(1)), Is.True);
            Transfers(2);
            Assert.That(_firmware.WriteMacroCompleted(CodeChannel.HTTP, false, T(1)), Is.True);
        }
        TransferUntil(() => _firmware.IsIdle(CodeChannel.HTTP), "G28 did not end");
        Transfers(2);
        Assert.That(_delivered.Where(packet => packet.IsCodeReply && packet.Text.Length > 0), Is.Empty, "G28 failed");
        Assert.That(_delivered.Count(packet => packet.IsCodeReply), Is.EqualTo(3), "G28 did not reply");
        Assert.That(_firmware.AxesHomed, Is.EquivalentTo(new[] { 'X', 'Y' }));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void SurvivingCodeAfterALostConnection(bool marker)
    {
        // Decision of 2026-11-10 (C2): a T-code that survives a lost connection (InvalidateResources, and the emergency stop of RRF) has an invalid stamp.
        // Its next request waits for DSF's first packet of the next connection. With the marker the epoch is 0 and known, and the request goes out as a
        // system macro request with epoch 0. Without it (a stock DSF) the epoch stays unknown and the request goes out as in v7
        _firmware.OnCode("T1", _ => FakeReply.ToolChange("tfree0.g", "tpre1.g", "tpost1.g"));
        KnowEpoch(CodeChannel.HTTP, 1);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "T1"), T(1)), Is.True);
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.HTTP).Length == 1, "T1 did not request tfree0.g");
        Assert.That(_firmware.WriteMacroStarted(CodeChannel.HTTP, T(1)), Is.True);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G91"), T(1)), Is.True);
        Transfers(2);

        _firmware.SimulateConnectionLoss();
        Transfers(3);
        Assert.That(Delivered(Request.ExecuteMacro, CodeChannel.HTTP), Has.Length.EqualTo(1), "tpre1.g was requested before DSF's first packet");

        Assert.That(_firmware.WriteGetObjectModel("state", "v", marker ? FakeTag.FirstMarker : (ushort)0), Is.True);
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.HTTP).Length == 2, "T1 did not go on with tpre1.g");
        FakePacket tpre = Delivered(Request.ExecuteMacro, CodeChannel.HTTP)[1];
        Assert.That((tpre.Text, tpre.IsFromCode, tpre.Tag), Is.EqualTo(marker ? ("tpre1.g", false, T(0)) : ("tpre1.g", true, (ushort)0)));
    }

    [Test]
    public void StackEventsCarryTheChannelEpochAtTheirWrite()
    {
        // CE-9 source (2): WaitForAcknowledgement of a box and the request of a trigger macro carry the channel epoch when the SBC task writes them
        KnowEpoch(CodeChannel.HTTP, 3);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "M291 P\"box\" S2"), T(3)), Is.True);
        TransferUntil(() => Delivered(Request.WaitForAcknowledgement, CodeChannel.HTTP).Length == 1, "The box was not opened");
        Assert.That(Delivered(Request.WaitForAcknowledgement, CodeChannel.HTTP)[0].Tag, Is.EqualTo(T(3)));

        KnowEpoch(CodeChannel.Trigger, 7);
        _firmware.FireTrigger(4);
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.Trigger).Length == 1, "trigger4.g was not requested");
        Assert.That(Delivered(Request.ExecuteMacro, CodeChannel.Trigger)[0].Tag, Is.EqualTo(T(7)));
    }

    [Test]
    public void GateDropsDsfPacketsAfterATaggedAbortOfAllFiles()
    {
        // CE-24: RRF's own abort of all files closes the gate of the channel; DSF packets other than InvalidateChannel are dropped until DSF answers
        // with InvalidateChannel(e+1), which opens it again
        bool release = false;
        _firmware.OnCode("G4", code => (code.Text == "G4 P1" && !release) ? FakeReply.NotFinished : null);
        KnowEpoch(CodeChannel.HTTP, 1);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "M98 P\"macro.g\""), T(1)), Is.True);
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.HTTP).Length == 1, "M98 did not request its macro");
        Assert.That(_firmware.WriteMacroStarted(CodeChannel.HTTP, T(1)), Is.True);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P1"), T(1)), Is.True);
        TransferUntil(() => _firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P1", "The macro did not run");

        _firmware.AbortAllFiles(CodeChannel.HTTP);
        TransferUntil(() => Delivered(Request.AbortFile, CodeChannel.HTTP).Length == 1, "RRF did not abort the files");
        FakePacket abort = Delivered(Request.AbortFile, CodeChannel.HTTP)[0];
        Assert.That(abort.AbortAll && abort.Tag == T(1), Is.True);
        release = true;

        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P2"), T(1)), Is.True);
        Transfer();
        Assert.That(Sum(_firmware.EpochCounters.GateDrops, CodeChannel.HTTP, SbcRequest.Code), Is.EqualTo(1));

        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, T(2)), Is.True);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P3"), T(2)), Is.True);
        TransferUntil(() => _firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "G4 P3"), "The gate stayed closed");
        Assert.That(_firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text), Has.None.EqualTo("G4 P2"));
    }

    [Test]
    public void NoGateForAnUntaggedAbortOfAllFiles()
    {
        // CE-24: a DSF without tags answers no AbortFile(all) (V7-46), so an untagged one sets no gate that would never open again (FD-37, point 10)
        bool release = false;
        _firmware.OnCode("G4", code => (code.Text == "G4 P1" && !release) ? FakeReply.NotFinished : null);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "M98 P\"macro.g\""), 0), Is.True);
        TransferUntil(() => Delivered(Request.ExecuteMacro, CodeChannel.HTTP).Length == 1, "M98 did not request its macro");
        Assert.That(_firmware.WriteMacroStarted(CodeChannel.HTTP, 0), Is.True);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P1"), 0), Is.True);
        TransferUntil(() => _firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P1", "The macro did not run");
        _firmware.AbortAllFiles(CodeChannel.HTTP);
        TransferUntil(() => Delivered(Request.AbortFile, CodeChannel.HTTP).Length == 1, "RRF did not abort the files");
        Assert.That(Delivered(Request.AbortFile, CodeChannel.HTTP)[0].IsTagged, Is.False);
        release = true;
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P2"), 0), Is.True);
        TransferUntil(() => _firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "G4 P2"), "A gate dropped the code");
    }

    [Test]
    public void ResultOfADoCodeOfAnOlderEpochIsDropped()
    {
        // CE-6, CE-10, AC-14: the chunks of a DoCode result carry the tag of the DoCode. After the abort a stale result does not finish the newer DoCode
        KnowEpoch(CodeChannel.Aux, 1);
        _firmware.PassCodeToSbc(CodeChannel.Aux, "M929");
        TransferUntil(() => Delivered(Request.DoCode, CodeChannel.Aux).Length == 1, "M929 was not passed on");
        Assert.That(Delivered(Request.DoCode, CodeChannel.Aux)[0].Tag, Is.EqualTo(T(1)));
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.Aux, T(2)), Is.True);
        Transfer();
        Assert.That(_firmware.IsIdle(CodeChannel.Aux), Is.True, "The apply did not end the DoCode (CE-25 (b))");

        _firmware.PassCodeToSbc(CodeChannel.Aux, "M111 P-1");
        TransferUntil(() => Delivered(Request.DoCode, CodeChannel.Aux).Length == 2, "M111 was not passed on");
        Assert.That(Delivered(Request.DoCode, CodeChannel.Aux)[1].Tag, Is.EqualTo(T(2)));
        Assert.That(_firmware.WriteMessage((MessageTypeFlags)(1 << (int)CodeChannel.Aux), "Event logging is disabled\n", T(1)), Is.True);
        Transfer();
        Assert.That(_firmware.GetFinishedSbcCodes(), Is.Empty, "The stale result finished the newer DoCode");
        Assert.That(Sum(_firmware.EpochCounters.StaleDrops, CodeChannel.Aux, SbcRequest.Message), Is.EqualTo(1));

        Assert.That(_firmware.WriteMessage((MessageTypeFlags)(1 << (int)CodeChannel.Aux), "Current DCS log level: Information\n", T(2)), Is.True);
        Transfer();
        Assert.That(_firmware.GetFinishedSbcCodes().Select(item => item.Code), Is.EqualTo(new[] { "M111 P-1" }));
    }

    [Test]
    public void CopyKeepsTheTagWithoutTheMarker()
    {
        // CE-8: a refused packet is written again with the tag of the original, but not with the first marker, which only the first packet of a
        // connection carries (CE-4)
        _firmware.FailBufferLocks(CodeChannel.HTTP, 1);
        Assert.That(_firmware.WriteEvaluateExpression(CodeChannel.HTTP, "1+1", Marker(0)), Is.True);
        TransferUntil(() => _delivered.Any(packet => packet.Request == Request.EvaluationResult), "The evaluation was not answered");
        FakeSbcRequest[] requests = [.. _firmware.GetReceivedRequests().Where(request => request.Request == SbcRequest.EvaluateExpression)];
        Assert.That(requests, Has.Length.EqualTo(2));
        Assert.That(requests[0].Tag, Is.EqualTo(Marker(0)));
        Assert.That(requests[1].Tag, Is.EqualTo(T(0)));
        Assert.That(_delivered.Single(packet => packet.Request == Request.EvaluationResult).Tag, Is.EqualTo(T(0)));
    }
}
