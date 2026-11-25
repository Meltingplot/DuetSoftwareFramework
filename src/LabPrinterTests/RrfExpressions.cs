using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Expressions that RRF evaluates
/// </summary>
/// <remarks>
/// Each expression reads a global of the test or a field of RRF's object model, so DSF sends it to RRF instead of evaluating it (G1Helpers). With a
/// literal in its place DSF would evaluate it itself, and the test would show nothing about RRF (PR #104, "In SBC mode the string has to come from
/// a variable"). The fixes are in the RRF fork's 3.7-dev and need an RRF build with them
/// </remarks>
[TestFixture]
public class RrfExpressions : LabTest
{
    private const string MacroFolder = $"{Lab.MacroDirectory}/rrf-expressions";
    private const string GcMacro = $"{MacroFolder}/take-drop-gc.g";
    private const string DateTimeGlobal = "labTestDateTime";

    /// <summary>
    /// Number of rounds of the take/drop macro. Each round lays out a gap, the operand and three fillers in the heap and then releases the gap
    /// </summary>
    private const int GcRounds = 30;

    [OneTimeTearDown]
    public async Task DeleteMacrosAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, GcMacro);
            await Lab.DeleteAsync(session, MacroFolder);
        }
    }

    /// <summary>
    /// Put a date and time string into a global and let RRF convert it with datetime()
    /// </summary>
    private async Task<string> DateTimeOfGlobalAsync(string text, string pid)
    {
        await G1Helpers.SetGlobalAsync(Session, DateTimeGlobal, $"\"{text}\"");
        return (await G1Helpers.SendOrRecoverAsync(Session, $"echo {{datetime(global.{DateTimeGlobal})}}", pid)).Trim();
    }

    /// <summary>
    /// datetime() accepted a month outside 1 to 12 and read outside the days-before-month table (RRF #104, fuzz finding E6)
    /// </summary>
    /// <remarks>
    /// SafeStrptime stored the month as tm_mon = month - 1 without a check and then read _DAYS_BEFORE_MONTH[tm_mon] (RRF src/libc/strptime.cpp:237).
    /// Since #104 it refuses a month outside 1 to 12 (strptime.cpp:143), and ParseDateTime throws "string is not a valid date and time"
    /// (ExpressionParser.cpp:2436-2442). RRF prints a valid result with gmtime_r (ObjectModel.cpp:80-87). Unfixed: month 99 gave 2034-03-01T00:00:00
    /// on the lab printer (PR #104, "On a board"); what months 0 and 13 give was not checked, a date in any case and not the error
    /// </remarks>
    [TestCase("2026-00-01T00:00:00")]
    [TestCase("2026-13-01T00:00:00")]
    [TestCase("2026-99-01T00:00:00")]
    public async Task DateTimeRefusesMonthOutsideOneToTwelve(string text)
    {
        string pid = await Lab.GetControlServerPidAsync();
        Assert.That(await DateTimeOfGlobalAsync("2026-11-24T01:02:03", pid), Is.EqualTo("2026-11-24T01:02:03"), "RRF does not convert a valid date from a global");

        string reply = await DateTimeOfGlobalAsync(text, pid);
        await G1Helpers.AssertControlServerGoesOnAsync(Session, pid, $"datetime(\"{text}\")");
        Assert.That(reply, Does.StartWith("Error").And.Contain("string is not a valid date and time"));
    }

    /// <summary>
    /// datetime() took a year outside the range of mktime and looped once per year since 1970 in the evaluating task (RRF #109, fuzz finding E7a)
    /// </summary>
    /// <remarks>
    /// SafeStrptime put no limit on %Y; first_day loops once per year since 1970 (strptime.cpp:76-84), and mktime returns -1 for a tm_year beyond
    /// ±10000 (PR #109). Since #109 a year outside 1900 ± 10000 is refused (strptime.cpp:185), so datetime() throws "string is not a valid date and
    /// time" (ExpressionParser.cpp:2440). Only the first years outside the range are used, 11901 and -8101, which loop about 10,000 times without the
    /// fix; year 2147483647 would loop for minutes (PR estimate) and is never used. Unfixed: the parse succeeds and mktime gives -1. RRF stores a
    /// date-time in 56 bits (ObjectModel.h:161, :167, :170), so -1 comes back as 2^56 - 1 seconds, which gmtime_r cannot turn into a date of four
    /// digits; DSF parses the text with DateTime.TryParse and throws in its link thread when that fails (Link/Protocol/Reader.cs:443-445,
    /// LinkService.cs:448-458). From reading, not run: the unfixed build may stop DCS, so the test restarts DCS and resets RRF before it fails
    /// </remarks>
    [TestCase("11901-01-01T00:00:00")]
    [TestCase("-8101-01-01T00:00:00")]
    public async Task DateTimeRefusesYearOutsideMktimeRange(string text)
    {
        string pid = await Lab.GetControlServerPidAsync();
        string reply = await DateTimeOfGlobalAsync(text, pid);
        await G1Helpers.AssertControlServerGoesOnAsync(Session, pid, $"datetime(\"{text}\")");
        Assert.That(reply, Does.StartWith("Error").And.Contain("string is not a valid date and time"));
    }

    /// <summary>
    /// floor(), ceil(), round() and pow() returned a wrong int instead of the float 2147483648 when the result was 2^31 (RRF #110, fuzz finding E3)
    /// </summary>
    /// <remarks>
    /// (float)INT32_MAX is 2^31, so the old checks f &lt;= (float)INT32_MAX let 2^31 through to a conversion that cannot hold it; on the board VCVT
    /// saturates to 2147483647 (PR #110, from the disassembly, not run). Since #110 the bound is f &lt; (float)INT32_MAX (RRF ExpressionParser.cpp:2032-2046,
    /// pow :2281-2302) and the result stays the float 2147483648. mod() then takes the float path, fmodf(2147483648, 10) = 8, where the int path gives
    /// 2147483647 % 10 = 7 (:2048-2066). A plain == cannot tell them apart, because (float)2147483647 is 2147483648. The value is a float global,
    /// because DSF would evaluate a literal itself. pow(2, 30) stays an int, as before
    /// </remarks>
    [TestCase("floor(global.labTestTwoTo31)")]
    [TestCase("ceil(global.labTestTwoTo31)")]
    [TestCase("round(global.labTestTwoTo31)")]
    [TestCase("pow(global.labTestTwo, 31)")]
    public async Task RoundingOfTwoToTheThirtyOneStaysFloat(string expression)
    {
        await G1Helpers.SetGlobalAsync(Session, "labTestTwoTo31", "2147483648.0");
        await G1Helpers.SetGlobalAsync(Session, "labTestTwo", "2");
        Assert.That((await SendCodeAsync("echo {pow(global.labTestTwo, 30)}")).Trim(), Is.EqualTo("1073741824"), "pow(2, 30) is no longer the int 2^30");

        string reply = (await SendCodeAsync($"echo {{mod({expression}, 10)}}")).Trim();
        Assert.That(double.TryParse(reply, NumberStyles.Float, CultureInfo.InvariantCulture, out double rest) ? rest : double.NaN, Is.EqualTo(8),
            $"mod({expression}, 10) is \"{reply}\", 7 means the int 2147483647");
    }

    /// <summary>
    /// vector() with 536870912 or more elements wrapped the size of the array around and wrote beyond the heap (RRF #102, fuzz finding E5)
    /// </summary>
    /// <remarks>
    /// On the board (32-bit size_t, 8-byte ExpressionValue) 2^29 elements ask for 4 bytes, the space check passes and the loop constructs 2^29
    /// elements beyond them (PR #102, from the disassembly, not run on a board). Since #102 ArrayHandle::Allocate refuses such a count with "Array too
    /// large" before it touches the heap (RRF ArrayHandle.cpp:28-32). The count is a global: DSF refuses the literal form itself since DSF #239
    /// (MetaExpressionParser.cs:1531-1537). Unfixed: memory corruption, then a fault and a reset of RRF, an E-stop with ATX off on the CHX; the test
    /// recovers before it fails. M122 shows that the heap is intact afterwards (Heap.cpp:386-396)
    /// </remarks>
    [Test]
    public async Task VectorTooLargeIsRefused()
    {
        await G1Helpers.SetGlobalAsync(Session, "labTestVectorCount", "536870912");
        string pid = await Lab.GetControlServerPidAsync();
        int upTimeBefore = (await G1Helpers.QueryFirmwareAsync(Session, "state.upTime")).GetInt32();

        string reply = await G1Helpers.SendOrRecoverAsync(Session, "echo {#vector(global.labTestVectorCount, 0)}", pid);
        await G1Helpers.AssertControlServerGoesOnAsync(Session, pid, "vector(536870912, 0)");
        int upTimeAfter = (await G1Helpers.QueryFirmwareAsync(Session, "state.upTime")).GetInt32();
        if (upTimeAfter < upTimeBefore)
        {
            await Lab.RecoverAsync();
            Assert.Fail($"RRF restarted: state.upTime {upTimeBefore} before and {upTimeAfter} after; reply \"{reply.Trim()}\"");
        }

        string m122 = await SendCodeAsync("M122", 30);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain(G1Helpers.ArrayTooLarge));
            Assert.That(m122, Does.Contain("Heap OK"), "M122 finds the heap damaged");
        });
    }

    /// <summary>
    /// take() and drop() of an object model array inside an array element gave the elements of element 0 and reported an internal error
    /// (RRF #108, fuzz finding E4)
    /// </summary>
    /// <remarks>
    /// move.axes[].drivers reads its parent with context.GetIndex(1) and its length with GetLastIndex() (RRF Move.cpp:101-106). EvaluateTake and
    /// EvaluateDrop did not put the parent index (param >> 8) into their context, so REPORT_INTERNAL_ERROR fired (ObjectModel.cpp:596-616), which
    /// prints "Internal Error in ..." (RepRap.cpp:2360-2363), and the code went on with index 0. Since #108 they add it (ExpressionParser.cpp:1076,
    /// :1141, :1152). The index operator always did (ApplyObjectModelArrayIndex), so it gives the expected elements. The test uses the axis after
    /// axis 0 with the most drivers whose first driver differs from that of axis 0 (on the CHX Z with four drivers, config.g M584 Z0:1:2:3), so each
    /// element shows the bug. move is not an SBC field, so RRF evaluates every echo. Unfixed: take and drop give drivers of axis 0, or an index error
    /// where axis 0 has fewer drivers, and the journal has "Internal Error in"
    /// </remarks>
    [Test]
    public async Task TakeAndDropOfNestedArrayUseTheirParent()
    {
        List<string>[] drivers = Lab.Read(Session, model => model.Move.Axes.Select(axis => axis.Drivers.Select(driver => driver.ToString()).ToList()).ToArray());
        Assume.That(drivers.Length, Is.GreaterThan(1), "The machine has one axis");
        int axis = Enumerable.Range(1, drivers.Length - 1)
            .Where(i => drivers[i].Count > 0 && (drivers[0].Count == 0 || drivers[i][0] != drivers[0][0]))
            .OrderByDescending(i => drivers[i].Count)
            .DefaultIfEmpty(-1)
            .First();
        Assume.That(axis, Is.Positive, "No axis after axis 0 has a first driver other than that of axis 0");
        int count = drivers[axis].Count;
        TestContext.Progress.WriteLine($"Axis {axis} has the drivers {string.Join(", ", drivers[axis])}, axis 0 {string.Join(", ", drivers[0])}");

        string cursor = await Lab.GetJournalCursorAsync();
        List<string> wrong = [];
        for (int k = 0; k < count; k++)
        {
            string index = (await SendCodeAsync($"echo {{move.axes[{axis}].drivers[{k}]}}")).Trim();
            string take = (await SendCodeAsync($"echo {{take(move.axes[{axis}].drivers, {count})[{k}]}}")).Trim();
            string drop = (await SendCodeAsync($"echo {{drop(move.axes[{axis}].drivers, {k})[0]}}")).Trim();
            if (index != drivers[axis][k])
            {
                wrong.Add($"drivers[{k}] = {index}, the model of DSF has {drivers[axis][k]}");
            }
            if (take != drivers[axis][k])
            {
                wrong.Add($"take(drivers, {count})[{k}] = {take}");
            }
            if (drop != drivers[axis][k])
            {
                wrong.Add($"drop(drivers, {k})[0] = {drop}");
            }
        }
        int internalErrors = await G1Helpers.CountJournalLinesAsync(cursor, "Internal Error in");

        Assert.Multiple(() =>
        {
            Assert.That(wrong, Is.Empty, $"elements of move.axes[{axis}].drivers that are not {string.Join(", ", drivers[axis])}");
            Assert.That(internalErrors, Is.Zero, "\"Internal Error in\" lines in the journal of DCS");
        });
    }

    /// <summary>
    /// take() and drop() of a heap string returned wrong text when allocating the result garbage-collected the heap (RRF #106, review finding RV-2)
    /// </summary>
    /// <remarks>
    /// The old code held p.Ptr(), a pointer into the heap, and copied from it after StringHandle had allocated the result. AllocateSpace collects
    /// when no block has room and at least four times the length is waiting to be recycled (RRF Heap.cpp:291-347); the collection moves the strings
    /// down and adjusts only the handles (:73-149). Since #106 the copy reads through the handle after the allocation (ExpressionParser.cpp:1117-1123,
    /// :1195-1201). The macro makes the move likely: per round it sets a 100 character gap, the 200 character operand and three 200 character fillers,
    /// releases the gap and then takes 199 characters from the operand and drops 1, comparing both with the expected text. Local variables keep the
    /// strings out of the object model. The number of take/drop pairs per round follows the free heap that M122 reports (one pair needs about 408
    /// bytes), so that collections happen during the rounds. The test is statistical: a model of AllocateSpace and the collection (not measured on
    /// the board) gave wrong text in at least one round in each of 800 simulated heap layouts, a median of 23 of 30 rounds and only 2 in 5 % of them.
    /// So the unfixed build fails the test in practically every run; with fewer collections than half the rounds the test is inconclusive
    /// </remarks>
    [Test]
    public async Task TakeOfHeapStringSurvivesGarbageCollection()
    {
        Match before = G1Helpers.HeapRegex().Match(await SendCodeAsync("M122", 30));
        if (!before.Success)
        {
            Assert.Ignore("M122 shows no heap line");
        }
        long allocated = long.Parse(before.Groups[1].Value), used = long.Parse(before.Groups[2].Value), recyclable = long.Parse(before.Groups[3].Value);
        int inner = (int)Math.Clamp(Math.Ceiling((allocated - used + recyclable) / 408.0) + 2, 8, 40);
        TestContext.Progress.WriteLine($"Heap before: {before.Value}; {GcRounds} rounds of {inner} take/drop pairs");

        await Lab.UploadAsync(Session, GcMacro, MakeGcMacro(inner));
        string pid = await Lab.GetControlServerPidAsync();
        string reply = await G1Helpers.SendOrRecoverAsync(Session, $"M98 P\"{GcMacro}\"", pid, 900);
        string m122 = await SendCodeAsync("M122", 30);
        Match after = G1Helpers.HeapRegex().Match(m122);

        int rounds = (await QueryAsync("global.labTestGcRounds")).GetInt32(), roundsHit = (await QueryAsync("global.labTestGcRoundsHit")).GetInt32();
        int wrongTake = (await QueryAsync("global.labTestGcWrongTake")).GetInt32(), wrongDrop = (await QueryAsync("global.labTestGcWrongDrop")).GetInt32();
        int collections = after.Success ? int.Parse(after.Groups[4].Value) - int.Parse(before.Groups[4].Value) : -1;
        TestContext.Progress.WriteLine($"Heap after: {after.Value}; {collections} collections, {rounds} rounds, {roundsHit} with wrong text; wrong take {wrongTake}, drop {wrongDrop}");

        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.Contain("Error"), "The macro failed");
            Assert.That(rounds, Is.EqualTo(GcRounds), "Not every round ran");
            Assert.That(wrongTake, Is.Zero, "take() of a heap string returned wrong text");
            Assert.That(wrongDrop, Is.Zero, "drop() of a heap string returned wrong text");
            Assert.That(m122, Does.Contain("Heap OK"), "M122 finds the heap damaged");
        });
        if (collections < GcRounds / 2)
        {
            Assert.Inconclusive($"Only {collections} garbage collections in {GcRounds} rounds, too few to exercise the fix");
        }
    }

    /// <summary>
    /// Macro of <see cref="TakeOfHeapStringSurvivesGarbageCollection"/>. Every line stays below the 255 characters of a keyword argument
    /// (DuetAPI/Commands/Code/Parser.cs:790-792) and every string below RRF's MaxStringExpressionLength of 256 (ExpressionParser.cpp:2554)
    /// </summary>
    private static string MakeGcMacro(int inner)
    {
        const string pattern = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        string operand = string.Concat(Enumerable.Repeat(pattern, 4))[..200], filler = new('f', 200);
        StringBuilder macro = new();
        foreach (string counter in new[] { "labTestGcRounds", "labTestGcRoundsHit", "labTestGcWrongTake", "labTestGcWrongDrop" })
        {
            macro.Append(G1Helpers.Global(counter, "0"));
        }
        macro.Append($"var expTake = \"{operand[..199]}\"\n");
        macro.Append($"var expDrop = \"{operand[1..]}\"\n");
        foreach (string name in new[] { "gap", "op", "f0", "f1", "f2", "t", "d" })
        {
            macro.Append($"var {name} = 0\n");
        }
        macro.Append("var hit = false\n");
        macro.Append($"while iterations < {GcRounds}\n");
        macro.Append($"  set var.gap = \"{new string('g', 100)}\"\n");
        macro.Append($"  set var.op = \"{operand}\"\n");
        macro.Append($"  set var.f0 = \"{filler}\"\n");
        macro.Append($"  set var.f1 = \"{filler}\"\n");
        macro.Append($"  set var.f2 = \"{filler}\"\n");
        macro.Append("  set var.gap = 0\n");
        macro.Append("  set var.hit = false\n");
        macro.Append($"  while iterations < {inner}\n");
        macro.Append("    set var.t = take(var.op, 199)\n");
        macro.Append("    if var.t != var.expTake\n");
        macro.Append("      set global.labTestGcWrongTake = global.labTestGcWrongTake + 1\n");
        macro.Append("      set var.hit = true\n");
        macro.Append("    set var.d = drop(var.op, 1)\n");
        macro.Append("    if var.d != var.expDrop\n");
        macro.Append("      set global.labTestGcWrongDrop = global.labTestGcWrongDrop + 1\n");
        macro.Append("      set var.hit = true\n");
        macro.Append("  if var.hit\n");
        macro.Append("    set global.labTestGcRoundsHit = global.labTestGcRoundsHit + 1\n");
        macro.Append("  set global.labTestGcRounds = global.labTestGcRounds + 1\n");
        return macro.ToString();
    }

    /// <summary>
    /// Printing INT_MIN with %d negated it with signed overflow, undefined behaviour (RRFLibraries #3, binary code fuzz finding B6). Smoke test only:
    /// the unfixed build is not expected to fail it
    /// </summary>
    /// <remarks>
    /// RRF computes the global, because DSF cannot evaluate the global operand. Its JSON goes through catf("%" PRIi32) (RRF ObjectModel.cpp:968-970),
    /// which ends in FormattedPrinter::PrintI (RRFLibraries SafeVsnprintf.cpp); M409 with R1 is answered by RRF (MCodeHandler.cs:846-850), and DSF's own
    /// fetch of the key "global" takes the same path. Since #3 PrintI negates the unsigned copy. No effect of the old code on a board is known: the PR
    /// says it was not seen or checked, and the host build printed the right text at -O1; only UBSan showed the overflow. So this test only shows
    /// that INT_MIN gets through both paths
    /// </remarks>
    [Test]
    public async Task IntMinGoesThroughTheObjectModelJson()
    {
        await G1Helpers.SetGlobalAsync(Session, "labTestOne", "1");
        await G1Helpers.SetGlobalAsync(Session, "labTestIntMin", "-2147483647 - global.labTestOne");

        JsonElement fromFirmware = await G1Helpers.QueryFirmwareAsync(Session, "global.labTestIntMin");
        bool inModel = await Lab.TryWaitForModelAsync(Session, model => model.Global.TryGetValue("labTestIntMin", out JsonElement? value) &&
                                                                    value?.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out int i) && i == int.MinValue,
                                                      TimeSpan.FromSeconds(30));
        Assert.Multiple(() =>
        {
            Assert.That(fromFirmware.ValueKind == JsonValueKind.Number && fromFirmware.TryGetInt32(out int i) ? i : (int?)null, Is.EqualTo(int.MinValue),
                $"M409 R1 gave {fromFirmware}");
            Assert.That(inModel, Is.True, "The model of DSF does not show -2147483648");
        });
    }
}
