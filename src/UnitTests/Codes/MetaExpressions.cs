using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using DuetControlServer.Codes.Meta;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using Code = DuetControlServer.Commands.Code;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Codes;

/// <summary>
/// Tests for the meta G-code expressions that DSF evaluates or substitutes itself (Codes/Meta/Expressions.cs), its functions
/// fileexists, fileread and exists (Codes/Meta/Functions.cs) and the keywords echo, var, global, set and skip (KeywordHandler)
/// </summary>
[TestFixture]
public class MetaExpressions
{
    private FakeFirmwareHost _host = null!;

    [SetUp]
    public async Task SetUp() => _host = await FakeFirmwareHost.StartAsync();

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    private Expressions Expressions => _host.Services.GetRequiredService<Expressions>();

    private Functions Functions => _host.Services.GetServices<IHostedService>().OfType<Functions>().Single();

    /// <summary>
    /// Create a code on the HTTP channel that is not part of a file
    /// </summary>
    private Code CreateCode()
    {
        Code code = _host.Services.GetRequiredService<CodeFactory>().Create();
        code.Channel = CodeChannel.HTTP;
        return code;
    }

    private string WriteFile(string relativePath, string content)
    {
        string path = Path.Combine(_host.BaseDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    #region echo
    [Test]
    public async Task Echo()
    {
        // The expressions are separated by spaces (wiki, Echo command). 1+1 and the SBC field directories.system are evaluated by DSF,
        // the expression with the firmware value move.axes[0].letter is forwarded after DSF has substituted its own fields (Expressions.cs:1009-1042)
        Assert.That(await _host.ExecuteAsync("echo 1+1, \"a\", directories.system, directories.system ^ move.axes[0].letter"), Is.EqualTo("2 a 0:/sys/ 0:/sys/X\n"));
    }

    [Test]
    public async Task EchoToFile()
    {
        // > creates the file, >> appends a line, >>> appends without a newline; the default folder is /sys (wiki, Echo command)
        Assert.That(await _host.ExecuteAsync("echo >\"out.txt\" \"a\", 1"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("echo >>\"out.txt\" \"b\""), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("echo >>>\"out.txt\" \"c\""), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("echo >>\"out.txt\" \"d\""), Is.EqualTo("\n"));

        byte[] content = await System.IO.File.ReadAllBytesAsync(Path.Combine(_host.BaseDirectory, "sys", "out.txt"));
        Assert.That(System.Text.Encoding.UTF8.GetString(content), Is.EqualTo("a 1\nb\ncd\n"));
        Assert.That(content[0], Is.EqualTo((byte)'a'), "the file has no byte order mark (KeywordHandler.cs:151)");

        // > replaces the file
        Assert.That(await _host.ExecuteAsync("echo >\"out.txt\" \"new\""), Is.EqualTo("\n"));
        Assert.That(await System.IO.File.ReadAllTextAsync(Path.Combine(_host.BaseDirectory, "sys", "out.txt")), Is.EqualTo("new\n"));
    }

    [Test]
    public async Task EchoToFileWithExpression()
    {
        // The file name may be an expression in { }. KeywordHandler creates missing directories (KeywordHandler.cs:144-147)
        Assert.That(await _host.ExecuteAsync("echo >{\"0:/sys/\" ^ \"sub/x.txt\"} \"y\""), Is.EqualTo("\n"));
        Assert.That(await System.IO.File.ReadAllTextAsync(Path.Combine(_host.BaseDirectory, "sys", "sub", "x.txt")), Is.EqualTo("y\n"));

        // A character literal also gives a file name (the single quote branch in KeywordHandler.cs:83-91)
        Assert.That(await _host.ExecuteAsync("echo >'z' \"q\""), Is.EqualTo("\n"));
        Assert.That(await System.IO.File.ReadAllTextAsync(Path.Combine(_host.BaseDirectory, "sys", "z")), Is.EqualTo("q\n"));
    }

    [Test]
    public async Task EchoToFileOutsideVolume()
    {
        // FilePathResolver refuses paths that leave the volume (FilePathResolver.cs:63-72), SimpleCode reports the ArgumentException
        string reply = await _host.ExecuteAsync("echo >\"../../escaped.txt\" \"x\"");
        Assert.That(reply, Does.StartWith("Error: ").And.Contain("escapes the volume root directory"));
        Assert.That(System.IO.File.Exists(Path.Combine(_host.BaseDirectory, "..", "escaped.txt")), Is.False);
    }

    [Test]
    public async Task Skip() => Assert.That(await _host.ExecuteAsync("skip"), Is.EqualTo("\n"));
    #endregion

    #region Variables
    [Test]
    public async Task GlobalVariable()
    {
        Assert.That(await _host.ExecuteAsync("global g = 1"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("set global.g = global.g + 1"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("echo global.g"), Is.EqualTo("2\n"));
    }

    [TestCase("var = 1", "expected a new variable name")]
    [TestCase("var x", "expected '='")]
    [TestCase("var x y = 1", "expected '='")]
    [TestCase("var x! = 1", "expected '='")]
    [TestCase("global a.b = 1", "expected '='")]
    public async Task InvalidVariableDeclaration(string code, string error)
    {
        // KeywordHandler validates the name and the '=' before it asks the firmware (KeywordHandler.cs:180-258). Dots are only allowed for set.
        // SimpleCode reports the CodeParserException without a line break (SimpleCode.cs:190-192)
        Assert.That(await _host.ExecuteAsync(code), Is.EqualTo($"Error: {error}"));
        Assert.That(_host.Firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.SetVariable), Is.False);
    }

    [Test]
    public async Task AssignmentWithIndex()
    {
        // The index of an assignment is passed on as is, including spaces and quotes inside the brackets (KeywordHandler.cs:194-223)
        await _host.ExecuteAsync("set global.arr[ 1 ] = 5");
        Assert.That(_host.Firmware.GetReceivedRequests().Where(request => request.Request == SbcRequest.SetVariable).Select(request => request.Text),
                    Has.Some.EqualTo("global.arr[ 1 ]"));
    }
    #endregion

    #region Functions
    [Test]
    public async Task FileExists()
    {
        // fileexists is true for files only (wiki, Functions), DSF resolves the path relative to 0:/ (Functions.cs:65-73)
        WriteFile("sys/a.g", "G1 X1\n");
        Assert.That(await _host.ExecuteAsync("echo fileexists(\"0:/sys/a.g\"), fileexists(\"sys/a.g\"), fileexists(\"0:/sys\"), fileexists(\"missing.g\")"),
                    Is.EqualTo("true true false false\n"));
        Assert.That(await _host.ExecuteAsync("echo fileexists(1)"), Is.EqualTo("Error: fileexists requires a string argument"));
    }

    [Test]
    public async Task Exists()
    {
        // An SBC field is answered by DSF, anything else by the firmware (Functions.cs:44-56)
        Assert.That(await _host.ExecuteAsync("echo exists(directories.system), exists(global.nothing), exists(move.axes)"), Is.EqualTo("true false true\n"));
        Assert.That(await Functions.Exists(CodeChannel.HTTP, "exists", ["directories.web"]), Is.EqualTo(true));
        Assert.That(async () => await Functions.Exists(CodeChannel.HTTP, "exists", []), Throws.ArgumentException.With.Message.EqualTo("exists requires an argument"));
    }

    [Test]
    public async Task FileReadFromEcho()
    {
        WriteFile("sys/data.csv", "1,2,3\n");
        Assert.That(await _host.ExecuteAsync("echo #fileread(\"0:/sys/data.csv\", 0, 5, ',')"), Is.EqualTo("3\n"));
        Assert.That(await _host.ExecuteAsync("echo fileread(\"0:/sys/data.csv\", 0, 5)"), Is.EqualTo("Error: fileread requires 4 arguments"));
    }

    private async Task<object?[]> FileReadAsync(string content, int offset, int length, char delimiter = ',')
    {
        WriteFile("sys/data.csv", content);
        return (object?[])(await Functions.FileRead(CodeChannel.HTTP, "fileread", ["0:/sys/data.csv", offset, length, delimiter]))!;
    }

    [Test]
    public async Task FileReadElements()
    {
        // The wiki (Notes on the fileread function): strings in double quotes with doubled quotes and separators inside, numbers,
        // characters in single quotes and empty elements, which become null. Spaces around elements are ignored. Only the first line is read
        Assert.That(await FileReadAsync(" 1 , 2.5,\"a,\"\"b\" ,'c',, -3\nsecond line", 0, 10), Is.EqualTo(new object?[] { 1, 2.5f, "a,\"b", 'c', null, -3 }));

        // The offset skips elements and the length limits them
        Assert.That(await FileReadAsync("1,2,3,4,5", 1, 2), Is.EqualTo(new object?[] { 2, 3 }));

        // Another separator character, and numbers that do not fit into an int
        Assert.That(await FileReadAsync("1;9999999999;+4", 0, 5, ';'), Is.EqualTo(new object?[] { 1, 9999999999f, 4 }));

        // "an empty file will give rise to an array comprising a single null element"
        Assert.That(await FileReadAsync(string.Empty, 0, 5), Is.EqualTo(new object?[] { null }));

        // "The number of array elements returned will be one greater than the number of separator characters found"
        Assert.That(await FileReadAsync("1,", 0, 5), Is.EqualTo(new object?[] { 1, null }));
    }

    [TestCase("abc", "in fileread() function: format error at column 0")]
    [TestCase("1x", "in fileread() function: format error at column 0")]
    [TestCase("1,\"abc", "in fileread() function: unterminated string after column 2")]
    [TestCase("x\"a\"", "in fileread() function: format error at column 0")]
    [TestCase("\"a\"b", "in fileread() function: unexpected value after element at column 0")]
    [TestCase("'", "in fileread() function: expected character after column 0")]
    [TestCase("'a", "in fileread() function: unterminated single quote at column 0")]
    public void FileReadFormatError(string content, string error)
    {
        // "if any elements do not conform to the above, then the command containing the fileread call will be aborted" (wiki)
        Assert.That(async () => await FileReadAsync(content, 0, 5), Throws.ArgumentException.With.Message.EqualTo(error));
    }

    private static IEnumerable<TestCaseData> InvalidFileReadArguments()
    {
        // Functions.cs:84-107
        yield return new TestCaseData(new object?[] { "a", 0, 1 }, "fileread requires 4 arguments").SetName("TooFewArguments");
        yield return new TestCaseData(new object?[] { 1, 0, 1, ',' }, "fileread requires a string parameter for the filename").SetName("NoFileName");
        yield return new TestCaseData(new object?[] { "a", -1, 1, ',' }, "fileread requires a non-negative offset").SetName("NegativeOffset");
        yield return new TestCaseData(new object?[] { "a", 0, "1", ',' }, "fileread requires a positive number of elements to read").SetName("LengthNotInt");
        yield return new TestCaseData(new object?[] { "a", 0, 51, ',' }, "fileread cannot read more than 50 elements at once").SetName("TooManyElements");
        yield return new TestCaseData(new object?[] { "a", 0, 1, "," }, "fileread requires a delimiter character").SetName("DelimiterNotChar");
    }

    [TestCaseSource(nameof(InvalidFileReadArguments))]
    public void FileReadArguments(object?[] arguments, string error) =>
        Assert.That(async () => await Functions.FileRead(CodeChannel.HTTP, "fileread", arguments), Throws.ArgumentException.With.Message.EqualTo(error));

    [Test]
    public void FileReadMissingFile()
    {
        // "If the file cannot be opened and read ... the command containing the fileread call will be aborted" (wiki)
        Assert.That(async () => await Functions.FileRead(CodeChannel.HTTP, "fileread", ["0:/sys/missing.csv", 0, 1, ',']), Throws.InstanceOf<FileNotFoundException>());
    }

    // The following cases document where DSF's fileread deviates from RRF's ReadArrayFromFile (src/GCodes/GCodeBuffer/ExpressionParser.cpp)
    // and from the wiki. They fail on the current code

    [Test, Explicit("fileread accepts up to 50 elements, RRF only 40 (Configuration.h:281 MaxFileReadArrayElements, ExpressionParser.cpp:1192-1195)")]
    public void FileReadElementLimitMatchesFirmware() =>
        Assert.That(async () => await FileReadAsync("1", 0, 41), Throws.ArgumentException);

    [Test, Explicit("fileread rejects a length of 0, RRF only rejects negative lengths (ExpressionParser.cpp:2179-2183)")]
    public async Task FileReadZeroElements() => Assert.That(await FileReadAsync("1,2", 0, 0), Is.Empty);

    [Test, Explicit("fileread does not parse hex numbers, RRF does (NumericConverter::AcceptHex, ExpressionParser.cpp:1319)")]
    public async Task FileReadHexNumber() => Assert.That(await FileReadAsync("0x10", 0, 1), Is.EqualTo(new object?[] { 16 }));

    [Test, Explicit("fileread accepts a character literal without its closing quote, RRF reports a format error (ExpressionParser.cpp:1268-1283)")]
    public void FileReadUnterminatedCharacter() => Assert.That(async () => await FileReadAsync("'ab", 0, 1), Throws.ArgumentException);

    [Test, Explicit("fileread does not check skipped elements, the wiki: 'Each element (including each skipped element) must be one of the following'")]
    public void FileReadInvalidSkippedElement() => Assert.That(async () => await FileReadAsync("abc,1", 1, 1), Throws.ArgumentException);

    [Test, Explicit("fileread joins numbers separated by spaces, RRF reports a format error (ExpressionParser.cpp:1344-1358)")]
    public void FileReadSpaceInsideNumber() => Assert.That(async () => await FileReadAsync("1 2", 0, 1), Throws.ArgumentException);
    #endregion

    #region Substitution of SBC fields
    /// <summary>
    /// Substitute the SBC fields of an expression like DSF does before it forwards a code to the firmware
    /// </summary>
    private Task<string> SubstituteAsync(string expression, Code? code = null) => Expressions.EvaluateExpressionToStringAsync(code ?? CreateCode(), expression, true, false);

    [Test]
    public async Task SubstituteScalars()
    {
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Network.CorsSite = "a\"b'c";
            _host.Model.Job.LastFileAborted = true;
            _host.Model.State.LogLevel = EventLogLevel.Info;
        }

        // Strings are quoted with doubled quotes, # gives their length, enums their JSON value (Expressions.cs:502-620)
        Assert.That(await SubstituteAsync("network.corsSite"), Is.EqualTo("\"a\"\"b''c\""));
        Assert.That(await SubstituteAsync("#network.corsSite"), Is.EqualTo("5"));
        Assert.That(await SubstituteAsync("job.lastFileAborted && true"), Is.EqualTo("true&& true"));
        Assert.That(await SubstituteAsync("state.logLevel"), Is.EqualTo("\"info\""));
        Assert.That(await SubstituteAsync("move.axes[0].letter"), Is.EqualTo("move.axes[0].letter"), "fields of the firmware are left alone");
        Assert.That(await SubstituteAsync("max(1, directories.system)"), Is.EqualTo("max(1,\"0:/sys/\")"));
        Assert.That(await SubstituteAsync("{directories.web}"), Is.EqualTo("{\"0:/www/\"}"));
        Assert.That(await SubstituteAsync("\"directories.system\" ^ 'd'"), Is.EqualTo("\"directories.system\" ^ 'd'"), "strings and characters are not substituted");

        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Network.CorsSite = null;
        }
        Assert.That(await SubstituteAsync("network.corsSite"), Is.EqualTo("null"));
    }

    [Test]
    public async Task SubstituteArrays()
    {
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Volumes.Clear();
        }
        Assert.That(await SubstituteAsync("volumes"), Is.EqualTo("vector(0,0)"), "an empty list (Expressions.cs:570-573)");
        Assert.That(await SubstituteAsync("#volumes"), Is.EqualTo("0"));

        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Volumes.Add(new Volume { FreeSpace = 12345 });
        }
        Assert.That(await SubstituteAsync("#volumes"), Is.EqualTo("1"));
        Assert.That(await SubstituteAsync("volumes[0].freeSpace"), Is.EqualTo("12345"));
        Assert.That(() => SubstituteAsync("volumes"), Throws.InstanceOf<CodeParserException>().With.Message.EqualTo("missing array index"));

        // The index of an SBC field is evaluated, by the firmware if needed (Expressions.cs:917-940)
        Assert.That(await SubstituteAsync("volumes[1 - 1].freeSpace"), Is.EqualTo("12345"));
        Assert.That(() => SubstituteAsync("volumes[\"a\"].freeSpace"), Throws.InstanceOf<CodeParserException>().With.Message.EqualTo("Index value in square brackets must be of type integer"));
    }

