using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Utility;
using NUnit.Framework;

namespace UnitTests.Commands;

/// <summary>
/// Tests for the value parsing, the conversion operators, the equality and the JSON converter of <see cref="CodeParameter"/>
/// (DuetAPI/Commands/Code/Parameter.cs). Code handlers in DSF and plugins read parameters only through these operators,
/// so the conversions each operator accepts and rejects are pinned down here as Parameter.cs implements them
/// </summary>
[TestFixture]
public class CodeParameterConversions
{
    #region Parsing of text values
    /// <summary>
    /// Value types the string constructor derives from unquoted text, in the order of the checks in Parameter.cs:91-163
    /// </summary>
    private static IEnumerable<TestCaseData> ParsedValues()
    {
        yield return new TestCaseData("12", typeof(int)).SetName("Integer");
        yield return new TestCaseData("-12", typeof(int)).SetName("NegativeInteger");
        yield return new TestCaseData("0x1F", typeof(int)).SetName("HexInteger");
        // Above int.MaxValue the decimal check falls through to uint (Parameter.cs:148-152)
        yield return new TestCaseData("3000000000", typeof(uint)).SetName("UnsignedInteger");
        yield return new TestCaseData("1.5", typeof(float)).SetName("Float");
        yield return new TestCaseData("99999999999", typeof(float)).SetName("IntegerTooBigForUInt");
        yield return new TestCaseData("abc", typeof(string)).SetName("UnquotedString");
        // A colon makes it an array: floats if there is a dot anywhere, else ints, else uints, else a string (Parameter.cs:102-132)
        yield return new TestCaseData("1:2:3", typeof(int[])).SetName("IntArray");
        yield return new TestCaseData("1:4000000000", typeof(uint[])).SetName("UIntArray");
        yield return new TestCaseData("1.5:2", typeof(float[])).SetName("FloatArray");
        yield return new TestCaseData("a:b", typeof(string)).SetName("ColonString");
        yield return new TestCaseData("1.x:2", typeof(string)).SetName("BadFloatArray");
    }

    [TestCaseSource(nameof(ParsedValues))]
    public void ParseUnquotedValue(string value, Type expectedType)
    {
        CodeParameter parameter = new('P', value, false, false);
        Assert.That(parameter.Type, Is.EqualTo(expectedType));
        Assert.That(parameter.IsExpression, Is.False);
        Assert.That(parameter.ToString(), Is.EqualTo("P" + value));
    }

    [Test]
    public void ParseSpecialValues()
    {
        // Quoted values are always strings, even if they look like numbers (Parameter.cs:61-65)
        CodeParameter quoted = new('P', "12", true, false);
        Assert.That(quoted.Type, Is.EqualTo(typeof(string)));
        Assert.That((string)quoted, Is.EqualTo("12"));

        // No value as in G28 X (Parameter.cs:66-70)
        CodeParameter empty = new('X', string.Empty, false, false);
        Assert.That(empty.IsNull, Is.True);
        Assert.That(empty.Type, Is.Null);

        // Curly braces make an expression (Parameter.cs:96-101)
        CodeParameter expression = new('S', " {1 + 2} ", false, false);
        Assert.That(expression.IsExpression, Is.True);
        Assert.That((string)expression, Is.EqualTo("{1 + 2}"));
    }

    [Test]
    public void ParseDriverIds()
    {
        // One driver ID gives a DriverId, a colon-separated list an array of them (Parameter.cs:71-90)
        CodeParameter single = new('P', "1.2", false, true);
        Assert.That(single.Type, Is.EqualTo(typeof(DriverId)));
        Assert.That(single.IsDriverId, Is.False, "IsDriverId is only set by Code.ConvertDriverIds");
        Assert.That((DriverId)single!, Is.EqualTo(new DriverId(1, 2)));

        CodeParameter list = new('P', "0.1:2.3", false, true);
        Assert.That(list.Type, Is.EqualTo(typeof(DriverId[])));
        Assert.That((DriverId[])list!, Is.EqualTo(new[] { new DriverId(0, 1), new DriverId(2, 3) }));

        // The DriverId string constructor rejects more than one dot (DriverId.cs:84-87)
        Assert.That(() => new CodeParameter('P', "1.2.3", false, true), Throws.ArgumentException);
    }
    #endregion

