using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Codes of a macro around a line that cannot be read or a code that fails in DSF
/// </summary>
/// <remarks>
/// MacroFile.RunAsync starts up to BufferedMacroCodes (16, config.json) codes of a macro before it waits for the first one (MacroFile.cs:401-414), and
/// each code carries the token of its macro (MacroFile.cs:346). DSF #204 (L7 of design/open-findings.md, seen on the printer on 2026-11-23) made a failed
/// code abort its macro before it completes (Code.SetException, Commands/Generic/Code.cs:313-323), and made a line that cannot be read stop the reading
/// while the codes before it still finish (MacroFile.cs:415-433, 470-476). The token part of #204 was replaced by upstream d108e9767 (merged in #212,
/// Code.ExecuteAsync keeps the token of the file), the two other parts are fork code. A code that RRF refuses is covered by
/// VariableCopies.ObjectArrayIsRefused. RRF never sees the codes that fail here: DSF fails them before they are sent. The opposite case, a code that RRF
/// passes to DSF and that fails there (DoCode, #220, #237, #242), needs a code from an RRF text channel (USB or PanelDue). The lab printer has no USB
/// cable to the SBC (design/open-findings.md:67) and no PanelDue, and trigger actions cannot call file functions since RRF #115, so it is not tested.
/// Each macro reports through global variables, because its output does not reach the reply of the M98 that started it
/// </remarks>
[TestFixture]
public class MacroErrors : LabTest
{
    private const string Directory = $"{Lab.MacroDirectory}/macro-errors";
    private const string ResetMacro = $"{Directory}/reset.g";
    private const string UnreadableLineMacro = $"{Directory}/unreadable-line.g";
    private const string FailingInDsfMacro = $"{Directory}/failing-in-dsf.g";

    /// <summary>
    /// Lines after the code that fails in DSF, more than DSF reads ahead, as in the unit test MacroCodeAfterError.NoCodeRunsAfterFailingCodeInLongMacro
    /// </summary>
    private const int LinesAfterFailure = 40;

    [OneTimeSetUp]
    public async Task UploadMacrosAsync()
    {
        Lab.RequirePrinter();
        await using DuetHttpSession session = await Lab.ConnectAsync();

        // The test macros contain no conditional block, so that the else below is unexpected (CodeFile.cs:568-573, a block ending before it would set
        // _lastCodeBlock at CodeFile.cs:827). The globals are therefore reset by a macro of their own
        await Lab.UploadAsync(session, ResetMacro,
            G2Helpers.Global("labTestBeforeElse", "false") +
            G2Helpers.Global("labTestAfterElse", "false") +
            G2Helpers.Global("labTestBeforeFailure", "false") +
            G2Helpers.Global("labTestAfterFailure", "0"));

        // The dwell keeps the set waiting in its flush (KeywordHandler.cs:244-248) while DSF reads the else
        await Lab.UploadAsync(session, UnreadableLineMacro, """
            G4 P500
            set global.labTestBeforeElse = true
            else
            set global.labTestAfterElse = true

            """);

        // iterations outside a loop throws in DSF (CodeFile.GetIterations, CodeFile.cs:255-268, called from the evaluation context of Expressions.cs:1021
        // through MetaExpressionParser.cs:822), before anything is sent
        await Lab.UploadAsync(session, FailingInDsfMacro,
            "set global.labTestBeforeFailure = true\n" +
            "echo iterations\n" +
            string.Concat(Enumerable.Repeat("set global.labTestAfterFailure = global.labTestAfterFailure + 1\n", LinesAfterFailure)));
    }

