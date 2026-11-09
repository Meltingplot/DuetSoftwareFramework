# SBC link: logic adaptations

Status: 2026-11-09, updated for draft 5 of [sbc-link-v8-requirements.md](sbc-link-v8-requirements.md), which
replaces the v8 session layer by a channel epoch on v7. Nothing here is agreed beyond the items marked "decided", and
nothing is implemented.

Tim's layering rule (2026-11-09):
> We must not mix levels: motion is not link, and safety is neither link nor protocol. If the link is down it must
> signal event-based to the logic; the logic decides.

And: "no special cases in the link — if it is not defined, it does not exist."

So the requirements hold only the link (L) and the session layer (S): the events they report and the guarantees they
give. This list holds the rest: what the logic (G) of DSF or RRF does on such an event, the defects of the logic that
the link work found, and the findings of the safety review that are not link topics. The requirements link here and
contain no logic requirement.

Columns:
- **Event**: the link event or guarantee the logic reacts to, with the requirement that defines it (`CE-n`, `VF-n`
  of the requirements); "—" for a defect of the logic;
- **Side**: DSF, RRF or both;
- **Reaction**: what the logic does;
- **v7**: what the logic does today;
- **Source**: the decision or finding the item comes from (`FD-n` = final decision of draft 5);
- **Status**: "decided" (by Tim, or agreed in v2), "open" or "deferred".

Basis and paths as in the requirements (section 0): DSF `v3.7-dev` at 7f26d0ca, RRF fork at 0dccb9efe; `DCS/` =
`src/DuetControlServer/`, `RRF/` = RRF `src/`. `V7-n`, `E-n`, `C-n` are items of
[sbc-link-v7-contract.md](sbc-link-v7-contract.md), `K-n` of [open-findings.md](open-findings.md), `v2 Rn` of
[sbc-link-requirements.md](sbc-link-requirements.md).

## 1. Reactions to link events

Draft 5 has no session resume (FD-10): a link loss ends the session on both sides as today. The events of VF-2
(LinkDown, LinkRestored, ConnectionLost, PeerRestarted, health state) report what happens; the reactions below are
today's, kept.

