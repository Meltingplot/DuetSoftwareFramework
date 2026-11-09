# SBC link v8: session and channel layer requirements

Status: draft 1, 2026-11-09. Nothing here is agreed or implemented. No code was changed for it.
Audience: Tim, later Christian Hammacher (Duet3D). The reader knows DSF and RRF, not the working papers.
Scope: the layer between the link and the logic: channels, ordering, priority and overtaking, cancellation,
correlation, flow control, and retransmission above the link.
Not in scope: everything in [sbc-link-requirements.md](sbc-link-requirements.md) (v2: framing, integrity, link
retransmission, recovery, the stop path on the wire, the dead-man switch), IAP flashing (v2 C2), G-code semantics.

Goal (Tim, 2026-11-09):
> We keep finding edge cases that are tied to the strict ordering of the protocol. At the same time some commands
> must legitimately overtake (M112). I believe the problem comes from a wrong separation of protocol and channels.
> My goal is to remove our edge-case catch code from the codebase in the medium term. Follow best practices of
> mainstream high-volume protocols, and require as few changes to the logic code as possible.

Constraint (Tim: "the other end is an embedded system with RAM constraints"): RRF runs on a SAME70 with fixed RAM.
M122 on the lab printer on 2026-11-09 06:4x UTC (MB6HC, RRF 3.7.0+1.mp.9-dev.3, CHX configuration), read for this
document: "RAM: static 131040, dynamic 105276 (1744 recycled), never used 95868, free sys stack 172", heap 6144/5036/612.

## 0. Basis and conventions

- **Code:** DSF `v3.7-dev` at 7f26d0ca. `src/DuetControlServer` is unchanged since 808d56fa, where the catalogue
  was made. RRF fork at 0dccb9efe, whose tree equals fork `3.7-dev` ddec2d001 (the DSF submodule pin).
- **Paths:** `DCS/` = `src/DuetControlServer/`. `RRF/` = RRF `src/`. Line numbers refer to the commits above.
- **PRs:** `#n` = Meltingplot/DuetSoftwareFramework, `RRF #n` = Meltingplot/RepRapFirmware.
- **Working papers** (2026-11-09; this document repeats every code reference it needs):
  - [sbc-link-v8-catalogue.md](sbc-link-v8-catalogue.md): 47 catch-code items. `CC-n` here is row n of its
    catch-code index (section 12), numbered so to keep them apart from PR numbers. Its letter IDs (`A1`, `B7`,
    `NEW-1`) are kept;
  - [sbc-link-v8-patterns.md](sbc-link-v8-patterns.md): 12 patterns `P1`–`P12` from mainstream protocols.
- **IDs of other documents are qualified,** because several collide:

| Prefix | Document |
|---|---|
| `v2 Rn`, `v2 Cn` | [sbc-link-requirements.md](sbc-link-requirements.md) |
| `CS-` | [sbc-link-current-state.md](sbc-link-current-state.md) |
| `SF-` | [sbc-link-findings-upstream.md](sbc-link-findings-upstream.md) |
| `DF-` | [dsf-findings-upstream.md](dsf-findings-upstream.md) |
| `OF-` | [open-findings.md](open-findings.md) |
| `LT §14` | [lab-test-plan.md](lab-test-plan.md), section 14 (lab runs) |

- **Status of statements:** "printer" = seen on the lab printer, with the run in LT §14. "Code" = from reading the
  code, not tested. Everything open is in section 10.
- **Keywords:** MUST, SHOULD, MAY as in RFC 2119.

## 1. Relation to v2 and to the decision "no fork-only v8"

- v2 covers the link layer. v8 needs exactly two things from it:
  - Each transfer is delivered once and in order within a link session (v2 R2). On SPI this is the extended link
    with `SequenceGuard`; the census shows it for single faults after the activation (v2 "Implementation status").
    Multiple faults and the bench test are open. On USB it rests on the USB transport (CS-K2); not verified.
  - A session change is visible: the 11-bit session status of the extended header on SPI (v2), a reconnect on USB
    (`DCS/Link/Adapter/USB.cs:257-280`).
- v2 R10 (stop within 100 ms on a disturbed link) stays a link requirement. v8 only defines what "expedited" means above
  the link (V8-19, V8-20).
- The v2 decision log (2026-10-08) says: "Negotiation on top of v7 instead of a fork-only v8", because mixed versions
  keep working and there is no protocol-number collision with upstream (v2 C1).
- **This document keeps that decision.** "v8" names a session layer that is negotiated as a new link capability
  on top of the v7 framing. The transfer header keeps protocol version 7 (`RRF/SBC/SbcMessageFormats.h:24`).
  A real version bump is decision D1.

## 2. Problem statement

v7 has channels only in the logic: the channel sits in request-specific payloads, and `PacketHeader` has none
(`RRF/SBC/SbcMessageFormats.h:101-107`). Below that, the protocol knows only transfer positions. Each catch code
re-derives "which event belongs to what" from position or timing. The catalogue counts 47 catch-code items:
28 in the session layer, 13 in the logic, 6 in the link; 17 are fork-only.

