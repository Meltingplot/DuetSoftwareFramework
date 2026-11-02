using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using DuetSharedLibrary;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;

namespace UnitTests.SPI;

/// <summary>
/// How RepRapFirmware deals with a transfer whose sequence number does not follow the last one
/// </summary>
public enum SequenceMismatchHandling
{
    /// <summary>
    /// Report a connection reset like RRF does (DataTransfer::IsConnectionReset), which drops the transfer and resets the connection
    /// </summary>
    ConnectionReset,

    /// <summary>
    /// Process the transfer and continue with its sequence number
    /// </summary>
    Ignore
}

/// <summary>
/// Direction of an SPI exchange
/// </summary>
public enum ExchangeDirection
{
    /// <summary>
    /// Bytes RRF receives from DSF
    /// </summary>
    ToRrf,

    /// <summary>
    /// Bytes DSF receives from RRF
    /// </summary>
    ToDsf
}

/// <summary>
/// Corruption of a single SPI exchange
/// </summary>
/// <param name="Exchange">Index of the exchange, counted from the moment the fault is armed</param>
/// <param name="Direction">Direction that is corrupted</param>
/// <param name="Mutate">Mutation of the bytes</param>
public sealed record SpiFault(int Exchange, ExchangeDirection Direction, SpiMutation Mutate);

/// <summary>
/// Mutation of the bytes of an exchange
/// </summary>
public delegate void SpiMutation(Span<byte> data);

/// <summary>
/// RepRapFirmware at the other end of the SPI link, ported from RRF 3.7.0+1.mp.5 (Meltingplot fork 03a87245b): the SPI part of DataTransfer
/// (src/SBC/DataTransfer.cpp: DoTransfer, RestartTransfer, StartNextTransfer, ResetConnection, setup_spi) and the transfer handling of
/// SbcInterface::TaskLoop (src/SBC/SbcInterface.cpp), including the emergency stop when an established connection is lost (13fa7c2dd)
/// </summary>
/// <remarks>
/// Every SPI exchange of DSF is one transfer for RRF, because the SAME70 ends it on the rising edge of chip select (SPI_IER_NSSR in setup_spi and
/// SBC_SPI_HANDLER) however many bytes were clocked. RRF receives at most the bytes it has set up, the rest of its buffer keeps its content, and
/// DSF reads the underrun byte for the bytes RRF did not set up. The SAME5x ends transfers differently and is not covered.
/// Time is virtual and passes only while DSF waits for the transfer ready pin, so the timeouts of RRF race the ones of DSF without real waiting
/// </remarks>
public sealed class FakeRrfSpi : ISpiDevice, IInputGpioPin
{
    // SbcMessageFormats.h
    private const uint SbcTransferTimeout = 500, SbcConnectionTimeout = 4000;
    private const int SbcTransferBufferSize = 8192;
    private static readonly int HeaderSize = Marshal.SizeOf<TransferHeader>();

    /// <summary>
    /// Virtual time per byte clocked, about 8 MHz
    /// </summary>
    private const double MsPerByte = 0.001;

    private enum InternalTransferState { ExchangingHeader, ExchangingHeaderResponse, ExchangingData, ExchangingDataResponse, ExchangingDataResponseRetry, Resetting, ResettingDataResponse, ProcessingData }
    private enum TransferState { DoingFullTransfer, DoingPartialTransfer, ConnectionReset, Finished }
    private enum DmaTarget { Header, Response, Data }

    // DataTransfer
    private InternalTransferState _state = InternalTransferState.ExchangingData;
    private ushort _lastTransferNumber;
    private TransferHeader _rxHeader, _txHeader;
    private readonly byte[] _rxHeaderBytes = new byte[HeaderSize];
    private uint _rxResponse, _txResponse;
    private readonly byte[] _rxBuffer = new byte[SbcTransferBufferSize], _txBuffer = new byte[SbcTransferBufferSize];
    private int _txPointer;
    private byte _packetId;
    private bool _transferReadyHigh;

    // SPI DMA transfer
    private DmaTarget _dmaTarget;
    private byte[] _dmaTx = [];
    private bool _dmaActive;

    // SbcInterface
    private bool _isConnected;
    private double _deadline = double.PositiveInfinity;

