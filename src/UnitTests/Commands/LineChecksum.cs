using DuetAPI;
using DuetAPI.Commands;
using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using static UnitTests.Commands.Code;

namespace UnitTests.Commands;

/// <summary>
/// Tests for the checksum, CRC and line number checks of numbered G-code lines
/// </summary>
[TestFixture]
public class LineChecksum
{
    [Test]
    public void ParseLineChecksum()
    {
        // XOR checksum with 1-3 digits
        foreach (DuetAPI.Commands.Code code in Parse("N1 G1 X10*80"))
        {
            Assert.That(code.LineNumber, Is.EqualTo(1));
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode));
            Assert.That(code.MajorNumber, Is.EqualTo(1));
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.GetInt('X'), Is.EqualTo(10));
        }

        // CRC-16 with exactly 5 digits
        foreach (DuetAPI.Commands.Code code in Parse("N1 G1 X10*34671"))
        {
            Assert.That(code.LineNumber, Is.EqualTo(1));
            Assert.That(code.MajorNumber, Is.EqualTo(1));
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.GetInt('X'), Is.EqualTo(10));
        }

        // Example from the Duet3D wiki (CRC covers every byte before the asterisk)
        foreach (DuetAPI.Commands.Code code in Parse("N100 G0 X100 F6000*51369"))
        {
            Assert.That(code.LineNumber, Is.EqualTo(100));
            Assert.That(code.MajorNumber, Is.EqualTo(0));
            Assert.That(code.GetInt('X'), Is.EqualTo(100));
            Assert.That(code.GetInt('F'), Is.EqualTo(6000));
        }

        // CRLF line ending and trailing content after the digits are discarded like RRF does
        foreach (DuetAPI.Commands.Code code in Parse("N1 G1 X10*34671 ; comment\r\n"))
        {
            Assert.That(code.MajorNumber, Is.EqualTo(1));
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Comment, Is.Null);
        }

        // Asterisks inside expressions and quoted strings do not start a checksum block
        foreach (DuetAPI.Commands.Code code in Parse("N7 G1 X{2*3}*122"))
        {
            Assert.That(code.LineNumber, Is.EqualTo(7));
            Assert.That(code.Parameters.Count, Is.EqualTo(1));
            Assert.That(code.Parameters[0].IsExpression, Is.True);
            Assert.That((string)code.Parameters[0], Is.EqualTo("{2*3}"));
        }
        foreach (DuetAPI.Commands.Code code in Parse("N1 M117 \"a*b\"*44"))
        {
            Assert.That(code.MajorNumber, Is.EqualTo(117));
            Assert.That((string)code.Parameters[0], Is.EqualTo("a*b"));
        }

        // Comment lines are not checked
        foreach (DuetAPI.Commands.Code code in Parse("N1 ;hello*world"))
        {
            Assert.That(code.Type, Is.EqualTo(CodeType.Comment));
            Assert.That(code.Comment, Is.EqualTo("hello*world"));
        }
        foreach (DuetAPI.Commands.Code code in Parse("N1 G1 X10 ;c*5"))
        {
            Assert.That(code.MajorNumber, Is.EqualTo(1));
            Assert.That(code.Comment, Is.EqualTo("c*5"));
        }

        // Numbered lines without a checksum still work
        foreach (DuetAPI.Commands.Code code in Parse("N2 G1 Y5"))
        {
            Assert.That(code.LineNumber, Is.EqualTo(2));
            Assert.That(code.GetInt('Y'), Is.EqualTo(5));
        }
    }

    [Test]
    public async Task ParseBadLineChecksum()
    {
        foreach (string line in new[] { "N1 G1 X10*81", "N1 G1 X10*34670", "N1 G1 X10*1234", "N1 G1 X10*", "N1 G1 X10*123456", "N1 M117 Hello*World*80" })
        {
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(line));
            CodeParserBuffer buffer = new(128, false);
            DuetAPI.Commands.Code result = new();
            await Assert.CatchAsync<CodeParserException>(async () => await DuetAPI.Commands.Code.ParseAsync(stream, result, buffer), line);
        }

        // Comment-only lines are not verified
        foreach (string line in new[] { "N1 (comment)*99", "N1 (comment)*12345" })
        {
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(line));
            CodeParserBuffer buffer = new(128, false);
            DuetAPI.Commands.Code result = new();
            await Assert.DoesNotThrowAsync(async () => await DuetAPI.Commands.Code.ParseAsync(stream, result, buffer), line);
        }

        // Lines without a line number are not affected
        Assert.DoesNotThrow(() => new DuetAPI.Commands.Code("G1 X10 ; *81"));
    }

    [Test]
    public void ParseLineChecksumSynchronously()
    {
        // The synchronous parser strips the checksum or CRC block without verifying it
        foreach (string line in new[] { "N1 G1 X10*80", "N1 G1 X10*81", "N1 G1 X10*34670", "N1 G1 X10*1234", "N1 G1 X10*", "N1 G1 X10*80 ; comment\r\n" })
        {
            DuetAPI.Commands.Code code = new(line);
            Assert.That(code.LineNumber, Is.EqualTo(1), line);
            Assert.That(code.Type, Is.EqualTo(CodeType.GCode), line);
            Assert.That(code.MajorNumber, Is.EqualTo(1), line);
            Assert.That(code.Parameters.Count, Is.EqualTo(1), line);
            Assert.That(code.GetInt('X'), Is.EqualTo(10), line);
            Assert.That(code.Comment, Is.Null, line);
            Assert.That(code.Flags.HasFlag(CodeFlags.IsLastCode), Is.True, line);
        }

        // Expressions, quoted strings and comments keep their asterisks
        DuetAPI.Commands.Code expressionCode = new("N7 G1 X{2*3}*99");
        Assert.That((string)expressionCode.Parameters[0], Is.EqualTo("{2*3}"));
        DuetAPI.Commands.Code stringCode = new("N1 M117 \"a*b\"*99");
        Assert.That((string)stringCode.Parameters[0], Is.EqualTo("a*b"));
        DuetAPI.Commands.Code commentCode = new("N1 G1 X10 ;c*5");
        Assert.That(commentCode.Comment, Is.EqualTo("c*5"));

        // The whole checksum block counts towards the code length and the next line is read normally
        using StringReader reader = new("N1 G1 X10*80\nG1 Y5\n");
        DuetAPI.Commands.Code result = new();
        Assert.That(DuetAPI.Commands.Code.Parse(reader, result), Is.True);
        Assert.That(result.GetInt('X'), Is.EqualTo(10));
        Assert.That(result.Length, Is.EqualTo(13));
        result.Reset();
        Assert.That(DuetAPI.Commands.Code.Parse(reader, result), Is.True);
        Assert.That(result.LineNumber, Is.Null);
        Assert.That(result.GetInt('Y'), Is.EqualTo(5));
    }

    [Test]
    public async Task ParseLineChecksumMultipleCodes()
    {
        // The whole line is verified before the first code is returned
        byte[] codeBytes = Encoding.UTF8.GetBytes("N1 G91 G1 X10*99999\n");
        await using (MemoryStream memoryStream = new(codeBytes))
        {
            CodeParserBuffer buffer = new(128, true);
            DuetAPI.Commands.Code code = new();
            await Assert.CatchAsync<CodeParserException>(async () => await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer));
        }

        // Both codes are served from the verified line and the lengths add up to the next line
        codeBytes = Encoding.UTF8.GetBytes("N1 G91 G1 X10*63\nN2 G1 Y5*07249\nG90");    // CRCs must be zero-padded to 5 digits
        await using (MemoryStream memoryStream = new(codeBytes))
        {
            CodeParserBuffer buffer = new(8, true);
            DuetAPI.Commands.Code code = new();

            Assert.That(await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer), Is.True);
            Assert.That(code.LineNumber, Is.EqualTo(1));
            Assert.That(code.MajorNumber, Is.EqualTo(91));
            Assert.That(code.FilePosition, Is.EqualTo(0));
            Assert.That(code.Length, Is.EqualTo(8));     // the parser counts the character that starts the next code

            code.Reset();
            Assert.That(await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer), Is.True);
            Assert.That(code.LineNumber, Is.EqualTo(1));
            Assert.That(code.MajorNumber, Is.EqualTo(1));
            Assert.That(code.GetInt('X'), Is.EqualTo(10));
            Assert.That(code.FilePosition, Is.EqualTo(7));
            Assert.That(code.Length, Is.EqualTo(10));
            Assert.That(code.Flags.HasFlag(CodeFlags.IsLastCode), Is.True);

            code.Reset();
            Assert.That(await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer), Is.True);
            Assert.That(code.LineNumber, Is.EqualTo(2));
            Assert.That(code.GetInt('Y'), Is.EqualTo(5));
            Assert.That(code.FilePosition, Is.EqualTo(17));
            Assert.That(code.Length, Is.EqualTo(15));

            code.Reset();
            Assert.That(await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer), Is.True);
            Assert.That(code.MajorNumber, Is.EqualTo(90));
            Assert.That(code.FilePosition, Is.EqualTo(32));
        }

        // The synchronous parser returns the first code of a numbered line without reading the rest of the line
        DuetAPI.Commands.Code firstCode = new("N1 G91 G1 X10*63");
        Assert.That(firstCode.MajorNumber, Is.EqualTo(91));
    }

    [Test]
    public async Task ParseLineNumberAfterNumberedLine()
    {
        // Lines following a numbered line continue counting from its line number
        byte[] codeBytes = Encoding.UTF8.GetBytes("N5 G1 X1\nG1 Y2\nN7 G91 G1 Z3\nG1 Z4\n");
        await using MemoryStream memoryStream = new(codeBytes);
        CodeParserBuffer buffer = new(128, false);
        DuetAPI.Commands.Code code = new();
        foreach (long expectedLineNumber in new long[] { 5, 6, 7, 7, 8 })
        {
            code.Reset();
            Assert.That(await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer), Is.True);
            Assert.That(code.LineNumber, Is.EqualTo(expectedLineNumber));
        }
    }

    private static string WithChecksum(string line) => $"{line}*{DuetAPI.Commands.Code.ComputeLineChecksum(Encoding.ASCII.GetBytes(line))}";

    private static string WithCrc(string line) => $"{line}*{DuetAPI.Commands.Code.ComputeLineCrc(Encoding.ASCII.GetBytes(line)):D5}";

    private static async Task<List<DuetAPI.Commands.Code>> ParseEnforcedAsync(string content, CodeParserBuffer? buffer = null)
    {
        await using MemoryStream memoryStream = new(Encoding.UTF8.GetBytes(content));
        buffer ??= new(128, true) { EnforceLineNumbers = true };
        List<DuetAPI.Commands.Code> codes = [];
        while (true)
        {
            DuetAPI.Commands.Code code = new();
            if (!await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer) && buffer.GetPosition(memoryStream) >= memoryStream.Length)
            {
                break;
            }
            codes.Add(code);
        }
        return codes;
    }

    [Test]
    public async Task ParseLineNumberSequence()
    {
        // Numbering may start at any line and with any value. Blank, comment and unnumbered lines count too
        List<DuetAPI.Commands.Code> codes = await ParseEnforcedAsync("; header\nG28\nN10 G1 X1\n\n; comment\nG1 Y1\nN14 G91 G1 Z1\nN15 M400\n");
        Assert.That(codes.FindAll(code => code.Type != CodeType.None).ConvertAll(code => code.LineNumber), Is.EqualTo(new long?[] { 1, 2, 10, 12, 13, 14, 14, 15 }));

        // Duplicate, skipped and lower line numbers are rejected
        foreach (string content in new[] { "N1 G1 X1\nN1 G1 X2\n", "N1 G1 X1\nN3 G1 X2\n", "N5 G1 X1\nN4 G1 X2\n", "N1 G1 X1\nG1 X2\nN2 G1 X3\n" })
        {
            await Assert.CatchAsync<CodeParserException>(async () => await ParseEnforcedAsync(content), content);
        }

        // Example of a corrupted macro file. The added line lacks the CRC used by the other lines
        const string macro = "N1 var bed_temp = 90.0 *21270 ; Bed temperature in °C\nN2 var tool_temp = 180.0 *02760 ; Tool temperature in °C\n\nN4 var wait_for_heatsoak = true *37938\n";
        await Assert.DoesNotThrowAsync(async () => await ParseEnforcedAsync(macro));
        CodeParserException? e = await Assert.CatchAsync<CodeParserException>(async () => await ParseEnforcedAsync(macro + "N6 var test = 0\n"));
        Assert.That(e?.Message, Is.EqualTo("Missing CRC on line N6"));
        e = await Assert.CatchAsync<CodeParserException>(async () => await ParseEnforcedAsync(macro + WithCrc("N6 var test = 0 ") + "\n"));
        Assert.That(e?.Message, Is.EqualTo("Expected line number N5 but got N6"));

        // Without enforcement the line numbers are taken as they are
        await Assert.DoesNotThrowAsync(async () => await ParseEnforcedAsync("N1 G1 X1\nN1 G1 X2\nN7 G1 X3\n", new CodeParserBuffer(128, true)));
    }

    [Test]
    public async Task ParseLineNumberSequenceAfterSeek()
    {
        const string content = "N1 G1 X1\nN2 G1 X2\nN3 G1 X3\n";
        await using MemoryStream memoryStream = new(Encoding.UTF8.GetBytes(content));
        CodeParserBuffer buffer = new(128, true) { EnforceLineNumbers = true };
        DuetAPI.Commands.Code code = new();

        Assert.That(await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer), Is.True);
        Assert.That(buffer.LineNumbersStarted, Is.True);
        long? loopLineNumber = code.LineNumber;
        code.Reset();
        Assert.That(await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer), Is.True);

        // Seeking to an arbitrary position resynchronises on the next numbered line
        memoryStream.Seek(content.IndexOf("N3"), SeekOrigin.Begin);
        buffer.Invalidate();
        Assert.That(buffer.LineNumbersStarted, Is.False);
        code.Reset();
        Assert.That(await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer), Is.True);
        Assert.That(code.LineNumber, Is.EqualTo(3));

        // Restarting a loop restores the line number and checks the next line against it
        memoryStream.Seek(0, SeekOrigin.Begin);
        buffer.Invalidate();
        buffer.LineNumber = loopLineNumber;
        buffer.LineNumbersStarted = true;
        code.Reset();
        Assert.That(await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer), Is.True);
        Assert.That(code.LineNumber, Is.EqualTo(1));

        memoryStream.Seek(0, SeekOrigin.Begin);
        buffer.Invalidate();
        buffer.LineNumber = 2;
        buffer.LineNumbersStarted = true;
        code.Reset();
        await Assert.CatchAsync<CodeParserException>(async () => await DuetAPI.Commands.Code.ParseAsync(memoryStream, code, buffer));
    }

    [Test]
    public async Task ParseRequiredLineChecksumType()
    {
        // Numbered lines before the first checksum do not need one, XOR checksums and CRCs may be used on their own
        foreach (string content in new[]
        {
            $"N1 G1 X1\n{WithChecksum("N2 G1 X2")}\n{WithChecksum("N3 G1 X3")}\n",
            $"{WithCrc("N1 G1 X1")}\n{WithCrc("N2 G1 X2")}\n",
            // Blank, comment-only, unnumbered and empty numbered lines do not need a checksum
            $"{WithCrc("N1 G1 X1")}\n\nN3 ; comment\nG4 P1\nN5\nN6 (comment)\nN7 (one) (two) ; three\n{WithCrc("N8 G1 X2")}\n"
        })
        {
            await Assert.DoesNotThrowAsync(async () => await ParseEnforcedAsync(content), content);
        }

        // Once a checksum type has been used, all following numbered lines must use it
        foreach (string content in new[]
        {
            $"{WithChecksum("N1 G1 X1")}\nN2 G1 X2\n",
            $"{WithCrc("N1 G1 X1")}\nN2 G1 X2 ; comment\n",
            $"{WithCrc("N1 G1 X1")}\nN2 (comment) G1 X2\n",
            $"{WithCrc("N1 G1 X1")}\n{WithChecksum("N2 G1 X2")}\n",
            $"{WithChecksum("N1 G1 X1")}\n{WithCrc("N2 G1 X2")}\n"
        })
        {
            await Assert.CatchAsync<CodeParserException>(async () => await ParseEnforcedAsync(content), content);
        }

        // The required checksum type is kept when the buffer is invalidated
        CodeParserBuffer buffer = new(128, true) { EnforceLineNumbers = true };
        await ParseEnforcedAsync($"{WithCrc("N1 G1 X1")}\n", buffer);
        buffer.Invalidate();
        Assert.That(buffer.RequiredChecksumType, Is.EqualTo(LineChecksumType.Crc));
        await Assert.CatchAsync<CodeParserException>(async () => await ParseEnforcedAsync("N7 G1 X1\n", buffer));

        // Without enforcement checksums remain optional
        await Assert.DoesNotThrowAsync(async () => await ParseEnforcedAsync($"{WithCrc("N1 G1 X1")}\nN2 G1 X2\n{WithChecksum("N3 G1 X3")}\n", new CodeParserBuffer(128, true)));
    }

    [Test]
    public void ComputeLineChecksums()
    {
        Assert.That(DuetAPI.Commands.Code.ComputeLineCrc(Encoding.ASCII.GetBytes("123456789")), Is.EqualTo(0x31C3));
        Assert.That(DuetAPI.Commands.Code.ComputeLineCrc(Encoding.ASCII.GetBytes("N100 G0 X100 F6000")), Is.EqualTo(51369));
        Assert.That(DuetAPI.Commands.Code.ComputeLineChecksum(Encoding.ASCII.GetBytes("N1 G1 X10")), Is.EqualTo(80));
    }
}
