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
/// Rules of milestone E1 of design/sbc-link-v8-requirements.md (draft 8) as the fake firmware models them, driven directly like LinkService does
/// so that every packet can be placed into a chosen transfer: RRF's SBC task records InvalidateChannel and never refuses it, its main task applies
/// it at the top of its next spin with the per-channel routine of a link loss (CE-7, CE-25), and nothing of that channel is written in between
/// (CE-12). These rules need no tag, so they hold towards every DSF. The model of today's RRF (<see cref="FakeRrfModel.V7"/>) is run as well where
/// the rule changes what RRF does, so that the test shows the old behaviour first (AC-18, AC-21)
/// </summary>
[TestFixture]
public class FakeFirmwareRecordApply
{
    private ServiceProvider _provider = null!;
    private FakeFirmware _firmware = null!;

    [SetUp]
    public void SetUp()
    {
        // Only the code factory is needed, so the link adapter of these services does nothing
        _provider = DcsServices.Build();
        _firmware = new FakeFirmware { Model = FakeRrfModel.RecordApply };
        _firmware.Connect();
        _delivered.Clear();
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    #region Helpers
    private Code MakeCode(CodeChannel channel, string text)
    {
        Code code = _provider.GetRequiredService<CodeFactory>().Create();
        code.Channel = channel;
        using StringReader reader = new(text);
        DuetAPI.Commands.Code.Parse(reader, code);
        return code;
    }

    /// <summary>
    /// Packets delivered by all transfers so far
    /// </summary>
    private readonly List<FakePacket> _delivered = [];

    /// <summary>
    /// Perform a transfer and read the delivered packets like LinkService does, resending packets on request
    /// </summary>
    /// <returns>Delivered packets except buffer updates</returns>
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

    /// <summary>
    /// Perform transfers until a condition holds
    /// </summary>
    private void TransferUntil(System.Func<bool> condition, string description, int maxTransfers = 10)
    {
        for (int i = 0; i < maxTransfers && !condition(); i++)
        {
            Transfer();
        }
        Assert.That(condition(), Is.True, description);
    }

    /// <summary>
    /// Packets of a channel delivered after the given transfer, except code replies and buffer updates
    /// </summary>
    private FakePacket[] EventsAfter(int transfer, CodeChannel channel) =>
        [.. _delivered.Where(packet => packet.Transfer > transfer && packet.Channel == channel && !packet.IsCodeReply && packet.Request != Request.ResendPacket)];

    private FakePacket[] ResendsOf(SbcRequest request) => [.. _delivered.Where(packet => packet.Request == Request.ResendPacket && packet.ResendRequest?.Request == request)];

    /// <summary>
    /// Let M98 of the given macro run on a channel until RRF waits for the macro and its request has reached DSF, without answering it
    /// </summary>
    private void StartMacroWait(CodeChannel channel, string macro)
    {
        Assert.That(_firmware.WriteCode(MakeCode(channel, $"M98 P\"{macro}\""), 0), Is.True);
        TransferUntil(() => _delivered.Any(packet => packet.Request == Request.ExecuteMacro && packet.Channel == channel && packet.Text == macro), "RRF did not request the macro");
        Assert.That(_firmware.IsIdle(channel), Is.False, string.Join("; ", _delivered) + " | executed " + string.Join(",", _firmware.GetExecutedCodes()));
    }

    /// <summary>
    /// Start a macro on a channel and let RRF run its first code, which the test handler holds
    /// </summary>
    private void StartMacro(CodeChannel channel, string macro, string firstCode)
    {
        StartMacroWait(channel, macro);
        Assert.That(_firmware.WriteMacroStarted(channel, 0), Is.True);
        Assert.That(_firmware.WriteCode(MakeCode(channel, firstCode), 0), Is.True);
        TransferUntil(() => _firmware.GetExecutedCodes(channel).Any(code => code.Text == firstCode), "RRF did not run the first code of the macro");
    }
    #endregion

    [Test]
    public void InvalidateChannelIsNeverRefused()
    {
        // While M98 waits for its macro the main task holds the buffer mutex (GCodes.cpp:590), and today the SBC task refuses InvalidateChannel when it
        // cannot get that mutex within SbcYieldTimeout (SbcInterface.cpp:897, :914-918, DF-T11). The record takes no mutex (CE-7)
        StartMacroWait(CodeChannel.HTTP, "wait.g");
        _firmware.FailBufferLocks(CodeChannel.HTTP, 5);
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, 0), Is.True);
        Transfer();
        Transfer();
        Assert.That(ResendsOf(SbcRequest.InvalidateChannel), Is.Empty, "RRF refused InvalidateChannel");
        Assert.That(_firmware.IsIdle(CodeChannel.HTTP), Is.True, "The abort was not applied");
    }

    [Test]
    public void V7RefusesInvalidateChannelForABusyMutex()
    {
        // The same with the model of today's RRF, which is what DSF's catch code #161 is for
        _firmware = new FakeFirmware { Model = FakeRrfModel.V7 };
        _firmware.Connect();
        StartMacroWait(CodeChannel.HTTP, "wait.g");
        _firmware.FailBufferLocks(CodeChannel.HTTP, 1);
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, 0), Is.True);
        Transfer();
        Transfer();
        Assert.That(ResendsOf(SbcRequest.InvalidateChannel), Has.Length.EqualTo(1));
    }

    [Test]
    public void RecordEndsTheMacroWaitAndTheApplyComesInTheNextSpin()
    {
        // AC-16: the record ends the wait with an error (SbcInterface.cpp:893-894), so the main task returns from RequestMacroFile at once instead of
        // after SbcMaxRequestTime, finishes M98 (its reply is surplus, RRF #46 until E3) and applies the abort at the top of its next spin
        StartMacroWait(CodeChannel.HTTP, "wait.g");
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, 0), Is.True);
        _firmware.PerformFullTransfer();
        Assert.That(_firmware.IsAbortRecorded(CodeChannel.HTTP), Is.False, "The main task did not apply the abort in the spin after the record");
        Assert.That(_firmware.IsIdle(CodeChannel.HTTP), Is.True);
        Transfer();
        Assert.That(_delivered.Where(packet => packet.IsCodeReply && packet.Channel == CodeChannel.HTTP), Is.Empty, "M98 replied although DSF dropped it");
    }

    [Test]
    public void ApplyWaitsForTheMacroWaitOfAnotherChannel()
    {
        // AC-16: while the main task waits for a macro of another channel it does not reach the top of its spin, so the apply comes after that answer.
        // Nothing of the aborted channel is written before it
        _firmware.OnCode("G4", code => (code.Text == "G4 P7") ? FakeReply.NotFinished : null);
        StartMacro(CodeChannel.HTTP, "http.g", "G4 P7");
        StartMacroWait(CodeChannel.Telnet, "telnet.g");
        int recordTransfer = _firmware.TransferCount + 1;
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, 0), Is.True);
        Transfer();
        Transfer();
        Assert.That(_firmware.IsAbortRecorded(CodeChannel.HTTP), Is.True, "The abort was applied while the main task waited for the macro of Telnet");

        Assert.That(_firmware.WriteMacroStarted(CodeChannel.Telnet, 0), Is.True);
        Transfer();
        Assert.That(_firmware.IsAbortRecorded(CodeChannel.HTTP), Is.False, "The abort was not applied after the wait of Telnet ended");
        Transfer();
        Assert.That(EventsAfter(recordTransfer, CodeChannel.HTTP), Is.Empty, "RRF wrote an event of the aborted channel");
    }

    [Test]
    public void NothingOfTheChannelIsWrittenBetweenRecordAndApply()
    {
        // AC-20: with the apply delayed, the SBC task writes no event of the channel after the record (CE-12), and the apply voids the pending ones
        // (CE-25). Here a macro closal is pending at the record: RRF closed the macro in the spin after DSF ended it, and InvalidateChannel comes
        // with the next transfer, before the SBC task has written the closal (DcsKeepsRunningWhenAnAbortArrivesBeforeAMacroFileClosalIsSent)
        StartMacro(CodeChannel.HTTP, "http.g", "G4 P1");
        _firmware.ApplyDelay = 3;
        Assert.That(_firmware.WriteMacroCompleted(CodeChannel.HTTP, false, 0), Is.True);
        _firmware.PerformFullTransfer();
        Assert.That(_firmware.GetStackDepth(CodeChannel.HTTP), Is.Zero, "RRF did not close the macro");
        int recordTransfer = _firmware.TransferCount + 1;
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, 0), Is.True);
        for (int i = 0; i < 6; i++)
        {
            Transfer();
        }
        Assert.That(_firmware.IsAbortRecorded(CodeChannel.HTTP), Is.False, "The abort was not applied");
        Assert.That(EventsAfter(recordTransfer, CodeChannel.HTTP), Is.Empty, "RRF wrote an event of the aborted channel");
        Assert.That(_firmware.IsIdle(CodeChannel.HTTP), Is.True);
    }

    [Test]
    public void ReplyOfARunningCodeGoesOutBetweenRecordAndApply()
    {
        // AC-20: replies of a code that still runs are no channel events (CE-12), they go out with the reply FIFO
        bool release = false;
        _firmware.OnCode("G4", code => (code.Text == "G4 P5" && !release) ? FakeReply.NotFinished : FakeReply.Ok($"reply of {code.Text}"));
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P5"), 0), Is.True);
        TransferUntil(() => _firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P5", "G4 P5 did not start");

        // The apply cannot end G4 P5, which survives at the base level (CE-25). Its reply is suppressed by RRF #46 until E3, so switch that off
        _firmware.CodeReplySurplus = false;
        _firmware.ApplyDelay = 4;
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, 0), Is.True);
        Transfer();
        release = true;
        Transfer();
        Transfer();
        Assert.That(_firmware.IsAbortRecorded(CodeChannel.HTTP), Is.True);
        Assert.That(_delivered.Any(packet => packet.IsCodeReply && packet.Text == "reply of G4 P5"), Is.False, "The main task ran the channel before the apply");
        TransferUntil(() => _delivered.Any(packet => packet.IsCodeReply && packet.Text == "reply of G4 P5"), "The reply of the running code did not go out after the apply");
    }

    [Test]
    public void CodesSentBetweenRecordAndApplyRunAfterTheApplyInOrder()
    {
        // AC-20: codes of the channel that DSF sends after InvalidateChannel wait in the ring until the apply (CE-7 (b), (e)), also when the buffer was
        // invalidated for a macro request (SbcInterface.cpp:376-380, :1497-1504), whose cause the abort has voided
        StartMacroWait(CodeChannel.HTTP, "wait.g");
        _firmware.ApplyDelay = 2;
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, 0), Is.True);
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P11"), 0), Is.True);
        Transfer();
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P12"), 0), Is.True);
        Transfer();
        Assert.That(_firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text), Has.None.StartsWith("G4 P1"), "A code ran before the apply");
        TransferUntil(() => _firmware.GetExecutedCodes(CodeChannel.HTTP).Count(code => code.Text.StartsWith("G4 P1")) == 2, "The codes did not run after the apply");
        Assert.That(_firmware.GetExecutedCodes(CodeChannel.HTTP).Where(code => code.Text.StartsWith("G4 P1")).Select(code => code.Text), Is.EqualTo(new[] { "G4 P11", "G4 P12" }));
        Assert.That(_firmware.GetDiscardedCodes(CodeChannel.HTTP), Is.Empty);
    }

    [Test]
    public void LockBetweenRecordAndApplyIsRefusedAndSucceedsAfter()
    {
        // AC-20: the SBC task keeps off the buffer between record and apply, so a Lock is refused as for a busy mutex (CE-7 (d)) and DSF sends it again
        _firmware.ApplyDelay = 2;
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, 0), Is.True);
        Transfer();
        Assert.That(_firmware.WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel.HTTP, 0), Is.True);
        TransferUntil(() => _delivered.Any(packet => packet.Request == Request.Locked && packet.Channel == CodeChannel.HTTP), "The lock was not granted after the apply");
        Assert.That(ResendsOf(SbcRequest.LockAllMovementSystemsAndWaitForStandstill), Is.Not.Empty, "The lock was not refused between record and apply");
        Assert.That(_firmware.MovementLockOwner, Is.EqualTo(CodeChannel.HTTP));
    }

    [Test]
    public void ApplyReleasesTheLocksOfAnIdleChannel()
    {
        // AC-13 (CE-25 (a), FD-27): an Unlock that DSF dropped at the abort (K28) or whose copy RRF drops after the abort leaves no lock, because the
        // apply releases the locks of an idle buffer
        Assert.That(_firmware.WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel.HTTP, 0), Is.True);
        Transfer();
        Assert.That(_firmware.MovementLockOwner, Is.EqualTo(CodeChannel.HTTP));
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, 0), Is.True);
        Transfer();
        Assert.That(_firmware.MovementLockOwner, Is.Null, "The apply kept the lock of an idle channel");

        // Another channel locks right after the apply
        Assert.That(_firmware.WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel.Telnet, 0), Is.True);
        TransferUntil(() => _delivered.Any(packet => packet.Request == Request.Locked && packet.Channel == CodeChannel.Telnet), "Telnet did not get the lock");
    }

    [Test]
    public void V7KeepsTheLocksOfAnIdleChannel()
    {
        // The same with the model of today's RRF: InvalidateChannel releases no lock (SbcInterface.cpp:897-918)
        _firmware = new FakeFirmware { Model = FakeRrfModel.V7 };
        _firmware.Connect();
        Assert.That(_firmware.WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel.HTTP, 0), Is.True);
        Transfer();
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, 0), Is.True);
        Transfer();
        Assert.That(_firmware.MovementLockOwner, Is.EqualTo(CodeChannel.HTTP));
    }

    [Test]
    public void ApplyKeepsTheLocksWhileACodeRuns()
    {
        // AC-13 (FD-27): a code that still runs at the base level survives the abort and keeps its locks until it ends (GCodes2.cpp:5112-5116), because
        // UnlockAll would change a running G28 (GCodes.cpp:5658-5663). No other channel gets the lock meanwhile
        bool release = false;
        _firmware.OnCode("G4", code => (code.Text == "G4 P5" && !release) ? FakeReply.NotFinished : null);
        Assert.That(_firmware.WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel.HTTP, 0), Is.True);
        Transfer();
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P5"), 0), Is.True);
        TransferUntil(() => _firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "G4 P5", "G4 P5 did not start");
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, 0), Is.True);
        Assert.That(_firmware.WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel.Telnet, 0), Is.True);
        Transfer();
        Transfer();
        Assert.That(_firmware.MovementLockOwner, Is.EqualTo(CodeChannel.HTTP), "The apply released the lock of a running code");

        release = true;
        TransferUntil(() => _delivered.Any(packet => packet.Request == Request.Locked && packet.Channel == CodeChannel.Telnet), "Telnet did not get the lock after the code ended");
    }

    [Test]
    public void TriggerFiredBetweenRecordAndApplyRunsAfterTheApply()
    {
        // AC-17 (b): a recorded abort of the trigger channel counts as busy (CE-7 (c)), so CheckTriggers keeps the trigger pending and requests its
        // macro after the apply, in the new epoch
        _firmware.ApplyDelay = 2;
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.Trigger, 0), Is.True);
        Transfer();
        _firmware.FireTrigger(5);
        Transfer();
        Assert.That(_firmware.TriggersPending, Is.EqualTo(1u << 5), "The trigger did not stay pending until the apply");
        TransferUntil(() => _delivered.Any(packet => packet.Request == Request.ExecuteMacro && packet.Channel == CodeChannel.Trigger && packet.Text == "trigger5.g"),
            "The trigger macro was not requested after the apply");
        Assert.That(_firmware.TriggersPending, Is.Zero);
    }

    [Test]
    public void UnsentTriggerRequestEndsWithAnErrorAtTheRecord()
    {
        // AC-17 (a): the main task asked for trigger5.g in the spin before InvalidateChannel arrives, so the request is unsent at the record. The
        // record ends the wait with an error and the request is never written (CE-7 (4), CE-12)
        _firmware.FireTrigger(5);
        _firmware.PerformFullTransfer();
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.Trigger, 0), Is.True);
        int recordTransfer = _firmware.TransferCount + 1;
        for (int i = 0; i < 4; i++)
        {
            Transfer();
        }
        Assert.That(EventsAfter(recordTransfer, CodeChannel.Trigger).Where(packet => packet.Request == Request.ExecuteMacro), Is.Empty, "The void request was written");
        Assert.That(_firmware.IsIdle(CodeChannel.Trigger), Is.True);
    }

    [Test]
    public void ApplyEndsACodePassedOnToDsf()
    {
        // AC-18, CE-25 (b): a DoCode whose result DSF never sends (K13) no longer keeps the channel busy. Today InvalidateChannel leaves it alone
        _firmware.PassCodeToSbc(CodeChannel.Aux, "M929");
        TransferUntil(() => _delivered.Any(packet => packet.Request == Request.DoCode), "The code was not passed on");
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.Aux, 0), Is.True);
        Transfer();
        Assert.That(_firmware.IsIdle(CodeChannel.Aux), Is.True, "The code passed on is still executing on the SBC");

        // A late result is printed to its target and finishes nothing (SbcInterface.cpp:774-803)
        Assert.That(_firmware.WriteMessage((MessageTypeFlags)(1 << (int)CodeChannel.Aux), "Event logging is disabled\n", 0), Is.True);
        Transfer();
        Assert.That(_firmware.GetFinishedSbcCodes(), Is.Empty);
        Assert.That(_firmware.GetReceivedMessages(), Has.Count.EqualTo(1));
    }

    [Test]
    public void UnsentCodeForDsfIsNotWrittenAfterTheAbort()
    {
        // CE-25 (b): an unsent DoCode is ended as well. Clearing sendToSbc alone would not do, the code would ask again on its next spin (GCodes2.cpp:603-606)
        _firmware.PassCodeToSbc(CodeChannel.Aux, "M929");
        _firmware.PerformFullTransfer();
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.Aux, 0), Is.True);
        for (int i = 0; i < 4; i++)
        {
            Transfer();
        }
        Assert.That(_delivered.Where(packet => packet.Request == Request.DoCode), Is.Empty);
        Assert.That(_firmware.IsIdle(CodeChannel.Aux), Is.True);
    }

    [Test]
    public void V7KeepsACodePassedOnToDsfAfterTheAbort()
    {
        // K13 with the model of today's RRF: the channel waits for a result that DSF does not send after its abort
        _firmware = new FakeFirmware { Model = FakeRrfModel.V7 };
        _firmware.Connect();
        _firmware.PassCodeToSbc(CodeChannel.Aux, "M929");
        TransferUntil(() => _delivered.Any(packet => packet.Request == Request.DoCode), "The code was not passed on");
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.Aux, 0), Is.True);
        Transfer();
        Transfer();
        Assert.That(_firmware.IsIdle(CodeChannel.Aux), Is.False);
    }

    [Test]
    public void ApplyClosesABoxAtTheBaseLevelWithoutAMessageAcknowledged()
    {
        // AC-18, CE-25 (c): the routine ends the wait as cancelled without the abort flag, so the main task pops the box level and M291 ends with its
        // final reply instead of the box staying open (OF-T10). No MessageAcknowledged is written for it, and nothing stops a print (GCodes.cpp:593-616)
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "M291 P\"box\" S2"), 0), Is.True);
        TransferUntil(() => _delivered.Any(packet => packet.Request == Request.WaitForAcknowledgement && packet.Channel == CodeChannel.HTTP), "The box was not opened");
        int recordTransfer = _firmware.TransferCount + 1;
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, 0), Is.True);
        for (int i = 0; i < 3; i++)
        {
            Transfer();
        }
        Assert.That(EventsAfter(recordTransfer, CodeChannel.HTTP).Where(packet => packet.Request == Request.MessageAcknowledged), Is.Empty);
        Assert.That(_firmware.IsIdle(CodeChannel.HTTP), Is.True, "The box is still open in RRF");

        // A stock DSF gets the final reply of M291 right after the abort (requirements section 4.5, second table)
        Assert.That(_delivered.Where(packet => packet.IsCodeReply && packet.Transfer > recordTransfer && packet.Channel == CodeChannel.HTTP), Has.Exactly(1).Items);
    }

    [Test]
    public void V7LeavesABoxAtTheBaseLevelOpen()
    {
        // OF-T10 with the model of today's RRF: InvalidateChannel leaves the box open, the next code is ignored
        _firmware = new FakeFirmware { Model = FakeRrfModel.V7 };
        _firmware.Connect();
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "M291 P\"box\" S2"), 0), Is.True);
        TransferUntil(() => _delivered.Any(packet => packet.Request == Request.WaitForAcknowledgement && packet.Channel == CodeChannel.HTTP), "The box was not opened");
        Assert.That(_firmware.WriteInvalidateChannel(CodeChannel.HTTP, 0), Is.True);
        Transfer();
        Transfer();
        Assert.That(_firmware.IsIdle(CodeChannel.HTTP), Is.False);
    }

    /// <summary>
    /// "Tell DSF" flags that a link loss can leave pending (AC-21, K5)
    /// </summary>
    public enum PendingFlag
    {
        /// <summary>macroFileClosed: RRF closed a macro and DSF has yet to be told</summary>
        MacroFileClosed,
        /// <summary>messagePromptPending: a blocking box has yet to be announced</summary>
        MessagePrompt,
        /// <summary>messageAcknowledged: the acknowledgement of a box has yet to be written, set by InvalidateResources itself today (:1772)</summary>
        MessageAcknowledged,
        /// <summary>sendToSbc: a text code has yet to be passed on</summary>
        DoCode,
        /// <summary>invalidated: the buffer was invalidated for a macro request</summary>
        Invalidated
    }

    /// <summary>
    /// Bring a channel into a state where the given flag is pending and lose the link before the SBC task writes it
    /// </summary>
    private void LoseLinkWith(PendingFlag flag)
    {
        switch (flag)
        {
            case PendingFlag.MacroFileClosed:
                StartMacro(CodeChannel.HTTP, "http.g", "G4 P1");
                Assert.That(_firmware.WriteMacroCompleted(CodeChannel.HTTP, false, 0), Is.True);
                _firmware.PerformFullTransfer();
                break;
            case PendingFlag.MessagePrompt:
            case PendingFlag.MessageAcknowledged:
                Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "M291 P\"box\" S2"), 0), Is.True);
                _firmware.PerformFullTransfer();
                if (flag == PendingFlag.MessageAcknowledged)
                {
                    TransferUntil(() => _delivered.Any(packet => packet.Request == Request.WaitForAcknowledgement), "The box was not opened");
                }
                break;
            case PendingFlag.DoCode:
                _firmware.PassCodeToSbc(CodeChannel.HTTP, "M929");
                _firmware.PerformFullTransfer();
                break;
            case PendingFlag.Invalidated:
                StartMacroWait(CodeChannel.HTTP, "wait.g");
                break;
        }
        _firmware.SimulateConnectionLoss();
    }

    [Test]
    public void LinkLossLeavesNoStaleEvent([Values] PendingFlag flag)
    {
        // AC-21 (K5): InvalidateResources runs the routine of the abort (CE-25), so the first exchanges of the new connection carry nothing of the old one
        LoseLinkWith(flag);
        Assert.That(_firmware.HadReset(), Is.True, "DSF does not see the connection reset");
        int reconnectTransfer = _firmware.TransferCount;
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P99"), 0), Is.True);
        for (int i = 0; i < 3; i++)
        {
            Transfer();
        }
        Assert.That(EventsAfter(reconnectTransfer, CodeChannel.HTTP), Is.Empty, $"RRF wrote an event of the old connection");

        // A stale invalidated flag would drop the first codes of the new connection (SbcInterface.cpp:376-380)
        Assert.That(_firmware.GetDiscardedCodes(CodeChannel.HTTP).Select(code => code.Text), Has.None.EqualTo("G4 P99"));
    }

    [Test]
    public void V7WritesStaleEventsAfterALinkLoss([Values(PendingFlag.MacroFileClosed, PendingFlag.MessageAcknowledged, PendingFlag.DoCode)] PendingFlag flag)
    {
        // K5 with the model of today's RRF: InvalidateResources leaves macroFileClosed and sendToSbc and sets messageAcknowledged (SbcInterface.cpp:1746-1773)
        _firmware = new FakeFirmware { Model = FakeRrfModel.V7 };
        _firmware.Connect();
        LoseLinkWith(flag);
        int reconnectTransfer = _firmware.TransferCount;
        for (int i = 0; i < 3; i++)
        {
            Transfer();
        }
        Assert.That(EventsAfter(reconnectTransfer, CodeChannel.HTTP), Is.Not.Empty);
    }
}
