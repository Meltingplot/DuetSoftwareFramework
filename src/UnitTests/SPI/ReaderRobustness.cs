using System;
using System.Runtime.InteropServices;
using System.Text;
using DuetControlServer.Link.Protocol;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace UnitTests.SPI;

/// <summary>
/// Tests for firmware data that the packet reader must handle without crashing or mismatching responses
/// </summary>
[TestFixture]
public class ReaderRobustness
{
    [Test]
    public void EvaluationResultWithDigits()
    {
        // Ordinary values are rounded to the requested number of decimals
        Assert.That(ReadFloatWithDigits(1.2345f, 2), Is.EqualTo(1.23m));

        // RepRapFirmware may report values that cannot be represented as a decimal
        Assert.That(ReadFloatWithDigits(float.NaN, 2), Is.NaN);
        Assert.That(ReadFloatWithDigits(float.PositiveInfinity, 2), Is.EqualTo(float.PositiveInfinity));
        Assert.That(ReadFloatWithDigits(float.NegativeInfinity, 2), Is.EqualTo(float.NegativeInfinity));
        Assert.That(ReadFloatWithDigits(float.MaxValue, 2), Is.EqualTo(float.MaxValue));

        // ... and more decimals than a decimal can hold
        Assert.That(ReadFloatWithDigits(1.2345f, 255), Is.EqualTo(1.2345m));
    }

    private static object? ReadFloatWithDigits(float value, byte numDigits)
    {
        const string expression = "move.axes[0].position";
        int headerSize = Marshal.SizeOf<EvaluationResultHeader>();

        EvaluationResultHeader header = new()
        {
            Type = DataType.FloatWithDigits,
            Channel = DuetAPI.CodeChannel.HTTP,
            ExpressionLength = (ushort)Encoding.UTF8.GetByteCount(expression),
            FloatValue = value
        };

        Span<byte> blob = new byte[headerSize + 1 + header.ExpressionLength];
        MemoryMarshal.Write(blob, in header);
        blob[headerSize] = numDigits;
        Encoding.UTF8.GetBytes(expression, blob[(headerSize + 1)..]);

        Reader.ReadEvaluationResult(blob, out _, out _, out object? result);
        return result;
    }

    [TestCase(DataType.IntArray, sizeof(int))]
    [TestCase(DataType.UIntArray, sizeof(uint))]
    [TestCase(DataType.FloatArray, sizeof(float))]
    [TestCase(DataType.DriverIdArray, sizeof(uint))]
    [TestCase(DataType.BoolArray, sizeof(byte))]
    public void EvaluationResultArray(DataType type, int itemSize)
    {
        // Arrays that fit the packet are read as before
        Assert.That(ReadEvaluationResult(type, 3, new byte[3 * itemSize]), Is.InstanceOf<Array>().With.Length.EqualTo(3));

        // RepRapFirmware does not send arrays (DataTransfer::WriteEvaluationResult), but a malformed count must be reported
        // like any other malformed packet and must not allocate more than the packet can hold
        foreach (int count in new[] { 4, -1, 0x40000000, int.MaxValue })
        {
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<ArgumentOutOfRangeException>(() => ReadEvaluationResult(type, count, new byte[3 * itemSize]), $"count {count}");
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore, Is.LessThan(65536), $"count {count}");
        }
    }

    [Test]
    public void EvaluationResultDateTime()
    {
        // RepRapFirmware formats dates as yyyy-MM-ddTHH:mm:ss (ExpressionValue::AppendAsString in ObjectModel.cpp)
        Assert.That(ReadEvaluationResult(DataType.DateTime, "2026-11-01T12:34:56"), Is.EqualTo(new DateTime(2026, 11, 1, 12, 34, 56)));

        // A value that is not a date is a malformed packet
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadEvaluationResult(DataType.DateTime, "2026-11-01T\u00002:34:56"));
    }

    private static object? ReadEvaluationResult(DataType type, string value) => ReadEvaluationResult(type, Encoding.UTF8.GetByteCount(value), Encoding.UTF8.GetBytes(value));

    private static object? ReadEvaluationResult(DataType type, int intValue, byte[] payload)
    {
        const string expression = "state.time";
        int headerSize = Marshal.SizeOf<EvaluationResultHeader>();

        EvaluationResultHeader header = new()
        {
            Type = type,
            Channel = DuetAPI.CodeChannel.HTTP,
            ExpressionLength = (ushort)Encoding.UTF8.GetByteCount(expression),
            IntValue = intValue
        };

        byte[] blob = new byte[headerSize + header.ExpressionLength + payload.Length];
        MemoryMarshal.Write(blob, in header);
        Encoding.UTF8.GetBytes(expression, blob.AsSpan(headerSize));
        payload.CopyTo(blob.AsSpan(headerSize + header.ExpressionLength));

        Reader.ReadEvaluationResult(blob, out _, out _, out object? result);
        return result;
    }

    [Test]
    public void ObjectModelKey()
    {
        Assert.That(Reader.TryReadObjectModelKey("{\"key\":\"boards\",\"flags\":\"d99\",\"result\":[{\"key\":\"nested\"}]}"u8, out string key), Is.True);
        Assert.That(key, Is.EqualTo("boards"));

        Assert.That(Reader.TryReadObjectModelKey("{\"key\":\"\",\"flags\":\"d99f\",\"result\":{\"key\":\"nested\"}}"u8, out key), Is.True);
        Assert.That(key, Is.EqualTo(string.Empty));

        Assert.That(Reader.TryReadObjectModelKey("{\"flags\":{\"key\":\"nested\"},\"key\":\"move.axes\",\"next\":9}"u8, out key), Is.True);
        Assert.That(key, Is.EqualTo("move.axes"));

        // No key or malformed content must not be treated as a mismatch
        Assert.That(Reader.TryReadObjectModelKey("{\"result\":null}"u8, out _), Is.False);
        Assert.That(Reader.TryReadObjectModelKey("{\"key\":42}"u8, out _), Is.False);
        Assert.That(Reader.TryReadObjectModelKey("{\"key\":\"limits"u8, out _), Is.False);
        Assert.That(Reader.TryReadObjectModelKey(""u8, out _), Is.False);
    }
}
