using System;
using System.Buffers.Binary;
using System.Text;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Utility;
using DuetControlServer.Link.Protocol;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using CodeFlags = DuetAPI.Commands.CodeFlags;

namespace UnitTests.SPI;

/// <summary>
/// Codes in the binary format of the SPI link, read the way RRF reads them: CodeHeader, one CodeParameter per parameter and then the array
/// values and strings in the order of the parameters, strings padded to a dword (SbcMessageFormats.h; BinaryParser::Seen and GetArray in
/// src/GCodes/GCodeBuffer/BinaryParser.cpp)
/// </summary>
[TestFixture]
public class BinaryCodeLayout
{
    /// <summary>
    /// Size of CodeHeader with the line number, and of CodeParameter (SbcMessageFormats.h)
    /// </summary>
    private const int CodeHeaderSize = 20, CodeParameterSize = 8;

    /// <summary>
    /// Values of DataType in SbcMessageFormats.h
    /// </summary>
    private const byte Int = 0, UInt = 1, Float = 2, IntArray = 3, UIntArray = 4, FloatArray = 5, String = 6, Expression = 7, DriverId = 8, DriverIdArray = 9, Null = 14;

    /// <summary>
    /// Values of CodeFlags in SbcMessageFormats.h
    /// </summary>
    private const byte HasMajorCommandNumber = 1, HasMinorCommandNumber = 2, HasFilePosition = 4, EnforceAbsolutePosition = 8, HasExplicitLineNumber = 16;

