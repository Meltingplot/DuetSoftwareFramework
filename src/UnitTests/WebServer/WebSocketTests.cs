using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.WebServer;

using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// Tests of WS /machine (DuetWebServer/Endpoints/WebSocketEndpoint.cs) against the DSF REST API wiki page: the server sends the full model,
/// then a patch after each "OK\n" from the client, and answers "PING\n" with "PONG\n"
/// </summary>
[TestFixture]
public class WebSocketTests : WebServerFixture
{
    private static async Task SendTextAsync(WebSocket webSocket, string text) =>
        await webSocket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private async Task<ClientWebSocket> OpenAsync(string pathAndQuery = "machine", Action<ClientWebSocketOptions>? configure = null)
    {
        (ClientWebSocket? webSocket, HttpStatusCode status) = await TryConnectWebSocketAsync(0, pathAndQuery);
        Assert.That(status, Is.EqualTo(HttpStatusCode.SwitchingProtocols));
        return webSocket!;
    }

    /// <summary>
    /// Close a socket from the client side and wait until DWS has released it (WebSocketEndpoint.cs:264). DWS does not answer the close
    /// frame (see WebServerFindings.ClientCloseIsAnswered), so only the client's half is closed here
    /// </summary>
    private async Task CloseFromClientAsync(WebSocket webSocket)
    {
        int disconnected = Dws.Log.Entries.Count(e => e.Message.Contains("WebSocket disconnected from"));
        await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await WaitUntilAsync(() => Dws.Log.Entries.Count(e => e.Message.Contains("WebSocket disconnected from")) > disconnected, "DWS did not release the socket");
    }