| # | v7 assumption | Where | Evidence (what broke) | Catch code today |
|---|---|---|---|---|
| A-1 | Replies and stack events belong to the first buffered code of the channel (`BufferedCodes[0]`) | `DCS/Link/Channel/Processor.cs:1108-1117`, `:1264-1271`, `:1383-1388`, `:655-665`; channel = first matching destination bit, `DCS/Link/Channel/Manager.cs:107-121` | DF-T10: the reply of a dropped M98 completed the next `G4 P0` (printer, 2026-11-08). OF-T4 out-of-order trigger message. NEW-2: the shared reply FIFO drops a reply silently when full (`RRF/Platform/OutputMemory.cpp:487-506`), code, not observed | RRF drains before abort and prompt (`RRF/SBC/SbcInterface.cpp:1543-1549`, `:1579-1590`), dummy reply (`:1564-1573`), `codeReplySurplus` (RRF #46), DSF `PendingReplies`, Unbuffered M122/M409 (`DCS/Codes/CodeProcessor.cs:202-207`) |
| A-2 | The pipeline depth is exactly one transfer, counted, never acknowledged | RRF reads DSF's packets of transfer N, then writes what DSF reads in N+1 (`RRF/SBC/SbcInterface.cpp:257-268`, `:310-1359` before `:1389-1643`). USB does the same (`RRF/SBC/DataTransfer.cpp:1302-1357`, `:1362-1371`) | DF-T2, DF-T12 (DCS stopped, printer 2026-11-08 22:01). NEW-1: the USB resend looks one transfer back (`DCS/Link/Adapter/USB.cs:717-721`), against the RRF code; then OF-T13 does not exist. Code, not tested | #134, #158, #88, `BytesReserved` (`DCS/Link/LinkService.cs:452`, `:747-753`), one-ExchangeData read-ahead invalidation (B7) |
| A-3 | Abort (`InvalidateChannel`) needs no answer and no epoch | RRF closes files silently (`FileAbortSent`, `RRF/SBC/SbcInterface.cpp:903`), drops only ring codes (`:905`); the running code goes on | DF-T1, DF-T2, DF-T10, DF-T11, DF-T12; OF-T11 (daemon.g), OF-T10 open cases | #134, #158, #161, RRF #43, #45, #46 |
| A-4 | `ResendPacket` is NACK, back-pressure and polling at once, and the copy is reordered | RRF goes on with the later packets (`RRF/SBC/SbcInterface.cpp:1354-1358`). The copy goes into a later transfer (`DCS/Link/Adapter/SPI.cs:821-856`). Side effects before the refusal: InvalidateChannel ends the macro wait (`:883-895`) before the mutex (`:897`); a Code packet resolves a macro wait (`:382-392`) before its refusal (`:394-399`) | DF-T11 resend (printer 2026-11-08 22:39; 2 resends in 2000 aborts on 2026-11-09, LT §14). NEW-3: a refused code overtaken by a later code of its channel, code | #161, `_packetsBeingResent` (`DCS/Link/Adapter/SPI.cs:94`, `:305`, `:841-845`, upstream 86eee4ac0) |
| A-5 | Shared resources without per-channel credit | One 4096-byte code ring (`RRF/SBC/SbcInterface.cpp:76`, `SbcMessageFormats.h:47`); one 64-entry reply FIFO for all channels (`RRF/SBC/SbcInterface.h:155`, `RRF/Platform/OutputMemory.h:15-19`); one file-operation slot (`SbcInterface.h:110-153`) | DF-T3: trigger2.g's M98 and M112 held back (printer 2026-11-08) | #133 `SpareBufferSpace` (`DCS/Link/LinkInterface.cs:40-44`, `:570-583`) |
| A-6 | The stop is a packet in the lock-step transfer, completed by counting | `DCS/Link/LinkService.cs:324-335`, `:454-456` (#88); a macro M112 flushes first (`DCS/Codes/Handlers/MCodeHandler.cs:750`) | OF stop path tests: about 503 ms worst case behind a long code transfer that noise keeps failing; OF-P3: M112 replies before RRF processed the stop (lead ≤ 0.05 ms) | #88, priority codes moved to an idle channel (`DCS/Commands/Generic/SimpleCode.cs:110-116`) |

The latest lab evidence: on 2026-11-09 `AbortsCrossingMacroRequests` passed 2000 aborts with all fixes, but still
logged 2 InvalidateChannel resends, one out-of-order "Macro file … not found" after each, and 249 empty out-of-order
replies (LT §14, 06:23:59). The catch code works; the protocol still produces the events it catches.

## 3. Principles and layering

| Layer | Owns | State | Does not |
|---|---|---|---|
| L: link (v7 framing + v2 capabilities) | Transfers, CRC, response codes, transfer sequence and phase guards, transfer retransmission, session status | Per link | Know channels or request types |
| S: session/channel (new, thin) | Packet header fields channel, epoch, sequence; message classes; ordering; ACK/NAK; credit; RESET handshake; stale-epoch filter; request ids | Fixed table of 16 channel entries on each side | Take logic mutexes in the packet loop, except to hand over logic requests (V8-7) |
| G: logic (unchanged where possible) | G-code execution, macro stack, object model, files | `GCodeBuffer`, `Processor` | See transfers, packet ids or pipeline depth |

| ID | Principle | Followed by |
|---|---|---|
| PR1 | Identity, not position or timing. Late frames are discarded by identity; timers are not used to decide that they stopped. | HTTP/2 RFC 9113 §5.1 ("SHOULD NOT use timers"), §6.4; JSON-RPC 2.0 §4; Modbus TCP guide §4.4.1.3; USB MSC BOT §5.1 |
| PR2 | Order is kept within a channel, never across channels except at named barriers. | QUIC RFC 9000 §2; SCTP RFC 9260 §1.5.2; SSH RFC 4254 §4; AMQP 0-9-1 §4.6 |
| PR3 | A few absolute classes; one expedited class that is never refused and never deferred. | RFC 9218 §4.1 (after RFC 9113 §5.3.2 deprecated the tree); CAN 2.0 Part A §2; USB 2.0 §8.4.6.4; caution RFC 6093 §5 |
| PR4 | Cancellation is a handshake that changes an epoch; repeated requests get the same answer. | RFC 9113 §6.4; RFC 9000 §3.5, §4.4, §13.3; AMQP 0-9-1 §2.3.7; SCTP RFC 6525 §5.2.1 |
| PR5 | Flow control by absolute, monotonic credit; control traffic is exempt. | RFC 9000 §4.1, §4.2; AMQP 1.0 Part 2 §2.6.7; RFC 9113 §5.2.1; MQTT 5.0 §4.9 |
| PR6 | One retransmission mechanism per layer. A refusal means "nothing was done". Re-sent content is identical. | RFC 9113 §8.7; MQTT 5.0 §4.4; USB 2.0 §8.6.2; RFC 9000 §2.2 |
| PR7 | Request state lives on the (channel, epoch) object and dies with it. | RFC 9000 §4.4; CLAUDE.md "Keep state on the object whose lifetime it shares" |
| PR8 | The firmware never retransmits and keeps no per-message state. DSF holds every copy. | Pattern survey §6; RRF keeps no copy today (`RRF/SBC/DataTransfer.h:238-241`) |

## 4. Requirements

Columns: **Removes** = catalogue items (`CC-n`) whose catch code is no longer needed in a v8 session. **RAM** = new
static RRF RAM; arrays have 16 entries (15 channels, `RRF/GCodes/GCodeChannel.h:20`, plus alignment). **Logic** =
change to the G layer.

### 4.1 Layering, header, classes

| ID | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|
| V8-1 | Both programs MUST have an S layer between L and G as in section 3. L MUST NOT depend on request types; G MUST NOT depend on transfers, packet ids or pipeline depth. | P12; AMQP 1.0 Part 2 §2.1.1 | enables all | 0 | none (code moves into S) |
| V8-2 | Every packet MUST carry request, length, channel, epoch and sequence in a fixed, 4-byte-aligned header. Global packets use a reserved channel value. Receivers use only the header channel; v7 payload channel fields keep their place and are written equal. Illustration: request (2), length (2), channel (1), epoch (1), sequence (2) = 8 bytes, the size of the v7 header, because `id` and `resendPacketId` are not needed (V8-21). | P1; RFC 9113 §4.1; AMQP 0-9-1 §2.3.1, §4.2.3 | — | 0 | none |
| V8-3 | Every request type MUST belong to exactly one class (X, C, D, G, section 5) through one fixed table in this spec. No header bit duplicates it. | P5; RFC 9218 §2 | — | 0 | none |
| V8-4 | The may-pass table of section 5 is normative. D packets of one channel and direction MUST be processed in sequence order. Packets of different channels MAY pass each other, except at the barriers listed there. | P2, P11; PCIe ordering table (secondary sources only) | — | 0 | none, if the barrier list is complete (Q7) |

### 4.2 Sequence, acknowledgement, refusal

| ID | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|
| V8-5 | DSF MUST number D and G packets per (stream, epoch) from 0, 16 bits, compared by RFC 1982 §3.2. A stream is one channel, or the global stream. RRF MUST report per stream the next sequence it expects, as an absolute value, whenever it changed. DSF keeps every packet until it is acknowledged. | P3; RFC 9260 §6.5; AMQP 1.0 Part 2 §2.8.10 | CC-25 (resend lookup by transfer position, `DCS/Link/Adapter/SPI.cs:823`), CC-27 (NEW-1, `USB.cs:717-721`) | 32 B (u16 × 16) | none |
| V8-6 | "Not accepted now" MUST be an acknowledgement with a stall flag at sequence n. RRF then drops later packets of that stream until n arrives again. DSF resends n, n+1, … of that stream only, with identical content. Other streams go on. | P3, P9; USB 2.0 §8.6.2; MQTT 5.0 §4.6; RFC 9000 §2.2 | CC-26 (`_packetsBeingResent`), NEW-3; CC-31 (Lock polled by resend, `SPI.cs:1107-1120`, `LinkService.cs:615-619`) becomes a plain stall | 2 B (stall bitmap) | none. Lock keeps its v7 meaning: the channel waits until standstill (`RRF/SBC/SbcInterface.cpp:607-634`) |
| V8-7 | RRF MUST NOT refuse a packet that has had an effect. Handlers that today act before they refuse (InvalidateChannel `RRF/SBC/SbcInterface.cpp:883-895`, Code `:382-392`) MUST act only once accepted, or never refuse. | P9; RFC 9113 §8.7 | root of DF-T11 resend | 0 | small: statement order in RRF `SbcInterface::ExchangeData` only |
| V8-8 | ACK, NAK, credit and RESET_ACK SHOULD be one C-class "channel state" report with absolute values (epoch, next sequence, stall, credit), so a repeated or reordered report is harmless. It replaces `ResendPacket` (0) and `CodeBufferUpdate` (2). RRF reserves TX space for it as it reserves space for resend headers today (`RRF/SBC/DataTransfer.h:228-231`). | P4 pitfalls; RFC 9000 §4.2 | — | 2 B (dirty bitmap) | none |

### 4.3 Cancellation and epochs

| ID | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|
| V8-9 | InvalidateChannel MUST become RESET(c, e+1), and RRF MUST never refuse it. On receipt, without the GB mutex, RRF records e+1, ends a macro wait, and purges the ring codes of c (`InvalidateBufferedCodes` needs only a critical section, `RRF/SBC/SbcInterface.cpp:2527-2529`). With the GB mutex, later, it applies `PauseSequenceAborted`, `AbortFile(true)` (`:899-902`). Then it sends RESET_ACK(c, e+1). A repeated RESET(c, e+1) gets the same ACK. DSF sends no D packet on c between RESET and RESET_ACK. | P6; RFC 9113 §6.4; RFC 9000 §3.5, §13.3; RFC 6525 §5.2.1; AMQP 0-9-1 §2.3.7 | CC-1 (silent closes), CC-6 (#161: `Processor.cs:126-136`, `:883-905`; `ILinkAdapter.cs:203`; `LinkService.cs:620-626`), CC-8 (`invalidationPending`, `GCodeBuffer.h:422`) | 48 B (epoch, pending epoch, flags × 16) | small: the RRF InvalidateChannel handler is split into receipt and apply, with today's calls (`:880-911`). Behaviour as with #161: the channel waits for RRF's answer, now an explicit one |
| V8-10 | Every D packet MUST carry an epoch; receivers MUST drop D packets of another epoch and count them. RRF MUST tag a message with the epoch of the code or request it belongs to, taken when the code was handed to the GB (`FillBuffer`, `:1890-1950`) or the request was made, not with the current channel epoch. The tag of a queued reply is captured when it is queued (`HandleGCodeReply`, `:2353-2406`). | P6, P7; RFC 9000 §4.4 | CC-2 (#134, `Processor.cs:119-124`, `:888-892`, `:1357-1362`), CC-3 (#158, `:1285-1290`), CC-4 (RRF #45, `SbcInterface.cpp:904`), CC-7 (RRF #46 `codeReplySurplus`: `GCodeBuffer.h:405`, `GCodes.cpp:4450-4458`, `:4584-4593`, `:3703-3708`); A7 (OF-T13) | 80 B (code epoch per GB 16 B, tag per FIFO entry 64 × 1 B) | small: if every message of a code passes one capture point. CLAUDE.md rule 3 prefers passing the identity as a parameter down the reply path (`GCodes.cpp` reply functions); open which writers bypass it (Q6) |
| V8-11 | Per-GB request state (`requestedMacroFile` `GCodeBuffer.h:403`, the abort, prompt and closal flags) MUST be bound to the epoch it was created in. RRF MUST NOT send a request of an older epoch; it resolves it locally as failed. This covers state machines that go on after a RESET (OF-T10: G28, `GCodes4.cpp:358-413`). | P6 "what epochs do not fix"; PR7 | CC-5 (RRF #43, `SbcInterface.cpp:891-894`); A5 (OF-T11 daemon.g) | 16 B (request epoch per GB) | small: one compare where requests are written (`:1513-1527`, `:1597-1611`) |
| V8-12 | An RRF abort of all files (`AbortFile`, abortAll) SHOULD be a request for a RESET, which DSF answers with RESET(c, e+1). DSF stays the only writer of epochs. | RFC 9000 §3.5 (STOP_SENDING) | the one-transfer drop after the abort request (`gb->Invalidate()`, `:1575`) for abort-all | 0 | small: DSF `HandleAbortFileRequest` (`LinkService.cs:866-889`) issues the RESET after `FilesAborted(true)` |

### 4.4 Correlation

| ID | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|
| V8-13 | Global requests and their answers MUST carry an id that the answer echoes: GetObjectModel/ObjectModel, RRF file requests (17–27) and DSF file results (22–28, 31). Evaluations and variables echo the sequence of their D packet. An answer without a pending id is dropped and logged as a protocol fault. | P8; JSON-RPC 2.0 §4, §6; Modbus TCP guide §4.4.1.3 | CC-32 (one query per transfer, `QuerySent`, `LinkService.cs:470-478`, `:714`), CC-33 (fork key check 4dc381d0, `:716-723`), CC-34 (content match, `:1005-1023`, `:1123-1141`), CC-35 (file slot by state, `SbcInterface.cpp:1204-1207`, `:1221-1224`) | 1 B in the file slot. OM and evaluation answers are written while the request is processed (`:443-494`, `:682-760`), so the id is copied | none |
| V8-14 | Code replies and stack events (ExecuteMacro with fromCode, WaitForAcknowledgement, single AbortFile, MacroFileClosed, Locked, DoCode results) SHOULD carry the (epoch, sequence) of the code they belong to. DSF matches by identity, not by `BufferedCodes[0]`. RRF keeps the sequence of the code that opened a stack level next to the existing "identity of the command that invoked this macro frame" (`RRF/GCodes/GCodeMachineState.h:250-256`). Later milestone (D3). | P8 option (b) | CC-9–CC-12 (M99 family: `Processor.cs:650-666`, `GCodes.cpp:4460-4465`, `SbcInterface.cpp:1564-1573`, drains), CC-16–CC-20 (position match, `PendingReplies`, start code by position, Unbuffered M122/M409, `_waitingForPrintCompletion`), CC-22, CC-23, CC-24; NEW-2 becomes detectable | 224 B static (code sequence per GB 32 B, FIFO tag grows to 4 B: +192 B) plus 2 B per pushed level inside the heap-allocated `GCodeMachineState` (`GCodeBuffer.cpp:1061`), ≤ 300 B at full depth on all channels (`MaxStackDepth` 10, `Config/Configuration.h:243`) | medium: DSF `Processor` matching (`:1102-1168`, `:1264-1305`, `:1373-1389`, `:650-724`); RRF `GCodeBuffer` push/pop carries one more field |

### 4.5 Flow control

| ID | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|
| V8-15 | RRF MUST advertise an absolute, monotonic credit limit for code bytes: released + capacity, where "released" counts every code byte RRF received and no longer holds (taken by a GB, purged, or dropped on arrival). 16 bits, RFC 1982. RRF MUST NOT refuse a code within the limit. X and C packets and all RRF→DSF traffic are exempt. | P4; RFC 9000 §4.1, §4.2; AMQP 1.0 Part 2 §2.6.7; RFC 9113 §5.2.1; RFC 4254 §5.4 | CC-30 (`BytesReserved`, `LinkService.cs:452`, `:747-753`; `LinkInterface.cs:37`), the cascade refusal (`codeBufferAvailable`, `SbcInterface.cpp:394-414`), NEW-3 | 4 B | none |
| V8-16 | The advertised capacity MUST be storable after fragmentation. Today RRF reports the largest contiguous block (`:1394-1399`) and defragments only when 384 bytes or less are free (`:2436-2446`). How the guarantee is met is open (Q4). | P4 pitfalls | — | 0 | none |
| V8-17 | Per-channel limits and a reserve for time-critical channels MUST exist, so codes buffered ahead cannot take the room of a trigger macro. Where they live is decision D4. | P4; RFC 9000 §4.1; USB 2.0 §5.5.4 | CC-28 (#133, `LinkInterface.cs:40-44`, `:570-583`; `Processor.cs:1076-1079`), CC-29 (share 82d581d1, `LinkInterface.cs:52`) | 0 (D4 a) or 64 B (D4 b) | none |
| V8-18 | A reply MUST NOT be dropped silently. If the reply FIFO is full (`OutputMemory.cpp:487-506`), RRF SHOULD record the loss per channel and report it with the next message of that channel. | P8 pitfalls | — (NEW-2 has no catch code) | 2 B | open: who waits when the FIFO is full |

### 4.6 Expedited class and stop

| ID | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|
| V8-19 | EmergencyStop and Reset are class X. DSF MUST write them first in a transfer (as `LinkService.cs:324-354` does). RRF MUST execute them on read without a mutex (as `SbcInterface.cpp:336-344` does). They are never refused, never credit-limited, never part of a stream, and idempotent through a stop number. Getting them through a disturbed data phase is v2 R10. | P5; CAN 2.0 Part A §2; USB 2.0 §8.4.6.4; RFC 9260 §6.6 | CC-40 in part (halt and reset ordering in `Execute`) | 1 B | none |
| V8-20 | RRF MUST answer EmergencyStop with STOP_ACK(stop number), written after `reprap.EmergencyStop()` returned. DSF completes M112 on STOP_ACK instead of one transfer later. | P6/P11 (explicit fence) | CC-37 (#88, `LinkService.cs:315-317`, `:454-456`, `:599`); OF-P3 | 1 B | none |
| V8-21 | v8 sessions MUST NOT use `ResendPacket` or `resendPacketId`. The link repeats transfers (v2), S has only V8-6, RRF keeps no copy. | P9; MQTT 5.0 §4.4; RFC 8260 §1.1 | with V8-5 and V8-6: CC-25–CC-27 | 0 | none |
| V8-22 | An M112 read from a macro MUST keep its flush (`MCodeHandler.cs:750`). The wiki tells users to put M81 before M112 in a trigger macro (`wiki-content/User_manual/Reference/Gcodes.md:2682`), so order inside a macro matters. The fast path for door and E-stop logic is the RRF-local trigger action (rrf-local-trigger-actions.md TA1, RRF #25). With V8-17 the flush no longer waits for room taken by other channels (DF-T3). | — | — | 0 | none |

### 4.7 Session, transport, firmware

| ID | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|
| V8-23 | No rule may depend on transfer numbers, pipeline depth or which transfer carried a packet. SPI and USB MUST behave the same. | P6; RFC 9000 §12.3 (packet numbers apart from stream identity) | A7, C3 (NEW-1, OF-T13) | 0 | none |
| V8-24 | Epochs and sequences MUST start at 0 at every link session start and MUST NOT be reset otherwise, except by RESET. A session change discards all channel state on both sides, as today (v2 R6, `LinkService.cs:388-396`). | P10; RFC 9260 §1.3 | — | 0 | none |
| V8-25 | The firmware part MUST use static tables only: no heap allocation in the protocol path, no per-message buffer, no reorder buffer, no new transfer buffer. Total new static RAM MUST stay below 1 KB, checked with the map file. | PR8; pattern survey §6 | — | — | — |
| V8-26 | The RRF S layer SHOULD be a separate source file without GCodes dependency, so the host harness can compile it unchanged. Today the harness replaces `ExchangeData` and executes no packet (`Tools/sbc-harness/README.md:95`). | — | — | 0 | none |
| V8-27 | M122 on both sides SHOULD count per channel: stale-epoch drops, stalls, go-back-N resends, RESETs, credit waits. Counted, not alarmed (v2 R7). | v2 R7 | — | ≤ 64 B (u16 × 2 × 16) | none |

## 5. Message classes and ordering

| Class | Meaning | Rules |
|---|---|---|
| X expedited | Stop and reset, and STOP_ACK | First in a transfer, executed on read, never refused, never in a stream, idempotent |
| C session control | RESET, RESET_ACK, channel state (ACK, NAK, credit), RESET request, link capability activation | Never refused, never credit-limited, MUST be able to pass D and G (deadlock avoidance) |
| D(c) channel data | Everything bound to channel c | In sequence order per (channel, direction, epoch); credit for codes; NAK per stream |
| G global | Object model, print info, key changes, file operations, untargeted messages | Global stream with its own sequence; answers matched by id |
| I IAP | `WriteIap`, `StartIap` | Outside the session; v7 unchanged (v2 C2) |

**May-pass table** (same direction; row = later packet, column = earlier packet). "Must" means a design that
blocks it is wrong, as with PCIe posted writes. The PCIe table itself was only seen in secondary sources.

| Later \ earlier | X | C | D same channel | D other channel | G |
|---|---|---|---|---|---|
| X | no | must | must | must | must |
| C | no | no (same channel), may (other) | must | must | must |
| D | no | no (same channel), may (other) | no | may | may |
| G | no | may | may | may | no; file operations stay one at a time |

Passing happens only through placement in a transfer, NAK stalls and credit waits; within one transfer packets
are processed in order, as today. **Barriers** across channels that v8 keeps:
- X: a stop or reset ends every channel.
- Lock/Unlock (10/11) lock all movement systems; RRF says "currently this is used only by M505"
  (`RRF/SBC/SbcInterface.cpp:606`). The barrier is in the logic (standstill), not in the link.
- PrintPaused and PrintStopped act on File and File2 together (catalogue A10, `SbcInterface.cpp:506-542`, `:1495-1504`).
- Other cross-channel dependencies are not known (Q7).

**v7 messages in v8** (numbers from `RRF/SBC/SbcMessageFormats.h:269-349`):

| Dir | v7 request | v8 class | Identity in v8 | v7 pairing it replaces |
|---|---|---|---|---|
| → RRF | 0 EmergencyStop, 1 Reset | X | stop number | completion one transfer later (#88) |
| → RRF | 2 Code | D | (c, e, seq); credit | position of `BufferedCodes` |
| → RRF | 3 GetObjectModel | G | request id | queue head + one per transfer + key check |
| → RRF | 5 SetPrintFileInfo, 30 ObjectModelKeyChanged | G | seq | none (S3 framing bug stays a separate fix, OF-S3) |
| → RRF | 6 PrintStopped | G, barrier File/File2 | seq | final empty reply (`Processor.cs:1129-1134`) |
| → RRF | 7 MacroCompleted, 18 MacroStarted | D | (c, e, seq); answer to the request number | "the next code belongs to the macro" (B8) |
| → RRF | 10 Lock, 11 Unlock | D, logic barrier | (c, e, seq) | resend as polling |
| → RRF | 16 EvaluateExpression, 20 SetVariable, 21 DeleteLocalVariable, 29 SetLastCodeResult | D | (c, e, seq), echoed | (channel, expression) or (channel, name) |
| → RRF | 17 Message | D if targeted, else G | DoCode id if it answers a DoCode | next targeted message finishes the GB (B9) |
| → RRF | 19 InvalidateChannel | C: RESET(c, e+1) | new epoch | none (no answer) |
| → RRF | 22–28, 31 file results | G | request id | `fileOperation` state, late replies skipped |
| → RRF | 0x4D50 ActivateLinkCapabilities | C | — | unchanged |
| → RRF | 12, 13 IAP | I | — | unchanged |
| → RRF | 15 FileChunk (deprecated, CAN expansion updates, `SbcMessageFormats.h:328`) | G | request id | synchronous answer |
| → DSF | 0 ResendPacket, 2 CodeBufferUpdate | C: channel state | absolute values | packet id of a TX two transfers back; free space minus `BytesReserved` |
| → DSF | 1 ObjectModel | G | request id | order |
| → DSF | 3 Message (code reply) | D | code epoch (V8-10), code seq (V8-14) | first channel bit, `BufferedCodes[0]`, PushFlag joining |
| → DSF | 3 Message (generic) | G | none (notification) | — |
| → DSF | 4 ExecuteMacro, 13 WaitForAcknowledgement | D | request number; code seq if from a code | start code = `BufferedCodes[0]` |
| → DSF | 5 AbortFile | single: D; all: C (RESET request) | code seq of the M99 / new epoch | M99 at `[0]`, drains, dummy reply |
| → DSF | 7 PrintPaused | G, barrier File/File2 | file positions (already identities) | unchanged in M1–M4 (D7) |
| → DSF | 9 Locked, 11 EvaluationResult, 16 VariableResult | D | seq of the request | first lock request; content |
| → DSF | 12 DoCode | D | DoCode id | — |
| → DSF | 14 MacroFileClosed, 15 MessageAcknowledged | D | code epoch; level's start code seq (V8-14) | pop the current level |
| → DSF | 10 FileChunk (deprecated, `:281`); 17–27 file requests | G | request id | one slot, synchronous |
| → DSF | new: RESET_ACK, STOP_ACK | C; X | epoch; stop number | — |

## 6. Firmware RAM budget

Measured basis (M122 of 2026-11-09, see the top; meaning from the RRF code):
- "static 131040" is RAM up to `_end`, "dynamic 105276 (1744 recycled)" is `mallinfo` in use and free,
  "never used 95868" is `heapLimit − heapTop`, "free sys stack 172" is in words, 688 bytes
  (`RRF/Platform/Tasks.cpp:300-314`, `:369-370`).
- "heap 6144/5036/612" is RRF's string heap, allocated/used/recyclable (`RRF/Platform/Heap.cpp:394-395`), not malloc.
- The non-cached PBUF pool holds the three 8 KB transfer buffers (`RRF/SBC/DataTransfer.cpp:455-479`), allocated
  byte by byte from the pool (`RRF/Networking/LwipEthernet/LwipEthernetInterface.cpp:92-119`). The harness models
  the pool as 16 × 1552 = 24,832 bytes (`Tools/sbc-harness/README.md:111`), which leaves 256 bytes. v8 needs no
  buffer there.

| Item | Bytes | Requirement |
|---|---|---|
| Per stream: next expected sequence (u16 × 16) | 32 | V8-5 |
| Stall and dirty bitmaps | 4 | V8-6, V8-8 |
| Per channel: epoch, pending epoch, flags (3 × 16) | 48 | V8-9 |
| Per GB: epoch of the current code; epoch of the pending request (2 × 16) | 32 | V8-10, V8-11 |
| Reply FIFO tag, 1 B × 64 entries (`OutputMemory.h:15-19`) | 64 | V8-10 |
| File slot request id (padded) | 4 | V8-13 |
| Credit: released counter + capacity | 4 | V8-15 |
| Stop number, STOP_ACK flag | 2 | V8-19, V8-20 |
| Lost-reply marker | 2 | V8-18 |
| Counters (if V8-27 is done in RRF) | 64 | V8-27 |
| **M1–M4 total** | **≈ 256** | 0.27 % of 95,868 |
| V8-17 per-channel limits in RRF (only with D4 b) | +64 | V8-17 |
| V8-14: code sequence per GB, FIFO tag 1 → 4 B | +224 | V8-14 |
| V8-14: 2 B per pushed level in existing heap objects, worst case | ≤ +300 | V8-14 |
| **Everything, worst case** | **≈ 850** | 0.89 % |
| Removed: `codeReplySurplus`, `invalidationPending` (2 B per GB, `GCodeBuffer.h:405`, `:422`) | −30 | V8-9, V8-10 |

Field sizes and wrap-around (RFC 1982 §3.2: values in flight must stay below 2^(n−1)):

| Field | Bits | Bound on values in flight |
|---|---|---|
| Sequence per stream | 16 | Credit bounds codes: the ring holds at most 170 codes (4096 / 24, smallest code 4 + 20 bytes, `SbcMessageFormats.h:368-385`), far below 2^15 |
| Epoch per channel | 8 | One RESET in flight per channel (V8-9); 2 bits would do, 8 leave margin |
| Credit counter | 16 | Capacity 4096 < 2^15 |
| Request number, stop number | 8 | RRF waits for each blocking request (`SbcMaxRequestTime` 3 s, `SbcMessageFormats.h:42`) |

Not estimated: flash, CPU, SBC task stack (1400 words, `SbcInterface.cpp:39-43`; the RRF stack analyzer in `Tools/`
can check it), padding inside `GCodeMachineState`. Whether static growth reduces "never used" byte for byte is not
checked.

## 7. Migration and compatibility

**Negotiation.**
- New capability bit `SessionLayer` = 8, next to `PhaseGuard` 1, `SequenceGuard` 2, `ExtendedHeader` 4
  (`RRF/SBC/SbcMessageFormats.h:139`, `DCS/Link/Protocol/Shared/ExtendedLink.cs:13-33`).
- Offered in `limits.sbcLinkCapabilities`, activated with `ActivateLinkCapabilities` (0x4D50) as today
  (`RRF/Platform/RepRap.cpp:324`, `:369`; `DCS/Link/LinkService.cs:1520-1600`).
- On SPI only together with `ExtendedHeader` and `SequenceGuard` (section 1). Today RRF offers capabilities only on
  SAME70 (`RRF/SBC/DataTransfer.cpp:474-479`). USB: decision D6.
- The switch MUST happen before any channel traffic of the connection, at a transfer both sides agree on (Q8).
- Without the bit on either side, the link runs v7 as today.

**Coexistence.**

| DSF | RRF | Result |
|---|---|---|
| fork with v8 | fork with v8 | v8 session |
| fork with v8 | stock, or older fork | v7, with whatever catch code the v7 path keeps (D2) |
| stock | fork with v8 | v7; the fork RRF fixes (RRF #43, #45, #46) stay harmless in v7 |
| stock | stock | v7 as upstream |

**Removal plan.** Catch code can be deleted from the v8 path once the gate passes. From the v7 path only after D2.

| Milestone | Content | Catch code no longer needed (catalogue `CC-n`) | Gate before deletion |
|---|---|---|---|
| M0 | Preparation: RRF S layer as its own file (V8-26), harness extension, fake firmware v8 mode. NEW-1 is fixed in v7 separately, as a normal fork fix with its own test | none | existing suites green |
| M1 | V8-1–V8-4, V8-9–V8-12, V8-23, V8-24 | CC-1–CC-8 (A0, #134, #158, RRF #45, RRF #43, #161, RRF #46 both parts). 7 of them fork-only | the TriggerFuzz abort tests and `TriggerMacros.TriggerChannelWorksAfterInvalidationDuringTriggerRequest` (DF-T1, T2, T10, T11, T12) green in v8 mode with the catch code removed; lab `AbortsCrossingMacroRequests` with `LAB_ABORT_ITERATIONS=2000`: no block, no stack underrun, no "none was requested", **no out-of-order reply** |
| M2 | V8-5–V8-8, V8-21 | CC-25, CC-26, CC-27, CC-31 | NAK of every D request type at every position with other channels interleaved (fake firmware `FailBufferLocks`); harness replay of the S layer with 0 differences; the same tests over the USB adapter |
| M3 | V8-15–V8-18 | CC-28, CC-29, CC-30 | `TriggerMacros.TriggerMacroGetsSpareCodeBufferRoom`, `LinkInterfaceRequests.CodesBufferedAheadLeaveSpareRoom`; lab `CodeBufferReserve.TriggerMacroGetsRoomWhileOtherChannelsReadAhead` (DF-T3, LT §14, 2026-11-08) |
| M4 | V8-13, V8-19, V8-20, V8-22 | CC-32, CC-33, CC-34, CC-35, CC-37, CC-40 | `StopPath`, `StopPathFuzz`, `M112RepliesOnlyOnceRrfIsStopped` (OF-P3) pass; object model tests across a DCS restart (LT §9, "Failed to find query") |
| M5 (D3) | V8-14 | CC-9–CC-12, CC-16–CC-20, CC-22–CC-24 (all upstream content) | `ChannelProcessor` (`M99WithoutReply`, `M99ReplyBeforeAbortRequest`, `M99ReadAheadAfterM99`, `ReplyForStartCodeBeforeMacroFileClosed`, `PrintCompletionReply…`) rewritten for identity; a lab sequence of 3 × M122 + M291 S2 (B4) |

Not removed by v8: CC-13 (`IsTriggerBusy`, upstream content), CC-14/CC-15 (pause, D7), CC-21 (B7 read-ahead invalidation,
Q5), CC-36 (activation via OM, link), CC-38 (priority codes hop channels, logic), CC-39 (RRF #25, safety boundary v2 SB1),
CC-41 (stop on a lost connection, v2 R6/R13), CC-42 (RRF-internal), CC-43 (invalidate on reset, v2 R1/R2), CC-44–CC-46 (link,
v2), CC-47 (RRF-internal mutex). That is 33 of 47 items removable, among them 10 of the 11 fork-only items of the
session layer.

## 8. Verification

- **Unit tests** in fork-only files under `src/UnitTests` (CLAUDE.md rule 7):
  - The fake firmware (`src/UnitTests/FakeFirmware/FakeFirmware.cs`) gets a v8 mode that implements the RRF side of
    this spec. Every requirement gets a test.
  - Every existing test of a deleted catch code keeps running in v8 mode. Those tests are the regression set.
- **RRF host harness** (`firmware/RepRapFirmware/Tools/sbc-harness`): today it compiles `DataTransfer.cpp` and
  `TaskLoop` unchanged and replaces `ExchangeData`. With V8-26 it also compiles the S layer. A
  `PortMatchesRealFirmware`-style replay (`src/UnitTests/SPI/LinkRecovery.cs`) compares the fake firmware's S layer
  with the real one; pass = 0 differences.
- **Fuzzing** (`src/UnitTests/Fuzz`): a session fuzzer adds random NAKs, RESETs, credit stalls and RRF restarts
  across channels to `TriggerFuzz` and `StopPathFuzz`. Invariants: every code completes exactly once; no reply
  reaches another code; no channel stays blocked; no DCS stop.
- **Lab** (`src/LabPrinterTests`, procedure LT): `AbortsCrossingMacroRequests` (2000 aborts), the T3 trigger check,
  M112 under load, a full print. Plus v2 R4: codes/s and transfers/s within 2 % of v7 on a clean link.

| Acceptance | Pass when |
|---|---|
| AC1 | 2000 aborts: 0 blocks, 0 "Out-of-order reply", 0 "Resending packet", stale drops counted only in M122 |
| AC2 | No code is completed by a message of another code in any fuzz run (identity check in the fake firmware) |
| AC3 | M112 completes only after STOP_ACK in every StopPath case |
| AC4 | No refusal of a credited code in any run |
| AC5 | New static RRF RAM ≤ 1 KB by map file; no new heap allocation in the S layer (code review) |
| AC6 | All four combinations of section 7 run; the v7 combinations behave as today |

## 9. Decisions for Tim

| # | Decision | Options | Recommendation |
|---|---|---|---|
| D1 | How v8 relates to the 2026-10-08 decision "negotiation on top of v7" (v2 C1) | (a) capability `SessionLayer` on the v7 framing, protocol version 7; (b) real protocol version 8; (c) (a) now, (b) if Duet3D adopts it and assigns the number | (c). Same reasons as 2026-10-08: mixed versions work, no number collision. Only Duet3D can give a version number without a collision |
| D2 | What happens to the v7 path and its catch code | (a) keep the v7 path with all fork catch code; (b) once v8 has passed the lab gates and the printers run a v8 RRF, delete the fork catch code from the v7 path too, so v7 is upstream code again; (c) require v8 except in `UpdateOnly` mode | (b). Only (b) or (c) meet "remove our catch code from the codebase". (b) keeps flashing and stock combinations working and matches CLAUDE.md rule 8. Behaviour change: fork DSF with stock RRF gets upstream's v7 bugs back (DF-T2, T3, T11, T12; DF-T1 and T10 are in stock RRF anyway) |
| D3 | How replies find their code | (a) epoch tag only (V8-10, M1, no G change); (b) also code identity (V8-14, M5, medium G change) | (a) first. (b) only if the A-1 family keeps producing findings or Duet3D wants it, because it is the only part that changes logic code noticeably |
| D4 | Where per-channel credit and the reserve live (V8-17) | (a) RRF advertises one absolute limit; DSF splits it per channel and keeps the reserve, exactly instead of by estimate; (b) RRF advertises per-channel limits too (QUIC two-level, +64 B) | (a). DSF is the only sender and knows the job streams (`LinkInterface.cs:52`); the exact absolute limit is what removes the guesswork. (b) if Duet3D prefers the firmware to protect itself |
| D5 | Expedited flag in the CRC-protected transfer header (v2 R10) | (a) part of v8 M4; (b) part of the v2 link work | (b). It is link layer; V8-19 only needs X first in a transfer |
| D6 | USB | (a) v8 on SPI only, like v2; (b) the S layer also on USB | (b) for the design (V8-23, NEW-1). Testing needs a USB setup; the lab printer has none (OF-T13) |
| D7 | Pause and print stop (A10) | (a) keep the v7 mechanism; (b) PrintPaused becomes an RRF request for a RESET of File and File2 | (a) until M4; then decide with test data. The pause position logic is not analysed here |
| D8 | When to show this to Christian Hammacher | (a) now, before M1; (b) after M1 has passed the lab | (a). M1 replaces fork fixes of upstream bugs; if Duet3D wants another shape, CLAUDE.md rule 8 says to follow upstream |

## 10. Open questions and what is unverified

| # | Question | Status |
|---|---|---|
| Q1 | NEW-1: RRF composes its USB answer one transfer behind (`DataTransfer.cpp:1302-1357`, `:1362-1371`), so `USB.cs:717-721` looks into the wrong buffer, may copy the wrong packet, or throws `ArgumentException`, which `LinkService.cs:446` does not catch | Code reading, not tested. OF-T13 relied on the `USB.cs` comment and probably does not exist |
| Q2 | NEW-2: can the 64-entry reply FIFO fill in practice (stalled link, M122 chunks)? | Not shown |
| Q3 | NEW-3: can RRF refuse a code that DSF's estimate allowed? | Not shown |
| Q4 | Is logical credit always physically storable, given that `DefragmentBufferedCodes` runs only when 384 bytes or less are free and moves one gap per run (`SbcInterface.cpp:2436-2525`)? | Not analysed |
| Q5 | B7: how RRF tells in-flight read-ahead codes from new ones after a push without counting ExchangeData: a per-channel generation counter, or a stack-level tag. RRF levels pushed by M120 (`localPush`) may not exist in DSF | Not analysed; B7 is upstream design and has no known failure |
| Q6 | Does every message of a code pass one capture point for its tag? OF-T10 lists writers outside `HandleReply` (M122 `GCodes2.cpp:2292`, `GpInPort.cpp:90`, `GCodes4.cpp:1894`) | Not checked |
| Q7 | Cross-channel dependencies in the logic beyond Lock, pause and stop | Not listed; V8-4 depends on it |
| Q8 | The switch point of `SessionLayer` within a connection, given the switch-window findings of the extended header (OF-S1, OF-S5) | Open |
| Q9 | Is `AbortFile(true)` harmless when RRF already aborted all files itself (V8-12)? | Not checked |
| Q10 | Does a refused `LockAllMovementSystemsAndWaitForStandstill` have side effects (V8-7)? | Not checked |
| Q11 | SetPrintFileInfo alignment: the 64-bit `lastModifiedTime` (`SbcMessageFormats.h:460`) after another packet in the same transfer (catalogue D4, `LinkService.cs:407`) | Open, also in v7 |
| Q12 | H1 ("Daemon: Out-of-order reply ''" after a DCS start) and H2 ("Failed to find suitable stack level for flush request") | Causes not known; whether v8 removes them is not known |

Also unverified:
- The M122 figures are one reading on one printer and configuration; they change with the configuration.
- RAM figures are estimates from field sizes; padding, `StaticSemaphore_t` and `GCodeMachineState` sizes were not
  measured.
- The PBUF pool size comes from the harness model, not from the board's `lwipopts.h:267` expression.
- DSF line numbers come from the catalogue, made at 808d56fa; `src/DuetControlServer` has no change since, and the
  resend, abort, reply and buffer-space lines were re-read. RRF lines were re-read at 0dccb9efe for the packet loop,
  InvalidateChannel, the per-channel requests, the reply FIFO, the code ring, the USB transfer, the transfer buffers
  and the M122 RAM report; the rest come from the catalogue at the same commit. The pattern survey cited RRF at
  cde4e4fa1; this document uses current lines only.
- The protocol references below were opened by the pattern survey, not again for this document.

## 11. References

Opened by the pattern survey (text search, section numbers from the documents):
- HTTP/2: RFC 9113 §2, §4.1, §5, §5.1, §5.1.1, §5.2.1, §5.3.1, §5.3.2, §6.4, §6.8, §8.7. Priorities: RFC 9218 §2, §4.1.
- QUIC: RFC 9000 §2, §2.1, §2.2, §3.5, §4.1, §4.2, §4.4, §4.5, §4.6, §12.3, §13.3, §19.4, §19.5; RFC 9002 §4.2.
- SCTP: RFC 9260 §1.3, §1.5.2, §6.5, §6.6; RFC 6525 §4.1, §4.4, §5.2.1, §5.2.2; RFC 8260 §1.1 (only §1.1 and §3 read).
- SSH connection protocol: RFC 4254 §4, §5, §5.2, §5.3, §5.4.
- TCP: RFC 9293 §3.4.1, §3.8.5; urgent data: RFC 6093 §2.1, §3.1, §5. Serial numbers: RFC 1982 §3.2.
- MQTT 5.0 (OASIS, 2019-03-07): §2.2.1, §4.4, §4.6, §4.9, §4.10.
- AMQP 1.0 (OASIS, 2012-10-29) Part 2: §2.1.1, §2.5.6, §2.6.6, §2.6.7, §2.6.10, §2.8.10. AMQP 0-9-1 (2008-11-13):
  §2.3.1, §2.3.7, §4.2.3, §4.3, §4.6, §4.7; `amqp0-9-1.xml` classes channel and basic (no section numbers).
- JSON-RPC 2.0: §4, §4.1, §6. gRPC: `doc/PROTOCOL-HTTP2.md` at grpc/grpc cf61c7d62a.
- USB 2.0: §5.3.2, §5.5.4, §5.5.5, §8.4.6.4, §8.5.1, §8.6.2, §8.6.4. USB Mass Storage Bulk-Only 1.0: §3.4, §5.1, §6.3.1.
- Modbus Messaging on TCP/IP Implementation Guide V1.0b: §3.1.3, §4.2.1, §4.4.1.3.
- Bosch CAN 2.0 Part A §2, §3.1.4 (a re-typeset copy, not Bosch's site); TI SLOA101B.

Not opened (secondary sources only): PCI Express Base Specification (ordering table, posted/non-posted passing,
flow-control credit types; from patent US 8,495,265 B2 and a Design & Reuse article on PCIe 6.1 UIO). CiA 301.
ISO 11898-1.

Repository: [sbc-link-requirements.md](sbc-link-requirements.md), [sbc-link-current-state.md](sbc-link-current-state.md),
[sbc-link-findings-upstream.md](sbc-link-findings-upstream.md), [dsf-findings-upstream.md](dsf-findings-upstream.md),
[open-findings.md](open-findings.md), [rrf-local-trigger-actions.md](rrf-local-trigger-actions.md),
[lab-test-plan.md](lab-test-plan.md). Duet3D wiki: `User_manual/Reference/Gcodes.md` (M112), clone at a106ad4.