    #region Object constructor
    /// <summary>
    /// Values passed to the object constructor and the text it derives from them (Parameter.cs:171-225)
    /// </summary>
    private static IEnumerable<TestCaseData> ObjectValues()
    {
        yield return new TestCaseData("text", "Ptext", false).SetName("String");
        yield return new TestCaseData("{var.x}", "P{var.x}", true).SetName("Expression");
        yield return new TestCaseData(-3, "P-3", false).SetName("Int");
        yield return new TestCaseData(3000000000u, "P3000000000", false).SetName("UInt");
        yield return new TestCaseData(1.5f, "P1.5", false).SetName("Float");
        yield return new TestCaseData(5000000000L, "P5000000000", false).SetName("Long");
        yield return new TestCaseData(new[] { 1, -2 }, "P1:-2", false).SetName("IntArray");
        yield return new TestCaseData(new[] { 1u, 2u }, "P1:2", false).SetName("UIntArray");
        yield return new TestCaseData(new[] { 1.5f, 2f }, "P1.5:2", false).SetName("FloatArray");
        yield return new TestCaseData(new[] { 1L, 5000000000L }, "P1:5000000000", false).SetName("LongArray");
        yield return new TestCaseData(new DriverId(1, 2), "P1.2", false).SetName("OtherObject");
        yield return new TestCaseData(null, "P", false).SetName("Null");
    }

    [TestCaseSource(nameof(ObjectValues))]
    public void CreateFromObject(object? value, string expectedText, bool isExpression)
    {
        CodeParameter parameter = new('P', value);
        Assert.That(parameter.ToString(), Is.EqualTo(expectedText));
        Assert.That(parameter.IsExpression, Is.EqualTo(isExpression));
        Assert.That(parameter.Type, Is.EqualTo(value?.GetType()));
    }
    #endregion

    #region Conversion operators
    private static CodeParameter P(object? value) => new('P', value);

