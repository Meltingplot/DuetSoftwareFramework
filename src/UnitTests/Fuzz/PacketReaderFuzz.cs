using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI;
using DuetControlServer.Link.Protocol;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace UnitTests.Fuzz;

/// <summary>
/// Fuzz tests for the packet reader. The link adapters pass each packet limited to its length, so a malformed packet
/// must fail with an <see cref="ArgumentOutOfRangeException"/>, which is what LinkService reports with a dump of the packet
/// </summary>
[TestFixture]
public class PacketReaderFuzz
{
    /// <summary>
    /// Reader functions and their seeds
    /// </summary>
    private static readonly Dictionary<string, (Action<byte[]> read, Func<IEnumerable<byte[]>> seeds)> Targets = new()
    {
        ["PacketHeader"] = (data => Reader.ReadPacketHeader(data, out _), () => [Blob("packetHeader.bin"), Blob("packetHeaderResend.bin")]),
        ["CodeBufferUpdate"] = (data => Reader.ReadCodeBufferUpdate(data, out _), () => [Blob("codeBufferUpdate.bin")]),
        ["Message"] = (data => Reader.ReadMessage(data, out _, out _), () => [Blob("message.bin"), Blob("emptyMessage.bin")]),
        ["MacroRequest"] = (data => Reader.ReadMacroRequest(data, out _, out _, out _), () => [Blob("macroRequest.bin")]),
        ["AbortFile"] = (data => Reader.ReadAbortFile(data, out _, out _), () => [Blob("abortFile.bin")]),
        ["LegacyPrintPaused"] = (data => Reader.ReadLegacyPrintPaused(data, out _, out _), () => [Blob("printPaused.bin")]),
        ["PrintPaused"] = (data => Reader.ReadPrintPaused(data, out _, out _, out _), () => [Blob("printPaused.bin"), Packet(new PrintPausedHeader { FilePosition = 1234, FilePosition2 = 5678, PauseReason = 1 })]),
        ["CodeChannel"] = (data => Reader.ReadCodeChannel(data, out _), () => [Blob("codeChannel.bin")]),
        ["FileChunkRequest"] = (data => Reader.ReadFileChunkRequest(data, out _, out _, out _), () => [Blob("fileChunk.bin")]),
        ["EvaluationResult"] = (data => Reader.ReadEvaluationResult(data, out _, out _, out _), () => [Blob("evaluationResult.bin"), .. EvaluationResults()]),
        ["DoCode"] = (data => Reader.ReadDoCode(data, out _, out _), () => [Blob("doCode.bin")]),
        ["StringRequest"] = (data => Reader.ReadStringRequest(data, out string _), () => [Blob("stringRequest.bin")]),
        ["GetFileList"] = (data => Reader.ReadGetFileList(data, out _, out _, out _), () => [Packet(new GetFileListHeader { StartIndex = 2, MaxLength = 1024, DirectoryLength = 9 }, "0:/macros")]),
        ["OpenFile"] = (data => Reader.ReadOpenFile(data, out _, out _, out _, out _), () => [Packet(new OpenFileHeader { ForWriting = 1, Append = 0, FilenameLength = 13, PreAllocSize = 4096 }, "0:/sys/test.g")]),
        ["SeekFile"] = (data => Reader.ReadSeekFile(data, out _, out _), () => [Packet(new SeekFileHeader { Handle = 1, Offset = 512 })]),
        ["FileRequest"] = (data => Reader.ReadFileRequest(data, out _, out _), () => [Packet(new ReadFileHeader { Handle = 1, MaxLength = 256 })]),
        ["FileHandle"] = (data => Reader.ReadFileHandle(data, out _), () => [Packet(new FileHandleHeader { Handle = 1 })])
    };

    private static IEnumerable<TestCaseData> TargetNames => Targets.Keys.Select(target => (target == "EvaluationResult")
        ? new TestCaseData(target).Explicit("Arrays are allocated with the count from the packet before it is checked, and a malformed DateTime throws a FormatException")
        : new TestCaseData(target));

