using System.Collections.Generic;
using System.IO;
using System.Linq;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.Codes;
using DuetControlServer.Commands;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Code = DuetControlServer.Commands.Code;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Tests that drive the fake firmware directly like LinkService does, to check the packet order and the resend handling
/// </summary>
[TestFixture]
public class FakeFirmwareProtocolTests
{
    private ServiceProvider _provider = null!;
    private FakeFirmware _firmware = null!;

    [SetUp]
    public void SetUp()
    {
        // Only the code factory is needed, so the link adapter of these services does nothing
        _provider = DcsServices.Build();

        _firmware = new FakeFirmware();
        _firmware.Connect();
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    private Code MakeCode(CodeChannel channel, string text)
    {
        Code code = _provider.GetRequiredService<CodeFactory>().Create();
        code.Channel = channel;
        using StringReader reader = new(text);
        DuetAPI.Commands.Code.Parse(reader, code);
        return code;
    }

    /// <summary>
    /// Perform a transfer and read the delivered packets like LinkService does, resending packets on request
    /// </summary>
    /// <returns>Delivered packets except buffer updates</returns>
    private List<FakePacket> Transfer()
    {
        _firmware.PerformFullTransfer();

        List<FakePacket> packets = [.. _firmware.GetSentPackets().Where(packet => packet.Transfer == _firmware.TransferCount)];
        for (int i = 0; i < _firmware.PacketsToRead; i++)
        {
            PacketHeader header = _firmware.ReadNextPacket()!.Value;
            if ((Request)header.Request == Request.ResendPacket)
            {
                _firmware.ResendPacket(header, out _, out _);
            }
        }
        return [.. packets.Where(packet => packet.Request != Request.CodeBufferUpdate)];
    }

    /// <summary>
    /// Perform transfers until packets other than buffer updates are delivered
    /// </summary>
    /// <returns>Delivered packets</returns>
    private List<FakePacket> NextPackets()
    {
        for (int i = 0; i < 5; i++)
        {
            List<FakePacket> packets = Transfer();
            if (packets.Count > 0)
            {
                return packets;
            }
        }
        return [];
    }

    [Test]
    public void ReplyLatency()
    {
        Transfer();
        Assert.That(_firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P1"), 0), Is.True);

        // The SBC task stores the code after the transfer that sent it, the main task executes it before the next transfer
        // and the SBC task writes the reply after that one, so DSF receives it with the third transfer
        Assert.That(Transfer(), Is.Empty);
        Assert.That(Transfer(), Is.Empty);
        List<FakePacket> packets = Transfer();
        Assert.That(packets, Has.Count.EqualTo(1));
        Assert.That(packets[0].IsCodeReply, Is.True);
        Assert.That(packets[0].MessageType, Is.EqualTo(MessageTypeFlags.HttpMessage | MessageTypeFlags.BinaryCodeReplyFlag));
    }

    [Test]
    public void VariableRequestWhileWaitingForMacro()
    {
        Transfer();
        _firmware.WriteCode(MakeCode(CodeChannel.HTTP, "M98 P\"test.g\""), 0);
        Assert.That(NextPackets().Single().Request, Is.EqualTo(Request.ExecuteMacro));

        // The main task holds the mutex of the channel while it waits for the macro, so the variable request has to be sent again
        _firmware.WriteSetVariable(CodeChannel.HTTP, true, "var.x", "1", 0);
        Assert.That(NextPackets().Single().Request, Is.EqualTo(Request.ResendPacket));

        // Once the macro has been started, the resent request creates the variable in the frame of the macro
        Assert.That(_firmware.WriteMacroStarted(CodeChannel.HTTP, 0), Is.True);
        Assert.That(NextPackets().Single().Request, Is.EqualTo(Request.ResendPacket));
        List<FakePacket> packets = NextPackets();
        Assert.That(packets.Single().Request, Is.EqualTo(Request.VariableResult));
        Assert.That(packets.Single().Value, Is.EqualTo(1));
        Assert.That(_firmware.GetStackDepth(CodeChannel.HTTP), Is.EqualTo(1));
        Assert.That(_firmware.TryGetLocalVariable(CodeChannel.HTTP, "x", out object? value) ? value : null, Is.EqualTo(1));

        // The reply to M98 comes before the notification about the closed file, and the variable is gone afterwards
        _firmware.WriteMacroCompleted(CodeChannel.HTTP, false, 0);
        packets = NextPackets();
        Assert.That(packets.Select(packet => packet.Request), Is.EqualTo(new[] { Request.Message, Request.MacroFileClosed }));
        Assert.That(packets[0].RelatedCode?.Text, Is.EqualTo("M98 P\"test.g\""));
        Assert.That(_firmware.TryGetLocalVariable(CodeChannel.HTTP, "x", out _), Is.False);
    }

    [Test]
    public void ResendBlocksRequestsOfSameType()
    {
        Transfer();
        _firmware.WriteCode(MakeCode(CodeChannel.HTTP, "M98 P\"test.g\""), 0);
        NextPackets();
        _firmware.WriteSetVariable(CodeChannel.HTTP, true, "var.x", "1", 0);
        Assert.That(NextPackets().Single().Request, Is.EqualTo(Request.ResendPacket));

        // SPI.cs does not write another request of a type that is being resent, else the order would change
        Assert.That(_firmware.WriteSetVariable(CodeChannel.HTTP, true, "var.y", "2", 0), Is.False);
        Assert.That(_firmware.WriteEvaluateExpression(CodeChannel.Telnet, "1", 0), Is.True);
    }

    [Test]
    public void MovementLock()
    {
        Transfer();
        _firmware.WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel.HTTP, 0);
        Assert.That(NextPackets().Single().Request, Is.EqualTo(Request.Locked));

        // Another channel has to wait until the lock is released, see GCodes::LockResource
        _firmware.WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel.Telnet, 0);
        Assert.That(NextPackets().Single().Request, Is.EqualTo(Request.ResendPacket));
        Assert.That(NextPackets().Single().Request, Is.EqualTo(Request.ResendPacket));

        // The resent lock request goes out before the unlock request because resends are written while the packets are read
        _firmware.WriteUnlock(CodeChannel.HTTP, 0);
        Assert.That(NextPackets().Single().Request, Is.EqualTo(Request.ResendPacket));
        List<FakePacket> packets = NextPackets();
        Assert.That(packets.Single().Request, Is.EqualTo(Request.Locked));
        Assert.That(packets.Single().Channel, Is.EqualTo(CodeChannel.Telnet));
    }

    [Test]
    public void CodesDiscardedAfterMacroRequest()
    {
        Transfer();
        _firmware.WriteCode(MakeCode(CodeChannel.HTTP, "M98 P\"test.g\""), 0);
        _firmware.WriteCode(MakeCode(CodeChannel.HTTP, "G4 P2"), 0);

        // The macro request invalidates the channel, so RRF throws away the read-ahead code and DSF sends it again later
        Assert.That(NextPackets().Single().Request, Is.EqualTo(Request.ExecuteMacro));
        Transfer();
        Assert.That(_firmware.GetDiscardedCodes().Select(code => code.Text), Is.EqualTo(new[] { "G4 P2" }));
        Assert.That(_firmware.GetReceivedRequests().Count(request => request.Request == SbcRequest.Code), Is.EqualTo(2));
    }
}
