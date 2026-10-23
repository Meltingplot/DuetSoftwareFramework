using System;
using System.IO;
using System.Reflection;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer;
using DuetControlServer.Codes;
using DuetControlServer.Commands;
using DuetControlServer.Files;
using DuetControlServer.Link;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Channel;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Code = DuetControlServer.Commands.Code;

namespace UnitTests.SPI;

/// <summary>
/// Tests for the matching of code replies to buffered codes when RRF aborts a macro file
/// </summary>
[TestFixture]
public class ChannelProcessor
{
    private const MessageTypeFlags ReplyFlags = MessageTypeFlags.BinaryCodeReplyFlag | (MessageTypeFlags)(1 << (int)CodeChannel.HTTP);

    private ServiceProvider _provider = null!;
    private Processor _processor = null!;
    private string _macroFile = null!;

    /// <summary>
    /// Link adapter that does nothing, the tests feed replies and abort requests to the processor directly
    /// </summary>
    public class NullLinkAdapter : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == $"get_{nameof(ILinkAdapter.ProtocolVersion)}")
            {
                return (int)Consts.ProtocolVersion;
            }
            return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void) ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    [SetUp]
    public void SetUp()
    {
        _macroFile = Path.GetTempFileName();

        ServiceCollection services = new();
        services
            .AddLogging()
            .AddSingleton(Options.Create(new Settings()))
            .AddSingleton<IHostApplicationLifetime, ApplicationLifetime>()
            .AddSingleton(DispatchProxy.Create<ILinkAdapter, NullLinkAdapter>())
            .AddSingleton<LinkInterface>()
            .AddCodes()
            .AddCommands()
            .AddFiles()
            .AddLink()
            .AddModel()
            .AddUtility();
        _provider = services.BuildServiceProvider();
        _processor = ActivatorUtilities.CreateInstance<Processor>(_provider, CodeChannel.HTTP);
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        System.IO.File.Delete(_macroFile);
    }

    private Code MakeCode(int majorNumber)
    {
        Code code = _provider.GetRequiredService<CodeFactory>().Create();
        code.Channel = CodeChannel.HTTP;
        code.Type = CodeType.MCode;
        code.MajorNumber = majorNumber;
        return code;
    }

    /// <summary>
    /// Push a macro started by the given code (if any) and buffer the given codes from it
    /// </summary>
    private void StartMacro(Code? startCode, params Code[] bufferedCodes)
    {
        MacroFile macro = _provider.GetRequiredService<FileFactory>().CreateMacro("0:/macros/test", _macroFile, CodeChannel.HTTP, startCode)!;
        _processor.Push(macro).StartCode = startCode;
        _processor.BufferedCodes.AddRange(bufferedCodes);
    }

    private static bool CompletedByReply(Code code) => code.Result is not null;

    [Test]
    public void M99ReplyAfterAbortRequest()
    {
        Code startCode = MakeCode(98), m99 = MakeCode(99), readAhead = MakeCode(98);
        StartMacro(startCode, m99, readAhead);

        // RRF wrote [abort][empty reply for the start code] while the M99 was running, its reply comes with the next transfer
        _processor.FilesAborted(false);
        Assert.That(_processor.BufferedCodes, Is.EqualTo(new[] { startCode }));

        // The caller's next code (e.g. another M98) has already been sent when the late M99 reply arrives
        Code nextCode = MakeCode(98);
        _processor.BufferedCodes.Add(nextCode);
        _processor.HandleReply(ReplyFlags, string.Empty);
        _processor.HandleReply(ReplyFlags, string.Empty);

        Assert.That(CompletedByReply(startCode), Is.True);
        Assert.That(CompletedByReply(nextCode), Is.False);
        Assert.That(_processor.BufferedCodes, Is.EqualTo(new[] { nextCode }));

        // No empty reply is dropped any more
        _processor.HandleReply(ReplyFlags, string.Empty);
        Assert.That(CompletedByReply(nextCode), Is.True);
    }

    [Test]
    public void M99ReplyBeforeAbortRequest()
    {
        Code startCode = MakeCode(98), m99 = MakeCode(99), readAhead = MakeCode(98);
        StartMacro(startCode, m99, readAhead);

        // RRF wrote [M99 reply][abort][empty reply for the start code]
        _processor.HandleReply(ReplyFlags, string.Empty);
        _processor.FilesAborted(false);
        Code nextCode = MakeCode(98);
        _processor.BufferedCodes.Add(nextCode);
        _processor.HandleReply(ReplyFlags, string.Empty);

        Assert.That(CompletedByReply(m99), Is.True);
        Assert.That(CompletedByReply(startCode), Is.True);
        Assert.That(CompletedByReply(nextCode), Is.False);

        _processor.HandleReply(ReplyFlags, string.Empty);
        Assert.That(CompletedByReply(nextCode), Is.True);
    }

    [Test]
    public void M99ReadAheadAfterM99()
    {
        // Only the first M99 is executed, RRF discards the second one and never replies to it
        Code startCode = MakeCode(98), m99 = MakeCode(99), secondM99 = MakeCode(99);
        StartMacro(startCode, m99, secondM99);

        _processor.HandleReply(ReplyFlags, string.Empty);
        _processor.FilesAborted(false);
        _processor.HandleReply(ReplyFlags, string.Empty);

        Assert.That(CompletedByReply(startCode), Is.True);
        Assert.That(_processor.BufferedCodes, Is.Empty);
    }

    [Test]
    public void M99InMacroWithoutStartCode()
    {
        // e.g. daemon.g: RRF sends neither the M99 reply nor a reply for a start code back as code replies
        Code m99 = MakeCode(99);
        StartMacro(null, m99);

        _processor.FilesAborted(false);
        Assert.That(_processor.BufferedCodes, Is.Empty);

        // The next code on this channel must still get its reply
        Code nextCode = MakeCode(4);
        _processor.BufferedCodes.Add(nextCode);
        _processor.HandleReply(ReplyFlags, string.Empty);
        Assert.That(CompletedByReply(nextCode), Is.True);
    }
}
