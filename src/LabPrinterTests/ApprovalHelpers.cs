using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Helpers of the approval tests that use jobs, files on the SBC, the SBC clock and delete requests at the root of the virtual SD card
/// </summary>
public static partial class ApprovalHelpers
{
    /// <summary>
    /// Directory on the SBC for the files these tests create there. It is below /var like the one of ControlServerCapabilities and away from the
    /// side-load directory /var/tmp/lab-tests, which run-lab-tests.py expects to be gone after a run
    /// </summary>
    public const string SbcDirectory = "/var/tmp/lab-sbc-files";

    /// <summary>
    /// Macro code that creates a global or sets it if it exists, as in VariableCopies
    /// </summary>
    public static string Global(string name, string value) => $"""
        if !exists(global.{name})
          global {name} = {value}
        else
          set global.{name} = {value}

        """;

    /// <summary>
    /// Create or reset globals with a macro in the test macro directory. Echo output of a macro does not reach the reply of its M98, so the tests read globals
    /// </summary>
    public static async Task SetGlobalsAsync(DuetHttpSession session, string macro, params (string Name, string Value)[] globals)
    {
        await Lab.UploadAsync(session, macro, string.Concat(globals.Select(item => Global(item.Name, item.Value))));
        string reply = await Lab.SendCodeAsync(session, $"M98 P\"{macro}\"", TimeSpan.FromSeconds(10));
        Assert.That(reply, Does.Not.Contain("Error"), $"Setting the globals failed: {reply}");
    }

    /// <summary>
    /// Skip the test unless the SBC has python3, which the scripts of these tests use for what the shell cannot do (clock, sparse files, IPC)
    /// </summary>
    public static async Task RequirePython3Async()
    {
        if ((await Lab.RunOnSbcAsync("command -v python3 >/dev/null && echo yes || echo no")).Trim() != "yes")
        {
            Assert.Ignore("The SBC has no python3");
        }
    }

    /// <summary>
    /// Physical directory of drive 0: BaseDirectory of the DCS configuration /opt/dsf/conf/config.json, /opt/dsf/sd if it sets none
    /// (DuetControlServer/Settings.cs:29 and :122)
    /// </summary>
    public static async Task<string> GetSdDirectoryAsync()
    {
        string output = await Lab.RunOnSbcAsync("""
            sudo -n python3 -c 'import json; print(json.load(open("/opt/dsf/conf/config.json")).get("BaseDirectory") or "/opt/dsf/sd")'
            """);
        return output.Trim().TrimEnd('/');
    }

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)(-[^+]*)?\+mp\.(\d+)")]
    private static partial Regex ForkVersionRegex();

    /// <summary>
    /// Skip the test unless DCS is the fork release 3.7.0+mp.<paramref name="minimum"/> or newer. For codes whose handling in older builds could do
    /// harm, so a test must not depend on the version only to fail
    /// </summary>
    /// <remarks>
    /// The version is the one DCS reports (model sbc.dsf.version, LabSetUp logs it). A side-load reports the version of its branch
    /// (src/Directory.Build.props). Later Duet3D versions count from +mp.1 again, they are newer than any 3.7.0 fork release
    /// </remarks>
    public static void RequireForkRelease(DuetHttpSession session, int minimum, string reason)
    {
        string? version = Lab.Read(session, model => model.SBC?.DSF.Version);
        Match match = ForkVersionRegex().Match(version ?? string.Empty);
        if (match.Success)
        {
            Version duet3d = new(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
            bool prerelease = match.Groups[4].Success;
            if (duet3d > new Version(3, 7, 0) || (duet3d == new Version(3, 7, 0) && !prerelease && int.Parse(match.Groups[5].Value) >= minimum))
            {
                return;
            }
        }
        Assert.Ignore($"DSF {version ?? "of unknown version"} is not 3.7.0+mp.{minimum} or newer: {reason}");
    }

    /// <summary>
    /// Script that sends one code to DCS through its IPC socket as root and prints the JSON of the result, in the way of AbortsCrossingMacroRequests
    /// (ipc.md: one JSON object per message; Command mode; SimpleCode). Arguments: code and channel. For codes that have to run inside one SBC script,
    /// e.g. while a file is replaced for the length of the code
    /// </summary>
    public const string IpcCodeScript = """
        import json, socket, sys, time

        def receive(sock, data, timeout):
            deadline = time.monotonic() + timeout
            while True:
                try:
                    text = data[0].decode().lstrip()
                    message, end = json.JSONDecoder().raw_decode(text)
                    data[0] = text[end:].encode()
                    return message
                except ValueError:
                    pass
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("no answer from DCS")
                sock.settimeout(remaining)
                chunk = sock.recv(65536)
                if not chunk:
                    raise ConnectionError("DCS closed the connection")
                data[0] += chunk

        sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        sock.connect("/run/dsf/dcs.sock")
        data = [b""]
        init = receive(sock, data, 5)
        sock.sendall(json.dumps({"mode": "Command", "version": init["version"]}).encode())
        if not receive(sock, data, 5).get("success"):
            raise RuntimeError("DCS refused the command mode")
        sock.sendall(json.dumps({"command": "SimpleCode", "code": sys.argv[1], "channel": sys.argv[2]}).encode())
        print(json.dumps(receive(sock, data, 60)))
        """;
}

/// <summary>
/// HTTP session of the /machine API without a WebSocket, for answers DuetHttpClient does not pass on (status and text of a refused delete) and for
/// sessions that live only by their requests (DuetWebServer SessionStorage.MaintainSessions)
/// </summary>
public sealed class RawMachineSession : IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly string _sessionKey;

    private RawMachineSession(HttpClient client, string sessionKey)
    {
        _client = client;
        _sessionKey = sessionKey;
    }

    /// <summary>
    /// Open a session with GET /machine/connect, as PluginPermissions does
    /// </summary>
    public static async Task<RawMachineSession> ConnectAsync()
    {
        HttpClient client = new() { BaseAddress = Lab.BaseUri, Timeout = TimeSpan.FromSeconds(60) };
        try
        {
            using HttpResponseMessage connect = await client.GetAsync($"machine/connect?password={HttpUtility.UrlEncode(DuetAPI.Connection.Defaults.Password)}");
            connect.EnsureSuccessStatusCode();
            using JsonDocument json = JsonDocument.Parse(await connect.Content.ReadAsStringAsync());
            return new RawMachineSession(client, json.RootElement.GetProperty("sessionKey").GetString()!);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Send a request with the key of this session
    /// </summary>
    /// <param name="method">HTTP method</param>
    /// <param name="path">Path relative to the base URL, with each file path encoded as DuetHttpClient does (Uri.EscapeDataString, RestConnector.cs:477)</param>
    /// <returns>Status and body of the answer</returns>
    public async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Add("X-Session-Key", _sessionKey);
        using HttpResponseMessage response = await _client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Send a request of the rr_ API, which authorizes by the IP address of a client after rr_connect (SessionKeyAuthenticationHandler.cs:55-79)
    /// </summary>
    public async Task<string> SendRepRapFirmwareRequestAsync(string pathAndQuery)
    {
        using HttpResponseMessage response = await _client.GetAsync(pathAndQuery);
        return await response.Content.ReadAsStringAsync();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            using HttpRequestMessage disconnect = new(HttpMethod.Get, "machine/disconnect");
            disconnect.Headers.Add("X-Session-Key", _sessionKey);
            using HttpResponseMessage _ = await _client.SendAsync(disconnect);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            // The session expires by itself
        }
        _client.Dispose();
    }
}
