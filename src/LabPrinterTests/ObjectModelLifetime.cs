using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// The lock of RRF on the lifetime of object model objects, with message boxes as the objects that are created and deleted
/// </summary>
/// <remarks>
/// RRF fork PR #70 (K22 of the pointer census) added ObjectModel::lifetimeLock: the main task takes it for writing before it deletes an object model
/// object, and the SBC requests EvaluateExpression and SetVariable hold it for reading until they have sent their result (RRF SbcInterface.cpp:819,
/// :1077), because the result may point into such an object. Message boxes are such objects. M291 creates one under the write lock
/// (RepRap::SendAlert, RepRap.cpp:2374-2376, called for M291 at GCodes7.cpp:100), M292 deletes one under it (MessageBox::Acknowledge,
/// MessageBox.cpp:132), and RepRap::Spin takes it for every check of the timeout while a box exists (RepRap.cpp:807, MessageBox.cpp:163-165).
/// These are the only writers the envelope of the lab tests allows; the others of PR #70 (M669, M563, M950, ...) change the machine
/// </remarks>
[TestFixture]
public class ObjectModelLifetime : LabTest
{
    private const string Directory = $"{Lab.MacroDirectory}/object-model-lifetime";
    private const string ResetMacro = $"{Directory}/reset.g";
    private const string WriterMacro = $"{Directory}/writer.g";

    /// <summary>
    /// Channel of the reader, Telnet like AbortsCrossingMacroRequests, to keep off SBC, the default channel of the plugins. The writer runs on HTTP
    /// </summary>
    private const CodeChannel ReaderChannel = CodeChannel.Telnet;

    private const int DurationSeconds = 60;

    /// <summary>
    /// Title of the test message boxes, so that the writer closes only its own
    /// </summary>
    private const string BoxTitle = "Lab test";

    /// <summary>
    /// Longest time between two message boxes of the writer and longest reply time of the reader. A reader holds the lock for one request and a
    /// writer for one create or delete (PR #70), and each loop of the writer takes about ten SBC requests, so a gap this long means that one side
    /// waited for something else, e.g. a timeout
    /// </summary>
    private const int MaximumGapMilliseconds = 2000;

    /// <summary>
    /// Least number of message boxes and of reader requests per run, one per second
    /// </summary>
    private const int MinimumCount = DurationSeconds;

    [OneTimeSetUp]
    public async Task UploadMacrosAsync()
    {
        Lab.RequirePrinter();
        await using DuetHttpSession session = await Lab.ConnectAsync();
        await Lab.UploadAsync(session, ResetMacro,
            G2Helpers.Global("labTestLifetimeWrites", "0") +
            G2Helpers.Global("labTestLifetimeMaxGap", "0") +
            G2Helpers.Global("labTestLifetimeStopTitle", "null") +
            G2Helpers.Global("labTestLifetimeBoxShown", "false"));

        // One box at a time, so that MessageBox::Create never times out a box of someone else (MessageBox.cpp:48-120): the loop stops when it finds a
        // box, and M292 closes the own box at once by its sequence number, which starts at 1 (MessageBox.h:66), so S0, which would close the first box
        // of the list (MessageBox.cpp:139), is never sent. M291 S1 is non-blocking and T2 closes a box that M292 missed after 2 s (Gcodes.md:3925-3928,
        // 3942-3943 of the wiki); M292 S closes only the box with that number and does nothing if it has gone (GCodes7.cpp:121-150, Gcodes.md:3987).
        // The conditions and the values with state fields go to RRF (EvaluateExpression and SetVariable), because DSF resolves only SBC fields
        // itself (Expressions.cs:1123-1126, KeywordHandler.cs:261). The time is in RRF milliseconds: state.upTime and state.msUpTime come from the
        // same start time within one expression (RepRap.cpp:361, 381)
        await Lab.UploadAsync(session, WriterMacro, $$"""
            var start = state.upTime
            var seq = 0
            var now = 0
            var last = mod(state.upTime, 100000) * 1000 + state.msUpTime
            while state.upTime - var.start < {{DurationSeconds}}
              if state.messageBox != null
                set global.labTestLifetimeStopTitle = state.messageBox.title
                break
              M291 P"Lab printer test of the object model lifetime lock, closes itself" R"{{BoxTitle}}" S1 T2
              set var.seq = 0
              if state.messageBox != null
                if state.messageBox.title == "{{BoxTitle}}"
                  set var.seq = state.messageBox.seq
              if var.seq != 0
                M292 P0 S{var.seq}
              set global.labTestLifetimeWrites = global.labTestLifetimeWrites + 1
              set var.now = mod(state.upTime, 100000) * 1000 + state.msUpTime
              set global.labTestLifetimeMaxGap = max(global.labTestLifetimeMaxGap, var.now - var.last)
              set var.last = var.now

            """);
    }

