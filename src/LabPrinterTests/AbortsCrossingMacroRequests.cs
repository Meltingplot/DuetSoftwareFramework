using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DuetAPI;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Aborts of a code channel that cross the macro request of an M98 on that channel
/// </summary>
/// <remarks>
/// RRF executes M98, asks DSF for the macro in its next exchange and gives the request up when it reads an InvalidateChannel
/// (GCodes.cpp:3697-3699, SbcInterface.cpp:880-883, GCodeBuffer.cpp:1285-1291). Before #134 DSF still started the macro when the request reached it while
/// its abort was pending or in the same exchange as the InvalidateChannel, RRF warned "Macro file has been started ... but none was requested"
/// (SbcInterface.cpp:852) and the channel waited for the end of that macro forever (T2). The fix discards such a request and logs
/// "was requested before RRF got the abort of all files" at debug level (Processor.DoMacroFile).
/// The window is about one exchange, it cannot be hit on purpose from outside DCS. A Python script on the SBC therefore sends M98 and the IPC command
/// InvalidateChannel with delays spread over the time an M98 takes, as the unit tests TriggerFuzz.ChannelWorksAfterAbortWhileMacroRequestIsUnderway and
/// ChannelWorksAfterAbortCrossingMacroRequest do it with held transfers. RRF 3.7.0+1.mp.8 has a neighbouring window that the fix does not cover (T1): when it
/// reads the InvalidateChannel before it has sent the request, it still sends the request afterwards, because ResolveMacroRequest leaves the requested
/// file set (GCodeBuffer.cpp:1285-1291, GCodeBuffer.h:223) and the non-blocking branch sends it (SbcInterface.cpp:1576-1590). That blocks the channel
/// with the fix as well, so a block here does not tell T1 from T2. The test uses Telnet like MacrosOnOtherChannels, to keep off HTTP (DWC) and SBC,
/// the default channel of IPC clients such as the plugins (DuetAPI/Connection/Defaults.cs)
/// </remarks>
[TestFixture, Category("Restart")]
public partial class AbortsCrossingMacroRequests : LabTest
{
    private const CodeChannel Channel = CodeChannel.Telnet;
    private const string Directory = $"{Lab.MacroDirectory}/abort";
    private const string Macro = $"{Directory}/short.g";

    /// <summary>
    /// Number of aborts, LAB_ABORT_ITERATIONS overrides it
    /// </summary>
    private static int Iterations => int.TryParse(Environment.GetEnvironmentVariable("LAB_ABORT_ITERATIONS"), out int iterations) ? iterations : 600;

    /// <summary>
    /// Message of the fix for a discarded request (Processor.DoMacroFile on fix/macro-start-after-abort)
    /// </summary>
    private const string DiscardMessage = "was requested before RRF got the abort of all files. Discarding request.";

    [OneTimeSetUp]
    public async Task UploadMacroAsync()
    {
        Lab.RequirePrinter();
        await using DuetHttpSession session = await Lab.ConnectAsync();
        await Lab.UploadAsync(session, Macro, "G4 P1\n");
    }

