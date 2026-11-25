using System;
using System.Diagnostics;
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
/// Tests for M98.1, which starts a macro on the code channel with index S in inputs[] without holding the caller (Duet3D/RepRapFirmware#1337).
/// USB and USB2 are left out, and all other parameters except W are the same as for M98. With W0 (default) M98.1 returns at once, also when the
/// channel is busy, and the macro waits for the channel without limit. With W&lt;ms&gt; the caller waits up to that long and gets a warning ('result' 1)
/// if the channel is still busy, so that calls cannot pile up
/// </summary>
[TestFixture]
public class MacrosOnOtherChannels
{
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// W for the tests in which the caller gives up
    /// </summary>
    private static readonly TimeSpan ShortWaitTime = TimeSpan.FromMilliseconds(300);

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
    /// Wait for a message in the object model, i.e. what a web interface shows in its console
    /// </summary>
    /// <param name="content">Text the message contains</param>
    /// <param name="timeout">How long to wait</param>
    /// <returns>The message or null if it did not come</returns>
    private async Task<Message?> WaitForMessageAsync(string content, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        try
        {
            while (true)
            {
                using (await _host.Model.AccessReadOnlyAsync(cts.Token))
                {
                    Message? message = _host.Model.Messages.FirstOrDefault(message => message.Content.Contains(content));
                    if (message is not null)
                    {
                        return message;
                    }
                }
                await Task.Delay(10, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

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

    private void StartTargetMacro(string command = "M98.1")
    {
        RunTriggerMacro($"{command} S0 P\"target.g\"\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M400"), "The macro did not start on the other channel");
    }

    private void FinishTargetMacro()
    {
        _release.Set();
        AssertWaitUntil(firmware => firmware.GetStackDepth(CodeChannel.HTTP) == 0, "The macro on the other channel did not finish");
    }

    [TestCase("M98.1")]
    [TestCase("M98.1 W1000")]
    public void TriggerChannelStaysFreeWhileMacroRuns(string command)
    {
        StartTargetMacro(command);

        // The purpose of M98.1: the macro is still running and further trigger macros can start
        Assert.That(_host.Firmware.GetStackDepth(CodeChannel.HTTP), Is.EqualTo(1));
        RunTriggerMacro(string.Empty, "G4 P12");

        // DSF has made a regular M98 of it for the other channel, RRF never gets to see M98.1
        Assert.That(Executed(CodeChannel.HTTP), Is.EqualTo(new[] { "M98 P\"target.g\"", "M400" }));
        Assert.That(_host.Firmware.GetReceivedCodes().Any(code => code.Text.StartsWith("M98.")), Is.False);
        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Ok }));

        FinishTargetMacro();
    }

    [TestCase("M98.1", 0, CodeChannel.HTTP)]
    [TestCase("M98.1", 1, CodeChannel.Telnet)]
    [TestCase("M98.1", 4, CodeChannel.Aux)]
    [TestCase("M98.1", 8, CodeChannel.SBC)]
    [TestCase("M98.1", 10, CodeChannel.Aux2)]
    public void ChannelNumbersOfInputs(string command, int number, CodeChannel channel)
    {
        RunTriggerMacro($"{command} S{number} P\"target.g\"\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(channel).Any(code => code.Text == "M400"), "The macro did not start on the expected channel");
        Assert.That(_host.Firmware.GetStackDepth(channel), Is.EqualTo(1));

        _release.Set();
        AssertWaitUntil(firmware => firmware.GetStackDepth(channel) == 0, "The macro did not finish");
    }

    [Test]
    public void ChannelNumberFromExpression()
    {
        RunTriggerMacro("var channel = 1\nM98.1 S{var.channel} P\"target.g\"\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Telnet).Any(code => code.Text == "M400"), "The macro did not start on Telnet");

        _release.Set();
        AssertWaitUntil(firmware => firmware.GetStackDepth(CodeChannel.Telnet) == 0, "The macro did not finish");
    }

    [TestCase("M98.1")]
    [TestCase("M98.1 W500")]
    [TestCase("M98.1 W{var.x * 100}")]
    public void SameParametersAsM98(string command)
    {
        // S selects the channel and W is the wait time, both are left out. Every other parameter goes to the M98. Expressions refer to the variables of the macro calling M98.1
        RunTriggerMacro($"var x = 5\n{command} P\"target.g\" A{{var.x}} S0 C\"yellow\" K1 B1.5\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M400"), "The macro did not start");

        Assert.That(Received(CodeChannel.HTTP)[0], Is.EqualTo("M98 P\"target.g\" A{5} C\"yellow\" K1 B1.5"));

        FinishTargetMacro();
    }

    [Test]
    public void SameParameterValuesAsM98()
    {
        // The macro must not see a difference, e.g. the string "5" must not become a number and true must not become a string.
        // Arrays are left to LabPrinterTests.MacrosOnOtherChannels because the fake firmware does not parse them in parameters of M98
        const string parameters = "A{var.x} B{\"5\"} C{\"yellow\"} D{true} E{1.5} F\"plain\" I7 H{var.x + 1}";
        string[] letters = ["A", "B", "C", "D", "E", "F", "I", "H"];
        _host.WriteMacro("values.g", string.Concat(letters.Select(letter => $"var v{letter} = param.{letter}\n")) + "M400\n");

        // M98 from the trigger channel, which then waits behind M400 in the macro
        _host.WriteMacro("trigger5.g", $"var x = 5\nM98 P\"values.g\" {parameters}\n");
        _host.Firmware.RequestMacro(CodeChannel.Trigger, "trigger5.g", true);
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Trigger).Any(code => code.Text == "M400"), "M98 did not start the macro");
        object?[] expected = [.. letters.Select(letter => _host.Firmware.TryGetLocalVariable(CodeChannel.Trigger, "v" + letter, out object? value) ? value : "missing")];
        _release.Set();
        AssertWaitUntil(firmware => firmware.GetStackDepth(CodeChannel.Trigger) == 0, "M98 did not finish");
        _release.Reset();

