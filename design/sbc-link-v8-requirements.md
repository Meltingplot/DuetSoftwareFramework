# SBC link v8: session and channel layer requirements

Status: draft 3, 2026-11-09. Nothing here is agreed or implemented. No code was changed for it.
Draft 2 adds Tim's requirement of the same day: "The protocol must guarantee a logical compatibility to the current
implementation, so that we do not create deadlocks due to concurrency" (PR9, section 4.8, V8-4, section 5, AC7, AC8).
Draft 3 checks every requirement against the v7 contract reverse engineered from the code
([sbc-link-v7-contract.md](sbc-link-v7-contract.md), items `V7-n`, edges `E-n`, cycles `C-n`), as Tim asked: "check
the requirements against the current requirements of v7 … make sure that they align or name the difference and the
reason". Section 4.9 gives every V7 item its status. Draft 3 adds V8-34 to V8-39, amends V8-4 to V8-7, V8-10,
V8-15, V8-18, V8-19, V8-23, V8-24 and V8-29 to V8-33 (marked "Draft 3"), adds notes to V8-12 and V8-20, and adds
decisions D9 to D11 and questions Q13 to Q15.
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
  - [sbc-link-v8-patterns.md](sbc-link-v8-patterns.md): 12 patterns `P1`–`P12` from mainstream protocols;
  - [sbc-link-v7-contract.md](sbc-link-v7-contract.md): the v7 contract of both sides, 101 items `V7-n`, the wait-for
    graph (`E-1`…`E-53`, cycles `C-1`…`C-8`) and the v7 defects (findings `K1`…`K25` in open-findings.md).
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
| PR2 | Order is kept within a channel. Across channels v8 keeps the order of v7 (PR9); it does not use the freedom that multiplexed protocols give independent streams. | QUIC RFC 9000 §2; SCTP RFC 9260 §1.5.2; SSH RFC 4254 §4; AMQP 0-9-1 §4.6 |
| PR3 | A few absolute classes; one expedited class that is never refused and never deferred. | RFC 9218 §4.1 (after RFC 9113 §5.3.2 deprecated the tree); CAN 2.0 Part A §2; USB 2.0 §8.4.6.4; caution RFC 6093 §5 |
| PR4 | Cancellation is a handshake that changes an epoch; repeated requests get the same answer. | RFC 9113 §6.4; RFC 9000 §3.5, §4.4, §13.3; AMQP 0-9-1 §2.3.7; SCTP RFC 6525 §5.2.1 |
| PR5 | Flow control by absolute, monotonic credit; control traffic is exempt. | RFC 9000 §4.1, §4.2; AMQP 1.0 Part 2 §2.6.7; RFC 9113 §5.2.1; MQTT 5.0 §4.9 |
| PR6 | One retransmission mechanism per layer. A refusal means "nothing was done". Re-sent content is identical. | RFC 9113 §8.7; MQTT 5.0 §4.4; USB 2.0 §8.6.2; RFC 9000 §2.2 |
| PR7 | Request state lives on the (channel, epoch) object and dies with it. | RFC 9000 §4.4; CLAUDE.md "Keep state on the object whose lifetime it shares" |
| PR8 | The firmware never retransmits and keeps no per-message state. DSF holds every copy. | Pattern survey §6; RRF keeps no copy today (`RRF/SBC/DataTransfer.h:238-241`) |
| PR9 | Logical compatibility: v8 changes how events travel, not which orders the logic can see. Every order the logic sees in v8 is one it can also see in v7; v8 may only remove orders (those the catch code filters today). So the logic meets no new concurrency, and no new wait cycle can form. | RFC 9113, Abstract (HTTP/2 is "an optimized expression of the semantics of" HTTP); CLAUDE.md bug-fix rule 2 ("find a path that already works") |

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
| V8-4 | The may-pass table of section 5 is normative. D packets of one retransmission sequence (V8-6) and direction MUST be processed in sequence order. Packets of different channels MUST reach the logic in the order DSF wrote them, as in v7, and MAY pass each other only where v7 lets them (V8-29). Draft 3: "per sequence" instead of "per channel", and the order rule holds per direction as V8-29 says. | P2, P11; PCIe ordering table (secondary sources only) | — | 0 | none (V8-28) |

### 4.2 Sequence, acknowledgement, refusal

| ID | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|
| V8-5 | DSF MUST number D and G packets per (stream, epoch) from 0, 16 bits, compared by RFC 1982 §3.2. A stream is one retransmission sequence: D-code(c) or D-req(c) of a channel (V8-6), or the global stream. RRF MUST report per stream the next sequence it expects, as an absolute value, whenever it changed. DSF keeps every packet until it is acknowledged. Draft 3: two streams per channel. | P3; RFC 9260 §6.5; AMQP 1.0 Part 2 §2.8.10 | CC-25 (resend lookup by transfer position, `DCS/Link/Adapter/SPI.cs:823`), CC-27 (NEW-1, `USB.cs:717-721`) | 64 B (u16 × 2 × 16) | none |
| V8-6 | "Not accepted now" MUST be an acknowledgement with a stall flag at sequence n of one stream. RRF then drops later packets of that stream until n arrives again. DSF resends n, n+1, … of that stream only, with identical content. Other streams go on. **Draft 3:** each channel c has two streams. D-req(c) holds the packets that RRF handles under the GB mutex and refuses while the mutex is busy (V7-11): Unlock, EvaluateExpression, targeted Message, SetVariable, DeleteLocalVariable, SetLastCodeResult; PrintStopped goes into D-req(File). D-code(c) holds the other D packets of c: Code, MacroStarted, MacroCompleted, Lock (V8-36). Reason: RRF admits a Code, a MacroStarted and a MacroCompleted for a waiting macro without the mutex (`RRF/SBC/SbcInterface.cpp:382-392`, `:558-566`, `:827-865`), and the mutex holder can be waiting for exactly these: an M98 holds the mutex in its macro wait (E-37, V7-87). In one stream they would stall behind a mutex request that DSF wrote while the M98 ran (a DeleteLocalVariable, K25; a SetLastCodeResult of a DSF-internal code), and the wait would end by its 3 s timeout, which v7 does not do: there the later packet passes the refused one (V7-10). A MacroCompleted refused while RRF does not wait stalls D-code(c) harmlessly, because DSF sends nothing else on c until MacroFileClosed (E-17). For the same reason no G packet is refused for a mutex, so a file result never waits behind one (E-38, E-53). | P3, P9; USB 2.0 §8.6.2; MQTT 5.0 §4.6; RFC 9000 §2.2 | CC-26 (`_packetsBeingResent`), NEW-3; CC-31 (Lock polled by resend, `SPI.cs:1107-1120`, `LinkService.cs:615-619`) becomes a pending request (V8-36) | 4 B (stall bitmap per stream kind) | none |
| V8-7 | RRF MUST NOT refuse a packet that has had an effect. Handlers that today act before they refuse (InvalidateChannel `RRF/SBC/SbcInterface.cpp:883-895`, Code `:382-392`) MUST act only once accepted, or never refuse. **Draft 3** adds the two the RRF side found (v7 contract X-6): SetVariable evaluates and assigns, then refuses if its VariableResult does not fit, so the copy of a `var` or `global` fails with "variable '…' already exists" (`:1022-1093`); it MUST reserve the OutputBuffer and the TX room before it assigns. Lock keeps the lock of the movement system it reached when it refuses (`RRF/GCodes/GCodes.cpp:1947-1962`, `:1969-1999`); V8-36 makes it a request that is never refused. Re-evaluating GetObjectModel and EvaluateExpression on a copy has no effect; restarting the burst window (`SbcInterface.cpp:753`) changes only the pacing. | P9; RFC 9113 §8.7 | root of DF-T11 resend; X-6 (K21) | 0 | small: statement order in RRF `SbcInterface::ExchangeData` only |
| V8-36 | **Draft 3. Lock is a pending request, not a stall.** RRF MUST accept LockAllMovementSystemsAndWaitForStandstill (10) and keep it as one pending bit per GB. At every exchange it tries `LockAllMovementSystemsAndWaitForStandstill(gb)` under the GB mutex with `SbcYieldTimeout`, as v7 does for every resent copy (`RRF/SBC/SbcInterface.cpp:606-634`), and sends Locked once that succeeds. The partial lock it keeps between tries stays as in v7 (`RRF/GCodes/GCodes.cpp:1947-1962`), so other channels still cannot add moves to the system it reached. RESET and a session change cancel a pending Lock and release what it holds (`UnlockAll`, `GCodes.cpp:5648-5668`); DSF answers a Locked whose request it no longer has with Unlock. In v7 such a lock stays until RRF releases it by itself (G-13; the release point `GCodes2.cpp:5016` was seen only in passing). | P8 (pending answer), P9 | CC-31; G-13 (K14) | 2 B (pending bitmap) | small: DSF `DCS/Link/Channel/Processor.cs:838-849` writes Unlock for an unmatched Locked |
| V8-8 | ACK, NAK, credit and RESET_ACK SHOULD be one C-class "channel state" report with absolute values (epoch, next sequence, stall, credit), so a repeated or reordered report is harmless. It replaces `ResendPacket` (0) and `CodeBufferUpdate` (2). RRF reserves TX space for it as it reserves space for resend headers today (`RRF/SBC/DataTransfer.h:228-231`). | P4 pitfalls; RFC 9000 §4.2 | — | 2 B (dirty bitmap) | none |

