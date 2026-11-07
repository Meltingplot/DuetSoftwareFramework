# Lab printer tests

Hardware tests that run against the CHX 350 lab printer (Pi 5 + Duet 3 MB6HC, six expansion boards). Each test reproduces a bug seen on that printer, so a DSF or RRF build can be checked before it is released. The project is fork-only and not part of the solution. The unit tests in `src/UnitTests` cover everything that can run without hardware.

## Running

```
src/LabPrinterTests/run-lab-tests.sh [--side-load [--fault-injection]] [dotnet test arguments]
```

- Needs Tim's hardware token (plink uses gpg-agent as Pageant), PuTTY's `plink.exe` and the `mpadmin` account on the SBC (passwordless sudo).
- The script stops `mp-ota-gate.timer`, so that no update starts, and starts it again at the end.
- `tunnel.py` forwards `127.0.0.1:18080` in WSL to the SBC's HTTP API, with one `plink -nc` per connection. A plink listener on Windows does not work reliably, because Windows may drop connections from WSL to it.
- `--side-load` publishes DCS from this tree and bind-mounts `DuetAPI.dll`, `DuetControlServer.dll` and `DuetSharedLibrary.dll` over the image's copies for the run. It removes them again at the end. Both steps restart DCS and send M999.
- `--fault-injection` side-loads the test-only build that corrupts a single SPI exchange when one is written to `/var/tmp/lab-tests/spi-fault` (`DuetControlServer/Link/Adapter/SpiFaultInjection.cs`). Only `SpiFaultInjection` uses it; see below.
- Examples: `--filter "TestCategory!=Restart"` skips the restarts. `--filter "FullyQualifiedName~LinkSoak"` runs the 30-minute soak.
- `LAB_WATCHDOG_RESTARTS` sets the number of watchdog resets of `FirmwareRestart.LinkRecoversAfterWatchdogReset` (default 5), e.g. 40 to look for a reboot race that hits about one restart in 40.
- Without `LAB_PRINTER_URL` and `LAB_PRINTER_SHELL`, which the script sets, every test is skipped.

## What the tests may do

- A test runs only while the printer is idle with no job and every expansion board running. If a job runs when the run starts, nothing is touched.
- Always allowed: codes such as M409, M122, M291/M292, test macros in `0:/macros/lab-tests`, and global variables named `labTest...`. The globals stay until the next reset.
- Category `Restart`: firmware restarts (`M122 P1001`; on the fork, the lost link also triggers an E-stop and switches ATX off), DCS restarts and M999.
- Category `Trigger`: a temporary trigger 30 with `sys/trigger30.g`. These tests are skipped if the machine uses either.
- Category `FaultInjection` (explicit): single corrupted SPI exchanges. A fault that breaks the link costs a DCS restart and M999, and on the fork the lost link also triggers an E-stop and switches ATX off.
- Category `Approval` (explicit): `IntrusiveCodes`, codes that change the state of the machine, such as M112, M929 or a test file in `0:/gcodes`. Tim decides on each run, see `design/lab-test-plan.md`. Run one with `--filter "FullyQualifiedName~IntrusiveCodes.M112StaysHaltedUntilM999"`.
- Explicit only: the 30-minute soak, and the M581.1 string literal test, which hangs RRF until its watchdog resets it.
- Never: print jobs, motion or heaters.
- A test that leaves a channel stuck restarts DCS and sends M999 before it fails.

## Tests and the bugs they cover