    /// <summary>
    /// Conversions that succeed: (parameter value, conversion, expected result)
    /// </summary>
    private static IEnumerable<TestCaseData> Conversions()
    {
        // float and float? accept float, int and uint (Parameter.cs:246-293)
        yield return new TestCaseData(1.5f, "float", 1.5f).SetName("FloatFromFloat");
        yield return new TestCaseData(2, "float", 2f).SetName("FloatFromInt");
        yield return new TestCaseData(3u, "float", 3f).SetName("FloatFromUInt");
        yield return new TestCaseData(1.5f, "float?", 1.5f).SetName("NullableFloatFromFloat");
        yield return new TestCaseData(2, "float?", 2f).SetName("NullableFloatFromInt");
        yield return new TestCaseData(3u, "float?", 3f).SetName("NullableFloatFromUInt");

        // int and int? accept int and float, which Convert.ToInt32 rounds to the nearest even number (Parameter.cs:301-340)
        yield return new TestCaseData(-4, "int", -4).SetName("IntFromInt");
        yield return new TestCaseData(2.5f, "int", 2).SetName("IntFromFloat");
        yield return new TestCaseData(-4, "int?", -4).SetName("NullableIntFromInt");
        yield return new TestCaseData(3.5f, "int?", 4).SetName("NullableIntFromFloat");

        // uint and uint? accept uint, DriverId, int and float (Parameter.cs:348-403)
        yield return new TestCaseData(7u, "uint", 7u).SetName("UIntFromUInt");
        yield return new TestCaseData(new DriverId(1, 2), "uint", 0x10002u).SetName("UIntFromDriverId");
        yield return new TestCaseData(7, "uint", 7u).SetName("UIntFromInt");
        yield return new TestCaseData(7.4f, "uint", 7u).SetName("UIntFromFloat");
        yield return new TestCaseData(7u, "uint?", 7u).SetName("NullableUIntFromUInt");
        yield return new TestCaseData(new DriverId(1, 2), "uint?", 0x10002u).SetName("NullableUIntFromDriverId");
        yield return new TestCaseData(7, "uint?", 7u).SetName("NullableUIntFromInt");
        yield return new TestCaseData(7.4f, "uint?", 7u).SetName("NullableUIntFromFloat");

        // long and long? accept long, int, uint and float (Parameter.cs:411-464)
        yield return new TestCaseData(5000000000L, "long", 5000000000L).SetName("LongFromLong");
        yield return new TestCaseData(-2, "long", -2L).SetName("LongFromInt");
        yield return new TestCaseData(3000000000u, "long", 3000000000L).SetName("LongFromUInt");
        yield return new TestCaseData(2.5f, "long", 2L).SetName("LongFromFloat");
        yield return new TestCaseData(5000000000L, "long?", 5000000000L).SetName("NullableLongFromLong");
        yield return new TestCaseData(-2, "long?", -2L).SetName("NullableLongFromInt");
        yield return new TestCaseData(3000000000u, "long?", 3000000000L).SetName("NullableLongFromUInt");
        yield return new TestCaseData(2.5f, "long?", 2L).SetName("NullableLongFromFloat");

        // bool is int > 0 (Parameter.cs:472-480)
        yield return new TestCaseData(1, "bool", true).SetName("BoolFromOne");
        yield return new TestCaseData(0, "bool", false).SetName("BoolFromZero");
        yield return new TestCaseData(-1, "bool", false).SetName("BoolFromNegative");
        yield return new TestCaseData(1, "bool?", true).SetName("NullableBoolFromOne");

        yield return new TestCaseData("text", "string", "text").SetName("StringFromString");

        // DriverId accepts DriverId and uint (Parameter.cs:509-524)
        yield return new TestCaseData(new DriverId(1, 2), "DriverId", new DriverId(1, 2)).SetName("DriverIdFromDriverId");
        yield return new TestCaseData(0x30004u, "DriverId", new DriverId(3, 4)).SetName("DriverIdFromUInt");

        yield return new TestCaseData("192.168.1.2", "IPAddress", IPAddress.Parse("192.168.1.2")).SetName("IPAddressFromString");

        // float[] accepts float[], float, int, uint, int[] and uint[] (Parameter.cs:553-585)
        yield return new TestCaseData(new[] { 1.5f }, "float[]", new[] { 1.5f }).SetName("FloatArrayFromFloatArray");
        yield return new TestCaseData(1.5f, "float[]", new[] { 1.5f }).SetName("FloatArrayFromFloat");
        yield return new TestCaseData(2, "float[]", new[] { 2f }).SetName("FloatArrayFromInt");
        yield return new TestCaseData(3u, "float[]", new[] { 3f }).SetName("FloatArrayFromUInt");
        yield return new TestCaseData(new[] { 1, 2 }, "float[]", new[] { 1f, 2f }).SetName("FloatArrayFromIntArray");
        yield return new TestCaseData(new[] { 1u, 2u }, "float[]", new[] { 1f, 2f }).SetName("FloatArrayFromUIntArray");

        // int[] accepts int[], int, float and float[] (Parameter.cs:594-618)
        yield return new TestCaseData(new[] { 1, -2 }, "int[]", new[] { 1, -2 }).SetName("IntArrayFromIntArray");
        yield return new TestCaseData(-2, "int[]", new[] { -2 }).SetName("IntArrayFromInt");
        yield return new TestCaseData(1.5f, "int[]", new[] { 2 }).SetName("IntArrayFromFloat");
        yield return new TestCaseData(new[] { 1.5f, 2.5f }, "int[]", new[] { 2, 2 }).SetName("IntArrayFromFloatArray");

        // uint[] accepts uint[], uint, DriverId[], DriverId and non-negative int, int[], float and float[] (Parameter.cs:627-667)
        yield return new TestCaseData(new[] { 1u, 2u }, "uint[]", new[] { 1u, 2u }).SetName("UIntArrayFromUIntArray");
        yield return new TestCaseData(3u, "uint[]", new[] { 3u }).SetName("UIntArrayFromUInt");
        yield return new TestCaseData(new[] { new DriverId(0, 1), new DriverId(1, 0) }, "uint[]", new[] { 1u, 0x10000u }).SetName("UIntArrayFromDriverIdArray");
        yield return new TestCaseData(new DriverId(1, 1), "uint[]", new[] { 0x10001u }).SetName("UIntArrayFromDriverId");
        yield return new TestCaseData(4, "uint[]", new[] { 4u }).SetName("UIntArrayFromInt");
        yield return new TestCaseData(new[] { 4, 5 }, "uint[]", new[] { 4u, 5u }).SetName("UIntArrayFromIntArray");
        yield return new TestCaseData(4.2f, "uint[]", new[] { 4u }).SetName("UIntArrayFromFloat");
        yield return new TestCaseData(new[] { 4.2f, 5f }, "uint[]", new[] { 4u, 5u }).SetName("UIntArrayFromFloatArray");

        // long[] accepts long[], long, int, uint, float, int[], uint[] and float[] (Parameter.cs:676-715)
        yield return new TestCaseData(new[] { 5000000000L }, "long[]", new[] { 5000000000L }).SetName("LongArrayFromLongArray");
        yield return new TestCaseData(5000000000L, "long[]", new[] { 5000000000L }).SetName("LongArrayFromLong");
        yield return new TestCaseData(-1, "long[]", new[] { -1L }).SetName("LongArrayFromInt");
        yield return new TestCaseData(3000000000u, "long[]", new[] { 3000000000L }).SetName("LongArrayFromUInt");
        yield return new TestCaseData(1.5f, "long[]", new[] { 2L }).SetName("LongArrayFromFloat");
        yield return new TestCaseData(new[] { 1, -1 }, "long[]", new[] { 1L, -1L }).SetName("LongArrayFromIntArray");
        yield return new TestCaseData(new[] { 3000000000u }, "long[]", new[] { 3000000000L }).SetName("LongArrayFromUIntArray");
        yield return new TestCaseData(new[] { 0.5f, 1.5f }, "long[]", new[] { 0L, 2L }).SetName("LongArrayFromFloatArray");

        // DriverId[] accepts DriverId[], DriverId, uint[] and uint (Parameter.cs:724-747)
        yield return new TestCaseData(new[] { new DriverId(0, 1) }, "DriverId[]", new[] { new DriverId(0, 1) }).SetName("DriverIdArrayFromDriverIdArray");
        yield return new TestCaseData(new DriverId(0, 1), "DriverId[]", new[] { new DriverId(0, 1) }).SetName("DriverIdArrayFromDriverId");
        yield return new TestCaseData(new[] { 0x10002u }, "DriverId[]", new[] { new DriverId(1, 2) }).SetName("DriverIdArrayFromUIntArray");
        yield return new TestCaseData(0x10002u, "DriverId[]", new[] { new DriverId(1, 2) }).SetName("DriverIdArrayFromUInt");
    }