    [Test]
    public async Task SubstituteFunctionResults()
    {
        // Custom functions are called by DSF and their results are encoded as literals, arrays with a trailing comma if they have one element (Expressions.cs:886-907)
        WriteFile("sys/data.csv", "1, 2.5, \"a\", 'c', , \n");
        Assert.That(await SubstituteAsync("fileread(\"0:/sys/data.csv\", 0, 10, ',')"), Is.EqualTo("{1,2.5,\"a\",'c',null,null}"));
        Assert.That(await SubstituteAsync("fileread(\"0:/sys/data.csv\", 0, 1, ',')"), Is.EqualTo("{1,}"));
        Assert.That(await SubstituteAsync("#fileread(\"0:/sys/data.csv\", 0, 10, ',')"), Is.EqualTo("6"));
        Assert.That(await SubstituteAsync("fileexists(\"0:/sys/data.csv\") && fileexists(\"0:/sys/nothing\")"), Is.EqualTo("true &&false"), "the result replaces the function call including the spaces in front of its name");
    }

    [TestCase("\"abc", "Unterminated quotes")]
    [TestCase("'a", "Unterminated quotes")]
    [TestCase("'", "Unterminated quotes")]
    [TestCase("1)", "Unexpected round bracket")]
    [TestCase("1]", "Unexpected square bracket")]
    [TestCase("1}", "Unexpected curly bracket")]
    [TestCase("(1", "Unterminated round bracket")]
    [TestCase("a[1", "Unterminated square bracket")]
    [TestCase("{1", "Unterminated curly bracket")]
    [TestCase("iterations", "not executing a file")]
    [TestCase("line + 1", "not executing a file")]
    public void SubstitutionError(string expression, string error) =>
        Assert.That(() => SubstituteAsync(expression), Throws.InstanceOf<CodeParserException>().With.Message.EqualTo(error));

