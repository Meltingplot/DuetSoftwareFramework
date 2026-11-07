# Findings against upstream DSF and RRF

Status: 2026-11-07, written for Christian Hammacher (Duet3D). First version 2026-11-02.

What was tested:
- DSF `v3.7-dev` at 1f72a780.
- RRF `3.7-dev` at 231daaac87.

The code is still the same on 2026-11-07:
- DSF `v3.7-dev` at 973838bf has no change since 1f72a780 in `src/DuetControlServer/Link`, `Model/UpdateService.cs`, `Model/ObjectModel.cs`, `Utility/FirmwareUpdater.cs`, `Codes/Handlers/MCodeHandler.cs` and `DuetAPI/ObjectModel/Boards`.
- RRF `3.7-dev` at 25ec27aec3 has no change in `src/SBC` since 231daaac87.

The line numbers below therefore still apply.

The Meltingplot fork has fixes and a negotiated extension for most of these findings (last section). Everything below is about upstream behaviour. Line numbers refer to the upstream commits above.

## How we tested

- **SPI link census** (`src/UnitTests/SPI/LinkRecovery.cs` in the fork):
  - It runs the real DSF SPI adapter against a C# port of RRF's `DataTransfer` and `SbcInterface` (`FakeRrfSpi.cs`), in virtual time.
  - It injects exactly one fault per run, 4142 faults in all: every bit of every exchange, special values of each field, whole-word patterns, both directions, in transfers with data on both sides, one side or none.
  - The port was checked against a host build of the real `DataTransfer.cpp` and `SbcInterface.cpp`: 0 differences over 4189 replayed cases.
- **Upstream applicability:**
  - `DataTransfer.cpp`, `DataTransfer.h` and `SbcMessageFormats.h` of the port's base are identical to upstream `3.7-dev`.
  - `SbcInterface.cpp` differs only by the fork's emergency stop on a lost connection and a mutex around message output.
  - On the DSF side, the census was run with upstream's `SPI.cs`, changed only to take the SPI device and the TfrRdy pin as interfaces.
- **Pass criterion:** the link must recover through the protocol's own retries. A connection reset counts as a failure, because DSF then invalidates everything pending (`LinkService.Invalidate`). So does a stop of DCS.
- **Hardware:** a Duet 3 MB6HC with a Pi 5 (lab printer). A test build of the fork's DSF injected single faults on the real link:
  - 2026-11-02, RRF 3.7.0+1.mp.5 (fork, same SPI code as upstream), 526 cases: every outcome matched the census prediction for that combination, including the livelocks of F3.
  - 2026-11-06, RRF 3.7.0+1.mp.7 with the fork's link capabilities active: 672 of 672 injected faults and 15 of 15 replayed ESD events were repaired by retries.
- **Object model (O1):** found on the lab printer and reproduced with the fork's fake firmware host, which runs the real `LinkService` and `UpdateService` against a fake RRF (`src/UnitTests/FakeFirmware`).

## Summary

| | Breaks the link |
|---|---|
| Single faults, 4142 | **125**: DCS stops 20×, DCS has to restart 7×, livelock 16×, connection reset 82× |
| Single-bit errors, transfer without data | **34 of 320 bits**, all in the 4-byte header response; all 32 bits of the response DSF sends |
| Single-bit errors, transfer with data | 1–3 of 720–768 bits, all in the header response |
| RRF restarts, 128 | 6 unnoticed by DSF |
| DCS restarts, 42 | 2 need a second start of DCS |
| Single faults of the hardware case set, 3234 (F9) | 661 are repaired but leave TfrRdy low, so a DCS that starts right after one of them fails its first start |
| RRF watchdog resets on the MB6HC (F10) | about 40 % take 4 s longer to recover |

Other findings that do not break the link:
- **F11:** a data CRC error seen only by DSF restarts the whole transfer. The census has 2416 full transfer restarts, 1831 with a data retry.
- **O1:** after an RRF reset, DSF can lose the firmware and IAP file names of the main board, so M997 and `DuetControlServer -u` cannot update it. This happened in 2 of 5 resets in the fake firmware host.