    /// <summary>
    /// Conversions that are rejected with an <see cref="InvalidParameterTypeException"/>: (parameter value, conversion)
    /// </summary>
    private static IEnumerable<TestCaseData> RejectedConversions()
    {
        // "long won't fit" and "uint won't fit" in the comments of Parameter.cs
        yield return new TestCaseData(5000000000L, "float").SetName("FloatFromLong");
        yield return new TestCaseData("x", "float?").SetName("NullableFloatFromString");
        yield return new TestCaseData(3u, "int").SetName("IntFromUInt");
        yield return new TestCaseData(5000000000L, "int?").SetName("NullableIntFromLong");
        yield return new TestCaseData(5000000000L, "uint").SetName("UIntFromLong");
        yield return new TestCaseData("x", "uint?").SetName("NullableUIntFromString");
        yield return new TestCaseData("x", "long").SetName("LongFromString");
        yield return new TestCaseData("x", "long?").SetName("NullableLongFromString");
        yield return new TestCaseData(1u, "bool").SetName("BoolFromUInt");
        yield return new TestCaseData(1, "string").SetName("StringFromInt");
        yield return new TestCaseData(1, "DriverId").SetName("DriverIdFromInt");
        yield return new TestCaseData(1, "IPAddress").SetName("IPAddressFromInt");
        yield return new TestCaseData(new[] { 1L }, "float[]").SetName("FloatArrayFromLongArray");
        yield return new TestCaseData(new[] { 1u }, "int[]").SetName("IntArrayFromUIntArray");
        yield return new TestCaseData(-1, "uint[]").SetName("UIntArrayFromNegativeInt");
        yield return new TestCaseData(new[] { 1, -1 }, "uint[]").SetName("UIntArrayFromNegativeIntArray");
        yield return new TestCaseData(-1f, "uint[]").SetName("UIntArrayFromNegativeFloat");
        yield return new TestCaseData(new[] { -1f }, "uint[]").SetName("UIntArrayFromNegativeFloatArray");
        yield return new TestCaseData("x", "long[]").SetName("LongArrayFromString");
        yield return new TestCaseData(1, "DriverId[]").SetName("DriverIdArrayFromInt");
    }

