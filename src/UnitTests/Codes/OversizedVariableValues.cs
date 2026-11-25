using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer.Codes.Meta.Parsing;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Codes;

/// <summary>
/// Fork tests (Meltingplot): DSF refuses a variable value whose JSON RepRapFirmware could never send back, before the code goes to the
/// firmware, and its port of the firmware expression parser has the firmware's array limits.
/// </summary>
/// <remarks>
/// Lab printer 2026-11-24 (RRF 3.7.0+1.mp.9, DSF 3.7.0+mp.14): global m = vector(128,vector(128,vector(n,10))) with n of 21 and more made
/// RRF ask for the SetVariable request again after it had created the variable, so it failed with "variable 'global.m' already exists",
/// and for every n tested (20 included) the "global" object model key no longer fitted the output buffers, so DSF asked for it again and
/// again. The fake firmware does not emulate vector() (FakeExpressionEvaluator, "unknown function"), so the tests of values that DSF has
/// to forward check the request that DSF sends, not the reply.
/// </remarks>
[TestFixture]
public class OversizedVariableValues
{
    private const string TooLong = "Error: value too long to send to the SBC";

    private FakeFirmwareHost _host = null!;

    [SetUp]
    public async Task SetUp() => _host = await FakeFirmwareHost.StartAsync();

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    private bool SentSetVariable() => _host.Firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.SetVariable);

    private string? SentExpression(string varName) =>
        _host.Firmware.GetReceivedRequests().LastOrDefault(request => request.Request == SbcRequest.SetVariable && request.Text == varName)?.Argument;

    #region Refused
    // The first element of vector(128,vector(128,vector(n,10))) is 128 * (3n + 1) + 129 bytes of JSON, and the SetVariable reply holds it
    // whole: 7937 bytes for n = 20, 8321 for n = 21. A VariableResult packet carries at most 8192 - 8 - 8 - strlen(name) bytes of JSON
    [TestCase("global m = vector(128,vector(128,vector(20,10)))", Description = "the reply fits, but the global no longer fits into one response of the global key")]
    [TestCase("global m = vector(128,vector(128,vector(21,10)))", Description = "the first element does not fit into the reply")]
    [TestCase("global m = vector(128,vector(128,vector(30,10)))")]
    [TestCase("global m = vector(128,vector(128,vector(128,10)))")]
    [TestCase("var m = vector(128,vector(128,vector(21,10)))")]
    [TestCase("var m = vector(128,vector(128,vector(30,10)))")]
    [TestCase("global m = vector(255,vector(255,vector(255,vector(255,0))))", Description = "4228250625 elements, decided without walking them")]
    public async Task ValueIsRefusedBeforeItGoesToTheFirmware(string code)
    {
        Assert.That(await _host.ExecuteAsync(code), Is.EqualTo(TooLong));
        Assert.That(SentSetVariable(), Is.False, "the firmware must not see the code, it would assign the value first");
    }

    [Test]
    public async Task SetOfAGlobalIsRefused()
    {
        Assert.That(await _host.ExecuteAsync("global m = 0"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("set global.m = vector(128,vector(128,vector(20,10)))"), Is.EqualTo(TooLong));
        Assert.That(SentExpression("global.m"), Is.EqualTo("0"), "only the global statement went to the firmware");
    }

    [Test]
    public async Task IndexedSetOfAGlobalIsRefused()
    {
        // The element is 128 * 193 + 129 bytes of JSON, more than one response of the global key can hold, while its own first element fits
        // into the reply. The fake firmware does not emulate indices, but DSF refuses the code before it gets there
        Assert.That(await _host.ExecuteAsync("set global.m[0] = vector(128,vector(64,10))"), Is.EqualTo(TooLong));
        Assert.That(SentSetVariable(), Is.False);
    }
    #endregion

    #region Forwarded
    [Test]
    public async Task LocalVariableThatFitsIsForwarded()
    {
        // 7937 + 2 bytes fit into the reply, and a local variable is not part of the object model
        await _host.ExecuteAsync("var m = vector(128,vector(128,vector(20,10)))");
        Assert.That(SentExpression("var.m"), Is.EqualTo("vector(128,vector(128,vector(20,10)))"));
    }

    [Test]
    public async Task SetOfALocalVariableThatFitsIsForwarded()
    {
        await _host.ExecuteAsync("set var.m = vector(128,vector(128,vector(20,10)))");
        Assert.That(SentExpression("var.m"), Is.EqualTo("vector(128,vector(128,vector(20,10)))"));
    }

    [TestCase("global s = vector(10, 1)", "global.s", "vector(10, 1)")]
    [TestCase("global s = vector(255, \"abc\")", "global.s", "vector(255, \"abc\")")]
    [TestCase("global s = 1", "global.s", "1")]
    [TestCase("global s = \"text\"", "global.s", "\"text\"")]
    public async Task SmallValueIsForwarded(string code, string varName, string expression)
    {
        await _host.ExecuteAsync(code);
        Assert.That(SentExpression(varName), Is.EqualTo(expression));
    }

    [Test]
    public async Task ValueTheSbcCannotEvaluateIsForwardedUnchanged()
    {
        // move.axes is not an SBC field, so DSF cannot work out the length and leaves the decision to the firmware
        const string expression = "vector(128,vector(128,vector(#move.axes * 10,10)))";
        await _host.ExecuteAsync($"global m = {expression}");
        Assert.That(SentExpression("global.m"), Is.EqualTo(expression));
    }

    [Test]
    public async Task ExpressionThePortRejectsIsLeftToTheFirmware()
    {
        // vector(256, 0) does not fit into a firmware heap block. The firmware reports that itself, DSF forwards the code unchanged
        await _host.ExecuteAsync("global m = vector(256, 0)");
        Assert.That(SentExpression("global.m"), Is.EqualTo("vector(256, 0)"));
    }
    #endregion

    #region Array limits of the parser port
    // RRF ArrayHandle::Allocate (src/Platform/ArrayHandle.cpp) throws "Array too large" for an array that needs more than one heap block
    // of 2048 bytes (src/Platform/Heap.cpp): 4 bytes of header and 8 bytes per element leave room for 255 elements
    [TestCase("#vector(255, 0)", 255)]
    [TestCase("#(vector(200, 0) ^ vector(55, 0))", 255)]
    public void ArrayOfTheLargestSize(string expression, int length)
    {
        Assert.That(MetaExpressionParser.TryEvaluate(expression, null, out object? value), Is.True);
        Assert.That(value, Is.EqualTo(length));
    }

    [TestCase("vector(256, 0)")]
    [TestCase("vector(1000000000, 0)")]
    [TestCase("vector(200, 0) ^ vector(56, 0)")]
    public void ArrayThatIsTooLarge(string expression)
    {
        // The firmware's GCodeException has no position, so there is no column in the message either
        Assert.That(() => MetaExpressionParser.TryEvaluate(expression, null, out _), Throws.TypeOf<CodeParserException>().With.Message.EqualTo("Array too large"));
    }

    [Test]
    public async Task EchoOfAnArrayThatIsTooLarge()
    {
        // echo takes the fast path of Expressions (Expressions.cs, EvaluateExpressionToValueAsync), so DSF answers as the firmware would
        Assert.That(await _host.ExecuteAsync("echo #vector(255, 0)"), Is.EqualTo("255\n"));
        Assert.That(await _host.ExecuteAsync("echo vector(256, 0)"), Is.EqualTo("Error: Array too large"));
    }

    // RRF ParseGeneralArray (src/GCodes/GCodeBuffer/ExpressionParser.cpp) takes MaxLiteralArrayElements = 40 elements (src/Config/Configuration.h).
    // It checks the count before it skips a comma, so a trailing comma after the 40th element is refused as well
    private static string Literal(int numElements, bool trailingComma = false) =>
        "{" + string.Join(",", Enumerable.Range(1, numElements)) + (trailingComma ? ",}" : "}");

    [Test]
    public void LiteralArrayOfTheLargestSize()
    {
        Assert.That(MetaExpressionParser.TryEvaluate("#" + Literal(40), null, out object? value), Is.True);
        Assert.That(value, Is.EqualTo(40));
        Assert.That(MetaExpressionParser.TryEvaluate("#" + Literal(39, true), null, out value), Is.True);
        Assert.That(value, Is.EqualTo(39));
    }

    [TestCase(41, false)]
    [TestCase(40, true)]
    public void LiteralArrayWithTooManyElements(int numElements, bool trailingComma)
    {
        Assert.That(() => MetaExpressionParser.TryEvaluate(Literal(numElements, trailingComma), null, out _),
                    Throws.TypeOf<CodeParserException>().With.Message.StartsWith("too many array elements (column "));
    }
    #endregion
}
