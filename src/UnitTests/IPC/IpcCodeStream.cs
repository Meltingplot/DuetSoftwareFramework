using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using DuetControlServer.IPC.Processors;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// Tests of the CodeStream mode (DuetControlServer/IPC/Processors/CodeStream.cs): codes are sent line by line and the replies come back as text
/// </summary>
[TestFixture]
public class IpcCodeStream
{
    private FakeFirmwareHost _host = null!;
    private IpcLog _log = null!;

    /// <summary>
    /// Number of times M7301 has been executed by the firmware
    /// </summary>
    private int _slowCodes;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await FakeFirmwareHost.StartAsync(firmware =>
        {
            firmware.OnCode("M7300", _ => FakeReply.Ok("fast"));
            firmware.OnCode("M7302", _ => FakeReply.Error("failed"));
            int spins = 0;
            firmware.OnCode("M7301", _ =>
            {
                // Finish only on every tenth spin, so that the next codes have to wait for a free code instance
                if (++spins % 10 != 0)
                {
                    return FakeReply.NotFinished;
                }
                Interlocked.Increment(ref _slowCodes);
                return FakeReply.Ok("slow");
            });
        }, ipc: true);
        _log = IpcLog.Attach(_host);
    }

    [OneTimeTearDown]
    public async Task StopHost() => await _host.DisposeAsync();

    [SetUp]
    public void ClearLog() => _log.Clear();

    [TearDown]
    public void CheckLog()
    {
        IpcStatics.RemoveLeakedCodeStreams();
        Assert.That(_log.UnexpectedExceptions, Is.Empty, "The IPC server ran into an exception it did not expect");
    }

    private sealed class Stream(CodeStreamConnection connection) : IDisposable
    {
        public CodeStreamConnection Connection { get; } = connection;
        private readonly NetworkStream _stream = connection.GetStream();
        public StreamReader Reader => _reader ??= new(_stream);
        private StreamReader? _reader;
        public StreamWriter Writer => _writer ??= new(_stream) { AutoFlush = true, NewLine = "\n" };
        private StreamWriter? _writer;

        public async Task<string> ReadLineAsync()
        {
            using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
            return await Reader.ReadLineAsync(cts.Token) ?? throw new IOException("Code stream closed");
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _stream.Dispose();
            Connection.Dispose();
        }
    }

    private async Task<Stream> ConnectAsync(int bufferSize = 4, CodeChannel channel = CodeChannel.SBC)
    {
        CodeStreamConnection connection = new();
        await connection.ConnectAsync(bufferSize, channel, _host.SocketPath);
        return new Stream(connection);
    }

    [Test]
    public async Task CodesAreExecutedLineByLine()
    {
        using Stream stream = await ConnectAsync();
        await stream.Writer.WriteLineAsync("echo 1 + 2");
        Assert.That(await stream.ReadLineAsync(), Is.EqualTo("3"));
        await stream.Writer.WriteLineAsync("M7300");
        Assert.That(await stream.ReadLineAsync(), Is.EqualTo("fast"));
        await stream.Writer.WriteLineAsync("M7302");
        // The fake firmware prefixes errors with the code like RRF does
        Assert.That(await stream.ReadLineAsync(), Is.EqualTo("Error: M7302: failed"));
    }

    [Test]
    public async Task SeveralCodesOnALine()
    {
        // CodeStream.ProcessAsync parses codes until the line is used up
        using Stream stream = await ConnectAsync();
        await stream.Writer.WriteLineAsync("M7300 M7300");
        Assert.That(await stream.ReadLineAsync(), Is.EqualTo("fast"));
        Assert.That(await stream.ReadLineAsync(), Is.EqualTo("fast"));
    }

    [Test]
    public async Task ParseErrorIsReportedAndTheStreamGoesOn()
    {
        // A parse error is written as "Error: Failed to parse code from line ..." and the next line is read (CodeStream.ProcessAsync)
        using Stream stream = await ConnectAsync();
        await stream.Writer.WriteLineAsync("G1 X{");
        Assert.That(await stream.ReadLineAsync(), Is.EqualTo("Error: Failed to parse code from line 'G1 X{'"));
        await stream.Writer.WriteLineAsync("M7300");
        Assert.That(await stream.ReadLineAsync(), Is.EqualTo("fast"));
    }

    [Test]
    public async Task ParseErrorWaitsForPendingCodes()
    {
        // The faulty line is reported only after the pending codes have finished, so the replies stay in order
        using Stream stream = await ConnectAsync(bufferSize: 3);
        int slowCodes = Volatile.Read(ref _slowCodes);
        await stream.Writer.WriteAsync("M7301\nG1 X{\nM7300\n");
        Assert.That(await stream.ReadLineAsync(), Is.EqualTo("slow"));
        Assert.That(await stream.ReadLineAsync(), Is.EqualTo("Error: Failed to parse code from line 'G1 X{'"));
        Assert.That(await stream.ReadLineAsync(), Is.EqualTo("fast"));
        Assert.That(Volatile.Read(ref _slowCodes), Is.EqualTo(slowCodes + 1));
    }

    [Test]
    public async Task CodesWaitForAFreeBuffer()
    {
        // With a buffer of one code, the next code waits until the previous one has finished (CodeStream.ProcessAsync)
        using Stream stream = await ConnectAsync(bufferSize: 1);
        await stream.Writer.WriteAsync("M7301\nM7301\nM7301\n");
        for (int i = 0; i < 3; i++)
        {
            Assert.That(await stream.ReadLineAsync(), Is.EqualTo("slow"));
        }
    }

    [Test]
    public async Task MarlinClientsGetAnOkAfterErrors()
    {
        // Repetier and other hosts expect an "ok" after an error message in Marlin and NanoDLP emulation (CodeStream.ProcessAsync)
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Inputs[CodeChannel.Telnet]!.Compatibility = Compatibility.Marlin;
        }
        try
        {
            using Stream stream = await ConnectAsync(channel: CodeChannel.Telnet);
            await stream.Writer.WriteLineAsync("G1 X{");
            Assert.That(await stream.ReadLineAsync(), Is.EqualTo("Error: Failed to parse code from line 'G1 X{'"));
            Assert.That(await stream.ReadLineAsync(), Is.EqualTo("ok"));
        }
        finally
        {
            using (await _host.Model.AccessReadWriteAsync())
            {
                _host.Model.Inputs[CodeChannel.Telnet]!.Compatibility = Compatibility.RepRapFirmware;
            }
        }
    }

    [Test]
    public async Task MessagesForTheChannelAreForwarded()
    {
        // LinkService passes targeted messages to CodeStream.RecordMessage, which writes them to the streams of that channel
        using Stream sbc = await ConnectAsync(channel: CodeChannel.SBC);
        using Stream telnet = await ConnectAsync(channel: CodeChannel.Telnet);
        Assert.That(CodeStream.HasClientsWaitingForMessages, Is.True, "Telnet is one of the channels of generic messages");
        _host.Firmware.SendMessage(MessageTypeFlags.SbcMessage, "for SBC\n");
        Assert.That(await sbc.ReadLineAsync(), Is.EqualTo("for SBC"));
        _host.Firmware.SendMessage(MessageTypeFlags.TelnetMessage | MessageTypeFlags.WarningMessageFlag, "for Telnet\n");
        Assert.That(await telnet.ReadLineAsync(), Is.EqualTo("Warning: for Telnet"));
    }

    [Test]
    public async Task ClosedStreamIsUnregistered()
    {
        int streams = IpcStatics.CodeStreamCount;
        Stream stream = await ConnectAsync();
        await stream.Writer.WriteLineAsync("M7300");
        Assert.That(await stream.ReadLineAsync(), Is.EqualTo("fast"));
        Assert.That(IpcStatics.CodeStreamCount, Is.EqualTo(streams + 1));
        stream.Dispose();

        // ReadLineAsync gets the end of the stream and ProcessAsync removes the stream again
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        while (IpcStatics.CodeStreamCount != streams)
        {
            await Task.Delay(10, cts.Token);
        }
    }

    [Test]
    public async Task ClientClosesWhileCodesAreRunning()
    {
        // Replies of codes that finish after the client has gone are dropped
        int slowCodes = Volatile.Read(ref _slowCodes);
        using (Stream stream = await ConnectAsync())
        {
            await stream.Writer.WriteAsync("M7301\nM7301\n");
        }
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        while (Volatile.Read(ref _slowCodes) < slowCodes + 2)
        {
            await Task.Delay(10, cts.Token);
        }

        // The channel is still usable
        using Stream next = await ConnectAsync();
        await next.Writer.WriteLineAsync("M7300");
        Assert.That(await next.ReadLineAsync(), Is.EqualTo("fast"));
    }

    [Test]
    public void ConnectsSynchronously()
    {
        using CodeStreamConnection connection = new();
        connection.Connect(2, CodeChannel.SBC, _host.SocketPath);
        Assert.That(connection.BufferSize, Is.EqualTo(2));
        using NetworkStream stream = connection.GetStream();
        using StreamWriter writer = new(stream) { AutoFlush = true };
        using StreamReader reader = new(stream);
        writer.Write("M7300\n");
        Assert.That(reader.ReadLine(), Is.EqualTo("fast"));
    }

    [Test]
    [Explicit("Bug: the CodeStream constructor registers the stream before it checks the buffer size, so a refused stream stays registered")]
    public async Task RefusedStreamIsNotRegistered()
    {
        // Only ProcessAsync removes a stream from CodeStream._streams, but a refused stream never gets there. The leaked entry keeps
        // CodeStream.HasClientsWaitingForMessages true (its channel is still the default, HTTP), so LockWrapper keeps clearing the messages
        int streams = IpcStatics.CodeStreamCount;
        using (RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!))
        {
            using var response = await client.RequestAsync("{\"mode\":\"CodeStream\",\"version\":13,\"bufferSize\":0,\"channel\":\"SBC\"}");
            Assert.That(RawIpcClient.GetErrorType(response.RootElement), Is.EqualTo(nameof(ArgumentException)));
            (_, bool closed) = await client.ReceiveUntilClosedAsync(TimeSpan.FromSeconds(5));
            Assert.That(closed, Is.True);
        }
        Assert.That(IpcStatics.CodeStreamCount, Is.EqualTo(streams));
    }
}