    private static object? Convert(CodeParameter? parameter, string conversion) => conversion switch
    {
        "float" => (float)parameter,
        "float?" => (float?)parameter,
        "int" => (int)parameter,
        "int?" => (int?)parameter,
        "uint" => (uint)parameter,
        "uint?" => (uint?)parameter,
        "long" => (long)parameter,
        "long?" => (long?)parameter,
        "bool" => (bool)parameter,
        "bool?" => (bool?)parameter,
        "string" => (string?)parameter,
        "DriverId" => (DriverId?)parameter,
        "IPAddress" => (IPAddress?)parameter,
        "float[]" => (float[]?)parameter,
        "int[]" => (int[]?)parameter,
        "uint[]" => (uint[]?)parameter,
        "long[]" => (long[]?)parameter,
        "DriverId[]" => (DriverId[]?)parameter,
        _ => throw new ArgumentException(conversion)
    };

    [TestCaseSource(nameof(Conversions))]
    public void Conversion(object value, string conversion, object expected) => Assert.That(Convert(P(value), conversion), Is.EqualTo(expected));

    [TestCaseSource(nameof(RejectedConversions))]
    public void RejectedConversion(object value, string conversion)
    {
        InvalidParameterTypeException e = Assert.Throws<InvalidParameterTypeException>(() => Convert(P(value), conversion))!;
        Assert.That(e.Letter, Is.EqualTo('P'));
    }

    [TestCase("float")]
    [TestCase("int")]
    [TestCase("uint")]
    [TestCase("long")]
    [TestCase("bool")]
    public void NonNullableConversionOfMissingParameter(string conversion)
    {
        // A missing parameter cannot be converted to a value type (Parameter.cs:264, 315, 370, 432); the message names no letter
        InvalidParameterTypeException e = Assert.Throws<InvalidParameterTypeException>(() => Convert(null, conversion))!;
        Assert.That(e.Letter, Is.Null);
        Assert.That(e.Message, Does.StartWith("Cannot convert n/a parameter"));
    }

    [TestCase("float?")]
    [TestCase("int?")]
    [TestCase("uint?")]
    [TestCase("long?")]
    [TestCase("bool?")]
    [TestCase("string")]
    [TestCase("DriverId")]
    [TestCase("IPAddress")]
    [TestCase("float[]")]
    [TestCase("int[]")]
    [TestCase("uint[]")]
    [TestCase("long[]")]
    [TestCase("DriverId[]")]
    public void NullableConversionOfMissingParameter(string conversion) => Assert.That(Convert(null, conversion), Is.Null);
    #endregion