    [OneTimeTearDown]
    public async Task DeleteMacrosAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, ResetMacro);
            await Lab.DeleteAsync(session, WriterMacro);
            await Lab.DeleteAsync(session, Directory);
        }
    }

    /// <summary>
    /// Reader on the SBC: for the given time it alternates echo, which DSF passes to RRF as EvaluateExpression (Expressions.cs:1043), and set global,
    /// which goes to RRF as SetVariable (KeywordHandler.cs:279), both with state.messageBox, the object that the writer creates and deletes. It
    /// measures the reply time of every request and counts the echoes that saw a box
    /// </summary>
    private const string ReaderScript = """
        channel, seconds = sys.argv[1], float(sys.argv[2])
        result = {"echoes": 0, "sets": 0, "boxSeen": 0, "maxMs": 0, "errors": []}
        try:
            ipc = Ipc({"mode": "Command"})
            texts = ["echo state.messageBox != null", "set global.labTestLifetimeBoxShown = state.messageBox != null"]
            deadline = time.monotonic() + seconds
            i = 0
            while time.monotonic() < deadline:
                text = texts[i % 2]
                i += 1
                start = time.monotonic()
                code(ipc, text, channel)
                reply = ipc.receive(10)
                result["maxMs"] = max(result["maxMs"], round((time.monotonic() - start) * 1000))
                if reply is None:
                    result["errors"].append("no reply to " + text)
                    break
                if not reply.get("success"):
                    result["errors"].append(outcome(reply))
                    if len(result["errors"]) >= 3:
                        break
                elif text.startswith("echo"):
                    result["echoes"] += 1
                    if "true" in (reply.get("result") or ""):
                        result["boxSeen"] += 1
                else:
                    result["sets"] += 1
            ipc.close()
        except Exception as e:
            result["errors"].append("%s: %s" % (type(e).__name__, e))
        print(json.dumps(result))
        """;

    /// <summary>
    /// RRF fork PR #70 fixed a use-after-free: an SBC expression or assignment that ended at an object model object read it after every lock had
    /// been released, while the main task could delete it (in the PR: M669 during a SetVariable of move.kinematics.forwardMatrix). Its behaviour
    /// change is that the deleting commands, M291 and M292 among them, now wait until a running SBC expression evaluation or assignment has sent its
    /// result, and that these wait while the main task deletes. This test runs both sides at once for 60 s: a writer macro on HTTP shows and closes
    /// test boxes in a loop, and an IPC client on Telnet reads state.messageBox through echo and set global. It checks that neither side stalls
    /// (no gap over 2 s, at least one box and one read per second), that the reader saw boxes, and that RRF neither reset (up time, the software
    /// reset lines of M122) nor damaged its heap ("Heap OK", Heap.cpp:392). The unfixed build passes as well: freeing a box under a reader needs the
    /// reader to be preempted at the wrong moment, which no test can force. So this test guards the lock that PR #70 added, against a deadlock (no
    /// reply, then a watchdog reset) and against a hold that waits for something else (a gap over 2 s), not the use-after-free itself. Each run
    /// gives a few thousand reads that overlap boxes (logged). The HMI shows the flickering test boxes for 60 s. A box of someone else stops the writer,
    /// and the test is then inconclusive
    /// </summary>
    [Test]
    public async Task DeletingCommandsDoNotStallAssignments()
    {
        if (!Lab.Read(Session, model => model.Inputs[ReaderChannel] is not null))
        {
            Assert.Ignore($"{ReaderChannel} is disabled");
        }
        if ((await Lab.RunOnSbcAsync("command -v python3 || true")).Trim().Length == 0)
        {
            Assert.Ignore("The SBC has no python3 for the IPC client");
        }
        if ((await QueryAsync("state.messageBox")).ValueKind != JsonValueKind.Null)
        {
            Assert.Ignore("A message box is shown, so the test does not create its own");
        }

        await SendCodeAsync($"M98 P\"{ResetMacro}\"");
        string resetBefore = SoftwareReset(await SendCodeAsync("M122", 30));
        int upTimeBefore = Lab.Read(Session, model => model.State.UpTime);

        Task<string> writer = SendCodeAsync($"M98 P\"{WriterMacro}\"", DurationSeconds + 60);
        Task<JsonDocument?> reader = G2Helpers.RunIpcScriptAsync(ReaderScript, DurationSeconds + 60, ReaderChannel.ToString(), DurationSeconds.ToString());
        try
        {
            await Task.WhenAll(writer, reader);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or InvalidOperationException)
        {
            string state = $"writer {(writer.IsCompletedSuccessfully ? "done" : "stuck")}, reader {(reader.IsCompletedSuccessfully ? "done" : "stuck")}";
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
            Assert.Fail($"The test macro or the IPC reader did not end ({state}): {e.Message}");
        }

        string writerReply = await writer;
        using JsonDocument readerResult = (await reader)!;
        JsonElement read = readerResult.RootElement;
        JsonElement writes = await QueryAsync("global.labTestLifetimeWrites"), maxGap = await QueryAsync("global.labTestLifetimeMaxGap"), stopTitle = await QueryAsync("global.labTestLifetimeStopTitle");
        string m122 = await SendCodeAsync("M122", 30);
        int upTimeAfter = Lab.Read(Session, model => model.State.UpTime);
        TestContext.Progress.WriteLine($"Writer: {writes} boxes, longest gap {maxGap} ms, reply \"{writerReply.Trim()}\". Reader: {read.GetRawText()}");

        if (stopTitle.ValueKind == JsonValueKind.String)
        {
            // A box of the test that is still there means that M292 missed it
            Assert.That(stopTitle.GetString(), Is.Not.EqualTo(BoxTitle), "M292 did not close a test box");
            Assert.Inconclusive($"The box \"{stopTitle.GetString()}\" of someone else stopped the writer after {writes} boxes");
        }

        Assert.Multiple(() =>
        {
            // The writer ran at least DurationSeconds of RRF up time. Half of it leaves room for the update interval of the object model of DSF
            Assert.That(upTimeAfter, Is.GreaterThanOrEqualTo(upTimeBefore + DurationSeconds / 2), "RRF restarted");
            Assert.That(SoftwareReset(m122), Is.EqualTo(resetBefore), "M122 reports a new software reset");
            Assert.That(m122, Does.Contain("Heap OK"), "M122 finds the string heap damaged");
            Assert.That(writerReply, Does.Not.Contain("Error"), "The writer macro failed");
            Assert.That(read.GetProperty("errors").EnumerateArray().Select(error => error.GetString()), Is.Empty, "The reader got errors");
            Assert.That(writes.ValueKind == JsonValueKind.Number ? writes.GetInt32() : -1, Is.GreaterThanOrEqualTo(MinimumCount), "The writer showed less than one box per second");
            Assert.That(maxGap.ValueKind == JsonValueKind.Number ? maxGap.GetInt32() : -1, Is.InRange(0, MaximumGapMilliseconds - 1), "The writer stalled");
            Assert.That(read.GetProperty("echoes").GetInt32() + read.GetProperty("sets").GetInt32(), Is.GreaterThanOrEqualTo(MinimumCount), "The reader got less than one reply per second");
            Assert.That(read.GetProperty("maxMs").GetInt32(), Is.LessThan(MaximumGapMilliseconds), "A reader request stalled");
            Assert.That(read.GetProperty("boxSeen").GetInt32(), Is.Positive, "The reader never saw a test box, so the two sides did not overlap");
        });
    }

    /// <summary>
    /// The lines of M122 about the last software reset of RRF, as in MacroCloseStress
    /// </summary>
    private static string SoftwareReset(string m122) =>
        string.Join('\n', m122.Split('\n').Select(line => line.Trim()).Where(line =>
            line.StartsWith("Last software reset") || line.StartsWith("Software reset code") || line.StartsWith("Stack:")));
}
