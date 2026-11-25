using DuetControlServer.Codes.Meta.Parsing;
using NUnit.Framework;

namespace UnitTests.Machine;

/// <summary>
/// floor(), ceil(), round() and pow() return an integer only if the result fits into int32, else the float, as RRF does
/// (RRF ExpressionParser.cpp, floor/ceil/round and pow cases, fixed by RRF fork commit 33e6a184e).
/// (float)int.MaxValue is 2^31, so an inclusive upper bound let 2^31 through and the conversion gave 2147483647
/// </summary>
[TestFixture]
public class MetaExpressionIntegerRange
{
    private static object? Eval(string expression)
    {
        Assert.That(MetaExpressionParser.TryEvaluate(expression, null, out object? value), Is.True, "expected the expression to be resolved on the SBC");
        return value;
    }

    [TestCase("floor(2147483648.0)")]
    [TestCase("ceil(2147483648.0)")]
    [TestCase("round(2147483648.0)")]
    [TestCase("pow(2, 31)")]
    public void TwoToThe31IsFloat(string expression)
    {
        object? value = Eval(expression);
        Assert.That(value, Is.TypeOf<float>());
        Assert.That(value, Is.EqualTo(2147483648f));
    }

    [TestCase("floor(2147483520.0)", 2147483520)]          // largest float below 2^31
    [TestCase("ceil(-2147483648.0)", int.MinValue)]
    [TestCase("round(-2147483648.0)", int.MinValue)]
    [TestCase("pow(-2, 31)", int.MinValue)]
    [TestCase("pow(2, 30)", 1073741824)]
    public void InRangeIsInteger(string expression, int expected)
    {
        object? value = Eval(expression);
        Assert.That(value, Is.TypeOf<int>());
        Assert.That(value, Is.EqualTo(expected));
    }

    [TestCase("floor(-2147483904.0)", -2147483904f)]       // largest float below -2^31
    [TestCase("pow(-2, 33)", -8589934592f)]
    public void BelowRangeIsFloat(string expression, float expected)
    {
        object? value = Eval(expression);
        Assert.That(value, Is.TypeOf<float>());
        Assert.That(value, Is.EqualTo(expected));
    }
}