    // Transfer ready pin as seen by DSF
    private bool _pinLevel, _value;
    private readonly Queue<bool> _edges = new();

    // Test control
    private readonly Func<int, bool> _sendData;
    private readonly SequenceMismatchHandling _sequenceMismatchHandling;
    private readonly bool _pollWhileDisconnected, _rrfFirstOnTie;
    private readonly byte _underrunByte;
    private SpiFault? _fault;
    private int _faultArmedAt;
    private int _transfersStarted, _payloadId;

    /// <summary>
    /// Create the firmware end and start the first transfer like SbcInterface::TaskLoop does
    /// </summary>
    /// <param name="sendData">Whether RRF sends a message in the given transfer, counted from 1 for the first one</param>
    /// <param name="sequenceMismatchHandling">How a transfer with an unexpected sequence number is handled</param>
    /// <param name="pollWhileDisconnected">Whether RRF restarts the transfer after SbcConnectionTimeout while it is not connected, like builds with SUPPORTS_SBC_OVER_USB do</param>
    /// <param name="rrfFirstOnTie">Whether RRF times out first when both sides would time out at the same moment</param>
    /// <param name="underrunByte">Byte DSF reads when it clocks more bytes than RRF has set up</param>
    public FakeRrfSpi(Func<int, bool> sendData, SequenceMismatchHandling sequenceMismatchHandling = SequenceMismatchHandling.ConnectionReset,
        bool pollWhileDisconnected = false, bool rrfFirstOnTie = true, byte underrunByte = 0)
    {
        _underrunByte = underrunByte;
        _sendData = sendData;
        _sequenceMismatchHandling = sequenceMismatchHandling;
        _pollWhileDisconnected = pollWhileDisconnected;
        _rrfFirstOnTie = rrfFirstOnTie;

        // DataTransfer::Init
        _txHeader.FormatCode = Consts.FormatCode;
        _txHeader.ProtocolVersion = Consts.ProtocolVersion;

        // Data written before the first transfer
        WritePayload();
        StartNextTransfer();
    }

    /// <summary>
    /// Current virtual time in ms
    /// </summary>
    public double Now { get; private set; }

    /// <summary>
    /// Number of SPI exchanges so far
    /// </summary>
    public int Exchanges { get; private set; }

    /// <summary>
    /// Number of times RRF lost an established connection, each of which causes an emergency stop
    /// </summary>
    public int Disconnects { get; private set; }

    /// <summary>
    /// Number of times RRF reset the connection, including those before a connection was established
    /// </summary>
    public int ConnectionResets { get; private set; }

    /// <summary>
    /// Payloads RRF has written for DSF, in order
    /// </summary>
    public List<string> SentPayloads { get; } = [];

    /// <summary>
    /// Payloads RRF has processed from DSF, in order
    /// </summary>
    public List<string> ReceivedPayloads { get; } = [];

    /// <summary>
    /// Cancelled once the virtual time or the number of exchanges exceeds the budget
    /// </summary>
    public CancellationTokenSource Budget { get; } = new();

    /// <summary>
    /// Virtual time after which <see cref="Budget"/> is cancelled
    /// </summary>
    public double TimeBudget { get; set; } = 60000;

    /// <summary>
    /// Number of exchanges after which <see cref="Budget"/> is cancelled
    /// </summary>
    public int ExchangeBudget { get; set; } = 2000;

    private void CheckBudget()
    {
        if (Now >= TimeBudget || Exchanges >= ExchangeBudget)
        {
            Budget.Cancel();
        }
    }

    /// <summary>
    /// Trace of the exchanges and timeouts, recorded if set
    /// </summary>
    public List<string>? Trace { get; set; }

    /// <summary>
    /// Whether the fault has been applied
    /// </summary>
    public bool FaultApplied { get; private set; }

    /// <summary>
    /// Virtual time at which the fault was applied
    /// </summary>
    public double FaultTime { get; private set; }

    /// <summary>
    /// Corrupt a single exchange, counted from now
    /// </summary>
    /// <param name="fault">Fault to inject</param>
    public void Arm(SpiFault fault)
    {
        _fault = fault;
        _faultArmedAt = Exchanges;
    }

