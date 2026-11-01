using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Smoke tests that run the real LinkService and code pipelines against the fake firmware
/// </summary>
[TestFixture]
public class FakeFirmwareTests
{
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(200);

    private FakeFirmwareHost _host = null!;

    [SetUp]
    public async Task SetUp() => _host = await FakeFirmwareHost.StartAsync();

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    /// <summary>
    /// Get the executed codes of a channel as text
    /// </summary>
    private string[] Executed(CodeChannel channel = CodeChannel.HTTP) => [.. _host.Firmware.GetExecutedCodes(channel).Select(code => code.Text)];

    [Test]
    public async Task SimpleCode()
    {
        string reply = await _host.ExecuteAsync("G4 P1");

        // DCS terminates every reply to a client code with a newline
        Assert.That(reply, Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "G4 P1" }));
        Assert.That(_host.Firmware.GetUnemulated(), Is.Empty);
    }

    [Test]
    public async Task ErrorReply()
    {
        _host.Firmware.OnCode("G1", code => FakeReply.Error("Bad move"));

        Message? result = await _host.ExecuteCodeAsync("G1 X10");

        // RRF prefixes errors with the command, see GCodes::HandleResult
        Assert.That(result?.Type, Is.EqualTo(MessageType.Error));
        Assert.That(result?.Content.TrimEnd(), Is.EqualTo("G1: Bad move"));
        Assert.That(await _host.ExecuteAsync("G1 X20"), Is.EqualTo("Error: G1: Bad move\n"));
    }

    [Test]
    public async Task Macro()
    {
        string macro = _host.WriteMacro("two.g", "G4 P10\nG4 P11\n");

        Assert.That(await _host.ExecuteAsync($"M98 P\"{macro}\""), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "M98 P\"two.g\"", "G4 P10", "G4 P11" }));

        // The reply that completes M98 comes after the replies of the macro codes
        FakePacket[] replies = [.. _host.Firmware.GetSentPackets().Where(packet => packet.IsCodeReply)];
        Assert.That(replies.Select(packet => packet.RelatedCode?.Text), Is.EqualTo(new[] { "G4 P10", "G4 P11", "M98 P\"two.g\"" }));
        Assert.That(_host.Firmware.GetStackDepth(CodeChannel.HTTP), Is.Zero);
    }

    [Test]
    public async Task MissingMacro()
    {
        Assert.That(await _host.ExecuteAsync("M98 P\"missing.g\""), Is.EqualTo("Error: M98: Macro file missing.g not found\n"));
        Assert.That(_host.Firmware.GetStackDepth(CodeChannel.HTTP), Is.Zero);
    }

    [Test]
    public async Task NestedMacroWithM99()
    {
        _host.WriteMacro("inner.g", "G4 P21\nM99\nG4 P22\n");
        _host.WriteMacro("outer.g", "M98 P\"inner.g\"\nG4 P20\n");

        Assert.That(await _host.ExecuteAsync("M98 P\"outer.g\""), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "M98 P\"outer.g\"", "M98 P\"inner.g\"", "G4 P21", "M99", "G4 P20" }));

        // The read-ahead code after M99 must not be executed and RRF does not reply to M99 (4d80f4663)
        Assert.That(Executed(), Does.Not.Contain("G4 P22"));
        Assert.That(_host.Firmware.GetSentPackets().Any(packet => packet.IsCodeReply && packet.RelatedCode?.Text == "M99"), Is.False);
        Assert.That(_host.Firmware.GetStackDepth(CodeChannel.HTTP), Is.Zero);
    }

    [Test]
    public async Task ClientCodeAfterMacroWithM99()
    {
        // The late-M99-reply class of bugs: RRF does not reply to M99, the next reply belongs to the code that started the macro
        _host.WriteMacro("return.g", "G4 P31\nM99\nG4 P32\n");

        Task<string> m98 = _host.StartCode("M98 P\"return.g\"");
        Task<string> next = _host.StartCode("G4 P33");

        Assert.That(await m98.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
        Assert.That(await next.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "M98 P\"return.g\"", "G4 P31", "M99", "G4 P33" }));
    }

    [Test]
    public async Task ObjectModelQuery()
    {
        string reply = await _host.ExecuteAsync("M409 K\"state.status\"");

        Assert.That(reply, Does.Contain("\"key\":\"state.status\""));
        Assert.That(reply, Does.Contain("\"result\":\"idle\""));

        // DCS answers from its object model mirror
        Assert.That(Executed(), Is.Empty);
    }

    [Test]
    public async Task MessageBox()
    {
        Task<string> m291 = _host.StartCode("M291 P\"x\" S2");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.Request == Request.WaitForAcknowledgement), FakeFirmwareHost.DefaultTimeout), Is.True);
        Assert.That(m291.Wait(HoldTime), Is.False, "M291 S2 must block until it is acknowledged");

        Assert.That(await _host.ExecuteAsync("M292"), Is.EqualTo("\n"));
        Assert.That(await m291.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
        Assert.That(_host.Firmware.MessageBoxCount, Is.Zero);
        Assert.That(_host.Firmware.GetStackDepth(CodeChannel.HTTP), Is.Zero);
    }

    [Test]
    public async Task GlobalVariable()
    {
        Assert.That(await _host.ExecuteAsync("global labTest = 5"), Is.EqualTo("\n"));

        // At the time of writing DCS cannot resolve keys inside "global" from its mirror and lets the firmware answer, this test accepts both
        Assert.That(await _host.ExecuteAsync("M409 K\"global.labTest\""), Does.Contain("\"result\":5"));
        Assert.That(await _host.ExecuteAsync("echo global.labTest"), Is.EqualTo("5\n"));
    }

    [TestCase(1, "G4 P41")]
    [TestCase(2, "G4 P42")]
    public async Task ConditionalMacro(int value, string expectedCode)
    {
        _host.WriteMacro("cond.g", $"var x = {value}\nif var.x == 1\n  G4 P41\nelse\n  G4 P42\nG4 P43\n");

        Assert.That(await _host.ExecuteAsync("M98 P\"cond.g\""), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "M98 P\"cond.g\"", expectedCode, "G4 P43" }));
    }

    [Test]
    public async Task MacroParameter()
    {
        _host.WriteMacro("param.g", "if param.S == 7\n  G4 P71\nelse\n  G4 P72\n");

        Assert.That(await _host.ExecuteAsync("M98 P\"param.g\" S7"), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "M98 P\"param.g\" S7", "G4 P71" }));
    }

    [Test]
    public void SystemMacro()
    {
        // Like a trigger macro that RRF requests on its own
        _host.WriteMacro("trigger5.g", "G4 P81\nG4 P82\n");
        _host.Firmware.RequestMacro(CodeChannel.Trigger, "trigger5.g", true);

        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.Request == Request.MacroFileClosed && packet.Channel == CodeChannel.Trigger), FakeFirmwareHost.DefaultTimeout), Is.True);
        Assert.That(Executed(CodeChannel.Trigger), Is.EqualTo(new[] { "G4 P81", "G4 P82" }));
        Assert.That(_host.Firmware.GetSentPackets().Single(packet => packet.Request == Request.ExecuteMacro).IsFromCode, Is.False);
        Assert.That(_host.Firmware.GetStackDepth(CodeChannel.Trigger), Is.Zero);
    }

    [Test]
    public void MissingSystemMacro()
    {
        _host.Firmware.RequestMacro(CodeChannel.Trigger, "trigger6.g", true);

        // GCodes::DoFileMacro reports the missing file with a warning
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.Request == Request.Message && packet.Text == "Macro file trigger6.g not found\n"), FakeFirmwareHost.DefaultTimeout), Is.True);
        Assert.That(_host.Firmware.GetStackDepth(CodeChannel.Trigger), Is.Zero);
    }

    [Test]
    public async Task AbortInMacro()
    {
        _host.WriteMacro("abort.g", "G4 P51\nabort \"stopped\"\nG4 P52\n");

        // DCS cancels the code that started the aborted macro, so the client gets no reply text
        string reply = await _host.ExecuteAsync("M98 P\"abort.g\"");

        Assert.That(reply, Does.Not.StartWith("Error"));
        Assert.That(Executed(), Is.EqualTo(new[] { "M98 P\"abort.g\"", "G4 P51" }));
        // The reply can arrive before DCS sends the InvalidateChannel request with the next transfer (Processor.cs:893-895)
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetReceivedRequests().Any(request => request.Request == DuetControlServer.Link.Protocol.SbcRequests.Request.InvalidateChannel), FakeFirmwareHost.DefaultTimeout), Is.True);
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetStackDepth(CodeChannel.HTTP) == 0, FakeFirmwareHost.DefaultTimeout), Is.True);
    }

    [Test]
    public async Task CancelledMessageBoxInMacro()
    {
        // M292 P1 cancels a blocking message box, which aborts the macro that opened it (GCodes::SpinGCodeBuffer)
        _host.WriteMacro("ask.g", "M291 P\"q\" S3\nG4 P61\n");

        Task<string> m98 = _host.StartCode("M98 P\"ask.g\"");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.Request == Request.WaitForAcknowledgement), FakeFirmwareHost.DefaultTimeout), Is.True);

        Assert.That(await _host.ExecuteAsync("M292 P1"), Is.EqualTo("\n"));
        Assert.That(await m98.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
        Assert.That(Executed(), Does.Not.Contain("G4 P61"));
        Assert.That(_host.Firmware.GetSentPackets().Any(packet => packet.Request == Request.AbortFile), Is.True);
        Assert.That(_host.Firmware.GetStackDepth(CodeChannel.HTTP), Is.Zero);
    }

    [Test]
    public async Task CodeWhileWaitingForMessageBox()
    {
        Task<string> m291 = _host.StartCode("M291 P\"x\" S2");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.Request == Request.WaitForAcknowledgement), FakeFirmwareHost.DefaultTimeout), Is.True);

        // RRF ignores most codes while a message box waits for acknowledgement, see GCodes::HandleGcode
        Assert.That(await _host.ExecuteAsync("G4 P1"), Is.EqualTo("Warning: G4: Awaiting input, command ignored\n"));

        await _host.ExecuteAsync("M292");
        Assert.That(await m291.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
    }

    [Test]
    public async Task NotFinishedCode()
    {
        // A handler can keep a code running like RRF does when a code handler returns GCodeResult::notFinished
        using ManualResetEventSlim release = new();
        _host.Firmware.OnCode("M400", code => release.IsSet ? FakeReply.Ok("done") : FakeReply.NotFinished);

        Task<string> m400 = _host.StartCode("M400");
        Assert.That(m400.Wait(HoldTime), Is.False);

        release.Set();
        Assert.That(await m400.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("done\n"));
    }

    [Test]
    public async Task EmergencyStop()
    {
        Assert.That(await _host.ExecuteAsync("M112"), Is.EqualTo("\n"));

        Assert.That(_host.Firmware.IsEmergencyStopped, Is.True);
        Assert.That(_host.Firmware.GetReceivedRequests().Count(request => request.Request == DuetControlServer.Link.Protocol.SbcRequests.Request.EmergencyStop), Is.EqualTo(1));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.State.Status, Is.EqualTo(MachineStatus.Halted));
        }
    }

    [Test]
    public async Task ResetAfterEmergencyStop()
    {
        // The wiki on M112: the Duet "can be started again by sending M999, pressing the reset button or power cycling the board"
        IHostApplicationLifetime lifetime = _host.Services.GetRequiredService<IHostApplicationLifetime>();
        TaskCompletionSource<DateTime> stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = lifetime.ApplicationStopping.Register(() => stopping.TrySetResult(DateTime.UtcNow));

        Assert.That(await _host.ExecuteAsync("M112"), Is.EqualTo("\n"));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.State.Status, Is.EqualTo(MachineStatus.Halted));
        }

        // M999 without parameters is not sent as a code but as a reset request, see the M999 case in MCodeHandler and LinkInterface.ResetFirmwareAsync
        Assert.That(await _host.ExecuteAsync("M999"), Is.EqualTo("\n"));
        DateTime replied = DateTime.UtcNow;
        Assert.That(_host.Firmware.GetReceivedRequests().Select(request => request.Request).Where(request => request is SbcRequest.EmergencyStop or SbcRequest.Reset),
            Is.EqualTo(new[] { SbcRequest.EmergencyStop, SbcRequest.Reset }));
        Assert.That(_host.Firmware.GetReceivedCodes(), Is.Empty);
        Assert.That(_host.Firmware.IsEmergencyStopped, Is.False, "RRF has not been reset");

        // DCS stops itself a second after the reply so that the reply can still reach the clients (Restart=always in duetcontrolserver.service starts it again)
        Assert.That(stopping.Task.IsCompleted, Is.False, "DCS must not stop before the reply has gone out");
        DateTime stopped = await stopping.Task.WaitAsync(FakeFirmwareHost.DefaultTimeout);
        Assert.That(stopped - replied, Is.GreaterThan(TimeSpan.FromMilliseconds(500)));

        // LinkService has left its transfer loop after the reset, the services must still stop
        Assert.That(async () => await _host.DisposeAsync().AsTask().WaitAsync(FakeFirmwareHost.DefaultTimeout), Throws.Nothing, "Host did not stop after the reset");
    }

    [Test]
    public async Task CodeAfterEmergencyStopWaitsForReset()
    {
        await _host.ExecuteAsync("M112");

        // After M112 no code reaches RRF until it is reset, which is intended
        Task<string> code = _host.StartCode("G4 P1");
        Assert.That(code.Wait(HoldTime), Is.False);
        Assert.That(_host.Firmware.GetReceivedCodes(), Is.Empty);

        // The reset invalidates the waiting code, so it finishes without a reply and is never sent
        Assert.That(await _host.ExecuteAsync("M999"), Is.EqualTo("\n"));
        Assert.That(await code.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.Empty);
        Assert.That(_host.Firmware.GetReceivedCodes(), Is.Empty);
    }

    [Test]
    public async Task FirmwareReset()
    {
        await _host.ExecuteAsync("global resetTest = 1");
        _host.Firmware.SimulateReset();
        Assert.That(_host.Firmware.WaitUntil(firmware => !firmware.IsResetPending, FakeFirmwareHost.DefaultTimeout), Is.True);

        // LinkService has invalidated everything and the globals are gone after the next update
        Assert.That(await _host.ExecuteAsync("G4 P1"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("echo exists(global.resetTest)"), Is.EqualTo("false\n"));
    }

    [Test]
    public async Task SeededObjectModel()
    {
        _host.Firmware.ObjectModel.Set("state.atxPower", true);
        await _host.Model.WaitForFullUpdateAsync();

        Assert.That(await _host.ExecuteAsync("M409 K\"state.atxPower\""), Does.Contain("\"result\":true"));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.State.AtxPower, Is.True);
            Assert.That(_host.Model.Boards.Single().FirmwareVersion, Is.EqualTo("3.7.0+1.mp.5"));
        }
    }
}