With polling while disconnected (`SUPPORTS_SBC_OVER_USB`) the counts are almost the same: one livelock fewer, one DCS restart more.

**Risk per transfer** ≈ bit error rate × number of fatal bit positions. That is about 34 for an empty transfer, so empty transfers dominate. Every failing bit sits in a 4-byte response word, which has no checksum.

## Findings in the SPI link

### F1. One bit turns Success into a refusal, and DCS stops (20 stops, 5 DCS restarts at connect)

- **Why one bit is enough:** the response codes are one bit apart (`TransferResponse.cs:14–44`): Success 1, BadFormat 2, BadProtocolVersion 3, BadDataLength 4.
- **What happens:** when the response to DSF's header has bit 1 flipped (1 → 3), or another single bit lands on a refusal code, `SPI.ExchangeHeader` throws "RepRapFirmware refused …" (`SPI.cs:1757–1762`). `LinkService` stops, and so does DCS.
- **Fork fix:** DSF-only. The refusal codes take the default path, which restarts the transfer (DSF #91).

### F2. An acknowledgement seen by one side only resets the connection (most of the 82 resets)

The most frequent case, which accounts for all 32 bits above:
1. In a transfer without data, any bit error in the header response that DSF sends makes RRF read something other than Success.
2. RRF takes `RestartTransfer(true)`. With no data it assumes "still in sync", sets `lastTransferNumber = rxHeader.sequenceNumber - 1` and exchanges the header again (`DataTransfer.cpp:859`), so it expects DSF to repeat the transfer.
3. DSF received Success, so it moves on with the next sequence number.
4. `IsConnectionReset` (`DataTransfer.h:192–196`) then sees a mismatch, RRF resets the connection, and DSF reports "Connection to controller has been reset".

The same one-sided outcome happens in the data response, when only one side sees `BadDataChecksum` or `BadResponse`.

**Fork fix:** DSF-only, and it works with stock RRF (DSF #92). When RRF repeats the sequence number DSF saw last, DSF sends its last completed transfer again instead of treating it as a reset. Number 1 is excluded, because every new RRF connection starts at 1, so it may come from a restart.

### F3. Phase desync without any timeout: livelock (16)

- **What happens:**
  1. DSF is in the header phase while RRF is in the data phase, for example after a corrupted first word.
  2. DSF reads RRF's data as a header and adopts its protocol version before it checks the header CRC (`SPI.cs:1663` before `SPI.cs:1675`), here version 0.
  3. DSF switches to 12-byte headers and CRC16, while RRF keeps answering `BadDataChecksum`.
- **Why it never ends:** the exchanges never stop, so neither side's 500 ms or 4 s timeout fires. The link stays stuck until DCS is restarted.
- **On hardware:** seen on the MB6HC as expected: "Bad header CRC16 (expected 0x0000, got 0x85d1)", "RepRapFirmware didn't receive valid data either (code 0x00000006)", and about 600,000 suppressed journal lines.
- **Journal flood:** while the link is stuck, DSF logs every retry as a warning. On the MB6HC that was about 20,000 lines per second: `Bad header CRC16`, `Note: RepRapFirmware didn't receive valid data either`, `Restarting full transfer because the number of maximum retries has been exceeded`. journald with a burst limit of 2000 per 30 s suppressed 1.2 million lines in 60 s (2026-11-07).
- **Also the cause of** the 2 DCS restarts that need a second start: the first header of the new DCS lands in RRF's data phase.
- **Fork:**
  - RRF phase guards handle it, but they are only switched on by negotiation. A DSF-only attempt, checking the CRC before adopting the version, broke more cases than it fixed when we tried it.
  - Where the guards cannot help yet (the transfer that negotiates them), DSF #102 ends the livelock. DCS stops when no transfer has completed for 60 s, systemd starts it again, and RRF times out meanwhile.
  - On the MB6HC with a fault injected into that transfer, DCS stopped 60.03 s after the fault in both runs, and the link came back (2026-11-07).

### F4. An exchange replaced by the exact BadResponse word

0xFEFEFEFE in place of a header, data or a response sends the two sides down different paths. This accounts for most of the remaining resets and livelocks in the census. As random corruption it is very unlikely, because it needs the exact 32-bit pattern.

### F5. Deadlock at connect (1, only without polling while disconnected)

- While not connected, RRF waits for the next transfer without a timeout (`SbcInterface.cpp:165`, `TaskBase::TimeoutUnlimited`), with TfrRdy low.
- A newly started DCS waits for TfrRdy to go high.
- Builds that poll while disconnected recover; others need a reset.

### F6. Restarts detected only through sequence numbers (6 of 128 RRF restarts unnoticed)

- **When it happens:** a restarted RRF numbers its transfers from 1 (`DataTransfer` constructor, `ResetConnection(true)`, `StartNextTransfer`). If DSF happens to expect exactly 1 next, right after the 16-bit wrap, the restart is not noticed.
- **What gets lost:** in these cases the transfer DSF had in flight even completes across the reboot.
  1. The freshly booted RRF reads DSF's `BadResponse` in its header phase as a request to retry the data response (`DataTransfer.cpp:921`).
  2. It answers Success (`DataTransfer.cpp:872–874`).
  3. DSF's packets in that transfer are lost without either side noticing.
- **Probability:** about 1 in 65536 per restart.
- **Fork:** the negotiated extended header carries a session status, so a restart shows at the first header.

### F7. Packets read past the end of the transfer

- `ReadNextPacket` reads packet headers and data from the whole receive buffer, not only the bytes of the current transfer (`SPI.cs:378`, `USB.cs:439–442`).
- A packet whose length runs past the data of this transfer is therefore assembled from leftover bytes of an earlier one.
- **Fork fix:** DSF #89. Read only within the data of the current transfer, so such a packet is dumped as malformed.

### F8. After a lost connection, the corrected protocol version never reaches RRF

- After a lost connection, DSF resets `_txHeader.ProtocolVersion` (`SPI.cs:332`) but does not rewrite the header buffer or its CRC.
- If the version had been adopted from a bad header (F3), every retry resends that header and RRF rejects it with `BadHeaderChecksum` indefinitely.
- **On the lab printer** after watchdog resets (M122 P1001): the link did not recover in 4 of 5 restarts, logging 6,000–10,000 "bad header checksum" lines.
- **Fork fix:** DSF #59. Rewrite the header after resetting the version.

### F9. A recovered fault can leave TfrRdy low, and a new DCS then fails its first start

- **The parity:** RRF toggles TfrRdy for every exchange it arms (`DataTransfer.cpp:353–354`). Only `ResetConnection(true)` sets the level back (`DataTransfer.cpp:1232–1235`). A recovery that arms an odd number of extra exchanges leaves RRF waiting at its next header with TfrRdy low.
- **Why only a new DCS fails:** a running DSF follows the parity, so it does not notice. A newly started DCS expects TfrRdy high (`SPI.cs:1525–1537`) and gives up after `SbcConnectTimeout`, 500 ms (`Settings.cs:173`). RRF resets the connection only after its 4 s timeout.
- **Census:** 661 of the 3234 single faults of the hardware case set are repaired by the protocol but leave the idle header with TfrRdy low.
- **On the MB6HC** (2026-11-02):
  - After one such fault, DCS was stopped and the pin read 0. The new DCS logged "Connection timeout while waiting for TfrRdy pin (waited 500ms, 0 glitches)".
  - After none or two such faults, the pin read 1 and the new DCS connected.
- **Fork fix:** DSF #99. While a connection is made for the first time and TfrRdy is low, DSF waits up to RRF's connection timeout plus its transfer timeout. On the MB6HC the new DCS then connected after 3.96 s (2026-11-03) and after 3.84 s and 3.88 s with the link capabilities active (2026-11-07).

### F10. After an RRF reset, DSF exchanges in the boot window, and both sides wait for a timeout

- **The boot:** during the reset TfrRdy keeps its level. `DataTransfer::Init` drives it low (`DataTransfer.cpp:451`), and the SBC task arms the first header right after `InitFromTask` (`SbcInterface.cpp:120–121`).
- **The race:** DSF takes the falling edge as the signal for its pending exchange.
  - If its 4-byte `BadResponse` reaches the first header, RRF arms a data response retry with TfrRdy low, while DSF waits for a header with TfrRdy high. RRF's 500 ms transfer timeout ends that.
  - If the exchange comes just before the first header, the idle header ends with the wrong parity (F9). RRF's 4 s connection timeout then resets the connection a second time.
- **On the MB6HC** (2026-11-03), recorded with a DSF build that logs the TfrRdy edges:
  - After `M122 P1001`, MISO read 0xFF for about 285 ms. Then the falling and the rising edge came 20–30 µs apart.
  - Watchdog recoveries took 12.2–16.8 s, about 40 % of them near 16 s.
- **Fork fix:** RRF Meltingplot/RepRapFirmware#36, a 10 ms delay between `InitFromTask` and the first `StartNextTransfer` (`SbcBootArmDelay`). DSF's reaction then reaches the still disabled SPI. With it, all 15 restarts recovered in 11.8–12.2 s with one reset each.

### F11. A data CRC error seen only by DSF restarts the whole transfer

- **What happens:**
  1. When DSF finds a bad data CRC32, it answers `BadDataChecksum` (`SPI.cs:1830–1833`).
  2. If RRF received DSF's data fine, it answers Success.
  3. DSF treats that as an unexpected code: it sends `BadResponse` and restarts the full transfer (`SPI.cs:1838–1850`), although exchanging the data again would do.
- **On the lab printer** this explained 4 of 5 CRC events during an ESD test (2026-11-04): one flipped bit in RRF's data to DSF, then a full restart.
- **Census:** 2416 full transfer restarts over the single faults with stock RRF, 1831 with a data retry. No outcome changes.
- **Fork fix:** DSF #105. After its own bad data CRC32, DSF exchanges the data again when RRF answers Success or `BadDataChecksum`.

## Findings in the object model

### O1. After an RRF reset, DSF can lose the firmware and IAP file names of the main board

- **Background:**
  - Since RRF 26fba8969e (2026-09-28), `boards[0].firmwareDate`, `firmwareFileName`, `iapFileNameSBC` and `iapFileNameSD` are verbose (`Platform.cpp:220–229`).
  - DSF reads verbose fields only in the first query of a connection or while a subscriber asks for them (`UpdateService.cs:252`).
- **The race on a reset:**
  1. `LinkService.Invalidate` first cancels the pending object model query (`LinkService.cs:1432`, `LinkInterface.cs:645`).
  2. Only then it calls `ObjectModel.ConnectionLost` (`LinkService.cs:1442`), which clears the sequence numbers (`ObjectModel.cs:869`).
  3. Meanwhile the cancelled query continues on the thread pool (`ModelQueryRequest.cs:30`, `UpdateService.cs:374`). The next update can therefore start before the sequence numbers are cleared.
  4. It then reads every key without the verbose flag. The four fields stay empty until DCS restarts.
- **Effect:** M997 refuses with "Cannot update firmware because IAP and firmware filenames are unknown" (`MCodeHandler.cs:1134`). `DuetControlServer -u` next to a running DCS skips the main board (`FirmwareUpdater.cs:282`).
- **On the lab printer** (2026-11-07): after a test run with six watchdog resets, the running DCS reported all four fields empty. A freshly started DCS had them.
- **Reproduced:** in the fork's fake firmware host the names were lost in 2 of 5 resets. Holding the model lock over the reset loses them every time.
- **Fork fix:** branch `fix/verbose-board-fields-after-reset`. `ConnectionLost` requests a resync, which the update service performs before its next query, as it already does for a new verbose subscriber (`UpdateService.cs:238–246`). That leaves the update service as the only writer of the sequence numbers.

## Findings in the parsers and the packet reader (fuzz tests of DSF)

| | Finding | Upstream code | Fork fix |
|---|---|---|---|
| P1 | `Code.Parse` loses the first character of a following code on a line with several codes, so file info fails for a footer with such a line. It reads the character instead of peeking at it, which a `TextReader` cannot undo. Regression since 721e5df0. | `DuetAPI/Commands/Code/Parser.cs` | DSF #84 |
| P2 | An array evaluation result with a malformed item count allocates the array before checking the remaining packet data, which can mean huge allocations. A malformed `DateTime` throws a `FormatException` that skips the packet dump. | `DuetControlServer/Link/Protocol/Reader.cs` | DSF #85 |
| P3 | `Code.Length` is one byte too long for a code followed by another on the same line, and for the last code of a file without a final newline. `JobProcessor` uses `FilePosition + Length` as the pause position when RRF reports none. | `Parser.cs`, `ParserAsync.cs` | DSF #86 (first part; the second part concerns fork-only checksum code) |

## What the fork does about it

**Fixes that need no RRF change** (they work with stock RRF):
- DSF #59 (F8), #89 (F7), #91 (F1), #92 (F2, and part of F3).
- DSF #84, #85, #86 (P1–P3).
- DSF #99 (F9), #102 (F3, the livelock ends with a DCS restart after 60 s) and #105 (F11).
- DSF `fix/verbose-board-fields-after-reset` (O1), not merged yet.
- DSF #98 raises the default of `MaxSbcRetries` from 3 to 10. Under random bit errors in both directions, with the link capabilities active, the retry limit was the only source of resets left in the census.
- DSF #103 adds recovery counters to DSF's M122: full transfer restarts, header and data retries, transfers sent and requested again, extended link left by RRF, lost connections and connection resets.

With the SPI fixes, the census goes from 125 to 35 single faults that break the link.

**A fix that needs RRF only:** RRF Meltingplot/RepRapFirmware#36 (F10).

**Fixes that need both sides,** RRF Meltingplot/RepRapFirmware#35 and DSF #93, switched on only per connection by a capability handshake:
- RRF offers capabilities in `limits.sbcLinkCapabilities`, a verbose key; stock RRF reports `null`.
- DSF activates them with a new request (0x4D50).
- They end with any reset.

The capabilities:
- phase guards in RRF (F3, F4);
- an RRF sequence guard (F2);
- an extended header, which carries a 5-bit sequence counter with an 11-bit session status (F6) and response codes that are 16 bits apart, with up to 3 flipped bits corrected (F1, F2, F4).

**Results with the handshake:**
- In the census, no single fault after the activation breaks the link, and every RRF and DCS restart is noticed.
- On the MB6HC, 678 injected faults were all repaired by retries.
- Mixed versions behave like today.

The extended link had one restart case of its own, found on the lab printer on 2026-11-07. The rebooted RRF did not decode DSF's extended `BadResponse` in its first header, so both sides ran one exchange apart until DSF lost the connection. The fix is in RRF (Meltingplot/RepRapFirmware branch `fix/sbc-decode-header-unconnected`). The regular link does not have this case.

Details are in `design/sbc-link-requirements.md`.

## Reproducing

- Census: `dotnet test src/UnitTests --filter "Name=SingleCorruptedExchange"`, plus `RrfRestart` and `DcsRestart_RrfOfToday`. These are explicit tests in the fork.
- `TraceCase` prints the exchanges of a single case.
- Port check against the real RRF code: `PortMatchesRealFirmware`, with a host build of `DataTransfer.cpp` and `SbcInterface.cpp` (`RRF_HOST_HARNESS`).
- F9: `DcsStartsAfterRecoveredFault`. F10: `ExtendedLinkNoticesEveryRebootQuickly`, with the fork's boot delay. F11: `BadDataOnlyDsfGotIsExchangedAgain`. O1: `VerboseFieldsAfterReset` (branch `fix/verbose-board-fields-after-reset`).
- Hardware: `src/LabPrinterTests/README.md`, section "SPI fault injection".
