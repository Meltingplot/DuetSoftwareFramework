using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.WebServer;

using File = System.IO.File;
using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// Tests of the REST API of DWS (DuetWebServer/Endpoints/MachineEndpoints.cs). Expected status codes follow the DSF REST API wiki page
/// (github.com/Duet3D/DuetSoftwareFramework/wiki/REST-API)
/// </summary>
[TestFixture]
public class MachineEndpointTests : WebServerFixture
{
    private HttpClient _client = null!;
    private string _key = null!;

    protected override Task ConfigureDcsAsync() => SetPasswordAsync("machine-secret");

    [OneTimeSetUp]
    public async Task ConnectAsync()
    {
        _client = CreateClient(0);
        _key = await ConnectAsync(_client, "machine-secret");
    }

    [OneTimeTearDown]
    public void DisposeClient() => _client?.Dispose();

    /// <summary>
    /// Log in again if a test has let the session expire
    /// </summary>
    [SetUp]
    public async Task RenewSessionAsync()
    {
        if (!SessionStorage.CheckSessionKey(_key, true))
        {
            _key = await ConnectAsync(_client, "machine-secret");
        }
    }

    private volatile bool _release;

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, HttpContent? content = null) => SendAsync(_client, method, uri, _key, content);

    private static string Encode(string path) => Uri.EscapeDataString(path);

    [Test]
    public async Task ConnectChecksThePassword()
    {
        // "403: Forbidden (invalid password)"
        using (HttpResponseMessage response = await _client.GetAsync("machine/connect?password=wrong"))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }
        using (HttpResponseMessage response = await _client.GetAsync("machine/connect"))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }

        // A new key works until it is disconnected
        string key = await ConnectAsync(_client, "machine-secret");
        Assert.That((await SendAsync(_client, HttpMethod.Get, "machine/noop", key)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await SendAsync(_client, HttpMethod.Get, "machine/disconnect", key)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await SendAsync(_client, HttpMethod.Get, "machine/noop", key)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await _client.GetAsync("machine/noop")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task ModelAndStatus()
    {
        foreach (string uri in new[] { "machine/model", "machine/status" })
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, uri);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/json"));
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.That(json.RootElement.GetProperty("sbc").GetProperty("dsf").GetProperty("userSessions").GetArrayLength(), Is.GreaterThan(0));
        }
    }

    [Test]
    public async Task CodesReturnTheirReply()
    {
        using (HttpResponseMessage response = await SendAsync(HttpMethod.Post, "machine/code", new StringContent("echo 1 + 2")))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("3\n"));
        }

        // async=true does not wait for the reply, it becomes a generic message (DSF REST API wiki page)
        using (HttpResponseMessage response = await SendAsync(HttpMethod.Post, "machine/code?async=true", new StringContent("echo 4")))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty);
        }

        // An error reply is still 200
        using (HttpResponseMessage response = await SendAsync(HttpMethod.Post, "machine/code", new StringContent("echo {1 +}")))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.StartWith("Error: "));
        }
    }

    [Test]
    public async Task FilesAndDirectories()
    {
        byte[] content = Encoding.UTF8.GetBytes("G1 X5\n");

        // PUT creates missing directories and the file, "201: File created"
        using (HttpResponseMessage response = await SendAsync(HttpMethod.Put, "machine/file/" + Encode("0:/gcodes/rest/sub/file.g") + "?timeModified=2023-04-05T06:07:08", new ByteArrayContent(content)))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        }
        string file = Path.Combine(Dcs.BaseDirectory, "gcodes", "rest", "sub", "file.g");
        Assert.That(File.ReadAllBytes(file), Is.EqualTo(content));
        Assert.That(File.GetLastWriteTime(file), Is.EqualTo(new DateTime(2023, 4, 5, 6, 7, 8)));
        Assert.That(File.Exists(file + ".part"), Is.False);

        // Paths with separators that are not encoded work as well (catch-all route)
        using (HttpResponseMessage response = await SendAsync(HttpMethod.Get, "machine/file/0:/gcodes/rest/sub/file.g"))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(content));
        }
        using (HttpResponseMessage response = await SendAsync(HttpMethod.Get, "machine/file/" + Encode("0:/gcodes/rest/missing.g")))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        // File info: 404 for a missing file
        using (HttpResponseMessage response = await SendAsync(HttpMethod.Get, "machine/fileinfo/" + Encode("0:/gcodes/rest/sub/file.g") + "?readThumbnailContent=true"))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.That(json.RootElement.GetProperty("size").GetInt64(), Is.EqualTo(content.Length));
        }
        using (HttpResponseMessage response = await SendAsync(HttpMethod.Get, "machine/fileinfo/" + Encode("0:/gcodes/rest/missing.g")))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        // Directory list: "type" d or f, "size" in bytes; 404 for a missing directory
        using (HttpResponseMessage response = await SendAsync(HttpMethod.Get, "machine/directory/" + Encode("0:/gcodes/rest")))
        {
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.That(json.RootElement.EnumerateArray().Select(e => e.GetProperty("type").GetString() + e.GetProperty("name").GetString()), Is.EqualTo(new[] { "dsub" }));
        }
        using (HttpResponseMessage response = await SendAsync(HttpMethod.Get, "machine/directory/" + Encode("0:/gcodes/rest/missing")))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        // "201: Directory created"
        using (HttpResponseMessage response = await SendAsync(HttpMethod.Put, "machine/directory/" + Encode("0:/gcodes/rest/other")))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        }

        // Move: 204, 409 if the target exists (unless forced), 404 if the source is missing (unless forced)
        Assert.That((await MoveAsync("0:/gcodes/rest/sub", "0:/gcodes/rest/other", null)).StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await MoveAsync("0:/gcodes/rest/sub", "0:/gcodes/rest/other", "true")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        File.WriteAllText(Path.Combine(Dcs.BaseDirectory, "gcodes", "rest", "second.g"), "second");
        Assert.That((await MoveAsync("0:/gcodes/rest/second.g", "0:/gcodes/rest/other/file.g", "false")).StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await MoveAsync("0:/gcodes/rest/second.g", "0:/gcodes/rest/other/file.g", "true")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(File.ReadAllText(Path.Combine(Dcs.BaseDirectory, "gcodes", "rest", "other", "file.g")), Is.EqualTo("second"));
        Assert.That((await MoveAsync("0:/gcodes/rest/other/file.g", "0:/gcodes/rest/moved.g", null)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await MoveAsync("0:/gcodes/rest/missing.g", "0:/gcodes/rest/x.g", null)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await MoveAsync("0:/gcodes/rest/missing.g", "0:/gcodes/rest/x.g", "true")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        // Delete: 204, 404 if missing; a directory that is not empty only with recursive=true
        Assert.That((await SendAsync(HttpMethod.Delete, "machine/file/" + Encode("0:/gcodes/rest/moved.g"))).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await SendAsync(HttpMethod.Delete, "machine/file/" + Encode("0:/gcodes/rest/moved.g"))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        File.WriteAllText(Path.Combine(Dcs.BaseDirectory, "gcodes", "rest", "other", "keep.g"), "keep");
        Assert.That((await SendAsync(HttpMethod.Delete, "machine/file/" + Encode("0:/gcodes/rest/other"))).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That((await SendAsync(HttpMethod.Delete, "machine/file/" + Encode("0:/gcodes/rest/other") + "?recursive=true")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(Directory.Exists(Path.Combine(Dcs.BaseDirectory, "gcodes", "rest", "other")), Is.False);
    }

    private Task<HttpResponseMessage> MoveAsync(string from, string to, string? force)
    {
        // The wiki documents application/x-www-form-urlencoded, DuetHttpClient sends multipart/form-data (RestConnector.MoveAsync); ReadFormAsync takes both
        HttpContent content = (force is null)
            ? new FormUrlEncodedContent([new("from", from), new("to", to)])
            : new MultipartFormDataContent { { new StringContent(from), "from" }, { new StringContent(to), "to" }, { new StringContent(force), "force" } };
        return SendAsync(HttpMethod.Post, "machine/file/move", content);
    }

    [Test]
    public async Task PluginsAndSystemPackages()
    {
        // The fake host runs no plugin service and DCS has no root plugin support (Settings.RootPluginSupport defaults to false), so these
        // commands fail in DCS and DWS reports "500: Generic error". The DWS of this test runs in the test process, which DCS treats as an
        // external program without SuperUser, so the system package commands are already refused by their permission check
        Assert.That((await SendAsync(HttpMethod.Put, "machine/plugin", new ByteArrayContent(Encoding.UTF8.GetBytes("no zip file")))).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That((await SendAsync(HttpMethod.Delete, "machine/plugin", new StringContent("MissingPlugin"))).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That((await SendAsync(HttpMethod.Post, "machine/startPlugin", new StringContent("MissingPlugin"))).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That((await SendAsync(HttpMethod.Post, "machine/stopPlugin", new StringContent("MissingPlugin"))).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That((await SendAsync(HttpMethod.Put, "machine/systemPackage", new ByteArrayContent([1, 2, 3]))).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That((await SendAsync(HttpMethod.Delete, "machine/systemPackage", new StringContent("package"))).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));

        // Plugin data can be set via PATCH only for plugins without SBC executable (MachineEndpoints.cs:607-634, DSF REST API wiki page)
        using (await Dcs.Model.AccessReadWriteAsync())
        {
            Plugin plugin = new() { Id = "WebPlugin" };
            plugin.Data["value"] = JsonDocument.Parse("1").RootElement.Clone();
            Dcs.Model.Plugins["WebPlugin"] = plugin;
            Plugin sbcPlugin = new() { Id = "SbcPlugin", SbcExecutable = "plugin.py" };
            sbcPlugin.Data["value"] = JsonDocument.Parse("1").RootElement.Clone();
            Dcs.Model.Plugins["SbcPlugin"] = sbcPlugin;
        }
        try
        {
            static StringContent Patch(string plugin, string key, string value) => new($"{{\"plugin\":\"{plugin}\",\"key\":\"{key}\",\"value\":{value}}}", Encoding.UTF8, "application/json");

            Assert.That((await SendAsync(HttpMethod.Patch, "machine/plugin", Patch("WebPlugin", "value", "{\"a\":2}"))).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            using (await Dcs.Model.AccessReadOnlyAsync())
            {
                Assert.That(Dcs.Model.Plugins["WebPlugin"].Data["value"]!.Value.GetProperty("a").GetInt32(), Is.EqualTo(2));
            }
            Assert.That((await SendAsync(HttpMethod.Patch, "machine/plugin", Patch("SbcPlugin", "value", "2"))).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That((await SendAsync(HttpMethod.Patch, "machine/plugin", Patch("MissingPlugin", "value", "2"))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await SendAsync(HttpMethod.Patch, "machine/plugin", Patch("WebPlugin", "missing", "2"))).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That((await SendAsync(HttpMethod.Patch, "machine/plugin", new StringContent("{"))).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        }
        finally
        {
            using (await Dcs.Model.AccessReadWriteAsync())
            {
                Dcs.Model.Plugins.Remove("WebPlugin");
                Dcs.Model.Plugins.Remove("SbcPlugin");
            }
        }
    }

    [Test]
    public async Task ReadOnlySessionsCannotWrite()
    {
        // No login path creates read-only sessions (all pass readWrite: true), so make one like a read-only login would
        using DuetAPIClient.CommandConnection connection = new();
        await connection.ConnectAsync(Dcs.SocketPath!);
        int sessionId = await connection.AddUserSessionAsync(AccessLevel.ReadOnly, SessionType.HTTP, "127.0.0.10");
        string key = SessionStorage.MakeSessionKey(sessionId, string.Empty, false);

        // Program.cs:72-73: the read-only policy admits readOnly and readWrite sessions, the read-write policy only readWrite ones
        Assert.That((await SendAsync(_client, HttpMethod.Get, "machine/model", key)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await SendAsync(_client, HttpMethod.Post, "machine/code", key, new StringContent("M115"))).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await SendAsync(_client, HttpMethod.Get, "rr_gcode?gcode=M115", key)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await SendAsync(_client, HttpMethod.Get, "rr_reply", key)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await SendAsync(_client, HttpMethod.Get, "machine/disconnect", key)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await GetUserSessionsAsync()).Any(s => s.Id == sessionId), Is.False);
    }

    [Test]
    public async Task LongRunningCodesKeepTheirSession()
    {
        // A code that has not finished keeps its session from expiring (MachineEndpoints.cs:178-195, SessionStorage.cs:397)
        _release = false;
        Dcs.Firmware.OnCode("M4711", _ => _release ? FakeReply.Ok("done") : FakeReply.NotFinished);
        string key = await ConnectAsync(_client, "machine-secret");
        Task<HttpResponseMessage> request = SendAsync(_client, HttpMethod.Post, "machine/code", key, new StringContent("M4711"));
        await WaitUntilAsync(() => Dws.Log.Entries.Any(e => e.Message == $"Session {key} started a long-running request"), "The request did not start");

        ExpireIdleSessions();
        Assert.That(SessionStorage.CheckSessionKey(key, true), Is.True);

        _release = true;
        using HttpResponseMessage response = await request.WaitAsync(FakeFirmwareHost.DefaultTimeout);
        Assert.That(await response.Content.ReadAsStringAsync(), Does.StartWith("done"));
        ExpireIdleSessions();
        Assert.That(SessionStorage.CheckSessionKey(key, false), Is.False);
    }
}
