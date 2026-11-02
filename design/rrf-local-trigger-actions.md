# RRF-local trigger actions: requirements (draft)

Status: draft, 2026-10-08. Prerequisite for [sbc-link-requirements.md](sbc-link-requirements.md)
(safety boundary SB1). Requirements only; the G-code syntax is not defined yet.

## Purpose

Today the operating-mode logic of the risk assessment (HZ-001) runs as G-code macros on the SBC:

1. A door contact changes a Duet input.
2. RRF requests a trigger macro from DSF over the link.
3. The macro switches heaters off, enables keepout zones, and during motion switches an output to the
   hardware emergency stop.

That puts the SBC, Linux and the SPI link into a function that blocks motion while a person may reach
in. This spec moves the function into RRF, so that it works without the SBC.

## Requirements

| ID | Requirement |
|---|---|
| TA1 | **Inline action.** A trigger can carry an action of up to 255 characters of G-code (RRF's `MaxGCodeStringLength` is 256 including the terminator), similar to the command string of M911. RRF executes it itself: no macro file, no DSF, no link. |
| TA2 | **Door open.** Regardless of machine state: heaters off and the motion lock (keepout zones) on. If an axis is moving, RRF switches the hardware emergency stop (stop category 0, EN 60204-1) itself. |
| TA3 | **Modes as in HZ-001.** Normal mode (no motion) is active after start-up, after a door opening and after the automatic mode is aborted. Automatic mode is possible only with all door contacts closed and after a plausible open → closed transition. Changing the mode never causes motion by itself. |
| TA4 | **Reaction time.** From the door contact switching to the axes standing still: within the process safety time of about 100 ms (200 mm at 2,000 mm/s, EN ISO 13855), counting all moves that are already queued. |
| TA5 | **Configuration.** Defined in the trigger configuration and visible in the object model. The SBC keeps its plausibility checks and the user interface as a second layer, which is not safety-related. |
| TA6 | **Validation.** The motion lock with an open door is a safety function with PLr c (S2/F1/P1, EN ISO 13849-1). It is validated to that level, including the RRF software. |

## Open points

- **G-code interface.** It extends the existing M581 triggers (wiki: T0 = emergency stop, T1 = pause,
  T2 and up run `sys/triggerN.g`). The syntax must be designed against the RRF source and the Duet3D
  wiki before it is fixed (CLAUDE.md G-code rules).
- **Classification.** HZ-001 states that the door contacts are not SRP/CS (safety-related parts of a
  control system) and that the measure is step-1 inherent design. That is to be reviewed with the
  safety engineer against the EN ISO 13849-1 definition of a safety function.
- **Queued moves.** A move that is already queued can last longer than 100 ms (for example a slow Z
  move). TA4 therefore needs a way to stop a running move.
