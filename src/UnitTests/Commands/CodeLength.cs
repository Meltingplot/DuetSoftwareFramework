using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DuetAPI.Commands;
using NUnit.Framework;

namespace UnitTests.Commands;

/// <summary>
/// Tests for the length of parsed codes. JobProcessor takes FilePosition + Length as the position after a code,
/// which becomes the pause position when RepRapFirmware does not report one
/// </summary>
[TestFixture]
public class CodeLength
{
    [Test]
    public async Task FollowingCodeAsync()
    {
        List<DuetAPI.Commands.Code> codes = await ParseFileAsync("G1 X1 G0 Y2\nM400\n");

        Assert.That(codes.ConvertAll(code => code.FilePosition), Is.EqualTo(new long?[] { 0, 6, 12 }));
        Assert.That(codes.ConvertAll(code => code.Length), Is.EqualTo(new int?[] { 6, 6, 5 }));
    }

    [Test]
    public async Task FollowingCodeOnNumberedLineAsync()
    {
        List<DuetAPI.Commands.Code> codes = await ParseFileAsync("N1 G91 G1 X10*63\nG90\n");

        Assert.That(codes.ConvertAll(code => code.FilePosition), Is.EqualTo(new long?[] { 0, 7, 17 }));
        Assert.That(codes.ConvertAll(code => code.Length), Is.EqualTo(new int?[] { 7, 10, 4 }));
    }

    [TestCase("G1 X1", 5)]
    [TestCase("G1 X1\n", 6)]
    [TestCase("G1 X1\r\n", 7)]
    [TestCase("N1 M400", 7)]
    [TestCase("N1 G1 X10*80", 12)]
    public async Task EndOfFileAsync(string content, int length)
    {
        List<DuetAPI.Commands.Code> codes = await ParseFileAsync(content);

        Assert.That(codes, Has.Count.EqualTo(1));
        Assert.That(codes[0].FilePosition, Is.EqualTo(0));
        Assert.That(codes[0].Length, Is.EqualTo(length));
    }

    [TestCase("G1 X1", 5)]
    [TestCase("G1 X1\n", 6)]
    [TestCase("N1 G1 X10*80", 12)]
    public void EndOfInput(string content, int length)
    {
        using StringReader reader = new(content);
        DuetAPI.Commands.Code code = new();

        Assert.That(DuetAPI.Commands.Code.Parse(reader, code), Is.True);
        Assert.That(code.Length, Is.EqualTo(length));
    }

    [Test]
    public async Task SyncMatchesAsync()
    {
        const string line = "G1 X1 G0 Y2 M400";

        List<int?> syncLengths = [];
        using (StringReader reader = new(line))
        {
            for (DuetAPI.Commands.Code code = new(); DuetAPI.Commands.Code.Parse(reader, code); code = new())
            {
                syncLengths.Add(code.Length);
            }
        }
        List<DuetAPI.Commands.Code> asyncCodes = await ParseFileAsync(line);

        Assert.That(syncLengths, Is.EqualTo(new int?[] { 6, 6, 4 }));
        Assert.That(asyncCodes.ConvertAll(code => code.Length), Is.EqualTo(syncLengths));
    }

    /// <summary>
    /// Read all codes like CodeFile does
    /// </summary>
    private static async Task<List<DuetAPI.Commands.Code>> ParseFileAsync(string content)
    {
        await using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));
        CodeParserBuffer buffer = new(128, true) { EnforceLineNumbers = true };
        List<DuetAPI.Commands.Code> codes = [];
        do
        {
            DuetAPI.Commands.Code code = new();
            if (await DuetAPI.Commands.Code.ParseAsync(stream, code, buffer))
            {
                codes.Add(code);
            }
        }
        while (buffer.GetPosition(stream) < stream.Length);
        return codes;
    }
}
