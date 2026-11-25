using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// File positions and lengths of the codes DSF reads from a macro, as an IPC code interceptor gets them
/// </summary>
/// <remarks>
/// <c>Code.Length</c> is the number of bytes a code takes in its file. JobProcessor stores <c>FilePosition + Length</c> as the position after a code
/// (JobProcessor.cs:566) and pauses there when RRF reports no position (JobProcessor.cs:685), so a length one byte too long makes a resumed job start one
/// byte into the next code. That symptom needs a paused job, which is outside what the tests may do, so the test checks the cause: macro files are read
/// with the same parser and buffer as jobs (CodeFile.cs:74, :410), and an interceptor gets <c>filePosition</c> and <c>length</c> of each code
/// (Code.cs:122, :127, serialized in camel case, CommandContext.cs:57). Every code passes the Pre stage (Start.cs:60, Pre.cs:30), and a code read from a
/// macro has the flag IsFromMacro (MacroFile.cs:347, CodeFlags.cs:46)
/// </remarks>
[TestFixture]
public class CodeLengths : LabTest
{
    /// <summary>
    /// Channel of the macro. DSF has no Telnet server, so only IPC clients reach it, and the interceptor sees no codes of DWC or the HMI
    /// </summary>
    private const CodeChannel Channel = CodeChannel.Telnet;

    private const string Directory = $"{Lab.MacroDirectory}/code-lengths";
    private const string Macro = $"{Directory}/lengths.g";

    /// <summary>
    /// Two codes on one line and a last code without a final NL. G4 P0 (dwell 0 ms) and M400 (wait for moves) do nothing on an idle machine.
    /// The second G starts a new code because the first one is complete (Parser.cs:728-730), as G1 X1 G0 Y2 in the unit tests
    /// </summary>
    private const string MacroContent = "G4 P0 G4 P0\nM400";

    /// <summary>
    /// File positions and lengths the fixed parser gives, as UnitTests/Commands/CodeLength.cs FollowingCodeAsync (G1 X1 G0 Y2) and EndOfFileAsync
    /// ("G1 X1" without NL, length 5)
    /// </summary>
    private static readonly (long Position, int Length)[] Expected = [(0, 6), (6, 6), (12, 4)];

    /// <summary>
    /// A code interceptor in Pre mode for one channel (DuetAPI/Connection/InitMessages/InterceptInitMessage.cs) that answers every code with Ignore,
    /// so each code runs as without it (CodeInterception.cs:310), and a command connection that sends the M98. Codes of the interceptor's own
    /// connection are not intercepted (CodeInterception.cs:226), hence the second connection. The interceptor runs in a thread, so it answers while the
    /// M98 waits for its macro
    /// </summary>
    private const string Script = """
        class Interceptor(threading.Thread):
            def __init__(self, channel):
                super().__init__(daemon=True)
                self.ipc = Ipc("Intercept", interceptionMode="Pre", channels=[channel], filters=[], priorityCodes=False)
                self.codes, self.error, self.stopping = [], None, False

            def run(self):
                try:
                    while not self.stopping:
                        code = self.ipc.receive(0.2)
                        if code is None:
                            continue
                        self.codes.append({key: code.get(key) for key in ("type", "majorNumber", "flags", "filePosition", "length")})
                        self.ipc.send({"command": "Ignore"})
                except Exception as e:
                    if not self.stopping:
                        self.error = repr(e)

        interceptor = Interceptor(ARGS["channel"])
        interceptor.start()
        time.sleep(0.5)
        codes = Ipc()
        reply = codes.request({"command": "SimpleCode", "code": 'M98 P"%s"' % ARGS["macro"], "channel": ARGS["channel"]}, 30)
        time.sleep(0.5)
        interceptor.stopping = True
        interceptor.ipc.close()
        interceptor.join(2)
        finish({"reply": reply, "codes": interceptor.codes, "error": interceptor.error})
        """;

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
    /// The interceptor gets the three codes of the macro at positions 0, 6 and 12 with lengths 6, 6 and 4.
    /// Without #86 the async parser counted the character that starts the next code and then put it back (#86a), and counted the end of the stream as a
    /// NL (#86b): the first G4 P0 had length 7 and M400 length 5, the positions were the same (PR #86, ParserAsync.cs:143-157 and :165-177 now).
    /// Upstream still has both. Deterministic, one run catches it
    /// </summary>
    [Test]
    public async Task MacroCodePositionsAndLengths()
    {
        if (Lab.Read(Session, model => model.Inputs[Channel] is null))
        {
            Assert.Ignore($"{Channel} is disabled");
        }
        await Lab.UploadAsync(Session, Macro, MacroContent);

        JsonElement result;
        try
        {
            result = await G3Helpers.RunPythonAsync(Script, new { channel = Channel.ToString(), macro = Macro }, 90);
        }
        catch (InvalidOperationException e)
        {
            // The script failed or timed out, the channel may wait for an interception that never ends
            await Lab.RecoverAsync();
            Assert.Fail($"The script failed: {e.Message}");
            throw;
        }
        TestContext.Progress.WriteLine($"Script: {result}");

        JsonElement reply = result.GetProperty("reply");
        if (!G3Helpers.IsSuccess(reply))
        {
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
            Assert.Fail($"M98 on {Channel} did not complete: {reply}");
        }

        List<(long Position, int Length)> macroCodes = [.. result.GetProperty("codes").EnumerateArray()
            .Where(code => (code.GetProperty("flags").GetInt32() & (int)CodeFlags.IsFromMacro) != 0)
            .Select(code => (code.GetProperty("filePosition").GetInt64(), code.GetProperty("length").GetInt32()))];
        Assert.Multiple(() =>
        {
            Assert.That(G3Helpers.GetString(result, "error"), Is.Null, "The interceptor failed");
            Assert.That(macroCodes, Is.EqualTo(Expected), $"(filePosition, length) of the macro codes of \"{MacroContent.Replace("\n", "\\n")}\"");
        });
    }
}
