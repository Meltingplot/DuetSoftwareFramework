using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Commands;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Commands;

/// <summary>
/// Tests for the SimpleCode command: priority codes, the order and replies of the codes of one command, parse errors and disabled channels
/// </summary>
[TestFixture]
public class SimpleCodeExecution
{
    private FakeFirmwareHost _host = null!;

    [SetUp]
    public async Task SetUp() => _host = await FakeFirmwareHost.StartAsync();

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    private string[] Executed(CodeChannel channel = CodeChannel.HTTP) => [.. _host.Firmware.GetExecutedCodes(channel).Select(code => code.Text)];

    /// <summary>
    /// Create a SimpleCode command like the IPC server does
    /// </summary>
    private DuetControlServer.Commands.SimpleCode CreateSimpleCode(string code, CodeChannel channel)
    {
        DuetControlServer.Commands.SimpleCode simpleCode = _host.Services.GetRequiredService<CommandFactory>().Create<DuetControlServer.Commands.SimpleCode>();
        simpleCode.Code = code;
        simpleCode.Channel = channel;
        return simpleCode;
    }

    /// <summary>
    /// Keep M400 running in the firmware until the returned event is set
    /// </summary>
    private ManualResetEventSlim HoldM400()
    {
        ManualResetEventSlim release = new();
        _host.Firmware.OnCode("M400", _ => release.IsSet ? FakeReply.Ok() : FakeReply.NotFinished);
        return release;
    }