    #region DataTransfer
    private void WritePayload()
    {
        if (!_sendData(++_transfersStarted))
        {
            return;
        }

        // A message packet like DataTransfer::WriteMessage writes it
        string payload = $"rrf:{++_payloadId}";
        byte[] text = Encoding.UTF8.GetBytes(payload);
        PacketHeader packet = new() { Request = (ushort)FirmwareRequest.Message, Id = _packetId++, Length = (ushort)(Marshal.SizeOf<MessageHeader>() + text.Length) };
        MemoryMarshal.Write(_txBuffer.AsSpan(_txPointer), in packet);
        _txPointer += Marshal.SizeOf<PacketHeader>();
        MessageHeader message = new() { MessageType = MessageTypeFlags.UsbMessage, Length = (ushort)text.Length };
        MemoryMarshal.Write(_txBuffer.AsSpan(_txPointer), in message);
        _txPointer += Marshal.SizeOf<MessageHeader>();
        text.CopyTo(_txBuffer, _txPointer);
        _txPointer = (_txPointer + text.Length + 3) / 4 * 4;
        SentPayloads.Add(payload);
    }

    private void StartNextTransfer()
    {
        _lastTransferNumber = _rxHeader.SequenceNumber;

        // Reset RX transfer header
        _rxHeader.FormatCode = Consts.InvalidFormatCode;
        _rxHeader.NumPackets = 0;
        _rxHeader.ProtocolVersion = 0;
        _rxHeader.DataLength = 0;
        _rxHeader.ChecksumData32 = 0;
        _rxHeader.ChecksumHeader32 = 0;
        MemoryMarshal.Write(_rxHeaderBytes, in _rxHeader);

        // Set up TX transfer header
        _txHeader.NumPackets = _packetId;
        _txHeader.SequenceNumber++;
        _txHeader.DataLength = (ushort)_txPointer;
        _txHeader.ChecksumData32 = CRC32.Calculate(_txBuffer.AsSpan(0, _txPointer));
        byte[] header = new byte[HeaderSize];
        MemoryMarshal.Write(header, in _txHeader);
        _txHeader.ChecksumHeader32 = CRC32.Calculate(header.AsSpan(0, HeaderSize - sizeof(uint)));

        ExchangeHeader();
    }

    private void ResetConnection(bool fullReset)
    {
        ConnectionResets++;

        // Clear the remaining data to send
        _dmaActive = false;
        _txPointer = 0;
        _packetId = 0;

        // Reset the TfrRdy pin level and the seq numbers only if no communication is taking place
        if (fullReset)
        {
            _transferReadyHigh = false;
            _lastTransferNumber = _rxHeader.SequenceNumber = _txHeader.SequenceNumber = 0;
        }

        // Kick off a new transfer
        StartNextTransfer();
    }

    private void ExchangeHeader()
    {
        _state = InternalTransferState.ExchangingHeader;
        byte[] header = new byte[HeaderSize];
        MemoryMarshal.Write(header, in _txHeader);
        SetupSpi(DmaTarget.Header, header);
    }

    private void ExchangeResponse(uint response)
    {
        _txResponse = response;
        _state = (_state == InternalTransferState.ExchangingHeader) ? InternalTransferState.ExchangingHeaderResponse : InternalTransferState.ExchangingDataResponse;
        SetupSpi(DmaTarget.Response, BitConverter.GetBytes(response));
    }

    private void ExchangeData()
    {
        int bytesToExchange = Math.Max(_rxHeader.DataLength, _txHeader.DataLength);
        _state = InternalTransferState.ExchangingData;
        SetupSpi(DmaTarget.Data, _txBuffer.AsSpan(0, bytesToExchange).ToArray());
    }

    private void RestartTransfer(bool ownRequest)
    {
        if (ownRequest)
        {
            if (_rxHeader.DataLength > 0 || _txPointer > 0)
            {
                // Transfer bad data response and restart the transfer
                _txResponse = TransferResponse.BadResponse;
                _state = InternalTransferState.Resetting;
                SetupSpi(DmaTarget.Response, BitConverter.GetBytes(_txResponse));
            }
            else
            {
                // No data was transferred so we are still in sync. Continue with the next transfer
                _lastTransferNumber = (ushort)(_rxHeader.SequenceNumber - 1);
                ExchangeHeader();
            }
        }
        else if (_state != InternalTransferState.ExchangingHeader)
        {
            // SBC wants to restart the transfer
            ExchangeHeader();
        }
        else
        {
            // Last data response exchange failed, try to perform it again
            ExchangeResponse(TransferResponse.Success);
            _state = InternalTransferState.ExchangingDataResponseRetry;
        }
    }

