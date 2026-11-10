# Lab printer tests

Hardware tests that run against the CHX 350 lab printer (Pi 5 + Duet 3 MB6HC, six expansion boards). Each test reproduces a bug seen on that printer, so a DSF or RRF build can be checked before it is released. The project is fork-only and not part of the solution. The unit tests in `src/UnitTests` cover everything that can run without hardware.

## Running

```
src/LabPrinterTests/run-lab-tests.sh [--side-load [--fault-injection [--plain-link]]] [dotnet test arguments]
```

- `run-lab-tests.sh` starts `run-lab-tests.py`, which does the work.
- Needs Tim's hardware token (plink uses gpg-agent as Pageant), PuTTY's `plink.exe` and the `mpadmin` account on the SBC (passwordless sudo).
- **Output:** each test shows as `PASS`, `FAIL` (with its message) or `SKIP` when it ends, in color on a terminal, unless `NO_COLOR` is set.
- **Evaluation at the end** (sections 9 and 10 of `design/lab-test-plan.md`):
  - the test results and the watchdog recovery times,
  - the journal lines of DCS since the start, counted by the categories of section 9, and messages journald suppressed,
  - the self-test runs and the M122 counters before and after,
  - the restore check of section 10.
  - The journal findings do not change the exit code, which is that of `dotnet test`.