    #region Equality
    [Test]
    public void Equality()
    {
        // Parameters are equal if letter and value are equal, and a parameter equals a plain value of the same type (Parameter.cs:756-778)
        CodeParameter a = new('S', 1), b = new('S', 1), c = new('T', 1), d = new('S', 2);
        Assert.That(a == b, Is.True);
        Assert.That(a.Equals(b), Is.True);
        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        Assert.That(a != c, Is.True);
        Assert.That(a == d, Is.False);
        Assert.That(a == (object)1, Is.True);
        Assert.That(a == (object)1u, Is.False, "int.Equals does not accept a boxed uint");

        CodeParameter? none = null;
        Assert.That(none == null, Is.True);
        Assert.That(none == (object)1, Is.False);

        // A parameter without a value equals null (Parameter.cs:766, see also Parameter.EqualityOfNullParameters)
        CodeParameter empty = new('X', null);
        Assert.That(empty.Equals(null), Is.True);
        Assert.That(empty == (object)1, Is.False);
        Assert.That(empty.GetHashCode(), Is.EqualTo('X'.GetHashCode()));
    }
    #endregion

    #region JSON
    private static CodeParameter Deserialize(string json) => JsonSerializer.Deserialize<CodeParameter>(json)!;

    [Test]
    public void ReadJson()
    {
        // CodeParameterConverter.Read accepts the flags as numbers or booleans (Parameter.cs:824-845)
        CodeParameter quoted = Deserialize("{\"letter\":\"P\",\"value\":\"12\",\"isString\":1}");
        Assert.That(quoted.Letter, Is.EqualTo('P'));
        Assert.That(quoted.Type, Is.EqualTo(typeof(string)));

        CodeParameter driver = Deserialize("{\"letter\":\"D\",\"value\":\"1.2\",\"isDriverId\":true,\"isString\":false}");
        Assert.That(driver.Type, Is.EqualTo(typeof(DriverId)));

        CodeParameter driverAsNumber = Deserialize("{\"letter\":\"D\",\"value\":\"3\",\"isDriverId\":1}");
        Assert.That((DriverId)driverAsNumber!, Is.EqualTo(new DriverId(0, 3)));

        CodeParameter number = Deserialize("{\"letter\":\"S\",\"value\":\"12\",\"isString\":false,\"isDriverId\":0}");
        Assert.That(number.Type, Is.EqualTo(typeof(int)));

        // Without a letter the parameter is unprecedented ('@'), without a value it is empty
        CodeParameter defaults = Deserialize("{\"other\":\"x\"}");
        Assert.That(defaults.Letter, Is.EqualTo('@'));
        Assert.That(defaults.IsNull, Is.True);
    }

    [TestCase("1")]
    [TestCase("\"P1\"")]
    [TestCase("{\"letter\":\"P\"")]
    public void ReadInvalidJson(string json) => Assert.That(() => Deserialize(json), Throws.InstanceOf<JsonException>());

    [Test]
    public void WriteJson()
    {
        // CodeParameterConverter.Write flags driver IDs and strings that are not expressions (Parameter.cs:856-867)
        Assert.That(JsonSerializer.Serialize(new CodeParameter('P', "x", true, false)), Is.EqualTo("{\"letter\":\"P\",\"value\":\"x\",\"isString\":true}"));
        Assert.That(JsonSerializer.Serialize(new CodeParameter('P', "{1}", false, false)), Is.EqualTo("{\"letter\":\"P\",\"value\":\"{1}\",\"isString\":false}"));
        Assert.That(JsonSerializer.Serialize(new CodeParameter('P', "1.2", false, true)), Is.EqualTo("{\"letter\":\"P\",\"value\":\"1.2\",\"isDriverId\":true,\"isString\":false}"));
        Assert.That(JsonSerializer.Serialize(new CodeParameter('P', "1.2:1.3", false, true)), Is.EqualTo("{\"letter\":\"P\",\"value\":\"1.2:1.3\",\"isDriverId\":true,\"isString\":false}"));

        // Round trip
        CodeParameter original = new('P', "1.2:1.3", false, true);
        CodeParameter copy = Deserialize(JsonSerializer.Serialize(original));
        Assert.That((DriverId[])copy!, Is.EqualTo((DriverId[])original!));
    }
    #endregion
}
