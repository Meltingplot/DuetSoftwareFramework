using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Commands;
using DuetControlServer.Link;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace UnitTests.SPI;

/// <summary>
/// What LinkInterface checks before a request goes to the link, and how it resolves requests that are pending when the link is invalidated.
/// No transfer takes place, the requests only queue up
/// </summary>
[TestFixture]
public class LinkInterfaceRequests
{
    /// <summary>
    /// Link adapter of a firmware with the given protocol version that does nothing else
    /// </summary>
    public class VersionedLinkAdapter : DispatchProxy
    {
        public int Version { get; set; } = Consts.ProtocolVersion;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            (targetMethod!.Name == $"get_{nameof(ILinkAdapter.ProtocolVersion)}") ? Version :
                (targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)) ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }

    private static ServiceProvider Build(int protocolVersion = Consts.ProtocolVersion)
    {
        ILinkAdapter adapter = DispatchProxy.Create<ILinkAdapter, VersionedLinkAdapter>();
        ((VersionedLinkAdapter)(object)adapter).Version = protocolVersion;
        return DcsServices.Build(new Settings(), adapter);
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Requests with a cancelled token are cancelled at once, without going to the link
    /// </summary>
    [Test]
    public async Task CancelledRequests()
    {
        using ServiceProvider provider = Build();
        LinkInterface link = provider.GetRequiredService<LinkInterface>();
        CancellationToken cancelled = new(true);
        await Assert.ThatAsync(() => link.RequestObjectModel("state", "v", cancelled), Throws.InstanceOf<OperationCanceledException>());
        await Assert.ThatAsync(() => link.EvaluateExpressionAsync(CodeChannel.SBC, "1", cancelled), Throws.InstanceOf<OperationCanceledException>());
        await Assert.ThatAsync(() => link.SetVariableAsync(CodeChannel.SBC, true, "global.x", "1", cancelled), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(link.ModelQueryRequests, Is.Empty);
        Assert.That(link.EvaluateExpressionRequests, Is.Empty);
        Assert.That(link.VariableRequests, Is.Empty);
    }

    /// <summary>
    /// Expressions and variable names must fit the fields of RRF (Consts.MaxExpressionLength and MaxVariableLength, MaxSbcExpressionLength in
    /// SbcMessageFormats.h), and messages the configured maximum
    /// </summary>
    [Test]
    public void TooLong()
    {
        using ServiceProvider provider = Build();
        LinkInterface link = provider.GetRequiredService<LinkInterface>();
        string longExpression = new('1', Consts.MaxExpressionLength), longName = "global." + new string('x', Consts.MaxVariableLength);
        Assert.Throws<ArgumentException>(() => link.EvaluateExpressionAsync(CodeChannel.SBC, longExpression));
        Assert.Throws<ArgumentException>(() => link.SetVariableAsync(CodeChannel.SBC, true, longName, "1"));
        Assert.Throws<ArgumentException>(() => link.SetVariableAsync(CodeChannel.SBC, true, "global.x", longExpression));
        Assert.Throws<ArgumentException>(() => link.SendMessage(MessageTypeFlags.UsbMessage, new string('m', new Settings().MaxMessageLength + 1)));
    }

    /// <summary>
    /// Firmware of protocol version 1 cannot evaluate expressions or take messages, and variables need version 5
    /// </summary>
    [TestCase(1), TestCase(4)]
    public void OldFirmware(int protocolVersion)
    {
        using ServiceProvider provider = Build(protocolVersion);
        LinkInterface link = provider.GetRequiredService<LinkInterface>();
        Assert.Throws<NotSupportedException>(() => link.SetVariableAsync(CodeChannel.SBC, true, "global.x", "1"));
        if (protocolVersion == 1)
        {
            Assert.Throws<NotSupportedException>(() => link.EvaluateExpressionAsync(CodeChannel.SBC, "1"));
            Assert.Throws<NotSupportedException>(() => link.SendMessage(MessageTypeFlags.UsbMessage, "hi"));
        }
    }

    /// <summary>
    /// The same expression on the same channel is evaluated once, and changed object model keys are reported once
    /// </summary>
    [Test]
    public void NoDuplicateRequests()
    {
        using ServiceProvider provider = Build();
        LinkInterface link = provider.GetRequiredService<LinkInterface>();
        _ = link.EvaluateExpressionAsync(CodeChannel.SBC, "move.axes[0].homed");
        _ = link.EvaluateExpressionAsync(CodeChannel.SBC, "move.axes[0].homed");
        _ = link.EvaluateExpressionAsync(CodeChannel.HTTP, "move.axes[0].homed");
        Assert.That(link.EvaluateExpressionRequests, Has.Count.EqualTo(2));

        link.ObjectModelKeyChanged("network");
        link.ObjectModelKeyChanged("network");
        link.ObjectModelKeyChanged("volumes");
        Assert.That(link.UpdatedObjectModelKeys, Is.EqualTo(new[] { "network", "volumes" }));
    }

    /// <summary>
    /// A code without a result has nothing to tell RRF, most likely because it was cancelled (LinkInterface.SetLastCodeResultAsync)
    /// </summary>
    [Test]
    public void CodeWithoutResult()
    {
        using ServiceProvider provider = Build();
        LinkInterface link = provider.GetRequiredService<LinkInterface>();
        Code code = provider.GetRequiredService<CommandFactory>().Create<Code>();
        Assert.That(link.SetLastCodeResultAsync(code).IsCompletedSuccessfully, Is.True);
        Assert.That(link.SetLastCodeResultRequests, Is.Empty);
    }

    /// <summary>
    /// Only one firmware update can be pending
    /// </summary>
    [Test]
    public async Task OneFirmwareUpdateAtATime()
    {
        using ServiceProvider provider = Build();
        LinkInterface link = provider.GetRequiredService<LinkInterface>();
        using CancellationTokenSource cts = new();
        Task first = link.UpdateFirmware(new MemoryStream([1]), new MemoryStream([2]), cts.Token);
        await Assert.ThatAsync(() => link.UpdateFirmware(new MemoryStream([1]), new MemoryStream([2])), Throws.InvalidOperationException);
        cts.Cancel();
        await Assert.ThatAsync(() => first, Throws.InstanceOf<OperationCanceledException>());

        // Nothing holds the update lock meanwhile
        await link.WaitForUpdateAsync().WaitAsync(Timeout);
    }

    /// <summary>
    /// When the link is invalidated, every pending request is cancelled: print info and print stop, code results, evaluations, variables and
    /// object model queries, and the keys and messages still to send are dropped (LinkInterface.Invalidate and InvalidateAsync)
    /// </summary>
    [TestCase(false), TestCase(true)]
    public async Task InvalidateCancelsPendingRequests(bool asynchronous)
    {
        using ServiceProvider provider = Build();
        LinkInterface link = provider.GetRequiredService<LinkInterface>();
        Code code = provider.GetRequiredService<CommandFactory>().Create<Code>();
        code.Channel = CodeChannel.SBC;
        code.Result = new Message(MessageType.Success, string.Empty);

        Task[] pending =
        [
            link.SetPrintFileInfo(),
            link.StopPrintAsync(PrintStoppedReason.Abort),
            link.SetLastCodeResultAsync(code),
            link.EvaluateExpressionAsync(CodeChannel.SBC, "1+1"),
            link.SetVariableAsync(CodeChannel.SBC, true, "global.x", "1"),
            link.RequestObjectModel("state", "v")
        ];
        link.ObjectModelKeyChanged("network");
        link.SendMessage(MessageTypeFlags.UsbMessage, "hi");

        // The print requests wait for the print state lock
        while (link.SetPrintInfoRequest is null || link.StopPrintRequest is null)
        {
            await Task.Yield();
        }

        if (asynchronous)
        {
            await link.InvalidateAsync(CancellationToken.None);
        }
        else
        {
            link.Invalidate();
        }
        foreach (Task task in pending)
        {
            await Assert.ThatAsync(() => task.WaitAsync(Timeout), Throws.InstanceOf<OperationCanceledException>());
        }
        Assert.That(link.UpdatedObjectModelKeys, Is.Empty);
        Assert.That(link.MessagesToSend, Is.Empty);
        Assert.That(link.BufferSpace, Is.Zero);
    }

    /// <summary>
    /// Link adapter that takes every code
    /// </summary>
    public class CodeLinkAdapter : VersionedLinkAdapter
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            (targetMethod!.Name == nameof(ILinkAdapter.WriteCode)) ? true : base.Invoke(targetMethod, args);
    }

    /// <summary>
    /// RRF keeps one code buffer of SbcCodeBufferSize (4096) bytes for all channels and stores each code with a BufferedCodeHeader of 4 bytes
    /// in front of it (SbcInterface.cpp:76, 404-412, SbcMessageFormats.h:31, 47, 368-373). Codes buffered ahead leave room for two codes of
    /// maximum size free, which only the next code of a channel without buffered codes may use (LinkInterface.SendCode, Processor.BufferCode)
    /// </summary>
    [Test]
    public void CodesBufferedAheadLeaveSpareRoom()
    {
        ILinkAdapter adapter = DispatchProxy.Create<ILinkAdapter, CodeLinkAdapter>();
        using ServiceProvider provider = DcsServices.Build(new Settings(), adapter);
        LinkInterface link = provider.GetRequiredService<LinkInterface>();
        Code code = provider.GetRequiredService<CommandFactory>().Create<Code>();
        const int maxCodeSize = Consts.BufferedCodeHeaderSize + 384, spareRoom = 2 * maxCodeSize;

        // The job streams share what is left of the reported buffer
        link.MaxReportedBufferSpace = link.BufferSpace = 4096;
        Assert.That(link.GetMaxBufferSpacePerChannel(1), Is.EqualTo(4096 - spareRoom));
        Assert.That(link.GetMaxBufferSpacePerChannel(2), Is.EqualTo((4096 - spareRoom) / 2));

        // A code buffered ahead must not take the spare room, whichever channel it is on
        link.BufferSpace = spareRoom + 100;
        Assert.That(link.SendCode(code, 100, false), Is.False);
        Assert.That(link.BufferSpace, Is.EqualTo(spareRoom + 100));
        Assert.That(link.SendCode(code, 99, false), Is.True);
        Assert.That(link.BufferSpace, Is.EqualTo(spareRoom + 1));
        Assert.That(link.SendCode(code, 1, false), Is.False);

        // The next code of two channels without buffered codes still fits, even with the maximum size
        Assert.That(link.SendCode(code, maxCodeSize, true), Is.True);
        Assert.That(link.SendCode(code, maxCodeSize, true), Is.True);
        Assert.That(link.BufferSpace, Is.EqualTo(1));
        Assert.That(link.BytesReserved, Is.EqualTo(99 + 2 * maxCodeSize));
    }

    /// <summary>
    /// M122 of DSF shows the space of the code buffer
    /// </summary>
    [Test]
    public void Diagnostics()
    {
        using ServiceProvider provider = Build();
        LinkInterface link = provider.GetRequiredService<LinkInterface>();
        StringBuilder builder = new();
        link.PrintDiagnostics(builder);
        Assert.That(builder.ToString(), Is.EqualTo($"Code buffer space: 0{Environment.NewLine}"));
    }
}
