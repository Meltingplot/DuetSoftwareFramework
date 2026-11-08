using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Channel;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Codes;

/// <summary>
/// Trigger macros as the CHX 350 runs them: an input or expression trigger fires in RRF, RRF asks DSF for sys/triggerN.g on the trigger
/// channel (GCodes::CheckTriggers, GCodes.cpp:981-1062, and the blocking macro request of SbcInterface.cpp:1492-1505), and DSF runs the macro
/// (LinkService.HandleMacroRequest, Processor.DoMacroFile, MacroFile). The machine uses T2 (external emergency stop input), T4 and T9 (doors)
/// as pin triggers and T3, T5, T7, T8 and T10 as expression triggers that RRF evaluates itself
/// </summary>
[TestFixture]
public class TriggerMacros
{
    private FakeFirmwareHost _host = null!;

    /// <summary>
    /// Channels whose M400 does not finish until they are released. M400 stands in for anything that keeps a channel busy
    /// </summary>
    private readonly HashSet<CodeChannel> _held = [];

    [SetUp]
    public async Task SetUp()
    {
        _host = await FakeFirmwareHost.StartAsync();
        _host.Firmware.OnCode("M400", code =>
        {
            lock (_held)
            {
                return _held.Contains(code.Channel) ? FakeReply.NotFinished : FakeReply.Ok();
            }
        });
    }

    [TearDown]
    public async Task TearDown()
    {
        lock (_held)
        {
            _held.Clear();
        }
        await _host.DisposeAsync();
    }

    private void Hold(params CodeChannel[] channels)
    {
        lock (_held)
        {
            _held.UnionWith(channels);
        }
    }

    private void Release(params CodeChannel[] channels)
    {
        lock (_held)
        {
            _held.ExceptWith(channels);
        }
    }

    #region Helpers
    /// <summary>
    /// Build a macro the way chx350-config deploys it on the printer: every line with a code starts with its line number and ends with a CRC,
    /// e.g. "N9 M112 *61639", the CRC-16/XMODEM of "N9 M112 " (ParserChecksum.cs:204-216, 259-264). Comment and blank lines stay as they are.
    /// DSF checks the line numbers and the CRCs of a macro (CodeFile.cs:74, ParserAsync.cs:101-123, 220-230)
    /// </summary>
    /// <param name="lines">Lines of the macro with their indentation, the line number is the index plus one</param>
    /// <returns>Content of the macro</returns>
    internal static string Numbered(params string[] lines)
    {
        StringBuilder result = new();
        for (int i = 0; i < lines.Length; i++)
        {
            string content = lines[i].TrimStart();
            if (content.Length == 0 || content.StartsWith(';'))
            {
                result.Append(lines[i]).Append('\n');
                continue;
            }

            string numbered = $"N{i + 1} {content} ";
            ushort crc = DuetAPI.Commands.Code.ComputeLineCrc(Encoding.ASCII.GetBytes(numbered));
            result.Append(lines[i][..^content.Length]).Append(numbered).Append('*').Append(crc.ToString("D5")).Append('\n');
        }
        return result.ToString();
    }

