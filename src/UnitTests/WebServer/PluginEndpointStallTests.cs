using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.WebServer;

using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// Requests for a plugin endpoint whose plugin is alive but does not accept or does not answer (DSF error class census, finding TP1).
/// DWS forwards such a request over the UNIX socket of the endpoint (CustomEndpointMiddleware.cs:62-66, 278-280, HttpEndpointConnection.cs).
/// The plugin listens with a backlog of HttpEndpointUnixSocket.DefaultBacklog = 4 (DuetAPIClient/HttpEndpoints/HttpEndpointUnixSocket.cs:18, 72)
/// and invokes its request handler inline in the accept loop (HttpEndpointUnixSocket.cs:125-160), so a hung, stopped or blocked plugin stops
/// accepting. Linux keeps backlog + 1 connections pending on a UNIX listener that does not accept (measured with .NET 10 on Linux 6.18:
/// backlog 1 takes 2 connects, backlog 4 takes 5)
/// </summary>
[TestFixture]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class PluginEndpointStallTests : WebServerFixture
{
    private HttpClient _client = null!;
    private CommandConnection _connection = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _client = CreateClient(0);
        _connection = new CommandConnection();
        await _connection.ConnectAsync(Dcs.SocketPath!);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _connection?.Dispose();
    }

    /// <summary>
    /// Register an endpoint whose plugin listens on the socket but never accepts, and wait until DWS has it (ModelObserver.cs:169-182)
    /// </summary>
    /// <param name="path">Path of the endpoint in the namespace "stall"</param>
    /// <param name="backlog">Backlog of the listening socket</param>
    /// <returns>Listening socket</returns>
    private async Task<Socket> AddEndpointThatDoesNotAcceptAsync(string path, int backlog)
    {
        int logStart = Dws.Log.Count;
        string socketPath;
        using (HttpEndpointUnixSocket endpoint = await _connection.AddHttpEndpointAsync(HttpEndpointType.GET, "stall", path))
        {
            // Take over the socket file DCS assigned: the endpoint of DuetAPIClient deletes it on dispose
            socketPath = endpoint.SocketPath;
        }
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(backlog);
        await WaitUntilAsync(() => Dws.Log.Since(logStart).Any(e => e.Message == $"Registered HTTP GET endpoint via /machine/stall/{path}"), "DWS did not register the endpoint");
        return listener;
    }

    /// <summary>
    /// Register an endpoint with a request handler and wait until DWS has it, like MiddlewareTests.AddEndpointAsync
    /// </summary>
    private async Task<HttpEndpointUnixSocket> AddEndpointAsync(string path, Func<HttpEndpointConnection, Task> handler)
    {
        int logStart = Dws.Log.Count;
        HttpEndpointUnixSocket endpoint = await _connection.AddHttpEndpointAsync(HttpEndpointType.GET, "stall", path);
        endpoint.OnEndpointRequestReceived += async (socket, connection) =>
        {
            try
            {
                await handler(connection);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The request has been aborted
            }
        };
        await WaitUntilAsync(() => Dws.Log.Since(logStart).Any(e => e.Message == $"Registered HTTP GET endpoint via /machine/stall/{path}"), "DWS did not register the endpoint");
        return endpoint;
    }

    private async Task RemoveEndpointAsync(string path)
    {
        int logStart = Dws.Log.Count;
        await _connection.RemoveHttpEndpointAsync(HttpEndpointType.GET, "stall", path);
        await WaitUntilAsync(() => Dws.Log.Since(logStart).Any(e => e.Message.StartsWith("New number of custom HTTP endpoints")), "DWS did not remove the endpoint");
    }

    /// <summary>
    /// Number of requests for an endpoint that DWS has started or finished since the given log position, as the ASP.NET Core hosting layer
    /// logs them ("Request starting ..." with event ID 1, "Request finished ..." with event ID 2)
    /// </summary>
    private int CountRequests(int logStart, string path, int eventId) => Dws.Log.Since(logStart).Count(e =>
        e.Category == "Microsoft.AspNetCore.Hosting.Diagnostics" && e.EventId.Id == eventId && e.Message.Contains($"/machine/stall/{path} "));

    /// <summary>
    /// Read from a connection until the other end closes it
    /// </summary>
    /// <param name="socket">Connection</param>
    /// <param name="timeout">Maximum time to wait</param>
    /// <returns>True if the other end closed the connection in time</returns>
    private static async Task<bool> IsClosedByPeerAsync(Socket socket, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        byte[] buffer = new byte[4096];
        try
        {
            while (await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token) > 0) { }
            return true;
        }
        catch (SocketException)
        {
            // Reset by the peer
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Accept the connections that are pending on a listener
    /// </summary>
    /// <param name="listener">Listening socket</param>
    /// <param name="window">Time to wait for another connection</param>
    /// <returns>Accepted connections</returns>
    private static async Task<List<Socket>> AcceptPendingAsync(Socket listener, TimeSpan window)
    {
        List<Socket> accepted = [];
        while (true)
        {
            using CancellationTokenSource cts = new(window);
            try
            {
                accepted.Add(await listener.AcceptAsync(cts.Token));
            }
            catch (OperationCanceledException)
            {
                return accepted;
            }
        }
    }

    [Test]
    public async Task RequestThatThePluginDoesNotAnswerEndsWhenTheClientAborts()
    {
        TaskCompletionSource requestReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource closedByDws = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource stopPlugin = new();
        using HttpEndpointUnixSocket endpoint = await AddEndpointAsync("noanswer", async connection =>
        {
            using (connection)
            {
                await connection.ReadRequestAsync();
                requestReceived.TrySetResult();

                // The plugin accepted and read the request but never answers. JsonHelper.ReceiveUtf8JsonAsync throws a SocketException when
                // DWS closes its end of the connection
                try
                {
                    await connection.ReadRequestAsync(stopPlugin.Token);
                }
                catch (SocketException)
                {
                    closedByDws.TrySetResult();
                }
            }
        });
        try
        {
            using CancellationTokenSource abort = new();
            Task<HttpResponseMessage> request = _client.GetAsync("machine/stall/noanswer", abort.Token);
            await requestReceived.Task.WaitAsync(FakeFirmwareHost.DefaultTimeout);
            abort.Cancel();
            Assert.That(async () => await request, Throws.InstanceOf<OperationCanceledException>());

            // DWS waits for the response with context.RequestAborted, so it ends the request and closes the connection to the plugin
            bool closed = await Task.WhenAny(closedByDws.Task, Task.Delay(FakeFirmwareHost.DefaultTimeout)) == closedByDws.Task;
            Assert.That(closed, Is.True, "DWS did not end the request after the client aborted it");
        }
        finally
        {
            stopPlugin.Cancel();
            await RemoveEndpointAsync("noanswer");
        }
    }

    [Test]
    public async Task RequestsInTheBacklogOfAnEndpointEndWhenTheClientAborts()
    {
        // As many requests as the backlog of a plugin that never accepts holds
        const int backlog = 1, requests = backlog + 1;
        int logStart = Dws.Log.Count;
        using Socket listener = await AddEndpointThatDoesNotAcceptAsync("backlog", backlog);
        using CancellationTokenSource abort = new();
        Task<HttpResponseMessage>[] tasks = [.. Enumerable.Range(0, requests).Select(_ => _client.GetAsync("machine/stall/backlog", abort.Token))];
        List<Socket> accepted = [];
        try
        {
            await WaitUntilAsync(() => CountRequests(logStart, "backlog", 1) == requests, "DWS did not start the requests");
            abort.Cancel();

            // DWS ends every request when the client aborts it, instead of waiting for a response that never comes
            await WaitUntilAsync(() => CountRequests(logStart, "backlog", 2) == requests, "DWS did not end the requests in the backlog after the client aborted them");

            // and closes its end of the connection to the plugin, which the plugin finds closed once it accepts it
            accepted = await AcceptPendingAsync(listener, TimeSpan.FromMilliseconds(500));
            Assert.That(accepted, Has.Count.LessThanOrEqualTo(requests));
            foreach (Socket connection in accepted)
            {
                Assert.That(await IsClosedByPeerAsync(connection, FakeFirmwareHost.DefaultTimeout), Is.True, "DWS did not close the connection of an aborted request");
            }
        }
        finally
        {
            abort.Cancel();
            foreach (Socket connection in accepted)
            {
                connection.Dispose();
            }
            listener.Dispose();
            await RemoveEndpointAsync("backlog");
        }
    }
}
