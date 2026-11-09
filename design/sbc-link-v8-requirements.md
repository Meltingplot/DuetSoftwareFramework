# SBC link: channel epoch on v7 (requirements; replaces the v8 session layer)

Status: draft 5, 2026-11-09. Nothing here is implemented. No code was changed for it. The decisions of section 10 are
Tim's.
Draft 5 replaces the v8 session layer of drafts 1 to 4 by a minimal design: a **channel epoch on protocol version 7**,
switched on as a link capability, plus **evidence gates** that decide later whether more is needed. Tim decided this
on 2026-11-09 after three expert reviews of draft 4 (section 11). The file keeps its path so that links stay valid.
Section 10.3 lists what happened to every requirement of drafts 1 to 4.
Audience: Tim, later Christian Hammacher (Duet3D). The reader knows DSF and RRF, not the working papers.

Scope: the channel epoch (capability, tag, filters, the two companion fixes), the stop path above the transfer retries,
the rule that a halt is never lost, the evidence gates, and the list of separate v7 fixes found on the way.
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
  these commits for draft 5, including those that came from the review reports.
- **Paths:** `DCS/` = `src/DuetControlServer/`. `RRF/` = RRF `src/`. `pkg/` = `pkg/` of this repository.
- **PRs:** `#n` = Meltingplot/DuetSoftwareFramework, `RRF #n` = Meltingplot/RepRapFirmware.
- **IDs of this document:** `CE-n` requirements of the channel epoch and its companions (section 4), `VF-n` separate
  v7 fixes (section 5), `EG-n` evidence gates (section 7.3), `E0`–`E3` milestones (section 7.2), `AC-n` acceptance
  (section 8), `FD-n` final decisions (section 10), `Qn` open questions (section 12), `PRn` principles (section 3).
- **Working papers** (this document repeats every code reference it needs):
  - [sbc-link-v8-catalogue.md](sbc-link-v8-catalogue.md): 47 catch-code items. `CC-n` is row n of its index
    (section 12). Its letter IDs (`A1`, `B7`, `NEW-1`) are kept;
  - [sbc-link-v8-patterns.md](sbc-link-v8-patterns.md): patterns `P1`–`P12` from mainstream protocols;
  - [sbc-link-v7-contract.md](sbc-link-v7-contract.md): the v7 contract, 101 items `V7-n`, the wait-for graph
    (`E-1`…`E-53`, cycles `C-1`…`C-8`) and the v7 defects (findings `K1`…`K27` in open-findings.md).
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
  (CE-19, FD-12). v2 R10 (stop within 100 ms on a disturbed link) gets one requirement here (CE-16).
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

| # | v7 assumption | Evidence | What draft 5 does |
|---|---|---|---|
| A-1 | Replies and stack events belong to the first buffered code of the channel (`DCS/Link/Channel/Processor.cs:1102-1117`) | DF-T10 (printer, 2026-11-08); OF-T4 | Replies and stack events of an older epoch are dropped (CE-14). Matching by position within an epoch stays (FD-3); code identity only on evidence (EG-1) |
| A-2 | The pipeline depth is exactly one transfer, counted, never acknowledged (`RRF/SBC/SbcInterface.cpp:257-268`) | DF-T2, DF-T12 (printer 2026-11-08) | The abort windows no longer count transfers (CE-13, CE-14). The resend lookup and `BytesReserved` stay; NEW-1 is VF-1 |
| A-3 | Abort (`InvalidateChannel`) needs no answer and no epoch | DF-T1, T2, T10, T11, T12 | The epoch (CE-5 to CE-15); RRF never refuses InvalidateChannel (CE-7) |
| A-4 | `ResendPacket` is NACK, back-pressure and polling at once; a refused packet can have acted already | DF-T11 (printer); NEW-3 | InvalidateChannel is never refused (CE-7); copies keep their tag (CE-8); act-before-refuse fixes (VF-3, VF-4); Lock as a pending request (VF-5); per-channel stall only on evidence (EG-2) |
| A-5 | Shared resources without per-channel credit (`RRF/SBC/SbcInterface.cpp:401-433`, one 64-entry reply FIFO `RRF/Platform/OutputMemory.h:16`) | DF-T3 (printer) | Unchanged: #133 stays; absolute credit only on evidence (EG-3) |
| A-6 | The stop waits for the transfer in progress and is completed by counting (`DCS/Link/LinkService.cs:313-335`, `:454-456`, `:598-599`) | Stop path tests: about 503 ms worst case (OF "Stop path under SPI faults"); OF-P3 | The stop overtakes retries (CE-16); a halt is never lost (CE-17) |

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

| ID | Principle | Status in draft 5 |
|---|---|---|
| PR1 | Identity, not position or timing | Partly: the epoch identifies what an abort cancelled; replies are still matched by position within an epoch (FD-3) |
| PR2 | Order is kept within a channel; across channels the v7 order stays | Holds: nothing reorders packets |
| PR3 | A few absolute classes; one expedited class | Dropped (no classes). What remains is CE-16: a pending stop overtakes the retries of a transfer |
| PR4 | Cancellation changes an epoch | Holds, without an acknowledgement packet: InvalidateChannel carries the new epoch and is never refused (CE-7) |
| PR5 | Flow control by absolute credit | Dropped; only on evidence (EG-3) |
| PR6 | A refusal means nothing was done; re-sent content is identical | Holds as fixes: CE-7, CE-8, VF-3, VF-4 |
| PR7 | Request state lives on the (channel, epoch) object | Holds: the epoch is kept on DSF's channel `Processor` and on RRF's `GCodeBuffer` (CLAUDE.md rule 3) |
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
| CE-3 | L | **Activation before channel traffic.** Activation rides on an object model round trip (`DCS/Link/LinkService.cs:1539-1575`: query of `limits.sbcLinkCapabilities`, ActivateLinkCapabilities, check of `state.sbcLinkCapabilities`). After every connect and reset, DSF MUST NOT run the channel Spins (`:459-462`), which write every Code, MacroStarted, MacroCompleted, InvalidateChannel, Lock and Unlock, until the ChannelEpoch state is known: ActivateLinkCapabilities with ChannelEpoch written, or the capability not offered, or a bound expired (Q23), after which the connection runs without it. RX processing goes on meanwhile. ChannelEpoch MUST NOT wait for the sequence-range condition of ExtendedHeader (`DCS/Link/Adapter/SPI.cs:940-945`); a later request MUST repeat the whole set, because RRF replaces the active set (`RRF/SBC/DataTransfer.cpp:834`) and leaves the extended link if ExtendedHeader is missing (`:829-832`) | — | No new order: events are only delayed, as on a slow link in v7. A new wait, at connect only, bounded; v7 already holds codes at connect until the first buffer report (`DCS/Link/LinkInterface.cs:613`, V7-14). The bound MUST stay well below RRF's 3 s macro wait (`SbcMessageFormats.h:42`), so a macro request at connect does not time out |
| CE-4 | S | **Epoch lifetime.** Each side keeps one epoch per channel. Both start at 0 when ChannelEpoch is activated in a connection (RRF: when the activation sets the bit and it was not set before, and it marks every `codeEpoch` invalid then; DSF: when it writes the activation). Nothing of the epoch is stamped, set or written while ChannelEpoch is not active. The epochs end with the connection: RRF clears ChannelEpoch with every connection reset (`RRF/SBC/DataTransfer.cpp:1431`) and marks every `codeEpoch` invalid in `InvalidateResources` (`RRF/SBC/SbcInterface.cpp:1717-1780`); DSF resets its epochs where it ends the connection (`DCS/Link/LinkService.cs:389-396`, next to `_queryLinkCapabilities`). DSF MUST NOT reset them in `Processor.Invalidate()` (`DCS/Link/Channel/Processor.cs:1491-1538`), because that also runs on every emergency stop (`DCS/Link/LinkService.cs:1511-1517`, `DCS/Link/LinkInterface.cs:606-612`), while RRF keeps its epochs across a stop | — | Not visible to the logic |