    /// <summary>
    /// Wait for the server to close the socket and return the close status
    /// </summary>
    private static async Task<(WebSocketCloseStatus?, string?)> ReceiveCloseAsync(WebSocket webSocket)
    {
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        byte[] buffer = new byte[65536];
        while (true)
        {
            WebSocketReceiveResult result = await webSocket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Complete the closing handshake, the server waits for it
                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
                return (result.CloseStatus, result.CloseStatusDescription);
            }
        }
    }

    [Test]
    public async Task ModelPatchesAndPing()
    {
        using ClientWebSocket webSocket = await OpenAsync("machine?verbose=true&obsolete=true");
        using (JsonDocument model = JsonDocument.Parse((await ReceiveTextAsync(webSocket))!))
        {
            Assert.That(model.RootElement.TryGetProperty("boards", out _), Is.True);
        }

        // "PING\n" is answered with "PONG\n" (WebSocketEndpoint.cs:309-316)
        await SendTextAsync(webSocket, "PING\n");
        Assert.That(await ReceiveTextAsync(webSocket), Is.EqualTo("PONG\n"));

        // After "OK\n" the next change of the model is sent as a patch (WebSocketEndpoint.cs:337-356)
        await SendTextAsync(webSocket, "OK\n");
        Dcs.Firmware.ObjectModel.Set("state.upTime", 4711);
        string? patch = await ReceiveTextAsync(webSocket);
        using (JsonDocument json = JsonDocument.Parse(patch!))
        {
            Assert.That(json.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object));
        }

        // Several lines in one message and blank lines are fine
        await SendTextAsync(webSocket, "\r\nPING\r\n\n");
        Assert.That(await ReceiveTextAsync(webSocket), Is.EqualTo("PONG\n"));

        await CloseFromClientAsync(webSocket);
    }

    [Test]
    public async Task InvalidMessagesCloseTheSocket()
    {
        // WebSocketEndpoint.cs:288-299 and 317-321
        using (ClientWebSocket webSocket = await OpenAsync())
        {
            await ReceiveTextAsync(webSocket);
            await SendTextAsync(webSocket, "HELLO\n");
            Assert.That(await ReceiveCloseAsync(webSocket), Is.EqualTo(((WebSocketCloseStatus?)WebSocketCloseStatus.InvalidMessageType, "Unsupported command. The only supported commands are 'OK' and 'PING'")));
        }
        using (ClientWebSocket webSocket = await OpenAsync())
        {
            await ReceiveTextAsync(webSocket);
            await webSocket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);
            Assert.That(await ReceiveCloseAsync(webSocket), Is.EqualTo(((WebSocketCloseStatus?)WebSocketCloseStatus.InvalidMessageType, "Only text commands are supported")));
        }
        using (ClientWebSocket webSocket = await OpenAsync())
        {
            await ReceiveTextAsync(webSocket);
            await SendTextAsync(webSocket, new string('O', 200));
            Assert.That(await ReceiveCloseAsync(webSocket), Is.EqualTo(((WebSocketCloseStatus?)WebSocketCloseStatus.InvalidPayloadData, "Message is too long")));
        }
        await WaitUntilAsync(() => Dws.Log.Entries.Count(e => e.Message.Contains("WebSocket disconnected from")) >= 3, "The sockets were not closed");
    }

    [Test]
    public async Task HttpRequestsAndForeignOriginsAreRejected()
    {
        // "If the query is no WebSocket request, HTTP code 400 is returned"
        using HttpClient client = CreateClient(0);
        using (HttpResponseMessage response = await client.GetAsync("machine"))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        // An Origin header must match the Host header or the CORS site (ModelObserver.CheckWebSocketOrigin)
        Assert.That(await ConnectWithOriginAsync("http://evil.example"), Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await ConnectWithOriginAsync($"http://{Dws.BaseUri.Host}:{Dws.BaseUri.Port}"), Is.EqualTo(HttpStatusCode.SwitchingProtocols));

        // The CORS site of the object model is accepted as well (ModelObserver.cs:153-166)
        int logStart = Dws.Log.Count;
        await SetCorsSiteAsync("http://*.allowed.example");
        await WaitUntilAsync(() => Dws.Log.Since(logStart).Any(e => e.Message == "Changing CORS policy to accept site 'http://*.allowed.example'"), "DWS did not take the CORS site");
        try
        {
            Assert.That(await ConnectWithOriginAsync("http://printer.allowed.example"), Is.EqualTo(HttpStatusCode.SwitchingProtocols));
            Assert.That(await ConnectWithOriginAsync("http://printer.other.example"), Is.EqualTo(HttpStatusCode.Forbidden));
        }
        finally
        {
            logStart = Dws.Log.Count;
            await SetCorsSiteAsync(null);
            await WaitUntilAsync(() => Dws.Log.Since(logStart).Any(e => e.Message == "Reset CORS policy"), "DWS did not reset the CORS policy");
        }
        Assert.That(await ConnectWithOriginAsync("http://printer.allowed.example"), Is.EqualTo(HttpStatusCode.Forbidden));
    }

    private async Task SetCorsSiteAsync(string? site)
    {
        using (await Dcs.Model.AccessReadWriteAsync())
        {
            Dcs.Model.Network.CorsSite = site;
        }
    }

    private async Task<HttpStatusCode> ConnectWithOriginAsync(string origin)
    {
        using ClientWebSocket webSocket = new();
        webSocket.Options.CollectHttpResponseDetails = true;
        webSocket.Options.SetRequestHeader("Origin", origin);
        using HttpMessageInvoker invoker = new(CreateHandler(0));
        try
        {
            await webSocket.ConnectAsync(new Uri($"ws://{Dws.BaseUri.Host}:{Dws.BaseUri.Port}/machine"), invoker, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            return webSocket.HttpStatusCode;
        }
        await ReceiveTextAsync(webSocket);
        await CloseFromClientAsync(webSocket);
        return HttpStatusCode.SwitchingProtocols;
    }

    [Test]
    public async Task PasswordRequiresSessionKey()
    {
        await SetPasswordAsync("ws-secret");
        try
        {
            // "In case password protection is enabled and the session key is missing or invalid, HTTP code 403 is returned"
            Assert.That((await TryConnectWebSocketAsync(1, "machine")).Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That((await TryConnectWebSocketAsync(1, "machine?sessionKey=invalid")).Status, Is.EqualTo(HttpStatusCode.Forbidden));

            using HttpClient client = CreateClient(1);
            string key = await ConnectAsync(client, "ws-secret");
            (ClientWebSocket? webSocket, HttpStatusCode status) = await TryConnectWebSocketAsync(1, "machine?sessionKey=" + key);
            Assert.That(status, Is.EqualTo(HttpStatusCode.SwitchingProtocols));
            using (webSocket)
            {
                await ReceiveTextAsync(webSocket!);

                // An open socket keeps its session from expiring (SessionStorage.cs:397)
                ExpireIdleSessions();
                Assert.That(SessionStorage.CheckSessionKey(key, false), Is.True);

                await CloseFromClientAsync(webSocket!);
                await WaitUntilAsync(() => Dws.Log.Entries.Any(e => e.Message == $"Session {key} unregistered a WebSocket connection"), "The socket was not unregistered");
            }
            ExpireIdleSessions();
            Assert.That(SessionStorage.CheckSessionKey(key, false), Is.False);
        }
        finally
        {
            await SetPasswordAsync(DuetAPI.Connection.Defaults.Password);
        }
    }
}