        RunTriggerMacro($"var x = 5\nM98.1 S0 P\"values.g\" {parameters}\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M400"), "M98.1 did not start the macro");
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
        _host.WriteMacro("trigger5.g", "M98.1 S0 P\"second.g\" R1\nM400\n");
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
        RunTriggerMacro("var x = \"target\"\nM98.1 S0 P{var.x ^ \".g\"}\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M400"), "The macro did not start");

        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Ok }));

        FinishTargetMacro();
    }

    #region Busy channel
    [Test]
    public async Task MacroWaitsForCodeThatTookTheChannel()
    {
        // The caller saw the channel free in inputs[], then a code from a client took it before M98.1.
        // M98.1 returns at once and the macro starts after the code
        _host.WriteMacro("second.g", "G4 P21\n");
        Task<string> m400 = _host.StartCode("M400");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M400"), "M400 did not start");

        RunTriggerMacro("M98.1 S0 P\"second.g\"\n", "G4 P11");

        Assert.That(m400.IsCompleted, Is.False, "The trigger macro waited for the busy channel");
        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Ok }));
        Assert.That(Received(CodeChannel.HTTP), Does.Not.Contain("M98 P\"second.g\""));

        _release.Set();
        await m400.WaitAsync(FakeFirmwareHost.DefaultTimeout);
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "G4 P21") && firmware.GetStackDepth(CodeChannel.HTTP) == 0, "The macro did not start");
        Assert.That(Received(CodeChannel.HTTP).Take(2), Is.EqualTo(new[] { "M400", "M98 P\"second.g\"" }));
    }

    [Test]
    public void MacroWaitsForMacro()
    {
        StartTargetMacro();
        _host.WriteMacro("second.g", "G4 P21\n");

        RunTriggerMacro("M98.1 S0 P\"second.g\"\n", "G4 P12");

        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Ok, CodeResult.Ok }));
        Assert.That(Received(CodeChannel.HTTP), Does.Not.Contain("M98 P\"second.g\""));

        _release.Set();
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "G4 P21") && firmware.GetStackDepth(CodeChannel.HTTP) == 0, "The second macro did not start");
        Assert.That(Executed(CodeChannel.HTTP), Is.EqualTo(new[] { "M98 P\"target.g\"", "M400", "M98 P\"second.g\"", "G4 P21" }));
    }

    [Test]
    public void SecondCallWaitsForFirstMacro()
    {
        // The channel is busy with the first macro as soon as M98.1 returns, so the second macro waits for the first one instead of going between
        _host.WriteMacro("second.g", "G4 P21\n");

        RunTriggerMacro("M98.1 S0 P\"target.g\"\nM98.1 S0 P\"second.g\"\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M400"), "The macro did not start");

        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Ok, CodeResult.Ok }));
        Assert.That(Received(CodeChannel.HTTP), Does.Not.Contain("M98 P\"second.g\""));

        _release.Set();
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "G4 P21") && firmware.GetStackDepth(CodeChannel.HTTP) == 0, "The second macro did not start");
        Assert.That(Executed(CodeChannel.HTTP), Is.EqualTo(new[] { "M98 P\"target.g\"", "M400", "M98 P\"second.g\"", "G4 P21" }));
    }

    [Test]
    public async Task MacroWaitsForMessageBoxFromCode()
    {
        // The message box is on a stack level without a file like the M98 to start, so ChannelProcessor.IsIdle regards the channel as idle
        Task<string> m291 = _host.StartCode("M291 P\"x\" S2");
        AssertWaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.Request == Request.WaitForAcknowledgement), "The message box did not open");

        RunTriggerMacro("M98.1 S0 P\"target.g\"\n", "G4 P11");
        await Task.Delay(HoldTime);

        Assert.That(Received(CodeChannel.HTTP), Does.Not.Contain("M98 P\"target.g\""));
        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Ok }));

        await _host.ExecuteAsync("M292");
        await m291.WaitAsync(FakeFirmwareHost.DefaultTimeout);
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M400"), "The macro did not start after the message box");

        FinishTargetMacro();
    }

    [Test]
    public async Task MacroWaitsForMessageBoxInMacro()
    {
        // Same as above with the macro below the message box, e.g. the final S2 box that shows the result of a flow
        _host.WriteMacro("ask.g", "M291 P\"x\" S2\n");
        RunTriggerMacro("M98.1 S0 P\"ask.g\"\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.Request == Request.WaitForAcknowledgement), "The message box did not open");

        RunTriggerMacro("M98.1 S0 P\"target.g\"\n", "G4 P12");
        await Task.Delay(HoldTime);

        Assert.That(Received(CodeChannel.HTTP), Does.Not.Contain("M98 P\"target.g\""));
        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Ok, CodeResult.Ok }));

        await _host.ExecuteAsync("M292");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M400"), "The macro did not start after the message box");

        FinishTargetMacro();
    }

    [TestCase("")]
    [TestCase(" W0")]
    public async Task MacroWaitsWithoutLimit(string waitTime)
    {
        // W0 is the default: M98.1 returns at once and the macro waits until the channel is free, however long that takes
        Task<string> m400 = _host.StartCode("M400", CodeChannel.Telnet);
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Telnet).Any(code => code.Text == "M400"), "M400 did not start");

        Message? result = await _host.ExecuteCodeAsync($"M98.1 S1 P\"target.g\"{waitTime}");
        Assert.That(result?.Type, Is.EqualTo(MessageType.Success));

        await Task.Delay(ShortWaitTime * 3);
        Assert.That(Received(CodeChannel.Telnet), Does.Not.Contain("M98 P\"target.g\""));

        _release.Set();
        await m400.WaitAsync(FakeFirmwareHost.DefaultTimeout);
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Telnet).Count(code => code.Text == "M400") == 2 && firmware.GetStackDepth(CodeChannel.Telnet) == 0, "The macro did not start");
    }

    [Test]
    public async Task CallerGetsWarningAfterW()
    {
        // With W the caller waits for the channel, so 'result' tells it that the macro did not start, and nothing keeps waiting after that
        Task<string> m400 = _host.StartCode("M400", CodeChannel.Telnet);
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Telnet).Any(code => code.Text == "M400"), "M400 did not start");

        Stopwatch stopwatch = Stopwatch.StartNew();
        Message? result = await _host.ExecuteCodeAsync($"M98.1 S1 P\"target.g\" W{ShortWaitTime.TotalMilliseconds}");
        stopwatch.Stop();

        Assert.That(result?.Type, Is.EqualTo(MessageType.Warning));
        Assert.That(result?.Content.TrimEnd(), Is.EqualTo("Macro file target.g not started because code channel Telnet is busy"));
        Assert.That(stopwatch.Elapsed, Is.GreaterThanOrEqualTo(ShortWaitTime));
        Assert.That(stopwatch.Elapsed, Is.LessThan(ShortWaitTime + HoldTime * 5), "M98.1 waited much longer than W");
        Assert.That(LastCodeResults(CodeChannel.HTTP), Is.EqualTo(new[] { CodeResult.Warning }));

        _release.Set();
        await m400.WaitAsync(FakeFirmwareHost.DefaultTimeout);
        await Task.Delay(HoldTime);
        Assert.That(Received(CodeChannel.Telnet), Does.Not.Contain("M98 P\"target.g\""));
    }

    [Test]
    public async Task CallerWaitsForChannelWithinW()
    {
        StartTargetMacro();
        _host.WriteMacro("second.g", "G4 P21\n");

        // The trigger macro stays in M98.1 until the first macro has finished, then the second one starts and 'result' is 0
        _host.WriteMacro("trigger5.g", $"M98.1 S0 P\"second.g\" W{(int)FakeFirmwareHost.DefaultTimeout.TotalMilliseconds}\nG4 P12\n");
        _host.Firmware.RequestMacro(CodeChannel.Trigger, "trigger5.g", true);
        await Task.Delay(HoldTime);
        Assert.That(Executed(CodeChannel.Trigger), Does.Not.Contain("G4 P12"));
        Assert.That(Received(CodeChannel.HTTP), Does.Not.Contain("M98 P\"second.g\""));

        _release.Set();
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Trigger).Any(code => code.Text == "G4 P12") && firmware.GetStackDepth(CodeChannel.Trigger) == 0, "The trigger macro did not finish");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "G4 P21") && firmware.GetStackDepth(CodeChannel.HTTP) == 0, "The second macro did not start");
        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Ok, CodeResult.Ok }));
    }

    [Test]
    public async Task CallerGetsWarningForMessageBox()
    {
        // A message box waiting for acknowledgement keeps the channel busy, although ChannelProcessor.IsIdle regards it as idle
        Task<string> m291 = _host.StartCode("M291 P\"x\" S2");
        AssertWaitUntil(firmware => firmware.GetSentPackets().Any(packet => packet.Request == Request.WaitForAcknowledgement), "The message box did not open");

        RunTriggerMacro($"M98.1 S0 P\"target.g\" W{ShortWaitTime.TotalMilliseconds}\n", "G4 P11");

        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Warning }));

        await _host.ExecuteAsync("M292");
        await m291.WaitAsync(FakeFirmwareHost.DefaultTimeout);
        await Task.Delay(HoldTime);
        Assert.That(Received(CodeChannel.HTTP), Does.Not.Contain("M98 P\"target.g\""));
    }

    [Test]
    public void BusyRightAfterStart()
    {
        // The channel must be busy as soon as M98.1 returns, or a second call right after it would queue its macro behind the first one
        _host.WriteMacro("second.g", "G4 P21\n");

        RunTriggerMacro($"M98.1 S0 P\"target.g\"\nM98.1 S0 P\"second.g\" W{ShortWaitTime.TotalMilliseconds}\n", "G4 P11");
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M400"), "The macro did not start");

        Assert.That(LastCodeResults(CodeChannel.Trigger), Is.EqualTo(new[] { CodeResult.Ok, CodeResult.Warning }));
        Assert.That(Received(CodeChannel.HTTP), Does.Not.Contain("M98 P\"second.g\""));

        FinishTargetMacro();
    }

    [Test]
    public async Task ResetDropsWaitingMacro()
    {
        // The waiting macro belongs to the other channel, so a reset cancels it like the other pending codes of that channel
        StartTargetMacro();
        _host.WriteMacro("second.g", "G4 P21\n");
        RunTriggerMacro("M98.1 S0 P\"second.g\"\n", "G4 P12");

        _host.Firmware.SimulateReset();
        AssertWaitUntil(firmware => !firmware.IsResetPending, "DSF did not see the reset");

        // The channel is free again, a macro that survived the reset would start now
        Assert.That(await _host.ExecuteAsync("G4 P31"), Is.EqualTo("\n"));
        await Task.Delay(HoldTime);
        Assert.That(Received(CodeChannel.HTTP), Does.Not.Contain("M98 P\"second.g\""));
    }
    #endregion

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

        RunTriggerMacro("M98.1 S0 P\"ask.g\"\n", "G4 P11");
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

        RunTriggerMacro("M98.1 S0 P\"echo.g\"\n", "G4 P11");

        Assert.That(await WaitForMessageAsync("output of the macro", FakeFirmwareHost.DefaultTimeout), Is.Not.Null);
    }

    [TestCase("M98.1 S3 P\"target.g\"", "Invalid code channel number 3", TestName = "USB does not run macros without a USB host")]
    [TestCase("M98.1 S14 P\"target.g\"", "Invalid code channel number 14", TestName = "USB2 does not run macros without a USB host")]
    [TestCase("M98.1 S5 P\"target.g\"", "Invalid code channel number 5", TestName = "Trigger channel is not open to M98.1")]
    [TestCase("M98.1 S2 P\"target.g\"", "Invalid code channel number 2", TestName = "File channel is not open to M98.1")]
    [TestCase("M98.1 S9 P\"target.g\"", "Invalid code channel number 9", TestName = "Daemon channel is not open to M98.1")]
    [TestCase("M98.1 S15 P\"target.g\"", "Invalid code channel number 15", TestName = "Channel number out of range")]
    [TestCase("M98.1 S-1 P\"target.g\"", "Invalid code channel number -1", TestName = "Negative channel number")]
    [TestCase("M98.1 S0 P\"target.g\"", "same code channel", TestName = "Own channel")]
    [TestCase("M98.1 S1 P\"missing.g\"", "Macro file missing.g not found", TestName = "Missing macro")]
    [TestCase("M98.1 S1", "Missing P parameter", TestName = "Missing P parameter")]
    [TestCase("M98.1 P\"target.g\"", "Missing S parameter", TestName = "Missing S parameter")]
    [TestCase("M98.1 S1 P\"target.g\" W-1", "Invalid wait time -1", TestName = "Negative wait time")]
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

        Message? result = await _host.ExecuteCodeAsync("M98.1 S1 P\"target.g\"");

        Assert.That(result?.Type, Is.EqualTo(MessageType.Error));
        Assert.That(result?.Content, Does.Contain("disabled"));
    }
}