### 4.3 Cancellation and epochs

| ID | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|
| V8-9 | InvalidateChannel MUST become RESET(c, e+1), and RRF MUST never refuse it. On receipt, without the GB mutex, RRF records e+1, ends a macro wait, and purges the ring codes of c (`InvalidateBufferedCodes` needs only a critical section, `RRF/SBC/SbcInterface.cpp:2527-2529`). With the GB mutex, later, it applies `PauseSequenceAborted`, `AbortFile(true)` (`:899-902`). Then it sends RESET_ACK(c, e+1). A repeated RESET(c, e+1) gets the same ACK. DSF sends no D packet on c between RESET and RESET_ACK. | P6; RFC 9113 §6.4; RFC 9000 §3.5, §13.3; RFC 6525 §5.2.1; AMQP 0-9-1 §2.3.7 | CC-1 (silent closes), CC-6 (#161: `Processor.cs:126-136`, `:883-905`; `ILinkAdapter.cs:203`; `LinkService.cs:620-626`), CC-8 (`invalidationPending`, `GCodeBuffer.h:422`) | 48 B (epoch, pending epoch, flags × 16) | small: the RRF InvalidateChannel handler is split into receipt and apply, with today's calls (`:880-911`). Behaviour as with #161: the channel waits for RRF's answer, now an explicit one |
| V8-10 | Every D packet MUST carry an epoch; receivers MUST drop D packets of another epoch and count them. RRF MUST tag a message with the epoch of the code or request it belongs to, taken when the code was handed to the GB (`FillBuffer`, `:1890-1950`) or the request was made, not with the current channel epoch. The tag of a queued reply is captured when it is queued (`HandleGCodeReply`, `:2353-2406`). **Draft 3:** (a) the reassembly state of a reply (Push chunks, DSF `_lastPartialMessage`, `DCS/Link/Channel/Processor.cs:1184-1189`) belongs to (channel, epoch) and MUST be discarded with the epoch and on a session change; v7 keeps it across `Invalidate` and prepends it to the next reply (V7-30, G-8). (b) DoCode (12) and the Message that answers it are exempt from the epoch filter: RESET does not end a DoCode in v7 (InvalidateChannel leaves `sendToSbc` and the executing state alone, V7-53, V7-70), and a dropped result would leave RRF's GB "executing on SBC" without a bound. | P6, P7; RFC 9000 §4.4 | CC-2 (#134, `Processor.cs:119-124`, `:888-892`, `:1357-1362`), CC-3 (#158, `:1285-1290`), CC-4 (RRF #45, `SbcInterface.cpp:904`), CC-7 (RRF #46 `codeReplySurplus`: `GCodeBuffer.h:405`, `GCodes.cpp:4450-4458`, `:4584-4593`, `:3703-3708`); A7 (OF-T13); G-8 (K11) | 80 B (code epoch per GB 16 B, tag per FIFO entry 64 × 1 B) | small: if every message of a code passes one capture point. CLAUDE.md rule 3 prefers passing the identity as a parameter down the reply path (`GCodes.cpp` reply functions); open which writers bypass it (Q6) |
| V8-11 | Per-GB request state (`requestedMacroFile` `GCodeBuffer.h:403`, the abort, prompt and closal flags) MUST be bound to the epoch it was created in. RRF MUST NOT send a request of an older epoch; it resolves it locally as failed. This covers state machines that go on after a RESET (OF-T10: G28, `GCodes4.cpp:358-413`). | P6 "what epochs do not fix"; PR7 | CC-5 (RRF #43, `SbcInterface.cpp:891-894`); A5 (OF-T11 daemon.g) | 16 B (request epoch per GB) | small: one compare where requests are written (`:1513-1527`, `:1597-1611`) |
| V8-12 | An RRF abort of all files (`AbortFile`, abortAll) SHOULD be a request for a RESET, which DSF answers with RESET(c, e+1). DSF stays the only writer of epochs. Draft 3 note: DSF's channel then waits for RESET_ACK where v7 sends at once; the wait is E-16 of a DSF abort and bounded as C-4 (the receipt part ends a macro wait, the apply waits for the GB mutex, which RRF's 3 s waits bound), and RRF still runs codes of c only after the abort, as in v7. V8-39 uses the same request. | RFC 9000 §3.5 (STOP_SENDING) | the one-transfer drop after the abort request (`gb->Invalidate()`, `:1575`) for abort-all; late replies after RRF's own abort (G-20, K18) | 0 | small: DSF `HandleAbortFileRequest` (`LinkService.cs:866-889`) issues the RESET after `FilesAborted(true)` |

### 4.4 Correlation

| ID | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|
| V8-13 | Global requests and their answers MUST carry an id that the answer echoes: GetObjectModel/ObjectModel, RRF file requests (17–27) and DSF file results (22–28, 31). Evaluations and variables echo the sequence of their D packet. An answer without a pending id is dropped and logged as a protocol fault. | P8; JSON-RPC 2.0 §4, §6; Modbus TCP guide §4.4.1.3 | CC-32 (one query per transfer, `QuerySent`, `LinkService.cs:470-478`, `:714`), CC-33 (fork key check 4dc381d0, `:716-723`), CC-34 (content match, `:1005-1023`, `:1123-1141`), CC-35 (file slot by state, `SbcInterface.cpp:1204-1207`, `:1221-1224`) | 1 B in the file slot. OM and evaluation answers are written while the request is processed (`:443-494`, `:682-760`), so the id is copied | none |
| V8-39 | **Draft 3. RRF requests carry a number; late answers are dropped.** Every ExecuteMacro and DoCode MUST carry a request number per GB that DSF's answer echoes (MacroStarted, MacroCompleted, the DoCode result). RRF MUST drop and count an answer whose request is no longer pending, which in v7 happens after the 3 s macro timeout (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1263-1274`): a late MacroStarted gives "none was requested", a late Code runs at the caller's level, a late MacroCompleted(error) pops whatever level is current (V7-37, V7-40). For a macro request whose wait expired after the request was written, RRF MUST also request RESET(c) (V8-12), so that DSF drops the level it may have pushed and the codes it sent for it (decision D10). DSF MUST answer every DoCode, also one it cancels, with a result (an error), so that RRF's GB does not stay "executing on SBC" (V7-70, G-12). | P8, P6 | X-9, X-25 (K23); G-12 (K13) | 16 B (request number per GB) | small: DSF `DCS/Codes/Pipelines/Executed.cs:170-230` sends an error result for a cancelled DoCode; RRF compares the number where answers are read (`SbcInterface.cpp:382-392`, `:558-566`, `:772-808`, `:827-865`) |
| V8-14 | Code replies and stack events (ExecuteMacro with fromCode, WaitForAcknowledgement, single AbortFile, MacroFileClosed, Locked, DoCode results) SHOULD carry the (epoch, sequence) of the code they belong to. DSF matches by identity, not by `BufferedCodes[0]`. RRF keeps the sequence of the code that opened a stack level next to the existing "identity of the command that invoked this macro frame" (`RRF/GCodes/GCodeMachineState.h:250-256`). Later milestone (D3). | P8 option (b) | CC-9–CC-12 (M99 family: `Processor.cs:650-666`, `GCodes.cpp:4460-4465`, `SbcInterface.cpp:1564-1573`, drains), CC-16–CC-20 (position match, `PendingReplies`, start code by position, Unbuffered M122/M409, `_waitingForPrintCompletion`), CC-22, CC-23, CC-24; NEW-2 becomes detectable | 224 B static (code sequence per GB 32 B, FIFO tag grows to 4 B: +192 B) plus 2 B per pushed level inside the heap-allocated `GCodeMachineState` (`GCodeBuffer.cpp:1061`), ≤ 300 B at full depth on all channels (`MaxStackDepth` 10, `Config/Configuration.h:243`) | medium: DSF `Processor` matching (`:1102-1168`, `:1264-1305`, `:1373-1389`, `:650-724`); RRF `GCodeBuffer` push/pop carries one more field |

### 4.5 Flow control

| ID | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|
| V8-15 | RRF MUST advertise an absolute, monotonic credit limit for code bytes: released + capacity, where "released" counts every code byte RRF received and no longer holds (taken by a GB, purged, or dropped on arrival). 16 bits, RFC 1982. RRF MUST NOT refuse a code within the limit. X and C packets and all RRF→DSF traffic are exempt. **Draft 3:** a stop or a RESET does not reset the credit; only a session change does (V8-24). DSF MUST NOT zero its view of the limit on an emergency stop, as v7 does (`DCS/Link/LinkInterface.cs:613`), because RRF need not report again: after an EmergencyStop packet it reports only if its ring changes (V7-14, RRF Q2). Codes after a stop then go out and get RRF's "stopped" result (V7-16), an order v7 has whenever the ring was not empty. | P4; RFC 9000 §4.1, §4.2; AMQP 1.0 Part 2 §2.6.7; RFC 9113 §5.2.1; RFC 4254 §5.4 | CC-30 (`BytesReserved`, `LinkService.cs:452`, `:747-753`; `LinkInterface.cs:37`), the cascade refusal (`codeBufferAvailable`, `SbcInterface.cpp:394-414`), NEW-3; G-11 (K12) | 4 B | none |
| V8-16 | The advertised capacity MUST be storable after fragmentation. Today RRF reports the largest contiguous block (`:1394-1399`) and defragments only when 384 bytes or less are free (`:2436-2446`). How the guarantee is met is open (Q4). | P4 pitfalls | — | 0 | none |
| V8-17 | Per-channel limits and a reserve for time-critical channels MUST exist, so codes buffered ahead cannot take the room of a trigger macro. Where they live is decision D4. | P4; RFC 9000 §4.1; USB 2.0 §5.5.4 | CC-28 (#133, `LinkInterface.cs:40-44`, `:570-583`; `Processor.cs:1076-1079`), CC-29 (share 82d581d1, `LinkInterface.cs:52`) | 0 (D4 a) or 64 B (D4 b) | none |
| V8-18 | A reply MUST NOT be dropped silently. If the reply FIFO is full (`OutputMemory.cpp:487-506`), RRF SHOULD record the loss per channel and report it with the next message of that channel. **Draft 3:** recording the loss MUST NOT add a wait: a producer that finds the FIFO full does not block, as in v7 (V7-28); a blocking producer would hold its GB mutex while it waits for the SBC task, a wait v7 does not have (PR9). | P8 pitfalls | — (NEW-2 has no catch code) | 2 B | none: nobody waits (draft 2 left this open) |

### 4.6 Expedited class and stop

| ID | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|
| V8-19 | EmergencyStop and Reset are class X. DSF MUST write them first in a transfer (as `LinkService.cs:324-354` does). RRF MUST execute them on read without a mutex (as `SbcInterface.cpp:336-344` does). They are never refused, never credit-limited, never part of a stream, and idempotent through a stop number. Getting them through a disturbed data phase is v2 R10. **Draft 3:** the X class covers both kinds of emergency stop. DSF-requested: the EmergencyStop packet stops the hardware in the SBC task, and the main task's next spin aborts every GB, which sends AbortFile(all) to DSF (V7-12). RRF-initiated: `DoEmergencyStop` (`RRF/GCodes/GCodes.cpp:1065-1070`) also runs `GCodes::Reset`, which clears those abort flags again (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:145`), so DSF hears nothing (V7-17); V8-38 reports it. | P5; CAN 2.0 Part A §2; USB 2.0 §8.4.6.4; RFC 9260 §6.6 | CC-40 in part (halt and reset ordering in `Execute`) | 1 B | none |
| V8-20 | RRF MUST answer EmergencyStop with STOP_ACK(stop number), written after `reprap.EmergencyStop()` returned. DSF completes M112 on STOP_ACK instead of one transfer later. | P6/P11 (explicit fence) | CC-37 (#88, `LinkService.cs:315-317`, `:454-456`, `:599`); OF-P3; G-2 on USB (K6, draft 3) | 1 B | none |
| V8-21 | v8 sessions MUST NOT use `ResendPacket` or `resendPacketId`. The link repeats transfers (v2), S has only V8-6, RRF keeps no copy. | P9; MQTT 5.0 §4.4; RFC 8260 §1.1 | with V8-5 and V8-6: CC-25–CC-27 | 0 | none |
| V8-22 | An M112 read from a macro MUST keep its flush (`MCodeHandler.cs:750`). The wiki tells users to put M81 before M112 in a trigger macro (`wiki-content/User_manual/Reference/Gcodes.md:2682`), so order inside a macro matters. The fast path for door and E-stop logic is the RRF-local trigger action (rrf-local-trigger-actions.md TA1, RRF #25). With V8-17 the flush no longer waits for room taken by other channels (DF-T3). | — | — | 0 | none |
| V8-38 | **Draft 3. RRF reports its own emergency stops.** When RRF stops without a request from DSF (`DoEmergencyStop`: trigger 0, PanelDue's M112 bytes, an M112 code that RRF executes, a trigger action with M112; `RRF/GCodes/GCodes.cpp:490-499`, `:990-999`, `:1065-1070`; `GCodes2.cpp:1953-1955`), it MUST send an X-class STOPPED(stop number) first in its next transfer. DSF handles it like the completion of its own halt: `InvalidateCodes` (`DCS/Link/LinkService.cs:1499-1518`) and `state.status` halted. In v7 DSF gets nothing and keeps its macro levels and codes while RRF has popped all of its own and runs the codes left in its ring as "stopped" (V7-17, X-5). DSF cancelling its codes after a halt is an order the logic sees in v7 too, when an M112 from DWC meets an RRF-local stop. The stop on a link loss (fork RRF #21) ends the session instead (V8-24). | P5, P6 | X-5 (K20) | 1 B | small: one call in RRF `GCodes::DoEmergencyStop` sets the flag of the S layer; decision D9 |

### 4.7 Session, transport, firmware

| ID | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|
| V8-23 | No rule may depend on transfer numbers, pipeline depth or which transfer carried a packet. SPI and USB MUST behave the same. **Draft 3:** the one exception is the read-ahead drop after a stack request (V8-37), which the catalogue keeps (CC-21, Q5); draft 2 forbade it without a replacement. It rests on the depth of one that both links have (V7-1). | P6; RFC 9000 §12.3 (packet numbers apart from stream identity) | A7, C3 (NEW-1, OF-T13) | 0 | none |
| V8-24 | Epochs and sequences MUST start at 0 at every link session start and MUST NOT be reset otherwise, except by RESET. A session change discards all channel state on both sides, as today (v2 R6, `LinkService.cs:388-396`). **Draft 3:** "as today" holds only for DSF. Epochs restart at 0, so they cannot filter what is left of the old session; therefore: (a) RRF MUST clear in `InvalidateResources` every per-GB flag that makes it send a packet: `macroFileClosed`, `messagePromptPending`, `messageAcknowledged` (after it acknowledged the open boxes itself, `RRF/SBC/SbcInterface.cpp:1772`), `invalidated`, `invalidationPending`, `codeReplySurplus`, an unsent `sendToSbc`, `reportPause` (V7-96, X-12; `:1717-1780` vs `RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:128-150`). In v7 a stale MacroFileClosed pops DSF's base level after a reconnect, and DCS stops (K5). (b) DSF MUST NOT write a D, G or C packet of the old session into the new one; the SPI adapter re-exchanges its pending TX after a reconnect (`DCS/Link/Adapter/SPI.cs:442-477`), and RRF processes it if its number follows (V7-91, G-3, K7). An X packet whose STOP_ACK has not come MUST be sent again. (c) Partial replies and messages are discarded (V8-10). | P10; RFC 9260 §1.3 | X-12 (K5), G-3 (K7) | 0 | small: RRF `InvalidateResources` clears the flags; DSF's SPI adapter drops the pending TX on a session change |
| V8-35 | **Draft 3. Unknown and invalid packets; nothing of v8 before the switch.** (a) Neither side MAY send a v8 header or a new v8 packet type before both have activated `SessionLayer` (section 7). A v7 RRF ends its read loop at a request number ≥ 32 other than 0x4D50 (`InvalidRequest` = 32) and drops the rest of the transfer, codes included, without a refusal (`RRF/SBC/SbcInterface.cpp:326-330`, `SbcMessageFormats.h:346`); the numbers below 32 are all assigned (`SbcMessageFormats.h:313-344`), the deprecated ones are skipped and acknowledged (`SbcInterface.cpp:674-679`); a v7 DSF ignores unknown types silently (`DCS/Link/LinkService.cs:611-701`). New v8 types MUST use numbers outside the upstream range, as 0x4D50 does (v2 C1). (b) In a v8 session a packet that the receiver cannot use (unknown type, invalid channel, invalid length such as a Code over 384 bytes, `SbcInterface.cpp:350-375`) MUST be rejected for good and counted (V8-27): RRF marks it rejected in the channel state report (V8-8), DSF completes the code with an error. It is never a stall, never the end of the read loop, never a silent drop. v7 refuses an over-long code on every copy (V7-20) and drops one with an invalid channel silently (V7-6). | P10, P12; v2 C1 | G-16, X-18 (K4); G-17, X-19 (K24) | 2 B (reject bitmap) | none |
| V8-37 | **Draft 3. The read-ahead drop after a stack request stays (B7).** After RRF wrote ExecuteMacro, AbortFile or WaitForAcknowledgement for channel c, it MUST drop the Code packets of c that DSF wrote before it processed that request, and DSF MUST suspend those codes and send them again after the pop, before newer ones, exactly as v7 does (V7-22: `RRF/SBC/SbcInterface.cpp:376-380`, `:1495-1504`; `DCS/Link/Channel/Processor.cs:158-189`, `:991-1016`). Until Q5 is answered, "before it processed" is the v7 rule "in the next exchange" (V8-23). The dropped codes count as received for the sequence (V8-5) and as released for the credit (V8-15); DSF's copies after the pop are new packets with new sequence numbers. | — (PR9, V8-28) | — (CC-21 stays) | 0 | none |
| V8-25 | The firmware part MUST use static tables only: no heap allocation in the protocol path, no per-message buffer, no reorder buffer, no new transfer buffer. Total new static RAM MUST stay below 1 KB, checked with the map file. | PR8; pattern survey §6 | — | — | — |
| V8-26 | The RRF S layer SHOULD be a separate source file without GCodes dependency, so the host harness can compile it unchanged. Today the harness replaces `ExchangeData` and executes no packet (`Tools/sbc-harness/README.md:95`). | — | — | 0 | none |
| V8-27 | M122 on both sides SHOULD count per channel: stale-epoch drops, stalls, go-back-N resends, RESETs, credit waits. Counted, not alarmed (v2 R7). | v2 R7 | — | ≤ 64 B (u16 × 2 × 16) | none |

### 4.8 Logical compatibility and deadlock freedom (PR9)

| ID | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|
| V8-28 | A v8 session MUST be a refinement of v7 for the logic of both sides: every order in which a logic layer sees its events (codes and their replies per channel, macro requests, starts and closals, message boxes, locks, flushes, evaluations, messages, aborts, stops) MUST also be possible in v7 with the catch code of DSF 7f26d0ca and RRF ddec2d001. v8 MAY remove orders, namely those that the catch code removes today (stale replies, requests and closals across an abort, codes overtaking a refused code of their channel). It MUST NOT add any. | PR9; RFC 9113, Abstract | — | 0 | none: this requirement is what keeps it so |
| V8-29 | Across channels, packets MUST be handed to the logic in the order DSF wrote them (RRF: one transfer at a time, packets in order, `RRF/SBC/SbcInterface.cpp:310-1359`; DSF: `DCS/Link/LinkService.cs:434-451`). A packet may be passed only where v7 already lets it be: a stalled packet (V8-6, in v7 a refused one, `:1355-1358`) lets later packets of other channels pass. A stall MUST NOT let a later packet of its own channel pass (v7 does, NEW-3; removing that order is allowed by V8-28). **Draft 3, per direction, no more order than v7 has (V7-82):** DSF→RRF: RRF hands packets to its logic in the order DSF wrote them (V7-2); DSF writes channel data round robin as in v7 (`DCS/Link/Channel/Manager.cs:72-99`) and keeps causal order (V8-34); it MUST NOT wait to put channels into an order. RRF→DSF: DSF hands packets to its logic in the order RRF wrote them (V7-2); RRF keeps the v7 composition (read-phase answers, the reply FIFO in production order, the file request, per-channel requests in channel-number order, PrintPaused last; V7-4), with the channel state report in place of ResendPacket and CodeBufferUpdate; it MUST NOT hold a request back to order it by the time it was raised, and MUST NOT add a drain that v7 does not have (V7-32). "Own channel" in the stall rule means own stream (V8-6): a stall in D-req(c) lets D-code(c) pass, as v7 lets a later packet pass a refused one. Neither logic relies on cross-channel order beyond the barriers of section 5, and neither may start to. | PR2, PR9 | NEW-3 | 0 | none |
| V8-30 | The spec MUST contain the wait-for graph of every blocking wait on both sides, v7 waits and the new ones of v8 (RESET_ACK, credit, stall, STOP_ACK), each with file:line and its bound, and MUST show that the graph has no cycle that is not broken by a bound. **Draft 3:** the v7 graph is [sbc-link-v7-contract.md](sbc-link-v7-contract.md) section 15: edges E-1…E-53 of both sides (the five initial edges of draft 2 are E-37, E-42, E-29, E-11 and E-14…E-16 there) and cycles C-1…C-8. v8 changes it so: E-26…E-28 no longer wait for the link thread (V8-34), which removes C-1 and C-2; E-16 becomes the wait for RESET_ACK with the same bound (V8-9); E-14 becomes the credit wait (V8-15); the resend polling of V7-11 becomes the stall (V8-6), in its own stream so that E-37 cannot wait behind it; E-15 and E-50 become the pending Lock (V8-36). New edges: E-v1 M112 → STOP_ACK, bounded by the 4.5 s of E-21 (V8-20); E-v2 a DSF channel after RRF's abort request → RESET_ACK (V8-12), bounded as C-4. C-3 is in the logic (lock order of M606 S1) and stays until it is fixed in v7 (V8-33, D11). C-4…C-8 stay as in v7, bounded. | PR9 | — | 0 | none |
| V8-31 | X and C packets MUST be handled without waiting: not for D or G traffic, not for a logic mutex, not for the RRF main task. Work that needs a logic mutex (V8-9 "apply") is deferred as a state, not waited for, and the answer (RESET_ACK) is sent once it is done. D and G packets keep their v7 hand-over (`SbcYieldTimeout`, then a stall instead of a resend). The S layer of DSF runs in the packet loop of `LinkService` and MUST NOT await a channel lock there that the logic can hold while it waits for the link (edge 3 of V8-30). **Draft 3:** more generally, the DSF packet loop MUST NOT take, synchronously or not, a lock whose holder can wait for the packet loop. In v7 the job lock is such a lock: M23, M32, M37 and the job loop hold it while they wait for SetPrintFileInfo to be written (E-27, E-28), and the loop takes it for PrintPaused and AbortFile on File/File2 (E-7): cycles C-1, C-2. V8-34 removes those waits, so the job lock may stay where it is. The same holds for every lock of V7-83; a new wait for the packet loop under one of them violates this rule. | PR9; RFC 9000 §4.1 (control frames are not flow-controlled) | C-1, C-2 (with V8-34) | 0 | none |
| V8-32 | Every new wait MUST be bounded. A wait that v7 breaks with a timeout MUST NOT become unbounded in v8. **Draft 3 replaces the rest of draft 2,** which ended the link session when RESET_ACK did not come or a stall or credit wait made no progress within a bound: that ends a session where v7 goes on, e.g. a full code buffer during a long move or M505 waiting for standstill, which is a new order (PR9). Waits that replace a v7 wait keep the v7 bound: RESET_ACK = E-16 (none while RRF refuses; RRF refuses only while its main task holds the GB mutex, and RRF's own 3 s waits bound that, V7-40, V7-71), credit = E-14 (none), a stall = the resend polling of V7-11 (none), the pending Lock = E-15, E-50 (none). These are back-pressure; their liveness is the link's (v2 R8 silence limit), and v8 MUST NOT end the session for them. The only new wait is STOP_ACK (V8-20), bounded by the 4.5 s of M112 (E-21), after which DSF stops DCS as today. | PR9; v2 R6, R8 | — | 0 | none |
| V8-33 | Before M1, the wait-for graph of V8-30 and the list of cross-channel dependencies of the logic (Q7) MUST be complete and reviewed. Any rule of this spec that would add an order or a wait cycle blocks the milestone until it is changed. **Draft 3:** the graph is in the v7 contract; C-3 (M606 S1 takes the job lock before the channel locks) and K25 (the deletion of while-loop locals without a flush) are logic defects of v7 that v8 does not remove; they MUST be fixed in v7 before M1 (D11). | PR9 | — | 0 | none |
| V8-34 | **Draft 3. A request without an answer completes on acceptance.** A DSF packet that the logic awaits as written and that has no answer (E-26: SetPrintFileInfo, PrintStopped, SetLastCodeResult, DeleteLocalVariable, Unlock) MUST be complete for the logic once the S layer has accepted it into its stream (V8-5), not when it is written into a transfer. Accepting MUST NOT wait for the packet loop; the content is captured at acceptance (v7 reads the print file info when it writes, `DCS/Link/LinkService.cs:405`). S MUST write a packet no later than every packet that the logic hands over after the request completed (causal order across streams), so RRF still reads SetPrintFileInfo before the M23 or M32 that follows it (V7-65) and SetLastCodeResult before the next code (V7-62). A packet that a session change loses after acceptance is lost as a written packet can be in v7 (V7-91), and the logic is invalidated as today. This removes the waits of the logic on the link thread under the job lock (E-27, E-28) and with them C-1 and C-2. | P3, P11; V8-5 | C-1, C-2 (K1, K2) | 0 | none: `LinkInterface` completes these requests on acceptance (`DCS/Link/LinkInterface.cs:397-406` and the other request methods); `JobProcessor` and `MCodeHandler` stay as they are |

### 4.9 Alignment with the v7 contract

Every item of [sbc-link-v7-contract.md](sbc-link-v7-contract.md) gets one status:
- **identical**: v8 keeps the v7 behaviour that the logic sees;
- **changed**: v8 changes it; the difference and the reason are given, and the last column says whether the change
  only removes orders the logic could see in v7 (PR9, V8-28);
- **removed**: v8 drops the mechanism; the reason is given;
- **gap**: draft 2 did not cover the item, or covered it in a way that would add an order, a wait or a cycle; draft 3
  closes it with the new or amended requirement named in the third column.

Counts: 52 identical, 26 changed, 3 removed, 20 gap. Every change, including the gap fixes, only removes orders. Two
add a wait in a case where v7 has none, each bounded and known from v7: V7-13 (STOP_ACK instead of the wait for the
transfer, with the same 4.5 s bound) and V7-46 (RESET_ACK after RRF's own abort, the wait v7 has after DSF's abort,
bounded as C-4). V8-34 removes three waits (E-26…E-28).

| V7 | Status | v8 requirement(s) | Difference | Reason | Only removes orders (PR9) |
|---|---|---|---|---|---|
| V7-1 pipeline depth one | changed | V8-23 | S and the logic no longer depend on the depth; RRF still composes one transfer behind | catch code of A-2 (CC-2, CC-25, CC-30) | yes: events in the old abort windows are dropped by epoch, not by position |
| V7-2 processing in the writer's order | identical | V8-4, V8-29 | — | — | — |
| V7-3 DSF's TX order | gap | V8-34, V8-29 (amended), V8-19 | X first, C next, then D and G in the order the logic handed them over, instead of the fixed kind order of `LinkService.Execute` | V8-34 needs causal order to keep SetPrintFileInfo before the M23 without a wait | yes: every hand-over order is one v7 produces when the two packets go into different transfers; only the kind order inside one transfer, for packets the logic did not order, is gone |
| V7-4 RRF's TX order | identical | V8-29 (amended), V8-8 | the channel state report takes the place of ResendPacket and CodeBufferUpdate; the logic does not see it | — | — |
| V7-5 pacing | identical | — | — | RRF-internal; v2 R4 asks for the same throughput | — |
| V7-6 unknown and invalid packets | gap | V8-35 (new) | nothing of v8 before the switch; in a v8 session an unusable packet is rejected and counted | a new v8 type sent to a v7 RRF would end its read loop and drop the rest of the transfer (X-18) | yes: removes the silent loss |
| V7-7 packet ids, resend lookup | removed | V8-5, V8-21 | — | CC-25, CC-27 (NEW-1); the `ArgumentException` that stops DCS goes with it | yes |
| V7-8 refusal | changed | V8-6, V8-7, V8-21 | a stall at sequence n of one stream, go-back-N in that stream; a refusal means nothing was done; RRF still keeps no copy | PR6, PR8 | yes |
| V7-9 SPI resend guard | removed | V8-6 | — | CC-26 | yes |
| V7-10 a refused packet is overtaken | gap | V8-6 (amended), V8-29 (amended) | a stalled packet is passed by other channels and by the other stream of its channel, not by its own stream | draft 2 stalled the whole channel, so an answer to RRF's macro wait could wait behind a refused mutex request until RRF's 3 s timeout, a cycle v7 does not have | yes: removes own-stream overtaking (NEW-3), keeps the passing that RRF's waits need |
| V7-11 why RRF refuses | gap | V8-6 (amended), V8-9, V8-36 | mutex refusals stall D-req(c) only; InvalidateChannel is never refused; Lock becomes a pending request; the 10 ms hand-over stays (V8-31) | as V7-10 | yes |
| V7-12 EmergencyStop | identical | V8-19 | a stop number makes it idempotent | — | — |
| V7-13 halt completion | changed | V8-20 | M112 completes on STOP_ACK, written after `reprap.EmergencyStop()` returned; on USB a dropped stop cannot complete | OF-P3, G-2 (K6) | yes; the STOP_ACK wait replaces the v7 wait for the transfer and keeps its 4.5 s bound (E-21) |
| V7-14 code buffer after a stop | gap | V8-15 (amended) | DSF keeps its credit view after a stop | v7 zeroes it and RRF need not report again (G-11, K12) | yes: codes after a stop get RRF's "stopped" result, which v7 also produces whenever the ring was not empty |
| V7-15 Reset | identical | V8-19 | — | — | — |
| V7-16 M112 is DSF's | identical | V8-22 | — | the flush is G-code semantics (wiki: M81 before M112) | — |
| V7-17 RRF-local stops | gap | V8-38 (new), V8-19 (amended) | RRF reports its own stops; DSF invalidates as after its own | Tim: both kinds of stop in the X class; X-5 (K20) | yes: "codes cancelled after a halt" is a v7 order (an M112 from DWC can meet an RRF-local stop); decision D9 |
| V7-18 firmware update | identical | — (class I, v2 C2) | — | — | — |
| V7-19 when DSF writes a Code | changed | V8-9, V8-15 | the block by an unconfirmed abort becomes the wait for RESET_ACK; buffer space becomes credit | CC-6 | yes: same blocking, explicit end |
| V7-20 code admission | gap | V8-15, V8-35 (new) | a code within the credit is never refused and there is no cascade; an invalid length is rejected for good instead of refused on every copy | CC-30; G-17, X-19 (K24): draft 2 had no answer for a code that can never fit | yes: "code ends with an error" is a v7 outcome |
| V7-21 flow control by estimate | changed | V8-15, V8-16, V8-17 | absolute credit instead of estimate, spare room and share | CC-28, CC-29, CC-30 | yes |
| V7-22 read-ahead drop (B7) | gap | V8-37 (new), V8-23 (amended) | — (kept) | V8-23 of draft 2 forbade the rule it rests on, with no replacement (Q5) | — |
| V7-23 RRF holds codes while a stack event is unsent | identical | — | — | RRF logic | — |
| V7-24 per-channel FIFO | identical | V8-4 | — | — | — |
| V7-25 comment codes get no reply | identical | — | — | — | — |
| V7-26 one final reply per code | changed | V8-10, V8-18; V8-14 (M5) | replies of an old epoch are dropped, a lost reply is reported; position matching stays until M5 | CC-7, NEW-2 | yes |
| V7-27 reply routing in DSF | identical | V8-2 | the header channel equals the one destination bit RRF sets | — | — |
| V7-28 one reply FIFO | changed | V8-10, V8-18 (amended) | each entry carries its code's epoch; a full FIFO records the loss; nobody waits | NEW-2 (OF S7); draft 2 left "who waits" open | yes |
| V7-29 suppressed replies | changed | V8-10; V8-14 (M5) | `codeReplySurplus` becomes the epoch tag; the M99 suppression stays until M5; output outside `HandleReply` is open (Q6) | CC-7 | yes |
| V7-30 partial replies survive invalidation | gap | V8-10 (amended), V8-24 (amended) | reassembly state dies with the epoch and the session | G-8 (K11) | yes: removes the stale prefix |
| V7-31 reply and stack event in both orders | identical | V8-14 (M5) | both orders stay until M5 | — | — |
| V7-32 drains | identical | V8-14 (M5), V8-29 | drains and Unbuffered M122/M409 stay until M5; no new drain | — | — |
| V7-33 completion for clients | identical | — | — | — | — |
| V7-34 blocking macro request | changed | V8-11, V8-39 | a request of an older epoch is not sent; the request carries a number | CC-5 (RRF #43) | yes |
| V7-35 several macros of one code | identical | — | — | — | — |
| V7-36 system macros on an empty stack | changed | V8-5, V8-34 | the refusal (MacroCompleted with error) is kept until acknowledged and cannot be lost | G-6 (K9) | yes: removes RRF's 3 s wait for a lost refusal |
| V7-37 macro that cannot be opened | identical | V8-39 | the case after the start belongs to V7-40 | — | — |
| V7-38 macro start, implicit start | changed | V8-7 | only an accepted Code starts the macro | DF-T11 family | yes |
| V7-39 daemon.g | changed | V8-11 | a request of an older epoch is not sent | A5 (OF T11) | yes |
| V7-40 macro wait timeout | gap | V8-39 (new) | late answers are dropped by request number; an expired request asks for RESET(c) | X-9, X-25 (K23); draft 2 had request numbers only in the table of section 5 | yes: "channel aborted after a macro request" is a v7 order (an abort can come at any time); decision D10 |
| V7-41 MacroCompleted | changed | V8-10; V8-14 (M5) | one of an old epoch is dropped; X-7 stays until M5 | — | yes |
| V7-42 MacroFileClosed | changed | V8-10, V8-24 (amended) | the epoch replaces #158 and RRF #45; no closal of the old session arrives | CC-3, CC-4; K5 | yes |
| V7-43 DSF's macro reader | identical | — | — | DSF logic | — |
| V7-44 M98 | identical | — | — | — | — |
| V7-45 single AbortFile, M99 | identical | V8-14 (M5) | — | CC-9…CC-12 stay until M5 | — |
| V7-46 AbortFile all | changed | V8-12 | RRF's abort of all files asks for RESET(c, e+1); DSF's channel then waits for RESET_ACK | G-20 (K18): late replies get a stale epoch and are dropped | yes; the RESET_ACK wait is E-16 of a DSF abort, bounded as C-4, and RRF runs codes of c only after the abort, as in v7 |
| V7-47 stack depth | identical | — | — | RRF logic; its overflow uses V7-46 | — |
| V7-48 WaitForAcknowledgement | identical | V8-14 (M5) | G-14 stays (K15) | — | — |
| V7-49 MessageAcknowledged | changed | V8-10 | an acknowledgement of an old epoch is dropped (a box at the base level after an abort, OF T10) | — | yes |
| V7-50 cancel with abort | identical | — | — | — | — |
| V7-51 M291 blocks only lower levels | identical | — | — | logic | — |
| V7-52 DSF side of an abort | changed | V8-9 | InvalidateChannel becomes RESET(c, e+1) after the same DSF work; G-21 stays (K19) | CC-1 | yes |
| V7-53 RRF side of InvalidateChannel | changed | V8-9, V8-11, V8-10 (amended) | receipt and apply are split; requests and prompts of the older epoch are resolved locally; DoCode stays untouched as in v7 | CC-5, CC-8 | yes: a prompt resolved as cancelled is what a user can do in v7 |
| V7-54 refused InvalidateChannel | changed | V8-9 | never refused | CC-6, CC-8 (DF-T11) | yes |
| V7-55 abort window 1 | removed | V8-10 | — | CC-2 (#134) | yes |
| V7-56 abort window 2 | changed | V8-9 | the window becomes the wait for RESET_ACK | CC-6 (#161) | yes: same wait (E-16), explicit end |
| V7-57 Lock | gap | V8-36 (new), V8-6, V8-7 (amended) | Lock is accepted and pending; RRF tries again every exchange; RESET cancels it and releases its lock | draft 2 made it a stall although a refused Lock keeps the lock it took (X-6); G-13 (K14) | yes: other channels still cannot add moves to the locked system; the Locked wait (E-15) is the v7 wait |
| V7-58 Unlock | identical | — | section 5 corrected: Unlock frees every resource of the GB (X-24) | — | — |
| V7-59 EvaluateExpression | changed | V8-13, V8-6 | the result echoes the request's sequence; stream D-req | CC-34 | yes |
| V7-60 SetVariable, DeleteLocalVariable | gap | V8-13, V8-7 (amended), V8-6 | echo; no assignment before a refusal; stream D-req | X-6 (K21): draft 2's V8-7 missed SetVariable | yes: removes "already exists" on a copy |
| V7-61 variables only after a flush | identical | — | the deletion without a flush stays (K25); a v7 logic fix (D11) | — | — |
| V7-62 SetLastCodeResult | changed | V8-34, V8-6 | completes on acceptance; stream D-req, so a refused copy is still passed by the next code (G-7 stays) | E-26 | yes |
| V7-63 object model | changed | V8-13 | a query id instead of one query per transfer and the key check | CC-32, CC-33, G-18 (K16) | yes |
| V7-64 logic waits for the OM | identical | — | — | — | — |
| V7-65 SetPrintFileInfo | gap | V8-34 (new), V8-31 (amended) | completes on acceptance, written before the M23 by causal order | C-1, C-2 (K1, K2): draft 2 kept the wait under the job lock | yes: RRF sees the same order; the cycle is gone |
| V7-66 ObjectModelKeyChanged | identical | — | OF-S3 stays a separate fix | — | — |
| V7-67 print end | gap | V8-34, V8-6 (amended) | completes on acceptance; PrintStopped is in D-req(File), not in the global stream | in the global stream a PrintStopped refused for the File mutex would hold back the file result that the holder of that mutex waits for (E-53, E-38) until RRF's 3 s timeout | yes |
| V7-68 PrintPaused | identical | — (D7) | — | — | — |
| V7-69 Message to RRF | identical | V8-6 | targeted messages in D-req | — | — |
| V7-70 DoCode | gap | V8-39 (new), V8-10 (amended) | a request number; DSF answers a cancelled DoCode with an error; DoCode is exempt from the epoch filter | G-12 (K13); draft 2's epoch filter would drop a DoCode or its result across a RESET, which v7 lets through | yes: an error result is a v7 outcome |
| V7-71 RRF's file I/O | changed | V8-13, V8-5 | requests and results carry an id; a late result is dropped; results are not lost | CC-35, X-8 (K22), G-6 (K9) | yes |
| V7-72 stack mirror by position | identical | V8-14 (M5) | — | — | — |
| V7-73 pop on the base level is fatal | identical | V8-10, V8-24 (amended) | stays; its stale sources (K5, T12) are filtered | — | — |
| V7-74 … V7-79 level routing, start code, pop effects, flush, prioritized codes, job read-ahead | identical | — | — | DSF and RRF logic, untouched by v8 | — |
| V7-80 M606 S1 | identical | V8-33 (amended) | C-3 stays until the v7 fix (K3, D11) | — | — |
| V7-81 triggers | identical | — | — | RRF logic | — |
| V7-82 no cross-channel order | gap | V8-29 (amended) | — (kept) | draft 2 stated one direction and "the order DSF wrote them"; RRF→DSF has production order for replies and channel-number order for requests; requiring more would add waits | — |
| V7-83 the link thread takes logic locks | gap | V8-31 (amended), V8-34 | the packet loop takes no lock whose holder waits for it | C-1, C-2 (K1, K2) | yes |
| V7-84 completion never blocks the link thread | identical | — | — | — | — |
| V7-85 DSF waits without timeout | gap | V8-32 (amended) | — (kept); the only new wait is STOP_ACK | draft 2 ended the session when a credit or stall wait made no progress, where v7 goes on | — |
| V7-86 … V7-88 RRF task waits | identical | V8-31 | — | the 10 ms hand-over and RRF's waits stay | — |
| V7-89 link-level bounds | identical | — (v2) | — | — | — |
| V7-90 fatal errors in DSF's packet loop | identical | — | the `ArgumentException` of V7-7 goes with V8-21 | — | — |
| V7-91 SPI reconnect | gap | V8-24 (amended) | no packet of the old session in the new one, except a stop without STOP_ACK | G-3 (K7) | yes |
| V7-92 DSF's SPI reset detection | identical | — (v2) | — | — | — |
| V7-93 USB failure | changed | V8-20, V8-23 | a dropped stop cannot complete M112; above L, USB behaves as SPI | G-2 (K6) | yes |
| V7-94 DSF invalidation | identical | V8-24 | (the zeroing of the code buffer is V7-14) | — | — |
| V7-95 RRF on a link loss | identical | V8-24 | the fork's stop on a link loss stays (CC-41) | — | — |
| V7-96 stale RRF flags across a reconnect | gap | V8-24 (amended) | RRF clears them in `InvalidateResources` | X-12 (K5): draft 2 said "as today" | yes |
| V7-97 what RRF assumes after a reconnect | identical | V8-24 | — | — | — |
| V7-98 protocol versions | identical | section 1 (D1) | the header keeps protocol version 7 | — | — |
| V7-99 protocol < 3 | identical | D2 | only on the v7 path | — | — |
| V7-100 `UpdateOnly` | identical | — | — | — | — |
| V7-101 link capabilities | changed | section 7, V8-35 | new capability `SessionLayer` = 8; nothing of v8 before both have it | — | yes (link only) |

## 5. Message classes and ordering

| Class | Meaning | Rules |
|---|---|---|
| X expedited | Stop and reset, STOP_ACK, and RRF's STOPPED (V8-38) | First in a transfer, executed on read, never refused, never in a stream, idempotent |
| C session control | RESET, RESET_ACK, channel state (ACK, NAK, reject, credit), RESET request, link capability activation | Never refused, never credit-limited, MUST be able to pass D and G (deadlock avoidance) |
| D(c) channel data | Everything bound to channel c, in two streams: D-req(c) for the requests RRF handles under the GB mutex, D-code(c) for the rest (V8-6) | In sequence order per (stream, direction, epoch); credit for codes; NAK per stream |
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

"may" means: allowed where v7 allows it too (V8-28, V8-29), i.e. when the earlier packet is stalled or waits for
credit. Otherwise packets keep the order in which DSF wrote them, across channels as well; within one transfer they
are processed in order, as today. **Barriers** across channels that v8 keeps:
- X: a stop or reset ends every channel.
- Lock (10) locks all movement systems; RRF says "currently this is used only by M505"
  (`RRF/SBC/SbcInterface.cpp:606`). Unlock (11) frees every resource the GB owns except those of the previous level,
  not only movement (`RRF/GCodes/GCodes.cpp:5648-5668`; draft 3 correction, X-24). The barrier is in the logic
  (standstill), not in the link.
- PrintPaused and PrintStopped act on File and File2 together (catalogue A10, `SbcInterface.cpp:506-542`, `:1495-1504`).
- Other cross-channel dependencies are not known (Q7). The v7 contract found none on either side beyond these
  (V7-82): DSF writes channels round robin, RRF writes replies in production order and requests in channel-number order.

**v7 messages in v8** (numbers from `RRF/SBC/SbcMessageFormats.h:269-349`):

| Dir | v7 request | v8 class | Identity in v8 | v7 pairing it replaces |
|---|---|---|---|---|
| → RRF | 0 EmergencyStop, 1 Reset | X | stop number | completion one transfer later (#88) |
| → RRF | 2 Code | D | (c, e, seq); credit | position of `BufferedCodes` |
| → RRF | 3 GetObjectModel | G | request id | queue head + one per transfer + key check |
| → RRF | 5 SetPrintFileInfo, 30 ObjectModelKeyChanged | G | seq | none (S3 framing bug stays a separate fix, OF-S3) |
| → RRF | 6 PrintStopped | D-req(File), barrier File/File2 (draft 3, V8-6) | seq | final empty reply (`Processor.cs:1129-1134`) |
| → RRF | 7 MacroCompleted, 18 MacroStarted | D-code | (c, e, seq); request number (V8-39) | "the next code belongs to the macro" (B8) |
| → RRF | 10 Lock, 11 Unlock | Lock D-code, a pending request (V8-36); Unlock D-req; logic barrier | (c, e, seq) | resend as polling |
| → RRF | 16 EvaluateExpression, 20 SetVariable, 21 DeleteLocalVariable, 29 SetLastCodeResult | D-req | (c, e, seq), echoed | (channel, expression) or (channel, name) |
| → RRF | 17 Message | D-req if targeted, else G | request number if it answers a DoCode (V8-39); not filtered by epoch then (V8-10) | next targeted message finishes the GB (B9) |
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
| → DSF | 12 DoCode | D, not filtered by epoch (V8-10) | request number (V8-39) | — |
| → DSF | 14 MacroFileClosed, 15 MessageAcknowledged | D | code epoch; level's start code seq (V8-14) | pop the current level |
| → DSF | 10 FileChunk (deprecated, `:281`); 17–27 file requests | G | request id | one slot, synchronous |
| → DSF | new: RESET_ACK, STOP_ACK | C; X | epoch; stop number | — |
| → DSF | new: STOPPED (draft 3, V8-38) | X | stop number | none: v7 sends nothing for an RRF-local stop (V7-17) |

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
| Per stream: next expected sequence (u16 × 2 × 16, two D streams per channel since draft 3) | 64 | V8-5 |
| Stall bitmaps (two stream kinds), dirty and reject bitmaps | 8 | V8-6, V8-8, V8-35 |
| Pending Lock bitmap (draft 3) | 2 | V8-36 |
| STOPPED flag (draft 3) | 1 | V8-38 |
| Request number per GB (draft 3) | 16 | V8-39 |
| Per channel: epoch, pending epoch, flags (3 × 16) | 48 | V8-9 |
| Per GB: epoch of the current code; epoch of the pending request (2 × 16) | 32 | V8-10, V8-11 |
| Reply FIFO tag, 1 B × 64 entries (`OutputMemory.h:15-19`) | 64 | V8-10 |
| File slot request id (padded) | 4 | V8-13 |
| Credit: released counter + capacity | 4 | V8-15 |
| Stop number, STOP_ACK flag | 2 | V8-19, V8-20 |
| Lost-reply marker | 2 | V8-18 |
| Counters (if V8-27 is done in RRF) | 64 | V8-27 |
| **M1–M4 total** | **≈ 311** (draft 2: 256) | 0.32 % of 95,868 |
| V8-17 per-channel limits in RRF (only with D4 b) | +64 | V8-17 |
| V8-14: code sequence per GB, FIFO tag 1 → 4 B | +224 | V8-14 |
| V8-14: 2 B per pushed level in existing heap objects, worst case | ≤ +300 | V8-14 |
| **Everything, worst case** | **≈ 900** (draft 2: 850) | 0.94 % |
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
| M0 | Preparation: RRF S layer as its own file (V8-26), harness extension, fake firmware v8 mode. The wait-for graph and the cross-channel dependencies (V8-30, Q7, V8-33). NEW-1 is fixed in v7 separately, as a normal fork fix with its own test. Draft 3: C-3 and K25 fixed in v7 the same way (V8-33, D11) | none | existing suites green; V8-33 reviewed |
| M1 | V8-1–V8-4, V8-9–V8-12, V8-23, V8-24, V8-35, V8-37 | CC-1–CC-8 (A0, #134, #158, RRF #45, RRF #43, #161, RRF #46 both parts). 7 of them fork-only | the TriggerFuzz abort tests and `TriggerMacros.TriggerChannelWorksAfterInvalidationDuringTriggerRequest` (DF-T1, T2, T10, T11, T12) green in v8 mode with the catch code removed; lab `AbortsCrossingMacroRequests` with `LAB_ABORT_ITERATIONS=2000`: no block, no stack underrun, no "none was requested", **no out-of-order reply** |
| M2 | V8-5–V8-8, V8-21, V8-34, V8-36 | CC-25, CC-26, CC-27, CC-31 | NAK of every D request type at every position with other channels interleaved (fake firmware `FailBufferLocks`); harness replay of the S layer with 0 differences; the same tests over the USB adapter. Draft 3: an M98 whose macro answer follows a refused DeleteLocalVariable starts without a timeout; PrintPaused and AbortFile(File) sent while M23 waits for SetPrintFileInfo (C-1) do not block |
| M3 | V8-15–V8-18 | CC-28, CC-29, CC-30 | `TriggerMacros.TriggerMacroGetsSpareCodeBufferRoom`, `LinkInterfaceRequests.CodesBufferedAheadLeaveSpareRoom`; lab `CodeBufferReserve.TriggerMacroGetsRoomWhileOtherChannelsReadAhead` (DF-T3, LT §14, 2026-11-08) |
| M4 | V8-13, V8-19, V8-20, V8-22, V8-38, V8-39 | CC-32, CC-33, CC-34, CC-35, CC-37, CC-40 | `StopPath`, `StopPathFuzz`, `M112RepliesOnlyOnceRrfIsStopped` (OF-P3) pass; object model tests across a DCS restart (LT §9, "Failed to find query") |
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
| AC7 | No deadlock: in every fuzz and lab run each code ends (reply, cancel or error) within the bound of V8-32; a watchdog in the fake firmware host flags any channel that has work but makes no progress |
| AC8 | Refinement (V8-28): the same scripted input in v7 mode and v8 mode gives per channel the same sequence of logic events, apart from the events that the v7 catch code drops |

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
| D9 | How DSF learns of an emergency stop that RRF made by itself (V8-38, V7-17) | (a) X-class STOPPED; DSF runs `InvalidateCodes` and reports halted, as after its own M112; (b) as v7: no packet, DSF sees it in the object model and keeps its macro levels and codes until M999 restarts DCS | (a). Tim asked that both kinds of stop are covered by the X class, and only (a) leaves the two stacks equal (V7-72). Behaviour change: after an RRF-local stop DSF cancels its codes at once instead of letting RRF answer them with "stopped"; the logic sees that order in v7 when an M112 from DWC meets an RRF-local stop |
| D10 | What RRF does when a macro wait expires after the request went out (V8-39, V7-40) | (a) drop late answers by request number and request RESET(c); (b) only drop late answers: DSF keeps the level it pushed and its macro codes run at the caller's level (X-9 stays); (c) as v7 | (a). Only (a) leaves the stacks equal. Behaviour change: the channel's whole stack is aborted instead of the caller going on after "not found"; this happens only when DSF answers later than 3 s |
| D11 | When to fix the v7 cycles C-1 to C-3 and the deletion of while-loop locals (K1 to K3, K25) | (a) as fork fixes in v7 now, each with a unit test that shows the fault first (CLAUDE.md rules 1 and 7); (b) only through v8: V8-34 removes C-1 and C-2, while C-3 and K25 are in the logic and need a v7 fix anyway | (a) for C-3 and K25, which v8 does not remove and V8-33 needs gone before M1; for C-1 and C-2 first a test that shows reachability, then (a). All the lines involved are upstream code, so they are also candidates for a report to Duet3D, once each is shown not to come from fork code |

## 10. Open questions and what is unverified

| # | Question | Status |
|---|---|---|
| Q1 | NEW-1: RRF composes its USB answer one transfer behind (`DataTransfer.cpp:1302-1357`, `:1362-1371`), so `USB.cs:717-721` looks into the wrong buffer, may copy the wrong packet, or throws `ArgumentException`, which `LinkService.cs:446` does not catch | Code reading, not tested. OF-T13 relied on the `USB.cs` comment and probably does not exist |
| Q2 | NEW-2: can the 64-entry reply FIFO fill in practice (stalled link, M122 chunks)? | Not shown |
| Q3 | NEW-3: can RRF refuse a code that DSF's estimate allowed? | Not shown |
| Q4 | Is logical credit always physically storable, given that `DefragmentBufferedCodes` runs only when 384 bytes or less are free and moves one gap per run (`SbcInterface.cpp:2436-2525`)? | Not analysed |
| Q5 | B7: how RRF tells in-flight read-ahead codes from new ones after a push without counting ExchangeData: a per-channel generation counter, or a stack-level tag. RRF levels pushed by M120 (`localPush`) may not exist in DSF | Not analysed; B7 is upstream design and has no known failure |
| Q6 | Does every message of a code pass one capture point for its tag? OF-T10 lists writers outside `HandleReply` (M122 `GCodes2.cpp:2292`, `GpInPort.cpp:90`, `GCodes4.cpp:1894`) | Not checked |
| Q7 | Cross-channel dependencies in the logic beyond Lock, pause and stop | Partly answered by the v7 contract (V7-82): neither side's link handling relies on cross-channel order; a list of the logic's own dependencies (e.g. M606, M98.n) is still to be done in M0; V8-29, V8-30 and V8-33 depend on it |
| Q8 | The switch point of `SessionLayer` within a connection, given the switch-window findings of the extended header (OF-S1, OF-S5) | Open |
| Q9 | Is `AbortFile(true)` harmless when RRF already aborted all files itself (V8-12)? | Not checked |
| Q10 | Does a refused `LockAllMovementSystemsAndWaitForStandstill` have side effects (V8-7)? | Answered in draft 3: yes, it keeps the lock of the movement system it reached and releases only the lower ones (`RRF/GCodes/GCodes.cpp:1947-1962`, `:1969-1999`); V8-36 |
| Q11 | SetPrintFileInfo alignment: the 64-bit `lastModifiedTime` (`SbcMessageFormats.h:460`) after another packet in the same transfer (catalogue D4, `LinkService.cs:407`) | Open, also in v7 (G-19) |
| Q12 | H1 ("Daemon: Out-of-order reply ''" after a DCS start) and H2 ("Failed to find suitable stack level for flush request") | Causes not known; whether v8 removes them is not known |
| Q13 | Which DSF-internal codes complete without a flush while an earlier RRF code of their level runs, and so write a mutex request (SetLastCodeResult, DeleteLocalVariable) during RRF's macro wait? K25 is one case | Not listed. V8-6 does not depend on the list; its tests should cover each case |
| Q14 | Can C-1, C-2 and C-3 be reached (v7 contract section 15)? | Not shown. A unit test with the fake firmware can send PrintPaused or AbortFile(File) while M23 or M606 S1 holds the job lock |
| Q15 | Can a MacroCompleted cross an RRF-side pop and mark the next outer macro finished (X-7)? | Not shown; V8-14 (M5) would remove it |

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
- Draft 3: the v7 contract and its alignment (section 4.9) come from reading the code; none of its 25 findings K1–K25
  was tested. The new requirements V8-34 to V8-39 and the stream split of V8-6 are designed on paper only; whether
  two streams per channel cover every case of Q13 is not shown.

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

Repository: [sbc-link-v7-contract.md](sbc-link-v7-contract.md), [sbc-link-requirements.md](sbc-link-requirements.md), [sbc-link-current-state.md](sbc-link-current-state.md),
[sbc-link-findings-upstream.md](sbc-link-findings-upstream.md), [dsf-findings-upstream.md](dsf-findings-upstream.md),
[open-findings.md](open-findings.md), [rrf-local-trigger-actions.md](rrf-local-trigger-actions.md),
[lab-test-plan.md](lab-test-plan.md). Duet3D wiki: `User_manual/Reference/Gcodes.md` (M112), clone at a106ad4.
