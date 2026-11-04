using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using DuetSharedLibrary;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Link.Adapter;

/// <summary>
/// Exchange of a transfer
/// </summary>
public enum SpiExchange
{
    /// <summary>
    /// Transfer headers
    /// </summary>
    Header,

    /// <summary>
    /// Response to the headers
    /// </summary>
    HeaderResponse,

    /// <summary>
    /// Transfer data
    /// </summary>
    Data,

    /// <summary>
    /// Response to the data
    /// </summary>
    DataResponse
}

/// <summary>
/// Kind of transfer, by which side has data in it
/// </summary>
public enum SpiTransferKind
{
    /// <summary>
    /// Any transfer
    /// </summary>
    Any,

    /// <summary>
    /// Neither side has data
    /// </summary>
    NoData,

    /// <summary>
    /// Both sides have data
    /// </summary>
    BothData,

    /// <summary>
    /// Only RRF has data
    /// </summary>
    RrfData,

    /// <summary>
    /// Only DSF has data
    /// </summary>
    DsfData
}

/// <summary>
/// Bytes of an exchange that are corrupted
/// </summary>
public enum SpiFaultDirection
{
    /// <summary>
    /// Bytes DSF sends
    /// </summary>
    ToRrf,

    /// <summary>
    /// Bytes DSF receives
    /// </summary>
    ToDsf,

    /// <summary>
    /// Bytes in both directions of the same exchange, as a disturbance on both data lines
    /// </summary>
    Both
}

/// <summary>
/// Single corrupted SPI exchange, as the link census (UnitTests/SPI/LinkRecovery.cs) injects it into its port of RRF
/// and a build with SPI_FAULT_INJECTION injects it into the real link
/// </summary>
/// <param name="Kind">Kind of transfer to hit</param>
/// <param name="Exchange">Exchange to corrupt</param>
/// <param name="Direction">Bytes that are corrupted</param>
/// <param name="Mutation">Mutation of the bytes, see <see cref="Apply"/></param>
public sealed record SpiFaultSpec(SpiTransferKind Kind, SpiExchange Exchange, SpiFaultDirection Direction, string Mutation)
{
    /// <summary>
    /// Text form, which <see cref="Parse"/> reads
    /// </summary>
    public override string ToString() => $"{Kind} {Exchange} {Direction} {Mutation}";

    /// <summary>
    /// Read a fault from its text form, e.g. "BothData HeaderResponse ToDsf flip 2"
    /// </summary>
    /// <param name="text">Text form</param>
    /// <returns>Fault</returns>
    /// <exception cref="FormatException">Invalid text</exception>
    public static SpiFaultSpec Parse(string text)
    {
        string[] parts = text.Trim().Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || !Enum.TryParse(parts[0], out SpiTransferKind kind) || !Enum.TryParse(parts[1], out SpiExchange exchange) || !Enum.GetNames<SpiFaultDirection>().Contains(parts[2]))
        {
            throw new FormatException($"Invalid SPI fault: {text}");
        }
        Apply(parts[3], []);
        return new SpiFaultSpec(kind, exchange, Enum.Parse<SpiFaultDirection>(parts[2]), parts[3]);
    }

    /// <summary>
    /// Corrupt the bytes of an exchange. Mutations:
    /// "flip bit", "zero offset size", "ones offset size", "add offset size delta", "word value" (32 bits at the start), "fill value".
    /// Fields are little-endian and cut off at the end of the exchange, offsets beyond it leave it as it is
    /// </summary>
    /// <param name="mutation">Mutation</param>
    /// <param name="data">Bytes of the exchange</param>
    /// <exception cref="FormatException">Invalid mutation</exception>
    public static void Apply(string mutation, Span<byte> data)
    {
        string[] args = mutation.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long Arg(int index) => (args.Length > index) ? ParseNumber(args[index]) : throw new FormatException($"Invalid SPI mutation: {mutation}");
        switch (args.FirstOrDefault())
        {
            case "flip":
                long bit = Arg(1);
                if (bit / 8 < data.Length)
                {
                    data[(int)(bit / 8)] ^= (byte)(1 << (int)(bit % 8));
                }
                break;
            case "zero":
                Field(data, Arg(1), Arg(2)).Clear();
                break;
            case "ones":
                Field(data, Arg(1), Arg(2)).Fill(0xFF);
                break;
            case "add":
                Span<byte> field = Field(data, Arg(1), Arg(2));
                ulong value = 0;
                for (int i = field.Length - 1; i >= 0; i--)
                {
                    value = (value << 8) | field[i];
                }
                value = unchecked(value + (ulong)Arg(3));
                for (int i = 0; i < field.Length; i++)
                {
                    field[i] = (byte)(value >> (8 * i));
                }
                break;
            case "word":
                if (data.Length >= sizeof(uint))
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)Arg(1));
                }
                break;
            case "fill":
                data.Fill((byte)Arg(1));
                break;
            default:
                throw new FormatException($"Invalid SPI mutation: {mutation}");
        }
    }

    private static Span<byte> Field(Span<byte> data, long offset, long size) => (offset < data.Length) ? data[(int)offset..(int)Math.Min(offset + size, data.Length)] : [];

    private static long ParseNumber(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? long.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : long.Parse(text, CultureInfo.InvariantCulture);
}

