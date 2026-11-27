using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// M581 K (upstream RRF 3.7.1) together with the fork's action lines of M581.1 (U), RRF fix of the sync of 2026-11-27
/// (Meltingplot/RepRapFirmware, "Fixed M581 K breaking the M112 action line and the lock of triggers")
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Without K, trigger 0 is an emergency stop, trigger 1 pauses and all others run their macro (wiki <c>Gcodes.md</c>, M581).
/// An action line that is just M112 makes a trigger that runs its macro an emergency stop (RRF <c>TriggerItem::GetAction</c>).</item>
/// <item>Only a trigger that runs its macro file can have an action line (RRF <c>TriggerItem::Configure</c>).</item>
/// <item>The tests only configure the unused trigger 30 without an expression that can become true, so nothing fires, and they delete it
/// again with P-1. A trigger stays unlocked while it watches no input and has no expression, so P-1 always works here.
/// The lock and the firing are checked offline by <c>Tools/trigger-action-test</c> in RepRapFirmware.</item>
/// <item>Firmware without K reports a trigger without "(K"; the tests are then ignored.</item>
/// </list>
/// </remarks>
[TestFixture, Category("Trigger")]
public class TriggerActions : LabTest
{
    private const int TriggerNumber = 30;

    /// <summary>
    /// Check that the test trigger is free and that RRF knows K
    /// </summary>
    [SetUp]
    public async Task CheckTriggerAsync()
    {
        string report = await SendCodeAsync($"M581 T{TriggerNumber}");
        Assert.That(report, Does.Contain("is not configured"), $"Trigger {TriggerNumber} is in use");
        if (!report.Contains("(K"))
        {
            Assert.Ignore($"RRF does not know M581 K: {report.Trim()}");
        }
    }

    /// <summary>
    /// Delete the test trigger, which also resets its K action. This runs after an ignored test too, so it does not rely on K
    /// </summary>
    [TearDown]
    public async Task DeleteTriggerAsync()
    {
        await SendCodeAsync($"M581 T{TriggerNumber} P-1");
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain("is not configured"));
    }

    /// <summary>
    /// Without K a trigger above 1 runs its macro
    /// </summary>
    [Test]
    public async Task TriggerReportsItsAction()
    {
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain($"Trigger {TriggerNumber} (K2, run macro) is not configured"));
    }

    /// <summary>
    /// A trigger that does not run its macro file cannot have an action line. Before the fix, only triggers 0 and 1 refused one
    /// </summary>
    [Test]
    public async Task ActionLineIsRefusedForFeedHoldTrigger()
    {
        string reply = await SendCodeAsync($"M581.1 T{TriggerNumber} K3 P\"state.upTime < 0\" U\";\"");
        Assert.That(reply, Does.Contain($"Trigger {TriggerNumber} does not run a macro file, so it cannot have an action"));
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain("is not configured"), "The refused M581.1 configured the trigger");
    }

    /// <summary>
    /// An action line that is just M112 makes the trigger an emergency stop, which the report shows as K0. Before the fix it said K2
    /// and the trigger fired as trigger 0, whatever action trigger 0 had
    /// </summary>
    [Test]
    public async Task M112ActionLineIsReportedAsEmergencyStop()
    {
        Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} U\"M112\""), Does.Not.Contain("Error"));
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain($"Trigger {TriggerNumber} (K0, emergency stop) is not configured"));
    }

    /// <summary>
    /// A K other than K2 deletes the action line, so the trigger does what K says
    /// </summary>
    [Test]
    public async Task FeedHoldActionDeletesActionLine()
    {
        Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} U\"M112\""), Does.Not.Contain("Error"));
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber} K3"), Does.Not.Contain("Error"));
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain($"Trigger {TriggerNumber} (K3, feed hold) is not configured"));

        // P-1 resets the action to the default (wiki Gcodes.md, M581)
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber} P-1"), Does.Not.Contain("Error"));
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain($"Trigger {TriggerNumber} (K2, run macro) is not configured"));
    }
}
