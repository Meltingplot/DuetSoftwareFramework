using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using DuetAPI;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Helpers of the IPC, journal, packaging and web server tests: IPC clients on the SBC, journal entries with their priority, the versions of the
/// installed packages and the build of the running DuetWebServer
/// </summary>
public static class G3Helpers
{
    /// <summary>
    /// Last fork release that has none of the DWS, DPS and packaging fixes #222, #225, #229 and #234: `git tag --contains` lists no tag for their
    /// merge commits bd051f9a6, 9eebc5180, e4e865a7c and 520765373. v3.7-dev builds report this version too until the next version bump
    /// (src/Directory.Build.props), so a package with exactly this version may have them or not
    /// </summary>
    public const string LastReleaseBeforeNewFixes = "3.7.0+mp.15";

    /// <summary>
    /// Python prelude of every script run by <see cref="RunPythonAsync"/>. ARGS holds the arguments of the test, Ipc is a client of the protocol of
    /// ipc.md as in AbortsCrossingMacroRequests: one JSON object per message, DCS finds its end by counting braces (JsonHelper.ReceiveUtf8JsonAsync).
    /// DCS sends a server init message with its protocol version, the client answers with its mode and gets a success response
    /// (IPC/Server.cs GetConnectionProcessorAsync). Errors come back as {"success": false, "errorType": ..., "errorMessage": ...}
    /// (DuetAPI/Commands/Responses.cs ErrorResponse)
    /// </summary>
    private const string PythonPrelude = """
        import base64, grp, json, os, pwd, socket, subprocess, sys, threading, time

        ARGS = json.loads(base64.b64decode(sys.argv[1]))

        class Ipc:
            def __init__(self, mode="Command", **init):
                self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                self.sock.connect("/run/dsf/dcs.sock")
                self.data = b""
                server = self.receive(5)
                if server is None:
                    raise RuntimeError("DCS sent no init message")
                message = {"mode": mode, "version": server["version"]}
                message.update(init)
                self.send(message)
                reply = self.receive(5)
                if reply is None or not reply.get("success"):
                    raise RuntimeError("DCS refused the %s mode: %s" % (mode, reply))

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

            def request(self, message, timeout=10):
                self.send(message)
                return self.receive(timeout)

            def close(self):
                try:
                    self.sock.close()
                except OSError:
                    pass

        def finish(result):
            print(json.dumps(result))
            sys.stdout.flush()
            sys.exit(0)

        """;

    /// <summary>
    /// Run a Python script as root on the SBC, as AbortsCrossingMacroRequests does for its IPC client. Root gets every IPC command
    /// (IPC/Connection.cs:84-91). The script gets <see cref="PythonPrelude"/> and must print its result as one JSON line at the end, e.g. with finish().
    /// coreutils timeout ends it if the local side goes away, plink does not stop a remote script
    /// </summary>
    /// <param name="script">Python code</param>
    /// <param name="arguments">Arguments, serialized as JSON into ARGS</param>
    /// <param name="timeoutSeconds">Time after which timeout kills the script</param>
    /// <returns>Last line of the output as JSON</returns>
    /// <exception cref="InvalidOperationException">The script failed or timed out</exception>
    public static async Task<JsonElement> RunPythonAsync(string script, object arguments, int timeoutSeconds)
    {
        string encodedArguments = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(arguments));
        string output = await Lab.RunOnSbcAsync($$"""
            command -v python3 >/dev/null || { echo '{"error": "python3 is missing"}'; exit 0; }
            sudo -n timeout {{timeoutSeconds}} python3 - '{{encodedArguments}}' <<'LABPY'
            {{PythonPrelude}}
            {{script}}
            LABPY
            """);

