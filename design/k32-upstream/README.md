# K32 on upstream code

This folder repeats the check behind finding K32 in `design/dsf-findings-upstream.md`: an emergency stop of DSF that crosses the end of a macro never reaches RRF. It runs two tests against the code of Duet3D/DuetSoftwareFramework `v3.7-dev` f76ffc70, without any change to that code.

## Run it

```
design/k32-upstream/reproduce.sh [directory for the checkout]
```

The script needs git, tar and the .NET SDK that upstream builds with. It:

1. adds a git worktree of f76ffc70 (fetched from GitHub if the repository does not have it),
2. copies the fake firmware and the test `EmergencyStopCrossingMacroEnd` from fork commit 38a601c6 into `src/UnitTests`,
3. applies `fake-firmware-for-upstream.patch`,
4. copies `K32UpstreamHostOutcome.cs` into `src/UnitTests/SPI`,
5. stops if a tracked file of upstream differs, and
6. runs the three tests.

Without an argument the checkout goes to a new temporary directory. The script prints how to remove it.

## Files

| File | Content |
|---|---|
| `reproduce.sh` | The steps above. |
| `fake-firmware-for-upstream.patch` | What the fork's test files need to compile against upstream. `FakeFirmware.cs` and `FakeSbcRequest.cs` lose the link capabilities, and `ResendPacket` loses its third parameter. `EmergencyStopCrossingMacroEnd.cs` loses the test `EmergencyStopWhenTheDataBeforeItCannotBeProcessed`, which needs the fork's SPI constructor. Every change is marked `K32-UPSTREAM`. The test that shows the finding is unchanged. |
| `K32UpstreamHostOutcome.cs` | The same case in a generic host built like `Program.cs` builds it. It is not part of the fork's unit tests: its assertions describe the faulty outcome, so it fails on the fork. |

The fork commit 38a601c6 is the last one before the channel epoch (#167). Later versions of the fake firmware need the epoch, which upstream does not have.

## Result

Run on 2026-11-11 with .NET SDK 10.0.401: 1 failed, 2 passed.

| Test | Result | Meaning |
|---|---|---|
| `EmergencyStopCrossingMacroEnd.EmergencyStopWhileTheClosalOfAMacroIsPending` | Failed: "RRF did not get the emergency stop, transfer thread Faulted Stack underrun on channel HTTP" | The finding. |
| `EmergencyStopCrossingMacroEnd.EmergencyStopWhenRrfClosesAMacroInTheTransferOfTheStop` | Passed | The closal that RRF writes in the transfer of the stop is handled. |
| `K32UpstreamHostOutcome.M112OutcomeInGenericHostWhenTheClosalOfAMacroIsPending` | Passed | Its assertions are the outcome below. |

Output of the third test:

```
released transfers at +82 ms
M112 completed at +83 ms, reply '\n'
transfer thread ended at +85 ms, status Faulted
ApplicationStopping at +85 ms, host run task completed at +92 ms (RanToCompletion)
M98 reply ''
state.status in DCS: Halted
RRF emergency stopped: False, EmergencyStop requests RRF received: 0
requests DSF wrote that never went out: #0 EmergencyStop
```

M112 replies with success, DCS reports the machine as halted, RRF has received no `EmergencyStop`, the packet is left in DSF's transmit buffer, and the host stops.

## Limits

- The firmware side is the fork's fake firmware, not RRF. It models RRF writing the closal of a macro into the transfer before it reads the stop.
- The check is pinned to f76ffc70. Later upstream commits were not run.
- `K32UpstreamHostOutcome` does not start `PeriodicUpdateService` and adds no IPC socket; the rest of the host is built like `Program.cs:121-190` at f76ffc70.
