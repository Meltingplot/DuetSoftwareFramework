using System;
using System.Runtime.InteropServices;
using System.Text;
using DuetControlServer.Link.Protocol;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace UnitTests.SPI;

/// <summary>
/// Tests for firmware data that the packet reader must handle without crashing
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
}