- **Files:** the raw test output, the TRX file and the journal of the run are kept in `src/LabPrinterTests/TestResults/<UTC time>`, or in `LAB_RESULTS_DIR`.
- The script stops `mp-ota-gate.timer`, so that no update starts, and starts it again at the end, also after Ctrl+C.
- `tunnel.py` forwards `127.0.0.1:18080` in WSL to the SBC's HTTP API, with one `plink -nc` per connection. A plink listener on Windows does not work reliably, because Windows may drop connections from WSL to it.
- `--side-load` publishes DCS from this tree and bind-mounts `DuetAPI.dll`, `DuetControlServer.dll` and `DuetSharedLibrary.dll` over the image's copies for the run. It removes them again at the end. Both steps restart DCS and send M999.
- `--fault-injection` side-loads the test-only build that corrupts a single SPI exchange when one is written to `/var/tmp/lab-tests/spi-fault` (`DuetControlServer/Link/Adapter/SpiFaultInjection.cs`). Only the `FaultInjection` tests use it; see below.
- `--plain-link` creates `/var/tmp/lab-tests/spi-plain-link` before DCS starts. The fault injection build then keeps the plain link: it supports no link capabilities, so it neither queries nor activates them, as with an RRF that offers none. The RRF on the printer offers them, and the production build always activates them. The file goes with the side-load directory at the end.
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
- Explicit only: the 30-minute soak.
- `AbortsCrossingMacroRequests` (explicit, `Restart`) runs a Python IPC client as root on the SBC, which aborts Telnet with the IPC command `InvalidateChannel`, and sets the DCS log level to debug for about a minute (`M111 P-1 S"debug"`, set back afterwards). `LAB_ABORT_ITERATIONS` sets the number of aborts (default 600).
- `PluginPermissions.WebServerPassesPermissionCheckOfSystemPackages` sends `DELETE /machine/systemPackage` for `labtest_no_such_package`, a name no Debian package can have. With root plugin support, DPS runs `dpkg -r` with it.
- `CodeBufferReserve` keeps Telnet and Aux2 waiting for 12 s with RRF's code buffer full. Without the fix, no trigger macro, including those of the doors and the E-stop input, gets a code to RRF in that time.
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
| `ControlServerRestart.NoReplyOfTheOldConnectionAfterRestart` (explicit) | After a DCS start the journal showed "Daemon: Out-of-order reply ''": RRF wrote the reply of a code of the old connection to the new one (about 18 of 430 starts) | RRF with the channel epoch (RRF #47) |
| `TriggerMacros.TriggerMacroCallingMacro` | Garbled trigger macro names ("Macro file trigger8.g not found") | RRF fork PR #12 and #13 (3.7.0-rc.2+1-mp.3) |
| `TriggerMacros.DeleteFirmwareTriggerWithM581Point1` | `M581.1 T# P-1` does not delete a trigger that RRF evaluates | DSF #61 (upstream still has it) |
| `TriggerMacros.ReportFirmwareTriggerWithM581Point1` | `M581.1 T#` and `M581.1 T# R<n>` were refused without P | DSF #63 (upstream still has it) |
| `TriggerMacros.ReportSbcTriggerWithM581` | A plain `M581 T#` report deleted a trigger that DSF evaluates | DSF #63 (upstream still has it) |
| `TriggerMacros.StringLiteralInExpression` | M581.1 with a string literal in its expression hung RRF 3.7 until its watchdog reset it | RRF 59a04159b (upstream, in 3.7.0+1.mp.1 and later) |
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
| `SpiFaultLostConnection.RrfStopsWhenDsfLosesTheConnectionInATriggerMacro` (explicit) | After a lost connection on the extended link in a transfer with counter value 0, RRF kept the session while DSF dropped the trigger macro (P4) | DSF #138 |
| `SpiFaultInjection` with the plain-link cases of P1 (explicit) | One bit error left the plain link stuck until DCS restarted when RRF answered the header with a header response while DSF had data (P1) | DSF #137 |
| `IntrusiveCodes.M112StaysHaltedUntilM999` (explicit) | M112 completed before the emergency stop reached RRF | DSF #88 |
| `IntrusiveCodes.M111OutputDoesNotCrashDcs` (explicit) | DCS crashed with a stack overflow while `M111 P-1 O1` was active | DSF #70 |
| `IntrusiveCodes.M30AndM472Delete` (explicit) | M30 used the wrong folder for a relative name, M30 and M472 reported success for a missing file | DSF #74, #80 |
| `IntrusiveCodes.M929InvalidLevelReportsAnError` (explicit) | M929 with an invalid log level reported success | DSF #81 |
| `IntrusiveCodes.M606FromHttpOnlyWarns` (explicit) | M606 S1 from another channel than File forked the job reader of DCS | DSF #76 |
| `RefusedMacroPaths.*` | M98 with a path that leaves its volume or names an unknown drive stopped DCS, RRF stayed halted until M999 (G2) | DSF #132 |
| `CodeBufferReserve.TriggerMacroGetsRoomWhileOtherChannelsReadAhead` | Codes read ahead on two waiting channels filled RRF's code buffer, so a trigger macro got no code out (T3) | DSF #133 |
| `AbortsCrossingMacroRequests.ChannelWorksAfterAbortsCrossingMacroRequests` (explicit) | An abort that crossed the macro request of an M98 started a macro RRF does not run and blocked the channel (T2). A block can also come from T1 (RRF) | DSF #134 |
| `PluginPermissions.PluginsStartWithTheirManifestPermissions` | No bug: guards #135, the installed plugins start and keep running with only their manifest permissions | DSF #135 |
| `PluginPermissions.WebServerPassesPermissionCheckOfSystemPackages` | No bug: guards #135, DELETE /machine/systemPackage gets past the permission check | DSF #135 |

## SPI fault injection

`SpiFaultInjection.SingleFaultsMatchCensus` checks the link census of `src/UnitTests/SPI/LinkRecovery.cs` on the real link. The census
writes its cases with the predicted outcome per kind of transfer. `_Extended` is for the extended link, which DCS uses with the RRF on the printer.
`_RrfOfToday` is for the plain link, which needs `--plain-link`:

```
LAB_SPI_FAULT_CASES=/tmp/cases.json dotnet test src/UnitTests --filter "Name=WriteHardwareCases_Extended"   # or _RrfOfToday
LAB_SPI_FAULT_CASES=/tmp/cases.json src/LabPrinterTests/run-lab-tests.sh --side-load --fault-injection --filter "FullyQualifiedName~SpiFaultInjection"
LAB_SPI_FAULT_CASES=/tmp/plain.json src/LabPrinterTests/run-lab-tests.sh --side-load --fault-injection --plain-link --filter "FullyQualifiedName~SpiFaultInjection"
```

- The test fails at once if DCS uses another link than the case file is for. It reads that from the journal of the running DCS (`Lab.GetLinkModeAsync`).
- Each case arms one fault and waits until it hits the next transfer of the requested kind. It then sorts the result as `Retry` (repaired by the protocol), `Reconnect`, `DcsRestart` or `NoRecovery` (no answer to `M409 ... R1` after 2 s), and compares it with the census prediction for the kind of transfer that was hit.
- `LAB_SPI_FAULT_SELECT`: `failures` (predicted to break the link), `responses` (every fault in a response word, where the census finds all failing single bits), or `all`. The default is `failures,responses`. Use `all` for a case file that has been cut down to the cases of interest.
- `LAB_SPI_FAULT_MAX` limits the number of cases.
- `UnitTests` `InjectorMatchesCensus` checks that the injector hits the same exchanges as the census.

A fault can take two options between the direction and the mutation, e.g. `Any Header ToRrf times=1000 counter=0 flip 96`:

- `times=N` corrupts up to N exchanges of the transfer the fault hits first, such as its retries. The injector tells the transfer by the sequence number in the header of DSF, so the fault ends when DSF goes on with another transfer or loses the connection on the extended link.
- `counter=N` hits only a transfer whose header from DSF has an extended sequence number with counter value N, so it never hits on the plain link.

`SpiFaultLostConnection` makes DSF lose the connection on the extended link with such a fault while trigger 30 runs a long macro, and checks that RRF stops. It needs no case file and runs without `--plain-link`:

```
src/LabPrinterTests/run-lab-tests.sh --side-load --fault-injection --filter "FullyQualifiedName~SpiFaultLostConnection"
```

- The fault corrupts each header of DSF on its way to RRF, so DSF gives up after 110 header exchanges (`MaxSbcRetries` of 10 in `SPI.ExchangeHeader` and `SPI.PerformFullTransfer`) and loses the connection.
- Case 0 puts that on counter value 0, where DSF before #138 left the extended link with a sequence number that follows the last one of RRF. Case 16 is the control, which worked before.
- Each case costs an emergency stop of RRF (on the fork also ATX off) and M999. When RRF does not stop, the test reports whether trigger 30 still runs and restarts DCS and sends M999.
- `LinkRecovery.LabFaultLosesConnectionAndRrfStops` checks the fault offline. Keep it in line with the lab test.

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
