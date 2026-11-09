# SBC link: channel epoch on v7 (requirements; replaces the v8 session layer)

Status: draft 7, 2026-11-09. Nothing here is implemented. No code was changed for it. The decisions of section 10 are
Tim's.
Draft 5 replaced the v8 session layer of drafts 1 to 4 by a minimal design: a **channel epoch on protocol version 7**,
switched on as a link capability, plus **evidence gates** that decide later whether more is needed. Tim decided this
on 2026-11-09 after three expert reviews of draft 4 (section 11). Draft 6 records his decisions after a second review
round of draft 5 (section 10.3): an epoch change ends everything of the old epoch on both sides, locks and DoCode
included (CE-25); one DSF epoch plus an RRF gate bit instead of a second epoch (CE-24); a stop never goes into a
SequenceGuard repeat (CE-16); a level id only on evidence (EG-6). Draft 7 applies an independent review of draft 6
(section 11, decisions FD-24 to FD-30 in section 10.4): the abort releases the locks only of an idle GB (CE-25 (a));
the receipt of InvalidateChannel acts once (CE-7); only the current epoch passes the filters (CE-5); the stop rule and
the halt rule are a link milestone of their own, L1 (section 7.2). The file keeps its path so that links stay valid.
Section 10.5 lists what happened to every requirement of drafts 1 to 4.
Audience: Tim, later Christian Hammacher (Duet3D). The reader knows DSF and RRF, not the working papers.

