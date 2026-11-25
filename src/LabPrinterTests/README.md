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
- `--side-load` publishes DCS and DWS from this tree and bind-mounts `DuetAPI.dll`, `DuetControlServer.dll`, `DuetSharedLibrary.dll`, `DuetAPIClient.dll` and `DuetWebServer.dll` over the image's copies for the run. It removes them again at the end. Both steps restart DCS and DWS and send M999.
  - DWS is side-loaded with DCS because the two must match: since upstream 61df011ec the web server, not DCS, refuses to delete a volume root, so a new DCS next to the web server of an image before DSF 3.7.0+mp.15 would let `DELETE /machine/file/0%3A%2F` through.
  - Files that only an image or package changes (systemd units, AppArmor profiles, file capabilities, DPS, package scripts) stay those of the image. Tests of such fixes check the installed state and are skipped when it predates the fix.
- `--fault-injection` side-loads the test-only build that corrupts a single SPI exchange when one is written to `/var/tmp/lab-tests/spi-fault` (`DuetControlServer/Link/Adapter/SpiFaultInjection.cs`). Only the `FaultInjection` tests use it; see below.
- `--plain-link` creates `/var/tmp/lab-tests/spi-plain-link` before DCS starts. The fault injection build then keeps the plain link: it supports no link capabilities, so it neither queries nor activates them, as with an RRF that offers none. The RRF on the printer offers them, and the production build always activates them. The file goes with the side-load directory at the end.
- Examples: `--filter "TestCategory!=Restart"` skips the restarts. `--filter "FullyQualifiedName~LinkSoak"` runs the 30-minute soak.
- `LAB_WATCHDOG_RESTARTS` sets the number of watchdog resets of `FirmwareRestart.LinkRecoversAfterWatchdogReset` (default 5), e.g. 40 to look for a reboot race that hits about one restart in 40.
- `LAB_PRESSURE_SECONDS` sets how long `OutputBufferPressure` keeps RRF's output buffers busy (default 60).
- `LAB_MACRO_CLOSE_MINUTES` sets the duration of `MacroCloseStress` (default 60), e.g. 3 to check its rate. Run it with `--filter "FullyQualifiedName~MacroCloseStress"`.
- `LAB_SZP_RUNS` sets the number of accelerometer runs of `ExpansionBoards.SzpCoilKeepsUpdatingDuringAccelerometerRuns` (default 700).
- `LAB_AUTOMATIC_MODE_WAIT` makes the `JobSimulation` tests wait up to that many seconds for automatic mode instead of skipping. With `--side-load` the mode has to be set after the run started: the side-load restarts DCS, and the M999 that follows runs `config.g`, which leaves automatic mode.
- Without `LAB_PRINTER_URL` and `LAB_PRINTER_SHELL`, which the script sets, every test is skipped.
- **Tests of fixes the printer may not have yet** skip instead of failing, so that a run on an older image or firmware shows no false failures and costs no emergency stop where the bug would end DCS or hang a channel:
  - DSF fixes after 3.7.0+mp.15 (`G3Helpers.RequireControlServerWithNewFixesAsync`, `RequireWebServerWithNewFixesAsync`): the test runs if the service is side-loaded from this tree and runs that file, or if its package is newer. v3.7-dev builds report 3.7.0+mp.15 too until the next version bump.
  - Firmware fixes (`G7Helpers.RequireMainBoardFixes`, `WhyNotFixed`): the test runs if the board reports a fork release with the fix. A test build such as 3.7.0+1.mp.12-dev.2 counts as older than 3.7.0+1.mp.12, so `LAB_ASSUME_FIXES` names the fixes it contains, e.g. `LAB_ASSUME_FIXES="RRF#102 RRF#104 RRF#112"`.

## What the tests may do

