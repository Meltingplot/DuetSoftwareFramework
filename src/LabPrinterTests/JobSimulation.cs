using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Job files simulated with M37, the one job path that runs on the CHX without motion or heat. The test files contain only comments, G4 and
/// <c>set global.labTest...</c>, and they are deleted again
/// </summary>
/// <remarks>
/// Why M37 is safe and M32 is not (chx350-config branch 3.7 at 3100431, RRF fork 3.7-dev at 0d4295065, DSF of this branch):
/// - RRF starts a simulated file like a print and runs start.g (SimulateFile, GCodes3.cpp:287-327; StartPrinting, GCodes.cpp:4046). The CHX start.g
///   returns at once when simulating (sys/start.g:4-12, M99). Before that it ends the simulation with M2 unless global.machine_mode is "automatic",
///   so the tests run only in automatic mode (<see cref="RequireAutomaticModeAsync"/>). Everything that waits, checks hardware or heats comes after
///   that return (sys/start.g:13-89, e.g. weekly-before-job.g at :86)
/// - At the end of a simulation RRF runs no stop.g: the branch for exitSimulationWhenFileComplete only reports the simulated time
///   (GCodes.cpp:5000-5030), stop.g is set up only in the branch of a real print (:5031, :5080; GCodes4.cpp:844). The CHX stop.g returns when
///   simulating anyway (sys/stop.g:5-6); else it would run print/finish.g, which moves Z and the gantry (sys/meltingplot/print/finish.g:26-27)
/// - A cancel, such as the M2 of start.g or of a test file, runs neither cancel.g nor stop.g while simulating (GCodes2.cpp:848-861)
/// - In simulation RRF skips the G-codes above G4 except mode changes (GCodes2.cpp:210-220), only adds G4 of the file to the simulated time
///   (GCodes.cpp:4080-4088) and skips most M-codes (GCodes2.cpp:687-700). DSF ignores M-codes of a simulated file except M0-M2
///   (MCodeHandler.cs:82-86). Global variables are set as usual: RRF checks no simulation in SetVariable (SbcInterface.cpp:1036)
/// - No test pauses a simulation: RRF runs pause.g when all axes count as homed (GCodes4.cpp:557-566), which a simulation pretends
///   (GCodes3.cpp:313), and the CHX pause.g retracts (sys/pause.g:7-8)
/// - M32 is not used: a real job runs all of start.g, including weekly-before-job.g, and stop.g with print/finish.g at its end
/// - The daemon books spool use and watches the net feed only while the status is "processing" (sys/trigger8.g:27, sys/daemon.g:324); a simulation
///   reports "simulating"
/// - Residual risk, for Tim's approval: StartPrinting resets the extruder positions (GCodes.cpp:4026). daemon.g takes a changed extruder position as
///   motion for 0.25 s (sys/daemon.g:86-92), and a door opened in that window halts the machine with M112 (sys/trigger4.g:19-25). Keep the doors closed.
///   Event macros that pause a job (heater-fault.g, driver-error.g, filament-error.g, expansion-timeout.g, M25) are not expected within the second a
///   test file takes
/// </remarks>
[TestFixture, Category("Approval"), Explicit("Needs Tim's approval for each run")]
public class JobSimulation : LabTest
{
    /// <summary>
    /// Directory of the test files. The tests delete their files and this directory, and 0:/gcodes/lab-tests if no other test has files there
    /// </summary>
    private const string Directory = "0:/gcodes/lab-tests/simulation";

    private const string GlobalsMacro = $"{Lab.MacroDirectory}/simulation-globals.g";
    private const string AsDsf = ApprovalHelpers.AsDsf;

    /// <summary>
    /// Time a simulated test file may take from M37 to the end, including start.g of the CHX and the update of job.lastFileName
    /// </summary>
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(60);

    private readonly List<string> _files = [];

