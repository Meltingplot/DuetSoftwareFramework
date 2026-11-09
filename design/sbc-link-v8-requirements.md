# SBC link: channel epoch on v7 (requirements; replaces the v8 session layer)

Status: draft 8, 2026-11-09. Nothing here is implemented. No code was changed for it. The decisions of section 10 are
Tim's.
Draft 5 replaced the v8 session layer of drafts 1 to 4 by a minimal design: a **channel epoch on protocol version 7**,
plus **evidence gates** that decide later whether more is needed. Tim decided this on 2026-11-09 after three expert
reviews of draft 4 (section 11). Draft 6 records his decisions after a second review round of draft 5 (section 10.3):
an epoch change ends everything of the old epoch on both sides, locks and DoCode included (CE-25); one DSF epoch plus
an RRF gate bit instead of a second epoch (CE-24); a stop never goes into a SequenceGuard repeat (CE-16); a level id
only on evidence (EG-6). Draft 7 applies an independent review of draft 6 (section 11, decisions FD-24 to FD-33 in
section 10.4): the abort releases the locks only of an idle GB; InvalidateChannel acts once; only the current epoch
passes the filters (CE-5); the stop rule and the halt rule are a link milestone of their own, L1 (section 7.2).
Draft 8 applies a root-cause pass over draft 7 (section 11, decisions FD-34 to FD-37 in section 10.6). It found three
clusters of special cases, each with one cause and with a path in RRF that already works without them:
- RRF's SBC task only **records** a channel abort, and the main task **applies** it at the top of its spin (CE-7);
- between record and apply RRF writes no channel event of that GB (CE-12);
- the apply is the per-channel routine of a link loss, for every DSF, which also fixes K5 (CE-25);
- the epoch needs no capability and no activation: both sides always tag (CE-4, CE-5).

Two decisions of the same day are reversed: FD-31 (Q33 option (a)) by FD-34, and FD-2 (capability, activation with
0x4D50) by FD-37. Section 10.7 lists what draft 8 removed. The file keeps its path so that links stay valid.
Section 10.5 lists what happened to every requirement of drafts 1 to 4.
Audience: Tim, later Christian Hammacher (Duet3D). The reader knows DSF and RRF, not the working papers.

Scope: the channel epoch (tag, filters, the two companion fixes, the end of the old epoch, RRF's abort
gate), the stop path above the transfer retries, the rule that a halt is never lost, the evidence gates, and the list
of separate v7 fixes found on the way.
Not in scope: what the logic of DSF or RRF does on an event, and the defects of the logic. They are in
[sbc-link-v8-logic-adaptations.md](sbc-link-v8-logic-adaptations.md) (items `LA-n`). Also not in scope: safety
functions (v2 SB1), IAP flashing (v2 C2), G-code semantics. [sbc-link-requirements.md](sbc-link-requirements.md) (v2)
keeps the link requirements R1 to R14 and the decision "negotiation on top of v7" (v2 C1).

Goal (Tim, 2026-11-09):
> We keep finding edge cases that are tied to the strict ordering of the protocol. At the same time some commands
> must legitimately overtake (M112). I believe the problem comes from a wrong separation of protocol and channels.
> My goal is to remove our edge-case catch code from the codebase in the medium term. Follow best practices of
> mainstream high-volume protocols, and require as few changes to the logic code as possible.

Layering rule (Tim, 2026-11-09), unchanged since draft 4:
> We must not mix levels: motion is not link, and safety is neither link nor protocol. If the link is down it must
> signal event-based to the logic; the logic decides.

and "no special cases in the link — if it is not defined, it does not exist". Every requirement here is tagged L
(link) or S (session: the channel layer above the link, where the epoch lives).

Constraint (Tim: "the other end is an embedded system with RAM constraints"): RRF runs on a SAME70 with fixed RAM.
M122 on the lab printer on 2026-11-09 06:4x UTC (MB6HC, RRF 3.7.0+1.mp.9-dev.3, CHX configuration): "RAM: static
131040, dynamic 105276 (1744 recycled), never used 95868, free sys stack 172", heap 6144/5036/612.

## 0. Basis and conventions

- **Code:** DSF `v3.7-dev` at 7f26d0ca (`src/DuetControlServer` is unchanged since 808d56fa). RRF fork at 0dccb9efe,
  whose tree equals fork `3.7-dev` ddec2d001 (the DSF submodule pin). Every file:line in this draft was re-read at
  these commits for draft 5, 6, 7 or 8, including those that came from the review reports.
- **Paths:** `DCS/` = `src/DuetControlServer/`. `RRF/` = RRF `src/`. `pkg/` = `pkg/` of this repository.
- **PRs:** `#n` = Meltingplot/DuetSoftwareFramework, `RRF #n` = Meltingplot/RepRapFirmware.
- **IDs of this document:** `CE-n` requirements of the channel epoch and its companions (section 4), `VF-n` separate
  v7 fixes (section 5), `EG-n` evidence gates (section 7.3), `E0`–`E3` and `L1` milestones (section 7.2), `AC-n`
  acceptance (section 8), `FD-n` final decisions (section 10), `Qn` open questions (section 12), `PRn` principles
  (section 3).
- **Working papers** (this document repeats every code reference it needs):
  - [sbc-link-v8-catalogue.md](sbc-link-v8-catalogue.md): 47 catch-code items. `CC-n` is row n of its index
    (section 12). Its letter IDs (`A1`, `B7`, `NEW-1`) are kept;
  - [sbc-link-v8-patterns.md](sbc-link-v8-patterns.md): patterns `P1`–`P12` from mainstream protocols;
  - [sbc-link-v7-contract.md](sbc-link-v7-contract.md): the v7 contract, 101 items `V7-n`, the wait-for graph
    (`E-1`…`E-53`, cycles `C-1`…`C-8`) and the v7 defects (findings `K1`…`K28` in open-findings.md).
- **IDs of other documents are qualified:**

| Prefix | Document |
|---|---|
| `v2 Rn`, `v2 Cn`, `v2 SBn` | [sbc-link-requirements.md](sbc-link-requirements.md) |
| `LA-` | [sbc-link-v8-logic-adaptations.md](sbc-link-v8-logic-adaptations.md) |
| `DF-` | [dsf-findings-upstream.md](dsf-findings-upstream.md) |
| `OF-` | [open-findings.md](open-findings.md) |
| `LT §14` | [lab-test-plan.md](lab-test-plan.md), section 14 (lab runs) |

- **Status of statements:** "printer" = seen on the lab printer, with the run in LT §14. "Code" = from reading the
  code, not tested. Everything open is in section 12.
- **Keywords:** MUST, SHOULD, MAY as in RFC 2119.

## 1. Relation to v2 and to drafts 1 to 4

- The v2 decision of 2026-10-08, "negotiation on top of v7 instead of a fork-only v8" (v2 C1), holds again. Draft 4
  had reversed it with an own protocol version; draft 5 drops that (FD-1). The link capabilities PhaseGuard,
  SequenceGuard and ExtendedHeader stay negotiated as in v2 ("Implementation status"). Since draft 8 the channel epoch
  itself needs no negotiation (FD-37): its tag rides in a header field that a v7 peer ignores (CE-5), so there is no
  capability and no activation for it.
- v2 R6, R8 and R13 stay v2 requirements, as before draft 4. v2 R9 (integrity) becomes conditional on an EMC test
  (CE-19, FD-12). v2 R10 (stop within 100 ms on a disturbed link) gets one requirement here (CE-16, milestone L1).
- The link guarantee the epoch needs is v2 R2: each transfer is delivered once and in order within a connection. The
  census shows it for single faults with all capabilities active (v2 "Implementation status").
- There is no session resume (FD-10): a link loss ends the session on both sides as today (RRF
  `RRF/SBC/SbcInterface.cpp:194-254`, fork emergency stop `:229-230`, `InvalidateResources` `:1717-1780`; DSF
  `DCS/Link/Adapter/SPI.cs:383-390`, `DCS/Link/LinkService.cs:389-396`, job abort `:1504-1508`).
- The epoch milestone is shown to Christian Hammacher after it passed the lab (FD-14, old D8).

## 2. Problem statement

v7 has channels only in the logic: the channel sits in request-specific payloads, and `PacketHeader` has none
(`RRF/SBC/SbcMessageFormats.h:101-107`). Each catch code re-derives "which event belongs to what" from position or
timing. The catalogue counts 47 catch-code items, 17 of them fork-only.

| # | v7 assumption | Evidence | What drafts 5 to 8 do |
|---|---|---|---|
| A-1 | Replies and stack events belong to the first buffered code of the channel (`DCS/Link/Channel/Processor.cs:1102-1117`) | DF-T10 (printer, 2026-11-08); OF-T4 | Replies and stack events of an older epoch are dropped (CE-14). Matching by position within an epoch stays (FD-3); code identity only on evidence (EG-1) |
| A-2 | The pipeline depth is exactly one transfer, counted, never acknowledged (`RRF/SBC/SbcInterface.cpp:257-268`) | DF-T2, DF-T12 (printer 2026-11-08) | The abort windows no longer count transfers (CE-13, CE-14). The resend lookup and `BytesReserved` stay; NEW-1 is VF-1 |
| A-3 | Abort (`InvalidateChannel`) needs no answer and no epoch | DF-T1, T2, T10, T11, T12 | The epoch (CE-5 to CE-15); RRF never refuses InvalidateChannel: its SBC task records it, its main task applies it (CE-7) |
| A-4 | `ResendPacket` is NACK, back-pressure and polling at once; a refused packet can have acted already | DF-T11 (printer); NEW-3 | InvalidateChannel is never refused (CE-7); copies keep their tag (CE-8); act-before-refuse fixes (VF-3, VF-4); Lock as a pending request (VF-5); per-channel stall only on evidence (EG-2) |
| A-5 | Shared resources without per-channel credit (`RRF/SBC/SbcInterface.cpp:401-433`, one 64-entry reply FIFO `RRF/Platform/OutputMemory.h:16`) | DF-T3 (printer) | Unchanged: #133 stays; absolute credit only on evidence (EG-3) |
| A-6 | The stop waits for the transfer in progress and is completed by counting (`DCS/Link/LinkService.cs:313-335`, `:454-456`, `:598-599`) | Stop path tests: about 503 ms worst case (OF "Stop path under SPI faults"); OF-P3 | The stop overtakes retries (CE-16); a halt is never lost (CE-17); both in the link milestone L1 |

Latest lab evidence (LT §14, 2026-11-09 morning, run 2): `AbortsCrossingMacroRequests` passed 2000 aborts with all
fixes, but logged 2 InvalidateChannel resends (06:25:12, 06:27:29), each followed by an out-of-order "M98: Macro file
… not found", and 249 empty out-of-order replies; DCS discarded 17 crossing macro requests by #134. The run's journal
was re-read for this draft and shows the same counts. What the epoch does with them:

| Leftover | Cause | With the channel epoch |
|---|---|---|
| 249 empty out-of-order replies | RRF wrote them before it read the abort (LT §14 assessment) | They carry the epoch e of their code and arrive after DSF moved to e+1: dropped and counted (CE-11, CE-14) |
| 2 × "M98: Macro file … not found" | The M98 was handed to the GB in epoch e; the first InvalidateChannel copy came before `isWaitingForMacro` was set and flagged nothing (OF-T10) | The reply carries e, whichever copy ended the wait: dropped (CE-11, CE-14) |
| 2 InvalidateChannel resends | RRF refuses InvalidateChannel when the GB mutex is busy (`RRF/SBC/SbcInterface.cpp:897`, `:914-918`) after it acted (`:880-895`) | Gone: RRF never refuses it; the record needs no mutex (CE-7) |
| 17 crossing macro requests | Written by RRF before it read the abort | Tagged e: dropped by the filter instead of by #134's transfer window (CE-12, CE-14) |

## 3. Principles

| ID | Principle | Status in draft 8 |
|---|---|---|
| PR1 | Identity, not position or timing | Partly: the epoch identifies what an abort cancelled; replies are still matched by position within an epoch (FD-3) |
| PR2 | Order is kept within a channel; across channels the v7 order stays | Holds: nothing reorders packets |
| PR3 | A few absolute classes; one expedited class | Dropped (no classes). What remains is CE-16 (milestone L1): a pending stop overtakes the retries of a transfer |
| PR4 | Cancellation changes an epoch | Holds, without an acknowledgement packet: InvalidateChannel carries the new epoch, is never refused and acts once (CE-7). An epoch change ends everything of the old epoch on both sides, in RRF by the routine of a link loss (CE-25) |
| PR5 | Flow control by absolute credit | Dropped; only on evidence (EG-3) |
| PR6 | A refusal means nothing was done; re-sent content is identical | Holds as fixes: CE-7, CE-8, VF-3, VF-4 |
| PR7 | Request state lives on the (channel, epoch) object | Holds: the epoch is kept on DSF's channel `Processor` and on RRF's `GCodeBuffer` (CLAUDE.md rule 3). DSF's evaluation and variable requests live on `LinkInterface` (`DCS/Link/LinkInterface.cs:144-159`, `:194-208`); the epoch change reaches them through a hook (CE-25) |
| PR8 | The firmware never retransmits and keeps no per-message state | Holds as in v7 (`RRF/SBC/DataTransfer.h:238-241`) |
| PR9 | **Logical compatibility:** the epoch changes how events travel, not which orders the logic can see. Every order the logic sees with the epoch is one it can also see in v7 with the catch code of DSF 7f26d0ca and RRF 0dccb9efe; the epoch may only remove orders (those the catch code filters today). So the logic meets no new concurrency and no new wait cycle | Holds; every requirement has a PR9 column, section 4.8 checks the decisions of draft 8, and section 4.10 checks every V7 item |
| PR10 | Link faults never end a print (draft 4) | Dropped (FD-10): no session resume. v2 R1 stays the goal of the link; "DCS never exits on link data" stays as VF-1 |

## 4. Requirements

Columns: **Layer** = L or S. **Removes** = catalogue items (`CC-n`) whose catch code the requirement makes unnecessary
towards a peer that tags. **PR9** = why the requirement adds no order and no unbounded wait.

Terms:
- a **tagged** packet has bit 15 of its tag set (CE-5); an untagged packet is a v7 packet;
- a channel's epoch is **known** to RRF from DSF's first tagged packet of that channel in a connection (CE-4);
- an **epoch pair** is a fork DSF with a fork RRF, both from milestone E2;
- **record** and **apply** are the two steps of an abort in RRF (CE-7); the **routine** is the per-channel routine
  that the apply shares with a link loss (CE-25).

### 4.1 Epoch lifetime: no capability, no activation

