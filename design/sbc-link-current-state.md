# SBC link: current behaviour (as-is)

Status: derived from the code on 2026-10-08 (DSF `v3.7-dev` at 89ad4a68, RRF fork `3.7-dev`). Nothing
here is a documented specification. It records what the link does today, as the baseline for
[sbc-link-requirements.md](sbc-link-requirements.md).

DSF paths are relative to `src/`. RRF paths refer to the Meltingplot RepRapFirmware fork.

## 1. Transport and framing

| ID | Today the link ... | Source |
|---|---|---|
| L1 | runs over SPI (default) or USB, selected by `CommunicationMethod`. Over SPI the SBC is the master (`/dev/spidev0.0`, mode 0, 8 MHz by default) and RRF the slave. | `DuetControlServer/Settings.cs`, `Link/Extensions.cs` |
| L2 | uses a `TfrRdy` GPIO line (default GPIO 25), driven by RRF. RRF toggles it each time it has armed the next exchange. | `Link/Adapter/SPI.cs` `WaitForTransfer`; RRF `src/SBC/DataTransfer.cpp` `setup_spi` |
| L3 | moves up to 8,192 bytes per direction per transfer. The kernel spidev buffer must be at least that large. | `Link/Protocol/Shared/Consts.cs`, RRF `SbcTransferBufferSize` |
| L4 | splits a transfer into up to four exchanges: a 16-byte header, a 4-byte response, the data, and a 4-byte response. | `SPI.cs` `PerformFullTransfer` |
| L5 | carries requests as packets: an 8-byte header (request, id, length, resend id), padded to 4 bytes. | `Link/Protocol/Shared/PacketHeader.cs`, `SPI.cs` `WritePacket` |
| L6 | identifies itself with format code 0x5F (0x60 means RRF runs standalone) and protocol version 7. | `Consts.cs` |

## 2. Integrity and error handling

| ID | Today the link ... | Source |
|---|---|---|
| E1 | protects header and data with CRC32 (CRC16 below protocol version 4). CRC32 detects every error burst up to 32 bits; other corruption goes undetected with a probability of about 1 in 4 billion. | `SPI.cs` `WriteCRC`, `ExchangeHeader`, `ExchangeData` |
| E2 | resends a failed header or data block, up to `MaxSbcRetries` (3) times per phase. | `SPI.cs` |
| E3 | steers retries with unprotected 4-byte response codes 1–6 and 0xFEFEFEFE. `Success` (1) and `BadProtocolVersion` (3) differ by one bit. | `Link/Protocol/Shared/TransferResponse.cs` |
| E4 | lets RRF ask for a packet to be sent again when it cannot process it yet (`ResendPacket`). DSF keeps its last 3 transmit buffers for this. | `SPI.cs` `ResendPacket` |
| E5 | detects a restarted peer by sequence numbers that each side increments per transfer. | `SPI.cs` `HadReset`; RRF `DataTransfer.h` `IsConnectionReset` |
| E6 | writes a packet that passed the CRC but cannot be parsed to `sys/transferDump.bin`, then stops `LinkService`. | `Link/LinkService.cs`, `SPI.cs` `DumpMalformedPacket` |
| E7 | stops `LinkService` (and so DCS) on any exception other than `OperationCanceledException` in the transfer path: refused response codes, `IOException` from spidev or GPIO. | `SPI.cs` `PerformFullTransfer`, `LinkService.cs` |

## 3. Timing and throughput

| ID | Today the link ... | Source |
|---|---|---|
| T1 | DSF side: waits for `TfrRdy` up to 500 ms within a transfer, 4 s between transfers, and 500 ms when first connecting. | `Settings.cs` `SbcTransferTimeout`, `SbcConnectionTimeout`, `SbcConnectTimeout` |
| T2 | RRF side: waits up to 500 ms within a transfer and 4 s between transfers before treating the SBC as lost. | RRF `src/SBC/SbcMessageFormats.h` |
| T3 | is paced by RRF: transfers at most 25 ms apart, sooner after 4 events. After expression evaluations, variable assignments and macro requests, a 50 ms burst window keeps transfers 2 ms apart. Configurable with M576 S, P, B and D. | RRF `src/SBC/SbcInterface.cpp`; wiki M576 |
| T4 | RRF waits up to 3 s for the SBC to answer a blocking file request, then treats the request as failed. | RRF `SbcMaxRequestTime` |
| T5 | runs the DSF transfer loop on a dedicated highest-priority thread. | `LinkService.cs` `ExecuteAsync` |
| T6 | ran at about 1,700 transfers/s on the lab CHX 350 (observed). | M122 / journal on the lab printer |
| T7 | does not bound the time between full transfers. DSF handles RRF's file requests (open, read, write, close, directory listing, delete, secure delete with `fsync`) and waits for locks (object model, code channels, job, print state) synchronously on the transfer thread. RRF accepts M576 S values up to 4 s. The M122 value "max time between full transfers" can become very large (observed). | `LinkService.cs` `Handle*` methods; RRF `HandleM576` |

