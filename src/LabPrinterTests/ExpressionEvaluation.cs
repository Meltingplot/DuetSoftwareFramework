using System;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Expressions and parameters that DSF evaluates itself
/// </summary>
/// <remarks>
/// DSF evaluates an expression on the SBC when it can resolve every identifier in it (Codes/Meta/Expressions.cs:1015-1031), so these tests use
/// literals and SBC fields only, never a global (G1Helpers). Each fix is in DuetAPI or DuetControlServer and reaches the printer with the side-load
/// </remarks>
[TestFixture]
public class ExpressionEvaluation : LabTest
{
    private const string BigGlobal = "labTestOversized";

    /// <summary>
    /// <c>M20 S31e9</c> ended with an OverflowException instead of an error reply (DSF #162, fuzz finding G12).
    /// </summary>
    /// <remarks>
    /// M20 runs in DSF (Codes/Handlers/MCodeHandler.cs:146) and reads S as an int (:167). DSF parses 31e9 as a float, and before #162 the int
    /// conversion of CodeParameter called Convert.ToInt32, which throws an OverflowException for it. Code.ProcessInternally turns only a
    /// MissingParameterException or an InvalidParameterTypeException into an error reply (Commands/Generic/Code.cs:248-253), so the
    /// OverflowException reached DWS, which answers HTTP 500 (DuetWebServer/Endpoints/EndpointHelper.cs:171-172), and the session throws an
    /// HttpRequestException. Since #162 a value that does not fit fails Fits (DuetAPI/Commands/Code/Parameter.cs:248, :319) and the conversion throws
    /// InvalidParameterTypeException with "Cannot convert S parameter to Int32 (value 31e9)" (DuetAPI/Exceptions/InvalidParameterTypeException.cs:11).
    /// The control with S2 and C1 shows that M20 itself works (MCodeHandler.cs:167-172). No restart either way
    /// </remarks>
    [Test]
    public async Task ParameterOutOfRangeIsAnError()
    {
        Assert.That(await SendCodeAsync("M20 S2 C1 P\"0:/sys\""), Does.StartWith("{").And.Not.Contain("Error"), "M20 S2 does not list 0:/sys");

        string reply;
        try
        {
            reply = await SendCodeAsync("M20 S31e9 P\"0:/sys\"");
        }
        catch (HttpRequestException e)
        {
            Assert.Fail($"M20 S31e9 got no reply but {e.Message}, the OverflowException of the conversion reached DWS");
            throw;
        }
        Assert.That(reply, Does.StartWith("Error").And.Contain("Cannot convert S parameter to Int32 (value 31e9)"));
    }

    /// <summary>
    /// An expression nested too deeply ended DCS with a stack overflow (DSF #218, census finding ST1). DCS now refuses it like RRF's CheckStack
    /// </summary>
    /// <remarks>
    /// Both parsers of DCS descend once per bracket. A stack overflow cannot be caught in .NET, so DCS ended and systemd started it again; RRF lost the
    /// link (PR #218: about 8.6 k brackets on a 1.5 MB stack, x86_64 Debug). Since #218 both check the stack that is left and throw a
    /// CodeParserException "Expression nesting too deep": MetaExpressionParser.ParseInternal (Codes/Meta/Parsing/MetaExpressionParser.cs:117-123)
    /// and eatExpression, which only replaces SBC fields (Codes/Meta/Expressions.cs:867-873). A keyword argument such as echo is limited to 255
    /// characters (DuetAPI/Commands/Code/Parser.cs:790-792), so the cases use parameters, whose length is not limited, and 200,000 brackets:
    /// <list type="bullet">
    /// <item>parser: M20 S{...} runs in DSF, which evaluates all of its expressions before it reads them (MCodeHandler.cs:146-147,
    /// Codes/CodeProcessor.cs:165-169). Without identifiers the whole expression goes to MetaExpressionParser.TryEvaluate (Expressions.cs:1015-1031)</item>
    /// <item>SBC fields: G4 is not handled by DSF (Codes/Handlers/GCodeHandler.cs:18). Its parameter holds the SBC field sbc.dsf.pluginSupport, so DSF
    /// replaces only that before it sends the code (Commands/Generic/Code.cs:213-216, Expressions.cs:463-466) and eatExpression descends once per
    /// bracket (Expressions.cs:891-894). The refused code never reaches RRF; if it did, G4 P0 would only dwell for 0 ms</item>
    /// </list>
    /// The unfixed build ends DCS (the PR's counter-check ended the unit test host for both paths with "Stack overflow"), and the test restarts DCS
    /// and resets RRF before it fails. Before DSF #251 each refusal logged the exception with a stack trace of thousands of lines (finding LT3), so the
    /// test waits for the rate limit of journald afterwards, also when the test fails (G1Helpers.LetJournaldRateLimitPassAsync), and then checks that
    /// the refusal left at most G1Helpers.MaxRefusalLines lines
    /// </remarks>
    [TestCase("M20 P\"0:/sys\" S", "1", TestName = "DeeplyNestedExpressionIsRefused(parser)")]
    [TestCase("G4 P", "sbc.dsf.pluginSupport ? 0 : 0", TestName = "DeeplyNestedExpressionIsRefused(SBC fields)")]
    public async Task DeeplyNestedExpressionIsRefused(string codeStart, string innermost)
    {
        await G3Helpers.RequireControlServerWithNewFixesAsync("#218");
        const int depth = 200_000;
        string code = $"{codeStart}{{{new string('(', depth)}{innermost}{new string(')', depth)}}}";
        string pid = await Lab.GetControlServerPidAsync();
        string cursor = await Lab.GetJournalCursorAsync();

        int lines;
        try
        {
            string reply = await G1Helpers.SendOrRecoverAsync(Session, code, pid, 60);
            TestContext.Progress.WriteLine($"Reply: {G1Helpers.Shorten(reply.Trim())}");
            await G1Helpers.AssertControlServerGoesOnAsync(Session, pid, $"{depth} nested brackets");
            Assert.That(reply, Does.StartWith("Error").And.Contain(G1Helpers.NestingTooDeep));
        }
        finally
        {
            lines = await G1Helpers.LetJournaldRateLimitPassAsync(cursor);
        }
        Assert.That(lines, Is.LessThan(G1Helpers.MaxRefusalLines), "The refusal was logged with a stack frame per nesting level (DSF #251)");
    }

