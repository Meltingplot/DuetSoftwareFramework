using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Codes;
using DuetControlServer.Codes.Handlers;
using DuetControlServer.Commands;
using DuetControlServer.Files;
using DuetControlServer.Link;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Code = DuetControlServer.Commands.Code;

namespace UnitTests.Codes;

/// <summary>
/// Tests for M581 and M581.1 with triggers that RRF or DSF evaluates
/// </summary>
[TestFixture]
public class SbcTriggers
{
    private const int TriggerNumber = 30;

    // DSF evaluates this expression itself because it contains an SBC field, and its value is a constant, so no firmware is needed
    private const string SbcExpression = "sbc.dsf.pluginSupport";

    private static CodeParameter TParameter => new('T', TriggerNumber);

    private ServiceProvider _provider = null!;
    private SbcTriggerService _triggers = null!;

    [SetUp]
    public void SetUp()
    {
        ServiceCollection services = new();
        services
            .AddLogging()
            .AddSingleton(Options.Create(new Settings()))
            .AddSingleton<IHostApplicationLifetime, ApplicationLifetime>()
            .AddSingleton(DispatchProxy.Create<ILinkAdapter, UnitTests.SPI.ChannelProcessor.NullLinkAdapter>())
            .AddSingleton<LinkInterface>()
            .AddCodes()
            .AddCommands()
            .AddFiles()
            .AddLink()
            .AddModel()
            .AddUtility();
        _provider = services.BuildServiceProvider();
        _triggers = _provider.GetRequiredService<SbcTriggerService>();
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    private Code MakeM581(int minorNumber, params CodeParameter[] parameters)
    {
        Code code = _provider.GetRequiredService<CodeFactory>().Create();
        code.Type = CodeType.MCode;
        code.MajorNumber = 581;
        code.MinorNumber = minorNumber;
        code.Parameters = [.. parameters];
        return code;
    }

    /// <summary>
    /// Register a trigger that DSF evaluates
    /// </summary>
    /// <remarks>
    /// M581.1 registers such a trigger only after RRF has cleared the slot, which needs a firmware, so the trigger state is added directly
    /// </remarks>
    private void AddSbcTrigger(int condition)
    {
        Type stateType = typeof(SbcTriggerService).GetNestedType("TriggerState", BindingFlags.NonPublic)!;
        object state = Activator.CreateInstance(stateType, nonPublic: true)!;
        stateType.GetProperty("Expression")!.SetValue(state, SbcExpression);
        stateType.GetProperty("WatchedPaths")!.SetValue(state, new[] { SbcExpression });
        stateType.GetProperty("Condition")!.SetValue(state, condition);

        IDictionary triggers = (IDictionary)typeof(SbcTriggerService).GetField("_triggers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_triggers)!;
        triggers[TriggerNumber] = state;
    }

    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

    private Task<Message?> ConfigureAsync(params CodeParameter[] parameters) => _triggers.ConfigureAsync(MakeM581(1, parameters), Timeout);

    private async Task<Message?> ProcessM581Async(params CodeParameter[] parameters) =>
        await _provider.GetRequiredKeyedService<ICodeHandler>(Keys.MCodes).ProcessAsync(MakeM581(0, parameters), Timeout);

    [Test]
    public async Task DeleteIsPassedToFirmware()
    {
        // RRF evaluates triggers without SBC fields itself, so M581.1 P-1 must reach it like plain M581 P-1 does
        Assert.That(await ConfigureAsync(TParameter, new('P', -1)), Is.Null);
    }

    [TestCase(-1)]
    [TestCase(32)]
    public async Task InvalidTriggerNumberIsPassedToFirmware(int triggerNumber)
    {
        // RRF reports the error for both codes, its trigger numbers are never more than DSF's
        Assert.That(await ConfigureAsync(new CodeParameter('T', triggerNumber), new('P', SbcExpression)), Is.Null);
        Assert.That(await ProcessM581Async(new CodeParameter('T', triggerNumber)), Is.Null, "plain M581");
    }

    [Test]
    public async Task ReportOfFirmwareTriggerIsPassedToFirmware()
    {
        Assert.That(await ConfigureAsync(TParameter), Is.Null);
        Assert.That(await ProcessM581Async(TParameter), Is.Null);
    }

    [Test]
    public async Task ConditionOfFirmwareTriggerIsPassedToFirmware()
    {
        Assert.That(await ConfigureAsync(TParameter, new('R', 1)), Is.Null);
        Assert.That(await ProcessM581Async(TParameter, new('R', 1)), Is.Null);
    }

    // Same wording as RRF's TriggerItem::Configure
    [TestCase(0, "Trigger 30 fires when expression {sbc.dsf.pluginSupport} becomes true")]
    [TestCase(1, "Trigger 30 fires only when printing and expression {sbc.dsf.pluginSupport} becomes true")]
    [TestCase(2, "Trigger 30 fires only when not printing and expression {sbc.dsf.pluginSupport} becomes true")]
    [TestCase(-1, "Trigger 30 if enabled would fire when expression {sbc.dsf.pluginSupport} becomes true")]
    public async Task ReportSbcTrigger(int condition, string report)
    {
        AddSbcTrigger(condition);
        Assert.That((await ConfigureAsync(TParameter))?.Content, Is.EqualTo(report));
        Assert.That((await ProcessM581Async(TParameter))?.Content, Is.EqualTo(report), "plain M581");
    }

    [Test]
    public async Task ChangeConditionOfSbcTrigger()
    {
        AddSbcTrigger(0);

        Message? result = await ConfigureAsync(TParameter, new('R', 2));
        Assert.That(result?.Type, Is.EqualTo(MessageType.Success));
        Assert.That((await ConfigureAsync(TParameter))?.Content, Does.StartWith("Trigger 30 fires only when not printing and"));

        result = await ProcessM581Async(TParameter, new('R', -1));
        Assert.That(result?.Type, Is.EqualTo(MessageType.Success), "plain M581");
        Assert.That((await ConfigureAsync(TParameter))?.Content, Does.StartWith("Trigger 30 if enabled would fire when"));
    }

    [Test]
    public async Task PlainM581WithInputsReplacesSbcTrigger()
    {
        AddSbcTrigger(0);

        // RRF configures its own trigger for these inputs, so DSF must stop evaluating its expression
        Assert.That(await ProcessM581Async(TParameter, new('P', "io1.in"), new('S', 1)), Is.Null);
        Assert.That(await ConfigureAsync(TParameter), Is.Null, "DSF still reports the trigger as its own");
    }

    [Test]
    public async Task FirmwareExpressionReplacesSbcTrigger()
    {
        AddSbcTrigger(0);

        // RRF evaluates an expression without SBC fields itself, so DSF must stop evaluating its own like for plain M581 with inputs
        Assert.That(await ConfigureAsync(TParameter, new('P', "sensors.gpIn[0].value = 1")), Is.Null);
        Assert.That(await ConfigureAsync(TParameter), Is.Null, "DSF still reports the trigger as its own");
    }

    [TestCase(-1, ExpectedResult = false)]
    [TestCase(0, ExpectedResult = true)]
    public async Task<bool> EnableCondition(int condition)
    {
        // R-1 disables a trigger like in RRF; the printer is not printing here, so R2 would fire as well
        MethodInfo isConditionMet = typeof(SbcTriggerService).GetMethod("IsTriggerConditionMetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return await (Task<bool>)isConditionMet.Invoke(_triggers, [condition, Timeout])!;
    }
}
