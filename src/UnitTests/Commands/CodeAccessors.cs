using System;
using System.Net;
using System.Text.Json;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using NUnit.Framework;
using GCode = DuetAPI.Commands.Code;

namespace UnitTests.Commands;

/// <summary>
/// Tests for the parameter accessors, the text reconstruction and the code type converter of <see cref="Code"/>
/// (DuetAPI/Commands/Code/Code.cs and Type.cs). The Get* accessors throw a <see cref="MissingParameterException"/> for
/// a missing parameter or return the default, the TryGet* accessors skip expressions, which are evaluated before a handler reads them
/// </summary>
[TestFixture]
public class CodeAccessors
{
    /// <summary>
    /// Code with one parameter of each type and an expression parameter that the TryGet* accessors must skip
    /// </summary>
    private static GCode CreateCode()
    {
        GCode code = new()
        {
            Type = CodeType.MCode,
            MajorNumber = 1234,
            Parameters =
            [
                new CodeParameter('E', "{1+1}", false, false),
                new CodeParameter('F', 1.5f),
                new CodeParameter('I', -3),
                new CodeParameter('U', 3000000000u),
                new CodeParameter('L', 5000000000L),
                new CodeParameter('B', 1),
                new CodeParameter('S', "text"),
                new CodeParameter('D', "1.2", false, true),
                new CodeParameter('A', "10.0.0.1"),
                new CodeParameter('X', "1.5:2.5", false, false),
                new CodeParameter('Y', "1:2", false, false),
                new CodeParameter('Z', "1:4000000000", false, false),
                new CodeParameter('W', new[] { 5000000000L }),
                new CodeParameter('V', "0.1:0.2", false, true)
            ]
        };
        return code;
    }

