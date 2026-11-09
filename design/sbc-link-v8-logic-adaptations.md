# SBC link v8: logic adaptations

Status: 2026-11-09, started with draft 4 of [sbc-link-v8-requirements.md](sbc-link-v8-requirements.md). Nothing here is
agreed beyond the items marked "decided", and nothing is implemented.

Tim's layering rule (2026-11-09):
> We must not mix levels: motion is not link, and safety is neither link nor protocol. If the link is down it must
> signal event-based to the logic; the logic decides.

And: "no special cases in the link — if it is not defined, it does not exist."

So the v8 requirements hold only the link (L) and the session layer (S): the events they report and the guarantees they
give. This list holds the rest: what the logic (G) of DSF or RRF does on such an event, and the defects of the logic
that the v8 work found. The v8 document links here and contains no logic requirement.

Columns:
- **Event**: the protocol event or guarantee the logic reacts to, with the v8 requirement that defines it; "—" for a
  defect of the logic;
- **Side**: DSF, RRF or both;
- **Reaction**: what the logic does;
- **v7**: what the logic does today;
- **Source**: the decision or finding the item comes from;
- **Status**: "decided" (by Tim, or agreed in v2) or "open".

Basis and paths as in the v8 requirements (section 0): DSF `v3.7-dev` at 7f26d0ca, RRF fork at 0dccb9efe; `DCS/` =
`src/DuetControlServer/`, `RRF/` = RRF `src/`. `V7-n`, `E-n`, `C-n` are items of
[sbc-link-v7-contract.md](sbc-link-v7-contract.md), `K-n` of [open-findings.md](open-findings.md), `v2 Rn` of
[sbc-link-requirements.md](sbc-link-requirements.md).

## 1. Reactions to link and session events (PR10)

