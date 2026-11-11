using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPIClient;
using DuetControlServer.IPC.Processors;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// Clients that go away in the middle of a command, a code, an interception or a subscription must not leave anything behind in DCS:
/// no registered processor, no blocked code channel and no pending code. Server.ProcessConnectionAsync disposes the connection when the
/// processor ends, and each processor unregisters itself in the finally block of its ProcessAsync method
/// </summary>
[TestFixture]
public class IpcDisconnects
{
    private FakeFirmwareHost _host = null!;
    private IpcLog _log = null!;

    /// <summary>
    /// Whether M7401 is held in the firmware
    /// </summary>
    private volatile bool _hold;

    /// <summary>
    /// Number of times the firmware finished M7401
    /// </summary>
    private int _heldCodesFinished;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await FakeFirmwareHost.StartAsync(firmware =>
        {
            firmware.OnCode("M7400", _ => FakeReply.Ok("fast"));
            firmware.OnCode("M7401", _ =>
            {
                if (_hold)
                {
                    return FakeReply.NotFinished;
                }
                Interlocked.Increment(ref _heldCodesFinished);
                return FakeReply.Ok("held");
            });
            firmware.OnCode("M7402", _ => FakeReply.Ok("from firmware"));
            firmware.OnCode("M7403", _ => FakeReply.Ok("from firmware"));
        }, ipc: true);
        _log = IpcLog.Attach(_host);
    }

    [OneTimeTearDown]
    public async Task StopHost() => await _host.DisposeAsync();

    [SetUp]
    public void ClearLog()
    {
        _hold = false;
        _log.Clear();
    }

    [TearDown]
    public void CheckLog()
    {
        _hold = false;
        Assert.That(_log.UnexpectedExceptions, Is.Empty, "The IPC server ran into an exception it did not expect");
    }

    private Task<string> StartCode(string code, CodeChannel channel) => Task.Run(async () =>
    {
        using CommandConnection connection = new();
        await connection.ConnectAsync(_host.SocketPath);
        return await connection.PerformSimpleCodeAsync(code, channel);
    });

    private async Task<string> ExecuteAsync(string code, CodeChannel channel) => await StartCode(code, channel).WaitAsync(FakeFirmwareHost.DefaultTimeout);

    [Test]
    public async Task CommandClientGoneWhileItsCodeRuns()
    {
        // Command.ProcessAsync awaits the command and only then sends the response, so the code is not cancelled when the client goes.
        // Sending the response fails with a SocketException, which ends the processor without an error (Command.cs:126-130)
        int received = _host.Firmware.GetReceivedCodes(CodeChannel.Telnet).Count(code => code.MajorNumber == 7401);
        int finished = Volatile.Read(ref _heldCodesFinished);
        _hold = true;
        using (RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Command\",\"version\":13}"))
        {
            await client.SendAsync("{\"command\":\"SimpleCode\",\"code\":\"M7401\",\"channel\":\"Telnet\"}");
            await IpcStatics.WaitUntilAsync(() => _host.Firmware.GetReceivedCodes(CodeChannel.Telnet).Count(code => code.MajorNumber == 7401) > received, "the firmware has the code");
        }
        _hold = false;

        // The code finishes once and the channel takes the next code
        Assert.That(await ExecuteAsync("M7400", CodeChannel.Telnet), Is.EqualTo("fast\n"));
        Assert.That(Volatile.Read(ref _heldCodesFinished), Is.EqualTo(finished + 1));
        Assert.That(_host.Firmware.GetReceivedCodes(CodeChannel.Telnet).Count(code => code.MajorNumber == 7401), Is.EqualTo(received + 1), "The code was sent to the firmware once");
    }

    [TestCase(InterceptionMode.Pre)]
    [TestCase(InterceptionMode.Post)]
    [TestCase(InterceptionMode.Executed)]
    public async Task InterceptorGoneWhileIntercepting(InterceptionMode mode)
    {
        // The receive of the next command fails with a SocketException, which ends CodeInterception.ProcessAsync. Its finally block wakes the
        // waiting code, which carries on as if it had been ignored, and removes the interceptor (CodeInterception.cs:203-215)
        InterceptConnection interceptor = new();
        await interceptor.ConnectAsync(mode, [CodeChannel.Telnet], ["M7402"], false, _host.SocketPath);
        int id = interceptor.Id;
        Task<string> reply = StartCode("M7402", CodeChannel.Telnet);
        Code code = await interceptor.ReceiveCodeAsync().AsTask().WaitAsync(FakeFirmwareHost.DefaultTimeout);
        Assert.That(code.MajorNumber, Is.EqualTo(7402));
        Assert.That(IpcStatics.IsInterceptorRegistered(id), Is.True);
        interceptor.Dispose();

        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("from firmware\n"));
        await IpcStatics.WaitUntilAsync(() => !IpcStatics.IsInterceptorRegistered(id), "the interceptor is unregistered");
        Assert.That(await ExecuteAsync("M7402", CodeChannel.Telnet), Is.EqualTo("from firmware\n"));
    }

    [Test]
    public async Task IdleInterceptorGoneIsRemovedByTheNextCode()
    {
        // An idle interceptor waits for a code without reading from its socket (CodeInterception.cs:174), so DCS learns that the client is gone
        // only when it passes the next matching code: the send fails, ProcessAsync ends and wakes the code, which carries on as if it had been ignored
        InterceptConnection interceptor = new();
        await interceptor.ConnectAsync(InterceptionMode.Pre, [CodeChannel.Telnet], ["M7403"], false, _host.SocketPath);
        int id = interceptor.Id;
        interceptor.Dispose();

        Assert.That(await ExecuteAsync("M7403", CodeChannel.Telnet), Is.EqualTo("from firmware\n"));
        await IpcStatics.WaitUntilAsync(() => !IpcStatics.IsInterceptorRegistered(id), "the interceptor is unregistered");
    }

    [TestCase(SubscriptionMode.Patch, true)]
    [TestCase(SubscriptionMode.Patch, false)]
    [TestCase(SubscriptionMode.Full, false)]
    public async Task SubscriberGone(SubscriptionMode mode, bool filtered)
    {
        // ModelSubscription.ProcessAsync swallows the SocketException of the closed connection and its finally block removes the subscription,
        // the verbose and obsolete counts and the observer handler of the Patch mode (ModelSubscription.cs:294-324)
        int handlers = IpcStatics.GetObserverHandlerCount(_host);
        Assume.That(ModelSubscription.VerboseSubscribers || ModelSubscription.ObsoleteSubscribers, Is.False);
        SubscribeConnection subscription = new();
        await subscription.ConnectAsync(mode, filtered ? ["move/axes[*]/homed"] : null, _host.SocketPath, verbose: true, obsolete: true);
        await subscription.GetObjectModelAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);
        int id = subscription.Id;
        Assert.That(IpcStatics.IsSubscriptionRegistered(id), Is.True);
        Assert.That(IpcStatics.GetObserverHandlerCount(_host), Is.EqualTo((mode == SubscriptionMode.Patch) ? handlers + 1 : handlers));
        Assert.That(ModelSubscription.VerboseSubscribers && ModelSubscription.ObsoleteSubscribers, Is.True);
        subscription.Dispose();

        // In Full mode the processor waits for the next update before it writes to the socket again, so keep the model changing
        bool homed = false;
        try
        {
            await IpcStatics.WaitUntilAsync(() => !IpcStatics.IsSubscriptionRegistered(id), "the subscription is unregistered",
                () => _host.Firmware.ObjectModel.Set("move.axes[0].homed", homed = !homed));
        }
        finally
        {
            _host.Firmware.ObjectModel.Set("move.axes[0].homed", false);
        }
        Assert.That(IpcStatics.GetObserverHandlerCount(_host), Is.EqualTo(handlers));
        Assert.That(ModelSubscription.VerboseSubscribers || ModelSubscription.ObsoleteSubscribers, Is.False);
    }

    [Test]
    public async Task EveryCommandGetsItsResponse()
    {
        // DWS keeps several command connections and runs requests on all of them at the same time. Every connection gets the responses to its own
        // commands in order, because Command.ProcessAsync handles one command after another (Command.cs:95-155)
        CodeChannel[] channels = [CodeChannel.HTTP, CodeChannel.Telnet, CodeChannel.USB, CodeChannel.SBC];
        Task[] clients = [.. channels.Select((channel, index) => Task.Run(async () =>
        {
            using CommandConnection connection = new();
            await connection.ConnectAsync(_host.SocketPath);
            for (int i = 0; i < 12; i++)
            {
                int value = index * 100 + i;
                switch (i % 4)
                {
                    case 0:
                        Assert.That(await connection.PerformSimpleCodeAsync($"echo {value}", channel), Is.EqualTo($"{value}\n"));
                        break;
                    case 1:
                        Assert.That((await connection.EvaluateExpressionAsync($"{value} + 1", channel)).GetInt32(), Is.EqualTo(value + 1));
                        break;
                    case 2:
                        Assert.That(await connection.ResolvePathAsync($"0:/macros/{value}.g"), Does.EndWith($"/macros/{value}.g"));
                        break;
                    default:
                        Assert.That((await connection.GetObjectModelAsync("move/axes[1]/letter")).Move.Axes[1].Letter, Is.EqualTo('Y'));
                        Assert.That(await connection.FlushAsync(channel), Is.True);
                        break;
                }
            }
        }))];
        await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Test]
    public async Task ClientsAreDisconnectedWhenDcsStops()
    {
        // On shutdown the processors are cancelled with the stopping token, Server.ProcessConnectionAsync closes every connection and
        // Server.ExecuteAsync waits for them (Server.cs:117-123), so a DCS restart does not hang on connected clients
        FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(ipc: true);
        IpcLog log = IpcLog.Attach(host);
        List<RawIpcClient> clients = [];
        try
        {
            clients.Add(await RawIpcClient.ConnectAsync(host.SocketPath!, "{\"mode\":\"Command\",\"version\":13}"));
            clients.Add(await RawIpcClient.ConnectAsync(host.SocketPath!, "{\"mode\":\"Intercept\",\"version\":13,\"interceptionMode\":\"Pre\",\"channels\":[\"SBC\"],\"filters\":[\"M7404\"]}"));
            clients.Add(await RawIpcClient.ConnectAsync(host.SocketPath!, "{\"mode\":\"CodeStream\",\"version\":13,\"bufferSize\":4,\"channel\":\"SBC\"}"));
            RawIpcClient subscriber = await RawIpcClient.ConnectAsync(host.SocketPath!, "{\"mode\":\"Subscribe\",\"version\":13,\"subscriptionMode\":\"Patch\",\"filters\":[\"state/status\"]}");
            clients.Add(subscriber);
            (await subscriber.ReceiveAsync())!.Dispose();
            await subscriber.SendAsync("{\"command\":\"Acknowledge\"}");

            Stopwatch stopwatch = Stopwatch.StartNew();
            await host.DisposeAsync();
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "Stopping DCS waited for the clients");

            foreach (RawIpcClient client in clients)
            {
                (_, bool closed) = await client.ReceiveUntilClosedAsync(TimeSpan.FromSeconds(5));
                Assert.That(closed, Is.True, "The connection was not closed");
            }
            Assert.That(log.UnexpectedExceptions, Is.Empty, "The IPC server ran into an exception it did not expect");
        }
        finally
        {
            foreach (RawIpcClient client in clients)
            {
                client.Dispose();
            }
            await host.DisposeAsync();
        }
    }
}
