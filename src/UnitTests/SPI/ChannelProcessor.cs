using System.IO;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Codes;
using DuetControlServer.Commands;
using DuetControlServer.Files;
using DuetControlServer.Link.Channel;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Code = DuetControlServer.Commands.Code;

namespace UnitTests.SPI;

/// <summary>
/// Tests for the matching of code replies to buffered codes, message boxes, macro files and the end of a print
/// </summary>
[TestFixture]
public class ChannelProcessor
{
    private const MessageTypeFlags ReplyFlags = MessageTypeFlags.BinaryCodeReplyFlag | (MessageTypeFlags)(1 << (int)CodeChannel.HTTP);

    private ServiceProvider _provider = null!;
    private Processor _processor = null!;
    private string _macroFile = null!;

    [SetUp]
    public void SetUp()
    {
        _macroFile = Path.GetTempFileName();

        _provider = DcsServices.Build();
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

    /// <summary>
    /// Check if a code has been completed, cancelled codes have no result
    /// </summary>
    private static bool IsCompleted(Code code) => code.Result is not null;

    /// <summary>
    /// Get the reply of a completed code without the newline that the Executed pipeline stage may already have appended on another thread
    /// </summary>
    private static string? Reply(Code code) => code.Result?.Content.TrimEnd('\n');

    [Test]
    public void M99WithoutReply()
    {
        Code startCode = MakeCode(98), m99 = MakeCode(99), readAhead = MakeCode(98);
        StartMacro(startCode, m99, readAhead);

        // RRF does not reply to an M99 that requested a single file abort, it writes [abort][empty reply for the start code]
        _processor.FilesAborted(false, false);
        Assert.That(IsCompleted(m99), Is.True);
        Assert.That(IsCompleted(readAhead), Is.False);
        Assert.That(_processor.BufferedCodes, Is.EqualTo(new[] { startCode }));

        // The caller's next code (e.g. another M98) may already have been sent when the reply for the start code arrives
        Code nextCode = MakeCode(98);
        _processor.BufferedCodes.Add(nextCode);
        _processor.HandleReply(ReplyFlags, string.Empty);

        Assert.That(IsCompleted(startCode), Is.True);
        Assert.That(IsCompleted(nextCode), Is.False);
        Assert.That(_processor.BufferedCodes, Is.EqualTo(new[] { nextCode }));

        _processor.HandleReply(ReplyFlags, string.Empty);
        Assert.That(IsCompleted(nextCode), Is.True);
    }

    [Test]
    public void M99ReplyBeforeAbortRequest()
    {
        Code startCode = MakeCode(98), m99 = MakeCode(99), readAhead = MakeCode(98);
        StartMacro(startCode, m99, readAhead);

        // Older RRF versions write [M99 reply][abort][empty reply for the start code]
        _processor.HandleReply(ReplyFlags, string.Empty);
        _processor.FilesAborted(false, false);
        Code nextCode = MakeCode(98);
        _processor.BufferedCodes.Add(nextCode);
        _processor.HandleReply(ReplyFlags, string.Empty);

        Assert.That(IsCompleted(m99), Is.True);
        Assert.That(IsCompleted(startCode), Is.True);
        Assert.That(IsCompleted(nextCode), Is.False);

        _processor.HandleReply(ReplyFlags, string.Empty);
        Assert.That(IsCompleted(nextCode), Is.True);
    }

    [Test]
    public void M99ReadAheadAfterM99()
    {
        // Only the first M99 is executed, RRF discards the second one and never replies to it
        Code startCode = MakeCode(98), m99 = MakeCode(99), secondM99 = MakeCode(99);
        StartMacro(startCode, m99, secondM99);

        _processor.FilesAborted(false, false);
        _processor.HandleReply(ReplyFlags, string.Empty);

        Assert.That(IsCompleted(m99), Is.True);
        Assert.That(IsCompleted(secondM99), Is.False);
        Assert.That(IsCompleted(startCode), Is.True);
        Assert.That(_processor.BufferedCodes, Is.Empty);
    }

    [Test]
    public void M99InMacroWithoutStartCode()
    {
        // e.g. daemon.g: RRF sends neither the M99 reply nor a reply for a start code back as code replies
        Code m99 = MakeCode(99);
        StartMacro(null, m99);

        _processor.FilesAborted(false, false);
        Assert.That(IsCompleted(m99), Is.True);
        Assert.That(_processor.BufferedCodes, Is.Empty);

        // The next code on this channel must still get its reply
        Code nextCode = MakeCode(4);
        _processor.BufferedCodes.Add(nextCode);
        _processor.HandleReply(ReplyFlags, string.Empty);
        Assert.That(IsCompleted(nextCode), Is.True);
    }

    [Test]
    public void PartialReply()
    {
        Code code = MakeCode(122);
        _processor.BufferedCodes.Add(code);

        // RRF splits long replies and sets the push flag on every chunk but the last. A flag of an earlier chunk applies to the whole reply
        _processor.HandleReply(ReplyFlags | MessageTypeFlags.PushFlag | MessageTypeFlags.WarningMessageFlag, "first ");
        Assert.That(IsCompleted(code), Is.False);
        Assert.That(_processor.BufferedCodes, Is.EqualTo(new[] { code }));

        _processor.HandleReply(ReplyFlags, "second");
        Assert.That(Reply(code), Is.EqualTo("first second"));
        Assert.That(code.Result?.Type, Is.EqualTo(MessageType.Warning));
        Assert.That(_processor.BufferedCodes, Is.Empty);
    }

    [TestCase(MessageTypeFlags.ErrorMessageFlag, MessageType.Error)]
    [TestCase(MessageTypeFlags.WarningMessageFlag, MessageType.Warning)]
    [TestCase(MessageTypeFlags.ErrorMessageFlag | MessageTypeFlags.WarningMessageFlag, MessageType.Error)]
    [TestCase(MessageTypeFlags.NoDestinationMessage, MessageType.Success)]
    public void ReplyType(MessageTypeFlags typeFlags, MessageType type)
    {
        Code code = MakeCode(4);
        _processor.BufferedCodes.Add(code);
        _processor.HandleReply(ReplyFlags | typeFlags, "reply");
        Assert.That(code.Result?.Type, Is.EqualTo(type));
    }

    [TestCase(MessageTypeFlags.LogOff, EventLogLevel.Off)]
    [TestCase(MessageTypeFlags.LogWarn, EventLogLevel.Warn)]
    [TestCase(MessageTypeFlags.LogInfo, EventLogLevel.Info)]
    [TestCase(MessageTypeFlags.NoDestinationMessage, EventLogLevel.Debug)]
    public void ReplyLogLevel(MessageTypeFlags logFlags, EventLogLevel logLevel)
    {
        Code code = MakeCode(4);
        _processor.BufferedCodes.Add(code);
        _processor.HandleReply(ReplyFlags | logFlags, "reply");
        Assert.That(code.ReplyLogLevel, Is.EqualTo(logLevel));
    }

    [Test]
    public void OutOfOrderReply()
    {
        // No code waits for these replies
        Assert.That(_processor.HandleReply(ReplyFlags, "unexpected"), Is.False);
        Assert.That(_processor.HandleReply(ReplyFlags, string.Empty), Is.False);
    }

    [Test]
    public void PrintCompletionReply()
    {
        // When a print runs to the end, RRF runs stop.g and replies once more, and no code waits for that reply
        _processor.WaitForPrintCompletion();
        Assert.That(_processor.HandleReply(ReplyFlags, "unexpected"), Is.False, "only an empty reply confirms the end of the print");
        Assert.That(_processor.HandleReply(ReplyFlags, string.Empty), Is.True);
        Assert.That(_processor.HandleReply(ReplyFlags, string.Empty), Is.False, "the end of the print is confirmed only once");
    }

    [Test]
    public void PrintCompletionReplyBeforeStopMacroClosed()
    {
        // RRF writes the final reply before the request that closes stop.g, which has no start code
        StartMacro(null);
        _processor.CurrentState.MacroCompleted = true;
        _processor.WaitForPrintCompletion();

        Assert.That(_processor.HandleReply(ReplyFlags, string.Empty), Is.True);
        Assert.That(_processor.PendingReplies, Is.Empty);
    }

    [Test]
    public void PrintCompletionAfterCodeReplies()
    {
        Code code = MakeCode(4);
        _processor.BufferedCodes.Add(code);
        _processor.WaitForPrintCompletion();

        // A code that waits for its reply gets it first, the confirmation follows
        _processor.HandleReply(ReplyFlags, string.Empty);
        Assert.That(IsCompleted(code), Is.True);
        Assert.That(_processor.HandleReply(ReplyFlags, string.Empty), Is.True);
    }

    [Test]
    public void InvalidateStopsWaitingForPrintCompletion()
    {
        _processor.WaitForPrintCompletion();
        _processor.Invalidate(false);
        Assert.That(_processor.HandleReply(ReplyFlags, string.Empty), Is.False);
    }

    [Test]
    public void ReplyWhileMessageBoxOpen()
    {
        Code m291 = MakeCode(291);
        _processor.BufferedCodes.Add(m291);

        // RRF requests the acknowledgement for the blocking message box of M291 S2 or S3
        _processor.WaitForAcknowledgement();
        Assert.That(_processor.BufferedCodes, Is.Empty);
        Assert.That(_processor.CurrentState.StartCode, Is.SameAs(m291));

        // A reply while the box is open waits until the box has been acknowledged
        Assert.That(_processor.HandleReply(ReplyFlags, "reply"), Is.True);
        Assert.That(IsCompleted(m291), Is.False);

        _processor.MessageAcknowledged();
        Assert.That(Reply(m291), Is.EqualTo("reply"));
        Assert.That(_processor.Stack, Has.Count.EqualTo(1));
    }

    [Test]
    public void ReplyAfterMessageBoxAcknowledged()
    {
        Code m291 = MakeCode(291);
        _processor.BufferedCodes.Add(m291);
        _processor.WaitForAcknowledgement();

        // Acknowledging the box puts M291 back in line for its reply
        _processor.MessageAcknowledged();
        Assert.That(_processor.BufferedCodes, Is.EqualTo(new[] { m291 }));
        _processor.HandleReply(ReplyFlags, string.Empty);
        Assert.That(IsCompleted(m291), Is.True);
    }

    [Test]
    public void ReplyForStartCodeBeforeMacroFileClosed()
    {
        Code startCode = MakeCode(98);
        StartMacro(startCode);
        _processor.CurrentState.MacroCompleted = true;

        // RRF may send the reply for the code that started the macro before it reports the macro file closed
        Assert.That(_processor.HandleReply(ReplyFlags, "done"), Is.True);
        Assert.That(IsCompleted(startCode), Is.False);

        _processor.MacroFileClosed();
        Assert.That(Reply(startCode), Is.EqualTo("done"));
        Assert.That(_processor.BufferedCodes, Is.Empty);
        Assert.That(_processor.Stack, Has.Count.EqualTo(1));
    }

    [Test]
    public void ReplyForStartCodeAfterMacroFileClosed()
    {
        Code startCode = MakeCode(98);
        StartMacro(startCode);
        _processor.CurrentState.MacroCompleted = true;

        _processor.MacroFileClosed();
        Assert.That(_processor.BufferedCodes, Is.EqualTo(new[] { startCode }));

        _processor.HandleReply(ReplyFlags, "done");
        Assert.That(Reply(startCode), Is.EqualTo("done"));
    }

    [Test]
    public void InvalidateCancelsCodesAndMacros()
    {
        Code startCode = MakeCode(98), macroCode = MakeCode(4);
        StartMacro(startCode, macroCode);

        // e.g. after RRF has been reset
        _processor.Invalidate(false);
        Assert.That(IsCompleted(macroCode), Is.False);
        Assert.That(_processor.BufferedCodes, Is.Empty);
        Assert.That(_processor.Stack, Has.Count.EqualTo(1));

        // Nothing on this channel waits for a reply any more
        Assert.That(_processor.HandleReply(ReplyFlags, string.Empty), Is.False);
    }
}
