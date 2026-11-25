using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// File info of job files whose content once made DSF fail
/// </summary>
/// <remarks>
/// M36 parses the file with FileInfoParser.ParseAsync (MCodeHandler.cs:483-499) and replies {"err":1,...} when that throws or when its result cannot
/// be serialized (MCodeHandler.cs:515-519). DWC and the HMI ask for the same file info for every file they list (GET /machine/fileinfo), and M23/M32
/// put it into the object model, so a file that breaks the parser breaks the job list as well. Each test uploads one file to
/// <see cref="G2Helpers.GCodeDirectory"/>, reads it with M36 and deletes it again; no file is ever printed. The expected values are those of the unit
/// tests named per test, which parse the same content with the same FileInfoParser and the default filters (the CHX image keeps them, see
/// <see cref="G2Helpers.JobFileHeader"/>). The class is not called FileInfo, which would hide System.IO.FileInfo in this namespace
/// </remarks>
[TestFixture]
public class FileInfoReplies : LabTest
{
    /// <summary>
    /// Read the file info of a test file. A DCS that ends or hangs while it parses the file is restarted and RRF reset before the test fails, so the
    /// tests after it find the printer idle again
    /// </summary>
    private async Task<JsonDocument> ReadFileInfoAsync(string name, string content)
    {
        string pid = await Lab.GetControlServerPidAsync(), reply;
        try
        {
            reply = await G2Helpers.GetFileInfoAsync(Session, name, content, 30);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            string newPid = await Lab.GetControlServerPidAsync();
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
            Assert.Fail($"M36 of {name} got no reply ({e.GetType().Name}: {e.Message}), DCS pid {pid} before and {newPid} after");
            throw;
        }

        string pidAfter = await Lab.GetControlServerPidAsync();
        if (pidAfter != pid)
        {
            await Lab.ResetHaltedFirmwareAsync();
            await ReconnectSessionAsync();
            Assert.Fail($"DCS was restarted while it read the file info of {name} (pid {pid} before and {pidAfter} after)");
        }
        TestContext.Progress.WriteLine($"M36 of {name}: {(reply.Length > 300 ? reply[..300] + "..." : reply.Trim())}");
        return G2Helpers.ParseFileInfo(reply);
    }

    /// <summary>
    /// A filament index in an IdeaMaker comment made DSF grow the filament list up to that index (UB2): ";Material#100000000 Used: 1" allocated one
    /// float per index and took quadratic time to trim it. Fixed in DSF #219 (commit "Fixed DCS running out of memory or stopping when a job file names
    /// a large filament index or many filaments"): FileInfoParser keeps at most MaxFilaments = 20 filaments, RRF's limit (FileInfoParser.cs:40-44), and
    /// ignores a higher index as RRF does (FileInfoParser.cs:470-479 for the IdeaMaker filter "material\#(?&lt;index&gt;...)", Settings.cs:369).
    /// Unfixed, the reply lists 100,001 filaments (5, 100,000 zeros and 1), as the unit test PrintFileInfo.FilamentIndexIsBounded showed against the
    /// source before the fix. The index is kept at 100,000, so that an unfixed DSF only allocates about 400 kB. Deterministic, one M36
    /// </summary>
    [Test]
    public async Task FilamentIndexIsBounded()
    {
        await G3Helpers.RequireControlServerWithNewFixesAsync("#219");
        using JsonDocument info = await ReadFileInfoAsync("filament-index.g", G2Helpers.JobFileHeader + ";Material#0 Used: 5.0\n;Material#100000 Used: 1.0\nG1 X1\n");
        JsonElement root = info.RootElement;
        Assert.That(root.GetProperty("err").GetInt32(), Is.Zero);
        float[] filament = [.. root.GetProperty("filament").EnumerateArray().Select(value => value.GetSingle())];
        Assert.That(filament, Is.EqualTo(new[] { 5F }), $"{filament.Length} filaments");
    }