    [Test]
    public async Task SubstituteParameters()
    {
        // Expressions in parameters are encapsulated in curly braces again so that RRF knows they were expressions (Expressions.cs:457-484)
        Code code = CreateCode();
        code.Type = DuetAPI.Commands.CodeType.MCode;
        code.MajorNumber = 117;
        code.Parameters.Add(new DuetAPI.Commands.CodeParameter('S', "{directories.system}", false, false));
        code.Parameters.Add(new DuetAPI.Commands.CodeParameter('P', 1));
        Assert.That(Expressions.ContainsSbcFields(code), Is.True);
        Assert.That(await Expressions.EvaluateAsync(code, false), Is.Null);
        Assert.That(code.ToString(), Is.EqualTo("M117 S{\"0:/sys/\"} P1"));

        // With all expressions evaluated, the value is a plain parameter
        code.Parameters[0] = new DuetAPI.Commands.CodeParameter('S', "{1 + 2}", false, false);
        await Expressions.EvaluateAsync(code, true);
        Assert.That(code.ToString(), Is.EqualTo("M117 S3 P1"));
    }
    #endregion

    #region Detection of SBC fields
    [TestCase("directories.system", false, true)]
    [TestCase("volumes[0].freeSpace", false, true)]
    [TestCase("move.axes[0].letter", false, false)]
    [TestCase("move..axes", false, false)]
    [TestCase("nothing.here", false, false)]
    [TestCase("iterations", false, true)]
    [TestCase("line", false, true)]
    [TestCase("fileexists", true, true)]
    [TestCase("sin", true, false)]
    public void IsSbcExpression(string expression, bool isFunction, bool expected) => Assert.That(Expressions.IsSbcExpression(expression, isFunction), Is.EqualTo(expected));

