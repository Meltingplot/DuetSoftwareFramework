using System;
using System.Diagnostics;
using System.Linq;
using DuetControlServer.Codes.Meta;
using NUnit.Framework;

namespace UnitTests.Codes;

/// <summary>
/// Fork tests (Meltingplot) of the lower bounds that DSF uses to refuse variable values whose JSON RepRapFirmware could never send back
/// (Codes/Meta/VariableValueLength.cs)
/// </summary>
[TestFixture]
public class VariableValueLengths
{
    // vector() fills its array with one reference (MetaExpressionParser, vector)
    private static object?[] Vector(int count, object? value) => Enumerable.Repeat(value, count).ToArray();

    [Test]
    public void Limits()
    {
        // 8192 bytes per transfer (RRF SbcTransferBufferSize), 8 bytes PacketHeader, 8 bytes EvaluationResultHeader, 4 bytes StringHeader
        Assert.That(VariableValueLength.MaxVariableResultJsonLength("global.m"), Is.EqualTo(8192 - 8 - 8 - 8));
        Assert.That(VariableValueLength.MaxVariableResultJsonLength("var.m"), Is.EqualTo(8192 - 8 - 8 - 5));
        Assert.That(VariableValueLength.MaxObjectModelResponseLength, Is.EqualTo(8192 - 8 - 4));
    }

    [TestCase(null, 4)]
    [TestCase(true, 4)]
    [TestCase(false, 5)]
    [TestCase(0, 1)]
    [TestCase(-2147483648, 11)]
    [TestCase(10u, 2)]
    [TestCase(1.5f, 1)]
    [TestCase('a', 2)]
    [TestCase("abc", 5)]
    public void Scalar(object? value, long length) => Assert.That(VariableValueLength.MinimumJsonLength(value, long.MaxValue), Is.EqualTo(length));

    [Test]
    public void ArraysOfIntegersAreExact()
    {
        // As RRF writes them: [10,10,...] is 3n + 1 bytes, and vector(128, that) has 127 commas and two brackets
        Assert.That(VariableValueLength.MinimumJsonLength(Array.Empty<object?>(), long.MaxValue), Is.EqualTo(2));
        Assert.That(VariableValueLength.MinimumJsonLength(Vector(20, 10), long.MaxValue), Is.EqualTo(61));
        Assert.That(VariableValueLength.MinimumJsonLength(Vector(128, Vector(20, 10)), long.MaxValue), Is.EqualTo(7937));
        Assert.That(VariableValueLength.MinimumJsonLength(Vector(128, Vector(128, Vector(20, 10))), long.MaxValue), Is.EqualTo(1016065));
        Assert.That(VariableValueLength.MinimumJsonLength(new object?[] { 1, -1, null, true }, long.MaxValue), Is.EqualTo("[1,-1,null,true]".Length));
    }

    [Test]
    public void ReplyHoldsTheFirstElement()
    {
        // Not a global: only the reply counts, which holds the brackets and the first element (7937 + 2 or 8321 + 2 bytes)
        Assert.That(VariableValueLength.IsTooLong(Vector(128, Vector(128, Vector(20, 10))), "var.m", false), Is.False);
        Assert.That(VariableValueLength.IsTooLong(Vector(128, Vector(128, Vector(21, 10))), "var.m", false), Is.True);
        Assert.That(VariableValueLength.IsTooLong(Vector(128, Vector(128, Vector(20, 10))), "global.m", true), Is.True, "the global key holds all of it");
    }

    [Test]
    public void BoundaryOfTheReply()
    {
        // The first element is an array of a ints of value 10 and one int of d digits: 2 + 3a + d bytes, plus the outer brackets
        int limit = VariableValueLength.MaxVariableResultJsonLength("var.m");
        object?[] ValueWithReply(int replyLength)
        {
            int rest = replyLength - 2 - 2;                 // outer and inner brackets
            int a = (rest - 1) / 3;                         // a times "10," and then one number of 1 to 3 digits
            int digits = rest - 3 * a;
            object?[] inner = new object?[a + 1];
            Array.Fill(inner, 10, 0, a);
            inner[a] = (int)Math.Pow(10, digits - 1);
            return [inner];
        }
        Assert.That(2 + VariableValueLength.MinimumJsonLength(ValueWithReply(limit)[0], long.MaxValue), Is.EqualTo(limit));
        Assert.That(VariableValueLength.IsTooLong(ValueWithReply(limit), "var.m", false), Is.False);
        Assert.That(VariableValueLength.IsTooLong(ValueWithReply(limit + 1), "var.m", false), Is.True);
    }

    [Test]
    public void BoundaryOfTheGlobalKey()
    {
        // A string of n characters is n + 2 bytes; the reply is not involved because a string goes in binary form
        int limit = VariableValueLength.MaxObjectModelResponseLength;
        Assert.That(VariableValueLength.IsTooLong(new string('x', limit - 2), "global.s", true), Is.False);
        Assert.That(VariableValueLength.IsTooLong(new string('x', limit - 1), "global.s", true), Is.True);
        Assert.That(VariableValueLength.IsTooLong(new string('x', limit - 1), "var.s", false), Is.False);
    }

    [Test]
    public void SharedArraysAreWalkedOnce()
    {
        object?[] value = Vector(255, Vector(255, Vector(255, Vector(255, Vector(255, 0)))));
        Stopwatch stopwatch = Stopwatch.StartNew();
        Assert.That(VariableValueLength.IsTooLong(value, "global.m", true), Is.True);
        Assert.That(VariableValueLength.MinimumJsonLength(value, long.MaxValue), Is.GreaterThan(1_000_000_000_000L));
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
    }
}