    private static int I32(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));

    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

    private static float F32(byte[] data, int offset) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset));

    /// <summary>
    /// Every parameter type DSF can send, with every flag of the header
    /// </summary>
    [Test]
    public void EveryParameterType()
    {
        Code code = new()
        {
            Channel = CodeChannel.File,
            Type = CodeType.MCode,
            MajorNumber = 950,
            MinorNumber = 2,
            LineNumber = 42,
            FilePosition = 1000,
            Flags = CodeFlags.EnforceAbsolutePosition | CodeFlags.HasExplicitLineNumber
        };
        code.Parameters.AddRange(
        [
            new CodeParameter('A', 5),
            new CodeParameter('B', 7u),
            new CodeParameter('C', new DriverId(1, 2)),
            new CodeParameter('D', 1.5F),
            new CodeParameter('E', new[] { 1, -2 }),
            new CodeParameter('F', new uint[] { 3, 4 }),
            new CodeParameter('G', new[] { new DriverId(0, 1), new DriverId(2, 3) }),
            new CodeParameter('H', new[] { 0.5F, 2.5F }),
            new CodeParameter('I', "ab"),
            new CodeParameter('J', "{1+1}"),
            new CodeParameter('K', null)
        ]);

        byte[] data = new byte[Consts.BufferSize];
        int length = Writer.WriteCode(data, code, Consts.ProtocolVersion);

        // CodeHeader
        Assert.That(data[0], Is.EqualTo((byte)CodeChannel.File));
        Assert.That(data[1], Is.EqualTo(HasMajorCommandNumber | HasMinorCommandNumber | HasFilePosition | EnforceAbsolutePosition | HasExplicitLineNumber));
        Assert.That(data[2], Is.EqualTo(11));
        Assert.That((char)data[3], Is.EqualTo('M'));
        Assert.That((I32(data, 4), I32(data, 8), U32(data, 12), I32(data, 16)), Is.EqualTo((950, 2, 1000u, 42)));

        // CodeParameter: letter, type and the value or the number of array items or bytes
        (char Letter, byte Type, uint Value)[] expected =
        [
            ('A', Int, 5), ('B', UInt, 7), ('C', DriverId, (1u << 16) | 2), ('D', Float, BitConverter.SingleToUInt32Bits(1.5F)), ('E', IntArray, 2), ('F', UIntArray, 2),
            ('G', DriverIdArray, 2), ('H', FloatArray, 2), ('I', String, 2), ('J', Expression, 5), ('K', Null, 0)
        ];
        for (int i = 0; i < expected.Length; i++)
        {
            int offset = CodeHeaderSize + i * CodeParameterSize;
            Assert.That(((char)data[offset], data[offset + 1], U32(data, offset + 4)), Is.EqualTo(expected[i]), $"parameter {i}");
        }

        // Array values and strings follow in the order of the parameters
        int extra = CodeHeaderSize + expected.Length * CodeParameterSize;
        Assert.That((I32(data, extra), I32(data, extra + 4)), Is.EqualTo((1, -2)));
        Assert.That((U32(data, extra + 8), U32(data, extra + 12)), Is.EqualTo((3u, 4u)));
        Assert.That((U32(data, extra + 16), U32(data, extra + 20)), Is.EqualTo((1u, (2u << 16) | 3)));
        Assert.That((F32(data, extra + 24), F32(data, extra + 28)), Is.EqualTo((0.5F, 2.5F)));
        Assert.That(Encoding.UTF8.GetString(data, extra + 32, 2), Is.EqualTo("ab"));
        Assert.That(Encoding.UTF8.GetString(data, extra + 36, 5), Is.EqualTo("{1+1}"));
        Assert.That(length, Is.EqualTo(extra + 36 + 8));
    }

    /// <summary>
    /// RRF has no binary type for other values, so DSF refuses them
    /// </summary>
    [Test]
    public void UnsupportedParameterType()
    {
        Code code = new() { Type = CodeType.GCode, MajorNumber = 1 };
        code.Parameters.Add(new CodeParameter('X', 1L));
        Assert.Throws<ArgumentException>(() => Writer.WriteCode(new byte[Consts.BufferSize], code, Consts.ProtocolVersion));
    }

    /// <summary>
    /// A code that does not fit the buffer is refused instead of being cut off, and its size is still counted
    /// </summary>
    [Test]
    public void CodeTooLong()
    {
        Code code = new() { Type = CodeType.MCode, MajorNumber = 117 };
        code.Parameters.Add(new CodeParameter('S', new string('x', 600)));
        Assert.Throws<ArgumentException>(() => Writer.WriteCode(new byte[384], code, Consts.ProtocolVersion));
    }

    /// <summary>
    /// A comment is sent as a string parameter '@' of at most Consts.MaxCommentLength bytes, cut on a UTF-8 character boundary (Writer.WriteCode)
    /// </summary>
    [Test]
    public void LongCommentIsCutOnACharacterBoundary()
    {
        // 99 ASCII characters and an ä of two bytes make 101 bytes, and byte 100 is the middle of the ä
        Code code = new() { Type = CodeType.Comment, Comment = new string('x', Consts.MaxCommentLength - 1) + "ä" };
        byte[] data = new byte[Consts.BufferSize];
        int length = Writer.WriteCode(data, code, Consts.ProtocolVersion);
        Assert.That(((char)data[CodeHeaderSize], data[CodeHeaderSize + 1], I32(data, CodeHeaderSize + 4)), Is.EqualTo(('@', String, Consts.MaxCommentLength - 1)));
        Assert.That(Encoding.UTF8.GetString(data, CodeHeaderSize + CodeParameterSize, Consts.MaxCommentLength - 1), Is.EqualTo(new string('x', Consts.MaxCommentLength - 1)));
        Assert.That(length, Is.EqualTo(CodeHeaderSize + CodeParameterSize + 100));
    }

    /// <summary>
    /// Names and expressions longer than the fields of RRF are refused (MaxSbcExpressionLength, SetVariableHeader and DeleteLocalVariableHeader in
    /// SbcMessageFormats.h)
    /// </summary>
    [Test]
    public void TooLongForRrf()
    {
        byte[] data = new byte[Consts.BufferSize];
        string longName = new('v', Consts.MaxVariableLength + 1), longExpression = new('1', Consts.MaxExpressionLength + 1);
        Assert.Throws<ArgumentException>(() => Writer.WriteEvaluateExpression(data, CodeChannel.SBC, longExpression));
        Assert.Throws<ArgumentException>(() => Writer.WriteSetVariable(data, CodeChannel.SBC, true, longName, "1"));
        Assert.Throws<ArgumentException>(() => Writer.WriteSetVariable(data, CodeChannel.SBC, true, "global.x", new string('1', Consts.MaxExpressionLength)));
        Assert.Throws<ArgumentException>(() => Writer.WriteDeleteLocalVariable(data, CodeChannel.SBC, longName));
        Assert.Throws<ArgumentException>(() => Writer.WriteAssignFilament(data, 0, new string('f', 33)));
    }
}