| Test | Bug | Fixed in |
|---|---|---|
| `UnbufferedCodes.M409*DuringMacro` | M409 from a client deadlocked a channel that ran macros (DSF 3.7.0+mp.7) | DSF #51 (mp.8), replaced by upstream 30ea08be |
| `MessageBoxes.OneBoxAfterM122` | Three M122 followed by M291 S2 opened several message boxes | RRF f2e44bcc (RRF 3.7.0+1.mp.5) |
| `MacroReturns.NestedM99KeepsCallerVariables` | A late M99 reply completed the caller's next M98: "unknown variable" (DSF 3.7.0+mp.5) | DSF upstream da8e9bb8 with RRF 4d80f4663 (DSF mp.6) |
| `Diagnostics.RepeatedM122` | M122 hard-faulted RRF in `Flash::ReadUserSignature` (RRF 3.7.0-rc.2+1-mp.3) | RRF 3.7.0-rc.2+1-mp.4 |
| `FirmwareRestart.LinkRecoversAfterWatchdogReset` | The link never recovered after RRF restarted (DSF mp.8 and upstream, 4 of 5 restarts). Also checks object model answers after the reconnect | DSF #59 |
| `ControlServerRestart.PluginServicesStartWithControlServer` | The plugin services stayed down after DCS was stopped and started | DSF #37 (mp.2) |
| `ControlServerRestart.ResetDoesNotHangShutdown` | DCS hung in its shutdown for 15 s after M999. Intermittent, see the test | Open |
| `TriggerMacros.TriggerMacroCallingMacro` | Garbled trigger macro names ("Macro file trigger8.g not found") | RRF fork PR #12 and #13 (3.7.0-rc.2+1-mp.3) |
| `TriggerMacros.DeleteFirmwareTriggerWithM581Point1` | `M581.1 T# P-1` does not delete a trigger that RRF evaluates | DSF #61 (upstream still has it) |
| `TriggerMacros.ReportFirmwareTriggerWithM581Point1` | `M581.1 T#` and `M581.1 T# R<n>` were refused without P | DSF #63 (upstream still has it) |
| `TriggerMacros.ReportSbcTriggerWithM581` | A plain `M581 T#` report deleted a trigger that DSF evaluates | DSF #63 (upstream still has it) |
| `TriggerMacros.StringLiteralInExpression` (explicit) | M581.1 with a string literal in its expression hangs RRF 3.7 | Open (upstream RRF) |
| `MacrosOnOtherChannels.TriggerKeepsFiringWhileMacroRuns` | No bug: M98.n starts a macro on Aux, HTTP, Telnet or Aux2 from a trigger macro and the trigger keeps firing | New in M98.n |
| `MacrosOnOtherChannels.CallIsRefused` | No bug: calls M98.n refuses, including USB and USB2 | New in M98.n |
| `SelfTestAutostart.FlowStartsAfterBootAndCodesGetAnswers` | No bug: the machine starts its self-test with M98.3 after a boot, and M409 from a client gets answers meanwhile | New in M98.n |
| `TriggerMacros.RrfExpressionReplacesSbcTrigger` | An M581.1 expression that RRF evaluates left the old trigger running in DSF | DSF #67 (upstream still has it) |
| `TriggerMacros.DisabledSbcTriggerDoesNotFire` | A trigger that DSF evaluates fired with `R-1` while not printing | DSF #63 (upstream still has it) |
| `LineChecksums.*` | No bug: a wrong CRC, a missing CRC or a wrong line number ends a macro | New in the line checksums |
| `CodeReplies.M36MissingFileIsValidJson` | M36 replied with invalid JSON for a missing file | DSF #72 |
| `CodeReplies.M39ReportsSizesAndClusterSize` | M39 reported sizes in "Gb" and no cluster size | DSF #82 |
| `LinkSoak.NoLostConnectionWhileIdle` (explicit) | TfrRdy timeouts every 15 to 30 minutes (DSF 3.7.0-rc.1) | Upstream 46886f5d |
| `SpiFaultRestarts.StuckLinkRestartsDcs` (explicit) | A stuck link needed a manual restart of DCS | DSF #102 |
| `SpiFaultRestarts.DcsStartsWhileTfrRdyIsLow` (explicit) | A new DCS failed its first start when a recovered fault left TfrRdy low | DSF #99 |
| `IntrusiveCodes.M112StaysHaltedUntilM999` (explicit) | M112 completed before the emergency stop reached RRF | DSF #88 |
| `IntrusiveCodes.M111OutputDoesNotCrashDcs` (explicit) | DCS crashed with a stack overflow while `M111 P-1 O1` was active | DSF #70 |
| `IntrusiveCodes.M30AndM472Delete` (explicit) | M30 used the wrong folder for a relative name, M30 and M472 reported success for a missing file | DSF #74, #80 |
| `IntrusiveCodes.M929InvalidLevelReportsAnError` (explicit) | M929 with an invalid log level reported success | DSF #81 |
| `IntrusiveCodes.M606FromHttpOnlyWarns` (explicit) | M606 S1 from another channel than File forked the job reader of DCS | DSF #76 |

## SPI fault injection

`SpiFaultInjection.SingleFaultsMatchCensus` checks the link census of `src/UnitTests/SPI/LinkRecovery.cs` on the real link. The census
writes its cases with the predicted outcome per kind of transfer, for the firmware on the printer:

```
LAB_SPI_FAULT_CASES=/tmp/cases.json dotnet test src/UnitTests --filter "Name=WriteHardwareCases_RrfOfToday"   # or _Extended
LAB_SPI_FAULT_CASES=/tmp/cases.json src/LabPrinterTests/run-lab-tests.sh --side-load --fault-injection --filter "FullyQualifiedName~SpiFaultInjection"
```

- Each case arms one fault and waits until it hits the next transfer of the requested kind. It then sorts the result as `Retry` (repaired by the protocol), `Reconnect`, `DcsRestart` or `NoRecovery` (no answer to `M409 ... R1` after 2 s), and compares it with the census prediction for the kind of transfer that was hit.
- `LAB_SPI_FAULT_SELECT`: `failures` (predicted to break the link), `responses` (every fault in a response word, where the census finds all failing single bits), or `all`. The default is `failures,responses`.
- `LAB_SPI_FAULT_MAX` limits the number of cases.
- `UnitTests` `InjectorMatchesCensus` checks that the injector hits the same exchanges as the census.

`SpiFaultRestarts` uses the same build for two checks on the firmware that is on the printer. They need no case file:

```
src/LabPrinterTests/run-lab-tests.sh --side-load --fault-injection --filter "FullyQualifiedName~SpiFaultRestarts"
```

- `StuckLinkRestartsDcs` arms a fault of the kind `Activation`, which waits for the transfer that activates the link capabilities. A watchdog reset makes DCS connect and activate again. The test measures the stop at the line of systemd, because a stuck link logs more than journald lets through, and a DCS that writes all lines with one priority loses its own message about the stop.
- `DcsStartsWhileTfrRdyIsLow` reads the pin after stopping DCS. A fault that leaves it high is no result, so the test tries the next one and is inconclusive without any.
- The faults are checked offline by `LabFaultInActivationRestartsDcs` and `LabFaultLeavesTfrRdyLow`. Keep them in line with the lab test.

## Not covered

- The end-of-print reply and M32 from a job file (upstream 9acd058f) need a print job.
- Case 2 of #57 (RRF takes the data of a retried transfer) has never been observed and needs fault injection.
- Object model responses that shift after a reconnect have no unit test, because `LinkService` gets them from `ILinkAdapter.ReadObjectModel` as a span, which a proxy adapter cannot supply. On hardware, `FirmwareRestart` compares the object model of DCS with the answers of RRF.