| ID | Event | Side | Reaction | v7 | Source | Status |
|---|---|---|---|---|---|---|
| LA-1 | SessionExpired (V8-45, V8-46) | RRF | Stop of category 0: emergency stop and `InvalidateResources` (files aborted, heaters off) | RRF does this on every lost connection, after 4 s without a transfer or on a connection reset (`RRF/SBC/SbcInterface.cpp:171-176`, `:194-254`; stop `:229-230`, fork RRF #21; `InvalidateResources` `:1717-1780`) | v2 R8 | decided |
| LA-2 | SessionExpired (V8-45, V8-46) | DSF | Discard pending work as after a reset today (`Invalidate`, `DCS/Link/LinkService.cs:1605-1622`), because RRF has dropped its state (LA-1) | DSF invalidates and aborts the job after every reconnect (`DCS/Link/Adapter/SPI.cs:383-390`, `DCS/Link/LinkService.cs:388-396`, `:1504-1508`) and stops DCS after 60 s without a complete transfer (`SPI.cs:326-331`) | follows from LA-1 and V7-97; not decided by Tim | open |
| LA-3 | PeerRestarted (V8-45, V8-46) | both | As v2 R6: DCS discards pending work (`Invalidate`), RRF performs a stop of category 0 (as LA-1) | The same, but also after every timeout or sequence gap (V7-92, V7-95) | v2 R6 | decided |
| LA-4 | LinkDown and LinkRestored within the session lifetime (V8-46) | both | None that the protocol requires: the session keeps its state (V8-45). What the logic does while the link is down, e.g. hold new motion (v2 R13 stage 1) or the door reaction that the interim state still runs over the link (v2 SB2), belongs to the logic and safety work of v2 SB1 ([rrf-local-trigger-actions.md](rrf-local-trigger-actions.md)), not to v8 | RRF: after 4 s the reaction of LA-1; DSF: retries, then a reconnect with `Invalidate` and job abort (LA-2) | PR10; Tim: "motion is not link, and safety is neither link nor protocol" | open |
| LA-5 | LinkDown longer than RRF's own waits for DSF | RRF | Open. Within the session lifetime the protocol keeps a request pending (V8-45), but the logic's waits still expire: the macro wait after 3 s (E-37), file requests after 3 + 3 s (E-38), the waits of a pause (E-51, E-52) and of the job end (E-53). An expired macro wait goes on as "not found" (V7-40), and with LA-8 RRF then requests RESET(c), which on File aborts the print. PR10 says link faults never end a print; whether these waits are suspended while the link is down is a logic decision | The same: a gap of more than 3 s fails macro and file requests (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1266-1273`, `RRF/SBC/SbcInterface.cpp:2292-2338`) | PR10, D10 | open |
| LA-6 | Link health state and counters (V8-46) | DSF (DWC, M122) | DWC shows "link degraded" when repairs exceed a set rate, and during an outage stale values with the time left until the stop (v2 R7, R11); M122 shows the counters | Not implemented (v2 implementation status) | v2 R7, R11 | decided |

## 2. Reactions to stops, aborts and requests

| ID | Event | Side | Reaction | v7 | Source | Status |
|---|---|---|---|---|---|---|
| LA-7 | STOPPED from RRF, an emergency stop RRF made by itself (V8-38) | DSF | Like the completion of its own halt: `InvalidateCodes` (`DCS/Link/LinkService.cs:1499-1518`) and `state.status` halted | DSF gets nothing; it keeps its macro levels and codes while RRF has popped its own and answers the codes left in its ring with "stopped" (V7-17, X-5, K20) | D9 | decided |
| LA-8 | A macro wait that expires after RRF wrote the request. Protocol: late answers are dropped by request number (V8-39); RRF may request RESET(c) (V8-12) | RRF | Request RESET(c), so that DSF drops the level it may have pushed and the codes it sent for it. Behaviour change: the channel's whole stack is aborted instead of the caller going on after "not found"; only when DSF answers later than 3 s | Goes on as "not found"; a late MacroStarted gives "none was requested", a late Code runs at the caller's level, a late MacroCompleted(error) pops the current level (V7-40, K23) | D10 | decided |
| LA-9 | Locked for a lock request that DSF no longer has (V8-36) | DSF | Answer with Unlock | Logs an error, sends no Unlock, and RRF keeps the movement lock (`DCS/Link/Channel/Processor.cs:838-849`; G-13, K14) | draft 3 (V8-36), moved here by the layering rule | open |
| LA-10 | A DoCode that DSF cancels (V8-39) | DSF | Answer with an error result, so that RRF's GB does not stay "executing on SBC" | Sends nothing; RRF's GB stays "executing on SBC" without a bound (`DCS/Codes/Pipelines/Executed.cs:170-230`; G-12, K13) | draft 3 (V8-39), moved here by the layering rule | open |
| LA-11 | No STOP_ACK within 4.5 s of an M112 (V8-20, V8-32) | DSF | Stop DCS, as today after the 4.5 s of M112 (E-21, `DCS/Codes/Handlers/MCodeHandler.cs:755-763`). For RRF the restarted DCS is a PeerRestarted (LA-3) | The same, but on the halt completion by counting (V7-13) | draft 3 (V8-32) | open: whether it stays as it is under PR10 |
| LA-12 | Job abort. Protocol guarantee: a RESET drops the codes of the older epoch on both sides (V8-9, V8-10) | DSF | Reset File and File2 before writing PrintStopped(abort) | PrintStopped(abort) alone (`DCS/Files/JobProcessor.cs:763-767`); RRF purges the ring codes of File and File2 on read, without an abort of the channels (`RRF/SBC/SbcInterface.cpp:509-517`). PrintStopped(normal) needs both mutexes (`:521-539`); its stall is bounded by RRF's main-task waits and accepted (V8-6) | Tim, 2026-11-09 | decided |
| LA-13 | PrintPaused (catalogue A10) | both | Keep the v7 mechanism until M4; then decide with test data whether PrintPaused becomes an RRF request for a RESET of File and File2. The pause position logic is not analysed | v7 (V7-68) | D7 | open until M4 |

## 3. Unchanged logic that v8 relies on

| ID | Event | Side | Reaction | v7 | Source | Status |
|---|---|---|---|---|---|---|
| LA-14 | M112 read from a macro or from a source that is not prioritized | DSF | Keeps its flush (`DCS/Codes/Handlers/MCodeHandler.cs:750`). The wiki tells users to put M81 before M112 in a trigger macro (`wiki-content/User_manual/Reference/Gcodes.md:2682`), so order inside a macro matters. The fast path for door and E-stop logic is the RRF-local trigger action ([rrf-local-trigger-actions.md](rrf-local-trigger-actions.md) TA1, RRF #25). With V8-17 the flush no longer waits for room taken by other channels (DF-T3) | The same (V7-16) | former V8-22, moved here by the layering rule | decided (v7 kept, PR9) |

## 4. Logic defects found by the v8 work

These are findings in the logic, outside v8 (Tim, 2026-11-09; decision D11 revised). They are no precondition for any
v8 milestone. Each needs a fix the way CLAUDE.md describes, with a unit test that shows the fault first.

| ID | Event | Side | Reaction (fix) | v7 | Source | Status |
|---|---|---|---|---|---|---|
| LA-15 | — | DSF | C-3: M606 S1 takes the job lock before the channel locks, against the order "channel before job" (`DCS/Files/JobProcessor.cs:821-822`); fix the lock order | K3 (`DCS/Codes/Handlers/MCodeHandler.cs:1133-1135`, `DCS/Files/JobProcessor.cs:397`, `DCS/Link/LinkInterface.cs:315-324`) | K3, D11 | open |
| LA-16 | — | DSF | M260 and M261 with V delete a stale variable without a flush; order the deletion after the earlier codes of the level, as `var` does (`DCS/Codes/Handlers/KeywordHandler.cs:244-248`) | K25 (`DCS/Codes/Handlers/MCodeHandler.cs:798-805`) | K25, D11 | open |
| LA-17 | — | DSF | The result of a code that DSF handles itself is sent as SetLastCodeResult without a flush, so an earlier RRF code can overwrite it | K26 (`DCS/Commands/Generic/Code.cs:266-292`, `DCS/Codes/Pipelines/Pre.cs:30-35`) | K26, D11 | open |
| LA-18 | — | DSF | Other DSF-internal codes that complete without a flush while an earlier RRF code of their level runs and so write a request that RRF refuses during its macro wait: not listed yet; missing flushes are logic, out of the v8 scope | V7-33 | v8 Q13 | open |