## 4. Reaction to link loss

| ID | Today ... | Source |
|---|---|---|
| R1 | DSF, once retries are used up or a `TfrRdy` wait times out: logs "Lost connection to Duet", sets the machine status to Disconnected, clears boards, global variables and sequence counters in the object model, and keeps trying to reconnect. | `SPI.cs`, `Model/ObjectModel.cs` `ConnectionLost` |
| R2 | DSF, after every reconnect: cancels pending codes and macros and closes files opened for RRF ("Connection to controller has been reset"). | `SPI.cs` `_hadReset`, `LinkService.cs` `Invalidate` |
| R3 | RRF (fork), when an established connection is lost by timeout or reset, over SPI or USB: reports "Lost connection to SBC", performs an emergency stop (heaters and motors off, expansion boards reset), drops its SBC resources, and stays stopped until the controller is reset. | RRF `SbcInterface.cpp`, commit 13fa7c2d |

## 5. Safety-relevant behaviour

| ID | Today ... | Source |
|---|---|---|
| S1 | A software emergency stop (M112, or the DWC button) reaches RRF only over the link. If RRF hasn't confirmed it within 4.5 s, DCS shuts itself down ("Halt timed out, stopping DCS"). R3 then stops RRF after its own 4 s timeout. Worst case from M112 to stop: about 8.5 s. | `Codes/Handlers/MCodeHandler.cs` M112, `LinkService.cs` |
| S2 | M999 makes DSF send a reset request. RRF performs an emergency stop, then a software reset, and DCS restarts itself. A 4.5 s timeout applies here too. | `MCodeHandler.cs` M999; RRF `SbcInterface.cpp` |
| S3 | Heater control, heater fault detection and motion control run in RRF on the Duet and keep running without the link. | RRF architecture |
| S4 | The link has no documented safety rating (no performance level or SIL) and was not designed as a safety function. | — |

## 6. Firmware update

| ID | Today ... | Source |
|---|---|---|
| U1 | DSF flashes RRF over the link: the IAP binary in 1,536-byte segments, then the firmware in 2,048-byte segments, verified with CRC16, up to 3 attempts. After that: "Please install it manually". | `LinkService.cs` `PerformFirmwareUpdate`, `Consts.cs` |
| U2 | The update path checks integrity only (CRC16); it does not check where the firmware image came from. | — |
| U3 | `UpdateOnly` mode lets DSF connect to firmware with a newer protocol version so that it can flash it. | `SPI.cs` `ExchangeHeader` |

## 7. Compatibility

| ID | Today ... | Source |
|---|---|---|
| K1 | DSF adapts to older RRF protocol versions (CRC16 below 4, the old pause format below 7) and refuses version 1. Any version mismatch logs "Incompatible firmware, please upgrade as soon as possible". | `SPI.cs`, `LinkService.cs` |
| K2 | The USB transport is started with M576.1 (in the RRF source, not in the wiki). It checks the protocol version in the M576.1 reply, has no CRC of its own (it relies on USB's), and reconnects after 250 ms. | `Link/Adapter/USB.cs`; RRF `SbcInterface.cpp` `HandleM576` |

## 8. Diagnostics and user feedback

| ID | Today ... | Source |
|---|---|---|
| D1 | The DSF section of M122 shows: SPI speed, `TfrRdy` glitches and missed edges, full transfers/s, the longest time between transfers, the longest pin waits, codes/s, and the largest RX/TX sizes. | `SPI.cs` `PrintDiagnostics` |
| D2 | The RRF "SBC interface" section of M122 shows: transfer state, failed transfers, checksum errors, RX/TX sequence numbers, SPI underruns and overruns, disconnects, timeouts (total and caused by the SBC), buffer state and open files. | RRF `DataTransfer.cpp`, `SbcInterface.cpp` `Diagnostics` |
| D3 | DSF messages: "Lost connection to Duet (…)", "Connection to Duet established", "Connection to controller has been reset", "Incompatible firmware, please upgrade as soon as possible", "Did not get enough CPU time during SPI transfer, your SBC may be overloaded". RRF messages: "Lost connection to SBC (timeout / connection reset)", "Connection to SBC established over SPI!". | `SPI.cs`, `LinkService.cs`; RRF `SbcInterface.cpp` |
| D4 | While the link is down, DWC shows the status Disconnected and loses board and global-variable data until the link reconnects. | `ObjectModel.cs` `ConnectionLost` |

## 9. Not covered today

- No requirement for EMC immunity or for recovery time. Robustness comes only from the CRC plus up to 3
  retries per phase.
- No recovery from a phase desync other than the timeouts in T1 and T2, which lead to R1–R3.
- No distinction between "the peer restarted" and "the link was disturbed": both lead to R2 and R3.
