# SBC link robustness: requirements

Status: version 2, agreed on 2026-10-08, not implemented yet. Replaces version 1 of 2026-10-07; the
decision log at the end lists what changed.
Scope: the SPI link between Duet Control Server (DCS) on the SBC and RepRapFirmware (RRF), on the
Meltingplot fork of DSF and RRF.
Baseline: [sbc-link-current-state.md](sbc-link-current-state.md) describes what the link does today.
Prerequisite: [rrf-local-trigger-actions.md](rrf-local-trigger-actions.md) moves the operating-mode
logic off the link.

## Vision

> As long as the Duet and the SBC are both running, nothing that happens on the link may reset it or
> end the job. A disturbance may cost time, never the job.

## Normative basis

| Area | References |
|---|---|
| Machinery safety | Machinery Directive 2006/42/EC and Machinery Regulation (EU) 2023/1230 (applies from 20 January 2027), control-system rules in Annex I / Annex III 1.2.1: no unexpected start-up, no uncontrolled change of parameters, stopping must not be prevented. EN ISO 12100 (risk assessment), EN ISO 13849-1 (performance levels), EN 60204-1 (stop categories), EN ISO 13850 (emergency stop), EN ISO 14119 (interlocking devices), EN ISO 13855 (approach speeds). |
| EMC | EMC Directive 2014/30/EU. Generic immunity standard EN IEC 61000-6-2 (industrial), which also covers EN IEC 61000-6-1. Basic standards IEC 61000-4-2 (ESD), -4-3 (radiated RF), -4-4 (fast transient bursts), -4-5 (surge), -4-6 (conducted RF). |
| User experience | EN ISO 9241-110 (interaction principles: self-descriptiveness, error tolerance, no needless alarms). |

## Safety boundary

- **SB1: No safety function depends on the link.** The emergency stop is hardwired (stop category 0,
  EN 60204-1). The operating-mode logic of HZ-001 (open door → Normal mode, motion lock, heaters off)
  moves into RRF and runs without the SBC; see the prerequisite spec.
- **SB2: Interim state.** Until the prerequisite is delivered, the door reaction still runs through
  the link (Duet input → trigger macro on the SBC → output to the hardware emergency stop). A broken
  link stops new motion, but a move that is already queued runs to its end. A queued move can last
  longer than the 40–60 ms planning lookahead, for example a slow Z move. The interim state therefore
  does not guarantee the ~100 ms process safety time of the door.
- The link still carries motion commands. Its integrity (R9) and the stop path (R10) apply the
  Annex I/III 1.2.1 rules to the data it carries, even though the link is not a safety function.

## Requirements

| ID | Requirement | Basis |
|---|---|---|
| R1 | **Never reset while both ends are alive.** The link session may be reset only when one side has provably lost its state (restart, crash, reflash), or under R8. It is never reset because of a missed or corrupted transfer, a failed checksum, a phase shift, a missing, extra or stuck `TfrRdy` edge, or a chip-select or clock glitch. | Vision |
| R2 | **Recover within 20 ms, losing nothing.** Any single fault is repaired within 20 ms, which is half the shortest motion lookahead of 40 ms. No data (codes, replies, file data, object model) is lost or duplicated in either direction. | R3 criterion A |
| R3 | **EMC immunity at EN IEC 61000-6-2 levels.** For continuous phenomena (radiated and conducted RF), criterion A applies, defined as: the link never lets the motion queue run dry; its throughput may degrade. For transients (ESD, bursts, surge), criterion B applies: temporary degradation, automatic recovery, the job continues. | EMC Directive, EN IEC 61000-6-2 |
| R4 | **Almost no cost on a clean link.** Without disturbances, codes/s and transfers/s (M122) stay within 2% of today. | — |
| R5 | **No fragile timing.** Correctness must not depend on timings measured on a particular SBC, kernel or Pi model. Timing values may only affect speed. | — |
| R6 | **Real restarts are caught.** When either side restarts, DCS discards pending work and RRF performs a stop of category 0, as today. | Annex I/III 1.2.1 (no unexpected start-up) |
| R7 | **Disturbances are visible without alarming.** Every repaired disturbance is counted in M122. DWC shows a "link degraded" state when repairs exceed a set rate; there is no message per repair. | EN ISO 9241-110 |
| R8 | **Total silence.** If no exchange succeeds for 30 s (configurable from 5 to 120 s), RRF performs a stop of category 0. This is the only reset triggered by a timeout. | EN 60204-1 |
| R9 | **Integrity.** At most 10⁻⁷ undetected corrupted transfers per hour at the worst error rate the spec allows. This is oriented on the PL d range of EN ISO 13849-1 (10⁻⁷ to 10⁻⁶ per hour) and needs a second, independent check in addition to the transfer CRC32. | Annex I/III 1.2.1 (no uncontrolled change of parameters) |
| R10 | **Priority stop path.** A software stop (M112, or the DWC stop button) reaches RRF within 100 ms while the link is disturbed but alive. Stop requests bypass pending retries and resyncs. | Annex I/III 1.2.1 (stopping must not be prevented) |
| R11 | **Outage display.** While the link is down but the R8 limit has not been reached, DWC keeps the last values visible, marks them as stale, and shows "link to controller interrupted, retrying" with the time left until the safety stop. | EN ISO 9241-110 |
| R12 | **Parity on demand.** Error correction is part of release 1: after a failed check, the sender transmits parity instead of resending the whole block. Nothing extra is sent on a clean link (R4). | R2, R3 |