    [OneTimeSetUp]
    public void CheckHeapLimit()
    {
        // A mutated count field can make the reader allocate gigabytes, which must fail inside the test host
        // instead of exhausting the machine. runtimeconfig.template.json sets the limit
        Assert.That(GC.GetConfigurationVariables().TryGetValue("GCHeapHardLimit", out object? limit) && Convert.ToUInt64(limit) is > 0 and <= 2UL << 30,
                    "GC heap hard limit is not set, not fuzzing the packet reader");
    }

    [TestCaseSource(nameof(TargetNames))]
    public void Read(string target)
    {
        (Action<byte[]> read, Func<IEnumerable<byte[]>> seeds) = Targets[target];
        Fuzzer.Run([.. seeds()], input =>
        {
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                read(input);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Expected for packets whose content does not fit their length
            }

            // Each value is decoded from the packet, so the memory needed is bounded by its length
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            if (allocated > 64 * input.Length + 65536)
            {
                throw new FuzzFailure("allocation", $"Reading {input.Length} bytes allocated {allocated} bytes");
            }
        }, binary: true);
    }

    /// <summary>
    /// Evaluation results of each data type, as RepRapFirmware's SbcInterface encodes them
    /// </summary>
    private static IEnumerable<byte[]> EvaluationResults()
    {
        const string expression = "move.axes[0].position";
        foreach (DataType type in Enum.GetValues<DataType>())
        {
            EvaluationResultHeader header = new()
            {
                Type = type,
                Channel = CodeChannel.SBC,
                ExpressionLength = (ushort)expression.Length
            };

            List<byte> payload = [];
            switch (type)
            {
                case DataType.IntArray or DataType.UIntArray or DataType.FloatArray or DataType.DriverIdArray:
                    header.IntValue = 3;
                    payload.AddRange(Enumerable.Range(1, 3).SelectMany(i => BitConverter.GetBytes(i)));
                    break;
                case DataType.BoolArray:
                    header.IntValue = 3;
                    payload.AddRange([1, 0, 1]);
                    break;
                case DataType.String:
                    header.IntValue = 4;
                    payload.AddRange("text"u8.ToArray());
                    break;
                case DataType.Expression:
                    byte[] json = "{\"a\":[1,\"b\",true,null,{\"c\":false}]}"u8.ToArray();
                    header.IntValue = json.Length;
                    payload.AddRange(json);
                    break;
                case DataType.DateTime:
                    header.IntValue = 19;
                    payload.AddRange("2026-11-01T12:34:56"u8.ToArray());
                    break;
                case DataType.ULong or DataType.Bitmap64:
                    // The value follows the expression on the next 4-byte boundary
                    payload.AddRange(new byte[(4 - expression.Length % 4) % 4]);
                    payload.AddRange(BitConverter.GetBytes(0x123456789ul));
                    break;
                case DataType.FloatWithDigits:
                    header.FloatValue = 1.2345f;
                    break;
                default:
                    header.IntValue = 42;
                    break;
            }

            byte[] data = Packet(header, (type == DataType.FloatWithDigits) ? new byte[] { 2 } : null);
            yield return [.. data, .. Encoding.UTF8.GetBytes(expression), .. payload];
        }
    }

    /// <summary>
    /// Encode a header followed by its content
    /// </summary>
    private static byte[] Packet<T>(T header, string content) where T : struct => Packet(header, Encoding.UTF8.GetBytes(content));

    /// <summary>
    /// Encode a header followed by its content
    /// </summary>
    private static byte[] Packet<T>(T header, byte[]? content = null) where T : struct
    {
        byte[] data = new byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(data, in header);
        return [.. data, .. content ?? []];
    }

    /// <summary>
    /// Read a captured packet
    /// </summary>
    private static byte[] Blob(string filename) => System.IO.File.ReadAllBytes(Path.Combine(Directory.GetCurrentDirectory(), "../../../SPI/Blobs", filename));
}
