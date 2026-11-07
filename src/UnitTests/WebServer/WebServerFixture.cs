using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetWebServer.Singletons;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.WebServer;

using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// Base class for fixtures that run DuetWebServer against DCS with the fake firmware, connected over the DCS IPC socket like on the SBC
/// </summary>
public abstract class WebServerFixture
{
    /// <summary>
    /// DCS with the fake firmware
    /// </summary>
    protected FakeFirmwareHost Dcs { get; private set; } = null!;

    /// <summary>
    /// DuetWebServer
    /// </summary>
    protected DwsHost Dws { get; private set; } = null!;

    /// <summary>
    /// Change the DWS settings of this fixture
    /// </summary>
    /// <param name="config">Configuration file content, keys as in DuetWebServer/Settings.cs</param>
    protected virtual void ConfigureDws(JsonObject config) { }

    /// <summary>
    /// Set up DCS before DWS starts, e.g. set a machine password
    /// </summary>
    /// <returns>Asynchronous task</returns>
    protected virtual Task ConfigureDcsAsync() => Task.CompletedTask;

    /// <summary>
    /// Directory of the DCS IPC socket and the custom HTTP endpoint sockets. It lies outside the virtual SD card like /run/dsf on the SBC,
    /// so that file requests cannot move or delete the sockets
    /// </summary>
    protected string SocketDirectory { get; } = Path.Combine(Path.GetTempPath(), "dsf-sock-" + Guid.NewGuid().ToString("N"));

    [OneTimeSetUp]
    public async Task StartServersAsync()
    {
        Dcs = await FakeFirmwareHost.StartAsync(configureSettings: settings => settings.SocketDirectory = SocketDirectory, ipc: true);
        await ConfigureDcsAsync();
        Dws = await DwsHost.StartAsync(Dcs.SocketPath!, Path.Combine(Dcs.BaseDirectory, "www"), ConfigureDws);

        // The model observer of DWS caches messages for rr_reply and resolves the web directory, wait for it to connect (ModelObserver.cs:102)
        await WaitUntilAsync(() => Dws.Log.Entries.Any(e => e.Message == "Connections to DuetControlServer established"), "DWS did not connect to DCS");
    }

    [OneTimeTearDown]
    public async Task StopServersAsync()
    {
        try
        {
            if (Dws is not null)
            {
                Dws.AssertNoUnhandledException();
            }
        }
        finally
        {
            if (Dws is not null)
            {
                await Dws.DisposeAsync();
            }
            if (Dcs is not null)
            {
                await Dcs.DisposeAsync();
            }
            if (Directory.Exists(SocketDirectory))
            {
                Directory.Delete(SocketDirectory, true);
            }
        }
    }

    /// <summary>
    /// Set the machine password like M551 does (MCodeHandler.cs, case 551)
    /// </summary>
    /// <param name="password">New password, "reprap" (Defaults.Password) or empty for none (CheckPassword.cs:21)</param>
    /// <returns>Asynchronous task</returns>
    protected Task SetPasswordAsync(string password) => Dcs.ExecuteAsync($"M551 P\"{password}\"");

    /// <summary>
    /// User sessions in the DCS object model (sbc.dsf.userSessions)
    /// </summary>
    /// <returns>Copy of the user sessions</returns>
    protected async Task<DcsUserSession[]> GetUserSessionsAsync()
    {
        using (await Dcs.Model.AccessReadOnlyAsync())
        {
            return [.. Dcs.Model.SBC!.DSF.UserSessions.Select(session => new DcsUserSession(session.Id, session.Origin, session.AccessLevel, session.SessionType))];
        }
    }

