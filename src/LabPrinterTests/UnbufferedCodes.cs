using System;
using System.Linq;
using System.Threading.Tasks;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Unbuffered codes from a client while a macro runs on the same channel
/// </summary>
/// <remarks>
/// DSF 3.7.0+mp.7 deadlocked the channel: every macro level waited for the M409, and the M409 waited for the macro (fixed in #51, replaced by upstream 30ea08be)
/// DSF marks every M122 and M409 Unbuffered (CodeProcessor.cs:207-212), and since the fix a code waits only for the unbuffered codes of its own stack
/// level (Codes/Pipelines/Start.cs:26-39), so the codes of a macro no longer wait for an M409 that a client sent on the base level below it. Unfixed,
/// both requests time out and the channel stays busy until DCS restarts (PR #51), so the test restarts DCS and resets RRF before it fails.
/// Deterministic. M409 R1 goes to RRF at once (MCodeHandler.cs:846-850), and the hardware runs of PR #51 showed that such a forwarded code is enough
/// for the deadlock, without a nested macro: on plain mp.7 a G4 P0 of the single macro was held in the Start stage and both requests timed out.
/// M409 without R is answered by DSF after its flush (MCodeHandler.cs:859-879), or passed on to RRF after the flush when the mirror has no value,
/// as for state.messageBox.title without a message box (MCodeHandler.cs:872-876)
/// </remarks>
[TestFixture]
public class UnbufferedCodes : LabTest
{
    private const string OuterMacro = $"{Lab.MacroDirectory}/m409-outer.g", InnerMacro = $"{Lab.MacroDirectory}/m409-inner.g";

    /// <summary>
    /// Macro without nesting of the hardware runs of PR #51: a dwell and then more codes than DSF reads ahead
    /// </summary>
    private const string SingleMacro = $"{Lab.MacroDirectory}/m409-single.g";

    [OneTimeSetUp]
    public async Task UploadMacrosAsync()
    {
        Lab.RequirePrinter();
        await using DuetHttpSession session = await Lab.ConnectAsync();
        await Lab.UploadAsync(session, OuterMacro, $"G4 S3\nM98 P\"{InnerMacro}\"\n");
        await Lab.UploadAsync(session, InnerMacro, "G4 P10\n");
        await Lab.UploadAsync(session, SingleMacro, "G4 S3\n" + string.Concat(Enumerable.Repeat("G4 P0\n", 600)));
    }

    [OneTimeTearDown]
    public async Task DeleteMacrosAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, OuterMacro);
            await Lab.DeleteAsync(session, InnerMacro);
            await Lab.DeleteAsync(session, SingleMacro);
        }
    }

    [TestCase(OuterMacro, "", "state.status", TestName = "M409AnsweredByDsfDuringMacro")]
    [TestCase(OuterMacro, "", "state.messageBox.title", TestName = "M409ForwardedToRrfDuringMacro")]
    [TestCase(OuterMacro, "R1 ", "state.status", TestName = "M409R1DuringNestedMacro")]
    [TestCase(SingleMacro, "R1 ", "state.status", TestName = "M409R1DuringSingleMacro")]
    public async Task M409DuringMacro(string macroFile, string flags, string key)
    {
        // The M409 arrives while the macro is in its first G4, before the outer macro calls the inner one or the single macro gets to its G4 P0 codes.
        // RRF answers M409 R1 with "key" too (RepRap.cpp:1879)
        Task<string> macro = SendCodeAsync($"M98 P\"{macroFile}\"", 20);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Task<string> query = SendCodeAsync($"M409 {flags}K\"{key}\"", 20);

        try
        {
            await Task.WhenAll(macro, query);
        }
        catch (OperationCanceledException)
        {
            string state = $"macro {(macro.IsCompletedSuccessfully ? "finished" : "stuck")}, M409 {(query.IsCompletedSuccessfully ? "finished" : "stuck")}";
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
            Assert.Fail($"The HTTP channel deadlocked ({state})");
        }

        Assert.That(await macro, Does.Not.Contain("Error"));
        Assert.That(await query, Does.Contain($"\"key\":\"{key}\""));
    }
}
