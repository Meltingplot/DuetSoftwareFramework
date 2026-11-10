# SBC link robustness: requirements

Status: version 2, agreed on 2026-10-08. Partly implemented on 2026-11-02: DSF PRs #89–#93 and RRF
PR Meltingplot/RepRapFirmware#35. The [implementation status](#implementation-status-2026-11-02) lists
what is done and measured. Replaces version 1 of 2026-10-07; the decision log at the end lists what changed.
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
| R9 | **Integrity.** At most 10⁻⁷ undetected corrupted transfers per hour at the worst error rate the spec allows. This is oriented on the PL d range of EN ISO 13849-1 (10⁻⁷ to 10⁻⁶ per hour) and needs a second, independent check in addition to the transfer CRC32. *Conditional on an EMC test (2026-11-09, [channel-epoch requirements](sbc-link-v8-requirements.md) CE-19): measure corrupted transfers per hour under IEC 61000-4-3 and IEC 61000-4-6, 1 h each, at the levels of EN IEC 61000-6-2. At ≤ 100 corrupted transfers per hour CRC32 alone meets R9 (about 2⁻³² undetected per corrupted transfer, so ≤ 2.3 × 10⁻⁸ per hour); above that, a second, independent CRC trailer per transfer is added as a link capability.* | Annex I/III 1.2.1 (no uncontrolled change of parameters) |
| R10 | **Priority stop path.** A software stop (M112, or the DWC stop button) reaches RRF within 100 ms while the link is disturbed but alive. Stop requests bypass pending retries and resyncs. | Annex I/III 1.2.1 (stopping must not be prevented) |
| R11 | **Outage display.** While the link is down but the R8 limit has not been reached, DWC keeps the last values visible, marks them as stale, and shows "link to controller interrupted, retrying" with the time left until the safety stop. | EN ISO 9241-110 |
| R12 | **Parity on demand.** Error correction is part of release 1: after a failed check, the sender transmits parity instead of resending the whole block. Nothing extra is sent on a clean link (R4). | R2, R3 |
| R13 | **Dead-man switch, two stages.** This is an additional layer on top of R10 and the hardwired emergency stop. Stage 1: if DSF has not answered an exchange that RRF has armed for 100 ms, RRF holds motion. It starts no further moves and keeps positions, heaters and the session, without needing the SBC (no `pause.g`). When valid exchanges return, the job resumes automatically, and DWC logs the hold. Stage 2: the R8 limit leads to a stop of category 0. Only the time RRF spends armed and waiting counts; RRF's own processing does not. Only verified exchanges from the same session reset the watchdog. The watchdog runs outside RRF's SBC task. | Annex I/III 1.2.1 (stopping must not be prevented) |
| R14 | **DSF answers in bounded time.** DSF answers every exchange that RRF has armed within 100 ms, and normally far sooner, whatever its processing load. File I/O for RRF, lock waits, JSON processing and garbage collection must not delay the answer. While connected, RRF arms an exchange at least every 25 ms (M576 S is capped accordingly). Planned pauses (firmware update, M999 reset) are announced over the link and suspend R13. | R3 criterion A, R13 |

## Compatibility

| ID | Requirement |
|---|---|
| C1 | **Negotiation on top of protocol version 7.** A capability handshake switches robust features on only when both sides support them. Stock and fork DSF/RRF can be combined freely and fall back to today's v7 behaviour. There is no fork-specific protocol number to collide with upstream. |
| C2 | **Flashing stays as today** (v7 IAP, CRC16). Security of the update path (Machinery Regulation Annex III 1.1.9, Cyber Resilience Act (EU) 2024/2847) is a separate project. |
| C3 | **Boards with fewer features** (Mini 5+) negotiate what they support, with no guarantees. |

## Scope