### 4.2 Tag

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-5 | S | **Carrier and format.** Once ChannelEpoch is active, `PacketHeader.resendPacketId` (16 bit, `RRF/SBC/SbcMessageFormats.h:101-107`) carries the tag in both directions: bit 15 = tagged, bits 0–5 = epoch, bits 6–14 = 0. 0 means untagged (v7). The field is free outside ResendPacket: DSF writes 0 (`DCS/Link/Protocol/Writer.cs:52`, `DCS/Link/Adapter/SPI.cs:789`, `USB.cs:683`) and reads it only in the resend lookup (`SPI.cs:831`, `USB.cs:729`, called from `DCS/Link/LinkService.cs:613-614`); RRF writes it only in `WritePacketHeader` (`RRF/SBC/DataTransfer.cpp:2290-2303`) and never reads it. In ResendPacket the field keeps its v7 meaning (`RRF/SBC/DataTransfer.h:238-241`). Payloads MUST NOT grow: RRF advances its read pointer only by what each handler reads (`RRF/SBC/DataTransfer.cpp:628-640`; e.g. `ReadCodeChannel` reads 4 bytes, `:705-709`), so a longer payload would garble the later packets of a stock RRF. The channel stays where it is (payload or destination bit). The epoch has 6 bits because the reply path carries it in the free bits of RRF's `MessageType` (CE-11); epochs are compared for equality, so a stale packet matches a newer epoch only after 64 aborts of its channel while it is in flight (Q28) | — | No order change; a stock peer never sees a tag (CE-18) |
| CE-6 | S | **What is tagged.** DSF tags every packet with a channel field: Code, MacroStarted, MacroCompleted, InvalidateChannel (with the new epoch), Lock, Unlock, EvaluateExpression, SetVariable, DeleteLocalVariable, SetLastCodeResult. RRF tags code replies (Message with `BinaryCodeReplyFlag`) and the stack events ExecuteMacro, AbortFile, WaitForAcknowledgement, MacroFileClosed and MessageAcknowledged. Untagged stay: ResendPacket, ObjectModel, CodeBufferUpdate, generic and targeted messages, EvaluationResult, VariableResult, Locked, DoCode, PrintPaused, file requests and results, and DSF's Message to RRF. DoCode stays untagged because InvalidateChannel does not end it in v7 (V7-53, V7-70) | — | Tagging alone changes nothing |

### 4.3 The two companions

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-7 | S | **Companion 1: RRF never refuses InvalidateChannel.** Today RRF acts before it refuses: it flags `codeReplySurplus`, voids an unsent request and ends a macro wait (`RRF/SBC/SbcInterface.cpp:880-895`), then waits 10 ms for the GB mutex (`:897`) and refuses (`:914-918`); this causes the resends of T11 (DF-T11, CC-6). RRF MUST split the handler. **Receipt**, without the GB mutex, always: record the new epoch (CE-4), end a macro wait with an error (`:894`), purge the channel's ring codes (`InvalidateBufferedCodes` needs only a critical section, `:2527-2529`), set "apply pending" (the existing `invalidationPending`, `RRF/GCodes/GCodeBuffer/GCodeBuffer.h:422`). **Apply**, with the GB mutex: `PauseSequenceAborted`, `AbortFile(true)`, `FileAbortSent` (`:901-903`), at once if the 10 ms try succeeds, otherwise wherever the SBC task next holds that mutex, at the latest in the write phase (`:1530-1533`). Until E3 the fork catch code of the v7 path stays in both parts, each now run once: in the receipt the `codeReplySurplus` flag of a code waiting for its macro (`:886-889`, RRF #46) and the voiding of an unsent request (`:893`, RRF #43); in the apply `MacroFileClosedSent` (`:904`, RRF #45) and the flag of a code still running (`:908-911`). The apply MUST NOT purge the ring again (today `:905`), because codes of the new epoch may have arrived since the receipt. `FillBuffer` MUST hold the channel's codes while the apply is pending (add to `:1892-1894`). `FileAbortSent` MUST stay, else RRF would answer DSF's own abort with AbortFile(all), which DSF answers with another InvalidateChannel (CE-15). This is a v7 fix that works with a stock DSF. Without copies, the first-copy guard of `codeReplySurplus` (`firstCopy = !IsInvalidationPending()`, `:882`) is dropped: a second abort while an apply is pending is a new abort, not a copy. `codeReplySurplus` itself (`GCodeBuffer.h:405`) is deleted in E3 | CC-6 (#161), CC-8's first-copy role | v7 already ends the wait before the mutex (`:883-895`, the breaker of C-4); RRF still runs codes of the channel only after the apply, as v7 does after an accepted InvalidateChannel. Removes the resend and its reordering |
| CE-8 | S | **Companion 2: copies keep their tag.** DSF MUST write a resent copy with the tag of the original. Today the copy gets 0, because `ResendPacket` writes it through `WritePacket` (`DCS/Link/Adapter/SPI.cs:837` → `:782-790`; `USB.cs:735` → `:676-684`). The tag is passed as a parameter, not looked up | — | No order change |

