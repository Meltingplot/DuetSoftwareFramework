using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Answers of RRF to the expressions, variable assignments and object model requests of DSF
/// </summary>
/// <remarks>
/// The expressions read a global or a field of RRF's object model, so DSF sends them to RRF (G1Helpers). DSF reads every answer in the transfer
/// thread of its link (Link/LinkService.cs:437-459): an exception there sends a requested emergency stop and ends the thread, and DCS stops
/// </remarks>
[TestFixture]
public class VariableAnswers : LabTest
{
    private const string MacroFolder = $"{Lab.MacroDirectory}/variable-answers";
    private const string NonFiniteMacro = $"{MacroFolder}/non-finite.g";

    private const string Ten = "labTestTen";
    private const string ReportLoad = "labTestReportLoad";
    private const string Huge = "labTestHuge";

    private const string DiscardMessage = "Cannot store excessively long object model response";
    private const string ObjectModelResend = "(request GetObjectModel)";

    [OneTimeTearDown]
    public async Task DeleteMacrosAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, NonFiniteMacro);
            await Lab.DeleteAsync(session, MacroFolder);
        }
    }

    /// <summary>
    /// DCS stopped when RRF answered an expression or a variable assignment with NaN, an infinity or a float beyond the range of decimal (DSF #11)
    /// </summary>
    /// <remarks>
    /// RRF divides without a check (RRF ExpressionParser.cpp:588-593) and sends every float as FloatWithDigits, for an expression
    /// (DataTransfer.cpp:1779-1781) as for a variable (:2052-2054). DSF converted it with (decimal)value, which throws an OverflowException for NaN,
    /// ±inf and magnitudes beyond about 7.9e28 (PR #11: the trace from HandleVariableResult of a CHX calibration macro). Since #11 such a value
    /// stays a float (Link/Protocol/Reader.cs:263-264, :619-630), and echo prints it with ToString("G") of the invariant culture
    /// (Codes/Meta/Expressions.cs:552-555): "Infinity", "-Infinity", "NaN". state.upTime is not an SBC field, so RRF evaluates the echoes;
    /// the macro assigns the same values to local variables, which go to RRF as SetVariable (Codes/Handlers/KeywordHandler.cs:261-279), so the
    /// answers take the path of HandleVariableResult (LinkService.cs:1270-1272). Unfixed: the first echo stops DCS; the test restarts DCS and
    /// resets RRF before it fails (an E-stop on the CHX)
    /// </remarks>
    [Test]
    public async Task NonFiniteResultsDoNotStopDcs()
    {
        await Lab.UploadAsync(Session, NonFiniteMacro,
            G1Helpers.Global("labTestNonFinite", "false") +
            "var inf = (1 + state.upTime) / 0\n" +
            "var nan = (state.upTime * 0) / 0\n" +
            "var big = state.upTime * 0 + 1e30\n" +
            "set global.labTestNonFinite = isnan(var.nan) && var.inf > var.big && var.big > 1e29\n");

        string pid = await Lab.GetControlServerPidAsync();
        string cursor = await Lab.GetJournalCursorAsync();
        foreach ((string expression, string expected) in new[]
        {
            ("(1 + state.upTime) / 0", "Infinity"),
            ("(-1 - state.upTime) / 0", "-Infinity"),
            ("(state.upTime * 0) / 0", "NaN")
        })
        {
            string reply = (await G1Helpers.SendOrRecoverAsync(Session, $"echo {{{expression}}}", pid)).Trim();
            Assert.That(reply, Is.EqualTo(expected), expression);
        }

        string beyondDecimal = (await G1Helpers.SendOrRecoverAsync(Session, "echo {state.upTime * 0 + 1e30}", pid)).Trim();
        Assert.That(double.TryParse(beyondDecimal, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 7.9e28, Is.True,
            $"1e30 came back as \"{beyondDecimal}\"");

        string macroReply = await G1Helpers.SendOrRecoverAsync(Session, $"M98 P\"{NonFiniteMacro}\"", pid);
        await G1Helpers.AssertControlServerGoesOnAsync(Session, pid, "the non-finite answers");
        int overflows = await G1Helpers.CountJournalLinesAsync(cursor, "OverflowException");
        JsonValueKind result = (await QueryAsync("global.labTestNonFinite")).ValueKind;
        Assert.Multiple(() =>
        {
            Assert.That(macroReply, Does.Not.Contain("Error"), "The macro failed");
            Assert.That(result, Is.EqualTo(JsonValueKind.True), "The local variables did not get the values");
            Assert.That(overflows, Is.Zero, "OverflowException lines in the journal of DCS");
        });
    }

    /// <summary>
    /// A global too long to report stopped DSF's object model: RRF treated the overflow of its output buffers as "try again", and DSF requested the key
    /// "global" again for ever (RRF #113). This test covers the discard; OutputBufferPressure covers the wait for free buffers of the same fix
    /// </summary>
    /// <remarks>
    /// The global is vector(128, vector(128, vector(20, global.labTestTen))). DSF cannot evaluate the global operand, so its own length check (DSF #239)
    /// stays quiet and RRF assigns the value: its SetVariable answer is cut after the first element and fits (7937 bytes, PR #114). The JSON of the key
    /// "global" is about 1 MB. Since #113 the GetObjectModel case waits for 36 free buffers and then discards a response that does not fit, once per
    /// request, with "Cannot store excessively long object model response, discarding request (output buffers full, key global, ...)" (RRF
    /// SbcInterface.cpp:496-520); DSF logs "Invalid key global in the object model" and keeps its old copy (Model/UpdateService.cs:322). Unfixed (lab
    /// printer 2026-11-24, PR #113): no discard, "Resending packet #0 (request GetObjectModel)" (Link/LinkService.cs:725) every 1.75 s without end and
    /// a model that stops, so state.upTime no longer changes in the model of DWS.
    /// Side effect even with the fix: while the global is long, DSF's copy of every global stays as it was, and each request of the key puts the error
    /// into the messages of DWC. The window is kept to the first discard plus 3 s. Setting the global back to a short value ends it either way
    /// </remarks>
    [Test]
    public async Task GlobalTooLongToReportIsDiscardedOnce()
    {
        G7Helpers.RequireMainBoardFixes(Session, G7Helpers.Rrf113);
        await G1Helpers.SetGlobalAsync(Session, Ten, "10");
        await G1Helpers.SetGlobalAsync(Session, ReportLoad, "1");
        await Lab.WaitForModelAsync(Session, model => GetInt(model.Global, ReportLoad) == 1, TimeSpan.FromSeconds(30), $"global.{ReportLoad} = 1 in the model");

        string pid = await Lab.GetControlServerPidAsync();
        string cursor = await Lab.GetJournalCursorAsync();
        using CancellationTokenSource stopSampling = new();
        Task<double> upTimeGap = G1Helpers.SampleUpTimeGapAsync(Session, stopSampling.Token);
        string reply;
        Stopwatch untilDiscard = Stopwatch.StartNew();
        int discards = 0;
        try
        {
            reply = await G1Helpers.SendOrRecoverAsync(Session, $"set global.{ReportLoad} = vector(128, vector(128, vector(20, global.{Ten})))", pid, 60);
            while (untilDiscard.Elapsed < TimeSpan.FromSeconds(15) && (discards = await G1Helpers.CountJournalLinesAsync(cursor, DiscardMessage)) == 0)
            {
                await Task.Delay(1000);
            }
            untilDiscard.Stop();
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await G1Helpers.TryCleanUpAsync(Session, $"set global.{ReportLoad} = 0");
        }
        bool fetchedAgain = await Lab.TryWaitForModelAsync(Session, model => GetInt(model.Global, ReportLoad) == 0, TimeSpan.FromSeconds(30));
        stopSampling.Cancel();
        double maxUpTimeGap = await upTimeGap;

        discards = await G1Helpers.CountJournalLinesAsync(cursor, DiscardMessage);
        int resends = await G1Helpers.CountJournalLinesAsync(cursor, ObjectModelResend);
        int invalidKey = await G1Helpers.CountJournalLinesAsync(cursor, "Invalid key global in the object model");
        TestContext.Progress.WriteLine($"Reply \"{reply.Trim()}\"; first discard after {untilDiscard.Elapsed.TotalSeconds:0.0} s; {discards} discards, " +
                                       $"{invalidKey} \"Invalid key global\", {resends} GetObjectModel resends; longest time without a new state.upTime {maxUpTimeGap:0.0} s");

        await G1Helpers.AssertControlServerGoesOnAsync(Session, pid, "the long global");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.Contain("Error"), "RRF did not take the global, whose SetVariable answer fits");
            Assert.That(discards, Is.Positive, "RRF never discarded the response for the key global");
            Assert.That(resends, Is.LessThan(Math.Max(discards, 1)), "DSF requested the object model again more often than RRF discarded it");
            Assert.That(maxUpTimeGap, Is.LessThanOrEqualTo(5), "longest time without a new state.upTime in the model of DWS, in s");
            Assert.That(fetchedAgain, Is.True, "DSF did not get the key global again after the global was short again");
        });
    }

    /// <summary>
    /// A global or an expression whose answer is longer than a transfer was assigned and then requested again: "already exists" for global, a set that
    /// never answers, an echo that never answers (RRF #114)
    /// </summary>
    /// <remarks>
    /// The answer of vector(128, vector(128, vector(30, global.labTestTen))) holds at least its first element, 128 × 91 + 129 bytes, more than the
    /// 8176 bytes an answer may carry (PR #114). DSF cannot evaluate the global operand, so RRF decides. Since #114 SetVariable builds the answer before
    /// it assigns the value and refuses it with "value too long to send to the SBC" (RRF SbcInterface.cpp:1036, :1180), and EvaluateExpression
    /// answers with the same error (:833). Unfixed: the global is created and the request comes again, which fails with "variable 'global.labTestHuge'
    /// already exists"; the echo is never answered, and the test restarts DCS and resets RRF (an E-stop on the CHX) before it fails
    /// </remarks>
    [Test]
    public async Task GlobalWhoseAnswerDoesNotFitIsRefused()
    {
        G7Helpers.RequireMainBoardFixes(Session, G7Helpers.Rrf114);
        await G1Helpers.SetGlobalAsync(Session, Ten, "10");
        if (await G1Helpers.GlobalExistsAsync(Session, Huge))
        {
            Assert.Ignore($"global.{Huge} exists from an earlier run, the test needs a reset of RRF first");
        }

        string pid = await Lab.GetControlServerPidAsync();
        string globalReply, echoReply;
        bool created = false;
        try
        {
            globalReply = await G1Helpers.SendOrRecoverAsync(Session, $"global {Huge} = vector(128, vector(128, vector(30, global.{Ten})))", pid);
            created = await G1Helpers.GlobalExistsAsync(Session, Huge);
            echoReply = await G1Helpers.SendOrRecoverAsync(Session, $"echo {{vector(128, vector(128, vector(30, global.{Ten})))}}", pid);
        }
        finally
        {
            if (created)
            {
                // Without the fix the global exists, and the key global cannot be reported until it is short
                await G1Helpers.TryCleanUpAsync(Session, $"set global.{Huge} = 0");
            }
        }

        await G1Helpers.AssertControlServerGoesOnAsync(Session, pid, "the answers that do not fit");
        Assert.Multiple(() =>
        {
            Assert.That(globalReply, Does.StartWith("Error").And.Contain(G1Helpers.ValueTooLong), "global");
            Assert.That(created, Is.False, $"global.{Huge} was created");
            Assert.That(echoReply, Does.StartWith("Error").And.Contain(G1Helpers.ValueTooLong), "echo");
        });
    }

    private static int? GetInt(JsonModelDictionary globals, string name) =>
        globals.TryGetValue(name, out JsonElement? value) && value?.ValueKind == JsonValueKind.Number ? value.Value.GetInt32() : null;
}
