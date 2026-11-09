# SBC link v7: catalogue of ordering catch code and edge cases

Input for the v8 requirements document. Research only, no code was changed.

## 0. Basis and conventions

- **DSF:** Meltingplot `v3.7-dev` at 808d56fa. `DCS/` = `src/DuetControlServer/`. PR branch read: `origin/fix/fake-firmware-reply-after-invalidate` (#163, since merged).
- **RRF:** the submodule `firmware/RepRapFirmware` at 0dccb9efe = fork 3.7-dev 9678339c8 + Meltingplot/RepRapFirmware#46 (since merged; same tree as fork 3.7-dev ddec2d001, the pin since #164). `RRF/` = its `src/`.
- **Upstream check:**
  - DSF: `git merge-base --is-ancestor <commit> upstream/v3.7-dev`, local ref 973838bf (2026-11-03, the ref the design docs use).
  - RRF: Duet3D `3.7-dev` fetched into FETCH_HEAD = 25ec27aec (2026-11-06).
  - For RRF I also diffed the fork against FETCH_HEAD (`git diff FETCH_HEAD HEAD -- src/SBC src/GCodes src/Platform/RepRap.cpp`). Code that blame puts on a fork commit but that is identical to upstream counts as upstream content (e.g. the drains of f2e44bcc9).
- **Origin labels:**
  - "upstream" = the blamed commit is in upstream.
  - "fork" = it is not.
  - "fork→upstream" = a fork commit that Duet3D accepted (it is an ancestor of the upstream head).
- **PR numbers:** taken from the first merge commit on the ancestry path. "RRF #n" = Meltingplot/RepRapFirmware#n.
- **Line numbers:** at the commits above. They differ from the line numbers in the design docs, which cite older commits.
- **Statement status:**
  - "Printer" = seen on the lab printer, with the doc that records it.
  - "Code" = from reading the code, not tested.
  - "Open" = could not be settled.
- **Layers:**
  - **L** = link/transport (framing, exchanges, CRC, transfer sequence numbers).
  - **P** = protocol/session (multiplexing, correlation, flow control, abort, reset).
  - **G** = logic (G-code state, macro stack, code pipeline).
  - Tim's constraint: G should change as little as possible.

## 1. Summary

| | L | P | G | Total |
|---|---|---|---|---|
| Catch-code items in the catalogue (sections 4–11) | 6 | 28 | 13 | 47 |
| of which fork-only code | 4 | 11 | 2 | 17 |

How items were counted:
- Each row in the "Catch-code index" (section 12) is one item. Pure edge cases without catch code (open findings) are listed but not counted.
- An item can span DSF and RRF. Its layer is the one the catch code belongs to, not the one where the symptom appears.

The five most important ordering assumptions:
1. **Replies are matched to codes by position.** `BufferedCodes[0]` of the channel takes the next reply, macro request, message prompt, single-file abort or closal (`DCS/Link/Channel/Processor.cs:1108-1117`, `:1264-1271`, `:1383-1388`, `:655-665`).
   - Every catch code in sections 4 and 5 exists so that the n-th event of a channel still belongs to the n-th code: drains, the M99 and dummy replies, codeReplySurplus, PendingReplies, Unbuffered M122/M409.
2. **The pipeline depth is exactly one transfer on both sides.**
   - RRF writes the data of transfer N+1 after it has read DSF's packets of transfer N (`RRF/SBC/SbcInterface.cpp:257-268`, `:310-1359` before `:1389-1643`).
   - So RRF's data of transfer N was written before RRF read DSF's packets of N.
   - The abort windows (#134, #158, #161), the halt completion (#88), `BytesReserved`, the ResendPacket buffer index and RRF's one-ExchangeData read-ahead invalidation (B7) count transfers instead of acknowledging identities.
3. **Abort (InvalidateChannel) has no acknowledgement and no epoch.**
   - Everything RRF wrote before it read the abort is still delivered, and DSF can tell it apart from post-abort data only by counting transfers.
   - RRF closes files silently (`FileAbortSent`, `SbcInterface.cpp:903`), so DSF and RRF must drop the same things by convention. Items: T1, T2, T10, T11, T12, T13.
4. **RRF uses "ResendPacket" both as NACK and as back-pressure.**
   - RRF goes on processing the later packets of the same transfer (`SbcInterface.cpp:1354-1358`), so a refused request is overtaken by the later packets of its own transfer, and by the whole next transfer that DSF wrote before it saw the refusal.
   - The upstream `_packetsBeingResent` guard covers only the transfer that carries the copy (`DCS/Link/Adapter/SPI.cs:305`, `:841-845`).
   - Requests that need the channel mutex are refused after 10 ms (`SbcYieldTimeout`, `SbcInterface.cpp:45`). The main task holds that mutex for a whole spin, including an M98 macro wait (`RRF/GCodes/GCodes.cpp:590`). Item: T11.
5. **One shared resource per direction, without per-channel credit.**
   - One 4 KB code ring for all channels (`SbcInterface.cpp:76`, `SbcMessageFormats.h:47`).
   - One 64-entry reply FIFO for all channels (`SbcInterface.h:155`, `OutputMemory.h:15-19`).
   - One file-operation slot (`SbcInterface.h:110-153`).
   - Emergency stop and reset go out as packets inside the same lock-step transfer, paced by RRF (up to 25 ms, `SbcInterface.cpp:1361-1371`). An M112 in a macro first waits for a flush of the macro's codes (`DCS/Codes/Handlers/MCodeHandler.cs:750`).
   - Items: T3, R10, E2.

New observations from this research (code reading, not tested):
- **NEW-1.** The USB adapter of DSF resolves ResendPacket in the TX buffer of the transfer just completed (`DCS/Link/Adapter/USB.cs:717-721`). RRF's USB transport, however, sends data that it composed after the previous transfer (`RRF/SBC/DataTransfer.cpp:1303-1357`, `:1362-1371`), exactly like SPI.
  - If the RRF code is read correctly, the premise of T13 is wrong.
  - The USB resend then copies the wrong packet, or throws `ArgumentException`, which `LinkService` does not catch (`LinkService.cs:446`) and which stops DCS.
- **NEW-2.** RRF's single reply FIFO drops a reply silently when it is full: `OutputStack::Push` releases the buffer and logs `OutputStackOverflow` (`RRF/Platform/OutputMemory.cpp:487-506`). DSF matches replies by position, so the following replies of that channel shift by one. Not observed.
- **NEW-3.** A code that RRF refuses for lack of buffer space can be overtaken by a later code of the same channel. DSF writes that later code into the next transfer before it sees the refusal, and RRF may accept it (`SbcInterface.cpp:313`, `:394-414`; `SPI.cs:305`, `:900`). Reachability depends on RRF refusing a code that DSF's estimate let through. Not shown.

## 2. Timing model of v7 (as built)

### 2.1 What a transfer is

**SPI.** One transfer = up to four exchanges, each armed by RRF with a TfrRdy toggle (`RRF/SBC/DataTransfer.cpp:353`):
1. a 16-byte header (`SpiTransferHeader`, `SbcMessageFormats.h:115-124`);
2. a 4-byte header response;
3. the data, if either side has data;
4. a 4-byte data response.

Details:
- Up to 8192 bytes per direction (`SbcMessageFormats.h:26`).
- Full duplex: both sides send their data in the same exchange.
- Headers carry a per-transfer sequence number. Restart detection is a sequence distance other than 1:
  - DSF: `SPI.HadReset`, `DCS/Link/Adapter/SPI.cs:485-495`.
  - RRF: `IsConnectionReset`, `RRF/SBC/DataTransfer.h:212-215`.
- The fork's extended header splits it into a 5-bit counter and an 11-bit session status (`SbcMessageFormats.h:147-149`, design/sbc-link-requirements.md "Capabilities").

**USB.**
- DSF writes its header, reads RRF's header, writes its data, reads RRF's data (`USB.cs:1678-1688`, `:1735-1750`).
- RRF reads DSF's header, writes its own, reads DSF's data, writes its own (`DataTransfer.cpp:1303-1357`).
- There is no CRC, no response word and no sequence number (`UsbTransferHeader.cs` doc comment, `SbcMessageFormats.h:178-184`).
- A reset is detected only by a reconnect (`USB.cs:257-280`, `:414-419`).

**Packets.**
- 8-byte header: request, id, length, resendPacketId (`PacketHeader.cs`, `SbcMessageFormats.h:101-107`), padded to 4 bytes.
- Packet ids restart at 0 in every transfer (DSF `SPI.cs:439`, `USB.cs:361`; RRF `DataTransfer.cpp:1159`, `:1213` after a completed SPI transfer, `:1354` after a USB transfer, ids assigned at `:2298`).
- There is no channel-level or request-level identifier.

### 2.2 Order of processing per transfer

**RRF SBC task, one loop** (`RRF/SBC/SbcInterface.cpp:152-288`):
1. `DoTransfer` until finished.
2. `ExchangeData()` (`:267`).
3. `StartNextTransfer()` (`:268`), which arms the header with the TX data composed in step 2.

**RRF `ExchangeData` order** (`SbcInterface.cpp:310-1644`):
1. Process every DSF packet of the transfer just finished, in order (`:314-1359`).
   - A packet that cannot be processed gets a ResendPacket header written into the TX buffer (`:1354-1358`, `DataTransfer.h:238-241`).
   - Processing then continues with the next packet.
   - Responses produced here (ObjectModel, EvaluationResult, VariableResult, Locked) are written immediately, i.e. before the code replies.
2. Pacing delay: up to `maxDelayBetweenTransfers` (25 ms default, `SbcMessageFormats.h:37`). It is skipped after 4 events or while a file operation is pending, and shortened to 2 ms in burst mode after evaluations, variables and macro requests (`:1361-1386`).
3. Queued code replies and generic messages, one FIFO for all channels (`WriteQueuedCodeReplies`, `:1389`, `:292-308`).
4. Code buffer update (`:1392-1400`).
5. File operation request, one slot (`:1403-1482`).
6. Per channel, in channel order (`:1485-1637`):
   - drop buffered codes while invalidated (`:1495-1504`);
   - MacroFileClosed (`:1506-1511`);
   - blocking macro request (`:1513-1527`);
   - then under `gb->mutex` with `SbcYieldTimeout`: abort request with drain and dummy reply (`:1540-1577`), WaitForAcknowledgement with drain (`:1579-1590`), MessageAcknowledged (`:1591-1595`), non-blocking macro request (`:1597-1611`), DoCode (`:1613-1634`).
7. PrintPaused (`:1639-1643`).

**The GCodes main task** runs in parallel at lower priority (SBC task priority 2, `RRF/Platform/TaskPriorities.h:17`). It:
- queues replies through `HandleGCodeReply` (`SbcInterface.cpp:2353-2406`);
- takes `gb.mutex` for a whole `SpinGCodeBuffer` (`GCodes.cpp:590`);
- may run up to 8 SBC file codes per spin (`MaxSbcFileCodesPerSpin`, `GCodes.cpp:56`, `:810`).

**DSF `LinkService.Execute` order** (`DCS/Link/LinkService.cs:313-602`):
1. emergency stop (`:324-335`);
2. reset (`:337-354`);
3. firmware update (`:357-386`);
4. `HadReset` → `Invalidate` (`:388-396`);
5. link capability activation (`:398-399`);
6. print info or print stopped (`:401-431`);
7. process every packet of the RX data (`:433-452`), which writes file results and resend copies into the next TX;
8. complete the halt that went out with the previous transfer (`:454-456`);
9. `channels.Spin()` (`:458-462`), round robin with a rotating start channel (`Channel/Manager.cs:72-99`);
10. one object model query (`:470-478`);
11. OM key changes (`:480-495`), SetLastCodeResult (`:497-513`), evaluations and variables (`:515-578`), messages (`:580-594`);
12. `PerformFullTransfer` (`:598`).

**Per channel, `Processor.Spin`** (`Processor.cs:881-1034`), in fixed priority:
1. abort windows of #134 and #161 (`:883-897`);
2. comments and pending replies (`:900-901`);
3. hold while an abort is unconfirmed (`:902-905`);
4. lock or unlock (`:907-929`);
5. InvalidateChannel (`:931-937`);
6. MacroStarted and MacroCompleted (`:939-989`);
7. suspended codes (`:991-1003`);
8. pending codes (`:1005-1016`);
9. flush (`:1018-1027`).

**Consequence: both directions are pipelined by exactly one transfer, on SPI and on USB.**
- DSF's TX(k+1) is written after DSF processed RX(k). RRF's TX(k+1) is written after RRF processed DSF's TX(k).
- An answer to a DSF request sent in transfer k therefore arrives at the earliest in RX(k+1), plus up to 25 ms pacing.
- A request RRF sent in RX(k) was written without knowledge of DSF's TX(k).
- For USB this rests on `DataTransfer.cpp:1362-1371`: "txPointer/packetId are set by ExchangeData and must be preserved until DoTransferUsb sends them". DSF's comment at `USB.cs:717-720` says the opposite (see NEW-1).

### 2.3 Resend semantics

**RRF side.**
- RRF refuses a packet by writing `ResendPacket` with the packet's id (`DataTransfer.h:238-241`). It keeps no copy.
- TX space for one resend header per received packet is always reserved (`FreeTxSpace`, `DataTransfer.h:200-201`, `:228-231`).
- Refusal reasons:
  - **Code:** code buffer full; after the first refusal every later Code packet of the transfer is refused too (`codeBufferAvailable`, `SbcInterface.cpp:394-414`). This includes codes of other channels.
  - **Mutex not available within 10 ms:** PrintStopped (`:522-539`), MacroCompleted (`:568-596`), Lock (`:619-627`), Unlock (`:649-657`), EvaluateExpression (`:698-744`), targeted Message (`:785-789`), InvalidateChannel (`:897-918`), SetVariable (`:949-954`), DeleteLocalVariable (`:1131-1136`), SetLastCodeResult (`:1307-1312`).
  - **No output buffer:** GetObjectModel (`:466-471`), Message (`:812-822`).
- **Lock** is refused until the machine stands still. The resend doubles as polling (`SPI.cs:1107-1120`, `LinkService.cs:615-619`).

**DSF side.**
- DSF keeps 3 TX buffers (`SPI.cs:83-89`, `USB.cs:54-90`).
- On SPI it looks up the id in the buffer two transfers back (`_txBuffer.Next`, `SPI.cs:823`), on USB one back (`_txBuffer.Previous`, `USB.cs:721`).
- It writes the copy with a new id into the current TX buffer (`SPI.cs:821-856`), during RX processing, so the copy comes first in that transfer.
- **SPI only:** `_packetsBeingResent` blocks new packets of the same request type for the rest of that one transfer (`SPI.cs:94`, `:305`, `:841-845`, `:900`, …). Upstream 86eee4ac0: "Keep track of packets being resent to avoid getting out-of-order".
  - Excluded: EmergencyStop, Reset, Lock, SetLastCodeResult, ObjectModelKeyChanged, per the fake firmware's mirror list (`UnitTests/FakeFirmware/FakeFirmware.cs:60`).
  - USB has no such guard (`USB.cs:786-803`).
- **Fork (#161):** `ResendPacket` reports the channel of a resent InvalidateChannel (`ILinkAdapter.cs:203`, `SPI.cs:835-836`, `USB.cs:733-734`). `LinkService` reopens the abort window for that channel (`LinkService.cs:620-626`).

**Transfer-level resend (link layer, fork).** The sequence guards resend a whole transfer when the peer repeats a sequence number:
- DSF #92: `SPI.cs:1979-2019`, `:408-419`.
- RRF #35: `DataTransfer.cpp:924-995`.

### 2.4 Code buffer space

**RRF.**
- One ring of 4096 bytes for all channels (`SbcInterface.cpp:76`, `SbcMessageFormats.h:47`).
- Codes are stored in arrival order with a 4-byte `BufferedCodeHeader` (`SbcInterface.cpp:404-433`).
- `FillBuffer` hands a GB the first pending code of its channel (`:1890-1950`). It holds codes back while the GB has an unsent stack event (`:1892-1898`).
- RRF reports the largest contiguous free block (`:1397`), plus defragmentation (`:1392`, `:2436`).

**DSF.**
- `BufferSpace = reported − BytesReserved`, where `BytesReserved` = code bytes written into the TX that RRF had not seen when it wrote the report (`LinkService.cs:747-753`, reset after RX processing `:452`; `LinkInterface.cs:37`, `:573-583`).
- Per-channel limit `(MaxReportedBufferSpace − SpareBufferSpace) / NumJobStreams` (`LinkInterface.cs:52`, upstream 82d581d1).
- Fork #133 keeps `SpareBufferSpace` = 2 × (4 + MaxCodeBufferSize) free for codes buffered ahead. A channel without buffered codes may use it (`LinkInterface.cs:40-44`, `:570-583`; `Processor.cs:1076-1079`).
- After an emergency stop the space is 0 until RRF reports again (`LinkInterface.cs:613`).

### 2.5 Emergency stop, reset and firmware update

**M112 path.**
- `MCodeHandler` M112 (`DCS/Codes/Handlers/MCodeHandler.cs:749-772`) flushes the code's level first, unless prioritized (`:750`). Then `WaitForUpdateAsync` and `EmergencyStopAsync` (`LinkInterface.cs:345-356`), with a 4.5 s timeout that stops DCS (`:755-763`).
- Prioritized means M108, M112, M122, M292 and M999 from SimpleCode. They are moved to an idle channel if their own is busy (`Commands/Generic/SimpleCode.cs:110-116`, `Codes/CodeProcessor.cs:209-257`).
- Codes that DSF reads from a macro file, such as trigger2.g's M112, are not prioritized and wait for the flush.

**LinkService.**
- `InvalidateCodes()` (all channels, job, requests), then writes EmergencyStop as the first packet of the next TX and skips the channel spins (`LinkService.cs:324-335`).
- The stop request completes only after that transfer has completed and its RX data, written by RRF before the stop, has been processed (`transferredHaltRequest`, `:315-317`, `:454-456`, `:599`; fork #88).

**RRF.**
- `reprap.EmergencyStop()` in the SBC task when it reads the packet (`SbcInterface.cpp:336-338`).
- In the fork, the GCodes reset of every channel is deferred to the main task (`requestsFromOtherTasks`, `GCodes.cpp:69-76`, `:1073-1079`, `:460-487`, `:581-584`; RRF #20).

**Reset (M999).**
- `Invalidate()`; `WriteReset` drops everything else in the TX buffer (`_txPointer = 0`, `SPI.cs:886`); an immediate `PerformFullTransfer`. A pending halt is completed too, because RRF stops before it resets (`LinkService.cs:337-354`).
- RRF: `EmergencyStop` + `SoftwareReset` (`SbcInterface.cpp:341-344`).
- 4.5 s timeout (`MCodeHandler.cs:1367-1375`).

**Firmware update.** `Invalidate()`, the halt is completed, IAP (`LinkService.cs:357-386`).

**Lost connection.**
- DSF invalidates everything on the reconnect (`HadReset`, `LinkService.cs:388-396`, `:1605-1622`). After any timeout `_hadReset` is set, "RRF may have dropped pending requests without the sequence numbers showing it" (`SPI.cs:386-390`, upstream 4a1ec34c).
- RRF (fork #21, 13fa7c2dd) performs an emergency stop when an established connection is lost (`SbcInterface.cpp:229-230`).

### 2.6 Timeouts that act as correlation

| Where | Value | Effect |
|---|---|---|
| RRF `SbcYieldTimeout` | 10 ms | Mutex wait before a request is refused and resent (`SbcInterface.cpp:45`) |
| RRF `SbcMaxRequestTime` | 3 s | Macro and file request give up. A late answer is dropped by state (`GCodeBuffer.cpp:1266-1272`, `SbcInterface.cpp:1204-1207`, `:1221-1224`) |
| RRF transfer, connection | 500 ms, 4 s | Lost SBC → emergency stop (fork) (`SbcMessageFormats.h:43-45`) |
| DSF M112, M999 | 4.5 s | DCS stops itself (`MCodeHandler.cs:757`, `:1369`) |
| DSF `SbcRestartTimeout` | 60 s | DCS stops on a livelock (`SPI.cs:326-331`, #102) |
| DSF extended switch | 16 transfers | DSF gives up the extended link (`SPI.cs:64`, `:421-433`) |
| DSF/RRF abort windows | 1 or 2 transfers | See section 4 |

## 3. v7 message types

"Ch" = channel-bound (carries a `CodeChannel`). "G" = global. Pairing = how the answer is matched today.

### 3.1 SBC → RRF

| Req | Name | Ch/G | Pairing today | Overtaking | DSF writer / RRF handler |
|---|---|---|---|---|---|
| 0 | EmergencyStop | G | None. DSF completes M112 after the next transfer's RX (`LinkService.cs:454-456`) | Must overtake. First packet of a TX; channel spins skipped (`LinkService.cs:324-335`). Still waits for RRF's pacing and transfer retries (R10) | `SPI.cs:862-872` / `SbcInterface.cpp:336-338` |
| 1 | Reset | G | None. DSF transfers once and exits (`LinkService.cs:341-351`) | Must overtake. Drops the rest of the TX (`SPI.cs:886`) | `SPI.cs:878-890` / `:341-344` |
| 2 | Code | Ch | Reply = next Message with the channel bit, by position (`Processor.cs:1108-1117`) | In order per channel. Refused when the ring is full, then all later codes of that transfer too (`:394-414`). Dropped silently while the GB is invalidated (`:376-380`) | `SPI.cs:897-920` / `:347-440` |
| 3 | GetObjectModel | G | Response 1 = head of `ModelQueryRequests` with `QuerySent`, one query per transfer (`LinkService.cs:470-478`, `:714`) + fork key check (`:716-723`) | Refused without an output buffer (`:466-471`) | `SPI.cs:967+` / `:443-494` |
| 5 | SetPrintFileInfo | G | None. TCS completes when written (`LinkService.cs:405-410`) | Must be first in the TX for 64-bit alignment, per the DSF comment (`LinkService.cs:407`) | `SPI.cs:1028+` / `:497-503` |
| 6 | PrintStopped | G (File/File2) | None. Normal completion → DSF waits for one empty reply (`LinkService.cs:413-431`, `Processor.cs:1129-1134`) | abort → RRF drops buffered File codes (`:509-517`) | `SPI.cs:1057+` / `:506-542` |
| 7 | MacroCompleted | Ch | Answer: MacroFileClosed 14, or none for error (`Processor.cs:952-981`) | Resent on mutex timeout (`:568-596`). Resolves a macro wait if one is pending (`:558-566`) | `SPI.cs:1083+` / `:545-604` |
| 10 | LockMovementAndWaitForStandstill | Ch | Locked 9 = first lock request on the channel's stack (`Processor.cs:838-849`) | Polled by resend until standstill | `SPI.cs:1107-1120` / `:607-634` |
| 11 | Unlock | Ch | None | Resent on mutex timeout | `SPI.cs:1127+` / `:637-664` |
| 12/13 | WriteIap / StartIap | G | IAP protocol | Exclusive; RRF stops (`SbcInterface.cpp:667-672`) | `SPI.cs:1152-1265` / `:1646+` |
| 15 | FileChunk | G | Answer to FirmwareRequest 10; RRF calls it deprecated (`SbcMessageFormats.h:328`) | — | `SPI.cs:1266+` / `:676-679` |
| 16 | EvaluateExpression | Ch | EvaluationResult 11 matched by (channel, expression string) (`LinkService.cs:1005-1023`); duplicates merged (`LinkInterface.cs:144-153`) | Resent on mutex timeout | `SPI.cs:1296+` / `:682-760` |
| 17 | Message | G or Ch (destination bits) | A non-push message to a channel that is "executing on SBC" finishes that GB (`:772-798`) | Resent on mutex timeout or without memory | `SPI.cs:1326+` / `:764-824` |
| 18 | MacroStarted | Ch | None. Resolves RRF's macro wait; "none was requested" otherwise (`:839-853`) | — | `SPI.cs:1355-1373` / `:827-865` |
| 19 | InvalidateChannel | Ch | None today. #161 treats "no resend in the next RX" as acceptance (`Processor.cs:883-905`) | Should overtake the channel's queued work but cannot. Resent on mutex timeout (`:897-918`); later packets are processed meanwhile | `SPI.cs:1379-1398` / `:868-925` |
| 20 | SetVariable | Ch | VariableResult 16 by (channel, name) (`LinkService.cs:1123-1141`); writing stops at the first unwritable to keep order (`:572`) | Resent on mutex timeout | `SPI.cs:1406+` / `:928-1109` |
| 21 | DeleteLocalVariable | Ch | None. Completed when written (`LinkService.cs:554-558`) | Resent on mutex timeout | `SPI.cs:1436+` / `:1112-1143` |
| 22–28, 31 | File results | G | Single slot: RRF matches by `fileOperation` state (`:1146-1285`); late replies dropped (`:1204-1207`, `:1221-1224`) | — | `SPI.cs:1465-1665` / `:1146-1285` |
| 29 | SetLastCodeResult | Ch | None | Resent on mutex timeout | `SPI.cs:1666+` / `:1288-1316` |
| 30 | ObjectModelKeyChanged | G | None. RRF reads the key at the wrong offset (open finding S3) | — | `SPI.cs:1690-1707` / `:1319-1331` |
| 0x4D50 | ActivateLinkCapabilities (fork) | G | Checked by a later OM query of `state.sbcLinkCapabilities` (`LinkService.cs:1569-1574`) | Exempt from RRF's "request ≥ 32" check (`:326`) | `SPI.cs:937+` / `:1334-1342` |

### 3.2 RRF → SBC

| Req | Name | Ch/G | Pairing today | Overtaking | RRF writer / DSF handler |
|---|---|---|---|---|---|
| 0 | ResendPacket | refers to a packet id of DSF's TX two transfers back (SPI) | id within one transfer | Written first in its TX (during RX processing) | `DataTransfer.h:238-241` / `LinkService.cs:613-627`, `SPI.cs:821-856` |
| 1 | ObjectModel | G | order (see 3.1) | — | `DataTransfer.cpp:1445+` / `LinkService.cs:708-740` |
| 2 | CodeBufferUpdate | G | timing: minus `BytesReserved` | — | `:1471+` / `LinkService.cs:745-754` |
| 3 | Message (code reply, generic, targeted) | Ch by destination bits, or G | Code reply → `BufferedCodes[0]` of the first channel whose bit is set (`Manager.cs:107-121`, `Processor.cs:1102-1168`). Push flag = more chunks follow (`:1198-1215`) | One FIFO for all channels (`SbcInterface.cpp:2367-2384`) | `:1488-1541` / `LinkService.cs:764-813` |
| 4 | ExecuteMacro | Ch | Start code = `BufferedCodes[0]` if fromCode (`Processor.cs:1373-1389`); system macro refused when the stack is not empty (`:1364-1370`) | Blocking request before the mutex section, non-blocking (daemon.g) after it (`:1513-1527`, `:1597-1611`) | `:1543+` / `LinkService.cs:845-861` |
| 5 | AbortFile (single / all) | Ch | Single: completes the M99 at `BufferedCodes[0]` (`Processor.cs:655-665`); RRF sends a dummy reply for the start code (`:1564-1573`) | Single abort drains the reply FIFO first (`:1543-1549`); abort-all never waits | `:1566+` / `LinkService.cs:866-889` |
| 7 | PrintPaused | G (File/File2) | File positions (identity) | RRF drops File codes until the pause is written (`:1495-1504`) | `:1602+` / `LinkService.cs:895-925` |
| 9 | Locked | Ch | first lock request (`Processor.cs:838-849`) | written during RX processing | `:1622+` / `LinkService.cs:931-947` |
| 10 | FileChunk | G | synchronous answer 15 | — | — / `LinkService.cs:953-995` |
| 11 | EvaluationResult | Ch | (channel, expression) | — | `:1640-1855` / `LinkService.cs:1000-1029` |
| 12 | DoCode | Ch | DSF runs the code prioritized on that channel; the result goes back as Message 17 (`Codes/Pipelines/Executed.cs:173-199`) | — | `:1856+` / `LinkService.cs:1034-1050` |
| 13 | WaitForAcknowledgement | Ch | Start code = `BufferedCodes[0]` (`Processor.cs:1264-1271`) | Drains replies first (`:1579-1590`) | `:1876+` / `LinkService.cs:1055-1071` |
| 14 | MacroFileClosed | Ch | Pops the current stack level and puts the start code back to `[0]` (`Processor.cs:1283-1305`) | Written before the blocking macro request; no drain (fork #29, content equal to upstream) | `:1584+` / `LinkService.cs:1076-1092` |
| 15 | MessageAcknowledged | Ch | Pops the message box level (`Processor.cs:1320-1345`) | no drain | `:1894+` / `LinkService.cs:1097-1113` |
| 16 | VariableResult | Ch | (channel, name) | — | `:1912-2080` / `LinkService.cs:1118-1147` |
| 17–27 | File requests | G | Synchronous on DSF's transfer thread, answered in the same order; one in flight | — | `:2081-2290` / `LinkService.cs:1152-1494` |

## 4. Abort and InvalidateChannel family (layer P)

### A0. Base: abort-all without acknowledgement (upstream design)

- **Assumption:**
  - DSF removes every macro level at once and only notifies RRF (`Processor.AbortAllFilesAsync`, `Processor.cs:766-833`; `_allFilesAborted`, `:117`, `:822`; Spin step `:931-937`).
  - RRF closes its files without telling DSF (`gb->FileAbortSent()`, "don't notify the SBC", `SbcInterface.cpp:903`).
  - RRF drops only the codes still in the ring (`InvalidateBufferedCodes`, `:905`), not the code being executed.
  - Both sides therefore drop by convention, and everything in flight in either direction (one transfer each way) is ambiguous.
- **Code:** upstream, DSF 4a576390, RRF 8207b22cc and f522904f5.
- **Is the base of** A1–A7.
- **v8:** an abort with an acknowledgement that carries a channel epoch. Every channel-bound message carries the epoch, and stale ones are dropped by identity on both sides.

### A1. T2 – a macro request that crosses InvalidateChannel is started by DSF

- **Symptom:** the channel is blocked; RRF warns "Macro file has been started … but none was requested". design/dsf-findings-upstream.md T2. Printer: the fix fired on 2026-11-08 (lab-test-plan.md §14, 22:00 run).
- **Assumption:** RRF's data of the transfer that carried InvalidateChannel, and earlier data, was written before RRF read it. RRF ends that macro wait with an error.
- **Catch code (DSF, fork, #134, 654aed1e):**
  - `_allFilesAbortedSent` (`Processor.cs:119-124`) is set in Spin step 3 (`:934`) and cleared one Spin later (`:888-892`).
  - `DoMacroFile` discards requests while the flag is set (`:1357-1362`).
- **Layer:** P. **Firmware RAM:** 0.
- **v8:** abort epoch. A request carries the epoch it was raised in, so DSF drops it without counting transfers.

### A2. T12 – an abort that crosses the closal of a macro underruns DSF's stack and stops DCS

- **Symptom:** `InvalidOperationException: Stack underrun` in `Processor.Pop` ← `MacroFileClosed`. Printer 2026-11-08 22:01 (lab-test-plan.md §14). design/dsf-findings-upstream.md T12.
- **Assumption:** RRF reports a closal written before it read the abort (same window as A1). Or RRF writes one after it read it in the same ExchangeData, because packets are read first (`SbcInterface.cpp:314-1359`) and closals written later (`:1506-1511`).
- **Catch code:**
  - DSF (fork, #158, af07d869): `MacroFileClosed` discards a closal while `_allFilesAborted || _allFilesAbortedSent` (`Processor.cs:1285-1290`).
  - RRF (fork, #45, 18a443b7b): `gb->MacroFileClosedSent()` in InvalidateChannel (`SbcInterface.cpp:904`), as `GCodeBuffer::Reset` does (`GCodeBuffer.cpp:128-149`).
- **Layer:** P. **Firmware RAM:** 0 (clears an existing flag).
- **v8:** abort epoch on MacroFileClosed. Better still, the stack event names the stack level or file it closes.

### A3. T1 (RRF) – InvalidateChannel ends a macro wait but still sends the request

- **Symptom:** DSF runs a trigger macro that RRF does not run; later triggers are discarded with "System macro … stack is not empty" (`Processor.cs:1364-1370`, `:1390-1395`). design/dsf-findings-upstream.md T1.
- **Assumption:** the request is a per-GB string that the SBC task sends later (`requestedMacroFile`, `GCodeBuffer.h:403`). Ending the wait does not clear it.
- **Catch code (RRF, fork, #43, 4cf5273dc):** `gb->MacroRequestSent()` before `ResolveMacroRequest` (`SbcInterface.cpp:891-894`). The DSF fake firmware mirrors it (#144).
- **Layer:** P. **Firmware RAM:** 0.
- **v8:** requests are discrete messages with an id or epoch, not a polled per-GB slot.

### A4. T11 (resend) – an InvalidateChannel that RRF asks for again blocks the channel

- **Symptom:** "Resending packet #0 (request InvalidateChannel)"; RRF warns "none was requested"; the next code gets no reply. Printer 2026-11-08 22:39 (lab-test-plan.md §14). design/dsf-findings-upstream.md T11 (resend).
- **Assumptions:**
  - While an M98 waits for its macro, the main task holds the recursive `gb.mutex` (`GCodes.cpp:590`, c5af8416b). `RequestMacroFile` releases only its own level (`GCodeBuffer.cpp:1253`, `:1263`).
  - InvalidateChannel waits 10 ms (`SbcInterface.cpp:897`) and is refused. After it has already ended the macro wait (`:891-894`), the later packets are processed.
  - DSF treated the abort as done once written. A resend reorders it behind DSF's next transfer.
- **Catch code (DSF, fork, #161, 4221cbde):**
  - `_allFilesAbortUnconfirmed`: the channel holds until an RX arrives without a resend (`Processor.cs:126-131`, `:893-905`, `:934`).
  - `_allFilesAbortResent` (`:133-136`, `:883-887`).
  - `AllFilesAbortResent()` (`:1312-1315`).
  - `ILinkAdapter.ResendPacket(…, out CodeChannel? invalidatedChannel)` (`ILinkAdapter.cs:203`, `SPI.cs:835-836`, `USB.cs:733-734`).
  - `LinkService.cs:620-626`.
  - Reset in `Invalidate` (`Processor.cs:1537`).
  - Behaviour change: the channel's next request goes out one transfer later.
- **Printer:** 2026-11-09, 2000 aborts, 2 resends, the channel stayed free (lab-test-plan.md §14 on branch #163).
- **Layer:** P. **Firmware RAM:** 0.
- **v8:**
  - abort with an explicit acknowledgement;
  - a NACK that holds back every later packet of the same channel ("per-channel in-order delivery": a refusal stops the channel's stream until the copy arrives);
  - or RRF accepts InvalidateChannel without the GB mutex.

### A5. T11 (daemon.g) – no catch code, open

- **Edge case:** at the base level of the Daemon channel RRF requests daemon.g without waiting (`GCodeBuffer.cpp:1256-1262`; non-blocking path `SbcInterface.cpp:1597-1611`). InvalidateChannel clears the request only while `IsWaitingForMacro` (`:883-895`), so a request not yet sent survives the abort. design/open-findings.md T11. From the code.
- **Layer:** P.
- **v8:** abort epoch.

### A6. T10 (RRF) – the reply of a code that DSF dropped goes to the next code

- **Symptom:** a `G4 P0` after an abort finished with "M98: Macro file … not found". Printer 2026-11-08. design/dsf-findings-upstream.md T10 on branch #163; open-findings.md T10.
- **Assumption:** DSF drops every code it sent on the channel (`InvalidateRegular`, `Processor.cs:1449-1485`). RRF drops only the ring, while the executing code still replies. Replies are matched by position.
- **Catch code (RRF, fork, #46 open, 0dccb9efe):**
  - `codeReplySurplus` (`GCodeBuffer.h:248-249`, `:405`), set in InvalidateChannel (`SbcInterface.cpp:880-888`, `:907-911`); cleared on `PutBinary` (`GCodeBuffer.cpp:323`) and in `Reset` (`:145`).
  - Suppression in `HandleReplyPreserveResult` (`GCodes.cpp:4450-4458`), `HandleReply` (`:4584-4593`) and the "Macro file not found" warning (`:3703-3708`).
  - `invalidationPending`, so that only the first copy flags (`GCodeBuffer.h:245-246`, `:422`; `SbcInterface.cpp:882`, `:912`, `:916`).
  - DSF #163 mirrors this in the fake firmware.
- **Still uncovered** (open-findings.md T10 on #163): a first copy before `isWaitingForMacro` is set (printer 2026-11-09, 2×); replies written before the abort; M291 S2 at the base level; state machines (G28) that go on; M122 and other writers outside HandleReply.
- **Layer:** P (RRF). **Firmware RAM:** 2 bool per GB, about 2 × 15 bytes before padding.
- **v8:** replies carry the id of the code they answer, and DSF drops replies for ids it cancelled. That makes `codeReplySurplus` and `invalidationPending` unnecessary.

### A7. T13 – the USB window of #134 also covers post-abort data (open, premise questioned)

- **Doc:** open-findings.md T13. It rests on the comment `USB.cs:717-720` ("the firmware sees our data before composing its response").
- **What the RRF code shows:** `DoTransferUsb` sends `txBuffer` (`DataTransfer.cpp:1340-1347`), which `ExchangeData` filled after the previous transfer (`SbcInterface.cpp:267-268`; "txPointer/packetId are set by ExchangeData and must be preserved until DoTransferUsb sends them", `DataTransfer.cpp:1362-1371`).
- **Conclusion:** if that reading is right, USB has the same one-transfer pipeline as SPI. The #134 window is then correct on USB, and T13 does not exist. NEW-1 exists instead.
- **Status:** not tested. The CHX uses SPI (open-findings.md T13).
- **Layer:** P/L.
- **v8:** acknowledgement by id removes any dependence on the transport's pipeline depth.

### A8. Single-file abort (M99) family

- **Symptom:** a late empty M99 reply completed the caller's next code ("unknown variable" after a nested M99). Fork #44, later reverted. Upstream pair: DSF da8e9bb8 and RRF 4d80f4663.
- **Assumption:** RRF writes code replies before abort requests. The DSF code that receives the single abort is `BufferedCodes[0]`.
- **Catch code (all upstream content):**
  - DSF `FilesAborted` completes the M99 at `BufferedCodes[0]` itself (`Processor.cs:650-666`, da8e9bb8).
  - RRF drops the empty reply while a single abort is pending (`GCodes.cpp:4460-4465`, 4d80f4663).
  - RRF writes a dummy empty reply for the code that started the aborted macro (`SbcInterface.cpp:1564-1573`, f522904f5).
  - RRF drains queued replies before a single abort (`:1543-1549`, f2e44bcc9). The commit message says: "The SBC matches replies to codes by position, so those late replies complete the wrong codes".
  - Root cause per f2e44bcc9 and fork 54c40be1b: c76c13cc2 runs up to 8 file codes per spin under one mutex hold (`GCodes.cpp:56`, `:810`).
- **Layer:** P (RRF) + G (DSF `FilesAborted`). **Firmware RAM:** 0.
- **v8:** reply ids. The single abort names the code that caused it (M99) and the code that resumes.

### A9. Trigger requested before the previous abort was sent

- **Symptom:** "System macro … stack is not empty" + "not found" (second race of an earlier fork analysis, not in the repository).
- **Assumption:** DSF keeps the aborted file on its stack until the abort reaches it.
- **Catch code (RRF, fork→upstream 185ebb3de):** `IsTriggerBusy` includes `IsAbortRequested()` (`GCodes.cpp:429`).
- **Layer:** G (RRF). **Firmware RAM:** 0.
- **v8:** stack events with an id or epoch.

### A10. Pause and print stop

- **Assumption:** it may take several transfers until the pause takes effect, so job codes that DSF sent before it learned of the pause must be dropped.
- **Catch code (all upstream):**
  - RRF keeps the File GBs invalidated while `reportPause` is set (`SbcInterface.cpp:1495-1504`, 8207b22cc and 8b14e2846).
  - `FillBuffer` holds file codes back while `reportPause` is set (`:1893`).
  - PrintStopped(abort) drops the File codes (`:506-517`).
  - DSF `PrintPaused` pops all macros of the File channel, "When we get here, RRF has done the same" (`Processor.cs:1431-1444`).
  - DSF `HandlePrintPaused` (`LinkService.cs:895-925`).
- **Layer:** P/G. **Firmware RAM:** 0.
- **v8:** a pause epoch on the file channels. File positions in PrintPaused are already identities.

## 5. Reply correlation and stack events

### B1. Code replies by position (base)

- **Code (upstream):**
  - `Processor.HandleReply` → `BufferedCodes[0]` (`Processor.cs:1102-1117`).
  - Chunks joined by PushFlag, one partial message per channel (`:1184-1215`).
  - Channel from the destination bit; the first matching channel wins (`Manager.cs:107-121`).
  - Fallback "Out-of-order reply" (`Processor.cs:1162-1167`), then a generic message (`LinkService.cs:791-797`).
- **Layer:** G (in the link Processor, which is logic per its own remark "should be merged with Codes.Pipelines.Firmware", `Processor.cs:27-29`).
- **v8:** a per-channel code id, echoed in replies and stack events.

### B2. PendingReplies re-order queue

- **Code (upstream, 4a576390 and 75eb6536):**
  - Replies that arrive after MacroCompleted or while waiting for an acknowledgement are queued (`Processor.cs:1119-1153`) and dispatched when the stack pops (`:870-876`, used at `:653`, `:966`, `:1301`, `:1332`).
  - "Replies must be consumed in arrival order, else multi-chunk replies are reassembled wrongly" (`:447-450`).
  - Leftovers are logged as "Pending out-of-order reply" (`:1029-1033`).
- **Layer:** G. **v8:** reply ids.

### B3. The code that started a macro or message box is taken by position

- **Code (upstream):**
  - `DoMacroFile` (`Processor.cs:1373-1389`), `WaitForAcknowledgement` (`:1264-1271`).
  - The start code is put back at `[0]` on MacroFileClosed (`:1292-1302`), MessageAcknowledged (`:1326-1333`) and MacroCompleted with error (`:960-967`).
  - RRF drains replies before WaitForAcknowledgement (`SbcInterface.cpp:1579-1590`, f2e44bcc9; "The SBC attributes this request to the first code it has buffered for this channel").
  - Fork 54c40be1b (RRF #29) removed the drains of MacroFileClosed and the blocking macro request again; the content is now equal to upstream.
- **Layer:** G (DSF) + P (RRF drains). **Firmware RAM:** 0.
- **v8:** ExecuteMacro and WaitForAcknowledgement carry the id of the requesting code.

### B4. M122 and M409 are forced Unbuffered

- **Symptom:** a message box or macro request of the next code overtook the missing M122/M409 chunks (3 × M122 + M291 S2 showed several dialogs). Side effect: the Unbuffered deadlock (fork #51, replaced by upstream 30ea08be via #54).
- **Code (upstream):**
  - `CodeProcessor.cs:202-207` (da8e9bb8, df0bfd68).
  - Per-level Unbuffered counter (`Codes/Pipelines/Start.cs:27-39`, 30ea08be).
  - An earlier fork analysis (not in the repository) says one full-transfer window remains in RRF; it is unverified.
- **Layer:** G. **v8:** reply ids, plus "a reply's chunks are contiguous per channel" or chunk ids.

### B5. Final empty reply after a print stopped

- **Code (upstream 9acd058f):**
  - `_waitingForPrintCompletion` (`Processor.cs:1170-1179`, `:1129-1134`, `:1155-1160`).
  - `LinkService.cs:413-431`: "RRF writes the final reply before the request closing stop.g, so it may arrive first".
- **Layer:** G. **v8:** reply ids, or a print-completed event.

### B6. T4 – a missing trigger macro is not shown after DSF ran a code on the Trigger channel (open)

- **Symptom:** "Trigger: Out-of-order reply: 'Macro file trigger8.g not found'" (open-findings.md T4).
- **Cause:** a pushed warning to a channel with `lastCodeFromSbc` (`GCodes.cpp:3703-3708`) has no buffered code in DSF (`Processor.cs:1162-1167`).
- **No catch code.** Layer P. **v8:** the system-macro result references the request id.

### B7. Read-ahead codes are invalidated on every stack push (new name, upstream mechanism)

- **Mechanism:**
  - When RRF sends a blocking macro request, an abort request, a message prompt or a non-blocking macro request, it sets `gb->Invalidate()` (`SbcInterface.cpp:1525`, `:1575`, `:1588`, `:1609`).
  - In the next ExchangeData, Code packets for that channel are dropped silently (`:376-380`), then the ring is purged and the flag cleared (`:1495-1504`).
  - `FillBuffer` holds codes back while a stack event is unsent (`:1892-1898`).
  - DSF mirrors this by moving its buffered codes to `SuspendedCodes` on `Push` and sending them again after the pop (`Processor.cs:158-189`, Spin step 7 `:991-1003`).
- **Assumption:**
  - The window is exactly one ExchangeData: RRF drops what DSF wrote into the transfer after RRF's request, and accepts the next.
  - This relies on DSF processing RX(k) before it writes TX(k+1), which holds today (2.2).
  - A Code packet dropped this way gets no resend, so it is not covered by `_packetsBeingResent` either.
- **Code (upstream):** RRF 8207b22cc, 637bfeb48, 8b14e2846; DSF 4a576390, c5e3214b.
- **Layer:** P (RRF) + G (DSF suspension). **Firmware RAM:** 1 bool per GB (`invalidated`, `GCodeBuffer.h:421`).
- **v8:** codes carry ids and a stack-level tag, so RRF can refuse "codes after id X on level L" explicitly and DSF knows which ones to send again. Or flow control per stack level.

### B8. Implicit start of a macro

- **Code (upstream 8207b22cc):**
  - A Code packet for a GB that waits for a macro resolves the wait (`SbcInterface.cpp:382-392`). So do MacroStarted (`:839-849`) and MacroCompleted (`:558-566`).
  - Otherwise RRF warns "Macro file has been started on channel … but none was requested" (`:850-853`).
- **Assumption:** the next code on the channel belongs to the requested macro.
- **Layer:** P. **v8:** MacroStarted with the request id; codes carry the stack level.

### B9. A firmware code executed on DSF is finished by the next targeted message

- **Code:**
  - RRF: a non-push message whose only destination bit is a GB "executing on SBC" → `SetFinished` (`SbcInterface.cpp:772-798`, upstream 4527c436d). The fork takes the mutex (fb2693400, RRF #20).
  - DSF: the result of an IsFromFirmware code goes back as Message (`Codes/Pipelines/Executed.cs:173-199`).
- **Layer:** P. **Firmware RAM:** 0. **v8:** DoCode id echoed in its result.

### B10. NEW-2 – the shared reply FIFO overflows and drops a reply (code, not observed)

- **Code:**
  - `gcodeReply` is one `OutputStack` for all channels (`SbcInterface.h:155`, `:2367-2384`), with 64 entries when `HAS_SBC_INTERFACE` (`OutputMemory.h:15-19`).
  - `Push` on a full stack releases the buffer, logs `OutputStackOverflow` and returns false (`OutputMemory.cpp:487-506`).
  - Replies are written at most once per ExchangeData, up to the TX capacity (`SbcInterface.cpp:292-308`), so a stalled link can let the FIFO fill.
- **Effect:** with B1, every later reply of that channel shifts by one.
- **Layer:** P. **v8:** reply ids (a gap is detectable), and per-channel credit for replies.

### B11. Legacy protocol < 3 branches

- **Code (upstream):** the closal or acknowledgement is taken from an empty reply (`Processor.cs:1122-1126`, `:1143-1147`, `:977-981`).
- Purely order-based. v8 could drop them.
- **Layer:** G.

## 6. Resend, flow control and the code buffer

### C1. ResendPacket as NACK and back-pressure (upstream design)

- **Code:**
  - RRF: `SbcInterface.cpp:1354-1358`, `DataTransfer.h:238-241`, and the refusal list in 2.3.
  - DSF: `SPI.cs:821-856` (buffer `_txBuffer.Next`, `:823`) and `USB.cs:715-747`.
- **Assumptions:**
  - Packet ids are unique only within one transfer, and the buffer is found by transfer position (pipeline depth one).
  - Packets after a refused one are processed (except codes after a code refused for buffer space).
- **Not verified:** an earlier fork review (not in the repository) says that after the sequence guard resent a transfer, "the ResendPacket lookup is off by one". The SPI buffer rotation `:408-419` vs `:435-437` was not re-checked here.
- **Layer:** P. **Firmware RAM:** 0 per packet, only the 8-byte header in TX.
- **v8:**
  - per-channel sequence numbers with a cumulative acknowledgement;
  - a refusal stops the channel's stream (go-back-N per channel), so nothing on that channel overtakes;
  - Lock polling becomes an explicit "pending" answer.

### C2. `_packetsBeingResent` guard per request type (SPI)

- **Code (upstream 86eee4ac0):** `SPI.cs:94`, `:305` (cleared per transfer), `:841-845`, and the checks at `:900`, `:970`, `:1001`, `:1031`, `:1060`, `:1086`, `:1130`, `:1269`, `:1299`, `:1329`, `:1358`, `:1382`, `:1409`, `:1439`, `:1468`, `:1492`, `:1517`, `:1547`, `:1572`, `:1596`, `:1620`, `:1644`.
- **Assumption:** blocking the same request type, across all channels, for the transfer that carries the copy keeps the order.
- **Gap (NEW-3, code):**
  - It does not cover the transfer in between, which DSF wrote before it saw the refusal.
  - `codeBufferAvailable` is reset at the start of every ExchangeData (`SbcInterface.cpp:313`), so a code of the same channel in that transfer can be stored before the copy.
  - DSF then has [C1, C2] buffered, and C2's reply completes C1 (B1).
  - Reachable only if RRF refuses a code that DSF's estimate let through (2.4: largest contiguous block vs the linear estimate). Not shown.
- **Layer:** L/P. **v8:** as C1.

### C3. NEW-1 – USB resend looks into the wrong TX buffer and has no type guard (code, not tested)

- **Code:** `USB.cs:717-721` (upstream 75eb6536, comment and `Previous`), `USB.cs:786-803` (no `_packetsBeingResent`).
- **RRF side:** `DataTransfer.cpp:1303-1357`, `:1362-1371`; `SbcInterface.cpp:267-268`.
- **Effect if confirmed:**
  - The copy is a packet with the same id from the wrong transfer, or `ArgumentException` "resend for invalid packet" (`USB.cs:746`).
  - That exception is not the `ArgumentOutOfRangeException` caught at `LinkService.cs:446`, so it ends LinkService and DCS.
- **Layer:** L/P. **v8:** acknowledgement by id, the same on every transport.

### C4. T3 – all channels together fill RRF's code buffer

- **Symptom:** trigger2.g's M98 and M112 were held back (printer 2026-11-08, lab-test-plan.md §14). design/dsf-findings-upstream.md T3.
- **Assumption:** one shared ring; per-channel limits do not add up to a guarantee.
- **Catch code:**
  - DSF fork #133 (a2e4f1e4): `SpareBufferSpace` (`LinkInterface.cs:40-44`, `:52`, `:570-583`), `mayUseSpareRoom` (`Processor.cs:1076-1079`).
  - Upstream share 82d581d1.
  - RRF ring and reporting: `SbcInterface.cpp:76`, `:394-434`, `:1392-1400`, `:2436`.
- **Layer:** P. **Firmware RAM:** 0 (DSF-only).
- **v8:** per-channel credits, or a reserved priority share announced by RRF.

### C5. `BytesReserved` – the buffer estimate assumes pipeline depth one

- **Code (upstream):** `LinkService.cs:452`, `:747-753`; `LinkInterface.cs:37`, `:573-583`.
- **Assumption:** RRF's CodeBufferUpdate in RX(k+1) does not yet include the codes in TX(k+1).
- **Layer:** P. **v8:** credits that RRF grants and acknowledges with a sequence number.

### C6. Lock is polled through resend

- **Code (upstream):** `SPI.cs:1107-1120`, `LinkService.cs:615-619`, RRF `:607-634`. The fake firmware excludes Lock from the resend check (`FakeFirmware.cs:60`).
- **Layer:** P. **v8:** a pending or deferred answer with the request id.

## 7. Correlation of global requests

### D1. Object model responses by order

- **Symptom:** responses shifted by one key after a timeout (earlier fork analysis of the TfrRdy timeout, not in the repository). "Failed to find query for object model response" when DCS restarts (lab-test-plan.md §9 line 321; §14 2026-11-07 and 2026-11-08).
- **Code:**
  - Upstream: one query per transfer and `QuerySent` (`LinkService.cs:470-478`, `:714`; 4a1ec34c, 86eee4ac0); `_hadReset` after every timeout (`SPI.cs:386-390`).
  - Fork 4dc381d0 (direct commit, released in 3.7.0-rc.1+mp.3): the response key must equal the requested key (`LinkService.cs:716-723`, `Protocol/Reader.TryReadObjectModelKey`).
  - Open PR #57 (seq+2, on hold for Chris) would remove the key check. An earlier fork review (not in the repository) rates the key check as against CLAUDE.md rule 1 (receiver-side check).
- **Layer:** P. **Firmware RAM:** 0. **v8:** a query id echoed in the response.

### D2. Evaluations and variables matched by content

- **Code (upstream):** `LinkService.cs:1005-1023`, `:1123-1141`, `:572`; duplicates merged (`LinkInterface.cs:144-153`).
- **Assumption:** at most one outstanding evaluation per (channel, expression) and one assignment per (channel, name). Order is kept by stopping at the first unwritable request.
- **Layer:** P. **v8:** request ids.

### D3. File operations: one slot and late replies

- **Code:**
  - The single slot (`SbcInterface.h:110-153`) is matched by `fileOperation` state (`SbcInterface.cpp:1146-1285`).
  - Late replies are skipped (`:1204-1207`, `:1221-1224`; fork→upstream e1f126fe9).
  - A reply that came just before the timeout still counts (`GCodeBuffer.cpp:1266-1272`; fork→upstream 168e92cb4).
  - DSF handles file requests synchronously on the transfer thread (`LinkService.cs:1152-1494`, design/sbc-link-current-state.md T7).
- **Layer:** P. **Firmware RAM:** 0. **v8:** request ids; asynchronous file I/O off the link thread (R14).

### D4. Link capability activation rides on OM queries (fork #93)

- **Code:** `LinkService.cs:1520-1600`. The activation packet is written before the print info in the same loop (`:399` vs `:405`), while the comment at `:407` asks for SetPrintFileInfo to come first for alignment.
- **Open:** whether RRF's 64-bit read of `PrintStartedHeader::lastModifiedTime` (`SbcMessageFormats.h:455-467`, `DataTransfer.cpp:669`) faults at a 4-byte offset is not checked.
- **Layer:** P/L. **v8:** explicit field alignment in the framing, independent of packet order.

## 8. Emergency stop, reset and firmware update

### E1. M112 completes only after RRF can have read the stop

- **Code (fork #88, 8f7c3f3b):** `transferredHaltRequest` (`LinkService.cs:315-317`, `:454-456`, `:599`); also completed on reset and update (`:348-351`, `:364-366`).
- **Assumption:** the stop is in the transfer just completed; RRF's data of that transfer predates it.
- **Open finding P3:** M112 can reply before RRF has processed the stop, because DSF's own last Success is never acknowledged; lead ≤ 0.05 ms (open-findings.md P3).
- **Layer:** P. **Firmware RAM:** 0.
- **v8:** an expedited STOP class with an explicit acknowledgement ("stopped", with the stop sequence number).

### E2. M112 overtakes by changing channel, not by priority on the link

- **Code (upstream):** `SimpleCode.cs:110-116`; `CodeProcessor.cs:209-257` (move to an idle channel); `MCodeHandler.cs:750` (flush unless prioritized); `LinkInterface.cs:345-356`.
- **Edge cases:**
  - An M112 in a macro, e.g. trigger2.g, waits for the flush of the macro's earlier codes. Those need code buffer space (T3).
  - The stop then goes out in RRF's pacing: 25–50 ms when idle, up to about 503 ms behind a failing long code transfer (open-findings.md stop path table, R10).
- **Fork RRF mitigation (#25, 794e78625):** trigger actions with M112 run inside RRF and fire as trigger 0 (`GCodes.cpp:987`, `:1017-1050`; `GCodes2.cpp:712`; `GCodes3.cpp:428`; design/rrf-local-trigger-actions.md TA1).
- **Layer:** G (DSF) + P. **Firmware RAM:** `triggerActionRunning` 1 byte (`GCodes.h:747`), plus the trigger action storage of #25, not sized here.
- **v8:** an expedited class that bypasses every channel queue, credit and retry (R10), handled by RRF's SBC task on receipt.

### E3. Halt and reset sequencing in LinkService

- **Code:**
  - `InvalidateCodes()` before the write, `skipChannels` (`LinkService.cs:324-335`).
  - Reset drops the TX (`SPI.cs:886`) and transfers at once (`LinkService.cs:341-351`).
  - 4.5 s timeouts stop DCS (`MCodeHandler.cs:755-763`, `:1367-1375`).
- **Upstream:** 4a576390, c5e3214b, 2cbc04ae, d027f15b. Fork #149 changed the token (30035f3f, ST1).
- **Layer:** P.

### E4. A trigger request that crosses an emergency stop

- **Observation:** the macro starts after the stop, but its codes are held back, because `BufferSpace` is 0 after `InvalidateCodes` (`LinkInterface.cs:613`; open-findings.md stop path, observation).
- **No catch code.** Layer P. **v8:** stop epoch; credits re-granted explicitly.

### E5. RRF restart just before the stop

- The restarted RRF gets the stop in only about half of the cases. It drops its first transfer by a connection reset (open-findings.md stop path). Whether that matters is open.
- **Layer:** L/P.

### E6. Fork RRF: emergency stop on every lost connection (#21, 13fa7c2dd)

- **Code:** `SbcInterface.cpp:229-230`; ATX off in `RepRap::EmergencyStop` (fork, `RepRap.cpp` diff).
- **Effect:** every HadReset path (section 9) ends the job, which is why the link census counts reconnects as failures (design/sbc-link-findings-upstream.md "Pass criterion").
- **Layer:** P. **Firmware RAM:** 0.

### E7. Fork RRF: requests from other tasks are carried out by the main task (#20, b963e098a, 7e936c1a5)

- **Code:** `GCodes.cpp:69-76`, `:223-230`, `:460-487`, `:581-584`, `:1073-1079`, `:1389-1393`, `:5269-5273`.
- An emergency stop from the SBC task stops the hardware at once and resets the GBs in the main task's next spin. No code starts in between.
- **Layer:** G (RRF). **Firmware RAM:** 4 bytes (`std::atomic<uint32_t>`, `GCodes.cpp:76`).

### E8. U2 (design/dsf-findings-upstream.md) – an IAP timeout looks like a cancellation

- **Code (fork 2d1deb6a):** `LinkService.cs:375-379`.
- The coupling: a transport timeout and a code cancellation share `OperationCanceledException`.
- **Layer:** P.

### E9. U2 (design/open-findings.md) – the M999 of the remote `DCS -u` update has no channel

- **Code:** `FirmwareUpdater.cs:464-470` vs `:248-252` (per the doc, not re-read). Reset is global but issued as a code on a channel.
- **Layer:** G.

## 9. Session and link

### F-A. Every reconnect or reset invalidates everything

- **Code (upstream):**
  - `LinkService.cs:388-396`, `:1499-1518`, `:1605-1622`.
  - `SPI.HadReset` (`SPI.cs:485-495`), `_hadReset` after every timeout (`:386-390`, 4a1ec34c).
  - USB on reconnect (`USB.cs:274-278`).
  - RRF `InvalidateResources` (`SbcInterface.cpp:232-238`, `:1717`).
- **Assumption:** nothing in flight can be attributed after a gap, so all of it is dropped (R2 of sbc-link-requirements.md asks for the opposite).
- **Layer:** P. **v8:** session id and per-channel sequence numbers with acknowledgements, so that a link gap does not end the session (R1, R2).

### F-B. Sequence guards (transfer resend)

- **Code:**
  - DSF fork #92: `SPI.cs:1979-2019`, swap `:408-419`, sequence 1 left to the restart detection (`:1979-1980`, 6de50c00).
  - RRF fork #35: `DataTransfer.cpp:924-995`, 8 KB `lastTxBuffer` (`:476-479`).
- **Open finding S4:** a lost connection while resending can send a transfer of the old session.
- **Layer:** L. **Firmware RAM:** 8192 bytes (PBUF pool) + 2 × 16-byte headers + a pointer and flags (`DataTransfer.h:170-176`).
- **v8:** an L-layer ARQ with session id.

### F-C. Phase guard and extended header (#93, RRF #35, #41)

- **Code:** `DataTransfer.cpp:826-922`; DSF `SPI.cs:58-64`, `:421-433`, `:501-517`; `Protocol/Shared/ExtendedLink.cs`.
- **Open findings:** S1 (two bit errors in the switch transfer), S5 (lost connection in the switch window), P4 (see F-E).
- **Layer:** L. **Firmware RAM:** 2 × 4 bytes capabilities (`DataTransfer.h:171`).

### F-D. Link findings F1–F11 (design/sbc-link-findings-upstream.md)

All are L layer. They exist because two lock-step state machines follow unprotected 4-byte responses:

| ID | Short form | Fork fix |
|---|---|---|
| F1 | A one-bit refusal stops DCS | #91 |
| F2 | An acknowledgement seen by one side only → reset | #92 |
| F3 | Phase desync livelock | RRF guards #35; #102 (60 s); #137 |
| F4 | BadResponse word | guards |
| F5 | Deadlock at connect | — |
| F6 | Restart hidden by the 16-bit wrap | extended header |
| F7 | Packets read past the transfer | #89 |
| F8 | Corrected version never sent | #59 |
| F9 | TfrRdy parity | #99 |
| F10 | Boot race | RRF #36 `SbcBootArmDelay`, `SbcInterface.cpp:46`, `:121-127` |
| F11 | DSF-only data CRC → full restart | #105 |

Also: `MaxSbcRetries` raised to 10 (#98); `SbcRestartTimeout` (#102, `SPI.cs:326-331`).

### F-E. P1, P2, P4 (stop path tests, open-findings.md)

- P1 and P2 are fixed by #137 (061f5a94).
- **P4 (open, plain link):** DSF repeats a transfer with the same number after "SPI data transfer failed", invalidates, and RRF keeps the session; the open trigger file blocks later triggers (`GCodes.cpp:423-433`). The fix would be case 2 of #57, which is on hold.
- The extended-link part is fixed by #138 (a94da3b9, `SPI.cs:451-459`).
- **Layer:** L/P. **v8:** session epoch exchanged at every reconnect.

### F-F. ST1 – a transfer that does not complete keeps DCS from stopping

- **Fix:** #149 (30035f3f), `LinkService.cs:596-598`.
- The coupling: the synchronous transfer thread also serves file I/O (R14).
- **Layer:** L.

## 10. RRF-internal task coupling (G, RRF)

### G1. Garbled macro request name

- **Symptom:** "Macro file trigger8.g not found" with an existing file (earlier fork analysis, fixed in an earlier fork RRF release).
- **Code (fork→upstream 6c53cc748):** `MutexLocker` in `RequestMacroFile` (`GCodeBuffer.cpp:1253`).
- **Assumption:** the per-GB request slot is shared between `CheckTriggers` and the SBC task.
- **Firmware RAM:** 0. **v8:** a request is a queued message object, not a shared slot.

### G2. Mutexes for cross-task writes (RRF #20)

- **Code:** pause (`GCodes.cpp:1211-1212`, 532e93696) and SetFinished (B9).
- **Firmware RAM:** 0.

### G3. The recursive GB mutex is held across the M98 wait

- **Code (upstream c5af8416b):** `GCodes.cpp:590`. Root of A4.
- **No catch code.** **v8:** InvalidateChannel and STOP must not need the GB mutex.

## 11. Other edge cases (open, no catch code)

- **H1. "Daemon: Out-of-order reply ''" within 2 s after a DCS start.** 18 times in 430 starts (lab-test-plan.md §9 line 323). Cause not shown; "probably a code of the old DCS instance, not proven".
- **H2. "Failed to find suitable stack level for flush request", 2 times during the T2 run.** Lab-test-plan.md §14, 2026-11-08 22:00. Code at `Processor.cs:568`, `:592`. Cause not analysed.
- **H3. Macros do not start on USB/USB2 without a host.** RRF's `DiscardPartialLine` probably resets a binary code from DSF on the same GB (open-findings.md; "likely cause, not proven"). This is a GB shared by two producers, with no reply and no timeout in DSF.
- **H4. S3 – RRF never recognises ObjectModelKeyChanged** (string header vs raw key, `SPI.cs:1690-1707` vs `SbcInterface.cpp:1319-1331`). A framing bug, not ordering. Listed for the message table.

## 12. Catch-code index (counted in section 1)

| # | Item | Layer | Origin | DSF / RRF | v8 property that removes it |
|---|---|---|---|---|---|
| 1 | A0 `_allFilesAborted` + silent RRF closes | P | upstream | both | abort ack + epoch |
| 2 | A1 `_allFilesAbortedSent` (#134) | P | fork | DSF | epoch |
| 3 | A2 `MacroFileClosed` discard (#158) | P | fork | DSF | epoch / level id |
| 4 | A2 `MacroFileClosedSent` in InvalidateChannel (RRF #45) | P | fork | RRF | epoch |
| 5 | A3 `MacroRequestSent` in InvalidateChannel (RRF #43) | P | fork | RRF | epoch, discrete requests |
| 6 | A4 `_allFilesAbortUnconfirmed`/`_allFilesAbortResent`, resend channel (#161) | P | fork | DSF | abort ack; per-channel NACK ordering |
| 7 | A6 `codeReplySurplus` (RRF #46) | P | fork | RRF | reply ids |
| 8 | A6 `invalidationPending` (RRF #46) | P | fork | RRF | abort ack (no copies) |
| 9 | A8 DSF M99 completion in `FilesAborted` | G | upstream | DSF | reply ids |
| 10 | A8 RRF empty M99 reply suppression | P | upstream | RRF | reply ids |
| 11 | A8 RRF dummy reply after single abort | P | upstream | RRF | reply ids |
| 12 | A8/B3 RRF drains (abort, WaitForAck) | P | upstream | RRF | reply ids |
| 13 | A9 `IsTriggerBusy` ‖ `IsAbortRequested` | G | fork→upstream | RRF | stack event ids |
| 14 | A10 file GB invalidated while `reportPause` | P | upstream | RRF | pause epoch |
| 15 | A10 DSF `PrintPaused` pops "RRF has done the same" | G | upstream | DSF | pause epoch |
| 16 | B1 replies → `BufferedCodes[0]`, out-of-order fallback | G | upstream | DSF | code ids |
| 17 | B2 `PendingReplies` | G | upstream | DSF | reply ids |
| 18 | B3 start code by position | G | upstream | DSF | request ids |
| 19 | B4 Unbuffered M122/M409 | G | upstream | DSF | reply ids / chunk ids |
| 20 | B5 `_waitingForPrintCompletion` | G | upstream | DSF | reply ids / event |
| 21 | B7 read-ahead invalidation on push (RRF drop + DSF suspend) | P | upstream | both | code ids + level tag |
| 22 | B8 implicit macro start by code arrival | P | upstream | RRF | MacroStarted with id |
| 23 | B9 SetFinished by targeted message (+ fork mutex) | P | upstream (+fork) | RRF | DoCode id |
| 24 | B11 legacy protocol < 3 empty-reply branches | G | upstream | DSF | drop |
| 25 | C1 ResendPacket by transfer position | P | upstream | both | per-channel seq + cumulative ack |
| 26 | C2 `_packetsBeingResent` | L | upstream | DSF | per-channel go-back-N |
| 27 | C3 USB resend `Previous` buffer (NEW-1) | L | upstream | DSF | ack by id |
| 28 | C4 `SpareBufferSpace` (#133) | P | fork | DSF | per-channel credits |
| 29 | C4 per-channel buffer share | P | upstream | DSF | credits |
| 30 | C5 `BytesReserved` | P | upstream | DSF | credits with seq |
| 31 | C6 Lock polling via resend | P | upstream | both | pending answer |
| 32 | D1 one OM query per transfer + `QuerySent` | P | upstream | DSF | query id |
| 33 | D1 OM key check (4dc381d0) | P | fork | DSF | query id |
| 34 | D2 eval/var by content | P | upstream | DSF | request ids |
| 35 | D3 file slot state + late replies + Take(0) | P | upstream (fork→upstream) | RRF | request ids |
| 36 | D4 link-capability activation via OM | L | fork | DSF | handshake in the link layer |
| 37 | E1 `transferredHaltRequest` (#88) | P | fork | DSF | STOP ack |
| 38 | E2 priority codes hop channels; macro M112 flushes | G | upstream | DSF | expedited class |
| 39 | E2 trigger actions with local M112 (RRF #25) | G | fork | RRF | (keeps the stop off the link) |
| 40 | E3 halt/reset ordering in `Execute` | P | upstream | DSF | expedited class |
| 41 | E6 RRF emergency stop on lost connection (#21) | P | fork | RRF | session survives gaps (R1) |
| 42 | E7 `requestsFromOtherTasks` (RRF #20) | G | fork | RRF | — (RRF-internal, stays) |
| 43 | F-A invalidate everything on reset/timeout | P | upstream | both | session + seq |
| 44 | F-B sequence guards DSF/RRF | L | fork | both | L-layer ARQ |
| 45 | F-C phase guard / extended header | L | fork | both | robust framing |
| 46 | F-D DSF-only link fixes (#59, #89, #91, #99, #102, #105, #137) and RRF #36 | L | fork | both | robust framing |
| 47 | G1 mutex in `RequestMacroFile` | G | fork→upstream | RRF | queued request objects |

How the rows add up to the counts in section 1:
- **P (28):** items 1–8, 10–12, 14, 21–23, 25, 28–35, 37, 40, 41, 43.
- **G (13):** items 9, 13, 15–20, 24, 38, 39, 42, 47.
- **L (6):** items 26, 27, 36, 44, 45, 46.
- **Fork-only (17):** P items 2–8, 28, 33, 37, 41; G items 39, 42; L items 36, 44, 45, 46.
- Items 13, 35 and 47 are fork→upstream and count as upstream.
- F-E, F-F, E4, E5, A5, A7, B6, B10 and section 11 are edge cases without their own catch code and are not counted.
- The split is a judgement per row. The writer may regroup.

## 13. Places where DSF matches by order or by timing windows instead of identity

| # | What | Where |
|---|---|---|
| 1 | Code reply → first buffered code of the channel | `Processor.cs:1108-1117` |
| 2 | Reply chunks joined by PushFlag, one partial per channel | `Processor.cs:1184-1215` |
| 3 | Reply channel = first matching destination bit | `Manager.cs:107-121` |
| 4 | Replies queued until a pop, then re-dispatched in arrival order | `Processor.cs:1119-1153`, `:870-876` |
| 5 | Macro start code = `BufferedCodes[0]` | `Processor.cs:1383-1388` |
| 6 | Message box start code = `BufferedCodes[0]` | `Processor.cs:1264-1271` |
| 7 | Closal and acknowledgement pop the current level and put the start code back at `[0]` | `Processor.cs:1292-1304`, `:1326-1335` |
| 8 | Single abort completes the M99 at `[0]`, pops one level | `Processor.cs:650-724` |
| 9 | Locked → first lock request in stack order | `Processor.cs:838-849` |
| 10 | Empty reply after print stop = print completion | `Processor.cs:1129-1134`, `:1155-1160` |
| 11 | System macro refused if the stack is not empty | `Processor.cs:1364-1370`, `:1390-1395` |
| 12 | PrintPaused: pop all file macros, "RRF has done the same" | `Processor.cs:1431-1444` |
| 13 | Read-ahead codes suspended on every push, resent after the pop | `Processor.cs:158-189`, `:991-1003` |
| 14 | Abort windows counted in transfers (1, 2 or until an RX without resend) | `Processor.cs:883-905`, `:934` |
| 15 | Halt complete one transfer after the write | `LinkService.cs:454-456`, `:599` |
| 16 | OM response → queue head; one query per transfer; key check as safety net | `LinkService.cs:470-478`, `:712-739` |
| 17 | Evaluation result by (channel, expression) | `LinkService.cs:1005-1023` |
| 18 | Variable result by (channel, name); stop at the first unwritable | `LinkService.cs:1123-1141`, `:572` |
| 19 | Buffer space = report − bytes sent since (pipeline depth one) | `LinkService.cs:747-753`, `:452` |
| 20 | Resend copy found by transfer position (SPI two back, USB one back) and per-transfer packet id | `SPI.cs:823`, `USB.cs:721` |
| 21 | Same-type block for one transfer after a resend | `SPI.cs:305`, `:841-845` |
| 22 | Restart = sequence distance ≠ 1; any timeout = reset | `SPI.cs:485-495`, `:386-390` |
| 23 | Sequence guard: distance 0 = resend last, 2 = wait, number 1 = maybe restart | `SPI.cs:1979-2019` |
| 24 | Extended-link switch given up after 16 transfers | `SPI.cs:64`, `:421-433` |
| 25 | M112 and M999 4.5 s, livelock 60 s, retries 10 | `MCodeHandler.cs:757`, `:1369`; `SPI.cs:326-339` |
| 26 | Priority code moved to "an idle channel" so it is not queued behind others | `CodeProcessor.cs:209-257` |
| 27 | M122/M409 Unbuffered so that nothing follows before their chunks | `CodeProcessor.cs:202-207` |
| 28 | Link capability check by OM query order (offered, then active) | `LinkService.cs:1539-1575` |

On the RRF side the counterparts are listed in section 2.2 and items B7, B8, B9, A8, D3. In short:
- `FillBuffer` holds codes back until a stack event has been sent (`SbcInterface.cpp:1892-1898`).
- One-ExchangeData invalidation (`:1495-1504`, `:376-380`).
- Drains (`:1543-1549`, `:1582-1585`) and the dummy reply (`:1564-1573`).
- `fileOperation` state (`:1146-1285`).
- `IsConnectionReset` (`DataTransfer.h:212-215`).
- 3 s request timeouts (`GCodeBuffer.cpp:1266`).
- The 10 ms mutex yield that turns into resends (`SbcInterface.cpp:45`).

## 14. RAM the RRF SBC interface uses today (MB6HC, SAME70)

Measured by Tim on 2026-11-09: static 131040, dynamic 105276, never used 95868, bss 126792. Not measured here; this section lists what the code allocates.

| Item | Size | Where | Notes |
|---|---|---|---|
| SPI/USB RX buffer | 8192 B | `DataTransfer.cpp:459-465`, `SbcMessageFormats.h:26` | SAME70: taken from the lwIP PBUF pool, static non-cached RAM that SBC builds do not use for Ethernet (`:456-458`, `LwipEthernetInterface.cpp:93-118`). Not heap |
| SPI/USB TX buffer | 8192 B | `DataTransfer.cpp:467-473` | as RX; one buffer, no copies |
| Sequence guard last-TX buffer (fork #35) | 8192 B | `DataTransfer.cpp:476-479` | PBUF pool. Sequence guard offered only if it fits (`:478`). An earlier fork note (not in the repository) says the non-cached RAM is then full |
| Without lwIP (other boards) | 2 × 8192 B heap | `DataTransfer.cpp:483-484` | — |
| SPI headers and responses | 2 × 16 + 2 × 4 B static non-cached | `DataTransfer.h:130-133`, `.cpp:415-418` | — |
| USB headers | 2 × 8 B static non-cached | `DataTransfer.h:157-158`, `.cpp:420-421` | — |
| Sequence guard headers (fork) | 2 × 16 B + pointer, size, id, 2 bools | `DataTransfer.h:170-176` | inside `DataTransfer` |
| Capabilities (fork) | 2 × 4 B | `DataTransfer.h:171` | — |
| Shared code buffer | 4096 B heap | `SbcInterface.cpp:76`, `SbcMessageFormats.h:47` | one ring for all channels; `rxPointer`, `txPointer`, `txEnd`, `sendBufferUpdate` (`SbcInterface.h:101-103`) |
| SBC task stack | 1400 words = 5600 B (1600 words in debug) | `SbcInterface.cpp:39-43`, `:78-79` | plus the Task/TCB object. Priority 2 (`TaskPriorities.h:17`) |
| `SbcInterface` object | not summed | heap, `RepRap.cpp:472` | contains `DataTransfer`, `GCodeFileInfo fileInfo` (`SbcInterface.h:96`), 2 mutexes and 2 binary semaphores (`:111-114`, `:156`), the file-operation slot (about 15 fields, `:116-153`), the background write (`:148-152`) and the reply FIFO |
| Reply FIFO `gcodeReply` | 64 × (4 B pointer + 4 B `MessageType`) + count | `SbcInterface.h:155`, `OutputMemory.h:15-19`, `:178-179` | **Every** `OutputStack` gets depth 64 instead of 4 when `HAS_SBC_INTERFACE`: AuxDevice (`AuxDevice.h:91`), NetworkResponder (`NetworkResponder.h:87`), USB device (`UsbDeviceRrf.h:44`), HttpResponder static (`HttpResponder.h:137`). Which of them exist at run time in SBC mode was not checked. The reply payloads come from the shared OutputBuffer pool (40 × 256 B, `Configuration.h:179-180`) |

**Per GCodeBuffer** (up to 15 channels, `GCodeChannel.h:20-22`; channels absent in a build are null, `SbcInterface.cpp:1488-1493`):

| Field | Size | Where | Origin |
|---|---|---|---|
| `macroSemaphore` (BinarySemaphore = FreeRTOS `StaticSemaphore_t`) | not measured | `GCodeBuffer.h:398`, `RRFLibraries RTOSIface.h:148-150` | upstream |
| `isWaitingForMacro`, `macroFileClosed` | 2 B | `GCodeBuffer.h:399-400` | upstream |
| `requestedMacroFile` `String<120>` | 121 B | `GCodeBuffer.h:403`, `Configuration.h:143`, `String.h:62` | upstream |
| `isBinaryBuffer` | 1 B | `:404` | upstream |
| `codeReplySurplus` | 1 B | `:405` | fork #46 |
| `macroJustStarted`, `macroFileError`, `macroFileEmpty` | 1 B (bitfield) | `:407-410` | upstream |
| `abortFile`, `abortAllFiles`, `sendToSbc`, `messagePromptPending`, `messageAcknowledged` | 5 B | `:414-418` | upstream |
| `invalidated` | 1 B | `:421` | upstream |
| `invalidationPending` | 1 B | `:422` | fork #46 |
| `BinaryParser binaryParser` | about 28–32 B (reference, `size_t`, pointer, int, `Bitmap<uint32_t or uint64_t>`, 2 pointers) | `GCodeBuffer.h:359-361`, `BinaryParser.h` private section, `RepRapFirmware.h:433-436` | upstream |

Per GCodeMachineState (per stack level): `fileId` (1 B) and 3 bits `lastCodeFromSbc`, `macroStartedByCode`, `fileFinished` (`GCodeMachineState.h:237-239`, `:277-281`). Upstream.

Rough total: about 165 B + one semaphore per GB, so about 2.5 KB + 15 semaphores for 15 GBs. Not measured; padding is not included.

**Per-packet and per-request bookkeeping on RRF: none.**
- The resend writes only an 8-byte header with the refused packet's id (`DataTransfer.h:238-241`) and keeps no copy. TX space for it is reserved per received packet (`:228-231`).
- `packetId` is one `uint16_t` (`DataTransfer.h:168`).
- Outstanding requests are represented by the per-GB flags above (one macro request per GB, one prompt, one abort, one closal), the single file-operation slot and the reply FIFO.
- There is no table of outstanding requests and no ids.

**Firmware state added by each catch code:**

| Item | Firmware RAM |
|---|---|
| A1, A4, B1–B5, C2–C5, D1, D2, E1, E3 | 0 (DSF only) |
| A2 (RRF #45), A3 (RRF #43) | 0 (they clear existing fields) |
| A6 (RRF #46) | 2 bool per GB (≤ 30 B) |
| A8 drains, dummy reply, M99 suppression | 0 |
| A9 | 0 |
| B7 | `invalidated`, 1 B per GB (upstream) |
| D3 | 0 |
| E2 trigger actions (RRF #25) | `triggerActionRunning` 1 B + action storage per trigger (`TriggerItem.h` diff, +19 lines, not sized) |
| E6 | 0 |
| E7 | 4 B |
| F-B sequence guard | 8192 B PBUF + about 45 B |
| F-C capabilities | 8 B |
| F-D `SbcBootArmDelay` | 0 (constexpr) |
| G1 | 0 |

**Implication for v8:**
- The v7 catch code costs RRF almost no RAM. The cost is in correctness.
- Whatever v8 adds per channel (sequence number, epoch, last acknowledged id, current code id) is a few bytes × 15.
- Any reorder buffer or retransmit copy on the RRF side would cost transfer-buffer-sized RAM. The non-cached PBUF pool is already used for the third 8 KB buffer.

## 15. Open questions that the code did not settle

1. **NEW-1 (USB pipeline):** is RRF's USB response really composed one transfer behind, as `DataTransfer.cpp:1362-1371` and `SbcInterface.cpp:267-268` read? If so, `USB.cs:717-721` is wrong (resend of the wrong packet, or a DCS stop on `ArgumentException`) and T13 is void. Needs a USB test; the CHX has no USB link.
2. **NEW-2:** can the 64-entry reply FIFO fill in practice? For example a stalled link with several channels producing replies, or M122 chunks.
3. **NEW-3:** can RRF refuse a code that DSF's buffer estimate allowed, given the ring layout and defragmentation? If yes, a same-channel code from the in-between transfer overtakes the copy.
4. **SPI ResendPacket after a sequence-guard resend:** an earlier fork review (not in the repository) notes an off-by-one of the buffer lookup in that case. Not re-verified.
5. **SetPrintFileInfo alignment vs ActivateLinkCapabilities written first in the same transfer:** does RRF's 64-bit `time_t` read fault at a 4-byte offset (`DataTransfer.cpp:669`)?
6. **RRF window for M122/M409 chunks overtaken by a macro request:** named in an earlier fork analysis (not in the repository) but not shown. It decides whether DSF's Unbuffered marking (B4) can go.
7. **H1 and H2:** causes not shown.
8. **T10 leftovers** (open-findings.md T10 on #163): whether v8 has to cover state machines such as G28 that continue after InvalidateChannel. InvalidateChannel does not call `AbortStateMachine`.
9. **Sizes not measured here:** `StaticSemaphore_t`, `GCodeFileInfo`, `SbcInterface` and `TriggerItem` growth of RRF #25. A map file or `sizeof` print would settle them.

## 16. ID collisions in the existing docs (for the writer)

| ID | Meaning 1 | Meaning 2 |
|---|---|---|
| U2 | dsf-findings-upstream.md: IAP stops answering → M997 silent | open-findings.md: M999 of remote `DCS -u` has no channel |
| P1–P3 | sbc-link-findings-upstream.md: parser and reader findings | open-findings.md: stop path findings P1–P4 |
| L1 | sbc-link-findings-upstream.md: journal priority | open-findings.md: shutdown, code stuck in Firmware stage |
| O1 | sbc-link-findings-upstream.md: verbose board fields | open-findings.md: JSON update paths |
| S1–S4 | sbc-link-current-state.md: safety items | open-findings.md: SPI link findings S1–S5 |
| T1–T7 | sbc-link-current-state.md: timing | open-findings.md / dsf-findings-upstream.md: trigger findings |
| T11 | open-findings.md: the daemon.g case | dsf-findings-upstream.md: the resend case |

The v8 document should qualify IDs by their source document.
