using System.Runtime.Versioning;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using WebServerEndpointConnection = DuetWebServer.Middleware.HttpEndpointConnection;

namespace UnitTests.IPC;

/// <summary>
/// A plugin registers an HTTP endpoint with AddHttpEndpoint and serves the UNIX socket that DCS assigns to it
/// (Documentation/articles/ipc.md, "Custom HTTP endpoints"). DuetWebServer forwards a request as ReceivedHttpRequest and relays the
/// SendHttpResponse of the plugin; its own connection class (DuetWebServer/Middleware/HttpEndpointConnection.cs) stands in for DWS here
/// </summary>
[TestFixture]
[UnsupportedOSPlatform("windows")]
public class IpcHttpEndpoints
{
    private FakeFirmwareHost _host = null!;
    private CommandConnection _connection = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await FakeFirmwareHost.StartAsync(ipc: true);
        _connection = new CommandConnection();
        await _connection.ConnectAsync(_host.SocketPath);
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        _connection.Dispose();
        await _host.DisposeAsync();
    }

    /// <summary>
    /// Forward a request to an endpoint socket like DWS does
    /// </summary>
    private static async Task<SendHttpResponse> RequestAsync(string socketPath, ReceivedHttpRequest request)
    {
        using WebServerEndpointConnection webServer = new();
        webServer.Connect(socketPath);
        await webServer.SendHttpRequest(request);
        return await webServer.GetHttpResponse().AsTask().WaitAsync(FakeFirmwareHost.DefaultTimeout);
    }

    [Test]
    public async Task PluginAnswersRequestsOnItsEndpoint()
    {
        using HttpEndpointUnixSocket endpoint = await _connection.AddHttpEndpointAsync(HttpEndpointType.GET, "ipcendpoints", "status");
        endpoint.OnEndpointRequestReceived += (socket, connection) => _ = Task.Run(async () =>
        {
            ReceivedHttpRequest request = await connection.ReadRequestAsync();
            await connection.SendResponseAsync(200, $"hello {request.Queries["name"]} from {request.RemoteIPAddress}", HttpResponseType.PlainText);
        });

        SendHttpResponse response = await RequestAsync(endpoint.SocketPath, new ReceivedHttpRequest { RemoteIPAddress = "127.0.0.1", Queries = { ["name"] = "DWS" } });
        Assert.That(response.StatusCode, Is.EqualTo(200));
        Assert.That(response.ResponseType, Is.EqualTo(HttpResponseType.PlainText));
        Assert.That(response.Response, Is.EqualTo("hello DWS from 127.0.0.1"));

        // Every request comes on a connection of its own, which the plugin closes after the response (HttpEndpointConnection.SendResponse)
        response = await RequestAsync(endpoint.SocketPath, new ReceivedHttpRequest { Queries = { ["name"] = "again" } });
        Assert.That(response.Response, Is.EqualTo("hello again from "));
        Assert.That(await _connection.RemoveHttpEndpointAsync(HttpEndpointType.GET, "ipcendpoints", "status"), Is.True);
    }

    [Test]
    public async Task SynchronousPluginAnswersRequests()
    {
        using HttpEndpointUnixSocket endpoint = _connection.AddHttpEndpoint(HttpEndpointType.POST, "ipcendpoints", "sync");
        endpoint.OnEndpointRequestReceived += (socket, connection) => _ = Task.Run(() =>
        {
            ReceivedHttpRequest request = connection.ReadRequest();
            connection.SendResponse(201, request.Body, HttpResponseType.JSON);
            Assert.That(connection.IsConnected, Is.False, "A plain HTTP endpoint connection is closed after its response");
        });

        SendHttpResponse response = await RequestAsync(endpoint.SocketPath, new ReceivedHttpRequest { ContentType = "application/json", Body = "{\"a\":1}" });
        Assert.That(response.StatusCode, Is.EqualTo(201));
        Assert.That(response.ResponseType, Is.EqualTo(HttpResponseType.JSON));
        Assert.That(response.Response, Is.EqualTo("{\"a\":1}"));
        Assert.That(_connection.RemoveHttpEndpoint(HttpEndpointType.POST, "ipcendpoints", "sync"), Is.True);
    }

    [Test]
    public async Task EndpointWithoutHandlerAnswersWithAnError()
    {
        // HttpEndpointUnixSocket.AcceptConnectionsAsync answers 500 and closes the connection right after accepting it if the plugin has not
        // registered a handler. It does not read the request, so the request is not sent here: its send could fail when the plugin has closed already
        using HttpEndpointUnixSocket endpoint = await _connection.AddHttpEndpointAsync(HttpEndpointType.GET, "ipcendpoints", "nohandler");
        using WebServerEndpointConnection webServer = new();
        webServer.Connect(endpoint.SocketPath);
        SendHttpResponse response = await webServer.GetHttpResponse().AsTask().WaitAsync(FakeFirmwareHost.DefaultTimeout);
        Assert.That(response.StatusCode, Is.EqualTo(500));
        Assert.That(response.Response, Is.EqualTo("No event handler registered"));
        await _connection.RemoveHttpEndpointAsync(HttpEndpointType.GET, "ipcendpoints", "nohandler");
    }

    [Test]
    public async Task WebSocketEndpointKeepsItsConnection()
    {
        // A WebSocket endpoint may send any number of messages on one connection (HttpEndpointConnection: Close only if !isWebSocket)
        using HttpEndpointUnixSocket endpoint = await _connection.AddHttpEndpointAsync(HttpEndpointType.WebSocket, "ipcendpoints", "socket");
        endpoint.OnEndpointRequestReceived += (socket, connection) => _ = Task.Run(async () =>
        {
            using (connection)
            {
                await connection.ReadRequestAsync();
                await connection.SendResponseAsync(200, "first", HttpResponseType.PlainText);
                Assert.That(connection.IsConnected, Is.True);
                await connection.SendResponseAsync(200, "second", HttpResponseType.PlainText);
            }
        });

        using WebServerEndpointConnection webServer = new();
        webServer.Connect(endpoint.SocketPath);
        await webServer.SendHttpRequest(new ReceivedHttpRequest());
        Assert.That((await webServer.GetHttpResponse().AsTask().WaitAsync(FakeFirmwareHost.DefaultTimeout)).Response, Is.EqualTo("first"));
        Assert.That((await webServer.GetHttpResponse().AsTask().WaitAsync(FakeFirmwareHost.DefaultTimeout)).Response, Is.EqualTo("second"));
        await _connection.RemoveHttpEndpointAsync(HttpEndpointType.WebSocket, "ipcendpoints", "socket");
        Assert.That(System.IO.File.Exists(endpoint.SocketPath), Is.True, "The plugin owns the socket file until it disposes the endpoint");
    }
}