    private bool IsConnectionReset() => _rxHeader.FormatCode == Consts.FormatCode && _rxHeader.SequenceNumber != (ushort)(_lastTransferNumber + 1);

    private TransferState Finish()
    {
        _txPointer = 0;
        _packetId = 0;
        _state = InternalTransferState.ProcessingData;

        // StartNextTransfer takes over the sequence number of a transfer that is processed
        return (IsConnectionReset() && _sequenceMismatchHandling == SequenceMismatchHandling.ConnectionReset) ? TransferState.ConnectionReset : TransferState.Finished;
    }

    private TransferState DoTransfer()
    {
        switch (_state)
        {
            case InternalTransferState.ExchangingHeader:
            {
                // (1) Exchanged transfer headers
                uint headerResponse = MemoryMarshal.Read<uint>(_rxHeaderBytes);
                if (headerResponse == TransferResponse.BadResponse)
                {
                    // SBC received a bad response code. We must have been happy if we got here, else RRF would have complained
                    RestartTransfer(false);
                    break;
                }

                uint checksum = CRC32.Calculate(_rxHeaderBytes.AsSpan(0, HeaderSize - sizeof(uint)));
                if (_rxHeader.ChecksumHeader32 != checksum)
                {
                    ExchangeResponse(TransferResponse.BadHeaderChecksum);
                    break;
                }
                if (_rxHeader.FormatCode != Consts.FormatCode)
                {
                    ExchangeResponse(TransferResponse.BadFormat);
                    break;
                }
                if (_rxHeader.ProtocolVersion != Consts.ProtocolVersion)
                {
                    ExchangeResponse(TransferResponse.BadProtocolVersion);
                    break;
                }
                if (_rxHeader.DataLength > SbcTransferBufferSize)
                {
                    ExchangeResponse(TransferResponse.BadDataLength);
                    break;
                }

                ExchangeResponse(TransferResponse.Success);
                break;
            }

            case InternalTransferState.ExchangingHeaderResponse:
                // (2) Exchanged response to transfer header
                if (_rxResponse == TransferResponse.Success && _txResponse == TransferResponse.Success)
                {
                    if (_rxHeader.DataLength != 0 || _txHeader.DataLength != 0)
                    {
                        // Perform the actual data transfer
                        ExchangeData();
                    }
                    else
                    {
                        // Everything OK
                        return Finish();
                    }
                }
                else if (_rxResponse == TransferResponse.BadHeaderChecksum || _txResponse == TransferResponse.BadHeaderChecksum)
                {
                    // Failed to exchange header, restart the full transfer
                    ExchangeHeader();
                }
                else
                {
                    // Restart the full transfer
                    RestartTransfer(_rxResponse != TransferResponse.BadResponse);
                }
                break;

            case InternalTransferState.ExchangingData:
            {
                // (3) Exchanged data
                if (MemoryMarshal.Read<uint>(_rxBuffer) == TransferResponse.BadResponse)
                {
                    RestartTransfer(false);
                    break;
                }

                uint checksum = CRC32.Calculate(_rxBuffer.AsSpan(0, _rxHeader.DataLength));
                if (_rxHeader.ChecksumData32 != checksum)
                {
                    ExchangeResponse(TransferResponse.BadDataChecksum);
                    break;
                }

                ExchangeResponse(TransferResponse.Success);
                break;
            }

            case InternalTransferState.ExchangingDataResponse:
                // (4a) Exchanged response to data transfer
                if (_rxResponse == TransferResponse.Success && _txResponse == TransferResponse.Success)
                {
                    // Everything OK
                    return Finish();
                }

                if (_rxResponse == TransferResponse.BadDataChecksum || _txResponse == TransferResponse.BadDataChecksum)
                {
                    // Resend the data if a checksum error occurred
                    ExchangeData();
                }
                else if (_rxResponse == TransferResponse.BadResponse)
                {
                    // Restart the full transfer
                    RestartTransfer(false);
                }
                else
                {
                    // Restart the data response transfer
                    RestartTransfer(true);
                    _state = InternalTransferState.ResettingDataResponse;
                }
                break;

            case InternalTransferState.ExchangingDataResponseRetry:
                // (4b) Exchanged response to data transfer when new transfer is being started (fallback on bad response)
                if (_rxResponse == TransferResponse.Success && _txResponse == TransferResponse.Success)
                {
                    // Retry succeeded
                    ExchangeHeader();
                }
                else if ((_rxResponse & 0xFF) == Consts.FormatCode)
                {
                    // The SBC restarted the full transfer and sent a new header instead of a data response.
                    // Most of that header was lost, so report a bad header checksum to make the SBC send it again
                    ExchangeResponse(TransferResponse.BadHeaderChecksum);
                    _state = InternalTransferState.ExchangingHeaderResponse;
                }
                else
                {
                    // Retry failed, reset the connection
                    return TransferState.ConnectionReset;
                }
                break;

            case InternalTransferState.Resetting:
                // Transmitted bad response, attempt to restart the connection
                ExchangeHeader();
                break;

            case InternalTransferState.ResettingDataResponse:
                // Transmitted bad response after data response exchange, attempt to restart the data response exchange
                ExchangeResponse(TransferResponse.Success);
                break;

            default:
                // Should never get here
                ExchangeHeader();
                break;
        }
        return (_state == InternalTransferState.ExchangingHeader) ? TransferState.DoingFullTransfer : TransferState.DoingPartialTransfer;
    }

