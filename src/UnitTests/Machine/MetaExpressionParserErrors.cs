using DuetAPI;
using DuetControlServer.Codes.Meta.Parsing;
using NUnit.Framework;

namespace UnitTests.Machine;

/// <summary>
/// Number literals that make MetaExpressionParser throw something else than the CodeParserException it documents for syntax errors
/// (MetaExpressionParser.cs:18-22 and the exception tag of TryEvaluate), found by ExpressionFuzz. Through Expressions the exception reaches
/// SimpleCode, which only turns CodeParserException and ArgumentException into an error reply (SimpleCode.cs:190-192)
/// </summary>
[TestFixture]
public class MetaExpressionParserErrors
{
    [TestCase("1e")]
    [TestCase("1.5e+")]
    [TestCase("2E-")]
    [TestCase("1 + 3e")]
    [Explicit("An exponent without digits makes ParseNumber call float.Parse on the incomplete token (MetaExpressionParser.cs:1376-1379), which throws a FormatException")]
    public void ExponentWithoutDigits(string expression) =>
        Assert.That(() => MetaExpressionParser.TryEvaluate(expression, null, out _), Throws.InstanceOf<CodeParserException>());

    [Test, Explicit("More than 16 hex digits make ParseNumber call Convert.ToUInt64 on them (MetaExpressionParser.cs:1345), which throws an OverflowException")]
    public void HexNumberTooLong() =>
        Assert.That(() => MetaExpressionParser.TryEvaluate("0x" + new string('F', 17), null, out _), Throws.InstanceOf<CodeParserException>());

    [TestCase("1e3", 1000f)]
    [TestCase("1.5E-1", 0.15f)]
    [TestCase("0xFFFFFFFFFFFFFFFF", 1.8446744E+19f)]
    public void ValidNumbers(string expression, float expected)
    {
        // Complete exponents and up to 16 hex digits work; a hex value beyond uint becomes a float (MetaExpressionParser.cs:1388-1400)
        Assert.That(MetaExpressionParser.TryEvaluate(expression, null, out object? value), Is.True);
        Assert.That(value, Is.EqualTo(expected));
    }
}