        string? lastLine = output.Trim().Split('\n').LastOrDefault();
        if (string.IsNullOrWhiteSpace(lastLine))
        {
            throw new InvalidOperationException("The script printed no result");
        }
        using JsonDocument json = JsonDocument.Parse(lastLine);
        JsonElement result = json.RootElement.Clone();
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String &&
            error.GetString() == "python3 is missing")
        {
            Assert.Ignore("The SBC has no python3 for the IPC client");
        }
        return result;
    }

    /// <summary>
    /// Whether an IPC response is a success response
    /// </summary>
    public static bool IsSuccess(JsonElement response) =>
        response.ValueKind == JsonValueKind.Object && response.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Get a string property of a JSON object or null
    /// </summary>
    public static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Run a code on a channel that no HTTP client uses, e.g. Telnet, through a root IPC client with the IPC command SimpleCode
    /// (DuetAPI/Commands/Generic/SimpleCode.cs). DSF has no Telnet server, so only IPC clients reach that channel
    /// </summary>
    /// <returns>IPC response, null if DCS did not answer in time</returns>
    public static async Task<JsonElement> RunCodeOnChannelAsync(string code, CodeChannel channel, int timeoutSeconds = 20)
    {
        JsonElement result = await RunPythonAsync("""
            ipc = Ipc()
            finish({"reply": ipc.request({"command": "SimpleCode", "code": ARGS["code"], "channel": ARGS["channel"]}, ARGS["timeout"])})
            """, new { code, channel = channel.ToString(), timeout = timeoutSeconds }, timeoutSeconds + 30);
        return result.GetProperty("reply");
    }

    /// <summary>
    /// What is stored under a virtual path: DCS resolves it with the IPC command ResolvePath (DuetAPI/Commands/Files/ResolvePath.cs), and the script
    /// reads the file and the ".part" file that PUT /machine/file and, since #222, rr_upload write first
    /// </summary>
    /// <returns>Physical path, the file and the .part file, each null if missing</returns>
    public static async Task<(string Path, StoredFile? File, StoredFile? Part)> GetStoredFileAsync(string virtualPath)
    {
        JsonElement result = await RunPythonAsync("""
            ipc = Ipc()
            resolved = ipc.request({"command": "ResolvePath", "path": ARGS["path"]}, 10)
            if resolved is None or not resolved.get("success"):
                raise RuntimeError("ResolvePath failed: %s" % resolved)
            path = resolved["result"]

            def read(name):
                try:
                    st = os.lstat(name)
                    with open(name, "rb") as f:
                        content = f.read(65536)
                    return {"inode": st.st_ino, "size": st.st_size, "content": content.decode("utf-8", "replace")}
                except FileNotFoundError:
                    return None

            finish({"path": path, "file": read(path), "part": read(path + ".part")})
            """, new { path = virtualPath }, 60);

        static StoredFile? Read(JsonElement element) => element.ValueKind == JsonValueKind.Object
            ? new StoredFile(element.GetProperty("inode").GetInt64(), element.GetProperty("size").GetInt64(), element.GetProperty("content").GetString()!)
            : null;
        return (result.GetProperty("path").GetString()!, Read(result.GetProperty("file")), Read(result.GetProperty("part")));
    }

    /// <summary>
    /// A file on the SBC
    /// </summary>
    public sealed record StoredFile(long Inode, long Size, string Content);

    /// <summary>
    /// Remove an HTTP endpoint of a test again, e.g. after its script was killed. DCS keeps an endpoint in the object model until RemoveHttpEndpoint
    /// (Commands/HttpEndpoints/RemoveHttpEndpoint.cs), it does not watch the connection that added it. The socket file goes too
    /// </summary>
    public static Task RemoveHttpEndpointAsync(string ns, string path) => RunPythonAsync("""
        ipc = Ipc()
        removed = ipc.request({"command": "RemoveHttpEndpoint", "endpointType": "GET", "namespace": ARGS["namespace"], "path": ARGS["path"]}, 10)
        try:
            os.unlink("/run/dsf/%s/%s-GET.sock" % (ARGS["namespace"], ARGS["path"]))
        except OSError:
            pass
        finish({"removed": removed})
        """, new { @namespace = ns, path }, 60);

    /// <summary>
    /// An entry of the journal with the syslog priority journald stored for it
    /// </summary>
    /// <param name="Priority">PRIORITY field, -1 if missing</param>
    /// <param name="Message">MESSAGE field</param>
    /// <param name="Binary">Whether journalctl gave MESSAGE as an array of bytes, which it does for a message with control characters such as ESC
    /// (journalctl(1), output mode json)</param>
    public sealed record JournalEntry(int Priority, string Message, bool Binary);

    /// <summary>
    /// Read the entries of a unit since a cursor with their priority (journalctl -o json)
    /// </summary>
    public static async Task<List<JournalEntry>> ReadJournalEntriesAsync(string cursor, string unit)
    {
        string output = await Lab.RunOnSbcAsync($"sudo -n journalctl -u {unit} --after-cursor='{cursor}' -o json --all --output-fields=MESSAGE,PRIORITY --no-pager");
        List<JournalEntry> entries = [];
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using JsonDocument json = JsonDocument.Parse(line);
            int priority = json.RootElement.TryGetProperty("PRIORITY", out JsonElement priorityElement) && int.TryParse(priorityElement.GetString(), out int value) ? value : -1;
            if (!json.RootElement.TryGetProperty("MESSAGE", out JsonElement message))
            {
                continue;
            }
            if (message.ValueKind == JsonValueKind.Array)
            {
                byte[] bytes = [.. message.EnumerateArray().Select(item => (byte)item.GetInt32())];
                entries.Add(new JournalEntry(priority, Encoding.UTF8.GetString(bytes), true));
            }
            else if (message.ValueKind == JsonValueKind.String)
            {
                entries.Add(new JournalEntry(priority, message.GetString()!, false));
            }
        }
        return entries;
    }

    /// <summary>
    /// Compare the version of an installed package with dpkg --compare-versions. Package versions are the DSF version with '-' replaced by '~'
    /// (pkg/deb/build.sh:25, pkg/common/common.functions:75)
    /// </summary>
    /// <param name="package">Package name</param>
    /// <param name="relation">Relation of dpkg --compare-versions, e.g. gt or lt</param>
    /// <param name="version">Version to compare with</param>
    /// <returns>Installed version, null if the package is not installed, and whether the relation holds</returns>
    public static async Task<(string? Version, bool Holds)> ComparePackageVersionAsync(string package, string relation, string version)
    {
        string output = (await Lab.RunOnSbcAsync($$"""
            v=$(dpkg-query -W -f '${Version}' {{package}} 2>/dev/null || true)
            if [ -z "$v" ]; then echo none
            elif dpkg --compare-versions "$v" {{relation}} '{{version}}'; then echo "yes $v"
            else echo "no $v"
            fi
            """)).Trim();
        return output == "none" ? (null, false) : (output[(output.IndexOf(' ') + 1)..], output.StartsWith("yes "));
    }

    /// <summary>
    /// The DuetWebServer that serves the tests
    /// </summary>
    /// <param name="PackageVersion">Version of the installed duetwebserver package</param>
    /// <param name="PackageIsNewer">Whether that version is newer than <see cref="LastReleaseBeforeNewFixes"/></param>
    /// <param name="SideLoaded">Whether /opt/dsf/bin/DuetWebServer.dll is a bind mount, i.e. a side-load of the tree the tests come from</param>
    /// <param name="RunsMountedFile">Whether the running DWS has mapped the file that is at that path now (same device and inode in
    /// /proc/PID/maps), so it was started after the side-load. Null if DWS is not running or maps no DuetWebServer.dll</param>
    public sealed record WebServerBuild(string? PackageVersion, bool PackageIsNewer, bool SideLoaded, bool? RunsMountedFile)
    {
        /// <summary>
        /// Whether the running DWS is known to have the DWS fixes after <see cref="LastReleaseBeforeNewFixes"/>. A DWS that runs the file at the path
        /// has them if that file is the side-load or a newer package. A DWS that runs another file runs the package file under a side-load, or a
        /// file that was replaced after it started, whose version is unknown. Without a mapping (not running, or no separate DLL) only a newer package tells
        /// </summary>
        public bool HasNewFixes => RunsMountedFile switch
        {
            true => SideLoaded || PackageIsNewer,
            false => SideLoaded && PackageIsNewer,
            null => PackageIsNewer
        };
    }

    /// <summary>
    /// Find out which DuetWebServer runs. run-lab-tests.py --side-load bind-mounts DuetWebServer.dll and DuetAPIClient.dll next to the DCS
    /// assemblies and restarts DWS (SIDE_LOAD_WEB_DLLS, run-lab-tests.py:38)
    /// </summary>
    public static async Task<WebServerBuild> GetWebServerBuildAsync()
    {
        JsonElement result = await RunPythonAsync("""
            def run(*command):
                return subprocess.run(command, capture_output=True, text=True).stdout.strip()

            version = run("dpkg-query", "-W", "-f", "${Version}", "duetwebserver")
            newer = bool(version) and subprocess.run(["dpkg", "--compare-versions", version, "gt", ARGS["release"]]).returncode == 0
            dll = "/opt/dsf/bin/DuetWebServer.dll"
            mounted = subprocess.run(["findmnt", "-n", dll], capture_output=True).returncode == 0
            pid = int(run("systemctl", "show", "-p", "MainPID", "--value", "duetwebserver") or 0)
            runs = None
            if pid > 0 and os.path.exists(dll):
                st = os.stat(dll)
                device = "%02x:%02x" % (os.major(st.st_dev), os.minor(st.st_dev))
                with open("/proc/%d/maps" % pid) as maps:
                    for line in maps:
                        fields = line.split()
                        if len(fields) >= 6 and fields[5] == dll:
                            runs = fields[3] == device and int(fields[4]) == st.st_ino
                            break
            finish({"version": version or None, "newer": newer, "sideLoaded": mounted, "runsMountedFile": runs})
            """, new { release = LastReleaseBeforeNewFixes }, 60);

        JsonElement runs = result.GetProperty("runsMountedFile");
        return new WebServerBuild(GetString(result, "version"), result.GetProperty("newer").GetBoolean(), result.GetProperty("sideLoaded").GetBoolean(),
            runs.ValueKind == JsonValueKind.Null ? null : runs.GetBoolean());
    }

    /// <summary>
    /// Skip a test of a DWS fix after <see cref="LastReleaseBeforeNewFixes"/> unless the running DWS is known to have it: either it was side-loaded
    /// from the tree the tests come from and started from that file, or the installed package is newer than that release
    /// </summary>
    /// <param name="fix">Fix, e.g. "#222"</param>
    public static async Task RequireWebServerWithNewFixesAsync(string fix)
    {
        WebServerBuild build = await GetWebServerBuildAsync();
        TestContext.Progress.WriteLine($"DuetWebServer: package {build.PackageVersion ?? "not installed"}, side-loaded {build.SideLoaded}, runs the mounted file {build.RunsMountedFile?.ToString() ?? "unknown"}");
        if (!build.HasNewFixes)
        {
            Assert.Ignore($"Cannot tell whether the running DuetWebServer has {fix}: it is not side-loaded from this tree and the duetwebserver package is " +
                $"{build.PackageVersion ?? "not installed"}. {fix} is in no release up to {LastReleaseBeforeNewFixes}, and v3.7-dev builds report that version too");
        }
    }

    /// <summary>
    /// CRC-32 as DWS checks it for rr_upload (DuetWebServer/Utility/CRC32.cs: polynomial 0xedb88320, start and final XOR 0xffffffff)
    /// </summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
        }
        return crc ^ uint.MaxValue;
    }

    /// <summary>
    /// An HTTP session of its own for requests that DuetHttpClient has no call for, as PluginPermissions.UninstallSystemPackageAsync opens one.
    /// Requests carry its key in X-Session-Key (DuetWebServer/Authorization/SessionKeyAuthenticationHandler.cs:40)
    /// </summary>
    public sealed class RawHttpSession : IAsyncDisposable
    {
        /// <summary>
        /// HTTP client with the base address of the printer
        /// </summary>
        public HttpClient Client { get; }

        /// <summary>
        /// Key of the session
        /// </summary>
        public string SessionKey { get; }

        private RawHttpSession(HttpClient client, string sessionKey)
        {
            Client = client;
            SessionKey = sessionKey;
        }

        /// <summary>
        /// Open a session with GET /machine/connect and the default password
        /// </summary>
        public static async Task<RawHttpSession> ConnectAsync()
        {
            HttpClient client = new() { BaseAddress = Lab.BaseUri, Timeout = TimeSpan.FromSeconds(60) };
            try
            {
                using HttpResponseMessage connect = await client.GetAsync($"machine/connect?password={HttpUtility.UrlEncode(DuetAPI.Connection.Defaults.Password)}");
                connect.EnsureSuccessStatusCode();
                using JsonDocument json = JsonDocument.Parse(await connect.Content.ReadAsStringAsync());
                return new RawHttpSession(client, json.RootElement.GetProperty("sessionKey").GetString()!);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Send a request of this session
        /// </summary>
        public async Task<(int Status, string Body)> SendAsync(HttpMethod method, string uri, HttpContent? content = null)
        {
            using HttpRequestMessage request = new(method, uri) { Content = content };
            request.Headers.Add("X-Session-Key", SessionKey);
            using HttpResponseMessage response = await Client.SendAsync(request);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            try
            {
                using HttpRequestMessage disconnect = new(HttpMethod.Get, "machine/disconnect");
                disconnect.Headers.Add("X-Session-Key", SessionKey);
                using HttpResponseMessage _ = await Client.SendAsync(disconnect);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // The session expires anyway
            }
            Client.Dispose();
        }
    }
}
