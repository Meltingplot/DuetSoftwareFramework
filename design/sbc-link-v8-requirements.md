# SBC link v8: link and session layer requirements

Status: draft 4, 2026-11-09. Nothing here is implemented. No code was changed for it. The decisions of section 9 are
Tim's; the requirements are not agreed yet.
Draft 2 adds Tim's requirement of the same day: "The protocol must guarantee a logical compatibility to the current
implementation, so that we do not create deadlocks due to concurrency" (PR9, section 4.8, V8-4, section 5, AC7, AC8).
Draft 3 checks every requirement against the v7 contract reverse engineered from the code
([sbc-link-v7-contract.md](sbc-link-v7-contract.md), items `V7-n`, edges `E-n`, cycles `C-n`), as Tim asked: "check
the requirements against the current requirements of v7 … make sure that they align or name the difference and the
reason". Section 4.9 gives every V7 item its status. Draft 3 added V8-34 to V8-39 and decisions D9 to D11.
Draft 4 records the decisions Tim made in an interview on 2026-11-09 (section 9) and applies his layering rule:
> We must not mix levels: motion is not link, and safety is neither link nor protocol. If the link is down it must
> signal event-based to the logic; the logic decides.

and "no special cases in the link — if it is not defined, it does not exist". This document therefore holds only link
(L) and session (S) requirements, each tagged with its layer. What the logic of DSF or RRF does on an event, and the
defects of the logic, are in [sbc-link-v8-logic-adaptations.md](sbc-link-v8-logic-adaptations.md) (items `LA-n`).
Draft 4 makes v8 its own protocol version (section 1, V8-41), puts the v2 link features into v8 (V8-40), adds PR10
"link faults never end a print" (section 4.10), the per-packet integrity check (V8-42) and one stream per message
(V8-48), states the general rule of the two streams per channel (V8-6) and the write order across streams (V8-29,
V8-34), moves V8-22 and the logic parts of V8-36, V8-38 and V8-39 to the logic list, and drops the precondition of
V8-33. Changed text is marked "Draft 4".
Audience: Tim, later Christian Hammacher (Duet3D). The reader knows DSF and RRF, not the working papers.
Scope: the link and session layers of v8: the v8 link (the v2 link features, always on), channels, ordering, priority
and overtaking, cancellation, correlation, flow control, retransmission above the link, integrity per packet, the
session lifetime and the events the layers report to the logic.
Not in scope: what the logic does on an event and the defects of the logic (logic list); safety functions (v2 SB1);
IAP flashing (v2 C2); G-code semantics. [sbc-link-requirements.md](sbc-link-requirements.md) (v2) keeps the framing,
transfer retransmission, recovery times and the stop path on the wire (v2 R1–R5, R10, R12, R14); v8 takes over v2 R6,
R8, R9 and R13 (section 1).

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
    graph (`E-1`…`E-53`, cycles `C-1`…`C-8`) and the v7 defects (findings `K1`…`K27` in open-findings.md).
- **IDs of other documents are qualified,** because several collide:

| Prefix | Document |
|---|---|
| `v2 Rn`, `v2 Cn`, `v2 SBn` | [sbc-link-requirements.md](sbc-link-requirements.md) |
| `LA-` | [sbc-link-v8-logic-adaptations.md](sbc-link-v8-logic-adaptations.md) |
| `CS-` | [sbc-link-current-state.md](sbc-link-current-state.md) |
| `SF-` | [sbc-link-findings-upstream.md](sbc-link-findings-upstream.md) |
| `DF-` | [dsf-findings-upstream.md](dsf-findings-upstream.md) |
| `OF-` | [open-findings.md](open-findings.md) |
| `LT §14` | [lab-test-plan.md](lab-test-plan.md), section 14 (lab runs) |

- **Status of statements:** "printer" = seen on the lab printer, with the run in LT §14. "Code" = from reading the
  code, not tested. Everything open is in section 10.
- **Keywords:** MUST, SHOULD, MAY as in RFC 2119.

## 1. Relation to v2 and to the decision "no fork-only v8"

- The v2 decision log (2026-10-08) chose "Negotiation on top of v7 instead of a fork-only v8", because mixed versions
  keep working and no protocol number collides with upstream (v2 C1). Drafts 1 to 3 kept it: v8 was to be a
  capability `SessionLayer` on the v7 framing, with protocol version 7 in the header (`RRF/SBC/SbcMessageFormats.h:24`).
- **Draft 4 reverses that decision for normal operation (D1, D2; Tim, 2026-11-09).** v8 is its own protocol version,
  with a number from a fork range. It is not 8, so that it cannot collide with a future upstream version 8; the idea is
  the same as 0x4D50 for ActivateLinkCapabilities. If Duet3D adopts v8, it moves to upstream's number.
  Reason: the goal is to remove the catch code, and the catch code can only go where v8 runs. Negotiated on top of v7,
  every mixed pair would run v7 and keep it. As its own version, required for normal operation, v8 removes the fork's
  v7 catch code of both sides wherever it runs. Mixed stock and fork pairs remain possible for firmware updates only
  (V8-41, section 7). USB and boards other than SAME70 keep v7 until they are ported (D6), and with it the catch code
  of the v7 path.
- v8 is built now, without waiting for upstream, and shown to Christian Hammacher once M1 has passed the lab (D8).
- **The v2 link features are part of v8 from the first transfer (D14, V8-40):** the v8 transfer header is the extended
  header of v2, PhaseGuard and SequenceGuard are always on, and there is no activation with 0x4D50. There is no switch
  window any more, which closes Q8 and, for v8, the switch-window findings OF-S1 and OF-S5.
- v8 needs two things from the link:
  - Each transfer is delivered once and in order within a session (v2 R2). Draft 4: also across an interruption
    within the session lifetime (V8-43). The census shows v2 R2 for single faults with all capabilities active (v2
    "Implementation status"); multiple faults and the bench test are open.
  - A session change is visible. Draft 4: a session id per side that changes only on a restart (V8-45). The v2
    status (0x7FF or 0) does not tell a restart from a connection reset
    (`DCS/Link/Protocol/Shared/ExtendedLink.cs:47-48`, `:62-65`).
- v8 takes over four v2 requirements; the v2 document marks them superseded:
  - v2 R6 (real restarts are caught) becomes the event PeerRestarted (V8-45, V8-46); the stop is the logic's
    reaction (LA-3);
  - v2 R8 (total silence) becomes the session lifetime and the event SessionExpired; the stop is the logic's
    reaction (LA-1);
  - v2 R9 (integrity) becomes the per-packet check (V8-42);
  - v2 R13 (dead-man switch) is not a link requirement: its stage 1 moves to the logic and safety work (V8-47, LA-4),
    its stage 2 is R8.
- v2 R10 (stop within 100 ms on a disturbed link) stays a link requirement. v8 defines what "expedited" means above
  the link (V8-19, V8-20) and carries the expedited flag in its header (D5).

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

**Draft 4, a second problem:** in v7 a link fault ends or disturbs a print. RRF stops on every lost connection, DSF
aborts the job after every reconnect, and DCS exits on several kinds of link data. Section 4.10 lists the paths.

## 3. Principles and layering

| Layer | Owns | State | Does not |
|---|---|---|---|
| L: link (v7 framing with the v2 extended header, PhaseGuard and SequenceGuard always on, D14) | Transfers, CRC, response codes, transfer sequence and phase guards, transfer retransmission, resync after an interruption, the link health state | Per link | Know channels or request types; act on the machine |
| S: session/channel (new, thin) | Session id and lifetime; packet header fields channel, epoch, sequence; per-packet check; message classes; ordering; ACK/NAK; credit; RESET handshake; stale-epoch filter; request ids; the events reported to the logic | Fixed table of 16 channel entries on each side | Take logic mutexes in the packet loop, except to hand over logic requests (V8-7); act on the machine; decide what the logic does on an event |
| G: logic (unchanged where possible) | G-code execution, macro stack, object model, files; the reactions to link events (logic list) | `GCodeBuffer`, `Processor` | See transfers, packet ids or pipeline depth |

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
| PR10 | **Draft 4. Link faults never end a print.** The link repairs and resyncs for as long as the session lives. Within the session lifetime the link and session layers stay in state and only report events; they have no motion or safety function, and the logic decides what to do. A defined event exists for every case; there is no special case in the link. | Tim, 2026-11-09 (layering rule, section 9 D12); v2 Vision ("nothing that happens on the link may reset it or end the job"), v2 R1, v2 SB1 |

## 4. Requirements

Columns: **Layer** = L (link) or S (session), section 3. **Removes** = catalogue items (`CC-n`) whose catch code is no
longer needed in a v8 session. **RAM** = new static RRF RAM; arrays have 16 entries (15 channels,
`RRF/GCodes/GCodeChannel.h:20`, plus alignment). **Logic** = change to G-layer code that implementing the requirement
needs; what the logic does on an event is not a requirement here but an item of the logic list.

### 4.1 Layering, header, classes

| ID | Layer | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|---|
| V8-1 | S | Both programs MUST have an S layer between L and G as in section 3. L MUST NOT depend on request types; G MUST NOT depend on transfers, packet ids or pipeline depth. **Draft 4:** L and S MUST NOT act on the machine and MUST NOT decide for the logic; they report events (V8-46, V8-47). | P12; AMQP 1.0 Part 2 §2.1.1 | enables all | 0 | none (code moves into S) |
| V8-2 | S | Every packet MUST carry request, length, channel, epoch and sequence in a fixed header. Global packets use a reserved channel value. Receivers use only the header channel; v7 payload channel fields keep their place and are written equal. Illustration: request (2), length (2), channel (1), epoch (1), sequence (2) = 8 bytes, the size of the v7 header, because `id` and `resendPacketId` are not needed (V8-21). **Draft 4:** the header also carries the per-packet check (V8-42), and every packet starts on an 8-byte boundary, so that a 64-bit field such as `lastModifiedTime` of SetPrintFileInfo (`RRF/SBC/SbcMessageFormats.h:460`) is aligned wherever the packet sits in the transfer (Q11). | P1; RFC 9113 §4.1; AMQP 0-9-1 §2.3.1, §4.2.3 | — | 0 | none |
| V8-3 | S | Every request type MUST belong to exactly one class (X, C, D, G, section 5) through one fixed table in this spec. No header bit duplicates it. | P5; RFC 9218 §2 | — | 0 | none |
| V8-4 | S | The may-pass table of section 5 is normative. D packets of one retransmission sequence (V8-6) and direction MUST be processed in sequence order. Packets of different channels MUST reach the logic in the order DSF wrote them, as in v7, and MAY pass each other only where v7 lets them (V8-29). Draft 3: "per sequence" instead of "per channel", and the order rule holds per direction as V8-29 says. | P2, P11; PCIe ordering table (secondary sources only) | — | 0 | none (V8-28) |

### 4.2 Sequence, acknowledgement, refusal