Scope: the channel epoch (capability, tag, filters, the two companion fixes, the end of the old epoch, RRF's abort
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
  these commits for draft 5, 6 or 7, including those that came from the review reports.
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
  had reversed it with an own protocol version; draft 5 drops that (FD-1). The channel epoch is a link capability on
  v7, offered and activated like PhaseGuard, SequenceGuard and ExtendedHeader (v2 "Implementation status").
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

| # | v7 assumption | Evidence | What drafts 5 to 7 do |
|---|---|---|---|
| A-1 | Replies and stack events belong to the first buffered code of the channel (`DCS/Link/Channel/Processor.cs:1102-1117`) | DF-T10 (printer, 2026-11-08); OF-T4 | Replies and stack events of an older epoch are dropped (CE-14). Matching by position within an epoch stays (FD-3); code identity only on evidence (EG-1) |
| A-2 | The pipeline depth is exactly one transfer, counted, never acknowledged (`RRF/SBC/SbcInterface.cpp:257-268`) | DF-T2, DF-T12 (printer 2026-11-08) | The abort windows no longer count transfers (CE-13, CE-14). The resend lookup and `BytesReserved` stay; NEW-1 is VF-1 |
| A-3 | Abort (`InvalidateChannel`) needs no answer and no epoch | DF-T1, T2, T10, T11, T12 | The epoch (CE-5 to CE-15); RRF never refuses InvalidateChannel (CE-7) |
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
| 2 InvalidateChannel resends | RRF refuses InvalidateChannel when the GB mutex is busy (`RRF/SBC/SbcInterface.cpp:897`, `:914-918`) after it acted (`:880-895`) | Gone: RRF never refuses it (CE-7) |
| 17 crossing macro requests | Written by RRF before it read the abort | Tagged e: dropped by the filter instead of by #134's transfer window (CE-12, CE-14) |

## 3. Principles

| ID | Principle | Status in draft 7 |
|---|---|---|
| PR1 | Identity, not position or timing | Partly: the epoch identifies what an abort cancelled; replies are still matched by position within an epoch (FD-3) |
| PR2 | Order is kept within a channel; across channels the v7 order stays | Holds: nothing reorders packets |
| PR3 | A few absolute classes; one expedited class | Dropped (no classes). What remains is CE-16 (milestone L1): a pending stop overtakes the retries of a transfer |
| PR4 | Cancellation changes an epoch | Holds, without an acknowledgement packet: InvalidateChannel carries the new epoch, is never refused and acts once (CE-7). An epoch change ends everything of the old epoch on both sides (CE-25) |
| PR5 | Flow control by absolute credit | Dropped; only on evidence (EG-3) |
| PR6 | A refusal means nothing was done; re-sent content is identical | Holds as fixes: CE-7, CE-8, VF-3, VF-4 |
| PR7 | Request state lives on the (channel, epoch) object | Holds: the epoch is kept on DSF's channel `Processor` and on RRF's `GCodeBuffer` (CLAUDE.md rule 3). DSF's evaluation and variable requests live on `LinkInterface` (`DCS/Link/LinkInterface.cs:144-159`, `:194-208`); the epoch change reaches them through a hook (CE-25) |
| PR8 | The firmware never retransmits and keeps no per-message state | Holds as in v7 (`RRF/SBC/DataTransfer.h:238-241`) |
| PR9 | **Logical compatibility:** the capability changes how events travel, not which orders the logic can see. Every order the logic sees with ChannelEpoch is one it can also see in v7 with the catch code of DSF 7f26d0ca and RRF 0dccb9efe; the capability may only remove orders (those the catch code filters today). So the logic meets no new concurrency and no new wait cycle | Holds; every requirement has a PR9 column, and section 4.10 checks every V7 item |
| PR10 | Link faults never end a print (draft 4) | Dropped (FD-10): no session resume. v2 R1 stays the goal of the link; "DCS never exits on link data" stays as VF-1 |

## 4. Requirements

Columns: **Layer** = L or S. **Removes** = catalogue items (`CC-n`) whose catch code the requirement makes unnecessary
in an epoch connection. **PR9** = why the requirement adds no order and no unbounded wait.

### 4.1 Capability, activation, lifetime

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-1 | L | **Capability bit.** A link capability `ChannelEpoch` = 8 MUST be added after PhaseGuard 1, SequenceGuard 2, ExtendedHeader 4 (`RRF/SBC/SbcMessageFormats.h:139-144`, `DCS/Link/Protocol/Shared/ExtendedLink.cs:13-39`). It is offered in `limits.sbcLinkCapabilities` (`RRF/Platform/RepRap.cpp:324`), shown active in `state.sbcLinkCapabilities` (`:369`) and activated with the existing ActivateLinkCapabilities 0x4D50 (`RRF/SBC/SbcInterface.cpp:1334-1342`, `RRF/SBC/DataTransfer.cpp:826-835`). No new packet type, no new header, protocol version stays 7 (`SbcMessageFormats.h:24`) | — | Link only |
| CE-2 | L | **Offered everywhere.** RRF MUST offer ChannelEpoch on every board and transport. Today it offers capabilities only inside the SAME70 block (`RRF/SBC/DataTransfer.cpp:475-479`); ChannelEpoch is set outside it. DSF's USB adapter MUST support and activate it: today it supports no capability and never queries (`DCS/Link/Adapter/USB.cs:808`, `:815`; the query needs a supported capability, `DCS/Link/LinkService.cs:1547`). ChannelEpoch concerns packets, not SPI transfers, so it does not depend on the third transfer buffer | — | Link only |
| CE-3 | L | **Activation before channel traffic.** Activation rides on an object model round trip (`DCS/Link/LinkService.cs:1539-1575`: query of `limits.sbcLinkCapabilities`, ActivateLinkCapabilities, check of `state.sbcLinkCapabilities`). After every connect and reset, DSF MUST NOT run the channel Spins (`:459-462`), which write every Code, MacroStarted, MacroCompleted, InvalidateChannel, Lock and Unlock, until the ChannelEpoch state is known: ActivateLinkCapabilities with ChannelEpoch written, or the capability not offered, or a bound expired (Q23), after which the connection runs without it. The bound MUST be a timer: the capability query (`:1546`) waits behind the other object model queries, of which DSF writes one per transfer (`:470-478`), and an answer for another key leaves the query at the head "sent" for good (`:714-723`, K16), so no count of transfers bounds the hold. VF-8 is therefore a prerequisite of E2 (section 7.2). RX processing goes on meanwhile. ChannelEpoch MUST NOT wait for the sequence-range condition of ExtendedHeader (`DCS/Link/Adapter/SPI.cs:940-945`); a later request MUST repeat the whole set, because RRF replaces the active set (`RRF/SBC/DataTransfer.cpp:834`) and leaves the extended link if ExtendedHeader is missing (`:829-832`) | — | No new order: events are only delayed, as on a slow link in v7. A new wait, at connect only, bounded; v7 already holds codes at connect until the first buffer report (`DCS/Link/LinkInterface.cs:613`, V7-14). The bound MUST stay well below RRF's 3 s macro wait (`SbcMessageFormats.h:42`), so a macro request at connect does not time out |
| CE-4 | S | **Epoch lifetime.** Each side keeps one epoch per channel. Both start at 0 when ChannelEpoch is activated in a connection (RRF: when the activation sets the bit and it was not set before, and it marks every `codeEpoch` invalid then; DSF: when it writes the activation). Nothing of the epoch is stamped, set or written while ChannelEpoch is not active. The epochs end with the connection: RRF clears ChannelEpoch with every connection reset (`RRF/SBC/DataTransfer.cpp:1431`) and marks every `codeEpoch` invalid in `InvalidateResources` (`RRF/SBC/SbcInterface.cpp:1717-1780`); DSF resets its epochs where it ends the connection (`DCS/Link/LinkService.cs:389-396`, next to `_queryLinkCapabilities`). DSF MUST NOT reset them in `Processor.Invalidate()` (`DCS/Link/Channel/Processor.cs:1491-1538`), because that also runs on every emergency stop (`DCS/Link/LinkService.cs:1511-1517`, `DCS/Link/LinkInterface.cs:606-612`), while RRF keeps its epochs across a stop | — | Not visible to the logic |

### 4.2 Tag

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-5 | S | **Carrier and format.** Once ChannelEpoch is active, `PacketHeader.resendPacketId` (16 bit, `RRF/SBC/SbcMessageFormats.h:101-107`) carries the tag in both directions: bit 15 = tagged, bits 0–5 = epoch, bits 6–14 = 0. 0 means untagged (v7). The field is free outside ResendPacket: DSF writes 0 (`DCS/Link/Protocol/Writer.cs:52`, `DCS/Link/Adapter/SPI.cs:789`, `USB.cs:683`) and reads it only in the resend lookup (`SPI.cs:831`, `USB.cs:729`, called from `DCS/Link/LinkService.cs:613-614`); RRF writes it only in `WritePacketHeader` (`RRF/SBC/DataTransfer.cpp:2290-2303`) and never reads it. In ResendPacket the field keeps its v7 meaning (`RRF/SBC/DataTransfer.h:238-241`). Payloads MUST NOT grow: RRF advances its read pointer only by what each handler reads (`RRF/SBC/DataTransfer.cpp:628-640`; e.g. `ReadCodeChannel` reads 4 bytes, `:705-709`), so a longer payload would garble the later packets of a stock RRF. The channel stays where it is (payload or destination bit). The epoch has 6 bits because the reply path carries it in the free bits of RRF's `MessageType` (CE-11). **Comparison (FD-21, FD-28, FD-29):** a tagged packet passes only if its tag equals the channel epoch; every other tagged packet is dropped and counted (CE-20), until a level id exists (EG-6). The one exception is InvalidateChannel, which DSF always tags with the next epoch e+1 (CE-6, CE-13): RRF's receipt acts only if the tag follows the channel epoch as an RFC 1982 serial number with SERIAL_BITS = 6 (RFC 1982 section 3.2), and an equal or preceding InvalidateChannel is a repeat that is counted and ignored (CE-7). The serial comparison also sorts the drops for the counters: stale when the tag precedes the channel epoch (the pair at distance 32, for which RFC 1982 defines no order, counts as stale), ahead when it follows. On an intact link no tag is ahead: RRF raises its epoch only at DSF's InvalidateChannel, which it never refuses (CE-7) and reads before every packet DSF wrote after it, and RRF's tags are RRF epochs. Draft 6 let an ahead tag pass; it is dropped now, because a packet 33 to 63 aborts old also looks ahead. If RRF ever missed an InvalidateChannel, it would drop the channel's DSF packets as ahead until the next InvalidateChannel of the channel, whose tag follows and resynchronises; the counter shows it (CE-20). A stale packet is recognised while fewer than 64 aborts of its channel happened during its flight; its counter class is right below 32 (Q28). **Untagged packets pass:** tag 0 is never dropped by the epoch, also in an epoch connection. This is the path for RRF's packets written before RRF read the activation, while DSF has switched already when it wrote it (CE-4), such as a config.g request made before activation (CE-3), and for every packet of a peer that has not activated | — | No order change; a stock peer never sees a tag (CE-18). Dropping a tag that is not current removes orders only |
| CE-6 | S | **What is tagged.** DSF tags every packet with a channel field: Code, MacroStarted, MacroCompleted, InvalidateChannel (with the new epoch), Lock, Unlock, EvaluateExpression, SetVariable, DeleteLocalVariable, SetLastCodeResult; and every chunk of a DoCode result (a Message whose destination is the DoCode's channel, `DCS/Codes/Pipelines/Executed.cs:175-209`) with the epoch of its DoCode, passed as a parameter. RRF tags code replies (Message with `BinaryCodeReplyFlag`), the stack events ExecuteMacro, AbortFile, WaitForAcknowledgement, MacroFileClosed and MessageAcknowledged, DoCode, PrintPaused (with File's epoch) and its answers Locked, EvaluationResult and VariableResult, which carry the tag of the request they answer; the source of each tag is the table under CE-12. Untagged stay: ResendPacket, ObjectModel, CodeBufferUpdate, generic and other targeted messages, file requests and results, and DSF's other packets (EmergencyStop, Reset, GetObjectModel, SetPrintFileInfo, PrintStopped, other Messages, ObjectModelKeyChanged, 0x4D50, IAP). Draft 5 left DoCode, PrintPaused and the answers untagged; FD-16, FD-18 and FD-19 changed that | — | Tagging alone changes nothing |

### 4.3 The two companions

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-7 | S | **Companion 1: RRF never refuses InvalidateChannel.** Today RRF acts before it refuses: it flags `codeReplySurplus`, voids an unsent request and ends a macro wait (`RRF/SBC/SbcInterface.cpp:880-895`), then waits 10 ms for the GB mutex (`:897`) and refuses (`:914-918`); this causes the resends of T11 (DF-T11, CC-6). RRF MUST split the handler. **Receipt**, without the GB mutex, always: in an epoch connection act only if the tag follows the channel epoch, else count and ignore the packet (CE-5, FD-28), and record the tag as the channel epoch (CE-4); end a macro wait with an error (`:894`), purge the channel's ring codes (`InvalidateBufferedCodes` needs only a critical section, `:2527-2529`), set "apply pending". **Apply**, with the GB mutex: `PauseSequenceAborted`, `AbortFile(true)`, `FileAbortSent` (`:901-903`); void a macro request that has not gone out (`MacroRequestSent`), as `InvalidateResources` does on a link loss (`:1766-1769`), because `AbortFile(true)` has popped the level that the request belongs to (CE-12); in an epoch connection end a DoCode wait (CE-25 (b)); then, **only if the GB is completely idle** (`gb->IsCompletelyIdle()`: state normal and the buffer neither ready nor executing, `RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:947-955`), `UnlockAll(gb)`, so that the abort releases the channel's locks (`RRF/GCodes/GCodes.cpp:5650-5668`; FD-17, FD-27, CE-25 (a)). Otherwise a code of the GB still runs, e.g. a G28 in its state machine, and releases the locks when it ends, as in v7 (`UnlockAll` in `HandleResult`, `RRF/GCodes/GCodes2.cpp:5112-5116`). The apply runs at once if the 10 ms try succeeds, otherwise wherever the SBC task next holds that mutex, at the latest at the start of the mutex section of the write phase (`SbcInterface.cpp:1530-1533`), before the GB's AbortFile is written (`:1549`). Until E3 the fork catch code of the v7 path stays in both parts, each now run once: in the receipt the `codeReplySurplus` flag of a code waiting for its macro (`:886-889`, RRF #46) and the voiding of an unsent request (`:893`, RRF #43); in the apply `MacroFileClosedSent` (`:904`, RRF #45) and the flag of a code still running (`:908-911`). The apply MUST NOT purge the ring again (today `:905`), because codes of the new epoch may have arrived since the receipt. `FillBuffer` MUST hold the channel's codes while the apply is pending (add to `:1892-1894`). Likewise the main task MUST NOT start other new work on that GB while the apply is pending (FD-31): no trigger or daemon macro, no text code from PanelDue or USB; it skips the GB in its spin and does not wait, so it releases the GB mutex as usual (`RRF/GCodes/GCodes.cpp:590`) and the apply can run. `FillBuffer` runs on the main task (`RRF/GCodes/GCodes.cpp:734`, `:813`), so it MUST read the flag inside its critical section (`SbcInterface.cpp:1901-1903`), where the receipt's purge cannot run, and the receipt MUST set the flag before it reads the next packet; a bool read needs no other guard. The flag reuses `invalidationPending` (`RRF/GCodes/GCodeBuffer/GCodeBuffer.h:422`); its name and its comments ("Accessed only by the SBC task", `:420`; "the next one is a resent copy", `:422`) MUST change, e.g. to `abortApplyPending`, set by the receipt, cleared by the apply, read by `FillBuffer`. The comment at `:420` is wrong already in v7 for `invalidated` (`:421`), which `FillBuffer` reads (`SbcInterface.cpp:1892`). `FileAbortSent` MUST stay, else RRF would answer DSF's own abort with AbortFile(all), which DSF answers with another InvalidateChannel (CE-15). This is a v7 fix that works with a stock DSF. **Repeats (FD-28):** the first-copy guard of `codeReplySurplus` (`firstCopy = !IsInvalidationPending()`, `SbcInterface.cpp:882`) is dropped, because DSF writes no copy once RRF never refuses. A repeated transfer can still deliver an InvalidateChannel twice: on SPI without SequenceGuard, RRF processes again a transfer that DSF repeats because it missed RRF's response (`RRF/SBC/DataTransfer.cpp:923-929`, `:1132-1137`); SequenceGuard is offered on SAME70 only (`:475-479`). Acting twice would purge the codes stored since, end a new macro wait and abort new files with `FileAbortSent`, so that DSF never learns of that abort. In an epoch connection the receipt ignores the repeat by its tag. Without ChannelEpoch it cannot tell a repeat from a new abort; the repeated transfer then repeats its other packets too, codes included, as in v7. `codeReplySurplus` itself (`GCodeBuffer.h:405`) is deleted in E3 | CC-6 (#161), CC-8's first-copy role | v7 already ends the wait before the mutex (`SbcInterface.cpp:883-895`, the breaker of C-4); RRF still runs codes of the channel only after the apply, as v7 does after an accepted InvalidateChannel. Removes the resend and its reordering. The lock release on an idle GB has the effect of an Unlock right after the abort, a v7 order; no lock is released while a code of the GB runs (CE-25). A voided macro request is one that DSF never sees, as after a link loss. Ignoring a repeat removes orders only |
| CE-8 | S | **Companion 2: copies keep their tag.** DSF MUST write a resent copy with the tag of the original. Today the copy gets 0, because `ResendPacket` writes it through `WritePacket` (`DCS/Link/Adapter/SPI.cs:837` → `:782-790`; `USB.cs:735` → `:676-684`). The tag is passed as a parameter, not looked up | — | No order change |

### 4.4 RRF side of the epoch

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-9 | S | **Epoch of a code and of the GB's work.** RRF MUST stamp every code it accepts into the ring with the tag of its Code packet, the one that passed the filter (CE-10; FD-29), in the free `BufferedCodeHeader.padding` (`RRF/SBC/SbcMessageFormats.h:368-373`; written today only `isPending` and `length`, `RRF/SBC/SbcInterface.cpp:424-426`); an untagged code gets an invalid stamp (CE-5). Draft 6 stamped the channel epoch; the two are equal for every tagged code that passes, but the packet's own tag stays right if a level id is added later (EG-6). `FillBuffer` MUST carry the stamp into a per-GB `codeEpoch` (valid bit + 6-bit epoch) when it hands the code over (it reads the ring header at `:1910` and hands the code over with `PutBinary` at `:1945`). **Two tag sources (FD-19, was Q22; the table below lists every packet):** (1) the output of a code DSF sent, and the helper macros RRF requests while it executes that code, carry `codeEpoch`; (2) work that RRF starts itself carries the channel epoch at the moment RRF raises it. For a macro request without a code (`AsyncSystemMacroCode`, which makes `fromCode` false, `RRF/GCodes/GCodes.cpp:3699`) and for a text code (`GCodeBuffer::Put`, `PutAndDecode`, `RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:294`, `:334`, `:345`: PanelDue, USB, trigger actions), `codeEpoch` MUST become the channel epoch then, as in draft 5. For an event that RRF raises while a code of an older epoch may still run on the GB (the box timeout, PrintPaused), the epoch MUST be kept with the event and MUST NOT overwrite `codeEpoch`, else that code's later replies would carry the new epoch (Q31). RRF's own AbortFile(all) needs no store: an InvalidateChannel between its raise and its write voids it (the apply calls `FileAbortSent`, `RRF/SBC/SbcInterface.cpp:903`, before `:1549`, CE-7), so the channel epoch at the write is the one at the raise. Without source (2), a trigger request after an abort of the Trigger channel would carry the epoch of the last code before the abort and DSF would drop it (RRF then times out after 3 s and the trigger is lost), and the closal of a system macro without codes would be dropped, so DSF's level would wait for it for good (E-17). `InvalidateResources` marks `codeEpoch` invalid; an invalid `codeEpoch` gives no tag (v7 behaviour) | — | Not visible to the logic |
| CE-10 | S | **Filter of stale DSF packets (FD-16, was Q25; FD-29).** RRF MUST drop and count every tagged DSF channel packet whose tag is not the channel epoch, stale or ahead (CE-5), before any side effect: Code, MacroStarted, MacroCompleted, Lock, Unlock, EvaluateExpression, SetVariable, DeleteLocalVariable, SetLastCodeResult and the chunks of a DoCode result. Side effects that the drop must precede: a Code resolves a macro wait at `RRF/SBC/SbcInterface.cpp:383-392` before its buffer check (`:395`); MacroCompleted resolves a wait or pops (`:558-597`); MacroStarted resolves a wait (`:839-842`); a DoCode result finishes the GB (`:774-793`). A dropped request gets no answer; DSF has failed it already (CE-25). InvalidateChannel is never refused; its receipt ignores only a repeat, whose tag does not follow the channel epoch (CE-7). Draft 5 applied stale copies of the other types and answered EvaluateExpression and SetVariable; draft 6 let an ahead tag pass | CC-5 (RRF #43) together with CE-12 | Removes orders only: a cancelled code no longer runs after the abort, a stale request no longer acts on the new epoch. A request without answer is failed by DSF at the epoch change, as `InvalidateCodes` fails all of them on a stop (`DCS/Link/LinkInterface.cs:615-641`) |
| CE-11 | S | **Tag of code replies.** `GCodeBuffer::GetResponseMessageType` (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1335-1344`) is the only place that builds the reply type of a code from the SBC (`(1u << channel) \| BinaryCodeReplyFlag`, `:1340`; no other code sets `BinaryCodeReplyFlag`). It has 15 call sites in 14 lines: `GCodes.cpp:3706`, `:4467`, `:4520`, `:4592`, `:4598`, `:6051`; `GCodes2.cpp:2292` (M122); `GCodes4.cpp:1894`, `:1906`; `GCodes6.cpp:140`; `StringParser.cpp:473`; `GPIO/GpInPort.cpp:90` (two); `SbcInterface.cpp:1568`, `:1571`. It MUST put `codeEpoch` into the free `MessageType` bits: bits 18–23 = epoch, bit 26 = valid (`RRF/Platform/MessageType.h`: destinations end at 0x3FFFF, `:39`; flags are bits 24, 25, 27–31, `:43-50`; bit 26 is unused). The type travels unchanged through the reply FIFO's 32-bit types (`RRF/Platform/OutputMemory.h:178-179`). `DataTransfer::WriteCodeReply` (`RRF/SBC/DataTransfer.cpp:1488-1503`) MUST move it into `resendPacketId` (CE-5) and clear bits 18–23 and 26 in the type it writes. Chunks of different epochs never merge, because the push merge compares the whole type (`SbcInterface.cpp:2369`). Two places compare a type for equality, both with `GenericMessage` (`GCodes2.cpp:2293`, `GCodes6.cpp:141`); an SBC reply type never equals it, with or without the bits; two compare with `NoDestinationMessage` = 0 (`RRF/Platform/RepRap.cpp:762`, `:2367`). Deferred M122 output keeps the type it was given (`RRF/Platform/RepRap.h:57`, `RepRap.cpp:762-766`), so it carries the epoch of the code that asked. Routing tests single bits only (`RRF/Platform/Platform.cpp:3254-3256`, `:3266-3273`, `:3314-3321`). This answers Q6: M122, `GpInPort` and the SD speed test get the tag too | CC-7 (RRF #46 `codeReplySurplus`, `GCodeBuffer.h:405`) in epoch mode | Removes orders only: a reply of an older epoch no longer completes a code of the new one |
| CE-12 | S | **Stack events and requests.** The stack events of CE-6 MUST carry the tag that the table below gives them: `codeEpoch` of their GB at the time they are written (source 1), or for work that RRF started itself the epoch of source 2 (CE-9); `FillBuffer` hands no code to a GB while a closal, an acknowledgement, an abort or a prompt is unsent (`RRF/SBC/SbcInterface.cpp:1892-1894`), so `codeEpoch` cannot change in between. A macro request (`:1513-1527` blocking, `:1597-1611` non-blocking) whose `codeEpoch` is valid and not the channel epoch MUST NOT be written; it fails as follows (checked against the code for draft 7). **Blocking:** such a request is made after the receipt by a code of the old epoch that goes on, e.g. a G28 whose homing file the apply popped and which then requests the next one (OF-T10, `RRF/GCodes/GCodes4.cpp:358-413`). The receipt ended only a wait that existed when it came, and the apply ends none; the main task waits for this request while it holds the GB mutex (V7-87). RRF MUST end that wait as the receipt does, with `MacroRequestSent` and `ResolveMacroRequest(true, false)` (`SbcInterface.cpp:893-894`). This sets `macroFileError` (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1286-1291`), so `RequestMacroFile` returns false at once (`:1278-1283`) instead of after its 3 s timeout (`:1266-1273`), and the caller goes on as for a missing file; a G28 ends with "Homing file … not found" (`GCodes4.cpp:383-389`), a reply of the old epoch that DSF drops. Draft 6 only cleared the name, which wakes nothing. **Non-blocking:** only the Daemon GB outside a macro requests without waiting (`GCodeBuffer.cpp:1258-1261`), that is daemon.g. `DoFileMacro` has pushed and opened the macro's level by then (`RRF/GCodes/GCodes.cpp:3715-3725`, `:3761`), in the same spin under the GB mutex (`:590`). `macroFileError` is read only inside `RequestMacroFile` (`GCodeBuffer.cpp:1278`), so setting it would do nothing. The request is written inside the mutex section of the write phase, so after the apply, whose `AbortFile(true)` has popped that level (it has a file id, `RRF/GCodes/GCodeMachineState.cpp:193-218`) and which voids the request itself (CE-7); the Daemon GB requests daemon.g again after its dwell (`GCodes.cpp:680-690`). The tag check is the second guard there. This covers state machines that go on after an abort (OF-T10) and daemon.g (OF-T11, A5). The same holds for a DoCode (`SbcInterface.cpp:1613-1634`): one whose `codeEpoch` is valid and stale MUST NOT be written; the apply ends it (CE-25 (b)) | CC-5 (RRF #43), CC-4 (RRF #45) in epoch mode; A5 | Removes orders only: a macro request that fails is a v7 outcome (InvalidateChannel ends a wait with an error, V7-53); it fails at once instead of after 3 s, a shorter wait; for DoCode see CE-25 |

**Tag source per RRF→DSF packet (FD-19).** (1) = `codeEpoch`, the epoch of the code that the output belongs to; (2) =
the channel epoch when RRF raised the work. Request numbers from `RRF/SBC/SbcMessageFormats.h:269-349`; `RRF/` paths
shortened to the file.

| RRF → DSF | Raised by | Tag | Code |
|---|---|---|---|
| 3 Message with `BinaryCodeReplyFlag` | a code DSF sent; also the output of M122, `GpInPort` and the SD speed test (CE-11) | (1) | `GCodeBuffer.cpp:1335-1344` |
| 4 ExecuteMacro, `fromCode` true | a code DSF sent and the helper macros it needs: M98, homing files, tool change files, deploy and retract probe, pause.g, filament-change.g, resume.g, cancel.g, stop.g of a code, config-override.g | (1) | `GCodes.cpp:3699`; e.g. `GCodes4.cpp:383`, `:435-518`, `:565-578`, `:844`; `GCodes3.cpp:1215-1245`; `GCodes2.cpp:799-804`, `:1205`, `:3462` |
| 4 ExecuteMacro of a text code (`lastCodeFromSbc` false) | a code from PanelDue, USB or a trigger action | (1), set to the channel epoch when RRF took the code | `GCodes.cpp:3699`; `GCodeBuffer.cpp:294`, `:334`, `:345` |
| 4 ExecuteMacro, `AsyncSystemMacroCode` | RRF: config.g `GCodes.cpp:414`, daemon.g `:690`, triggerN.g `:1052`, start.g `:4001`, event macros `GCodes3.cpp:1432`, stop.g at the end of a print `GCodes4.cpp:844` | (2), in `codeEpoch` | `GCodes.cpp:3688-3699` |
| 5 AbortFile, single | M99; a box cancelled with abort inside a file after M292 | (1) | `GCodes.cpp:593-615` |
| 5 AbortFile, single, after the box timeout | the timeout of a box with J1 inside a file | (2), kept with the event | `Platform/MessageBox.cpp:172-177`; `GCodeBuffer.cpp:1328`; `GCodes.cpp:593-615` |
| 5 AbortFile(all) | RRF's own abort: `AbortPrint` after a state machine error, a stack overflow, the GB part of DSF's emergency stop; File2 also from File | (2), the channel epoch at the write (CE-9) | `GCodes.cpp:3623-3640`, `:1081-1087`, `:2059-2063`; `GCodes4.cpp:168-321` |
| 13 WaitForAcknowledgement | M291 S2 or higher, manual probing | (1) | `GCodes7.cpp:98-115`; `GCodes6.cpp:149-157` |
| 14 MacroFileClosed | the end of a macro | (1): the epoch of the macro's codes, or for a macro without codes the one set when it was requested | `GCodeBuffer.cpp:1294-1307` |
| 15 MessageAcknowledged after M292 | a code on any channel | (1) of the box's GB | `GCodes7.cpp:121-172` |
| 15 MessageAcknowledged after the box timeout | RRF | (2), kept with the event | `Platform/MessageBox.cpp:172-177`; `GCodes7.cpp:158-172` |
| 7 PrintPaused | RRF: M25 or M226 of any channel, filament and stall events, power fail, trigger 1 | (2) of File, kept next to `reportPause` (Q32) | `GCodes4.cpp:2012-2022`; `SbcInterface.h:184-196` |
| 12 DoCode | a text code that RRF passes on | (1) | `SbcInterface.cpp:1613-1634` |
| 9 Locked, 11 EvaluationResult, 16 VariableResult | an answer to a DSF request | the tag of that request | `SbcInterface.cpp:607-634`, `:682-760`, `:928-1109` |
| 0 ResendPacket, 1 ObjectModel, 2 CodeBufferUpdate, other Messages, file requests | not bound to a channel's work | none | — |

### 4.5 DSF side of the epoch, the end of the old epoch, RRF's abort gate

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-13 | S | **Epoch on the channel `Processor`.** DSF MUST keep the epoch on `Processor` and increment it where it writes InvalidateChannel (Spin step 3, `DCS/Link/Channel/Processor.cs:931-937`, only when the write succeeded); there it also ends the old epoch's requests (CE-25). In an epoch connection it replaces `_allFilesAbortedSent`, `_allFilesAbortUnconfirmed` and `_allFilesAbortResent` (`:119-136`; on the v7 path they stay until E3) with their uses (`:883-905`, `:1285-1290`, `:1357-1362`, `AllFilesAbortResent`, the out parameter of `ILinkAdapter.ResendPacket` and `DCS/Link/LinkService.cs:620-626`). `_allFilesAborted` (`:117`) stays: it is the trigger to write InvalidateChannel. When the epoch changes or is reset, DSF MUST discard the channel's partial reply (`_lastPartialMessage`, `:1184-1189`), so that no chunk of an older epoch is prepended to the next reply (K11 for aborts). Codes after the abort go out in the next Spin; RRF holds them until the apply (CE-7) | CC-2 (#134), CC-3 (#158), CC-6 (#161) | Same per-channel order as with #161, where DSF held the codes; now RRF holds them. Removes E-16 (the channel's wait for the acceptance of InvalidateChannel) |
| CE-14 | S | **Filter of stale RRF packets.** At the top of `LinkService.ProcessPacket` (`DCS/Link/LinkService.cs:609`), DSF MUST drop every tagged RRF packet whose tag is not the epoch of its channel, stale or ahead (CE-5, FD-29), and count it per channel and request type: code replies, ExecuteMacro, AbortFile, WaitForAcknowledgement, MacroFileClosed, MessageAcknowledged, DoCode, PrintPaused (against File's epoch) and the answers Locked, EvaluationResult and VariableResult. The channel comes from the payload or the reply's destination bit (`DCS/Link/Channel/Manager.cs:107-121`). A dropped reply is not routed to the console. A stale DoCode is not run; DSF fails it with an error (CE-25, LA-10). **Answers are filtered too (changed from draft 5, consequence of FD-16):** draft 5 left them unfiltered because DSF merges evaluations per (channel, expression) (`DCS/Link/LinkInterface.cs:144-153`) and a dropped answer would hang later identical ones. Now the epoch change fails every request of the old epoch first (CE-25), so no request of the new epoch waits for a dropped answer. An unfiltered stale answer would instead reach a request of the new epoch: an EvaluationResult resolves the first written request with the same channel and text (`DCS/Link/LinkService.cs:1009`), a VariableResult the first with the same name and channel, written or not (`:1127`), a Locked the first lock request from the top of the stack (`DCS/Link/Channel/Processor.cs:838-849`). ObjectModel stays untagged and is never filtered | CC-2, CC-3 | Removes orders only: the packets dropped are those the windows of #134 and #158 drop today, plus the replies that RRF #46 suppresses and those it misses (OF-T10). Without the answer filter, CE-25 would add an order v7 does not have: a stale answer completing a request of the new epoch |
| CE-15 | S | **RRF's own abort of all files.** DSF MUST answer an AbortFile(all) of the current epoch with InvalidateChannel(e+1), by setting `_allFilesAborted` in `FilesAborted(true)` (`DCS/Link/Channel/Processor.cs:646-760`, called from `DCS/Link/LinkService.cs:866-889`). Replies of codes RRF still ran then carry e and are dropped (K18, G-20). This InvalidateChannel also clears RRF's gate (CE-24). RRF's apply of that InvalidateChannel aborts files that are already aborted; whether that is harmless is Q9. Whether RRF requests an abort after its own 3 s macro-wait timeout (old D10) is a logic decision, open (LA-8): it would abort the File channel's print | — (K18 has no catch code; RRF #46 does not cover it) | "InvalidateChannel after RRF's AbortFile(all)" is a v7 order (a DSF abort can cross RRF's abort-all). No wait: DSF does not wait for an answer |
| CE-24 | S | **RRF's abort gate (FD-20).** Only in an epoch connection, because a v7 DSF does not answer AbortFile(all) with InvalidateChannel (V7-46), RRF MUST keep one gate bit per GB. It is set where RRF writes an AbortFile(all) (`RRF/SBC/SbcInterface.cpp:1549`, in the branch that wrote it, `:1550-1576`). While it is set, RRF MUST drop and count every DSF channel packet of that channel except InvalidateChannel, before any side effect, as CE-10 does. The InvalidateChannel(e+1) that DSF sends back (CE-15) clears it; so does an InvalidateChannel that DSF wrote for its own abort and that crossed the AbortFile(all), which DSF then drops as stale (CE-14). The gate cannot stay set: the AbortFile(all) carries the channel epoch at its write (CE-9), so DSF either answers it or has already written the InvalidateChannel that clears the gate. RRF clears the gate at activation and with the connection (`InvalidateResources`, CE-4). Nothing raises it on an RRF-local emergency stop: `DoEmergencyStop` (`RRF/GCodes/GCodes.cpp:1065-1070`) calls `Reset`, which clears the abort flags (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:145`), so no AbortFile(all) is written. The macro-wait timeout stays LA-8. The gate keeps the one gain of the rejected RRF epoch (FD-20): DSF packets that cross RRF's own abort no longer act on the GB before DSF's InvalidateChannel(e+1) | — | Removes orders only: in v7 such packets act on the GB after RRF aborted its files (V7-46). No new wait: DSF has cancelled its buffered codes and popped its levels on the AbortFile(all) (V7-46), and it fails the requests that the gate dropped when it writes InvalidateChannel(e+1) (CE-25) |
| CE-25 | S | **An epoch change ends everything of the old epoch on both sides (FD-17).** **RRF**, at InvalidateChannel(e+1): the receipt records e+1 and ends a macro wait (CE-7), so every stale DSF packet is dropped from then on (CE-10); requests of the old epoch are not written (CE-12); the apply aborts the files, **(b)** in an epoch connection ends a DoCode wait, unsent or sent, with `SetFinished(true)`, as `InvalidateResources` does on a link loss under the same mutex (`RRF/SBC/SbcInterface.cpp:1761-1765`; `SetFinished(true)` clears `sendToSbc`, `RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:957-963`), and then **(a)** releases the channel's locks with `UnlockAll`, **only if the GB is completely idle** (CE-7; FD-17, FD-27). The decision put (b) at the receipt; it needs the GB mutex ("the main task may be spinning this channel", `SbcInterface.cpp:1764`, `:784`), so it is in the apply (FD-25); until then `FillBuffer` holds the channel's codes (CE-7) and a stale DoCode result is dropped (CE-10). **Why only an idle GB (FD-27):** the apply can run while a code of the old epoch still executes on the GB; the handler says so itself (`SbcInterface.cpp:907-911`), and `AbortFile(true)` only pops file levels (`GCodeBuffer.cpp:1121-1150`), not the state machine of the level it leaves. `UnlockAll` at the base level also clears `toBeHomed` when it releases the movement lock (`RRF/GCodes/GCodes.cpp:5658-5663`), so a running G28 would stop homing (`RRF/GCodes/GCodes4.cpp:358-363`, `:394-406`), and another channel could take the movement lock and move while the G28 still runs. v7 never releases a lock while a code of the GB runs: M505 flushes first (`DCS/Codes/Handlers/MCodeHandler.cs:958`), and Spin writes the Unlock before any later code of the level (`DCS/Link/Channel/Processor.cs:907-929`). `IsCompletelyIdle` also holds while the GB waits for a DoCode result in state normal, because `executingOnSbc` is a buffer state of its own, neither `ready` nor `executing` (`GCodeBuffer.cpp:947-955`, `RRF/GCodes/GCodeBuffer/GCodeBuffer.h:37-40`, `:254`); such a GB executes nothing in RRF, and in an epoch connection (b) has ended that wait before. The idle release still covers the case that motivated (a), an Unlock that RRF refused and whose copy arrives after the abort and is dropped, and K28, an Unlock that DSF drops itself at the abort, because the GB is idle after M505's flush. If a code of the GB still runs at the apply, RRF keeps the lock until that code ends (`UnlockAll` in `HandleResult`, `RRF/GCodes/GCodes2.cpp:5112-5116`), as in v7; for K28 that is what remains. **DSF**, where it writes InvalidateChannel(e+1) (CE-13): the lock and flush requests of the channel are failed already by the abort (`Pop`, `DCS/Link/Channel/Processor.cs:211-215`, `:266-269`; `InvalidateRegular`, `:1462-1465`, `:1478-1481`, called at `:731` and `:823`); a hook MUST fail every evaluation, variable and SetLastCodeResult request of that channel that exists then, written or not, as `InvalidateCodes` fails them for all channels on a stop (`DCS/Link/LinkInterface.cs:615-641`). These requests live on `LinkInterface`, not on the channel (`:144-159`, `:194-208`). DSF fails a DoCode of the old epoch with an error and sends no result (LA-10); a DoCode is a `SimpleCode` that the channel does not keep (`Processor.cs:1040-1050`), so it gets its epoch as a parameter, and how DSF ends a running one is left to the design. **Behaviour changes:** (a) an abort releases the locks of an idle GB at once; in v7 RRF keeps them until an Unlock or until the GB's next code ends (`GCodes2.cpp:5112-5116`), and DSF itself drops an Unlock that is queued but not written when it aborts (`InvalidateRegular` and `Pop` resolve every lock request of a level false, Unlock included; K28); for a GB that is not idle nothing changes; (a) lands in E1 for every DSF once the E0 test shows that no v7 DSF relies on a lock surviving an InvalidateChannel; (b) an abort ends a running DoCode on both sides (K13); (c) an evaluation, variable or code-result request that a code issues between the abort and the write of InvalidateChannel, at the latest in the next Spin (`Processor.cs:822`, `:931-937`), fails too | — | (a) happens only on an idle GB and has the effect of an Unlock that RRF processes right after the InvalidateChannel, which v7 produces when DSF disposes the movement lock after the abort (`DCS/Link/LinkInterface.cs:439-443`), under the same GB mutex as the Unlock handler (`RRF/SBC/SbcInterface.cpp:649-652`); with a code running the lock is released when it ends, as in v7; (b) both endings are v7 outcomes: RRF ends the wait on a link loss (`:1761-1765`), DSF cancels a DoCode without result (G-12); (c) a failed request is a v7 outcome (`InvalidateCodes`). No new wait: the change only ends waits (E-25, E-26, E-47) |

### 4.6 Stop path and halt (link)

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-16 | L | **The stop overtakes retries, never into a repeat (FD-9, FD-15).** Today the link thread checks the halt request only at the top of its loop (`DCS/Link/LinkService.cs:325-335`; `InvalidateCodes` and the write of the stop happen there in the same iteration, `:327-328`), so a stop waits until `PerformFullTransfer` (`:598`) returned, and its retry loop resends the same TX (`DCS/Link/Adapter/SPI.cs:323-440`). Measured worst case about 503 ms behind a long code transfer that noise keeps failing (OF "Stop path under SPI faults"). When a stop is pending, DSF MUST put it first into the next new transfer, that is the next transfer whose data RRF reads. A retry of a transfer that failed before RRF completed it is such a transfer: it carries the stop first, and the content of the old transfer follows unchanged and MUST NOT be dropped (a dropped GetObjectModel would stay "sent" for good, K16; a dropped file result would make RRF wait 3 s). A SequenceGuard repeat stays a pure repeat, in both directions: RRF answers a transfer that it completed already with its last TX and does not read DSF's data (`RRF/SBC/DataTransfer.cpp:924-946`, `:950-961`), so a stop placed there would be lost; and DSF sends a transfer that RRF did not complete again as it was (`DCS/Link/Adapter/SPI.cs:1992-2010`), then the pending one (`:408-419`). DSF learns that RRF answers its retry as a repeat only in the header exchange of that retry (`SPI.cs:2012-2019`), after it built the retry; a stop that it placed there MUST stay pending and go first into the next new transfer (FD-26). No header change. **Resend lookup (FD-30):** a stop in front of a retried transfer MUST NOT break DSF's resend lookup. Packet ids restart at 0 in every transfer and follow the write order (`SPI.cs:439`, `:787`); the lookup walks the last TX in order, stops at the first packet whose id is not below the requested one (`:826-853`, `while (header.Id < packet.ResendPacketId ...)`) and otherwise throws `ArgumentException` (`:855`), which ends DCS (V7-7, VF-1). A stop with a new id in front of the old content would make every later resend request throw. DSF MUST therefore either (1) renumber the packets of the retry, the stop first, and rewrite the transfer header and its CRC (`NumPackets`, `DataLength`, `WriteCRC`, `:317-320`), so that the ids stay ascending, or (2) make the lookup independent of the order, searching every packet of the last TX for the id. The L1 PR chooses one. **Room (FD-30):** the stop is one `PacketHeader` without payload, 8 bytes (`RRF/SBC/SbcMessageFormats.h:101-107`; `SPI.cs:870`), and a transfer holds at most 8192 bytes (`DCS/Link/Protocol/Shared/Consts.cs:32`, `DCS/Settings.cs:158`; RRF `SbcMessageFormats.h:26`). DSF MUST keep 8 bytes free in every transfer it builds: every other packet is written only if 8 bytes remain after it (`CanWritePacket`, `SPI.cs:1714-1717`). So a stop always fits in front of a retry, and nothing of the old content moves to another transfer. **Milestone:** link work that does not depend on the epoch: L1 (section 7.2). Acceptance: M112 ≤ 100 ms in 1000 stops under injected noise (AC-4) | — | "RRF processes DSF packets after the stop in the same transfer" is a v7 order: in the iteration that writes the stop, DSF still writes resend copies and file results (`LinkService.cs:434-451`, `:613-627`), object model queries, key changes and messages (`:470-594`), which `InvalidateCodes` does not cancel (V7-94). The v7 contract says DSF writes nothing after the stop (V7-12, X-17); the code writes these non-channel packets. Codes after the stop get "stopped" as in v7 (V7-16) |
| CE-17 | L | **A halt is never lost (Tim, 2026-11-09).** Rejecting unusable data MUST never drop an emergency stop or the halt state of RRF. Facts: an emergency stop that RRF makes by itself sends no packet of its own: `GCodes::DoEmergencyStop` (`RRF/GCodes/GCodes.cpp:1065-1070`) calls `reprap.EmergencyStop()` and `Reset()` and then sends the generic message "Emergency Stop! Reset the controller to continue."; DSF learns of the halt only through the object model (`state.status` "halted", `RRF/Platform/RepRap.cpp:1983-1989`) and that message (V7-17, K20). DSF's own stop counts as done once the transfer that carried it completed and the RX of that transfer was processed (`DCS/Link/LinkService.cs:454-456`, `:599`; `DCS/Link/LinkInterface.cs:345-356`). Rules: **(a)** On SPI, corrupted data (CRC) is retried as a whole transfer and never processed in part: RRF processes only after both data responses were Success (`RRF/SBC/DataTransfer.cpp:1187-1215`), DSF only after `PerformFullTransfer` returned (`SPI.cs:360-373`, `:440`). On USB there is no transfer CRC and no retry (`RRF/SBC/DataTransfer.cpp:1303-1356`): a failed transfer ends the connection on RRF (`RRF/SBC/SbcInterface.cpp:270-288`) and DSF drops its RX (`DCS/Link/Adapter/USB.cs:363-404`). **(b)** A packet that passed the CRC but cannot be used is a protocol fault: it is rejected, counted and reported to the logic as an event (VF-1, VF-2). Today it ends DCS (`SPI.cs:855`, uncaught by `LinkService.cs:446-450`) or drops the rest of the transfer ("Read invalid packet", `:439-443`). Because the halt is a state, not an edge, DSF MUST then read the object model state again. A rejected or mismatched ObjectModel answer MUST NOT leave its query pending: today the key check discards it and leaves the query "sent" (`LinkService.cs:714-723`, `:470-478`), so no query goes out any more and a halt would never arrive (K16, VF-8). **(c)** DSF's own stop is complete only after a transfer that RRF processed. Over USB a failed transfer returns normally, so the halt of the transfer it dropped completes (`USB.cs:363-404` with `LinkService.cs:454-456`, K6, G-2): VF-9. **(d)** Acceptance AC-5. **Milestone:** link work that does not depend on the epoch: L1 (section 7.2), whose gate needs VF-1, VF-8 and VF-9 for (b) and (c) | — | Reading the state again adds a query, which v7 sends at any time; no new order. A halt request that stays pending is a v7 wait (E-21, 4.5 s) |

### 4.7 Peers, integrity, counters, RAM

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-18 | S | **Peers without ChannelEpoch.** A fork DSF with a stock RRF, and a stock DSF with a fork RRF, MUST run upstream v7. Nothing of the epoch is written unless ChannelEpoch is active: no tag, no `MessageType` bits on the wire (CE-11). The fork catch code CC-2 to CC-8 is deleted from the v7 path once the printers run the epoch RRF (E3, FD-11, old D2 option b). **Behaviour change** for such pairs: fork DSF + stock RRF gets back DF-T2 (#134 gone), DF-T11 resend (#161 gone, stock RRF refuses) and DF-T12 (#158 gone); DF-T1 and DF-T10 are stock RRF's anyway. Stock DSF + fork RRF gets back DF-T1 (RRF #43 gone), DF-T10 (RRF #46 gone) and DF-T12 (RRF #45 gone); DF-T2 is stock DSF's anyway; DF-T11 does not come back, because CE-7 stays in the fork RRF. DF-T3 does not come back: its fix #133 (CC-28) is no epoch catch code and stays. The lock release of the apply (CE-25 (a)) and its voiding of an unsent macro request (CE-7) are part of CE-7 and so reach a stock DSF with a fork RRF from E1: the channel's locks are released when RRF applies InvalidateChannel to an idle GB, and a daemon.g request whose level the apply popped is not sent (OF-T11). The gate, the end of a DoCode and the answer tags exist only in an epoch connection | CC-2 … CC-8 from the v7 path (E3) | v7 behaviour, by definition |
| CE-19 | L | **Integrity (v2 R9), conditional on an EMC test.** Measure corrupted transfers per hour under IEC 61000-4-3 and IEC 61000-4-6, 1 h each, at the levels of EN IEC 61000-6-2, from the checksum counters (RRF `checksumErrors`, `RRF/SBC/DataTransfer.cpp:1167`, `:1221`). At ≤ 100 corrupted transfers per hour CRC32 alone meets R9: about 2⁻³² undetected per corrupted transfer gives ≤ 2.3 × 10⁻⁸ per hour, below 10⁻⁷. Above that, a second, independent per-transfer CRC trailer is added as a v2 link capability. It can sit after the last packet inside the data length: both read loops read only the number of packets the header names (RRF `RRF/SBC/SbcInterface.cpp:314`, `RRF/SBC/DataTransfer.h:233-236`; DSF `DCS/Link/LinkService.cs:434`, `DCS/Link/Adapter/SPI.cs:523`) and stop at the data length (`RRF/SBC/DataTransfer.cpp:618-621`, `SPI.cs:531-534`). Details Q26 | — | Link only |
| CE-20 | S | **Counters for the evidence gates.** Both sides MUST count per channel and request type: stale drops (CE-10, CE-14), drops by the gate (CE-24), drops of tags that follow the channel epoch (CE-5), repeats of InvalidateChannel that the receipt ignored (RRF, CE-7; with the stale drops of their channel), out-of-order replies that are not stale (DSF, today only logged, `DCS/Link/Channel/Processor.cs:1165`), resends (DSF, today logged, `LinkService.cs:618`) and refusals (RRF, `packetAcknowledged` false, `RRF/SBC/SbcInterface.cpp:1354-1358`), including code refusals. M122 shows them on both sides. Counted, not alarmed (v2 R7) | — | Counting changes no order |
| CE-21 | L, S | **RAM.** The RRF part MUST use static fields only: no heap allocation, no side table. A tag per reply-FIFO entry (64 entries, `RRF/Platform/OutputMemory.h:16`) would be a side table against CLAUDE.md rule 3 and is not needed, because the FIFO's own type carries the tag (CE-11). New static RAM MUST stay below 1 KB, checked with the map file (section 6) | — | — |

### 4.8 Logical compatibility (PR9)

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-22 | S | **Refinement.** An epoch connection MUST be a refinement of v7 for the logic of both sides: every order in which a logic layer sees its events MUST also be possible in v7 with the catch code of DSF 7f26d0ca and RRF 0dccb9efe. The capability MAY remove orders, namely those the catch code removes today (stale replies, requests, closals and acknowledgements across an abort, stale copies of codes and macro answers). It MUST NOT add any. Section 4.10 checks every V7 item | — | This requirement |
| CE-23 | S | **Wait-for graph.** The v7 graph is [sbc-link-v7-contract.md](sbc-link-v7-contract.md) section 15. The capability changes it so: E-16 (a channel waits for the acceptance of InvalidateChannel) is gone (CE-7, CE-13); the codes of the channel wait instead in RRF's `FillBuffer` for the apply, bounded as C-4 (the apply needs the GB mutex, which the main task holds at most through its 3 s waits, V7-40, V7-71); the one new wait is the hold at connect (CE-3), bounded by a timer (Q23). The end of the old epoch (CE-25) only ends waits: E-25 and E-26 for the channel's requests, and E-47 (an RRF GB waits for a DoCode result without a bound) for an abort of its channel. The gate (CE-24) adds no wait: it drops packets, and DSF fails the requests it dropped (CE-25). C-1 to C-3 stay as in v7: C-1 and C-2 are K1 and K2, whose reachability tests come in E0 (Q14); C-3 is a logic finding (K3, LA-15). C-4 to C-8 stay, bounded | — | No new cycle: the hold at connect waits for the link thread only, which holds no logic lock while it waits |

### 4.9 Message summary

| Dir | v7 request (`RRF/SBC/SbcMessageFormats.h:269-349`) | Tag | Who filters | v7 pairing it replaces in epoch mode |
|---|---|---|---|---|
| → RRF | 2 Code, 7 MacroCompleted, 18 MacroStarted | epoch of the channel when written; a copy keeps it | RRF drops every tag other than the channel epoch (CE-10) and all while the gate is set (CE-24) | copies acting after an abort (A-4) |
| → RRF | 19 InvalidateChannel | new epoch e+1 | never refused, not stopped by the gate; the receipt ignores a repeat whose tag does not follow (CE-7, CE-24) | #161 (resend tracking); RRF's first-copy guard |
| → RRF | 10 Lock, 11 Unlock, 16 EvaluateExpression, 20 SetVariable, 21 DeleteLocalVariable, 29 SetLastCodeResult | epoch of the channel when written | RRF drops every tag other than the channel epoch (CE-10) and while gated (CE-24); DSF has failed the requests (CE-25) | — |
| → RRF | 17 Message: the chunks of a DoCode result | epoch of the DoCode | RRF drops every tag other than the channel epoch (CE-10) and while gated (CE-24) | — |
| → RRF | 0 EmergencyStop, 1 Reset, 3 GetObjectModel, 5 SetPrintFileInfo, 6 PrintStopped, other 17 Messages, file results, 30 ObjectModelKeyChanged, 0x4D50, IAP | none | — | — |
| → DSF | 3 Message with `BinaryCodeReplyFlag` | `codeEpoch` via `MessageType` bits (CE-11) | DSF drops every tag other than the channel epoch (CE-14) | RRF #46 suppression; position windows |
| → DSF | 4 ExecuteMacro, 5 AbortFile, 13 WaitForAcknowledgement, 14 MacroFileClosed, 15 MessageAcknowledged, 12 DoCode, 7 PrintPaused | as the table under CE-12 (CE-9, CE-12) | DSF drops every tag other than the channel epoch (CE-14) | #134, #158, RRF #43, RRF #45 |
| → DSF | 9 Locked, 11 EvaluationResult, 16 VariableResult | tag of the request | DSF drops every tag other than the channel epoch (CE-14) | — |
| → DSF | 0 ResendPacket (field = packet id), 1 ObjectModel, 2 CodeBufferUpdate, other Messages, file requests | none | never (CE-14) | — |

### 4.10 Alignment with the v7 contract

Every item of [sbc-link-v7-contract.md](sbc-link-v7-contract.md) gets exactly one status for an epoch connection:
- **identical**: the logic sees the v7 behaviour;
- **changed**: the capability changes it; the difference and the reason are given, and the last column says why the
  change only removes orders (PR9, CE-22);
- **removed**: the mechanism is dropped; the reason is given;
- **gap**: a v7 behaviour or defect that the capability does not address; the row names the separate v7 fix (`VF-n`),
  the logic-list item (`LA-n`) or the open question (`Qn`) that takes it.

Counts (draft 7): 43 identical, 35 changed, 3 removed, 20 gap, as in draft 6 (draft 5: 49, 25, 3, 24). A script
checked that every V7-1 … V7-101 has exactly one row (a range such as "V7-74 … V7-79" counts for each item) and that
every row that is not identical has a reason, a filled PR9 column and a requirement or target. Every change only
removes orders. One change adds a bounded wait: the hold at connect (V7-3, CE-3). Two changes alter what the logic
sees, within orders that v7 already has: the abort releases the locks of an idle GB (V7-53, V7-58) and ends a running
DoCode (V7-70).

| V7 | Status | Draft 7 | Difference | Reason | Only removes orders (PR9) |
|---|---|---|---|---|---|
| V7-1 pipeline depth one | identical | — | the depth stays; the abort windows no longer depend on it (V7-55, V7-56) | — | — |
| V7-2 processing in the writer's order | identical | — | filters drop packets before processing; the order is unchanged | — | — |
| V7-3 DSF's TX order | changed | CE-3 | after every connect and reset the channel Spins wait until the ChannelEpoch state is known | the epochs must be in place before the first channel packet | yes: events are only delayed, as on a slow link; the hold is a wait at connect only, bounded by a timer (Q23) |
| V7-4 RRF's TX order | identical | — | the tag rides in the header | — | — |
| V7-5 pacing | identical | — | — | — | — |
| V7-6 unknown and invalid packets | gap | VF-1; Q21 (K4: E0 test) | DSF rejects and counts instead of ignoring or stopping; RRF's end of the read loop at ≥ 32 stays (K4) | the capability adds no packet type (CE-1, CE-5), so it does not meet X-18 | n/a here; VF-1 only removes a DCS stop |
| V7-7 packet ids, resend lookup | gap | VF-1; CE-16 | the `ArgumentException` that ends DCS (`SPI.cs:855`) and NEW-1 on USB are fixed separately; a stop in front of a retry keeps the lookup working (renumbered or order-independent, CE-16) | not an epoch topic (FD-13) | n/a here; VF-1 only removes a DCS stop |
| V7-8 refusal | changed | CE-8 | a copy keeps the tag of the original | without it, a copy would look untagged and pass the filters | yes: same copies, same order |
| V7-9 SPI resend guard | identical | — | CC-26 stays | — | — |
| V7-10 a refused packet is overtaken | changed | CE-10 | a copy of a Code, MacroStarted or MacroCompleted that an abort overtook is dropped | in v7 such a copy runs or acts after the abort | yes: removes "a cancelled code runs after the abort"; NEW-3 and K27 stay (EG-3; K27 an E0 test, Q21) |
| V7-11 why RRF refuses | changed | CE-7 | InvalidateChannel is never refused; a repeat that a repeated transfer delivers is ignored by its tag; the other refusals stay | DF-T11 (CC-6); FD-28 | yes: removes the resend of InvalidateChannel and a second action of a repeat; the receipt acts without the mutex as v7 already does (C-4) |
| V7-12 EmergencyStop | changed | CE-16 (L1) | a pending stop goes first into the next new transfer, a retry ahead of its unchanged content included, with room kept for it and the resend lookup intact; a SequenceGuard repeat stays a pure repeat | stop path worst case about 503 ms (A-6); RRF does not read a repeat (FD-15) | yes: in v7 RRF already processes DSF packets after the stop in the same transfer (CE-16) |
| V7-13 halt completion | gap | VF-9; CE-17 (c) | over USB a failed transfer must not complete the halt | K6, G-2 | n/a here; VF-9 keeps the halt pending, a v7 wait (E-21) |
| V7-14 code buffer after a stop | gap | Q21 (K12: E0 test) | — | K12 is not an epoch topic; reported per FD-13 | n/a here |
| V7-15 Reset | identical | — | — | — | — |
| V7-16 M112 is DSF's | identical | LA-14 | — | — | — |
| V7-17 RRF-local stops | gap | CE-17 (b); Q27; LA-7 | DSF learns the halt from the object model, also after rejected data; STOPPED only after an E0 test shows K20 (FD-23); an RRF-local stop raises no gate (CE-24) | K20, X-5; Tim's halt rule | n/a here; reading the state again adds no order |
| V7-18 firmware update | identical | — | — | — | — |
| V7-19 when DSF writes a Code | changed | CE-13, CE-3 | the block by an unconfirmed abort is gone; codes after an abort go out at once and RRF holds them until the apply; no code before the ChannelEpoch state is known | CC-6 | yes: RRF runs them only after the apply, as v7 runs them only after the accepted abort |
| V7-20 code admission | gap | Q21 (K24: E0 test) | — | K24 is not an epoch topic | n/a here |
| V7-21 flow control by estimate | identical | — | credit dropped (FD-1); CC-28 … CC-30 stay; EG-3 watches it | — | — |
| V7-22 read-ahead drop (B7) | identical | — | CC-21 stays (Q5) | — | — |
| V7-23 RRF holds codes while a stack event is unsent | changed | CE-7 | `FillBuffer` also holds the channel's codes while the apply of InvalidateChannel is pending | the apply needs the GB mutex | yes: the same hold v7 has through #161, on the other side |
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
| V7-34 blocking macro request | changed | CE-12, CE-14 | the request carries the epoch; RRF does not write one of an older epoch and ends its wait at once with an error; DSF drops one of another epoch | CC-2 (#134), CC-5 (RRF #43); K17 stays (an E0 test, Q21; EG-1) | yes: a failed macro request is a v7 outcome |
| V7-35 several macros of one code | identical | — | — | — | — |
| V7-36 system macros on an empty stack | gap | Q21 (K9: E0 test) | — | K9 is not an epoch topic | n/a here |
| V7-37 macro that cannot be opened | identical | — | — | — | — |
| V7-38 macro start, implicit start | changed | CE-10; VF-3 | a stale Code or MacroStarted no longer resolves a macro wait; that a current Code resolves it before its refusal is VF-3 | DF-T11 family | yes |
| V7-39 daemon.g | changed | CE-9, CE-12, CE-7 | the daemon request carries the epoch of the moment RRF raised it (FD-19); one of an older epoch is not sent; the apply voids an unsent one whose level it popped | A5 (OF-T11) | yes |
| V7-40 macro wait timeout | gap | LA-8 | whether RRF requests an abort after its timeout is logic, open; K23 stays | FD-8 | n/a here; if decided, "channel aborted after a macro request" is a v7 order |
| V7-41 MacroCompleted | changed | CE-10 | a stale copy is dropped before it acts; X-7 stays (Q15) | a copy of an older epoch belongs to a level that no longer exists | yes |
| V7-42 MacroFileClosed | changed | CE-12, CE-14 | tagged; DSF drops one of another epoch; replaces #158 and RRF #45; the old-session case K5 is VF-6 | CC-3, CC-4 | yes |
| V7-43 DSF's macro reader | identical | — | — | — | — |
| V7-44 M98 | identical | — | — | — | — |
| V7-45 single AbortFile, M99 | changed | CE-12, CE-14 | a single abort of an older epoch is dropped; the M99 catch code CC-9 … CC-12 stays; a level id only on evidence (EG-6) | the epoch ties it to the level it was meant for | yes |
| V7-46 AbortFile all | changed | CE-15, CE-14, CE-24 | it carries the channel epoch at its write (CE-9); DSF answers one of the current epoch with InvalidateChannel(e+1); one of another epoch is dropped; until InvalidateChannel(e+1) arrives RRF drops the channel's DSF packets (gate) | K18, G-20; the gain of the rejected RRF epoch (FD-20) | yes: "InvalidateChannel after RRF's abort-all" is a v7 order; the gate drops packets that act after RRF's abort in v7; no wait |
| V7-47 stack depth | identical | — | — | — | — |
| V7-48 WaitForAcknowledgement | changed | CE-12, CE-14 | one of another epoch is dropped; G-14 (K15) stays (an E0 test, Q21) | stale box of an aborted level | yes |
| V7-49 MessageAcknowledged | changed | CE-9, CE-12, CE-14 | one of another epoch is dropped; after M292 it carries `codeEpoch`, after the box timeout the epoch when RRF raised it (FD-19); the timeout of a box that survived an abort at the base level carries the new epoch and passes, as in v7 (Q31) | in v7 it pops whatever level is current | yes |
| V7-50 cancel with abort | identical | — | — | — | — |
| V7-51 M291 blocks only lower levels | identical | — | — | — | — |
| V7-52 DSF side of an abort | changed | CE-13, CE-25 | DSF increments the epoch when it writes InvalidateChannel; the payload is unchanged; there it fails the channel's evaluation, variable and code-result requests and its DoCodes of the old epoch; G-21 (K19) stays (an E0 test, Q21) | CC-1's ambiguity becomes an identity; CC-1's code stays (`_allFilesAborted`, `FileAbortSent`) | yes |
| V7-53 RRF side of InvalidateChannel | changed | CE-7, CE-12, CE-25 | receipt without the mutex, ignored for a repeat; apply with it; no second purge; the apply voids an unsent macro request; requests of the older epoch are not sent; the apply ends a DoCode wait and releases the locks of an idle GB (behaviour changes) | CC-4, CC-5, CC-8; FD-17, FD-18 (K13), FD-25, FD-27, FD-28; OF-T11 | yes: v7 already acts before the mutex; the lock release on an idle GB equals an Unlock right after the abort, no lock is released while a code runs, and both DoCode endings are v7 outcomes (CE-25); a voided request is one DSF never sees |
| V7-54 refused InvalidateChannel | removed | CE-7 | — | never refused | yes |
| V7-55 abort window 1 | removed | CE-14 | — | CC-2 (#134): replaced by the filter | yes |
| V7-56 abort window 2 | removed | CE-7, CE-13 | — | CC-6 (#161): nothing to wait for | yes: removes E-16 |
| V7-57 Lock | changed | CE-10, CE-14, CE-25; VF-5; LA-9 | a stale Lock copy and a stale Locked are dropped; the abort releases what a Lock took if the GB is idle; Lock as a pending request stays VF-5, the Unlock for a Locked without request LA-9 | K14 (G-13), its abort case; FD-16, FD-17, FD-24, FD-27 | yes: removes "the Locked of an aborted request resolves a new one"; the release on an idle GB equals an Unlock (CE-25); VF-5 keeps the Locked wait (E-15) |
| V7-58 Unlock | changed | CE-10, CE-7, CE-25 | a stale Unlock copy is dropped; the abort releases the locks of an idle GB, so a dropped Unlock leaves none there; with a code of the GB running, the lock stays until that code ends, as in v7 (K28) | FD-17: without the release a dropped stale Unlock would leave the movement lock held until the GB's next code ends; FD-27: no release while a code runs | yes: the release equals an Unlock that RRF processes right after the abort, on an idle GB only (CE-25) |
| V7-59 EvaluateExpression | changed | CE-10, CE-14, CE-25 | a stale request is dropped without answer; DSF fails the channel's requests at the epoch change; a stale EvaluationResult is dropped | FD-16 (Q25) | yes: a failed evaluation is a v7 outcome (`InvalidateCodes`); the answer filter keeps a stale result from resolving a request of the new epoch |
| V7-60 SetVariable, DeleteLocalVariable | changed | CE-10, CE-14, CE-25; VF-4; Q21 (K27) | stale copies are dropped; DSF fails the channel's variable requests at the epoch change; a stale VariableResult is dropped; no assignment before a refusal is VF-4; K27 an E0 test | FD-16 (Q25); K21; K27 | yes: a failed variable request is a v7 outcome (`InvalidateCodes`) |
| V7-61 variables only after a flush | gap | LA-16 | — | K25 is a logic finding | n/a here |
| V7-62 SetLastCodeResult | changed | CE-10, CE-25; LA-17; Q21 (K10) | a stale copy is dropped; unwritten requests of the channel fail at the epoch change; K26 is logic (LA-17); K10 an E0 test | FD-16 (Q25) | yes: a cancelled SetLastCodeResult is a v7 outcome (`InvalidateCodes`, `DCS/Link/LinkInterface.cs:616-623`) |
| V7-63 object model | gap | VF-8; CE-17 (b) | a rejected or mismatched answer must not leave its query pending; VF-8 is a prerequisite of E2 (CE-3) | K16, G-18; a halt would never arrive | n/a here; VF-8 removes a permanent block |
| V7-64 logic waits for the OM | identical | — | — | — | — |
| V7-65 SetPrintFileInfo | gap | Q14 (K1, K2); Q11 (G-19) | the waits under the job lock stay | completion on acceptance was dropped with v8 | n/a here |
| V7-66 ObjectModelKeyChanged | identical | — | OF-S3 stays a separate fix | — | — |
| V7-67 print end | gap | LA-12 | DSF writes InvalidateChannel on File and File2 before PrintStopped(abort) | logic (decided by Tim) | n/a here; LA-12 orders an abort before the job cancel, which an IPC abort can do in v7 |
| V7-68 PrintPaused | changed | CE-6, CE-9, CE-14; LA-13 | it carries File's epoch when RRF raised the pause; DSF drops a stale one; File2's epoch is not carried (Q32); the pause mechanism stays v7 (FD-7) | FD-19: work that RRF starts itself | yes: removes "a pause raised before a job abort pauses DSF after it" |
| V7-69 Message to RRF | changed | CE-6, CE-10 | the chunks of a DoCode result carry the DoCode's epoch, and RRF drops stale ones; other messages stay untagged | FD-18 | yes: removes "a stale result finishes a newer DoCode or is output after the abort" |
| V7-70 DoCode | changed | CE-6, CE-12, CE-14, CE-25; LA-10 | DoCode carries `codeEpoch`; RRF does not write one of an older epoch and ends the DoCode wait in the apply; DSF fails a DoCode of the old epoch with an error and does not run a stale one; behaviour change: an abort of the channel ends a running DoCode | FD-18; K13 (G-12) | yes: both endings are v7 outcomes (RRF on a link loss, DSF's cancelled DoCode, G-12); removes the unbounded wait E-47 for aborts |
| V7-71 RRF's file I/O | gap | Q21 (K22, K9: E0 tests); EG-4 | — | request ids only on evidence (FD-1) | n/a here |
| V7-72 stack mirror by position | identical | — | identity only on evidence (FD-3, EG-1); a level id only on evidence (FD-21, EG-6) | — | — |
| V7-73 pop on the base level is fatal | changed | CE-14; VF-6 | its abort source (T12) is filtered by the epoch; its reconnect source (K5) is VF-6 | CC-3 | yes |
| V7-74 … V7-79 level routing, start code, pop effects, flush, prioritized codes, job read-ahead | identical | — | — | DSF and RRF logic | — |
| V7-80 M606 S1 | gap | LA-15 | — | C-3 (K3) is a logic finding | n/a here |
| V7-81 triggers | gap | LA-19 | — | the safety review found that a door trigger can be lost while the link is down (section 11) | n/a here; logic and safety work |
| V7-82 no cross-channel order | identical | — | — | — | — |
| V7-83 the link thread takes logic locks | gap | Q14 (K1, K2); LA-15 (C-3) | — | completion on acceptance was dropped with v8 | n/a here |
| V7-84 completion never blocks the link thread | identical | — | — | — | — |
| V7-85 DSF waits without timeout | changed | CE-25 | an epoch change also ends DSF's waits for the channel's evaluations, variables and code results of the old epoch; the hold at connect is recorded at V7-3 | FD-16: RRF drops stale requests, so nothing may wait for their answers | yes: it only ends waits, as `InvalidateCodes` does |
| V7-86 … V7-87 RRF task waits | identical | — | the end of the InvalidateChannel refusal is recorded at V7-11 | — | — |
| V7-88 RRF logical waits | changed | CE-25, CE-24 | an abort of the channel ends a GB's wait for a DoCode result; while the gate is set the GB gets no DSF packet of its channel | FD-18, FD-20 | yes: ends a wait; the gate drops packets that act after RRF's own abort in v7 |
| V7-89 link-level bounds | identical | — | the retry limit of 10 stays (`DCS/Settings.cs:193`, FD-10) | — | — |
| V7-90 fatal errors in DSF's packet loop | gap | VF-1 | — | DCS exits on link data | n/a here; VF-1 only removes a DCS stop |
| V7-91 SPI reconnect | gap | VF-7 | — | K7, G-3: the capability ends with the connection (`DataTransfer.cpp:1431`), so tags do not help | n/a here |
| V7-92 DSF's SPI reset detection | identical | — | no resume (FD-10) | — | — |
| V7-93 USB failure | gap | VF-9; CE-17 (c) | — | K6, G-2 | n/a here; VF-9 keeps the halt pending |
| V7-94 DSF invalidation | changed | CE-4, CE-13 | `Invalidate` after a connection change resets the epochs and the partial replies; `InvalidateCodes` (stop) keeps the epochs | RRF keeps its epochs across a stop | yes: not visible to the logic beyond K11 for aborts |
| V7-95 RRF on a link loss | identical | — | the epoch state ends with the connection (CE-4), not visible to the logic | — | — |
| V7-96 stale RRF flags across a reconnect | gap | VF-6 | — | K5, X-12 | n/a here |
| V7-97 what RRF assumes after a reconnect | identical | — | — | — | — |
| V7-98 protocol versions | identical | — | no new version (FD-1) | — | — |
| V7-99 protocol < 3 | identical | — | — | — | — |
| V7-100 `UpdateOnly` | identical | — | — | — | — |
| V7-101 link capabilities | changed | CE-1, CE-2, CE-3 | a fourth capability, offered on every board and on USB; DSF's USB adapter activates it | the epoch is a packet feature, not an SPI one | yes (link only); the hold at connect is recorded at V7-3 |

## 5. Separate v7 work

Each item is its own PR with unit tests in fork-only files under `src/UnitTests` (CLAUDE.md rule 7). None is part of
the ChannelEpoch capability; each works without it. Per the fork's rule "every bug found is fixed".

| ID | Layer | Fix | Source | Where |
|---|---|---|---|---|
| VF-1 | L | **DCS never exits on link data.** Unusable data is rejected and counted, never an exception that ends DCS, never a silent drop of the rest of a transfer without a report. Today: a resend request for an unknown packet id throws `ArgumentException` (`DCS/Link/Adapter/SPI.cs:855`, `USB.cs:746`), which the packet loop does not catch (it catches only `ArgumentOutOfRangeException`, dumps and rethrows it, `DCS/Link/LinkService.cs:446-450`); an invalid format code, a newer protocol version or a too long data length throws (`SPI.cs:1955-1957`, `:1960-1965`, `:1972-1977`); "Read invalid packet" drops the rest of the transfer (`LinkService.cs:439-443`). Includes NEW-1: over USB the resend looks one TX back (`USB.cs:717-721`), while RRF composes its USB answer one transfer behind (`RRF/SBC/DataTransfer.cpp:1303-1356`, `:1365-1367`). After a rejection DSF reads the object model state again (CE-17 (b)) | F-6, F-7 of draft 4; OF-S6; K4 (DSF side) | DSF |
| VF-2 | L | **Link events and health state.** The link reports to the logic of both sides LinkDown, LinkRestored, ConnectionLost and PeerRestarted, and a protocol fault per rejected packet (VF-1); it offers a health state ok, degraded or down with counters (retries, resyncs, rejected data, checksum errors). The logic decides what to do (LA-6). Constraint: the extended-link session status is the constant 0x7FF (`DCS/Link/Protocol/Shared/ExtendedLink.cs:65`), not a restart counter, so it cannot tell a restart from a connection reset (Q29) | v2 R7, R11; layering rule | both |
| VF-3 | S | **A Code resolves a macro wait only once accepted.** Today it resolves the wait (`RRF/SBC/SbcInterface.cpp:383-392`) before it can be refused for the cascade (`:395-399`) or for ring room (`:405-414`) | V7-38, B8, A-4 | RRF |
| VF-4 | S | **SetVariable assigns only once it can answer.** Today it evaluates and assigns, then refuses if its VariableResult does not fit, so the copy of a `var` or `global` fails with "already exists" | K21, X-6 | RRF |
| VF-5 | S | **Lock as a pending request.** RRF accepts Lock (10; `LockMovementAndWaitForStandstill` in RRF, `LockAllMovementSystemsAndWaitForStandstill` in DSF) and keeps it pending per GB, tries again at every exchange under the GB mutex (`RRF/SBC/SbcInterface.cpp:606-634`) and sends Locked once it succeeds; DSF no longer polls by resend (`DCS/Link/LinkService.cs:615-619`, `SPI.cs:841-845`). An abort or a connection change cancels it and releases what it holds (`UnlockAll`, `RRF/GCodes/GCodes.cpp:5648-5668`); for the abort the apply of CE-7 does that from E1 if the GB is idle (CE-25 (a)). In an epoch connection the pending Lock MUST keep the tag of its request for its Locked (CE-6), one byte per GB (section 6) | K14, G-13; CC-31 | both |
| VF-6 | S | **No stale per-GB flags after a reconnect.** `InvalidateResources` clears `macroFileClosed`, `messagePromptPending`, `invalidated`, `invalidationPending`, `codeReplySurplus`, an unsent `sendToSbc` and `reportPause`, and does not set `messageAcknowledged` itself (`RRF/SBC/SbcInterface.cpp:1717-1780`, `:1772`) | K5, X-12 | RRF |
| VF-7 | L | **No TX of the old session in the new one.** After a lost connection DSF exchanges the pending TX again (`DCS/Link/Adapter/SPI.cs:442-477`), and RRF processes it if its number follows (`RRF/SBC/DataTransfer.cpp:1396-1443`) | K7, G-3 | both |
| VF-8 | S | **A mismatched ObjectModel answer does not block the queries.** The key check discards it but leaves the query "sent" (`DCS/Link/LinkService.cs:714-723`, `:470-478`); the query MUST be sent again or failed, so updates and the halt state keep coming (CE-17 (b)). A prerequisite of E2, because the hold at connect waits for the capability query (CE-3), and of L1 (CE-17) | K16, G-18 | DSF |
| VF-9 | L | **A failed USB transfer does not complete the halt.** The halt request stays pending and is written again after the reconnect (CE-17 (c); `DCS/Link/Adapter/USB.cs:363-404`, `DCS/Link/LinkService.cs:454-456`, `:599`) | K6, G-2 | DSF |

Not kept (FD-13): time-based retries instead of the retry count (old V8-43); a systemd watchdog for DCS. The safety
review's point that DCS has none is an open item of the logic list (LA-20), not decided.

## 6. Firmware RAM budget

Measured basis: the M122 of 2026-11-09 above. "Never used 95868" is `heapLimit − heapTop`
(`RRF/Platform/Tasks.cpp:300-314`, `:369-370`, as read for draft 4).

| Item | Bytes | Requirement |
|---|---|---|
| Channel epoch per GB (6 bits in 1 B × 15 GBs, `RRF/GCodes/GCodeChannel.h:20`) | 15 | CE-4, CE-7 |
| `codeEpoch` per GB (valid bit + 6 bits, 1 B × 15) | 15 | CE-9 |
| Ring stamp in `BufferedCodeHeader.padding` (exists) | 0 | CE-9 |
| Apply pending: `invalidationPending` (exists, reused and renamed) | 0 | CE-7 |
| Capability bit in `offeredLinkCapabilities` / `activeLinkCapabilities` (exist) | 0 | CE-1 |
| Reply tag in the FIFO's `MessageType` (exists, `RRF/Platform/OutputMemory.h:179`) | 0 | CE-11 |
| Gate bit per GB (1 bit; fits the free bits 6–7 of the channel-epoch byte, else 1 B × 15) | ≤ 15 | CE-24 |
| Epoch kept with the box timeout (1 B × 15) and with PrintPaused next to `reportPause` (1 B) | 16 | CE-9 |
| Answer tags (Locked, EvaluationResult, VariableResult: the request's tag, passed as a parameter). This holds while each answer is written inside the handler of its request: Locked `RRF/SBC/SbcInterface.cpp:607-634`, EvaluationResult `:682-760`, VariableResult `:928-1109` | 0 | CE-6 |
| Tag of a pending Lock once VF-5 makes Lock a pending request, written later than its request was read (1 B × 15) | 15 | CE-6, VF-5 |
| **Epoch state** | **≤ 76** with VF-5 (draft 6: 61, draft 5: 30) | |
| Removed in E3: `codeReplySurplus` (bool × 15, `GCodeBuffer.h:405`) | −15 | CE-7, CE-18 |
| Counters: stale drops per channel (u16 × 15) | 30 | CE-20 |
| Counters: gate drops per channel (u16 × 15) and tags that follow the epoch (u16) | 32 | CE-20 |
| Counters: refusals per request type (u16 × 32) | 64 | CE-20 |
| **Total after E3** | **≈ 187** with VF-5 (draft 6: 172, draft 5: 109) | 0.20 % of 95,868; CE-21 |

Not measured: padding inside `GCodeBuffer` (the new bytes may fit into existing padding), flash, CPU. Whether static
growth reduces "never used" byte for byte is not checked.

## 7. Migration, milestones, evidence gates

### 7.1 Coexistence

| DSF | RRF | Result |
|---|---|---|
| fork with ChannelEpoch | fork with ChannelEpoch, any board, SPI or USB | epoch connection |
| fork with ChannelEpoch | stock or older fork | v7; until E3 with the fork catch code, after E3 upstream v7 (CE-18) |
| stock | fork with ChannelEpoch | v7 with CE-7; until E3 with the RRF catch code, after E3 upstream v7 plus CE-7 (CE-18) |
| stock | stock | v7 as upstream |

### 7.2 Milestones

| Milestone | Content | Catch code no longer needed (catalogue `CC-n`) | Gate |
|---|---|---|---|
| E0 preparation | Fake firmware epoch mode (`src/UnitTests/FakeFirmware/FakeFirmware.cs`); the counters of CE-20 on both sides, also in v7 mode for a baseline; K1/K2 reachability tests (Q14); tests of Q9 and Q15; a test that no v7 DSF relies on a lock surviving an InvalidateChannel (FD-17; the only DSF user is M505, `DCS/Codes/Handlers/MCodeHandler.cs:957-990`); a fake-firmware or harness test for each remaining v7 defect (FD-22: K4 RRF side, K9, K10, K11, K12, K15, K17, K19, K22, K24, K27, NEW-2); a test for K20 (FD-23) | none | existing suites green; the E0 tests written; what a test proves is fixed and reported per FD-13 |
| E1 | CE-7: RRF never refuses InvalidateChannel; its apply voids an unsent macro request and releases the locks of an idle GB (CE-25 (a)); a v7 fix, works with stock DSF. The receipt's tag check for repeats needs the epoch and comes with E2 | CC-6 (#161) where RRF has E1; CC-8's first-copy role | the E0 lock test green; lab `AbortsCrossingMacroRequests` 2000 aborts: 0 "Resending packet" for InvalidateChannel, 0 blocks; AC-13 |
| E2 | ChannelEpoch: CE-1 … CE-6, CE-8 … CE-15, CE-20, CE-24, CE-25 apart from (a), and the tag check of CE-7's receipt: only the current epoch passes, a repeated InvalidateChannel is ignored, the filters of every channel packet and answer, the gate, the end of a DoCode, DSF's hook for its requests. Prerequisite: VF-8 (CE-3). The stop rule moved to L1 (FD-30) | in epoch mode CC-2, CC-3, CC-4, CC-5, CC-7 (with CC-6, CC-8 from E1): 7 of the 17 fork-only items. CC-1 is upstream design: its ambiguity is resolved, its code stays (`_allFilesAborted`, `FileAbortSent`, CE-7) | lab 2000 aborts with 0 out-of-order replies, 0 resends, 0 blocks; the fake-firmware abort tests green with CC-2 … CC-8 switched off in epoch mode (AC-1, AC-2); AC-12, AC-14, AC-15; then shown to Christian Hammacher (FD-14) |
| E3 | Delete CC-2 … CC-8 from the v7 path of both sides once the printers run the epoch RRF (CE-18); delete `codeReplySurplus` | CC-2 … CC-8 | the coexistence cases of 7.1 behave as stated (AC-7) |
| L1 | Stop and halt (link): CE-16, the stop overtakes retries and never goes into a SequenceGuard repeat, with the resend lookup kept and room for the stop; CE-17, a halt is never lost. Its own PR; independent of the epoch and of E0 to E3. Prerequisites: VF-1, VF-8, VF-9 (CE-17 (b), (c)) | none: CC-37, CC-38 and CC-40 (stop path) stay | AC-4: M112 ≤ 100 ms over 1000 stops under injected noise, on the plain and the extended link; AC-5: the halt tests |
| then | Evidence gates (7.3) | — | — |

The decision text said "CC-1 … CC-8 deleted". The code says otherwise for CC-1: without `FileAbortSent`
(`RRF/SBC/SbcInterface.cpp:903`) RRF would answer DSF's own abort with AbortFile(all), which DSF answers with another
InvalidateChannel (CE-15), a loop. So CC-1 stays. The separate v7 fixes (section 5) are independent of the
milestones, except that E2 needs VF-8 (CE-3) and L1 needs VF-1, VF-8 and VF-9 (CE-17).

Not removed: CC-1 (code kept, see above), CC-9 … CC-12 (M99 family), CC-13, CC-14/CC-15 (pause, FD-7), CC-16 … CC-20
and CC-22 … CC-24 (matching by position, FD-3), CC-21 (B7, Q5), CC-25 … CC-27 (resend lookup; CC-27 is NEW-1, fixed
by VF-1), CC-28 … CC-30 (buffer estimate), CC-31 (VF-5), CC-32 … CC-35 (correlation of global requests), CC-36
(capability activation, now also used by ChannelEpoch), CC-37, CC-38, CC-40 (stop path), CC-39, CC-41 … CC-47 (link
and RRF-internal).

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

- **Unit tests** in fork-only files under `src/UnitTests`: the fake firmware gets an epoch mode that implements the
  RRF side of section 4. Every requirement gets a test. Every existing test of a deleted catch code keeps running in
  epoch mode; those tests are the regression set.
- **RRF host harness** (`firmware/RepRapFirmware/Tools/sbc-harness`): the `PortMatchesRealFirmware`-style replay
  (`src/UnitTests/SPI/LinkRecovery.cs`) covers CE-16 with SequenceGuard repeats and the resend lookup of a retry
  that carries a stop.
- **Fuzzing** (`src/UnitTests/Fuzz`): `TriggerFuzz` and `StopPathFuzz` with aborts, refusals and RRF restarts.
  Invariants: every code completes exactly once; no reply reaches another code; no channel stays blocked; no DCS stop.
- **Lab** (`src/LabPrinterTests`, procedure LT): the run set of 7.3, M112 under load, the EMC test of CE-19.

| Acceptance | Pass when |
|---|---|
| AC-1 | 3 × 2000 aborts in epoch mode: 0 blocks, 0 "Out-of-order reply", 0 "Resending packet", stale drops only in the counters |
| AC-2 | The fake-firmware abort tests (`TriggerFuzz` abort cases, `TriggerMacros.TriggerChannelWorksAfterInvalidationDuringTriggerRequest`, the DF-T1, T2, T10, T11, T12 tests) green in epoch mode with CC-2 … CC-8 switched off |
| AC-3 | No code is completed by a message of another code in any fuzz run (identity check in the fake firmware) |
| AC-4 | Milestone L1. M112 ≤ 100 ms in 1000 stops under injected noise, on the plain and the extended link; no stop lost; a stop is never placed into a SequenceGuard repeat in either direction, and one that DSF put into a retry that RRF then answered as a repeat goes first into the next new transfer (CE-16). A ResendPacket for any packet of a retry that carries the stop finds that packet and does not end DCS; a stop goes in front of a retry whose transfer is filled to its limit |
| AC-5 | Milestone L1. Halt (CE-17): malformed packets and corrupted transfers injected next to an RRF-local emergency stop (an M112 trigger in RRF) and next to ObjectModel answers that carry status halted: DSF reports halted within a bounded time, never misses it, and DCS does not exit. A dropped transfer during DSF's M112: the stop is not reported done until RRF processed it |
| AC-6 | New static RRF RAM ≤ 1 KB by map file; no heap allocation and no side table (CE-21) |
| AC-7 | The coexistence cases of 7.1 behave as stated, before and after E3; an epoch connection over the USB adapter and on a board other than SAME70 in the fake firmware |
| AC-8 | Refinement (CE-22): the same scripted input in v7 mode and epoch mode gives per channel the same sequence of logic events, apart from the events that the v7 catch code drops |
| AC-9 | No deadlock: in every fuzz and lab run each code ends (reply, cancel or error); a watchdog in the fake firmware host flags any channel that has work but makes no progress |
| AC-10 | The hold at connect (CE-3) ends within its timer in every connect and reset case of the census, also when an ObjectModel answer for another key arrives at connect (VF-8), and a macro request at connect is answered before RRF's 3 s timeout |
| AC-11 | EMC test of CE-19: corrupted transfers per hour measured; ≤ 100 per hour, or the trailer of CE-19 is specified |
| AC-12 | Crossing abort with the gate (CE-24): in the fake firmware RRF writes AbortFile(all) in the same exchange in which DSF writes InvalidateChannel(e+1), 1000 times with random timing, and RRF-local emergency stops in between: no channel stays gated or blocked; DSF answers only an AbortFile(all) of its current epoch; the channel runs codes afterwards; no DSF request that the gate dropped waits on DSF's side |
| AC-13 | Stale Unlock (CE-25 (a)): on an idle GB, an Unlock that RRF refused and whose copy arrives after InvalidateChannel(e+1) is dropped and leaves no lock; another channel locks and moves right after the apply. The same for an Unlock that DSF queued and dropped at the abort (K28). A GB that still runs a code at the apply, e.g. a G28 between two homing files, keeps its locks and `toBeHomed` until that code ends, and no other channel moves meanwhile. With a stock DSF (E1) no test of the existing suites relies on the lock after an InvalidateChannel |
| AC-14 | DoCode and abort (CE-25 (b)): a DoCode that runs in DSF when its channel is aborted fails with an error and sends no result; RRF's GB no longer executes on the SBC after the apply; a DoCode result of the old epoch is dropped and does not finish a newer DoCode; the next DoCode on that channel completes |
| AC-15 | Repeats, ahead tags and stale requests (CE-5, CE-7, CE-12): in the fake firmware without SequenceGuard a transfer with InvalidateChannel(e+1) is processed twice; the second receipt is counted and ignored, the codes stored after the first stay, a macro wait begun after it is not ended and no file of the new epoch is aborted. A DSF packet whose tag is ahead of RRF's epoch and an RRF packet whose tag is ahead of DSF's are dropped and counted; untagged packets pass. A G28 whose homing file an abort popped ends without RRF's 3 s macro timeout, and a daemon.g request whose level the apply popped is not sent |

## 9. Unchanged decisions and boundaries

- Matching by position stays within an epoch (FD-3); a level id only on evidence (FD-21, EG-6); pause stays v7 apart
  from the tag of PrintPaused (FD-7, FD-19, LA-13).
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

### 10.2 Final decisions (Tim, 2026-11-09, after the reviews)

| # | Decision | Applied in |
|---|---|---|
| FD-1 | **Direction.** No v8: no own version number, no new header, no streams, stall or go-back-N, no RESET_ACK, no STOP_ACK, no credit, no request ids, no session resume, no code identity for now. Instead a channel epoch on v7 plus evidence gates | sections 1, 4, 7.3 |
| FD-2 | **Carrier and switch-on.** A link capability `ChannelEpoch` = 8, offered in `limits.sbcLinkCapabilities`, activated with 0x4D50. RRF offers it outside the SAME70-only block, on every board and on USB; DSF holds channel traffic until the capability state is known, a bounded wait at connect | CE-1 … CE-3 |
| FD-3 | **The tag.** `resendPacketId` carries the epoch in both directions; payloads do not grow. Code identity only on evidence | CE-5, CE-6, EG-1 |
| FD-4 | **Companion 1.** RRF never refuses InvalidateChannel: receipt without the mutex, apply with it; `FillBuffer` holds the channel's codes until the apply; reuse `invalidationPending` (renamed since draft 7, CE-7), delete `codeReplySurplus`. A plain v7 fix | CE-7; deletion of `codeReplySurplus` in E3 (section 7.2) |
| FD-5 | **Companion 2.** DSF's resent copies keep their tag | CE-8 |
| FD-6 | **RRF side.** Stamp the epoch on accepted codes, carry it to `codeEpoch` in `FillBuffer`, filter stale Code, MacroStarted, MacroCompleted before their side effects; reply tag through `GetResponseMessageType` and free `MessageType` bits into `resendPacketId`; stack events carry `codeEpoch`; stale requests fail locally; `InvalidateResources` marks `codeEpoch` invalid | CE-9 … CE-12 (Q22 decided by FD-19) |
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
| FD-18 | **DoCode under the general rule (K13).** DoCode carries the channel epoch. At InvalidateChannel RRF ends its own pending DoCode wait (draft 6 puts it into the apply, because it needs the GB mutex, CE-25; confirmed by FD-25), and DSF fails a DoCode of the old epoch with an error. Behaviour change: an abort of the channel also ends a running DoCode. Draft 5's "DoCode untouched, never filtered" is removed | CE-6, CE-10, CE-12, CE-14, CE-25 (b), LA-10, AC-14 |
| FD-19 | **Tag sources in RRF (Q22).** CE-9 stands. Output of a code DSF sent, and the helper macros RRF requests while executing that code (homing, tool change, probe, pause.g, …), carry the code's epoch. Work that RRF starts itself carries the current channel epoch when raised: `AsyncSystemMacroCode` macros (`fromCode` false, `RRF/GCodes/GCodes.cpp:3699`: config.g `:414`, daemon.g `:690`, triggerN.g `:1052`, start.g `:4001`, event macros `GCodes3.cpp:1432`, stop.g at the end of a print `GCodes4.cpp:844`), PrintPaused, the box timeout and RRF's own AbortFile | CE-9 and the table under CE-12 |
| FD-20 | **One DSF epoch plus an RRF gate bit; two epochs rejected.** Tim proposed a DSF epoch d and an RRF epoch r. Both reviewers rejected it: **(a)** crossing aborts deadlock: RRF raises r and writes AbortFile(all) tagged (r+1, d) while DSF writes InvalidateChannel(d+1, r) in the same exchange; each side drops the other's announcing packet, and from then on every packet fails the other side's filter; **(b)** an RRF-local emergency stop has no announcing packet: `DoEmergencyStop` sends none of its own (`RRF/GCodes/GCodes.cpp:1065-1070`) and `GCodeBuffer::Reset` clears the abort flags (`GCodeBuffer.cpp:145`), so raising r there would make RRF drop all DSF channel packets while DSF waits for their answers without a bound (V7-85); **(c)** "K18 fixed directly" does not hold: the internal reply path has only 7 free `MessageType` bits (`RRF/Platform/MessageType.h:16-50`: bits 18–23 and 26), which d uses (CE-11), so r cannot travel with a reply; K18 is fixed by d through CE-15; **(d)** a dropped stale Unlock (FD-17). The one gain of r, that RRF drops crossing DSF packets between its own abort and DSF's InvalidateChannel(e+1), is kept by a gate bit per GB: set where AbortFile(all) is written (`RRF/SBC/SbcInterface.cpp:1549`); while set, RRF drops DSF channel packets of that channel except InvalidateChannel; the InvalidateChannel(e+1) that DSF sends back clears it; nothing raises it on an RRF-local emergency stop; the macro timeout stays LA-8 | CE-24, section 11 |
| FD-21 | **Level counter rejected, level id staged, serial comparison now.** Single-file aborts (M99, macro errors) stay matched by position. A level counter (stack depth) is rejected: a depth is reused after an abort (ABA); RRF has levels DSF does not know, the M120 local pushes (`localPush`, `RRF/GCodes/GCodeMachineState.h:272`; pushes at `GCodes2.cpp:2273`, `GCodes6.cpp:151`, `GCodes7.cpp:110`; `PopState` pops them together with the next file level, `GCodeBuffer.cpp:1092-1112`, `:1105`); and (depth, generation) in 7 bits wraps after 8 macros, because a depth up to 10 (`Config/Configuration.h:243`) needs 4 bits. A level id is the right idea later, under these conditions: its owner is DSF's `StackState` (`DCS/Link/Channel/StackState.cs:14`), not the file object, because DSF pushes levels without a file (message boxes `Processor.cs:1273`, M606 copies `:381`, `:389`); it only filters and never decides when a reply is delivered, so `PendingReplies` stays (`Processor.cs:450`, `:870-876`), otherwise an M98 could complete before its macro ends; it removes the cross-level misattributions (the late-M99 class, fork fix #44; parts of CC-9 and CC-16 … CC-18) but not CC-11 and CC-12 (dummy reply and drains), which would need per-code identity; B7 changes on both sides in lockstep; the 6-bit wrap bound against the 64-entry reply FIFO (`RRF/Platform/OutputMemory.h:16`) is not proven. Staged: **now** the epoch is compared as an RFC 1982 serial number, not for equality, so a later level id can come from the same per-channel counter without a new field (since FD-29 only the current epoch passes until then; the serial comparison decides the receipt of InvalidateChannel and the counter class); **later** the level id is added when EG-1 shows a cross-level misattribution after E2 | CE-5, EG-6, Q30 |
| FD-22 | **Remaining v7 defects (Q21).** A fake-firmware or harness test for each in E0: K4 (RRF side), K9, K10, K11, K12, K15, K17, K19, K22, K24, K27, NEW-2. What a test proves is fixed, and reported to Christian Hammacher per the K reporting rule (FD-13); the rest stay documented | E0 |
| FD-23 | **STOPPED and STOP_ACK (Q27).** STOPPED is deferred until an E0 test shows K20. STOP_ACK is dropped | E0, LA-7 |

### 10.4 Decisions after draft 6 (Tim, 2026-11-09)

Tim accepted every fix of the independent review of draft 6 (section 11) and confirmed three choices of draft 6.

| # | Decision | Applied in |
|---|---|---|
| FD-24 | **Answers carry the epoch of their request (confirms the consequence of FD-16).** Locked, EvaluationResult and VariableResult carry the tag of the request they answer, and DSF drops stale ones (CE-14). The tag needs no store while each answer is written inside the handler of its request; once VF-5 makes Lock a pending request, the Lock's tag is kept per GB (section 6) | CE-6, CE-14, VF-5, section 6 |
| FD-25 | **RRF ends a running DoCode in the apply, not at the receipt (confirms draft 6).** Ending it needs the GB mutex (`RRF/SBC/SbcInterface.cpp:1764`, `:784`); until the apply `FillBuffer` holds the channel's codes and a stale DoCode result is dropped | CE-25 (b), LA-10 |
| FD-26 | **A stop stays pending until a transfer whose data RRF reads (confirms draft 6's reading of FD-15).** A retry of a transfer that RRF did not complete carries it; a SequenceGuard repeat does not, and a stop that DSF put into a retry that RRF then answers as a repeat stays pending | CE-16, AC-4 |
| FD-27 | **The abort releases the locks of an idle GB only (review M1).** The apply calls `UnlockAll` only if `gb->IsCompletelyIdle()`; otherwise `HandleResult` releases the locks when the code ends (`RRF/GCodes/GCodes2.cpp:5112-5116`). Reason: the apply can run while a code of the old epoch still executes (`RRF/SBC/SbcInterface.cpp:907-911`), and `UnlockAll` at the base level clears `toBeHomed` (`RRF/GCodes/GCodes.cpp:5658-5663`), which changes a running G28. The dropped stale Unlock after M505 and K28 are still covered, because the GB is idle there | CE-7, CE-25 (a), V7-53, V7-57, V7-58, LA-22, AC-13 |
| FD-28 | **The receipt of InvalidateChannel acts once (review M2).** It records the tag as the channel epoch only if the tag follows it (RFC 1982); an equal or preceding InvalidateChannel is counted and ignored. Draft 6 had dropped v7's first-copy guard (`RRF/SBC/SbcInterface.cpp:882`), so a transfer repeated on SPI without SequenceGuard would have purged the codes stored since, ended a new macro wait and aborted new files without telling DSF. The sentence of draft 6 that a tag ahead cannot occur is corrected: InvalidateChannel always carries e+1 | CE-5, CE-7, CE-20, AC-15 |
| FD-29 | **Only the current epoch passes (review M3, m2).** Apart from InvalidateChannel's e+1, both sides drop and count every tag that is not the channel epoch, until a level id exists (EG-6). Draft 6 passed a tag that follows, which with 6 bits a packet 33 to 63 aborts old also does. RRF stamps a code with the tag of its packet that passed the filter | CE-5, CE-9, CE-10, CE-14, Q28 |
| FD-30 | **Stop and halt as link milestone L1 (review m1).** CE-16 and CE-17 do not depend on the epoch and move out of E2 into L1, with its own PR and gate (AC-4, AC-5). A stop in front of a retry MUST NOT break DSF's resend lookup (`DCS/Link/Adapter/SPI.cs:826-855`): the retry renumbers its packets, or the lookup becomes independent of the order. Every transfer keeps room for the stop (8 bytes) | CE-16, CE-17, section 7.2, AC-4, AC-5 |
| FD-31 | **No new work while the apply is pending (Q33, Tim 2026-11-09).** Option (a): as `FillBuffer` holds DSF's codes, the main task starts no other new work on a GB whose apply is pending (trigger or daemon macro, text code from PanelDue or USB). It skips that GB in its spin and does not wait for the apply, so it releases the GB mutex as usual (`RRF/GCodes/GCodes.cpp:590`) and the SBC task can apply; the work starts after the apply, in the new epoch. No new wait edge | CE-7, Q33 |
| FD-32 | **Timer of the hold at connect (Q23, Tim 2026-11-09).** E0 measures the time from connect to the ChannelEpoch state on the lab printer. The timer is set from it with margin, but always below RRF's 3 s macro wait (`RRF/SBC/SbcMessageFormats.h:42`), because the hold also delays MacroStarted, DSF's answer to RRF's own macro requests such as config.g (CE-3). On expiry DSF continues without ChannelEpoch (plain v7) and counts it | CE-3, Q23, E0 |
| FD-33 | **Epoch width 6 bits (Q28, Tim 2026-11-09).** Confirmed: the reply tag uses the free `MessageType` bits 18–23; a stale packet is recognised while fewer than 64 aborts of its channel happened during its flight; the counters show wrap-related drops if they ever occur | CE-5, Q28 |

### 10.5 What happened to the v8 requirements of drafts 1 to 4

| v8 requirement | Fate in draft 5 (and 6, 7) |
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
| V8-9 RESET and RESET_ACK | Replaced by CE-7 (never refused, receipt and apply), without an answer packet |
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
| V8-24 a session change clears state | Split: VF-6 (K5), VF-7 (K7); epochs CE-4 |
| V8-35 unknown and invalid packets | VF-1 (DSF); RRF's K4: an E0 test (FD-22) |
| V8-37 B7 stays | Unchanged (V7-22 identical) |
| V8-25 static RAM < 1 KB | Kept: CE-21 |
| V8-26 S layer as its own source file | Dropped |
| V8-27 counters | Kept: CE-20 |
| V8-28 refinement | Kept: CE-22 |
| V8-29 cross-channel write order | Dropped; the v7 order stays |
| V8-30 wait-for graph | Kept: CE-23 |
| V8-31 control packets without waiting | Kept for InvalidateChannel: the receipt of CE-7 |
| V8-32 every new wait bounded | Kept: the hold at connect (CE-3, Q23) |
| V8-33 graph reviewed before M1 | Replaced by the E0 reachability tests (Q14) |
| V8-34 completion on acceptance | Dropped; K1 and K2 open again (Q14) |
| V8-43 time-based retries and resync | Dropped (FD-10) |
| V8-44 no exit on link data | Kept as VF-1 |
| V8-45 session lifetime and resume | Dropped (FD-10) |
| V8-46 events to the logic | Kept as VF-2 |
| V8-47 no motion or safety function in the link | Kept as a principle (layering rule) |

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

## 12. Open questions and what is unverified

| # | Question | Status |
|---|---|---|
| Q1 | NEW-1: the USB resend looks into the wrong TX | Decided: fixed in VF-1. Code reading, not tested |
| Q2 | NEW-2: can the 64-entry reply FIFO fill in practice? | Open; EG-1 would show it as a non-stale out-of-order reply |
| Q3 | NEW-3: can RRF refuse a code that DSF's estimate allowed? | Closed into EG-3 |
| Q4 | Is logical credit physically storable? | Closed: no credit (FD-1) |
| Q5 | B7 read-ahead drop | Decided: B7 stays |
| Q6 | Does every message of a code pass one capture point for its tag? | Answered: `GetResponseMessageType` builds every SBC reply type; 15 call sites; bits 18–23 and 26 are free (CE-11) |
| Q7 | Cross-channel dependencies of the logic | Closed: the capability keeps the v7 order across channels |
| Q8 | Switch point of the session layer within a connection | Closed: ChannelEpoch is activated before any channel packet (CE-3). The switch-window findings OF-S1 and OF-S5 of the extended header stay open in open-findings.md |
| Q9 | Is `AbortFile(true)` harmless when RRF already aborted all files itself (CE-15)? | Decided: a fake-firmware test in E0 |
| Q10 | Side effects of a refused Lock | Answered in draft 3: it keeps the lock it reached (VF-5) |
| Q11 | SetPrintFileInfo alignment after ActivateLinkCapabilities in one TX (G-19) | Open again: with the capabilities negotiated on v7 (FD-11) the case of G-19 stays; whether the alignment matters was not checked |
| Q12 | H1 ("Daemon: Out-of-order reply ''" after a DCS start), H2 ("Failed to find suitable stack level for flush request") | Decided: watch them after E2 |
| Q13 | DSF-internal codes that write mutex requests during a macro wait | Answered: logic (LA-16 … LA-18) |
| Q14 | Can C-1, C-2 and C-3 be reached (K1–K3)? | Decided: fake-firmware tests in E0. K1 and K2 have no fix in drafts 5 and 6 |
| Q15 | Can a MacroCompleted cross an RRF-side pop (X-7)? | Decided: a fake-firmware test in E0 |
| Q16 | Can the global stream stall before SetPrintFileInfo? | Closed: no streams |
| Q17 | Version handshake with a v7 DSF | Closed: no own version |
| Q18 | Third transfer buffer missing on a v8 RRF | Closed: as today, SequenceGuard is offered only with the buffer (`RRF/SBC/DataTransfer.cpp:478`) |
| Q19 | PR10 details (lifetime, session id, LinkDown window) | Closed: no resume. The LinkDown window moves to Q29 |
| Q20 | Per-packet check details | Closed: replaced by CE-19; trailer details Q26 |
| Q21 | v7 defects that neither the capability nor section 5 fixes: K4 (RRF side), K9, K10, K11 (stop path), K12, K15, K17, K19, K22, K24, K27, NEW-2. Fix each or not? | Decided (FD-22): a fake-firmware or harness test for each in E0; what a test proves is fixed and reported (FD-13); the rest stay documented |
| Q22 | Tag of work that RRF started itself (a macro request without a code, the closal of a system macro without codes) | Decided (FD-19): CE-9 stands, two tag sources; the table under CE-12 lists every packet |
| Q23 | The bound of the hold at connect (CE-3), after which the connection runs without ChannelEpoch. Since draft 7 it is a timer: the capability query waits behind other object model queries (one per transfer, `DCS/Link/LinkService.cs:470-478`), and an answer for another key leaves the query "sent" (`:714-723`, K16) until VF-8, now a prerequisite of E2. Illustration for its value: 0.4 s, 16 transfers (the bound DSF uses for the extended switch, V7-101) at the idle pacing of 25 ms (`RRF/SBC/SbcMessageFormats.h:37`), below RRF's 3 s macro wait | Decided: a timer (review m7); its value is measured in E0 and stays below 3 s, on expiry plain v7, counted (FD-32) |
| Q24 | How the stop of CE-16 reaches RRF when the retry is a SequenceGuard repeat that RRF answers without processing | Decided (FD-15, confirmed by FD-26): the stop goes first into the next new transfer; a repeat stays a pure repeat (CE-16, AC-4, L1) |
| Q25 | Should RRF also drop stale copies of DeleteLocalVariable, SetLastCodeResult, Unlock and Lock (CE-10)? | Decided (FD-16): RRF drops every stale DSF channel packet; DSF fails its own requests of the old epoch (CE-25); the abort releases the locks of an idle GB (FD-17, FD-27) |
| Q26 | Algorithm and placement of the per-transfer CRC trailer, if the EMC test exceeds 100 corrupted transfers per hour (CE-19) | Open until the EMC test, and only if it requires one |
| Q27 | STOPPED from RRF (old D9) and STOP_ACK (old D5) | Decided (FD-23): STOPPED deferred until an E0 test shows K20; STOP_ACK dropped |
| Q28 | The epoch has 6 bits, not 8 as the example of FD-3: the reply path has only bits 18–23 and 26 free in `MessageType` (`RRF/Platform/MessageType.h:16-50`). Since FD-29 only the current epoch passes, so a stale packet is recognised while fewer than 64 aborts of its channel happened during its flight. The RFC 1982 order (CE-5, section 3.2), which the receipt of InvalidateChannel and the counter classes use, holds below 32; draft 6 passed ahead tags and so recognised a stale packet only below 32. A later level id from the same counter would use up values faster (Q30) | Decided: 6 bits (FD-33) |
| Q29 | Thresholds of VF-2 (LinkDown window, rate for "degraded") and how PeerRestarted is told from a connection reset without a restart counter (`ExtendedLink.cs:65`) | Open |
| Q30 | **New in draft 6. Level id (FD-21).** Before it is added: owner DSF's `StackState`; it only filters, `PendingReplies` stays; it does not cover CC-11 and CC-12; B7 changes on both sides in lockstep; the 6-bit wrap bound against the 64-entry reply FIFO is not proven | Open item; added only when EG-6 fires |
| Q31 | **New in draft 6. Work that RRF raises while a code of an older epoch can still run.** The box timeout and PrintPaused keep their epoch apart from `codeEpoch` (CE-9); writing it into `codeEpoch` would give that code's later replies the new epoch. A message box that survives an abort at the base level (`AbortFile` does nothing without a file, `RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1121-1123`; OF-T10) is not ended by the apply; its timeout acknowledgement carries the epoch when raised and passes DSF's filter, as in v7, where the code's epoch would have dropped it. Whether the apply should end such a box under the general rule, as `InvalidateResources` does on a link loss (`RRF/SBC/SbcInterface.cpp:1772`), is logic (LA-23) | Open, for Tim |
| Q32 | **New in draft 6.** PrintPaused has one tag, File's epoch; File2's epoch is not carried. DSF aborts File and File2 together on a job abort (LA-12); whether File2 can be aborted alone while a pause is reported was not checked | Open |
| Q33 | **New in draft 7. Work that RRF starts between the receipt and the apply.** The receipt raises the channel epoch at once; the apply comes when the SBC task gets the GB mutex (CE-7), which the main task holds for a whole spin, macro waits included (V7-87). Work that RRF starts on that GB in between gets the new epoch (source 2, CE-9), but the apply aborts it: a daemon.g request (voided by the apply, CE-7), a trigger macro whose request DSF has answered in the new epoch, a text code from PanelDue or USB that opened a macro; `AbortFile(true)` pops their levels, and their later packets carry the new epoch and pass DSF's filter. v7 has the same order with a refused and resent InvalidateChannel. Options: (a) the main task starts no new work on a GB whose apply is pending, as `FillBuffer` holds DSF's codes; (b) the main task runs a pending apply itself at the start of its spin, where it holds the GB mutex (`RRF/GCodes/GCodes.cpp:590`); (c) such work takes the old epoch, so that its requests fail. How often the window occurs was not measured | Decided: (a), the main task starts no new work on that GB until the apply (FD-31, CE-7) |

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
