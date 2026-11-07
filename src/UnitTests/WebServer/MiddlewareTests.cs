using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using NUnit.Framework;

namespace UnitTests.WebServer;

using File = System.IO.File;
using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// Tests of the middlewares of DWS: static files with the SPA fallback (Program.cs:104-135, FallbackMiddleware.cs) and third-party HTTP
/// endpoints that plugins register via DCS (CustomEndpointMiddleware.cs, HttpEndpointConnection.cs and DuetAPIClient/HttpEndpoints)
/// </summary>
[TestFixture]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class MiddlewareTests : WebServerFixture
{
    private HttpClient _client = null!;
    private CommandConnection _connection = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _client = CreateClient(0);
        _connection = new CommandConnection();
        await _connection.ConnectAsync(Dcs.SocketPath!);
        File.WriteAllText(Path.Combine(Dcs.BaseDirectory, "www", "index.html"), "<html>index</html>");
        File.WriteAllText(Path.Combine(Dcs.BaseDirectory, "www", "app.js"), "console.log(1)");
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _connection?.Dispose();
    }

    [Test]
    public async Task StaticFilesAndFallback()
    {
        // The index file is not cached, all other files for MaxAge seconds (Program.cs:120-131)
        using (HttpResponseMessage response = await _client.GetAsync(""))
        {
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("<html>index</html>"));
            Assert.That(response.Headers.CacheControl!.NoStore, Is.True);
            Assert.That(response.Headers.Pragma.ToString(), Is.EqualTo("no-cache"));
        }
        using (HttpResponseMessage response = await _client.GetAsync("app.js"))
        {
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("console.log(1)"));
            Assert.That(response.Headers.CacheControl!.MaxAge, Is.EqualTo(TimeSpan.FromSeconds(3600)));
        }

        // Client-side routes get the index file, including Explorer and Jobs paths with dots (FallbackMiddleware.cs:30-39)
        foreach (string route in new[] { "Settings/General", "Explorer/edit/sys/config.g", "Jobs/0.4mm%20Nozzle" })
        {
            Assert.That(await _client.GetStringAsync(route), Is.EqualTo("<html>index</html>"), route);
        }

        // Missing files with an extension, and unknown rr_ and machine requests, are 404
        foreach (string missing in new[] { "missing.css", "rr_missing", "machine/missing" })
        {
            using HttpResponseMessage response = await _client.GetAsync(missing);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), missing);
        }
    }

    [Test]
    public async Task SymlinkedFilesHaveTheirFullLength()
    {
        // DuetFileProvider.cs:47-70 resolves symlinks so that the content length is the one of the target
        string target = Path.Combine(Dcs.BaseDirectory, "sys", "linked.js");
        File.WriteAllText(target, new string('x', 5000));
        File.CreateSymbolicLink(Path.Combine(Dcs.BaseDirectory, "www", "link.js"), target);

        using HttpResponseMessage response = await _client.GetAsync("link.js");
        Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(5000));
        Assert.That(await response.Content.ReadAsStringAsync(), Has.Length.EqualTo(5000));
    }

    [Test]
    public async Task WebDirectoryCanChange()
    {
        // directories.web is resolved again when it changes (ModelObserver.cs:206-222) and the file provider follows (DuetFileProvider.cs:105-111)
        Directory.CreateDirectory(Path.Combine(Dcs.BaseDirectory, "www2"));
        File.WriteAllText(Path.Combine(Dcs.BaseDirectory, "www2", "index.html"), "<html>second</html>");
        int logStart = Dws.Log.Count;
        await SetWebDirectoryAsync("0:/www2");
        try
        {
            await WaitUntilAsync(() => Dws.Log.Since(logStart).Any(e => e.Message.StartsWith("Web directory changed to ") && e.Message.EndsWith("www2")), "DWS did not change the web directory");
            Assert.That(await _client.GetStringAsync(""), Is.EqualTo("<html>second</html>"));
        }
        finally
        {
            logStart = Dws.Log.Count;
            await SetWebDirectoryAsync("0:/www");
            await WaitUntilAsync(() => Dws.Log.Since(logStart).Any(e => e.Message.StartsWith("Web directory changed to ")), "DWS did not restore the web directory");
        }
        Assert.That(await _client.GetStringAsync(""), Is.EqualTo("<html>index</html>"));
    }

    private async Task SetWebDirectoryAsync(string directory)
    {
        using (await Dcs.Model.AccessReadWriteAsync())
        {
            Dcs.Model.Directories.Web = directory;
        }
    }

    /// <summary>
    /// Register an endpoint and wait until DWS has it (ModelObserver.cs:169-182)
    /// </summary>
    private async Task<HttpEndpointUnixSocket> AddEndpointAsync(HttpEndpointType type, string ns, string path, bool isUploadRequest, Func<HttpEndpointConnection, Task> handler)
    {
        int logStart = Dws.Log.Count;
        HttpEndpointUnixSocket endpoint = await _connection.AddHttpEndpointAsync(type, ns, path, isUploadRequest);
        endpoint.OnEndpointRequestReceived += async (socket, connection) =>
        {
            try
            {
                await handler(connection);
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The request has been aborted
            }
        };
        await WaitUntilAsync(() => Dws.Log.Since(logStart).Any(e => e.Message == $"Registered HTTP {type} endpoint via /machine/{ns}/{path}"), "DWS did not register the endpoint");
        return endpoint;
    }

    private async Task RemoveEndpointAsync(HttpEndpointUnixSocket endpoint)
    {
        int logStart = Dws.Log.Count;
        await _connection.RemoveHttpEndpointAsync(endpoint.EndpointType, endpoint.Namespace, endpoint.EndpointPath);
        endpoint.Dispose();
        await WaitUntilAsync(() => Dws.Log.Since(logStart).Any(e => e.Message.StartsWith("New number of custom HTTP endpoints")), "DWS did not remove the endpoint");
    }

    [Test]
    public async Task CustomEndpointsGetTheRequest()
    {
        TaskCompletionSource<ReceivedHttpRequest> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpEndpointUnixSocket json = await AddEndpointAsync(HttpEndpointType.POST, "test", "json", false, async connection =>
        {
            ReceivedHttpRequest request = await connection.ReadRequestAsync();
            received.TrySetResult(request);
            await connection.SendResponseAsync(201, "{\"echo\":" + JsonSerializer.Serialize(request.Body) + "}", HttpResponseType.JSON);
        });
        HttpEndpointUnixSocket status = await AddEndpointAsync(HttpEndpointType.GET, "test", "status", false, async connection =>
        {
            ReceivedHttpRequest request = await connection.ReadRequestAsync();
            await connection.SendResponseAsync(418, "ignored", HttpResponseType.StatusCode);
        });
        HttpEndpointUnixSocket text = await AddEndpointAsync(HttpEndpointType.GET, "test", "text", false, async connection =>
        {
            ReceivedHttpRequest request = await connection.ReadRequestAsync();
            await connection.SendResponseAsync(200, $"session {request.SessionId}", HttpResponseType.PlainText);
        });
        try
        {
            // The body, headers, query, content type, session and the remote end are passed on (CustomEndpointMiddleware.cs:250-276)
            string key = await ConnectAsync(_client);
            int sessionId = SessionStorage.GetSessionId(key);
            using (HttpResponseMessage response = await SendAsync(_client, HttpMethod.Post, "machine/test/json?a=1&b=x", key, new StringContent("payload ä", Encoding.UTF8, "text/plain")))
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
                Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/json"));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("{\"echo\":\"payload \\u00E4\"}"));
            }
            ReceivedHttpRequest request = await received.Task.WaitAsync(FakeFirmware.FakeFirmwareHost.DefaultTimeout);
            Assert.That(request.Body, Is.EqualTo("payload ä"));
            Assert.That(request.SessionId, Is.EqualTo(sessionId));
            Assert.That(request.Queries["a"], Is.EqualTo("1"));
            Assert.That(request.Headers["X-Session-Key"], Is.EqualTo(key));
            Assert.That(request.ContentType, Does.StartWith("text/plain"));
            Assert.That(request.RemoteIPAddress, Is.EqualTo(ClientAddress(0).ToString()));

            // A status code response has no body, plain text has its content type
            using (HttpResponseMessage response = await _client.GetAsync("machine/test/status"))
            {
                Assert.That((int)response.StatusCode, Is.EqualTo(418));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty);
            }

            // Custom endpoints are not covered by the authorization policies, anonymous requests get session ID -1 (ReceivedHttpRequest.SessionId)
            using (HttpResponseMessage response = await SendAsync(_client, HttpMethod.Get, "machine/test/text", "invalid-key"))
            {
                Assert.That(response.Content.Headers.ContentType!.ToString(), Is.EqualTo("text/plain; charset=utf-8"));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("session -1"));
            }

            // Only the registered method is forwarded
            using (HttpResponseMessage response = await _client.PostAsync("machine/test/text", new StringContent("x")))
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            }
        }
        finally
        {
            await RemoveEndpointAsync(json);
            await RemoveEndpointAsync(status);
            await RemoveEndpointAsync(text);
        }

        using HttpResponseMessage removed = await _client.GetAsync("machine/test/text");
        Assert.That(removed.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task CustomUploadsFilesAndRedirects()
    {
        // An upload request gets the body as temporary file (CustomEndpointMiddleware.cs:233-249), a file response is sent back as it is
        string? uploadedFile = null;
        HttpEndpointUnixSocket upload = await AddEndpointAsync(HttpEndpointType.PUT, "test", "upload", true, async connection =>
        {
            ReceivedHttpRequest request = await connection.ReadRequestAsync();
            uploadedFile = request.Body;
            await connection.SendResponseAsync(200, request.Body, HttpResponseType.File);
        });

        // A URI response makes DWS fetch that URI and pass the answer on (CustomEndpointMiddleware.cs:291-330)
        HttpEndpointUnixSocket redirect = await AddEndpointAsync(HttpEndpointType.GET, "test", "redirect", false, async connection =>
        {
            await connection.ReadRequestAsync();
            await connection.SendResponseAsync(200, new Uri(Dws.BaseUri, "app.js").ToString(), HttpResponseType.URI);
        });
        try
        {
            byte[] content = new byte[10000];
            new Random(4).NextBytes(content);
            using (HttpResponseMessage response = await _client.PutAsync("machine/test/upload", new ByteArrayContent(content)))
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/octet-stream"));
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(content));
            }
            Assert.That(uploadedFile, Is.Not.Null);
            File.Delete(uploadedFile!);

            using (HttpResponseMessage response = await _client.GetAsync("machine/test/redirect"))
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("console.log(1)"));
            }
        }
        finally
        {
            await RemoveEndpointAsync(upload);
            await RemoveEndpointAsync(redirect);
        }
    }

    [Test]
    public async Task CustomWebSockets()
    {
        // Each WebSocket message is passed on as request with the session of the sessionKey parameter, each response is sent back,
        // and a status code from 1000 on closes the socket (CustomEndpointMiddleware.cs:70-112, 148-205)
        HttpEndpointUnixSocket endpoint = await AddEndpointAsync(HttpEndpointType.WebSocket, "test", "ws", false, async connection =>
        {
            while (true)
            {
                ReceivedHttpRequest request = await connection.ReadRequestAsync();
                if (request.Body == "bye")
                {
                    await connection.SendResponseAsync(1000, "bye", HttpResponseType.StatusCode);
                    connection.Dispose();
                    return;
                }
                await connection.SendResponseAsync(200, $"{request.SessionId}:{request.Body}", HttpResponseType.PlainText);
            }
        });
        try
        {
            string key = await ConnectAsync(_client);
            int sessionId = SessionStorage.GetSessionId(key);
            (ClientWebSocket? webSocket, HttpStatusCode status) = await TryConnectWebSocketAsync(0, "machine/test/ws?sessionKey=" + key);
            Assert.That(status, Is.EqualTo(HttpStatusCode.SwitchingProtocols));
            using (webSocket)
            {
                await webSocket!.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, CancellationToken.None);
                Assert.That(await ReceiveTextAsync(webSocket), Is.EqualTo($"{sessionId}:hello"));
                await webSocket.SendAsync(Encoding.UTF8.GetBytes("bye"), WebSocketMessageType.Text, true, CancellationToken.None);
                Assert.That(await ReceiveTextAsync(webSocket), Is.Null);
                Assert.That(webSocket.CloseStatus, Is.EqualTo(WebSocketCloseStatus.NormalClosure));
                Assert.That(webSocket.CloseStatusDescription, Is.EqualTo("bye"));
                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }

            // Binary messages close the socket (CustomEndpointMiddleware.cs:161-166)
            (webSocket, status) = await TryConnectWebSocketAsync(0, "machine/test/ws");
            using (webSocket)
            {
                await webSocket!.SendAsync(new byte[] { 0 }, WebSocketMessageType.Binary, true, CancellationToken.None);
                Assert.That(await ReceiveTextAsync(webSocket), Is.Null);
                Assert.That(webSocket.CloseStatus, Is.EqualTo(WebSocketCloseStatus.InvalidMessageType));
                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        }
        finally
        {
            await RemoveEndpointAsync(endpoint);
        }
    }
}