- A test runs only while the printer is idle with no job and every expansion board running. If a job runs when the run starts, nothing is touched.
- Always allowed: codes such as M409, M122, M291/M292, test macros in `0:/macros/lab-tests`, and global variables named `labTest...`. The globals stay until the next reset.
- Category `Restart`: firmware restarts (`M122 P1001`; on the fork, the lost link also triggers an E-stop and switches ATX off), DCS restarts and M999.
- Category `Trigger`: a temporary trigger 30 with `sys/trigger30.g`. These tests are skipped if the machine uses either.
- Category `FaultInjection` (explicit): single corrupted SPI exchanges. A fault that breaks the link costs a DCS restart and M999, and on the fork the lost link also triggers an E-stop and switches ATX off.
- Category `Approval` (explicit): `IntrusiveCodes`, codes that change the state of the machine, such as M112, M929 or a test file in `0:/gcodes`. Tim decides on each run, see `design/lab-test-plan.md`. Run one with `--filter "FullyQualifiedName~IntrusiveCodes.M112StaysHaltedUntilM999"`. Also in this category:
  - `WebServer.WebServerComesBackAfterAKill`: SIGKILL of DWS, 5 to 15 s without the web interface, every HTTP session is lost.
  - `ControlServerStartup.SecondInstanceReportsSettingsErrors`: a second DCS as dsf under `unshare --mount` with bad settings in `/var/tmp/lab-tests/dcs-probe`; the running DCS is checked to be untouched.
  - `PluginServices.StoppedPluginLeavesNoChildProcesses`: stops and starts one plugin that has child processes.
  - `ExpansionBoards.RestartDuringAccelerometerRunKeepsLinkAndResetData` (also `Restart`): one watchdog reset of the SZP during an accelerometer run, then a DCS restart and M999, one E-stop.
  - `ExpansionBoards.SzpCoilKeepsUpdatingDuringAccelerometerRuns`: 700 accelerometer runs of the SZP, about 17 minutes, into a file in `0:/sys/accelerometer` that the test deletes.
  - `JobSimulation`: test files in `0:/gcodes/lab-tests` run as M37 simulations only, never with M32. On the CHX, `sys/start.g` returns before it heats or moves when simulating, and only in automatic mode, which the tests require; RRF runs no `stop.g` after a simulation. The doors must stay closed: at the start RRF resets the extruder positions, which `daemon.g` sees as motion for 0.25 s, and a door opened then triggers M112. An event macro that pauses the job (heater, driver, filament or expansion faults) would run `pause.g`, which retracts.
  - `SystemTime`: steps the SBC clock by -60 s, or forward by at least 60 s, and restores it. systemd-timesyncd is stopped meanwhile, because it undoes a step within milliseconds, and started again.
  - `RootDeletion`: non-recursive delete requests that name `0:/`. Each request form must first delete an empty test folder and keep a non-empty one, each alias of the root must list exactly what `0:/` lists, and `0:/` and `sys/config.g` are checked after every request; the test stops at the first change.
  - `FullDisk`: points `sys/config-override.g` or the event log at `/dev/full` or a small tmpfs. `config-override.g` is never deleted or written: it is backed up with its checksum to `/run/lab-sbc-files`, renamed aside and back as dsf, and the checksum is checked afterwards.
  - `IntrusiveCodes.M997*`: M997 with a 100-byte UF2 file and with `S0:4` and a missing file; both are refused before anything is flashed. `IntrusiveCodes.RunOnceRunsAfterConfig` (also `Restart`) writes `0:/sys/runonce.g` and sends M999.
