using System;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using DuetControlServer.IPC;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// Tests of the Intercept mode (DuetControlServer/IPC/Processors/CodeInterception.cs). Codes are started via a command connection,
/// an interceptor connection receives them and resolves, cancels, ignores or rewrites them
/// </summary>
[TestFixture]
public class IpcInterception
{
    private FakeFirmwareHost _host = null!;
    private IpcLog _log = null!;
    private CommandConnection _command = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await FakeFirmwareHost.StartAsync(firmware =>
        {
            firmware.OnCode("M7101", _ => FakeReply.Ok("from firmware"));
            firmware.OnCode("M7102", code => FakeReply.Ok(code.Parameters.Any(parameter => parameter.Letter == 'S') ? "rewritten S" : "rewritten"));
            firmware.OnCode("G4", _ => FakeReply.Ok("dwell"));
        }, ipc: true);
        _log = IpcLog.Attach(_host);
        _command = new CommandConnection();
        await _command.ConnectAsync(_host.SocketPath);
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        _command.Dispose();
        await _host.DisposeAsync();
    }

    [SetUp]
    public void ClearLog() => _log.Clear();

    [TearDown]
    public async Task CheckLog()
    {
        // A code that nobody intercepts still runs, so no interceptor of this test was left behind
        Assert.That(await _command.PerformSimpleCodeAsync("M7101", CodeChannel.SBC).WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("from firmware\n"));
        Assert.That(_log.UnexpectedExceptions, Is.Empty, "The IPC server ran into an exception it did not expect");
    }

    private async Task<InterceptConnection> ConnectAsync(InterceptionMode mode, string[] filters, bool priorityCodes = false, CodeChannel[]? channels = null,
        bool autoFlush = true, bool autoEvaluateExpressions = true)
    {
        InterceptConnection connection = new() { AutoFlush = autoFlush, AutoEvaluateExpressions = autoEvaluateExpressions };
        await connection.ConnectAsync(mode, channels ?? [CodeChannel.SBC], filters, priorityCodes, _host.SocketPath);
        return connection;
    }

    private static async Task<Code> ReceiveAsync(InterceptConnection connection) => await connection.ReceiveCodeAsync().AsTask().WaitAsync(FakeFirmwareHost.DefaultTimeout);

    private Task<string> StartCode(string code, CodeChannel channel = CodeChannel.SBC)
    {
        // Every code gets its own command connection, the protocol permits only one command at a time per connection
        return Task.Run(async () =>
        {
            using CommandConnection connection = new();
            await connection.ConnectAsync(_host.SocketPath);
            return await connection.PerformSimpleCodeAsync(code, channel);
        });
    }

    [TestCase(InterceptionMode.Pre)]
    [TestCase(InterceptionMode.Post)]
    public async Task ResolvedCodeReturnsTheResult(InterceptionMode mode)
    {
        // CodeInterception.InterceptAsync sets the result of a resolved code, which does not reach the firmware then
        using InterceptConnection interceptor = await ConnectAsync(mode, ["M7101"]);
        int codesInFirmware = _host.Firmware.GetReceivedCodes(CodeChannel.SBC).Count(item => item.MajorNumber == 7101);
        Task<string> reply = StartCode("M7101 P1");
        Code code = await ReceiveAsync(interceptor);
        Assert.That(code.Type, Is.EqualTo(CodeType.MCode));
        Assert.That(code.MajorNumber, Is.EqualTo(7101));
        Assert.That(code.Channel, Is.EqualTo(CodeChannel.SBC));
        Assert.That(code.GetInt('P'), Is.EqualTo(1));
        await interceptor.ResolveCodeAsync(MessageType.Warning, "resolved");
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("Warning: resolved\n"));
        Assert.That(_host.Firmware.GetReceivedCodes(CodeChannel.SBC).Count(item => item.MajorNumber == 7101), Is.EqualTo(codesInFirmware));
    }

    [Test]
    public async Task IgnoredCodeRunsInTheFirmware()
    {
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, ["M7101"]);
        Task<string> reply = StartCode("M7101");
        await ReceiveAsync(interceptor);
        interceptor.IgnoreCode();
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("from firmware\n"));
    }

    [Test]
    public async Task RewrittenCodeRunsInTheFirmware()
    {
        // Code.CopyFrom replaces the code with the one sent by the interceptor
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, ["M7101"]);
        Task<string> reply = StartCode("M7101");
        Code code = await ReceiveAsync(interceptor);
        code.MajorNumber = 7102;
        code.Parameters.Add(new CodeParameter('S', 5));
        await interceptor.RewriteCodeAsync(code);
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("rewritten S\n"));

        // Same with the synchronous call
        reply = StartCode("M7101");
        code = await ReceiveAsync(interceptor);
        code.MajorNumber = 7102;
        interceptor.RewriteCode(code);
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Does.StartWith("rewritten"));
    }

    [Test]
    public async Task CancelledCodeHasNoReply()
    {
        // A cancelled code throws an OperationCanceledException, which SimpleCode.ExecuteAsync leaves out of the reply
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, ["M7101"]);
        Task<string> reply = StartCode("M7101");
        await ReceiveAsync(interceptor);
        await interceptor.CancelCodeAsync();
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.Empty);
    }

    [Test]
    public async Task ResolveWithMessageObject()
    {
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, ["M7101"]);
        Task<string> reply = StartCode("M7101");
        await ReceiveAsync(interceptor);
        interceptor.ResolveCode(new Message(MessageType.Success, "sync"));
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("sync\n"));

        reply = StartCode("M7101");
        Code code = interceptor.ReceiveCode();
        Assert.That(code.MajorNumber, Is.EqualTo(7101));
        await interceptor.ResolveCodeAsync(new Message(MessageType.Error, "async"));
        // Errors of codes resolved by DSF get the code as prefix like RRF does it (Pipelines/Executed.cs)
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("Error: M7101: async\n"));

        reply = StartCode("M7101");
        await ReceiveAsync(interceptor);
        interceptor.ResolveCode(MessageType.Success, "plain");
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("plain\n"));

        reply = StartCode("M7101");
        await ReceiveAsync(interceptor);
        interceptor.CancelCode();
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.Empty);

        reply = StartCode("M7101");
        await ReceiveAsync(interceptor);
        await interceptor.IgnoreCodeAsync();
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("from firmware\n"));
    }

    [Test]
    public async Task ExecutedCodesAreReportedWithTheirResult()
    {
        // The Executed stage passes the code after it has been executed (Pipelines/Executed.cs)
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Executed, ["M7101"]);
        Task<string> reply = StartCode("M7101");
        Code code = await ReceiveAsync(interceptor);
        Assert.That(code.Result?.Content, Does.StartWith("from firmware"));
        await interceptor.IgnoreCodeAsync();
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("from firmware\n"));
    }

    /// <summary>
    /// DuetAPIClient enables AutoFlush by default (InterceptConnection.AutoFlush), and CodeInterception.InterceptAsync then flushes the channel
    /// before it passes the code. For a code in the Executed stage, ChannelProcessor.FlushAsync used to wait for every stage, the Executed stage
    /// included (PipelineStackItem.FlushAsync waits for its idle event), but that stage is busy with this very code, so the flush never finished.
    /// CodeLogger connects Executed interceptors with filters and the default AutoFlush, so "CodeLogger -t Executed -f M7101" blocked the channel.
    /// Seen on the printer (DSF 3.7.0+mp.13, 2026-11-07), see IpcInterceptionStages.ChannelWorksAfterAnExecutedInterceptorWithAutoFlushHasGone.
    /// A code in the Executed stage has nothing ahead of it, so the flush no longer waits for anything
    /// </summary>
    [Test]
    public async Task ExecutedCodesWithAutoFlush()
    {
        // Own host, the deadlock blocks the SBC channel for good
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware => firmware.OnCode("M7101", _ => FakeReply.Ok("from firmware")), ipc: true);
        using InterceptConnection interceptor = new();
        await interceptor.ConnectAsync(InterceptionMode.Executed, [CodeChannel.SBC], ["M7101"], false, host.SocketPath);
        Task<string> reply = host.StartCode("M7101", CodeChannel.SBC);
        Code code = await interceptor.ReceiveCodeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(code.MajorNumber, Is.EqualTo(7101));
        await interceptor.IgnoreCodeAsync();
        Assert.That(await reply.WaitAsync(TimeSpan.FromSeconds(3)), Is.EqualTo("from firmware\n"));
    }

    /// <summary>
    /// The Flush command of an interceptor flushes for the code that it is intercepting (Commands/Generic/Flush.cs), which is the same
    /// flush as the one of AutoFlush. In the Executed stage it waited for that stage as well, see <see cref="ExecutedCodesWithAutoFlush"/>
    /// </summary>
    [Test]
    public async Task ExecutedInterceptorCanFlush()
    {
        // Own host, a flush that does not return blocks the SBC channel for good
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware => firmware.OnCode("M7101", _ => FakeReply.Ok("from firmware")), ipc: true);
        using InterceptConnection interceptor = new() { AutoFlush = false };
        await interceptor.ConnectAsync(InterceptionMode.Executed, [CodeChannel.SBC], ["M7101"], false, host.SocketPath);
        Task<string> reply = host.StartCode("M7101", CodeChannel.SBC);
        await interceptor.ReceiveCodeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(await interceptor.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3)), Is.True);
        await interceptor.IgnoreCodeAsync();
        Assert.That(await reply.WaitAsync(TimeSpan.FromSeconds(3)), Is.EqualTo("from firmware\n"));
    }

    [TestCase("M71*", "M7101", true)]
    [TestCase("m7101", "M7101", true)]
    [TestCase("M7*", "G4", false)]
    [TestCase("M7100", "M7101", false)]
    [TestCase("G*", "G4", true)]
    [TestCase("Q0", "; comment", true)]
    [TestCase("echo", "echo 1", true)]
    public async Task Filters(string filter, string code, bool intercepted)
    {
        // CodeInterception.CanIntercept compares the short code string (Q0 for comments) case-insensitively and supports a trailing asterisk
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, [filter]);
        Task<string> reply = StartCode(code);
        if (intercepted)
        {
            await ReceiveAsync(interceptor);
            await interceptor.ResolveCodeAsync(MessageType.Success, "intercepted");
            Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("intercepted\n"));
        }
        else
        {
            Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.Not.EqualTo("intercepted\n"));
        }
    }

    [Test]
    public async Task WildcardFilterLongerThanTheCode()
    {
        // "M10*" should match M100 to M109 and leave G4 alone, but filter[..3] is compared with "G4"[..3].
        // Confirmed on the printer (DSF 3.7.0+mp.13, 2026-11-07), see IpcInterceptionStages.WildcardFilterLongerThanAComment
        // and WildcardFilterLongerThanTheCodeOutlivesTheClient
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, ["M10*"]);
        Assert.That(await StartCode("G4 P0").WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("dwell\n"));
    }

    [Test]
    public async Task OtherChannelsAreNotIntercepted()
    {
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, ["M7101"], channels: [CodeChannel.Telnet]);
        Assert.That(await StartCode("M7101", CodeChannel.SBC).WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("from firmware\n"));
    }

    [Test]
    public async Task PriorityCodesNeedPriorityInterceptors()
    {
        // SimpleCode flags M122 as prioritized, CanIntercept only passes it to interceptors that asked for priority codes
        using InterceptConnection normal = await ConnectAsync(InterceptionMode.Pre, ["M122"]);
        using InterceptConnection priority = await ConnectAsync(InterceptionMode.Pre, ["M122"], priorityCodes: true);
        Assert.That(priority.PriortyCodes, Is.True);
        Task<string> reply = StartCode("M122");
        Code code = await ReceiveAsync(priority);
        Assert.That(code.Flags.HasFlag(CodeFlags.IsPrioritized), Is.True);
        await priority.ResolveCodeAsync(MessageType.Success, "diagnostics");
        // MCodeHandler.CodeExecutedAsync appends the DCS diagnostics to every M122 result that is not empty
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Does.StartWith("diagnostics\n=== Duet Control Server ==="));
    }

    [Test]
    public async Task ExpressionsAreEvaluatedBeforeInterception()
    {
        // With AutoFlush and AutoEvaluateExpressions the code channel is flushed and the expressions are evaluated first (CodeInterception.InterceptAsync)
        using InterceptConnection evaluating = await ConnectAsync(InterceptionMode.Pre, ["M7101"]);
        Task<string> reply = StartCode("M7101 P{1+2}");
        Code code = await ReceiveAsync(evaluating);
        Assert.That(code.GetInt('P'), Is.EqualTo(3));
        Assert.That(code.GetParameter('P')!.IsExpression, Is.False);
        await evaluating.ResolveCodeAsync(MessageType.Success, string.Empty);
        await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout);
    }

    [Test]
    public async Task ExpressionsAreLeftAloneOnRequest()
    {
        using InterceptConnection raw = await ConnectAsync(InterceptionMode.Pre, ["M7101"], autoEvaluateExpressions: false);
        Task<string> rawReply = StartCode("M7101 P{1+2}");
        Code rawCode = await ReceiveAsync(raw);
        Assert.That(rawCode.GetParameter('P')!.IsExpression, Is.True);
        await raw.ResolveCodeAsync(MessageType.Success, string.Empty);
        await rawReply.WaitAsync(FakeFirmwareHost.DefaultTimeout);
    }

    [Test]
    public async Task CommandsWhileIntercepting()
    {
        // While a code is intercepted the interceptor may run the commands of the Command mode (CodeInterception.ProcessAsync)
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, ["M7101"]);
        Task<string> reply = StartCode("M7101");
        await ReceiveAsync(interceptor);

        // Flush refers to the intercepted code (Flush.ExecuteAsync)
        Assert.That(await interceptor.FlushAsync(), Is.True);
        Assert.That(interceptor.Flush(), Is.True);
        Assert.That(await interceptor.EvaluateExpressionAsync("2 * 21"), Is.Not.Null);
        Assert.That((await interceptor.EvaluateExpressionAsync("2 * 21")).GetInt32(), Is.EqualTo(42));

        // Codes of the interceptor itself are inserted before the intercepted code and not intercepted again (CanIntercept, SimpleCode.ExecuteAsync)
        Assert.That(await interceptor.PerformSimpleCodeAsync("M7101", CodeChannel.SBC).WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("from firmware\n"));

        // The diagnostics name the intercepting connection (Server.PrintDiagnostics)
        StringBuilder diagnostics = new();
        _host.Services.GetRequiredService<Server>().PrintDiagnostics(diagnostics);
        Assert.That(diagnostics.ToString(), Does.Contain($"IPC connection #{interceptor.Id} is intercepting M7101 (Pre)"));

        await interceptor.ResolveCodeAsync(MessageType.Success, "done");
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("done\n"));
    }

    [Test]
    public async Task CodeRunsOnWhenTheInterceptorDisconnects()
    {
        // When the interceptor terminates, CodeInterception.ProcessAsync wakes the waiting code, which then carries on as if it was ignored
        InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, ["M7101"]);
        Task<string> reply = StartCode("M7101");
        await ReceiveAsync(interceptor);
        interceptor.Dispose();
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("from firmware\n"));
    }

    [Test]
    public async Task DisconnectedInterceptorIsSkipped()
    {
        // An interceptor that went away while idle is removed when the next code fails to reach it
        InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, ["M7101"]);
        interceptor.Dispose();
        Assert.That(await StartCode("M7101").WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("from firmware\n"));
    }

    [Test]
    public async Task SeveralInterceptorsInTurn()
    {
        // Interceptors are asked one after another until one resolves the code (CodeInterception.InterceptAsync)
        using InterceptConnection first = await ConnectAsync(InterceptionMode.Pre, ["M7101"]);
        using InterceptConnection second = await ConnectAsync(InterceptionMode.Pre, ["M7101"]);
        Task<string> reply = StartCode("M7101");
        await ReceiveAsync(first);
        await first.IgnoreCodeAsync();
        await ReceiveAsync(second);
        await second.ResolveCodeAsync(MessageType.Success, "second");
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("second\n"));
    }

    [Test]
    public async Task UnsupportedCommandEndsTheInterception()
    {
        // Connection.ReceiveCommandAsync throws for a command that is not supported in this mode, which ends CodeInterception.ProcessAsync.
        // The server closes the connection and the code carries on
        using RawIpcClient interceptor = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Intercept\",\"version\":13,\"interceptionMode\":\"Pre\",\"channels\":[\"SBC\"],\"filters\":[\"M7101\"]}");
        Task<string> reply = StartCode("M7101");
        using (JsonDocument code = await interceptor.ReceiveAsync() ?? throw new AssertionException("No code"))
        {
            Assert.That(code.RootElement.GetProperty("command").GetString(), Is.EqualTo("Code"));
            Assert.That(code.RootElement.GetProperty("majorNumber").GetInt32(), Is.EqualTo(7101));
        }
        await interceptor.SendAsync("{\"command\":\"Acknowledge\"}");
        (var messages, bool closed) = await interceptor.ReceiveUntilClosedAsync(TimeSpan.FromSeconds(5));
        Assert.That(closed, Is.True);
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("from firmware\n"));
        Assert.That(_log.UnexpectedExceptions, Has.Count.EqualTo(1));
        _log.Clear();
    }

    [Test]
    [Explicit("Bug: a failing command while intercepting ends the connection instead of sending an error response like the Command mode")]
    public async Task FailingCommandWhileInterceptingIsReported()
    {
        // "You can run any commands like in the command mode while a code is being intercepted" (DSF wiki, API Overview), and the Command
        // mode sends an error back (Command.ProcessAsync). CodeInterception.ProcessAsync has no catch around command.InvokeAsync
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, ["M7101"]);
        Task<string> reply = StartCode("M7101");
        await ReceiveAsync(interceptor);
        await Assert.ThrowsAsync<InternalServerException>(async () => await interceptor.StartPluginAsync("NoSuchPlugin"));
        await interceptor.ResolveCodeAsync(MessageType.Success, "done");
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("done\n"));
    }

    [Test]
    [Explicit("Bug: an undefined interception mode is acknowledged and then ends with a KeyNotFoundException")]
    public async Task RefusesUndefinedInterceptionMode()
    {
        // The enum converter accepts numbers, so the init message passes, but CodeInterception.ProcessAsync looks the mode up in _connections.
        // Like CodeStream does for its buffer size, the constructor should refuse it so that the client gets an error response
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!);
        using JsonDocument response = await client.RequestAsync("{\"mode\":\"Intercept\",\"version\":13,\"interceptionMode\":7}");
        Assert.That(RawIpcClient.GetErrorType(response.RootElement), Is.EqualTo(nameof(ArgumentException)), response.RootElement.ToString());
    }

    [Test]
    public async Task InterceptorConnectsSynchronously()
    {
        using InterceptConnection interceptor = new();
        interceptor.Connect(InterceptionMode.Post, [CodeChannel.SBC], ["M7101"], false, _host.SocketPath);
        Assert.That(interceptor.Mode, Is.EqualTo(InterceptionMode.Post));
        Assert.That(interceptor.Channels, Is.EqualTo(new[] { CodeChannel.SBC }));
        Assert.That(interceptor.Filters, Is.EqualTo(new[] { "M7101" }));
        Task<string> reply = StartCode("M7101");
        interceptor.ReceiveCode();
        interceptor.IgnoreCode();
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("from firmware\n"));
    }
}
