using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files.Parser;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace UnitTests.Commands;

/// <summary>
/// Tests for lines with more than one code read by <see cref="DuetAPI.Commands.Code.Parse"/>, which the file info parser uses
/// </summary>
[TestFixture]
public class SyncParserFollowingCodes
{
    [Test]
    public void ParseSeveralCodes()
    {
        List<DuetAPI.Commands.Code> codes = ParseLine("G91 G1 Z5 F600 M400");

        Assert.That(codes, Has.Count.EqualTo(3));
        Assert.That(codes[0].Type, Is.EqualTo(CodeType.GCode));
        Assert.That(codes[0].MajorNumber, Is.EqualTo(91));
        Assert.That(codes[0].Parameters, Is.Empty);
        Assert.That(codes[0].Flags.HasFlag(CodeFlags.IsLastCode), Is.False);

        Assert.That(codes[1].Type, Is.EqualTo(CodeType.GCode));
        Assert.That(codes[1].MajorNumber, Is.EqualTo(1));
        Assert.That(codes[1].Parameters, Has.Count.EqualTo(2));
        Assert.That(codes[1].GetFloat('Z'), Is.EqualTo(5F));
        Assert.That(codes[1].GetInt('F'), Is.EqualTo(600));
        Assert.That(codes[1].Flags.HasFlag(CodeFlags.IsLastCode), Is.False);

        Assert.That(codes[2].Type, Is.EqualTo(CodeType.MCode));
        Assert.That(codes[2].MajorNumber, Is.EqualTo(400));
        Assert.That(codes[2].Flags.HasFlag(CodeFlags.IsLastCode), Is.True);
    }

    [Test]
    public void ParseSeveralCodesWithComments()
    {
        List<DuetAPI.Commands.Code> codes = ParseLine("M104 S0 (hotend) M140 S0 ; heaters off");

        Assert.That(codes, Has.Count.EqualTo(2));
        Assert.That(codes[0].MajorNumber, Is.EqualTo(104));
        Assert.That(codes[0].GetInt('S'), Is.EqualTo(0));
        Assert.That(codes[0].Comment, Is.EqualTo("hotend"));
        Assert.That(codes[1].MajorNumber, Is.EqualTo(140));
        Assert.That(codes[1].GetInt('S'), Is.EqualTo(0));
        Assert.That(codes[1].Comment, Is.EqualTo(" heaters off"));
    }

    [Test]
    public async Task ParseSeveralCodesLikeAsyncParser()
    {
        const string line = "G1 X1 Y2 G0 X3 M106 P0 S0.5 T1";
        List<DuetAPI.Commands.Code> syncCodes = ParseLine(line);
        List<DuetAPI.Commands.Code> asyncCodes = await ParseLineAsync(line);

        Assert.That(syncCodes.ConvertAll(code => code.ToString()), Is.EqualTo(asyncCodes.ConvertAll(code => code.ToString())));
    }

    [Test]
    public async Task FileInfoWithSeveralCodesInFooter()
    {
        // Only files with a G-code extension are parsed
        string fileName = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".gcode");
        try
        {
            await System.IO.File.WriteAllTextAsync(fileName, "G28\nG1 Z0.3 F3000\nG1 X10 Y10 E1\nM104 S0 M140 S0\n");

            using ServiceProvider services = DcsServices.Build();
            GCodeFileInfo info = await services.GetRequiredService<FileInfoParser>().ParseAsync(fileName, false);

            Assert.That(info.Height, Is.EqualTo(0.3F));
        }
        finally
        {
            System.IO.File.Delete(fileName);
        }
    }

    private static List<DuetAPI.Commands.Code> ParseLine(string line)
    {
        List<DuetAPI.Commands.Code> codes = [];
        using StringReader reader = new(line);
        for (DuetAPI.Commands.Code code = new(); DuetAPI.Commands.Code.Parse(reader, code); code = new())
        {
            codes.Add(code);
        }
        return codes;
    }

    private static async Task<List<DuetAPI.Commands.Code>> ParseLineAsync(string line)
    {
        List<DuetAPI.Commands.Code> codes = [];
        await using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(line));
        CodeParserBuffer buffer = new(128, false);
        for (DuetAPI.Commands.Code code = new(); await DuetAPI.Commands.Code.ParseAsync(stream, code, buffer); code = new())
        {
            codes.Add(code);
        }
        return codes;
    }
}
