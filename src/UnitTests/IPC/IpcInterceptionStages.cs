using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// The interception stages Pre, Post and Executed with and without filters, priority codes and AutoFlush
/// (DuetControlServer/IPC/Processors/CodeInterception.cs, Codes/Pipelines/Pre.cs, Post.cs and Executed.cs).
/// <see cref="IpcInterception"/> covers resolve, cancel, ignore and rewrite in the Pre stage
/// </summary>
[TestFixture]
public class IpcInterceptionStages
{
    private FakeFirmwareHost _host = null!;
    private IpcLog _log = null!;

    /// <summary>
    /// Whether M7502 is held in the firmware
    /// </summary>
    private volatile bool _hold;

    /// <summary>
    /// Number of times the firmware finished M7502
    /// </summary>
    private int _heldCodesFinished;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await FakeFirmwareHost.StartAsync(ConfigureFirmware, ipc: true);
        _log = IpcLog.Attach(_host);
    }

    private void ConfigureFirmware(FakeFirmware.FakeFirmware firmware)
    {
        foreach (string code in new[] { "M7500", "M7501", "M7503", "M7504", "M7505", "M7506", "M7507", "M7508", "M7509", "M7510" })
        {
            firmware.OnCode(code, _ => FakeReply.Ok("from firmware"));
        }
        firmware.OnCode("M7502", _ =>
        {
            if (_hold)
            {
                return FakeReply.NotFinished;
            }
            Interlocked.Increment(ref _heldCodesFinished);
            return FakeReply.Ok("held");
        });
        firmware.OnCode("G4", _ => FakeReply.Ok("dwell"));
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

    private static async Task<InterceptConnection> ConnectAsync(FakeFirmwareHost host, InterceptionMode mode, CodeChannel[] channels, string[] filters,
        bool priorityCodes = false, bool autoFlush = true, bool autoEvaluateExpressions = true)
    {
        InterceptConnection connection = new() { AutoFlush = autoFlush, AutoEvaluateExpressions = autoEvaluateExpressions };
        await connection.ConnectAsync(mode, channels, filters, priorityCodes, host.SocketPath);
        return connection;
    }

    private Task<InterceptConnection> ConnectAsync(InterceptionMode mode, CodeChannel[] channels, string[] filters, bool priorityCodes = false, bool autoFlush = true, bool autoEvaluateExpressions = true) =>
        ConnectAsync(_host, mode, channels, filters, priorityCodes, autoFlush, autoEvaluateExpressions);

    private static async Task<Code> ReceiveAsync(InterceptConnection connection) => await connection.ReceiveCodeAsync().AsTask().WaitAsync(FakeFirmwareHost.DefaultTimeout);

    /// <summary>
    /// Start a code on its own command connection, the protocol permits only one command at a time per connection
    /// </summary>
    private static Task<string> StartCode(FakeFirmwareHost host, string code, CodeChannel channel) => Task.Run(async () =>
    {
        using CommandConnection connection = new();
        await connection.ConnectAsync(host.SocketPath);
        return await connection.PerformSimpleCodeAsync(code, channel);
    });

    private Task<string> StartCode(string code, CodeChannel channel) => StartCode(_host, code, channel);

    private static async Task<string> ExecuteAsync(FakeFirmwareHost host, string code, CodeChannel channel, TimeSpan? timeout = null) =>
        await StartCode(host, code, channel).WaitAsync(timeout ?? FakeFirmwareHost.DefaultTimeout);

    [Test]
    public async Task WithoutFiltersEveryCodeOfTheChannelsIsIntercepted()
    {
        // CanIntercept passes every code when there are no filters (CodeInterception.cs:232-250), comments included
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, [CodeChannel.Daemon], []);
        Task<string> reply = StartCode("M7500\nG4 P0\n; a comment", CodeChannel.Daemon);
        Code mCode = await ReceiveAsync(interceptor);
        Assert.That(mCode.MajorNumber, Is.EqualTo(7500));
        await interceptor.ResolveCodeAsync(MessageType.Success, "resolved");
        Code gCode = await ReceiveAsync(interceptor);
        Assert.That((gCode.Type, gCode.MajorNumber), Is.EqualTo((CodeType.GCode, 4)));
        await interceptor.IgnoreCodeAsync();
        Code comment = await ReceiveAsync(interceptor);
        Assert.That(comment.Type, Is.EqualTo(CodeType.Comment));
        Assert.That(comment.Comment, Is.EqualTo(" a comment"));
        await interceptor.IgnoreCodeAsync();
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Does.StartWith("resolved\ndwell\n"));

        // Codes of other channels are left alone
        Assert.That(await ExecuteAsync(_host, "M7500", CodeChannel.Telnet), Is.EqualTo("from firmware\n"));
    }

    [Test]
    public async Task WithoutFiltersThereIsNoAutoFlush()
    {
        // AutoFlush and with it AutoEvaluateExpressions apply only to interceptors with filters (CodeInterception.cs:145-146)
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, [CodeChannel.LCD], [], autoFlush: true, autoEvaluateExpressions: true);
        Task<string> reply = StartCode("M7500 P{1+2}", CodeChannel.LCD);
        Code code = await ReceiveAsync(interceptor);
        Assert.That(code.GetParameter('P')!.IsExpression, Is.True);
        await interceptor.ResolveCodeAsync(MessageType.Success, string.Empty);
        await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout);
    }

    [Test]
    public async Task ExpressionsNeedAutoFlush()
    {
        // AutoEvaluateExpressions "requires AutoFlush to be true" (CodeInterception.cs:85, 146)
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, [CodeChannel.Telnet], ["M7509"], autoFlush: false, autoEvaluateExpressions: true);
        Task<string> reply = StartCode("M7509 P{1+2}", CodeChannel.Telnet);
        Code code = await ReceiveAsync(interceptor);
        Assert.That(code.GetParameter('P')!.IsExpression, Is.True);
        await interceptor.ResolveCodeAsync(MessageType.Success, string.Empty);
        await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout);
    }

    [Test]
    public async Task NoChannelsMeansEveryChannel()
    {
        // An interceptor without channels listens to all valid channels (CodeInterception.cs:144)
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, [], ["M7501"]);
        foreach (CodeChannel channel in new[] { CodeChannel.HTTP, CodeChannel.Telnet, CodeChannel.USB, CodeChannel.SBC })
        {
            Task<string> reply = StartCode("M7501", channel);
            Code code = await ReceiveAsync(interceptor);
            Assert.That(code.Channel, Is.EqualTo(channel));
            await interceptor.ResolveCodeAsync(MessageType.Success, channel.ToString());
            Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo(channel + "\n"));
        }
    }

    [Test]
    public async Task AutoFlushPassesTheCodeAfterThePendingCodes()
    {
        // With AutoFlush the channel is flushed before the code is passed (CodeInterception.cs:262-266), so a code that is still running in the
        // firmware ahead of it has finished by then
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, [CodeChannel.Telnet], ["M7503"], autoFlush: true);
        int finished = Volatile.Read(ref _heldCodesFinished);
        _hold = true;
        Task<string> reply = StartCode("M7502\nM7503", CodeChannel.Telnet);
        Task<Code> receive = interceptor.ReceiveCodeAsync().AsTask();
        await IpcStatics.WaitUntilAsync(() => _host.Firmware.GetReceivedCodes(CodeChannel.Telnet).Any(code => code.MajorNumber == 7502 && Volatile.Read(ref _heldCodesFinished) == finished), "the firmware has the first code");
        Assert.That(await Task.WhenAny(receive, Task.Delay(300)), Is.Not.SameAs(receive), "The code was passed before the channel was flushed");

        _hold = false;
        Code code = await receive.WaitAsync(FakeFirmwareHost.DefaultTimeout);
        Assert.That(code.MajorNumber, Is.EqualTo(7503));
        Assert.That(Volatile.Read(ref _heldCodesFinished), Is.EqualTo(finished + 1));
        await interceptor.IgnoreCodeAsync();
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("held\nfrom firmware\n"));
    }

    [Test]
    public async Task WithoutAutoFlushTheCodeIsPassedRightAway()
    {
        // Without AutoFlush the code is passed while the code ahead of it is still running in the firmware
        using InterceptConnection interceptor = await ConnectAsync(InterceptionMode.Pre, [CodeChannel.Telnet], ["M7507"], autoFlush: false);
        int finished = Volatile.Read(ref _heldCodesFinished);
        _hold = true;
        Task<string> reply = StartCode("M7502\nM7507", CodeChannel.Telnet);
        Code code = await ReceiveAsync(interceptor);
        Assert.That(code.MajorNumber, Is.EqualTo(7507));
        Assert.That(Volatile.Read(ref _heldCodesFinished), Is.EqualTo(finished), "The first code is still held");
        await interceptor.IgnoreCodeAsync();
        _hold = false;
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("held\nfrom firmware\n"));
    }

    [Test]
    public async Task PreAndPostSeeTheCodeInTurn()
    {
        // A code passes the Pre stage (Pre.cs:30-36) and reaches the Post interceptors when DCS has not handled it internally, which
        // Code.ProcessInternally does before the code goes to the firmware (Commands/Generic/Code.cs:284-293)
        using InterceptConnection pre = await ConnectAsync(InterceptionMode.Pre, [CodeChannel.Telnet], ["M7504"]);
        using InterceptConnection post = await ConnectAsync(InterceptionMode.Post, [CodeChannel.Telnet], ["M7504"]);
        Task<string> reply = StartCode("M7504", CodeChannel.Telnet);
        Code preCode = await ReceiveAsync(pre);
        Assert.That(preCode.Flags.HasFlag(CodeFlags.IsPreProcessed), Is.False);
        await pre.IgnoreCodeAsync();
        Code postCode = await ReceiveAsync(post);
        Assert.That(postCode.Flags.HasFlag(CodeFlags.IsPreProcessed), Is.True, postCode.Flags.ToString());
        Assert.That(postCode.Flags.HasFlag(CodeFlags.IsPostProcessed), Is.False, postCode.Flags.ToString());
        await post.IgnoreCodeAsync();
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("from firmware\n"));
    }

    [Test]
    public async Task PostStageCancelAndIgnore()
    {
        using InterceptConnection post = await ConnectAsync(InterceptionMode.Post, [CodeChannel.Telnet], ["M7508"]);
        int inFirmware = _host.Firmware.GetReceivedCodes(CodeChannel.Telnet).Count(code => code.MajorNumber == 7508);

        // A cancelled code fails with an OperationCanceledException (Commands/Generic/Code.cs:286, ProcessInternally.cs:32-39), which SimpleCode
        // leaves out of the reply
        Task<string> reply = StartCode("M7508", CodeChannel.Telnet);
        await ReceiveAsync(post);
        await post.CancelCodeAsync();
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.Empty);
        Assert.That(_host.Firmware.GetReceivedCodes(CodeChannel.Telnet).Count(code => code.MajorNumber == 7508), Is.EqualTo(inFirmware), "A cancelled code must not reach the firmware");

        // An ignored code goes on to the firmware
        reply = StartCode("M7508", CodeChannel.Telnet);
        await ReceiveAsync(post);
        await post.IgnoreCodeAsync();
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("from firmware\n"));

        // A rewritten code goes to the firmware as rewritten
        reply = StartCode("M7508", CodeChannel.Telnet);
        Code code = await ReceiveAsync(post);
        code.MajorNumber = 7505;
        await post.RewriteCodeAsync(code);
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("from firmware\n"));
        Assert.That(_host.Firmware.GetReceivedCodes(CodeChannel.Telnet).Last().MajorNumber, Is.EqualTo(7505));
    }

    [Test]
    public async Task ExecutedStageResolveReplacesTheResult()
    {
        // The Executed stage passes the code after it has been executed and keeps what the interceptor left in code.Result (Executed.cs:155-167 appends the newline first, 169-221 uses the result),
        // so a resolved code replies with the content of Resolve (CodeInterception.cs:298-301)
        using InterceptConnection executed = await ConnectAsync(InterceptionMode.Executed, [CodeChannel.Telnet], ["M7506"]);
        Task<string> reply = StartCode("M7506", CodeChannel.Telnet);
        Code code = await ReceiveAsync(executed);
        Assert.That(code.Result?.Content, Is.EqualTo("from firmware\n"));
        await executed.ResolveCodeAsync(MessageType.Warning, "replaced");
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("Warning: replaced"));

        // Cancel makes the code fail with an OperationCanceledException (Executed.cs:232-240), which SimpleCode leaves out of the reply
        reply = StartCode("M7506", CodeChannel.Telnet);
        await ReceiveAsync(executed);
        await executed.CancelCodeAsync();
        Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.Empty);
    }

    [Test]
    public async Task PriorityInterceptorsGetOnlyPriorityCodes()
    {
        // CanIntercept compares the IsPrioritized flag with PriorityCodes of the interceptor (CodeInterception.cs:226-227)
        using InterceptConnection priority = await ConnectAsync(InterceptionMode.Pre, [CodeChannel.Telnet], ["M7510"], priorityCodes: true);
        Assert.That(await ExecuteAsync(_host, "M7510", CodeChannel.Telnet), Is.EqualTo("from firmware\n"));
    }

    /// <summary>
    /// Confirmed on the printer (DSF 3.7.0+mp.13, 2026-11-07): an interceptor with the filter "M10*" made ";hello" fail, the journal said
    /// "Failed to execute code ;hello on pre stage". CanIntercept compares filter[..3] with the short code string cut at the same length
    /// (CodeInterception.cs:242-243), but the short string of a comment is "Q0" (CodeInterception.cs:234), so the slice throws an
    /// ArgumentOutOfRangeException, Pre.ProcessCodeAsync cancels the code with it (Pre.cs:38-45) and SimpleCode reports it as an error.
    /// A comment must behave as if there was no interceptor, since "M10*" cannot match it
    /// </summary>
    [Test]
    public async Task WildcardFilterLongerThanAComment()
    {
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(ipc: true);
        string expected = await ExecuteAsync(host, ";hello", CodeChannel.SBC);
        using InterceptConnection interceptor = await ConnectAsync(host, InterceptionMode.Pre, [CodeChannel.SBC], ["M10*"]);
        Assert.That(await ExecuteAsync(host, ";hello", CodeChannel.SBC), Is.EqualTo(expected));
    }

    /// <summary>
    /// Confirmed on the printer (DSF 3.7.0+mp.13, 2026-11-07): after the client with the filter "M10*" had exited, its interceptor stayed
    /// registered and short codes kept failing until a code that matches the filter (M105) was sent; only then did DCS notice the closed
    /// connection. An idle interceptor does not read from its socket (CodeInterception.cs:174), and CanIntercept checks Connection.IsConnected
    /// (CodeInterception.cs:226), which is Socket.Connected and stays true until DCS does I/O on the socket. Because CanIntercept throws before
    /// anything is sent (see <see cref="WildcardFilterLongerThanAComment"/>), codes shorter than the filter never get that far
    /// </summary>
    [Test]
    public async Task WildcardFilterLongerThanTheCodeOutlivesTheClient()
    {
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware => firmware.OnCode("G4", _ => FakeReply.Ok("dwell")), ipc: true);
        string expected = await ExecuteAsync(host, ";hello", CodeChannel.SBC);
        InterceptConnection interceptor = await ConnectAsync(host, InterceptionMode.Pre, [CodeChannel.SBC], ["M10*"]);
        int id = interceptor.Id;
        interceptor.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ExecuteAsync(host, ";hello", CodeChannel.SBC), Is.EqualTo(expected), "Comment after the client has gone");
            Assert.That(await ExecuteAsync(host, "G4 P0", CodeChannel.SBC), Is.EqualTo("dwell\n"), "G4 after the client has gone");

            // A matching code makes DCS notice the closed connection, after which the interceptor is gone
            await ExecuteAsync(host, "M105", CodeChannel.SBC);
            await IpcStatics.WaitUntilAsync(() => !IpcStatics.IsInterceptorRegistered(id), "the interceptor is unregistered");
            Assert.That(await ExecuteAsync(host, ";hello", CodeChannel.SBC), Is.EqualTo(expected), "Comment after M105");
        }
    }

    /// <summary>
    /// Seen on the printer (DSF 3.7.0+mp.13, 2026-11-07): with an Executed interceptor that had a filter and the DuetAPIClient default
    /// AutoFlush=true, M115 on Telnet got no reply, the channel stayed blocked after the client process had exited, other channels were fine
    /// and nothing was logged; only a DCS restart cleared it. CodeInterception.InterceptAsync flushes the channel before it passes the code
    /// (CodeInterception.cs:263), and for a code in the Executed stage ChannelProcessor.FlushAsync waited for every stage including Executed
    /// to become idle (PipelineStackItem.FlushAsync), but that stage is busy with this very code. The code was never sent, so the processor
    /// never did I/O on its socket and did not notice that the client was gone either.
    /// See <see cref="IpcInterception.ExecutedCodesWithAutoFlush"/> for the interceptor side
    /// </summary>
    [Test]
    public async Task ChannelWorksAfterAnExecutedInterceptorWithAutoFlushHasGone()
    {
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware => firmware.OnCode("M115", _ => FakeReply.Ok("FIRMWARE_NAME: RepRapFirmware")), ipc: true);
        InterceptConnection interceptor = await ConnectAsync(host, InterceptionMode.Executed, [CodeChannel.Telnet], ["M115"]);
        Task<string> first = StartCode(host, "M115", CodeChannel.Telnet);
        await IpcStatics.WaitUntilAsync(() => host.Firmware.GetReceivedCodes(CodeChannel.Telnet).Any(code => code.MajorNumber == 115), "the firmware has executed M115");
        interceptor.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ExecuteAsync(host, "M115", CodeChannel.HTTP, TimeSpan.FromSeconds(3)), Does.StartWith("FIRMWARE_NAME"), "Other channels work");
            Assert.That(async () => await first.WaitAsync(TimeSpan.FromSeconds(3)), Throws.Nothing, "M115 on Telnet got no reply");
            Assert.That(async () => await ExecuteAsync(host, "M115", CodeChannel.Telnet, TimeSpan.FromSeconds(3)), Throws.Nothing, "Telnet stays blocked after the client has gone");
        }
    }
}