    /// <summary>
    /// The file info of a file with a value beyond the float range could not be serialized, because System.Text.Json refuses NaN and infinity (CU1):
    /// M36 replied {"err":1,...}, and a selected file made the object model unserializable. Fixed in DSF #223: the footer scan skips a G0/G1 whose Z is
    /// not finite, as RRF does (FileInfoParser.cs:227, RRF FileInfoParser.cpp:497-498), and filament in m is checked after the conversion to mm
    /// (FileInfoParser.cs:501, :521). "1e39" parses to +Infinity (float.TryParse in DuetAPI Parameter.cs:153), and 1e36 m is a finite float whose 1e39 mm is
    /// not. Unfixed, Height is +Infinity and the first filament +Infinity, so M36 replies {"err":1,...}: the unit tests
    /// InfoParserNonFinite.ZBeyondFloatRangeInFooter and FilamentInMetresBeyondFloatRangeNextToFiniteOne, from which the content is taken, failed with the
    /// ArgumentException of System.Text.Json before the fix (PR #223). Deterministic, one M36. The M23 case, which changes the selected job, is not tested
    /// </summary>
    [Test]
    public async Task NonFiniteValuesKeepFileInfoReadable()
    {
        await G3Helpers.RequireControlServerWithNewFixesAsync("#223");
        string tooBigForMm = "1" + new string('0', 36);
        using JsonDocument info = await ReadFileInfoAsync("non-finite.g",
            G2Helpers.JobFileHeader + $";Filament used: {tooBigForMm}m, 1.5m\nG1 X1 Z0.3\nG1 X2 Z0.6\nG1 Z1e39\n");
        JsonElement root = info.RootElement;
        Assert.That(root.GetProperty("err").GetInt32(), Is.Zero, "The file info could not be read");
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("height").GetSingle(), Is.EqualTo(0.6F), "The height is not that of the last move with a finite Z");
            Assert.That(root.GetProperty("filament").EnumerateArray().Select(value => value.GetSingle()), Is.EqualTo(new[] { 1500F }));
        });
    }

    /// <summary>
    /// Nesting depth of the customInfo value, as in the unit test ExpressionNesting.CustomInfoWithDeepNestingIsSkipped. 8,600 levels ended DCS on a
    /// 1.5 MB stack in an x86_64 Debug build (PR #218), so this is far beyond any stack of DCS
    /// </summary>
    private const int DeepNesting = 100_000;

    /// <summary>
    /// A ";customInfo key = {...}" line in the header of a job file is evaluated whenever the file info is read (FileInfoParser.cs:563-577), and a deeply
    /// nested value ended DCS with a stack overflow, which .NET cannot catch: one uploaded file ended DCS whenever a client listed its folder (ST1).
    /// Fixed in DSF #218: MetaExpressionParser.ParseInternal refuses an expression with "Expression nesting too deep" when
    /// RuntimeHelpers.TryEnsureSufficientExecutionStack reports a low stack (MetaExpressionParser.cs:120-122), as RRF's ExpressionParser::CheckStack
    /// does, and FileInfoParser leaves the key out like any key that fails to evaluate (FileInfoParser.cs:101-114). The fast path of the evaluation
    /// (Expressions.cs:1016-1032) takes the value to that parser. Unfixed, M36 ends DCS: the unit test CustomInfoWithDeepNestingIsSkipped ended
    /// the test host with "Stack overflow" before the fix (PR #218); here systemd restarts DCS, RRF loses the link and stops, and the test resets it.
    /// Deterministic, one M36. The other half of #218, a deeply nested expression in a code, is not tested here
    /// </summary>
    [Test]
    public async Task DeeplyNestedCustomInfoIsSkipped()
    {
        await G3Helpers.RequireControlServerWithNewFixesAsync("#218");
        string nested = new string('(', DeepNesting) + "1" + new string(')', DeepNesting);
        string cursor = await Lab.GetJournalCursorAsync();
        try
        {
            await CheckNestedCustomInfoAsync(nested);
        }
        finally
        {
            // The refusal is logged with a stack trace of thousands of lines (lab run 2026-11-25), which journald limits
            await G1Helpers.LetJournaldRateLimitPassAsync(cursor);
        }
    }

    /// <summary>
    /// Read the file info of a file whose customInfo value is nested too deeply and check it
    /// </summary>
    private async Task CheckNestedCustomInfoAsync(string nested)
    {
        using JsonDocument info = await ReadFileInfoAsync("nested-custom-info.g", G2Helpers.JobFileHeader + $";customInfo deep = {{{nested}}}\n;customInfo ok = 1\nG1 X1\n");
        JsonElement root = info.RootElement;
        Assert.That(root.GetProperty("err").GetInt32(), Is.Zero, "The file info could not be read");
        JsonElement customInfo = root.GetProperty("customInfo");
        Assert.Multiple(() =>
        {
            Assert.That(customInfo.TryGetProperty("deep", out _), Is.False, "The deeply nested key has a value");
            Assert.That(customInfo.TryGetProperty("ok", out JsonElement ok) ? ok.GetInt32() : (int?)null, Is.EqualTo(1), "The key after the nested one is missing");
        });
    }
}
