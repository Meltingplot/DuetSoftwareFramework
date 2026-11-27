using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// X and Y moves on the CHX: from HTTP as the dashboard jogs, from a macro and from a job file started with M32. In the macro and in the job, each move is
/// followed by an M117, which RRF queues behind the move. These are the only lab tests that move the machine (stage 4 of design/lab-test-plan.md, Tim on
/// site). Tim (2026-11-27) asked for them after upstream 187840328: the regression it fixes could not show in the lab run of 2026-11-26, because no test moved
/// </summary>
/// <remarks>
/// Envelope (chx350-config branch 3.7 at 726e59a):
/// - Only in automatic mode with both doors closed. In default mode the whole axis range is a keep-out zone (sys/meltingplot/ce-declaration/operating-mode/
///   default.g:1), and daemon.g switches to automatic mode only when both doors were opened once and are closed (sys/daemon.g:174-190). A door opened while an
///   axis moved in the last 0.25 s halts the machine with M112 (sys/daemon.g:60-103, sys/trigger4.g:19-25), so the doors stay closed during the run
/// - The tests do not home. X, Y, Z and U must be homed before the run, e.g. with G28 (sys/homeall.g, 2:34 min on 2026-11-23)
/// - Only X and Y move, in machine coordinates (G53, which also ignores tool offsets, wiki Gcodes.md "G53"), on a square of <see cref="Step"/> mm with its
///   corner at X max/2, Y max/2 - <see cref="Step"/>: F6000 to that corner and F1200 along the square. Z and U do not move. U must stand at its maximum and Z
///   at <see cref="MinimumZ"/> mm or more, as when the machine's own macros move X and Y to max/2 with U at its maximum at F60000 (sys/bed.g:33-34,
///   sys/homez.g:39-40). No extruder moves, no heaters, fans or outputs: the only other code is M117, and the tests set the display message back. The CHX
///   writes no display message itself (its one M117 is commented out, sys/meltingplot/ce-declaration/e-stop.g:28)
/// - <see cref="QueuedCodesRunInAJob"/> starts a real job, which runs the CHX start.g and stop.g like any job; see there. It runs last, because the machine
///   is in default mode afterwards
/// </remarks>
[TestFixture, Category("Motion"), Explicit("Moves X and Y and runs a job with M32: stage 4, only with Tim on site"), NonParallelizable]
public class Motion : LabTest
{
    /// <summary>
    /// Directory of the job file. The test deletes its file and this directory, and 0:/gcodes/lab-tests if no other test has files there
    /// </summary>
    private const string Directory = "0:/gcodes/lab-tests/motion";

    private const string MacroFile = $"{Lab.MacroDirectory}/motion-queued-codes.g";

    /// <summary>
    /// Side of the square in mm. At <see cref="MoveFeed"/> a side takes 2 s, so every M117 after a side finds that move still running
    /// </summary>
    private const float Step = 40;

    private const int TravelFeed = 6000, MoveFeed = 1200;

    /// <summary>
    /// Lowest Z for X and Y moves, the height sys/bed.g:33 lifts Z to before it moves X and Y
    /// </summary>
    private const float MinimumZ = 20;

    private const float Tolerance = 0.05F;

    /// <summary>
    /// Time a macro or the moves sent from HTTP may take, including the way to the square from a parked gantry (about 470 mm)
    /// </summary>
    private const int MoveTimeoutSeconds = 90;

    /// <summary>
    /// Time the job may take from M32 to the end of stop.g. start.g waits up to 20 s for the coolant fans (sys/start.g:21-28) and may run the quick self-test
    /// (:51-52), stop.g lifts Z to its maximum at the Z speed limit of 1800 mm/min (sys/meltingplot/print/finish.g:26, operating-mode/automatic.g:2)
    /// </summary>
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(5);

    private readonly List<string> _files = [];
    private Square? _square;
    private string? _displayMessage;

    /// <summary>
    /// Axis indices and the start corner of the square in machine coordinates
    /// </summary>
    private sealed record Square(int X, int Y, float StartX, float StartY)
    {
        /// <summary>
        /// The four corners in the order the tests move to them, ending at the start
        /// </summary>
        public (float X, float Y)[] Corners =>
            [(StartX + Step, StartY), (StartX + Step, StartY - Step), (StartX, StartY - Step), (StartX, StartY)];
    }

