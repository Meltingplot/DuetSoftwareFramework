using System;

namespace DuetAPI.Commands;

/// <summary>
/// Type of the checksum block at the end of a numbered line
/// </summary>
public enum LineChecksumType
{
    /// <summary>
    /// No checksum block
    /// </summary>
    None,

    /// <summary>
    /// Legacy XOR checksum with 1 to 3 decimal digits
    /// </summary>
    Checksum,

    /// <summary>
    /// CRC-16/XMODEM with exactly 5 decimal digits
    /// </summary>
    Crc
}

public partial class Code
{
    /// <summary>
    /// Storage for a line that starts with a line number (N...)
    /// </summary>
    /// <remarks>
    /// Like RepRapFirmware, the asynchronous parser reads such lines as a whole so that a trailing checksum or CRC block can be
    /// verified before any code of the line is returned. Only the checksum block is removed from the line, so the
    /// stored bytes still map 1:1 to the original stream positions
    /// </remarks>
    internal sealed class NumberedLine
    {
        /// <summary>
        /// Bytes of the line following the leading 'N' character, including the trailing NL (if any)
        /// </summary>
        public byte[] Content = new byte[128];

        /// <summary>
        /// Number of valid bytes in <see cref="Content"/> after the checksum block has been removed
        /// </summary>
        public int Length;

        /// <summary>
        /// Index of the next byte to read
        /// </summary>
        public int Pointer;

        /// <summary>
        /// Stream position of the first byte in <see cref="Content"/>
        /// </summary>
        public long StartPosition;

        /// <summary>
        /// Number of bytes that were removed from the end of the line. This is added to the length of the last code on the line
        /// </summary>
        public int StrippedBytes;

        /// <summary>
        /// Type of the verified checksum block
        /// </summary>
        public LineChecksumType ChecksumType;

        /// <summary>
        /// Whether the line has any content between the line number and the checksum block or comment
        /// </summary>
        public bool HasContent;

        /// <summary>
        /// Whether there are unread bytes left
        /// </summary>
        public bool HasData => Pointer < Length;

        /// <summary>
        /// Clear the line
        /// </summary>
        public void Reset()
        {
            Length = Pointer = StrippedBytes = 0;
            ChecksumType = LineChecksumType.None;
            HasContent = false;
        }

        /// <summary>
        /// Append a byte to the line
        /// </summary>
        /// <param name="b">Byte to append</param>
        public void Append(byte b)
        {
            if (Length == Content.Length)
            {
                Array.Resize(ref Content, Content.Length * 2);
            }
            Content[Length++] = b;
        }

        /// <summary>
        /// Verify the checksum or CRC block of the line (if present) and remove it from the line
        /// </summary>
        /// <exception cref="CodeParserException">Checksum or CRC is malformed or does not match</exception>
        public void Verify()
        {
            bool hasNewLine = Length > 0 && Content[Length - 1] == '\n';
            int contentLength = hasNewLine ? Length - 1 : Length;

            int stripped;
            try
            {
                stripped = VerifyLineChecksum(Content, contentLength, out ChecksumType, out HasContent);
            }
            catch
            {
                Reset();
                throw;
            }

            if (stripped > 0)
            {
                if (hasNewLine)
                {
                    Content[contentLength - stripped] = (byte)'\n';
                }
                Length -= stripped;
                StrippedBytes = stripped;
            }
        }

        /// <summary>
        /// Get the line number of this line
        /// </summary>
        /// <returns>Line number as it appears in the line</returns>
        public string GetLineNumber() => ReadLineNumber(Content, Length);

        /// <summary>
        /// Read the next byte
        /// </summary>
        /// <param name="code">Code being read. The removed checksum block is added to its length once the line is exhausted</param>
        /// <returns>Next byte</returns>
        public byte Read(Code code)
        {
            byte b = Content[Pointer++];
            if (Pointer == Length)
            {
                code.Length += StrippedBytes;
                StrippedBytes = 0;
            }
            return b;
        }
    }

