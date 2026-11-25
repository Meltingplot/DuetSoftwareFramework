using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Fixes in the firmware of the expansion and tool boards (Duet3Expansion, CoreN2G) and in how RRF handles the restart of such a board
/// </summary>
/// <remarks>
/// The lab printer has a TOOL1LC (CAN address 20) and an SZP (60), both SAMC21, and four EXP1HCL (50 to 53), SAME5x (chx350-config config.g:24-28 waits for
/// the seven boards). Every test reads boards[].firmwareVersion and is ignored, naming the first fork release of the fix, when a board runs a build without it
/// (<see cref="G7Helpers.WhyNotFixed"/>). D3E #30, D3E #32 and RRF #112 are in no release yet (D3E 3.7.0+1.mp.6, RRF 3.7.0+1.mp.12 will be the first):
/// for a test build LAB_ASSUME_FIXES names them, e.g. LAB_ASSUME_FIXES="RRF#112 D3E#30 D3E#32"
/// </remarks>
[TestFixture]
public class ExpansionBoards : LabTest
{
    /// <summary>
    /// Boards whose M915 handler has drivers to check: HAS_SMART_DRIVERS and HAS_STALL_DETECT 1 (Duet3Expansion TOOL1LC.h:25-26, EXP1HCL.h:24-25). Other
    /// boards answer "stall detection not supported by this board" (CommandProcessor.cpp:477-479)
    /// </summary>
    private static readonly string[] StallDetectBoards = ["TOOL1LC", "EXP1HCL"];

    /// <summary>
    /// Boards built for the SAMC21: BOOTLOADER_NAME "SAMC21" in Duet3Expansion src/Config/*.h
    /// </summary>
    private static readonly string[] Samc21Boards = ["TOOL1LC", "SZP", "EXP1XD", "ATECM", "ATEIO"];

    /// <summary>
    /// Size of the SAMC21 system stack in words, SystemStackSize in Duet3Expansion Tasks.cpp:77
    /// </summary>
    private const int SystemStackWords = 200;

    /// <summary>
    /// Folder of the files of M956 F (RRF Accelerometers.cpp:592-598)
    /// </summary>
    private const string AccelerometerDirectory = "0:/sys/accelerometer/";

    private const string RestartFile = "lab-test-board-restart.csv";
    private const string FreezeFile = "lab-test-szp-freeze.csv";

    /// <summary>
    /// Last line RRF writes when the board restarts during a collection (Accelerometers.cpp:815)
    /// </summary>
    private const string RestartLine = "Board restarted before the collection was complete";

    /// <summary>
    /// Longest time without a new state.upTime that counts as no stall. RRF counts the up time once a second and DSF polls the live values every 100 ms
    /// (Settings.cs:283), so a gap of a little over 1 s is normal; the stall of RRF #112 C1 lasts at least one SbcMaxRequestTime of 3 s (SbcMessageFormats.h:42)
    /// </summary>
    private const double MaxUpTimeGapSeconds = 2.5;

    /// <summary>
    /// Reset data slots of an expansion board (NonVolatileMemory.h:80), and the only one whose stack dump reaches the last flash page
    /// </summary>
    private const int ResetDataSlots = 3, LastPageSlot = 2;

    /// <summary>
    /// Words of the stack dump of slot 2 in the last 64-byte page of the 512-byte NVM page: the slots follow a 56-byte header (NonVolatileMemory.h:94-98),
    /// 152 bytes each, and the stack starts at byte 44 of a slot (SoftwareReset.h:68-84), so slot 2's stack starts at byte 404 and word 11 at byte 448
    /// </summary>
    private const int FirstWordInLastPage = 11;

    /// <summary>
    /// Pattern the board fills its free RAM with, so also unused stack words (Duet3Expansion Tasks.cpp:87, :320-326)
    /// </summary>
    private const uint MemPattern = 0xA5A5A5A5;

    /// <summary>
    /// Number of M956 runs of <see cref="SzpCoilKeepsUpdatingDuringAccelerometerRuns"/>, 700 unless LAB_SZP_RUNS sets another one
    /// </summary>
    private static readonly int SzpRuns = int.TryParse(Environment.GetEnvironmentVariable("LAB_SZP_RUNS"), out int runs) && runs > 0 ? runs : 700;

    /// <summary>
    /// Samples per M956 run, as in the investigation of the freeze (szp-freeze-evidence-20261004/40-last-accel-run.csv: 1000 samples at 803 Hz)
    /// </summary>
    private const int SamplesPerRun = 1000;

