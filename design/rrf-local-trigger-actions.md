# RRF-local trigger actions: requirements and state

Status: 2026-11-11. The requirements are those of the draft of 2026-10-08. Since then RRF got the
mechanism (Meltingplot/RepRapFirmware#24 and #25, in the fork's RRF from 3.7.0+1.mp.1), and this
version says what it does, what is still open and how it relates to the findings on trigger macros.
Prerequisite for [sbc-link-requirements.md](sbc-link-requirements.md) (safety boundary SB1).

Line numbers: RRF `3.7-dev` 5c0cf3b8f, DSF `v3.7-dev` db50ae25, Duet3D wiki a106ad4
(`User_manual/Reference/Gcodes.md`).

## Purpose

A safety function must not depend on the SBC link. The link is one layer, and motion and safety are
others: the link reports its state, the logic decides what to do, and a function that keeps a person
safe has to work when the SBC, Linux or the link do not.

Today the operating-mode logic of the risk assessment (HZ-001) runs as G-code macros on the SBC:

1. A door contact changes a Duet input.
2. RRF requests a trigger macro from DSF over the link.
3. The macro switches heaters off, enables keepout zones, and during motion switches an output to the
   hardware emergency stop.

That puts the SBC, Linux and the SPI link into a function that blocks motion while a person may reach
in. This spec moves the function into RRF.

## What a trigger does in RRF

- **Trigger 0** is the emergency stop that RRF makes itself, as if M112 had been received, and
  **trigger 1** pauses the print (wiki `Gcodes.md:7190`). `CheckTriggers` calls `DoEmergencyStop()` for
  trigger 0 in the same spin, without waiting for the trigger channel (`RRF/GCodes/GCodes.cpp:1005-1008`).
- **Trigger 2 and higher** run `sys/triggerN.g` (wiki `:7190`; `GCodes.cpp:1060-1063`). In SBC mode
  that file comes from DSF, so such a trigger needs the link and DSF, whatever its macro does. A
  machine whose stop is a trigger macro that calls M112 depends on both for that stop.
- A trigger macro starts only when the trigger channel is free (`GCodes.cpp:1010`, `IsTriggerBusy`
  `:426-437`), and while one runs, the others wait.

## Requirements and their state

| ID | Requirement | State |
|---|---|---|
| TA1 | **Inline action.** A trigger can carry an action of up to 255 characters of G-code (RRF's `MaxGCodeStringLength` is 256 including the terminator), similar to the command string of M911. RRF executes it itself: no macro file, no DSF, no link. | Implemented in RRF (#25), see the next section. Only for triggers on an expression (M581.1), not for triggers on inputs (M581). |
| TA2 | **Door open.** Regardless of machine state: heaters off and the motion lock (keepout zones) on. If an axis is moving, RRF switches the hardware emergency stop (stop category 0, EN 60204-1) itself. | The commands for it are allowed in an action (M568, M140, M141, M599, M42, M112). Not configured on the machine yet: its door and emergency stop triggers still run macros, see "The CHX 350 today". |
| TA3 | **Modes as in HZ-001.** Normal mode (no motion) is active after start-up, after a door opening and after the automatic mode is aborted. Automatic mode is possible only with all door contacts closed and after a plausible open → closed transition. Changing the mode never causes motion by itself. | An action can set a global (`set`), which is what a mode change needs. The mode logic itself is configuration of the machine and is open. |
| TA4 | **Reaction time.** From the door contact switching to the axes standing still: within the process safety time of about 100 ms (200 mm at 2,000 mm/s, EN ISO 13855), counting all moves that are already queued. | Not measured. An action that is M112 fires as trigger 0, so it does not wait for the trigger channel. Every other action waits for it like a macro does. |
| TA5 | **Configuration.** Defined in the trigger configuration and visible in the object model. The SBC keeps its plausibility checks and the user interface as a second layer, which is not safety-related. | Defined with M581.1 and reported by `M581.1 T<n>`. Not in the object model: RRF has no entry for triggers there. |
| TA6 | **Validation.** The motion lock with an open door is a safety function with PLr c (S2/F1/P1, EN ISO 13849-1). It is validated to that level, including the RRF software. | Open. |

## What RRF implements

`M581.1 T<n> P"<expression>" U"<line of G-code>" D<ms> R<condition>`. `U` and `D` are additions of the
fork; the wiki knows T, P and R for M581.1 (`Gcodes.md:7244-7266`).

- **`U"<line>"` is the action.** One line of G-code that RRF runs instead of `triggerN.g`, read into a
  string of 256 bytes (`RRF/GCodes/TriggerItem.cpp:200-216`, `:230-240`; `Config/Configuration.h:169`).
  - `U""` deletes the action, so that `triggerN.g` runs again (`TriggerItem.cpp:232`). Configuring the
    trigger on inputs or endstops with M581 deletes it as well (`:297-298`).
  - Triggers 0 and 1 refuse an action (`:202-205`).
- **What an action may contain:** only what RRF executes itself and at once (`TriggerItem.cpp:443-484`).
  - M42, M81, M106, M107, M112, M117, M118, M140, M141, M143, M150, M302, M568 and M599;
  - `set`, `echo` without a redirection to a file, and a comment.
  - Nothing that SBC mode passes to DSF, that runs a macro, that waits or that moves. M581.1 refuses an
    action that starts with such a command, and a later command on the line is refused when it would
    run (`RRF/GCodes/GCodes2.cpp:124-128`).
- **How it runs:** when the trigger fires, the line is copied into the buffer of the trigger channel,
  as the power fail script of M911 is copied into its channel (`GCodes.cpp:1027-1058`; M911 at `:1559`).
  A meta command runs at once, other commands when the trigger channel is spun. An action waits for the
  trigger channel as a macro does (`:1010`), and while its commands run, other triggers wait (`:426-437`).
- **`U"M112"` makes the trigger an emergency stop like trigger 0** (`TriggerItem.cpp:233-239`). It sets
  the pending bit of trigger 0, so it fires before every other trigger and without waiting for the
  trigger channel (`GCodes.cpp:997`, `:1005-1008`; for M582 `RRF/GCodes/GCodes3.cpp:428`).
- **Fail-safe (#24):** if the expression of trigger 0, or of a trigger whose action is M112, cannot be
  evaluated, the trigger fires instead of being disabled (`TriggerItem.cpp:135-146`), and an error at
  M581.1 leaves it configured, so that its next check fires it (`:331-338`). The wiki says an error
  disables a trigger (`Gcodes.md:7264`); that still holds for every other trigger.
- **`D<ms>` is a confirmation time:** the trigger fires only once its expression has stayed true for
  that long, up to 65535 ms (`TriggerItem.cpp:118-133`, `:217-218`, `:242`). M582 checks the level
  without waiting (`:412-421`).
- **Lock:** once configured, trigger 0 and every trigger with an action can only be reported until the
  next restart. Any M581 or M581.1 that would change or delete it, `P-1` included, is refused with
  "Trigger N is locked until the next restart" (`TriggerItem.cpp:161-175`, `:342`). Upstream RRF lets
  trigger 0 be changed at any time.
- **Report:** `M581.1 T<n>` shows the expression, the confirmation time, the action and the lock
  (`TriggerItem.cpp:388-406`).

Not covered by the lock, from the description of #25: the globals that an expression reads stay
writable, and M950 can remove an input that an expression reads.

## In SBC mode

- **An action needs no DSF,** but only if RRF evaluates the trigger. DSF takes over every M581.1 whose
  expression contains an `sbc.*` field: it sends `M581 T<n> P-1` to RRF, evaluates the expression itself
  and runs `triggerN.g`; `U` and `D` are not used (`DuetControlServer/Model/SbcTriggerService.cs:122-161`).
  So a trigger with an action must not read SBC fields.
- **Trigger 0 with SBC fields** (finding T7 in [open-findings.md](open-findings.md)): DSF runs
  `trigger0.g` instead of an emergency stop. The `M581 T0 P-1` it sends first is refused by the fork's
  RRF when trigger 0 is configured, because of the lock; DSF does not check that reply
  (`SbcTriggerService.cs:133-140`).
- **Triggers that run a macro still depend on DSF.** The findings on them are in
  [open-findings.md](open-findings.md) and [findings-upstream-summary.md](findings-upstream-summary.md):
  codes buffered ahead can hold back the codes of a trigger macro (T3, fixed by #133), a trigger macro
  can get lost (T1, fixed by Meltingplot/RepRapFirmware#43; P4 on the plain link, open), and a trigger
  whose macro request fails is lost (LA-19 in
  [sbc-link-v8-logic-adaptations.md](sbc-link-v8-logic-adaptations.md)). An action has none of these
  paths.
- **A lost connection:** the fork's RRF makes an emergency stop when the connection to the SBC ends
  (`RRF/SBC/SbcInterface.cpp:284-289`). That stops the machine, but it is not the door function: it acts
  only after the connection timeout of 4 s.

## The CHX 350 today

Read from the lab printer on 2026-11-11 (`0:/sys`, configuration of the machine, RRF 3.7.0+1.mp.9):

| Trigger | Configured as | Runs |
|---|---|---|
| T2, emergency stop input | `M581 T2 P0:1 S0 R0` (`meltingplot/ce-declaration/e-stop.g:26`) | `trigger2.g` through DSF |
| T4 and T9, door switches | `M581 T4 P2:3 S0 R0` (`meltingplot/ce-declaration/ce-declaration.g:13`), `M581 T9 P2:3` (`config.g:286-287`) | `trigger4.g`, `trigger9.g` through DSF |
| T3, operating mode | `M581.1 T3 P"global.machine_mode_safe == …" R0` (`ce-declaration.g:12`) | `trigger3.g` through DSF |
| T5 to T8, T10, process watchdogs | `M581.1` with an expression (`config.g:214-291`) | `triggerN.g` through DSF |

No trigger of the machine has an action. The interim state SB2 of the link requirements therefore still
holds: the door reaction and the stop of the emergency stop input run through the link.

## Open points

- **Configuration of the machine.** Move the door check and the stop of the emergency stop input to
  triggers with an action, or to trigger 0. Triggers on inputs (M581) cannot have an action, so the
  inputs have to be read in an expression (`sensors.gpIn[n].value`). #25 names a plan for it
  (rrf-trigger-actions-plan.md), which is not in this repository.
- **TA4, reaction time.** Measure from the input to standstill, for an M112 action and for an action
  that waits for the trigger channel. A move that is already queued can last longer than 100 ms (for
  example a slow Z move); what the emergency stop does to a running move has to be measured.
- **TA5, object model.** The triggers, their actions and the lock are not in the object model.
- **TA6, validation,** and the **classification**: HZ-001 states that the door contacts are not SRP/CS
  (safety-related parts of a control system) and that the measure is step-1 inherent design. That is to
  be reviewed with the safety engineer against the EN ISO 13849-1 definition of a safety function.
- **Tests.** The DSF unit tests and the lab printer tests have no case for a trigger with an action;
  the fake firmware does not model one.
- **Documentation.** `U`, `D`, the fail-safe of trigger 0 and the lock deviate from the Duet3D
  documentation and have to be documented for our machines. Whether they are proposed upstream is
  not decided.
