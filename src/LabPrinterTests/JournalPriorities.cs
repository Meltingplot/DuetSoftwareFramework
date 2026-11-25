using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DuetHttpClient;
using NUnit.Framework;
using JournalEntry = LabPrinterTests.G3Helpers.JournalEntry;

namespace LabPrinterTests;

/// <summary>
/// Syslog priorities of the lines DCS writes to the journal
/// </summary>
/// <remarks>
/// journald rate-limits each priority on its own (PR #118). When every DCS line had priority 6, a stuck link that flooded the journal with warnings also
/// pushed out errors such as "No transfer with RepRapFirmware completed", the reason DCS stopped. Since #118 CommonLogFormatter starts each line with the
/// priority of its entry when systemd sets JOURNAL_STREAM, also each line of an exception, and leaves the colors out
/// (DuetSharedLibrary/CommonLogFormatter.cs:24, :51-52, :92-98, :110-120). journald stores a message with a control character such as ESC as an array of
/// bytes (journalctl -o json), which <see cref="G3Helpers.ReadJournalEntriesAsync"/> reports as Binary
/// </remarks>
[TestFixture]
public partial class JournalPriorities : LabTest
{
    private const string Directory = $"{Lab.MacroDirectory}/journal";
    private const string Macro = $"{Directory}/info.g";

    /// <summary>
    /// End of the macro path that the line about its start contains
    /// </summary>
    private const string MacroTail = "lab-tests/journal/info.g";

    /// <summary>
    /// IPC command that does not exist
    /// </summary>
    private const string UnknownCommand = "LabTestNoSuchCommand";

    /// <summary>
    /// Root IPC client that sends a command DCS does not know. DCS refuses it with ArgumentException "unsupported command ..." (IPC/Connection.cs:656) and
    /// logs "IPC#n: Failed to receive command" with the exception as an error (IPC/Processors/Command.cs:150). Nothing else happens
    /// </summary>
    private const string Script = """
        ipc = Ipc()
        finish({"reply": ipc.request({"command": ARGS["command"]}, 10)})
        """;

    [GeneratedRegex(@"^Current DCS log level: (\w+)", RegexOptions.Multiline)]
    private static partial Regex LogLevelRegex();

    [OneTimeTearDown]
    public async Task DeleteMacroAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, Macro);
            await Lab.DeleteAsync(session, Directory);
        }
    }

    /// <summary>
    /// An information line and the lines of an error with an exception reach the journal with priorities 6 and 3, as plain text without colors.
    /// The information line is "Starting macro file ..." of an M98 on HTTP (Files/FileFactory.cs:60), the error that of an unknown IPC command (see
    /// <see cref="Script"/>), whose exception follows on the next line. Without #118 DCS wrote "[" + color + level + reset + "] ..." without a priority
    /// (PR #118 diff of CommonLogFormatter.cs), so journald gave every line its default priority 6 and stored each colored line as bytes; the PR reports
    /// that the image DCS before the fix wrote everything with priority 6. Deterministic, one run catches it.
    /// DuetSharedLibrary.dll is part of the side-load, the fix is in DSF 3.7.0+mp.13 and later
    /// </summary>
    [Test]
    public async Task LinesHaveTheirPriorityAndNoColors()
    {
        string levelReply = await SendCodeAsync("M111 P-1");
        Match level = LogLevelRegex().Match(levelReply);
        Assert.That(level.Success, Is.True, $"M111 P-1 did not report the log level: {levelReply}");
        if (level.Groups[1].Value is not ("Trace" or "Debug" or "Information"))
        {
            Assert.Ignore($"DCS logs no information lines at log level {level.Groups[1].Value}");
        }

        await Lab.UploadAsync(Session, Macro, "; lab test: DCS logs the start of this macro as an information line\n");
        string cursor = await Lab.GetJournalCursorAsync();
        await SendCodeAsync($"M98 P\"{Macro}\"");
        JsonElement result = await G3Helpers.RunPythonAsync(Script, new { command = UnknownCommand }, 60);
        JsonElement reply = result.GetProperty("reply");
        Assert.That((G3Helpers.GetString(reply, "errorType"), G3Helpers.GetString(reply, "errorMessage")),
            Is.EqualTo(("ArgumentException", $"unsupported command {UnknownCommand}")), $"Reply to the unknown IPC command: {reply}");

        // journald may store the lines a moment after DCS wrote them
        List<JournalEntry> entries = [];
        int infoIndex = -1, exceptionIndex = -1;
        for (int i = 0; i < 10 && (infoIndex < 0 || exceptionIndex < 0); i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            entries = await G3Helpers.ReadJournalEntriesAsync(cursor, "duetcontrolserver");
            infoIndex = entries.FindIndex(entry => entry.Message.Contains("Starting macro file") && entry.Message.Contains(MacroTail));
            exceptionIndex = entries.FindIndex(entry => entry.Message.Contains($"unsupported command {UnknownCommand}"));
        }
        int errorIndex = exceptionIndex < 0 ? -1 : entries.FindLastIndex(exceptionIndex, entry => entry.Message.Contains("Failed to receive command"));

        foreach (int index in new[] { infoIndex, errorIndex, exceptionIndex }.Where(index => index >= 0))
        {
            TestContext.Progress.WriteLine($"Priority {entries[index].Priority}{(entries[index].Binary ? ", stored as bytes" : string.Empty)}: {entries[index].Message.Replace("\x1b", "\\e")}");
        }
        Assert.That(infoIndex, Is.Not.Negative, "The journal has no line about the start of the macro");
        Assert.That(errorIndex, Is.Not.Negative, "The journal has no error line about the unknown IPC command");

        Assert.Multiple(() =>
        {
            AssertEntry(entries[infoIndex], 6, "[info] ");
            AssertEntry(entries[errorIndex], 3, "[error] ");
            AssertEntry(entries[exceptionIndex], 3, null);
        });
    }

    /// <summary>
    /// Check the priority of a journal entry and that it is plain text
    /// </summary>
    private static void AssertEntry(JournalEntry entry, int priority, string? start)
    {
        string text = entry.Message.Replace("\x1b", "\\e");
        Assert.That(entry.Priority, Is.EqualTo(priority), $"Priority of '{text}'");
        Assert.That(entry.Binary, Is.False, $"journald stored '{text}' as bytes");
        // Ordinal: NUnit's Contain compares with the current culture, and ICU ignores ESC there, so it finds ESC in every string (lab run 2026-11-25)
        Assert.That(entry.Message.Contains('\x1b'), Is.False, $"Color codes in '{text}'");
        if (start is not null)
        {
            Assert.That(entry.Message, Does.StartWith(start));
        }
    }
}
