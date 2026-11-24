using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes.Handlers;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Code = DuetControlServer.Commands.Code;
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

    [TestCase("M32 \"job.g", "Unterminated string")]
    [TestCase("M30 \"a.g\" {1", "Unterminated curly brace")]
    [TestCase("M30 (abc", "Unterminated encapsulated comment")]
    public async Task CodeThatDsfCannotParseGetsTheErrorAsResult(string code, string error)
    {
        // RRF passes M30 and M32 from a text channel on to DSF before it reads their parameters (GCodes2.cpp:737-761 at RRF 6c45e07a7). StringParser ends
        // the line at the newline whatever its quote or brace state, and keeps '(' as text outside CNC mode (StringParser.cpp:113-116, :227-237, :311),
        // so the DoCode carries the line as typed (FindParameters, DataStart and DataLength, :1129-1276; SbcInterface.cpp:1797-1803). DSF's parser refuses
        // it, so no code reaches the Executed stage that sends the result of a DoCode (Executed.cs:170-209). SimpleCode sends the error as the result
        // instead, with the flags and the tag that stage uses (SimpleCode.cs:151-155, :250-254). Without it RRF's channel stays "executing on SBC"
        // and takes no further code (SbcInterface.cpp:884-903)
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        await host.ExecuteAsync("G4 P1", CodeChannel.USB);      // a code from DSF makes the epoch of the channel known, so RRF tags the DoCode
        host.Firmware.PassCodeToSbc(CodeChannel.USB, code);
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetFinishedSbcCodes().Count == 1 && firmware.IsIdle(CodeChannel.USB), Timeout), Is.True,
            "The code that DSF cannot parse did not finish");

        FakePacket doCode = host.Firmware.GetSentPackets().Single(packet => packet.Request == FirmwareRequest.DoCode);
        Assert.That(doCode.Text, Is.EqualTo(code));
        Assert.That(FakeTag.IsTagged(doCode.Tag), Is.True);
        FakeSbcRequest result = MessagesTo(host, CodeChannel.USB).Single();
        Assert.That(result.MessageFlags, Is.EqualTo(MessageTypeFlags.UsbMessage | MessageTypeFlags.ErrorMessageFlag));
        Assert.That(result.Tag, Is.EqualTo(doCode.Tag));
        Assert.That(host.Firmware.GetFinishedSbcCodes()[0].Code, Is.EqualTo(code));
        Assert.That(host.Firmware.GetFinishedSbcCodes()[0].Result, Does.Contain(error));

        // The channel takes the next code, and each DoCode gets one result
        host.Firmware.PassCodeToSbc(CodeChannel.USB, "M929");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetFinishedSbcCodes().Count == 2 && firmware.IsIdle(CodeChannel.USB), Timeout), Is.True,
            "The next code did not finish");
        Assert.That(host.Firmware.GetFinishedSbcCodes()[1], Is.EqualTo(("M929", "Event logging is disabled\n")));
        Assert.That(MessagesTo(host, CodeChannel.USB), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task CodeOfAClientThatDsfCannotParseSendsNothingToTheFirmware()
    {
        // Only a code from the firmware has a DoCode to answer. The parse error of a client's code goes back to the client alone, because a message to
        // the channel in RRF would finish a DoCode that the channel has passed on (SbcInterface.cpp:884-903)
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        Assert.That(await host.ExecuteAsync("M32 \"job.g", CodeChannel.USB), Is.EqualTo("Error: Unterminated string"));
        int transfer = host.Firmware.TransferCount;
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.TransferCount > transfer + 2, Timeout), Is.True);
        Assert.That(MessagesTo(host, CodeChannel.USB), Is.Empty);
    }

    [Test]
    public async Task CodeThatFailsInDsfGetsTheErrorAsResult()
    {
        // M32 needs the file information, and FileInfoParser passes on the CodeParserException of a header line that the code parser rejects
        // (InfoParserErrors). The code from the firmware is asynchronous, so the exception ends it without a result (ProcessInternally.cs:32-39,
        // CodeProcessor.cs:298-306) and the Executed stage sends nothing (Executed.cs:225-230). SimpleCode waits for the code and sends the error as
        // the result of the DoCode instead (SimpleCode.cs:178-193, :224-241). Without it RRF's channel stays "executing on SBC" (SbcInterface.cpp:884-903)
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        System.IO.File.WriteAllText(Path.Combine(host.BaseDirectory, "gcodes", "job.gcode"), "G1 X1\nM117 \"unterminated\n");
        await host.ExecuteAsync("G4 P1", CodeChannel.USB);      // a code from DSF makes the epoch of the channel known, so RRF tags the DoCode
        host.Firmware.PassCodeToSbc(CodeChannel.USB, "M32 \"job.gcode\"");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetFinishedSbcCodes().Count == 1 && firmware.IsIdle(CodeChannel.USB), Timeout), Is.True,
            "The code that failed in DSF did not finish");

        FakePacket doCode = host.Firmware.GetSentPackets().Single(packet => packet.Request == FirmwareRequest.DoCode);
        Assert.That(FakeTag.IsTagged(doCode.Tag), Is.True);
        FakeSbcRequest result = MessagesTo(host, CodeChannel.USB).Single();
        Assert.That(result.MessageFlags, Is.EqualTo(MessageTypeFlags.UsbMessage | MessageTypeFlags.ErrorMessageFlag));
        Assert.That(result.Tag, Is.EqualTo(doCode.Tag));
        Assert.That(host.Firmware.GetFinishedSbcCodes()[0].Code, Is.EqualTo("M32 \"job.gcode\""));
        Assert.That(host.Firmware.GetFinishedSbcCodes()[0].Result, Does.Contain("Unterminated string"));

        // The channel takes the next code, and each DoCode gets one result
        host.Firmware.PassCodeToSbc(CodeChannel.USB, "M929");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetFinishedSbcCodes().Count == 2 && firmware.IsIdle(CodeChannel.USB), Timeout), Is.True,
            "The next code did not finish");
        Assert.That(host.Firmware.GetFinishedSbcCodes()[1], Is.EqualTo(("M929", "Event logging is disabled\n")));
        Assert.That(MessagesTo(host, CodeChannel.USB), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task ExceptionOfAHandlerIsTheResultOfACodeFromTheFirmwareOnly()
    {
        // Any exception but a cancellation becomes the result of a code from the firmware, because RRF has no other way to learn that the code ended.
        // A client still gets an exception other than CodeParserException and ArgumentException as an exception (SimpleCode.cs:224)
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(configureServices: services => services.AddKeyedSingleton<ICodeHandler>(Keys.MCodes,
            (provider, _) => new ThrowingMCodeHandler(ActivatorUtilities.CreateInstance<MCodeHandler>(provider))));
        Assert.That(async () => await host.ExecuteAsync("M929", CodeChannel.USB),
            Throws.TypeOf<IOException>().With.Message.EqualTo(ThrowingMCodeHandler.Error));

        host.Firmware.PassCodeToSbc(CodeChannel.USB, "M929");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetFinishedSbcCodes().Count == 1 && firmware.IsIdle(CodeChannel.USB), Timeout), Is.True,
            "The code that failed in DSF did not finish");
        FakeSbcRequest result = MessagesTo(host, CodeChannel.USB).Single();
        Assert.That(result.MessageFlags, Is.EqualTo(MessageTypeFlags.UsbMessage | MessageTypeFlags.ErrorMessageFlag));
        Assert.That(result.Text, Does.Contain(ThrowingMCodeHandler.Error));
    }

    /// <summary>
    /// M-code handler that fails M929 with an exception that SimpleCode does not turn into an error reply for a client
    /// </summary>
    /// <param name="handler">Handler of DSF</param>
    private sealed class ThrowingMCodeHandler(ICodeHandler handler) : ICodeHandler
    {
        public const string Error = "Medium has been removed";

        public ValueTask<Message?> ProcessAsync(Code code, CancellationToken cancellationToken) =>
            (code.Type == CodeType.MCode && code.MajorNumber == 929) ? throw new IOException(Error) : handler.ProcessAsync(code, cancellationToken);

        public ValueTask CodeExecutedAsync(Code code, CancellationToken cancellationToken) => handler.CodeExecutedAsync(code, cancellationToken);
    }

    /// <summary>
    /// Messages that DSF sent to one channel alone, which are the results of the codes that channel passed on
    /// </summary>
    /// <param name="host">Host</param>
    /// <param name="channel">Code channel</param>
    /// <returns>Messages in order</returns>
    private static FakeSbcRequest[] MessagesTo(FakeFirmwareHost host, CodeChannel channel) =>
        [.. host.Firmware.GetReceivedMessages().Where(message => ((uint)message.MessageFlags & 0xFFFFu) == 1u << (int)channel)];
}
