using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// DSF checks line numbers and CRCs of macro files, and a bad line ends the macro. The machine configuration carries both on every line,
/// so normal operation shows only the good case
/// </summary>
/// <remarks>
/// The macros only set a global variable, labTestLine, to the number of the line
/// </remarks>
[TestFixture]
public class LineChecksums : LabTest
{
    private const string ResetMacro = $"{Lab.MacroDirectory}/checksums/reset.g";
    private const string TestMacro = $"{Lab.MacroDirectory}/checksums/test.g";

    /// <summary>
    /// A line that sets the global variable to its number
    /// </summary>
    private static string Line(int number) => $"N{number} set global.labTestLine = {number}";

    private static string WithCrc(string line) => $"{line}*{DuetAPI.Commands.Code.ComputeLineCrc(Encoding.ASCII.GetBytes(line)):D5}";

    private static string WithBadCrc(string line) => $"{line}*{(ushort)(DuetAPI.Commands.Code.ComputeLineCrc(Encoding.ASCII.GetBytes(line)) ^ 1):D5}";

    [OneTimeSetUp]
    public async Task UploadMacrosAsync()
    {
        Lab.RequirePrinter();
        await using DuetHttpSession session = await Lab.ConnectAsync();
        await Lab.UploadAsync(session, ResetMacro, """
            if !exists(global.labTestLine)
              global labTestLine = 0
            else
              set global.labTestLine = 0

            """);
    }

    [OneTimeTearDown]
    public async Task DeleteMacrosAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, TestMacro);
            await Lab.DeleteAsync(session, ResetMacro);
            await Lab.DeleteAsync(session, $"{Lab.MacroDirectory}/checksums");
        }
    }

    /// <summary>
    /// Run a macro with the given lines and return the journal of the run and the last line that ran. Output of a macro does not reach the M98 reply
    /// </summary>
    private async Task<(string Journal, int LastLine)> RunMacroAsync(params string[] lines)
    {
        await Lab.UploadAsync(Session, TestMacro, string.Join('\n', lines) + "\n");
        await SendCodeAsync($"M98 P\"{ResetMacro}\"");
        string cursor = await Lab.GetJournalCursorAsync();
        try
        {
            await SendCodeAsync($"M98 P\"{TestMacro}\"", 30);
        }
        catch (OperationCanceledException)
        {
            await Lab.RecoverAsync();
            Assert.Fail("The macro did not end");
        }
        int lastLine = (await QueryAsync("global.labTestLine")).GetInt32();
        return (await Lab.ReadJournalAsync(cursor), lastLine);
    }

    [Test]
    public async Task GoodLinesRun()
    {
        (string journal, int lastLine) = await RunMacroAsync([.. Enumerable.Range(1, 3).Select(number => WithCrc(Line(number)))]);
        Assert.That(lastLine, Is.EqualTo(3));
        Assert.That(journal, Does.Not.Contain("error on line").And.Not.Contain("Expected line number").And.Not.Contain("Missing CRC"));
    }

    [Test]
    public async Task WrongCrcEndsTheMacro()
    {
        (string journal, int lastLine) = await RunMacroAsync(WithCrc(Line(1)), WithBadCrc(Line(2)), WithCrc(Line(3)));
        Assert.That(lastLine, Is.LessThan(2), "A line with a wrong CRC or a line behind it ran");
        Assert.That(journal, Does.Contain("CRC error on line N2"));
    }

    [Test]
    public async Task WrongLineNumberEndsTheMacro()
    {
        (string journal, int lastLine) = await RunMacroAsync(WithCrc(Line(1)), WithCrc(Line(3)), WithCrc(Line(4)));
        Assert.That(lastLine, Is.LessThan(3), "A line with a wrong number or a line behind it ran");
        Assert.That(journal, Does.Contain("Expected line number N2 but got N3"));
    }

    [Test]
    public async Task MissingCrcEndsTheMacro()
    {
        (string journal, int lastLine) = await RunMacroAsync(WithCrc(Line(1)), Line(2), WithCrc(Line(3)));
        Assert.That(lastLine, Is.LessThan(2), "A line without a CRC or a line behind it ran");
        Assert.That(journal, Does.Contain("Missing CRC on line N2"));
    }
}