    /// <summary>
    /// How long to wait for automatic mode before a test, set with LAB_AUTOMATIC_MODE_WAIT in seconds (default: no wait). A side-load restarts DCS, RRF
    /// is halted then, and the M999 of the runner runs config.g again, which leaves automatic mode (run-lab-tests.py:91-110, :469-473). So with a side-load
    /// the mode can only be set on the machine after the run has started (Approval run 2026-11-25)
    /// </summary>
    private static readonly TimeSpan AutomaticModeWait =
        TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("LAB_AUTOMATIC_MODE_WAIT"), out int seconds) && seconds > 0 ? seconds : 0);

    [SetUp]
    public async Task RequireAutomaticModeAsync()
    {
        JsonElement mode = await QueryAsync("global.machine_mode");
        if (!IsAutomatic(mode) && AutomaticModeWait > TimeSpan.Zero)
        {
            TestContext.Progress.WriteLine($"global.machine_mode is {mode}: close both doors and switch the machine to automatic mode, waiting up to {AutomaticModeWait.TotalSeconds:0} s");
            Stopwatch waited = Stopwatch.StartNew();
            while (!IsAutomatic(mode) && waited.Elapsed < AutomaticModeWait)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                mode = await QueryAsync("global.machine_mode");
            }
        }
        if (!IsAutomatic(mode))
        {
            Assert.Ignore($"global.machine_mode is {mode}, not \"automatic\": the CHX start.g ends a simulation in any other mode (sys/start.g:5-11)");
        }
    }

    private static bool IsAutomatic(JsonElement mode) => mode.ValueKind == JsonValueKind.String && mode.GetString() == "automatic";

    [TearDown]
    public async Task DeleteFilesAsync()
    {
        if (!Lab.IsConfigured || _files.Count == 0)
        {
            return;
        }

        await using DuetHttpSession session = await Lab.ConnectAsync();
        foreach (string file in _files)
        {
            await Lab.DeleteAsync(session, file);
        }
        _files.Clear();
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
                    // Not empty: files of another test are still there, which deletes them itself
                    TestContext.Progress.WriteLine($"Kept {directory}: {e.Message}");
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Since upstream d108e9767 the codes of a job keep the job's token. A line that cannot be read, e.g. an <c>else</c> without <c>if</c>
    /// ("unexpected else", CodeFile.cs:572), made JobProcessor.DoFilePrint abort the job at once, which cancelled the codes it had read ahead of that
    /// line and not finished yet, although RRF executes them before it gets to the failing line. Fixed in DSF #212 (fork commit 91555b8fc): reading
    /// stops at that line and the job is aborted once the codes before it are done (JobProcessor.cs:527-547, :590-599).
    /// DSF reads up to 32 codes ahead (Settings.cs:233) and the <c>set global</c> before the else waits until the dwells before it are done
    /// (KeywordHandler.cs:244), so without the fix the abort cancels it: labTestJobBeforeElse stays false. That happens in nearly every run, it was
    /// 5 of 5 in the unit test Codes.JobFiles.CodesBeforeLineThatCannotBeReadStillRun; not counted on the printer.
    /// No fork release had the bug: DSF 3.7.0+mp.15 predates d108e9767, so it passes as well
    /// </summary>
    [Test]
    public async Task CodesBeforeUnreadableLineStillRun()
    {
        await ApprovalHelpers.SetGlobalsAsync(Session, GlobalsMacro,
            ("labTestJobBeforeDwells", "false"), ("labTestJobBeforeElse", "false"), ("labTestJobAfterElse", "false"));
        string file = await UploadJobAsync("unreadable-line.g",
            "; lab printer test: the else below cannot be read, the codes before it must still run\n" +
            "set global.labTestJobBeforeDwells = true\n" +
            string.Concat(Enumerable.Repeat("G4 P1\n", 10)) +
            "set global.labTestJobBeforeElse = true\n" +
            "else\n" +
            "set global.labTestJobAfterElse = true\n");
        string cursor = await Lab.GetJournalCursorAsync();

        Assert.That(await SendCodeAsync($"M37 P\"{file}\" F0"), Does.Not.Contain("Error"));
        await WaitForJobEndAsync(file);

        bool beforeDwells = (await QueryAsync("global.labTestJobBeforeDwells")).GetBoolean();
        bool beforeElse = (await QueryAsync("global.labTestJobBeforeElse")).GetBoolean();
        bool afterElse = (await QueryAsync("global.labTestJobAfterElse")).GetBoolean();
        bool aborted = Lab.Read(Session, model => model.Job.LastFileAborted);
        string journal = await Lab.ReadJournalAsync(cursor);
        Assert.Multiple(() =>
        {
            Assert.That(beforeDwells, Is.True, "The first line did not run");
            Assert.That(beforeElse, Is.True, "The line before the unreadable one was cancelled");
            Assert.That(afterElse, Is.False, "A line after the unreadable one ran");
            Assert.That(aborted, Is.True, "The job was not aborted (JobProcessor.cs:855)");
            Assert.That(journal, Does.Contain("unexpected else"));
        });
    }

    /// <summary>
    /// RRF holds the path of the file it prints in 120 bytes (MaxFilenameLength) and cuts a longer one in SBC mode without an error, so job.lastFileName and
    /// resurrect.g got a cut path (UB1 of the bounds census, cut name seen on the printer 2026-11-24). Fixed in DSF #219: SelectFileAsync refuses a virtual
    /// path of more than Consts.MaxFilePathLength = 120 bytes with "Filename too long (max 120 bytes)" (JobProcessor.cs:319-321, Consts.cs:50), which M37
    /// replies as error. Without the fix the Writer accepted names up to 254 bytes (Writer.cs:495-504 before #219), so the 130-byte path here is simulated
    /// under its cut name and the reply has no error. Deterministic
    /// </summary>
    [Test]
    public async Task LongJobPathIsRefused()
    {
        await G3Helpers.RequireControlServerWithNewFixesAsync("#219");
        await ApprovalHelpers.SetGlobalsAsync(Session, GlobalsMacro, ("labTestJobLongPath", "false"));

        // The stamp of UploadJobAsync comes first, so the 120 bytes RRF keeps of the path tell this run from an earlier one
        string name = "long-path-" + new string('x', 130 - Directory.Length - 1 - "HHmmssfff-long-path-".Length - ".g".Length) + ".g";
        string file = await UploadJobAsync(name, "; lab printer test: the path of this file is longer than RRF can hold\nset global.labTestJobLongPath = true\n");
        Assert.That(Encoding.UTF8.GetByteCount(file), Is.EqualTo(130));

        string reply = await SendCodeAsync($"M37 P\"{file}\" F0");
        if (!reply.Contains("Error"))
        {
            // The file is simulated under its cut name, let it end before the test fails
            string cut = file[..120];
            await Lab.TryWaitForModelAsync(Session, model => model.Job.LastFileName == cut && Lab.IsIdle(model), JobTimeout);
        }

        Assert.That(reply, Does.Contain("Filename too long (max 120 bytes)"));
        Assert.That((await QueryAsync("global.labTestJobLongPath")).GetBoolean(), Is.False, "The file was simulated");
    }

    /// <summary>
    /// File positions are 32-bit values in the SBC protocol and in RRF, so a pause past 4 GiB resumed near the start of the file (UB3). Fixed in DSF #219:
    /// SelectFileAsync refuses a file of more than uint.MaxValue bytes with "File too big (max 4294967295 bytes)" (JobProcessor.cs:322-326). Without the fix
    /// the file is simulated, and its first code M2 cancels the simulation at once (allowed in simulation by MCodeHandler.cs:82-86 and GCodes2.cpp:687-700,
    /// no macros on a cancelled simulation, GCodes2.cpp:848-861), so the reply has no error. Deterministic.
    /// The file is sparse, the SBC creates it on the file system of the SD directory, which must support holes. It is written as dsf, which owns the
    /// uploaded placeholder (DuetWebServer runs as dsf, duetwebserver.service:14, and writes uploads itself, MachineEndpoints.cs:269-279), so a symlink
    /// in its place could not make the test change a file that dsf cannot change anyway. As in the unit test
    /// Codes.PrintFileInfo.FileOf4GiBIsRefused, 100000 short codes at both ends keep the zeros of the hole away from what DSF reads: FileInfoParser
    /// reads at most 16 KiB from the start and 400 KiB from the end (Settings.cs:306, :311), and the job reader of an unfixed build stays at most
    /// 32 codes ahead of RRF (Settings.cs:233) and never gets past the G4 lines, which RRF only finishes in a job that M2 has not cancelled
    /// </summary>
    [Test]
    public async Task HugeJobFileIsRefused()
    {
        await G3Helpers.RequireControlServerWithNewFixesAsync("#219");
        await ApprovalHelpers.RequirePython3Async();
        string sdDirectory = await ApprovalHelpers.GetSdDirectoryAsync();

        // The upload creates the directory as DuetWebServer creates it; the sparse file replaces the placeholder and keeps its owner
        string file = await UploadJobAsync("huge.g", "; placeholder, replaced by a sparse file of 4 GiB\n");
        string physicalFile = sdDirectory + file["0:".Length..];
        string output = await Lab.RunOnSbcAsync($$"""
            set -e
            fs=$({{AsDsf}} stat -f -c %T '{{sdDirectory}}')
            case "$fs" in
              ext2/ext3|xfs|btrfs|tmpfs) ;;
              *) echo "unsupported $fs"; exit 0 ;;
            esac
            {{AsDsf}} python3 -I - '{{physicalFile}}' <<'EOF'
            import sys
            path, size = sys.argv[1], 1 << 32
            codes = b"G4 P1\n" * 100000
            with open(path, "r+b") as f:
                f.truncate(0)
                f.write(b"; lab printer test: sparse file of 4 GiB, M2 cancels a simulation at once\nM2\n" + codes)
                f.truncate(size)
                f.seek(size - len(codes))
                f.write(codes)
            EOF
            echo "size $({{AsDsf}} stat -c %s '{{physicalFile}}') bytes, $({{AsDsf}} du -k '{{physicalFile}}' | cut -f1) KiB allocated"
            """);
        if (output.StartsWith("unsupported"))
        {
            Assert.Ignore($"The SD directory is on a file system without holes ({output.Trim()}), a file of 4 GiB would fill it");
        }
        TestContext.Progress.WriteLine($"Sparse file {physicalFile}: {output.Trim()}");

        string reply = await SendCodeAsync($"M37 P\"{file}\" F0");
        if (!reply.Contains("Error"))
        {
            await Lab.TryWaitForModelAsync(Session, model => model.Job.LastFileName == file && Lab.IsIdle(model), JobTimeout);
        }
        Assert.That(reply, Does.Contain($"File too big (max {uint.MaxValue} bytes)"));
    }

    /// <summary>
    /// The Writer threw on the link task when the slicer comment of a selected file had more than 254 bytes (UB1), because GeneratedBy has a length byte in
    /// SetPrintFileInfo. FileInfoParser takes the comment of <c>; generated by</c> without a limit (Settings.cs:380). The link task has no catch there and a
    /// failed background service stops the host, so DCS stopped at M37 and RRF halted. Fixed in DSF #219: the Writer cuts it to
    /// Consts.MaxGeneratedByLength = 254 bytes (Writer.cs:501-510). Without the fix DCS gets a new PID, the test recovers (DCS restart and M999, one
    /// E-stop) and fails. Deterministic
    /// </summary>
    [Test]
    public async Task LongSlicerCommentIsCut()
    {
        await G3Helpers.RequireControlServerWithNewFixesAsync("#219");
        await ApprovalHelpers.SetGlobalsAsync(Session, GlobalsMacro, ("labTestJobLongSlicer", "false"));
        string slicer = "lab-test-slicer " + new string('s', 300 - "lab-test-slicer ".Length);
        string file = await UploadJobAsync("long-slicer.g", $"; generated by {slicer}\n; lab printer test: the slicer comment is longer than SetPrintFileInfo can carry\nset global.labTestJobLongSlicer = true\n");

        // M36 shows what FileInfoParser takes from the comment; DSF answers it without the link (MCodeHandler.cs:483-499)
        using (JsonDocument info = JsonDocument.Parse(await SendCodeAsync($"M36 \"{file}\"")))
        {
            string? generatedBy = info.RootElement.TryGetProperty("generatedBy", out JsonElement value) ? value.GetString() : null;
            if (generatedBy != slicer)
            {
                Assert.Inconclusive($"FileInfoParser took \"{generatedBy}\" as slicer, the GeneratedByFilters of this DCS do not match the comment");
            }
        }

        string pid = await Lab.GetControlServerPidAsync();
        string reply;
        bool ended = false;
        try
        {
            // M37 waits until the link task has sent the file information (MCodeHandler.cs:552-562)
            reply = await SendCodeAsync($"M37 P\"{file}\" F0");
            ended = await Lab.TryWaitForModelAsync(Session, model => model.Job.LastFileName == file && Lab.IsIdle(model), JobTimeout);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            reply = $"no reply: {e.Message}";
        }
        if (await Lab.GetControlServerPidAsync() != pid || reply.StartsWith("no reply"))
        {
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
            Assert.Fail($"DCS stopped when it sent the information of the selected file (M37: {reply})");
        }

        Assert.That(reply, Does.Not.Contain("Error"));
        Assert.That(ended, Is.True, "The simulation did not end");
        Assert.That((await QueryAsync("global.labTestJobLongSlicer")).GetBoolean(), Is.True, "The file was not simulated");
    }

    /// <summary>
    /// After M37 with F1 (the default, Gcodes.md M37) DSF writes the simulated time back into the file (JobProcessor.cs:861-896,
    /// FileInfoParser.UpdateSimulatedTimeAsync opens it for writing, FileInfoParser.cs:915-921). If that failed, the exception left the job loop and the
    /// host stopped (AS2 of the async census). Fixed in DSF #221: the failure is logged as "Failed to update simulation time of ..." and the job loop goes
    /// on (JobProcessor.cs:883-891). The owner of the uploaded file, dsf (duetwebserver.service:14), takes its write permission away (mode 0444), so
    /// DCS, which runs as dsf (duetcontrolserver.service:12) without CAP_DAC_OVERRIDE (duetcontrolserver postinst:36-43, ControlServerCapabilities),
    /// cannot open it for writing (FileMode.Open with FileAccess.ReadWrite, FileInfoParser.cs:921). No root command touches the file. G4 S2 gives a
    /// simulated time of 2 s, and DSF writes only a time above 0 (JobProcessor.cs:880, RRF lastDuration, GCodes.cpp:5020). Without the fix DCS gets a new PID, the test recovers (DCS restart and M999, one E-stop) and fails. Deterministic
    /// </summary>
    [Test]
    public async Task SimulatedTimeWriteFailureEndsOnlySimulation()
    {
        await G3Helpers.RequireControlServerWithNewFixesAsync("#221");
        await ApprovalHelpers.SetGlobalsAsync(Session, GlobalsMacro, ("labTestJobReadOnly", "false"), ("labTestJobAfterReadOnly", "false"));
        string sdDirectory = await ApprovalHelpers.GetSdDirectoryAsync();
        string file = await UploadJobAsync("read-only.g", "; lab printer test: DSF cannot write the simulated time into this file\nG4 S2\nset global.labTestJobReadOnly = true\n");
        string next = await UploadJobAsync("after-read-only.g", "; lab printer test: simulated after the file DSF could not write\nset global.labTestJobAfterReadOnly = true\n");
        string physicalFile = sdDirectory + file["0:".Length..];
        await Lab.RunOnSbcAsync($"{AsDsf} chmod 0444 '{physicalFile}'");

        string cursor = await Lab.GetJournalCursorAsync(), pid = await Lab.GetControlServerPidAsync();
        Assert.That(await SendCodeAsync($"M37 P\"{file}\""), Does.Not.Contain("Error"));
        bool ended = await Lab.TryWaitForModelAsync(Session, model => model.Job.LastFileName == file && Lab.IsIdle(model), JobTimeout);

        // The time is written after the end, once RRF has reported job.lastDuration
        string journal = string.Empty;
        for (int i = 0; i < 10 && !journal.Contains("Failed to update simulation time"); i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            journal = await Lab.ReadJournalAsync(cursor);
        }
        if (await Lab.GetControlServerPidAsync() != pid)
        {
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
            Assert.Fail("DCS stopped when it could not write the simulated time");
        }

        Assert.That(ended, Is.True, "The simulation did not end");
        Assert.That((await QueryAsync("global.labTestJobReadOnly")).GetBoolean(), Is.True, "The file was not simulated");
        Assert.That(journal, Does.Contain("Failed to update simulation time of"), "DSF wrote into a read-only file, or never tried");

        // The job loop still takes files
        string nextReply;
        try
        {
            nextReply = await SendCodeAsync($"M37 P\"{next}\" F0");
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            // An unfixed DCS that is still stopping
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
            Assert.Fail($"M37 of the next file got no reply: {e.Message}");
            throw;
        }
        Assert.That(nextReply, Does.Not.Contain("Error"));
        await WaitForJobEndAsync(next);
        Assert.That((await QueryAsync("global.labTestJobAfterReadOnly")).GetBoolean(), Is.True, "The next file was not simulated");
    }

    /// <summary>
    /// Upload a test file under a name of its own, so that job.lastFileName tells this run from an earlier one. RRF reports lastFileName only once the job
    /// has ended (PrintMonitor.cpp:76)
    /// </summary>
    private async Task<string> UploadJobAsync(string name, string content)
    {
        string file = $"{Directory}/{DateTime.UtcNow:HHmmssfff}-{name}";
        _files.Add(file);
        await Lab.UploadAsync(Session, file, content);
        return file;
    }

    /// <summary>
    /// Wait until the simulation of a file has ended: RRF reports it as last file and DSF has cleared the job file (JobProcessor.cs:851-857)
    /// </summary>
    private Task WaitForJobEndAsync(string file) =>
        Lab.WaitForModelAsync(Session, model => model.Job.LastFileName == file && Lab.IsIdle(model), JobTimeout, $"the simulation of {file} to end");
}
