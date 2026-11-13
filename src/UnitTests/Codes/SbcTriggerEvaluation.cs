using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Model;
using Microsoft.Extensions.DependencyInjection;
using Nito.AsyncEx;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Codes;

/// <summary>
/// M581.1 triggers whose expressions use SBC fields, which DSF evaluates itself (SbcTriggerService) because RRF cannot.
/// DSF watches the object model for changes of the fields of the expression and fires a trigger with M582 T<n> S1 on the trigger channel, so that RRF starts sys/triggerN.g
/// when the expression changes from false to true and the enable condition (R) is met, like RRF's TriggerItem::Check (TriggerItem.cpp:113-156)
/// </summary>
[TestFixture]
public class SbcTriggerEvaluation
{
    /// <summary>
    /// Triggers on another field that tell when the evaluation loop has got past the changes before them: the first fires when the field
    /// becomes low, the second when it becomes high
    /// </summary>
    private const int SentinelLow = 28, SentinelHigh = 29;

    private FakeFirmwareHost _host = null!;
    private int _sentinelLowRuns, _sentinelHighRuns;
    private bool _sentinelHigh;

    [SetUp]
    public async Task SetUp()
    {
        _host = await FakeFirmwareHost.StartAsync();
        _sentinelLowRuns = _sentinelHighRuns = 0;
        _sentinelHigh = false;
        foreach (int trigger in new[] { 20, 21, 22, 23, SentinelLow, SentinelHigh })
        {
            _host.WriteMacro($"trigger{trigger}.g", $"G4 P{trigger}01\n");
        }
        await SetAsync(avgLoad: 0.1F, temperature: 40F);
    }

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    /// <summary>
    /// Change the SBC fields the test triggers use. PeriodicUpdateService, which sets them in DCS, does not run in the fake firmware host
    /// </summary>
    private async Task SetAsync(float? avgLoad = null, float? temperature = null)
    {
        using (await _host.Model.AccessReadWriteAsync())
        {
            if (avgLoad is not null)
            {
                _host.Model.SBC!.CPU.AvgLoad = avgLoad;
            }
            if (temperature is not null)
            {
                _host.Model.SBC!.CPU.Temperature = temperature;
            }
        }
    }

    /// <summary>
    /// Number of times DSF fired a trigger. SbcTriggerService sends M582 T<n> S1 on the trigger channel, and RRF starts the macro (SbcTriggerService.cs:300-323).
    /// The executed codes count, because RRF discards the codes it has buffered when a macro starts and DSF sends them again
    /// </summary>
    private int Runs(int trigger) => _host.Firmware.GetExecutedCodes(CodeChannel.Trigger).Count(code => code.Text == $"M582 T{trigger} S1");