    /// <summary>
    /// How long to wait for the envelope before a test, set with LAB_AUTOMATIC_MODE_WAIT in seconds as for JobSimulation (default: no wait). A side-load run
    /// restarts DCS and sends M999 (run-lab-tests.py), which leaves the axes not homed and the machine in default mode, so with --side-load the axes can only be
    /// homed and the doors opened and closed once the run has started
    /// </summary>
    private static readonly TimeSpan EnvelopeWait =
        TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("LAB_AUTOMATIC_MODE_WAIT"), out int seconds) && seconds > 0 ? seconds : 0);

    /// <summary>
    /// The printer is busy while Tim homes the axes during that wait (G28 takes 2:34 min, sys/homeall.g), so the idle wait of <see cref="LabTest"/> is as long
    /// </summary>
    protected override TimeSpan IdleWait => EnvelopeWait > base.IdleWait ? EnvelopeWait : base.IdleWait;

    [SetUp]
    public async Task RequireEnvelopeAsync()
    {
        string? problem = CheckEnvelope();
        if (problem is not null && EnvelopeWait > TimeSpan.Zero)
        {
            TestContext.Progress.WriteLine($"{problem}. Waiting up to {EnvelopeWait.TotalSeconds:0} s");
            Stopwatch waited = Stopwatch.StartNew();
            while (problem is not null && waited.Elapsed < EnvelopeWait)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                problem = CheckEnvelope();
            }
        }
        if (problem is not null)
        {
            Assert.Ignore(problem);
        }
    }

    /// <summary>
    /// Check the envelope of the class remarks and keep the square and the display message to restore. It reads only the model of the session and sends no
    /// code: while Tim homes from DWC, a code on the HTTP channel waits until G28 has ended and its request times out
    /// </summary>
    /// <returns>Why the tests may not move now, or null</returns>
    private string? CheckEnvelope()
    {
        (Square? square, string? problem, string? displayMessage) = Lab.Read(Session, model =>
        {
            string? mode = model.Global.TryGetValue("machine_mode", out JsonElement? value) && value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
            (Square? square, string? problem) =
                mode != "automatic" ? (null, $"global.machine_mode is {mode ?? "not set"}, not \"automatic\": default mode blocks every move with a keep-out zone " +
                                             "(operating-mode/default.g:1). Open both doors once and close them") :
                !Lab.IsIdle(model) ? (null, $"The printer is {model.State.Status}, not idle") :
                GetSquare(model);
            return (square, problem, model.State.DisplayMessage);
        });
        _square = square;
        _displayMessage = displayMessage;
        return square is null ? problem ?? "Outside the envelope" : null;
    }

    /// <summary>
    /// Check the envelope of the class remarks and work out the square
    /// </summary>
    private static (Square? Square, string? Problem) GetSquare(ObjectModel model)
    {
        int Index(char letter)
        {
            for (int i = 0; i < model.Move.Axes.Count; i++)
            {
                if (model.Move.Axes[i].Letter == letter)
                {
                    return i;
                }
            }
            return -1;
        }

        int x = Index('X'), y = Index('Y'), z = Index('Z'), u = Index('U');
        if (x < 0 || y < 0 || z < 0 || u < 0)
        {
            return (null, "The machine has no X, Y, Z and U axes, so it is not the CHX these moves are made for");
        }

        Axis[] axes = [model.Move.Axes[x], model.Move.Axes[y], model.Move.Axes[z], model.Move.Axes[u]];
        if (axes.Any(axis => !axis.Homed))
        {
            return (null, $"Not homed: {string.Join(", ", axes.Where(axis => !axis.Homed).Select(axis => axis.Letter))}. Home X, Y, Z and U before the run, e.g. with G28");
        }
        if (model.Move.Axes[z].MachinePosition is not float zPosition || zPosition < MinimumZ)
        {
            return (null, $"Z is at {model.Move.Axes[z].MachinePosition}, below {MinimumZ} mm (sys/bed.g:33)");
        }
        if (model.Move.Axes[u].MachinePosition is not float uPosition || Math.Abs(uPosition - model.Move.Axes[u].Max) > Tolerance)
        {
            return (null, $"U is at {model.Move.Axes[u].MachinePosition}, not at its maximum {model.Move.Axes[u].Max} where the machine's own macros keep it while X and Y move");
        }

        // The door inputs read 0 while a door is open (sys/meltingplot/ce-declaration/doors/wait-closed.g:2)
        if (model.Sensors.GpIn.Count < 4 || model.Sensors.GpIn[2] is not GpInputPort left || model.Sensors.GpIn[3] is not GpInputPort right || left.Value == 0 || right.Value == 0)
        {
            return (null, "A door is open, or sensors.gpIn[2] and [3] are not the door inputs (sys/meltingplot/ce-declaration/ce-declaration.g:5-6)");
        }

        Square square = new(x, y, model.Move.Axes[x].Max / 2, model.Move.Axes[y].Max / 2 - Step);
        if (square.StartX + Step > model.Move.Axes[x].Max || square.StartY - Step < model.Move.Axes[y].Min)
        {
            return (null, $"The square of {Step} mm does not fit between the axis limits");
        }
        return (square, null);
    }

    [TearDown]
    public async Task RestoreAsync()
    {
        if (!Lab.IsConfigured || _square is null)
        {
            return;
        }

        await using DuetHttpSession session = await Lab.ConnectAsync();
        foreach (string file in _files)
        {
            await Lab.DeleteAsync(session, file);
        }
        _files.Clear();

        // M117 with an empty string clears the message again
        await Lab.SendCodeAsync(session, $"M117 \"{(_displayMessage ?? string.Empty).Replace("\"", "\"\"")}\"", TimeSpan.FromSeconds(10));
        _square = null;
    }

    [OneTimeTearDown]
    public async Task DeleteDirectoryAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            foreach (string directory in new[] { Directory, "0:/gcodes/lab-tests" })
            {
                try
                {
                    await Lab.DeleteAsync(session, directory);
                }
                catch (HttpRequestException e)
                {
                    // Missing, or not empty: files of another test are still there, which deletes them itself
                    TestContext.Progress.WriteLine($"Kept {directory}: {e.Message}");
                    break;
                }
            }
        }
    }

    /// <summary>
    /// No known bug: moves sent from HTTP the way DWC and the CHX panel send them, one request of four lines. A point is reached with M120, G90, G1 and M121
    /// (DuetWebControl src/plugins/CHX350/pages/Control.vue:437), a jog with G91 instead of G90 (src/components/panels/MovementPanel.vue:518,
    /// src/plugins/CHX350/flows/FlowPanel.vue:789). After each jog and M400 the machine position must have moved by the step, and M121 must have left the
    /// HTTP channel absolute again. It runs first, so that a failure of the queued codes below can be told from a failure of moves in general
    /// </summary>
    [Test]
    public async Task JogsLikeTheDashboard()
    {
        Square square = _square!;
        await MoveToStartAsync(square);

        float x = square.StartX, y = square.StartY;
        foreach ((float cornerX, float cornerY) in square.Corners)
        {
            string jog = cornerX != x ? $"X{Format(cornerX - x)}" : $"Y{Format(cornerY - y)}";
            Assert.That(await SendCodeAsync($"M120\nG91\nG1 {jog} F{MoveFeed}\nM121"), Does.Not.Contain("Error"), $"Jog {jog}");
            Assert.That(await SendCodeAsync("M400", MoveTimeoutSeconds), Does.Not.Contain("Error"));
            (x, y) = (cornerX, cornerY);
            await AssertPositionAsync(square, x, y, $"after the jog {jog}");

            // The key has an index, so DSF passes M409 to RRF instead of answering from its model
            Assert.That((await QueryAsync($"inputs[{(int)CodeChannel.HTTP}].axesRelative")).GetBoolean(), Is.False, $"M121 left the HTTP channel relative after the jog {jog}");
        }
    }

    /// <summary>
    /// RRF queues M117, and M3 to M5, M42, M104, M106, M107, M140, M141, M144, M150, M280, M300, M568 and G10 with temperatures, behind the moves when the code
    /// comes from a file, has no expression and moves are in flight (GCodeQueue::ShouldQueueMCode, RRF src/GCodes/GCodeQueue.cpp:35-102,
    /// GCodeBuffer::CanQueueCodes, src/GCodes/GCodeBuffer/GCodeBuffer.h:521-524). The wiki names only job files (Gcodes.md:246), but in SBC mode RRF counts
    /// every channel that runs a file, and a macro as well: StartNewFile marks the channel for every macro (GCodeMachineState::DoingFile,
    /// src/GCodes/GCodeMachineState.cpp:214-220; GCodeBuffer.cpp:422-426, called by DoFileMacro, GCodes.cpp:3806). The queue channel runs the code once the move
    /// before it has ended (GCodeQueue::FillBuffer and QueuedCode::AssignTo, GCodeQueue.cpp:140, :255).
    /// Since upstream e520116e9, which came with the RRF fork sync #117, the binary buffer of a channel is allocated in SbcInterface::FillBuffer when DSF has a
    /// code for that channel (src/SBC/SbcInterface.cpp). DSF sends nothing on the queue channel, so its buffer never got one, and every queued code overflowed
    /// and failed with "GCode command too long" (BinaryParser.cpp:43-50, GCodes2.cpp:124-128). Fixed by upstream 187840328, which allocates the buffer in
    /// QueuedCode::AssignTo. RRF sends that error as a generic message, which DCS logs as an error (LinkService.cs:880-956, ObjectModel.cs:784-808).
    /// Without the fix the display message stays at the M117 that runs with no move in flight, after the M400 at the start, and the journal has four
    /// "GCode command too long". Deterministic: each queued M117 follows a move of 2 s, and the M400 at the end waits until the queue is empty
    /// (LockMovementSystemAndWaitForStandstill, GCodes.cpp:2050, :2081-2088). The fork releases up to 3.7.0+1.mp.11 allocate the buffer in BinaryParser::Put
    /// and pass
    /// </summary>
    [Test, DependsOnTest(nameof(JogsLikeTheDashboard), AllowFailure = true)]
    public async Task QueuedCodesRunInAMacro()
    {
        Square square = _square!;
        string stamp = DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);
        _files.Add(MacroFile);
        await Lab.UploadAsync(Session, MacroFile, QueuedCodesFile(square, stamp, "a macro"));
        string cursor = await Lab.GetJournalCursorAsync();

        string reply = await SendCodeAsync($"M98 P\"{MacroFile}\"", MoveTimeoutSeconds);

        await AssertPositionAsync(square, square.StartX, square.StartY, "after the macro");
        string? message = (await QueryAsync("state.displayMessage")).GetString();
        string journal = await Lab.ReadJournalAsync(cursor);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.Contain("Error"));
            Assert.That(message, Is.EqualTo(DisplayText(stamp, square.Corners.Length)), "A queued M117 did not run");
            Assert.That(journal, Does.Not.Contain("GCode command too long"));
        });
    }

    /// <summary>
    /// The codes of <see cref="QueuedCodesRunInAMacro"/> in a job file started with M32 (wiki Gcodes.md "M32"), the path of a print. Same bug and same
    /// criteria; the job must also end without being aborted. The job runs as any job on the CHX:
    /// - start.g refuses the job with M0 when a coolant fan stands still, the emergency stop failed its monthly check or a self-test failed
    ///   (sys/start.g:21-60), and runs the quick self-test if none ran since the boot (:51-52), which neither moves nor heats
    ///   (sys/meltingplot/self-test/check-quick.g:23). A due weekly tier runs before the job and heats (:83-89, self-test/weekly-before-job.g), so the test
    ///   skips while global.self_test_due[1] is true. The door check of start.g passes without a prompt in automatic mode (:81, doors/ensure-checked-closed.g)
    /// - At the end RRF runs stop.g, and stop.g runs print/finish.g (sys/stop.g:8-10). With a tool selected that retracts with G10, which does not move a
    ///   cold extruder (RRF src/Movement/DDA.cpp:1418-1420), and deselects the tool (finish.g:5-16). It lifts Z to its maximum minus 1 mm and parks X at its
    ///   maximum, Y at its minimum and U at its maximum at F60000 (finish.g:24-27), and at its end it clears both door checks, so daemon.g drops to default mode
    ///   (finish.g:97-98; sys/daemon.g:191-200, operating-mode/default.g). The motion tests skip from then on until both doors have been opened and
    ///   closed again
    /// </summary>
    [Test, DependsOnTest(nameof(QueuedCodesRunInAMacro), AllowFailure = true)]
    public async Task QueuedCodesRunInAJob()
    {
        Square square = _square!;
        JsonElement due = await QueryAsync("global.self_test_due");
        if (due.ValueKind == JsonValueKind.Array && due.GetArrayLength() > 1 && due[1].ValueKind == JsonValueKind.True)
        {
            Assert.Ignore("The weekly self-test is due: start.g would run it before the job, and it heats (sys/start.g:83-89)");
        }

        string stamp = DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);
        string file = $"{Directory}/{stamp}-queued-codes.g";
        _files.Add(file);
        await Lab.UploadAsync(Session, file, QueuedCodesFile(square, stamp, "a job"));
        string cursor = await Lab.GetJournalCursorAsync();

        Assert.That(await SendCodeAsync($"M32 \"{file}\"", 30), Does.Not.Contain("Error"));
        TestContext.Progress.WriteLine("Job started. At its end stop.g parks the gantry and clears the door checks: open and close both doors for automatic mode");
        await Lab.WaitForModelAsync(Session, model => model.Job.LastFileName == file && Lab.IsIdle(model), JobTimeout, $"the job {file} to end");

        bool aborted = Lab.Read(Session, model => model.Job.LastFileAborted);
        string? message = (await QueryAsync("state.displayMessage")).GetString();
        string journal = await Lab.ReadJournalAsync(cursor);
        Assert.Multiple(() =>
        {
            Assert.That(aborted, Is.False, $"The job was aborted, journal since M32:\n{journal}");
            Assert.That(message, Is.EqualTo(DisplayText(stamp, square.Corners.Length)), "A queued M117 did not run");
            Assert.That(journal, Does.Not.Contain("GCode command too long"));
        });
    }

    /// <summary>
    /// File for the macro and the job: to the start corner as the machine's own macros move, with an expression (sys/bed.g:34), then M400 and an M117 that runs
    /// at once, then each side of the square followed by an M117 that RRF queues behind it, and M400 at the end. The texts are short, so each M117 fits in a
    /// queue item of 64 bytes (BufferSizePerQueueItem, RRF src/GCodes/GCodeQueue.h:15, src/Config/Configuration.h:172) and none contains an expression
    /// </summary>
    private static string QueuedCodesFile(Square square, string stamp, string what)
    {
        StringBuilder file = new();
        file.Append($"; lab printer test: X and Y moves from {what}, each followed by an M117 that RRF queues behind it (LabPrinterTests/Motion.cs)\n");
        file.Append("G90\n");
        file.Append($"G53 G1 X{{move.axes[{square.X}].max / 2}} Y{{move.axes[{square.Y}].max / 2 - {Format(Step)}}} F{TravelFeed}\n");
        file.Append("M400\n");
        file.Append($"M117 \"{DisplayText(stamp, 0)}\"\n");
        (float X, float Y)[] corners = square.Corners;
        for (int i = 0; i < corners.Length; i++)
        {
            file.Append($"G53 G1 X{Format(corners[i].X)} Y{Format(corners[i].Y)} F{MoveFeed}\n");
            file.Append($"M117 \"{DisplayText(stamp, i + 1)}\"\n");
        }
        file.Append("M400\n");
        return file.ToString();
    }

    private static string DisplayText(string stamp, int index) => $"lab {stamp} {index}";

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Move to the start corner as the CHX panel moves to a point (Control.vue:437), in machine coordinates, and wait until the move has ended
    /// </summary>
    private async Task MoveToStartAsync(Square square)
    {
        Assert.That(await SendCodeAsync($"M120\nG90\nG53 G1 X{Format(square.StartX)} Y{Format(square.StartY)} F{TravelFeed}\nM121"), Does.Not.Contain("Error"));
        Assert.That(await SendCodeAsync("M400", MoveTimeoutSeconds), Does.Not.Contain("Error"));
        await AssertPositionAsync(square, square.StartX, square.StartY, "at the start corner");
    }

    /// <summary>
    /// Wait until the model reports the machine position, which RRF updates while the machine stands as well
    /// </summary>
    private async Task AssertPositionAsync(Square square, float x, float y, string where)
    {
        static bool Near(float? position, float target) => position is float value && Math.Abs(value - target) <= Tolerance;

        await Lab.TryWaitForModelAsync(Session, model => Near(model.Move.Axes[square.X].MachinePosition, x) && Near(model.Move.Axes[square.Y].MachinePosition, y),
            TimeSpan.FromSeconds(5));
        (float? actualX, float? actualY) = Lab.Read(Session, model => (model.Move.Axes[square.X].MachinePosition, model.Move.Axes[square.Y].MachinePosition));
        Assert.That(Near(actualX, x) && Near(actualY, y), Is.True, $"Machine position {where} is X {actualX} Y {actualY}, not X {Format(x)} Y {Format(y)}");
    }
}
