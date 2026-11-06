using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Link.Channel;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Codes;

/// <summary>
/// Tests for M98.n, which starts a macro on another code channel and returns once it is queued.
/// n is the channel number of M118 P (wiki: Gcodes.md, M118) and all other parameters are the same as for M98
/// </summary>
[TestFixture]
public class MacrosOnOtherChannels
{
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(200);

    private FakeFirmwareHost _host = null!;
    private ManualResetEventSlim _release = null!;

    [SetUp]
    public async Task SetUp()
    {
        _host = await FakeFirmwareHost.StartAsync();

        // M400 stands in for whatever makes a macro blocking
        _release = new ManualResetEventSlim();
        _host.Firmware.OnCode("M400", _ => _release.IsSet ? FakeReply.Ok() : FakeReply.NotFinished);
        _host.WriteMacro("target.g", "M400\n");
    }

    [TearDown]
    public async Task TearDown()
    {
        _release.Set();
        await _host.DisposeAsync();
        _release.Dispose();
    }

    private string[] Executed(CodeChannel channel) => [.. _host.Firmware.GetExecutedCodes(channel).Select(code => code.Text)];

    private string[] Received(CodeChannel channel) => [.. _host.Firmware.GetReceivedCodes(channel).Select(code => code.Text)];

    private CodeResult[] LastCodeResults(CodeChannel channel) =>
        [.. _host.Firmware.GetReceivedRequests().Where(request => request.Request == SbcRequest.SetLastCodeResult && request.Channel == channel).Select(request => request.CodeResult)];

    private void AssertWaitUntil(Func<FakeFirmware.FakeFirmware, bool> condition, string message) =>
        Assert.That(_host.Firmware.WaitUntil(condition, FakeFirmwareHost.DefaultTimeout), Is.True, message);