- Explicit only: the 30-minute soak and the 60-minute `MacroCloseStress` (category `Trigger`).
- `MacroCloseStress` runs trigger 30 up to five times a second, and its macro nests four more. DCS logs two lines for each macro on the trigger channel, so the journal gets about 50 lines a second for an hour. The machine's own triggers keep priority (see the test).
- `AbortsCrossingMacroRequests` (explicit, `Restart`) runs a Python IPC client as root on the SBC, which aborts Telnet with the IPC command `InvalidateChannel`, and sets the DCS log level to debug for about a minute (`M111 P-1 S"debug"`, set back afterwards). `LAB_ABORT_ITERATIONS` sets the number of aborts (default 600).
- `PluginPermissions.WebServerPassesPermissionCheckOfSystemPackages` sends `DELETE /machine/systemPackage` for `labtest_no_such_package`, a name no Debian package can have. With root plugin support, DPS runs `dpkg -r` with it.
- `ControlServerCapabilities.FileReadsHaveTheRightsOfTheDsfUser` creates `/var/tmp/lab-capabilities` as root with two small files, one of them readable only by root, reads both with M36.2 and removes the directory again.
- Root Python IPC clients on the SBC, as in `AbortsCrossingMacroRequests`, also run in `CodeLengths`, `IpcCommands`, `TelnetCodes`, `PluginPermissions`, `JournalPriorities`, `WebServer`, `CodeInterception`, `ObjectModelLifetime` and `ControlServerRestart`. `WebServer.PluginRequestEndsWhenTheClientAborts` registers the endpoint `labtest/stall` for a few seconds and removes it again.
- `TelnetCodes.M20MarlinHeader` switches Telnet to Marlin mode with `M555 P2` if it is in another mode and restores the mode afterwards.
- `ObjectModelLifetime.DeletingCommandsDoNotStallAssignments` opens and closes non-blocking message boxes (`M291 S1`) for about a minute, which flicker on the HMI.
- `ExpressionEvaluation.DeeplyNestedExpressionIsRefused` sends codes of about 400 KB, and each refusal logs a long stack trace.
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
| `FirmwareRestart.LinkRecoversAfterWatchdogReset` | The link never recovered after RRF restarted (DSF mp.8 and upstream, 4 of 5 restarts). Also checks object model answers after the reconnect, one reset report and one exit of the extended link per reset, the time from the last full transfer restart to the reset, and the main board file names after the reset. #41 is caught in about 12 % of runs of 5 resets; #92b, #93b and #59 only if they happen to occur | DSF #59, #92, #93, #115; RRF fork PR #36, #41 |
| `ControlServerRestart.PluginServicesStartWithControlServer` | The plugin services stayed down after DCS was stopped and started. Also checks that the stop takes less than 3 s with a box open and M122 running, and that RRF is halted with ATX off and its up time going on afterwards | DSF #37 (mp.2), #149, #154; RRF fork PR #20, #21, #23 |
| `ControlServerRestart.ResetDoesNotHangShutdown` | DCS hung in its shutdown for 15 s after M999. Checks the stop time, the reply of a code from a second connection and that the plugin services restart with DCS | DSF #149, #151 |
| `ControlServerRestart.NoReplyOfTheOldConnectionAfterRestart` (explicit) | After a DCS start the journal showed "Daemon: Out-of-order reply ''": RRF wrote the reply of a code of the old connection to the new one (about 18 of 430 starts) | RRF with the channel epoch (RRF #47) |
| `TriggerMacros.TriggerMacroCallingMacro` | Garbled trigger macro names ("Macro file trigger8.g not found") | RRF fork PR #12 and #13 (3.7.0-rc.2+1-mp.3) |
| `TriggerMacros.DeleteFirmwareTriggerWithM581Point1` | `M581.1 T# P-1` does not delete a trigger that RRF evaluates | DSF #61 (upstream still has it) |
| `TriggerMacros.ReportFirmwareTriggerWithM581Point1` | `M581.1 T#` and `M581.1 T# R<n>` were refused without P | DSF #63 (upstream still has it) |
| `TriggerMacros.ReportSbcTriggerWithM581` | A plain `M581 T#` report deleted a trigger that DSF evaluates | DSF #63 (upstream still has it) |
| `TriggerMacros.StringLiteralInExpression` | M581.1 with a string literal in its expression hung RRF 3.7 until its watchdog reset it | RRF 59a04159b (upstream, in 3.7.0+1.mp.1 and later) |
| `MacrosOnOtherChannels.TriggerKeepsFiringWhileMacroRuns` | No bug: M98.1 starts a macro on Aux, HTTP, Telnet or Aux2 from a trigger macro that checks `inputs[]` first, and the trigger keeps firing | New in M98.n (#108), M98.1 S |
| `MacrosOnOtherChannels.CallIsRefused` | No bug: calls M98.1 refuses, including USB and USB2 | New in M98.n (#108), M98.1 S |
| `SelfTestAutostart.FlowStartsAfterBootAndCodesGetAnswers` | No bug: the machine starts its self-test with M98.1 S0 (formerly M98.3) after a boot, and M409 from a client gets answers meanwhile | New in M98.n |
| `TriggerMacros.RrfExpressionReplacesSbcTrigger` | An M581.1 expression that RRF evaluates left the old trigger running in DSF | DSF #67 (upstream still has it) |
| `TriggerMacros.DisabledSbcTriggerDoesNotFire` | A trigger that DSF evaluates fired with `R-1` while not printing | DSF #63 (upstream still has it) |
| `LineChecksums.*` | No bug: a wrong CRC, a missing CRC or a wrong line number ends a macro | New in the line checksums |
| `CodeReplies.M36MissingFileIsValidJson` | M36 replied with invalid JSON for a missing file | DSF #72 |
| `CodeReplies.M39ReportsSizesAndClusterSize` | M39 reported sizes in "Gb" and no cluster size; the cluster size is compared with `stat -f` | DSF #82 |
| `ControlServerCapabilities.*` | DCS had CAP_DAC_READ_SEARCH in its effective set, so M36.2 returned files only root may read from any volume, e.g. the SSH host keys of the `/etc/ssh` mount. Needs the DSF package with the fix: a side-load keeps the file capabilities of the installed one, so both tests skip on a package up to 3.7.0+mp.15 | DSF #232 |
| `MacroCloseStress.NoResetWhileMacrosCloseOnTheTriggerChannel` (explicit) | RRF reset while idle with a MemoryProtectionFault in `Heap::DeleteSlot` just after a trigger macro closed, about once per 40,000 closes (RRF 3.7.0+1.mp.9 and older, upstream code). Also checks the journal for garbled macro requests, 6 to 16 per hour if #12 regressed | RRF fork PR #50 (test build 3.7.0+1.mp.10-dev.1), #12, #13 |
| `OutputBufferPressure.ObjectModelKeepsUpdatingUnderOutputBufferPressure` | A global too long to report stopped the object model of DSF: RRF treated the overflow of its output buffers as "try again", DSF resent `GetObjectModel` forever. The fix waits for 36 free buffers, so the test checks that the model keeps up while M409 R1 and M122 on three other channels keep the buffers busy and a global brings the response for `global` close to a full transfer | RRF fork PR #113 (test build 3.7.0+1.mp.12-dev.1) |
| `VariableCopies.*` | A variable kept a raw pointer into the object model for an array of objects, a string or a port. On the CHX, `sys/daemon.g:37` keeps the filament name of extruder 0 and never saw a change. The tests use the text of a box of their own, the heaters and the port of a temperature sensor instead | RRF fork PR #57 (test build 3.7.0+1.mp.11-dev.1) |
| `LinkSoak.NoLostConnectionWhileIdle` (explicit) | TfrRdy timeouts every 15 to 30 minutes (DSF 3.7.0-rc.1) | Upstream 46886f5d |
| `SpiFaultRestarts.StuckLinkRestartsDcs` (explicit) | A stuck link needed a manual restart of DCS | DSF #102 |
| `SpiFaultRestarts.DcsStartsWhileTfrRdyIsLow` (explicit) | A new DCS failed its first start when a recovered fault left TfrRdy low | DSF #99 |
| `SpiFaultLostConnection.RrfStopsWhenDsfLosesTheConnectionInATriggerMacro` (explicit) | After a lost connection on the extended link in a transfer with counter value 0, RRF kept the session while DSF dropped the trigger macro (P4) | DSF #138 |
| `SpiFaultInjection.PlainLinkHeaderResponseFaultsRecover` (explicit, `--plain-link`) | One bit error in RRF's response to DSF's header left the plain link stuck until DCS restarted (P1), or was read as a refusal and stopped DCS | DSF #137, #91 |
| `SpiFaultInjection.SingleFaultsMatchCensus` (explicit) | Single faults broke the link where the lab test plan requires a repair by retries; the comparison with the census alone passed a regression of DSF | DSF #137, #91 |
| `SpiFaultRetries.BadRrfDataTakesOneDataRetry` (explicit) | One bit error in RRF's data cost a full transfer restart, two on the extended link (ESD test 2026-11-03) | DSF #105 |
| `SpiFaultRetries.NineBadDataExchangesKeepTheConnection` (explicit) | Bit errors in more than 3 attempts of one transfer lost the connection and RRF stopped | DSF #98 with #105 |
| `SpiFaultRetries.RefusalWordInHeaderResponseIsRetried` (explicit) | A header response that read as BadFormat, BadProtocolVersion or BadDataLength stopped DCS | DSF #91 |
| `SpiFaultRestarts.StopWhileTheLinkIsStuck` (explicit) | Stopping DCS while the SPI link was stuck hung 15 s until systemd killed it | DSF #149 |
| `IntrusiveCodes.M112StaysHaltedUntilM999` (explicit) | M112 completed before the emergency stop reached RRF | DSF #88 |
| `IntrusiveCodes.M111OutputDoesNotCrashDcs` (explicit) | DCS crashed with a stack overflow while `M111 P-1 O1` was active | DSF #70 |
| `IntrusiveCodes.M30AndM472Delete` (explicit) | M30 used the wrong folder for a relative name, M30 and M472 reported success for a missing file | DSF #74, #80 |
| `IntrusiveCodes.M929InvalidLevelReportsAnError` (explicit) | M929 with an invalid log level reported success | DSF #81 |
| `IntrusiveCodes.M606FromHttpOnlyWarns` (explicit) | M606 S1 from another channel than File forked the job reader of DCS | DSF #76 |
| `RefusedMacroPaths.*` | M98 with a path that leaves its volume or names an unknown drive stopped DCS, RRF stayed halted until M999 (G2). Also checks that the refused path is logged with priority error | DSF #132, #118 |
| `CodeBufferReserve.TriggerMacroGetsRoomWhileOtherChannelsReadAhead` | Codes read ahead on two waiting channels filled RRF's code buffer, so a trigger macro got no code out (T3) | DSF #133 |
| `AbortsCrossingMacroRequests.ChannelWorksAfterAbortsCrossingMacroRequests` (explicit) | An abort that crossed the macro request of an M98 started a macro RRF does not run and blocked the channel (T2). A block can also come from T1 (RRF) | DSF #134 |
| `PluginPermissions.PluginsStartWithTheirManifestPermissions` | No bug: guards #135, the installed plugins start and keep running with only their manifest permissions | DSF #135 |
| `PluginPermissions.WebServerPassesPermissionCheckOfSystemPackages` | No bug: guards #135, DELETE /machine/systemPackage gets past the permission check | DSF #135 |
| `CodeLengths.MacroCodePositionsAndLengths` | Code lengths counted the next code's first character or the end of the file (a paused job resumed one byte into the next code) | DSF #86 |
| `IpcCommands.InvalidateChannelIsAccepted` | DCS refused the IPC command InvalidateChannel | DSF #158 |
| `TelnetCodes.M20MarlinHeader` | M20 in Marlin mode started with "Begin file list:" | DSF #73 |
| `PluginPermissions.ExternalProgramIsRefusedPackageCommands` | Every IPC client got every command, so an external program could run package commands | DSF #135 |
| `JournalPriorities.LinesHaveTheirPriorityAndNoColors` | DCS wrote every journal line with priority 6 and color codes | DSF #118 |
| `Packaging.DsfFilesAreNotOwnedByTheAdminUser` | DSF package files were owned by UID 1001 (mpadmin on the CHX) | DSF #30 |
| `Packaging.RootPluginServiceSettingsBelongToRoot` | dsf could change the settings of the root plugin service, which ran in /opt/dsf/plugins. Fails on the CHX even with the fixed package until the image stops giving /opt/dsf/conf to dsf (`mp-dsf-configure`) | DSF #234 |
| `WebServer.FailedRrUploadKeepsTheFile` | A failed rr_upload deleted the file it was to replace, and a good one wrote it in place | DSF #222 |
| `WebServer.PluginRequestEndsWhenTheClientAborts` | A plugin endpoint request that the plugin never answered did not end when the client aborted it | DSF #229 |
| `WebServer.WebServerUnitRestartsIt` | systemd did not restart DWS after it ended | DSF #225 |
| `WebServer.WebServerComesBackAfterAKill` (explicit, Approval) | Same as above, checked by killing DWS | DSF #225 |
| `FileInfoReplies.FilamentIndexIsBounded` | A large IdeaMaker filament index made DCS grow the filament list to that index | DSF #219 |
| `FileInfoReplies.NonFiniteValuesKeepFileInfoReadable` | A Z or filament value beyond the float range made M36 reply err 1 | DSF #223 |
| `FileInfoReplies.DeeplyNestedCustomInfoIsSkipped` | A deeply nested customInfo value ended DCS with a stack overflow | DSF #218 |
| `CodeReplies.M36ReadsFooterWithTwoCodesOnOneLine` | A footer line with two codes made M36 fail | DSF #84 |
| `MacroErrors.CodesBeforeUnreadableLineStillRun` | Codes before an unreadable macro line were cancelled | DSF #204 |
| `MacroErrors.CodeThatFailsInDsfStopsTheMacro` | Codes after a failed macro code still ran | DSF #204 |
| `TriggerMacros.SbcTriggerConfiguredFromMacro` | M581.1 with an SBC field hung in a macro | DSF #216 |
| `TriggerMacros.TriggerAfterM99GetsItsMacro` | The next trigger macro was requested before the abort of the last one ("Discarding request", "trigger30.g not found") | RRF fork PR #13 (part 1), #12 in about 20 % of runs |
| `TriggerMacros.ActionCallingFileFunctionIsRefused` | Trigger actions calling fileexists() or fileread() depended on DSF | RRF fork PR #115 |
| `UnbufferedCodes.M409R1DuringNestedMacro`, `M409R1DuringSingleMacro` | A forwarded M409 deadlocked a macro on the same channel | DSF #51 (upstream 30ea08be) |
| `CodeInterception.ExecutedInterceptorWithAutoFlushGetsItsCode` | An Executed interceptor with AutoFlush blocked its channel for good | DSF #181 |
| `ObjectModelLifetime.DeletingCommandsDoNotStallAssignments` | No bug seen: M291/M292 against SBC expressions and assignments guard the new lifetime lock; the use-after-free itself cannot be forced | RRF fork PR #70 |
| `ExpressionEvaluation.ParameterOutOfRangeIsAnError` | `M20 S31e9` ended with an OverflowException (HTTP 500) instead of an error reply | DSF #162 |
| `ExpressionEvaluation.DeeplyNestedExpressionIsRefused` | An expression nested too deeply ended DCS with a stack overflow | DSF #218 |
| `ExpressionEvaluation.DateTimeStringsGoToRrf` | datetime() of strings that RRF accepts was refused by DSF ("can't convert value to DateTime") | DSF #238 (month 99 also RRF fork PR #104) |
| `ExpressionEvaluation.TwoToThe31IsAFloat` | floor(), ceil(), round() and pow() gave the int 2147483647 instead of the float 2147483648 for 2^31 | DSF #249 |
| `ExpressionEvaluation.OversizedArraysAndGlobalsAreRefused` | Values whose JSON cannot come back over the link reached RRF when DSF could evaluate them | DSF #239 with RRF fork PR #114 |
| `ExpressionEvaluation.FileExistsLooksInTheSystemDirectory` | A relative fileexists() looked in 0:/ instead of the system directory | DSF #240 |
| `ExpressionEvaluation.DateBeyondYear9999IsAnError` | A date and time beyond the year 9999 from RRF stopped DCS instead of failing the code | DSF #250 |
| `VariableAnswers.NonFiniteResultsDoNotStopDcs` | NaN, ±inf or floats beyond decimal from RRF stopped DCS | DSF #11 |
| `VariableAnswers.GlobalTooLongToReportIsDiscardedOnce` | A global too long to report stopped DSF's object model (GetObjectModel resent for ever) | RRF fork PR #113 |
| `VariableAnswers.GlobalWhoseAnswerDoesNotFitIsRefused` | A global or echo whose answer exceeds a transfer was resent: "already exists", hung channel | RRF fork PR #114 |
| `RrfExpressions.DateTimeRefusesMonthOutsideOneToTwelve` | datetime() accepted month 0, 13 or 99 and read outside the days-before-month table | RRF fork PR #104 |
| `RrfExpressions.DateTimeRefusesYearOutsideMktimeRange` | datetime() took years outside the range of mktime and looped once per year | RRF fork PR #109 |
| `RrfExpressions.RoundingOfTwoToTheThirtyOneStaysFloat` | floor/ceil/round/pow of 2^31 gave the int 2147483647 instead of the float | RRF fork PR #110 |
| `RrfExpressions.VectorTooLargeIsRefused` | vector() with 2^29 elements wrapped its size and wrote beyond the heap | RRF fork PR #102 |
| `RrfExpressions.TakeAndDropOfNestedArrayUseTheirParent` | take()/drop() of move.axes[n].drivers gave axis 0's drivers plus an internal error | RRF fork PR #108 |
| `RrfExpressions.TakeOfHeapStringSurvivesGarbageCollection` | take()/drop() of a heap string returned wrong text after a garbage collection. Statistical, see the test | RRF fork PR #106 |
| `RrfExpressions.IntMinGoesThroughTheObjectModelJson` | INT_MIN was printed with a signed overflow. Smoke test only, it cannot fail on the old build | RRFLibraries PR #3 |
| `ObjectModelSeqs.VolumesSeqFollowsDsf` | seqs.volumes never changed when DSF reported a change of its volumes | RRF fork PR #105 |
| `ObjectModelSerialization.GlobalWithInvalidUtf8KeepsTheModelServable` | A global with invalid UTF-8 made the model unserializable: new clients (DWC reload) got no model | DSF #185 |
| `ControlServerStartup.SecondInstanceReportsSettingsErrors` (explicit, Approval) | A DCS that could not start did not report why: wrong GPIO chip, an error file it could not write, settings it could not read | DSF #150 |
| `PluginServices.StoppedPluginLeavesNoChildProcesses` (explicit, Approval) | A stopped plugin left its child processes running | DSF #151 |
| `ExpansionBoards.M915RefusesDriverTheBoardDoesNotHave` | An M915 report of a driver an expansion board does not have was answered as if it existed; on a closed-loop board, after R1, it read beyond `dms[]` | Duet3Expansion PR #32 |
| `ExpansionBoards.Samc21SystemStackIs800Bytes` | The SAMC21 system stack grew to 3200 bytes, 2400 bytes less heap on TOOL1LC and SZP | Duet3Expansion PR #11 |
| `ExpansionBoards.RestartDuringAccelerometerRunKeepsLinkAndResetData` (explicit, Approval, Restart) | A board restart during M956 stalled CAN input and SBC transfers for up to 2 × 3 s; SAMC21 reset data could be cut short (the slot that shows it comes up in one run of three) | RRF fork PR #112, Duet3Expansion PR #30 |
| `ExpansionBoards.SzpCoilKeepsUpdatingDuringAccelerometerRuns` (explicit, Approval) | The SZP coil reading froze with state ok during M956 runs, about 1.4 % per run, until `M999 B60` | CoreN2G PR #2 |
| `JobSimulation.CodesBeforeUnreadableLineStillRun` (explicit, Approval) | A line that could not be read in a job (unexpected else) cancelled the codes read ahead before it; no release had it, upstream d108e9767 brought it | DSF 91555b8fc (#212) |
| `JobSimulation.LongJobPathIsRefused` (explicit, Approval) | RRF cut a job path over 120 bytes without an error | DSF #219 |
| `JobSimulation.HugeJobFileIsRefused` (explicit, Approval) | Files of 4 GiB or more were printed although file positions are 32 bit | DSF #219 |
| `JobSimulation.LongSlicerCommentIsCut` (explicit, Approval) | A slicer comment over 254 bytes stopped DCS at job start | DSF #219 |
| `JobSimulation.SimulatedTimeWriteFailureEndsOnlySimulation` (explicit, Approval) | DCS stopped when M37 could not write the simulated time back | DSF #221 |
| `SystemTime.BackwardClockStepReachesFirmware` (explicit, Approval) | A backward step of the SBC clock never reached RRF | DSF #215 |
| `SystemTime.SessionSurvivesClockStep` (explicit, Approval) | A forward clock step expired HTTP sessions without WebSocket | DSF #215 |
| `RootDeletion.*` (explicit, Approval) | Delete requests naming `0:/` deleted the whole SD card (H1) | DSF #136 (mp.14), upstream 61df011ec (mp.15) |
| `FullDisk.M500ReportsAFullDisk` (explicit, Approval) | M500 reported success on a full disk and left config-override.g empty | DSF #226 |
| `FullDisk.M929StopsLoggingOnAFullDisk` (explicit, Approval) | A failed event log write stopped DCS or became the reply of a code | DSF #227 |
| `IntrusiveCodes.M505Point1IsRelativeToTheCurrentFolder` (explicit, Approval) | A relative M505.1 folder was resolved against 0:/www | DSF #75 |
| `IntrusiveCodes.M997RefusesAnInvalidUf2File` (explicit, Approval) | M997 of an invalid UF2 stopped plugins and restarted DCS | DSF #197 |
| `IntrusiveCodes.M997RefusesModule0WithOtherModules` (explicit, Approval) | M997 S0:4 dropped module 4 | DSF #148 |
| `IntrusiveCodes.RunOnceRunsAfterConfig` (explicit, Approval, Restart) | runonce.g never ran in SBC mode on 3.7 | DSF #224 |

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
- DSF #59: a fault that the fix repairs recovers without it too in the lab; it needs a census case first.
- The job file cases of DSF #204 and #216 and the running-job case of #222 need a print job; M37 does not reach them, because DSF and RRF ignore M581/M581.1 from a simulated file.
- The DoCode path of DSF #220, #237 and #242 (a code that RRF passes to DSF fails there) needs USB or PanelDue, which the CHX lacks; trigger actions no longer reach it since RRF fork PR #115.
- The array parser part of RRF fork PR #108 (M584 and other codes with array parameters) would change the machine configuration.
- Duet3Expansion PR #2, #6, #24 and #29 need motion, extrusion or heaters. CoreN2G PR #4 needs a USB cable to the SBC. CoreN2G PR #6 would need the board's flash erased, which the lab tests never do.
- Races without a safe observable on the machine are left to host tests: CoreN2G PR #5 and #7, Duet3Expansion PR #20, RRFLibraries PR #2, FreeRTOS PR #1.
- Object model responses that shift after a reconnect have no unit test, because `LinkService` gets them from `ILinkAdapter.ReadObjectModel` as a span, which a proxy adapter cannot supply. On hardware, `FirmwareRestart` compares the object model of DCS with the answers of RRF.
