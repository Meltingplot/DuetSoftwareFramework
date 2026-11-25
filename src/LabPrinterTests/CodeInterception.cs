using System;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// IPC clients that intercept codes
/// </summary>
/// <remarks>
/// The clients run as root on the SBC (<see cref="G2Helpers.RunIpcScriptAsync"/>). They use Telnet like AbortsCrossingMacroRequests, to keep off HTTP
/// (DWC) and SBC, the default channel of IPC clients such as the plugins, and intercept only G4
/// </remarks>
[TestFixture]
public class CodeInterception : LabTest
{
    private const CodeChannel Channel = CodeChannel.Telnet;

    /// <summary>
    /// Opens an Executed interceptor for G4 with AutoFlush, sends G4 P0 from a command connection, ignores the intercepted code and waits for its
    /// reply. Then it closes the interceptor and checks with a new command connection that the channel still works. The init message has the keys of
    /// InterceptInitMessage in camel case, as the unit test IpcInterception.UnsupportedCommandEndsTheInterception sends them; DCS sends the
    /// intercepted code as {"command":"Code",...,"majorNumber":4,...} (IpcInterception.cs:374-376) and takes Ignore as {"command":"Ignore"}
    /// </summary>
    private const string Script = """
        channel = sys.argv[1]
        result = {}
        try:
            interceptor = Ipc({"mode": "Intercept", "interceptionMode": "Executed", "channels": [channel], "filters": ["G4"], "autoFlush": True})
            command = Ipc({"mode": "Command"})
            code(command, "G4 P0", channel)
            intercepted = interceptor.receive(5)
            result["intercepted"] = None if intercepted is None else intercepted.get("majorNumber")
            if intercepted is not None:
                interceptor.send({"command": "Ignore"})
            result["reply"] = outcome(command.receive(5))
            interceptor.close()
            command.close()

            # Without the fix the channel stays blocked after the interceptor has gone
            time.sleep(0.5)
            after = Ipc({"mode": "Command"})
            code(after, "G4 P0", channel)
            result["after"] = outcome(after.receive(5))
            after.close()
        except Exception as e:
            result["error"] = "%s: %s" % (type(e).__name__, e)
        print(json.dumps(result))
        """;

    /// <summary>
    /// An Executed interceptor with a filter and AutoFlush, the default of DuetAPIClient, never got its code, the code never got a reply, and its
    /// channel stayed blocked until DCS was restarted, also after the client had gone (I3 of design/open-findings.md, seen on the lab printer on
    /// 2026-11-07 with DSF 3.7.0+mp.13). CodeInterception.InterceptAsync flushes before it passes the code on (IPC/Processors/CodeInterception.cs:263,
    /// AutoFlush counts only with a filter, :145), and ChannelProcessor.FlushAsync waited for the Executed stage as well, which was busy with this very
    /// code. Fixed in DSF #181: the flush waits only for the stages after the one of the code (Codes/ChannelProcessor.cs:288). Unfixed, the
    /// interceptor gets nothing, both G4 P0 get no reply, and the test restarts DCS and resets RRF before it fails; the unit tests
    /// IpcInterception.ExecutedCodesWithAutoFlush and IpcInterceptionStages.ChannelWorksAfterAnExecutedInterceptorWithAutoFlushHasGone failed
    /// without the change (PR #181). Deterministic. PR #181 was not run on the lab printer before
    /// </summary>
    [Test]
    public async Task ExecutedInterceptorWithAutoFlushGetsItsCode()
    {
        if (!Lab.Read(Session, model => model.Inputs[Channel] is not null))
        {
            Assert.Ignore($"{Channel} is disabled");
        }

        JsonDocument? result;
        try
        {
            result = await G2Helpers.RunIpcScriptAsync(Script, 60, Channel.ToString());
        }
        catch (InvalidOperationException e)
        {
            // The script was killed or failed, the channel may be blocked
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
            Assert.Fail($"The IPC script failed: {e.Message}");
            throw;
        }
        if (result is null)
        {
            Assert.Ignore("The SBC has no python3 for the IPC client");
        }

        using (result)
        {
            JsonElement root = result.RootElement;
            string? error = root.TryGetProperty("error", out JsonElement errorElement) ? errorElement.GetString() : null;
            string? after = root.TryGetProperty("after", out JsonElement afterElement) ? afterElement.GetString() : null;
            if (after != "done")
            {
                // A blocked channel only works again after a restart of DCS
                await Lab.RecoverAsync();
                await ReconnectSessionAsync();
                Assert.Fail((error is not null) ? $"The IPC script did not run: {error}" : $"{Channel} is blocked after the interceptor has gone: {root.GetRawText()}");
            }

            Assert.Multiple(() =>
            {
                Assert.That(root.GetProperty("intercepted").ValueKind, Is.EqualTo(JsonValueKind.Number), "The interceptor did not get G4 P0");
                Assert.That(root.GetProperty("reply").GetString(), Is.EqualTo("done"), "The intercepted G4 P0 got no reply");
            });
        }
    }
}
