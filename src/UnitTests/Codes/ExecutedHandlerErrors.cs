using System;
using System.Threading.Tasks;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Codes;

/// <summary>
/// Codes whose reply handler in DCS (MCodeHandler.CodeExecutedAsync) throws after the firmware has replied, found by GCodeExecutionFuzz.
/// The Executed stage does not catch the exception (Executed.cs:76-97), PipelineStackItem only logs it (PipelineStackItem.cs:85-88),
/// so the code is never finished and its client waits forever
/// </summary>
[TestFixture]
public class ExecutedHandlerErrors
{
    private FakeFirmwareHost _host = null!;

    [SetUp]
    public async Task SetUp() => _host = await FakeFirmwareHost.StartAsync();

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    /// <summary>
    /// Execute a code and fail if there is no reply
    /// </summary>
    private async Task<string> ExecuteAsync(string code)
    {
        try
        {
            return await _host.ExecuteAsync(code, timeout: TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            Assert.Fail($"{code} got no reply");
            throw;
        }
    }

    [Test, Explicit("M122 with an expression parameter never gets a reply: CodeExecutedAsync reads P with GetInt (MCodeHandler.cs:1432), which throws for the unevaluated expression")]
    public async Task DiagnosticsWithExpressionParameter()
    {
        // RRF evaluates P with GetIValue and prints the normal diagnostics for 0 (GCodes2.cpp:2291, 2313). DCS leaves the expression to RRF
        // because it holds no SBC field (Code.cs:242) and MCodeHandler.ProcessAsync does not evaluate it for M122 (MCodeHandler.cs:789-795)
        Assert.That(await ExecuteAsync("M122 P{0}"), Is.EqualTo("\n"));
    }

    [Test]
    public async Task DiagnosticsWithNumber() => Assert.That(await ExecuteAsync("M122 P0"), Is.EqualTo("\n"));
}