    /// <summary>
    /// Remove all DWS sessions and DCS user sessions left behind by a previous test
    /// </summary>
    /// <returns>Asynchronous task</returns>
    protected async Task ClearSessionsAsync()
    {
        int before = (await GetUserSessionsAsync()).Length, logStart = Dws.Log.Count;
        ExpireIdleSessions();

        // Every expired session is removed from DCS by a connection of its own (SessionStorage.cs:404). When more sessions expire at once
        // than the DCS IPC backlog takes, some of these connections fail and the user sessions stay in DCS (see
        // WebServerFindings.ExpiredSessionsAreRemovedFromDcs), so wait for all of them to finish and remove the rest directly
        string category = typeof(DuetWebServer.Singletons.SessionStorage).FullName!;
        int expired = Dws.Log.Since(logStart).Count(e => e.Category == category && e.Message.EndsWith(" expired"));
        await WaitUntilAsync(async () =>
        {
            int failed = Dws.Log.Since(logStart).Count(e => e.Category == category && e.Message == "Failed to unregister expired user session");
            return before - (await GetUserSessionsAsync()).Length + failed >= expired;
        }, "Expired sessions were not unregistered");

        DcsUserSession[] leftOver = await GetUserSessionsAsync();
        if (leftOver.Length > 0)
        {
            using DuetAPIClient.CommandConnection connection = new();
            await connection.ConnectAsync(Dcs.SocketPath!);
            foreach (DcsUserSession session in leftOver)
            {
                await connection.RemoveUserSessionAsync(session.Id);
            }
        }
        Assert.That(await GetUserSessionsAsync(), Is.Empty, "User sessions were not removed");
    }

    /// <summary>
    /// Session storage singleton of DWS
    /// </summary>
    protected ISessionStorage SessionStorage => Dws.Services.GetRequiredService<ISessionStorage>();

    /// <summary>
    /// Let every session expire that has no WebSocket and no running request, as if all clients had been idle for longer than the session
    /// timeout. This calls the maintenance that the SessionExpiry service runs once per second (SessionExpiry.cs:28), with a negative timeout
    /// so that the age of a session does not matter (SessionStorage.cs:397). Expired sessions are removed from DCS in the background
    /// </summary>
    protected void ExpireIdleSessions() => SessionStorage.MaintainSessions(TimeSpan.FromTicks(-1), Dcs.SocketPath!);