CE-1, CE-2 and CE-3 (capability bit, offer on every board and transport, activation before channel traffic) were
removed in draft 8 (FD-37, section 10.7).

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-4 | S | **Epoch lifetime and the adopt rule (FD-37).** There is no capability and no activation for the epoch. Both sides always tag (CE-5). **DSF** keeps one epoch per channel (CE-13) and is the only writer of an epoch. Every epoch is 0 when DCS starts and where DSF ends a connection (`DCS/Link/LinkService.cs:389-396`, next to `_queryLinkCapabilities`); after a reset request the link loop ends (`:338-351`). DSF MUST NOT reset the epochs in `Processor.Invalidate()` (`DCS/Link/Channel/Processor.cs:1491-1538`), because that also runs on every emergency stop (`LinkService.cs:1511-1517`, `DCS/Link/LinkInterface.cs:606-612`), while RRF keeps its epochs across a stop. **RRF** keeps one byte per channel: the epoch (6 bits), "known" and the gate (CE-24). After boot and after `InvalidateResources` (`RRF/SBC/SbcInterface.cpp:1717-1780`, run on every lost connection, `:194-233`) every channel epoch is unknown and every `codeEpoch` is invalid. **Adopt rule:** while a channel's epoch is unknown, RRF takes it from the first tagged DSF packet of that channel, whatever its type, before the filter (CE-10). From then on it is known and the filter applies. An untagged packet changes nothing, so towards a stock DSF every channel stays unknown and RRF writes no tag (CE-18). **While unknown, RRF writes the channel's packets untagged,** not with a start value. Reasons: (a) RRF requests config.g as soon as the SBC is connected, before any DSF channel packet (`RRF/Platform/RepRap.cpp:627-637`, `RRF/GCodes/GCodes.cpp:414`). An untagged request passes DSF's filter whatever DSF's epoch is (CE-14). A start value 0 would be dropped by a DSF that did not notice an RRF restart and whose Trigger epoch is no longer 0; (b) the code supports it: the tag is a parameter of `WritePacketHeader`, and 0 is the v7 header (`RRF/SBC/DataTransfer.cpp:2290-2303`). The price is a window at the first request of a channel (Q34). **First marker:** the first tagged packet that DSF writes after it started or ended a connection carries bit 14 (CE-5). On it RRF forgets every channel epoch (all unknown, gates cleared), the same step as in `InvalidateResources`, and then adopts as above. So both sides forget at the same event, and the marker carries DSF's event to an RRF that missed it. A resent copy never carries the marker (CE-8). The table below shows the restart cases. The other candidate, an InvalidateChannel on every channel after a connect, fails on the code: InvalidateChannel ends a macro wait with an error (`SbcInterface.cpp:883-895`), so it would end RRF's wait for config.g; and it is an abort, which the logic sees | — | Not visible to the logic: the adopt rule and the marker change no event. No wait: nothing is held at connect (draft 7's hold is gone) |

**Restarts and the adopt rule.** "Noticed" figures are from the v2 status, row R6: with the link capabilities every
RRF restart (128 at every exchange, 56 right after a sequence wrap) and every DCS restart (42 cases) is noticed;
without them 6 RRF restarts right after a 16-bit sequence wrap go unnoticed.

| Case | RRF's epochs | DSF's epochs | Result |
|---|---|---|---|
| Both sides notice | all unknown | all 0, marker pending | DSF's first tagged packet carries the marker. RRF adopts each channel's epoch from DSF's first packet of that channel |
| RRF restarted, DSF did not notice | all unknown | unchanged, k per channel | RRF's packets are untagged and pass, so config.g is served. DSF's next packet of a channel carries k, and RRF adopts k. Healed without an InvalidateChannel. One corner: the first tagged packet of a channel is the resent copy of a packet that DSF wrote before an InvalidateChannel, and RRF restarted between that InvalidateChannel and the copy. RRF then adopts the older epoch and drops DSF's packets of the channel as ahead until its next InvalidateChannel; the counter of CE-20 shows it |
| DCS restarted or ended the connection, RRF did not notice | unchanged, k per channel | all 0, marker pending | The marker makes RRF forget; it adopts 0. Without the marker a channel with k ≠ 0 would be dead: RRF would drop every DSF packet of it, DSF every RRF packet, and an InvalidateChannel(1) need not follow k |
| A transfer that carries the marker is processed twice (SPI without SequenceGuard, CE-7) | forgotten and adopted twice, same result | — | An InvalidateChannel in that transfer acts twice, as in v7. Only in the first tagged transfer of a connection |

What an unnoticed restart leaves besides the epoch, the old session's work on the side that did not notice, is VF-7
and v2 R6. It is not an epoch topic.

### 4.2 Tag

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-5 | S | **Carrier and format.** `PacketHeader.resendPacketId` (16 bit, `RRF/SBC/SbcMessageFormats.h:101-107`) carries the tag in both directions, always: bit 15 = tagged, bits 0–5 = epoch, bit 14 = first marker (DSF to RRF only, CE-4), bits 6–13 = 0. 0 means untagged (v7). A stock peer ignores the field outside ResendPacket: DSF writes 0 (`DCS/Link/Protocol/Writer.cs:52`, `DCS/Link/Adapter/SPI.cs:789`, `USB.cs:683`) and reads it only in the resend lookup (`SPI.cs:831`, `USB.cs:729`, called from `DCS/Link/LinkService.cs:613-614`); RRF writes it only in `WritePacketHeader` (`RRF/SBC/DataTransfer.cpp:2290-2303`) and never reads it (the field occurs only at `SbcMessageFormats.h:106` and `DataTransfer.cpp:2290`, `:2300`). In ResendPacket the field keeps its v7 meaning (`RRF/SBC/DataTransfer.h:238-241`). Payloads MUST NOT grow: RRF advances its read pointer only by what each handler reads (`RRF/SBC/DataTransfer.cpp:628-640`; e.g. `ReadCodeChannel` reads 4 bytes, `:705-709`), so a longer payload would garble the later packets of a stock RRF. The channel stays where it is (payload or destination bit). The epoch has 6 bits because the reply path carries it in the free bits of RRF's `MessageType` (CE-11, FD-33). **Comparison (FD-21, FD-28, FD-29):** a tagged packet passes only if its tag equals the channel epoch; every other tagged packet is dropped and counted (CE-20), until a level id exists (EG-6). DSF always has an epoch; RRF adopts one first if it has none (CE-4). The one exception is InvalidateChannel, which DSF always tags with the next epoch e+1 (CE-6, CE-13): RRF's record acts if the epoch is unknown or the tag follows it as an RFC 1982 serial number with SERIAL_BITS = 6 (RFC 1982 section 3.2); an equal or preceding InvalidateChannel is a repeat that is counted and ignored (CE-7). The serial comparison also sorts the drops for the counters: stale when the tag precedes the channel epoch (the pair at distance 32, for which RFC 1982 defines no order, counts as stale), ahead when it follows. On an intact link no tag is ahead: RRF changes a known epoch only at DSF's InvalidateChannel, which it never refuses (CE-7) and reads before every packet DSF wrote after it, and RRF's tags are epochs it took from DSF. Draft 6 let an ahead tag pass; it is dropped, because a packet 33 to 63 aborts old also looks ahead. If RRF ever missed an InvalidateChannel, it would drop the channel's DSF packets as ahead until the next InvalidateChannel of the channel, whose tag follows and resynchronises; the counter shows it (CE-20). A stale packet is recognised while fewer than 64 aborts of its channel happened during its flight; its counter class is right below 32 (Q28). **Untagged packets pass:** tag 0 is never dropped by the epoch. This is the path for every packet of a stock peer and for RRF's packets of a channel whose epoch it does not know yet, such as the config.g request (CE-4). Until E3 an untagged packet still goes through DSF's v7 catch code (CE-13); what that leaves after E3 is Q34 | — | No order change; a stock peer ignores the tag (CE-18). Dropping a tag that is not current removes orders only |
| CE-6 | S | **What is tagged.** DSF tags every packet with a channel field: Code, MacroStarted, MacroCompleted, InvalidateChannel (with the new epoch), Lock, Unlock, EvaluateExpression, SetVariable, DeleteLocalVariable, SetLastCodeResult; and every chunk of a DoCode result (a Message whose destination is the DoCode's channel, `DCS/Codes/Pipelines/Executed.cs:175-209`) with the tag of its DoCode packet, passed as a parameter. RRF tags, for a channel whose epoch it knows: code replies (Message with `BinaryCodeReplyFlag`), the stack events ExecuteMacro, AbortFile, WaitForAcknowledgement, MacroFileClosed and MessageAcknowledged, DoCode, PrintPaused (with File's epoch) and its answers Locked, EvaluationResult and VariableResult, which carry the tag of the request they answer; the source of each tag is the table under CE-12. Untagged stay: ResendPacket, ObjectModel, CodeBufferUpdate, generic and other targeted messages, file requests and results, and DSF's other packets (EmergencyStop, Reset, GetObjectModel, SetPrintFileInfo, PrintStopped, other Messages, ObjectModelKeyChanged, 0x4D50, IAP). Draft 5 left DoCode, PrintPaused and the answers untagged; FD-16, FD-18 and FD-19 changed that | — | Tagging alone changes nothing |

### 4.3 The two companions

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-7 | S | **Companion 1: RRF never refuses InvalidateChannel. Its SBC task records the abort, its main task applies it (FD-34).** **Cause.** A GB belongs to the main task, which holds its mutex for a whole spin (`RRF/GCodes/GCodes.cpp:590`, V7-87). Today the SBC task carries out InvalidateChannel and must win that mutex: it acts first (`RRF/SBC/SbcInterface.cpp:880-895`), tries the mutex for 10 ms (`:897`) and then refuses (`:914-918`). This causes the resends of T11 (DF-T11, CC-6). **Working path.** Other tasks only record what they want, and the main task carries it out at the top of its spin: `requestsFromOtherTasks` (`GCodes.cpp:69-76`; set at `:228`, `:1077`, `:1391`, `:5271`; carried out at `:463-487`). `SpinGCodeBuffer` starts nothing while a request is pending (`:581-584`). **Record** (SBC task, no GB mutex, never refused), in this order: (1) a tagged InvalidateChannel acts only if the channel epoch is unknown or the tag follows it, else it is counted and ignored (CE-5, FD-28); the tag becomes the channel epoch and the gate is cleared (CE-24). An untagged InvalidateChannel always acts; (2) purge the channel's ring codes and clear `invalidated`, which is the step the write phase does today at `:1497-1504` (`InvalidateBufferedCodes` needs only a critical section, `:2527-2529`); (3) set the per-GB flag `abortRecorded` and the request bit, through a `GCodes` function as `EmergencyStop` does (`GCodes.cpp:1075-1079`); (4) end a macro wait with an error, with `MacroRequestSent` and `ResolveMacroRequest(true, false)` (`SbcInterface.cpp:893-894`), so that a main task blocked in the wait returns. The flag MUST be set before the SBC task reads the next packet. **Apply** (main task): in the request block at the top of `GCodes::Spin` (`GCodes.cpp:463-487`), after the stop and reset requests, so before `CheckTriggers` (`:517`) and before any channel is spun (`:520-545`). For every GB whose flag is set, the main task takes the GB mutex as it does at `:590` and `:1021`, runs the routine of CE-25 and then clears the flag. **Between record and apply** the GB gets no new work and the SBC task keeps off it: (a) `SpinGCodeBuffer` returns at `:581` for every GB, because the request bit is set; (b) `SbcInterface::FillBuffer` hands no code to a GB whose flag is set. This is one more entry in its hold list (`SbcInterface.cpp:1892-1894`), read inside its critical section (`:1901-1903`), where the record's purge cannot run. This hold stays from draft 7, because within one spin the main task fetches up to 8 codes without passing `:581` again (`GCodes.cpp:808-828`, `:734`); (c) `IsTriggerBusy` counts a recorded abort, as it counts an unsent abort today (`GCodes.cpp:423-433`, `:429`), so `CheckTriggers` keeps a trigger pending instead of starting it (`:1000`); (d) the SBC task does not take the GB's mutex. A packet whose handler needs it is refused, as today when the mutex is busy: MacroCompleted for an open macro (`SbcInterface.cpp:568-596`), Lock (`:619-627`), Unlock (`:649-657`), EvaluateExpression (`:698-744`), a DoCode result (`:785-789`), SetVariable (`:949`), DeleteLocalVariable (`:1131`), SetLastCodeResult (`:1307`), PrintStopped for a normal end (`:522-539`). The write phase skips the GB (CE-12); (e) a Code of the channel is stored in the ring as usual (`:401-433`) and is not dropped by the read-ahead rule (`:376-380`), whose cause the abort has voided. **Bound of the delay.** The apply runs at the top of the next `GCodes::Spin`, that is after the rest of the running main loop pass. What can make a pass long: the main task waits for a macro of the same channel (ended by the record, and by the write phase if the wait starts later, CE-12); for a macro of another channel (≤ 3 s, E-37; DSF answers in the next transfer); for a file result (≤ 3 s + 3 s, E-38, E-51 to E-53); for the answer of an expansion board (1000 ms per request, `RRF/CAN/CanInterface.h:27`; how many requests one step issues was not counted). The same waits bound C-4 in v7. The pass time on the lab printer is not measured (Q38). **Flags and tasks.** `abortRecorded` reuses the byte of `invalidationPending` (`RRF/GCodes/GCodeBuffer/GCodeBuffer.h:422`), renamed. It moves out of the group "Accessed only by the SBC task" (`:420`): the SBC task sets it, the main task clears it under the GB mutex, both read it. The warning at `:412-413` is about bit-fields that two tasks write. A `bool` with a byte of its own that one task sets and the other clears is the pattern of `abortFile`, `sendToSbc`, `messagePromptPending` and `messageAcknowledged` (`:414-418`). If a second InvalidateChannel is recorded while the apply runs, the clear can swallow its flag. That is harmless: between the routine and the clear nothing of the GB was started (the main task is in the apply, the SBC task keeps off the GB), and the second record has purged the ring itself. In the routine the main task now also clears flags that only the SBC task cleared so far (table under CE-25), each a byte of its own. The comment at `:420` is wrong already in v7 for `invalidated` (`:421`), which the main task sets for a pause (`RRF/GCodes/GCodes4.cpp:2016-2020`). **Until E3** the fork catch code stays next to it: at the record the `codeReplySurplus` flag of a code waiting for its macro (`SbcInterface.cpp:886-889`, RRF #46); in the apply, after the routine, the flag of a code still running (`:908-911`). The voiding of an unsent request (`:893`, RRF #43) and `MacroFileClosedSent` (`:904`, RRF #45) become part of the record and of the routine for good. `codeReplySurplus` itself (`GCodeBuffer.h:405`) is deleted in E3. **Repeats (FD-28).** The first-copy guard (`firstCopy = !IsInvalidationPending()`, `SbcInterface.cpp:882`) is dropped, because DSF writes no copy once RRF never refuses. A repeated transfer can still deliver an InvalidateChannel twice: on SPI without SequenceGuard, RRF processes again a transfer that DSF repeats because it missed RRF's response (`RRF/SBC/DataTransfer.cpp:923-929`, `:1132-1137`); SequenceGuard is offered on SAME70 only (`:475-479`). Acting twice would purge the codes stored since and abort new work. A tagged repeat is ignored by its tag. An untagged one cannot be told from a new abort; the repeated transfer then repeats its other packets too, codes included, as in v7. This is a v7 fix that works with every DSF (E1) | CC-6 (#161), CC-8's first-copy role | No new wait edge: the SBC task waits for nothing at the record (its 10 ms try of the GB mutex for InvalidateChannel, part of E-42, is gone). The main task takes the GB mutex at the top of its spin as it does at `GCodes.cpp:590` (E-40), and the SBC task does not hold that mutex while an abort is recorded. C-4 is broken as in v7, by ending the macro wait first. No new order: RRF runs codes of the channel only after the apply, as v7 does after an accepted InvalidateChannel. The refusals between record and apply are v7 refusals for a busy GB (V7-11), bounded by one main loop pass. A trigger that stays pending for a pass is a v7 order (V7-81). Ignoring a repeat removes orders only |
| CE-8 | S | **Companion 2: copies keep their tag.** DSF MUST write a resent copy with the tag of the original, without the first marker (CE-4). Today the copy gets 0, because `ResendPacket` writes it through `WritePacket` (`DCS/Link/Adapter/SPI.cs:837` → `:782-790`; `USB.cs:735` → `:676-684`). The tag is passed as a parameter, not looked up | — | No order change |

### 4.4 RRF side of the epoch

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-9 | S | **Epoch of a code; two tag sources (FD-36).** RRF MUST stamp every code it accepts into the ring with the tag of its Code packet, the one that passed the filter (CE-10; FD-29), in the free `BufferedCodeHeader.padding` (`RRF/SBC/SbcMessageFormats.h:368-373`; written today only `isPending` and `length`, `RRF/SBC/SbcInterface.cpp:424-426`); an untagged code gets an invalid stamp. `FillBuffer` MUST carry the stamp into a per-GB `codeEpoch` (valid bit + 6-bit epoch) when it hands the code over (it reads the ring header at `:1910` and hands the code over with `PutBinary` at `:1945`). **Only two tag sources remain:** (1) the replies of a code that DSF sent, and the macro requests RRF makes for that code (`fromCode` true, `RRF/GCodes/GCodes.cpp:3699`), carry `codeEpoch`; (2) everything else carries the channel epoch at the time the SBC task writes it: the other stack events, macro requests without a code, DoCode, PrintPaused. For (2) the epoch at the write equals the epoch when the event was raised, because no pending event survives an epoch change (the apply voids them, CE-25) and nothing is written between record and apply (CE-12). So no event keeps an epoch of its own. Gone from draft 7: the stored epochs of the box timeout and of PrintPaused, the argument why AbortFile(all) needs none, and the rule that a text code or a macro request without a code sets `codeEpoch`. A text code (`GCodeBuffer::Put`, `PutAndDecode`, `RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:294`, `:334`, `:345`) clears `lastCodeFromSbc`, so its replies are no SBC replies (`:1335-1344`) and its macro requests have `fromCode` false. **Why source (1) stays:** a code that still runs at the base level survives the abort (CE-25). Its later replies MUST carry its own epoch, so that DSF drops them, and its macro requests MUST fail (CE-12). **What source (2) gives:** a request or closal of work that RRF starts itself (config.g `GCodes.cpp:414`, daemon.g `:690`, triggerN.g `:1052`, start.g `:4001`, event macros `RRF/GCodes/GCodes3.cpp:1432`, stop.g `RRF/GCodes/GCodes4.cpp:844`) carries the current epoch. So a trigger request after an abort of the Trigger channel passes, and the closal of a system macro without codes is not dropped (else DSF's level would wait for it for good, E-17). An invalid `codeEpoch` or an unknown channel epoch gives no tag (v7 behaviour); `InvalidateResources` marks every `codeEpoch` invalid (CE-4). **What source (2) does not cover:** a code that survived the abort at the base level and later raises a stack event of its own, e.g. the message box of manual probing (`RRF/GCodes/GCodes6.cpp:149-157`), writes it with the new epoch, and DSF takes it, as in v7 (Q35) | — | Not visible to the logic |
| CE-10 | S | **Filter of stale DSF packets (FD-16, was Q25; FD-29).** For a channel whose epoch it knows (it adopts one first, CE-4), RRF MUST drop and count every tagged DSF channel packet whose tag is not the channel epoch, stale or ahead (CE-5), before any side effect: Code, MacroStarted, MacroCompleted, Lock, Unlock, EvaluateExpression, SetVariable, DeleteLocalVariable, SetLastCodeResult and the chunks of a DoCode result. Side effects that the drop must precede: a Code resolves a macro wait at `RRF/SBC/SbcInterface.cpp:383-392` before its buffer check (`:395`); MacroCompleted resolves a wait or pops (`:558-597`); MacroStarted resolves a wait (`:839-842`); a DoCode result finishes the GB (`:774-793`). A dropped request gets no answer; DSF has failed it already (CE-25). InvalidateChannel is never refused; its record ignores only a repeat, whose tag does not follow the channel epoch (CE-7). An untagged packet passes (CE-5). Draft 5 applied stale copies of the other types and answered EvaluateExpression and SetVariable; draft 6 let an ahead tag pass | CC-5 (RRF #43) together with CE-12 | Removes orders only: a cancelled code no longer runs after the abort, a stale request no longer acts on the new epoch. A request without answer is failed by DSF at the epoch change, as `InvalidateCodes` fails all of them on a stop (`DCS/Link/LinkInterface.cs:615-641`) |
| CE-11 | S | **Tag of code replies.** `GCodeBuffer::GetResponseMessageType` (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1335-1344`) is the only place that builds the reply type of a code from the SBC (`(1u << channel) \| BinaryCodeReplyFlag`, `:1340`; no other code sets `BinaryCodeReplyFlag`). It has 15 call sites in 14 lines: `GCodes.cpp:3706`, `:4467`, `:4520`, `:4592`, `:4598`, `:6051`; `GCodes2.cpp:2292` (M122); `GCodes4.cpp:1894`, `:1906`; `GCodes6.cpp:140`; `StringParser.cpp:473`; `GPIO/GpInPort.cpp:90` (two); `SbcInterface.cpp:1568`, `:1571`. It MUST put `codeEpoch` into the free `MessageType` bits: bits 18–23 = epoch, bit 26 = valid (`RRF/Platform/MessageType.h`: destinations end at 0x3FFFF, `:39`; flags are bits 24, 25, 27–31, `:43-50`; bit 26 is unused). The type travels unchanged through the reply FIFO's 32-bit types (`RRF/Platform/OutputMemory.h:178-179`). `DataTransfer::WriteCodeReply` (`RRF/SBC/DataTransfer.cpp:1488-1503`) MUST move it into `resendPacketId` (CE-5) and clear bits 18–23 and 26 in the type it writes. Chunks of different epochs never merge, because the push merge compares the whole type (`SbcInterface.cpp:2369`). Two places compare a type for equality, both with `GenericMessage` (`GCodes2.cpp:2293`, `GCodes6.cpp:141`); an SBC reply type never equals it, with or without the bits; two compare with `NoDestinationMessage` = 0 (`RRF/Platform/RepRap.cpp:762`, `:2367`). Deferred M122 output keeps the type it was given (`RRF/Platform/RepRap.h:57`, `RepRap.cpp:762-766`), so it carries the epoch of the code that asked. Routing tests single bits only (`RRF/Platform/Platform.cpp:3254-3256`, `:3266-3273`, `:3314-3321`). This answers Q6: M122, `GpInPort` and the SD speed test get the tag too | CC-7 (RRF #46 `codeReplySurplus`, `GCodeBuffer.h:405`) towards a DSF that tags | Removes orders only: a reply of an older epoch no longer completes a code of the new one |
| CE-12 | S | **Stack events and requests: nothing between record and apply, one failure path (FD-35, FD-36).** The tags are in the table below. **One condition in the write phase** (`RRF/SBC/SbcInterface.cpp:1485-1637`): while a GB's abort is recorded and not applied (CE-7), the SBC task skips that GB's block as a whole; it only ends a macro wait (below). So it writes no MacroFileClosed (`:1507`), no macro request (`:1514-1527`, `:1598-1611`), no AbortFile (`:1541-1577`), no prompt and no acknowledgement (`:1580-1595`) and no DoCode (`:1614-1634`), and it does not run the purge for `invalidated` (`:1497-1504`), which the record has done. PrintPaused (`:1640-1643`) is not written while File or File2 has an abort recorded. The apply then voids all of them (CE-25). Replies of a code that still runs keep going out with their code's epoch (`:1389`, CE-11); they are no channel events. **One failure path for a macro request that must not be written.** A GB that waits for a macro gets no ExecuteMacro (a) while its abort is recorded, or (b) when the request is one of a code (`fromCode`) whose `codeEpoch` is valid and is not the known channel epoch. In both cases the SBC task ends the wait where it would have written the request, with the two calls of the record, `MacroRequestSent` and `ResolveMacroRequest(true, false)` (`:893-894`). This sets `macroFileError` (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1286-1291`), so `RequestMacroFile` returns false at once (`:1278-1283`) instead of after its 3 s timeout (`:1266-1273`). Case (a) is a wait that starts after the record, in the rest of the running spin. Case (b) is a code that survived the abort at the base level and goes on, e.g. a G28 whose homing file the abort popped and which then requests the next one (OF-T10, `RRF/GCodes/GCodes4.cpp:358-413`); it ends with "Homing file … not found" (`GCodes4.cpp:383-389`), a reply of the old epoch that DSF drops. No DSF packet resolves such a wait in between: Code, MacroStarted and MacroCompleted resolve only a request that has gone out (`SbcInterface.cpp:383`, `:558`, `:839`). **The non-blocking request needs no path of its own.** Only the Daemon GB outside a macro requests without waiting (`GCodeBuffer.cpp:1258-1261`), that is daemon.g, never the request of a code. One that is unsent at the apply is voided by the routine together with the level that `DoFileMacro` pushed (`RRF/GCodes/GCodes.cpp:3715-3725`, CE-25); the Daemon GB requests daemon.g again after its dwell (`GCodes.cpp:680-690`). This covers OF-T11 (A5). Draft 7 had two failure paths and a second guard for it; they are gone. **Compared with today.** When RRF refuses InvalidateChannel today, it has ended the macro wait already (`:883-895`) and goes on writing events of that GB until the copy is accepted. The fork DSF drops them with the windows of #134 and #161 (CC-2, CC-6); a stock DSF takes them. With the record, RRF writes no event of the GB after it read the InvalidateChannel, to any DSF. What a stock DSF still sees are the events RRF wrote before it read the InvalidateChannel, one transfer (DF-T2) | CC-5 (RRF #43) and CC-4 (RRF #45) stop being catch code: their lines become part of the record and of the routine; A5 | Removes orders only. A macro request that fails is a v7 outcome (InvalidateChannel ends a wait with an error, V7-53); it fails at once instead of after 3 s, a shorter wait. "Write nothing" is bounded by the apply, one main loop pass (CE-7). The events held are void, and DSF waits for none of them: it popped its levels when it aborted (V7-52). No wait edge is added |

**Tag source per RRF→DSF packet (FD-36).** (1) = `codeEpoch`, the epoch of the code that the output belongs to; (2) =
the channel epoch when the SBC task writes the packet. A channel with an unknown epoch, or an invalid `codeEpoch`,
gives no tag. Request numbers from `RRF/SBC/SbcMessageFormats.h:269-349`; `RRF/` paths shortened to the file.

| RRF → DSF | Raised by | Tag | Code |
|---|---|---|---|
| 3 Message with `BinaryCodeReplyFlag` | a code DSF sent; also the output of M122, `GpInPort` and the SD speed test (CE-11) | (1) | `GCodeBuffer.cpp:1335-1344` |
| 4 ExecuteMacro, `fromCode` true | a code DSF sent and the helper macros it needs: M98, homing files, tool change files, deploy and retract probe, pause.g, filament-change.g, resume.g, cancel.g, stop.g of a code, config-override.g | (1) | `GCodes.cpp:3699`; e.g. `GCodes4.cpp:383`, `:435-518`, `:565-578`, `:844`; `GCodes3.cpp:1215-1245`; `GCodes2.cpp:799-804`, `:1205`, `:3462` |
| 4 ExecuteMacro, `fromCode` false | a text code (PanelDue, USB, trigger action); RRF itself with `AsyncSystemMacroCode`: config.g `GCodes.cpp:414`, daemon.g `:690`, triggerN.g `:1052`, start.g `:4001`, event macros `GCodes3.cpp:1432`, stop.g at the end of a print `GCodes4.cpp:844` | (2) | `GCodes.cpp:3688-3699`; `GCodeBuffer.cpp:294`, `:334`, `:345` |
| 5 AbortFile, single | M99; a box cancelled with abort inside a file, after M292 or the box timeout | (2) | `GCodes.cpp:593-615`; `Platform/MessageBox.cpp:172-177` |
| 5 AbortFile(all) | RRF's own abort: `AbortPrint` after a state machine error, a stack overflow, the GB part of DSF's emergency stop; File2 also from File | (2) | `GCodes.cpp:3623-3640`, `:1081-1087`, `:2059-2063`; `GCodes4.cpp:168-321` |
| 13 WaitForAcknowledgement | M291 S2 or higher, manual probing | (2) | `GCodes7.cpp:98-115`; `GCodes6.cpp:149-157` |
| 14 MacroFileClosed | the end of a macro | (2) | `GCodeBuffer.cpp:1294-1307` |
| 15 MessageAcknowledged | M292 on any channel; the box timeout | (2) of the box's GB | `GCodes7.cpp:121-172`; `Platform/MessageBox.cpp:172-177`; `GCodeBuffer.cpp:1313-1333` |
| 7 PrintPaused | RRF: M25 or M226 of any channel, filament and stall events, power fail, trigger 1 | (2) of File | `GCodes4.cpp:2012-2022`; `SbcInterface.h:184-196` |
| 12 DoCode | a text code that RRF passes on | (2) | `SbcInterface.cpp:1613-1634` |
| 9 Locked, 11 EvaluationResult, 16 VariableResult | an answer to a DSF request | the tag of that request | `SbcInterface.cpp:607-634`, `:682-760`, `:928-1109` |
| 0 ResendPacket, 1 ObjectModel, 2 CodeBufferUpdate, other Messages, file requests | not bound to a channel's work | none | — |

### 4.5 DSF side of the epoch, the end of the old epoch, RRF's abort gate

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-13 | S | **Epoch on the channel `Processor`; catch code until E3 without a switch for the filter.** DSF MUST keep the epoch on `Processor` and increment it where it writes InvalidateChannel (Spin step 3, `DCS/Link/Channel/Processor.cs:931-937`, only when the write succeeded); there it also ends the old epoch's requests (CE-25). DSF filters only tagged packets (CE-14), so the filter needs no switch. **Until E3:** the windows `_allFilesAbortedSent` and `_allFilesAbortResent` (`:119-136`; uses `:883-892`, `:1285-1290`, `:1357-1362`, `AllFilesAbortResent` `:1312-1315`, `DCS/Link/LinkService.cs:620-626`) act on untagged packets only; a tagged packet is decided by its tag. The hold `_allFilesAbortUnconfirmed` (`:131`, `:902-905`) is skipped once DSF has seen a tagged packet from RRF in the connection, because such an RRF never refuses InvalidateChannel (CE-7). The connection keeps that fact and forgets it with the epochs (CE-4); Spin gets it as a parameter. Until DSF has seen a tag, and towards an RRF that never tags, it runs its v7 catch code as today. **In E3** all of it is deleted, with the out parameter of `ILinkAdapter.ResendPacket` (CE-18). `_allFilesAborted` (`:117`) stays: it is the trigger to write InvalidateChannel. When the epoch changes or is reset, DSF MUST discard the channel's partial reply (`_lastPartialMessage`, `:1184-1189`), so that no chunk of an older epoch is prepended to the next reply (K11 for aborts). Codes after the abort go out in the next Spin; they wait in RRF's ring until the apply (CE-7) | CC-2 (#134), CC-3 (#158), CC-6 (#161) | Same per-channel order as with #161, where DSF held the codes; now they wait in RRF's ring. Removes E-16 (the channel's wait for the acceptance of InvalidateChannel) |
| CE-14 | S | **Filter of stale RRF packets.** At the top of `LinkService.ProcessPacket` (`DCS/Link/LinkService.cs:609`), DSF MUST drop every tagged RRF packet whose tag is not the epoch of its channel, stale or ahead (CE-5, FD-29), and count it per channel and request type: code replies, ExecuteMacro, AbortFile, WaitForAcknowledgement, MacroFileClosed, MessageAcknowledged, DoCode, PrintPaused (against File's epoch) and the answers Locked, EvaluationResult and VariableResult. The channel comes from the payload or the reply's destination bit (`DCS/Link/Channel/Manager.cs:107-121`). A dropped reply is not routed to the console. A stale DoCode is not run; DSF fails it with an error (CE-25, LA-10). An untagged packet passes the filter (CE-5) and, until E3, goes through the v7 windows (CE-13). **Answers are filtered too (changed from draft 5, consequence of FD-16):** draft 5 left them unfiltered because DSF merges evaluations per (channel, expression) (`DCS/Link/LinkInterface.cs:144-153`) and a dropped answer would hang later identical ones. Now the epoch change fails every request of the old epoch first (CE-25), so no request of the new epoch waits for a dropped answer. An unfiltered stale answer would instead reach a request of the new epoch: an EvaluationResult resolves the first written request with the same channel and text (`DCS/Link/LinkService.cs:1009`), a VariableResult the first with the same name and channel, written or not (`:1127`), a Locked the first lock request from the top of the stack (`DCS/Link/Channel/Processor.cs:838-849`). ObjectModel stays untagged and is never filtered | CC-2, CC-3 | Removes orders only: the packets dropped are those the windows of #134 and #158 drop today, plus the replies that RRF #46 suppresses and those it misses (OF-T10). Without the answer filter, CE-25 would add an order v7 does not have: a stale answer completing a request of the new epoch |
| CE-15 | S | **RRF's own abort of all files.** DSF MUST answer a tagged AbortFile(all) of the current epoch with InvalidateChannel(e+1), by setting `_allFilesAborted` in `FilesAborted(true)` (`DCS/Link/Channel/Processor.cs:646-760`, called from `DCS/Link/LinkService.cs:866-889`). An untagged AbortFile(all) is handled as in v7, without an answer (V7-46). Replies of codes RRF still ran then carry e and are dropped (K18, G-20). This InvalidateChannel also clears RRF's gate (CE-24). RRF's apply of that InvalidateChannel runs the routine on a GB whose files are aborted already; whether that is harmless is Q9. Whether RRF requests an abort after its own 3 s macro-wait timeout (old D10) is a logic decision, open (LA-8): it would abort the File channel's print | — (K18 has no catch code; RRF #46 does not cover it) | "InvalidateChannel after RRF's AbortFile(all)" is a v7 order (a DSF abort can cross RRF's abort-all). No wait: DSF does not wait for an answer |
| CE-24 | S | **RRF's abort gate (FD-20).** RRF MUST keep one gate bit per GB. It is set where RRF writes a tagged AbortFile(all), that is for a channel whose epoch it knows (`RRF/SBC/SbcInterface.cpp:1549`, in the branch that wrote it, `:1550-1576`). Only a tagged one is answered (CE-15); a v7 DSF answers none (V7-46), so towards it the gate is never set. Draft 7 armed the gate by the capability; now the known epoch arms it, per channel. While it is set, RRF MUST drop and count every DSF channel packet of that channel except InvalidateChannel, before any side effect, as CE-10 does. The InvalidateChannel(e+1) that DSF sends back (CE-15) clears it at the record (CE-7); so does an InvalidateChannel that DSF wrote for its own abort and that crossed the AbortFile(all), which DSF then drops as stale (CE-14). The gate cannot stay set: the AbortFile(all) carries the channel epoch at its write (CE-9), so DSF either answers it or has already written the InvalidateChannel that clears the gate. An AbortFile(all) that is unsent when an InvalidateChannel is recorded is never written (CE-12) and sets no gate. RRF clears every gate when it forgets the epochs (CE-4). Nothing raises it on an RRF-local emergency stop: `DoEmergencyStop` (`RRF/GCodes/GCodes.cpp:1065-1070`) calls `Reset`, which clears the abort flags (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:145`), so no AbortFile(all) is written. The macro-wait timeout stays LA-8. The gate keeps the one gain of the rejected RRF epoch (FD-20): DSF packets that cross RRF's own abort no longer act on the GB before DSF's InvalidateChannel(e+1) | — | Removes orders only: in v7 such packets act on the GB after RRF aborted its files (V7-46). No new wait: DSF has cancelled its buffered codes and popped its levels on the AbortFile(all) (V7-46), and it fails the requests that the gate dropped when it writes InvalidateChannel(e+1) (CE-25) |
| CE-25 | S | **An epoch change ends everything of the old epoch on both sides; in RRF by the routine of a link loss, for every DSF (FD-17, FD-36).** **Cause.** Today InvalidateChannel ends only a hand-picked part of the GB's pending work. The leftovers of the old epoch made draft 7 keep stored epochs, two failure paths for macro requests and branches "only in an epoch connection". **Working path.** `InvalidateResources` ends everything per channel on a link loss (`RRF/SBC/SbcInterface.cpp:1746-1773`). **RRF: one routine,** called under the GB mutex by `InvalidateResources` (SBC task, after it ended a macro wait, `:1756-1761`) and by the apply (main task, CE-7). It has no branch for its caller and none for the kind of DSF. The table below says what it does to each piece of state; no "tell DSF" flag of the GB is left set after it, so nothing of the old epoch is announced afterwards. This is also the fix of K5 (stale RRF events after a reconnect), in the same PR (E1), with its own unit test (AC-21). **A code that still runs at the base level survives the abort:** the routine does not call `AbortStateMachine` and does not reset the buffer, and `AbortFile(true)` only pops file levels (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1121-1150`). Its replies carry its own epoch (CE-9), and its macro requests fail at once (CE-12). **Locks, only of an idle GB (FD-27).** The routine calls `UnlockAll(gb)` only if `gb->IsCompletelyIdle()` (state normal and the buffer neither ready nor executing, `GCodeBuffer.cpp:947-955`). It can run while a code of the old epoch still executes on the GB (the v7 handler says so itself, `SbcInterface.cpp:907-911`). `UnlockAll` at the base level also clears `toBeHomed` when it releases the movement lock (`RRF/GCodes/GCodes.cpp:5658-5663`), so a running G28 would stop homing (`RRF/GCodes/GCodes4.cpp:358-363`, `:394-406`), and another channel could take the movement lock and move while the G28 still runs. v7 never releases a lock while a code of the GB runs: M505 flushes first (`DCS/Codes/Handlers/MCodeHandler.cs:958`), and Spin writes the Unlock before any later code of the level (`DCS/Link/Channel/Processor.cs:907-929`). The idle release covers an Unlock that RRF refused and whose copy arrives after the abort and is dropped, and K28, an Unlock that DSF drops itself at the abort; the GB is idle there after M505's flush. If a code of the GB still runs, RRF keeps the lock until that code ends (`UnlockAll` in `HandleResult`, `RRF/GCodes/GCodes2.cpp:5112-5116`), as in v7. **DSF,** where it writes InvalidateChannel(e+1) (CE-13): the lock and flush requests of the channel are failed already by the abort (`Pop`, `DCS/Link/Channel/Processor.cs:211-215`, `:266-269`; `InvalidateRegular`, `:1462-1465`, `:1478-1481`, called at `:731` and `:823`); a hook MUST fail every evaluation, variable and SetLastCodeResult request of that channel that exists then, written or not, as `InvalidateCodes` fails them for all channels on a stop (`DCS/Link/LinkInterface.cs:615-641`). These requests live on `LinkInterface`, not on the channel (`:144-159`, `:194-208`). DSF fails a DoCode of the old epoch with an error and sends no result (LA-10); a DoCode is a `SimpleCode` that the channel does not keep (`Processor.cs:1040-1050`), so it gets its epoch as a parameter, and how DSF ends a running one is left to the design. **Behaviour changes in RRF, for every DSF from E1:** (a) an abort releases the locks of an idle GB at once; in v7 RRF keeps them until an Unlock or until the GB's next code ends, and DSF itself drops an Unlock that is queued but not written when it aborts (K28); for a GB that is not idle nothing changes; (b) an abort ends a DoCode of the GB, sent or not (K13); (c) an abort closes a message box left at the base level (the open case of OF-T10) and writes no MessageAcknowledged for it; (d) after a reconnect no event of the old session is written (K5). What a stock DSF does in (b) and (c) is in the second table below. **Behaviour changes in DSF, from E2:** (e) DSF fails a DoCode of the old epoch with an error; (f) an evaluation, variable or code-result request that a code issues between the abort and the write of InvalidateChannel, at the latest in the next Spin (`Processor.cs:822`, `:931-937`), fails too | — | (a) happens only on an idle GB and has the effect of an Unlock that RRF processes right after the InvalidateChannel, which v7 produces when DSF disposes the movement lock after the abort (`DCS/Link/LinkInterface.cs:439-443`); with a code running the lock is released when it ends, as in v7; (b) both endings are v7 outcomes: RRF ends the wait on a link loss (`RRF/SBC/SbcInterface.cpp:1761-1765`), DSF cancels a DoCode without result (G-12); (c) a box closed as cancelled is the v7 outcome of M292 P1 (`RRF/GCodes/GCodes7.cpp:121-155`), and DSF has popped the box level at its abort; (d) removes events only; (e), (f) a failed request is a v7 outcome (`InvalidateCodes`). No new wait: the change only ends waits (E-25, E-26, E-47, E-48 for the base-level box, E-49). The routine takes no lock besides the GB mutex that its caller holds |

**What the routine does (CE-25).** "v7" = RRF 0dccb9efe: the InvalidateChannel handler
(`RRF/SBC/SbcInterface.cpp:868-925`) and `InvalidateResources` (`:1717-1780`). `RRF/` paths shortened to the file.

| State | v7: InvalidateChannel | v7: `InvalidateResources` | Routine, both callers |
|---|---|---|---|
| Macro wait, `isWaitingForMacro` (`GCodeBuffer.h:399`) | ended with an error (`:894`) | ended (`:1756-1759`) | Ended by the caller before it takes the mutex: by the record (CE-7) and by `InvalidateResources`. The main task cannot wait while it runs the apply |
| Unsent macro request, `requestedMacroFile` (`GCodeBuffer.h:403`) | voided only during a wait (`:893`, fork RRF #43) | voided (`:1766-1769`) | `MacroRequestSent` |
| DoCode: unsent `sendToSbc` (`GCodeBuffer.h:416`), sent `executingOnSbc` (`:447-452`) | untouched | the sent one is finished (`:1762-1765`), the unsent one stays | `SetFinished(true)` for both (`GCodeBuffer.cpp:957-966`). Clearing `sendToSbc` alone would not do: the code is still in the buffer and asks again on its next spin (`GCodes2.cpp:603-606`, `:730-732`) |
| Pause sequence | `PauseSequenceAborted` (`:901`) | not called | `PauseSequenceAborted`, before the files are aborted, because it reads the frames that `AbortFile` deletes (`GCodes.cpp:3646-3663`). It is in the routine, not only in the abort, because it is harmless on a link loss: `StopPrint` resets the pause state afterwards (`GCodes.cpp:4884-4885`, through `printAborted`, `SbcInterface.cpp:1776`, `GCodes.cpp:548-553`) |
| File levels; `abortFile`, `abortAllFiles` (`GCodeBuffer.h:414-415`) | aborted, flags cleared (`:902-903`) | the same (`:1770-1771`) | `AbortFile(true)`, `FileAbortSent`. Without `FileAbortSent` RRF would answer DSF's abort with AbortFile(all), which an epoch DSF answers with another InvalidateChannel (CE-15), a loop |
| `macroFileClosed` (`GCodeBuffer.h:400`) | cleared (`:904`, fork RRF #45) | stays (K5) | `MacroFileClosedSent` |
| Message box: a level with `waitingForAcknowledgement`; `messagePromptPending`, `messageAcknowledged` (`GCodeBuffer.h:417-418`) | untouched; a box at the base level stays open (OF-T10) | `MessageAcknowledged(true, true, 0, …)` (`:1772`), which sets `messageAcknowledged` (`GCodeBuffer.cpp:1328`; K5); `messagePromptPending` stays | `MessageAcknowledged(true, false, 0, …)`, then `MessagePromptSent` and `MessageAcknowledgementSent`. After `AbortFile(true)` only a box at the base level is left. `shouldAbort` MUST be false, see below |
| `invalidated` (`GCodeBuffer.h:421`) | untouched | stays (K5) | cleared; the event that set it is void |
| Pause report `reportPause` (`SbcInterface.h:99`), for File and File2 | untouched | stays (K5) | cleared when the GB is File or File2. DSF aborts the job on an abort of either (`DCS/Link/Channel/Processor.cs:826-831`, `:753-758`) |
| Locks | kept | kept | `UnlockAll` only if the GB is completely idle (FD-27) |
| `codeReplySurplus` (`GCodeBuffer.h:405`, fork RRF #46), until E3 only | set (`:888`, `:910`) | stays (K5) | Not in the routine. `InvalidateResources` clears it; the apply sets it after the routine as today (CE-7) |
| Reply FIFO, ring codes, `codeEpoch` | ring purged (`:905`) | FIFO released, ring reset (`:1719-1722`, `:1740-1744`) | Not touched. Replies carry their code's epoch (CE-11); the record purges the ring (CE-7); `InvalidateResources` does its own, and marks `codeEpoch` invalid (CE-4) |

**Why `shouldAbort` is false.** With `MessageAcknowledged(true, true, …)` the main task pops the box level and then, at
the base level, calls `StopPrint(nullptr, StopPrintReason::userCancelled)` (`RRF/GCodes/GCodes.cpp:593-616`). That
closes the print file of File and File2 and clears the move queues, whatever channel the box was on (`:4882-4947`). On
a link loss the print is stopped anyway (`printAborted`, `RRF/SBC/SbcInterface.cpp:1776`; the emergency stop, `:230`),
so the argument is redundant there. On the abort of one channel it would end a print that the abort did not address.
With `false` the main task pops the box level (`GCodes.cpp:593-596`) and the M291 ends with its final reply
(`RRF/GCodes/GCodes4.cpp:1958-1961`, `:1998-2008`), which carries the M291's epoch. The box stays in RRF's list of
message boxes until it is acknowledged or times out; a later M292 finds no waiting level
(`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1313-1333`). Removing it from the display is logic (LA-24).

**What a stock DSF does in the two new cases.** Read from the v7 path of DSF 7f26d0ca; not checked by `git blame` that
every line is upstream.

| Case | DSF at its abort | Today | With the routine (E1) |
|---|---|---|---|
| A DoCode runs when its channel is aborted (b) | DSF runs a DoCode as a prioritized code on that channel (`DCS/Commands/Generic/SimpleCode.cs:104-109`, `DCS/Link/Channel/Processor.cs:1040-1050`). It sends the result when the code has one and nothing when the code was cancelled (`DCS/Codes/Pipelines/Executed.cs:170-230`) | Cancelled: RRF's GB stays "executing on SBC" without a bound (K13). Completed: the result finishes the GB | Cancelled: the GB is free after the apply. Completed: the late result is printed to its target and finishes nothing (`RRF/SBC/SbcInterface.cpp:774-803`). If the GB has passed on a newer DoCode by then, the late result finishes that one early, because a stock DSF sends no tag. An epoch DSF tags the chunks, and RRF drops a stale one (CE-10) |
| A message box is open at the base level when its channel is aborted (c) | DSF pops its box level and cancels the M291 (`DCS/Link/Channel/Processor.cs:770`, `:806-814`) | The box stays open in RRF. After the user's M292, DSF logs "Tried to acknowledge a message, but no acknowledgement is requested!" (`:1341-1344`) and gets the M291's final reply | No MessageAcknowledged is written. The M291's final reply comes right after the abort. A stock DSF takes it as the reply of its first buffered code of the channel or logs it as out of order (`Processor.cs:1108-1117`, `:1162-1167`), as it does today after the M292; RRF #46 does not flag it, because the GB is idle while the box is open (`SbcInterface.cpp:908`). An epoch DSF drops it by its tag |

### 4.6 Stop path and halt (link)

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-16 | L | **The stop overtakes retries, never into a repeat (FD-9, FD-15).** Today the link thread checks the halt request only at the top of its loop (`DCS/Link/LinkService.cs:325-335`; `InvalidateCodes` and the write of the stop happen there in the same iteration, `:327-328`), so a stop waits until `PerformFullTransfer` (`:598`) returned, and its retry loop resends the same TX (`DCS/Link/Adapter/SPI.cs:323-440`). Measured worst case about 503 ms behind a long code transfer that noise keeps failing (OF "Stop path under SPI faults"). When a stop is pending, DSF MUST put it first into the next new transfer, that is the next transfer whose data RRF reads. A retry of a transfer that failed before RRF completed it is such a transfer: it carries the stop first, and the content of the old transfer follows unchanged and MUST NOT be dropped (a dropped GetObjectModel would stay "sent" for good, K16; a dropped file result would make RRF wait 3 s). A SequenceGuard repeat stays a pure repeat, in both directions: RRF answers a transfer that it completed already with its last TX and does not read DSF's data (`RRF/SBC/DataTransfer.cpp:924-946`, `:950-961`), so a stop placed there would be lost; and DSF sends a transfer that RRF did not complete again as it was (`DCS/Link/Adapter/SPI.cs:1992-2010`), then the pending one (`:408-419`). DSF learns that RRF answers its retry as a repeat only in the header exchange of that retry (`SPI.cs:2012-2019`), after it built the retry; a stop that it placed there MUST stay pending and go first into the next new transfer (FD-26). No header change. **Resend lookup (FD-30):** a stop in front of a retried transfer MUST NOT break DSF's resend lookup. Packet ids restart at 0 in every transfer and follow the write order (`SPI.cs:439`, `:787`); the lookup walks the last TX in order, stops at the first packet whose id is not below the requested one (`:826-853`, `while (header.Id < packet.ResendPacketId ...)`) and otherwise throws `ArgumentException` (`:855`), which ends DCS (V7-7, VF-1). A stop with a new id in front of the old content would make every later resend request throw. DSF MUST therefore either (1) renumber the packets of the retry, the stop first, and rewrite the transfer header and its CRC (`NumPackets`, `DataLength`, `WriteCRC`, `:317-320`), so that the ids stay ascending, or (2) make the lookup independent of the order, searching every packet of the last TX for the id. The L1 PR chooses one. **Room (FD-30):** the stop is one `PacketHeader` without payload, 8 bytes (`RRF/SBC/SbcMessageFormats.h:101-107`; `SPI.cs:870`), and a transfer holds at most 8192 bytes (`DCS/Link/Protocol/Shared/Consts.cs:32`, `DCS/Settings.cs:158`; RRF `SbcMessageFormats.h:26`). DSF MUST keep 8 bytes free in every transfer it builds: every other packet is written only if 8 bytes remain after it (`CanWritePacket`, `SPI.cs:1714-1717`). So a stop always fits in front of a retry, and nothing of the old content moves to another transfer. **Milestone:** link work that does not depend on the epoch: L1 (section 7.2). Acceptance: M112 ≤ 100 ms in 1000 stops under injected noise (AC-4) | — | "RRF processes DSF packets after the stop in the same transfer" is a v7 order: in the iteration that writes the stop, DSF still writes resend copies and file results (`LinkService.cs:434-451`, `:613-627`), object model queries, key changes and messages (`:470-594`), which `InvalidateCodes` does not cancel (V7-94). The v7 contract says DSF writes nothing after the stop (V7-12, X-17); the code writes these non-channel packets. Codes after the stop get "stopped" as in v7 (V7-16) |
| CE-17 | L | **A halt is never lost (Tim, 2026-11-09).** Rejecting unusable data MUST never drop an emergency stop or the halt state of RRF. Facts: an emergency stop that RRF makes by itself sends no packet of its own: `GCodes::DoEmergencyStop` (`RRF/GCodes/GCodes.cpp:1065-1070`) calls `reprap.EmergencyStop()` and `Reset()` and then sends the generic message "Emergency Stop! Reset the controller to continue."; DSF learns of the halt only through the object model (`state.status` "halted", `RRF/Platform/RepRap.cpp:1983-1989`) and that message (V7-17, K20). DSF's own stop counts as done once the transfer that carried it completed and the RX of that transfer was processed (`DCS/Link/LinkService.cs:454-456`, `:599`; `DCS/Link/LinkInterface.cs:345-356`). Rules: **(a)** On SPI, corrupted data (CRC) is retried as a whole transfer and never processed in part: RRF processes only after both data responses were Success (`RRF/SBC/DataTransfer.cpp:1187-1215`), DSF only after `PerformFullTransfer` returned (`SPI.cs:360-373`, `:440`). On USB there is no transfer CRC and no retry (`RRF/SBC/DataTransfer.cpp:1303-1356`): a failed transfer ends the connection on RRF (`RRF/SBC/SbcInterface.cpp:270-288`) and DSF drops its RX (`DCS/Link/Adapter/USB.cs:363-404`). **(b)** A packet that passed the CRC but cannot be used is a protocol fault: it is rejected, counted and reported to the logic as an event (VF-1, VF-2). Today it ends DCS (`SPI.cs:855`, uncaught by `LinkService.cs:446-450`) or drops the rest of the transfer ("Read invalid packet", `:439-443`). Because the halt is a state, not an edge, DSF MUST then read the object model state again. A rejected or mismatched ObjectModel answer MUST NOT leave its query pending: today the key check discards it and leaves the query "sent" (`LinkService.cs:714-723`, `:470-478`), so no query goes out any more and a halt would never arrive (K16, VF-8). **(c)** DSF's own stop is complete only after a transfer that RRF processed. Over USB a failed transfer returns normally, so the halt of the transfer it dropped completes (`USB.cs:363-404` with `LinkService.cs:454-456`, K6, G-2): VF-9. **(d)** Acceptance AC-5. **Milestone:** link work that does not depend on the epoch: L1 (section 7.2), whose gate needs VF-1, VF-8 and VF-9 for (b) and (c) | — | Reading the state again adds a query, which v7 sends at any time; no new order. A halt request that stays pending is a v7 wait (E-21, 4.5 s) |

### 4.7 Peers, integrity, counters, RAM

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-18 | S | **Peers that do not tag.** A fork DSF with a stock RRF, and a stock DSF with a fork RRF, MUST run upstream v7 above the link. The tag is always written, in a field that a stock peer ignores (CE-5). RRF learns no epoch from a stock DSF, so it writes no tag (CE-4), and the `MessageType` bits never reach the wire (CE-11). The fork catch code is deleted from the v7 path once the printers run the epoch RRF (E3, FD-11, old D2 option b): CC-2, CC-3 and CC-6 in DSF, CC-7 and the first-copy role of CC-8 in RRF. CC-4 and CC-5 are no catch code any more from E1: their lines are part of the record and of the routine. **Behaviour change** for such pairs after E3: fork DSF + stock RRF gets back DF-T2 (#134 gone), DF-T11 resend (#161 gone, stock RRF refuses) and DF-T12 (#158 gone); DF-T1 and DF-T10 are stock RRF's anyway. Stock DSF + fork RRF gets back DF-T10 (RRF #46 gone: replies of the code that ran reach DSF). DF-T1 and the RRF part of DF-T12 stay fixed, because the record and the routine do for good what RRF #43 and RRF #45 did (CE-7, CE-25). DF-T2 and the crossing part of DF-T12 are stock DSF's anyway. DF-T11 does not come back, because CE-7 stays in the fork RRF. DF-T3 does not come back: its fix #133 (CC-28) is no epoch catch code and stays. **What a stock DSF gets from a fork RRF from E1, as v7 fixes:** InvalidateChannel is never refused (CE-7); RRF writes no event of the GB between record and apply and voids the pending ones (CE-12), which covers OF-T11 for it; the routine with its behaviour changes (CE-25: locks of an idle GB, DoCode, base-level box, no stale events after a reconnect). The filters, the gate and the answer tags need a DSF that tags | CC-2, CC-3, CC-6, CC-7 and the old role of CC-8 from the v7 path (E3) | v7 behaviour, by definition |
| CE-19 | L | **Integrity (v2 R9), conditional on an EMC test.** Measure corrupted transfers per hour under IEC 61000-4-3 and IEC 61000-4-6, 1 h each, at the levels of EN IEC 61000-6-2, from the checksum counters (RRF `checksumErrors`, `RRF/SBC/DataTransfer.cpp:1167`, `:1221`). At ≤ 100 corrupted transfers per hour CRC32 alone meets R9: about 2⁻³² undetected per corrupted transfer gives ≤ 2.3 × 10⁻⁸ per hour, below 10⁻⁷. Above that, a second, independent per-transfer CRC trailer is added as a v2 link capability. It can sit after the last packet inside the data length: both read loops read only the number of packets the header names (RRF `RRF/SBC/SbcInterface.cpp:314`, `RRF/SBC/DataTransfer.h:233-236`; DSF `DCS/Link/LinkService.cs:434`, `DCS/Link/Adapter/SPI.cs:523`) and stop at the data length (`RRF/SBC/DataTransfer.cpp:618-621`, `SPI.cs:531-534`). Details Q26 | — | Link only |
| CE-20 | S | **Counters for the evidence gates.** Both sides MUST count per channel and request type: stale drops (CE-10, CE-14), drops by the gate (CE-24), drops of tags that follow the channel epoch (CE-5), repeats of InvalidateChannel that the record ignored (RRF, CE-7; with the stale drops of their channel), out-of-order replies that are not stale (DSF, today only logged, `DCS/Link/Channel/Processor.cs:1165`), resends (DSF, today logged, `LinkService.cs:618`) and refusals (RRF, `packetAcknowledged` false, `RRF/SBC/SbcInterface.cpp:1354-1358`), including code refusals and the refusals between record and apply. RRF also keeps the longest time from a record to its apply (CE-7, Q38). M122 shows them on both sides. Counted, not alarmed (v2 R7) | — | Counting changes no order |
| CE-21 | L, S | **RAM.** The RRF part MUST use static fields only: no heap allocation, no side table. A tag per reply-FIFO entry (64 entries, `RRF/Platform/OutputMemory.h:16`) would be a side table against CLAUDE.md rule 3 and is not needed, because the FIFO's own type carries the tag (CE-11). New static RAM MUST stay below 1 KB, checked with the map file (section 6) | — | — |

### 4.8 Logical compatibility (PR9)

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-22 | S | **Refinement.** An epoch pair MUST be a refinement of v7 for the logic of both sides: every order in which a logic layer sees its events MUST also be possible in v7 with the catch code of DSF 7f26d0ca and RRF 0dccb9efe. The epoch MAY remove orders, namely those the catch code removes today (stale replies, requests, closals and acknowledgements across an abort, stale copies of codes and macro answers). It MUST NOT add any. Section 4.10 checks every V7 item; the table below checks the decisions of draft 8 | — | This requirement |
| CE-23 | S | **Wait-for graph.** The v7 graph is [sbc-link-v7-contract.md](sbc-link-v7-contract.md) section 15. The epoch work changes it so: E-16 (a channel waits for the acceptance of InvalidateChannel) is gone (CE-7, CE-13); the SBC task's 10 ms wait for the GB mutex at InvalidateChannel (part of E-42) is gone. Between record and apply the channel's codes wait in RRF's ring, its void events stay unsent, and its packets that need the GB mutex are refused. That is bounded by one main loop pass, whose long waits are the ones that bound C-4 in v7 (E-37, E-38; CE-7). The apply takes the GB mutex on the main task, which is E-40, an existing edge. There is no wait at connect any more (draft 7's hold is gone, FD-37). The end of the old epoch (CE-25) only ends waits: E-25 and E-26 for the channel's requests, E-47 (an RRF GB waits for a DoCode result without a bound), E-48 for a box at the base level and E-49 for an unsent stack event, each for an abort of its channel. The gate (CE-24) adds no wait: it drops packets, and DSF fails the requests it dropped (CE-25). C-1 to C-3 stay as in v7: C-1 and C-2 are K1 and K2, whose reachability tests come in E0 (Q14); C-3 is a logic finding (K3, LA-15). C-4 to C-8 stay, bounded | — | No new cycle: between record and apply neither task waits for the other. The SBC task refuses or skips; the main task reaches the top of its spin by itself |

**PR9 check of the draft-8 decisions** against the v7 wait-for graph (edges `E-n`, cycles `C-n`).

| Decision | New order? | New wait or wait edge? | New cycle? | Bound |
|---|---|---|---|---|
| FD-34 the main task applies | No. Codes of the channel run after the apply, as after an accepted InvalidateChannel in v7. Refusals between record and apply are v7 refusals for a busy GB (V7-11). A trigger kept pending is a v7 order (V7-81) | No edge between the tasks. The record takes no mutex and waits for nothing; the SBC task's wait for the GB mutex at InvalidateChannel (E-42) goes. The apply takes the GB mutex on the main task as `GCodes.cpp:590` does (E-40), and the SBC task does not hold it while an abort is recorded | No. C-4 is broken as in v7: the record ends the macro wait first | Record to apply ≤ one main loop pass. Long passes: E-37 on another channel (≤ 3 s), E-38 (≤ 6 s), an expansion board's answer (1000 ms per request). Longer than in draft 7 when the main task waits on another channel; not measured (Q38) |
| FD-35 nothing written between record and apply | No. It removes orders: no event of the GB follows the abort, towards any DSF | A hold, no edge: the events held are void. DSF waits for none of them, because it popped its levels at its abort (V7-52) | No | The same pass. The apply ends the hold by voiding the events, so nothing is written late |
| FD-36 the apply is the routine of a link loss | No. Each ending is a v7 outcome: a DoCode ended (link loss), a box closed as cancelled (M292 P1), an unsent event that DSF never sees (link loss). Stale events after a reconnect are removed (K5) | None. It ends the logical waits E-47, E-48 and E-49 for the aborted channel. It takes only the GB mutex its caller holds | No | — |
| FD-37 no activation, both sides always tag | No. Tags, the adopt rule and the marker are invisible to the logic. An untagged packet is a v7 packet. The window of Q34 is the v7 order of DF-T2 | Removes the one new wait of draft 7, the hold at connect | No | — |

### 4.9 Message summary

| Dir | v7 request (`RRF/SBC/SbcMessageFormats.h:269-349`) | Tag | Who filters | v7 pairing it replaces in an epoch pair |
|---|---|---|---|---|
| → RRF | 2 Code, 7 MacroCompleted, 18 MacroStarted | epoch of the channel when written; a copy keeps it; the first tagged packet of a connection also carries the marker (CE-4) | RRF adopts the tag if it has no epoch (CE-4), else drops every tag other than the channel epoch (CE-10), and all while the gate is set (CE-24) | copies acting after an abort (A-4) |
| → RRF | 19 InvalidateChannel | new epoch e+1 | never refused, not stopped by the gate; the record ignores a repeat whose tag does not follow (CE-7, CE-24) | #161 (resend tracking); RRF's first-copy guard |
| → RRF | 10 Lock, 11 Unlock, 16 EvaluateExpression, 20 SetVariable, 21 DeleteLocalVariable, 29 SetLastCodeResult | epoch of the channel when written | as Code (CE-10, CE-24); refused between record and apply (CE-7); DSF has failed the requests of an old epoch (CE-25) | — |
| → RRF | 17 Message: the chunks of a DoCode result | tag of the DoCode packet | as Code (CE-10, CE-24) | — |
| → RRF | 0 EmergencyStop, 1 Reset, 3 GetObjectModel, 5 SetPrintFileInfo, 6 PrintStopped, other 17 Messages, file results, 30 ObjectModelKeyChanged, 0x4D50, IAP | none | — | — |
| → DSF | 3 Message with `BinaryCodeReplyFlag` | `codeEpoch` via `MessageType` bits (CE-11) | DSF drops every tag other than the channel epoch (CE-14) | RRF #46 suppression; position windows |
| → DSF | 4 ExecuteMacro, 5 AbortFile, 13 WaitForAcknowledgement, 14 MacroFileClosed, 15 MessageAcknowledged, 12 DoCode, 7 PrintPaused | as the table under CE-12; none while the channel's epoch is unknown; none written between record and apply (CE-12) | DSF drops every tag other than the channel epoch (CE-14); an untagged one goes through the v7 windows until E3 (CE-13) | #134, #158, RRF #43, RRF #45 |
| → DSF | 9 Locked, 11 EvaluationResult, 16 VariableResult | tag of the request | DSF drops every tag other than the channel epoch (CE-14) | — |
| → DSF | 0 ResendPacket (field = packet id), 1 ObjectModel, 2 CodeBufferUpdate, other Messages, file requests | none | never (CE-14) | — |

### 4.10 Alignment with the v7 contract

Every item of [sbc-link-v7-contract.md](sbc-link-v7-contract.md) gets exactly one status for an epoch pair (fork DSF
and fork RRF from E2):
- **identical**: the logic sees the v7 behaviour;
- **changed**: the epoch work changes it; the difference and the reason are given, and the last column says why the
  change only removes orders (PR9, CE-22);
- **removed**: the mechanism is dropped; the reason is given;
- **gap**: a v7 behaviour or defect that the epoch work does not address; the row names the separate v7 fix (`VF-n`),
  the logic-list item (`LA-n`) or the open question (`Qn`) that takes it.

Counts (draft 8): 44 identical, 36 changed, 3 removed, 18 gap (draft 7 and 6: 43, 35, 3, 20; draft 5: 49, 25, 3, 24).
Draft 8 moved five items: V7-3 and V7-101 are identical again (no hold at connect, no capability); V7-81 (triggers),
V7-95 and V7-96 (link loss, stale flags) are changed now. A script checked that every V7-1 … V7-101 has exactly one
row (a range such as "V7-74 … V7-79" counts for each item) and that every row that is not identical has a reason, a
filled PR9 column and a requirement or target. Every change only removes orders. No change adds a wait; draft 7 had
one, the hold at connect. Three changes alter what the logic sees, within orders that v7 already has, and since
draft 8 for every DSF: the abort releases the locks of an idle GB (V7-53, V7-58), ends a DoCode (V7-70) and closes a
message box left at the base level (V7-49).

| V7 | Status | Draft 8 | Difference | Reason | Only removes orders (PR9) |
|---|---|---|---|---|---|
| V7-1 pipeline depth one | identical | — | the depth stays; the abort windows no longer depend on it (V7-55, V7-56) | — | — |
| V7-2 processing in the writer's order | identical | — | filters drop packets before processing; the order is unchanged | — | — |
| V7-3 DSF's TX order | identical | — | nothing is held at connect: draft 7's hold (CE-3) is removed (FD-37); the tag rides in the header | — | — |
| V7-4 RRF's TX order | identical | — | the tag rides in the header | — | — |
| V7-5 pacing | identical | — | — | — | — |
| V7-6 unknown and invalid packets | gap | VF-1; Q21 (K4: E0 test) | DSF rejects and counts instead of ignoring or stopping; RRF's end of the read loop at ≥ 32 stays (K4) | the epoch adds no packet type (CE-5), so it does not meet X-18 | n/a here; VF-1 only removes a DCS stop |
| V7-7 packet ids, resend lookup | gap | VF-1; CE-16 | the `ArgumentException` that ends DCS (`SPI.cs:855`) and NEW-1 on USB are fixed separately; a stop in front of a retry keeps the lookup working (renumbered or order-independent, CE-16) | not an epoch topic (FD-13) | n/a here; VF-1 only removes a DCS stop |
| V7-8 refusal | changed | CE-8 | a copy keeps the tag of the original, without the first marker | without it, a copy would look untagged and pass the filters | yes: same copies, same order |
| V7-9 SPI resend guard | identical | — | CC-26 stays | — | — |
| V7-10 a refused packet is overtaken | changed | CE-10 | a copy of a Code, MacroStarted or MacroCompleted that an abort overtook is dropped | in v7 such a copy runs or acts after the abort | yes: removes "a cancelled code runs after the abort"; NEW-3 and K27 stay (EG-3; K27 an E0 test, Q21) |
| V7-11 why RRF refuses | changed | CE-7 | InvalidateChannel is never refused: the SBC task records it without the GB mutex, and a repeat that a repeated transfer delivers is ignored by its tag. One refusal reason is added: between record and apply a packet that needs the GB mutex is refused, as for a busy mutex. The other refusals stay | DF-T11 (CC-6); FD-28, FD-34 | yes: removes the resend of InvalidateChannel and a second action of a repeat; a refusal for a busy GB is a v7 refusal, here bounded by one main loop pass |
| V7-12 EmergencyStop | changed | CE-16 (L1) | a pending stop goes first into the next new transfer, a retry ahead of its unchanged content included, with room kept for it and the resend lookup intact; a SequenceGuard repeat stays a pure repeat | stop path worst case about 503 ms (A-6); RRF does not read a repeat (FD-15) | yes: in v7 RRF already processes DSF packets after the stop in the same transfer (CE-16) |
| V7-13 halt completion | gap | VF-9; CE-17 (c) | over USB a failed transfer must not complete the halt | K6, G-2 | n/a here; VF-9 keeps the halt pending, a v7 wait (E-21) |
| V7-14 code buffer after a stop | gap | Q21 (K12: E0 test) | — | K12 is not an epoch topic; reported per FD-13 | n/a here |
| V7-15 Reset | identical | — | — | — | — |
| V7-16 M112 is DSF's | identical | LA-14 | — | — | — |
| V7-17 RRF-local stops | gap | CE-17 (b); Q27; LA-7 | DSF learns the halt from the object model, also after rejected data; STOPPED only after an E0 test shows K20 (FD-23); an RRF-local stop raises no gate (CE-24) | K20, X-5; Tim's halt rule | n/a here; reading the state again adds no order |
| V7-18 firmware update | identical | — | — | — | — |
| V7-19 when DSF writes a Code | changed | CE-13 | the block by an unconfirmed abort is skipped once RRF has shown a tag, and deleted in E3; codes after an abort go out at once and wait in RRF's ring for the apply | CC-6 | yes: RRF runs them only after the apply, as v7 runs them only after the accepted abort |
| V7-20 code admission | gap | Q21 (K24: E0 test) | — | K24 is not an epoch topic | n/a here |
| V7-21 flow control by estimate | identical | — | credit dropped (FD-1); CC-28 … CC-30 stay; EG-3 watches it | — | — |
| V7-22 read-ahead drop (B7) | identical | — | CC-21 stays (Q5) | — | — |
| V7-23 RRF holds codes while a stack event is unsent | changed | CE-7, CE-25 | `FillBuffer` also holds the channel's codes while an abort is recorded and not applied; the apply clears the unsent-event flags that hold codes today | the main task applies the abort at the top of its next spin and could fetch a code before that (FD-34) | yes: the same hold v7 has through #161, on the other side |
| V7-24 per-channel FIFO | identical | — | — | — | — |
| V7-25 comment codes get no reply | identical | — | — | — | — |
| V7-26 one final reply per code | changed | CE-11, CE-14 | replies of another epoch, older or ahead, are dropped and counted; matching by position stays within an epoch; a level id only on evidence (EG-6) | DF-T10, OF-T10 leftovers | yes: removes "a stale reply completes the next code" |
| V7-27 reply routing in DSF | identical | — | the `MessageType` bits of the tag never reach the wire | — | — |
| V7-28 one reply FIFO | changed | CE-11 | each entry's type carries the epoch of its code; entries of different epochs never merge; NEW-2 stays (Q2; an E0 test, Q21) | the tag must be taken when the reply is produced | yes: no order change; a merge across epochs is removed |
| V7-29 suppressed replies | changed | CE-11, CE-14 | `codeReplySurplus` is replaced by DSF's filter; the M99 suppression stays; replies outside `HandleReply` (M122, `GpInPort`, SD speed test) get the tag too | CC-7; OF-T10 writers outside `HandleReply` | yes: the same replies are dropped, plus those #46 misses |
| V7-30 partial replies survive invalidation | changed | CE-13 | the partial reply of a channel is discarded when its epoch changes or is reset; the emergency stop path keeps K11 (an E0 test, Q21) | G-8 for aborts | yes: removes a stale prefix |
| V7-31 reply and stack event in both orders | identical | — | — | — | — |
| V7-32 drains | identical | — | — | — | — |
| V7-33 completion for clients | identical | — | — | — | — |
| V7-34 blocking macro request | changed | CE-12, CE-14 | the request carries the epoch of its code, or the channel epoch if no code made it; RRF writes none between record and apply and none of a code of an older epoch, and ends such a wait at once with an error; DSF drops one of another epoch | CC-2 (#134), CC-5 (RRF #43); K17 stays (an E0 test, Q21; EG-1) | yes: a failed macro request is a v7 outcome |
| V7-35 several macros of one code | identical | — | — | — | — |
| V7-36 system macros on an empty stack | gap | Q21 (K9: E0 test) | — | K9 is not an epoch topic | n/a here |
| V7-37 macro that cannot be opened | identical | — | — | — | — |
| V7-38 macro start, implicit start | changed | CE-10; VF-3 | a stale Code or MacroStarted no longer resolves a macro wait; that a current Code resolves it before its refusal is VF-3 | DF-T11 family | yes |
| V7-39 daemon.g | changed | CE-9, CE-12, CE-25 | the daemon request carries the channel epoch at its write (FD-36); none is written between record and apply; the routine voids an unsent one together with its level | A5 (OF-T11) | yes |
| V7-40 macro wait timeout | gap | LA-8 | whether RRF requests an abort after its timeout is logic, open; K23 stays | FD-8 | n/a here; if decided, "channel aborted after a macro request" is a v7 order |
| V7-41 MacroCompleted | changed | CE-10 | a stale copy is dropped before it acts; X-7 stays (Q15) | a copy of an older epoch belongs to a level that no longer exists | yes |
| V7-42 MacroFileClosed | changed | CE-12, CE-14, CE-25 | tagged with the channel epoch at its write; a pending one is voided by the abort and by a link loss; DSF drops one of another epoch; replaces #158; fixes the old-session case K5 | CC-3, CC-4; K5 (X-12) | yes |
| V7-43 DSF's macro reader | identical | — | — | — | — |
| V7-44 M98 | identical | — | — | — | — |
| V7-45 single AbortFile, M99 | changed | CE-12, CE-14, CE-25 | a single abort of an older epoch is dropped; a pending one is voided by the abort; the M99 catch code CC-9 … CC-12 stays; a level id only on evidence (EG-6) | the epoch ties it to the level it was meant for | yes |
| V7-46 AbortFile all | changed | CE-15, CE-14, CE-24, CE-12 | it carries the channel epoch at its write (CE-9); DSF answers a tagged one of the current epoch with InvalidateChannel(e+1); one of another epoch is dropped; an untagged one is handled as in v7; until InvalidateChannel(e+1) arrives RRF drops the channel's DSF packets (gate); one that is unsent when an InvalidateChannel is recorded is never written | K18, G-20; the gain of the rejected RRF epoch (FD-20) | yes: "InvalidateChannel after RRF's abort-all" is a v7 order; the gate drops packets that act after RRF's abort in v7; no wait |
| V7-47 stack depth | identical | — | — | — | — |
| V7-48 WaitForAcknowledgement | changed | CE-12, CE-14, CE-25 | one of another epoch is dropped; an unsent one is voided by the abort; G-14 (K15) stays (an E0 test, Q21); a box that a surviving code opens after the abort passes, as in v7 (Q35) | stale box of an aborted level | yes |
| V7-49 MessageAcknowledged | changed | CE-9, CE-12, CE-14, CE-25 | it carries the channel epoch at its write, without a stored epoch; one of another epoch is dropped; the abort closes a box left at the base level and writes no MessageAcknowledged for it (behaviour change, for every DSF) | in v7 it pops whatever level is current; the base-level box of OF-T10; FD-36 | yes: a box closed as cancelled is a v7 outcome (M292 P1), and DSF has popped the box level at its abort |
| V7-50 cancel with abort | identical | — | — | — | — |
| V7-51 M291 blocks only lower levels | identical | — | — | — | — |
| V7-52 DSF side of an abort | changed | CE-13, CE-25 | DSF increments the epoch when it writes InvalidateChannel; the payload is unchanged; there it fails the channel's evaluation, variable and code-result requests and its DoCodes of the old epoch; G-21 (K19) stays (an E0 test, Q21) | CC-1's ambiguity becomes an identity; CC-1's code stays (`_allFilesAborted`, `FileAbortSent`) | yes |
| V7-53 RRF side of InvalidateChannel | changed | CE-7, CE-12, CE-25 | the SBC task records without the mutex (epoch, ring purge, end of a macro wait) and ignores a repeat; the main task applies at the top of its next spin with the routine of a link loss: unsent macro request and stack events voided, DoCode ended, files aborted, a base-level box closed, locks of an idle GB released (behaviour changes, for every DSF); nothing of the GB is written in between; requests of a code of the older epoch are not sent | CC-4, CC-5, CC-8; FD-17, FD-18 (K13), FD-27, FD-28, FD-34, FD-35, FD-36; OF-T10, OF-T11 | yes: v7 already acts before the mutex; each ending is a v7 outcome (CE-25); the delay to the apply is bounded by one main loop pass; a voided request or event is one DSF never sees |
| V7-54 refused InvalidateChannel | removed | CE-7 | — | never refused | yes |
| V7-55 abort window 1 | removed | CE-14 | — | CC-2 (#134): replaced by the filter | yes |
| V7-56 abort window 2 | removed | CE-7, CE-13 | — | CC-6 (#161): nothing to wait for | yes: removes E-16 |
| V7-57 Lock | changed | CE-7, CE-10, CE-14, CE-25; VF-5; LA-9 | a stale Lock copy and a stale Locked are dropped; a Lock between record and apply is refused; the abort releases what a Lock took if the GB is idle; Lock as a pending request stays VF-5, the Unlock for a Locked without request LA-9 | K14 (G-13), its abort case; FD-16, FD-17, FD-24, FD-27 | yes: removes "the Locked of an aborted request resolves a new one"; the release on an idle GB equals an Unlock (CE-25); VF-5 keeps the Locked wait (E-15) |
| V7-58 Unlock | changed | CE-10, CE-7, CE-25 | a stale Unlock copy is dropped; the abort releases the locks of an idle GB, so a dropped Unlock leaves none there; with a code of the GB running, the lock stays until that code ends, as in v7 (K28) | FD-17: without the release a dropped stale Unlock would leave the movement lock held until the GB's next code ends; FD-27: no release while a code runs | yes: the release equals an Unlock that RRF processes right after the abort, on an idle GB only (CE-25) |
| V7-59 EvaluateExpression | changed | CE-10, CE-14, CE-25 | a stale request is dropped without answer; DSF fails the channel's requests at the epoch change; a stale EvaluationResult is dropped | FD-16 (Q25) | yes: a failed evaluation is a v7 outcome (`InvalidateCodes`); the answer filter keeps a stale result from resolving a request of the new epoch |
| V7-60 SetVariable, DeleteLocalVariable | changed | CE-10, CE-14, CE-25; VF-4; Q21 (K27) | stale copies are dropped; DSF fails the channel's variable requests at the epoch change; a stale VariableResult is dropped; no assignment before a refusal is VF-4; K27 an E0 test | FD-16 (Q25); K21; K27 | yes: a failed variable request is a v7 outcome (`InvalidateCodes`) |
| V7-61 variables only after a flush | gap | LA-16 | — | K25 is a logic finding | n/a here |
| V7-62 SetLastCodeResult | changed | CE-10, CE-25; LA-17; Q21 (K10) | a stale copy is dropped; unwritten requests of the channel fail at the epoch change; K26 is logic (LA-17); K10 an E0 test | FD-16 (Q25) | yes: a cancelled SetLastCodeResult is a v7 outcome (`InvalidateCodes`, `DCS/Link/LinkInterface.cs:616-623`) |
| V7-63 object model | gap | VF-8; CE-17 (b) | a rejected or mismatched answer must not leave its query pending; VF-8 is no prerequisite of E2 any more, because nothing of the epoch waits for a query (FD-37) | K16, G-18; a halt would never arrive | n/a here; VF-8 removes a permanent block |
| V7-64 logic waits for the OM | identical | — | — | — | — |
| V7-65 SetPrintFileInfo | gap | Q14 (K1, K2); Q11 (G-19) | the waits under the job lock stay | completion on acceptance was dropped with v8 | n/a here |
| V7-66 ObjectModelKeyChanged | identical | — | OF-S3 stays a separate fix | — | — |
| V7-67 print end | gap | LA-12 | DSF writes InvalidateChannel on File and File2 before PrintStopped(abort) | logic (decided by Tim) | n/a here; LA-12 orders an abort before the job cancel, which an IPC abort can do in v7 |
| V7-68 PrintPaused | changed | CE-6, CE-9, CE-12, CE-14, CE-25; LA-13 | it carries File's epoch at its write; it is not written while File or File2 has an abort recorded, and the abort of either voids a pending one; DSF drops a stale one; the pause mechanism stays v7 (FD-7) | FD-36; DSF aborts the job on an abort of either file channel | yes: removes "a pause raised before a job abort pauses DSF after it" |
| V7-69 Message to RRF | changed | CE-6, CE-10 | the chunks of a DoCode result carry the DoCode's epoch, and RRF drops stale ones; other messages stay untagged | FD-18 | yes: removes "a stale result finishes a newer DoCode or is output after the abort" |
| V7-70 DoCode | changed | CE-6, CE-12, CE-14, CE-25; LA-10 | DoCode carries the channel epoch at its write; none is written between record and apply; the routine ends a DoCode, sent or not, for every DSF (behaviour change); an epoch DSF fails a DoCode of the old epoch with an error and does not run a stale one | FD-18, FD-36; K13 (G-12) | yes: both endings are v7 outcomes (RRF on a link loss, DSF's cancelled DoCode, G-12); removes the unbounded wait E-47 for aborts |
| V7-71 RRF's file I/O | gap | Q21 (K22, K9: E0 tests); EG-4 | — | request ids only on evidence (FD-1) | n/a here |
| V7-72 stack mirror by position | identical | — | identity only on evidence (FD-3, EG-1); a level id only on evidence (FD-21, EG-6) | — | — |
| V7-73 pop on the base level is fatal | changed | CE-14, CE-25 | its abort source (T12) is filtered by the epoch, and RRF voids a pending closal at the abort; its reconnect source (K5) is removed by the routine (E1) | CC-3; K5 | yes |
| V7-74 … V7-79 level routing, start code, pop effects, flush, prioritized codes, job read-ahead | identical | — | — | DSF and RRF logic | — |
| V7-80 M606 S1 | gap | LA-15 | — | C-3 (K3) is a logic finding | n/a here |
| V7-81 triggers | changed | CE-7; LA-19 | a recorded abort of the Trigger channel counts as busy, so a trigger stays pending until the apply; a trigger whose request an abort ends is lost as in v7, and so is one that fires while the link is down (LA-19) | FD-34: no new work on a GB between record and apply; the safety review's finding stays logic and safety work | yes: "a trigger waits while the Trigger channel is busy" is a v7 order; here bounded by one main loop pass |
| V7-82 no cross-channel order | identical | — | — | — | — |
| V7-83 the link thread takes logic locks | gap | Q14 (K1, K2); LA-15 (C-3) | — | completion on acceptance was dropped with v8 | n/a here |
| V7-84 completion never blocks the link thread | identical | — | — | — | — |
| V7-85 DSF waits without timeout | changed | CE-25 | an epoch change also ends DSF's waits for the channel's evaluations, variables and code results of the old epoch | FD-16: RRF drops stale requests, so nothing may wait for their answers | yes: it only ends waits, as `InvalidateCodes` does |
| V7-86 … V7-87 RRF task waits | identical | — | InvalidateChannel takes no GB mutex any more (V7-11); the main task takes the GB mutex also for the apply, at the top of its spin (CE-7) | — | — |
| V7-88 RRF logical waits | changed | CE-25, CE-24 | an abort of the channel ends a GB's wait for a DoCode result, for the acknowledgement of a base-level box and for the SBC task to write a stack event; while the gate is set the GB gets no DSF packet of its channel | FD-18, FD-20, FD-36 | yes: ends waits; the gate drops packets that act after RRF's own abort in v7 |
| V7-89 link-level bounds | identical | — | the retry limit of 10 stays (`DCS/Settings.cs:193`, FD-10) | — | — |
| V7-90 fatal errors in DSF's packet loop | gap | VF-1 | — | DCS exits on link data | n/a here; VF-1 only removes a DCS stop |
| V7-91 SPI reconnect | gap | VF-7 | — | K7, G-3: the epoch does not tell sessions apart; both sides forget it with the connection (CE-4) | n/a here |
| V7-92 DSF's SPI reset detection | identical | — | no resume (FD-10) | — | — |
| V7-93 USB failure | gap | VF-9; CE-17 (c) | — | K6, G-2 | n/a here; VF-9 keeps the halt pending |
| V7-94 DSF invalidation | changed | CE-4, CE-13 | `Invalidate` after a connection change resets the epochs and the partial replies and makes the next tagged packet carry the first marker; `InvalidateCodes` (stop) keeps the epochs | RRF keeps its epochs across a stop | yes: not visible to the logic beyond K11 for aborts |
| V7-95 RRF on a link loss | changed | CE-25, CE-4 | `InvalidateResources` runs the routine that the abort shares: every unsent event of the old session is voided, an unsent DoCode is ended, boxes are cancelled without the abort flag (the print is stopped by `printAborted` and the emergency stop as before), the locks of an idle GB are released; the epochs are forgotten | FD-36: one routine for both; K5 | yes: removes the stale events of V7-96; the rest is not visible, because RRF is halted after a link loss |
| V7-96 stale RRF flags across a reconnect | changed | CE-25 | the routine clears `macroFileClosed`, `messagePromptPending`, `messageAcknowledged`, `invalidated` and `reportPause` and ends an unsent DoCode; `InvalidateResources` clears `codeReplySurplus` until E3; fixed in E1 with its own test (AC-21) | K5, X-12; FD-36 (was VF-6) | yes: removes events only |
| V7-97 what RRF assumes after a reconnect | identical | — | — | — | — |
| V7-98 protocol versions | identical | — | no new version (FD-1) | — | — |
| V7-99 protocol < 3 | identical | — | — | — | — |
| V7-100 `UpdateOnly` | identical | — | — | — | — |
| V7-101 link capabilities | identical | — | the epoch is no link capability (FD-37): no fourth capability, nothing to activate, nothing on USB; PhaseGuard, SequenceGuard and ExtendedHeader stay as they are | — | — |

## 5. Separate v7 work

Each item is its own PR with unit tests in fork-only files under `src/UnitTests` (CLAUDE.md rule 7). None is part of
the channel epoch; each works without it. Per the fork's rule "every bug found is fixed". VF-6 (stale flags after a
reconnect) moved into CE-25 in draft 8 (section 10.7).

| ID | Layer | Fix | Source | Where |
|---|---|---|---|---|
| VF-1 | L | **DCS never exits on link data.** Unusable data is rejected and counted, never an exception that ends DCS, never a silent drop of the rest of a transfer without a report. Today: a resend request for an unknown packet id throws `ArgumentException` (`DCS/Link/Adapter/SPI.cs:855`, `USB.cs:746`), which the packet loop does not catch (it catches only `ArgumentOutOfRangeException`, dumps and rethrows it, `DCS/Link/LinkService.cs:446-450`); an invalid format code, a newer protocol version or a too long data length throws (`SPI.cs:1955-1957`, `:1960-1965`, `:1972-1977`); "Read invalid packet" drops the rest of the transfer (`LinkService.cs:439-443`). Includes NEW-1: over USB the resend looks one TX back (`USB.cs:717-721`), while RRF composes its USB answer one transfer behind (`RRF/SBC/DataTransfer.cpp:1303-1356`, `:1365-1367`). After a rejection DSF reads the object model state again (CE-17 (b)) | F-6, F-7 of draft 4; OF-S6; K4 (DSF side) | DSF |
| VF-2 | L | **Link events and health state.** The link reports to the logic of both sides LinkDown, LinkRestored, ConnectionLost and PeerRestarted, and a protocol fault per rejected packet (VF-1); it offers a health state ok, degraded or down with counters (retries, resyncs, rejected data, checksum errors). The logic decides what to do (LA-6). Constraint: the extended-link session status is the constant 0x7FF (`DCS/Link/Protocol/Shared/ExtendedLink.cs:65`), not a restart counter, so it cannot tell a restart from a connection reset (Q29) | v2 R7, R11; layering rule | both |
| VF-3 | S | **A Code resolves a macro wait only once accepted.** Today it resolves the wait (`RRF/SBC/SbcInterface.cpp:383-392`) before it can be refused for the cascade (`:395-399`) or for ring room (`:405-414`) | V7-38, B8, A-4 | RRF |
| VF-4 | S | **SetVariable assigns only once it can answer.** Today it evaluates and assigns, then refuses if its VariableResult does not fit, so the copy of a `var` or `global` fails with "already exists" | K21, X-6 | RRF |
| VF-5 | S | **Lock as a pending request.** RRF accepts Lock (10; `LockMovementAndWaitForStandstill` in RRF, `LockAllMovementSystemsAndWaitForStandstill` in DSF) and keeps it pending per GB, tries again at every exchange under the GB mutex (`RRF/SBC/SbcInterface.cpp:606-634`) and sends Locked once it succeeds; DSF no longer polls by resend (`DCS/Link/LinkService.cs:615-619`, `SPI.cs:841-845`). An abort or a connection change cancels it and releases what it holds (`UnlockAll`, `RRF/GCodes/GCodes.cpp:5648-5668`); the routine of CE-25 does that from E1 if the GB is idle, and it then has to drop the pending Lock as well. Towards a DSF that tags, the pending Lock MUST keep the tag of its request for its Locked (CE-6), one byte per GB (section 6) | K14, G-13; CC-31 | both |
| VF-7 | L | **No TX of the old session in the new one.** After a lost connection DSF exchanges the pending TX again (`DCS/Link/Adapter/SPI.cs:442-477`), and RRF processes it if its number follows (`RRF/SBC/DataTransfer.cpp:1396-1443`) | K7, G-3 | both |
| VF-8 | S | **A mismatched ObjectModel answer does not block the queries.** The key check discards it but leaves the query "sent" (`DCS/Link/LinkService.cs:714-723`, `:470-478`); the query MUST be sent again or failed, so updates and the halt state keep coming (CE-17 (b)). A prerequisite of L1 (CE-17). No prerequisite of E2 any more: nothing of the epoch waits for a query (FD-37) | K16, G-18 | DSF |
| VF-9 | L | **A failed USB transfer does not complete the halt.** The halt request stays pending and is written again after the reconnect (CE-17 (c); `DCS/Link/Adapter/USB.cs:363-404`, `DCS/Link/LinkService.cs:454-456`, `:599`) | K6, G-2 | DSF |

Not kept (FD-13): time-based retries instead of the retry count (old V8-43); a systemd watchdog for DCS. The safety
review's point that DCS has none is an open item of the logic list (LA-20), not decided.

## 6. Firmware RAM budget

Measured basis: the M122 of 2026-11-09 above. "Never used 95868" is `heapLimit − heapTop`
(`RRF/Platform/Tasks.cpp:300-314`, `:369-370`, as read for draft 4).

| Item | Bytes | Requirement |
|---|---|---|
| Channel epoch per GB: epoch 6 bits, "known" 1 bit, gate 1 bit (1 B × 15 GBs, `RRF/GCodes/GCodeChannel.h:20`) | 15 | CE-4, CE-24 |
| `codeEpoch` per GB (valid bit + 6 bits, 1 B × 15) | 15 | CE-9 |
| Ring stamp in `BufferedCodeHeader.padding` (exists) | 0 | CE-9 |
| `abortRecorded`: the byte of `invalidationPending` (exists, renamed) | 0 | CE-7 |
| Request bit in `requestsFromOtherTasks` (exists, `RRF/GCodes/GCodes.cpp:76`) | 0 | CE-7 |
| Reply tag in the FIFO's `MessageType` (exists, `RRF/Platform/OutputMemory.h:179`) | 0 | CE-11 |
| Epochs kept with events (draft 7: 16 B for the box timeout and PrintPaused) | 0 | CE-9: removed |
| Capability bit (draft 7) | 0 | removed (FD-37) |
| Answer tags (Locked, EvaluationResult, VariableResult: the request's tag, passed as a parameter). This holds while each answer is written inside the handler of its request: Locked `RRF/SBC/SbcInterface.cpp:607-634`, EvaluationResult `:682-760`, VariableResult `:928-1109` | 0 | CE-6 |
| Tag of a pending Lock once VF-5 makes Lock a pending request, written later than its request was read (1 B × 15) | 15 | CE-6, VF-5 |
| **Epoch state** | **45** with VF-5, 30 without (draft 7: ≤ 76, draft 6: 61, draft 5: 30) | |
| Removed in E3: `codeReplySurplus` (bool × 15, `GCodeBuffer.h:405`) | −15 | CE-7, CE-18 |
| Counters: stale drops per channel (u16 × 15) | 30 | CE-20 |
| Counters: gate drops per channel (u16 × 15) and tags that follow the epoch (u16) | 32 | CE-20 |
| Counters: refusals per request type (u16 × 32) | 64 | CE-20 |
| Longest time from record to apply (u16, ms) | 2 | CE-20 |
| **Total after E3** | **≈ 158** with VF-5 (draft 7: 187, draft 6: 172, draft 5: 109) | 0.16 % of 95,868; CE-21 |

Not measured: padding inside `GCodeBuffer` (the new bytes may fit into existing padding), flash, CPU. Whether static
growth reduces "never used" byte for byte is not checked.

## 7. Migration, milestones, evidence gates

### 7.1 Coexistence

| DSF | RRF | Result |
|---|---|---|
| fork from E2 | fork from E2, any board, SPI or USB | epoch pair: tags, filters, gate. No negotiation: RRF learns each channel's epoch from DSF's first tagged packet (CE-4) |
| fork from E2 | stock or older fork | v7; RRF ignores the tags; until E3 with the fork catch code, after E3 upstream v7 (CE-18) |
| stock | fork from E1 | v7 with record, apply and the routine (CE-7, CE-12, CE-25); RRF writes no tag; until E3 with RRF #46, after E3 without (CE-18) |
| stock | stock | v7 as upstream |

### 7.2 Milestones

| Milestone | Content | Catch code no longer needed (catalogue `CC-n`) | Gate |
|---|---|---|---|
| E0 preparation | Fake firmware (`src/UnitTests/FakeFirmware/FakeFirmware.cs`): record and apply as two steps with a settable delay; tags with the adopt rule and the marker; a stock mode that neither tags nor filters. **To test first, before E1:** (1) that no v7 DSF relies on a lock surviving an InvalidateChannel (FD-17; the only DSF user is M505, `DCS/Codes/Handlers/MCodeHandler.cs:957-990`); (2) stock-mode tests of a DoCode and of a base-level message box across an abort, which first record today's behaviour (AC-18); (3) the K5 test, which first shows the fault (AC-21); (4) that a stock peer is not disturbed by tags: a tagging DSF against the fake firmware in stock mode, and today's RRF in the host harness against tagged input. **Before E2:** restarts of either side on SPI and USB, noticed and unnoticed (AC-19); the window of Q34. **Also:** the counters of CE-20 on both sides, also for untagged traffic as a baseline; K1/K2 reachability tests (Q14); tests of Q9 and Q15; a fake-firmware or harness test for each remaining v7 defect (FD-22: K4 RRF side, K9, K10, K11, K12, K15, K17, K19, K22, K24, K27, NEW-2); a test for K20 (FD-23) | none | existing suites green; the E0 tests written; what a test proves is fixed and reported per FD-13 |
| E1 | RRF, for every DSF, no tag needed: InvalidateChannel is recorded by the SBC task and never refused; the main task applies it; nothing of the GB is written in between (CE-7, CE-12); the apply is the routine that `InvalidateResources` shares, which is also the fix of K5 (CE-25). An untagged InvalidateChannel always acts. v7 fixes | CC-6 (#161) where RRF has E1; CC-8's first-copy role; CC-4 and CC-5 become part of the record and the routine | the E0 tests (1) to (3) green; lab `AbortsCrossingMacroRequests` 2000 aborts: 0 "Resending packet" for InvalidateChannel, 0 blocks; the longest record-to-apply time read from its counter (Q38); AC-13, AC-16, AC-17, AC-18, AC-20, AC-21 |
| E2 | Tags, filters, gate: CE-4 … CE-6, CE-8 … CE-11, CE-13 … CE-15, CE-20, CE-24, the tag rules of CE-7 and CE-12 (a repeated InvalidateChannel is ignored, a request of a code of an older epoch is not written), and DSF's part of CE-25 (the hook for its requests, the end of a DoCode). No activation and no prerequisite fix. The stop rule moved to L1 (FD-30) | towards an RRF that tags: CC-2, CC-3, CC-7 (with CC-6, CC-8 from E1). CC-1 is upstream design: its ambiguity is resolved, its code stays (`_allFilesAborted`, `FileAbortSent`, CE-25) | lab 2000 aborts with 0 out-of-order replies, 0 resends, 0 blocks; the fake-firmware abort tests green with the fork catch code switched off for tagged packets (AC-1, AC-2); AC-12, AC-14, AC-15, AC-19; then shown to Christian Hammacher (FD-14) |
| E3 | Delete the fork catch code from the v7 path of both sides once the printers run the epoch RRF (CE-18): CC-2, CC-3, CC-6 in DSF; `codeReplySurplus` (CC-7) and what is left of CC-8 in RRF | CC-2, CC-3, CC-6, CC-7, CC-8 | the coexistence cases of 7.1 behave as stated (AC-7) |
| L1 | Stop and halt (link): CE-16, the stop overtakes retries and never goes into a SequenceGuard repeat, with the resend lookup kept and room for the stop; CE-17, a halt is never lost. Its own PR; independent of the epoch and of E0 to E3. Prerequisites: VF-1, VF-8, VF-9 (CE-17 (b), (c)) | none: CC-37, CC-38 and CC-40 (stop path) stay | AC-4: M112 ≤ 100 ms over 1000 stops under injected noise, on the plain and the extended link; AC-5: the halt tests |
| then | Evidence gates (7.3) | — | — |

The decision text said "CC-1 … CC-8 deleted". The code says otherwise for three of them. CC-1: without `FileAbortSent`
(`RRF/SBC/SbcInterface.cpp:903`) RRF would answer DSF's own abort with AbortFile(all), which DSF answers with another
InvalidateChannel (CE-15), a loop. So CC-1 stays. CC-4 and CC-5: `MacroFileClosedSent` (`:904`) and `MacroRequestSent`
(`:893`) are what "no pending event survives an abort" needs, so since draft 8 they are lines of the routine and of
the record, not catch code (CE-7, CE-25). The separate v7 fixes (section 5) are independent of the milestones, except
that L1 needs VF-1, VF-8 and VF-9 (CE-17).

Not removed: CC-1 (code kept, see above), CC-4 and CC-5 (now lines of the routine and the record), CC-9 … CC-12 (M99
family), CC-13, CC-14/CC-15 (pause, FD-7), CC-16 … CC-20 and CC-22 … CC-24 (matching by position, FD-3), CC-21 (B7,
Q5), CC-25 … CC-27 (resend lookup; CC-27 is NEW-1, fixed by VF-1), CC-28 … CC-30 (buffer estimate), CC-31 (VF-5),
CC-32 … CC-35 (correlation of global requests), CC-36 (activation of the link capabilities, which the epoch does not
use), CC-37, CC-38, CC-40 (stop path), CC-39, CC-41 … CC-47 (link and RRF-internal).

### 7.3 Evidence gates

The counters of CE-20 run on both sides in this run set: 3 × 2000 aborts (`AbortsCrossingMacroRequests`,
`src/LabPrinterTests/AbortsCrossingMacroRequests.cs`), `TriggerFuzz` (`src/UnitTests/Fuzz/TriggerFuzz.cs`),
`CodeBufferReserve` (`src/LabPrinterTests/CodeBufferReserve.cs`) and 10 full prints. A counter above 0 together with
a symptom triggers the next step, each as a capability of its own:

| ID | Counter and symptom | Next step |
|---|---|---|
| EG-1 | An out-of-order reply that is not stale | Code identity (old V8-14) |
| EG-2 | A resend other than Lock | Per-channel stall (old V8-6) |
| EG-3 | A code refused within DSF's estimate | Absolute credit (old V8-15) |
| EG-4 | An object model or file mismatch | Request ids (old V8-13) |
| EG-5 | K18 seen (a reply after RRF's own abort that the epoch did not drop) | A RESET request (old V8-12) |
| EG-6 | After E2, EG-1 shows a cross-level misattribution: a reply or stack event that is not stale and belongs to another macro level of the same epoch (the late-M99 class, fork fix #44) | Level id from the per-channel epoch counter, under the conditions of FD-21 (Q30) |

## 8. Verification and acceptance

- **Unit tests** in fork-only files under `src/UnitTests`: the fake firmware implements the RRF side of section 4
  (record and apply as two steps, the routine, tags with the adopt rule) and keeps a stock mode. Every requirement
  gets a test. Every existing test of a deleted catch code keeps running against the tagging fake firmware; those
  tests are the regression set.
- **RRF host harness** (`firmware/RepRapFirmware/Tools/sbc-harness`): the `PortMatchesRealFirmware`-style replay
  (`src/UnitTests/SPI/LinkRecovery.cs`) covers CE-16 with SequenceGuard repeats and the resend lookup of a retry
  that carries a stop. The harness also runs RRF's side of E1: record, apply, the routine and K5 (AC-20, AC-21).
- **Fuzzing** (`src/UnitTests/Fuzz`): `TriggerFuzz` and `StopPathFuzz` with aborts, refusals and RRF restarts.
  Invariants: every code completes exactly once; no reply reaches another code; no channel stays blocked; no DCS stop.
- **Lab** (`src/LabPrinterTests`, procedure LT): the run set of 7.3, M112 under load, the EMC test of CE-19.

| Acceptance | Pass when |
|---|---|
| AC-1 | 3 × 2000 aborts with an epoch pair: 0 blocks, 0 "Out-of-order reply", 0 "Resending packet", stale drops only in the counters |
| AC-2 | The fake-firmware abort tests (`TriggerFuzz` abort cases, `TriggerMacros.TriggerChannelWorksAfterInvalidationDuringTriggerRequest`, the DF-T1, T2, T10, T11, T12 tests) green against the tagging fake firmware with the fork catch code switched off for tagged packets |
| AC-3 | No code is completed by a message of another code in any fuzz run (identity check in the fake firmware) |
| AC-4 | Milestone L1. M112 ≤ 100 ms in 1000 stops under injected noise, on the plain and the extended link; no stop lost; a stop is never placed into a SequenceGuard repeat in either direction, and one that DSF put into a retry that RRF then answered as a repeat goes first into the next new transfer (CE-16). A ResendPacket for any packet of a retry that carries the stop finds that packet and does not end DCS; a stop goes in front of a retry whose transfer is filled to its limit |
| AC-5 | Milestone L1. Halt (CE-17): malformed packets and corrupted transfers injected next to an RRF-local emergency stop (an M112 trigger in RRF) and next to ObjectModel answers that carry status halted: DSF reports halted within a bounded time, never misses it, and DCS does not exit. A dropped transfer during DSF's M112: the stop is not reported done until RRF processed it |
| AC-6 | New static RRF RAM ≤ 1 KB by map file; no heap allocation and no side table (CE-21) |
| AC-7 | The coexistence cases of 7.1 behave as stated, before and after E3; an epoch pair over the USB adapter and on a board other than SAME70 in the fake firmware; a stock-mode peer on either side sees no difference from the tags |
| AC-8 | Refinement (CE-22): the same scripted input against the stock-mode and the tagging fake firmware gives per channel the same sequence of logic events, apart from the events that the v7 catch code drops and the behaviour changes of CE-25 |
| AC-9 | No deadlock: in every fuzz and lab run each code ends (reply, cancel or error); a watchdog in the fake firmware host flags any channel that has work but makes no progress |
| AC-10 | Removed in draft 8: there is no hold at connect (FD-37). Its place is taken by AC-19 |
| AC-11 | EMC test of CE-19: corrupted transfers per hour measured; ≤ 100 per hour, or the trailer of CE-19 is specified |
| AC-12 | Crossing abort with the gate (CE-24): in the fake firmware RRF writes AbortFile(all) in the same exchange in which DSF writes InvalidateChannel(e+1), 1000 times with random timing, and RRF-local emergency stops in between: no channel stays gated or blocked; DSF answers only an AbortFile(all) of its current epoch; the channel runs codes afterwards; no DSF request that the gate dropped waits on DSF's side |
| AC-13 | Stale Unlock (CE-25 (a)): on an idle GB, an Unlock that RRF refused and whose copy arrives after InvalidateChannel(e+1) is dropped and leaves no lock; another channel locks and moves right after the apply. The same for an Unlock that DSF queued and dropped at the abort (K28). A GB that still runs a code at the apply, e.g. a G28 between two homing files, keeps its locks and `toBeHomed` until that code ends, and no other channel moves meanwhile. With a stock DSF (E1) no test of the existing suites relies on the lock after an InvalidateChannel |
| AC-14 | DoCode and abort with an epoch pair (CE-25 (b), (e)): a DoCode that runs in DSF when its channel is aborted fails with an error and sends no result; RRF's GB no longer executes on the SBC after the apply; an unsent DoCode is not written; a DoCode result of the old epoch is dropped and does not finish a newer DoCode; the next DoCode on that channel completes |
| AC-15 | Repeats, ahead tags and stale requests (CE-5, CE-7, CE-12): in the fake firmware without SequenceGuard a transfer with InvalidateChannel(e+1) is processed twice; the second record is counted and ignored, the codes stored after the first stay, a macro wait begun after it is not ended and no file of the new epoch is aborted. A DSF packet whose tag is ahead of RRF's epoch and an RRF packet whose tag is ahead of DSF's are dropped and counted; untagged packets pass. A G28 whose homing file an abort popped ends without RRF's 3 s macro timeout, and a daemon.g request whose level the abort popped is not sent |
| AC-16 | Abort during a macro wait (CE-7, E1): InvalidateChannel arrives while the main task waits for a macro of that channel, once inside a spin and once from `CheckTriggers`. The wait ends at once, the apply runs at the top of the next spin, and there is no 3 s timeout. The same while the main task waits for a macro of another channel: the apply comes after that answer, nothing of the aborted GB is written before it, and the channel works afterwards |
| AC-17 | Abort crossing work that RRF starts itself (CE-7, CE-12, E1), for a trigger macro, daemon.g and a text code from PanelDue: (a) the request is unsent or sent at the record: it ends with an error and no ExecuteMacro of it is written afterwards; (b) the trigger fires between record and apply: it stays pending, and its macro runs after the apply; (c) the macro runs at the record: both sides drop it. In no case does a packet of the old work pass DSF's filter, and no channel stays blocked. In stock mode the same, without the filter |
| AC-18 | Stock-DSF mode (CE-25 (b), (c), E0 and E1). A DoCode runs when its channel is aborted: RRF's GB is free after the apply; a late result is printed and finishes nothing; the test records what a late result does to a newer DoCode. A message box is open at the base level when its channel is aborted: no MessageAcknowledged is written, the M291 ends, a print on File goes on (no `StopPrint`), and the test records where the M291's final reply goes. Both tests run first against the model of today's RRF and show today's behaviour |
| AC-19 | Restarts and the adopt rule (CE-4), on SPI and on USB: a DCS restart and an RRF restart, each noticed and not noticed by the other side. After the first packets every used channel has the same epoch on both sides; config.g runs after an RRF restart; no channel drops current packets. The two corner cases of CE-4 (a stale copy first; the marker's transfer processed twice) behave as stated. The window of Q34 is shown, with the catch code and without it |
| AC-20 | Nothing between record and apply (CE-12, E1): with the apply delayed, in the fake firmware and in the RRF harness, RRF writes no MacroFileClosed, ExecuteMacro, AbortFile, WaitForAcknowledgement, MessageAcknowledged, DoCode or PrintPaused of the GB after the record, and none of the ones pending then after the apply. Replies of a code that still runs go out. Codes sent in between run after the apply, in order, also when the GB was invalidated for a pause. A Lock sent in between is refused and succeeds after the apply |
| AC-21 | K5 (CE-25, E1): a link loss with each of `macroFileClosed`, `messagePromptPending`, `messageAcknowledged`, an unsent DoCode, `invalidated` and `reportPause` pending. The first exchanges of the new connection carry none of them, and DCS does not stop with "Stack underrun". The test fails against RRF 0dccb9efe |

## 9. Unchanged decisions and boundaries

- Matching by position stays within an epoch (FD-3); a level id only on evidence (FD-21, EG-6); pause stays v7 apart
  from the tag of PrintPaused (FD-7, FD-36, LA-13).
- Logic defects stay on the logic list: K3 (C-3, LA-15), K25 (LA-16), K26 (LA-17), the Q13 cases (LA-18).
- B7 stays as it is (Q5).

## 10. Decisions

### 10.1 History

- Drafts 1 to 3 designed v8 as a capability `SessionLayer` on v7. Draft 4 recorded an interview with Tim
  (decisions D1 to D14 of draft 4): its own protocol version, the v2 link features always on, streams, credit,
  RESET_ACK, STOP_ACK, request ids, session resume, a per-packet check.
- Three expert reviews of draft 4 followed on 2026-11-09 (section 11).
- Tim then decided the minimal design below (FD-1 to FD-14); draft 5 recorded it.
- Two reviewers checked draft 5 on the same day, together with Tim's proposal of a second epoch owned by RRF
  (section 11). Both rejected the second epoch. Tim then decided FD-15 to FD-23 (section 10.3); draft 6 records them.
- An independent reviewer checked draft 6 on the same day (section 11). Tim accepted every fix and confirmed three
  choices of draft 6 (FD-24 to FD-30, section 10.4); draft 7 records them. Tim then decided Q33, Q23 and Q28
  (FD-31 to FD-33).
- A root-cause pass over draft 7 followed on the same day (section 11, fourth pass). It found three clusters of
  special cases, each with one cause and with a path in RRF that already works. Tim decided FD-34 to FD-37
  (section 10.6); draft 8 records them. Two of them reverse decisions of the same day: FD-34 reverses FD-31 (Q33
  option (a)), and FD-37 reverses FD-2 (capability and activation with 0x4D50) and makes FD-32 void.

### 10.2 Final decisions (Tim, 2026-11-09, after the reviews)

| # | Decision | Applied in |
|---|---|---|
| FD-1 | **Direction.** No v8: no own version number, no new header, no streams, stall or go-back-N, no RESET_ACK, no STOP_ACK, no credit, no request ids, no session resume, no code identity for now. Instead a channel epoch on v7 plus evidence gates | sections 1, 4, 7.3 |
| FD-2 | **Carrier and switch-on.** A link capability `ChannelEpoch` = 8, offered in `limits.sbcLinkCapabilities`, activated with 0x4D50. RRF offers it outside the SAME70-only block, on every board and on USB; DSF holds channel traffic until the capability state is known, a bounded wait at connect. **Reversed by FD-37 (draft 8):** no capability, no activation, no hold | — (was CE-1 … CE-3, removed) |
| FD-3 | **The tag.** `resendPacketId` carries the epoch in both directions; payloads do not grow. Code identity only on evidence | CE-5, CE-6, EG-1 |
| FD-4 | **Companion 1.** RRF never refuses InvalidateChannel: receipt without the mutex, apply with it; `FillBuffer` holds the channel's codes until the apply; reuse `invalidationPending` (renamed since draft 7, CE-7), delete `codeReplySurplus`. A plain v7 fix. Refined by FD-34 (draft 8): the SBC task only records, the main task applies | CE-7; deletion of `codeReplySurplus` in E3 (section 7.2) |
| FD-5 | **Companion 2.** DSF's resent copies keep their tag | CE-8 |
| FD-6 | **RRF side.** Stamp the epoch on accepted codes, carry it to `codeEpoch` in `FillBuffer`, filter stale Code, MacroStarted, MacroCompleted before their side effects; reply tag through `GetResponseMessageType` and free `MessageType` bits into `resendPacketId`; stack events carry `codeEpoch`; stale requests fail locally; `InvalidateResources` marks `codeEpoch` invalid | CE-9 … CE-12 (Q22 decided by FD-19; tag sources reduced by FD-36) |
| FD-7 | **DSF side.** The epoch on the channel `Processor`, replacing the three abort flags; one filter at the top of `ProcessPacket`; answers to DSF requests are never filtered (superseded for answers: since FD-16 they carry the request's tag and stale ones are dropped, CE-14) | CE-13, CE-14 (reset point corrected, CE-4) |
| FD-8 | **RRF's own abort.** DSF answers AbortFile(all) with InvalidateChannel(e+1). Whether RRF requests an abort after its 3 s macro-wait timeout is logic, open | CE-15, LA-8 |
| FD-9 | **Stop overtakes retries.** A pending stop goes first into the next retry; M112 ≤ 100 ms in 1000 stops under noise; no header change (refined by FD-15: never into a SequenceGuard repeat) | CE-16, AC-4, L1 |
| FD-10 | **No session resume.** A link loss ends the session as today; the retry limit of 10 stays; time-based retries are not kept | sections 1, 5 |
| FD-11 | **Peers without ChannelEpoch** run upstream v7; the fork catch code is deleted from the v7 path once the printers run the epoch RRF. The 2026-10-08 decision "negotiation on top of v7" (v2 C1) holds | CE-18, E3, v2 document |
| FD-12 | **Integrity (v2 R9)** conditional on an EMC test: ≤ 100 corrupted transfers per hour, else a second per-transfer CRC trailer | CE-19, AC-11 |
| FD-13 | **Separate v7 work**, each its own PR with unit tests: DCS never exits on link data (incl. NEW-1), link events and health state, and the bug fixes act-before-refuse, Lock as a pending request, stale flags after a reconnect, the old session's TX; added on 2026-11-09: the halt rule with K16 and K6. Not kept: time-based retries, a systemd watchdog (open item LA-20). Safety-relevant K findings (K1–K3, K6, K12) go to Christian Hammacher once a test proves each and shows it is not fork code | section 5, CE-17 |
| FD-14 | **Unchanged from earlier decisions.** Epoch only, identity on evidence (old D3); pause stays v7 (old D7); shown to Christian Hammacher after the epoch milestone passed the lab (old D8, E2); Q12: watch H1 and H2 afterwards; logic defects stay on the logic list; K1/K2 reachability tests in E0. STOPPED from RRF (old D9) and STOP_ACK (old D5): deferred; STOPPED only as a capability of its own once a test shows the RRF-local-stop problem (K20, X-5) — confirmed by FD-23, which drops STOP_ACK | sections 7, 9, 12 |

Draft 4's decisions: D1 and D2 reversed (FD-1, FD-11); D3 kept (FD-14); D4 dropped with credit; D5 dropped (no
expedited flag; CE-16 needs no header); D6 superseded (ChannelEpoch on every board and on USB; NEW-1 in VF-1); D7
kept; D8 kept for E2; D9 deferred (Q27); D10 now logic, open (LA-8); D11 kept for K3, K25, K26, while K1 and K2 are
open again (Q14), because V8-34 was dropped; D12 kept, but PR10 dropped; D13 kept (FD-13); D14 dropped: the v2 link
features stay capabilities.

### 10.3 Decisions after draft 5 (Tim, 2026-11-09)

| # | Decision | Applied in |
|---|---|---|
| FD-15 | **Stop path (Q24).** A pending stop goes first into the next new transfer. A SequenceGuard repeat stays a pure repeat: RRF answers a repeated transfer with its last answer without reading the new content (`RRF/SBC/DataTransfer.cpp:924-946`, `:950-961`), so a stop placed in a repeat would be lost. Acceptance stays M112 ≤ 100 ms over 1000 stops under injected noise. Draft 6 reads "new transfer" as "a transfer whose data RRF reads", so a retry of a transfer that RRF did not complete still carries the stop (FD-9); confirmed by FD-26 | CE-16, AC-4, L1 |
| FD-16 | **Stale channel packets (Q25).** RRF drops every DSF channel packet of an old epoch. At InvalidateChannel DSF fails its own pending requests of the old epoch, so nothing waits forever: lock and flush requests (`Processor.cs` `InvalidateRegular`), evaluations and variables (`LinkInterface.cs`, merged per (channel, expression) at `:144-153`; a hook). **Consequence found in the code, confirmed by Tim (FD-24):** RRF's answers carry the tag of their request and DSF drops stale ones, because once the old requests are failed an unfiltered stale answer would complete a request of the new epoch (CE-14); SetLastCodeResult requests are failed with the others, as `InvalidateCodes` does | CE-6, CE-10, CE-14, CE-25 |
| FD-17 | **Locks; the general rule.** An epoch change ends everything of the old epoch on both sides. DSF counts an Unlock as done once it is written (`DCS/Link/Channel/Processor.cs:919-921`); RRF's apply of InvalidateChannel releases no lock (`RRF/SBC/SbcInterface.cpp:897-918`), while `UnlockAll` runs e.g. when the File channel pauses (`RRF/GCodes/GCodes.cpp:644-656`); a dropped stale Unlock would leave the movement lock held. So the abort releases the channel's locks: the apply calls `UnlockAll` for that GB (restricted to an idle GB by FD-27). A test in E0 checks that no v7 DSF relies on a lock surviving an InvalidateChannel. Behaviour change: the locks of an idle GB are released at the abort, not at the next Unlock or when the GB's next code ends | CE-7, CE-25 (a), E0, E1, AC-13 |
| FD-18 | **DoCode under the general rule (K13).** DoCode carries the channel epoch. At InvalidateChannel RRF ends its own pending DoCode wait (draft 6 puts it into the apply, because it needs the GB mutex, CE-25; confirmed by FD-25), and DSF fails a DoCode of the old epoch with an error. Behaviour change: an abort of the channel also ends a running DoCode. Draft 5's "DoCode untouched, never filtered" is removed. Since FD-36 (draft 8) RRF ends the DoCode for every DSF | CE-6, CE-10, CE-12, CE-14, CE-25 (b), LA-10, AC-14 |
| FD-19 | **Tag sources in RRF (Q22).** CE-9 stands. Output of a code DSF sent, and the helper macros RRF requests while executing that code (homing, tool change, probe, pause.g, …), carry the code's epoch. Work that RRF starts itself carries the current channel epoch when raised: `AsyncSystemMacroCode` macros (`fromCode` false, `RRF/GCodes/GCodes.cpp:3699`: config.g `:414`, daemon.g `:690`, triggerN.g `:1052`, start.g `:4001`, event macros `GCodes3.cpp:1432`, stop.g at the end of a print `GCodes4.cpp:844`), PrintPaused, the box timeout and RRF's own AbortFile. Since FD-36 (draft 8) such work carries the channel epoch at its write, and no event stores an epoch | CE-9 and the table under CE-12 |
| FD-20 | **One DSF epoch plus an RRF gate bit; two epochs rejected.** Tim proposed a DSF epoch d and an RRF epoch r. Both reviewers rejected it: **(a)** crossing aborts deadlock: RRF raises r and writes AbortFile(all) tagged (r+1, d) while DSF writes InvalidateChannel(d+1, r) in the same exchange; each side drops the other's announcing packet, and from then on every packet fails the other side's filter; **(b)** an RRF-local emergency stop has no announcing packet: `DoEmergencyStop` sends none of its own (`RRF/GCodes/GCodes.cpp:1065-1070`) and `GCodeBuffer::Reset` clears the abort flags (`GCodeBuffer.cpp:145`), so raising r there would make RRF drop all DSF channel packets while DSF waits for their answers without a bound (V7-85); **(c)** "K18 fixed directly" does not hold: the internal reply path has only 7 free `MessageType` bits (`RRF/Platform/MessageType.h:16-50`: bits 18–23 and 26), which d uses (CE-11), so r cannot travel with a reply; K18 is fixed by d through CE-15; **(d)** a dropped stale Unlock (FD-17). The one gain of r, that RRF drops crossing DSF packets between its own abort and DSF's InvalidateChannel(e+1), is kept by a gate bit per GB: set where AbortFile(all) is written (`RRF/SBC/SbcInterface.cpp:1549`); while set, RRF drops DSF channel packets of that channel except InvalidateChannel; the InvalidateChannel(e+1) that DSF sends back clears it; nothing raises it on an RRF-local emergency stop; the macro timeout stays LA-8 | CE-24, section 11 |
| FD-21 | **Level counter rejected, level id staged, serial comparison now.** Single-file aborts (M99, macro errors) stay matched by position. A level counter (stack depth) is rejected: a depth is reused after an abort (ABA); RRF has levels DSF does not know, the M120 local pushes (`localPush`, `RRF/GCodes/GCodeMachineState.h:272`; pushes at `GCodes2.cpp:2273`, `GCodes6.cpp:151`, `GCodes7.cpp:110`; `PopState` pops them together with the next file level, `GCodeBuffer.cpp:1092-1112`, `:1105`); and (depth, generation) in 7 bits wraps after 8 macros, because a depth up to 10 (`Config/Configuration.h:243`) needs 4 bits. A level id is the right idea later, under these conditions: its owner is DSF's `StackState` (`DCS/Link/Channel/StackState.cs:14`), not the file object, because DSF pushes levels without a file (message boxes `Processor.cs:1273`, M606 copies `:381`, `:389`); it only filters and never decides when a reply is delivered, so `PendingReplies` stays (`Processor.cs:450`, `:870-876`), otherwise an M98 could complete before its macro ends; it removes the cross-level misattributions (the late-M99 class, fork fix #44; parts of CC-9 and CC-16 … CC-18) but not CC-11 and CC-12 (dummy reply and drains), which would need per-code identity; B7 changes on both sides in lockstep; the 6-bit wrap bound against the 64-entry reply FIFO (`RRF/Platform/OutputMemory.h:16`) is not proven. Staged: **now** the epoch is compared as an RFC 1982 serial number, not for equality, so a later level id can come from the same per-channel counter without a new field (since FD-29 only the current epoch passes until then; the serial comparison decides the receipt of InvalidateChannel and the counter class); **later** the level id is added when EG-1 shows a cross-level misattribution after E2 | CE-5, EG-6, Q30 |
| FD-22 | **Remaining v7 defects (Q21).** A fake-firmware or harness test for each in E0: K4 (RRF side), K9, K10, K11, K12, K15, K17, K19, K22, K24, K27, NEW-2. What a test proves is fixed, and reported to Christian Hammacher per the K reporting rule (FD-13); the rest stay documented | E0 |
| FD-23 | **STOPPED and STOP_ACK (Q27).** STOPPED is deferred until an E0 test shows K20. STOP_ACK is dropped | E0, LA-7 |

### 10.4 Decisions after draft 6 (Tim, 2026-11-09)

Tim accepted every fix of the independent review of draft 6 (section 11) and confirmed three choices of draft 6.

| # | Decision | Applied in |
|---|---|---|
| FD-24 | **Answers carry the epoch of their request (confirms the consequence of FD-16).** Locked, EvaluationResult and VariableResult carry the tag of the request they answer, and DSF drops stale ones (CE-14). The tag needs no store while each answer is written inside the handler of its request; once VF-5 makes Lock a pending request, the Lock's tag is kept per GB (section 6) | CE-6, CE-14, VF-5, section 6 |
| FD-25 | **RRF ends a running DoCode in the apply, not at the receipt (confirms draft 6).** Ending it needs the GB mutex (`RRF/SBC/SbcInterface.cpp:1764`, `:784`); until the apply `FillBuffer` holds the channel's codes and a stale DoCode result is dropped. Since FD-34 (draft 8) the main task runs the apply and holds the mutex there | CE-25 (b), LA-10 |
| FD-26 | **A stop stays pending until a transfer whose data RRF reads (confirms draft 6's reading of FD-15).** A retry of a transfer that RRF did not complete carries it; a SequenceGuard repeat does not, and a stop that DSF put into a retry that RRF then answers as a repeat stays pending | CE-16, AC-4 |
| FD-27 | **The abort releases the locks of an idle GB only (review M1).** The apply calls `UnlockAll` only if `gb->IsCompletelyIdle()`; otherwise `HandleResult` releases the locks when the code ends (`RRF/GCodes/GCodes2.cpp:5112-5116`). Reason: the apply can run while a code of the old epoch still executes (`RRF/SBC/SbcInterface.cpp:907-911`), and `UnlockAll` at the base level clears `toBeHomed` (`RRF/GCodes/GCodes.cpp:5658-5663`), which changes a running G28. The dropped stale Unlock after M505 and K28 are still covered, because the GB is idle there | CE-7, CE-25 (a), V7-53, V7-57, V7-58, LA-22, AC-13 |
| FD-28 | **The receipt of InvalidateChannel acts once (review M2).** It records the tag as the channel epoch only if the tag follows it (RFC 1982); an equal or preceding InvalidateChannel is counted and ignored. Draft 6 had dropped v7's first-copy guard (`RRF/SBC/SbcInterface.cpp:882`), so a transfer repeated on SPI without SequenceGuard would have purged the codes stored since, ended a new macro wait and aborted new files without telling DSF. The sentence of draft 6 that a tag ahead cannot occur is corrected: InvalidateChannel always carries e+1 | CE-5, CE-7, CE-20, AC-15 |
| FD-29 | **Only the current epoch passes (review M3, m2).** Apart from InvalidateChannel's e+1, both sides drop and count every tag that is not the channel epoch, until a level id exists (EG-6). Draft 6 passed a tag that follows, which with 6 bits a packet 33 to 63 aborts old also does. RRF stamps a code with the tag of its packet that passed the filter | CE-5, CE-9, CE-10, CE-14, Q28 |
| FD-30 | **Stop and halt as link milestone L1 (review m1).** CE-16 and CE-17 do not depend on the epoch and move out of E2 into L1, with its own PR and gate (AC-4, AC-5). A stop in front of a retry MUST NOT break DSF's resend lookup (`DCS/Link/Adapter/SPI.cs:826-855`): the retry renumbers its packets, or the lookup becomes independent of the order. Every transfer keeps room for the stop (8 bytes) | CE-16, CE-17, section 7.2, AC-4, AC-5 |
| FD-31 | **No new work while the apply is pending (Q33, Tim 2026-11-09).** Option (a): as `FillBuffer` holds DSF's codes, the main task starts no other new work on a GB whose apply is pending (trigger or daemon macro, text code from PanelDue or USB). It skips that GB in its spin and does not wait for the apply, so it releases the GB mutex as usual (`RRF/GCodes/GCodes.cpp:590`) and the SBC task can apply; the work starts after the apply, in the new epoch. No new wait edge. **Reversed by FD-34 (draft 8):** the main task applies the abort itself, which was option (b) | — (was CE-7), Q33 |
| FD-32 | **Timer of the hold at connect (Q23, Tim 2026-11-09).** E0 measures the time from connect to the ChannelEpoch state on the lab printer. The timer is set from it with margin, but always below RRF's 3 s macro wait (`RRF/SBC/SbcMessageFormats.h:42`), because the hold also delays MacroStarted, DSF's answer to RRF's own macro requests such as config.g (CE-3). On expiry DSF continues without ChannelEpoch (plain v7) and counts it. **Void since FD-37 (draft 8):** there is no hold at connect | — (was CE-3), Q23 |
| FD-33 | **Epoch width 6 bits (Q28, Tim 2026-11-09).** Confirmed: the reply tag uses the free `MessageType` bits 18–23; a stale packet is recognised while fewer than 64 aborts of its channel happened during its flight; the counters show wrap-related drops if they ever occur | CE-5, Q28 |

### 10.5 What happened to the v8 requirements of drafts 1 to 4

| v8 requirement | Fate in draft 5 (and 6, 7, 8) |
|---|---|
| V8-1 S layer between L and G | Dropped. The epoch lives in existing objects (`Processor`, `GCodeBuffer`) |
| V8-2 fixed header with channel, epoch, sequence | Dropped. The tag rides in `resendPacketId` (CE-5) |
| V8-3 message classes | Dropped |
| V8-4 may-pass table | Dropped; the v7 order stays (PR2) |
| V8-5 sequence numbers per stream | Dropped; on evidence (EG-2) |
| V8-6 stall in two streams per channel | Dropped; on evidence (EG-2) |
| V8-48 one message, one stream | Dropped with V8-6 |
| V8-7 no refusal after an effect | Kept as CE-7 (InvalidateChannel), VF-3 (Code), VF-4 (SetVariable) |
| V8-36 Lock as a pending request | Kept as VF-5 |
| V8-8 channel state report | Dropped |
| V8-9 RESET and RESET_ACK | Replaced by CE-7 (never refused; record by the SBC task, apply by the main task), without an answer packet |
| V8-10 epoch on channel data, tag taken when the reply is produced | Kept in reduced form: CE-5, CE-6, CE-9, CE-11, CE-14 |
| V8-11 requests bound to their epoch | Kept: CE-12 |
| V8-12 RRF's abort as a RESET request | Replaced by CE-15 (DSF answers AbortFile(all) with InvalidateChannel) and, in draft 6, RRF's gate (CE-24); a RESET request only on evidence (EG-5) |
| V8-13 request ids for global requests | Dropped; on evidence (EG-4) |
| V8-39 request numbers for RRF requests | Dropped; the timeout reaction is logic (LA-8, open) |
| V8-14 code identity | Dropped for now (FD-3); on evidence (EG-1) |
| V8-15 … V8-17 absolute credit, storable capacity, per-channel limits | Dropped; on evidence (EG-3) |
| V8-18 no silent reply loss | Dropped; NEW-2 stays open (Q2) |
| V8-19 expedited class | Dropped; CE-16 keeps the stop ahead of retries (L1) |
| V8-20 STOP_ACK | Dropped; CE-17 (c) |
| V8-21 no ResendPacket | Dropped; ResendPacket stays |
| V8-22 macro M112 flush | Already logic in draft 4 (LA-14) |
| V8-38 STOPPED | Deferred until an E0 test shows K20 (FD-23); CE-17 (b) makes sure DSF sees the halt |
| V8-40 v2 link features always on | Dropped; they stay capabilities (v2 C1) |
| V8-41 own protocol version | Dropped (FD-1) |
| V8-42 per-packet integrity check | Replaced by CE-19 (v2 R9 conditional on an EMC test) |
| V8-23 no rule on transfer numbers | Dropped as a requirement; the epoch removes the abort windows' dependence on them |
| V8-24 a session change clears state | Split: K5 by the routine of CE-25 (was VF-6), VF-7 (K7); epochs CE-4 |
| V8-35 unknown and invalid packets | VF-1 (DSF); RRF's K4: an E0 test (FD-22) |
| V8-37 B7 stays | Unchanged (V7-22 identical) |
| V8-25 static RAM < 1 KB | Kept: CE-21 |
| V8-26 S layer as its own source file | Dropped |
| V8-27 counters | Kept: CE-20 |
| V8-28 refinement | Kept: CE-22 |
| V8-29 cross-channel write order | Dropped; the v7 order stays |
| V8-30 wait-for graph | Kept: CE-23 |
| V8-31 control packets without waiting | Kept for InvalidateChannel: the record of CE-7 |
| V8-32 every new wait bounded | Kept. Draft 7 had one new wait, the hold at connect; draft 8 has none (FD-37). The delay from record to apply is bounded by one main loop pass (CE-7) |
| V8-33 graph reviewed before M1 | Replaced by the E0 reachability tests (Q14) |
| V8-34 completion on acceptance | Dropped; K1 and K2 open again (Q14) |
| V8-43 time-based retries and resync | Dropped (FD-10) |
| V8-44 no exit on link data | Kept as VF-1 |
| V8-45 session lifetime and resume | Dropped (FD-10) |
| V8-46 events to the logic | Kept as VF-2 |
| V8-47 no motion or safety function in the link | Kept as a principle (layering rule) |

### 10.6 Decisions after draft 7: root-cause pass (Tim, 2026-11-09)

A root-cause pass over draft 7 grouped its special cases into three clusters with one cause each (section 11, fourth
pass). The "receipt" of the decisions above is the record of draft 8.

| # | Decision | Applied in |
|---|---|---|
| FD-34 | **The main task applies a channel abort. Reverses FD-31 (Q33 option (a)).** Cause: a GB belongs to RRF's main task, which holds its mutex for a whole spin (`RRF/GCodes/GCodes.cpp:590`), while the SBC task carried out InvalidateChannel and had to win that mutex. Working path: other tasks record a request, and the main task carries it out at the top of its spin (`requestsFromOtherTasks`, `GCodes.cpp:76`, `:463-487`, `:581`). The SBC task only records: the epoch from the tag if it follows, the ring purge, the end of a macro wait, a per-GB flag. The main task applies at the top of its spin, before `CheckTriggers` (`:517`). InvalidateChannel is never refused. Gone: the apply by the SBC task with its 10 ms try and its placement rules, the main task's skip rule, "no second purge" | CE-7, CE-23, E1, AC-16, AC-17 |
| FD-35 | **Between record and apply the SBC task writes no channel event of that GB.** One condition in the write phase; the apply voids what was pending. A stock DSF therefore sees no event that RRF wrote after it read the InvalidateChannel | CE-12, AC-20 |
| FD-36 | **The apply is the per-channel routine of a link loss, for every DSF.** Cause: InvalidateChannel ended only a hand-picked part of the channel's pending work. Working path: `InvalidateResources` (`RRF/SBC/SbcInterface.cpp:1746-1773`). One routine for both callers, with no branch for the kind of DSF. It leaves no "tell DSF" flag set, which also fixes K5 in the same PR (E1), with its own test. Behaviour changes for every DSF: an abort ends a DoCode (K13) and closes a message box left at the base level (the open case of OF-T10). Kept: locks are released only if the GB is idle (FD-27); a code still running at the base level survives. Result: two tag sources and no stored epochs; Q31 and LA-23 are closed | CE-9, CE-25, the table under CE-12, E1, AC-18, AC-21, LA-10, LA-22, LA-23 |
| FD-37 | **No activation: both sides always tag. Reverses FD-2; FD-32 is void.** Cause: the capability was switched on through an object-model round trip, which queues behind other queries. That forced the hold of all channel traffic at connect, its timer, VF-8 as a prerequisite of E2 and capability support in the USB adapter. Decision: the tag is always written in `resendPacketId`, which a stock peer ignores. RRF takes each channel's epoch from DSF's first tagged packet of that channel in a connection and filters from then on. The known epoch arms the gate. Both sides forget the epochs with the connection. A read-only object-model field for diagnostics is not part of the decision | CE-4, CE-5, CE-13, CE-18, CE-24, section 7.1, E2, AC-19 |

Where the decisions left a choice, draft 8 chose after reading the code (reasons in the requirement named):

| Choice | Chosen | Where |
|---|---|---|
| Where the apply sits | A request bit in `requestsFromOtherTasks` plus the per-GB flag; carried out in the request block at the top of `GCodes::Spin` under the GB mutex | CE-7 |
| RRF's own packets while a channel's epoch is unknown | Untagged, not a start value | CE-4 |
| A DCS restart that RRF did not notice | A first marker, bit 14, on DSF's first tagged packet of a connection; RRF then forgets its epochs and adopts. An InvalidateChannel on every channel at connect was rejected, because it would end RRF's wait for config.g | CE-4 |
| How DSF runs without its catch code before E3 | Per packet: a tagged packet is decided by its tag, an untagged one by the v7 windows. Per connection only the #161 hold, skipped once RRF has shown a tag | CE-13 |

### 10.7 Removed in draft 8

| ID or rule | Was (draft 7) | Why removed |
|---|---|---|
| CE-1 | Capability bit `ChannelEpoch` = 8 | FD-37: no capability |
| CE-2 | Offered on every board and transport; capability support in DSF's USB adapter | FD-37: the tag is written by the packet writers of both adapters (`DCS/Link/Adapter/SPI.cs:782-790`, `USB.cs:676-684`) and by `WritePacketHeader` in RRF, which no board excludes |
| CE-3 | Activation before channel traffic; the hold at connect | FD-37: nothing to activate, nothing to hold |
| Q23, FD-32, AC-10 | The timer of the hold at connect and its acceptance | FD-37 |
| VF-8 as a prerequisite of E2 | The hold waited for the capability query | FD-37; VF-8 stays a separate fix and a prerequisite of L1 |
| VF-6 | Stale per-GB flags after a reconnect, as a separate fix | FD-36: the routine that `InvalidateResources` shares with the abort clears them (CE-25, E1, AC-21) |
| FD-31, the skip rule | The main task skips a GB whose apply is pending | FD-34: the main task applies before it spins any GB; for the rest of the running pass the existing guard at `RRF/GCodes/GCodes.cpp:581` and `IsTriggerBusy` do it |
| The apply by the SBC task | 10 ms try of the GB mutex; "at the latest at the start of the mutex section of the write phase, before AbortFile is written"; "the apply must not purge the ring again" | FD-34: record, apply and fetch are in order on the main task from the top of its spin |
| Stored epochs | The epoch kept with the box timeout and with PrintPaused (16 B); the argument why AbortFile(all) needs none; `codeEpoch` set by text codes and by macro requests without a code | FD-36: no pending event survives an epoch change |
| Two failure paths for a stale macro request | Blocking: ended by the SBC task; non-blocking: voided by the apply, with the tag check as a second guard | FD-35, FD-36: one path in the write phase; the routine voids an unsent request like any other pending event |
| "Only in an epoch connection" | The end of a DoCode; the arming of the gate; "nothing of the epoch is stamped while ChannelEpoch is not active" | FD-36: the routine has no branch for the kind of DSF. FD-37: the known epoch of the channel replaces the switch |
| Q31, LA-23 | Work of an older epoch that the apply does not end | FD-36: decided |

## 11. Review of the v8 plan (2026-11-09)

Three reviews of draft 4 were made on 2026-11-09: by an embedded link owner, by a DSF architect and by a
safety/real-time engineer. The review reports are not in the repository; this section summarises them as they were
handed to this draft. Every code statement taken from them was checked again (section 0); where the code said
otherwise, this draft follows the code and says so (FD-6 → Q22, FD-7 → CE-4, the E2 gate → section 7.2, FD-11 →
CE-18).

- **Agreed by all three:** the channel epoch is enough for the ordering problems, together with the two companions
  (RRF never refuses InvalidateChannel; resent copies keep their tag). Drop the version bump, the new header, the
  streams, RESET_ACK, STOP_ACK, credit and session resume.
- **Disagreed on:** session resume, integrity, and the stop path. Tim decided: no resume (FD-10); integrity
  conditional on an EMC test (FD-12); the stop overtakes retries without a header change (FD-9).
- **The DSF architect's stage D** became the evidence gates (7.3): counters first, more protocol only when a counter
  and a symptom show the need.
- **Findings of the safety review that are not link topics** go to the logic and safety list, not into this document:
  - Under v2 SB2 a door opened while the link is down can be lost: RRF clears the trigger bit
    (`RRF/GCodes/GCodes.cpp:1016`) before it requests `triggerN.g` (`:1052`); with no SBC connected the request fails
    at once (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1246-1250`), otherwise after 3 s (`:1263-1274`); triggers react
    to edges only (`RRF/GCodes/TriggerItem.cpp:52-75`); `CheckTriggers` runs in the main loop (`GCodes.cpp:517`),
    which does not run while a spin (`:590`) waits for a macro from the SBC (up to 3 s, V7-40); only a trigger whose
    action is M112 alone fires as trigger 0 (`:987`, executed at `:997-998`). LA-19.
  - There is no systemd watchdog for DCS: the unit has `Type=notify` and `Restart=always` but no `WatchdogSec`
    (`pkg/common/duetcontrolserver/usr/lib/systemd/system/duetcontrolserver.service`). LA-20, open, not decided.
  - The extended-link "session status" is the constant 0x7FF (`DCS/Link/Protocol/Shared/ExtendedLink.cs:65`), not a
    restart counter. LA-21; also a constraint of VF-2 (Q29).

### Second review round (draft 5, 2026-11-09)

Two reviewers checked draft 5 and Tim's proposal of a second epoch, r, owned by RRF next to DSF's epoch d. Their
reports are not in the repository either; every code statement taken from them was re-read for draft 6 (section 0).

- **Two epochs:** both reviewers rejected them, for four reasons: crossing aborts deadlock; an RRF-local emergency stop
  has no packet that could announce a new r; r cannot travel with a reply, so "K18 fixed directly" does not hold; a
  dropped stale Unlock leaves the movement lock held. Details and code in FD-20. Tim rejected the two epochs and kept
  their one gain as RRF's gate bit (CE-24).
- **The dropped Unlock** led Tim to the general rule that an epoch change ends everything of the old epoch on both
  sides (FD-17, CE-25). The same rule removed draft 5's special case for DoCode (FD-18) and closed Q25 (FD-16).
- **Single-file aborts:** a level counter was rejected, a level id is staged behind evidence gate EG-6 under the
  conditions of FD-21; the epoch is compared as an RFC 1982 serial number from now on, so the level id can later share
  the counter.
- **Where draft 6 deviates from or adds to the decision texts because of the code** (each marked in its row): the end
  of the DoCode wait is in the apply, not at the receipt, because it needs the GB mutex (CE-25); RRF's answers are
  tagged and filtered, because the hook of FD-16 would otherwise let a stale answer complete a new request (CE-14);
  the epoch of the box timeout and of PrintPaused is kept apart from `codeEpoch` (CE-9, Q31); "next new transfer" is
  read as "a transfer whose data RRF reads", and a stop that DSF placed into a retry that RRF answers as a repeat stays
  pending (CE-16).

### Third review: independent reviewer on draft 6 (2026-11-09)

One independent reviewer checked draft 6. Verdict: ready after fixes, no redesign needed. The reviewer checked 112
file:line references and found all of them correct; the one imprecise reference was CE-9's `SbcInterface.cpp:1910`
for the handover, which is the head of the ring walk, while `PutBinary` is at `:1945` (fixed). The report is not in the
repository; every code statement taken from it was re-read for draft 7 (section 0). Tim accepted every fix (FD-27 to
FD-30) and confirmed three choices of draft 6 (FD-24 to FD-26).

- **M1, a lock released while a code runs:** the apply's `UnlockAll` could run while a code of the old epoch still
  executes, and at the base level it clears `toBeHomed`, which changes a running G28. Resolved: `UnlockAll` only on an
  idle GB (FD-27, CE-7, CE-25 (a)).
- **M2, a repeated InvalidateChannel acts twice:** draft 6 dropped the first-copy guard. Resolved: the receipt acts
  only if the tag follows the channel epoch (FD-28, CE-5, CE-7).
- **M3, ahead tags pass:** with 6 bits a packet 33 to 63 aborts old looks ahead. Resolved: every tag other than the
  channel epoch is dropped, apart from InvalidateChannel's e+1 (FD-29, CE-5, CE-10, CE-14, Q28).
- **m1, a stop in front of a retry breaks the resend lookup:** resolved by a requirement with two options and a
  reserved room of 8 bytes; CE-16 and CE-17 form the link milestone L1 (FD-30, section 7.2).
- **m2, the stamp:** RRF stamps the tag of the packet that passed the filter (FD-29, CE-9).
- **m3, the failure path of a stale macro request:** specified per request kind (CE-12); see the deviations below.
- **m4, answer tags:** 0 bytes only while each answer is written inside its request's handler; a pending Lock (VF-5)
  needs its tag per GB, 15 bytes (section 6).
- **m5, `invalidationPending`:** name and comment change; `FillBuffer` reads it inside its critical section (CE-7).
- **m6, untagged packets:** tag 0 passes in an epoch connection (CE-5).
- **m7, the hold at connect:** bounded by a timer; VF-8 is a prerequisite of E2 (CE-3, Q23).
- **K28, a new v7 finding from the review:** at an abort DSF drops an Unlock that is queued but not written
  ([open-findings.md](open-findings.md) K28; CE-25, LA-22).
- **Two points the reviewer could not verify** are in section 12.
- **Where draft 7 deviates from or adds to the review because of the code:** the review suggested treating a stale
  macro request as `macroFileError`. That fits the blocking request only: `macroFileError` is read only inside
  `RequestMacroFile` (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1278`), so for daemon.g it would do nothing. There the
  apply has popped the macro's level, and draft 7 lets the apply void the unsent request, as `InvalidateResources`
  does (`RRF/SBC/SbcInterface.cpp:1766-1769`); this also covers OF-T11 on the v7 path (CE-7, CE-12). Draft 6's reason
  ("the receipt has already ended a blocking wait") did not hold for a request made after the receipt, the G28 case of
  OF-T10. `IsCompletelyIdle` also counts a GB that waits for a DoCode result as idle (CE-25). While checking m3, work
  that RRF starts between the receipt and the apply was found to carry the new epoch although the apply aborts it
  (Q33).

### Fourth pass: root causes (draft 7, 2026-11-09)

A root-cause pass over draft 7 asked of every special case what causes it, and whether RRF already has a path that
works without it. It found three clusters. Tim decided all of them on 2026-11-09 (FD-34 to FD-37).

| Cluster | Special cases in draft 7 | One cause | Path that already works | Decision |
|---|---|---|---|---|
| Who carries out the abort | The "apply pending" flag with the `FillBuffer` hold and its critical-section rule; the main task skipping the GB (FD-31); "apply at the latest … before AbortFile is written"; "no second purge"; DoCode ended in the apply, not at the receipt (FD-25) | The SBC task carried out an abort on a GB that belongs to the main task | `requestsFromOtherTasks` (`RRF/GCodes/GCodes.cpp:76`, `:463-487`, `:581`) | FD-34, FD-35 |
| What the abort ends | A stored epoch for the box timeout and for PrintPaused; two failure paths for stale macro requests; "DoCode ended only in an epoch connection"; Q31, LA-23 | InvalidateChannel ended only a hand-picked part of the pending work | `InvalidateResources` (`RRF/SBC/SbcInterface.cpp:1746-1773`) | FD-36 |
| How the epoch is switched on | The hold of all channel traffic at connect (CE-3); its timer (FD-32, Q23); VF-8 as a prerequisite of E2; capability support in the USB adapter (CE-2); "a later request must repeat the whole set" | The switch rode on an object-model round trip | A header field that a stock peer ignores | FD-37 |

**Where draft 8 deviates from or adds to the decision texts because of the code.** Each point is marked in its
requirement.

1. **The `FillBuffer` hold does not go away completely (FD-34).** Record, apply and fetch are in order only from the
   top of the next spin. Within the running spin the main task fetches up to 8 codes without passing the guard at
   `GCodes.cpp:581` again (`:808-828`, `:734`), and the record can land in between. So `FillBuffer` keeps one entry
   in its hold list, read inside its critical section. The skip rule became the existing guard at `:581` plus one
   term in `IsTriggerBusy` (CE-7).
2. **DSF packets of the new epoch that need the GB mutex (FD-34).** In draft 7 the SBC task applied wherever it next
   held the GB mutex, so a Lock or an evaluation of the new epoch met an applied GB. With the main task applying they
   would act on the old stack, and the apply would then release a lock of the new epoch. So between record and apply
   such packets are refused, as for a busy mutex, a v7 path. Codes go to the ring, and the read-ahead drop
   (`SbcInterface.cpp:376-380`) does not apply to them (CE-7).
3. **`invalidated` (FD-35).** The record does the purge step of the write phase itself and clears `invalidated`, else
   a code of the new epoch would be dropped by the read-ahead rule; the routine clears it too, with `reportPause`
   for File and File2 (CE-7, CE-25).
4. **The box is closed without the abort flag (FD-36).** `InvalidateResources` calls
   `MessageAcknowledged(true, true, …)`. At the base level that makes the main task call `StopPrint` for the whole
   machine (`GCodes.cpp:593-616`). On a link loss the print is stopped anyway; on the abort of one channel it would
   end a print that was not addressed. The routine passes `shouldAbort` false for both callers (CE-25).
5. **An unsent DoCode is ended, not only its flag cleared (FD-36).** With only `sendToSbc` cleared, the code would
   ask again on its next spin (`GCodes2.cpp:603-606`, `:730-732`), also after a reconnect (K5). The routine calls
   `SetFinished(true)` for a sent and an unsent DoCode alike (CE-25).
6. **`PauseSequenceAborted` belongs in the routine (FD-36).** It must read the frames before `AbortFile` deletes
   them; on a link loss `StopPrint` resets the pause state afterwards (CE-25).
7. **"The epoch at the write equals the epoch at the raise" holds for pending events, not for a surviving code
   (FD-36).** A code that survives at the base level can raise a new stack event after the apply, e.g. the box of
   manual probing. It carries the new epoch and DSF takes it, as in v7 (CE-9, Q35).
8. **One failure path, with two conditions (FD-35, FD-36).** A blocking macro request that must not be written is
   ended in the write phase, whether the abort is still recorded or the requesting code is of an older epoch (CE-12).
9. **Stock DSF (FD-36).** A late result of a DoCode that the abort ended can finish a newer DoCode of the GB early,
   because a stock DSF sends no tag. The final reply of an M291 whose box the abort closed reaches a stock DSF right
   after the abort instead of after the M292 (CE-25, Q37).
10. **The gate is armed per channel, and only a tagged AbortFile(all) is answered (FD-37).** A gate set for an
    untagged AbortFile(all) would never be cleared, because DSF handles an untagged one as in v7 (CE-15, CE-24).
11. **The marker, not an InvalidateChannel at connect (FD-37).** An InvalidateChannel at connect would end RRF's wait
    for config.g (`SbcInterface.cpp:883-895`, `RRF/Platform/RepRap.cpp:627-637`). The marker is per connection, makes
    RRF forget its epochs, and is not carried by a copy (CE-4).
12. **A window stays (FD-37).** RRF's first request of a channel in a connection is untagged. If DSF's first packet
    of that channel is an InvalidateChannel that crosses it, the request passes DSF's filter. Until E3 the v7 window
    of #134 catches it, because it is untagged; after E3 it is the v7 order of DF-T2 (Q34).
13. **The delay to the apply can be longer than in draft 7 (FD-34).** The SBC task could apply while the main task
    was busy on another channel; now the apply waits for the main task's pass, up to 3 s if it waits for a macro of
    another channel (CE-7, Q38).
14. **CC-4 and CC-5 are not deleted.** `MacroFileClosedSent` and `MacroRequestSent` in the abort path are what "no
    pending event survives" needs. They are lines of the routine and of the record, so a stock DSF keeps the fixes of
    DF-T1 and of the RRF part of DF-T12 after E3 (CE-18, section 7.2).

## 12. Open questions and what is unverified

| # | Question | Status |
|---|---|---|
| Q1 | NEW-1: the USB resend looks into the wrong TX | Decided: fixed in VF-1. Code reading, not tested |
| Q2 | NEW-2: can the 64-entry reply FIFO fill in practice? | Open; EG-1 would show it as a non-stale out-of-order reply |
| Q3 | NEW-3: can RRF refuse a code that DSF's estimate allowed? | Closed into EG-3 |
| Q4 | Is logical credit physically storable? | Closed: no credit (FD-1) |
| Q5 | B7 read-ahead drop | Decided: B7 stays |
| Q6 | Does every message of a code pass one capture point for its tag? | Answered: `GetResponseMessageType` builds every SBC reply type; 15 call sites; bits 18–23 and 26 are free (CE-11) |
| Q7 | Cross-channel dependencies of the logic | Closed: the epoch keeps the v7 order across channels |
| Q8 | Switch point of the session layer within a connection | Closed again in draft 8: there is no switch. Both sides always tag, and RRF adopts each channel's epoch from DSF's first tagged packet (CE-4). What is left of a switch point is the window of Q34. The switch-window findings OF-S1 and OF-S5 of the extended header stay open in open-findings.md |
| Q9 | Is `AbortFile(true)` harmless when RRF already aborted all files itself (CE-15)? | Decided: a fake-firmware test in E0 |
| Q10 | Side effects of a refused Lock | Answered in draft 3: it keeps the lock it reached (VF-5) |
| Q11 | SetPrintFileInfo alignment after ActivateLinkCapabilities in one TX (G-19) | Open again: with the capabilities negotiated on v7 (FD-11) the case of G-19 stays; whether the alignment matters was not checked |
| Q12 | H1 ("Daemon: Out-of-order reply ''" after a DCS start), H2 ("Failed to find suitable stack level for flush request") | Decided: watch them after E2 |
| Q13 | DSF-internal codes that write mutex requests during a macro wait | Answered: logic (LA-16 … LA-18) |
| Q14 | Can C-1, C-2 and C-3 be reached (K1–K3)? | Decided: fake-firmware tests in E0. K1 and K2 have no fix in drafts 5 to 8 |
| Q15 | Can a MacroCompleted cross an RRF-side pop (X-7)? | Decided: a fake-firmware test in E0 |
| Q16 | Can the global stream stall before SetPrintFileInfo? | Closed: no streams |
| Q17 | Version handshake with a v7 DSF | Closed: no own version |
| Q18 | Third transfer buffer missing on a v8 RRF | Closed: as today, SequenceGuard is offered only with the buffer (`RRF/SBC/DataTransfer.cpp:478`) |
| Q19 | PR10 details (lifetime, session id, LinkDown window) | Closed: no resume. The LinkDown window moves to Q29 |
| Q20 | Per-packet check details | Closed: replaced by CE-19; trailer details Q26 |
| Q21 | v7 defects that neither the epoch nor section 5 fixes: K4 (RRF side), K9, K10, K11 (stop path), K12, K15, K17, K19, K22, K24, K27, NEW-2. Fix each or not? | Decided (FD-22): a fake-firmware or harness test for each in E0; what a test proves is fixed and reported (FD-13); the rest stay documented |
| Q22 | Tag of work that RRF started itself (a macro request without a code, the closal of a system macro without codes) | Decided (FD-19): CE-9 stands, two tag sources; the table under CE-12 lists every packet. Since draft 8 the second source is the channel epoch at the write, with no stored epoch (FD-36) |
| Q23 | The bound of the hold at connect (draft 7: a timer, FD-32) | Closed in draft 8 (FD-37): there is no hold at connect, so no timer |
| Q24 | How the stop of CE-16 reaches RRF when the retry is a SequenceGuard repeat that RRF answers without processing | Decided (FD-15, confirmed by FD-26): the stop goes first into the next new transfer; a repeat stays a pure repeat (CE-16, AC-4, L1) |
| Q25 | Should RRF also drop stale copies of DeleteLocalVariable, SetLastCodeResult, Unlock and Lock (CE-10)? | Decided (FD-16): RRF drops every stale DSF channel packet; DSF fails its own requests of the old epoch (CE-25); the abort releases the locks of an idle GB (FD-17, FD-27) |
| Q26 | Algorithm and placement of the per-transfer CRC trailer, if the EMC test exceeds 100 corrupted transfers per hour (CE-19) | Open until the EMC test, and only if it requires one |
| Q27 | STOPPED from RRF (old D9) and STOP_ACK (old D5) | Decided (FD-23): STOPPED deferred until an E0 test shows K20; STOP_ACK dropped |
| Q28 | The epoch has 6 bits, not 8 as the example of FD-3: the reply path has only bits 18–23 and 26 free in `MessageType` (`RRF/Platform/MessageType.h:16-50`). Since FD-29 only the current epoch passes, so a stale packet is recognised while fewer than 64 aborts of its channel happened during its flight. The RFC 1982 order (CE-5, section 3.2), which the record of InvalidateChannel and the counter classes use, holds below 32; draft 6 passed ahead tags and so recognised a stale packet only below 32. A later level id from the same counter would use up values faster (Q30) | Decided: 6 bits (FD-33) |
| Q29 | Thresholds of VF-2 (LinkDown window, rate for "degraded") and how PeerRestarted is told from a connection reset without a restart counter (`ExtendedLink.cs:65`) | Open |
| Q30 | **New in draft 6. Level id (FD-21).** Before it is added: owner DSF's `StackState`; it only filters, `PendingReplies` stays; it does not cover CC-11 and CC-12; B7 changes on both sides in lockstep; the 6-bit wrap bound against the 64-entry reply FIFO is not proven | Open item; added only when EG-6 fires |
| Q31 | **New in draft 6. Work that RRF raises while a code of an older epoch can still run** (the box timeout, PrintPaused; a message box that survives an abort at the base level) | Closed in draft 8 (FD-36): the routine closes a box left at the base level and voids every pending event, so no event needs an epoch of its own (CE-9, CE-25). What is left is Q35 |
| Q32 | **New in draft 6.** PrintPaused has one tag, File's epoch; File2's epoch is not carried | Restated in draft 8, closed for RRF: PrintPaused carries File's epoch at its write, is not written while File or File2 has an abort recorded, and the abort of either voids a pending one (CE-12, CE-25); DSF aborts the job on an abort of either file channel (`DCS/Link/Channel/Processor.cs:826-831`). Left for DSF: a PrintPaused that RRF wrote before it read an InvalidateChannel of File2 alone still carries File's current epoch and passes. LA-12 (decided) closes that: a job abort writes InvalidateChannel on File and File2 |
| Q33 | **New in draft 7. Work that RRF starts between the receipt and the apply.** Options were: (a) the main task starts no new work on a GB whose apply is pending; (b) the main task runs a pending apply itself at the start of its spin; (c) such work takes the old epoch | Decided again in draft 8 (FD-34, reverses FD-31): (b). The main task applies at the top of its spin; between record and apply no work starts on the GB (CE-7) |
| Q34 | **New in draft 8. The first request of a channel.** While a channel's epoch is unknown, RRF's packets of it are untagged and pass DSF's filter (CE-4, CE-5). If DSF's first packet of that channel in the connection is an InvalidateChannel that crosses an untagged request of RRF (the first trigger, daemon or PanelDue macro request of that channel), DSF starts the macro after its abort. Until E3 the window of #134 drops it, because it is untagged (CE-13); after E3 it is DF-T2 again, in this one case. The alternative: an RRF that has no epoch yet tags with the start value 0, which closes the window; then a DSF that did not notice an RRF restart drops RRF's requests, config.g included, on every channel whose epoch is no longer 0 | Open, for Tim. An E0 test shows the window (AC-19) |
| Q35 | **New in draft 8. A code that survives the abort at the base level and later raises a stack event of its own,** e.g. the message box of manual probing (`RRF/GCodes/GCodes6.cpp:149-157`). The event carries the channel epoch at its write, the new one, and DSF takes it, as in v7; draft 7 would have dropped it by the code's epoch. Its helper macro requests fail (CE-12), and its replies are dropped (CE-9). Whether such events should carry `codeEpoch` as well, which needs a rule for which events belong to a code, or whether the abort should end the base level's state machine (`AbortStateMachine`, as `AbortPrint` does, `RRF/GCodes/GCodes.cpp:3626`), which is logic | Open, for Tim |
| Q36 | **New in draft 8. Two corners of the adopt rule** (CE-4): after an unnoticed RRF restart, a stale copy as the first tagged packet of a channel makes RRF adopt an old epoch until the channel's next InvalidateChannel; a transfer that carries the marker and is processed twice lets an InvalidateChannel in it act twice. Both need an unnoticed restart or a repeated first transfer | Open: accept, or add a rule. E0 tests show them (AC-19) |
| Q37 | **New in draft 8. Stock DSF and the routine** (CE-25): a late DoCode result can finish a newer DoCode of the GB early; the final reply of an M291 whose box the abort closed comes right after the abort. Both reach a stock DSF untagged. Whether E1 may ship them to a stock DSF as they are | Open, for Tim; the E0 tests record the behaviour first (AC-18) |
| Q38 | **New in draft 8. The time from record to apply** is one main loop pass; it was not measured, and it is longer than in draft 7 while the main task waits on another channel (CE-7) | Open: the counter of CE-20 measures it in E1 on the lab printer |

Also unverified:
- The M122 figures are one reading on one printer and configuration.
- RAM figures are field counts; padding was not measured.
- The review reports were not available to this draft; section 11 rests on their summary.
- Everything in sections 4 and 5 is designed on paper; none of the K findings was tested.
- The USB bus CRC and retry below `DoTransferUsb` was not read; CE-17 (a) assumes nothing about it.
- RRF lines were re-read at 0dccb9efe for the capability offer and activation, the packet header and its writers,
  the read pointer, the packet loop, Code, MacroCompleted, MacroStarted, InvalidateChannel, the write phase,
  `FillBuffer`, `InvalidateBufferedCodes`, `InvalidateResources`, the reply FIFO and `HandleGCodeReply`,
  `GetResponseMessageType` and its callers, `MessageType`, `Platform::Message`, `DoEmergencyStop`,
  `RepRap::EmergencyStop`, `CheckTriggers`, `RequestMacroFile`, `TriggerItem::Check`, the sequence guard, the header
  checks and the USB transfer. DSF lines were re-read at 7f26d0ca for `ExtendedLink`, `Writer`, the SPI and USB packet
  writers and resend, the retry loop, the header checks, `LinkService.Execute`, `ProcessPacket`, `HandleObjectModel`,
  `HandleAbortFileRequest`, `InvalidateCodes`, `Invalidate`, `UpdateLinkCapabilities`, the `Processor` abort flags,
  Spin, `FilesAborted`, `Invalidate`, `LinkInterface` evaluations and `EmergencyStopAsync`, `Settings` and the systemd
  unit.
- Re-read for draft 6. RRF: `GuardSequence` and `CompleteGuardedTransfer`, the Message handler (DoCode result), the
  Lock, Unlock and InvalidateChannel handlers, the write phase from `:1484`, the AbortFile write, `InvalidateResources`,
  `FillBuffer`, `UnlockAll` and its callers, `HandleResult`, `StartNextGCode`, `AbortPrint`, `Push`, `DoEmergencyStop`,
  `DoFileMacro` and every `AsyncSystemMacroCode` call site, `ProcessEvent` and its caller, `MessageBox::TimeOut`,
  `MessageBoxClosed`, `AcknowledgeMessage`, `GCodeBuffer::Reset`, `PopState`, `AbortFile`, `RequestMacroFile`,
  `MessageAcknowledged`, `SetFinished`, `SentToSbc`, the local pushes, `MessageType`, the reply FIFO depth, the pause
  report. DSF: the SPI retry loop and sequence guard, `Pop`, `InvalidateRegular`, `Invalidate`, `AbortAllFilesAsync`,
  `FilesAborted`, `ResourceLocked`, the lock and unlock queueing, `CopyState`, `WaitForAcknowledgement`,
  `PendingReplies`, `StackState`, `DoFirmwareCode`, `HandleDoCode`, the Executed result write, `HandlePrintPaused`,
  `LinkInterface` evaluations, variables, movement lock and `InvalidateCodes`, the result matching in `LinkService`,
  M505. RFC 1982 section 3.2.
- Not checked for draft 6: whether a code of an older epoch can still run on the GB when RRF raises config.g, start.g
  or stop.g (for event macros the AutoPause GB is idle, `RRF/GCodes/GCodes.cpp:667-671`; daemon.g needs an idle
  Daemon, `:680-684`); whether a stale DeleteLocalVariable that RRF now drops can leave a variable that a later `var`
  of the new epoch trips over ("already exists"; the E0 test of K27 can cover it); how DSF reaches a running DoCode at
  the epoch change (CE-25).
- Re-read for draft 7. RRF: the InvalidateChannel handler, `IsIdle`, `IsCompletelyIdle`, `IsExecutingOnSbc`,
  `SetFinished`, `AbortFile`, `RequestMacroFile`, `ResolveMacroRequest`, `DoFileMacro`, `SetFileExecuting`,
  `DoingFile`, the start of daemon.g, `SpinGCodeBuffer`, the write phase (macro requests, AbortFile, DoCode),
  `FillBuffer` and its callers, `InvalidateBufferedCodes`, `InvalidateResources`, `UnlockAll`, `LockResource`,
  `HandleResult`, the homing states, the Lock, Unlock, Message, MacroCompleted and Code handlers, `GuardSequence`,
  the header checks, the capability offer, the flag comments of `GCodeBuffer.h`, `PacketHeader` and the transfer size.
  DSF: the SPI packet writer, resend lookup, retry loop and sequence guard, `CanWritePacket`, the buffer size,
  `Execute` and the packet loop, `HandleObjectModel`, `UpdateLinkCapabilities`, Spin steps 2 and 3, `Pop`,
  `InvalidateRegular`, `FilesAborted`, `AbortAllFilesAsync` and its callers, the lock and unlock queueing,
  `MovementLock`, M505, `JobProcessor.Abort`, `StopPrintAsync`.
- Re-read for draft 8. RRF: `requestsFromOtherTasks` and every use of it, `GCodes::Spin`,
  `SpinGCodeBuffer`, `StartNextGCode`, the SBC code loop of `DoFilePrint`, `CheckTriggers`, `IsTriggerBusy`,
  `DoEmergencyStop`, `EmergencyStop`, `AbortPrint`, `PauseSequenceAborted`, `DoFileMacro`, `StopPrint`, `UnlockAll`,
  the M291 and M292 handlers, `MessageBoxClosed`, `MessageBox::TimeOut`, the state `waitingForAcknowledgement` and
  the pause report in `RunStateMachine`, `Push`, the machine state constructor, `DoingFile`; `GCodeBuffer::Reset`,
  `IsCompletelyIdle`, `SetFinished`, `PopState`, `AbortFile`, `RequestMacroFile`, `ResolveMacroRequest`,
  `MacroFileClosed`, `MessageAcknowledged`, `GetResponseMessageType`, `Put`, `PutBinary`, `PutAndDecode`, the SBC
  flags and their comments in `GCodeBuffer.h`; in `SbcInterface.cpp` the task loop with the connection loss, the
  handlers of Code, PrintStopped, MacroCompleted, Lock, Unlock, EvaluateExpression, Message, MacroStarted,
  InvalidateChannel and ActivateLinkCapabilities, the refusal, the write phase, `InvalidateResources`, `FillBuffer`,
  `InvalidateBufferedCodes`, the file wait; `SbcInterface.h`; `WritePacketHeader` and every use of
  `resendPacketId`; the SBC start in `RepRap.cpp`; the two places in `GCodes2.cpp` that pass a code to the SBC; the
  CAN response timeout. DSF: `Execute` with the reset request and the connection end, `ProcessPacket`,
  `HandleDoCode`, `InvalidateCodes`, `UpdateLinkCapabilities`, `Invalidate`; in `Processor` the abort flags,
  `AbortAllFilesAsync`, Spin, `DoFirmwareCode`, `HandleReply`, `WaitForAcknowledgement`, `MacroFileClosed`,
  `AllFilesAbortResent`, `MessageAcknowledged`, `DoMacroFile`, `InvalidateRegular`, `Invalidate`; the result write in
  `Executed`; the prioritized codes in `CodeProcessor` and `SimpleCode`; the packet writers and resend lookups of
  both adapters; `Writer.WritePacketHeader`. The v7 contract, section 15.
- Not verified for draft 8: the time of a main loop pass and how many CAN requests one step issues (Q38); whether
  `SetFinished(true)` on an unsent text code leaves the string parser in a usable state in every case (the next
  command of the same line is decoded, `RRF/GCodes/GCodeBuffer/StringParser.cpp:1219-1232`); whether
  `PauseSequenceAborted`, run by the SBC task inside `InvalidateResources`, can disturb the main task (v7 already
  calls it from the SBC task, `RRF/SBC/SbcInterface.cpp:901`); what RRF's main task does between `GCodes::Spin`
  calls; which stage a stock DSF's DoCode is in when its channel is aborted (K19), so how often its result comes
  late; whether every line named for the stock DSF is upstream code.
- Not verified by the reviewer of draft 6, and not checked for draft 7: whether an RRF state machine that survives
  `AbortFile(true)` issues a move without locking again. G28 locks again before it waits for its move
  (`RRF/GCodes/GCodes4.cpp:395`); the other state machines were not read. Since FD-27 the apply releases no lock while
  a code of the GB runs, so this matters only where a lock is released under a running state machine for another
  reason.
- Not verified by the reviewer of draft 6, and only partly checked for draft 7: whether `FilesAborted(true)` on File
  already leads to `AbortAllFilesAsync` through the job abort in v7, which would change the status of V7-46 and the
  need for CE-15. In DCS only `AbortAllFilesAsync` sets `_allFilesAborted` (`DCS/Link/Channel/Processor.cs:822`); its
  only caller is `LinkInterface.AbortAllAsync` (`DCS/Link/LinkInterface.cs:557-561`), called by `abort`
  (`DCS/Codes/Handlers/KeywordHandler.cs:170`) and the IPC command InvalidateChannel
  (`DCS/Commands/Generic/InvalidateChannel.cs:36`). `FilesAborted(true)` calls `JobProcessor.Abort`
  (`Processor.cs:753-758`), which cancels the job and closes its files (`DCS/Files/JobProcessor.cs:929-945`); the job
  loop then writes PrintStopped(abort) (`:763-767`). Whether an IPC client such as DWC or a plugin, or a cancelled code
  of the job, reaches one of the two callers was not traced.

## 13. References

Protocol references of the pattern survey (opened by the survey, not again for this draft): HTTP/2 RFC 9113; QUIC
RFC 9000; SCTP RFC 9260, RFC 6525; SSH RFC 4254; RFC 1982; MQTT 5.0; AMQP 1.0 and 0-9-1; JSON-RPC 2.0; USB 2.0; Modbus
TCP; CAN 2.0. Section numbers are in [sbc-link-v8-patterns.md](sbc-link-v8-patterns.md).
Standards named by CE-19: IEC 61000-4-3, IEC 61000-4-6, EN IEC 61000-6-2 (as in v2 "Normative basis").

Repository: [sbc-link-v8-logic-adaptations.md](sbc-link-v8-logic-adaptations.md),
[sbc-link-v7-contract.md](sbc-link-v7-contract.md), [sbc-link-v8-catalogue.md](sbc-link-v8-catalogue.md),
[sbc-link-requirements.md](sbc-link-requirements.md), [sbc-link-current-state.md](sbc-link-current-state.md),
[sbc-link-findings-upstream.md](sbc-link-findings-upstream.md), [dsf-findings-upstream.md](dsf-findings-upstream.md),
[open-findings.md](open-findings.md), [rrf-local-trigger-actions.md](rrf-local-trigger-actions.md),
[lab-test-plan.md](lab-test-plan.md).