| ID | Layer | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|---|
| V8-5 | S | DSF MUST number D and G packets per (stream, epoch) from 0, 16 bits, compared by RFC 1982 §3.2. A stream is one retransmission sequence: D-code(c) or D-req(c) of a channel (V8-6), or the global stream. RRF MUST report per stream the next sequence it expects, as an absolute value, whenever it changed. DSF keeps every packet until it is acknowledged. Draft 3: two streams per channel. | P3; RFC 9260 §6.5; AMQP 1.0 Part 2 §2.8.10 | CC-25 (resend lookup by transfer position, `DCS/Link/Adapter/SPI.cs:823`), CC-27 (NEW-1, `USB.cs:717-721`) | 64 B (u16 × 2 × 16) | none |
| V8-6 | S | "Not accepted now" MUST be an acknowledgement with a stall flag at sequence n of one stream. RRF then drops later packets of that stream until n arrives again. DSF resends n, n+1, … of that stream only, with identical content. Other streams go on. Draft 3: each channel c has two streams, D-req(c) and D-code(c). **Draft 4, the general rule: a request that RRF may refuse while the GB mutex is busy MUST NOT block the codes of its channel or the answers that RRF's waits on that channel need.** So every such request goes into D-req(c): Unlock, EvaluateExpression, a Message for channel c (all its chunks, V8-48), SetVariable, DeleteLocalVariable, SetLastCodeResult; PrintStopped goes into D-req(File). D-code(c) holds the other D packets of c: Code, MacroStarted, MacroCompleted, Lock (V8-36). Reason: RRF admits a Code, a MacroStarted and a MacroCompleted for a waiting macro without the mutex (`RRF/SBC/SbcInterface.cpp:382-392`, `:558-566`, `:827-865`), and the mutex holder can be waiting for exactly these: an M98 holds the mutex in its macro wait (E-37, V7-87). In one stream they would stall behind a refused request until RRF's 3 s timeout, a wait v7 does not have, because there the later packet passes the refused one (V7-10). The split adds no order that v7 lacks (Q7): RRF applies a request when it reads it (e.g. `:1131-1140`, `:1307-1314`) but stores a code in its ring (`:401-433`) and runs it later, so the logic orders a request and a code of one channel only by a flush or by waiting for an answer. The one exception is SetLastCodeResult before the next code (V7-62), and v7 already lets the next code pass a refused one (G-7, K10). Examples of requests that can be refused during a macro wait, not things v8 fixes: the stale-variable deletion of M260 and M261 with V (K25, LA-16) and the SetLastCodeResult of a code DSF handles itself (K26, LA-17). Within D-req(c) the order of requests is kept across a refusal, which v7 does not do (K27). A MacroCompleted refused while RRF does not wait stalls D-code(c) harmlessly, because DSF sends nothing else on c until MacroFileClosed (E-17). PrintStopped(normal) needs the File and the File2 mutex (`:521-539`); its stall in D-req(File) is bounded by RRF's main-task waits and accepted. No G packet is refused for a mutex, so a file result never waits behind one (E-38, E-53); G packets can be refused for resources (Q16). | P3, P9; USB 2.0 §8.6.2; MQTT 5.0 §4.6; RFC 9000 §2.2 | CC-26 (`_packetsBeingResent`), NEW-3; CC-31 (Lock polled by resend, `SPI.cs:1107-1120`, `LinkService.cs:615-619`) becomes a pending request (V8-36) | 4 B (stall bitmap per stream kind) | none |
| V8-48 | S | **Draft 4. One message = one stream.** All chunks of one Message, the Push chunks and the final chunk, MUST go into the same stream, chosen by destination: D-req(c) when channel c is its only destination, the global stream otherwise. The stream MUST NOT depend on whether RRF needs a mutex for a chunk. DSF splits DoCode results and asynchronous results for USB and Aux into chunks with PushFlag (`DCS/Codes/Pipelines/Executed.cs:185-209`); RRF takes the GB mutex only for the final chunk of a message whose only destination is a channel executing on the SBC (`RRF/SBC/SbcInterface.cpp:774-797`). Split by mutex need, the Push chunks would travel in another stream than the final chunk. In v7 a refused final chunk is overtaken by later messages (V7-10); in one stream it is not. | P2, P3 | — | 0 | none |
| V8-7 | S | RRF MUST NOT refuse a packet that has had an effect. Handlers that today act before they refuse (InvalidateChannel `RRF/SBC/SbcInterface.cpp:883-895`, Code `:382-392`) MUST act only once accepted, or never refuse. Draft 3 adds the two the RRF side found (v7 contract X-6): SetVariable evaluates and assigns, then refuses if its VariableResult does not fit, so the copy of a `var` or `global` fails with "variable '…' already exists" (`:1022-1093`); it MUST reserve the OutputBuffer and the TX room before it assigns. Lock keeps the lock of the movement system it reached when it refuses (`RRF/GCodes/GCodes.cpp:1947-1962`, `:1969-1999`); V8-36 makes it a request that is never refused. Re-evaluating GetObjectModel and EvaluateExpression on a copy has no effect; restarting the burst window (`SbcInterface.cpp:753`) changes only the pacing. | P9; RFC 9113 §8.7 | root of DF-T11 resend; X-6 (K21) | 0 | small: statement order in RRF `SbcInterface::ExchangeData` only |
| V8-36 | S | **Draft 3. Lock is a pending request, not a stall.** RRF MUST accept LockAllMovementSystemsAndWaitForStandstill (10) and keep it as one pending bit per GB. At every exchange it tries `LockAllMovementSystemsAndWaitForStandstill(gb)` under the GB mutex with `SbcYieldTimeout`, as v7 does for every resent copy (`RRF/SBC/SbcInterface.cpp:606-634`), and sends Locked once that succeeds. The partial lock it keeps between tries stays as in v7 (`RRF/GCodes/GCodes.cpp:1947-1962`), so other channels still cannot add moves to the system it reached. RESET and a session change cancel a pending Lock and release what it holds (`UnlockAll`, `GCodes.cpp:5648-5668`). In v7 such a lock stays until RRF releases it by itself (G-13; the release point `GCodes2.cpp:5016` was seen only in passing). **Draft 4:** what DSF does with a Locked whose request it no longer has is logic (LA-9). | P8 (pending answer), P9 | CC-31; G-13 (K14) | 2 B (pending bitmap) | none in S; LA-9 |
| V8-8 | S | ACK, NAK, credit and RESET_ACK SHOULD be one C-class "channel state" report with absolute values (epoch, next sequence, stall, credit), so a repeated or reordered report is harmless. It replaces `ResendPacket` (0) and `CodeBufferUpdate` (2). RRF reserves TX space for it as it reserves space for resend headers today (`RRF/SBC/DataTransfer.h:228-231`). | P4 pitfalls; RFC 9000 §4.2 | — | 2 B (dirty bitmap) | none |

### 4.3 Cancellation and epochs

