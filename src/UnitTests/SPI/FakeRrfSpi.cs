using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using DuetSharedLibrary;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

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
/// <param name="RestartBootTime">Instead of corrupting the exchange, RRF restarts right before it and boots for this time in ms</param>
/// <param name="RestartAgain">The restarted RRF restarts once more right after its first transfer, as a reconnect timeout does</param>
/// <param name="RrfFirstSequenceNumber">Sequence number of the first transfer of RRF, to make it wrap before the fault</param>
public sealed record SpiFault(int Exchange, ExchangeDirection Direction, SpiMutation Mutate, double? RestartBootTime = null, bool RestartAgain = false, ushort RrfFirstSequenceNumber = 1);

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
    private uint _rxResponse, _txResponse, _rxResponseWord;
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
    private readonly bool _pollWhileDisconnected, _rrfFirstOnTie, _phaseGuard, _sequenceGuard;

    // Proposed change: capabilities RRF offers in limits.sbcLinkCapabilities and the ones DSF activated for the current connection
    private readonly LinkCapabilities _offeredCapabilities;
    private LinkCapabilities _activeCapabilities;
    private bool PhaseGuardActive => _phaseGuard || _activeCapabilities.HasFlag(LinkCapabilities.PhaseGuard);
    private bool SequenceGuardActive => _sequenceGuard || _activeCapabilities.HasFlag(LinkCapabilities.SequenceGuard);
    private bool Extended => _activeCapabilities.HasFlag(LinkCapabilities.ExtendedHeader);

    // Proposed change: last completed transfer, sent again when DSF repeats its part of it, and the transfer that waits meanwhile
    private TransferHeader _lastTxHeader, _pendingTxHeader;
    private byte[]? _lastTxData;
    private byte[] _pendingTxData = [];
    private int _pendingTxPointer;
    private byte _pendingPacketId;
    private bool _resending;
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
    /// <param name="phaseGuard">Proposed change: RRF recognises from what it receives that DSF is in another phase and goes back to it</param>
    /// <param name="sequenceGuard">Proposed change: when DSF repeats the last completed transfer, RRF sends it again, and when DSF is a transfer ahead, RRF waits for DSF to send it again</param>
    /// <param name="firstSequenceNumber">Sequence number of the first transfer</param>
    /// <param name="capabilities">Proposed change: capabilities RRF offers, which DSF activates with a request</param>
    public FakeRrfSpi(Func<int, bool> sendData, SequenceMismatchHandling sequenceMismatchHandling = SequenceMismatchHandling.ConnectionReset,
        bool pollWhileDisconnected = false, bool rrfFirstOnTie = true, byte underrunByte = 0, bool phaseGuard = false, bool sequenceGuard = false, ushort firstSequenceNumber = 1,
        LinkCapabilities capabilities = LinkCapabilities.None)
    {
        _offeredCapabilities = capabilities;
        _phaseGuard = phaseGuard;
        _sequenceGuard = sequenceGuard;
        _underrunByte = underrunByte;
        _sendData = sendData;
        _sequenceMismatchHandling = sequenceMismatchHandling;
        _pollWhileDisconnected = pollWhileDisconnected;
        _rrfFirstOnTie = rrfFirstOnTie;

        // DataTransfer::Init
        _txHeader.FormatCode = Consts.FormatCode;
        _txHeader.ProtocolVersion = Consts.ProtocolVersion;
        _txHeader.SequenceNumber = (ushort)(firstSequenceNumber - 1);

        // Data written before the first transfer
        WritePayload();
        StartNextTransfer();
    }

    /// <summary>
    /// Current virtual time in ms
    /// </summary>
    public double Now { get; private set; }

    /// <summary>
    /// Number of activation requests RRF processed
    /// </summary>
    public int Activations { get; private set; }

    /// <summary>
    /// Capabilities that are active for the current connection
    /// </summary>
    public LinkCapabilities ActiveCapabilities => _activeCapabilities;

    /// <summary>
    /// When RRF restarted, counted in exchanges: 3.5 is between the third and the fourth exchange
    /// </summary>
    public List<double> Restarts { get; } = [];

    /// <summary>
    /// Boot time of a restart to perform after the first transfer of the restarted RRF
    /// </summary>
    private double? _restartAgainBootTime;

    /// <summary>
    /// Virtual time at which a restarting RRF has booted, or null if it is running
    /// </summary>
    private double? _bootAt;

    /// <summary>
    /// The processor resets: SPI and DMA stop, TfrRdy becomes an output driven low (DataTransfer::Init) and the RAM content is gone
    /// </summary>
    private void Restart(double bootTime, double exchanges)
    {
        Trace?.Add($"   {Now,9:F3} ms: RRF restarts, booting for {bootTime} ms");
        Restarts.Add(exchanges);
        _dmaActive = false;
        _isConnected = false;
        _transferReadyHigh = false;
        if (_pinLevel)
        {
            _pinLevel = false;
            _edges.Enqueue(false);
        }

        _state = InternalTransferState.ExchangingData;
        _rxHeader = _txHeader = default;
        Array.Clear(_rxHeaderBytes);
        _rxResponse = _txResponse = _rxResponseWord = 0;
        _activeCapabilities = LinkCapabilities.None;
        _lastTransferNumber = 0;
        _txPointer = 0;
        _packetId = 0;
        _lastTxData = null;
        _resending = false;
        _deadline = double.PositiveInfinity;
        _bootAt = Now + bootTime;
    }

    /// <summary>
    /// The restarted RRF has booted and starts the first transfer like the constructor
    /// </summary>
    private void Boot()
    {
        _bootAt = null;
        _txHeader.FormatCode = Consts.FormatCode;
        _txHeader.ProtocolVersion = Consts.ProtocolVersion;
        WritePayload();
        StartNextTransfer();
        _deadline = _pollWhileDisconnected ? Now + SbcConnectionTimeout : double.PositiveInfinity;
    }

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
    /// Inputs and expected outputs for replaying the run against the real RRF code built as a host program, recorded if set.
    /// An exchange is recorded as "X hex" with what RRF sends before any corruption on the way to DSF, a timeout of the SBC task as "T"
    /// </summary>
    public List<(string Input, string? Output)>? Replay { get; set; }

    /// <summary>
    /// State reported after each replayed event, in the format of the host program
    /// </summary>
    private string ReplayStatus(int receivedBefore)
    {
        string wait = (_state != InternalTransferState.ExchangingHeader) ? "partial" : (_isConnected || _pollWhileDisconnected) ? "full" : "unlimited";
        string got = (ReceivedPayloads.Count > receivedBefore) ? string.Join(",", ReceivedPayloads.Skip(receivedBefore)) : "-";
        return $"state={_state} pin={(_pinLevel ? 1 : 0)} conn={(_isConnected ? 1 : 0)} disc={Disconnects} resets={ConnectionResets} wait={wait} got={got}";
    }

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

        // DataTransfer::WritePacketHeader pads only before the next packet, so the last packet ends unpadded
        _txPointer = (_txPointer + 3) / 4 * 4;

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
        _txPointer += text.Length;
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
        _txHeader.SequenceNumber = ExtendedLink.Next(_txHeader.SequenceNumber, Extended);
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

        // Proposed change: the capabilities end with the connection. Numbering from the counter on gives status 0, which tells DSF that RRF has left the extended link
        if (Extended)
        {
            _txHeader.SequenceNumber &= ExtendedLink.CounterMask;
        }
        _activeCapabilities = LinkCapabilities.None;
        _lastTxData = null;
        _resending = false;

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
        SetupSpi(DmaTarget.Response, BitConverter.GetBytes(EncodeResponse(response)));
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
                SetupSpi(DmaTarget.Response, BitConverter.GetBytes(EncodeResponse(_txResponse)));
            }
            else
            {
                // No data was transferred so we are still in sync. Continue with the next transfer
                _lastTransferNumber = ExtendedLink.Previous(_rxHeader.SequenceNumber);
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

    private bool IsConnectionReset() => _rxHeader.FormatCode == Consts.FormatCode && ExtendedLink.Distance(_lastTransferNumber, _rxHeader.SequenceNumber, Extended) != 1;

    /// <summary>
    /// Proposed change: send a response as an extended code on the extended link
    /// </summary>
    private uint EncodeResponse(uint response) => Extended ? ExtendedLink.EncodeResponse(response) : response;

    /// <summary>
    /// Proposed change: read a response, which DSF sends as an extended code once it uses the extended link
    /// </summary>
    private uint DecodeResponse(uint word) => Extended ? ExtendedLink.DecodeResponse(word) : word;

    private TransferState Finish()
    {
        if (_resending)
        {
            // Proposed change: DSF has completed the transfer it missed. Its data in it was processed already, so carry on with the pending transfer
            _txHeader = _pendingTxHeader;
            _pendingTxData.CopyTo(_txBuffer, 0);
            _txPointer = _pendingTxPointer;
            _packetId = _pendingPacketId;
            _resending = false;
            ExchangeHeader();
            return TransferState.DoingFullTransfer;
        }
        if (SequenceGuardActive)
        {
            _lastTxHeader = _txHeader;
            _lastTxData = _txBuffer.AsSpan(0, _txHeader.DataLength).ToArray();
        }

        _txPointer = 0;
        _packetId = 0;
        _state = InternalTransferState.ProcessingData;

        // StartNextTransfer takes over the sequence number of a transfer that is processed
        return (IsConnectionReset() && _sequenceMismatchHandling == SequenceMismatchHandling.ConnectionReset) ? TransferState.ConnectionReset : TransferState.Finished;
    }

    /// <summary>
    /// Whether the given bytes are a transfer header with a valid checksum
    /// </summary>
    private static bool IsValidHeader(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize && data[0] == Consts.FormatCode && MemoryMarshal.Read<uint>(data[(HeaderSize - sizeof(uint))..]) == CRC32.Calculate(data[..(HeaderSize - sizeof(uint))]);

    /// <summary>
    /// Proposed change: deal with DSF being in another phase than RRF, as far as what RRF received shows it
    /// </summary>
    /// <returns>Whether RRF went back to the phase of DSF</returns>
    private bool GuardPhase()
    {
        if (!PhaseGuardActive)
        {
            return false;
        }

        // RA: in a response state, the start of a header means DSF is in the header phase. Only 4 bytes of it arrived, so ask for it again
        if (_state is InternalTransferState.ExchangingHeaderResponse or InternalTransferState.ExchangingDataResponse && (_rxResponse & 0xFF) == Consts.FormatCode)
        {
            ExchangeResponse(TransferResponse.BadHeaderChecksum);
            _state = InternalTransferState.ExchangingHeaderResponse;
            return true;
        }

        // RB: in the data phase, a valid header means DSF did not get the Success of the header response. DSF got data instead of a header, so ask for it again
        if (_state == InternalTransferState.ExchangingData && IsValidHeader(_rxBuffer))
        {
            _rxBuffer.AsSpan(0, HeaderSize).CopyTo(_rxHeaderBytes);
            _rxHeader = MemoryMarshal.Read<TransferHeader>(_rxHeaderBytes);
            ExchangeResponse(TransferResponse.BadHeaderChecksum);
            _state = InternalTransferState.ExchangingHeaderResponse;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Proposed change: deal with DSF repeating the last completed transfer or being a transfer ahead
    /// </summary>
    /// <returns>Whether RRF asked for the headers to be exchanged again</returns>
    private bool GuardSequence()
    {
        if (!SequenceGuardActive || !_isConnected)
        {
            return false;
        }

        // DSF repeats the transfer RRF completed last, so DSF did not complete it. Send that transfer again before the current one
        int distance = ExtendedLink.Distance(_lastTransferNumber, _rxHeader.SequenceNumber, Extended);
        if (!_resending && _lastTxData is not null && distance == 0)
        {
            _pendingTxHeader = _txHeader;
            _pendingTxData = _txBuffer.AsSpan(0, _txHeader.DataLength).ToArray();
            _pendingTxPointer = _txPointer;
            _pendingPacketId = _packetId;
            _txHeader = _lastTxHeader;
            _lastTxData.CopyTo(_txBuffer, 0);
            _txPointer = _lastTxHeader.DataLength;
            _resending = true;

            // Make both sides exchange the headers again
            ExchangeResponse(TransferResponse.BadHeaderChecksum);
            return true;
        }

        // DSF completed a transfer that RRF did not. DSF sends it again once it sees RRF repeat its part
        if (distance == 2)
        {
            ExchangeResponse(TransferResponse.BadHeaderChecksum);
            return true;
        }
        return false;
    }

    private TransferState DoTransfer()
    {
        if (GuardPhase())
        {
            return TransferState.DoingPartialTransfer;
        }

        switch (_state)
        {
            case InternalTransferState.ExchangingHeader:
            {
                // (1) Exchanged transfer headers
                uint headerResponse = DecodeResponse(MemoryMarshal.Read<uint>(_rxHeaderBytes));
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
                if (Extended && ExtendedLink.Distance(_lastTransferNumber, _rxHeader.SequenceNumber, true) < 0)
                {
                    // Proposed change: DSF has left the extended link, which only a restart or a connection reset does. Leave it as well,
                    // so that DSF understands the responses. The sequence number no longer matches, so the connection is reset when the transfer ends
                    _activeCapabilities = LinkCapabilities.None;
                    _lastTxData = null;
                }
                if (GuardSequence())
                {
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
                if (DecodeResponse(MemoryMarshal.Read<uint>(_rxBuffer)) == TransferResponse.BadResponse)
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
                byte[] response = BitConverter.GetBytes(_rxResponseWord);
                received.CopyTo(response);
                _rxResponseWord = BitConverter.ToUInt32(response);
                _rxResponse = DecodeResponse(_rxResponseWord);
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

            if (_restartAgainBootTime is double bootTime && (transferComplete || hadReset))
            {
                _restartAgainBootTime = null;
                Restart(bootTime, Exchanges + 0.5);
                return;
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
        // Proposed change: activate the requested capabilities that RRF offers, from the next transfer on
        for (int offset = 0; offset + Marshal.SizeOf<PacketHeader>() <= _rxHeader.DataLength;)
        {
            PacketHeader packet = MemoryMarshal.Read<PacketHeader>(_rxBuffer.AsSpan(offset));
            offset += Marshal.SizeOf<PacketHeader>();
            if (packet.Request == (ushort)SbcRequest.ActivateLinkCapabilities && packet.Length >= sizeof(uint))
            {
                _activeCapabilities = (LinkCapabilities)MemoryMarshal.Read<uint>(_rxBuffer.AsSpan(offset)) & _offeredCapabilities;
                Activations++;
            }
            offset += (packet.Length + 3) / 4 * 4;
        }

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
        if (_bootAt is double bootAt && bootAt <= end)
        {
            Trace?.Add($"   {bootAt,9:F3} ms: RRF has booted");
            Now = bootAt;
            Boot();
            return true;
        }
        if (_deadline < end || (_deadline == end && _rrfFirstOnTie))
        {
            Trace?.Add($"   {_deadline,9:F3} ms: RRF timeout in {_state}{(_isConnected ? ", disconnect" : "")}");
            int receivedBefore = ReceivedPayloads.Count;
            Now = _deadline;
            RunTask(TransferState.DoingPartialTransfer, true);
            Replay?.Add(("T", $"T {ReplayStatus(receivedBefore)}"));
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
        if (_bootAt is double bootAt && Now >= bootAt)
        {
            Boot();
        }
        if (fault?.RestartBootTime is double bootTime)
        {
            Restart(bootTime, Exchanges - 0.5);
            _restartAgainBootTime = fault.RestartAgain ? bootTime : null;
        }
        else if (fault?.Direction == ExchangeDirection.ToRrf)
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
        string rrfOutput = Convert.ToHexStringLower(readBuffer);
        int receivedBefore = ReceivedPayloads.Count;

        if (fault?.Direction == ExchangeDirection.ToDsf && fault.RestartBootTime is null)
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
        Replay?.Add(($"X {Convert.ToHexStringLower(toRrf)}", $"R {rrfOutput} {ReplayStatus(receivedBefore)}"));
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