    /// <summary>
    /// Set up the next DMA transfer and toggle the transfer ready pin
    /// </summary>
    private void SetupSpi(DmaTarget target, byte[] tx)
    {
        _dmaTarget = target;
        _dmaTx = tx;
        _dmaActive = true;

        _transferReadyHigh = !_transferReadyHigh;
        if (_pinLevel != _transferReadyHigh)
        {
            _pinLevel = _transferReadyHigh;
            _edges.Enqueue(_pinLevel);
        }
    }

    /// <summary>
    /// Store the bytes received by a DMA transfer where RRF expects them. The rest of the target keeps its content
    /// </summary>
    private void CompleteDma(ReadOnlySpan<byte> received)
    {
        switch (_dmaTarget)
        {
            case DmaTarget.Header:
                received.CopyTo(_rxHeaderBytes);
                _rxHeader = MemoryMarshal.Read<TransferHeader>(_rxHeaderBytes);
                break;
            case DmaTarget.Response:
                byte[] response = BitConverter.GetBytes(_rxResponse);
                received.CopyTo(response);
                _rxResponse = BitConverter.ToUInt32(response);
                break;
            case DmaTarget.Data:
                received.CopyTo(_rxBuffer);
                break;
        }
    }
    #endregion

    #region SbcInterface
    /// <summary>
    /// Deal with the result of DataTransfer::DoTransfer like the loop in SbcInterface::TaskLoop
    /// </summary>
    private void RunTask(TransferState state, bool hadTimeout)
    {
        bool transferComplete = state == TransferState.Finished, hadReset = state == TransferState.ConnectionReset;
        if (transferComplete || hadReset || hadTimeout)
        {
            // Handle connection errors. Losing an established connection causes an emergency stop
            if (_isConnected && (hadReset || hadTimeout))
            {
                _isConnected = false;
                Disconnects++;
            }

            if (transferComplete)
            {
                _isConnected = true;

                // Handle exchanged data and kick off the next transfer
                ProcessData();
                WritePayload();
                StartNextTransfer();
            }
            else
            {
                // SPI: reset the connection if no data could be exchanged
                ResetConnection(hadTimeout);
            }
        }

        // Wait for the next DMA transfer
        _deadline = (_state != InternalTransferState.ExchangingHeader) ? Now + SbcTransferTimeout :
            (_isConnected || _pollWhileDisconnected) ? Now + SbcConnectionTimeout : double.PositiveInfinity;
    }

