using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
using NUnit.Framework;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Codes that the fake firmware passes on to DSF (DoCode), as RRF does for a text code it cannot run itself (GCodes2.cpp:600-606)
/// </summary>
[TestFixture]
public class FakeFirmwareDoCode
{
    private static readonly System.TimeSpan Timeout = FakeFirmwareHost.DefaultTimeout;

    [Test]
    public async Task ResultOfDsfFinishesTheCode()
    {
        // DSF runs the code as a prioritized code of its channel (Processor.DoFirmwareCode) and sends the result as a message to that channel
        // alone (Executed.cs:170-209), which finishes the code in RRF (SbcInterface.cpp:774-793)
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        host.Firmware.PassCodeToSbc(CodeChannel.Aux, "M929");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetFinishedSbcCodes().Count == 1 && firmware.IsIdle(CodeChannel.Aux), Timeout), Is.True,
            "The code passed on to DSF did not finish");
        Assert.That(host.Firmware.GetFinishedSbcCodes()[0].Code, Is.EqualTo("M929"));
        Assert.That(host.Firmware.GetFinishedSbcCodes()[0].Result, Does.StartWith("Event logging"));
        Assert.That(host.Firmware.GetSentPackets().Count(packet => packet.Request == FirmwareRequest.DoCode), Is.EqualTo(1));
    }

    [Test]
    public async Task ChannelWaitsForTheResult()
    {
        // The buffer of the channel holds the text code until DSF sent the result (GCodeBufferState::executingOnSbc), so a second code waits
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True);
        host.Firmware.PassCodeToSbc(CodeChannel.Aux, "M929");
        host.Firmware.PassCodeToSbc(CodeChannel.Aux, "M111 P-1");

        // The main task takes the code in one transfer, the SBC task writes it in the next and DSF gets it with the one after
        for (int i = 0; i < 10 && !host.Firmware.GetSentPackets().Any(packet => packet.Request == FirmwareRequest.DoCode); i++)
        {
            int transfer = host.Firmware.TransferCount;
            host.Firmware.HoldTransfers(1);
            Assert.That(host.Firmware.WaitUntil(firmware => firmware.TransferCount > transfer && firmware.IsTransferHeld, Timeout), Is.True);
        }
        Assert.That(host.Firmware.GetSentPackets().Count(packet => packet.Request == FirmwareRequest.DoCode), Is.EqualTo(1), "The second code went out before the first finished");
        Assert.That(host.Firmware.IsIdle(CodeChannel.Aux), Is.False);

        host.Firmware.ReleaseTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetFinishedSbcCodes().Count == 2, Timeout), Is.True, "The codes did not finish");
        Assert.That(host.Firmware.GetFinishedSbcCodes().Select(item => item.Code), Is.EqualTo(new[] { "M929", "M111 P-1" }));
        Assert.That(host.Firmware.GetFinishedSbcCodes()[1].Result, Does.StartWith("Current DCS log level"));
    }
}