    [OneTimeTearDown]
    public async Task DeleteMacroAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, Macro);
            await Lab.DeleteAsync(session, Directory);
        }
    }

    /// <summary>
    /// IPC client on the SBC. It runs as root, which DCS grants every command (Connection.cs:83-91), and speaks the protocol of ipc.md: one JSON object per
    /// message, DCS finds its end by counting braces (JsonHelper.ReceiveUtf8JsonAsync). Each iteration sends M98 on one connection, waits the delay, sends
    /// InvalidateChannel (DuetAPI/Commands/Generic/InvalidateChannel.cs) on the other and then G4 P0 to see if the channel still works. It stops at the
    /// first channel that does not answer. coreutils timeout ends it if the local side goes away, plink does not stop a remote script
    /// </summary>
    private const string Script = """
        import json, socket, statistics, sys, time

        macro, channel, iterations, pause = sys.argv[1], sys.argv[2], int(sys.argv[3]), 0.05

        class Ipc:
            def __init__(self):
                self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                self.sock.connect("/run/dsf/dcs.sock")
                self.data = b""
                init = self.receive(5)
                self.send({"mode": "Command", "version": init["version"]})
                if not self.receive(5).get("success"):
                    raise RuntimeError("DCS refused the command mode")

            def send(self, message):
                self.sock.sendall(json.dumps(message).encode())

            def receive(self, timeout):
                deadline = time.monotonic() + timeout
                while True:
                    try:
                        text = self.data.decode().lstrip()
                        message, end = json.JSONDecoder().raw_decode(text)
                        self.data = text[end:].encode()
                        return message
                    except ValueError:
                        pass
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        return None
                    self.sock.settimeout(remaining)
                    try:
                        chunk = self.sock.recv(65536)
                    except socket.timeout:
                        return None
                    if not chunk:
                        raise ConnectionError("DCS closed the connection")
                    self.data += chunk

        def code(ipc, text):
            ipc.send({"command": "SimpleCode", "code": text, "channel": channel})

        def outcome(reply):
            if reply is None:
                return "no reply"
            if reply.get("success"):
                return "done" if not reply.get("result") else "reply: " + reply["result"].strip()[:60]
            return reply.get("errorType", "error")

        codes, aborts = Ipc(), Ipc()
        m98 = 'M98 P"%s"' % macro

        # Time of a whole M98 without an abort, the delays are spread over it
        times = []
        for i in range(10):
            start = time.perf_counter()
            code(codes, m98)
            reply = codes.receive(10)
            if outcome(reply) != "done":
                print(json.dumps({"error": "M98 without an abort ended with " + outcome(reply)}))
                sys.exit(0)
            times.append(time.perf_counter() - start)
        span = statistics.median(times) * 1.2

        histogram, blocked, i = {}, None, -1
        for i in range(iterations):
            delay = span * (i % 40) / 39
            code(codes, m98)
            start = time.perf_counter()
            while time.perf_counter() - start < delay:
                pass
            aborts.send({"command": "InvalidateChannel", "channel": channel})
            abort_reply, m98_reply = aborts.receive(5), codes.receive(5)
            probe = None
            if abort_reply is not None and m98_reply is not None:
                code(aborts, "G4 P0")
                probe = aborts.receive(3)
            key = "%.1f ms: %s" % (delay * 1000, outcome(m98_reply))
            histogram[key] = histogram.get(key, 0) + 1
            if probe is None or not probe.get("success"):
                blocked = {"iteration": i, "delayMs": delay * 1000, "abort": outcome(abort_reply), "m98": outcome(m98_reply), "probe": outcome(probe)}
                break
            time.sleep(pause)

        print(json.dumps({"m98Ms": statistics.median(times) * 1000, "iterations": i + 1, "blocked": blocked, "histogram": histogram}))
        """;

    [GeneratedRegex(@"^Current DCS log level: (\w+)", RegexOptions.Multiline)]
    private static partial Regex LogLevelRegex();

    /// <summary>
    /// The channel keeps working after every abort. With the fix, the debug log counts the requests it discarded, which shows that the window was hit.
    /// The log level of DCS is set to debug for the loop (M111 P-1 S, MCodeHandler.cs:691-712) and set back afterwards
    /// </summary>
    [Test, Explicit("Runs about a minute of aborts; a block costs a DCS restart and M999, and RRF 3.7.0+1.mp.8 can block the channel through T1 with the fix as well")]
    public async Task ChannelWorksAfterAbortsCrossingMacroRequests()
    {
        Assert.That(Lab.Read(Session, model => model.Inputs[Channel] is not null), Is.True, $"{Channel} is disabled");
        string levelReply = await SendCodeAsync("M111 P-1");
        Match level = LogLevelRegex().Match(levelReply);
        Assert.That(level.Success, Is.True, $"M111 P-1 did not report the log level: {levelReply}");

        string cursor = await Lab.GetJournalCursorAsync(), output;
        Assert.That(await SendCodeAsync("M111 P-1 S\"debug\""), Does.Not.Contain("Error"));
        try
        {
            output = await Lab.RunOnSbcAsync($$"""
                command -v python3 >/dev/null || { echo '{"error": "python3 is missing"}'; exit 0; }
                sudo -n timeout 300 python3 - '{{Macro}}' '{{Channel}}' {{Iterations}} <<'EOF'
                {{Script}}
                EOF
                """);
        }
        catch (InvalidOperationException e)
        {
            // The script failed or timed out, the channel may be stuck. The restart sets the log level from the configuration again
            await Lab.RecoverAsync();
            Assert.Fail($"The script failed: {e.Message}");
            throw;
        }
        finally
        {
            try
            {
                await SendCodeAsync($"M111 P-1 S\"{level.Groups[1].Value}\"");
            }
            catch (Exception e)
            {
                // DCS has been restarted, which sets the level from the configuration anyway
                TestContext.Progress.WriteLine($"Could not set the log level back: {e.Message}");
            }
        }

        string journal = await Lab.ReadJournalAsync(cursor);
        int discarded = journal.Split('\n').Count(line => line.Contains(DiscardMessage));
        int notRequested = journal.Split('\n').Count(line => line.Contains("but none was requested"));
        TestContext.Progress.WriteLine($"Script: {output.Trim()}");
        TestContext.Progress.WriteLine($"DCS discarded {discarded} crossing requests; the journal has {notRequested} lines about a macro RRF did not request");

        using JsonDocument result = JsonDocument.Parse(output.Trim().Split('\n').Last());
        string? error = result.RootElement.TryGetProperty("error", out JsonElement errorElement) ? errorElement.GetString() : null;
        if (error == "python3 is missing")
        {
            Assert.Ignore("The SBC has no python3 for the IPC client");
        }
        bool blocked = result.RootElement.TryGetProperty("blocked", out JsonElement block) && block.ValueKind != JsonValueKind.Null;
        if (blocked || error is not null)
        {
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
        }
        if (error is not null)
        {
            Assert.Fail($"The script did not run: {error}");
        }
        if (blocked)
        {
            Assert.Fail($"{Channel} was blocked after an abort ({block}), DCS discarded {discarded} crossing requests before. " +
                "Without #134 this is T2. With it, it can be T1 (RRF sends a request it gave up, SbcInterface.cpp:880-883 and 1576-1590), which #134 does not cover");
        }
        if (discarded == 0)
        {
            Assert.Inconclusive($"No abort crossed a macro request in {result.RootElement.GetProperty("iterations")} iterations, or DCS has no fix that reports it");
        }
    }
}