| ID | Event | Side | Reaction | v7 | Source | Status |
|---|---|---|---|---|---|---|
| LA-1 | Connection lost (VF-2) | RRF | Stop of category 0: emergency stop and `InvalidateResources` (files aborted, heaters off), as today | RRF does this on every lost connection, after 4 s without a transfer or on a connection reset (`RRF/SBC/SbcInterface.cpp:171-176`, `:194-254`; stop `:229-230`, fork RRF #21; `InvalidateResources` `:1717-1780`) | v2 R8; FD-10 | decided (v7 kept) |
| LA-2 | Connection lost or reset (VF-2) | DSF | Discard pending work (`Invalidate`, `DCS/Link/LinkService.cs:1605-1622`) and abort the job, as today, because RRF has dropped its state (LA-1) | `DCS/Link/Adapter/SPI.cs:383-390`, `DCS/Link/LinkService.cs:389-396`, job `:1504-1508` | FD-10, V7-97 | decided (v7 kept) |
| LA-3 | PeerRestarted (VF-2) | both | As v2 R6: DCS discards pending work, RRF performs a stop of category 0, as today. Telling a restart from a connection reset needs more than the extended-link status (LA-21) | The same, after every timeout or sequence gap (V7-92, V7-95) | v2 R6 | decided (v7 kept) |
| LA-6 | Link health state and counters (VF-2) | DSF (DWC, M122) | DWC shows "link degraded" when repairs exceed a set rate, and during an outage stale values with the time left until the stop (v2 R7, R11); M122 shows the counters, including those of the evidence gates (CE-20) | Not implemented (v2 implementation status) | v2 R7, R11 | decided |

## 2. Reactions to stops, aborts and requests

| ID | Event | Side | Reaction | v7 | Source | Status |
|---|---|---|---|---|---|---|
| LA-7 | RRF halted by itself, seen as `state.status` "halted" in the object model (CE-17); STOPPED from RRF is deferred (Q27) | DSF | Like the completion of its own halt: `InvalidateCodes` (`DCS/Link/LinkService.cs:1499-1518`) and `state.status` halted | DSF keeps its macro levels and codes while RRF has popped its own and answers the codes left in its ring with "stopped" (V7-17, X-5, K20) | old D9, deferred by FD-14 | deferred, needs Tim's confirmation |
| LA-8 | RRF's macro wait expired after the request went out (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1263-1274`) | RRF | Open: request an abort of the channel (DSF then answers AbortFile(all) with InvalidateChannel(e+1), CE-15), so that DSF drops the level it may have pushed. Behaviour change: the channel's whole stack is aborted instead of the caller going on after "not found"; on the File channel this aborts the print | Goes on as "not found"; a late MacroStarted gives "none was requested", a late Code runs at the caller's level, a late MacroCompleted(error) pops the current level (V7-40, K23) | FD-8 (was decided as D10 in draft 4) | open |
| LA-9 | Locked for a lock request that DSF no longer has (VF-5) | DSF | Answer with Unlock | Logs an error, sends no Unlock, and RRF keeps the movement lock (`DCS/Link/Channel/Processor.cs:838-849`; G-13, K14) | VF-5 | open |
| LA-10 | A DoCode that DSF cancels | DSF | Answer with an error result, so that RRF's GB does not stay "executing on SBC" | Sends nothing; RRF's GB stays "executing on SBC" without a bound (`DCS/Codes/Pipelines/Executed.cs:170-230`; G-12, K13) | K13 | open |
| LA-12 | Job abort. Guarantee: InvalidateChannel(e+1) makes replies and stack events of the older epoch stale on both sides (CE-10, CE-14) | DSF | Write InvalidateChannel on File and File2 before PrintStopped(abort) | PrintStopped(abort) alone (`DCS/Files/JobProcessor.cs:763-767`); RRF purges the ring codes of File and File2 on read, without an abort of the channels (`RRF/SBC/SbcInterface.cpp:509-517`). PrintStopped(normal) needs both mutexes (`:521-539`) | Tim, 2026-11-09 (draft 4) | decided |
| LA-13 | PrintPaused (catalogue A10) | both | Keep the v7 mechanism; after E3 decide with test data whether PrintPaused becomes an abort of File and File2. The pause position logic is not analysed | v7 (V7-68) | FD-14 (old D7) | open |

## 3. Unchanged logic that the link work relies on

| ID | Event | Side | Reaction | v7 | Source | Status |
|---|---|---|---|---|---|---|
| LA-14 | M112 read from a macro or from a source that is not prioritized | DSF | Keeps its flush (`DCS/Codes/Handlers/MCodeHandler.cs:750`). The wiki tells users to put M81 before M112 in a trigger macro (`wiki-content/User_manual/Reference/Gcodes.md:2682`), so order inside a macro matters. The fast path for door and E-stop logic is the RRF-local trigger action ([rrf-local-trigger-actions.md](rrf-local-trigger-actions.md) TA1, RRF #25). The flush does not wait for room taken by other channels, because #133 (catalogue CC-28) stays | The same (V7-16) | former V8-22 | decided (v7 kept, PR9) |

## 4. Logic defects found by the link work

These are findings in the logic, outside the link work (Tim, 2026-11-09). They are no precondition for any milestone.
Each needs a fix the way CLAUDE.md describes, with a unit test that shows the fault first.

| ID | Event | Side | Reaction (fix) | v7 | Source | Status |
|---|---|---|---|---|---|---|
| LA-15 | — | DSF | C-3: M606 S1 takes the job lock before the channel locks, against the order "channel before job" (`DCS/Files/JobProcessor.cs:821-822`); fix the lock order | K3 (`DCS/Codes/Handlers/MCodeHandler.cs:1133-1135`, `DCS/Files/JobProcessor.cs:397`, `DCS/Link/LinkInterface.cs:315-324`) | K3 | open |
| LA-16 | — | DSF | M260 and M261 with V delete a stale variable without a flush; order the deletion after the earlier codes of the level, as `var` does (`DCS/Codes/Handlers/KeywordHandler.cs:244-248`) | K25 (`DCS/Codes/Handlers/MCodeHandler.cs:798-805`) | K25 | open |
| LA-17 | — | DSF | The result of a code that DSF handles itself is sent as SetLastCodeResult without a flush, so an earlier RRF code can overwrite it | K26 (`DCS/Commands/Generic/Code.cs:266-292`, `DCS/Codes/Pipelines/Pre.cs:30-35`) | K26 | open |
| LA-18 | — | DSF | Other DSF-internal codes that complete without a flush while an earlier RRF code of their level runs and so write a request that RRF refuses during its macro wait: not listed yet; missing flushes are logic | V7-33 | Q13 | open |

## 5. Findings of the safety review (2026-11-09) that are not link topics

| ID | Event | Side | Reaction | v7 | Source | Status |
|---|---|---|---|---|---|---|
| LA-19 | A door or E-stop input fires while the link is down or slow (v2 SB2, interim state) | RRF | Open, for the logic and safety work of v2 SB1 ([rrf-local-trigger-actions.md](rrf-local-trigger-actions.md)): such a trigger must not be lost | The trigger can be lost: RRF clears the trigger bit (`RRF/GCodes/GCodes.cpp:1016`) before it requests `triggerN.g` (`:1052`); with no SBC connected the request fails at once (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1246-1250`), otherwise after 3 s (`:1263-1274`); triggers react to edges only (`RRF/GCodes/TriggerItem.cpp:52-75`), so the input does not fire again; `CheckTriggers` runs in the main loop (`GCodes.cpp:517`), which does not run while a spin (`:590`) waits for a macro from the SBC (up to 3 s, V7-40). Only a trigger whose action is M112 alone fires as trigger 0 and stops at once (`:987`, `:997-998`) | safety review | open |
| LA-20 | DCS hangs without exiting | SBC | Open, not decided: a systemd watchdog for DCS | There is none: the unit has `Type=notify` and `Restart=always`, but no `WatchdogSec` (`pkg/common/duetcontrolserver/usr/lib/systemd/system/duetcontrolserver.service`). RRF notices a silent DCS only by its 4 s timeout (LA-1) | safety review; FD-13 (not kept as a requirement) | open |
| LA-21 | A restart of DCS or RRF versus a connection reset | both | The logic cannot tell them apart from the extended-link status: it is the constant 0x7FF while the extended link is active and 0 after leaving it (`DCS/Link/Protocol/Shared/ExtendedLink.cs:62-65`), not a restart counter. Any reaction that depends on the difference needs another source (requirements Q29) | Both are handled alike (LA-2, LA-3) | safety review | open |

## 6. Items of the draft-4 list that were removed

| ID | Was | Why removed |
|---|---|---|
| LA-4 | Reaction to LinkDown and LinkRestored within the session lifetime | Only existed for the session resume (FD-10). Its v2 SB2 point is LA-19 |
| LA-5 | Logic waits that expire while the link is down within the session lifetime | Only existed for the session resume (FD-10). The macro wait timeout is LA-8 |
| LA-11 | No STOP_ACK within 4.5 s of an M112 | STOP_ACK was dropped (FD-1); M112 keeps its v7 4.5 s bound (E-21), and CE-17 (c) keeps a halt pending until a transfer that RRF processed |
