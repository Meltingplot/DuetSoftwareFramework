using System;

namespace DuetSharedLibrary;

/// <summary>
/// Device for full-duplex SPI transfers
/// </summary>
public interface ISpiDevice
{
    /// <summary>
    /// Perform a full-duplex transfer
    /// </summary>
    /// <param name="writeBuffer">Data to write</param>
    /// <param name="readBuffer">Buffer for the data read at the same time</param>
    void TransferFullDuplex(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer);
}