    [Test]
    public void GetWithValue()
    {
        GCode code = CreateCode();
        Assert.That(code.GetFloat('F'), Is.EqualTo(1.5f));
        Assert.That(code.GetFloat('F', 9f), Is.EqualTo(1.5f));
        Assert.That(code.GetInt('I'), Is.EqualTo(-3));
        Assert.That(code.GetInt('I', 9), Is.EqualTo(-3));
        Assert.That(code.GetUInt('U'), Is.EqualTo(3000000000u));
        Assert.That(code.GetUInt('U', 9u), Is.EqualTo(3000000000u));
        Assert.That(code.GetLong('L'), Is.EqualTo(5000000000L));
        Assert.That(code.GetLong('L', 9L), Is.EqualTo(5000000000L));
        Assert.That(code.GetBool('B'), Is.True);
        Assert.That(code.GetBool('B', false), Is.True);
        Assert.That(code.GetString('S'), Is.EqualTo("text"));
        Assert.That(code.GetString('S', "x"), Is.EqualTo("text"));
        Assert.That(code.GetOptionalString('S'), Is.EqualTo("text"));
        Assert.That(code.GetDriverId('D'), Is.EqualTo(new DriverId(1, 2)));
        Assert.That(code.GetDriverId('D', new DriverId(9, 9)), Is.EqualTo(new DriverId(1, 2)));
        Assert.That(code.GetIPAddress('A'), Is.EqualTo(IPAddress.Parse("10.0.0.1")));
        Assert.That(code.GetIPAddress('A', IPAddress.Loopback), Is.EqualTo(IPAddress.Parse("10.0.0.1")));
        Assert.That(code.GetFloatArray('X'), Is.EqualTo(new[] { 1.5f, 2.5f }));
        Assert.That(code.GetFloatArray('X', []), Is.EqualTo(new[] { 1.5f, 2.5f }));
        Assert.That(code.GetIntArray('Y'), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(code.GetIntArray('Y', []), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(code.GetUIntArray('Z'), Is.EqualTo(new[] { 1u, 4000000000u }));
        Assert.That(code.GetUIntArray('Z', []), Is.EqualTo(new[] { 1u, 4000000000u }));
        Assert.That(code.GetLongArray('W'), Is.EqualTo(new[] { 5000000000L }));
        Assert.That(code.GetLongArray('W', []), Is.EqualTo(new[] { 5000000000L }));
        Assert.That(code.GetDriverIdArray('V'), Is.EqualTo(new[] { new DriverId(0, 1), new DriverId(0, 2) }));
        Assert.That(code.GetDriverIdArray('V', []), Is.EqualTo(new[] { new DriverId(0, 1), new DriverId(0, 2) }));

        // The default parameter of GetParameter is not an expression (Code.cs:196)
        Assert.That(code.GetParameter('Q', 5), Is.EqualTo(new CodeParameter('Q', 5)));
        Assert.That(code.GetParameter('I', 5), Is.SameAs(code.Parameters[2]));
    }

    [Test]
    public void GetWithoutValue()
    {
        GCode code = CreateCode();
        Assert.That(code.GetFloat('Q', 9f), Is.EqualTo(9f));
        Assert.That(code.GetInt('Q', 9), Is.EqualTo(9));
        Assert.That(code.GetUInt('Q', 9u), Is.EqualTo(9u));
        Assert.That(code.GetLong('Q', 9L), Is.EqualTo(9L));
        Assert.That(code.GetBool('Q', true), Is.True);
        Assert.That(code.GetString('Q', "x"), Is.EqualTo("x"));
        Assert.That(code.GetOptionalString('Q'), Is.Null);
        Assert.That(code.GetDriverId('Q', new DriverId(9, 9)), Is.EqualTo(new DriverId(9, 9)));
        Assert.That(code.GetIPAddress('Q', IPAddress.Loopback), Is.EqualTo(IPAddress.Loopback));
        Assert.That(code.GetFloatArray('Q', [9f]), Is.EqualTo(new[] { 9f }));
        Assert.That(code.GetIntArray('Q', [9]), Is.EqualTo(new[] { 9 }));
        Assert.That(code.GetUIntArray('Q', [9u]), Is.EqualTo(new[] { 9u }));
        Assert.That(code.GetLongArray('Q', [9L]), Is.EqualTo(new[] { 9L }));
        Assert.That(code.GetDriverIdArray('Q', [new DriverId(9, 9)]), Is.EqualTo(new[] { new DriverId(9, 9) }));
    }

    [Test]
    public void GetMissingParameter()
    {
        GCode code = CreateCode();
        Action<GCode>[] getters =
        [
            c => c.GetFloat('Q'), c => c.GetInt('Q'), c => c.GetUInt('Q'), c => c.GetLong('Q'), c => c.GetBool('Q'),
            c => c.GetString('Q'), c => c.GetDriverId('Q'), c => c.GetIPAddress('Q'), c => c.GetFloatArray('Q'),
            c => c.GetIntArray('Q'), c => c.GetUIntArray('Q'), c => c.GetLongArray('Q'), c => c.GetDriverIdArray('Q')
        ];
        foreach (Action<GCode> getter in getters)
        {
            MissingParameterException e = Assert.Throws<MissingParameterException>(() => getter(code))!;
            Assert.That(e.Letter, Is.EqualTo('Q'));
            Assert.That(e.Message, Is.EqualTo("Missing Q parameter"));
        }
    }

    [Test]
    public void TryGetWithValue()
    {
        GCode code = CreateCode();
        Assert.That(code.TryGetParameter('F', out CodeParameter? parameter) && parameter == (object)1.5f, Is.True);
        Assert.That(code.TryGetFloat('F', out float floatValue) && floatValue == 1.5f, Is.True);
        Assert.That(code.TryGetFloat('F', out float? nullableFloat) && nullableFloat == 1.5f, Is.True);
        Assert.That(code.TryGetInt('I', out int intValue) && intValue == -3, Is.True);
        Assert.That(code.TryGetInt('I', out int? nullableInt) && nullableInt == -3, Is.True);
        Assert.That(code.TryGetUInt('U', out uint uintValue) && uintValue == 3000000000u, Is.True);
        Assert.That(code.TryGetUInt('U', out uint? nullableUInt) && nullableUInt == 3000000000u, Is.True);
        Assert.That(code.TryGetLong('L', out long longValue) && longValue == 5000000000L, Is.True);
        Assert.That(code.TryGetLong('L', out long? nullableLong) && nullableLong == 5000000000L, Is.True);
        Assert.That(code.TryGetBool('B', out bool boolValue) && boolValue, Is.True);
        Assert.That(code.TryGetBool('B', out bool? nullableBool) && nullableBool == true, Is.True);
        Assert.That(code.TryGetString('S', out string? stringValue) && stringValue == "text", Is.True);
        Assert.That(code.TryGetDriverId('D', out DriverId? driverId) && driverId.Equals(new DriverId(1, 2)), Is.True);
        Assert.That(code.TryGetIPAddress('A', out IPAddress? address) && address.Equals(IPAddress.Parse("10.0.0.1")), Is.True);
        Assert.That(code.TryGetFloatArray('X', out float[]? floatArray), Is.True);
        Assert.That(floatArray, Is.EqualTo(new[] { 1.5f, 2.5f }));
        Assert.That(code.TryGetIntArray('Y', out int[]? intArray), Is.True);
        Assert.That(intArray, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(code.TryGetUIntArray('Z', out uint[]? uintArray), Is.True);
        Assert.That(uintArray, Is.EqualTo(new[] { 1u, 4000000000u }));
        Assert.That(code.TryGetLongArray('W', out long[]? longArray), Is.True);
        Assert.That(longArray, Is.EqualTo(new[] { 5000000000L }));
        Assert.That(code.TryGetDriverIdArray('V', out DriverId[]? driverIdArray), Is.True);
        Assert.That(driverIdArray, Is.EqualTo(new[] { new DriverId(0, 1), new DriverId(0, 2) }));
    }

    [TestCase('Q')]
    [TestCase('E')]
    public void TryGetWithoutValue(char letter)
    {
        // Missing and expression parameters are not found (Code.cs:204-216 and the other TryGet* accessors)
        GCode code = CreateCode();
        Assert.That(code.HasParameter(letter), Is.EqualTo(letter == 'E'));
        Assert.That(code.TryGetParameter(letter, out CodeParameter? parameter), Is.False);
        Assert.That(parameter, Is.Null);
        Assert.That(code.TryGetFloat(letter, out float floatValue) || floatValue != 0f, Is.False);
        Assert.That(code.TryGetFloat(letter, out float? nullableFloat) || nullableFloat is not null, Is.False);
        Assert.That(code.TryGetInt(letter, out int intValue) || intValue != 0, Is.False);
        Assert.That(code.TryGetInt(letter, out int? nullableInt) || nullableInt is not null, Is.False);
        Assert.That(code.TryGetUInt(letter, out uint uintValue) || uintValue != 0u, Is.False);
        Assert.That(code.TryGetUInt(letter, out uint? nullableUInt) || nullableUInt is not null, Is.False);
        Assert.That(code.TryGetLong(letter, out long longValue) || longValue != 0L, Is.False);
        Assert.That(code.TryGetLong(letter, out long? nullableLong) || nullableLong is not null, Is.False);
        Assert.That(code.TryGetBool(letter, out bool boolValue) || boolValue, Is.False);
        Assert.That(code.TryGetBool(letter, out bool? nullableBool) || nullableBool is not null, Is.False);
        Assert.That(code.TryGetString(letter, out string? stringValue) || stringValue is not null, Is.False);
        Assert.That(code.TryGetDriverId(letter, out DriverId? driverId) || driverId is not null, Is.False);
        Assert.That(code.TryGetIPAddress(letter, out IPAddress? address) || address is not null, Is.False);
        Assert.That(code.TryGetFloatArray(letter, out float[]? floatArray) || floatArray is not null, Is.False);
        Assert.That(code.TryGetIntArray(letter, out int[]? intArray) || intArray is not null, Is.False);
        Assert.That(code.TryGetUIntArray(letter, out uint[]? uintArray) || uintArray is not null, Is.False);
        Assert.That(code.TryGetLongArray(letter, out long[]? longArray) || longArray is not null, Is.False);
        Assert.That(code.TryGetDriverIdArray(letter, out DriverId[]? driverIdArray) || driverIdArray is not null, Is.False);
    }

    [Test]
    public void TryGetWithWrongType()
    {
        // The TryGet* accessors convert like the explicit operators, so a value of the wrong type throws instead of returning false
        GCode code = CreateCode();
        Assert.That(() => code.TryGetInt('S', out int _), Throws.InstanceOf<InvalidParameterTypeException>());
        Assert.That(() => code.TryGetString('I', out string? _), Throws.InstanceOf<InvalidParameterTypeException>());
    }

    [Test]
    public void CopyFrom()
    {
        GCode source = new("N12 G53 G1.2 X5 ; move") { FilePosition = 10, Length = 20 };
        GCode copy = new();
        copy.CopyFrom(source);
        Assert.That(copy.Type, Is.EqualTo(CodeType.GCode));
        Assert.That(copy.MajorNumber, Is.EqualTo(1));
        Assert.That(copy.MinorNumber, Is.EqualTo(2));
        Assert.That(copy.LineNumber, Is.EqualTo(12));
        Assert.That(copy.Flags, Is.EqualTo(source.Flags));
        Assert.That(copy.Comment, Is.EqualTo(" move"));
        Assert.That(copy.FilePosition, Is.EqualTo(10));
        Assert.That(copy.Length, Is.EqualTo(20));
        Assert.That(copy.Parameters, Is.SameAs(source.Parameters), "the parameter list is shared, see Code.cs:152");
        Assert.That(copy.ToString(), Is.EqualTo("G53 G1.2 X5 ; move"));

        GCode keyword = new("  echo \"x\"");
        copy.CopyFrom(keyword);
        Assert.That(copy.Keyword, Is.EqualTo(KeywordType.Echo));
        Assert.That(copy.KeywordArgument, Is.EqualTo("\"x\""));
        Assert.That(copy.Indent, Is.EqualTo(2));
    }

    #region Text reconstruction
    [Test]
    public void UnprecedentedString()
    {
        // An unprecedented parameter is returned as is or quoted with doubled quotes (Code.cs:956-962)
        GCode unprecedented = new() { Type = CodeType.MCode, MajorNumber = 32, Parameters = [new CodeParameter('@', "a \"b\".g")] };
        Assert.That(unprecedented.GetUnprecedentedString(), Is.EqualTo("a \"b\".g"));
        Assert.That(unprecedented.GetUnprecedentedString(true), Is.EqualTo("\"a \"\"b\"\".g\""));

        // Else the parameters are joined, strings quoted only if requested (Code.cs:964-984)
        GCode parameters = new() { Type = CodeType.MCode, MajorNumber = 32, Parameters = [new CodeParameter('P', "x\"y"), new CodeParameter('S', 2), new CodeParameter('E', "{1}")] };
        Assert.That(parameters.GetUnprecedentedString(), Is.EqualTo("Px\"y S2 E{1}"));
        Assert.That(parameters.GetUnprecedentedString(true), Is.EqualTo("P\"x\"\"y\" S2 E{1}"));

        Assert.That(new GCode().GetUnprecedentedString(), Is.Empty);
    }

    [TestCase(KeywordType.If, "if")]
    [TestCase(KeywordType.ElseIf, "elif")]
    [TestCase(KeywordType.Else, "else")]
    [TestCase(KeywordType.While, "while")]
    [TestCase(KeywordType.Break, "break")]
    [TestCase(KeywordType.Continue, "continue")]
    [TestCase(KeywordType.Abort, "abort")]
    [TestCase(KeywordType.Var, "var")]
    [TestCase(KeywordType.Set, "set")]
    [TestCase(KeywordType.Echo, "echo")]
    [TestCase(KeywordType.Global, "global")]
    [TestCase(KeywordType.Skip, "skip")]
    public void KeywordToString(KeywordType keyword, string text)
    {
        GCode code = new() { Type = CodeType.Keyword, Keyword = keyword };
        Assert.That(code.ToShortString(), Is.EqualTo(text));
        Assert.That(code.ToString(), Is.EqualTo(text));

        code.KeywordArgument = "1";
        Assert.That(code.ToString(), Is.EqualTo(text + " 1"));

        // A finished code shows its result (Code.cs:996-1000)
        code.Result = new Message(MessageType.Success, "done\n");
        Assert.That(code.ToString(), Is.EqualTo(text + " 1 => done"));
    }

    [Test]
    public void UnknownKeywordToString()
    {
        // KeywordToString (Code.cs:1101-1118) throws for a value that KeywordType does not define
        GCode code = new() { Type = CodeType.Keyword, Keyword = (KeywordType)255 };
        Assert.That(() => code.ToShortString(), Throws.InstanceOf<NotImplementedException>());
    }

    [Test]
    public void CodeToString()
    {
        Assert.That(new GCode().ToShortString(), Is.Empty);
        Assert.That(new GCode().ToString(), Is.Empty);

        GCode comment = new() { Type = CodeType.Comment, Comment = " text" };
        Assert.That(comment.ToShortString(), Is.EqualTo("(comment)"));
        Assert.That(comment.ToString(), Is.EqualTo("; text"));

        // T without a number, minor numbers, the G53 prefix and parameters without a value (Code.cs:1011-1095)
        Assert.That(new GCode { Type = CodeType.TCode }.ToShortString(), Is.EqualTo("T"));
        Assert.That(new GCode { Type = CodeType.GCode, MajorNumber = 29, MinorNumber = 1 }.ToShortString(), Is.EqualTo("G29.1"));
        Assert.That(new GCode { Type = CodeType.GCode, MajorNumber = 1, Flags = CodeFlags.EnforceAbsolutePosition }.ToShortString(), Is.EqualTo("G53 G1"));

        GCode code = new() { Type = CodeType.GCode, MajorNumber = 28, Parameters = [new CodeParameter('X', null), new CodeParameter('@', "free")], Comment = "home" };
        Assert.That(code.ToString(), Is.EqualTo("G28 X \"free\" ;home"), "strings are quoted, also without a letter (Code.cs:1022-1034)");

        // Parameters only, as in Fanuc style lines without a command
        GCode noType = new() { Parameters = [new CodeParameter('X', 1)] };
        Assert.That(noType.ToString(), Is.EqualTo("X1"));

        // The result of a finished code is appended without the trailing line break (Code.cs:1055-1059)
        code.Result = new Message(MessageType.Warning, "careful\n");
        Assert.That(code.ToString(), Is.EqualTo("G28 X \"free\" ;home => Warning: careful"));
    }
    #endregion

    #region Code type converter
    [TestCase(CodeType.None, "\"\"")]
    [TestCase(CodeType.Comment, "\"Q\"")]
    [TestCase(CodeType.Keyword, "\"K\"")]
    [TestCase(CodeType.GCode, "\"G\"")]
    [TestCase(CodeType.MCode, "\"M\"")]
    [TestCase(CodeType.TCode, "\"T\"")]
    public void CodeTypeJson(CodeType type, string json)
    {
        // CodeTypeConverter writes the letter of the type (Type.cs:72-85) and reads it back (Type.cs:51-70)
        Assert.That(JsonSerializer.Serialize(type), Is.EqualTo(json));
        Assert.That(JsonSerializer.Deserialize<CodeType>(json), Is.EqualTo(type));
    }

    [TestCase("\"X\"")]
    [TestCase("1")]
    public void InvalidCodeTypeJson(string json) => Assert.That(() => JsonSerializer.Deserialize<CodeType>(json), Throws.InstanceOf<JsonException>());

    [Test]
    public void UnknownCodeTypeToJson()
    {
        using System.IO.MemoryStream stream = new();
        using Utf8JsonWriter writer = new(stream);
        Assert.That(() => new CodeTypeConverter().Write(writer, (CodeType)'X', new JsonSerializerOptions()), Throws.InstanceOf<JsonException>());
    }

    [Test]
    public void CodeJsonRoundTrip()
    {
        // Codes travel as JSON over IPC, the type and the parameters use the converters
        GCode code = new("M106 P1 S0.5 C\"fan\"");
        string json = JsonSerializer.Serialize(code);
        GCode copy = JsonSerializer.Deserialize<GCode>(json)!;
        Assert.That(copy.ToString(), Is.EqualTo(code.ToString()));
        Assert.That(json, Does.Contain("\"type\":\"M\"").Or.Contain("\"Type\":\"M\""));
    }
    #endregion
}