## Compatibility

| ID | Requirement |
|---|---|
| C1 | **Negotiation on top of protocol version 7.** A capability handshake switches robust features on only when both sides support them. Stock and fork DSF/RRF can be combined freely and fall back to today's v7 behaviour. There is no fork-specific protocol number to collide with upstream. |
| C2 | **Flashing stays as today** (v7 IAP, CRC16). Security of the update path (Machinery Regulation Annex III 1.1.9, Cyber Resilience Act (EU) 2024/2847) is a separate project. |
| C3 | **Boards with fewer features** (Mini 5+) negotiate what they support, with no guarantees. |

## Scope

- **Boards:** R1–R12 apply to the SAME70 boards (MB6HC, MB6XD). Mini 5+ is best effort through C3.
- **SBCs:** Raspberry Pi 4 and Raspberry Pi 5.
- **Transport:** SPI only.
- **Out of scope:**
  - Processor resets caused by ESD or bursts. The system requirement is that the machine design
    (grounding, shielding, protection) prevents such resets at the test levels; the link only detects
    a restart (R6).
  - SBC over USB.
  - Compute Modules.
  - Security of the update path (C2).
  - The operating-mode logic itself (prerequisite spec).
- **Upstream:** whether to offer this to Duet3D is decided later; C1 keeps the fork compatible either
  way.

## Later milestone

| ID | Goal |
|---|---|
| D1 | **Normal speed under noise beyond the standard levels.** The link adapts transfer size and SPI clock so the job keeps running at normal speed in environments harsher than EN IEC 61000-6-2, and returns to full speed when the noise stops. |

## Acceptance tests

| Req. | Test | Pass when |
|---|---|---|
| R1, R2 | Inject every single fault at every point of the protocol, in a simulator and on the bench | No reset, nothing lost or duplicated, recovery within 20 ms |
| R1, R8 | During a print, interrupt any one line (SCK, MOSI, MISO, chip-select, `TfrRdy`) for 1 ms up to just below the R8 limit, then once beyond it | The print continues once the line is back; beyond the limit, RRF stops (category 0) |
| R3 | Machine-level EMC tests at EN IEC 61000-6-2 levels during a print, plus margin tests with interference injected directly onto the SBC cable | RF: no motion stall caused by the link. ESD, bursts, surge: the print finishes. |
| R4 | Clean link, M122 codes/s and transfers/s | Within 2% of today |
| R5 | Simulator with random edge merging and interrupt latency | Same results at any latency |
| R6 | Restart DCS; reboot the Duet | Each is detected; RRF stops (category 0) |
| R7, R11 | Raise the repair rate; interrupt the link for a few seconds | "Link degraded" state appears; during the outage DWC shows stale values and the countdown |
| R9 | Analysis plus fault-injection estimate at the worst allowed error rate | ≤ 10⁻⁷ undetected corrupted transfers per hour |
| R10 | M112 while faults are being injected | RRF stops within 100 ms |
| R12 | Continuous bit errors on large transfers | Recovered by parity without a full resend; no extra bytes on a clean link |
| C1 | Stock RRF with fork DSF, and fork RRF with stock DSF | Both run with today's v7 behaviour |

## Decision log

| Date | Decision | Reason |
|---|---|---|
| 2026-10-07 | No timing constants measured per SBC (R5) | A minimum edge spacing measured on Pi 4/Pi 5 could break with any kernel or hardware change. |
| 2026-10-08 | No safety function depends on the link; the operating-mode logic moves into RRF | With S2/F1/P1, the motion lock with an open door needs PL c (EN ISO 13849-1). The door chain through SBC macros cannot guarantee the ~100 ms process safety time (200 mm at 2,000 mm/s, EN ISO 13855) and has shown foreseeable software faults (the trigger-macro request race of October 2026). |
| 2026-10-08 | Recovery limit cut from 500 ms to 20 ms | Criterion A for continuous RF means the motion queue (40–60 ms lookahead) must never run dry. |
| 2026-10-08 | EMC target EN IEC 61000-6-2 | It covers residential and industrial use. |
| 2026-10-08 | Silence limit 30 s, configurable 5–120 s, then stop category 0 | Once the mode logic is local, a stalled job cannot surprise anyone; the limit only guards against a dead SBC leaving heaters on. |
| 2026-10-08 | Integrity target ≤ 10⁻⁷ per hour | Motion commands cross the link. CRC32 alone gives about 10⁻⁵ per hour when 1% of transfers are hit at 1,700 per second. |
| 2026-10-08 | Priority stop path, 100 ms | Stopping must not be prevented by link recovery. |
| 2026-10-08 | Negotiation on top of v7 instead of a fork-only v8 (reverses 2026-10-07) | Mixed versions keep working, and there is no protocol-number collision with upstream. |
| 2026-10-08 | Parity on demand in release 1 (reverses "no ECC unless required") | It gives margin for the 20 ms criterion-A limit and costs nothing on a clean link. |
| 2026-10-08 | R1–R12 on SAME70 boards, Pi 4 and Pi 5, SPI only | These are the hardware combinations in the products. |
| 2026-10-08 | Update-path security handled in a separate project | Machinery Regulation 1.1.9 and the CRA need work beyond the link. |