    /// <summary>
    /// Time without a change of the coil reading that counts as a freeze, FREEZE_S of szp-freeze-test.py:27
    /// </summary>
    private static readonly TimeSpan FreezeTime = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Poll interval and the longest wait for the end of a run, POLL_S and RUN_TIMEOUT_S of szp-freeze-test.py:26-28
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250), RunTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The SZP of the machine: CAN address, firmware, number of its accelerometer and index of its coil thermistor
    /// </summary>
    private sealed record Szp(int Address, string? FirmwareVersion, int Accelerometer, int SamplingRate, int CoilSensor);

    private static Szp? FindSzp(ObjectModel model)
    {
        var boards = G7Helpers.FindBoards(model, "SZP");
        if (boards.Length == 0)
        {
            return null;
        }
        int accelerometer = G7Helpers.FindAccelerometer(model, boards[0].Address);
        return new(boards[0].Address, boards[0].FirmwareVersion, accelerometer,
                   (accelerometer >= 0) ? model.Sensors.Accelerometers[accelerometer]!.SamplingRate : 0, G7Helpers.FindAnalogSensor(model, G7Helpers.SzpCoilSensor));
    }

    /// <summary>
    /// Runs and points of the last run of an accelerometer, both set when a run ends (RRF Accelerometers.cpp:129-138)
    /// </summary>
    private static (int Runs, int Points) GetRuns(ObjectModel model, int accelerometer) =>
        (accelerometer < model.Sensors.Accelerometers.Count && model.Sensors.Accelerometers[accelerometer] is Accelerometer item) ? (item.Runs, item.Points) : (-1, -1);

    /// <summary>
    /// An M915 report of a driver that the board does not have is refused with "No such driver &lt;address&gt;.&lt;driver&gt;"
    /// </summary>
    /// <remarks>
    /// Symptom: the main board passes remote driver numbers on without checking them (RRF Move.cpp:3622-3646, CanInterface.cpp:1317-1347), and the board
    /// reported a driver it does not have as if it existed. After M915 … R1 for such a driver, the report of a closed-loop board (EXP1HCL) read beyond
    /// dms[] (Duet3Expansion Move.cpp:1664-1668). Fix: Duet3Expansion PR #32, ProcessM915 refuses the request as a whole (CommandProcessor.cpp:403-417).
    ///
    /// Unfixed, the request takes the report branch (CommandProcessor.cpp:465-474): "Driver 50.1: , event on stall: no" on an EXP1HCL, whose
    /// AppendStallConfig skips the missing driver (TMC51xx.cpp:2384-2390; PR #32 shows these replies from the lab printer), and "Driver 20.1: no such
    /// driver, event on stall: no" on the TOOL1LC (TMC22xx.cpp:2594-2606). Both contain "Driver &lt;a&gt;.&lt;n&gt;:" and not "No such driver &lt;a&gt;.&lt;n&gt;",
    /// so the test fails on every board without the fix, every time.
    ///
    /// Read-only: without S, F, H, T or R, M915 only reports (Move.cpp:3762-3830). The remote reply comes with an output buffer, so RRF sends it without
    /// "Error:" (CanInterface.cpp:1336-1344, GCodes2.cpp:5104-5110). DSF converts M915 P to driver ids (DuetAPI Parser.cs:799-800, :976-987). The missing
    /// driver is boards[].maxMotors, the number of drivers the board announced (RRF ExpansionManager.cpp:64), so an off-by-one in the check fails too.
    /// Driver 0 is queried as well, to show that the board reports at all
    /// </remarks>
    [Test]
    public async Task M915RefusesDriverTheBoardDoesNotHave()
    {
        var boards = Lab.Read(Session, model => G7Helpers.FindBoards(model, StallDetectBoards));
        if (boards.Length == 0)
        {
            Assert.Ignore($"No {string.Join(" or ", StallDetectBoards)} is connected");
        }

        List<string> notChecked = [];
        List<(int Address, string ShortName, int MaxMotors, string? FirmwareVersion)> toCheck = [];
        foreach (var board in boards)
        {
            string? whyNot = G7Helpers.WhyNotFixed(G7Helpers.D3E32, $"Board {board.Address} ({board.ShortName})", board.FirmwareVersion);
            if (whyNot is not null)
            {
                notChecked.Add(whyNot);
            }
            else if (board.MaxMotors <= 0)
            {
                notChecked.Add($"Board {board.Address} ({board.ShortName}) announced no drivers");
            }
            else
            {
                toCheck.Add(board);
            }
        }
        if (toCheck.Count == 0)
        {
            Assert.Ignore(string.Join(". ", notChecked));
        }
        foreach (string reason in notChecked)
        {
            TestContext.Progress.WriteLine($"Not checked: {reason}");
        }

        List<(int Address, int Driver, string Control, string Missing)> results = [];
        foreach (var board in toCheck)
        {
            string control = (await SendCodeAsync($"M915 P{board.Address}.0", 30)).Trim();
            string missing = (await SendCodeAsync($"M915 P{board.Address}.{board.MaxMotors}", 30)).Trim();
            TestContext.Progress.WriteLine($"Board {board.Address} ({board.ShortName} {board.FirmwareVersion}): M915 P{board.Address}.0 -> \"{control}\", " +
                                           $"M915 P{board.Address}.{board.MaxMotors} -> \"{missing}\"");
            results.Add((board.Address, board.MaxMotors, control, missing));
        }

        Assert.Multiple(() =>
        {
            foreach ((int address, int driver, string control, string missing) in results)
            {
                Assert.That(control, Does.Contain($"Driver {address}.0: "), $"M915 P{address}.0 did not report driver {address}.0");
                Assert.That(missing, Does.Contain($"No such driver {address}.{driver}"), $"M915 P{address}.{driver} was not refused");
                Assert.That(missing, Does.Not.Contain($"Driver {address}.{driver}:"), $"M915 P{address}.{driver} reported a driver the board does not have");
            }
        });
    }

    /// <summary>
    /// The system stack of a SAMC21 board is 800 bytes: M122 B&lt;n&gt; reports more than 0 and at most 200 free words
    /// </summary>
    /// <remarks>
    /// Symptom: upstream 2d08ff76 made SystemStackSize a number of dwords, but the SAMC21 value stayed 800, so the TOOL1LC and the SZP got a system stack
    /// of 3200 bytes and 2400 bytes less heap (D3E PR #11, its table of _sstack). Fix: Duet3Expansion PR #11, SystemStackSize (200) dwords (Tasks.cpp:75-83).
    ///
    /// M122 B&lt;n&gt; prints the never-used part of the system stack in words (Tasks.cpp:936-945, :980; CommandProcessor.cpp:573-576). GetHandlerFreeStack
    /// stops at sysStackTop, so it cannot exceed the stack, and the stack is filled with a pattern at start-up (Tasks.cpp:320-326), so a stack that was used up
    /// shows 0. With 800 bytes the TOOL1LC and the SZP showed 104 free words (szp-freeze-evidence-20261004/20-m122-b60-a.txt:5, 22-m122-b20-healthy-samc21.txt:5,
    /// 3.7.0-rc.2+1-mp.1), so the handlers used 96 words. Unfixed, about (3200 - 384) / 4 = 704 words are free, so the test fails on every SAMC21 board with
    /// the old value.
    /// Upstream 3.7.0 has the 800 dwords but reports no "mp." version, so the test ignores it; it guards fork builds against an upstream sync that brings the
    /// old value back. 3.7.0-mp.1 printed the free bytes / 16 (corrected by 3d9ca2de in 3.7.0-mp.2), which is at most 50 and passes as well
    /// </remarks>
    [Test]
    public async Task Samc21SystemStackIs800Bytes()
    {
        var boards = Lab.Read(Session, model => G7Helpers.FindBoards(model, Samc21Boards));
        if (boards.Length == 0)
        {
            Assert.Ignore("No SAMC21 board is connected");
        }

        List<string> notChecked = [];
        var toCheck = boards.Where(board =>
        {
            string? whyNot = G7Helpers.WhyNotFixed(G7Helpers.D3E11, $"Board {board.Address} ({board.ShortName})", board.FirmwareVersion);
            if (whyNot is not null)
            {
                notChecked.Add(whyNot);
            }
            return whyNot is null;
        }).ToArray();
        if (toCheck.Length == 0)
        {
            Assert.Ignore(string.Join(". ", notChecked));
        }
        foreach (string reason in notChecked)
        {
            TestContext.Progress.WriteLine($"Not checked: {reason}");
        }

        List<(int Address, Match Stack, string Reply)> results = [];
        foreach (var board in toCheck)
        {
            string reply = await SendCodeAsync($"M122 B{board.Address}", 30);
            Match stack = G7Helpers.FreeSystemStackRegex().Match(reply);
            TestContext.Progress.WriteLine($"Board {board.Address} ({board.ShortName} {board.FirmwareVersion}): {(stack.Success ? stack.Value : "no line about the system stack")}");
            results.Add((board.Address, stack, reply));
        }

        Assert.Multiple(() =>
        {
            foreach ((int address, Match stack, string reply) in results)
            {
                Assert.That(stack.Success, Is.True, $"M122 B{address} shows no free system stack: {reply}");
                if (stack.Success)
                {
                    Assert.That(int.Parse(stack.Groups[2].Value, CultureInfo.InvariantCulture), Is.InRange(1, SystemStackWords),
                                $"free system stack of board {address} in words, the stack has {SystemStackWords}");
                }
            }
        });
    }

    /// <summary>
    /// The SZP restarts during an accelerometer run: RRF finishes the file without stalling the link, and the SZP keeps the reset data of the restart
    /// </summary>
    /// <remarks>
    /// <para>
    /// RRF #112 C1. Symptom: when an expansion board announced itself after a restart during a remote collection, the CAN receiver finished the file
    /// (Accelerometers::RemoteBoardRestarted: write, truncate, close, Accelerometers.cpp:809-821) while it held boardsLock. In SBC mode each of these
    /// waits for the SBC task (SbcInterface.cpp:2734-2749, at most SbcMaxRequestTime 3 s), which takes boardsLock to read board data for DSF, so the CAN
    /// input and the SBC transfers stalled for up to 2 × 3 s. Fix: RRF PR #112, the call after the lock (ExpansionManager.cpp:252-258); #112 also makes every
    /// wait for the SBC task under a write lock report an internal error (SbcInterface.cpp:2697-2704, text RepRap.cpp:2362).
    /// </para>
    /// <para>
    /// Unfixed, the stall shows as a gap of 3 s or more in state.upTime, as "Timeout while trying to truncate file" or "… close file" (SbcInterface.cpp:2669,
    /// :2691), and, with the DSF connection timeout of 4 s (Settings.cs:183), possibly as a lost link. It needs the SBC task in a read of the board data at
    /// that moment, which the live poll of DSF makes likely but not certain: the host test of PR #112 (Tools/sbc-wait-lock-test) fails in 16 of 88
    /// interleavings, the rate on the printer is not known. A build that has the check of #112 but calls RemoteBoardRestarted under the lock again logs
    /// "Internal Error in" on every such restart.
    /// </para>
    /// <para>
    /// D3E #30. Symptom: SoftwareReset saves the reset data and resets the processor (ExceptionHandlers.cpp:15-37). On the SAMC21, EnsureWritten returned
    /// once the last of the 8 page writes had started, so the reset could cut it short. Fix: Duet3Expansion PR #30, EnsureWritten waits for NVMCTRL READY
    /// (NonVolatileMemory.cpp:92-99). The last page holds only words 11 to 26 of the stack dump of slot 2 (<see cref="FirstWordInLastPage"/>). The test
    /// checks that M122 B60 shows the new record, "reason: deliberate WatchdogTimeout" (SoftwareReset.cpp:132-137, SoftwareReset.h:23 and :43) in the next
    /// slot (NonVolatileMemory.cpp:152-186: the highest valid slot is reported, a new record takes the first vacant one, all three are cleared when full),
    /// and that three reads of it give the same stack dump. That last check is the one that can fail without the fix, and only for a record in slot 2:
    /// the SZP record of 2026-10-04 in slot 2 (szp-freeze-evidence-20261004/20-m122-b60-a.txt:11 and 21-m122-b60-b.txt:11, SZP 3.7.0-rc.2+1-mp.1) gave
    /// different stack words 12, 15, 16, 20 and 25 in two reads, words 0 to 10 stayed the same, and words 13-19 and 25-26 read as the fill pattern a5a5a5a5
    /// with extra bits set. The slot moves on by one per record, so one run of the test in three writes slot 2; the test prints which slot it got.
    /// How often an unfixed build cuts the write of slot 2 is not known (one of the two slot-2 records in that evidence, the TOOL1LC's was clean).
    /// </para>
    /// <para>
    /// Cost: one watchdog reset of the SZP (M122 B60 P1001: RRF CanInterface.cpp:1443-1464, Duet3Expansion Platform.cpp:1685-1687 defers it and :1012-1015
    /// stops SysTick), then one DCS restart with M999 (<see cref="Lab.RecoverAsync"/>), an E-stop with ATX off. The M999 is needed: the wiki says a
    /// restarted board has lost its configuration (CAN_limitations.md:21, for the restart after a firmware update), and RRF sends none on the announcement
    /// (ExpansionManager.cpp:166-264). Without it the coil sensor (config.g:172) times out after 2 s with 2000 (TemperatureSensor.cpp:53-63,
    /// TemperatureSensor.h:140), and the Z probe and accelerometer 1 (config.g:168, :181) stay unconfigured on the board. In the freeze investigation the coil
    /// read again after M999 B60 only once M308 had been sent again (HANDOVER-szp-coil-temp-frozen.md:21-23, outside this repository). The SZP restart
    /// raises no E-stop: the E-stop and door inputs are on the main board (sys/meltingplot/ce-declaration/e-stop.g:24-26, ce-declaration.g:5-13), and
    /// sys/expansion-reconnect.g echoes an error and pauses only a job. M956 writes 0:/sys/accelerometer/lab-test-board-restart.csv, which the test deletes
    /// </para>
    /// </remarks>
    [Test, Category("Approval"), Category("Restart"),
     Explicit("Resets the SZP during an accelerometer run, then restarts DCS and resets the firmware: one E-stop with ATX off. Needs Tim's approval for each run")]
    public async Task RestartDuringAccelerometerRunKeepsLinkAndResetData()
    {
        Szp? szp = Lab.Read(Session, FindSzp);
        if (szp is null || szp.Accelerometer < 0)
        {
            Assert.Ignore("No SZP with an accelerometer is configured (chx350-config config.g:181)");
        }
        string? whyNotC1 = G7Helpers.WhyNotFixed(G7Helpers.Rrf112, "The main board", Lab.Read(Session, model => model.Boards[0].FirmwareVersion));
        string? whyNot30 = G7Helpers.WhyNotFixed(G7Helpers.D3E30, $"Board {szp.Address} (SZP)", szp.FirmwareVersion);
        if (whyNotC1 is not null && whyNot30 is not null)
        {
            Assert.Ignore($"{whyNotC1}. {whyNot30}");
        }
        foreach (string? reason in new[] { whyNotC1, whyNot30 }.Where(reason => reason is not null))
        {
            TestContext.Progress.WriteLine($"Part not checked: {reason}");
        }

        // The record the next one follows
        string resetBefore = G7Helpers.SoftwareResetLines(await SendCodeAsync($"M122 B{szp.Address}", 30));
        Match before = G7Helpers.SoftwareResetRegex().Match(resetBefore);
        Assert.That(before.Success || resetBefore.Contains(G7Helpers.NoResetData), Is.True, $"M122 B{szp.Address} shows no line about the last software reset");
        int expectedSlot = before.Success ? (int.Parse(before.Groups["slot"].Value, CultureInfo.InvariantCulture) + 1) % ResetDataSlots : 0;
        TestContext.Progress.WriteLine($"Before: {resetBefore.Split('\n')[0]}; the new record goes to slot {expectedSlot}");

        // Start a collection long enough to be running at the reset, 30 s at the rate the model reports or at 1000 samples a second
        string file = AccelerometerDirectory + RestartFile;
        await Lab.DeleteAsync(Session, file);
        int samples = Math.Max(szp.SamplingRate, 1000) * 30;
        string cursor = await Lab.GetJournalCursorAsync();
        int runsBefore = Lab.Read(Session, model => GetRuns(model, szp.Accelerometer).Runs);
        string started = await SendCodeAsync($"M956 P{szp.Accelerometer} S{samples} A0 F\"{RestartFile}\"", 30);
        Assert.That(started, Does.Not.Contain("Error"), $"M956 P{szp.Accelerometer} did not start the collection");

        // The model must keep up during the collection alone, or a gap after the reset says nothing
        double baselineGap;
        using (CancellationTokenSource baseline = new(TimeSpan.FromSeconds(5)))
        {
            baselineGap = await G7Helpers.SampleUpTimeGapAsync(Session, baseline.Token);
        }
        if (Lab.Read(Session, model => GetRuns(model, szp.Accelerometer).Runs) != runsBefore)
        {
            await Lab.DeleteAsync(Session, file);
            Assert.Inconclusive($"The collection of {samples} samples ended within 5 s, before the SZP was reset");
        }

        string? error = null, resetAfter = null, csv = null;
        string[] stackReads = [];
        (int Runs, int Points) after = (-1, -1);
        bool boardBack = false, coilOk = false;
        double gap = double.NaN;
        string journal = string.Empty;
        using CancellationTokenSource stopSampling = new();
        Task<double> gapTask = G7Helpers.SampleUpTimeGapAsync(Session, stopSampling.Token);
        try
        {
            try
            {
                try
                {
                    // Watchdog reset of the board, see M122 in the G-code dictionary
                    await SendCodeAsync($"M122 B{szp.Address} P1001");
                }
                catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
                {
                    // The board may restart before it replies
                }

                // RRF counts the lost collection as a run with 0 points once the board has announced itself again
                await Lab.TryWaitForModelAsync(Session, model => GetRuns(model, szp.Accelerometer).Runs != runsBefore, TimeSpan.FromSeconds(60));
                after = Lab.Read(Session, model => GetRuns(model, szp.Accelerometer));
                boardBack = await Lab.TryWaitForModelAsync(Session, Lab.IsIdle, TimeSpan.FromSeconds(60));
                await Task.Delay(TimeSpan.FromSeconds(3));
                stopSampling.Cancel();
                gap = await gapTask;

                if (boardBack)
                {
                    // Each M122 B<n> reads the record from the flash again (CommandProcessor.cpp:582-584, :599-601, NonVolatileMemory.cpp:27-38)
                    List<string> reads = [];
                    for (int i = 0; i < 3; i++)
                    {
                        reads.Add(G7Helpers.SoftwareResetLines(await SendCodeAsync($"M122 B{szp.Address}", 30)));
                    }
                    resetAfter = reads[0];
                    stackReads = [.. reads.Select(read => read.Split('\n').FirstOrDefault(line => line.StartsWith("Stack:")) ?? string.Empty)];
                }
                csv = await G7Helpers.ReadFileAsync(Session, file);
            }
            catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
            {
                error = $"{e.GetType().Name}: {e.Message}";
            }
            stopSampling.Cancel();
            if (double.IsNaN(gap))
            {
                gap = await gapTask;
            }

            // Before the recovery, whose restarts log link errors of their own
            journal = await Lab.ReadJournalAsync(cursor);
        }
        finally
        {
            stopSampling.Cancel();

            // The restarted SZP has lost its configuration, the reset of the main board runs config.g again
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
            await Lab.DeleteAsync(Session, file);
        }
        if (szp.CoilSensor >= 0)
        {
            coilOk = await Lab.TryWaitForModelAsync(Session, model => szp.CoilSensor < model.Sensors.Analog.Count && model.Sensors.Analog[szp.CoilSensor]?.State == TemperatureError.Ok,
                                                    TimeSpan.FromSeconds(60));
        }

        string[] journalLines = journal.Split('\n');
        string[] internalErrors = [.. journalLines.Where(line => line.Contains(G7Helpers.InternalError))];
        string[] fileTimeouts = [.. journalLines.Where(line => line.Contains(G7Helpers.FileTimeout))];
        string[] linkErrors = [.. journalLines.Where(line => G7Helpers.LinkErrors.Any(line.Contains))];
        string? lastLine = csv?.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0);
        Match afterMatch = G7Helpers.SoftwareResetRegex().Match(resetAfter ?? string.Empty);
        int afterSlot = afterMatch.Success ? int.Parse(afterMatch.Groups["slot"].Value, CultureInfo.InvariantCulture) : -1;

        TestContext.Progress.WriteLine($"Collection: runs {runsBefore} -> {after.Runs}, points {after.Points}, last line of the file: {lastLine ?? "(no file)"}");
        TestContext.Progress.WriteLine($"Longest time without a new state.upTime: {baselineGap:0.0} s before the reset, {gap:0.0} s after it");
        TestContext.Progress.WriteLine($"Journal: {internalErrors.Length} internal errors, {fileTimeouts.Length} file timeouts, {linkErrors.Length} link errors");
        foreach (string line in internalErrors.Concat(fileTimeouts).Concat(linkErrors).Take(10))
        {
            TestContext.Progress.WriteLine($"  {line}");
        }
        TestContext.Progress.WriteLine($"After: {resetAfter ?? "(not read)"}");
        if (afterSlot == LastPageSlot && stackReads.Length > 0)
        {
            // Information only: a word of the last page with all bits of the fill pattern and more, as in the record of 2026-10-04
            uint[] words = [.. stackReads[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(word => uint.Parse(word, NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
            int[] patternPlus = [.. Enumerable.Range(FirstWordInLastPage, Math.Max(0, words.Length - FirstWordInLastPage))
                                             .Where(i => (words[i] & MemPattern) == MemPattern && words[i] != MemPattern && words[i] != uint.MaxValue)];
            TestContext.Progress.WriteLine($"The record is in slot {LastPageSlot}, the one D3E #30 protects; stack words of the last page that read as the fill pattern " +
                                           $"with extra bits: {(patternPlus.Length == 0 ? "none" : string.Join(", ", patternPlus))}");
        }
        else
        {
            TestContext.Progress.WriteLine($"The record is in slot {afterSlot}, so it does not reach the last page that D3E #30 protects (slot {LastPageSlot} does)");
        }

        Assert.Multiple(() =>
        {
            Assert.That(error, Is.Null, "A code or the download of the file failed after the reset");
            Assert.That(boardBack, Is.True, "The SZP was not running again within 60 s");
            if (szp.CoilSensor >= 0)
            {
                Assert.That(coilOk, Is.True, "The SZP coil sensor did not read ok again after the DCS restart and M999");
            }
            if (whyNotC1 is null)
            {
                Assert.That(after.Runs, Is.EqualTo(runsBefore + 1), "RRF did not count the collection as ended after the restart (Accelerometers.cpp:129-138)");
                Assert.That(after.Points, Is.Zero, "points of the run that the restart ended");
                Assert.That(lastLine, Is.EqualTo(RestartLine), "last line of the collection file");
                if (baselineGap <= MaxUpTimeGapSeconds)
                {
                    Assert.That(gap, Is.LessThanOrEqualTo(MaxUpTimeGapSeconds), "longest time without a new state.upTime in the model after the reset, in s");
                }
                else
                {
                    TestContext.Progress.WriteLine($"Gap not checked: the model already lagged {baselineGap:0.0} s before the reset");
                }
                Assert.That(internalErrors, Is.Empty, "RRF waited for the SBC task under a write lock");
                Assert.That(fileTimeouts, Is.Empty, "file operations of RRF timed out");
                Assert.That(linkErrors, Is.Empty, "the link reported errors");
            }
            if (whyNot30 is null)
            {
                Assert.That(afterMatch.Success, Is.True, $"M122 B{szp.Address} shows no reset data after the watchdog reset");
                Assert.That(afterMatch.Groups["reason"].Value, Is.EqualTo("deliberate WatchdogTimeout"), "reason of the last reset");
                Assert.That(afterSlot, Is.EqualTo(expectedSlot), "slot of the new record");
                if (stackReads.Length > 0)
                {
                    Assert.That(stackReads.Distinct().Count(), Is.EqualTo(1), $"the stack dump differs between three reads:\n{string.Join('\n', stackReads)}");
                }
            }
        });
    }

    /// <summary>
    /// The SZP coil thermistor keeps updating while the accelerometer of the SZP runs M956 back to back
    /// </summary>
    /// <remarks>
    /// <para>
    /// Symptom: during M956 runs the coil reading of the SZP froze at its last value with state ok until M999 B60, in about 1.4 % of the runs (3 in about
    /// 214, CoreN2G PR #2). On the SAMC21 the I2C interrupt, which arms the DMA of the LIS FIFO reads, could preempt DMAC_Handler between the selection of a
    /// channel with CHID and the clearing of its flags; the ADC channel lost its completion, and AnalogIn never recovered, so no reading of ADC0 reached
    /// a filter again. Fix: CoreN2G PR #2 (DMAC_Handler selects, reads and clears under a critical section; StartConversion clears a stale flag), in
    /// Duet3Expansion since 3.7.0-rc.2+1-mp.2.
    /// </para>
    /// <para>
    /// The test runs M956 P&lt;SZP accelerometer&gt; S1000 A0 F"lab-test-szp-freeze.csv" <see cref="SzpRuns"/> times, each as soon as runs has advanced,
    /// and reads the coil from RRF with M409 R1 every 0.25 s (DSF forwards M409 R1, MCodeHandler.cs:846-850), as szp-freeze-test.py of the investigation
    /// did (/mnt/c/Eclipse/Firmware/szp-freeze-test.py, outside this repository). lastReading has two decimals (RRF TemperatureSensor.cpp:24). The freeze
    /// criterion is that script's: the reading unchanged for 20 s with state ok (FREEZE_S, szp-freeze-test.py:27). Before the freezes the reading changed
    /// every 0.25 to 0.5 s (HANDOVER-szp-coil-temp-frozen.md:108); in the logs of 2026-10-04 the longest unchanged time before a freeze was 1.6 s, and a
    /// freeze never thawed (HANDOVER:116, 163 runs while frozen). If the reading does not change twice in 20 s before the first run, the test is inconclusive.
    /// </para>
    /// <para>
    /// Unfixed: a freeze in about 1.4 % of the runs, so 700 runs miss it with a probability of 0.986^700 ≈ 5e-5 (the plan of PR #2); the freezes of the
    /// investigation came in runs 88 and 125 (HANDOVER:116, :120). A freeze lasts until the board is reset, so the test then restarts DCS and resets the
    /// firmware (one E-stop with ATX off) before it fails, as it does when the coil leaves state ok. A fixed build costs no restart. A run takes about 1.3 s
    /// (HANDOVER:106), so 700 runs take about 15 to 17 minutes. Also checked: every run ends with 1000 points within 30 s (the investigation had no short run)
    /// </para>
    /// </remarks>
    [Test, Category("Approval"), Explicit("Runs M956 on the SZP 700 times, about 17 minutes, into a file in 0:/sys/accelerometer. Needs Tim's approval for each run")]
    public async Task SzpCoilKeepsUpdatingDuringAccelerometerRuns()
    {
        Szp? szp = Lab.Read(Session, FindSzp);
        if (szp is null || szp.Accelerometer < 0)
        {
            Assert.Ignore("No SZP with an accelerometer is configured (chx350-config config.g:181)");
        }
        if (szp.CoilSensor < 0)
        {
            Assert.Ignore($"No sensor is named \"{G7Helpers.SzpCoilSensor}\" (chx350-config config.g:172)");
        }
        string? whyNot = G7Helpers.WhyNotFixed(G7Helpers.CoreN2G2, $"Board {szp.Address} (SZP)", szp.FirmwareVersion);
        if (whyNot is not null)
        {
            Assert.Ignore(whyNot);
        }

        string? lastReading = null, badState = null;
        int changes = 0, issued = 0, pollErrors = 0, failedPollsInARow = 0;
        double longestUnchanged = 0;
        bool frozen = false;
        Stopwatch sinceChange = Stopwatch.StartNew();

        async Task SampleAsync()
        {
            JsonElement result;
            try
            {
                using JsonDocument reply = JsonDocument.Parse(await SendCodeAsync($"M409 K\"sensors.analog[{szp.CoilSensor}]\" R1", 30));
                result = reply.RootElement.GetProperty("result").Clone();
                failedPollsInARow = 0;
            }
            catch (Exception e) when (e is OperationCanceledException or HttpRequestException or JsonException or KeyNotFoundException)
            {
                pollErrors++;
                failedPollsInARow++;
                TestContext.Progress.WriteLine($"Poll failed after {issued} runs: {e.Message}");
                return;
            }

            string reading = (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("lastReading", out JsonElement value)) ? value.GetRawText() : "(none)";
            string state = (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("state", out JsonElement text)) ? text.GetString() ?? "(none)" : "(none)";
            if (state != "ok")
            {
                badState ??= $"state {state}, reading {reading}, after {issued} runs";
            }
            else if (reading != lastReading)
            {
                if (lastReading is not null)
                {
                    changes++;
                    longestUnchanged = Math.Max(longestUnchanged, sinceChange.Elapsed.TotalSeconds);
                }
                lastReading = reading;
                sinceChange.Restart();
            }
            else if (sinceChange.Elapsed >= FreezeTime)
            {
                frozen = true;
            }
        }
        bool Stop() => frozen || badState is not null || failedPollsInARow >= 20;

        // The reading must change by itself, or a freeze cannot be told from a steady reading
        Stopwatch baseline = Stopwatch.StartNew();
        while (baseline.Elapsed < FreezeTime && badState is null)
        {
            await SampleAsync();
            await Task.Delay(PollInterval);
        }
        if (badState is not null)
        {
            Assert.Inconclusive($"The coil sensor was not ok before the first run: {badState}");
        }
        if (changes < 2)
        {
            Assert.Inconclusive($"The coil reading changed {changes} times in {FreezeTime.TotalSeconds:0} s before the first run (reading {lastReading})");
        }
        int baselineChanges = changes;

        string file = AccelerometerDirectory + FreezeFile;
        string code = $"M956 P{szp.Accelerometer} S{SamplesPerRun} A0 F\"{FreezeFile}\"";
        List<string> m956Errors = [], shortRuns = [], timeouts = [];
        int runsBefore = 0;
        Stopwatch? pending = null, total = Stopwatch.StartNew();
        while (!Stop() && m956Errors.Count + timeouts.Count < 5)
        {
            await SampleAsync();
            (int runs, int points) = Lab.Read(Session, model => GetRuns(model, szp.Accelerometer));
            if (pending is not null)
            {
                if (runs > runsBefore)
                {
                    pending = null;
                    if (points != SamplesPerRun)
                    {
                        shortRuns.Add($"run {issued}: {points} points");
                    }
                }
                else if (pending.Elapsed > RunTimeout)
                {
                    pending = null;
                    timeouts.Add($"run {issued}");
                }
            }
            if (pending is null)
            {
                if (issued == SzpRuns)
                {
                    break;
                }
                runsBefore = runs;
                string reply = await SendCodeAsync(code, 30);
                issued++;
                if (reply.Contains("Error"))
                {
                    m956Errors.Add($"run {issued}: {reply.Trim()}");
                }
                pending = Stopwatch.StartNew();
            }
            await Task.Delay(PollInterval);
        }

        // A freeze in the last runs shows only after the criterion
        Stopwatch tail = Stopwatch.StartNew();
        while (!Stop() && tail.Elapsed < FreezeTime + TimeSpan.FromSeconds(2))
        {
            await SampleAsync();
            await Task.Delay(PollInterval);
        }
        longestUnchanged = Math.Max(longestUnchanged, frozen ? 0 : sinceChange.Elapsed.TotalSeconds);

        TestContext.Progress.WriteLine($"{issued} runs in {total.Elapsed.TotalMinutes:0.0} min; coil reading {lastReading}, {changes - baselineChanges} changes during the runs, " +
                                       $"longest unchanged {longestUnchanged:0.0} s; {shortRuns.Count} short runs, {timeouts.Count} runs without an end, {m956Errors.Count} M956 errors, {pollErrors} failed polls");
        foreach (string line in m956Errors.Concat(shortRuns).Concat(timeouts).Take(10))
        {
            TestContext.Progress.WriteLine($"  {line}");
        }

        if (frozen || badState is not null || failedPollsInARow >= 20)
        {
            string problem = frozen ? $"The coil reading froze at {lastReading} with state ok after {issued} runs, unchanged for {FreezeTime.TotalSeconds:0} s"
                           : (badState is not null) ? $"The coil sensor left state ok: {badState}"
                           : $"The coil could not be read {failedPollsInARow} times in a row after {issued} runs";
            try
            {
                TestContext.Progress.WriteLine(await SendCodeAsync($"M122 B{szp.Address}", 30));
            }
            catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
            {
                TestContext.Progress.WriteLine($"M122 B{szp.Address} failed: {e.Message}");
            }

            // A frozen ADC needs a reset of the board, and a run may still write the file
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
            await Lab.DeleteAsync(Session, file);
            Assert.Fail(problem);
        }

        await Lab.DeleteAsync(Session, file);
        Assert.Multiple(() =>
        {
            Assert.That(issued, Is.EqualTo(SzpRuns), "runs started");
            Assert.That(m956Errors, Is.Empty, "M956 failed");
            Assert.That(timeouts, Is.Empty, $"runs that did not end within {RunTimeout.TotalSeconds:0} s");
            Assert.That(shortRuns, Is.Empty, $"runs with fewer than {SamplesPerRun} points");
            Assert.That(pollErrors, Is.Zero, "failed reads of the coil");
        });
    }
}
