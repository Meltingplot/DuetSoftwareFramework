# Lab printer tests

Hardware tests that run against the CHX 350 lab printer (Pi 5 + Duet 3 MB6HC, six expansion boards). Each test reproduces a bug seen on that printer, so a DSF or RRF build can be checked before it is released. The project is fork-only and not part of the solution. The unit tests in `src/UnitTests` cover everything that can run without hardware.

## Running

```
src/LabPrinterTests/run-lab-tests.sh [--side-load] [dotnet test arguments]
```

- Needs Tim's hardware token (plink uses gpg-agent as Pageant), PuTTY's `plink.exe` and the `mpadmin` account on the SBC (passwordless sudo).
- The script stops `mp-ota-gate.timer`, so that no update starts, and starts it again at the end.
- `tunnel.py` forwards `127.0.0.1:18080` in WSL to the SBC's HTTP API, with one `plink -nc` per connection. A plink listener on Windows does not work reliably, because Windows may drop connections from WSL to it.
- `--side-load` publishes DCS from this tree and bind-mounts `DuetAPI.dll`, `DuetControlServer.dll` and `DuetSharedLibrary.dll` over the image's copies for the run. It removes them again at the end. Both steps restart DCS and send M999.
- Examples: `--filter "TestCategory!=Restart"` skips the restarts. `--filter "FullyQualifiedName~LinkSoak"` runs the 30-minute soak.
- Without `LAB_PRINTER_URL` and `LAB_PRINTER_SHELL`, which the script sets, every test is skipped.

## What the tests may do

- A test runs only while the printer is idle with no job and every expansion board running. If a job runs when the run starts, nothing is touched.
- Always allowed: codes such as M409, M122, M291/M292, test macros in `0:/macros/lab-tests`, and global variables named `labTest...`. The globals stay until the next reset.
- Category `Restart`: firmware restarts (`M122 P1001`; on the fork, the lost link also triggers an E-stop and switches ATX off), DCS restarts and M999.
- Category `Trigger`: a temporary trigger 30 with `sys/trigger30.g`. These tests are skipped if the machine uses either.
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
| `TriggerMacros.StringLiteralInExpression` (explicit) | M581.1 with a string literal in its expression hangs RRF 3.7 | Open (upstream RRF) |
| `LinkSoak.NoLostConnectionWhileIdle` (explicit) | TfrRdy timeouts every 15 to 30 minutes (DSF 3.7.0-rc.1) | Upstream 46886f5d |

## Not covered

- The end-of-print reply and M32 from a job file (upstream 9acd058f) need a print job.
- Case 2 of #57 (RRF takes the data of a retried transfer) has never been observed and needs fault injection.
- Object model responses that shift after a reconnect have no unit test, because `LinkService` gets them from `ILinkAdapter.ReadObjectModel` as a span, which a proxy adapter cannot supply. On hardware, `FirmwareRestart` compares the object model of DCS with the answers of RRF.
