using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Helpers of the tests of expressions and the object model (ExpressionEvaluation, RrfExpressions, VariableAnswers, ObjectModelSeqs,
/// ObjectModelSerialization)
/// </summary>
/// <remarks>
/// Which side evaluates an expression decides what a test checks. DSF evaluates it itself only when it can resolve every identifier: the fast path
/// of Expressions.EvaluateExpressionAsync (Codes/Meta/Expressions.cs:1015-1031) gives the ExpressionContext only SBC fields
/// (Expressions.cs:1123, :1132; [SbcProperty] in DuetAPI/ObjectModel/ObjectModel.cs). Anything else, e.g. global.*, var.*, state.* or move.*,
/// goes to RRF as an EvaluateExpression request with the SBC fields replaced (Expressions.cs:1033-1049). global, var and set always go to RRF
/// as a SetVariable request (Codes/Handlers/KeywordHandler.cs:261-279)
/// </remarks>
internal static partial class G1Helpers
{
    /// <summary>
    /// Error text of RRF's ExpressionParser::CheckStack and of DSF since #218 (MetaExpressionParser.cs:122, Expressions.cs:872)
    /// </summary>
    public const string NestingTooDeep = "Expression nesting too deep";

    /// <summary>
    /// Error text of RRF for a value whose answer cannot be sent to the SBC (RRF SbcInterface.cpp:56), which DSF uses as well
    /// (Codes/Meta/VariableValueLength.cs:26)
    /// </summary>
    public const string ValueTooLong = "value too long to send to the SBC";

    /// <summary>
    /// Error text of RRF for an array that does not fit into a heap block (RRF ArrayHandle.cpp), which DSF uses as well
    /// (Codes/Meta/Parsing/ExpressionErrors.cs:27)
    /// </summary>
    public const string ArrayTooLarge = "Array too large";

    /// <summary>
    /// Heap line of M122, RRF Heap::Diagnostics (src/Platform/Heap.cpp:386-396), called for section 5 of Platform::Diagnostics
    /// </summary>
    [GeneratedRegex(@"heap memory allocated/used/recyclable (\d+)/(\d+)/(\d+), gc cycles (\d+)")]
    public static partial Regex HeapRegex();

    /// <summary>
    /// Text of a macro block that creates a global variable or sets it if an earlier run has left it, since globals stay until the next reset
    /// (as VariableCopies.Global)
    /// </summary>
    public static string Global(string name, string value) => $"""
        if !exists(global.{name})
          global {name} = {value}
        else
          set global.{name} = {value}

        """;

    /// <summary>
    /// Check if a global variable exists. RRF answers exists() of a global, because DSF answers it only for SBC paths (Expressions.cs:1109-1130)
    /// </summary>
    public static async Task<bool> GlobalExistsAsync(DuetHttpSession session, string name) =>
        (await Lab.SendCodeAsync(session, $"echo exists(global.{name})", TimeSpan.FromSeconds(30))).Trim() == "true";

    /// <summary>
    /// Create a global variable of a test or set it, and wait until the model of the session has it
    /// </summary>
    public static async Task SetGlobalAsync(DuetHttpSession session, string name, string expression)
    {
        string code = await GlobalExistsAsync(session, name) ? $"set global.{name} = {expression}" : $"global {name} = {expression}";
        string reply = await Lab.SendCodeAsync(session, code, TimeSpan.FromSeconds(30));
        Assert.That(reply, Does.Not.Contain("Error"), $"{code} failed");
        await Lab.WaitForModelAsync(session, model => model.Global.ContainsKey(name), TimeSpan.FromSeconds(30), $"global.{name} in the model");
    }

    /// <summary>
    /// Query an object model value of RRF. M409 with R1 is passed on to RRF (Codes/Handlers/MCodeHandler.cs:846-850, case 409 at :844)
    /// </summary>
    public static async Task<JsonElement> QueryFirmwareAsync(DuetHttpSession session, string key)
    {
        using JsonDocument reply = JsonDocument.Parse(await Lab.SendCodeAsync(session, $"M409 K\"{key}\" R1", TimeSpan.FromSeconds(30)));
        return reply.RootElement.GetProperty("result").Clone();
    }

    /// <summary>
    /// Send a code that may stop DCS or RRF without the fix. Without a reply DCS is restarted and RRF reset before the test fails
    /// (as RefusedMacroPaths.SendOrRecoverAsync)
    /// </summary>
    /// <returns>Reply of the code</returns>
    public static async Task<string> SendOrRecoverAsync(DuetHttpSession session, string code, string pid, int timeoutSeconds = 30)
    {
        try
        {
            return await Lab.SendCodeAsync(session, code, TimeSpan.FromSeconds(timeoutSeconds));
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            string newPid = await Lab.GetControlServerPidAsync();
            await Lab.RecoverAsync();
            Assert.Fail($"{Shorten(code)} got no reply ({e.GetType().Name}: {e.Message}), DCS pid {pid} before and {newPid} after");
            throw;
        }
    }

    /// <summary>
    /// Send a code to clean up in a finally block. A failure is only logged, so that it does not hide the result of the test, e.g. after a recovery
    /// </summary>
    public static async Task TryCleanUpAsync(DuetHttpSession session, string code)
    {
        try
        {
            string reply = await Lab.SendCodeAsync(session, code, TimeSpan.FromSeconds(60));
            if (reply.Contains("Error"))
            {
                TestContext.Progress.WriteLine($"Clean-up {code}: {reply.Trim()}");
            }
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            TestContext.Progress.WriteLine($"Clean-up {code} failed: {e.Message}");
        }
    }

    /// <summary>
    /// Check that DCS is still the same process, RRF is not halted and answers. Recover before the test fails, so that the next test finds a
    /// working printer (as RefusedMacroPaths.AssertDcsGoesOnAsync)
    /// </summary>
    public static async Task AssertControlServerGoesOnAsync(DuetHttpSession session, string pid, string what)
    {
        // R1 makes DSF pass the query on to RRF (MCodeHandler.cs:846-850)
        string firmwareReply = await SendOrRecoverAsync(session, "M409 K\"state.upTime\" R1", pid);
        string newPid = await Lab.GetControlServerPidAsync();
        bool halted = Lab.Read(session, model => model.State.Status) == MachineStatus.Halted;
        if (newPid != pid || halted || !firmwareReply.Contains("\"result\":"))
        {
            await Lab.RecoverAsync();
        }
        Assert.Multiple(() =>
        {
            Assert.That(newPid, Is.EqualTo(pid), $"DCS was restarted after {what}");
            Assert.That(firmwareReply, Does.Contain("\"result\":"), $"RRF did not answer M409 R1 after {what}");
            Assert.That(halted, Is.False, $"RRF is halted after {what}");
        });
    }

    /// <summary>
    /// Count the lines of the DCS log since a cursor that contain a text. The count runs on the SBC, so that a flood of log lines is not copied
    /// </summary>
    public static async Task<int> CountJournalLinesAsync(string cursor, string? text = null)
    {
        string filter = (text is null) ? string.Empty : $" | grep -F -- '{text}'";
        string output = await Lab.RunOnSbcAsync($"sudo -n journalctl -u duetcontrolserver --after-cursor='{cursor}' -o cat --no-pager{filter} | wc -l");
        return int.Parse(output.Trim());
    }

    /// <summary>
    /// Wait until journald takes the lines of DCS again after a flood of them. An exception that DCS logs with a deep stack trace has thousands of
    /// lines, and journald drops further lines of the unit for the rest of its rate limit interval once a unit exceeds its burst (systemd defaults
    /// RateLimitIntervalSec=30s, RateLimitBurst=10000, journald.conf(5); it notes "Suppressed N messages"). Later tests would miss the lines
    /// they look for
    /// </summary>
    public static async Task LetJournaldRateLimitPassAsync(string cursor)
    {
        int lines = await CountJournalLinesAsync(cursor);
        string suppressed = await Lab.RunOnSbcAsync(
            $"sudo -n journalctl -u systemd-journald --after-cursor='{cursor}' -o cat --no-pager | grep -F Suppressed | grep -c -F duetcontrolserver || true");
        TestContext.Progress.WriteLine($"DCS logged {lines} lines, journald suppression notes: {suppressed.Trim()}");
        if (lines > 2000 || suppressed.Trim() != "0")
        {
            await Task.Delay(TimeSpan.FromSeconds(31));
        }
    }

    /// <summary>
    /// Longest time in seconds in which the model of the session showed no new state.upTime, which RRF counts once a second
    /// (as OutputBufferPressure.SampleUpTimeGapAsync)
    /// </summary>
    public static async Task<double> SampleUpTimeGapAsync(DuetHttpSession session, CancellationToken cancellationToken)
    {
        int lastUpTime = Lab.Read(session, model => model.State.UpTime);
        Stopwatch sinceChange = Stopwatch.StartNew();
        double maxGap = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            int upTime = Lab.Read(session, model => model.State.UpTime);
            if (upTime != lastUpTime)
            {
                maxGap = Math.Max(maxGap, sinceChange.Elapsed.TotalSeconds);
                lastUpTime = upTime;
                sinceChange.Restart();
            }
            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return Math.Max(maxGap, sinceChange.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Shorten a long code for a message
    /// </summary>
    public static string Shorten(string text) => (text.Length <= 120) ? text : $"{text[..100]}... ({text.Length} characters)";
}
