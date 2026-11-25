using System;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// IPC commands of the command mode that plugins and tools on the SBC use
/// </summary>
[TestFixture]
public class IpcCommands : LabTest
{
    /// <summary>
    /// Channel to abort. It is idle, and DSF has no Telnet server, so only IPC clients use it
    /// </summary>
    private const CodeChannel Channel = CodeChannel.Telnet;

    /// <summary>
    /// One InvalidateChannel (DuetAPI/Commands/Generic/InvalidateChannel.cs) and then G4 P0 on the same channel to see that it still works
    /// </summary>
    private const string Script = """
        ipc = Ipc()
        abort = ipc.request({"command": "InvalidateChannel", "channel": ARGS["channel"]}, 20)
        probe = ipc.request({"command": "SimpleCode", "code": "G4 P0", "channel": ARGS["channel"]}, 10)
        finish({"abort": abort, "probe": probe})
        """;

    /// <summary>
    /// The IPC command InvalidateChannel is accepted and aborts an idle channel, which then runs the next code.
    /// Without #158 the command mode did not list InvalidateChannel (IPC/Processors/Command.cs:30 now), so DCS refused it before it ran with
    /// <c>ArgumentException</c> "unsupported command InvalidateChannel" (IPC/Connection.cs:654-657) and plugins and tools could not abort a channel (I8).
    /// Upstream still refuses it. AbortsCrossingMacroRequests catches the same refusal, but it is explicit and runs a minute of aborts.
    /// The command waits until RRF has the abort (Commands/Generic/InvalidateChannel.cs:36). Deterministic, one run catches it
    /// </summary>
    [Test]
    public async Task InvalidateChannelIsAccepted()
    {
        if (Lab.Read(Session, model => model.Inputs[Channel] is null))
        {
            Assert.Ignore($"{Channel} is disabled");
        }

        string pid = await Lab.GetControlServerPidAsync();
        JsonElement result;
        try
        {
            result = await G3Helpers.RunPythonAsync(Script, new { channel = Channel.ToString() }, 60);
        }
        catch (InvalidOperationException e)
        {
            await Lab.RecoverAsync();
            Assert.Fail($"The script failed: {e.Message}");
            throw;
        }
        TestContext.Progress.WriteLine($"Script: {result}");

        JsonElement abort = result.GetProperty("abort"), probe = result.GetProperty("probe");
        string newPid = await Lab.GetControlServerPidAsync();
        if (!G3Helpers.IsSuccess(probe))
        {
            // The channel did not answer after the abort, so it may be stuck
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
        }
        Assert.Multiple(() =>
        {
            Assert.That(G3Helpers.IsSuccess(abort), Is.True,
                $"DCS refused InvalidateChannel: {G3Helpers.GetString(abort, "errorType")} '{G3Helpers.GetString(abort, "errorMessage")}' ({abort})");
            Assert.That(G3Helpers.IsSuccess(probe), Is.True, $"G4 P0 on {Channel} after the abort: {probe}");
            Assert.That(newPid, Is.EqualTo(pid), "DCS was restarted");
        });
    }
}