    /// <summary>
    /// Write a file below the sys directory of the virtual SD card, creating its directories
    /// </summary>
    private void WriteSysFile(string path, string content)
    {
        string fullPath = Path.Combine(_host.BaseDirectory, "sys", path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        System.IO.File.WriteAllText(fullPath, content);
    }

    private string[] Executed(CodeChannel channel) => [.. _host.Firmware.GetExecutedCodes(channel).Select(code => code.Text)];

    private FakePacket[] MacroRequests(string fileName) =>
        [.. _host.Firmware.GetSentPackets().Where(packet => packet.Request == Request.ExecuteMacro && packet.Channel == CodeChannel.Trigger && packet.Text == fileName)];

    private FakeSbcRequest[] Requests(SbcRequest request, CodeChannel channel) =>
        [.. _host.Firmware.GetReceivedRequests().Where(item => item.Request == request && item.Channel == channel)];

    private void AssertWaitUntil(Func<FakeFirmware.FakeFirmware, bool> condition, string message) =>
        Assert.That(_host.Firmware.WaitUntil(condition, FakeFirmwareHost.DefaultTimeout), Is.True, message);

    /// <summary>
    /// Number of stack levels DSF has on a channel. The base level is one of them
    /// </summary>
    private int DsfStackDepth(CodeChannel channel)
    {
        Processor processor = _host.Services.GetRequiredService<Manager>()[channel];
        using (processor.Lock())
        {
            return processor.Stack.Count;
        }
    }

    /// <summary>
    /// Wait until both sides have closed every macro of the trigger channel and no trigger is pending.
    /// DSF pops its stack when RRF reports the closed macro (LinkService.HandleMacroFileClosed, Processor.MacroFileClosed)
    /// </summary>
    private async Task WaitForTriggerChannelIdleAsync()
    {
        AssertWaitUntil(firmware => firmware.TriggersPending == 0 && firmware.IsIdle(CodeChannel.Trigger), "The trigger channel did not become idle in RRF");
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        while (DsfStackDepth(CodeChannel.Trigger) != 1)
        {
            await Task.Delay(5, cts.Token);
        }
    }
    #endregion

    [Test]
    public void NumberedLinesAsDeployed()
    {
        // Lines 6 to 9 of trigger2.g as read from the lab printer on 2026-11-07, without the trailing comment of line 9
        string expected =
            "N6 if exists(global.self_test_estop_state) && ((\"\" ^ global.self_test_estop_state) == \"1\" || (\"\" ^ global.self_test_estop_state) == \"3\") *39026\n" +
            "  N7 M98 P\"0:/sys/meltingplot/self-test/estop-fired.g\" *59285\n" +
            "N8 M98 P\"0:/sys/meltingplot/lib/set-led-color.g\" C\"yellow\" *42838\n" +
            "N9 M112 *61639\n";

        string[] lines = Numbered(
            ";", ";", ";", ";", ";",
            "if exists(global.self_test_estop_state) && ((\"\" ^ global.self_test_estop_state) == \"1\" || (\"\" ^ global.self_test_estop_state) == \"3\")",
            "  M98 P\"0:/sys/meltingplot/self-test/estop-fired.g\"",
            "M98 P\"0:/sys/meltingplot/lib/set-led-color.g\" C\"yellow\"",
            "M112").Split('\n');

        Assert.That(string.Join('\n', lines[5..]), Is.EqualTo(expected));
    }

    [TestCase(2)]
    [TestCase(4)]
    [TestCase(9)]
    [TestCase(10)]
    public async Task TriggerMacroRuns(int trigger)
    {
        _host.WriteMacro($"trigger{trigger}.g", Numbered("; trigger macro", $"G4 P{trigger}01", $"G4 P{trigger}02", "if true", $"  G4 P{trigger}03"));

        _host.Firmware.FireTrigger(trigger);
        await WaitForTriggerChannelIdleAsync();

        // RRF asks for the macro on the trigger channel as a system macro, not as one started by a code (GCodes.cpp:3699)
        FakePacket request = MacroRequests($"trigger{trigger}.g").Single();
        Assert.That(request.IsFromCode, Is.False);

        // DSF tells RRF that the macro has started in the next transfer (LinkService.cs:377-405 and 541, Processor.cs:900-907),
        // which lets RRF's main task go on (GCodeBuffer::RequestMacroFile waits for it, SbcInterface.cpp:827-845)
        FakeSbcRequest started = Requests(SbcRequest.MacroStarted, CodeChannel.Trigger).Single();
        Assert.That(started.Transfer, Is.EqualTo(request.Transfer + 1));

        // The codes run in the order of the file on the trigger channel and nowhere else
        Assert.That(Executed(CodeChannel.Trigger), Is.EqualTo(new[] { $"G4 P{trigger}01", $"G4 P{trigger}02", $"G4 P{trigger}03" }));
        Assert.That(_host.Firmware.GetReceivedCodes().Where(code => code.Channel != CodeChannel.Trigger).Select(code => code.Text), Has.None.StartWith($"G4 P{trigger}0"));

        // DSF reports the end of the file without an error, RRF closes the macro and tells DSF (Processor.cs:910-914, FakeGCodes.DoFilePrint)
        FakeSbcRequest completed = Requests(SbcRequest.MacroCompleted, CodeChannel.Trigger).Single();
        Assert.That(completed.Flag, Is.False);
        Assert.That(_host.Firmware.GetSentPackets().Count(packet => packet.Request == Request.MacroFileClosed && packet.Channel == CodeChannel.Trigger), Is.EqualTo(1));
        Assert.That(_host.Firmware.GetUnemulated(), Is.Empty);
    }

    [Test]
    public async Task MissingTriggerMacro()
    {
        _host.Firmware.FireTrigger(6);
        await WaitForTriggerChannelIdleAsync();

        // DSF cannot open the file and reports an error instead of a start (Processor.cs:1333-1361, 910-935)
        Assert.That(Requests(SbcRequest.MacroStarted, CodeChannel.Trigger), Is.Empty);
        Assert.That(Requests(SbcRequest.MacroCompleted, CodeChannel.Trigger).Single().Flag, Is.True);

        // RRF reports the missing trigger file as a warning, because CheckTriggers asks for it with reportMissing (GCodes.cpp:1052, 3701-3707).
        // The trigger channel has not run a code from DSF at its base level, so it is a generic message that DSF shows (LinkService.cs:735-738)
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        while (true)
        {
            using (await _host.Model.AccessReadOnlyAsync(cts.Token))
            {
                if (_host.Model.Messages.Any(message => message.Type == MessageType.Warning && message.Content == "Macro file trigger6.g not found"))
                {
                    break;
                }
            }
            await Task.Delay(5, cts.Token);
        }

        // The trigger channel is free again for the next trigger
        _host.WriteMacro("trigger4.g", "G4 P401\n");
        _host.Firmware.FireTrigger(4);
        await WaitForTriggerChannelIdleAsync();
        Assert.That(Executed(CodeChannel.Trigger), Is.EqualTo(new[] { "G4 P401" }));
    }

    [Test, Explicit("After a code of DSF on the trigger channel RRF reports a missing trigger macro as a code reply with the push flag and nothing " +
        "follows (GCodes.cpp:3703-3706), so DSF logs an out-of-order reply and holds the message back (Processor.cs:1122-1127, LinkService.cs:727-733, 762-776)")]
    public async Task MissingTriggerMacroAfterCodeOfDsfOnTriggerChannel()
    {
        // DSF sends codes of its own on the trigger channel outside a macro: M552 with a new IP address, M905 after a change of the system time
        // and M550 after a change of the host name (PeriodicUpdateService.cs:169-224). RRF executes them on the base level of the channel,
        // which marks it as running codes from DSF (GCodeBuffer::PutBinary, lastCodeFromSbc)
        DuetControlServer.Commands.Code code = _host.Services.GetRequiredService<DuetControlServer.Codes.CodeFactory>().Create();
        code.Flags = DuetAPI.Commands.CodeFlags.IsInternallyProcessed | DuetAPI.Commands.CodeFlags.Asynchronous;
        code.Channel = CodeChannel.Trigger;
        code.Type = DuetAPI.Commands.CodeType.MCode;
        code.MajorNumber = 552;
        code.Parameters = [new('P', "192.168.1.2")];
        await code.ExecuteAsync();
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Trigger).Any(item => item.MajorNumber == 552), "M552 did not reach RRF");

