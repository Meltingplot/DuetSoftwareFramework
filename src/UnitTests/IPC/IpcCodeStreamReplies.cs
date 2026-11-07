using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// Replies and buffer limits of the CodeStream mode (DuetControlServer/IPC/Processors/CodeStream.cs), as the CodeStream tool uses it
/// (src/CodeStream/Commands.cs writes lines and prints the reply lines). <see cref="IpcCodeStream"/> covers parsing, parse errors and messages
/// </summary>
[TestFixture]
public class IpcCodeStreamReplies
{
    private FakeFirmwareHost _host = null!;
    private IpcLog _log = null!;

    /// <summary>
    /// Whether M7601 is held in the firmware
    /// </summary>
    private volatile bool _hold;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await FakeFirmwareHost.StartAsync(firmware =>
        {
            firmware.OnCode("M7600", code => FakeReply.Ok(code.ToString()));
            firmware.OnCode("M7601", code => _hold ? FakeReply.NotFinished : FakeReply.Ok(code.ToString()));
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
        IpcStatics.RemoveLeakedCodeStreams();
        Assert.That(_log.UnexpectedExceptions, Is.Empty, "The IPC server ran into an exception it did not expect");
    }

    private sealed class Stream : IDisposable
    {
        private readonly CodeStreamConnection _connection;
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public Stream(CodeStreamConnection connection)
        {
            _connection = connection;
            _stream = connection.GetStream();
            _reader = new(_stream);
            _writer = new(_stream) { AutoFlush = true, NewLine = "\n" };
        }

        public Task WriteAsync(string text) => _writer.WriteAsync(text);

        public async Task<string> ReadLineAsync(TimeSpan? timeout = null)
        {
            using CancellationTokenSource cts = new(timeout ?? FakeFirmwareHost.DefaultTimeout);
            try
            {
                return await _reader.ReadLineAsync(cts.Token) ?? throw new IOException("Code stream closed");
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException("No reply line in time");
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            _writer.Dispose();
            _stream.Dispose();
            _connection.Dispose();
        }
    }

    private async Task<Stream> ConnectAsync(int bufferSize, CodeChannel channel = CodeChannel.Telnet)
    {
        CodeStreamConnection connection = new();
        await connection.ConnectAsync(bufferSize, channel, _host.SocketPath);
        return new Stream(connection);
    }

    [Test]
    public async Task EveryCodeGetsOneReplyLine()
    {
        // With a buffer of one code each code waits for the previous one, so the replies come in order (CodeStream.cs:161-168, 212-219).
        // An empty line holds no code and gets no reply, a code without reply text gets an empty line (Executed.cs:153-156 makes an empty result "\n"),
        // and a code that fails with an ArgumentException is reported as "Error: ..." (CodeStream.cs:195-197; FilePathResolver.cs:118 refuses drive 9).
        // The failing code comes last, see FailedCodeKeepsTheReplyOfTheNextCode
        using Stream stream = await ConnectAsync(1);
        await stream.WriteAsync("M7600 P1\n\n; comment\nM7600 P2\nM470 P\"9:/new\"\n");
        Assert.That(await stream.ReadLineAsync(), Is.EqualTo("M7600 P1"));
        Assert.That(await stream.ReadLineAsync(), Is.Empty);
        Assert.That(await stream.ReadLineAsync(), Is.EqualTo("M7600 P2"));
        Assert.That(await stream.ReadLineAsync(), Is.EqualTo("Error: Invalid drive index"));
    }

    /// <summary>
    /// CodeStream.ProcessAsync puts a code instance back into its buffer as soon as the task of the code has completed (CodeStream.cs:212-219) and
    /// reuses it for the next line (Reset, CodeStream.cs:169). A code that fails in the pipeline is completed by CodeProcessor.CancelCode, which sets
    /// the exception first and only then hands the same instance to the Executed stage (CodeProcessor.cs:277-285, CodeCompleted). If the stream has
    /// already reused the instance by then, the Executed stage cancels the next code ("Cancelled code" with an empty code in the DCS log, then the next
    /// code is started and finished empty) and the reply of the next code is lost. It depends on timing; in the unit test suite it showed after
    /// other IPC fixtures had run
    /// </summary>
    [Test]
    [Explicit("Bug: a code that fails in a code stream can cancel the next code of the stream, whose reply is then lost")]
    public async Task FailedCodeKeepsTheReplyOfTheNextCode()
    {
        using Stream stream = await ConnectAsync(1);
        for (int i = 0; i < 50; i++)
        {
            await stream.WriteAsync($"M470 P\"9:/new\"\nM7600 P{i}\n");
            Assert.That(await stream.ReadLineAsync(), Is.EqualTo("Error: Invalid drive index"), $"Round {i}");
            Assert.That(await stream.ReadLineAsync(TimeSpan.FromSeconds(2)), Is.EqualTo($"M7600 P{i}"), $"Round {i}");
        }
    }

    [Test]
    public async Task MarlinClientsGetAnOkAfterAFailedCode()
    {
        // In Marlin and NanoDLP emulation an error is followed by "ok" (CodeStream.cs:198-205)
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Inputs[CodeChannel.USB]!.Compatibility = Compatibility.Marlin;
        }
        try
        {
            using Stream stream = await ConnectAsync(1, CodeChannel.USB);
            await stream.WriteAsync("M470 P\"9:/new\"\n");
            Assert.That(await stream.ReadLineAsync(), Is.EqualTo("Error: Invalid drive index"));
            Assert.That(await stream.ReadLineAsync(), Is.EqualTo("ok"));
        }
        finally
        {
            using (await _host.Model.AccessReadWriteAsync())
            {
                _host.Model.Inputs[CodeChannel.USB]!.Compatibility = Compatibility.RepRapFirmware;
            }
        }
    }

    [Test]
    public async Task BufferSizeLimitsTheCodesInFlight()
    {
        // ProcessAsync creates BufferSize code instances and waits for a free one before it parses the next code (CodeStream.cs:135-141, 161-168),
        // so no more than BufferSize codes of the stream are in DCS at a time
        const int bufferSize = 2;
        using Stream stream = await ConnectAsync(bufferSize);
        int before = _host.Firmware.GetReceivedCodes(CodeChannel.Telnet).Count(code => code.MajorNumber == 7601);
        _hold = true;
        await stream.WriteAsync("M7601 P1\nM7601 P2\nM7601 P3\nM7601 P4\n");
        await IpcStatics.WaitUntilAsync(() => _host.Firmware.GetReceivedCodes(CodeChannel.Telnet).Count(code => code.MajorNumber == 7601) > before, "the firmware has the first code");
        await Task.Delay(300);
        Assert.That(_host.Firmware.GetReceivedCodes(CodeChannel.Telnet).Count(code => code.MajorNumber == 7601) - before, Is.LessThanOrEqualTo(bufferSize));

        _hold = false;
        List<string> replies = [];
        for (int i = 0; i < 4; i++)
        {
            replies.Add(await stream.ReadLineAsync());
        }
        // The order is checked by RepliesStayInTheOrderOfTheCodes
        Assert.That(replies, Is.EquivalentTo(new[] { "M7601 P1", "M7601 P2", "M7601 P3", "M7601 P4" }));
    }

    [Test]
    public async Task LargestBufferSizeIsAccepted()
    {
        // The CodeStream constructor accepts 1 to Defaults.MaxCodeBufferSize (CodeStream.cs:107-110)
        using Stream stream = await ConnectAsync(DuetAPI.Connection.Defaults.MaxCodeBufferSize);
        await stream.WriteAsync("M7600 P1\n");
        Assert.That(await stream.ReadLineAsync(), Is.EqualTo("M7600 P1"));
    }

    /// <summary>
    /// The replies of a code stream must come in the order of the codes: CodeStream.ProcessAsync makes a parse error wait for the pending codes
    /// because "the code reply has to be written as well" (CodeStream.cs:240-246), and the CodeStream tool prints the reply lines as they come.
    /// Each code writes its reply in a continuation that runs asynchronously (CodeStream.cs:179-220, RunContinuationsAsynchronously) and then waits
    /// for the output lock. Codes on a channel finish in order, but when several finish in one go their continuations run in any order, often
    /// reversed, so the replies are written out of order. The firmware receives the codes in order
    /// </summary>
    [Test]
    [Explicit("Bug: replies of a code stream with a buffer of more than one code are written out of order when several codes finish together")]
    public async Task RepliesStayInTheOrderOfTheCodes()
    {
        using Stream stream = await ConnectAsync(16);
        for (int round = 0; round < 10; round++)
        {
            StringBuilder codes = new();
            for (int i = 0; i < 16; i++)
            {
                codes.Append($"M7600 P{i}\n");
            }
            await stream.WriteAsync(codes.ToString());
            List<string> replies = [];
            for (int i = 0; i < 16; i++)
            {
                replies.Add(await stream.ReadLineAsync());
            }
            Assert.That(replies, Is.EqualTo(Enumerable.Range(0, 16).Select(i => $"M7600 P{i}")), $"Round {round}");
        }
    }
}