    /// <summary>
    /// Log in via GET /machine/connect and return the session key
    /// </summary>
    /// <param name="client">HTTP client</param>
    /// <param name="password">Password or null to leave it out</param>
    /// <returns>Session key</returns>
    protected static async Task<string> ConnectAsync(HttpClient client, string? password = null)
    {
        using HttpResponseMessage response = await client.GetAsync((password is null) ? "machine/connect" : $"machine/connect?password={Uri.EscapeDataString(password)}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("sessionKey").GetString()!;
    }

    /// <summary>
    /// Send a request with an optional session key
    /// </summary>
    /// <param name="client">HTTP client</param>
    /// <param name="method">HTTP method</param>
    /// <param name="uri">Relative URI</param>
    /// <param name="sessionKey">Value of the X-Session-Key header or null to leave it out</param>
    /// <param name="content">Optional content</param>
    /// <returns>Response</returns>
    protected static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string uri, string? sessionKey = null, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, uri) { Content = content };
        if (sessionKey is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Session-Key", sessionKey);
        }
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Create an HTTP client whose connections come from the loopback address 127.0.0.(10 + client), so that DWS sees a client of its own.
    /// DWS keeps IP-based sessions per remote address (SessionStorage.GetTicketFromIpAddress)
    /// </summary>
    /// <param name="client">Number of the simulated client</param>
    /// <returns>HTTP client</returns>
    protected HttpClient CreateClient(int client = 0) => new(CreateHandler(client)) { BaseAddress = Dws.BaseUri, Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Create a handler that connects from the loopback address 127.0.0.(10 + client)
    /// </summary>
    /// <param name="client">Number of the simulated client</param>
    /// <returns>HTTP handler</returns>
    public static SocketsHttpHandler CreateHandler(int client)
    {
        IPAddress localAddress = ClientAddress(client);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    socket.Bind(new IPEndPoint(localAddress, 0));
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                    return new NetworkStream(socket, true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    /// <summary>
    /// Loopback address of a simulated client
    /// </summary>
    /// <param name="client">Number of the simulated client</param>
    /// <returns>Address the client connects from</returns>
    public static IPAddress ClientAddress(int client) => new([127, 0, 0, (byte)(10 + client)]);

    /// <summary>
    /// Invokers for WebSocket connections per simulated client, kept until the fixture ends because the sockets use their connections
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, HttpMessageInvoker> _webSocketInvokers = new();

    [OneTimeTearDown]
    public void DisposeWebSocketInvokers()
    {
        foreach (HttpMessageInvoker invoker in _webSocketInvokers.Values)
        {
            invoker.Dispose();
        }
    }

    /// <summary>
    /// Open a WebSocket to DWS from a simulated client
    /// </summary>
    /// <param name="client">Number of the simulated client</param>
    /// <param name="pathAndQuery">Path and query, e.g. machine?sessionKey=...</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Connected WebSocket or null and the HTTP status code of the handshake response</returns>
    protected async Task<(ClientWebSocket? WebSocket, HttpStatusCode Status)> TryConnectWebSocketAsync(int client, string pathAndQuery, CancellationToken cancellationToken = default)
    {
        ClientWebSocket webSocket = new();
        webSocket.Options.CollectHttpResponseDetails = true;
        Uri uri = new($"ws://{Dws.BaseUri.Host}:{Dws.BaseUri.Port}/{pathAndQuery}");
        HttpMessageInvoker invoker = _webSocketInvokers.GetOrAdd(client, c => new HttpMessageInvoker(CreateHandler(c)));
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(FakeFirmwareHost.DefaultTimeout);
        try
        {
            await webSocket.ConnectAsync(uri, invoker, cts.Token);
            return (webSocket, HttpStatusCode.SwitchingProtocols);
        }
        catch (WebSocketException)
        {
            HttpStatusCode status = webSocket.HttpStatusCode;
            webSocket.Dispose();
            return (null, status);
        }
    }

    /// <summary>
    /// Receive a complete text message
    /// </summary>
    /// <param name="webSocket">WebSocket to read from</param>
    /// <returns>Message or null if the socket was closed</returns>
    protected static async Task<string?> ReceiveTextAsync(WebSocket webSocket)
    {
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        using MemoryStream stream = new();
        byte[] buffer = new byte[16384];
        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Wait for a condition
    /// </summary>
    /// <param name="condition">Condition to wait for</param>
    /// <param name="message">Failure message</param>
    /// <param name="timeout">Timeout or null for the default</param>
    /// <returns>Asynchronous task</returns>
    protected static async Task WaitUntilAsync(Func<bool> condition, string message, TimeSpan? timeout = null)
    {
        await WaitUntilAsync(() => Task.FromResult(condition()), message, timeout);
    }

    /// <summary>
    /// Wait for a condition
    /// </summary>
    /// <param name="condition">Condition to wait for</param>
    /// <param name="message">Failure message</param>
    /// <param name="timeout">Timeout or null for the default</param>
    /// <returns>Asynchronous task</returns>
    protected static async Task WaitUntilAsync(Func<Task<bool>> condition, string message, TimeSpan? timeout = null)
    {
        DateTime end = DateTime.Now + (timeout ?? FakeFirmwareHost.DefaultTimeout);
        while (!await condition())
        {
            if (DateTime.Now > end)
            {
                Assert.Fail(message);
            }
            await Task.Delay(10);
        }
    }
}

/// <summary>
/// Copy of a user session in the DCS object model
/// </summary>
/// <param name="Id">Session ID</param>
/// <param name="Origin">Origin, the remote IP address for HTTP sessions</param>
/// <param name="AccessLevel">Access level</param>
/// <param name="SessionType">Session type</param>
public sealed record DcsUserSession(int Id, string Origin, AccessLevel AccessLevel, SessionType SessionType);