    /// <summary>
    /// Lookup table for CRC-16/XMODEM (polynomial 0x1021, initial value 0, no reflection). This is the CRC
    /// RepRapFirmware uses for CRC-protected lines of G-code
    /// </summary>
    private static readonly ushort[] Crc16Table = [
        0x0000, 0x1021, 0x2042, 0x3063, 0x4084, 0x50A5, 0x60C6, 0x70E7,
        0x8108, 0x9129, 0xA14A, 0xB16B, 0xC18C, 0xD1AD, 0xE1CE, 0xF1EF,
        0x1231, 0x0210, 0x3273, 0x2252, 0x52B5, 0x4294, 0x72F7, 0x62D6,
        0x9339, 0x8318, 0xB37B, 0xA35A, 0xD3BD, 0xC39C, 0xF3FF, 0xE3DE,
        0x2462, 0x3443, 0x0420, 0x1401, 0x64E6, 0x74C7, 0x44A4, 0x5485,
        0xA56A, 0xB54B, 0x8528, 0x9509, 0xE5EE, 0xF5CF, 0xC5AC, 0xD58D,
        0x3653, 0x2672, 0x1611, 0x0630, 0x76D7, 0x66F6, 0x5695, 0x46B4,
        0xB75B, 0xA77A, 0x9719, 0x8738, 0xF7DF, 0xE7FE, 0xD79D, 0xC7BC,
        0x48C4, 0x58E5, 0x6886, 0x78A7, 0x0840, 0x1861, 0x2802, 0x3823,
        0xC9CC, 0xD9ED, 0xE98E, 0xF9AF, 0x8948, 0x9969, 0xA90A, 0xB92B,
        0x5AF5, 0x4AD4, 0x7AB7, 0x6A96, 0x1A71, 0x0A50, 0x3A33, 0x2A12,
        0xDBFD, 0xCBDC, 0xFBBF, 0xEB9E, 0x9B79, 0x8B58, 0xBB3B, 0xAB1A,
        0x6CA6, 0x7C87, 0x4CE4, 0x5CC5, 0x2C22, 0x3C03, 0x0C60, 0x1C41,
        0xEDAE, 0xFD8F, 0xCDEC, 0xDDCD, 0xAD2A, 0xBD0B, 0x8D68, 0x9D49,
        0x7E97, 0x6EB6, 0x5ED5, 0x4EF4, 0x3E13, 0x2E32, 0x1E51, 0x0E70,
        0xFF9F, 0xEFBE, 0xDFDD, 0xCFFC, 0xBF1B, 0xAF3A, 0x9F59, 0x8F78,
        0x9188, 0x81A9, 0xB1CA, 0xA1EB, 0xD10C, 0xC12D, 0xF14E, 0xE16F,
        0x1080, 0x00A1, 0x30C2, 0x20E3, 0x5004, 0x4025, 0x7046, 0x6067,
        0x83B9, 0x9398, 0xA3FB, 0xB3DA, 0xC33D, 0xD31C, 0xE37F, 0xF35E,
        0x02B1, 0x1290, 0x22F3, 0x32D2, 0x4235, 0x5214, 0x6277, 0x7256,
        0xB5EA, 0xA5CB, 0x95A8, 0x8589, 0xF56E, 0xE54F, 0xD52C, 0xC50D,
        0x34E2, 0x24C3, 0x14A0, 0x0481, 0x7466, 0x6447, 0x5424, 0x4405,
        0xA7DB, 0xB7FA, 0x8799, 0x97B8, 0xE75F, 0xF77E, 0xC71D, 0xD73C,
        0x26D3, 0x36F2, 0x0691, 0x16B0, 0x6657, 0x7676, 0x4615, 0x5634,
        0xD94C, 0xC96D, 0xF90E, 0xE92F, 0x99C8, 0x89E9, 0xB98A, 0xA9AB,
        0x5844, 0x4865, 0x7806, 0x6827, 0x18C0, 0x08E1, 0x3882, 0x28A3,
        0xCB7D, 0xDB5C, 0xEB3F, 0xFB1E, 0x8BF9, 0x9BD8, 0xABBB, 0xBB9A,
        0x4A75, 0x5A54, 0x6A37, 0x7A16, 0x0AF1, 0x1AD0, 0x2AB3, 0x3A92,
        0xFD2E, 0xED0F, 0xDD6C, 0xCD4D, 0xBDAA, 0xAD8B, 0x9DE8, 0x8DC9,
        0x7C26, 0x6C07, 0x5C64, 0x4C45, 0x3CA2, 0x2C83, 0x1CE0, 0x0CC1,
        0xEF1F, 0xFF3E, 0xCF5D, 0xDF7C, 0xAF9B, 0xBFBA, 0x8FD9, 0x9FF8,
        0x6E17, 0x7E36, 0x4E55, 0x5E74, 0x2E93, 0x3EB2, 0x0ED1, 0x1EF0
    ];