    /// <summary>
    /// Run a macro on the trigger channel like RRF does when a trigger fires and wait until the trigger channel is free again
    /// </summary>
    private void RunTriggerMacro(string content, string marker)
    {
        _host.WriteMacro("trigger5.g", content + marker + "\n");
        int runs = Executed(CodeChannel.Trigger).Count(code => code == marker);
        _host.Firmware.RequestMacro(CodeChannel.Trigger, "trigger5.g", true);
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Trigger).Count(code => code.Text == marker) > runs && firmware.GetStackDepth(CodeChannel.Trigger) == 0,
            "The trigger macro did not finish");
    }

    private void StartTargetMacro()
    {
        RunTriggerMacro("M98.3 P\"target.g\"\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M400"), "The macro did not start on the other channel");
    }

    private void FinishTargetMacro()
    {
        _release.Set();
        AssertWaitUntil(firmware => firmware.GetStackDepth(CodeChannel.HTTP) == 0, "The macro on the other channel did not finish");
    }

    [Test]
    public void TriggerChannelStaysFreeWhileMacroRuns()
    {
        StartTargetMacro();

        // The purpose of M98.n: the macro is still running and further trigger macros can start
        Assert.That(_host.Firmware.GetStackDepth(CodeChannel.HTTP), Is.EqualTo(1));
        RunTriggerMacro(string.Empty, "G4 P12");

        // DSF has made a regular M98 of it for the other channel, RRF never gets to see M98.n
        Assert.That(Executed(CodeChannel.HTTP), Is.EqualTo(new[] { "M98 P\"target.g\"", "M400" }));
        Assert.That(_host.Firmware.GetReceivedCodes().Any(code => code.Text.StartsWith("M98.")), Is.False);
        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Ok }));

        FinishTargetMacro();
    }

    [TestCase(1, CodeChannel.USB)]
    [TestCase(2, CodeChannel.Aux)]
    [TestCase(3, CodeChannel.HTTP)]
    [TestCase(4, CodeChannel.Telnet)]
    [TestCase(5, CodeChannel.Aux2)]
    [TestCase(7, CodeChannel.USB2)]
    public void ChannelNumbersOfM118(int number, CodeChannel channel)
    {
        RunTriggerMacro($"M98.{number} P\"target.g\"\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(channel).Any(code => code.Text == "M400"), "The macro did not start on the expected channel");
        Assert.That(_host.Firmware.GetStackDepth(channel), Is.EqualTo(1));

        _release.Set();
        AssertWaitUntil(firmware => firmware.GetStackDepth(channel) == 0, "The macro did not finish");
    }

    [Test]
    public void SameParametersAsM98()
    {
        // No letter is reserved, the macro gets every parameter except for P. Expressions refer to the variables of the macro calling M98.n
        RunTriggerMacro("var x = 5\nM98.3 P\"target.g\" S{var.x} C\"yellow\" K1 A1.5\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M400"), "The macro did not start");

        Assert.That(Received(CodeChannel.HTTP)[0], Is.EqualTo("M98 P\"target.g\" S{5} C\"yellow\" K1 A1.5"));

        FinishTargetMacro();
    }

    [Test]
    public void SameParameterValuesAsM98()
    {
        // The macro must not see a difference, e.g. the string "5" must not become a number and true must not become a string.
        // Arrays are left to LabPrinterTests.MacrosOnOtherChannels because the fake firmware does not parse them in parameters of M98
        const string parameters = "S{var.x} B{\"5\"} C{\"yellow\"} D{true} E{1.5} F\"plain\" I7 H{var.x + 1}";
        string[] letters = ["S", "B", "C", "D", "E", "F", "I", "H"];
        _host.WriteMacro("values.g", string.Concat(letters.Select(letter => $"var v{letter} = param.{letter}\n")) + "M400\n");

        // M98 from the trigger channel, which then waits behind M400 in the macro
        _host.WriteMacro("trigger5.g", $"var x = 5\nM98 P\"values.g\" {parameters}\n");
        _host.Firmware.RequestMacro(CodeChannel.Trigger, "trigger5.g", true);
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Trigger).Any(code => code.Text == "M400"), "M98 did not start the macro");
        object?[] expected = [.. letters.Select(letter => _host.Firmware.TryGetLocalVariable(CodeChannel.Trigger, "v" + letter, out object? value) ? value : "missing")];
        _release.Set();
        AssertWaitUntil(firmware => firmware.GetStackDepth(CodeChannel.Trigger) == 0, "M98 did not finish");
        _release.Reset();

        RunTriggerMacro($"var x = 5\nM98.3 P\"values.g\" {parameters}\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M400"), "M98.3 did not start the macro");
        object?[] actual = [.. letters.Select(letter => _host.Firmware.TryGetLocalVariable(CodeChannel.HTTP, "v" + letter, out object? value) ? value : "missing")];

        Assert.That(expected, Does.Not.Contain("missing"));
        Assert.That(actual, Is.EqualTo(expected));
        Assert.That(actual[1], Is.EqualTo("5"));
        Assert.That(actual[3], Is.EqualTo(true));

        FinishTargetMacro();
    }

    [Test]
    public void ParameterRDoesNotChangeTheCallingMacro()
    {
        // M98 R marks the running macro as pausable, but with P it is a parameter for the new macro (GCodes2.cpp, M98)
        _host.WriteMacro("second.g", "G4 P21\n");
        _host.WriteMacro("trigger5.g", "M98.3 P\"second.g\" R1\nM400\n");
        _host.Firmware.RequestMacro(CodeChannel.Trigger, "trigger5.g", true);
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Trigger).Any(code => code.Text == "M400"), "The trigger macro did not reach M400");

        bool isPausable = _host.Services.GetRequiredService<Manager>()[CodeChannel.Trigger].CurrentState.File is MacroFile macro && macro.IsPausable;

        Assert.That(isPausable, Is.False);
        Assert.That(Received(CodeChannel.HTTP)[0], Is.EqualTo("M98 P\"second.g\" R1"));

        _release.Set();
        AssertWaitUntil(firmware => firmware.GetStackDepth(CodeChannel.Trigger) == 0, "The trigger macro did not finish");
    }

    [Test]
    public void FileNameFromExpression()
    {
        RunTriggerMacro("var x = \"target\"\nM98.3 P{var.x ^ \".g\"}\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M400"), "The macro did not start");

        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Ok }));

        FinishTargetMacro();
    }

    [Test]
    public void BusyWithMacro()
    {
        StartTargetMacro();
        _host.WriteMacro("second.g", "G4 P21\n");

        RunTriggerMacro("M98.3 P\"second.g\"\n", "G4 P12");

        // It is not queued and 'result' tells the caller. No message, the machine tick tries this every minute
        Assert.That(Received(CodeChannel.HTTP), Does.Not.Contain("M98 P\"second.g\""));
        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Ok, CodeResult.Warning }));
        using (_host.Model.AccessReadOnly())
        {
            Assert.That(_host.Model.Messages, Is.Empty);
        }

        // Once the channel is free the same call works
        FinishTargetMacro();
        RunTriggerMacro("M98.3 P\"second.g\"\n", "G4 P13");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "G4 P21"), "The second macro did not start");
        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Ok, CodeResult.Warning, CodeResult.Ok }));
    }

    [Test]
    public async Task BusyWithMessageBoxFromCode()
    {
        // The message box is on a stack level without a file like the M98 to start, so ChannelProcessor.IsIdle regards the channel as idle
        Task<string> m291 = _host.StartCode("M291 P\"x\" S2");
        AssertWaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.Request == Request.WaitForAcknowledgement), "The message box did not open");

        RunTriggerMacro("M98.3 P\"target.g\"\n", "G4 P11");

        Assert.That(Received(CodeChannel.HTTP), Does.Not.Contain("M98 P\"target.g\""));
        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Warning }));

        await _host.ExecuteAsync("M292");
        await m291.WaitAsync(FakeFirmwareHost.DefaultTimeout);
    }

    [Test]
    public async Task BusyWithMessageBoxInMacro()
    {
        // Same as above with the macro below the message box, e.g. the final S2 box that shows the result of a flow
        _host.WriteMacro("ask.g", "M291 P\"x\" S2\n");
        RunTriggerMacro("M98.3 P\"ask.g\"\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.Request == Request.WaitForAcknowledgement), "The message box did not open");

        RunTriggerMacro("M98.3 P\"target.g\"\n", "G4 P12");

        Assert.That(Received(CodeChannel.HTTP), Does.Not.Contain("M98 P\"target.g\""));
        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Ok, CodeResult.Warning }));

        await _host.ExecuteAsync("M292");
        AssertWaitUntil(firmware => firmware.GetStackDepth(CodeChannel.HTTP) == 0, "The macro did not finish");
    }

    [Test]
    public async Task BusyWithCode()
    {
        Task<string> m400 = _host.StartCode("M400");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M400"), "M400 did not start");

        RunTriggerMacro("M98.3 P\"target.g\"\n", "G4 P11");

        Assert.That(Received(CodeChannel.HTTP), Does.Not.Contain("M98 P\"target.g\""));
        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Warning }));

        _release.Set();
        await m400.WaitAsync(FakeFirmwareHost.DefaultTimeout);
    }

    [Test]
    public void BusyRightAfterStart()
    {
        // The channel must be busy as soon as M98.n returns, or two calls in a row would queue the second macro
        _host.WriteMacro("second.g", "G4 P21\n");

        RunTriggerMacro("M98.3 P\"target.g\"\nM98.3 P\"second.g\"\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M400"), "The macro did not start");

        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Ok, CodeResult.Warning }));
        Assert.That(Received(CodeChannel.HTTP), Does.Not.Contain("M98 P\"second.g\""));

        FinishTargetMacro();
    }

    [Test]
    public async Task OtherChannelsStayAvailableWhileMacroRuns()
    {
        StartTargetMacro();

        Assert.That(await _host.ExecuteAsync("G4 P31", CodeChannel.SBC), Is.EqualTo("\n"));
        RunTriggerMacro(string.Empty, "G4 P12");

        FinishTargetMacro();
    }

    [Test]
    public async Task CodesOfTheChannelWaitBehindMacro()
    {
        // As for a macro that a client started. That goes for every regular code, e.g. M32 from a web interface
        StartTargetMacro();

        Task<string> g4 = _host.StartCode("G4 P31");
        Assert.That(g4.Wait(HoldTime), Is.False, "The code has to wait until the macro has finished");
        Assert.That(Received(CodeChannel.HTTP), Does.Not.Contain("G4 P31"));

        FinishTargetMacro();
        Assert.That(await g4.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
    }

    [Test]
    public async Task MessageBoxOfMacroCanBeAcknowledged()
    {
        _host.WriteMacro("ask.g", "M291 P\"x\" S2\nG4 P21\n");

        RunTriggerMacro("M98.3 P\"ask.g\"\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.Request == Request.WaitForAcknowledgement), "The message box did not open");

        // DWC sends M292 on the HTTP channel
        await _host.ExecuteAsync("M292");

        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "G4 P21") && firmware.GetStackDepth(CodeChannel.HTTP) == 0, "The macro did not continue");
    }

    [Test]
    public async Task EmergencyStopWhileMacroRuns()
    {
        StartTargetMacro();

        // M112 is a priority code, so it does not wait behind the macro
        Assert.That(await _host.ExecuteAsync("M112"), Is.EqualTo("\n"));
        Assert.That(_host.Firmware.IsEmergencyStopped, Is.True);
    }

    [Test]
    public async Task OutputOfMacroIsAMessage()
    {
        // The UI gets the completion line of a flow this way, see also the remarks in LabTest.QueryAsync
        _host.WriteMacro("echo.g", "echo \"output of the macro\"\n");

        RunTriggerMacro("M98.3 P\"echo.g\"\n", "G4 P11");

        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        while (true)
        {
            using (await _host.Model.AccessReadOnlyAsync(cts.Token))
            {
                if (_host.Model.Messages.Any(message => message.Content.Contains("output of the macro")))
                {
                    return;
                }
            }
            await Task.Delay(10, cts.Token);
        }
    }

    [TestCase("M98.6 P\"target.g\"", "Invalid code channel number 6", TestName = "MQTT is no code channel")]
    [TestCase("M98.8 P\"target.g\"", "Invalid code channel number 8", TestName = "Channel number out of range")]
    [TestCase("M98.3 P\"target.g\"", "same code channel", TestName = "Own channel")]
    [TestCase("M98.4 P\"missing.g\"", "Macro file missing.g not found", TestName = "Missing macro")]
    [TestCase("M98.4 S1", "P", TestName = "Missing P parameter")]
    public async Task Errors(string code, string expectedText)
    {
        Message? result = await _host.ExecuteCodeAsync(code);

        Assert.That(result?.Type, Is.EqualTo(MessageType.Error));
        Assert.That(result?.Content, Does.Contain(expectedText));
        Assert.That(_host.Firmware.GetReceivedCodes(), Is.Empty);
        Assert.That(LastCodeResults(CodeChannel.HTTP), Is.EqualTo(new[] { CodeResult.Error }));
    }

    [Test]
    public async Task DisabledChannel()
    {
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Inputs[(int)CodeChannel.Telnet] = null;
        }

        Message? result = await _host.ExecuteCodeAsync("M98.4 P\"target.g\"");

        Assert.That(result?.Type, Is.EqualTo(MessageType.Error));
        Assert.That(result?.Content, Does.Contain("disabled"));
    }
}