    /// <summary>
    /// datetime() of a string that RRF accepts was refused by DSF with its own error "can't convert value to DateTime" (DSF #238, CU9)
    /// </summary>
    /// <remarks>
    /// Since #238 DateTimeFunction returns null for a string that DateTime.TryParse cannot convert (Codes/Meta/Parsing/MetaExpressionParser.cs:1150-1173),
    /// TryEvaluateBuiltinFunction then returns false (:981-986) and the expression goes to RRF (Expressions.cs:1033-1049). RRF reads it with
    /// strptime "%Y-%m-%dT%H:%M:%S" (RRF ExpressionParser.cpp:2436-2442), which takes fields without leading zeros and ignores the rest of the
    /// string, and refuses other strings with "string is not a valid date and time" (:2440). Month 99 is refused since RRF #104 (strptime.cpp:143).
    /// RRF prints the result with gmtime_r as yyyy-mm-ddThh:mm:ss (ObjectModel.cpp:80-87), DSF reads it back (Link/Protocol/Reader.cs:443-445) and
    /// prints it with the format "s" (Expressions.cs:561-564). A date-only string is still converted by DSF, on purpose (PR #238, "Kept on purpose").
    /// Unfixed: the first two strings and month 99 get "can't convert value to DateTime" from DSF instead of RRF's date or error
    /// </remarks>
    [TestCase("2026-1-5T0:0:0", "2026-01-05T00:00:00")]
    [TestCase("2026-12-01T10:20:30x", "2026-12-01T10:20:30")]
    [TestCase("2026-12-01", "2026-12-01T00:00:00")]
    [TestCase("2026-99-01T00:00:00", null)]
    public async Task DateTimeStringsGoToRrf(string text, string? expected)
    {
        await G3Helpers.RequireControlServerWithNewFixesAsync("#238");
        if (expected is null)
        {
            G7Helpers.RequireMainBoardFixes(Session, G7Helpers.Rrf104);
        }
        string reply = (await SendCodeAsync($"echo {{datetime(\"{text}\")}}", 30)).Trim();
        if (expected is null)
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("string is not a valid date and time"),
                "The error is not RRF's, so DSF refused the string itself or RRF accepted month 99 (RRF #104 missing)");
        }
        else
        {
            Assert.That(reply, Is.EqualTo(expected));
        }
    }

    /// <summary>
    /// floor(), ceil(), round() and pow() gave the int 2147483647 instead of the float 2147483648 for 2^31 (DSF #249, finding LT2; RRF #110 fixed the same
    /// bound in the firmware)
    /// </summary>
    /// <remarks>
    /// echo of literals is evaluated by DSF (class remarks). Since #249 FloatToIntOrFloat and Pow return an int only below 2^31 and from -2^31 on
    /// (Codes/Meta/Parsing/MetaExpressionParser.cs:1016, :1052), like RRF ExpressionParser.cpp:2038, :2293. Before, the bound was f &lt;= int.MaxValue, which
    /// is 2^31 as a float, so 2^31 passed and the conversion gave 2147483647 (measured on .NET 10 x64). DSF prints an int with digits only and a float in the
    /// shortest round-trip form (Expressions.cs:545-556, finding CU7), so the test parses the reply instead of comparing its text. The controls just inside
    /// the range stay ints. Unfixed: the four 2^31 replies are the int 2147483647. No restart
    /// </remarks>
    [TestCase("floor(2147483648.0)", null)]
    [TestCase("ceil(2147483648.0)", null)]
    [TestCase("round(2147483648.0)", null)]
    [TestCase("pow(2, 31)", null)]
    [TestCase("floor(2147483520.0)", "2147483520")]
    [TestCase("pow(-2, 31)", "-2147483648")]
    public async Task TwoToThe31IsAFloat(string expression, string? expectedInt)
    {
        await G3Helpers.RequireControlServerWithNewFixesAsync("#249");
        string reply = (await SendCodeAsync($"echo {{{expression}}}")).Trim();
        if (expectedInt is not null)
        {
            Assert.That(reply, Is.EqualTo(expectedInt));
            return;
        }
        Assert.That(int.TryParse(reply, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _), Is.False, $"{expression} gave the int {reply}");
        Assert.That(float.TryParse(reply, NumberStyles.Float, CultureInfo.InvariantCulture, out float value), Is.True, $"{expression} gave {reply}");
        Assert.That(value, Is.EqualTo(2147483648f));
    }

    /// <summary>
    /// Values whose JSON can never come back over the SBC link reached RRF when DSF could evaluate them (DSF #239)
    /// </summary>
    /// <remarks>
    /// On 2026-11-24 a global of vector(128,vector(128,vector(20,10))) was created, but the object model key "global" could no longer be fetched; from
    /// 21 on RRF asked for the SetVariable again after it had assigned the value (PR #239, RRF #113, #114). Since #239 DSF evaluates the value of
    /// global, var and set itself where it can and refuses it with RRF's text "value too long to send to the SBC" before it sends it
    /// (Codes/Handlers/KeywordHandler.cs:271-276, :306-319): a global whose JSON is longer than one object model response
    /// (Codes/Meta/VariableValueLength.cs:46, :55-74). The JSON of the n = 20 value is about 1 MB. vector() with more than 255 elements is refused
    /// with "Array too large" as in RRF (Codes/Meta/Parsing/MetaExpressionParser.cs:39, :959-971, :1531-1537). The values are literals, so DSF
    /// evaluates them; set is used on a global that holds 0, so the error cannot come from a global that already exists.
    /// Unfixed DSF: the length is 256, and the n = 20 value is assigned, which the echo of the global shows (RRF #114 refuses to send it, so the
    /// echo is not 0). n = 30 is refused by RRF #114 with the same text, so only the other two cases tell the builds apart. Without DSF #239 and
    /// RRF #114 the set of n = 30 is never answered (PR #114), and the test restarts DCS and resets RRF before it fails
    /// </remarks>
    [Test]
    public async Task OversizedArraysAndGlobalsAreRefused()
    {
        await G3Helpers.RequireControlServerWithNewFixesAsync("#239");
        G7Helpers.RequireMainBoardFixes(Session, G7Helpers.Rrf114);
        Assert.That((await SendCodeAsync("echo {#vector(256, 0)}")).Trim(), Does.Contain(G1Helpers.ArrayTooLarge));
        Assert.That((await SendCodeAsync("echo {#vector(255, 0)}")).Trim(), Is.EqualTo("255"), "The largest array that RRF allows");

        await G1Helpers.SetGlobalAsync(Session, BigGlobal, "0");
        string pid = await Lab.GetControlServerPidAsync();
        try
        {
            foreach (int n in new[] { 20, 30 })
            {
                string reply = await G1Helpers.SendOrRecoverAsync(Session, $"set global.{BigGlobal} = vector(128, vector(128, vector({n}, 10)))", pid);
                string value = (await G1Helpers.SendOrRecoverAsync(Session, $"echo {{global.{BigGlobal}}}", pid)).Trim();
                Assert.Multiple(() =>
                {
                    Assert.That(reply, Does.StartWith("Error").And.Contain(G1Helpers.ValueTooLong), $"n = {n}");
                    Assert.That(value, Is.EqualTo("0"), $"n = {n}: the global was assigned");
                });
            }
        }
        finally
        {
            // A global that RRF cannot report keeps the key "global" of DSF stale until it gets a short value
            await G1Helpers.TryCleanUpAsync(Session, $"set global.{BigGlobal} = 0");
        }
    }

    /// <summary>
    /// fileexists() with a relative name looked in 0:/ instead of the system directory (DSF #240)
    /// </summary>
    /// <remarks>
    /// fileexists() is a function of DSF (Codes/Meta/Functions.cs:28), which DSF evaluates in every code from HTTP. RRF combines a relative name
    /// with the system directory (Platform::SysFileExists), and since #240 so does DSF (Functions.cs:66-75, FileDirectory.System). A name that starts
    /// with a drive is unchanged. config.g is in the system directory of every machine (directories.system). Unfixed: "config.g" means 0:/config.g,
    /// so the first case is false, and "sys/config.g" means 0:/sys/config.g, so the second is true where the system directory is 0:/sys/ (the unit
    /// test MetaExpressions.FileExists: "sys/a.g no longer means 0:/sys/a.g")
    /// </remarks>
    [Test]
    public async Task FileExistsLooksInTheSystemDirectory()
    {
        await G3Helpers.RequireControlServerWithNewFixesAsync("#240");
        string? systemDirectory = Lab.Read(Session, model => model.Directories.System);
        Assume.That(systemDirectory, Is.Not.Null.And.Not.Empty, "The model has no system directory");
        string absolute = systemDirectory!.TrimEnd('/') + "/config.g";
        Assume.That((await SendCodeAsync($"echo fileexists(\"{absolute}\")")).Trim(), Is.EqualTo("true"), $"{absolute} does not exist");

        Assume.That((await SendCodeAsync($"echo fileexists(\"{systemDirectory.TrimEnd('/')}/sys/config.g\")")).Trim(), Is.EqualTo("false"),
            "The system directory has a folder sys with a config.g, so the second case shows nothing");

        string relative = (await SendCodeAsync("echo fileexists(\"config.g\")")).Trim();
        string relativeWithFolder = (await SendCodeAsync("echo fileexists(\"sys/config.g\")")).Trim();
        Assert.Multiple(() =>
        {
            Assert.That(relative, Is.EqualTo("true"), "config.g was not looked for in the system directory");
            Assert.That(relativeWithFolder, Is.EqualTo("false"), "sys/config.g was not looked for below the system directory");
        });
    }

    /// <summary>
    /// A date and time beyond the year 9999 from RRF stopped DCS instead of failing the code (DSF #250, finding LT1)
    /// </summary>
    /// <remarks>
    /// state.upTime makes RRF evaluate the expression, because DSF resolves only SBC fields and its own functions (G1Helpers, Expressions.cs:1015-1048,
    /// Functions.cs:27-29); 0 * state.upTime is the int 0 (RRF RepRap.cpp:381). RRF parses the string with SafeStrptime and mktime (ExpressionParser.cpp,
    /// ParseDateTime), adds an int to a DateTime without a range check (:508-517) and prints the year with %04u (ObjectModel.cpp:80-88), so
    /// 10000-01-01T00:00:00 reaches DSF as an evaluation result of type DateTime (DataTransfer.cpp:1820-1829). Since #250 Reader answers a date that
    /// DateTime cannot hold with a CodeParserException (Link/Protocol/Reader.cs:443-447), and only this code fails. The control without + 1 checks that
    /// RRF handles the year 9999 at all. Unfixed: Reader threw, the exception left LinkService.ProcessPacket and ended the link (from reading, not run on
    /// the printer); the test then restarts DCS and resets RRF before it fails (one E-stop). No restart with the fix
    /// </remarks>
    [Test]
    public async Task DateBeyondYear9999IsAnError()
    {
        await G3Helpers.RequireControlServerWithNewFixesAsync("#250");
        const string date = "datetime(\"9999-12-31T23:59:59\")";
        string pid = await Lab.GetControlServerPidAsync();

        string control = (await G1Helpers.SendOrRecoverAsync(Session, $"echo {{{date} + 0 * state.upTime}}", pid)).Trim();
        Assume.That(control, Is.EqualTo("9999-12-31T23:59:59"), "RRF does not handle the year 9999, so it cannot produce a later date");

        string reply = (await G1Helpers.SendOrRecoverAsync(Session, $"echo {{{date} + 1 + 0 * state.upTime}}", pid)).Trim();
        await G1Helpers.AssertControlServerGoesOnAsync(Session, pid, "a date beyond the year 9999");
        Assert.That(reply, Does.StartWith("Error").And.Contain("date and time 10000-01-01T00:00:00 is out of range"));
    }
}