        // RRF reports the missing trigger file (GCodes.cpp:1052, 3701-3707), so the user must see it like without the code before
        _host.Firmware.FireTrigger(6);
        await WaitForTriggerChannelIdleAsync();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        bool shown = false;
        while (!shown && !cts.IsCancellationRequested)
        {
            using (await _host.Model.AccessReadOnlyAsync())
            {
                shown = _host.Model.Messages.Any(message => message.Type == MessageType.Warning && message.Content == "Macro file trigger6.g not found");
            }
            await Task.Delay(5);
        }
        Assert.That(shown, Is.True, "The missing trigger macro was not reported: " +
            string.Join(", ", _host.Firmware.GetSentPackets().Where(packet => packet.Request == Request.Message && packet.Text.Contains("not found")).Select(packet => packet.ToString())));
    }

    [Test]
    public async Task TriggerFiresAgainWhileItsMacroRuns()
    {
        // A door that bounces: the trigger fires again while its macro runs
        Hold(CodeChannel.Trigger);
        _host.WriteMacro("trigger4.g", Numbered("G4 P401", "M400", "G4 P402"));
        _host.Firmware.FireTrigger(4);
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Trigger).Any(code => code.Text == "M400"), "trigger4.g did not reach M400");

        // RRF keeps checking the triggers while the macro runs and remembers the edge in a bitmap, so two more edges make one more run.
        // The next macro is requested only once the trigger channel is free (GCodes.cpp:983-989, 1000, 423-433)
        _host.Firmware.FireTrigger(4);
        _host.Firmware.FireTrigger(4);
        Assert.That(_host.Firmware.TriggersPending, Is.EqualTo(1u << 4));
        Assert.That(MacroRequests("trigger4.g"), Has.Length.EqualTo(1));

        Release(CodeChannel.Trigger);
        await WaitForTriggerChannelIdleAsync();

        // DSF runs the second request after the first macro has been closed, the codes of the two runs do not mix
        FakePacket[] requests = MacroRequests("trigger4.g");
        Assert.That(requests, Has.Length.EqualTo(2));
        int firstClosed = _host.Firmware.GetSentPackets().First(packet => packet.Request == Request.MacroFileClosed && packet.Channel == CodeChannel.Trigger).Transfer;
        Assert.That(requests[1].Transfer, Is.GreaterThan(firstClosed));
        Assert.That(Executed(CodeChannel.Trigger), Is.EqualTo(new[] { "G4 P401", "M400", "G4 P402", "G4 P401", "M400", "G4 P402" }));
        Assert.That(Requests(SbcRequest.MacroCompleted, CodeChannel.Trigger).Select(request => request.Flag), Is.EqualTo(new[] { false, false }));
    }

    [Test]
    public async Task LowestPendingTriggerRunsFirst()
    {
        // trigger9.g runs while the doors and the emergency stop input fire
        Hold(CodeChannel.Trigger);
        foreach (int trigger in new[] { 2, 4, 9, 10 })
        {
            _host.WriteMacro($"trigger{trigger}.g", Numbered($"G4 P{trigger}01", (trigger == 9) ? "M400" : $"G4 P{trigger}02"));
        }
        _host.Firmware.FireTrigger(9);
        AssertWaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Trigger).Any(code => code.Text == "M400"), "trigger9.g did not reach M400");
        _host.Firmware.FireTrigger(10);
        _host.Firmware.FireTrigger(4);
        _host.Firmware.FireTrigger(2);

        // A running trigger macro is not interrupted. Afterwards the lowest pending number goes first (GCodes.cpp:992-1000, wiki M581:
        // "If several triggers are pending, the one with the lowest trigger number takes priority"), so trigger2.g waits behind trigger9.g
        Release(CodeChannel.Trigger);
        await WaitForTriggerChannelIdleAsync();

        string[] order = [.. _host.Firmware.GetSentPackets().Where(packet => packet.Request == Request.ExecuteMacro && packet.Channel == CodeChannel.Trigger).Select(packet => packet.Text)];
        Assert.That(order, Is.EqualTo(new[] { "trigger9.g", "trigger2.g", "trigger4.g", "trigger10.g" }));
        Assert.That(Executed(CodeChannel.Trigger), Is.EqualTo(new[] { "G4 P901", "M400", "G4 P201", "G4 P202", "G4 P401", "G4 P402", "G4 P1001", "G4 P1002" }));
    }

    [Test]
    public async Task ConfigurationOfTheChxTriggersReachesFirmware()
    {
        // The trigger lines of the CHX configuration (2026-11-07). None of them uses an SBC field, so DSF passes all of them on to RRF
        // (SbcTriggerService.cs:117-128) and registers no trigger of its own. M582 is not a DSF code at all
        string[] lines =
        [
            "M581 T2 P0:1 S0 R0",
            "M581 T4 P2:3 S0 R0",
            "M581 T9 P2:3 S0 R0",
            "M581 T9 P2:3 S1 R0",
            "M582 T2",
            "M581.1 T3 P\"global.machine_mode_safe == 1431655765\" R0",
            "M581.1 T5 P\"global.z_motor_stall_deadline != 0 && (state.upTime > global.z_motor_stall_deadline || global.z_motor_stall_deadline > state.upTime + 30)\" R0",
            "M581.1 T7 P\"global.mfm_suppress_until > 0 && state.upTime >= global.mfm_suppress_until\" R1",
            "M581.1 T8 P\"state.upTime - global.tick_time >= 10\" R0",
            "M581.1 T8 P\"state.upTime - global.tick_time >= 10\" R1",
            "M581.1 T10 P\"global.fan_exhaust_until > 0 && state.upTime >= global.fan_exhaust_until\" R0",
            "M581.1 T3",
            "M581 T4"
        ];
        foreach (string line in lines)
        {
            Assert.That(await _host.ExecuteAsync(line), Is.EqualTo("\n"), line);
        }

        Assert.That(_host.Firmware.GetReceivedCodes(CodeChannel.HTTP).Select(code => code.Text), Is.EqualTo(lines));
        System.Collections.IDictionary triggers = (System.Collections.IDictionary)typeof(DuetControlServer.Model.SbcTriggerService)
            .GetField("_triggers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(_host.Services.GetRequiredService<DuetControlServer.Model.SbcTriggerService>())!;
        Assert.That(triggers.Count, Is.Zero);
    }

    /// <summary>
    /// RRF resolved a trigger macro request that was not sent yet as failed when DSF invalidated the channel, but sent it afterwards
    /// (SbcInterface.cpp:880-883, 1576-1590 at cde4e4fa1), so DSF started a macro that RRF did not run and discarded every later trigger request
    /// (Processor.cs:1301-1305). Meltingplot/RepRapFirmware#43 clears the request first, and FakeFirmware does the same (finding T1)
    /// </summary>
    [Test]
    public async Task TriggerChannelWorksAfterInvalidationDuringTriggerRequest()
    {
        // Found by TriggerFuzz with an abort of the trigger channel from outside, as the IPC command InvalidateChannel does
        // (Commands/Generic/InvalidateChannel.cs, LinkInterface.AbortAllAsync)
        _host.WriteMacro("trigger3.g", "G4 P301\n");
        _host.WriteMacro("trigger4.g", "G4 P401\n");

        // DSF aborts the files of the trigger channel while its transfers are held, so InvalidateChannel goes out with the next transfer but one
        _host.Firmware.HoldTransfers();
        AssertWaitUntil(firmware => firmware.IsTransferHeld, "The transfers were not held");
        _host.Firmware.FireTrigger(3);
        await _host.Services.GetRequiredService<DuetControlServer.Link.LinkInterface>().AbortAllAsync(CodeChannel.Trigger);

        // In the next transfer RRF's main task asks for trigger3.g after the SBC task has run, and DSF writes InvalidateChannel for the transfer
        // after it, which reaches RRF before the request has gone out
        _host.Firmware.HoldTransfers(1);
        AssertWaitUntil(firmware => firmware.IsTransferHeld && firmware.GetPendingRequests().Any(request => request.Request == SbcRequest.InvalidateChannel && request.Channel == CodeChannel.Trigger),
            "InvalidateChannel was not written");
        _host.Firmware.ReleaseTransfers();
        AssertWaitUntil(firmware => firmware.TriggersPending == 0 && firmware.IsIdle(CodeChannel.Trigger), "The trigger channel did not become idle in RRF");

        // Whatever happens to trigger3.g, both sides must end up with a free trigger channel, so that the next trigger runs
        _host.Firmware.FireTrigger(4);
        bool ran = _host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Trigger).Any(code => code.Text == "G4 P401"), TimeSpan.FromSeconds(2));
        Assert.That(ran, Is.True, $"trigger4.g did not run, DSF has {DsfStackDepth(CodeChannel.Trigger)} stack levels on the trigger channel, " +
            $"requests: {string.Join(", ", _host.Firmware.GetSentPackets().Where(packet => packet.Request == Request.ExecuteMacro).Select(packet => packet.Text))}, " +
            $"MacroCompleted errors: {Requests(SbcRequest.MacroCompleted, CodeChannel.Trigger).Count(request => request.Flag)}");
    }

    #region Trigger macros while the other channels are busy
    /// <summary>
    /// Times from the macro request of RRF to what DSF sends back, measured in the fake firmware
    /// </summary>
    /// <param name="Macro">Trigger macro</param>
    /// <param name="Started">MacroStarted request, which lets RRF's main task go on</param>
    /// <param name="FirstRequest">First request of the macro on the trigger channel after MacroStarted (code, expression or variable)</param>
    /// <param name="FirstCode">First code of the macro</param>
    /// <param name="EmergencyStop">Emergency stop request, if the macro has one</param>
    private sealed record Latency(string Macro, (int transfers, double ms) Started, (int transfers, double ms, string what) FirstRequest,
        (int transfers, double ms, string what) FirstCode, (int transfers, double ms)? EmergencyStop)
    {
        public override string ToString() =>
            $"{Macro}: MacroStarted after {Started.transfers} transfers / {Started.ms:F1} ms, first request ({FirstRequest.what}) after {FirstRequest.transfers} / {FirstRequest.ms:F1} ms, " +
            $"first code ({FirstCode.what}) after {FirstCode.transfers} / {FirstCode.ms:F1} ms" +
            ((EmergencyStop is not null) ? $", emergency stop after {EmergencyStop.Value.transfers} / {EmergencyStop.Value.ms:F1} ms" : string.Empty);
    }

    /// <summary>
    /// Measure the times of the last request of a trigger macro from the logs of the fake firmware
    /// </summary>
    private Latency Measure(string fileName)
    {
        FakePacket request = MacroRequests(fileName).Last();
        FakeSbcRequest[] after = [.. _host.Firmware.GetReceivedRequests().Where(item => item.Transfer > request.Transfer)];
        (int, double) since(FakeSbcRequest item) => (item.Transfer - request.Transfer, Stopwatch.GetElapsedTime(request.Timestamp, item.Timestamp).TotalMilliseconds);

        FakeSbcRequest started = after.First(item => item.Request == SbcRequest.MacroStarted && item.Channel == CodeChannel.Trigger);
        FakeSbcRequest firstRequest = after.First(item => item.Channel == CodeChannel.Trigger && item.Request is SbcRequest.Code or SbcRequest.EvaluateExpression or SbcRequest.SetVariable);
        FakeSbcRequest firstCode = after.First(item => item.Channel == CodeChannel.Trigger && item.Request == SbcRequest.Code);
        FakeSbcRequest? emergencyStop = after.FirstOrDefault(item => item.Request == SbcRequest.EmergencyStop);

        (int transfers, double ms) firstRequestTime = since(firstRequest), firstCodeTime = since(firstCode);
        string firstRequestText = (firstRequest.Request == SbcRequest.Code) ? firstRequest.Code!.Text : $"{firstRequest.Request} {firstRequest.Text}";
        return new Latency(fileName, since(started), (firstRequestTime.transfers, firstRequestTime.ms, firstRequestText),
            (firstCodeTime.transfers, firstCodeTime.ms, firstCode.Code!.Text), (emergencyStop is not null) ? since(emergencyStop) : null);
    }

    /// <summary>
    /// Channels that the load keeps busy, with the code each of them waits in
    /// </summary>
    private static readonly CodeChannel[] BusyChannels = [CodeChannel.File, CodeChannel.HTTP, CodeChannel.Daemon, CodeChannel.SBC, CodeChannel.Telnet];

    private Task<string>? _httpCode, _sbcCode;

    /// <summary>
    /// Keep the other channels busy: a running job on File, a long code on HTTP, daemon.g on Daemon, a macro on SBC and a macro that
    /// trigger5.g started on Telnet with M98.4. Each of them waits in M400 until it is released
    /// </summary>
    private async Task StartLoadAsync()
    {
        Hold(BusyChannels);

        System.IO.File.WriteAllText(Path.Combine(_host.BaseDirectory, "gcodes", "job.g"), "G1 X1 F6000\nM400\n" + string.Concat(Enumerable.Range(2, 40).Select(i => $"G1 X{i} Y{i}\n")));
        Assert.That(await _host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        _httpCode = _host.StartCode("M400", CodeChannel.HTTP);
        _host.WriteMacro("daemon.g", "M400\n");
        _host.Firmware.RequestMacro(CodeChannel.Daemon, "daemon.g");
        _host.WriteMacro("sbc-macro.g", "M400\n");
        _sbcCode = _host.StartCode("M98 P\"sbc-macro.g\"", CodeChannel.SBC);
        _host.WriteMacro("telnet-macro.g", "M400\n");
        _host.WriteMacro("trigger5.g", Numbered("M98.4 P\"telnet-macro.g\""));
        _host.Firmware.FireTrigger(5);

        AssertWaitUntil(firmware => BusyChannels.All(channel => firmware.GetExecutedCodes(channel).Any(code => code.Text == "M400")), "Not all channels are busy");
        await WaitForTriggerChannelIdleAsync();
    }

    /// <summary>
    /// Whether the channels of the load are still waiting in their M400
    /// </summary>
    private bool LoadIsBusy() => BusyChannels.All(channel => _host.Firmware.GetCurrentCode(channel)?.Text == "M400") &&
        _httpCode?.IsCompleted == false && _sbcCode?.IsCompleted == false;

    private async Task StopLoadAsync()
    {
        Release(BusyChannels);
        Assert.That(await _httpCode!.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
        Assert.That(await _sbcCode!.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
        AssertWaitUntil(firmware => BusyChannels.All(firmware.IsIdle) && firmware.GetExecutedCodes(CodeChannel.File).Any(code => code.Text == "G1 X41 Y41"), "The load did not finish");
    }

    /// <summary>
    /// Stand-in for trigger4.g: lines 1 to 9 as on the printer (comments shortened), then neutral codes for the part that was not read
    /// </summary>
    private static string Trigger4 => Numbered(";", ";", ";", ";", ";", ";", string.Empty,
        "if global.debug",
        "  echo \"trigger4 executed\"",
        string.Empty,
        "G4 P401",
        "G4 P402");

    /// <summary>
    /// Stand-in for trigger9.g: line 13 as on the printer with both doors closed, so its block is skipped, then neutral codes
    /// </summary>
    private static string Trigger9 => Numbered(";", ";", ";", ";", ";", ";", ";", ";", ";", ";", ";", ";",
        "if sensors.gpIn[2].value == 0 || sensors.gpIn[3].value == 0",
        "  var run_on = 0",
        "G4 P901",
        "G4 P902");

    /// <summary>
    /// Values that the stand-ins read: global.debug and the door inputs (closed, value 1)
    /// </summary>
    private async Task SeedMachineStateAsync()
    {
        Assert.That(await _host.ExecuteAsync("global debug = false"), Is.EqualTo("\n"));
        _host.Firmware.ObjectModel.Set("sensors", JsonNode.Parse("""{ "gpIn": [ { "value": 1 }, { "value": 1 }, { "value": 1 }, { "value": 1 } ] }"""));
    }

    [Test]
    public async Task DoorTriggersWhileOtherChannelsAreBusy()
    {
        await SeedMachineStateAsync();
        _host.WriteMacro("trigger4.g", Trigger4);
        _host.WriteMacro("trigger9.g", Trigger9);

        // Without load, for comparison
        List<Latency> latencies = [];
        foreach (int trigger in new[] { 4, 9 })
        {
            _host.Firmware.FireTrigger(trigger);
            await WaitForTriggerChannelIdleAsync();
            latencies.Add(Measure($"trigger{trigger}.g") with { Macro = $"trigger{trigger}.g idle" });
        }

        await StartLoadAsync();
        for (int run = 0; run < 3; run++)
        {
            foreach (int trigger in new[] { 4, 9 })
            {
                int before = Executed(CodeChannel.Trigger).Length;
                _host.Firmware.FireTrigger(trigger);
                await WaitForTriggerChannelIdleAsync();

                // The trigger macro runs to its end on its own channel while every other channel still waits
                Assert.That(Executed(CodeChannel.Trigger)[before..], Is.EqualTo(new[] { $"G4 P{trigger}01", $"G4 P{trigger}02" }));
                Assert.That(Requests(SbcRequest.MacroCompleted, CodeChannel.Trigger).Last().Flag, Is.False);
                Assert.That(LoadIsBusy(), Is.True, "A channel of the load finished early");
                latencies.Add(Measure($"trigger{trigger}.g") with { Macro = $"trigger{trigger}.g busy run {run + 1}" });
            }
        }
        await StopLoadAsync();

        foreach (Latency latency in latencies)
        {
            TestContext.Progress.WriteLine(latency);
        }

        // DSF answers the macro request in the next transfer whatever the other channels do (LinkService.cs:377-405, Manager.cs:72-91,
        // Processor.cs:900-907). How long the codes take depends on the thread pool and is only reported
        Assert.That(latencies.Select(latency => latency.Started.transfers), Is.All.EqualTo(1));
    }

    [Test]
    public async Task TriggerMacroGetsSpareCodeBufferRoom()
    {
        // The job waits (e.g. M116 at its start) and daemon.g waits (e.g. G4 in its loop) while the codes they have read ahead fill RRF's code
        // buffer, which all channels share (SbcInterface.cpp:76, 404-412, FakeFirmware.StoreCode). DSF keeps room for two codes of maximum size
        // "for time-critical codes on other channels" (LinkInterface.SpareBufferSpace): codes read ahead must leave it free, only the next code
        // of a channel without buffered codes may use it (LinkInterface.SendCode, Processor.BufferCode). Before, the share of the job streams
        // applied to every channel, so daemon.g could take that room
        await SeedMachineStateAsync();
        WriteEmergencyStopMacros();
        Hold(CodeChannel.File, CodeChannel.Daemon);
        System.IO.File.WriteAllText(Path.Combine(_host.BaseDirectory, "gcodes", "job.g"), "M400\n" + string.Concat(Enumerable.Repeat($"M4242 S\"{new string('x', 200)}\"\n", 40)));
        _host.WriteMacro("daemon.g", "M400\n" + string.Concat(Enumerable.Range(10, 15).Select(i => $"M4242 S\"daemon line {i} ...................\"\n")));
        Assert.That(await _host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        _host.Firmware.RequestMacro(CodeChannel.Daemon, "daemon.g");
        AssertWaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.File)?.Text == "M400" && firmware.GetCurrentCode(CodeChannel.Daemon)?.Text == "M400", "The job and daemon.g do not wait");
        AssertWaitUntil(firmware => firmware.GetReceivedCodes(CodeChannel.Daemon).Count > 1, "daemon.g did not read ahead");

        _host.Firmware.FireTrigger(2);
        bool stopped = _host.Firmware.WaitUntil(firmware => firmware.IsEmergencyStopped, TimeSpan.FromSeconds(2));
        ushort space = _host.Firmware.GetSentPackets().Last(packet => packet.Request == Request.CodeBufferUpdate).BufferSpace;
        string state = $"code buffer space {space} bytes, buffered File {_host.Firmware.GetReceivedCodes(CodeChannel.File).Count - 1}, Daemon {_host.Firmware.GetReceivedCodes(CodeChannel.Daemon).Count - 1}, Trigger {_host.Firmware.GetReceivedCodes(CodeChannel.Trigger).Count}";
        Release(CodeChannel.File, CodeChannel.Daemon);

        Assert.That(stopped, Is.True, $"trigger2.g did not stop the machine while the job and daemon.g waited: {state}");
    }

    /// <summary>
    /// Stand-in for trigger2.g: lines 6 to 9 as on the printer. set-led-color.g is a neutral stand-in of three codes because it was not read
    /// </summary>
    private void WriteEmergencyStopMacros()
    {
        _host.WriteMacro("trigger2.g", Numbered(";", ";", ";", ";", ";",
            "if exists(global.self_test_estop_state) && ((\"\" ^ global.self_test_estop_state) == \"1\" || (\"\" ^ global.self_test_estop_state) == \"3\")",
            "  M98 P\"0:/sys/meltingplot/self-test/estop-fired.g\"",
            "M98 P\"0:/sys/meltingplot/lib/set-led-color.g\" C\"yellow\"",
            "M112",
            string.Empty,
            "; end"));
        WriteSysFile("meltingplot/self-test/estop-fired.g", "G4 P271\n");
        WriteSysFile("meltingplot/lib/set-led-color.g", "M150 R255 U255 B0 S1\nM150 R255 U255 B0 S1 E1\nG4 P281\n");
    }

    [Test]
    public async Task EmergencyStopTriggerWhileOtherChannelsAreBusy()
    {
        await SeedMachineStateAsync();
        WriteEmergencyStopMacros();
        await StartLoadAsync();
        _host.Firmware.FireTrigger(2);
        AssertWaitUntil(firmware => firmware.IsEmergencyStopped, "trigger2.g did not stop the machine");

        // The self test is not armed, so estop-fired.g is skipped. M112 follows set-led-color.g, which DSF runs to its end first
        Assert.That(Executed(CodeChannel.Trigger), Is.EqualTo(new[] { "M98 P\"0:/sys/meltingplot/lib/set-led-color.g\" C\"yellow\"", "M150 R255 U255 B0 S1", "M150 R255 U255 B0 S1 E1", "G4 P281" }));
        Latency latency = Measure("trigger2.g");
        TestContext.Progress.WriteLine(latency with { Macro = "trigger2.g busy" });
        Assert.That(latency.Started.transfers, Is.EqualTo(1));
        Assert.That(latency.EmergencyStop, Is.Not.Null);
        Release(BusyChannels);
    }
    #endregion
}
