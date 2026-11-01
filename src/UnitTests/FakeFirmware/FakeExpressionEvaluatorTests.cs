using System.Collections.Generic;
using NUnit.Framework;

namespace UnitTests.FakeFirmware;

/// <summary>
/// Tests for the expression evaluator of the fake firmware
/// </summary>
[TestFixture]
public class FakeExpressionEvaluatorTests
{
    private static readonly Dictionary<string, object?> Values = new()
    {
        ["var.x"] = 1,
        ["var.f"] = 2.5F,
        ["var.s"] = "abc",
        ["state.status"] = "idle"
    };

    private static object? Evaluate(string expression)
    {
        FakeExpressionEvaluator evaluator = new(
            id => Values.TryGetValue(id, out object? value) ? value : throw new FakeExpressionException($"unknown value '{id}'"),
            id => id is "var" or "param" or "global" || Values.ContainsKey(id));
        return evaluator.Evaluate(expression);
    }

    [TestCase("1 + 2 * 3", 7)]
    [TestCase("(1 + 2) * 3", 9)]
    [TestCase("-var.x + 4", 3)]
    [TestCase("#var.s", 3)]
    [TestCase("{var.x + 1}", 2)]
    public void IntegerResults(string expression, int expected) => Assert.That(Evaluate(expression), Is.EqualTo(expected));

    [TestCase("var.f * 2", 5F)]
    [TestCase("3 / 2", 1.5F)]
    [TestCase("var.x + 0.5", 1.5F)]
    public void FloatResults(string expression, float expected) => Assert.That(Evaluate(expression), Is.EqualTo(expected));

    [TestCase("var.x == 1", true)]
    [TestCase("var.x = 1", true)]
    [TestCase("var.x != 1", false)]
    [TestCase("var.x >= 1 && var.f < 2", false)]
    [TestCase("var.x > 1 || var.f <= 2.5", true)]
    [TestCase("!(var.x == 1)", false)]
    [TestCase("state.status == \"idle\"", true)]
    [TestCase("var.s != \"ab\"", true)]
    [TestCase("null == null", true)]
    [TestCase("exists(var.x)", true)]
    [TestCase("exists(var.y)", false)]
    [TestCase("exists(var)", true)]
    [TestCase("exists(var.y) && var.y == 1", false)]
    [TestCase("exists(var.x) || var.y == 1", true)]
    public void BooleanResults(string expression, bool expected) => Assert.That(Evaluate(expression), Is.EqualTo(expected));

    [Test]
    public void OtherResults()
    {
        Assert.That(Evaluate("var.s ^ var.x"), Is.EqualTo("abc1"));
        Assert.That(Evaluate("\"a\"\"b\""), Is.EqualTo("a\"b"));
        Assert.That(Evaluate("var.x == 1 ? \"one\" : \"other\""), Is.EqualTo("one"));
        Assert.That(Evaluate("var.x == 2 ? var.y : 3"), Is.EqualTo(3));
        Assert.That(Evaluate("null"), Is.Null);
    }

    [TestCase("var.y")]
    [TestCase("var.x +")]
    [TestCase("var.x && true")]
    [TestCase("\"abc\" < 1")]
    [TestCase("max(1, 2)")]
    public void Errors(string expression) => Assert.That(() => Evaluate(expression), Throws.TypeOf<FakeExpressionException>());
}