    /// <summary>
    /// Record the payloads DSF sent
    /// </summary>
    private void ProcessData()
    {
        string data = Encoding.ASCII.GetString(_rxBuffer, 0, _rxHeader.DataLength);
        for (int index = data.IndexOf("dsf:", StringComparison.Ordinal); index >= 0; index = data.IndexOf("dsf:", index + 4, StringComparison.Ordinal))
        {
            int end = index + 4;
            while (end < data.Length && char.IsAsciiDigit(data[end]))
            {
                end++;
            }
            ReceivedPayloads.Add(data[index..end]);
        }
    }

    /// <summary>
    /// Let virtual time pass until the given moment and handle a timeout of RRF that occurs meanwhile
    /// </summary>
    /// <returns>Whether RRF timed out</returns>
    private bool AdvanceTo(double end)
    {
        if (_deadline < end || (_deadline == end && _rrfFirstOnTie))
        {
            Trace?.Add($"   {_deadline,9:F3} ms: RRF timeout in {_state}{(_isConnected ? ", disconnect" : "")}");
            Now = _deadline;
            RunTask(TransferState.DoingPartialTransfer, true);
            return true;
        }
        Now = end;
        return false;
    }

    /// <summary>
    /// Let virtual time pass without any exchange, e.g. while DCS restarts
    /// </summary>
    /// <param name="ms">Time to pass in ms</param>
    public void Wait(double ms)
    {
        double end = Now + ms;
        while (AdvanceTo(end))
        {
            // RRF timed out, it may time out again before the end
        }
    }
    #endregion

    #region ISpiDevice and IInputGpioPin
    /// <inheritdoc/>
    public void TransferFullDuplex(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer)
    {
        CheckBudget();
        int exchange = Exchanges++;
        byte[] toRrf = writeBuffer.ToArray();
        SpiFault? fault = (_fault is not null && exchange - _faultArmedAt == _fault.Exchange) ? _fault : null;
        if (fault?.Direction == ExchangeDirection.ToRrf)
        {
            fault.Mutate(toRrf);
        }

        // The rising edge of chip select ends the DMA transfer however many bytes were clocked
        bool completed = _dmaActive;
        int count = completed ? Math.Min(toRrf.Length, _dmaTx.Length) : 0;
        for (int i = 0; i < toRrf.Length; i++)
        {
            readBuffer[i] = (i < count) ? _dmaTx[i] : completed ? _underrunByte : (byte)0xFF;
        }
        _dmaActive = false;

        if (fault?.Direction == ExchangeDirection.ToDsf)
        {
            fault.Mutate(readBuffer);
        }
        if (fault is not null)
        {
            _fault = null;
            FaultApplied = true;
            FaultTime = Now;
        }

        Now += toRrf.Length * MsPerByte;
        InternalTransferState stateBefore = _state;
        if (completed)
        {
            CompleteDma(toRrf.AsSpan(0, count));
            RunTask(DoTransfer(), false);
        }
        Trace?.Add($"#{exchange} {Now,9:F3} ms{(fault is not null ? $" FAULT {fault.Direction}" : "")}: DSF {Convert.ToHexString(toRrf.AsSpan(0, Math.Min(toRrf.Length, 16)))} " +
            $"RRF {Convert.ToHexString(readBuffer[..Math.Min(readBuffer.Length, 16)])} | RRF {stateBefore} -> {_state}, pin {(_pinLevel ? 1 : 0)}, connected {_isConnected}");
    }

    /// <inheritdoc/>
    public int MissedEdges => 0;

    /// <inheritdoc/>
    public bool Value => _value;

    /// <inheritdoc/>
    public void FlushEvents()
    {
        while (_edges.TryDequeue(out bool level))
        {
            _value = level;
        }
    }

    /// <inheritdoc/>
    public bool WaitForEvent(int timeout)
    {
        CheckBudget();
        double end = Now + timeout;
        while (_edges.Count == 0)
        {
            if (!AdvanceTo(end))
            {
                // DSF times out first
                Trace?.Add($"   {Now,9:F3} ms: DSF timeout after {timeout} ms");
                throw new OperationCanceledException();
            }
        }
        _value = _edges.Dequeue();
        return _value;
    }
    #endregion
}
