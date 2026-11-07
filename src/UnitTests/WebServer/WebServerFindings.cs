using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NUnit.Framework;

namespace UnitTests.WebServer;

using File = System.IO.File;
using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// Reproducers of bugs that the DWS tests found. They are explicit so that the suite stays green; each one asserts the documented or
/// reference behaviour and fails until the bug is fixed
/// </summary>
[TestFixture]
public class WebServerFindings : WebServerFixture
{
    protected override void ConfigureDws(JsonObject config) => config["SessionTimeout"] = 3000;

    [SetUp]
    public async Task ResetAsync()
    {
        await SetPasswordAsync(DuetAPI.Connection.Defaults.Password);
        await ClearSessionsAsync();
    }

    /// <summary>
    /// rr_upload writes straight into the target file (RepRapFirmwareEndpoints.cs:261-279) and deletes it on a CRC mismatch (line 285), so a
    /// failed upload destroys the file it was meant to replace. RRF uploads to name + ".part" (UploadingNetworkResponder.cpp:59-62,
    /// UPLOAD_EXTENSION in Configuration.h:314) and deletes only that file on an error, renaming it over the original only on success
    /// (UploadingNetworkResponder.cpp:106-128). PUT /machine/file of DWS works the same way as RRF (MachineEndpoints.cs:269-291)
    /// </summary>
    [Test]
    [Explicit("Bug: a failed rr_upload deletes the file it was meant to replace")]
    public async Task FailedUploadKeepsExistingFile()
    {
        string file = Path.Combine(Dcs.BaseDirectory, "gcodes", "keep.g");
        await File.WriteAllTextAsync(file, "original");
        using HttpClient client = CreateClient(0);
        string key = await ConnectAsync(client);

        byte[] body = Encoding.UTF8.GetBytes("replacement");
        string wrongCrc = (RequestFuzz.Crc32(body) ^ 1).ToString("x8");
        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, $"rr_upload?name=0:/gcodes/keep.g&crc32={wrongCrc}", key, new ByteArrayContent(body));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("{\"err\":1}"));

        Assert.That(File.Exists(file), Is.True, "The existing file was deleted");
        Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("original"));
    }

    /// <summary>
    /// rr_connect reports a session timeout of 8000 ms whatever the DWS setting is (RepRapFirmwareEndpoints.cs:112). The RRF HTTP request
    /// docs define sessionTimeout as the idle timeout before a client is removed again, and DWS removes sessions after Settings.SessionTimeout
    /// (SessionExpiry.cs:28), 3000 ms in this fixture. A client that relies on the reported value loses its session
    /// </summary>
    [Test]
    [Explicit("Bug: rr_connect reports sessionTimeout 8000 instead of the configured SessionTimeout")]
    public async Task RrConnectReportsConfiguredSessionTimeout()
    {
        using HttpClient client = CreateClient(0);
        using JsonDocument json = JsonDocument.Parse(await client.GetStringAsync("rr_connect?password=reprap"));
        Assert.That(json.RootElement.GetProperty("sessionTimeout").GetInt32(), Is.EqualTo(3000));
    }

    /// <summary>
    /// rr_gcode answers {"bufferSpace":255,"err":0} (RepRapFirmwareEndpoints.cs:182). RRF answers {"buff":N} (HttpResponder.cpp:566) and the
    /// RRF HTTP request docs say "Returns {"buff": bufferSpace}"; DuetHttpClient reads "buff" too (GcodeReply.cs), so its PollConnector
    /// takes 0 bytes of buffer space and throws "G-code buffer is full" (PollConnector.cs:765-769)
    /// </summary>
    [Test]
    [Explicit("Deviation: rr_gcode reports bufferSpace instead of buff")]
    public async Task RrGcodeReportsBuff()
    {
        using HttpClient client = CreateClient(0);
        using JsonDocument json = JsonDocument.Parse(await client.GetStringAsync("rr_gcode?gcode=M115"));
        Assert.That(json.RootElement.TryGetProperty("buff", out JsonElement buff) && buff.GetInt32() > 0, Is.True, json.RootElement.GetRawText());
    }

    /// <summary>
    /// rr_disconnect answers 204 without content (RepRapFirmwareEndpoints.cs:154). The RRF HTTP request docs say it returns {"err": code},
    /// 0 if the session was removed, and RRF does so (HttpResponder.cpp:551-553)
    /// </summary>
    [Test]
    [Explicit("Deviation: rr_disconnect returns 204 instead of {\"err\":0}")]
    public async Task RrDisconnectReturnsErr()
    {
        using HttpClient client = CreateClient(0);
        await client.GetStringAsync("rr_connect?password=reprap");
        using HttpResponseMessage response = await client.GetAsync("rr_disconnect");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("{\"err\":0}"));
    }

    /// <summary>
    /// Every successful rr_connect adds another session for the IP address of the client (RepRapFirmwareEndpoints.cs:88-89). RRF keeps one
    /// IP-based session per address ("Don't register a second IP-based HTTP session", HttpResponder.cpp:803-808), and the RRF HTTP request
    /// docs say that further rr_connect calls return err 0 without creating a new session. In DWS the extra sessions show up as user sessions
    /// in DCS and cache every message (SessionStorage.cs:132-141) until they expire, while requests only ever use the first one
    /// </summary>
    [Test]
    [Explicit("Deviation: rr_connect adds another IP session for the same client")]
    public async Task RrConnectKeepsOneIpSession()
    {
        await SetPasswordAsync("secret");
        using HttpClient client = CreateClient(0);
        for (int i = 0; i < 3; i++)
        {
            using JsonDocument json = JsonDocument.Parse(await client.GetStringAsync("rr_connect?password=secret"));
            Assert.That(json.RootElement.GetProperty("err").GetInt32(), Is.Zero);
        }
        Assert.That(await GetUserSessionsAsync(), Has.Length.EqualTo(1));
    }

    /// <summary>
    /// rr_delete ignores the recursive parameter and deletes directories only when they are empty (RepRapFirmwareEndpoints.cs:354-383).
    /// The RRF HTTP request docs: "recursive (optional): Set this to yes to delete the directory recursively if applicable. Defaults to no
    /// (requires 3.5b3 and newer)", and RRF passes it on (HttpResponder.cpp:612-617)
    /// </summary>
    [Test]
    [Explicit("Deviation: rr_delete ignores recursive=yes")]
    public async Task RrDeleteDeletesRecursively()
    {
        string directory = Path.Combine(Dcs.BaseDirectory, "gcodes", "tree");
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        await File.WriteAllTextAsync(Path.Combine(directory, "sub", "file.g"), "G1 X1\n");
        using HttpClient client = CreateClient(0);
        string key = await ConnectAsync(client);

        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, "rr_delete?name=0:/gcodes/tree&recursive=yes", key);
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("{\"err\":0}"));
        Assert.That(Directory.Exists(directory), Is.False);
    }

    /// <summary>
    /// POST /machine/file/move reads the form before its try block (MachineEndpoints.cs:397), so a body that is no valid form, e.g. one with
    /// another content type or a NUL character, ends in an unhandled exception that Kestrel answers with 500 and logs as error. All other
    /// handlers report such errors through EndpointHelper.HandleDcsExceptionAsync
    /// </summary>
    [Test]
    [Explicit("Bug: POST /machine/file/move throws an unhandled exception for an invalid form")]
    public async Task MoveWithInvalidFormIsHandled()
    {
        using HttpClient client = CreateClient(0);
        string key = await ConnectAsync(client);
        int logStart = Dws.Log.Count;

        using (HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "machine/file/move", key, new StringContent("from=a&to=b")))
        {
            Assert.That(response.StatusCode, Is.Not.EqualTo(HttpStatusCode.NoContent));
        }
        using (HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "machine/file/move", key, new FormUrlEncodedContent([new("from", "0:/gcodes/a\0"), new("to", "0:/gcodes/b")])))
        {
            Assert.That(response.StatusCode, Is.Not.EqualTo(HttpStatusCode.NoContent));
        }
        Assert.That(Dws.Log.Since(logStart).Where(e => e.IsUnhandledException), Is.Empty);
    }

    /// <summary>
    /// PUT /machine/directory and PUT /machine/file put HttpUtility.UrlPathEncode(name) into the Location header of the 201 response
    /// (MachineEndpoints.cs:294 and 504). UrlPathEncode leaves everything after a question mark as it is, so a name with a question mark
    /// followed by a non-ASCII character makes Kestrel throw when it sets the header: the directory is created, but the client gets a 500 and
    /// DWS logs an unhandled exception. The DSF REST API wiki page documents "201: Directory created"
    /// </summary>
    [Test]
    [Explicit("Bug: the Location header of PUT /machine/directory and /machine/file throws for a name with '?' and a non-ASCII character")]
    public async Task CreatedLocationIsEncoded()
    {
        using HttpClient client = CreateClient(0);
        string key = await ConnectAsync(client);
        int logStart = Dws.Log.Count;

        using (HttpResponseMessage response = await SendAsync(client, HttpMethod.Put, "machine/directory/" + Uri.EscapeDataString("0:/gcodes/what?ä"), key))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        }
        using (HttpResponseMessage response = await SendAsync(client, HttpMethod.Put, "machine/file/" + Uri.EscapeDataString("0:/gcodes/what?ö.g"), key, new StringContent("G1 X1\n")))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        }
        Assert.That(Dws.Log.Since(logStart).Where(e => e.IsUnhandledException), Is.Empty);
    }

    /// <summary>
    /// A custom endpoint in the rr_ namespace is served as /rr_{path} (HttpEndpoint.cs:29, ModelObserver.cs:120) if it exists when DWS
    /// connects to DCS, but when the endpoints change later, ModelObserver rebuilds all of them as /machine/{namespace}/{path}
    /// (ModelObserver.cs:176-181), so an rr_ endpoint that a plugin registers while DWS runs is not reachable via /rr_{path}
    /// </summary>
    [Test]
    [Explicit("Bug: rr_ endpoints registered while DWS runs are mapped to /machine/rr_/{path}")]
    public async Task RrEndpointRegisteredAtRuntime()
    {
        using DuetAPIClient.CommandConnection connection = new();
        await connection.ConnectAsync(Dcs.SocketPath!);
        int logStart = Dws.Log.Count;
        using DuetAPIClient.HttpEndpointUnixSocket endpoint = await connection.AddHttpEndpointAsync(DuetAPI.ObjectModel.HttpEndpointType.GET, DuetAPI.ObjectModel.HttpEndpoint.RepRapFirmwareNamespace, "custom");
        endpoint.OnEndpointRequestReceived += async (socket, request) =>
        {
            await request.ReadRequestAsync();
            await request.SendResponseAsync(200, "custom reply", DuetAPI.Commands.HttpResponseType.PlainText);
        };
        await WaitUntilAsync(() => Dws.Log.Since(logStart).Any(e => e.Message.StartsWith("Registered HTTP GET endpoint")), "DWS did not register the endpoint");

        using HttpClient client = CreateClient(0);
        using HttpResponseMessage response = await client.GetAsync("rr_custom");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("custom reply"));
    }

    /// <summary>
    /// SessionStorage.MaintainSessions removes every expired session from DCS with an IPC connection of its own, all started at once
    /// (SessionStorage.cs:404 and 420-422). DCS listens with a backlog of 4 (DuetControlServer/Settings.cs:116, the same in
    /// pkg/common/duetcontrolserver/opt/dsf/conf/config.json), so when more sessions expire in the same pass, some connects fail with
    /// EAGAIN ("Resource temporarily unavailable"), DWS only logs "Failed to unregister expired user session" and the user sessions stay
    /// in sbc.dsf.userSessions for good
    /// </summary>
    [Test]
    [Explicit("Bug: expired sessions stay in DCS when more than the IPC backlog expire at once")]
    public async Task ExpiredSessionsAreRemovedFromDcs()
    {
        using HttpClient client = CreateClient(0);
        for (int i = 0; i < 32; i++)
        {
            await ConnectAsync(client);
        }
        Assert.That(await GetUserSessionsAsync(), Has.Length.EqualTo(33), "32 sessions plus the IP session of the first request");

        ExpireIdleSessions();
        await WaitUntilAsync(async () => (await GetUserSessionsAsync()).Length == 0, "Expired sessions are still in the DCS object model", TimeSpan.FromSeconds(5));
    }
}
