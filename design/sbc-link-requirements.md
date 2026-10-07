# SBC link robustness: requirements

Status: agreed on 2026-10-07, not implemented yet.
Scope: the SPI link between Duet Control Server (DCS) on the SBC and RepRapFirmware (RRF), on the
Meltingplot fork of DSF and RRF.

## Background

DCS is the SPI master and RRF the slave. RRF signals with the `TfrRdy` GPIO line when it is ready for
the next exchange. A transfer consists of up to four exchanges (header, header response, data, data
response); header and data are checked with CRC32 and resent on error. This is protocol version 7,
described in [Firmware link](../src/Documentation/articles/firmware-link.md).

On this fork, any loss of the link ends the running job. RRF calls `EmergencyStop()` when an
established connection is lost (RRF fork commit 13fa7c2d), and after a reconnect DCS discards pending
codes and closes files (`LinkService.Invalidate()`).

Today a single fault is enough to get there:

- **Response codes differ by a single bit:** `Success` = 1, `BadProtocolVersion` = 3. DCS answers
  "refused" codes with a plain `Exception`, which escapes `PerformFullTransfer` (it only catches
  `OperationCanceledException`) and stops `LinkService`. An `IOException` from `SpiDevice` or
  `InputGpioPin` does the same.
- **Header used before it is checked:** DCS adopts the received protocol version before verifying
  the header CRC.
- **Phase desync becomes a reset:** a missed or ghost `TfrRdy` edge, or a chip-select glitch, leaves
  the two sides one exchange apart. Only a 500 ms timeout resolves that, and the timeout ends the job.
- **Unnecessary invalidation:** DCS invalidates pending work after every timeout recovery, even when
  RRF kept its state.

Code: [`Link/Adapter/SPI.cs`](../src/DuetControlServer/Link/Adapter/SPI.cs),
[`Link/LinkService.cs`](../src/DuetControlServer/Link/LinkService.cs),
[`DuetSharedLibrary/InputGpioPin.cs`](../src/DuetSharedLibrary/InputGpioPin.cs); RRF
`src/SBC/DataTransfer.cpp` and `src/SBC/SbcInterface.cpp`.

## Vision

> As long as the Duet and the SBC are both running, nothing that happens on the link may reset it or
> end the job. A disturbance may cost time, never the job.

## Requirements

| ID | Requirement |
|---|---|
| R1 | **Never reset while both ends are alive.** The link session may be reset only when one side has provably lost its state (restart, crash, reflash), or under R8. It is never reset because of a missed or corrupted transfer, a failed checksum, a phase shift, a missing, extra or stuck `TfrRdy` edge, or a chip-select or clock glitch. |
| R2 | **Recover automatically, without losing anything.** Any single fault is repaired within 500 ms, typically within a few milliseconds. No data (codes, replies, file data, object model) is lost or duplicated in either direction. |
| R3 | **Survive EMI and ESD events.** Under ESD (IEC 61000-4-2) and fast transient bursts (IEC 61000-4-4), the link may pause briefly but the job continues: performance criterion B, meaning temporary degradation with automatic recovery and no operator action. |
| R4 | **Almost no cost on a clean link.** Without disturbances, codes/s and transfers/s (as reported by M122) stay within 2% of protocol version 7. |
| R5 | **No fragile timing.** Correctness must not depend on timings measured on a particular SBC, kernel or Pi model. Timing values may only affect speed. |
| R6 | **Real restarts are still caught.** When either side restarts, DCS discards pending work and RRF performs an emergency stop, as today. |
| R7 | **Disturbances are visible.** Every repaired disturbance is counted and shown in M122. |
| R8 | **Total silence.** If nothing at all gets through for 30 s (configurable), RRF performs an emergency stop. This is the only reset triggered by a timeout. |

## Compatibility

| ID | Requirement |
|---|---|
| C1 | **Fork pairs only.** Fork DSF and fork RRF use protocol version 8. A mismatched pair does not run jobs. |
| C2 | **Updating mismatched firmware.** DCS still connects to version 7 firmware, but only to flash the matching fork firmware (update-only mode). A version 8 update path may follow later as an opt-in. |
| C3 | **Capability negotiation.** Within version 8, both ends agree on the features they support, so boards with fewer features (Mini 5+) still connect. |
| C4 | *Proposed, not yet confirmed:* an upstream RRF that also reports version 8 is recognised as a mismatch through the capability handshake. It is never misread as the fork protocol. |

## Scope

- **Boards:** R1–R8 apply to the SAME70 boards (MB6HC, MB6XD). The Mini 5+ (SAME5x) connects through
  C3 with the features it supports but does not have to meet R1–R8.
- **SBCs:** *proposed:* any SBC that DSF runs on (R5 already requires this); tested on Raspberry Pi 4
  and Pi 5.
- **Out of scope:**
  - ESD that resets or latches up a processor. That is hardware immunity; the protocol only has to
    detect the restart (R6).
  - SBC over USB.
  - The firmware-flashing path, for now (see C2).
- **Upstream:** whether to offer this to Duet3D is decided later. The design stays self-contained so
  that remains possible.

## Later milestone: dream state

| ID | Goal |
|---|---|
| D1 | **Normal speed under constant noise.** Under sustained EMC/EMI (for example a spindle VFD, or motor cables next to the ribbon cable), the link adapts transfer size and SPI clock by itself so the job runs at normal speed, and returns to full speed when the noise stops. Built after R1–R8, from real noise measurements. |

## Means, not goals

Error-correcting codes (ECC/FEC) are a tool, not a requirement. They are used only if R1–R8 or D1
cannot be met without them.

## Acceptance tests

| Req. | Test | Pass when |
|---|---|---|
| R1, R2 | Inject every single fault at every point of the protocol, in a simulator and on the bench | No reset, nothing lost or duplicated, recovery within 500 ms |
| R1, R2 | During a print, interrupt any one line (SCK, MOSI, MISO, chip-select, `TfrRdy`) for 1 ms up to just below 30 s | The print continues once the line is back |
| R3 | ESD and burst tests during a print | The print finishes |
| R4 | Clean link, M122 codes/s and transfers/s | Within 2% of version 7 |
| R5 | Simulator with random edge merging and interrupt latency | Same results at any latency |
| R6 | Restart DCS; reboot the Duet | Each is detected as a reset |
| R8 | Interrupt a line for more than 30 s | RRF performs an emergency stop |
| C2 | Stock RRF with fork DSF | Update-only mode, flashing works |
| C3 | Mini 5+ with fork DSF and fork RRF | Connects with its reduced feature set |
| D1 | Continuous bursts on the cable during a print | The print finishes at normal speed |

## Decision log

| Date | Decision | Reason |
|---|---|---|
| 2026-10-07 | No ECC unless required | On a clean link, resending is cheaper than always-on error correction. Robustness comes from removing the reset paths, not from correcting bits. |
| 2026-10-07 | No timing constants measured per SBC | A minimum edge spacing measured on Pi 4/Pi 5 could break with any kernel or hardware change. Timing may only affect speed (R5). |
| 2026-10-07 | Protocol version 8, fork pairs only; version 7 kept for flashing | Simpler than running both protocols in normal operation; C3 handles boards with fewer features. |
| 2026-10-07 | Emergency stop after 30 s of total silence | To RRF a dead SBC and a cut wire look the same. 30 s is far longer than any repair, so it fires only on a real fault. |
| 2026-10-07 | R1–R8 required on SAME70 boards only | The Mini 5+ needs extra verification (SERCOM realignment, CPU headroom). |
| 2026-10-07 | Upstream adoption decided later | |