| ID | Layer | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|---|
| V8-9 | S | InvalidateChannel MUST become RESET(c, e+1), and RRF MUST never refuse it. On receipt, without the GB mutex, RRF records e+1, ends a macro wait, and purges the ring codes of c (`InvalidateBufferedCodes` needs only a critical section, `RRF/SBC/SbcInterface.cpp:2527-2529`). With the GB mutex, later, it applies `PauseSequenceAborted`, `AbortFile(true)` (`:899-902`). Then it sends RESET_ACK(c, e+1). A repeated RESET(c, e+1) gets the same ACK. DSF sends no D packet on c between RESET and RESET_ACK. **Draft 4:** this is also the guarantee for a job abort: codes of an older epoch are dropped on both sides. PrintStopped(abort) purges the ring codes of File and File2 in RRF without an epoch change (`:509-517`); that DSF resets both channels before it is logic (LA-12). | P6; RFC 9113 §6.4; RFC 9000 §3.5, §13.3; RFC 6525 §5.2.1; AMQP 0-9-1 §2.3.7 | CC-1 (silent closes), CC-6 (#161: `Processor.cs:126-136`, `:883-905`; `ILinkAdapter.cs:203`; `LinkService.cs:620-626`), CC-8 (`invalidationPending`, `GCodeBuffer.h:422`) | 48 B (epoch, pending epoch, flags × 16) | small: the RRF InvalidateChannel handler is split into receipt and apply, with today's calls (`:880-911`). Behaviour as with #161: the channel waits for RRF's answer, now an explicit one |
| V8-10 | S | Every D packet MUST carry an epoch; receivers MUST drop D packets of another epoch and count them. RRF MUST tag a message with the epoch of the code or request it belongs to, taken when the code was handed to the GB (`FillBuffer`, `:1890-1950`) or the request was made, not with the current channel epoch. The tag of a queued reply is captured when it is queued (`HandleGCodeReply`, `:2353-2406`). Draft 3: (a) the reassembly state of a reply (Push chunks, DSF `_lastPartialMessage`, `DCS/Link/Channel/Processor.cs:1184-1189`) belongs to (channel, epoch) and MUST be discarded with the epoch and on a session change; v7 keeps it across `Invalidate` and prepends it to the next reply (V7-30, G-8). (b) DoCode (12) and the Message that answers it are exempt from the epoch filter: RESET does not end a DoCode in v7 (InvalidateChannel leaves `sendToSbc` and the executing state alone, V7-53, V7-70), and a dropped result would leave RRF's GB "executing on SBC" without a bound. **Draft 4 (Q6):** of RRF's Messages only code replies (`BinaryCodeReplyFlag`) carry an epoch and are filtered. The S layer takes the tag when the reply enters the reply queue, in `HandleGCodeReply` (reached from `RRF/Platform/Platform.cpp:3254-3256`, `:3266-3270`, `:3315-3317`), in link code only; M0 audits every writer of code replies. Generic, targeted and log messages are never dropped: DSF routes them only to the object model, IPC and the event log (`DCS/Link/LinkService.cs:764-813`). The text of a filtered reply still goes to the event log, as DSF logs every message not flagged LogOff before it routes it (`:769-788`). | P6, P7; RFC 9000 §4.4 | CC-2 (#134, `Processor.cs:119-124`, `:888-892`, `:1357-1362`), CC-3 (#158, `:1285-1290`), CC-4 (RRF #45, `SbcInterface.cpp:904`), CC-7 (RRF #46 `codeReplySurplus`: `GCodeBuffer.h:405`, `GCodes.cpp:4450-4458`, `:4584-4593`, `:3703-3708`); A7 (OF-T13); G-8 (K11) | 80 B (code epoch per GB 16 B, tag per FIFO entry 64 × 1 B) | small: one capture point in `HandleGCodeReply`; CLAUDE.md rule 3 prefers passing the identity as a parameter down the reply path (`GCodes.cpp` reply functions) |
| V8-11 | S | Per-GB request state (`requestedMacroFile` `GCodeBuffer.h:403`, the abort, prompt and closal flags) MUST be bound to the epoch it was created in. RRF MUST NOT send a request of an older epoch; it resolves it locally as failed. This covers state machines that go on after a RESET (OF-T10: G28, `GCodes4.cpp:358-413`). | P6 "what epochs do not fix"; PR7 | CC-5 (RRF #43, `SbcInterface.cpp:891-894`); A5 (OF-T11 daemon.g) | 16 B (request epoch per GB) | small: one compare where requests are written (`:1513-1527`, `:1597-1611`) |
| V8-12 | S | An RRF abort of all files (`AbortFile`, abortAll) SHOULD be a request for a RESET, which DSF answers with RESET(c, e+1). DSF stays the only writer of epochs. Draft 3 note: DSF's channel then waits for RESET_ACK where v7 sends at once; the wait is E-16 of a DSF abort and bounded as C-4 (the receipt part ends a macro wait, the apply waits for the GB mutex, which RRF's 3 s waits bound), and RRF still runs codes of c only after the abort, as in v7. V8-39 uses the same request. | RFC 9000 §3.5 (STOP_SENDING) | the one-transfer drop after the abort request (`gb->Invalidate()`, `:1575`) for abort-all; late replies after RRF's own abort (G-20, K18) | 0 | small: DSF `HandleAbortFileRequest` (`LinkService.cs:866-889`) issues the RESET after `FilesAborted(true)` |

### 4.4 Correlation

| ID | Layer | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|---|
| V8-13 | S | Global requests and their answers MUST carry an id that the answer echoes: GetObjectModel/ObjectModel, RRF file requests (17–27) and DSF file results (22–28, 31). Evaluations and variables echo the sequence of their D packet. An answer without a pending id is dropped and logged as a protocol fault. | P8; JSON-RPC 2.0 §4, §6; Modbus TCP guide §4.4.1.3 | CC-32 (one query per transfer, `QuerySent`, `LinkService.cs:470-478`, `:714`), CC-33 (fork key check 4dc381d0, `:716-723`), CC-34 (content match, `:1005-1023`, `:1123-1141`), CC-35 (file slot by state, `SbcInterface.cpp:1204-1207`, `:1221-1224`) | 1 B in the file slot. OM and evaluation answers are written while the request is processed (`:443-494`, `:682-760`), so the id is copied | none |
| V8-39 | S | **Draft 3. RRF requests carry a number; late answers are dropped.** Every ExecuteMacro and DoCode MUST carry a request number per GB that DSF's answer echoes (MacroStarted, MacroCompleted, the DoCode result). RRF MUST drop and count an answer whose request is no longer pending, which in v7 happens after the 3 s macro timeout (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1263-1274`): a late MacroStarted gives "none was requested", a late Code runs at the caller's level, a late MacroCompleted(error) pops whatever level is current (V7-37, V7-40). **Draft 4:** RRF MAY request RESET(c) for a request that is no longer pending (V8-12). When it does is logic: after a macro wait that expired once the request had gone out (LA-8, D10). That DSF answers every DoCode, also one it cancels, is logic too (LA-10). | P8, P6 | X-9, X-25 (K23); G-12 (K13) with LA-10 | 16 B (request number per GB) | small: RRF compares the number where answers are read (`SbcInterface.cpp:382-392`, `:558-566`, `:772-808`, `:827-865`) |
| V8-14 | S | Code replies and stack events (ExecuteMacro with fromCode, WaitForAcknowledgement, single AbortFile, MacroFileClosed, Locked, DoCode results) SHOULD carry the (epoch, sequence) of the code they belong to. DSF matches by identity, not by `BufferedCodes[0]`. RRF keeps the sequence of the code that opened a stack level next to the existing "identity of the command that invoked this macro frame" (`RRF/GCodes/GCodeMachineState.h:250-256`). Later milestone, only if replies by position keep producing findings (D3). | P8 option (b) | CC-9–CC-12 (M99 family: `Processor.cs:650-666`, `GCodes.cpp:4460-4465`, `SbcInterface.cpp:1564-1573`, drains), CC-16–CC-20 (position match, `PendingReplies`, start code by position, Unbuffered M122/M409, `_waitingForPrintCompletion`), CC-22, CC-23, CC-24; NEW-2 becomes detectable | 224 B static (code sequence per GB 32 B, FIFO tag grows to 4 B: +192 B) plus 2 B per pushed level inside the heap-allocated `GCodeMachineState` (`GCodeBuffer.cpp:1061`), ≤ 300 B at full depth on all channels (`MaxStackDepth` 10, `Config/Configuration.h:243`) | medium: DSF `Processor` matching (`:1102-1168`, `:1264-1305`, `:1373-1389`, `:650-724`); RRF `GCodeBuffer` push/pop carries one more field |

### 4.5 Flow control

| ID | Layer | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|---|
| V8-15 | S | RRF MUST advertise an absolute, monotonic credit limit for code bytes: released + capacity, where "released" counts every code byte RRF received and no longer holds (taken by a GB, purged, or dropped on arrival). 16 bits, RFC 1982. RRF MUST NOT refuse a code within the limit. X and C packets and all RRF→DSF traffic are exempt. Draft 3: a stop or a RESET does not reset the credit; only a session change does (V8-24). DSF MUST NOT zero its view of the limit on an emergency stop, as v7 does (`DCS/Link/LinkInterface.cs:613`), because RRF need not report again: after an EmergencyStop packet it reports only if its ring changes (V7-14, RRF Q2). Codes after a stop then go out and get RRF's "stopped" result (V7-16), an order v7 has whenever the ring was not empty. | P4; RFC 9000 §4.1, §4.2; AMQP 1.0 Part 2 §2.6.7; RFC 9113 §5.2.1; RFC 4254 §5.4 | CC-30 (`BytesReserved`, `LinkService.cs:452`, `:747-753`; `LinkInterface.cs:37`), the cascade refusal (`codeBufferAvailable`, `SbcInterface.cpp:394-414`), NEW-3; G-11 (K12) | 4 B | none |
| V8-16 | S | The advertised capacity MUST be storable after fragmentation. Today RRF reports the largest contiguous block (`:1394-1399`) and defragments only when 384 bytes or less are free (`:2436-2446`). How the guarantee is met is analysed in M0 (Q4). | P4 pitfalls | — | 0 | none |
| V8-17 | S | Per-channel limits and a reserve for time-critical channels MUST exist, so codes buffered ahead cannot take the room of a trigger macro. **Draft 4 (D4 = a):** RRF advertises one absolute limit (V8-15); DSF splits it per channel and keeps the reserve. | P4; RFC 9000 §4.1; USB 2.0 §5.5.4 | CC-28 (#133, `LinkInterface.cs:40-44`, `:570-583`; `Processor.cs:1076-1079`), CC-29 (share 82d581d1, `LinkInterface.cs:52`) | 0 | none |
| V8-18 | S | A reply MUST NOT be dropped silently. If the reply FIFO is full (`OutputMemory.cpp:487-506`), RRF SHOULD record the loss per channel and report it with the next message of that channel. Draft 3: recording the loss MUST NOT add a wait: a producer that finds the FIFO full does not block, as in v7 (V7-28); a blocking producer would hold its GB mutex while it waits for the SBC task, a wait v7 does not have (PR9). | P8 pitfalls | — (NEW-2 has no catch code) | 2 B | none: nobody waits |

### 4.6 Expedited class and stop

| ID | Layer | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|---|
| V8-19 | S | EmergencyStop and Reset are class X. DSF MUST write them first in a transfer (as `LinkService.cs:324-354` does). RRF MUST execute them on read without a mutex (as `SbcInterface.cpp:336-344` does). They are never refused, never credit-limited, never part of a stream, and idempotent through a stop number. Getting them through a disturbed data phase is v2 R10. **Draft 4 (D5):** the expedited flag lives in the v8 transfer header (V8-40), laid out in M1 and used in M4. Draft 3: the X class covers both kinds of emergency stop. DSF-requested: the EmergencyStop packet stops the hardware in the SBC task, and the main task's next spin aborts every GB, which sends AbortFile(all) to DSF (V7-12). RRF-initiated: `DoEmergencyStop` (`RRF/GCodes/GCodes.cpp:1065-1070`) also runs `GCodes::Reset`, which clears those abort flags again (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:145`), so DSF hears nothing (V7-17); V8-38 reports it. | P5; CAN 2.0 Part A §2; USB 2.0 §8.4.6.4; RFC 9260 §6.6 | CC-40 in part (halt and reset ordering in `Execute`) | 1 B | none |
| V8-20 | S | RRF MUST answer EmergencyStop with STOP_ACK(stop number), written after `reprap.EmergencyStop()` returned. DSF completes M112 on STOP_ACK instead of one transfer later. | P6/P11 (explicit fence) | CC-37 (#88, `LinkService.cs:315-317`, `:454-456`, `:599`); OF-P3; G-2 on USB once ported (K6, D6) | 1 B | none |
| V8-21 | S | v8 sessions MUST NOT use `ResendPacket` or `resendPacketId`. The link repeats transfers (v2), S has only V8-6, RRF keeps no copy. | P9; MQTT 5.0 §4.4; RFC 8260 §1.1 | with V8-5 and V8-6: CC-25–CC-27 | 0 | none |
| V8-22 | — | **Draft 4: moved to the logic list (LA-14).** An M112 read from a macro keeps its flush; that is logic, not link. | — | — | 0 | — |
| V8-38 | S | **Draft 3. RRF reports its own emergency stops.** When RRF stops without a request from DSF (`DoEmergencyStop`: trigger 0, PanelDue's M112 bytes, an M112 code that RRF executes, a trigger action with M112; `RRF/GCodes/GCodes.cpp:490-499`, `:990-999`, `:1065-1070`; `GCodes2.cpp:1953-1955`), it MUST send an X-class STOPPED(stop number) first in its next transfer. In v7 DSF gets nothing and keeps its macro levels and codes while RRF has popped all of its own and runs the codes left in its ring as "stopped" (V7-17, X-5). **Draft 4 (D9):** the protocol part is the report; DSF's reaction is logic (LA-7). | P5, P6 | X-5 (K20) with LA-7 | 1 B | small: one call in RRF `GCodes::DoEmergencyStop` sets the flag of the S layer |

### 4.7 Link, session, transport, firmware

| ID | Layer | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|---|
| V8-40 | L | **Draft 4. The v2 link features are part of v8 from the first transfer (D14).** The v8 transfer header MUST be the extended header of v2 (sequence number with counter and status, Walsh response codes; v2 "Implementation status", capabilities), and PhaseGuard and SequenceGuard MUST be on from the first transfer of a v8 session. There is no activation: ActivateLinkCapabilities (0x4D50) and the query of `limits.sbcLinkCapabilities` (`DCS/Link/LinkService.cs:1539-1575`) are not used in a v8 session. The v8 header also carries the expedited flag (D5) and the session id (V8-45). On SAME70 RRF MUST have the third 8 KB transfer buffer that SequenceGuard needs. Today it takes it from the PBUF pool when there is room and otherwise does not offer SequenceGuard (`RRF/SBC/DataTransfer.cpp:475-479`); what a v8 RRF does then is open (Q18). This closes Q8 and, for v8, the switch-window findings OF-S1 and OF-S5. | v2 C1 (capabilities), P12 | CC-36 (activation via the object model) | 0 (the buffer is allocated today, section 6) | none |
| V8-41 | L | **Draft 4. Own protocol version, fallback to v7 for updates only (D1, D2, D6).** The v8 transfer header MUST carry v8's own protocol version, a number from a fork range that is not 8 (not chosen yet), until Duet3D adopts v8 and assigns one. On SAME70 over SPI DSF MUST offer only v8 in normal operation. A stock or older RRF answers a v8 header with BadProtocolVersion and sends 7 in its own (`RRF/SBC/DataTransfer.cpp:1115-1118`, `SbcMessageFormats.h:24`); DSF MUST then use v7 only in `UpdateOnly` mode (`DCS -u`, `DCS/Program.cs:58`, `DCS/Settings.cs:42-46`), reusing its downgrade path (`DCS/Link/Adapter/SPI.cs:1883-1893`, log `:1967-1970`), and otherwise report the incompatible firmware without starting normal operation. A v8 RRF MUST accept v7 only for the requests of a firmware update and refuse the others. DSF's update uses object model queries (`DCS/Model/UpdateService.cs:303`, `:340-347`), M997 with B as a code for an expansion board (`DCS/Utility/FirmwareUpdater.cs:170-180`; RRF reads the firmware file like every file in SBC mode, through its file requests, V7-71; not traced further), M997 for the main board through IAP (`:228-234`, V7-18) and M999 as Reset (`:248-254`). How a v8 RRF tells a v7 DSF in the first exchange and how it refuses in v7 is open (Q17). USB and boards other than SAME70 keep v7 for normal operation until they are ported (D6). Consequence: wherever v8 runs, the fork's v7 catch code of both sides is not needed; it stays as long as any transport runs v7 for normal operation. | D1, D2 (section 9) | the v7 catch code on the v8 path (section 7) | 0 | none |
| V8-42 | S | **Draft 4. Integrity per packet (v2 R9).** Every v8 packet MUST carry a second check, independent of the transfer CRC32 (`RRF/SBC/DataTransfer.cpp:1187`, `:1389`), computed by the sender when it creates the packet and checked by the receiver before it uses the packet. Target: at most 10⁻⁷ undetected corrupted transfers per hour at the worst error rate the spec allows (v2 R9; CRC32 alone gives about 10⁻⁵ per hour when 1 % of transfers are hit at 1,700 per second, v2 decision log 2026-10-08). Because it is end to end per packet, it also protects USB once v8 runs there (D6). The algorithm, its size and what the receiver does with a packet that fails it are open (Q20). | v2 R9 | — | 0 (section 6) | none |
| V8-23 | S | No rule may depend on transfer numbers, pipeline depth or which transfer carried a packet. SPI and USB MUST behave the same; **draft 4:** for USB once it is ported (D6). Draft 3: the one exception is the read-ahead drop after a stack request (V8-37), which the catalogue keeps (CC-21, Q5). It rests on the depth of one that both links have (V7-1). | P6; RFC 9000 §12.3 (packet numbers apart from stream identity) | A7, C3 (NEW-1, OF-T13) | 0 | none |
| V8-24 | S | Epochs and sequences MUST start at 0 at every session start and MUST NOT be reset otherwise, except by RESET. A session change discards all channel state on both sides. **Draft 4:** a session change is a new session id of either side or SessionExpired (V8-45); a resync within the session lifetime is not one and keeps all channel state. Draft 3: epochs restart at 0, so they cannot filter what is left of the old session; therefore: (a) RRF MUST clear at a session change every per-GB flag that makes it send a packet: `macroFileClosed`, `messagePromptPending`, `messageAcknowledged` (after it acknowledged the open boxes itself, `RRF/SBC/SbcInterface.cpp:1772`), `invalidated`, `invalidationPending`, `codeReplySurplus`, an unsent `sendToSbc`, `reportPause` (V7-96, X-12; `:1717-1780` vs `RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:128-150`). In v7 a stale MacroFileClosed pops DSF's base level after a reconnect, and DCS stops (K5). (b) DSF MUST NOT write a D, G or C packet of the old session into the new one; the SPI adapter re-exchanges its pending TX after a reconnect (`DCS/Link/Adapter/SPI.cs:442-477`), and RRF processes it if its number follows (V7-91, G-3, K7). An X packet whose STOP_ACK has not come MUST be sent again. (c) Partial replies and messages are discarded (V8-10). | P10; RFC 9260 §1.3 | X-12 (K5), G-3 (K7) | 0 | small: RRF clears the flags where it ends the session; DSF's SPI adapter drops the pending TX on a session change |
| V8-35 | S | **Draft 3. Unknown and invalid packets.** (a) **Draft 4:** nothing of v8 is sent in a v7 session, and a v8 session has v8 from its first transfer (V8-40, V8-41). A v7 RRF never reads a v8 packet type, because it refuses the v8 header (`RRF/SBC/DataTransfer.cpp:1115-1118`). What an unknown type does in v7: a v7 RRF ends its read loop at a request number ≥ 32 other than 0x4D50 (`InvalidRequest` = 32) and drops the rest of the transfer, codes included, without a refusal (`RRF/SBC/SbcInterface.cpp:326-330`, `SbcMessageFormats.h:346`); the deprecated numbers below 32 are skipped and acknowledged (`SbcInterface.cpp:674-679`); a v7 DSF ignores unknown types silently (`DCS/Link/LinkService.cs:611-701`). (b) In a v8 session a packet that the receiver cannot use (unknown type, invalid channel, invalid length such as a Code over 384 bytes, `SbcInterface.cpp:350-375`) MUST be rejected for good and counted (V8-27, V8-44): RRF marks it rejected in the channel state report (V8-8), DSF completes the code with an error. It is never a stall, never the end of the read loop, never a silent drop. v7 refuses an over-long code on every copy (V7-20) and drops one with an invalid channel silently (V7-6). | P10, P12 | G-16, X-18 (K4); G-17, X-19 (K24) | 2 B (reject bitmap) | none |
| V8-37 | S | **Draft 3. The read-ahead drop after a stack request stays (B7).** After RRF wrote ExecuteMacro, AbortFile or WaitForAcknowledgement for channel c, it MUST drop the Code packets of c that DSF wrote before it processed that request, and DSF MUST suspend those codes and send them again after the pop, before newer ones, exactly as v7 does (V7-22: `RRF/SBC/SbcInterface.cpp:376-380`, `:1495-1504`; `DCS/Link/Channel/Processor.cs:158-189`, `:991-1016`). "Before it processed" is the v7 rule "in the next exchange" (V8-23); **draft 4:** B7 stays as it is (Q5). The dropped codes count as received for the sequence (V8-5) and as released for the credit (V8-15); DSF's copies after the pop are new packets with new sequence numbers. | — (PR9, V8-28) | — (CC-21 stays) | 0 | none |
| V8-25 | L, S | The firmware part MUST use static tables only: no heap allocation in the protocol path, no per-message buffer, no reorder buffer, no new transfer buffer beyond the one of V8-40. Total new static RAM MUST stay below 1 KB, checked with the map file. | PR8; pattern survey §6 | — | — | — |
| V8-26 | S | The RRF S layer SHOULD be a separate source file without GCodes dependency, so the host harness can compile it unchanged. Today the harness replaces `ExchangeData` and executes no packet (`Tools/sbc-harness/README.md:95`). | — | — | 0 | none |
| V8-27 | S | M122 on both sides SHOULD count per channel: stale-epoch drops, stalls, go-back-N resends, RESETs, credit waits, rejected packets. Counted, not alarmed (v2 R7). The link counters are V8-46. | v2 R7 | — | ≤ 64 B (u16 × 2 × 16) | none |

### 4.8 Logical compatibility and deadlock freedom (PR9)

| ID | Layer | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|---|
| V8-28 | S | A v8 session MUST be a refinement of v7 for the logic of both sides: every order in which a logic layer sees its events (codes and their replies per channel, macro requests, starts and closals, message boxes, locks, flushes, evaluations, messages, aborts, stops) MUST also be possible in v7 with the catch code of DSF 7f26d0ca and RRF ddec2d001. v8 MAY remove orders, namely those that the catch code removes today (stale replies, requests and closals across an abort, codes overtaking a refused code of their channel). It MUST NOT add any. | PR9; RFC 9113, Abstract | — | 0 | none: this requirement is what keeps it so |
| V8-29 | S | Across channels, packets MUST be handed to the logic in the order DSF wrote them (RRF: one transfer at a time, packets in order, `RRF/SBC/SbcInterface.cpp:310-1359`; DSF: `DCS/Link/LinkService.cs:434-451`). A packet may be passed only where v7 already lets it be: a stalled packet (V8-6, in v7 a refused one, `:1355-1358`) lets later packets of other channels pass. A stall MUST NOT let a later packet of its own channel pass (v7 does, NEW-3; removing that order is allowed by V8-28). Draft 3, per direction, no more order than v7 has (V7-82): DSF→RRF: RRF hands packets to its logic in the order DSF wrote them (V7-2); DSF writes channel data round robin as in v7 (`DCS/Link/Channel/Manager.cs:72-99`) and keeps causal order (V8-34); it MUST NOT wait to put channels into an order. RRF→DSF: DSF hands packets to its logic in the order RRF wrote them (V7-2); RRF keeps the v7 composition (read-phase answers, the reply FIFO in production order, the file request, per-channel requests in channel-number order, PrintPaused last; V7-4), with the channel state report in place of ResendPacket and CodeBufferUpdate; it MUST NOT hold a request back to order it by the time it was raised, and MUST NOT add a drain that v7 does not have (V7-32). "Own channel" in the stall rule means own stream (V8-6): a stall in D-req(c) lets D-code(c) pass, as v7 lets a later packet pass a refused one. **Draft 4, write order across streams:** packets of one direction MUST be delivered in the order they were written, across all streams; only the later packets of a stalled stream wait, and packets of other streams pass it. This keeps SetPrintFileInfo before the M23 or M32 that follows it and SetLastCodeResult before the next code (V8-34) without a special case, as long as the earlier packet's stream is not stalled. A stalled SetLastCodeResult is passed by the next code as in v7 (G-7). A SetPrintFileInfo behind a stalled packet of the global stream would be passed by the M23, which v7 cannot do: open (Q16). | PR2, PR9 | NEW-3 | 0 | none |
| V8-30 | S | The spec MUST contain the wait-for graph of every blocking wait on both sides, v7 waits and the new ones of v8 (RESET_ACK, credit, stall, STOP_ACK), each with file:line and its bound, and MUST show that the graph has no cycle that is not broken by a bound. Draft 3: the v7 graph is [sbc-link-v7-contract.md](sbc-link-v7-contract.md) section 15: edges E-1…E-53 of both sides and cycles C-1…C-8. v8 changes it so: E-26…E-28 no longer wait for the link thread (V8-34), which removes C-1 and C-2; E-16 becomes the wait for RESET_ACK with the same bound (V8-9); E-14 becomes the credit wait (V8-15); the resend polling of V7-11 becomes the stall (V8-6), in its own stream so that E-37 cannot wait behind it; E-15 and E-50 become the pending Lock (V8-36). New edges: E-v1 M112 → STOP_ACK, bounded by the 4.5 s of E-21 (V8-20); E-v2 a DSF channel after RRF's abort request → RESET_ACK (V8-12), bounded as C-4. **Draft 4:** C-3 is a logic finding (K3, LA-15) outside v8 (D11). C-4…C-8 stay as in v7, bounded. | PR9 | — | 0 | none |
| V8-31 | S | X and C packets MUST be handled without waiting: not for D or G traffic, not for a logic mutex, not for the RRF main task. Work that needs a logic mutex (V8-9 "apply") is deferred as a state, not waited for, and the answer (RESET_ACK) is sent once it is done. D and G packets keep their v7 hand-over (`SbcYieldTimeout`, then a stall instead of a resend). The S layer of DSF runs in the packet loop of `LinkService` and MUST NOT await a channel lock there that the logic can hold while it waits for the link. Draft 3: more generally, the DSF packet loop MUST NOT take, synchronously or not, a lock whose holder can wait for the packet loop. In v7 the job lock is such a lock: M23, M32, M37 and the job loop hold it while they wait for SetPrintFileInfo to be written (E-27, E-28), and the loop takes it for PrintPaused and AbortFile on File/File2 (E-7): cycles C-1, C-2. V8-34 removes those waits, so the job lock may stay where it is. The same holds for every lock of V7-83; a new wait for the packet loop under one of them violates this rule. | PR9; RFC 9000 §4.1 (control frames are not flow-controlled) | C-1, C-2 (with V8-34) | 0 | none |
| V8-32 | S | Every new wait MUST be bounded. A wait that v7 breaks with a timeout MUST NOT become unbounded in v8. Draft 3 replaces the rest of draft 2, which ended the link session when RESET_ACK did not come or a stall or credit wait made no progress within a bound: that ends a session where v7 goes on, e.g. a full code buffer during a long move or M505 waiting for standstill, which is a new order (PR9). Waits that replace a v7 wait keep the v7 bound: RESET_ACK = E-16 (none while RRF refuses; RRF refuses only while its main task holds the GB mutex, and RRF's own 3 s waits bound that, V7-40, V7-71), credit = E-14 (none), a stall = the resend polling of V7-11 (none), the pending Lock = E-15, E-50 (none). These are back-pressure; their liveness is the session lifetime (V8-45), and v8 MUST NOT end the session for them. The only new wait is STOP_ACK (V8-20), bounded by the 4.5 s of M112 (E-21); what DSF does after it is logic (LA-11). | PR9; v2 R6, R8 | — | 0 | none |
| V8-33 | S | Before M1, the wait-for graph of V8-30 and the list of cross-channel dependencies of the logic (Q7) MUST be complete and reviewed. Any rule of this spec that would add an order or a wait cycle blocks the milestone until it is changed. Draft 3: the graph is in the v7 contract. **Draft 4 (D11 revised):** C-3 (K3), K25 and K26 are logic findings outside v8 (LA-15 to LA-17) and no precondition for M1. K1 and K2 (C-1, C-2) are removed by V8-34; tests that show their reachability are written in M0. Q7 is answered (section 10). | PR9 | — | 0 | none |
| V8-34 | S | **Draft 3. A request without an answer completes on acceptance.** A DSF packet that the logic awaits as written and that has no answer (E-26: SetPrintFileInfo, PrintStopped, SetLastCodeResult, DeleteLocalVariable, Unlock) MUST be complete for the logic once the S layer has accepted it into its stream (V8-5), not when it is written into a transfer. Accepting MUST NOT wait for the packet loop; the content is captured at acceptance (v7 reads the print file info when it writes, `DCS/Link/LinkService.cs:405`). S MUST write a packet no later than every packet that the logic hands over after the request completed (causal order across streams), so RRF still reads SetPrintFileInfo before the M23 or M32 that follows it (V7-65) and SetLastCodeResult before the next code (V7-62). **Draft 4:** RRF's side of that order is the write order across streams of V8-29, with its condition (Q16). A packet that a session change loses after acceptance is lost as a written packet can be in v7 (V7-91), and the logic is invalidated as today. This removes the waits of the logic on the link thread under the job lock (E-27, E-28) and with them C-1 and C-2. | P3, P11; V8-5 | C-1, C-2 (K1, K2) | 0 | none: `LinkInterface` completes these requests on acceptance (`DCS/Link/LinkInterface.cs:397-406` and the other request methods); `JobProcessor` and `MCodeHandler` stay as they are |

### 4.9 Alignment with the v7 contract

Every item of [sbc-link-v7-contract.md](sbc-link-v7-contract.md) gets one status:
- **identical**: v8 keeps the v7 behaviour that the logic sees;
- **changed**: v8 changes it; the difference and the reason are given, and the last column says whether the change
  only removes orders the logic could see in v7 (PR9, V8-28);
- **removed**: v8 drops the mechanism; the reason is given;
- **gap**: draft 2 did not cover the item, or covered it in a way that would add an order, a wait or a cycle; draft 3
  closes it with the new or amended requirement named in the third column.

Counts (draft 4): 45 identical, 33 changed, 3 removed, 20 gap. Draft 4 turns seven identical items into changed ones:
V7-89, V7-90, V7-92, V7-95 and V7-97 through PR10, V7-98 through D1 and D2, V7-69 through V8-48. Every change,
including the gap fixes, only removes orders, with one condition: V7-65 holds only once Q16 is solved. Two changes add a
wait in a case where v7 has none, each bounded and known from v7: V7-13 (STOP_ACK instead of the wait for the transfer,
with the same 4.5 s bound) and V7-46 (RESET_ACK after RRF's own abort, the wait v7 has after DSF's abort, bounded as
C-4). V8-34 removes three waits (E-26…E-28). PR10 changes no order the logic sees: a gap within the session lifetime
looks to the logic like a slow peer, which v7 has up to its timeouts.

| V7 | Status | v8 requirement(s) | Difference | Reason | Only removes orders (PR9) |
|---|---|---|---|---|---|
| V7-1 pipeline depth one | changed | V8-23 | S and the logic no longer depend on the depth; RRF still composes one transfer behind | catch code of A-2 (CC-2, CC-25, CC-30) | yes: events in the old abort windows are dropped by epoch, not by position |
| V7-2 processing in the writer's order | identical | V8-4, V8-29 | — | — | — |
| V7-3 DSF's TX order | gap | V8-34, V8-29 (amended), V8-19 | X first, C next, then D and G in the order the logic handed them over, delivered in write order across streams (draft 4), instead of the fixed kind order of `LinkService.Execute` | V8-34 needs causal order to keep SetPrintFileInfo before the M23 without a wait | yes: every hand-over order is one v7 produces when the two packets go into different transfers; only the kind order inside one transfer, for packets the logic did not order, is gone |
| V7-4 RRF's TX order | identical | V8-29 (amended), V8-8 | the channel state report takes the place of ResendPacket and CodeBufferUpdate; the logic does not see it | — | — |
| V7-5 pacing | identical | — | — | RRF-internal; v2 R4 asks for the same throughput | — |
| V7-6 unknown and invalid packets | gap | V8-35 (amended) | nothing of v8 in a v7 session; in a v8 session an unusable packet is rejected and counted | a new v8 type sent to a v7 RRF would end its read loop and drop the rest of the transfer (X-18) | yes: removes the silent loss |
| V7-7 packet ids, resend lookup | removed | V8-5, V8-21, V8-44 | — | CC-25, CC-27 (NEW-1); the `ArgumentException` that stops DCS goes with it (PR10) | yes |
| V7-8 refusal | changed | V8-6, V8-7, V8-21 | a stall at sequence n of one stream, go-back-N in that stream; a refusal means nothing was done; RRF still keeps no copy | PR6, PR8 | yes |
| V7-9 SPI resend guard | removed | V8-6 | — | CC-26 | yes |
| V7-10 a refused packet is overtaken | gap | V8-6 (amended), V8-29 (amended), V8-48 | a stalled packet is passed by other channels and by the other stream of its channel, not by its own stream; draft 4 states the rule behind the two streams | draft 2 stalled the whole channel, so an answer to RRF's macro wait could wait behind a refused mutex request until RRF's 3 s timeout, a cycle v7 does not have | yes: removes own-stream overtaking (NEW-3, K27), keeps the passing that RRF's waits need |
| V7-11 why RRF refuses | gap | V8-6 (amended), V8-9, V8-36 | mutex refusals stall D-req(c) only; InvalidateChannel is never refused; Lock becomes a pending request; the 10 ms hand-over stays (V8-31) | as V7-10 | yes |
| V7-12 EmergencyStop | identical | V8-19 | a stop number makes it idempotent | — | — |
| V7-13 halt completion | changed | V8-20 | M112 completes on STOP_ACK, written after `reprap.EmergencyStop()` returned; on USB, once ported (D6), a dropped stop cannot complete | OF-P3, G-2 (K6) | yes; the STOP_ACK wait replaces the v7 wait for the transfer and keeps its 4.5 s bound (E-21) |
| V7-14 code buffer after a stop | gap | V8-15 (amended) | DSF keeps its credit view after a stop | v7 zeroes it and RRF need not report again (G-11, K12) | yes: codes after a stop get RRF's "stopped" result, which v7 also produces whenever the ring was not empty |
| V7-15 Reset | identical | V8-19 | — | — | — |
| V7-16 M112 is DSF's | identical | LA-14 (former V8-22) | — | the flush is G-code semantics (wiki: M81 before M112); logic, so it is in the logic list | — |
| V7-17 RRF-local stops | gap | V8-38 (new), V8-19 (amended) | RRF reports its own stops; DSF's reaction is logic (LA-7) | Tim: both kinds of stop in the X class; X-5 (K20) | yes: "codes cancelled after a halt" is a v7 order (an M112 from DWC can meet an RRF-local stop); decision D9 |
| V7-18 firmware update | identical | — (class I, v2 C2) | — | v7 stays the update path, also against a v8 RRF (D2, V8-41) | — |
| V7-19 when DSF writes a Code | changed | V8-9, V8-15 | the block by an unconfirmed abort becomes the wait for RESET_ACK; buffer space becomes credit | CC-6 | yes: same blocking, explicit end |
| V7-20 code admission | gap | V8-15, V8-35 (new) | a code within the credit is never refused and there is no cascade; an invalid length is rejected for good instead of refused on every copy | CC-30; G-17, X-19 (K24): draft 2 had no answer for a code that can never fit | yes: "code ends with an error" is a v7 outcome |
| V7-21 flow control by estimate | changed | V8-15, V8-16, V8-17 | absolute credit instead of estimate, spare room and share; DSF splits the one limit (D4) | CC-28, CC-29, CC-30 | yes |
| V7-22 read-ahead drop (B7) | gap | V8-37 (new), V8-23 (amended) | — (kept) | V8-23 of draft 2 forbade the rule it rests on, with no replacement; Q5: B7 stays as it is | yes: v7 behaviour kept, no order added |
| V7-23 RRF holds codes while a stack event is unsent | identical | — | — | RRF logic | — |
| V7-24 per-channel FIFO | identical | V8-4 | — | — | — |
| V7-25 comment codes get no reply | identical | — | — | — | — |
| V7-26 one final reply per code | changed | V8-10, V8-18; V8-14 (M5) | replies of an old epoch are dropped, a lost reply is reported; position matching stays until M5 | CC-7, NEW-2 | yes |
| V7-27 reply routing in DSF | identical | V8-2 | the header channel equals the one destination bit RRF sets | — | — |
| V7-28 one reply FIFO | changed | V8-10, V8-18 (amended) | each entry carries its code's epoch; a full FIFO records the loss; nobody waits | NEW-2 (OF S7); draft 2 left "who waits" open | yes |
| V7-29 suppressed replies | changed | V8-10 (amended); V8-14 (M5) | `codeReplySurplus` becomes the epoch tag, taken when a code reply enters the reply queue; the M99 suppression stays until M5; the writers of code replies outside `HandleReply` are audited in M0 (Q6) | CC-7 | yes |
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
| V7-40 macro wait timeout | gap | V8-39 (new) | late answers are dropped by request number; RRF may request RESET(c); that it does so after an expired macro wait is logic (LA-8) | X-9, X-25 (K23); draft 2 had request numbers only in the table of section 5 | yes: "channel aborted after a macro request" is a v7 order (an abort can come at any time); decision D10 |
| V7-41 MacroCompleted | changed | V8-10; V8-14 (M5) | one of an old epoch is dropped; X-7 stays until M5 | a MacroCompleted written before an abort belongs to a level that no longer exists; the epoch identifies it instead of its position (A-2 family, contract V7-41) | yes |
| V7-42 MacroFileClosed | changed | V8-10, V8-24 (amended) | the epoch replaces #158 and RRF #45; no closal of the old session arrives | CC-3, CC-4; K5 | yes |
| V7-43 DSF's macro reader | identical | — | — | DSF logic | — |
| V7-44 M98 | identical | — | — | — | — |
| V7-45 single AbortFile, M99 | identical | V8-14 (M5) | — | CC-9…CC-12 stay until M5 | — |
| V7-46 AbortFile all | changed | V8-12 | RRF's abort of all files asks for RESET(c, e+1); DSF's channel then waits for RESET_ACK | G-20 (K18): late replies get a stale epoch and are dropped | yes; the RESET_ACK wait is E-16 of a DSF abort, bounded as C-4, and RRF runs codes of c only after the abort, as in v7 |
| V7-47 stack depth | identical | — | — | RRF logic; its overflow uses V7-46 | — |
| V7-48 WaitForAcknowledgement | identical | V8-14 (M5) | G-14 stays (K15) | — | — |
| V7-49 MessageAcknowledged | changed | V8-10 | an acknowledgement of an old epoch is dropped (a box at the base level after an abort, OF T10) | in v7 such an acknowledgement pops whatever level is current after the abort (X-11, OF T10); the epoch ties it to the level it was meant for | yes |
| V7-50 cancel with abort | identical | — | — | — | — |
| V7-51 M291 blocks only lower levels | identical | — | — | logic | — |
| V7-52 DSF side of an abort | changed | V8-9 | InvalidateChannel becomes RESET(c, e+1) after the same DSF work; G-21 stays (K19) | CC-1 | yes |
| V7-53 RRF side of InvalidateChannel | changed | V8-9, V8-11, V8-10 (amended) | receipt and apply are split; requests and prompts of the older epoch are resolved locally; DoCode stays untouched as in v7 | CC-5, CC-8 | yes: a prompt resolved as cancelled is what a user can do in v7 |
| V7-54 refused InvalidateChannel | changed | V8-9 | never refused | CC-6, CC-8 (DF-T11) | yes |
| V7-55 abort window 1 | removed | V8-10 | — | CC-2 (#134) | yes |
| V7-56 abort window 2 | changed | V8-9 | the window becomes the wait for RESET_ACK | CC-6 (#161) | yes: same wait (E-16), explicit end |
| V7-57 Lock | gap | V8-36 (new), V8-6, V8-7 (amended) | Lock is accepted and pending; RRF tries again every exchange; RESET cancels it and releases its lock; DSF's Unlock for a Locked without request is logic (LA-9) | draft 2 made it a stall although a refused Lock keeps the lock it took (X-6); G-13 (K14) | yes: other channels still cannot add moves to the locked system; the Locked wait (E-15) is the v7 wait |
| V7-58 Unlock | identical | — | section 5 corrected: Unlock frees every resource of the GB (X-24) | — | — |
| V7-59 EvaluateExpression | changed | V8-13, V8-6 | the result echoes the request's sequence; stream D-req | CC-34 | yes |
| V7-60 SetVariable, DeleteLocalVariable | gap | V8-13, V8-7 (amended), V8-6 | echo; no assignment before a refusal; stream D-req, which keeps a stale-variable deletion before the creation that follows it | X-6 (K21): draft 2's V8-7 missed SetVariable; K27 (draft 4) | yes: removes "already exists" on a copy and after an overtaken deletion |
| V7-61 variables only after a flush | identical | — | the stale-variable deletion of M260 and M261 with V without a flush stays; it is a logic finding (K25, LA-16) | — | — |
| V7-62 SetLastCodeResult | changed | V8-34, V8-6 | completes on acceptance; stream D-req, so a refused copy is still passed by the next code (G-7 stays); the missing flush is a logic finding (K26, LA-17) | E-26 | yes |
| V7-63 object model | changed | V8-13 | a query id instead of one query per transfer and the key check | CC-32, CC-33, G-18 (K16) | yes |
| V7-64 logic waits for the OM | identical | — | — | — | — |
| V7-65 SetPrintFileInfo | gap | V8-34 (new), V8-31 (amended), V8-29 (amended) | completes on acceptance, delivered before the M23 by the write order across streams | C-1, C-2 (K1, K2): draft 2 kept the wait under the job lock | yes once Q16 is solved: RRF sees the same order unless the global stream stalls before SetPrintFileInfo; the cycle is gone |
| V7-66 ObjectModelKeyChanged | identical | — | OF-S3 stays a separate fix | — | — |
| V7-67 print end | gap | V8-34, V8-6 (amended) | completes on acceptance; PrintStopped is in D-req(File), not in the global stream; that DSF resets File and File2 before an abort is logic (LA-12) | in the global stream a PrintStopped refused for the File mutex would hold back the file result that the holder of that mutex waits for (E-53, E-38) until RRF's 3 s timeout | yes |
| V7-68 PrintPaused | identical | — (D7, LA-13) | — | — | — |
| V7-69 Message to RRF | changed | V8-6, V8-48 | every chunk of a message goes into one stream chosen by destination; a stalled final chunk keeps later messages of that stream behind it | split by mutex need, the Push chunks of a DoCode result would travel apart from the final chunk, which alone needs the mutex (`RRF/SBC/SbcInterface.cpp:774-797`) | yes: removes later messages overtaking a refused final chunk (V7-10) |
| V7-70 DoCode | gap | V8-39 (new), V8-10 (amended) | a request number; DoCode is exempt from the epoch filter; DSF's error result for a cancelled DoCode is logic (LA-10) | G-12 (K13); draft 2's epoch filter would drop a DoCode or its result across a RESET, which v7 lets through | yes: an error result is a v7 outcome |
| V7-71 RRF's file I/O | changed | V8-13, V8-5 | requests and results carry an id; a late result is dropped; results are not lost | CC-35, X-8 (K22), G-6 (K9) | yes |
| V7-72 stack mirror by position | identical | V8-14 (M5) | — | — | — |
| V7-73 pop on the base level is fatal | identical | V8-10, V8-24 (amended) | stays; its stale sources (K5, T12) are filtered | — | — |
| V7-74 … V7-79 level routing, start code, pop effects, flush, prioritized codes, job read-ahead | identical | — | — | DSF and RRF logic, untouched by v8 | — |
| V7-80 M606 S1 | identical | LA-15 | C-3 is a logic finding (K3) outside v8 (D11) | — | — |
| V7-81 triggers | identical | — | — | RRF logic | — |
| V7-82 no cross-channel order | gap | V8-29 (amended) | — (kept): packets are delivered in write order and DSF writes channels round robin | draft 2 stated one direction and "the order DSF wrote them"; RRF→DSF has production order for replies and channel-number order for requests; requiring more would add waits | yes: v7 behaviour kept, no order added |
| V7-83 the link thread takes logic locks | gap | V8-31 (amended), V8-34 | the packet loop takes no lock whose holder waits for it | C-1, C-2 (K1, K2) | yes |
| V7-84 completion never blocks the link thread | identical | — | — | — | — |
| V7-85 DSF waits without timeout | gap | V8-32 (amended), V8-45 | — (kept); the only new wait is STOP_ACK; the liveness of the waits is the session lifetime | draft 2 ended the session when a credit or stall wait made no progress, where v7 goes on | yes: v7 behaviour kept; STOP_ACK is the one new wait (V7-13) |
| V7-86 … V7-88 RRF task waits | identical | V8-31 | — | the 10 ms hand-over and RRF's waits stay | — |
| V7-89 link-level bounds | changed | V8-43, V8-44, V8-45 | no retry count: the link retries and resyncs until the session lifetime runs out; RRF's 4 s timeout raises LinkDown instead of ending the connection; DCS no longer stops after 60 s without a transfer, a livelock leads to a resync | PR10: these bounds end a print on a link fault (section 4.10, F-1, F-4, F-5) | yes: a gap within the lifetime looks to the logic like a slow peer, which v7 has up to its timeouts; the logic's own waits still expire (LA-5) |
| V7-90 fatal errors in DSF's packet loop | changed | V8-44, V8-21 | DCS never stops on link data: an unusable packet or header is rejected and counted; the `ArgumentException` of V7-7 goes with V8-21 | PR10 (F-6, F-7) | yes: removes the DCS stop |
| V7-91 SPI reconnect | gap | V8-24 (amended), V8-45 | a resync within the lifetime keeps the session and exchanges the pending transfer again, which is then correct; after a new session id no packet of the old session is written, except a stop without STOP_ACK | G-3 (K7); PR10 | yes |
| V7-92 DSF's SPI reset detection | changed | V8-45 | only a new session id of the peer or SessionExpired ends the session; a timeout, a sequence gap or a resync does not | PR10; the v2 status does not tell a restart from a connection reset | yes: removes the invalidation after a short gap |
| V7-93 USB failure | changed | V8-20, V8-23 | once USB is ported (D6): a dropped stop cannot complete M112; above L, USB behaves as SPI. Until then v7, with NEW-1 fixed in M0 | G-2 (K6) | yes |
| V7-94 DSF invalidation | identical | V8-24 | what `Invalidate` does stays; that it runs only after SessionExpired or PeerRestarted is logic (LA-2, LA-3); the zeroing of the code buffer is V7-14 | — | — |
| V7-95 RRF on a link loss | changed | V8-45, V8-46 | the protocol reports LinkDown and keeps the session; the stop and `InvalidateResources` become the logic's reaction to SessionExpired and PeerRestarted (LA-1, LA-3), no longer to every timeout; the fork's stop on a link loss (CC-41) moves there | PR10 (F-1); v2 R6, R8 | yes: removes the stop on a short gap; the stop after the lifetime is v2 R8 |
| V7-96 stale RRF flags across a reconnect | gap | V8-24 (amended) | RRF clears them at a session change | X-12 (K5): draft 2 said "as today" | yes |
| V7-97 what RRF assumes after a reconnect | changed | V8-45 | after a resync within the lifetime nothing is gone; after PeerRestarted or SessionExpired as in v7 | PR10 | yes |
| V7-98 protocol versions | changed | V8-41 | v8 has its own protocol version from a fork range; on SAME70 over SPI DSF offers only v8 and falls back to v7 only in `UpdateOnly`; a v8 RRF accepts v7 only for a firmware update; USB and other boards keep v7 (D6) | D1, D2: the catch code goes only where v8 is required | yes (link only) |
| V7-99 protocol < 3 | identical | V8-41 | only on the v7 path | — | — |
| V7-100 `UpdateOnly` | identical | V8-41 | — | on SAME70 over SPI the only use of v7 against a stock or older RRF (D2) | — |
| V7-101 link capabilities | changed | V8-40 | no activation in a v8 session: the extended header, PhaseGuard and SequenceGuard are on from the first transfer; the v7 path keeps the handshake | D14: closes Q8 and the switch-window findings (OF-S1, OF-S5); draft 3's capability `SessionLayer` is superseded (D1) | yes (link only) |

### 4.10 Link faults never end a print (PR10)

**Draft 4.** Today a link fault ends or disturbs a print on both sides. Each row was re-read for this document.

| # | Path | Side | Effect | Where |
|---|---|---|---|---|
| F-1 | No transfer for 4 s while connected, 500 ms within a transfer, or a connection reset | RRF | "Lost connection to SBC"; emergency stop (fork RRF #21); every GB's files aborted, `printAborted` set, heaters off; RRF stays halted until M999 | `RRF/SBC/SbcInterface.cpp:171-176`, `:194-254` (stop `:229-230`); `InvalidateResources` `:1717-1780` (files `:1770`, `printAborted` `:1776`, heaters `:1779`); `RRF/SBC/SbcMessageFormats.h:43`, `:45` |
| F-2 | RRF's own waits for DSF: macro 3 s, file requests 3 + 3 s | RRF | The request fails ("Timeout while waiting for macro file …"), a macro goes on as "not found" (V7-40) | `SbcMessageFormats.h:42`; `RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1266-1273`; `SbcInterface.cpp:2292-2338` |
| F-3 | A reconnect after a timeout, a sequence gap, or RRF leaving the extended link | DSF | Counted as a reset: `Invalidate` cancels every code and macro and aborts the job | `DCS/Link/Adapter/SPI.cs:383-390`, `:485-495`, `:1984-1991`; `DCS/Link/LinkService.cs:388-396`, `:1605-1622` (job `:1504-1508`) |
| F-4 | More than 10 retries of a transfer or of a header | DSF | "Lost connection", then F-3 | `DCS/Settings.cs:193`; `SPI.cs:336-339`, `:1835`, `:442-477` |
| F-5 | No complete transfer for 60 s, e.g. a livelock | DSF | DCS stops; for RRF the new DCS is a restart (F-1) | `Settings.cs:188`; `SPI.cs:326-331` |
| F-6 | A header with an invalid format code, a newer protocol version or a too long data length | DSF | DCS stops (the exception ends the link thread, V7-90) | `SPI.cs:1955-1957`, `:1960-1965`, `:1972-1977` |
| F-7 | A resend request for a packet id DSF cannot find; a packet DSF cannot parse | DSF | DCS stops | `SPI.cs:855`; `LinkService.cs:446-450` |
| F-8 | A request number ≥ 32 other than 0x4D50 | RRF | The rest of the transfer, codes included, is neither processed nor refused | `SbcInterface.cpp:326-330` |
| F-9 | A connection reset of RRF | RRF | Its pending TX is dropped and it leaves the extended link | `RRF/SBC/DataTransfer.cpp:1420-1432` |

RRF in stand-alone mode also stops DCS (`SPI.cs:1951-1953`); that is a configuration, not a link fault. F-2 is the
logic's own timeout, not the link's (LA-5).

| ID | Layer | Requirement | Pattern | Removes | RAM | Logic |
|---|---|---|---|---|---|---|
| V8-43 | L | **Time-based retries and resync.** The link MUST repair a failed transfer by retries that are bounded by time, not by a count, and MUST resync (phase and sequence) after an interruption, for as long as the session lives (V8-45). Each side MUST keep its pending transfer across a resync and deliver every transfer once (v2 R2), also after an interruption of up to the session lifetime. A timing value only changes how fast the link retries and when LinkDown is raised (v2 R5). Today a count ends the connection (F-4), RRF resets it after 4 s (F-1) and drops its pending TX (F-9). | v2 R1, R2, R5 | the count of `MaxSbcRetries` (F-4) | 0 | none |
| V8-44 | L, S | **No exit on link data.** Data that the receiver cannot use, such as a header field out of range, an unknown packet type, an invalid length or channel, or an acknowledgement it cannot match, MUST be rejected and counted (V8-35, V8-46): at the link the transfer is answered as bad and repeated, at the session layer the packet is rejected. It MUST NOT raise an exception that ends the link thread or DCS (F-6, F-7) and MUST NOT end RRF's read loop (F-8). A livelock, where both sides answer each other out of step, MUST lead to a resync, not to a DCS restart (F-5). Out of scope: a DCS crash or restart counts as an intended stop (for RRF a PeerRestarted); an RRF reset is out of scope as in v2. | P12; v2 R1 | the DCS stop after 60 s (F-5) | 0 | none |
| V8-45 | S | **Session lifetime and resume.** A session is identified by a session id of each side, carried in the v8 header, that changes only when that side restarts (RRF boots, DCS starts). An interruption while both sides stay alive MUST resume the same session, with its codes, stacks, epochs, sequences and pending requests, if valid exchanges return within the session lifetime. The lifetime is the v2 R8 limit: 30 s by default, configurable from 5 to 120 s. A new session id of the peer ends the session (PeerRestarted); reaching the lifetime ends it (SessionExpired). Within the lifetime the link and session layers stay in state: they do not pause, hold, abort or stop anything. Today the v2 status is 0x7FF or 0 and does not tell a restart from a connection reset (`DCS/Link/Protocol/Shared/ExtendedLink.cs:47-48`, `:62-65`), and both sides end the session on every timeout (F-1, F-3). | P10; v2 R6, R8 | CC-43 in part (the invalidation after a timeout; after a restart it stays as LA-3) | 12 B | none |
| V8-46 | S | **Events to the logic.** The link and session layers MUST report to the logic of both sides: LinkDown (no valid exchange for longer than the retry window of V8-43), LinkRestored (valid exchanges again, same session ids), SessionExpired (no valid exchange for the session lifetime) and PeerRestarted (a new session id of the peer). Each event is reported once per change and none of them is a decision; what the logic does is in the logic list (LA-1 to LA-6). Each side MUST also offer a link health state, ok, degraded (repairs above a set rate, v2 R7) or down, with counters of retries, resyncs, rejected data and failed checks, which the logic can query for DWC and M122. | P12; v2 R7, R11 | — | ≤ 18 B | none |
| V8-47 | L, S | **No motion or safety function (v2 SB1).** The link and session layers MUST NOT pause, hold or stop motion, switch heaters or outputs, or act on the machine in any other way; an event is all they produce. Delivering a stop that the logic requested (X class, V8-19) is transport, not a function of the link. v2 R13 stage 1 (hold motion after 100 ms without an answer) is therefore not a v8 requirement; it moves to the logic and safety work (LA-4), because motion and safety are not link (Tim, 2026-11-09). R13 stage 2 is the session lifetime (V8-45). | v2 SB1 | — (CC-41, the fork's stop on a link loss, moves to LA-1 and LA-3) | 0 | none |

## 5. Message classes and ordering

| Class | Meaning | Rules |
|---|---|---|
| X expedited | Stop and reset, STOP_ACK, and RRF's STOPPED (V8-38) | First in a transfer, executed on read, never refused, never in a stream, idempotent |
| C session control | RESET, RESET_ACK, channel state (ACK, NAK, reject, credit), RESET request | Never refused, never credit-limited, MUST be able to pass D and G (deadlock avoidance) |
| D(c) channel data | Everything bound to channel c, in two streams: D-req(c) for the requests RRF may refuse while the GB mutex is busy and for every chunk of a message for c, D-code(c) for the rest (V8-6, V8-48) | In sequence order per (stream, direction, epoch); credit for codes; NAK per stream |
| G global | Object model, print info, key changes, file operations, messages not for exactly one channel | Global stream with its own sequence; answers matched by id; can stall on a resource refusal (Q16) |
| I IAP | `WriteIap`, `StartIap` | Outside the session; v7 unchanged (v2 C2) |

**May-pass table** (same direction; row = later packet, column = earlier packet). "Must" means a design that
blocks it is wrong, as with PCIe posted writes. The PCIe table itself was only seen in secondary sources.

| Later \ earlier | X | C | D same channel | D other channel | G |
|---|---|---|---|---|---|
| X | no | must | must | must | must |
| C | no | no (same channel), may (other) | must | must | must |
| D | no | no (same channel), may (other) | no (same stream); may (other stream, V8-6) | may | may |
| G | no | may | may | may | no; file operations stay one at a time |

"may" means: allowed where v7 allows it too (V8-28, V8-29), i.e. when the earlier packet is stalled or waits for
credit. Otherwise packets keep the order in which they were written, across channels and streams as well (V8-29,
draft 4); within one transfer they are processed in order, as today. **Barriers** across channels that v8 keeps:
- X: a stop or reset ends every channel.
- Lock (10) locks all movement systems; RRF says "currently this is used only by M505"
  (`RRF/SBC/SbcInterface.cpp:606`). Unlock (11) frees every resource the GB owns except those of the previous level,
  not only movement (`RRF/GCodes/GCodes.cpp:5648-5668`; draft 3 correction, X-24). The barrier is in the logic
  (standstill), not in the link.
- PrintPaused and PrintStopped act on File and File2 together (catalogue A10, `SbcInterface.cpp:506-542`, `:1495-1504`).
- **Draft 4 (Q7 answered):** beyond these the logic relies on no order across channels. Across streams it relies on
  SetPrintFileInfo before the M23 or M32 that follows it and on SetLastCodeResult before the next code, both kept by
  the write order of V8-29 (with Q16).

**v7 messages in v8** (numbers from `RRF/SBC/SbcMessageFormats.h:269-349`):

| Dir | v7 request | v8 class | Identity in v8 | v7 pairing it replaces |
|---|---|---|---|---|
| → RRF | 0 EmergencyStop, 1 Reset | X | stop number | completion one transfer later (#88) |
| → RRF | 2 Code | D-code | (c, e, seq); credit | position of `BufferedCodes` |
| → RRF | 3 GetObjectModel | G | request id | queue head + one per transfer + key check |
| → RRF | 5 SetPrintFileInfo, 30 ObjectModelKeyChanged | G | seq | none (S3 framing bug stays a separate fix, OF-S3) |
| → RRF | 6 PrintStopped | D-req(File), barrier File/File2 (draft 3, V8-6) | seq | final empty reply (`Processor.cs:1129-1134`) |
| → RRF | 7 MacroCompleted, 18 MacroStarted | D-code | (c, e, seq); request number (V8-39) | "the next code belongs to the macro" (B8) |
| → RRF | 10 Lock, 11 Unlock | Lock D-code, a pending request (V8-36); Unlock D-req; logic barrier | (c, e, seq) | resend as polling |
| → RRF | 16 EvaluateExpression, 20 SetVariable, 21 DeleteLocalVariable, 29 SetLastCodeResult | D-req | (c, e, seq), echoed | (channel, expression) or (channel, name) |
| → RRF | 17 Message | D-req(c) if channel c is its only destination, else G; all chunks of a message in one stream (V8-48) | request number if it answers a DoCode (V8-39); not filtered by epoch then (V8-10) | next targeted message finishes the GB (B9) |
| → RRF | 19 InvalidateChannel | C: RESET(c, e+1) | new epoch | none (no answer) |
| → RRF | 22–28, 31 file results | G | request id | `fileOperation` state, late replies skipped |
| → RRF | 0x4D50 ActivateLinkCapabilities | not sent in a v8 session (D14, V8-40) | — | unchanged on the v7 path |
| → RRF | 12, 13 IAP | I | — | unchanged |
| → RRF | 15 FileChunk (deprecated, CAN expansion updates, `SbcMessageFormats.h:328`) | G | request id | synchronous answer |
| → DSF | 0 ResendPacket, 2 CodeBufferUpdate | C: channel state | absolute values | packet id of a TX two transfers back; free space minus `BytesReserved` |
| → DSF | 1 ObjectModel | G | request id | order |
| → DSF | 3 Message (code reply) | D | code epoch (V8-10), code seq (V8-14) | first channel bit, `BufferedCodes[0]`, PushFlag joining |
| → DSF | 3 Message (generic, targeted, log) | G | none (notification); never dropped (V8-10) | — |
| → DSF | 4 ExecuteMacro, 13 WaitForAcknowledgement | D | request number; code seq if from a code | start code = `BufferedCodes[0]` |
| → DSF | 5 AbortFile | single: D; all: C (RESET request) | code seq of the M99 / new epoch | M99 at `[0]`, drains, dummy reply |
| → DSF | 7 PrintPaused | G, barrier File/File2 | file positions (already identities) | unchanged in M1–M4 (D7, LA-13) |
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
  the pool as 16 × 1552 = 24,832 bytes (`Tools/sbc-harness/README.md:111`), which leaves 256 bytes. **Draft 4:** the
  third buffer, which SequenceGuard needs, is mandatory for v8 on SAME70 (V8-40). RRF allocates it today when the pool
  has room (`DataTransfer.cpp:475-479`), so it is no new RAM; v8 needs no further buffer there.

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
| Draft 4: session ids of both sides (2 × 2 B, an illustration, Q19), time of the last valid exchange (4 B), lifetime setting (4 B) | 12 | V8-45 |
| Draft 4: link state and pending events (2 B), link counters (≤ 8 × u16) | ≤ 18 | V8-46 |
| Draft 4: per-packet check: a field inside each packet, in the existing transfer buffers; a lookup table, if one is used, is `constexpr` in flash as RRF's CRC32 tables are (`RRF/Storage/CRC32.cpp:19`, `:70`, `:106`, `:142`) | 0 | V8-42 |
| Draft 4: third 8 KB transfer buffer (allocated today) | 0 | V8-40 |
| **M1–M4 total, with V8-40 to V8-47** | **≈ 341** (draft 3: 311) | 0.36 % of 95,868 |
| V8-17 per-channel limits in RRF: not needed, D4 = a | (+64) | V8-17 |
| V8-14: code sequence per GB, FIFO tag 1 → 4 B | +224 | V8-14 |
| V8-14: 2 B per pushed level in existing heap objects, worst case | ≤ +300 | V8-14 |
| **Everything, worst case** | **≈ 865** (draft 3: 900 with D4 b) | 0.90 % |
| Removed: `codeReplySurplus`, `invalidationPending` (2 B per GB, `GCodeBuffer.h:405`, `:422`) | −30 | V8-9, V8-10 |

Field sizes and wrap-around (RFC 1982 §3.2: values in flight must stay below 2^(n−1)):

| Field | Bits | Bound on values in flight |
|---|---|---|
| Sequence per stream | 16 | Credit bounds codes: the ring holds at most 170 codes (4096 / 24, smallest code 4 + 20 bytes, `SbcMessageFormats.h:368-385`), far below 2^15 |
| Epoch per channel | 8 | One RESET in flight per channel (V8-9); 2 bits would do, 8 leave margin |
| Credit counter | 16 | Capacity 4096 < 2^15 |
| Request number, stop number | 8 | RRF waits for each blocking request (`SbcMaxRequestTime` 3 s, `SbcMessageFormats.h:42`) |
| Session id (draft 4) | 16 (illustration) | Changes only on a restart of its side; form and size open (Q19) |

Not estimated: flash, CPU, the bytes per packet on the wire for the per-packet check (v2 R4), SBC task stack (1400
words, `SbcInterface.cpp:39-43`; the RRF stack analyzer in `Tools/` can check it), padding inside
`GCodeMachineState`. Whether static growth reduces "never used" byte for byte is not checked.

## 7. Migration and compatibility

**Version (draft 4, V8-40, V8-41).**
- v8 has its own protocol version from a fork range, required for normal operation on SAME70 over SPI (D1, D2). There
  is no capability handshake for it. Draft 3's capability bit `SessionLayer` = 8 is superseded (D1, D14); the v2
  capabilities `PhaseGuard` 1, `SequenceGuard` 2 and `ExtendedHeader` 4 (`RRF/SBC/SbcMessageFormats.h:139-144`,
  `DCS/Link/Protocol/Shared/ExtendedLink.cs:13-33`) are always on in v8 and keep their handshake on the v7 path.
- DSF falls back to v7 only in `UpdateOnly`; a v8 RRF accepts v7 only for a firmware update (V8-41, Q17).
- USB and boards other than SAME70 keep v7 until they are ported (D6). Today RRF offers the v2 capabilities only on
  SAME70 (`RRF/SBC/DataTransfer.cpp:475-479`) and DSF's USB adapter has none (`DCS/Link/Adapter/USB.cs:808`, `:815`).

**Coexistence.**

| DSF | RRF | Result |
|---|---|---|
| fork with v8 | fork with v8, SAME70 over SPI | v8 session |
| fork with v8 | stock, or older fork | firmware update only, over v7 in `UpdateOnly`; no normal operation (D2) |
| stock | fork with v8, SAME70 over SPI | firmware update only, over v7; the v8 RRF refuses the other v7 requests (D2, Q17) |
| stock | stock | v7 as upstream |
| any | over USB, or a board other than SAME70 | v7 as today until ported (D6) |

**Removal plan.** Catch code can be deleted from the v8 path once the gate passes. Where v8 runs, v7 carries only
firmware updates (D2), so the fork's v7 catch code of both sides is not needed there. It stays as long as any transport
runs v7 for normal operation (D6: USB, boards other than SAME70).

| Milestone | Content | Catch code no longer needed (catalogue `CC-n`) | Gate before deletion |
|---|---|---|---|
| M0 | Preparation: RRF S layer as its own file (V8-26), harness extension, fake firmware v8 mode. **Draft 4:** NEW-1 fixed on USB v7 as a normal fork fix with its own test (Q1, D6). Harness analysis of Q2–Q4 before the credit design of M3 is fixed. Fake-firmware tests of Q9, Q14 (reachability of C-1, C-2 and C-3, K1–K3) and Q15. Audit of every writer of code replies (Q6). The wait-for graph and the cross-channel dependencies reviewed (V8-30, Q7, V8-33) | none | existing suites green; the M0 tests written; V8-33 reviewed |
| M1 | V8-1–V8-4, V8-9–V8-12, V8-23, V8-24, V8-35, V8-37, V8-40, V8-41; the expedited flag laid out in the header (D5) | CC-1–CC-8 (A0, #134, #158, RRF #45, RRF #43, #161, RRF #46 both parts). 7 of them fork-only. CC-36 (activation, fork-only, D14) | the TriggerFuzz abort tests and `TriggerMacros.TriggerChannelWorksAfterInvalidationDuringTriggerRequest` (DF-T1, T2, T10, T11, T12) green in v8 mode with the catch code removed; lab `AbortsCrossingMacroRequests` with `LAB_ABORT_ITERATIONS=2000`: no block, no stack underrun, no "none was requested", **no out-of-order reply**; draft 4: a fork DSF with a stock RRF runs only the firmware update, and the update works |
| M2 | V8-5–V8-8, V8-21, V8-34, V8-36, V8-48 | CC-25, CC-26, CC-27, CC-31 | NAK of every D request type at every position with other channels interleaved (fake firmware `FailBufferLocks`); harness replay of the S layer with 0 differences; the same tests over the USB adapter once ported. Draft 3: an M98 whose macro answer follows a refused DeleteLocalVariable starts without a timeout; PrintPaused and AbortFile(File) sent while M23 waits for SetPrintFileInfo (C-1) do not block. Draft 4: the chunks of a DoCode result stay together across a refused final chunk; a `var` after a refused stale-variable deletion succeeds (K27) |
| M3 | V8-15–V8-18 | CC-28, CC-29, CC-30 | `TriggerMacros.TriggerMacroGetsSpareCodeBufferRoom`, `LinkInterfaceRequests.CodesBufferedAheadLeaveSpareRoom`; lab `CodeBufferReserve.TriggerMacroGetsRoomWhileOtherChannelsReadAhead` (DF-T3, LT §14, 2026-11-08) |
| M4 | V8-13, V8-19, V8-20, V8-38, V8-39; the expedited flag used (D5) | CC-32, CC-33, CC-34, CC-35, CC-37, CC-40 | `StopPath`, `StopPathFuzz`, `M112RepliesOnlyOnceRrfIsStopped` (OF-P3) pass; object model tests across a DCS restart (LT §9, "Failed to find query") |
| M5 (D3) | V8-14, only if replies by position keep producing findings | CC-9–CC-12, CC-16–CC-20, CC-22–CC-24 (all upstream content) | `ChannelProcessor` (`M99WithoutReply`, `M99ReplyBeforeAbortRequest`, `M99ReadAheadAfterM99`, `ReplyForStartCodeBeforeMacroFileClosed`, `PrintCompletionReply…`) rewritten for identity; a lab sequence of 3 × M122 + M291 S2 (B4) |
| not placed | V8-42–V8-47 (integrity, PR10); milestone open (Q19) | the count of F-4 and the DCS stop of F-5 (not catalogue items) | AC9–AC13 |

V8-22 is no longer in M4; it moved to the logic list (LA-14). Not removed by v8: CC-13 (`IsTriggerBusy`, upstream
content), CC-14/CC-15 (pause, D7), CC-21 (B7 read-ahead invalidation, Q5), CC-38 (priority codes hop channels, logic),
CC-39 (RRF #25, safety boundary v2 SB1), CC-41 (stop on a lost connection: it moves to the logic list as the reaction
to SessionExpired and PeerRestarted, LA-1, LA-3), CC-42 (RRF-internal), CC-43 (invalidate on reset; the part after a
timeout goes with V8-45, the part after a restart stays as LA-3), CC-44–CC-46 (the v2 link, now part of v8, V8-40),
CC-47 (RRF-internal mutex). That is 34 of 47 items removable on the v8 path (draft 3: 33), among them 10 of the 11
fork-only items of the session layer and the fork-only activation CC-36 of the link layer.

## 8. Verification

- **Unit tests** in fork-only files under `src/UnitTests` (CLAUDE.md rule 7):
  - The fake firmware (`src/UnitTests/FakeFirmware/FakeFirmware.cs`) gets a v8 mode that implements the RRF side of
    this spec. Every requirement gets a test.
  - Every existing test of a deleted catch code keeps running in v8 mode. Those tests are the regression set.
  - Draft 4: the M0 tests of Q9, Q14 and Q15 in v7 mode first; fault injection of PR10 (interruptions up to and beyond
    the session lifetime, restarts of either side, malformed data at every field) in v8 mode.
- **RRF host harness** (`firmware/RepRapFirmware/Tools/sbc-harness`): today it compiles `DataTransfer.cpp` and
  `TaskLoop` unchanged and replaces `ExchangeData`. With V8-26 it also compiles the S layer. A
  `PortMatchesRealFirmware`-style replay (`src/UnitTests/SPI/LinkRecovery.cs`) compares the fake firmware's S layer
  with the real one; pass = 0 differences. Draft 4: the analysis of Q2–Q4 in M0.
- **Fuzzing** (`src/UnitTests/Fuzz`): a session fuzzer adds random NAKs, RESETs, credit stalls, interruptions and RRF
  restarts across channels to `TriggerFuzz` and `StopPathFuzz`. Invariants: every code completes exactly once; no reply
  reaches another code; no channel stays blocked; no DCS stop.
- **Lab** (`src/LabPrinterTests`, procedure LT): `AbortsCrossingMacroRequests` (2000 aborts), the T3 trigger check,
  M112 under load, a full print. Plus v2 R4: codes/s and transfers/s within 2 % of v7 on a clean link. Draft 4: the
  PR10 gates AC9–AC12 during a print, with ESD as in the v2 R3 test.

| Acceptance | Pass when |
|---|---|
| AC1 | 2000 aborts: 0 blocks, 0 "Out-of-order reply", 0 "Resending packet", stale drops counted only in M122 |
| AC2 | No code is completed by a message of another code in any fuzz run (identity check in the fake firmware) |
| AC3 | M112 completes only after STOP_ACK in every StopPath case |
| AC4 | No refusal of a credited code in any run |
| AC5 | New static RRF RAM ≤ 1 KB by map file; no new heap allocation in the S layer (code review) |
| AC6 | Draft 4: the combinations of section 7 behave as stated: a v8 session with fork DSF and RRF; only the firmware update, and a working one, with a stock or older RRF and with a stock DSF; v7 as today over USB and on boards other than SAME70 |
| AC7 | No deadlock: in every fuzz and lab run each code ends (reply, cancel or error) within the bound of V8-32; a watchdog in the fake firmware host flags any channel that has work but makes no progress |
| AC8 | Refinement (V8-28): the same scripted input in v7 mode and v8 mode gives per channel the same sequence of logic events, apart from the events that the v7 catch code drops |
| AC9 | Draft 4: fault injection (every single fault, bursts, interruptions of every line shorter than the lifetime) and ESD during a print: no job abort and no emergency stop caused by a link fault within the session lifetime; LinkDown and LinkRestored logged on both sides |
| AC10 | Draft 4: DCS never exits on injected malformed data (header fields, unknown types, invalid lengths and channels, acknowledgements it cannot match); each is rejected and counted |
| AC11 | Draft 4: a DCS freeze (`kill -STOP`, then `kill -CONT`) shorter than the lifetime: the session resumes, the job continues, and LinkDown and LinkRestored are logged on both sides |
| AC12 | Draft 4: a freeze longer than the lifetime raises SessionExpired on both sides |
| AC13 | Draft 4: integrity (V8-42): analysis plus a fault-injection estimate at the worst allowed error rate gives ≤ 10⁻⁷ undetected corrupted transfers per hour, as the v2 R9 test asks |

## 9. Decisions (Tim, 2026-11-09)

All decisions were made by Tim in an interview on 2026-11-09. The options of draft 3 are kept where they existed.
"Basis" marks a reason that is not Tim's own words.

| # | Question | Decision | Reason | Applied in |
|---|---|---|---|---|
| D1 | How v8 relates to the 2026-10-08 decision "negotiation on top of v7" (v2 C1) | (b) its own protocol version, with a number from a fork range: not 8, so that it cannot collide with a future upstream version 8, the same idea as 0x4D50 for ActivateLinkCapabilities. It moves to upstream's number if Duet3D adopts v8. This reverses the 2026-10-08 decision for normal operation; draft 3 had recommended (c), negotiation now | The catch code can only be removed where v8 runs, so v8 has to run everywhere it can; negotiated on top of v7, every mixed pair would keep v7 and its catch code. Mixed stock and fork pairs remain possible for firmware updates | section 1, V8-41, section 7, v2 decision log |
| D2 | What happens to the v7 path and its catch code | (c) v8 is required for normal operation, except `UpdateOnly`. DSF speaks only v8; a stock RRF answers BadProtocolVersion, and DSF then uses v7 only in `UpdateOnly`. A v8 RRF accepts v7 only for firmware-update requests and refuses the rest. Exception: D6 | Consequence: the fork's v7 catch code of both sides can be removed wherever v8 runs | V8-41, section 7 |
| D3 | How replies find their code | (a) the epoch tag first (V8-10, M1); (b) code identity (V8-14, M5) only if replies by position keep producing findings | Basis: (b) is the only part that changes logic code noticeably | V8-14, section 7 M5 |
| D4 | Where per-channel credit and the reserve live (V8-17) | (a) RRF advertises one absolute limit; DSF splits it per channel and keeps the reserve | Basis: DSF is the only sender and knows the job streams (`LinkInterface.cs:52`); the exact absolute limit removes the guesswork | V8-17, section 6 |
| D5 | Where the expedited flag lives (v2 R10) | In the v8 header, which is the extended header (D14); laid out in M1, used in M4 | Basis: with the v2 link inside v8 there is no separate v2 header to put it in | V8-19, V8-40, section 7 |
| D6 | USB and boards other than SAME70 | v8 first on SAME70 over SPI. USB and other boards keep v7 for normal operation until they are ported, an explicit exception to D2. NEW-1 (USB resend, OF-S6) is fixed for USB v7 in M0 | Basis: the v2 link features exist only on SAME70 over SPI (`RRF/SBC/DataTransfer.cpp:475-479`, `DCS/Link/Adapter/USB.cs:808`); the lab printer has no USB setup (OF-T13). Consequence: the catch code of the v7 path stays as long as any transport runs v7 for normal operation | V8-23, V8-41, section 7 |
| D7 | Pause and print stop (A10) | (a) keep the v7 mechanism until M4, then decide with data; it is logic (LA-13) | Basis: the pause position logic is not analysed | LA-13 |
| D8 | When to show this to Christian Hammacher | (b) after M1 has passed the lab. v8 is built now, without waiting for upstream | Not given; draft 3 had recommended (a) | section 1 |
| D9 | How DSF learns of an emergency stop that RRF made by itself (V7-17) | Protocol: RRF reports it with an X-class STOPPED (V8-38). Logic: DSF reacts as after its own M112, with `InvalidateCodes` (LA-7) | Both kinds of stop in the X class (Tim); only this leaves the two stacks equal (V7-72) | V8-38, LA-7 |
| D10 | Late answers after RRF's macro wait expired (V7-40) | Protocol: late answers are dropped by request number; RRF may request RESET(c) (V8-39, V8-12). Logic: RRF requests RESET(c) when a macro wait expires after the request went out (LA-8) | Only this leaves the stacks equal. Behaviour change: the channel's whole stack is aborted instead of the caller going on after "not found"; only when DSF answers later than 3 s | V8-39, LA-8 |
| D11 | When to fix the v7 cycles C-1 to C-3 and the defects of the logic (K1 to K3, K25, K26) | Revised by the layering rule: K3 (C-3), the corrected K25 and K26 are logic findings outside v8 (LA-15 to LA-17), and V8-33 no longer needs them fixed before M1. K1 and K2 (C-1, C-2) are removed by V8-34 (protocol); tests that show their reachability are written in M0 | Logic defects are not protocol requirements (D12) | V8-33, LA-15–LA-17, section 7 M0 |
| D12 | Layering | This document holds only link and session requirements, each tagged with its layer; the protocol defines events and guarantees; every logic reaction and every logic defect is in the logic list. Link faults never end a print (PR10) | Tim: "We must not mix levels: motion is not link, and safety is neither link nor protocol. If the link is down it must signal event-based to the logic; the logic decides." and "no special cases in the link — if it is not defined, it does not exist" | section 3, section 4.10, logic list |
| D13 | Reporting v7 bugs to Duet3D | The safety-relevant K findings (K1–K3, K6, K12) go to Christian Hammacher once a test proves each and shows that it is not fork code; the rest with the M1 presentation (D8) | Basis: CLAUDE.md "No guessing"; a report must rest on a test | section 10 |
| D14 | The v2 link features in v8 | Part of v8 from the first transfer: the v8 header is the extended header, PhaseGuard and SequenceGuard are always on, no activation with 0x4D50. The third 8 KB transfer buffer is mandatory for v8 on SAME70 | Closes Q8 and, for v8, the switch-window findings OF-S1 and OF-S5 | V8-40, section 6, section 7 |

## 10. Open questions and what is unverified

| # | Question | Status |
|---|---|---|
| Q1 | NEW-1: RRF composes its USB answer one transfer behind (`DataTransfer.cpp:1302-1357`, `:1362-1371`), so `USB.cs:717-721` looks into the wrong buffer, may copy the wrong packet, or throws `ArgumentException`, which `LinkService.cs:446` does not catch | Decided: fixed for USB v7 in M0 (D6). Code reading, not tested. OF-T13 relied on the `USB.cs` comment and probably does not exist |
| Q2 | NEW-2: can the 64-entry reply FIFO fill in practice (stalled link, M122 chunks)? | Decided: harness analysis in M0, before the credit design of M3 is fixed |
| Q3 | NEW-3: can RRF refuse a code that DSF's estimate allowed? | Decided: harness analysis in M0, before the credit design of M3 is fixed |
| Q4 | Is logical credit always physically storable, given that `DefragmentBufferedCodes` runs only when 384 bytes or less are free and moves one gap per run (`SbcInterface.cpp:2436-2525`)? | Decided: harness analysis in M0, before the credit design of M3 is fixed |
| Q5 | B7: how RRF tells in-flight read-ahead codes from new ones after a push without counting ExchangeData | Decided: B7 stays as it is (V8-37). B7 is upstream design and has no known failure |
| Q6 | Does every message of a code pass one capture point for its tag? OF-T10 lists writers outside `HandleReply` (M122 `GCodes2.cpp:2292`, `GpInPort.cpp:90`, `GCodes4.cpp:1894`) | Decided: only code replies (`BinaryCodeReplyFlag`) are filtered by epoch; the tag is taken when a reply enters the reply queue, in link code only, and M0 audits every writer. Generic, targeted and log messages are never dropped; a filtered reply's text still goes to the log (V8-10) |
| Q7 | Cross-channel dependencies in the logic beyond Lock, pause and stop | Answered by an inventory: the only reliance across channels and streams that the logic has is SetPrintFileInfo (global stream) before the M23 or M32 that follows it (`DCS/Files/JobProcessor.cs:356`, `DCS/Link/LinkService.cs:403-410`; RRF applies it on read without an answer, `RRF/SBC/SbcInterface.cpp:497-503`), plus, within one channel, SetLastCodeResult before the next code (V7-62; the inventory of the interview named only the first, this document adds the second). None between data packets of two channels. None between another request and a code of the same channel, because RRF applies requests when it reads them (e.g. `SbcInterface.cpp:1131-1140`, `:1307-1314`) while codes are stored in the ring (`:401-433`) and run later; DSF orders the two only by a flush or by waiting for an answer. Both orders are kept by the write order of V8-29, the first one under Q16 |
| Q8 | The switch point of `SessionLayer` within a connection, given the switch-window findings of the extended header (OF-S1, OF-S5) | Closed: v8 has the v2 link from the first transfer and no switch (D14, V8-40) |
| Q9 | Is `AbortFile(true)` harmless when RRF already aborted all files itself (V8-12)? | Decided: a fake-firmware test in M0 |
| Q10 | Does a refused `LockAllMovementSystemsAndWaitForStandstill` have side effects (V8-7)? | Answered in draft 3: yes, it keeps the lock of the movement system it reached and releases only the lower ones (`RRF/GCodes/GCodes.cpp:1947-1962`, `:1969-1999`); V8-36 |
| Q11 | SetPrintFileInfo alignment: the 64-bit `lastModifiedTime` (`SbcMessageFormats.h:460`) after another packet in the same transfer (catalogue D4, `LinkService.cs:407`) | Decided: v8 starts every packet on an 8-byte boundary (V8-2); no v7 fix. On SAME70 over SPI v7 carries only updates, which send no SetPrintFileInfo. On the v7 paths of D6 no ActivateLinkCapabilities is written (`USB.cs:808`, `:815`; RRF offers no capability outside SAME70, `DataTransfer.cpp:433`, `:475-479`), so nothing precedes SetPrintFileInfo there (`LinkService.cs:399-410`) |
| Q12 | H1 ("Daemon: Out-of-order reply ''" after a DCS start) and H2 ("Failed to find suitable stack level for flush request") | Decided: watch them after M1. Causes not known |
| Q13 | Which DSF-internal codes complete without a flush while an earlier RRF code of their level runs, and so write a mutex request (SetLastCodeResult, DeleteLocalVariable) during RRF's macro wait? K25 and K26 are two cases | Answered: missing flushes are logic, out of the v8 scope (LA-16 to LA-18). V8-6 does not depend on the list |
| Q14 | Can C-1, C-2 and C-3 be reached (v7 contract section 15)? | Decided: fake-firmware tests in M0, sending PrintPaused or AbortFile(File) while M23 or M606 S1 holds the job lock |
| Q15 | Can a MacroCompleted cross an RRF-side pop and mark the next outer macro finished (X-7)? | Decided: a fake-firmware test in M0; V8-14 (M5) would remove it |
| Q16 | **Draft 4. Can the global stream stall before SetPrintFileInfo?** RRF refuses GetObjectModel and a Message not for exactly one channel when it has no OutputBuffer or TX room (`RRF/SBC/SbcInterface.cpp:451-471`, `:810-822`). With go-back-N in the global stream (V8-6), a SetPrintFileInfo written after such a packet waits, and the M23 in D-code(File) passes it (V8-29), which v7 cannot do: v7 writes SetPrintFileInfo first in its transfer (`DCS/Link/LinkService.cs:405-407`), RRF never refuses it (`SbcInterface.cpp:497-503`), and the M23 goes out only after it (E-27). This contradicts "the write order covers SetPrintFileInfo before the M23 without a special case". A fix in the sense of V8-6 would put the global packets that RRF may refuse for resources into a stream of their own | Open, for Tim |
| Q17 | **Draft 4. Version handshake with a v7 DSF.** A stock DSF in normal mode stops with "Invalid protocol version" when RRF's header carries a number above 7 (`DCS/Link/Adapter/SPI.cs:1960-1965`), and in `UpdateOnly` it adopts RRF's number into its own header (`:1883-1893`) while it keeps writing v7 packets. How a v8 RRF tells a v7 DSF from a v8 DSF in the first exchange, which number it presents, and how it refuses requests in a v7 session (v7 has no rejection: a refusal makes DSF resend, V7-8, and a dropped packet leaves DSF waiting, V7-85) are open. So is whether a normal-mode fork DSF should also accept M997 over v7 against a stock RRF, or only `DCS -u` | Open |
| Q18 | **Draft 4.** What a v8 RRF on SAME70 does when it cannot allocate the third transfer buffer (V8-40). Today it then offers no SequenceGuard (`RRF/SBC/DataTransfer.cpp:475-479`) | Open |
| Q19 | **Draft 4. PR10 details:** the milestone of V8-42 to V8-47 (their header fields would belong to the M1 layout, as the expedited flag does); the form and size of the session id; how both sides agree on the configured lifetime (5–120 s, v2 R8); the retry window after which LinkDown is raised | Open |
| Q20 | **Draft 4. Integrity details (V8-42):** the algorithm and size of the check, and what a receiver does with a packet that fails it: it cannot trust the header, and RRF keeps no copy (PR8) | Open |

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
- Draft 3: the v7 contract and its alignment (section 4.9) come from reading the code; none of its 27 findings K1–K27
  was tested. The requirements V8-34 to V8-39 and the stream split of V8-6 are designed on paper only.
- Draft 4: the decisions of section 9 are recorded from the interview of 2026-11-09. The new requirements V8-40 to
  V8-48 are designed on paper only. Every code reference added in draft 4 was re-read at the commits of section 0:
  the link exits and timeouts of section 4.10, the version check and downgrade, the buffer allocation, the message
  chunks and their mutex, PrintStopped, the reply queue and the message routing in DSF.

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

Repository: [sbc-link-v8-logic-adaptations.md](sbc-link-v8-logic-adaptations.md), [sbc-link-v7-contract.md](sbc-link-v7-contract.md),
[sbc-link-requirements.md](sbc-link-requirements.md), [sbc-link-current-state.md](sbc-link-current-state.md),
[sbc-link-findings-upstream.md](sbc-link-findings-upstream.md), [dsf-findings-upstream.md](dsf-findings-upstream.md),
[open-findings.md](open-findings.md), [rrf-local-trigger-actions.md](rrf-local-trigger-actions.md),
[lab-test-plan.md](lab-test-plan.md). Duet3D wiki: `User_manual/Reference/Gcodes.md` (M112), clone at a106ad4.
