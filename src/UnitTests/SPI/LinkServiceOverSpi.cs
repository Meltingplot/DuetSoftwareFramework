using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Link;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;
using SpiAdapter = DuetControlServer.Link.Adapter.SPI;

namespace UnitTests.SPI;

/// <summary>
/// The LinkService of DCS on the real SPI adapter against the SPI port of RRF (<see cref="FakeRrfSpi"/>). The fake RRF sends the requests
/// of SbcInterface byte for byte as the Write functions of RRF's DataTransfer write them (src/SBC/DataTransfer.cpp, structures in
/// src/SBC/SbcMessageFormats.h), and the tests read the answers of DSF as the Read functions of DataTransfer read them
/// </summary>
[TestFixture]
public partial class LinkServiceOverSpi
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    #region Host
    /// <summary>
    /// DCS services with the LinkService running on the SPI adapter
    /// </summary>
    internal sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ApplicationLifetime _lifetime;

        private Host(ServiceProvider provider, FakeRrfSpi rrf, LinkService service, string baseDirectory)
        {
            _provider = provider;
            _lifetime = (ApplicationLifetime)provider.GetRequiredService<IHostApplicationLifetime>();
            Rrf = rrf;
            Service = service;
            BaseDirectory = baseDirectory;
        }

        public FakeRrfSpi Rrf { get; }

        public LinkService Service { get; }

        public string BaseDirectory { get; }

        public LinkInterface Link => _provider.GetRequiredService<LinkInterface>();

        public DuetControlServer.Model.ObjectModel Model => _provider.GetRequiredService<DuetControlServer.Model.ObjectModel>();

        public SpiAdapter Adapter => (SpiAdapter)_provider.GetRequiredService<ILinkAdapter>();

        public T Get<T>() where T : notnull => _provider.GetRequiredService<T>();

        /// <summary>
        /// Start the LinkService on a fake RRF that polls while it is not connected, like the SAME70 builds do
        /// </summary>
        /// <param name="capabilities">Link capabilities RRF offers and reports in limits.sbcLinkCapabilities</param>
        /// <param name="createAdapter">Create the SPI adapter from the services, the fake RRF and the settings, or null for the plain one</param>
        /// <param name="respond">Answers of RRF to packets of DSF in the next transfer, see <see cref="FakeRrfSpi.Respond"/></param>
        /// <param name="answerCapabilities">Whether to wait for the query of the link capabilities and answer it</param>
        /// <param name="bootArmDelay">See <see cref="FakeRrfSpi.BootArmDelay"/></param>
        /// <param name="transferDelay">See <see cref="FakeRrfSpi.TransferDelay"/></param>
        /// <param name="noiseSeed">Seed of the random bit errors, see <see cref="FakeRrfSpi.BitErrorRate"/></param>
        /// <param name="configure">Set up the fake RRF before DCS connects</param>
        /// <param name="firstPackets">Packets of RRF that come with the transfer of the connect, see <see cref="FakeRrfSpi"/></param>
        public static async Task<Host> StartAsync(LinkCapabilities capabilities = LinkCapabilities.None,
            Func<IServiceProvider, FakeRrfSpi, IOptions<Settings>, SpiAdapter>? createAdapter = null,
            Func<SbcRequest, ushort, byte[], IEnumerable<(FirmwareRequest Request, byte[] Payload, ushort ResendPacketId)>>? respond = null, bool answerCapabilities = true,
            double bootArmDelay = 0, double transferDelay = 0, int noiseSeed = 1, Action<FakeRrfSpi, string>? configure = null,
            IEnumerable<(FirmwareRequest Request, byte[] Payload, ushort Tag)>? firstPackets = null)
        {
            string baseDirectory = Path.Combine(Path.GetTempPath(), "dsf-spi-link-" + Guid.NewGuid().ToString("N"));
            foreach (string directory in new[] { "sys", "macros", "gcodes", "filaments", "firmware" })
            {
                Directory.CreateDirectory(Path.Combine(baseDirectory, directory));
            }

            Settings settings = new() { BaseDirectory = baseDirectory };
            FakeRrfSpi rrf = new(_ => false, pollWhileDisconnected: true, capabilities: capabilities, firstPackets: firstPackets)
            {
                RecordPackets = true,
                ExchangeBudget = int.MaxValue,
                TimeBudget = double.PositiveInfinity,
                Respond = respond,
                BootArmDelay = bootArmDelay,
                TransferDelay = transferDelay,
                Noise = new Random(noiseSeed)
            };
            configure?.Invoke(rrf, baseDirectory);
            ServiceProvider provider = DcsServices.Build(settings, DispatchProxy.Create<ILinkAdapter, DcsServices.NullLinkAdapter>(), services =>
                services.AddSingleton<ILinkAdapter>(sp => createAdapter?.Invoke(sp, rrf, sp.GetRequiredService<IOptions<Settings>>()) ??
                    new SpiAdapter(sp.GetRequiredService<EventLogger>(), sp.GetRequiredService<DuetControlServer.Model.ObjectModel>(),
                        NullLogger<SpiAdapter>.Instance, sp.GetRequiredService<IOptions<Settings>>(), rrf, rrf, rrf.Clock)));
            LinkService service = ActivatorUtilities.CreateInstance<LinkService>(provider);
            Host host = new(provider, rrf, service, baseDirectory);
            using CancellationTokenSource cts = new(Timeout);
            await service.StartAsync(cts.Token);
            if (!answerCapabilities)
            {
                return host;
            }

            // DCS asks for the capabilities of the link after every connect (LinkService.UpdateLinkCapabilities). RRF answers null for the key
            // if it has none, like every limit it does not know
            byte[] query = host.WaitFor(SbcRequest.GetObjectModel);
            Assert.That(SbcPackets.GetObjectModel(query), Is.EqualTo(("limits.sbcLinkCapabilities", "v")));
            string result = (capabilities == LinkCapabilities.None) ? "null" : ((uint)capabilities).ToString();
            rrf.SendPacket(FirmwareRequest.ObjectModel, RrfPackets.String($"{{\"key\":\"limits.sbcLinkCapabilities\",\"flags\":\"v\",\"result\":{result}}}"));
            return host;
        }

        /// <summary>
        /// Wait for the next packet of the given kind from DSF
        /// </summary>
        public byte[] WaitFor(SbcRequest request, Func<byte[], bool>? match = null) =>
            Rrf.WaitForPacket((r, payload) => r == request && (match is null || match(payload)), Timeout) ?? throw new TimeoutException($"DSF did not send {request}");

        /// <summary>
        /// Wait until the given condition is true, checking it whenever DSF sends a packet and at least every few milliseconds
        /// </summary>
        public void WaitUntil(Func<bool> condition, string what)
        {
            DateTime end = DateTime.UtcNow + Timeout;
            while (!condition())
            {
                if (DateTime.UtcNow > end)
                {
                    throw new TimeoutException($"Timed out waiting for {what}");
                }
                Rrf.WaitForPacket((_, _) => false, TimeSpan.FromMilliseconds(5));
            }
        }

        /// <summary>
        /// Whether the transfer thread of the LinkService has stopped with an error
        /// </summary>
        public Exception? Fault => Service.ExecuteTask?.Exception;

        public async ValueTask DisposeAsync()
        {
            // A transfer thread that is held could not stop
            Rrf.BeforeExchange = null;
            Rrf.HoldWhile = null;
            Rrf.Release();
            _lifetime.StopApplication();
            using (CancellationTokenSource cts = new(Timeout))
            {
                try
                {
                    await Service.StopAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Stopping took too long
                }
            }
            _lifetime.NotifyStopped();
            await _provider.DisposeAsync();
            try
            {
                Directory.Delete(BaseDirectory, true);
            }
            catch (IOException)
            {
                // A file may still be open, the temporary directory is left behind then
            }
        }
    }
    #endregion

    #region Packets as RRF writes and reads them
    /// <summary>
    /// Packet data of the requests RRF sends, as the Write functions of DataTransfer write them
    /// </summary>
    internal static class RrfPackets
    {
        private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

        private static byte[] U16(int value) { byte[] b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)value); return b; }

        private static byte[] U32(long value) { byte[] b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)value); return b; }

        private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

        /// <summary>
        /// StringHeader and text: WriteObjectModel, WriteCheckFileExists, WriteDeleteFileOrDirectory, WriteSecureDeleteFile
        /// </summary>
        public static byte[] String(string text) => Concat(U16(Encoding.UTF8.GetByteCount(text)), U16(0), Text(text));

        /// <summary>
        /// CodeChannelHeader: WriteLocked, WriteMacroFileClosed, WriteWaitForAcknowledgement, WriteMessageAcknowledged
        /// </summary>
        public static byte[] Channel(CodeChannel channel) => [(byte)channel, 0, 0, 0];

        /// <summary>
        /// CodeBufferUpdateHeader: WriteCodeBufferUpdate
        /// </summary>
        public static byte[] CodeBufferUpdate(int bufferSpace) => Concat(U16(bufferSpace), U16(0));

        /// <summary>
        /// ExecuteMacroHeader and file name: WriteMacroRequest
        /// </summary>
        public static byte[] ExecuteMacro(CodeChannel channel, bool fromCode, string fileName) => Concat([(byte)channel, 0, (byte)(fromCode ? 1 : 0), (byte)Text(fileName).Length], Text(fileName));

        /// <summary>
        /// AbortFileHeader: WriteAbortFileRequest
        /// </summary>
        public static byte[] AbortFile(CodeChannel channel, bool abortAll) => [(byte)channel, (byte)(abortAll ? 1 : 0), 0, 0];

        /// <summary>
        /// PrintPausedHeader: WritePrintPaused
        /// </summary>
        public static byte[] PrintPaused(uint position, uint position2, PrintPausedReason reason) => Concat(U32(position), U32(position2), [(byte)reason, 0, 0, 0]);

        /// <summary>
        /// DoCodeHeader and code: WriteDoCode
        /// </summary>
        public static byte[] DoCode(CodeChannel channel, string code) => Concat([(byte)channel, 0], U16(Text(code).Length), Text(code));

        /// <summary>
        /// EvaluationResultHeader with an integer and the expression: WriteEvaluationResult and WriteSetVariableResult for TypeCode::Int32
        /// </summary>
        public static byte[] IntResult(CodeChannel channel, string expression, int value) => Concat([(byte)DataType.Int, (byte)channel], U16(Text(expression).Length), U32(value), Text(expression));

        /// <summary>
        /// EvaluationResultHeader with JSON or an error message: WriteEvaluationResult with an OutputBuffer, WriteEvaluationError
        /// </summary>
        public static byte[] ExpressionResult(CodeChannel channel, string expression, string content) =>
            Concat([(byte)DataType.Expression, (byte)channel], U16(Text(expression).Length), U32(Text(content).Length), Text(expression), Text(content));

        /// <summary>
        /// MessageHeader and text: WriteCodeReply
        /// </summary>
        public static byte[] Message(MessageTypeFlags type, string text) => Concat(U32((uint)type), U16(Text(text).Length), U16(0), Text(text));

        /// <summary>
        /// GetFileListHeader and directory: WriteGetFileList
        /// </summary>
        public static byte[] GetFileList(string directory, uint startIndex, uint maxLength) => Concat(U32(startIndex), U32(maxLength), U16(Text(directory).Length), U16(0), Text(directory));

        /// <summary>
        /// OpenFileHeader and file name: WriteOpenFile
        /// </summary>
        public static byte[] OpenFile(string fileName, bool forWriting, bool append, uint preAllocSize) =>
            Concat([(byte)(forWriting ? 1 : 0), (byte)(append ? 1 : 0), (byte)Text(fileName).Length, 0], U32(preAllocSize), Text(fileName));

        /// <summary>
        /// ReadFileHeader: WriteReadFile
        /// </summary>
        public static byte[] ReadFile(uint handle, uint maxLength) => Concat(U32(handle), U32(maxLength));

        /// <summary>
        /// FileHandleHeader and data: WriteFileData
        /// </summary>
        public static byte[] WriteFile(uint handle, string data) => Concat(U32(handle), Text(data));

        /// <summary>
        /// SeekFileHeader: WriteSeekFile
        /// </summary>
        public static byte[] SeekFile(uint handle, uint offset) => Concat(U32(handle), U32(offset));

        /// <summary>
        /// FileHandleHeader: WriteTruncateFile, WriteCloseFile
        /// </summary>
        public static byte[] Handle(uint handle) => U32(handle);

        /// <summary>
        /// FileChunkHeader and file name. RRF no longer sends this request, SbcInterface.cpp only skips the answer (FileChunk_deprecated)
        /// </summary>
        public static byte[] FileChunk(string fileName, uint offset, uint maxLength) => Concat(U32(offset), U32(maxLength), U32(Text(fileName).Length), Text(fileName));
    }

    /// <summary>
    /// Packet data DSF sends, read as the Read functions of DataTransfer read it
    /// </summary>
    internal static class SbcPackets
    {
        private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));

        private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

        private static string Text(byte[] data, int offset, int length) => Encoding.UTF8.GetString(data, offset, length);

        /// <summary>
        /// ReadBoolean: BooleanHeader
        /// </summary>
        public static bool Boolean(byte[] data) => data[0] != 0;

        /// <summary>
        /// ReadGetObjectModel: GetObjectModelHeader, key and flags
        /// </summary>
        public static (string Key, string Flags) GetObjectModel(byte[] data) => (Text(data, 4, U16(data, 0)), Text(data, 4 + U16(data, 0), U16(data, 2)));

        /// <summary>
        /// ReadOpenFileResult: OpenFileResult
        /// </summary>
        public static (uint Handle, uint FileSize) OpenFileResult(byte[] data) => (U32(data, 0), U32(data, 4));

        /// <summary>
        /// ReadFileData: FileDataHeader and data
        /// </summary>
        public static (int BytesRead, string Data) FileReadResult(byte[] data)
        {
            int bytesRead = (int)U32(data, 0);
            return (bytesRead, (bytesRead > 0) ? Text(data, 4, bytesRead) : string.Empty);
        }

        /// <summary>
        /// ReadFileList: FileListHeader and FileListEntry items, each followed by its name padded to the next dword
        /// </summary>
        public static (bool EndOfList, List<(string Name, uint Size, uint LastModified, bool IsDirectory)> Entries) FileList(byte[] data)
        {
            int dataLength = (int)U32(data, 0);
            bool endOfList = data[4] != 0;
            List<(string, uint, uint, bool)> entries = [];
            for (int offset = 8; offset < 8 + dataLength;)
            {
                int nameLength = U16(data, offset + 8);
                entries.Add((Text(data, offset + 12, nameLength), U32(data, offset), U32(data, offset + 4), data[offset + 10] != 0));
                offset += (12 + nameLength + 3) & ~3;
            }
            return (endOfList, entries);
        }

        /// <summary>
        /// FileChunk and data
        /// </summary>
        public static (int DataLength, uint FileLength, string Data) FileChunk(byte[] data)
        {
            int dataLength = (int)U32(data, 0);
            return (dataLength, U32(data, 4), (dataLength > 0) ? Text(data, 8, dataLength) : string.Empty);
        }

        /// <summary>
        /// ReadSetVariable: SetVariableHeader, variable name and expression
        /// </summary>
        public static (CodeChannel Channel, bool Create, string Name, string Expression) SetVariable(byte[] data) =>
            ((CodeChannel)data[0], data[1] != 0, Text(data, 4, data[2]), Text(data, 4 + data[2], data[3]));

        /// <summary>
        /// ReadDeleteLocalVariable: DeleteLocalVariableHeader and variable name
        /// </summary>
        public static (CodeChannel Channel, string Name) DeleteLocalVariable(byte[] data) => ((CodeChannel)data[0], Text(data, 4, data[1]));

        /// <summary>
        /// ReadEvaluateExpression: CodeChannelHeader and the expression up to the end of the packet, which includes the padding of DSF.
        /// StringRef::copy stops at the first NUL (RRFLibraries StringRef.cpp:95-103)
        /// </summary>
        public static (CodeChannel Channel, string Expression) EvaluateExpression(byte[] data)
        {
            int end = Array.IndexOf(data, (byte)0, 4);
            return ((CodeChannel)data[0], Text(data, 4, ((end < 0) ? data.Length : end) - 4));
        }

        /// <summary>
        /// ReadMessage: MessageHeader and text
        /// </summary>
        public static (MessageTypeFlags Type, string Text) Message(byte[] data) => ((MessageTypeFlags)U32(data, 0), Text(data, 8, U16(data, 4)));

        /// <summary>
        /// ReadSetLastCodeResult: SetLastCodeResultHeader
        /// </summary>
        public static (CodeChannel Channel, CodeResult Result) SetLastCodeResult(byte[] data) => ((CodeChannel)data[0], (CodeResult)data[1]);

        /// <summary>
        /// ReadCodeChannel: CodeChannelHeader
        /// </summary>
        public static CodeChannel Channel(byte[] data) => (CodeChannel)data[0];

        /// <summary>
        /// ReadMacroCompleteInfo: MacroCompleteHeader
        /// </summary>
        public static (CodeChannel Channel, bool Error) MacroCompleted(byte[] data) => ((CodeChannel)data[0], data[1] != 0);

        /// <summary>
        /// CodeHeader of a code: channel, letter and major number
        /// </summary>
        public static (CodeChannel Channel, char Letter, int Major) Code(byte[] data) => ((CodeChannel)data[0], (char)data[3], (int)U32(data, 4));

        /// <summary>
        /// The key of ObjectModelKeyChanged as SbcInterface reads it: the packet data from its start, as a C string (SbcInterface.cpp:1300)
        /// </summary>
        public static string KeyAsRrfReadsIt(byte[] data)
        {
            int end = Array.IndexOf(data, (byte)0);
            return Encoding.UTF8.GetString(data, 0, (end < 0) ? data.Length : end);
        }
    }
    #endregion

    #region File requests
    /// <summary>
    /// RRF works on files of the SBC through open, read, write, seek, truncate and close requests (SbcInterface's file functions, DataTransfer::WriteOpenFile
    /// and the following). DSF answers each one, and a request for an unknown handle fails instead of stopping DCS
    /// </summary>
    [Test]
    public async Task FileReadWriteSeekTruncateClose()
    {
        await using Host host = await Host.StartAsync();
        FakeRrfSpi rrf = host.Rrf;
        string path = Path.Combine(host.BaseDirectory, "sys", "rrf.txt");

        // Create a file. Without append and with a pre-allocation size, DSF sets its length (LinkService.HandleOpenFile)
        rrf.SendPacket(FirmwareRequest.OpenFile, RrfPackets.OpenFile("0:/sys/rrf.txt", true, false, 64));
        (uint handle, uint size) = SbcPackets.OpenFileResult(host.WaitFor(SbcRequest.OpenFileResult));
        Assert.That(handle, Is.Not.EqualTo(Consts.NoFileHandle));
        Assert.That(size, Is.EqualTo(64));

        rrf.SendPacket(FirmwareRequest.WriteFile, RrfPackets.WriteFile(handle, "hello world"));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileWriteResult)), Is.True);
        rrf.SendPacket(FirmwareRequest.SeekFile, RrfPackets.SeekFile(handle, 5));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileSeekResult)), Is.True);
        rrf.SendPacket(FirmwareRequest.TruncateFile, RrfPackets.Handle(handle));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileTruncateResult)), Is.True);

        // Close has no answer (LinkService.HandleCloseFile), so wait for the answer to a request sent after it
        rrf.SendPacket(FirmwareRequest.CloseFile, RrfPackets.Handle(handle));
        rrf.SendPacket(FirmwareRequest.CheckFileExists, RrfPackets.String("0:/sys/rrf.txt"));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.CheckFileExistsResult)), Is.True);
        Assert.That(System.IO.File.ReadAllText(path), Is.EqualTo("hello"));

        // Read it back in pieces
        rrf.SendPacket(FirmwareRequest.OpenFile, RrfPackets.OpenFile("0:/sys/rrf.txt", false, false, 0));
        (handle, size) = SbcPackets.OpenFileResult(host.WaitFor(SbcRequest.OpenFileResult));
        Assert.That(size, Is.EqualTo(5));
        rrf.SendPacket(FirmwareRequest.ReadFile, RrfPackets.ReadFile(handle, 3));
        Assert.That(SbcPackets.FileReadResult(host.WaitFor(SbcRequest.FileReadResult)), Is.EqualTo((3, "hel")));
        rrf.SendPacket(FirmwareRequest.ReadFile, RrfPackets.ReadFile(handle, 100));
        Assert.That(SbcPackets.FileReadResult(host.WaitFor(SbcRequest.FileReadResult)), Is.EqualTo((2, "lo")));
        rrf.SendPacket(FirmwareRequest.ReadFile, RrfPackets.ReadFile(handle, 100));
        Assert.That(SbcPackets.FileReadResult(host.WaitFor(SbcRequest.FileReadResult)), Is.EqualTo((0, string.Empty)));
        rrf.SendPacket(FirmwareRequest.CloseFile, RrfPackets.Handle(handle));

        // Append to it
        rrf.SendPacket(FirmwareRequest.OpenFile, RrfPackets.OpenFile("0:/sys/rrf.txt", true, true, 0));
        (handle, size) = SbcPackets.OpenFileResult(host.WaitFor(SbcRequest.OpenFileResult));
        Assert.That(size, Is.EqualTo(5));
        rrf.SendPacket(FirmwareRequest.WriteFile, RrfPackets.WriteFile(handle, "!"));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileWriteResult)), Is.True);
        rrf.SendPacket(FirmwareRequest.CloseFile, RrfPackets.Handle(handle));

        // A handle that is not open fails every request, a read with -1 bytes (FileDataHeader.bytesRead, ReadFileData)
        const uint unknown = 4711;
        rrf.SendPacket(FirmwareRequest.ReadFile, RrfPackets.ReadFile(unknown, 10));
        Assert.That(SbcPackets.FileReadResult(host.WaitFor(SbcRequest.FileReadResult)).BytesRead, Is.EqualTo(-1));
        rrf.SendPacket(FirmwareRequest.WriteFile, RrfPackets.WriteFile(unknown, "x"));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileWriteResult)), Is.False);
        rrf.SendPacket(FirmwareRequest.SeekFile, RrfPackets.SeekFile(unknown, 0));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileSeekResult)), Is.False);
        rrf.SendPacket(FirmwareRequest.TruncateFile, RrfPackets.Handle(unknown));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileTruncateResult)), Is.False);
        rrf.SendPacket(FirmwareRequest.CloseFile, RrfPackets.Handle(unknown));

        // A file that does not exist cannot be opened for reading, which RRF sees from the handle
        rrf.SendPacket(FirmwareRequest.OpenFile, RrfPackets.OpenFile("0:/sys/missing.txt", false, false, 0));
        Assert.That(SbcPackets.OpenFileResult(host.WaitFor(SbcRequest.OpenFileResult)), Is.EqualTo((Consts.NoFileHandle, 0u)));

        // A file in a directory that does not exist yet is created with it
        rrf.SendPacket(FirmwareRequest.OpenFile, RrfPackets.OpenFile("0:/sys/new/dir/file.txt", true, false, 0));
        (handle, _) = SbcPackets.OpenFileResult(host.WaitFor(SbcRequest.OpenFileResult));
        Assert.That(handle, Is.Not.EqualTo(Consts.NoFileHandle));
        rrf.SendPacket(FirmwareRequest.CloseFile, RrfPackets.Handle(handle));
        rrf.SendPacket(FirmwareRequest.CheckFileExists, RrfPackets.String("0:/sys/new/dir/file.txt"));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.CheckFileExistsResult)), Is.True);

        Assert.That(System.IO.File.ReadAllText(path), Is.EqualTo("hello!"));
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// RRF checks for files and deletes files and directories through DSF (DataTransfer::WriteCheckFileExists, WriteDeleteFileOrDirectory,
    /// WriteSecureDeleteFile). A secure delete overwrites the file with zeros first and refuses directories (LinkService.HandleSecureDeleteFile)
    /// </summary>
    [Test]
    public async Task CheckAndDeleteFiles()
    {
        await using Host host = await Host.StartAsync();
        FakeRrfSpi rrf = host.Rrf;
        string sys = Path.Combine(host.BaseDirectory, "sys");
        System.IO.File.WriteAllText(Path.Combine(sys, "a.g"), "G28");
        System.IO.File.WriteAllText(Path.Combine(sys, "secret.txt"), "password");
        Directory.CreateDirectory(Path.Combine(sys, "full", "sub"));
        System.IO.File.WriteAllText(Path.Combine(sys, "full", "sub", "b.g"), "M400");

        rrf.SendPacket(FirmwareRequest.CheckFileExists, RrfPackets.String("0:/sys/a.g"));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.CheckFileExistsResult)), Is.True);
        rrf.SendPacket(FirmwareRequest.CheckFileExists, RrfPackets.String("0:/sys/none.g"));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.CheckFileExistsResult)), Is.False);

        rrf.SendPacket(FirmwareRequest.DeleteFileOrDirectory, RrfPackets.String("0:/sys/a.g"));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileDeleteResult)), Is.True);
        Assert.That(System.IO.File.Exists(Path.Combine(sys, "a.g")), Is.False);

        // A directory that is not empty is deleted only recursively
        rrf.SendPacket(FirmwareRequest.DeleteFileOrDirectory, RrfPackets.String("0:/sys/full"));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileDeleteResult)), Is.False);
        Assert.That(Directory.Exists(Path.Combine(sys, "full")), Is.True);
        rrf.SendPacket(FirmwareRequest.DeleteFileOrDirectoryRecursively, RrfPackets.String("0:/sys/full"));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileDeleteResult)), Is.True);
        Assert.That(Directory.Exists(Path.Combine(sys, "full")), Is.False);

        rrf.SendPacket(FirmwareRequest.SecureDeleteFile, RrfPackets.String("0:/sys/secret.txt"));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileDeleteResult)), Is.True);
        Assert.That(System.IO.File.Exists(Path.Combine(sys, "secret.txt")), Is.False);
        rrf.SendPacket(FirmwareRequest.SecureDeleteFile, RrfPackets.String("0:/sys"));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileDeleteResult)), Is.False);
        Assert.That(Directory.Exists(sys), Is.True);
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// In SBC mode MassStorage::Delete hands every delete to DSF before its check for the root folder (MassStorage.cpp:822-836, 840-858), e.g.
    /// for M472 P"0:/" R1 that RRF runs itself (GCodes2.cpp:3425-3440). DSF refuses to delete the root of a volume as well, so RRF gets false and
    /// reports that the delete failed
    /// </summary>
    [Test]
    public async Task RootIsNotDeleted()
    {
        await using Host host = await Host.StartAsync();
        FakeRrfSpi rrf = host.Rrf;
        string config = Path.Combine(host.BaseDirectory, "sys", "config.g");
        System.IO.File.WriteAllText(config, "M550 P\"test\"");

        foreach (string path in new[] { "0:/", "0:", "/", "0:/sys/..", "0:/.", "0:/sys/../", "0:/./", "./" })
        {
            rrf.SendPacket(FirmwareRequest.DeleteFileOrDirectoryRecursively, RrfPackets.String(path));
            Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileDeleteResult)), Is.False, path);
            Assert.That(System.IO.File.Exists(config), Is.True, path);
        }

        // Without recursion the root is refused the same way, even if it is empty
        foreach (string directory in Directory.GetDirectories(host.BaseDirectory))
        {
            Directory.Delete(directory, true);
        }
        rrf.SendPacket(FirmwareRequest.DeleteFileOrDirectory, RrfPackets.String("0:/"));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.FileDeleteResult)), Is.False);
        Assert.That(Directory.Exists(host.BaseDirectory), Is.True);
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// RRF pages through a directory listing by entry index and tells how much room it has (DataTransfer::WriteGetFileList). DSF sorts the entries
    /// by name so that every page sees the same order, sends as many as fit and tells whether the last one is among them (LinkService.HandleGetFileList)
    /// </summary>
    [Test]
    public async Task GetFileList()
    {
        await using Host host = await Host.StartAsync();
        FakeRrfSpi rrf = host.Rrf;
        string gcodes = Path.Combine(host.BaseDirectory, "gcodes");
        DateTime modified = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        foreach ((string name, string content) in new[] { ("b.gcode", "G1"), ("A.gcode", "G28 X"), ("c.gcode", "") })
        {
            System.IO.File.WriteAllText(Path.Combine(gcodes, name), content);
            System.IO.File.SetLastWriteTimeUtc(Path.Combine(gcodes, name), modified);
        }
        Directory.CreateDirectory(Path.Combine(gcodes, "Sub"));
        uint lastModified = (uint)new DateTimeOffset(modified).ToUnixTimeSeconds();

        rrf.SendPacket(FirmwareRequest.GetFileList, RrfPackets.GetFileList("0:/gcodes", 0, 1024));
        var (endOfList, entries) = SbcPackets.FileList(host.WaitFor(SbcRequest.FileListResult));
        Assert.That(endOfList, Is.True);
        Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "A.gcode", "b.gcode", "c.gcode", "Sub" }));
        Assert.That(entries[0].Size, Is.EqualTo(5));
        Assert.That(entries[0].LastModified, Is.EqualTo(lastModified));
        Assert.That(entries.Select(e => e.IsDirectory), Is.EqualTo(new[] { false, false, false, true }));
        Assert.That(entries[3].Size, Is.Zero);

        // Each entry of a seven-character name takes 12 + 8 bytes, so 45 bytes hold two entries
        rrf.SendPacket(FirmwareRequest.GetFileList, RrfPackets.GetFileList("0:/gcodes", 1, 45));
        (endOfList, entries) = SbcPackets.FileList(host.WaitFor(SbcRequest.FileListResult));
        Assert.That(endOfList, Is.False);
        Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "b.gcode", "c.gcode" }));
        rrf.SendPacket(FirmwareRequest.GetFileList, RrfPackets.GetFileList("0:/gcodes", 3, 45));
        (endOfList, entries) = SbcPackets.FileList(host.WaitFor(SbcRequest.FileListResult));
        Assert.That(endOfList, Is.True);
        Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "Sub" }));

        // A directory that does not exist is an empty list
        rrf.SendPacket(FirmwareRequest.GetFileList, RrfPackets.GetFileList("0:/gcodes/none", 0, 1024));
        (endOfList, entries) = SbcPackets.FileList(host.WaitFor(SbcRequest.FileListResult));
        Assert.That(endOfList, Is.True);
        Assert.That(entries, Is.Empty);
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// The deprecated request for a file chunk (FirmwareRequest.FileChunk, FileChunkHeader) is still answered with the data and the length of
    /// the file (FileChunk). RRF no longer sends it and skips the answer (SbcInterface.cpp:675-678), so only the format is checked
    /// </summary>
    [Test]
    public async Task FileChunk()
    {
        await using Host host = await Host.StartAsync();
        FakeRrfSpi rrf = host.Rrf;
        System.IO.File.WriteAllText(Path.Combine(host.BaseDirectory, "firmware", "board.bin"), "0123456789");
        System.IO.File.WriteAllText(Path.Combine(host.BaseDirectory, "sys", "data.txt"), "abcdef");

        // Binaries are looked up in the firmware directory first (LinkService.HandleFileChunkRequest)
        rrf.SendPacket(FirmwareRequest.FileChunk, RrfPackets.FileChunk("board.bin", 2, 4));
        Assert.That(SbcPackets.FileChunk(host.WaitFor(SbcRequest.FileChunk)), Is.EqualTo((4, 10u, "2345")));
        rrf.SendPacket(FirmwareRequest.FileChunk, RrfPackets.FileChunk("data.txt", 4, 100));
        Assert.That(SbcPackets.FileChunk(host.WaitFor(SbcRequest.FileChunk)), Is.EqualTo((2, 6u, "ef")));

        // A file that cannot be read still gets an answer
        rrf.SendPacket(FirmwareRequest.FileChunk, RrfPackets.FileChunk("missing.bin", 0, 4));
        host.WaitFor(SbcRequest.FileChunk);
        Assert.That(host.Fault, Is.Null);
    }
    #endregion

    #region Requests of DSF
    /// <summary>
    /// Expression evaluations, variable assignments and deletions, code results, messages and object model queries of DSF go out as RRF reads them
    /// (DataTransfer::ReadEvaluateExpression, ReadSetVariable, ReadDeleteLocalVariable, ReadSetLastCodeResult, ReadMessage, ReadGetObjectModel),
    /// and the answers of RRF complete them (WriteEvaluationResult, WriteSetVariableResult, WriteObjectModel)
    /// </summary>
    [Test]
    public async Task RequestsAndResults()
    {
        await using Host host = await Host.StartAsync();
        FakeRrfSpi rrf = host.Rrf;
        LinkInterface link = host.Link;

        Task<object?> evaluation = link.EvaluateExpressionAsync(CodeChannel.SBC, "1+1");
        Assert.That(SbcPackets.EvaluateExpression(host.WaitFor(SbcRequest.EvaluateExpression)), Is.EqualTo((CodeChannel.SBC, "1+1")));
        rrf.SendPacket(FirmwareRequest.EvaluationResult, RrfPackets.IntResult(CodeChannel.SBC, "1+1", 2));
        Assert.That(await evaluation.WaitAsync(Timeout), Is.EqualTo(2));

        // RRF sends JSON for object model values that have no binary type, and an error message when the expression fails (WriteEvaluationError)
        evaluation = link.EvaluateExpressionAsync(CodeChannel.SBC, "move.axes");
        host.WaitFor(SbcRequest.EvaluateExpression);
        rrf.SendPacket(FirmwareRequest.EvaluationResult, RrfPackets.ExpressionResult(CodeChannel.SBC, "move.axes", "[{\"letter\":\"X\",\"homed\":true,\"max\":300.5,\"min\":null,\"steps\":[1,2]}]"));
        object? json = await evaluation.WaitAsync(Timeout);
        Assert.That(json, Is.InstanceOf<object?[]>());
        Dictionary<string, object?> axis = (Dictionary<string, object?>)((object?[])json!)[0]!;
        Assert.That(axis["letter"], Is.EqualTo("X"));
        Assert.That(axis["homed"], Is.EqualTo(true));
        Assert.That(axis["max"], Is.EqualTo(300.5));
        Assert.That(axis["min"], Is.Null);
        Assert.That(axis["steps"], Is.EqualTo(new object?[] { 1.0, 2.0 }));

        evaluation = link.EvaluateExpressionAsync(CodeChannel.SBC, "nonsense");
        host.WaitFor(SbcRequest.EvaluateExpression);
        rrf.SendPacket(FirmwareRequest.EvaluationResult, RrfPackets.ExpressionResult(CodeChannel.SBC, "nonsense", "unknown value 'nonsense'"));
        await Assert.ThrowsAsync<CodeParserException>(async () => await evaluation.WaitAsync(Timeout));

        Task<object?> assignment = link.SetVariableAsync(CodeChannel.SBC, true, "global.fuzz", "4");
        Assert.That(SbcPackets.SetVariable(host.WaitFor(SbcRequest.SetVariable)), Is.EqualTo((CodeChannel.SBC, true, "global.fuzz", "4")));
        rrf.SendPacket(FirmwareRequest.VariableResult, RrfPackets.IntResult(CodeChannel.SBC, "global.fuzz", 4));
        Assert.That(await assignment.WaitAsync(Timeout), Is.EqualTo(4));

        // A deletion has no result, DSF completes it once the request is written (LinkService.Execute)
        await link.SetVariableAsync(CodeChannel.SBC, false, "local", null).WaitAsync(Timeout);
        Assert.That(SbcPackets.DeleteLocalVariable(host.WaitFor(SbcRequest.DeleteLocalVariable)), Is.EqualTo((CodeChannel.SBC, "local")));

        // Results that nothing waits for are dropped
        rrf.SendPacket(FirmwareRequest.EvaluationResult, RrfPackets.IntResult(CodeChannel.SBC, "nobody", 1));
        rrf.SendPacket(FirmwareRequest.VariableResult, RrfPackets.IntResult(CodeChannel.SBC, "global.nobody", 1));

        DuetControlServer.Commands.Code code = host.Get<DuetControlServer.Commands.CommandFactory>().Create<DuetControlServer.Commands.Code>();
        code.Channel = CodeChannel.Trigger;
        code.Result = new Message(MessageType.Warning, "careful");
        await link.SetLastCodeResultAsync(code).WaitAsync(Timeout);
        Assert.That(SbcPackets.SetLastCodeResult(host.WaitFor(SbcRequest.SetLastCodeResult)), Is.EqualTo((CodeChannel.Trigger, CodeResult.Warning)));

        // The format of this packet does not match what RRF reads, see ObjectModelKeyChangedIsReadByRrf, so only check that the key goes out
        link.ObjectModelKeyChanged("volumes");
        Assert.That(Encoding.UTF8.GetString(host.WaitFor(SbcRequest.ObjectModelKeyChanged)), Does.Contain("volumes"));

        link.SendMessage(MessageTypeFlags.UsbMessage | MessageTypeFlags.WarningMessageFlag, "hello RRF");
        Assert.That(SbcPackets.Message(host.WaitFor(SbcRequest.Message)), Is.EqualTo((MessageTypeFlags.UsbMessage | MessageTypeFlags.WarningMessageFlag, "hello RRF")));

        Task<byte[]> query = link.RequestObjectModel("state", "d99vn");
        Assert.That(SbcPackets.GetObjectModel(host.WaitFor(SbcRequest.GetObjectModel)), Is.EqualTo(("state", "d99vn")));
        string response = "{\"key\":\"state\",\"flags\":\"d99vn\",\"result\":{\"status\":\"idle\"}}";
        rrf.SendPacket(FirmwareRequest.ObjectModel, RrfPackets.String(response));
        Assert.That(Encoding.UTF8.GetString(await query.WaitAsync(Timeout)), Is.EqualTo(response));
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// Responses to object model queries are matched by order, so DSF drops one whose key is not the one asked for and keeps waiting
    /// (LinkService.HandleObjectModel). A response without any query is dropped, and an empty one fails the query because RRF could not fit it
    /// </summary>
    [Test]
    public async Task ObjectModelResponses()
    {
        await using Host host = await Host.StartAsync();
        FakeRrfSpi rrf = host.Rrf;

        rrf.SendPacket(FirmwareRequest.ObjectModel, RrfPackets.String("{\"key\":\"stray\",\"flags\":\"\",\"result\":1}"));
        Task<byte[]> query = host.Link.RequestObjectModel("job", "v");
        host.WaitFor(SbcRequest.GetObjectModel);
        rrf.SendPacket(FirmwareRequest.ObjectModel, RrfPackets.String("{\"key\":\"heat\",\"flags\":\"v\",\"result\":{}}"));
        string response = "{\"key\":\"job\",\"flags\":\"v\",\"result\":{}}";
        rrf.SendPacket(FirmwareRequest.ObjectModel, RrfPackets.String(response));
        Assert.That(Encoding.UTF8.GetString(await query.WaitAsync(Timeout)), Is.EqualTo(response));

        query = host.Link.RequestObjectModel("move", "v");
        host.WaitFor(SbcRequest.GetObjectModel);
        rrf.SendPacket(FirmwareRequest.ObjectModel, RrfPackets.String(string.Empty));
        await Assert.ThrowsAsync<ArgumentException>(async () => await query.WaitAsync(Timeout));
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// DSF tells RRF about object model keys that only DSF provides (LinkInterface.ObjectModelKeyChanged, written by
    /// PeriodicUpdateService for "network" and "volumes"). RRF reads the key from the start of the packet data (SbcInterface.cpp:1298-1310),
    /// but DSF writes a StringHeader before it (SPI.WriteObjectModelKeyChanged, Writer.WriteStringRequest), so RRF sees a length instead of the key
    /// </summary>
    [Test, Explicit("DSF writes a StringHeader before the key, RRF reads the key from the start of the packet, so RRF never sees 'network' or 'volumes'")]
    public async Task ObjectModelKeyChangedIsReadByRrf()
    {
        await using Host host = await Host.StartAsync();
        host.Link.ObjectModelKeyChanged("network");
        Assert.That(SbcPackets.KeyAsRrfReadsIt(host.WaitFor(SbcRequest.ObjectModelKeyChanged)), Is.EqualTo("network").IgnoreCase);
    }

    /// <summary>
    /// An emergency stop goes out at once and completes once RRF has carried it out with the next transfer (LinkService.Execute). DSF invalidates
    /// what is pending, and RRF performs the stop as soon as it reads the request (SbcInterface.cpp:336)
    /// </summary>
    [Test]
    public async Task EmergencyStop()
    {
        await using Host host = await Host.StartAsync();
        Task evaluation = host.Link.EvaluateExpressionAsync(CodeChannel.SBC, "state.status");
        host.WaitFor(SbcRequest.EvaluateExpression);
        await host.Link.EmergencyStopAsync().WaitAsync(Timeout);
        host.WaitFor(SbcRequest.EmergencyStop);
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await evaluation.WaitAsync(Timeout));
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// A reset goes out in a transfer of its own, after which the transfer thread ends (LinkService.Execute), because the reset of RRF ends the connection
    /// </summary>
    [Test]
    public async Task ResetEndsTheTransferThread()
    {
        await using Host host = await Host.StartAsync();
        await host.Link.ResetFirmwareAsync().WaitAsync(Timeout);
        host.WaitFor(SbcRequest.Reset);
        await host.Service.ExecuteTask!.WaitAsync(Timeout);
        Assert.That(host.Fault, Is.Null);
    }
    #endregion

    #region Requests of RRF
    /// <summary>
    /// RRF reports the space of its code buffer (DataTransfer::WriteCodeBufferUpdate), and DSF keeps the highest value for the share of each channel
    /// (LinkService.HandleCodeBufferUpdate)
    /// </summary>
    [Test]
    public async Task CodeBufferUpdate()
    {
        await using Host host = await Host.StartAsync();
        host.Rrf.SendPacket(FirmwareRequest.CodeBufferUpdate, RrfPackets.CodeBufferUpdate(3000));
        host.WaitUntil(() => host.Link.MaxReportedBufferSpace == 3000, "the buffer space");
        host.Rrf.SendPacket(FirmwareRequest.CodeBufferUpdate, RrfPackets.CodeBufferUpdate(1000));
        host.WaitUntil(() => host.Link.BufferSpace <= 1000, "the smaller buffer space");
        Assert.That(host.Link.MaxReportedBufferSpace, Is.EqualTo(3000));
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// A paused print is shown in the object model (LinkService.HandlePrintPaused, DataTransfer::WritePrintPaused)
    /// </summary>
    [Test]
    public async Task PrintPaused()
    {
        await using Host host = await Host.StartAsync();
        host.Rrf.SendPacket(FirmwareRequest.PrintPaused, RrfPackets.PrintPaused(1234, Consts.NoFilePosition, PrintPausedReason.User));
        host.WaitUntil(() =>
        {
            using (host.Model.AccessReadOnly())
            {
                return host.Model.State.Status == MachineStatus.Paused;
            }
        }, "the paused state");
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// RRF passes codes from its inputs to DSF when DSF has to perform them (DataTransfer::WriteDoCode). DSF deletes a file for M30 itself
    /// (MCodeHandler, case 30)
    /// </summary>
    [Test]
    public async Task DoCode()
    {
        await using Host host = await Host.StartAsync();
        string file = Path.Combine(host.BaseDirectory, "gcodes", "old.gcode");
        System.IO.File.WriteAllText(file, "G28");
        host.Rrf.SendPacket(FirmwareRequest.DoCode, RrfPackets.DoCode(CodeChannel.Telnet, "M30 \"0:/gcodes/old.gcode\""));
        host.WaitUntil(() => !System.IO.File.Exists(file), "the file to be deleted");
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// A macro that RRF asks for runs in DSF: DSF tells RRF that it started, sends its codes once RRF has reported room in its code buffer,
    /// and reports the end of the macro (DataTransfer::WriteMacroRequest, SbcInterface MacroStarted and MacroCompleted)
    /// </summary>
    [Test]
    public async Task MacroRequest()
    {
        await using Host host = await Host.StartAsync();
        FakeRrfSpi rrf = host.Rrf;
        System.IO.File.WriteAllText(Path.Combine(host.BaseDirectory, "sys", "rrf.g"), "G4 S0\n");
        rrf.SendPacket(FirmwareRequest.CodeBufferUpdate, RrfPackets.CodeBufferUpdate(SbcCodeBufferSize));
        rrf.SendPacket(FirmwareRequest.ExecuteMacro, RrfPackets.ExecuteMacro(CodeChannel.Trigger, false, "rrf.g"));

        Assert.That(SbcPackets.Channel(host.WaitFor(SbcRequest.MacroStarted)), Is.EqualTo(CodeChannel.Trigger));
        Assert.That(SbcPackets.Code(host.WaitFor(SbcRequest.Code)), Is.EqualTo((CodeChannel.Trigger, 'G', 4)));

        // RRF replies to the code with an empty message for its channel (DataTransfer::WriteCodeReply)
        rrf.SendPacket(FirmwareRequest.Message, RrfPackets.Message(MessageTypeFlags.BinaryCodeReplyFlag | MessageTypeFlags.TriggerMessage, string.Empty));
        Assert.That(SbcPackets.MacroCompleted(host.WaitFor(SbcRequest.MacroCompleted)), Is.EqualTo((CodeChannel.Trigger, false)));
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// A macro file that does not exist and one whose path FilePathResolver refuses (it leaves its volume or names an unknown drive) are both
    /// answered with MacroCompleted and the error flag (Processor.DoMacroFile), which makes RRF report "Macro file ... not found"
    /// (SbcInterface.cpp:545-560, GCodeBuffer::RequestMacroFile), and the transfer thread goes on
    /// </summary>
    [TestCase("missing.g")]
    [TestCase("../../escape.g")]
    [TestCase("0:/../escape.g")]
    [TestCase("9:/macros/escape.g")]
    public async Task MacroRequestForAFileThatCannotBeOpened(string fileName)
    {
        await using Host host = await Host.StartAsync();
        FakeRrfSpi rrf = host.Rrf;
        rrf.SendPacket(FirmwareRequest.ExecuteMacro, RrfPackets.ExecuteMacro(CodeChannel.Trigger, false, fileName));
        Assert.That(SbcPackets.MacroCompleted(host.WaitFor(SbcRequest.MacroCompleted)), Is.EqualTo((CodeChannel.Trigger, true)));

        rrf.SendPacket(FirmwareRequest.CheckFileExists, RrfPackets.String("0:/sys/none.g"));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.CheckFileExistsResult)), Is.False);
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// Number of bytes RRF has for buffered codes (SbcMessageFormats.h, SbcCodeBufferSize)
    /// </summary>
    private const int SbcCodeBufferSize = 4096;

    /// <summary>
    /// Requests for a channel RRF does not have are logged and dropped, and the link carries on (LinkService.Handle*: channel &lt; CodeChannel.Unknown)
    /// </summary>
    [Test]
    public async Task RequestsForAnUnknownChannelAreDropped()
    {
        await using Host host = await Host.StartAsync();
        FakeRrfSpi rrf = host.Rrf;
        rrf.SendPacket(FirmwareRequest.ExecuteMacro, RrfPackets.ExecuteMacro(CodeChannel.Unknown, false, "none.g"));
        rrf.SendPacket(FirmwareRequest.AbortFile, RrfPackets.AbortFile(CodeChannel.Unknown, true));
        rrf.SendPacket(FirmwareRequest.Locked, RrfPackets.Channel(CodeChannel.Unknown));
        rrf.SendPacket(FirmwareRequest.DoCode, RrfPackets.DoCode(CodeChannel.Unknown, "M115"));
        rrf.SendPacket(FirmwareRequest.WaitForAcknowledgement, RrfPackets.Channel(CodeChannel.Unknown));
        rrf.SendPacket(FirmwareRequest.MacroFileClosed, RrfPackets.Channel(CodeChannel.Unknown));
        rrf.SendPacket(FirmwareRequest.MessageAcknowledged, RrfPackets.Channel(CodeChannel.Unknown));
        rrf.SendPacket(FirmwareRequest.AbortFile, RrfPackets.AbortFile(CodeChannel.Daemon, false));
        rrf.SendPacket(FirmwareRequest.CheckFileExists, RrfPackets.String("0:/sys/none.g"));
        Assert.That(SbcPackets.Boolean(host.WaitFor(SbcRequest.CheckFileExistsResult)), Is.False);
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// Messages of RRF: a log message in parts (PushFlag) is put together before it is logged, and a generic message goes to the object model
    /// (LinkService.HandleMessage and OutputGenericMessage)
    /// </summary>
    [Test]
    public async Task Messages()
    {
        await using Host host = await Host.StartAsync();
        FakeRrfSpi rrf = host.Rrf;
        rrf.SendPacket(FirmwareRequest.Message, RrfPackets.Message(MessageTypeFlags.LogInfo | MessageTypeFlags.PushFlag, "first "));
        rrf.SendPacket(FirmwareRequest.Message, RrfPackets.Message(MessageTypeFlags.LogInfo | MessageTypeFlags.WarningMessageFlag, "second"));
        rrf.SendPacket(FirmwareRequest.Message, RrfPackets.Message(MessageTypeFlags.GenericMessage | MessageTypeFlags.PushFlag, "generic "));
        rrf.SendPacket(FirmwareRequest.Message, RrfPackets.Message(MessageTypeFlags.GenericMessage | MessageTypeFlags.ErrorMessageFlag, "message"));
        host.WaitUntil(() =>
        {
            using (host.Model.AccessReadOnly())
            {
                return host.Model.Messages.Any(m => m.Content == "generic message" && m.Type == MessageType.Error);
            }
        }, "the generic message");
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// RRF asks for a packet again when it could not take it, in the transfer after the one that carried it (FirmwareRequest.ResendPacket,
    /// PacketHeader.resendPacketId). DSF sends that packet again (LinkService.ProcessPacket, SPI.ResendPacket)
    /// </summary>
    [Test]
    public async Task ResendPacket()
    {
        int resends = 0;
        await using Host host = await Host.StartAsync(respond: (request, id, _) =>
            (request == SbcRequest.Message && Interlocked.Increment(ref resends) == 1) ? [(FirmwareRequest.ResendPacket, [], id)] : []);
        host.Link.SendMessage(MessageTypeFlags.UsbMessage, "twice");
        Assert.That(SbcPackets.Message(host.WaitFor(SbcRequest.Message)).Text, Is.EqualTo("twice"));
        Assert.That(SbcPackets.Message(host.WaitFor(SbcRequest.Message)).Text, Is.EqualTo("twice"));
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// A movement lock is granted once RRF reports it locked (DataTransfer::WriteLocked) and released with Unlock (SbcInterface.cpp:607, 637)
    /// </summary>
    [Test]
    public async Task MovementLock()
    {
        await using Host host = await Host.StartAsync(respond: (request, _, payload) =>
            (request == SbcRequest.LockAllMovementSystemsAndWaitForStandstill) ? [(FirmwareRequest.Locked, RrfPackets.Channel(SbcPackets.Channel(payload)), 0)] : []);
        IAsyncDisposable movementLock = await host.Link.LockAllMovementSystemsAndWaitForStandstill(CodeChannel.SBC).WaitAsync(Timeout);
        Assert.That(SbcPackets.Channel(host.WaitFor(SbcRequest.LockAllMovementSystemsAndWaitForStandstill)), Is.EqualTo(CodeChannel.SBC));
        await movementLock.DisposeAsync().AsTask().WaitAsync(Timeout);
        Assert.That(SbcPackets.Channel(host.WaitFor(SbcRequest.Unlock)), Is.EqualTo(CodeChannel.SBC));
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// The start of a print sends the information about its file, the end its reason (LinkService.Execute, SPI.WritePrintFileInfo and
    /// WritePrintStopped). RRF reads PrintStartedHeader, whose time_t puts the size at offset 16 of the 40-byte header, then the filament
    /// usages, the file name and the slicer (DataTransfer::ReadPrintStartedInfo), and PrintStoppedHeader (ReadPrintStoppedInfo)
    /// </summary>
    [Test]
    public async Task PrintStartedAndStopped()
    {
        await using Host host = await Host.StartAsync();
        using (await host.Model.AccessReadWriteAsync())
        {
            GCodeFileInfo file = host.Model.Job.File;
            file.FileName = "0:/gcodes/print.gcode";
            file.GeneratedBy = "fuzz slicer";
            file.Size = 123456;
            file.NumLayers = 7;
            file.Filament.Add(1.5F);
        }
        await host.Link.SetPrintFileInfo().Unwrap().WaitAsync(Timeout);
        byte[] info = host.WaitFor(SbcRequest.SetPrintFileInfo);
        const int headerSize = 40;
        int fileNameLength = BitConverter.ToUInt16(info, 0), generatedByLength = BitConverter.ToUInt16(info, 2), numFilaments = (int)BitConverter.ToUInt32(info, 4);
        Assert.That(numFilaments, Is.EqualTo(1));
        Assert.That(BitConverter.ToUInt32(info, 16), Is.EqualTo(123456));
        Assert.That(BitConverter.ToUInt32(info, 20), Is.EqualTo(7));
        Assert.That(BitConverter.ToSingle(info, headerSize), Is.EqualTo(1.5F));
        Assert.That(Encoding.UTF8.GetString(info, headerSize + 4, fileNameLength), Is.EqualTo("0:/gcodes/print.gcode"));
        Assert.That(Encoding.UTF8.GetString(info, headerSize + 4 + fileNameLength, generatedByLength), Is.EqualTo("fuzz slicer"));

        await host.Link.StopPrintAsync(PrintStoppedReason.UserCancelled).WaitAsync(Timeout);
        Assert.That(host.WaitFor(SbcRequest.PrintStopped)[0], Is.EqualTo((byte)PrintStoppedReason.UserCancelled));
        Assert.That(host.Fault, Is.Null);
    }

    /// <summary>
    /// A packet whose length runs past the data of the transfer cannot be read. DSF dumps the transfer to sys/transferDump.bin and stops the
    /// transfer thread with the error (LinkService.Execute, SPI.DumpMalformedPacket), so that systemd starts DCS again
    /// </summary>
    [Test]
    public async Task MalformedPacketStopsTheTransferThread()
    {
        await using Host host = await Host.StartAsync();
        host.Rrf.SendPacket(FirmwareRequest.CodeBufferUpdate, RrfPackets.CodeBufferUpdate(100), declaredLength: 1000);
        await host.Service.ExecuteTask!.ContinueWith(_ => { }).WaitAsync(Timeout);
        Assert.That(host.Fault?.InnerException, Is.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(System.IO.File.Exists(Path.Combine(host.BaseDirectory, "sys", "transferDump.bin")), Is.True);
    }
    #endregion

    #region Firmware update and errors of the transfer thread
    /// <summary>
    /// SPI adapter whose firmware update steps after StartIap and whose protocol version and transfers can be scripted. The steps after StartIap
    /// talk to the IAP program instead of RRF and wait for it in real time (SPI.VerifyFirmwareChecksum, WaitForIapReset), which the port does not emulate
    /// </summary>
    private sealed class ScriptedSpi(IServiceProvider services, FakeRrfSpi rrf, IOptions<Settings> settings) :
        SpiAdapter(services.GetRequiredService<EventLogger>(), services.GetRequiredService<DuetControlServer.Model.ObjectModel>(), NullLogger<SpiAdapter>.Instance, settings, rrf, rrf, rrf.Clock), ILinkAdapter
    {
        public Queue<bool> ChecksumResults { get; } = new();

        public MemoryStream Flashed { get; } = new();

        public List<(long Length, ushort Crc16)> Verifications { get; } = [];

        public bool IapReset { get; private set; }

        public int? ReportedProtocolVersion { get; set; }

        public Exception? ThrowInTransfer { get; set; }

        int ILinkAdapter.ProtocolVersion => ReportedProtocolVersion ?? ProtocolVersion;

        bool ILinkAdapter.PerformFullTransfer(bool connecting, CancellationToken cancellationToken)
        {
            if (!connecting && ThrowInTransfer is Exception e)
            {
                throw e;
            }
            return PerformFullTransfer(connecting, cancellationToken);
        }

        bool ILinkAdapter.FlashFirmwareSegment(Stream stream)
        {
            byte[] segment = new byte[Consts.FirmwareSegmentSize];
            int bytesRead = stream.Read(segment);
            if (bytesRead <= 0)
            {
                return false;
            }
            if (stream.Position == bytesRead)
            {
                // Every attempt starts at the beginning of the firmware
                Flashed.SetLength(0);
            }
            Flashed.Write(segment, 0, bytesRead);
            return true;
        }

        bool ILinkAdapter.VerifyFirmwareChecksum(long firmwareLength, ushort crc16)
        {
            Verifications.Add((firmwareLength, crc16));
            return ChecksumResults.Dequeue();
        }

        void ILinkAdapter.WaitForIapReset() => IapReset = true;
    }

    /// <summary>
    /// A firmware update sends the IAP binary to RRF in WriteIap packets of at most Consts.IapSegmentSize bytes and starts it with StartIap
    /// (SbcInterface.cpp:667-672 and 1670-1676), then flashes the firmware through the IAP program and checks its CRC16, up to three times
    /// (LinkService.PerformFirmwareUpdate). The transfer thread ends afterwards, because RRF restarts
    /// </summary>
    /// <param name="failedChecks">Number of checks of the CRC16 that fail before one succeeds</param>
    [TestCase(0), TestCase(2)]
    public async Task FirmwareUpdate(int failedChecks)
    {
        ScriptedSpi? adapter = null;
        await using Host host = await Host.StartAsync(createAdapter: (services, rrf, settings) => adapter = new ScriptedSpi(services, rrf, settings));
        for (int i = 0; i < failedChecks; i++)
        {
            adapter!.ChecksumResults.Enqueue(false);
        }
        adapter!.ChecksumResults.Enqueue(true);

        Random random = new(failedChecks);
        byte[] iap = new byte[2 * Consts.IapSegmentSize + 100], firmware = new byte[2 * Consts.FirmwareSegmentSize + 300];
        random.NextBytes(iap);
        random.NextBytes(firmware);
        await host.Link.UpdateFirmware(new MemoryStream(iap), new MemoryStream(firmware)).WaitAsync(Timeout);

        List<(SbcRequest Request, ushort Id, byte[] Payload)> packets = host.Rrf.GetReceivedPackets();
        List<byte[]> segments = [.. packets.Where(p => p.Request == SbcRequest.WriteIap).Select(p => p.Payload)];
        Assert.That(segments.Select(s => s.Length), Is.EqualTo(new[] { Consts.IapSegmentSize, Consts.IapSegmentSize, 100 }));
        Assert.That(segments.SelectMany(s => s), Is.EqualTo(iap));
        Assert.That(packets.FindIndex(p => p.Request == SbcRequest.StartIap), Is.GreaterThan(packets.FindLastIndex(p => p.Request == SbcRequest.WriteIap)));

        Assert.That(adapter.Flashed.ToArray(), Is.EqualTo(firmware));
        ushort crc16 = DuetControlServer.Utility.CRC16.Calculate(firmware);
        Assert.That(adapter.Verifications, Is.EqualTo(Enumerable.Repeat(((long)firmware.Length, crc16), failedChecks + 1)));
        Assert.That(adapter.IapReset, Is.True);
        await host.Service.ExecuteTask!.WaitAsync(Timeout);
        using (host.Model.AccessReadOnly())
        {
            Assert.That(host.Model.State.Status, Is.EqualTo(MachineStatus.Updating));
        }
    }

    /// <summary>
    /// After three failed checks of the CRC16 the update fails with an IOException for the M997 handler, which reports it, and the IAP program
    /// is not waited for (LinkService.PerformFirmwareUpdate and Execute)
    /// </summary>
    [Test]
    public async Task FirmwareUpdateFailsAfterThreeChecks()
    {
        ScriptedSpi? adapter = null;
        await using Host host = await Host.StartAsync(createAdapter: (services, rrf, settings) => adapter = new ScriptedSpi(services, rrf, settings));
        foreach (bool result in new[] { false, false, false })
        {
            adapter!.ChecksumResults.Enqueue(result);
        }
        await Assert.ThatAsync(() => host.Link.UpdateFirmware(new MemoryStream(new byte[10]), new MemoryStream(new byte[10])).WaitAsync(Timeout), Throws.InstanceOf<IOException>());
        Assert.That(adapter!.Verifications, Has.Count.EqualTo(3));
        Assert.That(adapter.IapReset, Is.False);
    }

    /// <summary>
    /// Firmware of protocol version 1 is not supported, so the transfer thread stops with an error (LinkService.Execute)
    /// </summary>
    [Test]
    public async Task ProtocolVersion1StopsTheTransferThread()
    {
        await using Host host = await Host.StartAsync(createAdapter: (services, rrf, settings) => new ScriptedSpi(services, rrf, settings) { ReportedProtocolVersion = 1 },
            answerCapabilities: false);
        await host.Service.ExecuteTask!.ContinueWith(_ => { }).WaitAsync(Timeout);
        Assert.That(host.Fault?.InnerException?.Message, Is.EqualTo("Unsupported firmware version. Upgrade your firmware manually"));
    }

    private static IEnumerable<TestCaseData> TransferThreadErrors =>
    [
        new TestCaseData(new IOException("broken"), TaskStatus.Faulted).SetName("{m}(IOException)"),
        new TestCaseData(new OperationCanceledException(), TaskStatus.Canceled).SetName("{m}(OperationCanceledException)"),
        new TestCaseData(new AggregateException(new OperationCanceledException()), TaskStatus.Canceled).SetName("{m}(AggregateException of a cancellation)"),
        new TestCaseData(new AggregateException(new IOException("broken")), TaskStatus.Faulted).SetName("{m}(AggregateException of an error)")
    ];

    /// <summary>
    /// The task of the transfer thread reports an error of the thread as a fault and a cancellation that is not a stop of DCS as cancelled, also
    /// when it comes wrapped in an AggregateException (LinkService.ExecuteAsync)
    /// </summary>
    [TestCaseSource(nameof(TransferThreadErrors))]
    public async Task TransferThreadError(Exception error, TaskStatus status)
    {
        ScriptedSpi? adapter = null;
        await using Host host = await Host.StartAsync(createAdapter: (services, rrf, settings) => adapter = new ScriptedSpi(services, rrf, settings));
        adapter!.ThrowInTransfer = error;
        await host.Service.ExecuteTask!.ContinueWith(_ => { }).WaitAsync(Timeout);
        Assert.That(host.Service.ExecuteTask.Status, Is.EqualTo(status));
        if (status == TaskStatus.Faulted)
        {
            Assert.That(host.Fault!.InnerException, Is.InstanceOf<IOException>());
        }
    }
    #endregion
}