### 4.4 RRF side of the epoch

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-9 | S | **Epoch of a code and of the GB's work.** RRF MUST stamp the channel epoch on every code it accepts into the ring, in the free `BufferedCodeHeader.padding` (`RRF/SBC/SbcMessageFormats.h:368-373`; written today only `isPending` and `length`, `RRF/SBC/SbcInterface.cpp:424-426`). `FillBuffer` MUST carry it into a per-GB `codeEpoch` (valid bit + 6-bit epoch) when it hands the code over (`:1910`, `:1945`). **Correction to FD-6 (Q22):** work that RRF starts itself has no ring code. When RRF raises a macro request without a code (`fromCode` false: triggers from `CheckTriggers`, `RRF/GCodes/GCodes.cpp:1052`; daemon.g, `RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1258-1260`) or takes a text code (`GCodeBuffer::Put`, `PutAndDecode`, `GCodeBuffer.cpp:294`, `:334`, `:345`: PanelDue, USB, trigger actions), `codeEpoch` MUST become the current channel epoch. Without this, a trigger request after an abort of the Trigger channel would carry the epoch of the last code before the abort and DSF would drop it (RRF then times out after 3 s and the trigger is lost), and the closal of a system macro without codes would be dropped, so DSF's level would wait for it for good (E-17). `InvalidateResources` marks `codeEpoch` invalid; an invalid `codeEpoch` gives no tag (v7 behaviour) | — | Not visible to the logic |
| CE-10 | S | **Filter of stale DSF packets.** RRF MUST drop and count a tagged Code, MacroStarted or MacroCompleted whose epoch is not the channel epoch, before any side effect: a Code resolves a macro wait at `RRF/SBC/SbcInterface.cpp:383-392` before its buffer check (`:395`); MacroCompleted resolves a wait or pops (`:558-597`); MacroStarted resolves a wait (`:839-842`). Stale copies of the other tagged types are counted and applied as in v7 (Q25); EvaluateExpression and SetVariable MUST still be answered, because DSF waits for the answer | CC-5 (RRF #43) together with CE-12 | Removes orders only: a cancelled code no longer runs after the abort, a stale MacroStarted or MacroCompleted no longer acts on a level of the new epoch |
| CE-11 | S | **Tag of code replies.** `GCodeBuffer::GetResponseMessageType` (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1335-1344`) is the only place that builds the reply type of a code from the SBC (`(1u << channel) | BinaryCodeReplyFlag`, `:1340`; no other code sets `BinaryCodeReplyFlag`). It has 15 call sites in 14 lines: `GCodes.cpp:3706`, `:4467`, `:4520`, `:4592`, `:4598`, `:6051`; `GCodes2.cpp:2292` (M122); `GCodes4.cpp:1894`, `:1906`; `GCodes6.cpp:140`; `StringParser.cpp:473`; `GPIO/GpInPort.cpp:90` (two); `SbcInterface.cpp:1568`, `:1571`. It MUST put `codeEpoch` into the free `MessageType` bits: bits 18–23 = epoch, bit 26 = valid (`RRF/Platform/MessageType.h`: destinations end at 0x3FFFF, `:39`; flags are bits 24, 25, 27–31, `:43-50`; bit 26 is unused). The type travels unchanged through the reply FIFO's 32-bit types (`RRF/Platform/OutputMemory.h:178-179`). `DataTransfer::WriteCodeReply` (`RRF/SBC/DataTransfer.cpp:1488-1503`) MUST move it into `resendPacketId` (CE-5) and clear bits 18–23 and 26 in the type it writes. Chunks of different epochs never merge, because the push merge compares the whole type (`SbcInterface.cpp:2369`). Two places compare a type for equality, both with `GenericMessage` (`GCodes2.cpp:2293`, `GCodes6.cpp:141`); an SBC reply type never equals it, with or without the bits; two compare with `NoDestinationMessage` = 0 (`RRF/Platform/RepRap.cpp:762`, `:2367`). Deferred M122 output keeps the type it was given (`RRF/Platform/RepRap.h:57`, `RepRap.cpp:762-766`), so it carries the epoch of the code that asked. Routing tests single bits only (`RRF/Platform/Platform.cpp:3254-3256`, `:3266-3273`, `:3314-3321`). This answers Q6: M122, `GpInPort` and the SD speed test get the tag too | CC-7 (RRF #46 `codeReplySurplus`, `GCodeBuffer.h:405`) in epoch mode | Removes orders only: a reply of an older epoch no longer completes a code of the new one |
| CE-12 | S | **Stack events and requests.** The stack events of CE-6 MUST carry `codeEpoch` of their GB at the time they are written; `FillBuffer` hands no code to a GB while a closal, an acknowledgement, an abort or a prompt is unsent (`RRF/SBC/SbcInterface.cpp:1892-1894`), so `codeEpoch` cannot change in between. A macro request (`:1513-1527` blocking, `:1597-1611` non-blocking) whose `codeEpoch` is valid and not the channel epoch MUST NOT be written; RRF clears it as failed (`MacroRequestSent`), as the receipt of CE-7 has already ended a blocking wait. This covers state machines that go on after an abort (OF-T10: G28 requests the next homing file, `RRF/GCodes/GCodes4.cpp:358-413`) and daemon.g (OF-T11, A5) | CC-5 (RRF #43), CC-4 (RRF #45) in epoch mode; A5 | Removes orders only: a macro request that fails is a v7 outcome (InvalidateChannel ends a wait with an error, V7-53) |

### 4.5 DSF side of the epoch

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-13 | S | **Epoch on the channel `Processor`.** DSF MUST keep the epoch on `Processor` and increment it where it writes InvalidateChannel (Spin step 3, `DCS/Link/Channel/Processor.cs:931-937`, only when the write succeeded). In an epoch connection it replaces `_allFilesAbortedSent`, `_allFilesAbortUnconfirmed` and `_allFilesAbortResent` (`:119-136`; on the v7 path they stay until E3) with their uses (`:883-905`, `:1285-1290`, `:1357-1362`, `AllFilesAbortResent`, the out parameter of `ILinkAdapter.ResendPacket` and `DCS/Link/LinkService.cs:620-626`). `_allFilesAborted` (`:117`) stays: it is the trigger to write InvalidateChannel. When the epoch changes or is reset, DSF MUST discard the channel's partial reply (`_lastPartialMessage`, `:1184-1189`), so that no chunk of an older epoch is prepended to the next reply (K11 for aborts). Codes after the abort go out in the next Spin; RRF holds them until the apply (CE-7) | CC-2 (#134), CC-3 (#158), CC-6 (#161) | Same per-channel order as with #161, where DSF held the codes; now RRF holds them. Removes E-16 (the channel's wait for the acceptance of InvalidateChannel) |
| CE-14 | S | **Filter of stale RRF packets.** At the top of `LinkService.ProcessPacket` (`DCS/Link/LinkService.cs:609`), DSF MUST drop a tagged code reply, ExecuteMacro, AbortFile, WaitForAcknowledgement, MacroFileClosed or MessageAcknowledged whose epoch is not the current epoch of its channel, and count it per channel and request type. The channel comes from the payload or the reply's destination bit (`DCS/Link/Channel/Manager.cs:107-121`). A dropped reply is not routed to the console. **DSF MUST NOT filter answers to its own requests** (EvaluationResult, VariableResult, Locked, ObjectModel; CE-6 leaves them untagged): DSF merges pending evaluations per (channel, expression) (`DCS/Link/LinkInterface.cs:144-153`), so a dropped answer would hang every later identical evaluation | CC-2, CC-3 | Removes orders only: the packets dropped are those the windows of #134 and #158 drop today, plus the replies that RRF #46 suppresses and those it misses (OF-T10) |
| CE-15 | S | **RRF's own abort of all files.** DSF MUST answer an AbortFile(all) of the current epoch with InvalidateChannel(e+1), by setting `_allFilesAborted` in `FilesAborted(true)` (`DCS/Link/Channel/Processor.cs:646-760`, called from `DCS/Link/LinkService.cs:866-889`). Replies of codes RRF still ran then carry e and are dropped (K18, G-20). RRF's apply of that InvalidateChannel aborts files that are already aborted; whether that is harmless is Q9. Whether RRF requests an abort after its own 3 s macro-wait timeout (old D10) is a logic decision, open (LA-8): it would abort the File channel's print | — (K18 has no catch code; RRF #46 does not cover it) | "InvalidateChannel after RRF's AbortFile(all)" is a v7 order (a DSF abort can cross RRF's abort-all). No wait: DSF does not wait for an answer |

### 4.6 Stop path and halt (link)

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-16 | L | **The stop overtakes retries.** Today the link thread checks the halt request only at the top of its loop (`DCS/Link/LinkService.cs:325-335`; `InvalidateCodes` and the write of the stop happen there in the same iteration, `:327-328`), so a stop waits until `PerformFullTransfer` (`:598`) returned, and its retry loop resends the same TX (`DCS/Link/Adapter/SPI.cs:323-440`). Measured worst case about 503 ms behind a long code transfer that noise keeps failing (OF "Stop path under SPI faults"). When a stop is pending, the next retry MUST carry the stop first instead of resending the old transfer unchanged; the content of the old transfer MUST follow unchanged and MUST NOT be dropped (a dropped GetObjectModel would stay "sent" for good, K16; a dropped file result would make RRF wait 3 s). The stop MUST reach RRF in a transfer that RRF processes: with SequenceGuard RRF answers a repeated sequence number with its last TX without processing DSF's data (`RRF/SBC/DataTransfer.cpp:924-945`), so a stop placed into such a repeat would be lost (Q24). No header change. Acceptance: M112 ≤ 100 ms in 1000 stops under injected noise (AC-4) | — | "RRF processes DSF packets after the stop in the same transfer" is a v7 order: in the iteration that writes the stop, DSF still writes resend copies and file results (`LinkService.cs:434-451`, `:613-627`), object model queries, key changes and messages (`:470-594`), which `InvalidateCodes` does not cancel (V7-94). The v7 contract says DSF writes nothing after the stop (V7-12, X-17); the code writes these non-channel packets. Codes after the stop get "stopped" as in v7 (V7-16) |
| CE-17 | L | **A halt is never lost (Tim, 2026-11-09).** Rejecting unusable data MUST never drop an emergency stop or the halt state of RRF. Facts: an emergency stop that RRF makes by itself sends no packet of its own: `GCodes::DoEmergencyStop` (`RRF/GCodes/GCodes.cpp:1065-1070`) calls `reprap.EmergencyStop()` and `Reset()` and then sends the generic message "Emergency Stop! Reset the controller to continue."; DSF learns of the halt only through the object model (`state.status` "halted", `RRF/Platform/RepRap.cpp:1983-1989`) and that message (V7-17, K20). DSF's own stop counts as done once the transfer that carried it completed and the RX of that transfer was processed (`DCS/Link/LinkService.cs:454-456`, `:599`; `DCS/Link/LinkInterface.cs:345-356`). Rules: **(a)** On SPI, corrupted data (CRC) is retried as a whole transfer and never processed in part: RRF processes only after both data responses were Success (`RRF/SBC/DataTransfer.cpp:1187-1215`), DSF only after `PerformFullTransfer` returned (`SPI.cs:360-373`, `:440`). On USB there is no transfer CRC and no retry (`RRF/SBC/DataTransfer.cpp:1303-1356`): a failed transfer ends the connection on RRF (`RRF/SBC/SbcInterface.cpp:270-288`) and DSF drops its RX (`DCS/Link/Adapter/USB.cs:363-404`). **(b)** A packet that passed the CRC but cannot be used is a protocol fault: it is rejected, counted and reported to the logic as an event (VF-1, VF-2). Today it ends DCS (`SPI.cs:855`, uncaught by `LinkService.cs:446-450`) or drops the rest of the transfer ("Read invalid packet", `:439-443`). Because the halt is a state, not an edge, DSF MUST then read the object model state again. A rejected or mismatched ObjectModel answer MUST NOT leave its query pending: today the key check discards it and leaves the query "sent" (`LinkService.cs:714-723`, `:470-478`), so no query goes out any more and a halt would never arrive (K16, VF-8). **(c)** DSF's own stop is complete only after a transfer that RRF processed. Over USB a failed transfer returns normally, so the halt of the transfer it dropped completes (`USB.cs:363-404` with `LinkService.cs:454-456`, K6, G-2): VF-9. **(d)** Acceptance AC-5 | — | Reading the state again adds a query, which v7 sends at any time; no new order. A halt request that stays pending is a v7 wait (E-21, 4.5 s) |

### 4.7 Peers, integrity, counters, RAM

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-18 | S | **Peers without ChannelEpoch.** A fork DSF with a stock RRF, and a stock DSF with a fork RRF, MUST run upstream v7. Nothing of the epoch is written unless ChannelEpoch is active: no tag, no `MessageType` bits on the wire (CE-11). The fork catch code CC-2 to CC-8 is deleted from the v7 path once the printers run the epoch RRF (E3, FD-11, old D2 option b). **Behaviour change** for such pairs: fork DSF + stock RRF gets back DF-T2 (#134 gone), DF-T11 resend (#161 gone, stock RRF refuses) and DF-T12 (#158 gone); DF-T1 and DF-T10 are stock RRF's anyway. Stock DSF + fork RRF gets back DF-T1 (RRF #43 gone), DF-T10 (RRF #46 gone) and DF-T12 (RRF #45 gone); DF-T2 is stock DSF's anyway; DF-T11 does not come back, because CE-7 stays in the fork RRF. DF-T3 does not come back: its fix #133 (CC-28) is no epoch catch code and stays | CC-2 … CC-8 from the v7 path (E3) | v7 behaviour, by definition |
| CE-19 | L | **Integrity (v2 R9), conditional on an EMC test.** Measure corrupted transfers per hour under IEC 61000-4-3 and IEC 61000-4-6, 1 h each, at the levels of EN IEC 61000-6-2, from the checksum counters (RRF `checksumErrors`, `RRF/SBC/DataTransfer.cpp:1167`, `:1221`). At ≤ 100 corrupted transfers per hour CRC32 alone meets R9: about 2⁻³² undetected per corrupted transfer gives ≤ 2.3 × 10⁻⁸ per hour, below 10⁻⁷. Above that, a second, independent per-transfer CRC trailer is added as a v2 link capability. It can sit after the last packet inside the data length: both read loops read only the number of packets the header names (RRF `RRF/SBC/SbcInterface.cpp:314`, `RRF/SBC/DataTransfer.h:233-236`; DSF `DCS/Link/LinkService.cs:434`, `DCS/Link/Adapter/SPI.cs:523`) and stop at the data length (`RRF/SBC/DataTransfer.cpp:618-621`, `SPI.cs:531-534`). Details Q26 | — | Link only |
| CE-20 | S | **Counters for the evidence gates.** Both sides MUST count per channel and request type: stale drops (CE-10, CE-14), out-of-order replies that are not stale (DSF, today only logged, `DCS/Link/Channel/Processor.cs:1165`), resends (DSF, today logged, `LinkService.cs:618`) and refusals (RRF, `packetAcknowledged` false, `RRF/SBC/SbcInterface.cpp:1354-1358`), including code refusals. M122 shows them on both sides. Counted, not alarmed (v2 R7) | — | Counting changes no order |
| CE-21 | L, S | **RAM.** The RRF part MUST use static fields only: no heap allocation, no side table. A tag per reply-FIFO entry (64 entries, `RRF/Platform/OutputMemory.h:16`) would be a side table against CLAUDE.md rule 3 and is not needed, because the FIFO's own type carries the tag (CE-11). New static RAM MUST stay below 1 KB, checked with the map file (section 6) | — | — |

### 4.8 Logical compatibility (PR9)

| ID | Layer | Requirement | Removes | PR9 |
|---|---|---|---|---|
| CE-22 | S | **Refinement.** An epoch connection MUST be a refinement of v7 for the logic of both sides: every order in which a logic layer sees its events MUST also be possible in v7 with the catch code of DSF 7f26d0ca and RRF 0dccb9efe. The capability MAY remove orders, namely those the catch code removes today (stale replies, requests, closals and acknowledgements across an abort, stale copies of codes and macro answers). It MUST NOT add any. Section 4.10 checks every V7 item | — | This requirement |
| CE-23 | S | **Wait-for graph.** The v7 graph is [sbc-link-v7-contract.md](sbc-link-v7-contract.md) section 15. The capability changes it so: E-16 (a channel waits for the acceptance of InvalidateChannel) is gone (CE-7, CE-13); the codes of the channel wait instead in RRF's `FillBuffer` for the apply, bounded as C-4 (the apply needs the GB mutex, which the main task holds at most through its 3 s waits, V7-40, V7-71); the one new wait is the hold at connect (CE-3), bounded by Q23. C-1 to C-3 stay as in v7: C-1 and C-2 are K1 and K2, whose reachability tests come in E0 (Q14); C-3 is a logic finding (K3, LA-15). C-4 to C-8 stay, bounded | — | No new cycle: the hold at connect waits for the link thread only, which holds no logic lock while it waits |

### 4.9 Message summary

| Dir | v7 request (`RRF/SBC/SbcMessageFormats.h:269-349`) | Tag | Who filters | v7 pairing it replaces in epoch mode |
|---|---|---|---|---|
| → RRF | 2 Code, 7 MacroCompleted, 18 MacroStarted | epoch of the channel when written; a copy keeps it | RRF drops stale (CE-10) | copies acting after an abort (A-4) |
| → RRF | 19 InvalidateChannel | new epoch e+1 | never refused (CE-7) | #161 (resend tracking) |
| → RRF | 10 Lock, 11 Unlock, 16 EvaluateExpression, 20 SetVariable, 21 DeleteLocalVariable, 29 SetLastCodeResult | epoch | counted, applied as v7 (Q25) | — |
| → RRF | 0 EmergencyStop, 1 Reset, 3 GetObjectModel, 5 SetPrintFileInfo, 6 PrintStopped, 17 Message, file results, 30 ObjectModelKeyChanged, 0x4D50, IAP | none | — | — |
| → DSF | 3 Message with `BinaryCodeReplyFlag` | `codeEpoch` via `MessageType` bits (CE-11) | DSF drops stale (CE-14) | RRF #46 suppression; position windows |
| → DSF | 4 ExecuteMacro, 5 AbortFile, 13 WaitForAcknowledgement, 14 MacroFileClosed, 15 MessageAcknowledged | `codeEpoch` (CE-9, CE-12) | DSF drops stale (CE-14) | #134, #158, RRF #43, RRF #45 |
| → DSF | 0 ResendPacket (field = packet id), 1 ObjectModel, 2 CodeBufferUpdate, other Messages, 7 PrintPaused, 9 Locked, 11 EvaluationResult, 12 DoCode, 16 VariableResult, file requests | none | never (CE-14) | — |

### 4.10 Alignment with the v7 contract

Every item of [sbc-link-v7-contract.md](sbc-link-v7-contract.md) gets exactly one status for an epoch connection:
- **identical**: the logic sees the v7 behaviour;
- **changed**: the capability changes it; the difference and the reason are given, and the last column says why the
  change only removes orders (PR9, CE-22);
- **removed**: the mechanism is dropped; the reason is given;
- **gap**: a v7 behaviour or defect that the capability does not address; the row names the separate v7 fix (`VF-n`),
  the logic-list item (`LA-n`) or the open question (`Qn`) that takes it.

Counts (draft 5): 49 identical, 25 changed, 3 removed, 24 gap. A script checked that every V7-1 … V7-101 has exactly
one row (a range such as "V7-74 … V7-79" counts for each item) and that every row that is not identical has a reason
and a filled PR9 column. Every change only removes orders. One change adds a bounded wait: the hold at connect
(V7-3, CE-3).

| V7 | Status | Draft 5 | Difference | Reason | Only removes orders (PR9) |
|---|---|---|---|---|---|
| V7-1 pipeline depth one | identical | — | the depth stays; the abort windows no longer depend on it (V7-55, V7-56) | — | — |
| V7-2 processing in the writer's order | identical | — | filters drop packets before processing; the order is unchanged | — | — |
| V7-3 DSF's TX order | changed | CE-3 | after every connect and reset the channel Spins wait until the ChannelEpoch state is known | the epochs must be in place before the first channel packet | yes: events are only delayed, as on a slow link; the hold is a bounded wait at connect only (Q23) |
| V7-4 RRF's TX order | identical | — | the tag rides in the header | — | — |
| V7-5 pacing | identical | — | — | — | — |
| V7-6 unknown and invalid packets | gap | VF-1; Q21 (K4) | DSF rejects and counts instead of ignoring or stopping; RRF's end of the read loop at ≥ 32 stays (K4) | the capability adds no packet type (CE-1, CE-5), so it does not meet X-18 | n/a here; VF-1 only removes a DCS stop |
| V7-7 packet ids, resend lookup | gap | VF-1 | the `ArgumentException` that ends DCS (`SPI.cs:855`) and NEW-1 on USB are fixed separately | not an epoch topic (FD-13) | n/a here; VF-1 only removes a DCS stop |
| V7-8 refusal | changed | CE-8 | a copy keeps the tag of the original | without it, a copy would look untagged and pass the filters | yes: same copies, same order |
| V7-9 SPI resend guard | identical | — | CC-26 stays | — | — |
| V7-10 a refused packet is overtaken | changed | CE-10 | a copy of a Code, MacroStarted or MacroCompleted that an abort overtook is dropped | in v7 such a copy runs or acts after the abort | yes: removes "a cancelled code runs after the abort"; NEW-3 and K27 stay (EG-3, Q21) |
| V7-11 why RRF refuses | changed | CE-7 | InvalidateChannel is never refused; the other refusals stay | DF-T11 (CC-6) | yes: removes the resend of InvalidateChannel; the receipt acts without the mutex as v7 already does (C-4) |
| V7-12 EmergencyStop | changed | CE-16 | a pending stop goes first into the transfer being retried, ahead of its unchanged content | stop path worst case about 503 ms (A-6) | yes: in v7 RRF already processes DSF packets after the stop in the same transfer (CE-16) |
| V7-13 halt completion | gap | VF-9; CE-17 (c) | over USB a failed transfer must not complete the halt | K6, G-2 | n/a here; VF-9 keeps the halt pending, a v7 wait (E-21) |
| V7-14 code buffer after a stop | gap | Q21 (K12) | — | K12 is not an epoch topic; reported per FD-13 | n/a here |
| V7-15 Reset | identical | — | — | — | — |
| V7-16 M112 is DSF's | identical | LA-14 | — | — | — |
| V7-17 RRF-local stops | gap | CE-17 (b); Q27; LA-7 | DSF learns the halt from the object model, also after rejected data; STOPPED is deferred | K20, X-5; Tim's halt rule | n/a here; reading the state again adds no order |
| V7-18 firmware update | identical | — | — | — | — |
| V7-19 when DSF writes a Code | changed | CE-13, CE-3 | the block by an unconfirmed abort is gone; codes after an abort go out at once and RRF holds them until the apply; no code before the ChannelEpoch state is known | CC-6 | yes: RRF runs them only after the apply, as v7 runs them only after the accepted abort |
| V7-20 code admission | gap | Q21 (K24) | — | K24 is not an epoch topic | n/a here |
| V7-21 flow control by estimate | identical | — | credit dropped (FD-1); CC-28 … CC-30 stay; EG-3 watches it | — | — |
| V7-22 read-ahead drop (B7) | identical | — | CC-21 stays (Q5) | — | — |
| V7-23 RRF holds codes while a stack event is unsent | changed | CE-7 | `FillBuffer` also holds the channel's codes while the apply of InvalidateChannel is pending | the apply needs the GB mutex | yes: the same hold v7 has through #161, on the other side |
| V7-24 per-channel FIFO | identical | — | — | — | — |
| V7-25 comment codes get no reply | identical | — | — | — | — |
| V7-26 one final reply per code | changed | CE-11, CE-14 | replies of an older epoch are dropped and counted; matching by position stays within an epoch | DF-T10, OF-T10 leftovers | yes: removes "a stale reply completes the next code" |
| V7-27 reply routing in DSF | identical | — | the `MessageType` bits of the tag never reach the wire | — | — |
| V7-28 one reply FIFO | changed | CE-11 | each entry's type carries the epoch of its code; entries of different epochs never merge; NEW-2 stays (Q2) | the tag must be taken when the reply is produced | yes: no order change; a merge across epochs is removed |
| V7-29 suppressed replies | changed | CE-11, CE-14 | `codeReplySurplus` is replaced by DSF's filter; the M99 suppression stays; replies outside `HandleReply` (M122, `GpInPort`, SD speed test) get the tag too | CC-7; OF-T10 writers outside `HandleReply` | yes: the same replies are dropped, plus those #46 misses |
| V7-30 partial replies survive invalidation | changed | CE-13 | the partial reply of a channel is discarded when its epoch changes or is reset; the emergency stop path keeps K11 | G-8 for aborts | yes: removes a stale prefix |
| V7-31 reply and stack event in both orders | identical | — | — | — | — |
| V7-32 drains | identical | — | — | — | — |
| V7-33 completion for clients | identical | — | — | — | — |
| V7-34 blocking macro request | changed | CE-12, CE-14 | the request carries the epoch; RRF does not write one of an older epoch; DSF drops one of another epoch | CC-2 (#134), CC-5 (RRF #43); K17 stays (EG-1) | yes: a failed macro request is a v7 outcome |
| V7-35 several macros of one code | identical | — | — | — | — |
| V7-36 system macros on an empty stack | gap | Q21 (K9) | — | K9 is not an epoch topic | n/a here |
| V7-37 macro that cannot be opened | identical | — | — | — | — |
| V7-38 macro start, implicit start | changed | CE-10; VF-3 | a stale Code or MacroStarted no longer resolves a macro wait; that a current Code resolves it before its refusal is VF-3 | DF-T11 family | yes |
| V7-39 daemon.g | changed | CE-9, CE-12 | the daemon request carries the epoch of the moment RRF raised it; one of an older epoch is not sent | A5 (OF-T11) | yes |
| V7-40 macro wait timeout | gap | LA-8 | whether RRF requests an abort after its timeout is logic, open; K23 stays | FD-8 | n/a here; if decided, "channel aborted after a macro request" is a v7 order |
| V7-41 MacroCompleted | changed | CE-10 | a stale copy is dropped before it acts; X-7 stays (Q15) | a copy of an older epoch belongs to a level that no longer exists | yes |
| V7-42 MacroFileClosed | changed | CE-12, CE-14 | tagged; DSF drops one of another epoch; replaces #158 and RRF #45; the old-session case K5 is VF-6 | CC-3, CC-4 | yes |
| V7-43 DSF's macro reader | identical | — | — | — | — |
| V7-44 M98 | identical | — | — | — | — |
| V7-45 single AbortFile, M99 | changed | CE-12, CE-14 | a single abort of an older epoch is dropped; the M99 catch code CC-9 … CC-12 stays | the epoch ties it to the level it was meant for | yes |
| V7-46 AbortFile all | changed | CE-15, CE-14 | DSF answers one of the current epoch with InvalidateChannel(e+1); one of another epoch is dropped | K18, G-20 | yes: "InvalidateChannel after RRF's abort-all" is a v7 order; no wait |
| V7-47 stack depth | identical | — | — | — | — |
| V7-48 WaitForAcknowledgement | changed | CE-12, CE-14 | one of another epoch is dropped; G-14 (K15) stays (Q21) | stale box of an aborted level | yes |
| V7-49 MessageAcknowledged | changed | CE-12, CE-14 | one of another epoch is dropped (a box at the base level after an abort, OF-T10) | in v7 it pops whatever level is current | yes |
| V7-50 cancel with abort | identical | — | — | — | — |
| V7-51 M291 blocks only lower levels | identical | — | — | — | — |
| V7-52 DSF side of an abort | changed | CE-13 | DSF increments the epoch when it writes InvalidateChannel; the payload is unchanged; G-21 (K19) stays (Q21) | CC-1's ambiguity becomes an identity; CC-1's code stays (`_allFilesAborted`, `FileAbortSent`) | yes |
| V7-53 RRF side of InvalidateChannel | changed | CE-7, CE-12 | receipt without the mutex and apply with it; no second purge; requests of the older epoch are not sent; DoCode stays untouched | CC-4, CC-5, CC-8 | yes: v7 already acts before the mutex |
| V7-54 refused InvalidateChannel | removed | CE-7 | — | never refused | yes |
| V7-55 abort window 1 | removed | CE-14 | — | CC-2 (#134): replaced by the filter | yes |
| V7-56 abort window 2 | removed | CE-7, CE-13 | — | CC-6 (#161): nothing to wait for | yes: removes E-16 |
| V7-57 Lock | gap | VF-5; LA-9 | Lock as a pending request; the Unlock for a Locked without request is logic | K14, G-13; not an epoch topic | n/a here; VF-5 keeps the Locked wait (E-15) |
| V7-58 Unlock | identical | — | — | — | — |
| V7-59 EvaluateExpression | identical | — | the answer is never filtered (CE-14) | — | — |
| V7-60 SetVariable, DeleteLocalVariable | gap | VF-4; Q21 (K27); Q25 | no assignment before a refusal is VF-4 | K21; K27 | n/a here |
| V7-61 variables only after a flush | gap | LA-16 | — | K25 is a logic finding | n/a here |
| V7-62 SetLastCodeResult | gap | LA-17; Q21 (K10) | — | K26 is a logic finding; K10 stays | n/a here |
| V7-63 object model | gap | VF-8; CE-17 (b) | a rejected or mismatched answer must not leave its query pending | K16, G-18; a halt would never arrive | n/a here; VF-8 removes a permanent block |
| V7-64 logic waits for the OM | identical | — | — | — | — |
| V7-65 SetPrintFileInfo | gap | Q14 (K1, K2); Q11 (G-19) | the waits under the job lock stay | completion on acceptance was dropped with v8 | n/a here |
| V7-66 ObjectModelKeyChanged | identical | — | OF-S3 stays a separate fix | — | — |
| V7-67 print end | gap | LA-12 | DSF writes InvalidateChannel on File and File2 before PrintStopped(abort) | logic (decided by Tim) | n/a here; LA-12 orders an abort before the job cancel, which an IPC abort can do in v7 |
| V7-68 PrintPaused | identical | LA-13 | — (FD-7) | — | — |
| V7-69 Message to RRF | identical | — | — | — | — |
| V7-70 DoCode | gap | LA-10 | untagged and never filtered, as in v7; the error result for a cancelled DoCode is logic | K13, G-12 | n/a here |
| V7-71 RRF's file I/O | gap | Q21 (K22, K9); EG-4 | — | request ids only on evidence (FD-1) | n/a here |
| V7-72 stack mirror by position | identical | — | identity only on evidence (FD-3, EG-1) | — | — |
| V7-73 pop on the base level is fatal | changed | CE-14; VF-6 | its abort source (T12) is filtered by the epoch; its reconnect source (K5) is VF-6 | CC-3 | yes |
| V7-74 … V7-79 level routing, start code, pop effects, flush, prioritized codes, job read-ahead | identical | — | — | DSF and RRF logic | — |
| V7-80 M606 S1 | gap | LA-15 | — | C-3 (K3) is a logic finding | n/a here |
| V7-81 triggers | gap | LA-19 | — | the safety review found that a door trigger can be lost while the link is down (section 11) | n/a here; logic and safety work |
| V7-82 no cross-channel order | identical | — | — | — | — |
| V7-83 the link thread takes logic locks | gap | Q14 (K1, K2); LA-15 (C-3) | — | completion on acceptance was dropped with v8 | n/a here |
| V7-84 completion never blocks the link thread | identical | — | — | — | — |
| V7-85 DSF waits without timeout | identical | — | the hold at connect is recorded at V7-3 | — | — |
| V7-86 … V7-88 RRF task waits | identical | — | the end of the InvalidateChannel refusal is recorded at V7-11 | — | — |
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
| VF-5 | S | **Lock as a pending request.** RRF accepts Lock (10; `LockMovementAndWaitForStandstill` in RRF, `LockAllMovementSystemsAndWaitForStandstill` in DSF) and keeps it pending per GB, tries again at every exchange under the GB mutex (`RRF/SBC/SbcInterface.cpp:606-634`) and sends Locked once it succeeds; DSF no longer polls by resend (`DCS/Link/LinkService.cs:615-619`, `SPI.cs:841-845`). An abort or a connection change cancels it and releases what it holds (`UnlockAll`, `RRF/GCodes/GCodes.cpp:5648-5668`) | K14, G-13; CC-31 | both |
| VF-6 | S | **No stale per-GB flags after a reconnect.** `InvalidateResources` clears `macroFileClosed`, `messagePromptPending`, `invalidated`, `invalidationPending`, `codeReplySurplus`, an unsent `sendToSbc` and `reportPause`, and does not set `messageAcknowledged` itself (`RRF/SBC/SbcInterface.cpp:1717-1780`, `:1772`) | K5, X-12 | RRF |
| VF-7 | L | **No TX of the old session in the new one.** After a lost connection DSF exchanges the pending TX again (`DCS/Link/Adapter/SPI.cs:442-477`), and RRF processes it if its number follows (`RRF/SBC/DataTransfer.cpp:1396-1443`) | K7, G-3 | both |
| VF-8 | S | **A mismatched ObjectModel answer does not block the queries.** The key check discards it but leaves the query "sent" (`DCS/Link/LinkService.cs:714-723`, `:470-478`); the query MUST be sent again or failed, so updates and the halt state keep coming (CE-17 (b)) | K16, G-18 | DSF |
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
| Apply pending: `invalidationPending` (exists, reused) | 0 | CE-7 |
| Capability bit in `offeredLinkCapabilities` / `activeLinkCapabilities` (exist) | 0 | CE-1 |
| Reply tag in the FIFO's `MessageType` (exists, `RRF/Platform/OutputMemory.h:179`) | 0 | CE-11 |
| **Epoch state** | **30** (the reviews estimated about 31) | |
| Removed in E3: `codeReplySurplus` (bool × 15, `GCodeBuffer.h:405`) | −15 | CE-7, CE-18 |
| Counters: stale drops per channel (u16 × 15) | 30 | CE-20 |
| Counters: refusals per request type (u16 × 32) | 64 | CE-20 |
| **Total after E3** | **≈ 109** | 0.11 % of 95,868; CE-21 |

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
| E0 preparation | Fake firmware epoch mode (`src/UnitTests/FakeFirmware/FakeFirmware.cs`); the counters of CE-20 on both sides, also in v7 mode for a baseline; K1/K2 reachability tests (Q14); tests of Q9 and Q15 | none | existing suites green; the E0 tests written |
| E1 | CE-7: RRF never refuses InvalidateChannel (a v7 fix, works with stock DSF) | CC-6 (#161) where RRF has E1; CC-8's first-copy role | lab `AbortsCrossingMacroRequests` 2000 aborts: 0 "Resending packet" for InvalidateChannel, 0 blocks |
| E2 | ChannelEpoch: CE-1 … CE-6, CE-8 … CE-15, CE-16, CE-20 | in epoch mode CC-2, CC-3, CC-4, CC-5, CC-7 (with CC-6, CC-8 from E1): 7 of the 17 fork-only items. CC-1 is upstream design: its ambiguity is resolved, its code stays (`_allFilesAborted`, `FileAbortSent`, CE-7) | lab 2000 aborts with 0 out-of-order replies, 0 resends, 0 blocks; the fake-firmware abort tests green with CC-2 … CC-8 switched off in epoch mode (AC-1, AC-2); then shown to Christian Hammacher (FD-14) |
| E3 | Delete CC-2 … CC-8 from the v7 path of both sides once the printers run the epoch RRF (CE-18); delete `codeReplySurplus` | CC-2 … CC-8 | the coexistence cases of 7.1 behave as stated (AC-7) |
| then | Evidence gates (7.3) | — | — |

The decision text said "CC-1 … CC-8 deleted". The code says otherwise for CC-1: without `FileAbortSent`
(`RRF/SBC/SbcInterface.cpp:903`) RRF would answer DSF's own abort with AbortFile(all), which DSF answers with another
InvalidateChannel (CE-15), a loop. So CC-1 stays. The separate v7 fixes (section 5) are independent of the milestones.

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

## 8. Verification and acceptance

- **Unit tests** in fork-only files under `src/UnitTests`: the fake firmware gets an epoch mode that implements the
  RRF side of section 4. Every requirement gets a test. Every existing test of a deleted catch code keeps running in
  epoch mode; those tests are the regression set.
- **RRF host harness** (`firmware/RepRapFirmware/Tools/sbc-harness`): the `PortMatchesRealFirmware`-style replay
  (`src/UnitTests/SPI/LinkRecovery.cs`) covers CE-16 with SequenceGuard repeats.
- **Fuzzing** (`src/UnitTests/Fuzz`): `TriggerFuzz` and `StopPathFuzz` with aborts, refusals and RRF restarts.
  Invariants: every code completes exactly once; no reply reaches another code; no channel stays blocked; no DCS stop.
- **Lab** (`src/LabPrinterTests`, procedure LT): the run set of 7.3, M112 under load, the EMC test of CE-19.

| Acceptance | Pass when |
|---|---|
| AC-1 | 3 × 2000 aborts in epoch mode: 0 blocks, 0 "Out-of-order reply", 0 "Resending packet", stale drops only in the counters |
| AC-2 | The fake-firmware abort tests (`TriggerFuzz` abort cases, `TriggerMacros.TriggerChannelWorksAfterInvalidationDuringTriggerRequest`, the DF-T1, T2, T10, T11, T12 tests) green in epoch mode with CC-2 … CC-8 switched off |
| AC-3 | No code is completed by a message of another code in any fuzz run (identity check in the fake firmware) |
| AC-4 | M112 ≤ 100 ms in 1000 stops under injected noise, on the plain and the extended link; no stop lost, also when a retry is a SequenceGuard repeat (CE-16) |
| AC-5 | Halt (CE-17): malformed packets and corrupted transfers injected next to an RRF-local emergency stop (an M112 trigger in RRF) and next to ObjectModel answers that carry status halted: DSF reports halted within a bounded time, never misses it, and DCS does not exit. A dropped transfer during DSF's M112: the stop is not reported done until RRF processed it |
| AC-6 | New static RRF RAM ≤ 1 KB by map file; no heap allocation and no side table (CE-21) |
| AC-7 | The coexistence cases of 7.1 behave as stated, before and after E3; an epoch connection over the USB adapter and on a board other than SAME70 in the fake firmware |
| AC-8 | Refinement (CE-22): the same scripted input in v7 mode and epoch mode gives per channel the same sequence of logic events, apart from the events that the v7 catch code drops |
| AC-9 | No deadlock: in every fuzz and lab run each code ends (reply, cancel or error); a watchdog in the fake firmware host flags any channel that has work but makes no progress |
| AC-10 | The hold at connect (CE-3) ends within its bound in every connect and reset case of the census, and a macro request at connect is answered before RRF's 3 s timeout |
| AC-11 | EMC test of CE-19: corrupted transfers per hour measured; ≤ 100 per hour, or the trailer of CE-19 is specified |

## 9. Unchanged decisions and boundaries

- Matching by position stays within an epoch (FD-3); pause stays v7 (FD-7, LA-13).
- Logic defects stay on the logic list: K3 (C-3, LA-15), K25 (LA-16), K26 (LA-17), the Q13 cases (LA-18).
- B7 stays as it is (Q5).

## 10. Decisions

### 10.1 History

- Drafts 1 to 3 designed v8 as a capability `SessionLayer` on v7. Draft 4 recorded an interview with Tim
  (decisions D1 to D14 of draft 4): its own protocol version, the v2 link features always on, streams, credit,
  RESET_ACK, STOP_ACK, request ids, session resume, a per-packet check.
- Three expert reviews of draft 4 followed on 2026-11-09 (section 11).
- Tim then decided the minimal design below (FD-1 to FD-14).

### 10.2 Final decisions (Tim, 2026-11-09, after the reviews)

| # | Decision | Applied in |
|---|---|---|
| FD-1 | **Direction.** No v8: no own version number, no new header, no streams, stall or go-back-N, no RESET_ACK, no STOP_ACK, no credit, no request ids, no session resume, no code identity for now. Instead a channel epoch on v7 plus evidence gates | sections 1, 4, 7.3 |
| FD-2 | **Carrier and switch-on.** A link capability `ChannelEpoch` = 8, offered in `limits.sbcLinkCapabilities`, activated with 0x4D50. RRF offers it outside the SAME70-only block, on every board and on USB; DSF holds channel traffic until the capability state is known, a bounded wait at connect | CE-1 … CE-3 |
| FD-3 | **The tag.** `resendPacketId` carries the epoch in both directions; payloads do not grow. Code identity only on evidence | CE-5, CE-6, EG-1 |
| FD-4 | **Companion 1.** RRF never refuses InvalidateChannel: receipt without the mutex, apply with it; `FillBuffer` holds the channel's codes until the apply; reuse `invalidationPending`, delete `codeReplySurplus`. A plain v7 fix | CE-7; deletion of `codeReplySurplus` in E3 (section 7.2) |
| FD-5 | **Companion 2.** DSF's resent copies keep their tag | CE-8 |
| FD-6 | **RRF side.** Stamp the epoch on accepted codes, carry it to `codeEpoch` in `FillBuffer`, filter stale Code, MacroStarted, MacroCompleted before their side effects; reply tag through `GetResponseMessageType` and free `MessageType` bits into `resendPacketId`; stack events carry `codeEpoch`; stale requests fail locally; `InvalidateResources` marks `codeEpoch` invalid | CE-9 … CE-12 (with the correction of Q22) |
| FD-7 | **DSF side.** The epoch on the channel `Processor`, replacing the three abort flags; one filter at the top of `ProcessPacket`; answers to DSF requests are never filtered | CE-13, CE-14 (reset point corrected, CE-4) |
| FD-8 | **RRF's own abort.** DSF answers AbortFile(all) with InvalidateChannel(e+1). Whether RRF requests an abort after its 3 s macro-wait timeout is logic, open | CE-15, LA-8 |
| FD-9 | **Stop overtakes retries.** A pending stop goes first into the next retry; M112 ≤ 100 ms in 1000 stops under noise; no header change | CE-16, AC-4 |
| FD-10 | **No session resume.** A link loss ends the session as today; the retry limit of 10 stays; time-based retries are not kept | sections 1, 5 |
| FD-11 | **Peers without ChannelEpoch** run upstream v7; the fork catch code is deleted from the v7 path once the printers run the epoch RRF. The 2026-10-08 decision "negotiation on top of v7" (v2 C1) holds | CE-18, E3, v2 document |
| FD-12 | **Integrity (v2 R9)** conditional on an EMC test: ≤ 100 corrupted transfers per hour, else a second per-transfer CRC trailer | CE-19, AC-11 |
| FD-13 | **Separate v7 work**, each its own PR with unit tests: DCS never exits on link data (incl. NEW-1), link events and health state, and the bug fixes act-before-refuse, Lock as a pending request, stale flags after a reconnect, the old session's TX; added on 2026-11-09: the halt rule with K16 and K6. Not kept: time-based retries, a systemd watchdog (open item LA-20). Safety-relevant K findings (K1–K3, K6, K12) go to Christian Hammacher once a test proves each and shows it is not fork code | section 5, CE-17 |
| FD-14 | **Unchanged from earlier decisions.** Epoch only, identity on evidence (old D3); pause stays v7 (old D7); shown to Christian Hammacher after the epoch milestone passed the lab (old D8, E2); Q12: watch H1 and H2 afterwards; logic defects stay on the logic list; K1/K2 reachability tests in E0. STOPPED from RRF (old D9) and STOP_ACK (old D5): deferred; STOPPED only as a capability of its own once a test shows the RRF-local-stop problem (K20, X-5) — **deferred, needs Tim's confirmation** (Q27) | sections 7, 9, 12 |

Draft 4's decisions: D1 and D2 reversed (FD-1, FD-11); D3 kept (FD-14); D4 dropped with credit; D5 dropped (no
expedited flag; CE-16 needs no header); D6 superseded (ChannelEpoch on every board and on USB; NEW-1 in VF-1); D7
kept; D8 kept for E2; D9 deferred (Q27); D10 now logic, open (LA-8); D11 kept for K3, K25, K26, while K1 and K2 are
open again (Q14), because V8-34 was dropped; D12 kept, but PR10 dropped; D13 kept (FD-13); D14 dropped: the v2 link
features stay capabilities.

### 10.3 What happened to the v8 requirements of drafts 1 to 4

| v8 requirement | Fate in draft 5 |
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
| V8-12 RRF's abort as a RESET request | Replaced by CE-15 (DSF answers AbortFile(all) with InvalidateChannel); a RESET request only on evidence (EG-5) |
| V8-13 request ids for global requests | Dropped; on evidence (EG-4) |
| V8-39 request numbers for RRF requests | Dropped; the timeout reaction is logic (LA-8, open) |
| V8-14 code identity | Dropped for now (FD-3); on evidence (EG-1) |
| V8-15 … V8-17 absolute credit, storable capacity, per-channel limits | Dropped; on evidence (EG-3) |
| V8-18 no silent reply loss | Dropped; NEW-2 stays open (Q2) |
| V8-19 expedited class | Dropped; CE-16 keeps the stop ahead of retries |
| V8-20 STOP_ACK | Dropped; CE-17 (c) |
| V8-21 no ResendPacket | Dropped; ResendPacket stays |
| V8-22 macro M112 flush | Already logic in draft 4 (LA-14) |
| V8-38 STOPPED | Deferred, needs Tim's confirmation (Q27); CE-17 (b) makes sure DSF sees the halt |
| V8-40 v2 link features always on | Dropped; they stay capabilities (v2 C1) |
| V8-41 own protocol version | Dropped (FD-1) |
| V8-42 per-packet integrity check | Replaced by CE-19 (v2 R9 conditional on an EMC test) |
| V8-23 no rule on transfer numbers | Dropped as a requirement; the epoch removes the abort windows' dependence on them |
| V8-24 a session change clears state | Split: VF-6 (K5), VF-7 (K7); epochs CE-4 |
| V8-35 unknown and invalid packets | VF-1 (DSF); RRF's K4 open (Q21) |
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
| Q14 | Can C-1, C-2 and C-3 be reached (K1–K3)? | Decided: fake-firmware tests in E0. K1 and K2 have no fix in draft 5 |
| Q15 | Can a MacroCompleted cross an RRF-side pop (X-7)? | Decided: a fake-firmware test in E0 |
| Q16 | Can the global stream stall before SetPrintFileInfo? | Closed: no streams |
| Q17 | Version handshake with a v7 DSF | Closed: no own version |
| Q18 | Third transfer buffer missing on a v8 RRF | Closed: as today, SequenceGuard is offered only with the buffer (`RRF/SBC/DataTransfer.cpp:478`) |
| Q19 | PR10 details (lifetime, session id, LinkDown window) | Closed: no resume. The LinkDown window moves to Q29 |
| Q20 | Per-packet check details | Closed: replaced by CE-19; trailer details Q26 |
| Q21 | **New.** v7 defects that neither the capability nor section 5 fixes: K4 (RRF side), K9, K10, K11 (stop path), K12, K15, K17, K19, K22, K24, K27, NEW-2. Fix each or not? K12 is safety-relevant and is reported per FD-13 once a test proves it | Open, for Tim |
| Q22 | **New. Tag of work that RRF started itself.** FD-6 says stack events carry `codeEpoch` from `FillBuffer`. The code shows two cases where the GB's last ring code is not the work the event belongs to: a macro request without a code (trigger, daemon.g) after an abort of its channel, which DSF would drop (trigger lost after RRF's 3 s timeout), and the closal of a system macro without codes, which DSF would drop (its level waits for good, E-17). CE-9 sets `codeEpoch` to the channel epoch when RRF raises such a request or takes a text code | Open: confirm CE-9 |
| Q23 | **New.** The bound of the hold at connect (CE-3), in transfers, and that the connection then runs without ChannelEpoch. Illustration: 16 transfers, the bound DSF uses for the extended switch (V7-101); at the idle pacing of 25 ms (`RRF/SBC/SbcMessageFormats.h:37`) that is 0.4 s, below RRF's 3 s macro wait. The capability query waits behind other object model queries (one per transfer, `DCS/Link/LinkService.cs:470-478`) | Open |
| Q24 | **New.** How the stop of CE-16 reaches RRF when the retry is a SequenceGuard repeat that RRF answers without processing (`RRF/SBC/DataTransfer.cpp:924-945`) | Open, design in E2, checked by the census replay (AC-4) |
| Q25 | **New.** Should RRF also drop stale copies of DeleteLocalVariable, SetLastCodeResult, Unlock and Lock (CE-10)? EvaluateExpression and SetVariable must be answered in any case | Open; the counters of CE-20 show whether stale copies occur |
| Q26 | **New.** Algorithm and placement of the per-transfer CRC trailer, if the EMC test exceeds 100 corrupted transfers per hour (CE-19) | Open until the EMC test |
| Q27 | **New.** STOPPED from RRF (old D9) as a capability of its own once a test shows the RRF-local-stop problem (K20, X-5); STOP_ACK (old D5) likewise deferred | Deferred, needs Tim's confirmation |
| Q28 | **New.** The epoch has 6 bits, not 8 as the example of FD-3: the reply path has only bits 18–23 and 26 free in `MessageType` (`RRF/Platform/MessageType.h:16-50`). A stale packet matches a newer epoch only after 64 aborts of its channel while it is in flight | Open: confirm |
| Q29 | **New.** Thresholds of VF-2 (LinkDown window, rate for "degraded") and how PeerRestarted is told from a connection reset without a restart counter (`ExtendedLink.cs:65`) | Open |

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