    /// <summary>
    /// Wait until DSF has fired a trigger the given number of times and the code of its macro has run
    /// </summary>
    private void WaitForRuns(int trigger, int runs) =>
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Trigger).Count(code => code.Text == $"M582 T{trigger} S1") >= runs &&
            firmware.GetExecutedCodes(CodeChannel.Trigger).Count(code => code.Text == $"G4 P{trigger}01") >= runs, FakeFirmwareHost.DefaultTimeout), Is.True, $"trigger{trigger}.g did not run {runs} times");

    /// <summary>
    /// Register the sentinel triggers last. The evaluation loop checks a snapshot of the triggers in the order of the dictionary
    /// (SbcTriggerService.cs:276-283), which is the order of registration while nothing has been removed
    /// </summary>
    private async Task RegisterSentinelAsync()
    {
        Assert.That(await _host.ExecuteAsync($"M581.1 T{SentinelLow} P\"sbc.cpu.temperature < 50\""), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync($"M581.1 T{SentinelHigh} P\"sbc.cpu.temperature > 50\""), Is.EqualTo("\n"));
    }

    /// <summary>
    /// Let a sentinel fire and wait for its macro. A trigger that fires wrongly on an earlier change has sent its M582 on the same channel before.
    /// The loop evaluates the expressions when it gets to them, so a change that is undone before is not seen; that is why the sentinels take turns
    /// instead of one of them going high and low again
    /// </summary>
    private async Task PassSentinelAsync()
    {
        _sentinelHigh = !_sentinelHigh;
        await SetAsync(temperature: _sentinelHigh ? 60F : 40F);
        if (_sentinelHigh)
        {
            WaitForRuns(SentinelHigh, ++_sentinelHighRuns);
        }
        else
        {
            WaitForRuns(SentinelLow, ++_sentinelLowRuns);
        }
    }

    [Test]
    public async Task RegisterReportAndChangeCondition()
    {
        Assert.That(await _host.ExecuteAsync("M581.1 T20 P\"sbc.cpu.avgLoad > 0.5\""), Is.EqualTo("\n"));

        // DSF clears the trigger in RRF with M581 P-1 on the same channel and keeps M581.1 to itself (SbcTriggerService.cs:133-140)
        Assert.That(_host.Firmware.GetReceivedCodes().Select(code => code.Text), Is.EqualTo(new[] { "M581 T20 P-1" }));
        Assert.That(_host.Firmware.GetReceivedCodes().Single().Channel, Is.EqualTo(CodeChannel.HTTP));

        // Reports and condition changes answer like RRF's TriggerItem::Configure for both codes (wiki M581.1 notes, TriggerItem.cpp:343-388)
        Assert.That(await _host.ExecuteAsync("M581.1 T20"), Is.EqualTo("Trigger 20 fires when expression {sbc.cpu.avgLoad > 0.5} becomes true\n"));
        Assert.That(await _host.ExecuteAsync("M581 T20 R1"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M581 T20"), Is.EqualTo("Trigger 20 fires only when printing and expression {sbc.cpu.avgLoad > 0.5} becomes true\n"));
        Assert.That(await _host.ExecuteAsync("M581.1 T20 R-1"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M581.1 T20"), Is.EqualTo("Trigger 20 if enabled would fire when expression {sbc.cpu.avgLoad > 0.5} becomes true\n"));
        Assert.That(_host.Firmware.GetReceivedCodes(), Has.Count.EqualTo(1));

        // R given with the expression
        Assert.That(await _host.ExecuteAsync("M581.1 T21 P\"sbc.cpu.avgLoad > 0.5\" R2"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M581.1 T21"), Is.EqualTo("Trigger 21 fires only when not printing and expression {sbc.cpu.avgLoad > 0.5} becomes true\n"));
    }

    [Test]
    public async Task FiresWhenTheExpressionBecomesTrue()
    {
        // Two triggers on the same field
        Assert.That(await _host.ExecuteAsync("M581.1 T20 P\"sbc.cpu.avgLoad > 0.5\" R0"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M581.1 T21 P\"sbc.cpu.avgLoad > 0.8\" R0"), Is.EqualTo("\n"));
        await RegisterSentinelAsync();

        await SetAsync(avgLoad: 0.6F);
        WaitForRuns(20, 1);
        await PassSentinelAsync();
        Assert.That((Runs(20), Runs(21)), Is.EqualTo((1, 0)));

        // T20 stays true and does not fire again, T21 becomes true
        await SetAsync(avgLoad: 0.9F);
        WaitForRuns(21, 1);
        await PassSentinelAsync();
        Assert.That((Runs(20), Runs(21)), Is.EqualTo((1, 1)));

        // Both stay true
        await SetAsync(avgLoad: 0.95F);
        await PassSentinelAsync();
        Assert.That((Runs(20), Runs(21)), Is.EqualTo((1, 1)));

        // False, then true again: both fire
        await SetAsync(avgLoad: 0.1F);
        await PassSentinelAsync();
        await SetAsync(avgLoad: 0.9F);
        WaitForRuns(20, 2);
        WaitForRuns(21, 2);
        await PassSentinelAsync();
        Assert.That((Runs(20), Runs(21)), Is.EqualTo((2, 2)));

        // The sentinels watch another field, the changes of avgLoad never made them fire (only PassSentinelAsync does)
        Assert.That((Runs(SentinelLow), Runs(SentinelHigh)), Is.EqualTo((_sentinelLowRuns, _sentinelHighRuns)));

        // The trigger macros run on the trigger channel
        Assert.That(_host.Firmware.GetExecutedCodes(CodeChannel.Trigger).Count(code => code.Text == "G4 P2001"), Is.EqualTo(2));
        Assert.That(_host.Firmware.GetReceivedCodes().Where(code => code.Channel != CodeChannel.Trigger).Select(code => code.Text), Has.None.StartWith("M98").And.None.StartWith("M582"));
    }

    [Test]
    public async Task ExpressionThatIsTrueAlreadyDoesNotFire()
    {
        // The initial value of the expression is determined when the trigger is created and when R changes, so neither fires it
        // (SbcTriggerService.cs:142-144, 181-190, like TriggerItem.cpp:323-332)
        await SetAsync(avgLoad: 0.9F);
        Assert.That(await _host.ExecuteAsync("M581.1 T20 P\"sbc.cpu.avgLoad > 0.5\""), Is.EqualTo("\n"));
        await RegisterSentinelAsync();

        await SetAsync(avgLoad: 0.95F);
        await PassSentinelAsync();
        Assert.That(await _host.ExecuteAsync("M581.1 T20 R0"), Is.EqualTo("\n"));
        await SetAsync(avgLoad: 0.96F);
        await PassSentinelAsync();
        Assert.That(Runs(20), Is.Zero);

        await SetAsync(avgLoad: 0.1F);
        await PassSentinelAsync();
        await SetAsync(avgLoad: 0.9F);
        WaitForRuns(20, 1);
    }

    [Test]
    public async Task EnableConditions()
    {
        // R1 fires only while printing, R2 only while not printing, R-1 never (wiki M581.1, SbcTriggerService.cs:328-347)
        Assert.That(await _host.ExecuteAsync("M581.1 T20 P\"sbc.cpu.avgLoad > 0.5\" R1"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M581.1 T21 P\"sbc.cpu.avgLoad > 0.5\" R2"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M581.1 T22 P\"sbc.cpu.avgLoad > 0.5\" R-1"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M581.1 T23 P\"sbc.cpu.avgLoad > 0.5\" R0"), Is.EqualTo("\n"));
        await RegisterSentinelAsync();

        // Not printing
        await SetAsync(avgLoad: 0.9F);
        WaitForRuns(23, 1);
        WaitForRuns(21, 1);
        await PassSentinelAsync();
        Assert.That((Runs(20), Runs(21), Runs(22), Runs(23)), Is.EqualTo((0, 1, 0, 1)));

        // Printing: RRF reports the status, the fake firmware keeps it once it is seeded
        _host.Firmware.ObjectModel.AutoStatus = false;
        _host.Firmware.ObjectModel.Set("state.status", "processing");
        using (CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout))
        {
            while (true)
            {
                using (await _host.Model.AccessReadOnlyAsync(cts.Token))
                {
                    if (_host.Model.State.Status == MachineStatus.Processing)
                    {
                        break;
                    }
                }
                await Task.Delay(5, cts.Token);
            }
        }
        await SetAsync(avgLoad: 0.1F);
        await PassSentinelAsync();
        await SetAsync(avgLoad: 0.9F);
        WaitForRuns(23, 2);
        WaitForRuns(20, 1);
        await PassSentinelAsync();
        Assert.That((Runs(20), Runs(21), Runs(22), Runs(23)), Is.EqualTo((1, 1, 0, 2)));
    }

    [Test]
    public async Task FirmwareTakesTheTriggerNumberBack()
    {
        // M581.1 P-1, M581 with inputs and M581.1 with an expression without SBC fields go to RRF, which takes the trigger number back
        // (wiki M581.1: "Using M581.1 to create an expression-based trigger deletes any trigger conditions previously configured by M581 on the same
        // trigger number, and vice versa", SbcTriggerService.cs:123-128)
        await RegisterSentinelAsync();
        foreach (int trigger in new[] { 20, 21, 22 })
        {
            Assert.That(await _host.ExecuteAsync($"M581.1 T{trigger} P\"sbc.cpu.avgLoad > 0.5\""), Is.EqualTo("\n"));
        }
        string[] takeOver = ["M581.1 T20 P-1", "M581 T21 P0 S1", "M581.1 T22 P\"global.x == 1\""];
        foreach (string code in takeOver)
        {
            Assert.That(await _host.ExecuteAsync(code), Is.EqualTo("\n"), code);
        }

        // Reports go to RRF as well now
        Assert.That(await _host.ExecuteAsync("M581.1 T20"), Is.EqualTo("\n"));
        string[] received = [.. _host.Firmware.GetReceivedCodes(CodeChannel.HTTP).Select(code => code.Text)];
        Assert.That(received[^4..], Is.EqualTo(takeOver.Append("M581.1 T20")));

        await SetAsync(avgLoad: 0.9F);
        await PassSentinelAsync();
        Assert.That((Runs(20), Runs(21), Runs(22)), Is.EqualTo((0, 0, 0)));
    }

    [Test]
    public async Task EvaluationErrorDoesNotStopTheOtherTriggers()
    {
        // The expression can be evaluated while avgLoad is low because || stops there, RRF evaluates the rest and fails at the unknown field
        // once avgLoad is high. An error must not stop the evaluation loop (SbcTriggerService.cs:315-318). What happens to the failing trigger
        // itself is the subject of ErrorInLaterEvaluationDisablesTheTrigger
        Assert.That(await _host.ExecuteAsync("M581.1 T20 P\"sbc.cpu.avgLoad < 0.5 || sbc.cpu.noSuchField == 1\""), Is.EqualTo("\n"));
        await RegisterSentinelAsync();

        await SetAsync(avgLoad: 0.9F);
        await PassSentinelAsync();
        await SetAsync(avgLoad: 0.95F);
        await PassSentinelAsync();
        Assert.That((Runs(SentinelLow), Runs(SentinelHigh)), Is.EqualTo((1, 1)));
    }

    [Test, Explicit("SbcTriggerService.cs:218-222 only logs an error of the initial evaluation, so M581.1 succeeds and creates the trigger (upstream 4229f552)")]
    public async Task ErrorInExpressionIsReported()
    {
        // Wiki M581.1: "If an error occurs while evaluating the expression during execution of M581.1 then an error message is returned and the
        // trigger is not created", like TriggerItem.cpp:328-339
        string reply = await _host.ExecuteAsync("M581.1 T20 P\"sbc.cpu.noSuchField == 1\"");
        string report = await _host.ExecuteAsync("M581.1 T20");

        Assert.That(reply, Does.StartWith("Error: "));
        Assert.That(report, Is.EqualTo("\n"), "DSF still reports the trigger as its own");
    }

    [Test, Explicit("SbcTriggerService.cs:315-318 only logs a later evaluation error, the trigger stays enabled and nobody is told (upstream 4229f552)")]
    public async Task ErrorInLaterEvaluationDisablesTheTrigger()
    {
        // Wiki M581.1: "If no error occurs while executing M581.1 but an error occurs while evaluating the expression subsequently, an error message
        // is reported to all input channels and the trigger is disabled", like TriggerItem.cpp:133-149 (condition = -1, "Trigger N disabled")
        Assert.That(await _host.ExecuteAsync("M581.1 T20 P\"sbc.cpu.avgLoad < 0.5 || sbc.cpu.noSuchField == 1\""), Is.EqualTo("\n"));
        await RegisterSentinelAsync();
        await SetAsync(avgLoad: 0.9F);
        await PassSentinelAsync();

        Assert.That(await _host.ExecuteAsync("M581.1 T20"), Does.StartWith("Trigger 20 if enabled would fire"));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.Messages.Any(message => message.Type == MessageType.Error && message.Content.Contains("Trigger 20 disabled")), Is.True);
        }
    }

    [Test]
    public async Task TriggerZeroIsAnEmergencyStop()
    {
        // Wiki M581: "Trigger number 0 causes an emergency stop as if M112 had been received ... T0 and T1 cannot be overridden with trigger files",
        // GCodes.cpp:994-998
        _host.WriteMacro("trigger0.g", "G4 P1\n");
        Assert.That(await _host.ExecuteAsync("M581.1 T0 P\"sbc.cpu.avgLoad > 0.5\""), Is.EqualTo("\n"));
        await SetAsync(avgLoad: 0.9F);

        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.IsEmergencyStopped, TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(_host.Firmware.GetExecutedCodes(CodeChannel.Trigger).Select(code => code.Text), Has.None.StartWith("M98").And.None.StartWith("M582"));
    }

    [Test, Explicit("DSF passes M582 on to RRF, whose trigger slot is empty, so a trigger that DSF evaluates is not checked (no M582 case in MCodeHandler.cs, SbcTriggerService.cs)")]
    public async Task M582ChecksTriggerOfDsf()
    {
        // Wiki M582: "You can use M582 regardless of whether the trigger was created using M581 or M581.1". RRF checks the level as if the
        // expression had just become true (TriggerItem::CheckLevel, TriggerItem.cpp:413-421)
        await SetAsync(avgLoad: 0.9F);
        Assert.That(await _host.ExecuteAsync("M581.1 T20 P\"sbc.cpu.avgLoad > 0.5\""), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M582 T20"), Is.EqualTo("\n"));

        WaitForRuns(20, 1);
    }
}

/// <summary>
/// Parts of SbcTriggerService that depend on the state of DCS only, tested without firmware and without the evaluation loop
/// </summary>
[TestFixture]
public class SbcTriggerServiceState
{
    private ServiceProvider _provider = null!;
    private SbcTriggerService _triggers = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = DcsServices.Build();
        _triggers = _provider.GetRequiredService<SbcTriggerService>();
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    private void AddTrigger(int number, params string[] watchedPaths)
    {
        Type stateType = typeof(SbcTriggerService).GetNestedType("TriggerState", BindingFlags.NonPublic)!;
        object state = Activator.CreateInstance(stateType, nonPublic: true)!;
        stateType.GetProperty("Expression")!.SetValue(state, "true");
        stateType.GetProperty("WatchedPaths")!.SetValue(state, watchedPaths);
        IDictionary triggers = (IDictionary)typeof(SbcTriggerService).GetField("_triggers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_triggers)!;
        triggers[number] = state;
    }

    /// <summary>
    /// Report a change like the Observer does and check whether the evaluation loop is woken up
    /// </summary>
    private bool Signals(params object[] path)
    {
        AsyncAutoResetEvent signal = (AsyncAutoResetEvent)typeof(SbcTriggerService).GetField("_signal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_triggers)!;
        typeof(SbcTriggerService).GetMethod("OnObservedPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_triggers, [path, PropertyChangeType.Property, null]);
        bool isSet = signal.IsSet;
        if (isSet)
        {
            // Taking the signal resets it, as the evaluation loop does
            signal.Wait();
        }
        return isSet;
    }

    [Test]
    public void ChangesOfWatchedFields()
    {
        // Paths are built from the property names and the names of item nodes (SbcTriggerService.cs:43-51) and compared without case
        // with the watched paths, their parents and their children (SbcTriggerService.cs:56-79)
        AddTrigger(20, "sbc.cpu.avgLoad", "network.interfaces.actualIP");

        Assert.Multiple(() =>
        {
            Assert.That(Signals("sbc", "cpu", "avgLoad"), Is.True, "same field");
            Assert.That(Signals("SBC", "CPU", "AVGLOAD"), Is.True, "other case");
            Assert.That(Signals("sbc", "cpu"), Is.True, "parent replaced");
            Assert.That(Signals("sbc", "cpu", "avgLoad", "value"), Is.True, "child");
            Assert.That(Signals("network", new ItemPathNode("interfaces", 0, new ArrayList()), "actualIP"), Is.True, "item of a list");
            Assert.That(Signals("sbc", "cpu", "temperature"), Is.False, "other field");
            Assert.That(Signals("sbc", "cpu", "avgLoadMax"), Is.False, "field with the same prefix");
            Assert.That(Signals("state", "upTime"), Is.False, "other key");
            Assert.That(Signals(), Is.False, "empty path");
            Assert.That(Signals(42), Is.False, "path without names");
        });
    }

    [Test]
    public void NothingToWatch()
    {
        Assert.That(Signals("sbc", "cpu", "avgLoad"), Is.False, "no trigger");
        AddTrigger(20);
        Assert.That(Signals("sbc", "cpu", "avgLoad"), Is.False, "trigger without fields");
    }

    // RRF fires R1 while PrintMonitor::IsPrinting and R2 otherwise (TriggerItem.cpp:153-156). That is true from StartedPrint to StoppedPrint
    // (GCodes.cpp:3993), and the status is pausing, resuming, paused, cancelling, simulating or processing then (RepRap::GetStatusIndex,
    // RepRap.cpp:1961-1968). Halted, off, starting and updating take priority over it, so RRF can be printing or not then; those are left out
    [TestCase(MachineStatus.Processing, 1, ExpectedResult = true)]
    [TestCase(MachineStatus.Pausing, 1, ExpectedResult = true)]
    [TestCase(MachineStatus.Paused, 1, ExpectedResult = true)]
    [TestCase(MachineStatus.Resuming, 1, ExpectedResult = true)]
    [TestCase(MachineStatus.Idle, 1, ExpectedResult = false)]
    [TestCase(MachineStatus.Busy, 1, ExpectedResult = false)]
    [TestCase(MachineStatus.ChangingTool, 1, ExpectedResult = false)]
    [TestCase(MachineStatus.Processing, 2, ExpectedResult = false)]
    [TestCase(MachineStatus.Paused, 2, ExpectedResult = false)]
    [TestCase(MachineStatus.Idle, 2, ExpectedResult = true)]
    [TestCase(MachineStatus.Busy, 2, ExpectedResult = true)]
    [TestCase(MachineStatus.ChangingTool, 2, ExpectedResult = true)]
    [TestCase(MachineStatus.Simulating, 1, ExpectedResult = true, Explicit = true,
        Reason = "SbcTriggerService.cs:341-344 does not count a simulation as printing, RRF does (PrintMonitor::IsPrinting, GCodes.cpp:3993)")]
    [TestCase(MachineStatus.Cancelling, 1, ExpectedResult = true, Explicit = true,
        Reason = "SbcTriggerService.cs:341-344 does not count cancelling a print as printing, RRF does (RepRap.cpp:1964)")]
    public async Task<bool> EnableConditionWhilePrinting(MachineStatus status, int condition)
    {
        DuetControlServer.Model.ObjectModel model = _provider.GetRequiredService<DuetControlServer.Model.ObjectModel>();
        using (await model.AccessReadWriteAsync())
        {
            model.State.Status = status;
        }

        MethodInfo isConditionMet = typeof(SbcTriggerService).GetMethod("IsTriggerConditionMetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return await (Task<bool>)isConditionMet.Invoke(_triggers, [condition, CancellationToken.None])!;
    }
}