    /// <summary>
    /// Compute the CRC-16/XMODEM of a byte sequence as used by RepRapFirmware for CRC-protected lines
    /// </summary>
    /// <param name="data">Data to compute the CRC for (typically the line from N up to but excluding the '*' character)</param>
    /// <returns>CRC-16</returns>
    public static ushort ComputeLineCrc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc = (ushort)((crc << 8) ^ Crc16Table[((crc >> 8) ^ b) & 0xFF]);
        }
        return crc;
    }

    /// <summary>
    /// Compute the legacy XOR checksum of a byte sequence as used by RepRapFirmware for checksum-protected lines
    /// </summary>
    /// <param name="data">Data to compute the checksum for (typically the line from N up to but excluding the '*' character)</param>
    /// <returns>Checksum</returns>
    public static byte ComputeLineChecksum(ReadOnlySpan<byte> data)
    {
        byte checksum = 0;
        foreach (byte b in data)
        {
            checksum ^= b;
        }
        return checksum;
    }

    /// <summary>
    /// Get the line number from the content of a numbered line
    /// </summary>
    /// <param name="line">Buffer holding the line content that follows the leading 'N' character</param>
    /// <param name="length">Number of bytes of the line in the buffer</param>
    /// <returns>Line number as it appears in the line</returns>
    private static string ReadLineNumber(byte[] line, int length)
    {
        string lineNumber = string.Empty;
        for (int i = 0; i < length && line[i] >= '0' && line[i] <= '9'; i++)
        {
            lineNumber += (char)line[i];
        }
        return lineNumber;
    }

    /// <summary>
    /// Verify the checksum or CRC block of a line that starts with a line number
    /// </summary>
    /// <param name="line">Buffer holding the line content that follows the leading 'N' character</param>
    /// <param name="length">Number of bytes of the line in the buffer, excluding the line terminator</param>
    /// <param name="checksumType">Type of the verified checksum block. This is always none if the line has no content</param>
    /// <param name="hasContent">Whether the line has any content between the line number and the checksum block or comment</param>
    /// <returns>Number of bytes at the end of the line that make up the checksum block (0 if there is none)</returns>
    /// <exception cref="CodeParserException">Checksum or CRC is malformed or does not match</exception>
    /// <remarks>
    /// This mirrors the rules of the StringParser in RepRapFirmware:
    /// - The checksum covers every byte from 'N' up to but excluding the '*' character (CR is not included)
    /// - A '*' outside of quoted strings, curly braces and comments starts the checksum block. Everything after it is dropped
    /// - 1 to 3 decimal digits are an XOR checksum, exactly 5 decimal digits are a CRC-16. Other lengths are invalid
    /// - Lines without any content between the line number and the '*' are not verified
    /// </remarks>
    internal static int VerifyLineChecksum(byte[] line, int length, out LineChecksumType checksumType, out bool hasContent)
    {
        checksumType = LineChecksumType.None;
        hasContent = false;
        byte checksum = (byte)'N';
        ushort crc = ComputeLineCrc([(byte)'N']);

        // Find the start of the checksum block
        int checksumStart = -1, braceDepth = 0;
        bool inQuotes = false, inEncapsulatedComment = false, inLineNumber = true;
        for (int i = 0; i < length; i++)
        {
            byte b = line[i];
            if (b == '\r')
            {
                continue;
            }

            if (inQuotes)
            {
                inQuotes = (b != '"');
            }
            else if (inEncapsulatedComment)
            {
                inEncapsulatedComment = (b != ')');
            }
            else
            {
                inLineNumber &= (b >= '0' && b <= '9');
                if (b == '"')
                {
                    inQuotes = true;
                }
                else if (b == '{')
                {
                    braceDepth++;
                }
                else if (b == '}' && braceDepth > 0)
                {
                    braceDepth--;
                }
                else if (b == '(' && braceDepth == 0)
                {
                    inEncapsulatedComment = true;
                }
                else if (b == ';')
                {
                    // Nothing after a comment start can be a checksum
                    return 0;
                }
                else if (b == '*' && braceDepth == 0)
                {
                    checksumStart = i;
                    break;
                }

                if (!inLineNumber && b != ' ' && b != '\t')
                {
                    hasContent = true;
                }
            }

            checksum ^= b;
            crc = (ushort)((crc << 8) ^ Crc16Table[((crc >> 8) ^ b) & 0xFF]);
        }

        if (checksumStart < 0)
        {
            // No checksum block present
            return 0;
        }

        // Get the declared value. Like RepRapFirmware, anything after the digits is discarded
        int numDigits = 0, declaredValue = 0;
        for (int i = checksumStart + 1; i < length && line[i] >= '0' && line[i] <= '9'; i++)
        {
            if (numDigits < 6)
            {
                declaredValue = (declaredValue * 10) + (line[i] - '0');
            }
            numDigits++;
        }

        // Empty numbered lines are not checked by RepRapFirmware either
        if (hasContent)
        {
            string lineNumber = ReadLineNumber(line, length);

            switch (numDigits)
            {
                case 1:
                case 2:
                case 3:
                    if (declaredValue != checksum)
                    {
                        throw new CodeParserException($"Checksum error on line N{lineNumber} (declared {declaredValue}, computed {checksum})");
                    }
                    checksumType = LineChecksumType.Checksum;
                    break;

                case 5:
                    if (declaredValue != crc)
                    {
                        throw new CodeParserException($"CRC error on line N{lineNumber} (declared {declaredValue}, computed {crc})");
                    }
                    checksumType = LineChecksumType.Crc;
                    break;

                default:
                    throw new CodeParserException($"Invalid checksum on line N{lineNumber} (expected 1 to 3 digits for a checksum or 5 digits for a CRC)");
            }
        }
        return length - checksumStart;
    }
}