- **Boards:** R1–R14 apply to the SAME70 boards (MB6HC, MB6XD). Mini 5+ is best effort through C3.
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
| R13 | Freeze DCS during a print (`kill -STOP`), resume it (`kill -CONT`) after 1 s, then repeat for longer than the R8 limit | Motion holds within 100 ms; the job resumes by itself; beyond the R8 limit, RRF stops (category 0) |
| R14 | Print while loading the SD card (writes with `fsync`), running large object-model queries, and making RRF file requests | No hold, no motion stall; the longest DSF answer time stays under 100 ms |
| C1 | Stock RRF with fork DSF, and fork RRF with stock DSF | Both run with today's v7 behaviour |

## Implementation status (2026-11-02)

Evidence comes from two kinds of tests:
- **The census:** `src/UnitTests/SPI/LinkRecovery.cs`, against `FakeRrfSpi`, a port of RRF's `DataTransfer` and `SbcInterface`.
  `PortMatchesRealFirmware` replays the census against a host build of the real RRF code; result: 0 differences in all passes.
  The single-fault census has 4142 faults: every bit of every exchange, special field values and whole-exchange patterns, in both directions.
- **The lab suite:** `src/LabPrinterTests`, on the CHX 350 (MB6HC).

| Req. | State | Evidence |
|---|---|---|
| R1, R2 | Simulator part met for an activated session; bench test pending | With all capabilities active, no single fault after the activation needs a reconnect, and every repair takes less than 20 ms (`ExtendedLinkRecoversFromEverySingleFault`, also for faults in the transfers of the switch). Still open: 7 faults in the connect transfer, before an activation is possible. The activation transfer itself fails like a stock transfer with DSF data (25 since DSF #137, 41 before), because the guards start with it. Without capabilities, 27 faults still need a reconnect or never recover (35 before DSF #137), against 125 before the DSF fixes (then of 4238 faults). Census of 2026-11-08: `LinkRecovery.SingleCorruptedExchange` and `SingleCorruptedExchange_ExtendedActivationTransfer`. |
| R3 | Not tested | — |
| R4 | Not measured | — |
| R5 | Not tested | The capabilities use no timing constant. |
| R6 | Met with the capabilities | Every RRF restart is noticed: 128 at every exchange, plus 56 right after a wrap of the regular sequence numbers. Every DCS restart is noticed, and the new DCS connects at once: 42 cases (`ExtendedLinkNoticesEveryRestart`, `DcsRestart`). Without capabilities, 6 RRF restarts right after a 16-bit wrap go unnoticed, and 2 of 42 DCS restarts need a second start of DCS. On the lab printer, watchdog resets recover in 12.3 s, against 15.9 s before. |
| R7, R8 | Not implemented | — |
| R9 | Not implemented | Data is still protected by CRC32 alone. The extended response codes (below) cover the response words, not the data. |
| R10–R14 | Not implemented | — |
| C1 | Implemented | `limits.sbcLinkCapabilities` (offered, verbose like every limit) and `state.sbcLinkCapabilities` (active for the connection). DCS reads the first after every connect and reset, then activates with request `ActivateLinkCapabilities` (0x4D50). Reading never activates anything. Lab printer: new DSF with stock RRF passed 12/12; old DSF with new RRF behaves as before. |
| C3 | Implemented | Only SAME70 boards offer capabilities. Mini 5+ and SAM4E report 0. |

Capabilities:
- **`PhaseGuard`:** RRF goes back to the header phase when it receives the start of a header in a response state, or a valid header in the data phase.
- **`SequenceGuard`:** RRF resends its last completed transfer when DSF repeats its part of it.
- **`ExtendedHeader`:**
  - The sequence number carries a 5-bit counter and an 11-bit session status. A side that restarted or reset sends status 0, which the other side sees at the first header.
  - The response codes are Walsh codes 16 bits apart, and up to 3 bit errors are corrected. This addresses E3 of the baseline within an activated session.
- **DSF side,** without capabilities: refused response codes no longer stop DCS (E7), the sequence guard resends a transfer RRF did not complete, and DSF goes back to the phase RRF is in.

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
| 2026-10-08 | R1–R14 on SAME70 boards, Pi 4 and Pi 5, SPI only | These are the hardware combinations in the products. |
| 2026-10-08 | Update-path security handled in a separate project | Machinery Regulation 1.1.9 and the CRA need work beyond the link. |
| 2026-10-08 | Two-stage dead-man switch (hold at 100 ms, resume automatically; category 0 stop at the R8 limit) on top of R10 | A stop pressed during an outage takes effect within about 100 ms, without throwing the job away on every short gap. |
| 2026-10-08 | The dead-man switch is driven by DSF's answer time to armed exchanges, not by the spacing of full transfers (R14) | The time between full transfers can become very long in normal operation (observed by Tim in M122). It includes RRF's pacing, RRF's processing, and DSF's synchronous file I/O and lock waits on the transfer thread. |
| 2026-11-02 | In the single-fault census, only a repair by protocol retries passes; a reconnect of either side and a DCS restart count as failures | A running print stops in both cases. |
| 2026-11-02 | Capabilities are offered in `limits.sbcLinkCapabilities` and activated only by a DSF request, per connection | Reading the key, for example from DWC or a plugin, must not change the link. |
| 2026-11-02 | After activation, the sequence number becomes a 5-bit counter plus an 11-bit session status | A small rolling counter is enough to tell next, repeated and missing transfers apart, as on CAN buses. The status makes a restart visible at once instead of through a sequence jump, which a 16-bit wrap can hide. |
| 2026-11-02 | Response codes with maximum bit distance and 3-bit correction | The census found every failing single bit in the unprotected response words, where `Success` and `BadProtocolVersion` differ by one bit. |
| 2026-11-02 | RRF rules stay only where a census case needs them; the wait for a missing transfer and the decoding of the first data word were dropped | Both were reached, but no case needed them. |
| 2026-11-02 | RRF leaves the extended link as soon as DSF has left it | It was dropped on census data and restored after the lab printer showed a restarted DCS failing to start without it. The census had counted a second start of DCS as a pass; it now counts it as a failure. |
| 2026-11-02 | The DSF sequence guard leaves sequence number 1 to the reset detection | Every new RRF connection starts at 1, so a 1 can also come from a restart. On the lab printer, the guard had taken a restart for a repeated transfer. |
| 2026-11-09 | v8 is its own protocol version, required for normal operation on SAME70 over SPI; v7 stays for firmware updates and, until they are ported, for USB and boards other than SAME70 (reverses "negotiation on top of v7" of 2026-10-08 for normal operation, C1). v8 contains the link features from its first transfer and takes over R6, R8, R9 and R13; see [sbc-link-v8-requirements.md](sbc-link-v8-requirements.md), decisions D1, D2, D6, D12, D14 | The catch code can only be removed where v8 runs; negotiated on top of v7, every mixed pair keeps it. Motion and safety are not link (Tim), so the link only reports events and the logic decides |
| 2026-11-09 | After three reviews, v8 is dropped for a channel epoch on v7: a link capability `ChannelEpoch` negotiated like the others, so the decision of 2026-10-08 (C1) holds again and the previous row is reversed. R6, R8 and R13 stay as they are; R9 becomes conditional on an EMC test; no session resume. See [sbc-link-v8-requirements.md](sbc-link-v8-requirements.md) (draft 5), decisions FD-1, FD-10 to FD-12 | The reviews found the epoch sufficient for the ordering problems, together with two fixes (RRF never refuses InvalidateChannel; resent copies keep their tag). Peers without the capability run upstream v7, and the fork catch code is deleted from the v7 path once the printers run the epoch RRF |
| 2026-11-09 | The channel epoch is no link capability: both sides always write its tag, in a header field that a v7 peer ignores, and RRF takes each channel's epoch from DSF's first tagged packet (reverses "a link capability `ChannelEpoch` negotiated like the others" of the previous row; C1 holds, because nothing is negotiated on top of v7 for it). See [sbc-link-v8-requirements.md](sbc-link-v8-requirements.md) (draft 8), decision FD-37 | The activation rode on an object-model round trip, which forced a hold of all channel traffic at connect, a timer and capability support over USB |