    [TestCase("echo 1, directories.system", true)]
    [TestCase("echo 1, \"directories.system\"", false)]
    [TestCase("echo move.axes[0].letter", false)]
    [TestCase("if directories.system == \"x\"", true)]
    [TestCase("if foo sin (3)", false)]
    [TestCase("M117 S{fileexists(\"x\")}", true)]
    [TestCase("M117 S{move.axes[0].letter}", false)]
    [TestCase("M117 S\"directories.system\"", false)]
    public void ContainsSbcFields(string text, bool expected)
    {
        Code code = CreateCode();
        code.CopyFrom(new DuetAPI.Commands.Code(text));
        Assert.That(Expressions.ContainsSbcFields(code), Is.EqualTo(expected));
    }

    [Test]
    public void ExtractFieldPaths()
    {
        // Indices are stripped and every dotted identifier counts, but not the contents of strings (Expressions.cs:261-335)
        Assert.That(Expressions.ExtractFieldPaths("tools[0].current > heat.heaters[var.i].current && \"a.b\" == x || 1.5 < state.status"),
                    Is.EquivalentTo(new[] { "tools.current", "heat.heaters.current", "state.status" }));
        Assert.That(Expressions.ExtractFieldPaths("move.axes"), Is.EquivalentTo(new[] { "move.axes" }));
        Assert.That(Expressions.ExtractFieldPaths("a b.c"), Is.EquivalentTo(new[] { "b.c" }));
        Assert.That(Expressions.ExtractFieldPaths("1 + 2"), Is.Empty);
    }
    #endregion
}
