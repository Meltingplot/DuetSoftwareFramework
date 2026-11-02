using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace UnitTests.SPI;

/// <summary>
/// Sequence numbers and response codes of the extended link
/// </summary>
[TestFixture]
public class ExtendedLinkCodes
{
    private static readonly uint[] Codes =
    [
        ExtendedLink.Success, ExtendedLink.BadHeaderChecksum, ExtendedLink.BadDataChecksum, ExtendedLink.BadResponse,
        ExtendedLink.BadFormat, ExtendedLink.BadProtocolVersion, ExtendedLink.BadDataLength
    ];

    private static readonly uint[] RegularCodes =
    [
        TransferResponse.Success, TransferResponse.BadHeaderChecksum, TransferResponse.BadDataChecksum, TransferResponse.BadResponse,
        TransferResponse.BadFormat, TransferResponse.BadProtocolVersion, TransferResponse.BadDataLength
    ];

    /// <summary>
    /// Words that occur where a response may be read: regular codes, idle and underrun patterns, the first word of a header
    /// and the first word of a packet header
    /// </summary>
    private static IEnumerable<uint> OtherWords() =>
        RegularCodes.Concat([TransferResponse.LowPin, TransferResponse.HighPin])
            .Concat(Enumerable.Range(0, 256).Select(numPackets => Consts.FormatCode | ((uint)numPackets << 8) | ((uint)Consts.ProtocolVersion << 16)))
            .Concat(Enumerable.Range(0, 64).SelectMany(request => Enumerable.Range(0, 256).Select(id => (uint)request | ((uint)id << 16))));

    private static int Distance(uint a, uint b) => BitOperations.PopCount(a ^ b);

    [Test]
    public void CodesAreFarApart()
    {
        foreach (uint a in Codes)
        {
            foreach (uint b in Codes.Where(b => b != a))
            {
                Assert.That(Distance(a, b), Is.GreaterThanOrEqualTo(16), $"0x{a:x8} and 0x{b:x8}");
            }
        }
    }

    [Test]
    public void CodesAreFarFromOtherWords()
    {
        foreach (uint code in Codes)
        {
            Assert.That(OtherWords().Min(word => Distance(code, word)), Is.GreaterThanOrEqualTo(10), $"0x{code:x8}");
            Assert.That(Distance(code & 0xFF, Consts.FormatCode), Is.GreaterThan(ExtendedLink.CorrectableBits), $"low byte of 0x{code:x8}");
        }
    }

    [Test]
    public void ErrorsOfUpToThreeBitsAreCorrected()
    {
        for (int i = 0; i < Codes.Length; i++)
        {
            Assert.That(ExtendedLink.EncodeResponse(RegularCodes[i]), Is.EqualTo(Codes[i]));
            for (int a = -1; a < 32; a++)
            {
                for (int b = a + 1; b < 32; b++)
                {
                    for (int c = b + 1; c <= 32; c++)
                    {
                        uint word = Codes[i] ^ ((a >= 0) ? 1u << a : 0) ^ (1u << b) ^ ((c < 32) ? 1u << c : 0);
                        Assert.That(ExtendedLink.DecodeResponse(word), Is.EqualTo(RegularCodes[i]), $"0x{word:x8}");
                    }
                }
            }
        }
    }

    [Test]
    public void OtherWordsAreNotDecoded()
    {
        foreach (uint word in OtherWords())
        {
            Assert.That(ExtendedLink.DecodeResponse(word), Is.EqualTo(word), $"0x{word:x8}");
        }
    }

    [Test]
    public void SequenceNumbers()
    {
        // The first extended number continues the counter of the regular one
        Assert.That(ExtendedLink.Next(0x1234, true), Is.EqualTo(0xFFF5));
        Assert.That(ExtendedLink.Next(0xFFFF, true), Is.EqualTo(0xFFE0));
        Assert.That(ExtendedLink.Next(0xFFFF, false), Is.EqualTo(0));
        Assert.That(ExtendedLink.Previous(0xFFE0), Is.EqualTo(0xFFFF));
        Assert.That(ExtendedLink.Previous(0), Is.EqualTo(0xFFFF));
        Assert.That(ExtendedLink.IsExtended(0xFFE0), Is.True);
        Assert.That(ExtendedLink.IsExtended(0xFFDF), Is.False);

        // Regular numbers count to 65535, extended ones to 31
        Assert.That(ExtendedLink.Distance(0x1234, 0x1235, true), Is.EqualTo(1));
        Assert.That(ExtendedLink.Distance(0x1234, 0x1234, true), Is.EqualTo(0));
        Assert.That(ExtendedLink.Distance(0xFFFF, 1, false), Is.EqualTo(2));
        Assert.That(ExtendedLink.Distance(0x1234, 0xFFF5, true), Is.EqualTo(1), "switch to the extended link");
        Assert.That(ExtendedLink.Distance(0xFFFF, 0xFFE0, true), Is.EqualTo(1), "wrap of the counter");
        Assert.That(ExtendedLink.Distance(0xFFFE, 0xFFE0, true), Is.EqualTo(2));
        Assert.That(ExtendedLink.Distance(0xFFE5, 1, true), Is.EqualTo(-1), "other side left the extended link");
        Assert.That(ExtendedLink.Distance(0xFFE5, 0xFFE6, false), Is.EqualTo(1), "regular numbers before the request");
    }
}