    /// <summary>
    /// Make a channel emulate Marlin. The fake firmware does not report inputs, so the value set here is kept
    /// </summary>
    private async Task EmulateMarlinAsync(CodeChannel channel)
    {
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Inputs[channel]!.Compatibility = Compatibility.Marlin;
        }
    }

    [TestCase("M108")]
    [TestCase("M122")]
    [TestCase("M292")]
    [TestCase("M999 B0")]
    public async Task PriorityCodeRunsFirst(string priorityCode)
    {
        // A priority code may go to another channel if its own is busy, so the order is checked across all channels
        Assert.That(await _host.ExecuteAsync($"G4 P1\n{priorityCode}\nG4 P2"), Is.EqualTo("\n\n\n"));
        Assert.That(_host.Firmware.GetExecutedCodes().Select(code => code.Text), Is.EqualTo(new[] { priorityCode, "G4 P1", "G4 P2" }));
        Assert.That(_host.Firmware.GetReceivedCodes().Single(code => code.Text == priorityCode).Flags.HasFlag(CodeFlags.IsPrioritized), Is.True);
    }

    [Test]
    public async Task ResetOfOtherBoardIsNotPrioritized()
    {
        // Only M999 for the main board (B0) is a priority code
        Assert.That(await _host.ExecuteAsync("G4 P1\nM999 B1\nG4 P2"), Is.EqualTo("\n\n\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "G4 P1", "M999 B1", "G4 P2" }));
        Assert.That(_host.Firmware.GetReceivedCodes().Any(code => code.Flags.HasFlag(CodeFlags.IsPrioritized)), Is.False);
    }

    [Test]
    public async Task PriorityReplyComesFirst()
    {
        _host.Firmware.OnCode("G4", code => FakeReply.Ok($"G4 {code.GetParameter('P')}"));
        _host.Firmware.OnCode("M108", _ => FakeReply.Warning("not heating"));

        // The reply of a priority code keeps its own prefix, the replies of the other codes follow
        Assert.That(await _host.ExecuteAsync("G4 P1\nM108\nG4 P2"), Is.EqualTo("Warning: M108: not heating\nG4 P1\nG4 P2\n"));
    }

    [Test]
    public async Task PriorityCodeWhileChannelIsBusy()
    {
        using ManualResetEventSlim release = HoldM400();
        Task<string> m400 = _host.StartCode("M400");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes().Any(code => code.Text == "M400"), FakeFirmwareHost.DefaultTimeout), Is.True);
        Task<string> g4 = _host.StartCode("G4 P1");

        // A normal code waits for the busy channel, a priority code goes to an idle channel of the same compatibility (CodeProcessor.StartCodeAsync)
        Assert.That(await _host.ExecuteAsync("M108"), Is.EqualTo("\n"));
        Assert.That(m400.IsCompleted || g4.IsCompleted, Is.False);
        FakeCode m108 = _host.Firmware.GetExecutedCodes().Single(code => code.Text == "M108");
        Assert.That(m108.Channel, Is.Not.EqualTo(CodeChannel.HTTP));
        Assert.That(m108.Channel, Is.Not.AnyOf(CodeChannel.File, CodeChannel.File2));

        release.Set();
        Assert.That(await m400.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
        Assert.That(await g4.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "M400", "G4 P1" }));
    }

    [Test]
    public async Task EmergencyStopWhileChannelIsBusy()
    {
        using ManualResetEventSlim release = HoldM400();
        _ = _host.StartCode("M400");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes().Any(code => code.Text == "M400"), FakeFirmwareHost.DefaultTimeout), Is.True);

        // M112 does not wait for the channel and is sent as an emergency stop request
        Assert.That(await _host.ExecuteAsync("M112"), Is.EqualTo("\n"));
        Assert.That(_host.Firmware.GetReceivedRequests().Count(request => request.Request == SbcRequest.EmergencyStop), Is.EqualTo(1));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.State.Status, Is.EqualTo(MachineStatus.Halted));
        }
    }

    [Test]
    public async Task AcknowledgeMessageBoxOfMacro()
    {
        _host.WriteMacro("ask.g", "M291 P\"Continue?\" S2\nG4 P61\n");

        Task<string> m98 = _host.StartCode("M98 P\"ask.g\"");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.Request == Request.WaitForAcknowledgement), FakeFirmwareHost.DefaultTimeout), Is.True);
        Assert.That(m98.IsCompleted, Is.False);

        // M292 is a priority code and reaches RRF while the macro on the same channel waits for it. It runs on that channel once DCS has
        // pushed the level for the message box, before that it goes to another idle channel, which acknowledges the message box as well
        Assert.That(await _host.ExecuteAsync("M292"), Is.EqualTo("\n"));
        Assert.That(await m98.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
        Assert.That(_host.Firmware.GetExecutedCodes().Select(code => code.Text), Is.EqualTo(new[] { "M98 P\"ask.g\"", "M291 P\"Continue?\" S2", "M292", "G4 P61" }));
    }

    [Test]
    public async Task RepliesInOrder()
    {
        _host.Firmware.OnCode("G4", code => FakeReply.Ok($"G4 {code.GetParameter('P')}"));

        Assert.That(await _host.ExecuteAsync("G4 P1\nG4 P2\nG4 P3"), Is.EqualTo("G4 P1\nG4 P2\nG4 P3\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "G4 P1", "G4 P2", "G4 P3" }));
    }

    [Test]
    public async Task ReplyWithError()
    {
        _host.Firmware.OnCode("G4", code => ((int)code.GetParameter('P')! == 2) ? FakeReply.Error("bad") : FakeReply.Ok($"G4 {code.GetParameter('P')}"));

        // The replies form one message whose type is the most severe one (Message.Append)
        Assert.That(await _host.ExecuteAsync("G4 P1\nG4 P2\nG4 P3"), Is.EqualTo("Error: G4 P1\nG4: bad\nG4 P3\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "G4 P1", "G4 P2", "G4 P3" }));
    }

    [Test]
    public async Task ConcurrentCommandsKeepTheirOrder()
    {
        // The IPC server runs the commands of different connections on different threads. The channel lock of SimpleCode
        // keeps the codes of one command together, else the codes of two commands started at the same time are interleaved
        const int codesPerCommand = 100;
        string[] first = [.. Enumerable.Range(1000, codesPerCommand).Select(number => $"G4 P{number}")];
        string[] second = [.. Enumerable.Range(2000, codesPerCommand).Select(number => $"G4 P{number}")];

        using Barrier barrier = new(2);
        Task<string> firstTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return _host.StartCode(string.Join('\n', first));
        });
        Task<string> secondTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return _host.StartCode(string.Join('\n', second));
        });
        await Task.WhenAll(firstTask, secondTask).WaitAsync(FakeFirmwareHost.DefaultTimeout);

        Assert.That(Executed(), Is.EqualTo(first.Concat(second)).Or.EqualTo(second.Concat(first)));
    }

    [Test]
    public async Task ParseError()
    {
        // The codes are parsed before any of them is started, so none is executed
        Assert.That(await _host.ExecuteAsync("G4 P1\nG1 X\"unterminated"), Is.EqualTo("Error: Unterminated string"));
        Assert.That(_host.Firmware.GetReceivedCodes(), Is.Empty);
    }

    [TestCase(Compatibility.Marlin)]
    [TestCase(Compatibility.NanoDLP)]
    public async Task ParseErrorWithOk(Compatibility compatibility)
    {
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Inputs[CodeChannel.HTTP]!.Compatibility = compatibility;
        }

        // Repetier and other hosts expect "ok" after an error message
        string[] lines = (await _host.ExecuteAsync("G1 X\"unterminated")).TrimEnd('\n').Split('\n');
        Assert.That(lines, Is.EqualTo(new[] { "Error: Unterminated string", "ok" }));
    }

    [Test]
    public async Task InvalidPath()
    {
        // ArgumentExceptions of codes are reported like parse errors. The path would leave the base directory
        string name = $"../dsf-test-{Guid.NewGuid():N}.g";
        Assert.That(await _host.ExecuteAsync($"M30 \"{name}\""), Is.EqualTo($"Error: Path {name} escapes the volume root directory"));

        await EmulateMarlinAsync(CodeChannel.HTTP);
        Assert.That((await _host.ExecuteAsync($"M30 \"{name}\"")).TrimEnd('\n').Split('\n'), Is.EqualTo(new[] { $"Error: Path {name} escapes the volume root directory", "ok" }));
    }

    [Test]
    public async Task DisabledChannel()
    {
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Inputs[(int)CodeChannel.Telnet] = null;
        }

        Assert.That(async () => await _host.ExecuteAsync("G4 P1", CodeChannel.Telnet), Throws.InvalidOperationException.With.Message.EqualTo("Requested code channel has been disabled"));
        Assert.That(_host.Firmware.GetReceivedCodes(), Is.Empty);
    }

    [Test]
    public async Task CodesFromFirmware()
    {
        // Codes that RRF passes on to DSF are prioritized, flagged and stay on their channel
        DuetControlServer.Commands.SimpleCode simpleCode = CreateSimpleCode("G4 P1\nG4 P2", CodeChannel.Aux);
        simpleCode.IsFromFirmware = true;

        Assert.That(await simpleCode.ExecuteAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n\n"));
        Assert.That(Executed(CodeChannel.Aux), Is.EqualTo(new[] { "G4 P1", "G4 P2" }));
        Assert.That(_host.Firmware.GetReceivedCodes().All(code => code.Flags.HasFlag(CodeFlags.IsPrioritized | CodeFlags.IsFromFirmware)), Is.True);
    }

    [Test]
    public async Task AsynchronousCodeFromFirmware()
    {
        System.IO.File.WriteAllText(System.IO.Path.Combine(_host.BaseDirectory, "gcodes", "a.g"), "G1 X1\n");

        // Processor.DoFirmwareCode runs codes from RRF asynchronously, their replies go back to RRF as messages for the channel
        DuetControlServer.Commands.SimpleCode simpleCode = CreateSimpleCode("M20 S2", CodeChannel.Aux);
        simpleCode.IsFromFirmware = true;
        simpleCode.ExecuteAsynchronously = true;

        Assert.That(await simpleCode.ExecuteAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.Empty);
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.Message), FakeFirmwareHost.DefaultTimeout), Is.True);
        FakeSbcRequest message = _host.Firmware.GetReceivedRequests().Single(request => request.Request == SbcRequest.Message);
        Assert.That(message.MessageFlags, Is.EqualTo(MessageTypeFlags.AuxMessage));
        using System.Text.Json.JsonDocument reply = System.Text.Json.JsonDocument.Parse(message.Text);
        Assert.That(reply.RootElement.GetProperty("files").EnumerateArray().Select(item => item.GetString()), Is.EqualTo(new[] { "a.g" }));
    }
}