/// <summary>
/// SPI device that corrupts a single exchange of the link when it is armed, to test the recovery of the link on hardware.
/// DCS uses it only when built with SPI_FAULT_INJECTION, and it is armed by writing the text form of a <see cref="SpiFaultSpec"/>
/// to the control file. It recognises the exchanges from what DSF sends: a header starts with the format code, a 4-byte exchange
/// after a header or data is the response to it, anything else is data
/// </summary>
/// <param name="device">Real device</param>
/// <param name="controlFile">File to watch for faults, or null to arm only through <see cref="Arm"/></param>
/// <param name="logger">Logger for the injected faults</param>
public sealed class FaultInjectingSpiDevice(ISpiDevice device, string? controlFile, ILogger logger) : ISpiDevice
{
    /// <summary>
    /// Interval at which the control file is checked
    /// </summary>
    private static readonly TimeSpan ControlFileInterval = TimeSpan.FromMilliseconds(50);

    private DateTime _lastCheck, _lastWrite;
    private SpiFaultSpec? _armed;
    private SpiExchange? _lastExchange;
    private int _txDataLength, _rxDataLength;

    /// <summary>
    /// Raised when a fault has been injected, with the kind of transfer it hit
    /// </summary>
    public event Action<SpiFaultSpec, SpiTransferKind>? Injected;

    /// <summary>
    /// Inject a fault into the next transfer that has the exchange and is of the requested kind
    /// </summary>
    /// <param name="fault">Fault to inject</param>
    public void Arm(SpiFaultSpec fault) => _armed = fault;

    /// <inheritdoc/>
    public void TransferFullDuplex(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer)
    {
        SpiExchange exchange;
        if ((writeBuffer.Length == Marshal.SizeOf<TransferHeader>() || writeBuffer.Length == 12) && writeBuffer[0] == Consts.FormatCode)
        {
            exchange = SpiExchange.Header;
            _txDataLength = BinaryPrimitives.ReadUInt16LittleEndian(writeBuffer[6..]);
            _rxDataLength = -1;
            CheckControlFile();
        }
        else if (writeBuffer.Length == sizeof(uint))
        {
            exchange = (_lastExchange is SpiExchange.Data or SpiExchange.DataResponse) ? SpiExchange.DataResponse : SpiExchange.HeaderResponse;
        }
        else
        {
            exchange = SpiExchange.Data;
        }
        _lastExchange = exchange;

        // Only the DSF part of the kind is known before the headers are exchanged
        SpiFaultSpec? fault = (_armed is not null && _armed.Exchange == exchange && Matches(_armed.Kind, _txDataLength, (exchange == SpiExchange.Header) ? null : _rxDataLength)) ? _armed : null;
        if (fault?.Direction is SpiFaultDirection.ToRrf or SpiFaultDirection.Both)
        {
            byte[] corrupted = writeBuffer.ToArray();
            SpiFaultSpec.Apply(fault.Mutation, corrupted);
            device.TransferFullDuplex(corrupted, readBuffer);
        }
        else
        {
            device.TransferFullDuplex(writeBuffer, readBuffer);
        }

        if (exchange == SpiExchange.Header)
        {
            // The data length of RRF counts only if its header is valid
            _rxDataLength = (readBuffer[0] == Consts.FormatCode && readBuffer.Length == Marshal.SizeOf<TransferHeader>() &&
                BinaryPrimitives.ReadUInt32LittleEndian(readBuffer[12..]) == CRC32.Calculate(readBuffer[..12])) ? BinaryPrimitives.ReadUInt16LittleEndian(readBuffer[6..]) : 0;
        }
        if (fault is not null)
        {
            if (fault.Direction is SpiFaultDirection.ToDsf or SpiFaultDirection.Both)
            {
                SpiFaultSpec.Apply(fault.Mutation, readBuffer);
            }
            _armed = null;

            SpiTransferKind kind = (_txDataLength, _rxDataLength) switch
            {
                (0, 0) => SpiTransferKind.NoData,
                (0, _) => SpiTransferKind.RrfData,
                (_, 0) => SpiTransferKind.DsfData,
                _ => SpiTransferKind.BothData
            };
            logger.LogWarning("SPI fault injected: {Fault}, hit a {Kind} transfer", fault, kind);
            Injected?.Invoke(fault, kind);
        }
    }

    private static bool Matches(SpiTransferKind kind, int txDataLength, int? rxDataLength) => kind switch
    {
        SpiTransferKind.NoData => txDataLength == 0 && (rxDataLength is null or 0),
        SpiTransferKind.BothData => txDataLength != 0 && (rxDataLength is null || rxDataLength > 0),
        SpiTransferKind.RrfData => txDataLength == 0 && (rxDataLength is null || rxDataLength > 0),
        SpiTransferKind.DsfData => txDataLength != 0 && (rxDataLength is null or 0),
        _ => true
    };

    /// <summary>
    /// Arm the fault written to the control file since the last check
    /// </summary>
    private void CheckControlFile()
    {
        DateTime now = DateTime.UtcNow;
        if (controlFile is null || now - _lastCheck < ControlFileInterval)
        {
            return;
        }
        _lastCheck = now;

        try
        {
            DateTime lastWrite = File.GetLastWriteTimeUtc(controlFile);
            if (_lastWrite == default)
            {
                // A fault written before DCS started belongs to the instance before it
                _lastWrite = lastWrite;
                return;
            }
            if (lastWrite != _lastWrite && File.Exists(controlFile))
            {
                _lastWrite = lastWrite;
                string text = File.ReadAllText(controlFile).Trim();
                if (text.Length > 0)
                {
                    _armed = SpiFaultSpec.Parse(text);
                    logger.LogWarning("SPI fault armed: {Fault}", _armed);
                }
                else if (_armed is not null)
                {
                    // An empty file disarms a fault that has not found its transfer
                    logger.LogWarning("SPI fault disarmed: {Fault}", _armed);
                    _armed = null;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            logger.LogError("Cannot arm the SPI fault from {File}: {Message}", controlFile, e.Message);
        }
    }
}
