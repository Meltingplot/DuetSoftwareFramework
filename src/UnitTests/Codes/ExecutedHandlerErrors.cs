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

    [Test, Explicit("M122 P{0} never gets a reply and blocks its channel: CodeExecutedAsync reads P with GetInt (MCodeHandler.cs:1432), which throws for the unevaluated expression")]
    public async Task DiagnosticsWithExpressionParameter()
    {
        // RRF evaluates P with GetIValue and prints the normal diagnostics for 0 (GCodes2.cpp:2291, 2313). DCS leaves the expression to RRF
        // because it holds no SBC field (Code.cs:242) and MCodeHandler.ProcessAsync does not evaluate it for M122 (MCodeHandler.cs:789-795).
        // Confirmed on the lab printer (RRF 3.7.0+1.mp.8, DSF 3.7.0+mp.13): no reply, the DCS log shows "Failed to process code in stage Executed"
        // with this InvalidParameterTypeException, DWS answered with HTTP 503, and the next regular code on the HTTP channel got no reply either,
        // because the unfinished M122 keeps the unbuffered code counter of the channel (CodeProcessor.cs:188-191, Start.cs:224-232)
        Task<string> diagnostics = _host.StartCode("M122 P{0}");
        Task<string> echo = _host.StartCode("echo \"alive\"");
        bool diagnosticsReplied = await Task.WhenAny(diagnostics, Task.Delay(TimeSpan.FromSeconds(3))) == diagnostics;
        bool echoReplied = await Task.WhenAny(echo, Task.Delay(TimeSpan.FromSeconds(3))) == echo;
        Assert.Multiple(() =>
        {
            Assert.That(diagnosticsReplied, Is.True, "M122 P{0} got no reply");
            Assert.That(echoReplied, Is.True, "the channel is blocked after M122 P{0}");
        });
    }

    [Test]
    public async Task DiagnosticsWithNumber() => Assert.That(await ExecuteAsync("M122 P0"), Is.EqualTo("\n"));

    [Test]
    public async Task DiagnosticsWithExpressionBoard()
    {
        // B is read before the code goes to RRF (MCodeHandler.cs:790), so the exception comes up in the internal processing stage, which
        // turns it into an error reply (Code.cs:276-281) prefixed with the command (Executed.cs:103-106). Same reply on the lab printer
        Assert.That(await ExecuteAsync("M122 B{0}"), Is.EqualTo("Error: M122: Cannot convert B parameter to Int32 (value {0})\n"));
        Assert.That(await ExecuteAsync("echo \"alive\""), Is.EqualTo("alive\n"));
    }
}
