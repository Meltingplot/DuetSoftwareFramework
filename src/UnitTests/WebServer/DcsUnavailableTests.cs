using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DuetWebServer.Singletons;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.WebServer;

using File = System.IO.File;
using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// DWS without DCS: every request that needs DCS must fail with the status codes of the DSF REST API wiki page ("502: Incompatible DCS
/// version", "503: DCS is unavailable"), as EndpointHelper.HandleDcsExceptionAsync maps them, and with {"err":1} for the rr_ requests
/// </summary>
[TestFixture]
public class DcsUnavailableTests
{
    private const string NotStarted = "Failed to connect to Duet, please check your connection (DCS is not started)";

    private string _directory = null!;
    private string _startErrorFile = null!;
    private DwsHost _dws = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dsf-nodcs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_directory, "www"));
        _startErrorFile = Path.Combine(_directory, "start-error.txt");
        _dws = await DwsHost.StartAsync(Path.Combine(_directory, "missing.sock"), Path.Combine(_directory, "www"), config => config["StartErrorFile"] = _startErrorFile);
        _client = new HttpClient(WebServerFixture.CreateHandler(0)) { BaseAddress = _dws.BaseUri };
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        _client?.Dispose();
        if (_dws is not null)
        {
            try
            {
                _dws.AssertNoUnhandledException();
            }
            finally
            {
                await _dws.DisposeAsync();
            }
        }
        Directory.Delete(_directory, true);
    }

    [TearDown]
    public void DeleteStartError() => File.Delete(_startErrorFile);

    /// <summary>
    /// Make a session like a login would, which is impossible without DCS
    /// </summary>
    private string MakeSession() => _dws.Services.GetRequiredService<ISessionStorage>().MakeSessionKey(1, string.Empty, true);

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string uri, string? key, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, uri) { Content = content };
        if (key is not null)
        {
            request.Headers.Add("X-Session-Key", key);
        }
        using HttpResponseMessage response = await _client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task LoginsFail()
    {
        Assert.That(await SendAsync(HttpMethod.Get, "rr_connect", null), Is.EqualTo((HttpStatusCode.ServiceUnavailable, NotStarted)));
        Assert.That(await SendAsync(HttpMethod.Get, "machine/connect", null), Is.EqualTo((HttpStatusCode.ServiceUnavailable, NotStarted)));

        // Without a key the password cannot be checked, so the request is not authenticated (SessionKeyAuthenticationHandler.cs:74-78)
        Assert.That((await SendAsync(HttpMethod.Get, "machine/model", null)).Status, Is.EqualTo(HttpStatusCode.Unauthorized));

        // The start error of DCS is reported instead if there is one (EndpointHelper.cs:135-140)
        await File.WriteAllTextAsync(_startErrorFile, "DCS could not start: test");
        Assert.That(await SendAsync(HttpMethod.Get, "machine/connect", null), Is.EqualTo((HttpStatusCode.ServiceUnavailable, "DCS could not start: test")));
    }

    [Test]
    public async Task RequestsWithSessionFail()
    {
        string key = MakeSession();
        (HttpMethod, string, HttpContent?)[] machineRequests =
        [
            (HttpMethod.Get, "machine/model", null),
            (HttpMethod.Post, "machine/code", new StringContent("M115")),
            (HttpMethod.Get, "machine/file/0%3A%2Fsys%2Fconfig.g", null),
            (HttpMethod.Put, "machine/file/0%3A%2Fsys%2Fx.g", new StringContent("x")),
            (HttpMethod.Get, "machine/fileinfo/0%3A%2Fsys%2Fx.g", null),
            (HttpMethod.Delete, "machine/file/0%3A%2Fsys%2Fx.g", null),
            (HttpMethod.Post, "machine/file/move", new FormUrlEncodedContent([new("from", "0:/a"), new("to", "0:/b")])),
            (HttpMethod.Get, "machine/directory/0%3A%2Fsys", null),
            (HttpMethod.Put, "machine/directory/0%3A%2Fsys%2Fd", null),
            (HttpMethod.Put, "machine/plugin", new ByteArrayContent([1])),
            (HttpMethod.Delete, "machine/plugin", new StringContent("p")),
            (HttpMethod.Patch, "machine/plugin", new StringContent("{\"plugin\":\"p\",\"key\":\"k\",\"value\":1}")),
            (HttpMethod.Post, "machine/startPlugin", new StringContent("p")),
            (HttpMethod.Post, "machine/stopPlugin", new StringContent("p")),
            (HttpMethod.Put, "machine/systemPackage", new ByteArrayContent([1])),
            (HttpMethod.Delete, "machine/systemPackage", new StringContent("p"))
        ];
        foreach ((HttpMethod method, string uri, HttpContent? content) in machineRequests)
        {
            Assert.That(await SendAsync(method, uri, key, content), Is.EqualTo((HttpStatusCode.ServiceUnavailable, NotStarted)), $"{method} {uri}");
        }

        // The rr_ requests answer like RRF does on errors
        foreach (string uri in new[] { "rr_gcode?gcode=M115", "rr_download?name=0:/sys/x.g", "rr_delete?name=0:/sys/x.g", "rr_files?dir=0:/sys", "rr_move?old=0:/a&new=0:/b",
            "rr_mkdir?dir=0:/sys/d", "rr_fileinfo?name=0:/sys/x.g", "rr_thumbnail?name=0:/sys/x.g&offset=1" })
        {
            Assert.That(await SendAsync(HttpMethod.Get, uri, key), Is.EqualTo((HttpStatusCode.OK, "{\"err\":1}")), uri);
        }
        Assert.That(await SendAsync(HttpMethod.Post, "rr_upload?name=0:/sys/x.g", key, new StringContent("x")), Is.EqualTo((HttpStatusCode.OK, "{\"err\":1}")));
        Assert.That(await SendAsync(HttpMethod.Get, "rr_upload", key), Is.EqualTo((HttpStatusCode.OK, "{\"err\":1}")));
        Assert.That(await SendAsync(HttpMethod.Get, "rr_filelist?dir=0:/sys", key), Is.EqualTo((HttpStatusCode.OK, "{\"err\":2}")));
        Assert.That((await SendAsync(HttpMethod.Get, "rr_model?key=state", key)).Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));

        // Disconnecting removes the DWS session first and fails when it removes the DCS user session
        Assert.That(await SendAsync(HttpMethod.Get, "rr_disconnect", key), Is.EqualTo((HttpStatusCode.OK, "{\"err\":1}")));
        Assert.That((await SendAsync(HttpMethod.Get, "machine/noop", key)).Status, Is.EqualTo(HttpStatusCode.Unauthorized));
        key = MakeSession();
        Assert.That(await SendAsync(HttpMethod.Get, "machine/disconnect", key), Is.EqualTo((HttpStatusCode.ServiceUnavailable, NotStarted)));
    }

    [Test]
    public async Task WebSocketsFail()
    {
        // Without a key the password cannot be checked (WebSocketEndpoint.cs:93-115)
        Assert.That(await ConnectAsync("machine"), Is.EqualTo(HttpStatusCode.ServiceUnavailable));

        // With a key the socket is accepted and closed again because the subscription fails (WebSocketEndpoint.cs:168-197)
        Assert.That(await ConnectAndReceiveCloseAsync(MakeSession()), Is.EqualTo(((WebSocketCloseStatus?)WebSocketCloseStatus.EndpointUnavailable, NotStarted)));
        await File.WriteAllTextAsync(_startErrorFile, "DCS could not start: socket test");
        Assert.That(await ConnectAndReceiveCloseAsync(MakeSession()), Is.EqualTo(((WebSocketCloseStatus?)WebSocketCloseStatus.EndpointUnavailable, "DCS could not start: socket test")));
    }

    private async Task<HttpStatusCode> ConnectAsync(string pathAndQuery)
    {
        using ClientWebSocket webSocket = new();
        webSocket.Options.CollectHttpResponseDetails = true;
        try
        {
            await webSocket.ConnectAsync(new Uri($"ws://{_dws.BaseUri.Host}:{_dws.BaseUri.Port}/{pathAndQuery}"), CancellationToken.None);
            return HttpStatusCode.SwitchingProtocols;
        }
        catch (WebSocketException)
        {
            return webSocket.HttpStatusCode;
        }
    }

    private async Task<(WebSocketCloseStatus?, string?)> ConnectAndReceiveCloseAsync(string key)
    {
        using ClientWebSocket webSocket = new();
        await webSocket.ConnectAsync(new Uri($"ws://{_dws.BaseUri.Host}:{_dws.BaseUri.Port}/machine?sessionKey={key}"), CancellationToken.None);
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        WebSocketReceiveResult result = await webSocket.ReceiveAsync(new byte[1024], cts.Token);
        Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Close));
        await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
        return (result.CloseStatus, result.CloseStatusDescription);
    }

    /// <summary>
    /// A DCS that speaks an older IPC protocol answers the init message with an IncompatibleVersionException (BaseConnection.cs:149-157)
    /// </summary>
    [Test]
    public async Task IncompatibleDcsVersion()
    {
        string socketPath = Path.Combine(_directory, "old.sock");
        using OldDcs oldDcs = new(socketPath);
        await SetSocketPathAsync(socketPath);
        try
        {
            string key = MakeSession();
            Assert.That(await SendAsync(HttpMethod.Get, "machine/connect", null), Is.EqualTo((HttpStatusCode.BadGateway, "Incompatible DCS version")));
            Assert.That(await SendAsync(HttpMethod.Get, "machine/model", key), Is.EqualTo((HttpStatusCode.BadGateway, "Incompatible DCS version")));
            Assert.That(await ConnectAsync("machine"), Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(await ConnectAndReceiveCloseAsync(key), Is.EqualTo(((WebSocketCloseStatus?)WebSocketCloseStatus.InternalServerError, "Incompatible DCS version")));
        }
        finally
        {
            await SetSocketPathAsync(Path.Combine(_directory, "missing.sock"));
        }
    }

    /// <summary>
    /// Change the socket path in the configuration file, which DWS reloads (Program.cs:45, reloadOnChange)
    /// </summary>
    private async Task SetSocketPathAsync(string socketPath)
    {
        JsonObject config = JsonNode.Parse(await File.ReadAllTextAsync(_dws.ConfigFile))!.AsObject();
        config["SocketPath"] = socketPath;
        await File.WriteAllTextAsync(_dws.ConfigFile, config.ToJsonString());
        IOptionsMonitor<DuetWebServer.Settings> settings = _dws.Services.GetRequiredService<IOptionsMonitor<DuetWebServer.Settings>>();
        DateTime end = DateTime.Now + FakeFirmwareHost.DefaultTimeout;
        while (settings.CurrentValue.SocketPath != socketPath)
        {
            if (DateTime.Now > end)
            {
                Assert.Fail("DWS did not reload its configuration");
            }
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// IPC server that rejects every client like a DCS with another protocol version
    /// </summary>
    private sealed class OldDcs : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        public OldDcs(string socketPath)
        {
            _socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            _socket.Listen(16);
            _ = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                Socket client;
                try
                {
                    client = await _socket.AcceptAsync();
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException)
                {
                    return;
                }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await client.SendAsync(Encoding.UTF8.GetBytes("{\"version\":12,\"id\":1}"));
                            await client.ReceiveAsync(new byte[4096]);
                            await client.SendAsync(Encoding.UTF8.GetBytes("{\"success\":false,\"errorType\":\"IncompatibleVersionException\",\"errorMessage\":\"Incompatible API version\"}"));
                            client.Shutdown(SocketShutdown.Both);
                        }
                        catch (SocketException)
                        {
                            // Client went away
                        }
                    }
                });
            }
        }

        public void Dispose() => _socket.Dispose();
    }
}
