using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Helpers of the tests for macros, file info and triggers: test job files and IPC clients on the SBC
/// </summary>
public static class G2Helpers
{
    /// <summary>
    /// Directory on the printer that holds the test job files. They are only read with M36 and never printed
    /// </summary>
    public const string GCodeDirectory = "0:/gcodes/lab-tests";

    /// <summary>
    /// First line of every test job file, so that a person who sees one in the job list knows what it is
    /// </summary>
    /// <remarks>
    /// Neither a filter of FileInfoParser (Settings.cs:321-413, the same as pkg/common/duetcontrolserver/opt/dsf/conf/config.json:59-114, which the CHX
    /// image does not change: mp-dsf-configure patches only PluginSupport and RootPluginSupport) nor the customInfo pattern (FileInfoParser.cs:552) or a
    /// thumbnail start (FileInfoParser.cs:712-732) matches it
    /// </remarks>
    public const string JobFileHeader = "; lab printer test file, never printed, deleted again\n";

    /// <summary>
    /// Create a global variable, or set it if an earlier run has left it, since globals stay until the next reset
    /// </summary>
    public static string Global(string name, string value) => $"""
        if !exists(global.{name})
          global {name} = {value}
        else
          set global.{name} = {value}

        """;

    /// <summary>
    /// Upload a test job file to <see cref="GCodeDirectory"/>, read its file info with M36 and delete it again
    /// </summary>
    /// <returns>Reply of M36</returns>
    /// <exception cref="OperationCanceledException">M36 got no reply in time</exception>
    /// <exception cref="HttpRequestException">M36 failed, e.g. because DCS has stopped</exception>
    public static async Task<string> GetFileInfoAsync(DuetHttpSession session, string name, string content, int timeoutSeconds = 10)
    {
        string fileName = $"{GCodeDirectory}/{name}";
        await Lab.UploadAsync(session, fileName, content);
        try
        {
            return await Lab.SendCodeAsync(session, $"M36 \"{fileName}\"", TimeSpan.FromSeconds(timeoutSeconds));
        }
        finally
        {
            await DeleteJobFileAsync(fileName);
        }
    }

    /// <summary>
    /// Delete a test job file and <see cref="GCodeDirectory"/> once it is empty. A new session, because DCS may have stopped meanwhile.
    /// A failure is only logged, so that it does not hide the result of the test
    /// </summary>
    public static async Task DeleteJobFileAsync(string fileName)
    {
        try
        {
            await using DuetHttpSession session = await Lab.ReconnectAsync(TimeSpan.FromMinutes(2));
            await Lab.DeleteAsync(session, fileName);

            // The delete of the HTTP API is not recursive (RestConnector.DeleteAsync), so a file of another test keeps the directory
            if ((await session.GetFileListAsync(GCodeDirectory)).Count == 0)
            {
                await Lab.DeleteAsync(session, GCodeDirectory);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone
        }
        catch (Exception e)
        {
            TestContext.Progress.WriteLine($"Could not delete {fileName}: {e.Message}");
        }
    }

    /// <summary>
    /// Parse the JSON reply of M36, which DSF writes as {"err":0,...} (MCodeHandler.cs:483-519)
    /// </summary>
    public static JsonDocument ParseFileInfo(string reply)
    {
        try
        {
            return JsonDocument.Parse(reply);
        }
        catch (JsonException e)
        {
            Assert.Fail($"M36 did not reply with JSON ({e.Message}): {reply}");
            throw;
        }
    }

    /// <summary>
    /// Python IPC client for the SBC. It runs as root, which DCS grants every command (Connection.cs:83-91), and speaks the protocol of ipc.md: one
    /// JSON object per message, which DCS ends by its braces (JsonHelper.ReceiveUtf8JsonAsync). The server sends its init message with the API version,
    /// the client answers with its mode and gets {"success":true} (IPC/Server.cs:155-167). Taken from AbortsCrossingMacroRequests, with the init
    /// message as a parameter so that it can open an interceptor too
    /// </summary>
    private const string IpcClient = """
        import json, socket, sys, time

        class Ipc:
            def __init__(self, init):
                self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                self.sock.connect("/run/dsf/dcs.sock")
                self.data = b""
                server = self.receive(5)
                if server is None:
                    raise RuntimeError("DCS sent no init message")
                init = dict(init, version=server["version"])
                self.send(init)
                reply = self.receive(5)
                if reply is None or not reply.get("success"):
                    raise RuntimeError("DCS refused the %s connection: %s" % (init["mode"], reply))

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

            def close(self):
                self.sock.close()

        def code(ipc, text, channel):
            ipc.send({"command": "SimpleCode", "code": text, "channel": channel})

        def outcome(reply):
            if reply is None:
                return "no reply"
            if reply.get("success"):
                # Telnet answers every code with "ok" (Marlin-style), which is no reply of the code
                result = (reply.get("result") or "").strip()
                return "done" if result in ("", "ok") else "reply: " + result[:200]
            return "%s: %s" % (reply.get("errorType", "error"), reply.get("errorMessage", ""))

        """;

    /// <summary>
    /// Run a Python script that uses <see cref="IpcClient"/> as root on the SBC. coreutils timeout ends it if the local side goes away, because plink
    /// does not stop a remote script
    /// </summary>
    /// <param name="script">Script, which prints one JSON object as its last line</param>
    /// <param name="timeoutSeconds">Time after which the script is killed</param>
    /// <param name="args">Arguments of the script, in sys.argv[1:]</param>
    /// <returns>The JSON object, or null if the SBC has no python3</returns>
    /// <exception cref="InvalidOperationException">The script failed or was killed</exception>
    public static async Task<JsonDocument?> RunIpcScriptAsync(string script, int timeoutSeconds, params string[] args)
    {
        string arguments = string.Join(' ', args.Select(arg => "'" + arg.Replace("'", "'\\''") + "'"));
        string output = await Lab.RunOnSbcAsync($$"""
            command -v python3 >/dev/null || { echo '{"error": "python3 is missing"}'; exit 0; }
            sudo -n timeout {{timeoutSeconds}} python3 - {{arguments}} <<'EOF'
            {{IpcClient}}
            {{script}}
            EOF
            """);
        TestContext.Progress.WriteLine($"IPC script: {output.Trim()}");

        JsonDocument result = JsonDocument.Parse(output.Trim().Split('\n').Last());
        if (result.RootElement.TryGetProperty("error", out JsonElement error) && error.GetString() == "python3 is missing")
        {
            result.Dispose();
            return null;
        }
        return result;
    }
}