    [OneTimeTearDown]
    public async Task DeleteMacrosAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            foreach (string macro in new[] { ResetMacro, UnreadableLineMacro, FailingInDsfMacro })
            {
                await Lab.DeleteAsync(session, macro);
            }
            await Lab.DeleteAsync(session, Directory);
        }
    }

    /// <summary>
    /// Run a macro with M98. The fixed and the unfixed build both reply, so a macro that does not end is a new fault, and the test restarts DCS
    /// and resets RRF before it fails
    /// </summary>
    private async Task<string> RunMacroAsync(string macro)
    {
        try
        {
            string reply = await SendCodeAsync($"M98 P\"{macro}\"", 30);
            TestContext.Progress.WriteLine($"M98 P\"{macro}\" replied \"{reply.Trim()}\"");
            return reply;
        }
        catch (OperationCanceledException)
        {
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
            Assert.Fail($"M98 P\"{macro}\" did not end within 30 s");
            throw;
        }
    }

    /// <summary>
    /// When DSF cannot read a line of a macro, e.g. an unexpected else, RRF has executed the lines before it, so DSF must let the codes before it finish
    /// that it has read ahead. Upstream d108e9767 gave the codes of a macro its token again, and its read catch aborts the macro at once
    /// (upstream v3.7-dev MacroFile.cs, catch of the read loop), which cancels them. DSF #204 changed that catch to stop the reading and abort the macro
    /// once the codes before the line are done (MacroFile.cs:415-433, 470-476); PR #204 found that with the token alone "G4 P1 and G4 P2 before the else
    /// were dropped", in 3 of 3 runs of that unit test. So a DSF with the token but without that change, i.e. upstream v3.7-dev, leaves labTestBeforeElse
    /// false: the set waits in its flush for the dwell when DSF reads the else, and the abort cancels it. The fork before #204 passes, because its codes carried the token of the channel.
    /// Deterministic as long as DSF reads three lines faster than the 500 ms dwell. The unit test is MacroCodeAfterError.CodesBeforeLineThatCannotBeReadStillRun,
    /// the job file variant (fork commit 91555b8fc in #212) needs a job and is not tested here
    /// </summary>
    [Test]
    public async Task CodesBeforeUnreadableLineStillRun()
    {
        await RunMacroAsync(ResetMacro);
        string cursor = await Lab.GetJournalCursorAsync();
        await RunMacroAsync(UnreadableLineMacro);
        await Task.Delay(TimeSpan.FromSeconds(1));
        string journal = await Lab.ReadJournalAsync(cursor);

        JsonElement before = await QueryAsync("global.labTestBeforeElse"), after = await QueryAsync("global.labTestAfterElse");
        Assert.That(journal.Split('\n').Any(line => line.Contains("Error while reading code from macro file") && line.Contains("unreadable-line.g")), Is.True,
            "DSF did not refuse the else (MacroFile.cs:426), so the test shows nothing");
        Assert.Multiple(() =>
        {
            Assert.That(before.ValueKind, Is.EqualTo(JsonValueKind.True), "The code before the line that cannot be read did not run");
            Assert.That(after.ValueKind, Is.EqualTo(JsonValueKind.False), "The line after the one that cannot be read ran");
        });
    }

    /// <summary>
    /// After a code of a macro failed in DSF, DSF still ran codes of the macro that it had read ahead: they carried the token of the channel, and the
    /// macro was aborted only when RunAsync got to the failed code (two upstream defects, PR #204). Fixed in DSF #204: Code.SetException aborts the
    /// macro of the failed code before it completes the code (Commands/Generic/Code.cs:313-323), and the codes read ahead carry the token of the macro
    /// (since #212 through upstream d108e9767, Code.ExecuteAsync). Before #204 the codes read ahead kept running after the abort, because nothing
    /// cancelled them: the unit test MacroCodeAfterError.NoCodeRunsAfterFailingCodeInLongMacro ran 14 of the 40 lines after a line that RRF refused, and a
    /// line that fails in DSF ends in the same Code.SetException, so labTestAfterFailure would be well above 0 here. With the token but without the
    /// abort in SetException, i.e. upstream v3.7-dev, 1 or 2 of the failing-code unit tests failed per run in 3 runs (table of PR #204); on the printer
    /// that case is statistical and not measured. The failing code is echo iterations outside a loop, as G1 X{iterations} in the unit tests, without a move
    /// </summary>
    [Test]
    public async Task CodeThatFailsInDsfStopsTheMacro()
    {
        await RunMacroAsync(ResetMacro);
        string cursor = await Lab.GetJournalCursorAsync();
        await RunMacroAsync(FailingInDsfMacro);
        await Task.Delay(TimeSpan.FromSeconds(1));
        string journal = await Lab.ReadJournalAsync(cursor);

        JsonElement before = await QueryAsync("global.labTestBeforeFailure"), after = await QueryAsync("global.labTestAfterFailure");
        Assert.That(journal.Split('\n').Any(line => line.Contains("Error while executing code") && line.Contains("failing-in-dsf.g")), Is.True,
            "DSF did not report the failing code (MacroFile.cs:459), so the test shows nothing");
        Assert.Multiple(() =>
        {
            Assert.That(before.ValueKind, Is.EqualTo(JsonValueKind.True), "The code before the failing one did not run");
            Assert.That(after.GetInt32(), Is.Zero, $"{after.GetInt32()} of the {LinesAfterFailure} lines after the failing code ran");
        });
    }
}
