# DSF findings for upstream

Status: 2026-11-08, K32 added on 2026-11-11, the privilege boundaries of the DSF census (UB5 to LX6, own section with its own base commit) on 2026-11-24, written for Christian Hammacher (Duet3D). The SPI link findings are in `design/sbc-link-findings-upstream.md`; this document covers everything else that the fork found and fixed on 2026-11-07 and 2026-11-08.

What was checked:
- DSF `v3.7-dev` at 973838bf, the head of upstream on 2026-11-08. The fork's `v3.7-dev` contains it (merge base 973838bf).
- RRF `3.7-dev` at 25ec27aec, the head of upstream on 2026-11-08.
- DuetIAP `3.7-dev` at 9af72da4.

Line numbers refer to these commits. PR numbers are those of Meltingplot/DuetSoftwareFramework unless they say RRF (Meltingplot/RepRapFirmware). Everything below is about upstream behaviour.

## How the origin was checked

- **Blame:** for every finding, `git blame` of the faulty lines at the parent of the fork's fix names commits that are in `upstream/v3.7-dev` (`git merge-base --is-ancestor`), all by Christian Hammacher except the postinst lines of PS8 (Manuel, 335e8ce2). In RRF the lines are by Christian Hammacher, except the root folder check of H1 (David Crocker, 73d1509f6).
- **Whole files:** `git diff upstream/v3.7-dev <parent of the fix>` is empty for `src/DuetPluginService`, `DuetControlServer/Commands/Plugins`, `Utility/Firmware.cs`, `Utility/FirmwareUpdater.cs`, `Program.cs`, `Model/PeriodicUpdateService.cs`, `DuetSharedLibrary/AsyncLock.cs`, `Utility/Logger.cs` and `Codes/Pipelines/Executed.cs`.
- **RRF:** the lines of T1 are the same in the fork's 3.7.0+1.mp.8 and upstream, and `MassStorage.cpp` is identical.
- **Commit column:** it names the commit the faulty line comes from. 4a576390 ("Added DI to DCS") often only moved older code; G2 and T2 are already in v3.6 (`SPI/Channel/Processor.cs:1216`, `:784`).
- **Fork code:** items that turned out to be fork code are listed at the end.

## How we tested

- **Offline:** the fork's unit tests in `src/UnitTests`.
  - The fake firmware host runs the real DCS services against a fake RRF.
  - `DcsHost` runs `Program.cs` with the generic host.
  - The DPS tests run the real commands with fake system tools.
  - The firmware update tests emulate DuetIAP 3.7-dev.
  - Every fix PR turned its reproducers into regular tests and states that they fail without the fix.
- **Hardware:** the lab printer, a Duet 3 MB6HC with a Pi 5, DSF 3.7.0+mp.13 and RRF 3.7.0+1.mp.8 (fork builds).
  - At the lines of G2 and T3 the DSF code of that build equals upstream (blame at tag `v3.7.0+mp.13`). The files of ST3 are identical to upstream in 3.7.0+mp.12.
  - "Printer" marks what was seen there. Everything else comes from the tests and from reading the code.

## Summary

| | Finding and effect | Upstream origin | Fork fix |
|---|---|---|---|
| G2 | `M98` with a path that `FilePathResolver` refuses stops DCS. RRF stays halted until M999. Printer. | 4c910baa | #132 |
| G12 | A float parameter that does not fit an int, uint or long (e.g. `M20 S31e9`) throws an OverflowException instead of an error reply. | 8434050b | #162 |
| T3 | Codes buffered ahead on all channels can fill RRF's whole code buffer, so the codes of a trigger macro (trigger2.g: M112) wait. Printer. | 82d581d1 | #133 |
| T2 | A macro request that crosses an InvalidateChannel is started by DSF. The channel stays blocked until DCS restarts. | 4a576390 | #134 |
| T1 | **RRF:** InvalidateChannel ends a macro wait but still sends the request. DSF runs a macro that RRF does not, and every later trigger is discarded until DCS restarts. | RRF 8207b22cc | RRF #43 |
| T12 | An abort that crosses the closal of a macro underruns DSF's stack, and the exception stops DCS. Printer. | 4a576390, RRF 8207b22cc | #158, RRF #45 |
| T11 | When RRF asks for an InvalidateChannel again, the copy drops the codes DSF sent after the first one, or ends macro waits that started in between. The channel stays blocked. Printer. | 4a576390, RRF 8207b22cc, c5af8416b | #161 |
| T10 | **RRF:** after an InvalidateChannel, RRF replies to the code it was running, and DSF gives that reply to the next code. Printer. | RRF 8207b22cc | RRF #46, #163 |
| K32 | An emergency stop of DSF that crosses the end of a macro never reaches RRF. M112 reports success, and DCS stops. Checked at f76ffc70. | 4a576390, d027f15b | #170 |
| I1 | `RequiredPermissionsAttribute.Check` is true for every command, so plugins get every IPC command and connection mode. | c42f948b | #135 |
| I8 | The IPC command `InvalidateChannel` is refused as unsupported, so the DuetAPIClient calls for it always fail. | 6dd3b8a3 | #158 |
| H1 | A delete that names the SD root deletes the whole virtual SD card. | 75eb6536, RRF 600857f87 | #136, RRF #44 |
| F3 | One bit error in RRF's header response leaves the plain link stuck until DCS restarts. A case of F3 in the SBC link findings. Printer. | 4a576390, ed7c3ab4 | #137 |
| U1 | M997 flashes a `.UF2` file without unpacking it. | 5810dfde | #148 |
| U2 | When the IAP program stops answering, M997 reports nothing and DCS does not stop. | 5810dfde | #148 |
| U3 | M997 flashes an empty firmware file. The IAP program erases the main board's flash first. | c5e3214b | #148 |
| U4 | `DuetControlServer -u` updates a board whose firmware file is missing. | 4a576390 | #148 |
| U5 | One unreadable firmware file stops `-u` for every board. | 4a576390 | #148 |
| U6 | `-u` drops the error of M997 B, waits 3 minutes and resets the main board anyway. | 4a576390, d5782a22 | #148 |
| U7 | M997 S0:4 updates module 0 and drops the others silently. | 4a576390 | #148 |
| ST1 | A transfer that does not complete keeps DCS from stopping until systemd kills it. Printer. | 2cbc04ae | #149 |
| ST2 | A code that a client waits for when DCS stops is never completed. | c5e3214b | #149 |
| ST3 | DSF's AsyncLock refuses a free lock for a cancelled token, so codes in the Executed stage fail while DCS stops and are never completed. Printer. | bb5edd5e | #149 |
| ST4 | The host waits 30 s for its services, systemd kills DCS after 15 s. | c5e3214b | #149 |
| SU1 | `"logLevel"` (lower case) in config.json stops DCS from starting. | 04271d02 | #150 |
| SU2 | A config.json that DCS restores holds the command-line values of that start, e.g. `LogLevel error` after `-u`. | 4c910baa | #150 |
| SU3 | Settings on the command line (`--Key=value`) are refused. | 9ca5be78 | #150 |
| SU4 | An invalid config.json exits with 71 instead of 78. | 273126d1 | #150 |
| SU5 | A start failure exits with 1 when the start error file cannot be written. | 2f4b5c26 | #150 |
| SU6 | DCS does not start without a plugin directory. | 491abf34 | #150 |
| PS1 | Root plugins are loaded and started without RootPluginSupport. Only the installation checks it. | 491abf34 | #151 |
| PS2 | The reserved ServicePlugins permission is refused only at installation, not in a manifest on disk. | 491abf34 | #151 |
| PS3 | DPS deletes the whole plugin directory for a manifest without id. | 3b17835f | #151 |
| PS4 | Uninstall deletes the files a manifest lists without containment, as root for root plugins. | 42a87449, 18cb4cdb | #151 |
| PS5 | A plugin upgrade writes through symlinks. | 3b17835f, 561d5c2c | #151 |
| PS6 | Shell commands in the arguments of a Python plugin are run by bash. | 3b17835f, 561d5c2c | #151 |
| PS7 | An absolute executable or a symlinked parent directory gets past the symlink check of StartPlugin. | 31b8e3d8, 561d5c2c | #151 |
| PS8 | Root plugin files belong to dsf, so the dsf user can change what the root service runs. | 335e8ce2 (postinst) | #151 (design) |
| PS9 | Smaller plugin service bugs: failed process report, closed DCS connection, child processes, missing manifest, registration leak, log. | see section | #151 |
| PS10 | Package installation of a plugin fails when DPS has inherited a variable of `InstallPackageEnvironment`. | 3b17835f | #153 |
| UB5 | The root DPS unpacks a combined system package into a directory in /tmp whose name is known during the upload, and installs whatever it finds there as root. | 3b17835f | #231 |
| FS4 | The root DPS runs commands from `plugins.json` and loads profiles from `apparmor.conf`, which the packages give to dsf. | 335e8ce2, e2bc260a, d828b052 | #234 |
| LX2 | The root DPS runs in `/opt/dsf/plugins`, where dsf may create the `appsettings.json` it reads and the `venv.py`/`pip.py` Python imports as root. | 42a87449 | #234 |
| FS5 | DCS has CAP_DAC_READ_SEARCH effective, so M36.2 and the other file codes return files only root may read. | 561d5c2c, 77c3c748 | #232 |
| LX1 | Python plugins with dependencies run without their AppArmor profile. | e2bc260a, b28b25e7, 1f187df2 | #233 |
| LX5 | Every plugin may create, replace and delete every file in /run/dsf, `dcs.sock` included. Partly fixed. | e2bc260a | #233 |
| LX6 | Root follows symlinks of dsf in gen-https-cert.sh, the `http.json` write of M586 and the reprapfirmware, duetwebserver and duetpimanagementplugin package scripts. | 0234537f, bd0c9151, 278bcc00, 8852e819, 78c784a3 | #235 |

H13 (`rr_delete` ignored `recursive=yes`) was fixed upstream in 5130ca43 and is left out. That commit also makes `rr_delete` one more path to H1.

## Codes, macros and channels

### G2. A macro path that FilePathResolver refuses stops DCS

- **What happens:** `M98 P"99:/macros/x.g"` (unknown drive), `../../x.g` or `0:/../x.g` stops DCS.
  - On the printer (2026-11-07) the journal showed `ArgumentException: Invalid drive index`, then `BackgroundServiceExceptionBehavior is configured to StopHost`. systemd restarted DCS, and RRF stayed halted until M999.
  - On 2026-11-08 the image build failed the lab test 3 of 3 times, the fix passed 3 of 3.
- **Cause:**
  - `Processor.DoMacroFile` calls `ToPhysical` without a try (`Link/Channel/Processor.cs:1309`, 4c910baa).
  - It throws for a path that leaves its volume (`Files/FilePathResolver.cs:69`) or names an unknown drive (`:91`, `:94`).
  - The packet loop catches only `ArgumentOutOfRangeException` (`Link/LinkService.cs:369-375`), so the exception ends LinkService and the host.
- **Fork fix (#132):** a refused path ends like a missing file. `macro` stays null and `MacroError` is set, so RRF replies "Macro file … not found" (RRF `GCodes2.cpp:1713`). The refused path is logged as an error.
- **Tests:** `MacroPaths.MacroPathRefusedByResolver`, `MacroPaths.RefusedPathInMacroEndsLikeMissingFile`, `LinkServiceOverSpi.MacroRequestForAFileThatCannotBeOpened`.

### G12. A number that does not fit the parameter type throws an OverflowException

- **What happens:** `M20 S31e9 P"0:/macs"` ends with `System.OverflowException: Value was either too large or too small for an Int32` instead of an error reply. Found by the weekly fuzz run (GCodeExecutionFuzz, seed 1754056457).
- **Cause:** DSF parses `31e9` as a float. `CodeParameter`'s conversion to int calls `Convert.ToInt32` for every float (`DuetAPI/Commands/Code/Parameter.cs:309-311`, 8434050b), which throws for values out of range and for NaN. The same holds for uint (also from a negative int), long and the arrays.
  - The conversions document `InvalidParameterTypeException` and refuse a long or uint that "won't fit". `Code.ProcessInternally` turns that exception into an error reply (`DuetControlServer/Commands/Generic/Code.cs:276-281`), but not an OverflowException, which reaches the client like G11.
- **Fork fix (#162):** a value that `Convert` would refuse falls through to the existing `InvalidParameterTypeException`, as the uint array conversion already did for negative values. M20 then answers "Error: M20: Cannot convert S parameter to Int32 (value 31e9)".
- **Not changed:** RRF reads `S31e9` as 31 and clamps integers that are too large, see G13 in `open-findings.md`.
- **Tests:** `Commands.CodeParameterConversions.RejectedConversion` (16 new cases), `Codes.FileCodes.ListFilesWithTypeOutOfRangeIsAnError`.

### T3. All channels together can fill RRF's code buffer

- **What happens:** a waiting job plus daemon.g reading ahead filled RRF's code buffer down to 32 bytes. The M98 of trigger2.g and the M112 behind it did not go out.
  - On the printer (2026-11-08) with the image build, the trigger macro ran 0 times in 5 s, with 324 bytes free.
  - With the fix it ran 10 times, with 496 and 468 bytes free.
- **Cause:**
  - RRF has one code buffer of 4096 bytes for all channels (RRF `SbcInterface.cpp:75`, `SbcMessageFormats.h:47`).
  - 82d581d1 shares it "among the active job streams, keeping spare room for time-critical codes on other channels" (`Link/LinkInterface.cs:40-46`). But `GetMaxBufferSpacePerChannel` limits each channel on its own (`Processor.cs:1012-1014`).
  - The only shared check is `BufferSpace > codeLength` (`LinkInterface.cs:564`).
- **Fork fix (#133):**
  - A spare room of 2 × (4-byte header + `MaxCodeBufferSize`) = 776 bytes.
  - A code buffered ahead must leave it free. The next code of a channel without buffered codes may use it, which is the existing `BytesBuffered == 0` rule.
- **Test:** `TriggerMacros.TriggerMacroGetsSpareCodeBufferRoom`, `LinkInterfaceRequests.CodesBufferedAheadLeaveSpareRoom`.

### T2. A macro request that crosses an InvalidateChannel is started by DSF

- **What happens:** an abort right after RRF requested the macro of an M98 leaves the channel blocked for good.
  - DSF starts the macro without a start code.
  - RRF warns "Macro file has been started on channel … but none was requested" (RRF `SbcInterface.cpp:827`).
  - The next code on the channel hangs.
- **Cause:**
  - RRF reads DSF's packets of a transfer before it writes its own for the next one (RRF `SbcInterface.cpp:258-259`, `:303-305`). So a macro request that comes with the transfer that carries InvalidateChannel, or earlier, was written before RRF read InvalidateChannel.
  - RRF ends that wait with an error (`:855-858`).
  - DSF clears `_allFilesAborted` as soon as InvalidateChannel is written (`Processor.cs:870`), and `DoMacroFile` (`:1270-1310`) does not look at it.
- **Fork fix (#134):** a new flag `_allFilesAbortedSent` lasts until the data of that transfer has been processed. `DoMacroFile` discards a request while it is set, without a reply.
  - A `MacroCompleted(error)` would be wrong here: when InvalidateChannel came first, RRF would close a file and pop a state (`SbcInterface.cpp:557-588`).
- **Test:** `TriggerFuzz.ChannelWorksAfterAbortWhileMacroRequestIsUnderway`, `TriggerFuzz.ChannelWorksAfterAbortCrossingMacroRequest`.
- **Printer (2026-11-08, RRF with #43, DSF with I8 fixed):** DCS discarded a crossing request ("was requested before RRF got the abort of all files"), and RRF never warned "none was requested". The run ended after about one second at T12.

### T1 (RRF). InvalidateChannel ends a macro wait but still sends the request

- **What happens:** an InvalidateChannel while RRF has a macro request pending blocks the channel until DCS restarts.
  - On the Trigger channel DSF runs a triggerN.g that RRF does not run and never pops it.
  - Every later trigger is discarded with "System macro … is requested but the stack is not empty" (DSF `Processor.cs:1301-1306`), and RRF reports "Macro file triggerN.g not found".
  - It can be reached through `abort` on the Trigger channel outside a macro. The IPC command `InvalidateChannel` reaches it too once I8 is fixed.
- **Cause (RRF, 8207b22cc):**
  - `InvalidateChannel` calls `ResolveMacroRequest(true, false)` but leaves `requestedMacroFile` set (`SbcInterface.cpp:855-858`).
  - Since the buffer no longer waits, the blocking path is skipped (`:1457`). The non-blocking path (`:1473`, `:1540-1553`) sends the request in the same exchange.
- **Fork fix (RRF #43):** `gb->MacroRequestSent()` before `ResolveMacroRequest`, as `GCodeBuffer::Reset` clears the request (`GCodeBuffer.cpp:128-140`). DSF needs no change; DSF #144 only adapts the fake firmware.
- **Not covered:** daemon.g, which is requested without waiting. An InvalidateChannel that RRF asks DSF to resend could end a wait that started after the first one.
- **Test:** `TriggerMacros.TriggerChannelWorksAfterInvalidationDuringTriggerRequest` (fake firmware). RRF #43 has only been built, not run on hardware.

### T12. An abort that crosses the closal of a macro stops DCS

- **What happens:** DCS logs `InvalidOperationException: Stack underrun on channel Telnet` in `Processor.Pop()` ← `MacroFileClosed()` ← `LinkService.HandleMacroFileClosed()`. The exception stops the host, systemd restarts DCS. Printer, 2026-11-08: the lab test `AbortsCrossingMacroRequests` (aborts of Telnet during M98 of a one-line macro), about one second after it started.
- **Cause:**
  - The abort removes all macro files of the channel from DSF's stack at once (`Processor.AbortAllFilesAsync`, `Processor.cs:722`, pop at `:770`). RRF does not report the files it closes for the InvalidateChannel (RRF `SbcInterface.cpp:865-866`, `FileAbortSent`).
  - RRF does report a macro that it closed just before (RRF `GCodes.cpp:722`, `SbcInterface.cpp:1450`). DSF pops for that closal as well (`Processor.cs:1218-1232`), which underruns the stack.
  - That closal reaches DSF in two ways. RRF wrote it before it read InvalidateChannel, in the same transfer or earlier. Or RRF wrote it after it read InvalidateChannel in the same exchange, because `ExchangeData` reads DSF's packets first (`SbcInterface.cpp:301`) and writes the closals later (`:1450`).
  - Origin: DSF 4a576390 (from v3.6 `SPI/Channel/Processor.cs:684`, `:1125-1140`), RRF 8207b22cc and f522904f5.
- **Reachable upstream:** through the abort keyword (`KeywordHandler.cs:168-171`) while RRF closes a macro on the same channel, e.g. from a second client of HTTP. Not shown. The IPC command does not reach it because of I8.
- **Fork fix:** both windows, as for T2 and T1.
  - DSF (#158): `MacroFileClosed` discards a closal while `_allFilesAborted` or `_allFilesAbortedSent` is set, the window of #134.
  - RRF (#45): InvalidateChannel clears a closal that has not gone out (`gb->MacroFileClosedSent()`), as `GCodeBuffer::Reset` does (`GCodeBuffer.cpp:142`). The main task sets it under the same buffer mutex (`GCodes.cpp:540`).
- **Not covered:** an InvalidateChannel that RRF asks DSF to resend, as in T11.
- **Test:** `TriggerFuzz.DcsKeepsRunningWhenAMacroFileClosalArrivesBeforeTheAbortGoesOut`, `DcsKeepsRunningWhenAnAbortCrossesAMacroFileClosal` (DSF side), `DcsKeepsRunningWhenAnAbortArrivesBeforeAMacroFileClosalIsSent` (RRF side in the fake firmware). RRF #45 has only been built, not run on hardware.

### T10 (RRF). The reply of a code that DSF dropped goes to the next code

- **What happens:** printer, 2026-11-08 (`AbortsCrossingMacroRequests`): after an IPC InvalidateChannel of Telnet while an M98 waited for its macro, the next `G4 P0` finished with "Error: M98: Macro file 0:/macros/lab-tests/abort/short.g not found". Its own reply came out of order.
- **Cause:**
  - DSF drops every code it has sent on the channel for the abort (`Processor.InvalidateRegular`) and matches code replies by order (`Processor.HandleReply`, `Processor.cs:1073-1086`).
  - RRF's InvalidateChannel drops only the codes that are still pending in the code buffer (`SbcInterface.cpp:867`, `InvalidateBufferedCodes`). The code being executed finishes and replies; an M98 reports its macro as not found once InvalidateChannel ends the wait (`:857`, `GCodes2.cpp:1713`).
- **Fork fix (RRF #46, DSF #163 for the fake firmware):** InvalidateChannel flags the code from the SBC that runs on the channel, and RRF drops its replies and push messages, as it already drops the surplus reply of a single file abort (`GCodes.cpp:4344`).
  - The flag covers an M98 that waits for its macro (before the wait is ended) and a code that `AbortFile` leaves running.
  - Only the first copy of InvalidateChannel flags a code (see T11 resend).
  - Behaviour change: RRF sends no reply for a code from the SBC that was running when InvalidateChannel arrived.
- **Not covered:** listed under T10 in `open-findings.md`.
- **Test:** `TriggerFuzz.NextCodeGetsItsOwnReplyAfterAbortWhileM98WaitsForItsMacro`, `NextCodeGetsItsOwnReplyAfterAbortWhileACodeRuns`, `NextCodeGetsItsOwnReplyAfterAbortWhileACodeOfAMacroRuns`, `NextCodeGetsItsOwnReplyWhenRrfAsksForTheAbortAgain` (fake firmware). RRF #46 builds (3.7.0+1.mp.9-dev.3), the lab run follows.

### T11 (resend). An abort that RRF asks for again blocks the channel

- **What happens:** printer, 2026-11-08 22:39:02 UTC (RRF with #43 and #45, DSF with #158): DCS logs "Resending packet #0 (request InvalidateChannel)". RRF warns "Macro file has been started on channel Telnet but none was requested", the next code on Telnet gets no reply, and the channel stays blocked until DCS restarts.
- **Cause:**
  - While an M98 waits for its macro, the main task holds the buffer mutex: `SpinGCodeBuffer` locks it for the whole spin (RRF `GCodes.cpp:540`, c5af8416b). `RequestMacroFile` releases only its own level of the recursive mutex (`GCodeBuffer.cpp:1252`, `:1263`).
  - InvalidateChannel waits only `SbcYieldTimeout` (10 ms, `SbcInterface.cpp:45`, `:860`) and asks DSF to send it again (`:1300`). Before that it ends the macro wait (`:857`). So during a macro wait the copy is the normal case.
  - DSF goes on with the channel as soon as the first copy is out (`Processor.cs:867-870`). `WriteCode` only waits for resends of codes (`SPI.cs:737`).
  - The copy then drops the codes RRF got in between (`InvalidateBufferedCodes`), which never get a reply. Or it ends a macro wait that started after the first copy, and DSF starts that macro, because the request came after its window (T2, #134).
- **Fork fix (#161):** DSF sends nothing else on the channel until RRF has answered the InvalidateChannel, i.e. until the data of the next transfer brought no resend request. RRF writes resend requests while it reads, before its other packets (`SbcInterface.cpp:1300`), so a resend request also reopens the window of #134 for the rest of that data and the next transfer.
  - `ILinkAdapter.ResendPacket` reports the channel of a resent InvalidateChannel.
  - Behaviour change: after an abort, the channel's next request goes out one transfer later.
- **Not changed:** the T1 part of InvalidateChannel still ends the macro wait on the first copy, and RRF still answers the M98 with "Macro file … not found" (T10).
- **Test:** `TriggerFuzz.CodesAfterAnAbortWaitUntilRrfHasProcessedIt`, `MacroRequestBetweenTwoCopiesOfAnAbortIsDiscarded`. `TriggerMacroAfterAnAcceptedAbortIsStarted` guards that a trigger macro after an accepted abort still starts. The fake firmware lets the buffer lock fail (`FakeFirmware.FailBufferLocks`).

### K32. An emergency stop that crosses the end of a macro never reaches RRF

Checked on 2026-11-11 at DSF `upstream/v3.7-dev` f76ffc70; the line numbers of this entry refer to that commit.

- **What happens:** M112 is sent while RRF's `MacroFileClosed` of a macro that has just ended is in the data that DSF processes in the same cycle.
  - DCS logs `InvalidOperationException: Stack underrun on channel HTTP` in `Processor.Pop()` ← `MacroFileClosed()` ← `LinkService.HandleMacroFileClosed()`.
  - The link thread ends before the transfer that would carry the `EmergencyStop` packet, so RRF never gets the stop.
  - M112 has replied with success by then, and DCS has set `state.status` to `halted`.
  - The exception stops the host (`BackgroundServiceExceptionBehavior` is the default, StopHost), and systemd restarts DCS.
- **Cause:**
  - In the cycle of the stop, `LinkService.Execute` first invalidates the codes, which pops every stack to its base level (`LinkService.cs:268`, `Processor.cs:1404-1451`). It then writes the stop into the transmit buffer and completes the request (`LinkService.cs:269-272`). Only after that it processes the packets that RRF sent with the last transfer (`:358-376`).
  - A `MacroFileClosed` among those packets pops once more (`Processor.cs:1228-1243`), now on the base level, and `Pop` throws (`:176-181`). The packet loop catches only `ArgumentOutOfRangeException` (`LinkService.cs:371-375`), so the exception ends the thread (`:218`) before the next transfer.
  - M112 reports success because the request is completed when the stop is written, not when it has been transferred (`LinkService.cs:272`, `MCodeHandler.cs:689-711`).
  - RRF writes the closal of a macro in the exchange after the macro has ended (RRF `SbcInterface.cpp:1450`), so DSF reads it one transfer later. The window is that one transfer.
  - Origin: 4a576390, narrowed to `InvalidateCodes` by d027f15b. v3.6 has the same order from reading (`SPI/Interface.cs:693-701`, `:795-812`), not run.
- **Effect on the machine:** RRF keeps running until it notices the lost connection. Upstream RRF then reports "Lost connection to SBC" and invalidates its SBC resources (RRF `SbcInterface.cpp:187-224` at `3.7-dev` of 2026-11-10); it does not call `EmergencyStop` there. What that does to a move or a heater that is on was not checked.
- **Reachable upstream:** an M112 from any channel while a macro ends on any channel. Shown with the test below; not seen on a printer.
- **Fork fix (#170):** the cycle of the stop writes the stop first, processes the data RRF sent before against the stack as it was, and only then invalidates the codes and transfers. If processing that data throws, the stop is transferred before the exception goes on. M112 completes once the data of the transfer that carried the stop has been processed.
- **Test:** `SPI.EmergencyStopCrossingMacroEnd.EmergencyStopWhileTheClosalOfAMacroIsPending`.
  - It runs the real DCS services against the fake firmware and fails without the fix: "RRF did not get the emergency stop, transfer thread Faulted Stack underrun on channel HTTP".
  - Run against upstream on 2026-11-11: the product code under `src` was byte-identical to f76ffc70 (`git diff upstream/v3.7-dev -- src ':!src/UnitTests'` empty), with the fork's fake firmware and this test added. The fake firmware needed its fork-only parts removed (the link capabilities and one parameter of `ResendPacket`); the test itself is unchanged. It failed with the same message in every run.
  - A second test, written for this check (`design/k32-upstream/K32UpstreamHostOutcome.cs`), ran the same case in the generic host as `Program.cs` builds it. It showed the outcome: M112 replied with success, `state.status` was `halted`, RRF had received no `EmergencyStop`, the packet was left in DSF's transmit buffer, and the host stopped.
  - `design/k32-upstream/reproduce.sh` repeats both runs on a checkout of f76ffc70; `design/k32-upstream/README.md` has the result.
- **Not checked:** real RRF and a printer; the fake firmware models RRF writing the closal into the transfer before it reads the stop. Upstream commits after f76ffc70.

## Permissions and files

### I1. `RequiredPermissionsAttribute.Check` grants every command

- **Cause:** `Check` loops over every `SbcPermissions` value including `None = 0` (`DuetAPI/Utility/RequiredPermissionsAttribute.cs:25-39`, `SbcPermissions.cs:17`). `HasFlag(None)` is always true, so `Check` returns true. This has been so since c42f948b (2020).
- **Effect:**
  - DCS never refuses a command (`IPC/Connection.cs:319-325`) or a connection mode (`IPC/Server.cs:237-258`) to a plugin, whatever its manifest says.
  - `ipc.md:39-41` documents "at least one of the listed permissions".
- **Fork fix (#135):** `Check` is `(_requiredPermissions & permissions) != None`.
- **Also needed (DWS system packages):**
  - `InstallSystemPackage` and `UninstallSystemPackage` require `SuperUser` (`InstallSystemPackage.cs:8`).
  - DWS forwards `PUT`/`DELETE /machine/systemPackage` (`MachineEndpoints.cs:53-54`, `:733`), but as a DSF service it holds only `ServicePlugins` (`Connection.cs:101-103`), so the endpoint only worked because of this bug.
  - The fork requires `SuperUser | ServicePlugins`. It does not grant SuperUser to the DSF services.
- **Behaviour change:** plugins are refused what their manifest does not cover. DSF services are refused `SetNetworkProtocol`, which only the root plugin DuetPiManagementPlugin sends.
- **Doc errors, not changed:** the table in `ipc.md:87` gives `ManagePlugins` for the package commands. `ipc.md:81` lists `CheckPassword` under `CommandExecution`, but the code requires `ObjectModelRead | ObjectModelReadWrite` (`CheckPassword.cs:9`).
- **Printer (2026-11-08, with the fix):** the three installed plugins started, 36 of 36 endpoints answered, and there was no refusal in the journal.
- **Tests:** `IPC/RequiredPermissions.cs`, `IPC/IpcPluginPermissions.cs`.

### I8. The IPC command `InvalidateChannel` is refused as unsupported

- **What happens:** `InvalidateChannel` and `InvalidateChannelAsync` of the DuetAPIClient (`DuetAPIClient/Commands/BaseCommandConnection.cs:430`, `:443-446`) always fail. DCS answers "unsupported command InvalidateChannel" (`IPC/Connection.cs:655`), in the Command and in the Intercept mode.
- **Cause (6dd3b8a3, "Version 3.4-b3"):** that commit added the command to DuetAPI, DuetAPIClient and DCS (`Commands/Generic/InvalidateChannel.cs`), but not to `Command.SupportedCommands` (`IPC/Processors/Command.cs:23-56`). The Intercept mode takes its commands from that list (`CodeInterception.cs:42`). v3.6-dev and v3.7-dev do not list it either.
- **Fork fix (#158):** `InvalidateChannel` is in `Command.SupportedCommands`. It keeps its permission `CodeInterceptionReadWrite` (`DuetAPI/Commands/Generic/InvalidateChannel.cs:8`).
  - Behaviour change: a plugin whose manifest grants only `CodeInterceptionReadWrite` may now open a Command connection (`IPC/Server.cs:237`). Every other command of that mode needs another permission.
  - The client documentation names `SuperUser` (`<seealso>` in `BaseCommandConnection.cs:429`, `:442`), the attribute requires `CodeInterceptionReadWrite`. The fork keeps the attribute and leaves the documentation unchanged.
- **Tests:** `IpcClientApi.InvalidateChannel`, `IpcPluginPermissions.InvalidateChannelNeedsCodeInterceptionReadWrite`. The lab test `AbortsCrossingMacroRequests` needs it to abort Telnet.

### H1. A delete that names the SD root deletes the whole virtual SD card

- **What happens:** `DELETE /machine/file/0%3A%2F?recursive=true` answers 204 and deletes the SD directory, `config.g` included. So do `0:`, `0:/.`, `/` and `0:/sys/..`. Shown in-process, not on the printer.
- **Cause:**
  - `CombineContained` accepts a result that equals the drive root (`Files/FilePathResolver.cs:67`, 75eb6536).
  - Every delete passes that result to `Directory.Delete(path, recursive)`:
    - DWS `MachineEndpoints.cs:362-366`;
    - `rr_delete` (`RepRapFirmwareEndpoints.cs:362-366`, recursive since 5130ca43);
    - M472 (`MCodeHandler.cs:817-823`);
    - the delete that RRF hands to DSF (`LinkService.cs:1087-1090`).
  - **RRF:** standalone, RRF refuses a recursive delete of the root, "Delete root folder is not allowed" (`MassStorage.cpp:839-858`, 73d1509f6). In SBC mode it hands the delete to DSF before that check (`:822-836`, 600857f87).
- **Fork fix (#136):**
  - `ToPhysical(…, forDeletion)`: `CombineContained` throws RRF's message when the result is the root of its drive. Trailing separators are ignored in that comparison.
  - The IPC command `ResolvePath` gets a `ForDeletion` property. DWS uses it for both endpoints.
  - **RRF #44** moves the existing root check above the SBC hand-off.
- **Not covered:** mount points below the SD directory can still be emptied.
- **Tests:** `SdRootDeletionTests.RootCannotBeDeleted`, `PathResolution.RootForDeletion`, `FileCodes.DeleteRootDirectory`, `LinkServiceOverSpi.RootIsNotDeleted`.

## SPI link

### F3 (case). One bit error in RRF's header response leaves the plain link stuck

This is a case of F3 in `design/sbc-link-findings-upstream.md`.
- **Sequence:**
  1. A flipped bit turns RRF's Success for DSF's header into BadHeaderChecksum (1 → 5).
  2. RRF goes on with the data (RRF `DataTransfer.cpp:959-965`), and DSF exchanges its header again (`SPI.cs:1763-1765`).
  3. RRF takes that header as data, answers BadDataChecksum (`:1005`) and from then on exchanges the data again whatever DSF answers (`:1025-1029`).
  4. DSF has already overwritten `_rxHeader` with RRF's data before the checksum check (`SPI.cs:1655`). It takes BadDataChecksum as "didn't receive valid data either" and keeps exchanging headers (`:1688-1692`).
- **Fork fix (#137):**
  - When RRF answers DSF's BadHeaderChecksum with BadDataChecksum, DSF goes on with the data, as its data phase already does (`SPI.IsExchangingData`).
  - A received header replaces `_rxHeader` only once its checksum is valid.
- **Effect of the fix:** the census goes from 35 to 27 failing single faults. On the printer the 6 cases of that kind broke the link without the fix and recovered by retries with it (2026-11-08).

## Firmware update

Nothing in this section ran on hardware. The tests emulate DuetIAP 3.7-dev 9af72da4.

### U1. M997 flashes a `.UF2` file without unpacking it

- **Cause:** `MCodeHandler.cs:1179` (5810dfde) and `Utility/Firmware.cs:117` compare the extension case-sensitively with `.uf2`. DuetIAP compares it case-insensitively (`iap_sd.cpp:133`).
- **Effect:** the UF2 container is flashed as the image.
- **Fork fix:** e97c4fa8.
- **Test:** `FirmwareUpdateFlashing.UpdateWithUf2FileInUpperCase`.

### U2. When the IAP program stops answering, M997 reports nothing and DCS does not stop

- **Cause:**
  - The TfrRdy timeout is an `OperationCanceledException` (`SPI.cs:1575`). LinkService passes it on to M997 (`LinkService.cs:306-310`).
  - M997's catch excludes `OperationCanceledException` (`MCodeHandler.cs:1194`, 5810dfde). So the code ends as cancelled, without the error reply, the start error file and the stop of DCS (`:1196-1220`).
- **Fork fix (2d1deb6a):** LinkService passes such a timeout on as an `IOException`, unless DCS is stopping.
- **Test:** `FirmwareUpdateFlashing.UpdateFailsWhenTheIapProgramStopsAnswering`.

### U3. M997 flashes an empty firmware file

- **Cause:** M997 opens the firmware file only after it has stopped the plugins, and never checks it (`MCodeHandler.cs:1153-1187`). The IAP program erases the flash before it receives the image (DuetIAP `iap.cpp:370`, `ErasingFlash` before `WritingUpgrade` at `:437`).
- **RRF:** in standalone mode RRF refuses such a file. The first word must be the top of the RAM (`RepRap.cpp:2057-2077`).
- **Fork fix (ead23cd4):** M997 opens and unpacks the image before it stops anything. It refuses an image shorter than 4 bytes with RRF's message "Firmware binary … is not valid for this electronics".
- **Not done:** RRF's comparison with the top of the RAM and with the flash size, because DCS does not know these values for the board.
- **Test:** `FirmwareUpdateFlashing.UpdateRefusesEmptyFirmwareFile`.

### U4. `DuetControlServer -u` updates a board whose firmware file is missing

- **Cause:**
  - `FirmwareUpdater.cs:51` enters every file with version null, and `:65-68` keeps null for a missing file.
  - `:77-79` then counts the board as outdated ("→ n/a", `:102`). It gets M997 B, a 3-minute wait and M999.
  - The warning at `:86-87`, meant for this case, is not reached. The remote update has the same code (`:284`, `:307`).
- **Fork fix:** 63b790d0.
- **Test:** `UpdateOnlyMode.BoardWithoutFirmwareFileIsLeftAlone`.

### U5. One unreadable firmware file stops `-u` for every board

- **Cause:** `Firmware.GetFirmwareVersionAsync` throws for an invalid UF2 file, and nothing catches it (`FirmwareUpdater.cs:67`, `:299`).
- **Fork fix (0d83ff3e):** only the boards of that file are skipped, with an error.
- **Test:** `UpdateOnlyMode.InvalidFirmwareFileLeavesOnlyItsBoardAlone`.

### U6. `-u` drops the error of M997 B

- **Cause:**
  - The reply of M997 B is not checked (`FirmwareUpdater.cs:173`, `:397`). `-u` waits 3 minutes for the new version (`:178-194`), although RRF has refused at once (`ExpansionManager.cpp:448ff`).
  - Then it resets the main board with M999 when any board was selected (`:229-247`, `:454`), even if no update started.
- **Fork fix (ae68e049):** the error is printed at once and the board is skipped. M999 goes out only if RRF accepted at least one M997 B.
- **Test:** `UpdateOnlyMode.ExpansionUpdateErrorIsReported`.

### U7. M997 with module 0 and other modules drops the others

- **Cause:** DCS takes M997 whenever S contains 0 (`MCodeHandler.cs:1111`), updates the main board and drops the other modules silently.
- **Wiki and RRF:** the wiki documents `M997 S0:1` for two modules. RRF refuses all modules when one cannot be updated (`GCodes3.cpp:816-836`).
- **Fork fix (f2048aa5):** M997 with module 0 plus others is refused before anything happens ("update module 4 separately"), because the update restarts RRF and DCS. Updating module 0 and passing on the rest would be the other choice.
- **Test:** `FirmwareUpdates.UpdateOfMainBoardWithOtherModules`.

## Stopping DCS

v3.6 stopped in a fixed order: it waited for a firmware update, shut the link down, then cancelled the pipelines, and exited by itself after 5 s at the latest (`v3.6-dev Program.cs:420-450`). c5e3214b removed that `ShutdownAsync`. ST2 and ST4 come from what it did.

### ST1. A transfer that does not complete keeps DCS from stopping

- **Cause:**
  - `PerformFullTransfer` gets `ApplicationStopped` (`LinkService.cs:285`, `:518`). 2cbc04ae changed that from `stoppingToken`; v3.6 used the program token (`v3.6-dev SPI/DataTransfer.cs:210`).
  - The host signals `ApplicationStopped` only after all services have stopped, so `LinkService.StopAsync` waits for the transfer thread until the shutdown timeout.
  - systemd kills DCS after 15 s (`duetcontrolserver.service:8`).
- **Printer (2026-11-08):**
  - With the link kept failing, two `systemctl restart` ended with SIGKILL after 15.14 s and 15.18 s. The journal kept logging "Lost connection to Duet" with "Maximum number of SPI transfer retries exceeded".
  - With the fix side-loaded, both took 0.03 s. Restarts without a stuck link took 0.03-0.05 s.
  - The stuck link was made with the fork's fault injection. Offline, the same hang happens with an RRF that stops answering.
- **Fork fix (30035f3f):** both calls get `stoppingToken`. The firmware update keeps `ApplicationStopped`.
- **Tests:** `ShutdownHang.LinkServiceStopsWhileRrfDoesNotAnswer`, `ShutdownHang.LinkServiceStopsWhileEveryTransferFails`.

### ST2. A code that a client waits for when DCS stops is never completed

- **Cause:**
  - The pipeline stages read with `ApplicationStopping` (`Codes/Pipelines/PipelineStackItem.cs:63`). That fires before `LinkService.StopAsync` cancels the link's codes into those queues (`LinkService.cs:240`).
  - The cancelled codes are never completed, so the IPC connection waits for them, and the IPC server waits for its connections until `ApplicationStopped` (`IPC/Server.cs:123`).
  - The same happens to a code sent in the second after M999.
- **Fork fix (fea02d3a):** the stages read until `CodeProcessorService.StopAsync`. The host calls it last, because `AddCodes` is registered first (`Program.cs:181`).
- **Tests:** `ShutdownHang.StopsWhileAnIpcCodeIsPending`, `ShutdownHang.StopsAfterM999WhileAnotherCodeIsPending`.

### ST3. AsyncLock refuses a free lock for a cancelled token

- **Cause:**
  - bb5edd5e replaced Nito.AsyncEx.AsyncLock with an own one that awaits `SemaphoreSlim.WaitAsync(token)` (`DuetSharedLibrary/AsyncLock.cs:48`, `:60`). That fails at once for a cancelled token, even when the lock is free.
  - Nito documents the opposite for `AsyncLock.LockAsync`: "If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available)" (Nito.AsyncEx.Coordination 5.1.2).
  - The event log locks with `ApplicationStopping` (`Utility/Logger.cs:59`, `:93`, `:224`, `:258`). While DCS stops, `Executed.cs:115-128` throws there. That is outside the try that completes the code (`:164-239`).
  - `PipelineStackItem.cs:85-88` only logs the exception, so the code is never completed.
- **Printer:** "Failed to process code in stage Executed" twice in the journal, on 2026-11-06 09:36:49 and 2026-11-07 11:24:14 UTC, with DSF 3.7.0+mp.12. The second has `TaskCanceledException` in `AsyncLock.LockAsync` from `EventLogger.LogAsync` and `Executed.ProcessCodeAsync`.
- **Fork fix (472daabd):**
  - `Wait(0)` first; the FIFO order is unchanged.
  - The catch in `PipelineStackItem` completes the code with the exception, and stays quiet for a cancellation during the stop.
  - It affects every AsyncLock caller in DCS, DWS and DPS, as before bb5edd5e.
- **Tests:** `AsyncLockCancelledToken.FreeLockIsTaken`, `ExecutedStageShutdown.CodeInExecutedStageCompletesWhileDcsStops`.

### ST4. The host waits longer than systemd

- **Cause:** DCS does not set `HostOptions.ShutdownTimeout`, so the host waits 30 s for its services. systemd sends SIGKILL after `TimeoutStopSec=15` (`duetcontrolserver.service:8`). v3.6 exited by itself after 5 s (`v3.6-dev Program.cs:434-441`).
- **Fork fix (b2a6affd):** 10 s, Tim's choice.
- **Firmware update:** a shorter timeout also cuts off a running firmware update, which v3.6 waited for. The fork therefore added #154, see "Left out". If you take this fix, take #154 with it.
- **Test:** `ShutdownHang.ShutdownTimeoutIsShorterThanTheStopTimeoutOfSystemd`.

## Startup and settings

### SU1. `"logLevel"` in config.json stops DCS from starting

- **Cause:** DCS reads the key case-insensitively (`Settings.cs:483-484`, which also takes short aliases such as `"info"`) but takes it out of the binding with a case-sensitive comparison (`:492`, 04271d02). So `"logLevel": "info"` also goes to the binder, which cannot convert the alias, and DCS ends with "Failed to initialize environment".
- **Fork fix:** f18e834e.
- **Test:** `SettingsParsing.LogLevelKeyIsCaseInsensitive`.
- **Not fixed:** DPS has the same comparison (`DuetPluginService/Program.cs:84`).

### SU2. A restored config.json holds the command-line values

- **Cause:** `PostConfigure` writes the settings of the current start (`Settings.cs:429`, 4c910baa). The wiki says that deleting the file reverts to factory defaults (`DSF_Other.md:204`).
- **Effect:** after `DuetControlServer -u`, which the reprapfirmware postinst runs (`postinst:11`) and which sets `LogLevel` to error (`Program.cs:94-98`), every later start logs only errors.
- **Fork fix (899386cd):** writes `new Settings()`.
- **Test:** `SettingsParsing.RestoredConfigFileHoldsTheDefaults`.

### SU3. Settings on the command line are refused

- **Cause:** 9ca5be78 passes unmatched tokens to `AddCommandLine` (`Program.cs:160`). The root command still treats them as errors, so `--Key=value` fails with "Unrecognized command or argument". DWS takes such arguments (`DuetWebServer/Program.cs:46`).
- **Fork fix (85924c8d):** `rootCommand.TreatUnmatchedTokensAsErrors = false`. An unknown argument is now ignored.
- **Test:** `DcsProcess.SettingsCanBeGivenOnTheCommandLine`.

### SU4. An invalid config.json exits with OsError

- **Cause:** the try around `AddJsonFile` (`Program.cs:156-169`) never fires, because the file is read in `Build()` (`:190`). The error lands at `:197-199`: exit 71, "Failed to initialize environment", instead of 78 and "Failed to load settings".
- **Fork fix:** 7b21e310.
- **Test:** `DcsProcess.InvalidConfigFileIsAConfigurationError`.

### SU5 and SU6. Minor start failures

- **SU5:** `Terminate` throws when the directory of the start error file is missing (`Program.cs:46`), so the exit code becomes 1. On a packaged system `/run/dsf` exists. Fork fix cf3aac40.
- **SU6:** `Directory.GetFiles` of a missing plugin directory stops the start (`Model/PeriodicUpdateService.cs:86-88`).
  - The packages create the directory (`pkg/common/common.functions:143`, duetcontrolserver `postinst:19-21`), so this only affects installs without it.
  - Fork fix c04b29a2. DPS has the same call (`DuetPluginService/PluginService.cs:37`), not fixed.

## Plugin service

**Who can write the manifests:** the manifests and plugin files in `/opt/dsf/plugins` belong to dsf. The duetcontrolserver postinst runs `chown -R dsf:dsf /opt/dsf/plugins` (`postinst:19-21`), and the regular DPS writes every plugin file (`DuetPluginService/Commands/InstallPlugin.cs:80-200`). So "a manifest on disk" below means anything that runs as dsf.

### PS1. Root plugins are loaded and started without RootPluginSupport

- **Cause:** only DCS `InstallPlugin` checks `RootPluginSupport` (`Commands/Plugins/InstallPlugin.cs:60`). None of the later paths does:
  - the manifests loaded at start (`Model/PeriodicUpdateService.cs:94-111`, 491abf34);
  - `ReloadPlugin`;
  - DCS `StartPlugin`, which sends a superUser plugin to the root service (`Commands/Plugins/StartPlugin.cs:140`);
  - DPS `StartPlugin` (`StartPlugin.cs:58`).
- **Effect:** a superUser manifest written to the plugin directory runs as root at the next start.
- **Fork fix (8b31b7e0):** a shared `PluginManifestCheck` at install, reload, load and start. The root DPS asks DCS for `sbc.dsf.rootPluginSupport`.
  - DuetPiManagementPlugin is a superUser plugin, so it would no longer run with the shipped `RootPluginSupport: false`. The fork therefore trusts a root plugin whose manifest is root-owned and has no write permission, i.e. installed by a package (7647099b). That is a design decision; see PS8.
- **Test:** `DpsThroughDcs.RootPluginIsNotStartedWithoutRootPluginSupport`.

### PS2. ServicePlugins in a manifest on disk is accepted

- **Cause:**
  - DCS and DPS refuse the reserved permission at installation only (`Commands/Plugins/InstallPlugin.cs:199-202`, DPS `InstallPlugin.cs:295-298`).
  - Manifests loaded at start (`PeriodicUpdateService.cs:94-111`, DPS `PluginService.cs:37ff`) or by `ReloadPlugin` are not checked.
  - A plugin connection gets the permissions of its manifest (`IPC/Connection.cs:123`, `:139`, `:151`).
- **Fork fix:** 4290652d.
- **Tests:** `DpsUninstallReloadPlugin.ReservedPermissionIsNotLoadedAtStart`, `DpsUninstallReloadPlugin.ReloadRefusesTheReservedPermission`.

### PS3. DPS deletes the whole plugin directory for a manifest without id

- **Cause:** `pluginBase = Path.Combine(PluginDirectory, plugin.Id)` becomes the plugin directory itself, which is then deleted recursively (DPS `InstallPlugin.cs:83-89`).
- **Reachability:** DCS refuses such a manifest first (`Commands/Plugins/InstallPlugin.cs:193`), so this is reachable only through DPS directly. `ReloadPlugin` of both services accepts it.
- **Fork fix:** 2819bdef.
- **Test:** `DpsInstallPlugin.ManifestWithoutIdIsRefused`.

### PS4. Uninstall deletes the listed files without containment

- **Cause:** `UninstallPlugin` combines the `DwcFiles` and `SdFiles` of the manifest with 0:/www and 0:/ and deletes them (DPS `UninstallPlugin.cs:95-128`) without checking that they stay inside. For a superUser plugin the root service does this as root (`:67`).
- **Fork fix (25eebd63):** a shared `Path.IsInside` check. Files outside are skipped with a warning.
- **Test:** `DpsUninstallReloadPlugin.UninstallDeletesOnlyFilesInsideTheSdDirectory`.

### PS5. A plugin upgrade writes through symlinks

- **Cause:** an upgrade keeps the old plugin directory (DPS `InstallPlugin.cs:84`). The zip-slip check compares paths lexically (`:162-167`), and `FileMode.Create` follows a symlink the plugin left there (`:185`).
- **Fork fix (43e20b43):** `Path.IsInside` resolves the symlinks of existing parent directories. Files are deleted and created with `CreateNew`.
- **Remaining risk:** a parent directory swapped for a symlink between the check and the write.
- **Test:** `DpsInstallPlugin.UpgradeDoesNotWriteThroughSymlinks`.

### PS6. Shell commands in the arguments of a Python plugin are run by bash

- **Cause:**
  - Python plugins start as `/bin/bash -c "{pluginDir}/venv/bin/python {command} {args}"` (DPS `Settings.cs:112-117`, `plugins.json:22-23`).
  - The check meant to stop that (`InstallPlugin.cs:300-306`) refuses only `"`, `$`, `` ` ``, `\` and line breaks. `;`, `|`, `&`, `<` and `>` need no break-out from the quoting, and bash runs them.
- **Fork fix (eed13fec):** the bash script is fixed, `-c "exec \"$0\" \"$@\"" {pluginDir}/venv/bin/python {command} {args}`, so interpreter, script and arguments arrive as positional parameters. bash stays, because the CHX image's AppArmor profile confines Python plugins on the path DPS → bash → python.
  - `plugins.json` is a conffile, so a changed copy keeps the old value on upgrade. DPS replaces exactly the old shipped value (94dd5f23).
- **Test:** `DpsStartStopPlugin.PythonPluginArgumentsDoNotRunShellCommands`.

### PS7. The symlink check of StartPlugin can be bypassed

- **Upstream's intent:** `StartPlugin` refuses a symlinked executable, so that a plugin cannot escape its AppArmor profile (DPS `StartPlugin.cs:94-100`, 561d5c2c).
- **Bypasses:**
  - `Path.Combine(…, "dsf", plugin.SbcExecutable)` returns an absolute `SbcExecutable` such as `/bin/bash` unchanged (`:84-87`). InstallPlugin finds that file as well (`InstallPlugin.cs:205-214`).
  - `FileInfo.LinkTarget` checks only the last path component, so a `dsf` directory that the plugin replaced with a symlink to `/bin` gets past it too.
- **Fork fix:** absolute `SbcExecutable`/`SbcExtraExecutables` are refused (e2caaab1). StartPlugin requires the executable inside `{PluginDirectory}/{id}` with symlinks resolved, and refuses a symlinked plugin directory (0c1f113a).
- **Test:** `DpsStartStopPlugin.ExecutableOutsideThePluginDirectoryIsNotStarted`.

### PS8. Root plugin files belong to dsf (fork design decision)

- **Upstream:** the postinst makes the whole plugin tree dsf-owned (`postinst:19-21`), and the regular DPS writes the files of root plugins too. So the dsf user can change the executable that the root service starts as root.
- **Fork (a design decision, not a one-line fix):**
  - The root service writes and owns the directory, manifest and venv of a superUser plugin. It starts one only if every entry down to the executable belongs to root, is not writable by group or others and is no symlink (eaa840bd).
  - `/opt/dsf/plugins` is root:dsf 1775 (67561acf).
  - Package-installed root plugins are trusted without RootPluginSupport (7647099b).
  - Root plugins installed earlier through the API have to be reinstalled.
- **Latent bug found on the way:** `statx_timestamp.__reserved` is 64-bit in `DuetSharedLibrary/Interop/Libc/Interop.stat.cs:64`; the kernel has 32 bits. No upstream code calls `statx` yet.

### PS9. Smaller plugin service bugs

| Bug | Upstream code | Fork fix, test |
|---|---|---|
| A failed `SetPluginProcess` leaves the process registered, so every later start is skipped as running | DPS `StartPlugin.cs:149` registers the process, `:154` reports it before the exit watcher at `:158`, `:113-116` skips the start | 096aff43, `FailedRegistrationAtDcsDoesNotBlockTheNextStart` |
| DPS keeps running without a DCS connection when DCS closes it; at start-up it stops | DPS `IPC/CommandService.cs:69-73` vs `:32` | f785340f, `ServiceStopsWhenDcsClosesTheConnection` |
| Child processes of a plugin outlive StopPlugin: after SIGTERM they are re-parented, and `Kill(true)` runs only on a timeout | DPS `StopPlugin.cs:57-77` | edba5456 + a1118764, `ChildProcessesAreStoppedWithThePlugin` |
| Uninstall of a plugin without manifest throws | DPS `UninstallPlugin.cs:72` before the `File.Exists` at `:88` | 3408540f, `PluginWithoutManifestIsUninstalled` |
| A plugin service that closes while DCS waits for the full model update stays registered in DCS | DCS `IPC/Processors/PluginService.cs:221`, outside the try at `:229` | 1cc446d2, `ServiceClosedBeforeThePluginsAreStartedIsUnregistered` |
| A command that cannot be received is logged as failure of the previous one | DPS `IPC/CommandService.cs:47`, `:55` | 2df78a88, `InvalidCommandIsNotLoggedAsThePreviousOne` |

### PS10. Package installation fails with an inherited environment variable

- **Cause:** DPS sets `InstallPackageEnvironment` with `ProcessStartInfo.EnvironmentVariables.Add` (DPS `InstallPlugin.cs:338`, `:375`), which throws when DPS's own environment already has the variable. The default holds `DEBIAN_FRONTEND` (`Settings.cs:86`).
- **Found by:** GitHub's runners, which set `DEBIAN_FRONTEND`.
- **Fork fix (#153):** the indexer, so the setting overrides an inherited value.
- **Test:** `DpsInstallPlugin.VariableOfTheServiceIsOverridden`.

## Privilege boundaries (error class census of DSF, 2026-11-24)

These findings concern who can make DSF do what as which user. The printers they were found for run in company networks behind firewalls, so the fork fixed them in normal public PRs and reports them here afterwards.

**What was checked:** DSF `upstream/v3.7-dev` at dbf9b0447 (2026-11-23), which the fork's `v3.7-dev` 8507c98c0 contains. Line numbers in this section refer to dbf9b0447. For each finding, the faulty lines were looked up there and their commit was checked with `git merge-base --is-ancestor <commit> upstream/v3.7-dev`. The fixes and their tests are fork PRs; each PR names its probes and counter-checks.

**Who "dsf" is:** DCS, DWS, the regular DPS and every plugin without superUser run as the user dsf. So "the dsf user" below means any of them, e.g. a plugin or a compromised web server. The fork does not regard dsf as trusted with root.

### UB5. The root DPS installs whatever is in a predictable unpack directory

- **Cause:** `InstallSystemPackage` unpacks a combined ZIP package into `Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(PackageFile))` and `Directory.CreateDirectory` accepts an existing directory (DPS `Commands/InstallSystemPackage.cs:96-97`). Every file there except `update.sh` is installed, and `update.sh` is run, as root (`:101`, `:138-151`).
- **Why the name is known:** `PackageFile` is DWS's upload file, `Path.GetTempFileName()` = `/tmp/tmpXXXXXX.tmp`, visible for the whole upload (DWS `Endpoints/MachineEndpoints.cs:725-735`). No DSF unit sets `PrivateTmp`.
- **Effect:** a local process that can write /tmp (any local user, DWS, plugins with `abstractions/user-tmp` in their profile) gets its own packages or `update.sh` run as root during a system package update. Needs `RootPluginSupport` (DCS `Commands/Packages/InstallSystemPackage.cs:24-27`).
- **Origin:** 3b17835f (`InstallSystemPackage.cs:96-101`).
- **Fork fix (#231):** `Directory.CreateTempSubdirectory("dsf-package-")`, a new directory with a random name and mode 0700.
- **Test:** `DpsRootService.CombinedPackageIgnoresAPlantedDirectory`.

### FS4. The root DPS runs commands from settings that the dsf user may change

- **Cause:** the root DPS runs the package, Python, systemd-run and AppArmor commands of `/opt/dsf/conf/plugins.json` as root and loads profiles made from `/opt/dsf/conf/apparmor.conf`. The packages give both files to dsf:
  - duetcontrolserver `postinst:19-20`: `chown -R dsf:dsf /opt/dsf/conf`, `chmod -R g+wX /opt/dsf/conf` (335e8ce2a, 2020);
  - duetpluginservice `postinst:4-5`: `chown -R dsf:dsf /opt/dsf/conf/plugins*` and `chown dsf:dsf /opt/dsf/bin/pipInstall2.py`, the installer the root DPS runs for root plugins (e2bc260ae, 2021);
  - RPM `duetpluginservice.spec:74`: `%defattr(-,dsf,dsf,-)` over the settings, the DPS binaries and `pipInstall2.py` (d828b052e, 2021).
- **Effect:** the dsf user sets e.g. `InstallPackageCommand` or an `LD_PRELOAD` in `InstallPackageEnvironment`; the root DPS reads it at its next start, and the next plugin with an `sbcPackageDependencies` entry runs it as root. That plugin needs no root plugin support.
- **Fork fix (#234):** `/opt/dsf/conf` is root:dsf 1775, `plugins.json` and `apparmor.conf` root:root 0644 (also on upgrade and in the RPM specs), and the root DPS refuses to run commands from a settings file or template that is a symlink, missing, not root's or writable by others, or in a directory where others may replace it. `pipInstall2.py` already belonged to root in the fork (eaa840bd, PS8).
- **Limit:** an upgrade keeps a `plugins.json` that was changed before; the PR lists the keys to check.
- **Tests:** `RootServicePackaging.*` (runs the maintainer scripts against a temporary tree), `DpsRootSettings.*`.

### LX2. The root DPS runs in a directory the dsf user may write

- **Cause:** `duetpluginservice-root.service:9` sets `WorkingDirectory=/opt/dsf/plugins` (42a87449e, 2025), where dsf may create files. `Host.CreateDefaultBuilder` reads `appsettings.json` from the working directory before `plugins.json`, and `pipInstall2.py` runs `python -m venv` and `python -m pip` with `shell=True` and no `cwd`, so Python imports a `venv.py` or `pip.py` from there.
- **Effect:** settings keys of dsf's choice, or Python code run as root during the installation of a root plugin.
- **Fork fix (#234):** `WorkingDirectory=/opt/dsf/bin`, as DCS, DWS and the regular DPS have.
- **Test:** `RootServicePackaging.RootServiceRunsInADirectoryOnlyRootMayChange`.

### FS5. DCS reads every file with CAP_DAC_READ_SEARCH

- **Cause:** the packages give DCS `cap_sys_ptrace,cap_dac_read_search+ep` (duetcontrolserver `postinst:27`, 561d5c2ca; 77c3c748e). With the effective bit both are effective in every thread for the whole run, so every file that a code or IPC client names is opened as if DCS were root for reading.
- **Effect:** `M36.2 P"<file>" S<offset>` returns the raw bytes of any file on any volume (and through a symlink below one); M38, `fileread()`, M36/M37/M98/M32, file lists and `GetFileInfo` likewise. On the fork's printer image the SSH host keys are on a volume and could be read by any client that may send codes.
- **Why DCS has them:** CAP_SYS_PTRACE for the peer check of IPC clients (18b298656, 2020), CAP_DAC_READ_SEARCH for `statfs` of volumes (48d603297, 2020); 561d5c2ca made them file capabilities so that AT_SECURE is set. Today only the reads of `/proc/<pid>/exe` and `/proc/<pid>/auxv` in `Connection.IsDsfService` need them (`IPC/Connection.cs:185-215`, `DuetSharedLibrary/ProcessHelpers.cs:71-108`); `statfs` failures are caught.
- **Fork fix (#232):** `+p` instead of `+ep`. DCS raises both capabilities into the calling thread's effective set only around those two reads (capget/capset) and restores it. AT_SECURE is still set for `+p` (getauxval(3); probe), and DCS stays non-dumpable.
- **Behaviour change:** a mount below a directory dsf cannot search drops out of `volumes`.
- **Tests:** `ControlServerCapabilities.*` (unit and lab).

### LX1. Python plugins with dependencies run without their AppArmor profile

- **Cause:** the profile template attaches by path, `{pluginDirectory}/** {` (`apparmor.conf:4`, e2bc260ae), but such a plugin is started as `/bin/bash -c "{pluginDir}/venv/bin/python …"` (`plugins.json:22-23`, b28b25e75), and the venv interpreter is a symlink to `/usr/bin/python3.x`. Neither is below the plugin directory, so the profile never attaches. The interpreter rule `python{2.[4-7],3,3.[0-9]+}` (`apparmor.conf:13`, 1f187df23) does not match `python3.10` or later either, because `+` is a literal in AppArmor globs.
- **Effect:** on a system with AppArmor and an unconfined DPS (stock DuetPi), these plugins run as dsf without the permissions the user approved.
- **Fork fix (#233):** the template declares `profile dsf.{pluginId} {`, and DPS starts every plugin without superUser through `aa-exec -p dsf.<id> --`, so the profile applies from the exec on, wherever the binaries are. The interpreter rule matches the versions of Debian's `abstractions/python`.
- **Tests:** `DpsStartStopPlugin.PythonPluginIsLaunchedThroughItsVirtualEnvironmentInItsProfile`, `DpsAppArmorProfiles.ShippedTemplate*`. The runtime chain on a kernel with AppArmor was not tested.

### LX5. Every plugin may create, replace and delete every file in /run/dsf

- **Cause:** the template grants `/{,var/}run/dsf/** rw,` (`apparmor.conf:30`, e2bc260ae). /run/dsf holds `dcs.sock`, `dcs.err` and the endpoint sockets of all plugins.
- **Effect:** a confined plugin can unlink `dcs.sock` (DWS then cannot reach DCS, also not for DWC's emergency stop), bind its own socket there, or replace other plugins' endpoint sockets.
- **Fork fix (#233), partly:** every plugin gets only `dcs.sock rw`; plugins with RegisterHttpEndpoints also get the endpoint socket names DCS reserves (`AddHttpEndpoint.cs:50`).
- **Not fixed:** AppArmor mediates path sockets by file rules, and the `w` that connecting needs also allows unlinking, so any plugin can still remove or replace `dcs.sock`. Endpoint namespaces are not tied to their plugin (`AddHttpEndpoint.cs:28-30` refuses only the reserved ones). A fix needs plugins under their own uid or a socket in a directory they cannot write; that is a design decision.

### LX6. Programs that run as root follow symlinks of the dsf user

- **Cause:** root writes, chowns or chmods files in directories that dsf may change, without guarding against a symlink:
  - DuetPiManagementPlugin (a superUser plugin, so root): `M586` with HTTPS runs `gen-https-cert.sh` when `/opt/dsf/conf/https.pfx` does not exist; it writes `https.key`, `.crt` and `.pfx` with openssl and runs `chown dsf.dsf` and `chmod 660` on them (`gen-https-cert.sh:36-37`, 8852e8198). `M586` also writes `http.json` with `FileMode.Create` (`Network/Protocols/HTTP.cs:151`, 78c784a32).
  - duetwebserver `postinst:3-4`: `chown dsf:dsf` and `chmod 664` on `http.json` (0234537f2, bd0c91516, 2020).
  - reprapfirmware `postinst:4`: `chown dsf:dsf /opt/dsf/sd/sys/*.bin` (278bcc00b). /opt/dsf/sd belongs to dsf, so dsf may also replace `sys` itself; `chown -h` would not help.
  - duetpimanagementplugin `postinst:17` and `prerm:5`, `:16`: append to and `sed -i` on `plugins.txt`.
- **Effect:** dsf puts a symlink in place of one of these files (or of `sys`), and at the next M586, package configuration or removal root writes through it or gives the target to dsf, e.g. `/root/.ssh/authorized_keys`. Probed in Debian 12 and 13 containers for the package scripts.
- **Fork fix (#235):** files that belong to dsf in the end are made with the rights of dsf (`setpriv --reuid=dsf --regid=dsf --clear-groups`): the certificate script, the copies of the firmware files that replace dpkg's, and the changes to `plugins.txt`. The duetwebserver postinst uses `chown -h` and no chmod; DuetPiManagementPlugin deletes `http.json` and creates it with `FileMode.CreateNew`.
- **Limit:** dpkg itself follows a symlink in place of a directory below /opt/dsf/sd when it unpacks (dpkg(1), `*.dpkg-new`); dsf chooses the directory, not the names or content.
- **Tests:** `RootSymlinks.*` (runs the scripts against a temporary tree with stubs).

### Not fixed, for discussion

- **`RootPluginSupport` is decided by the dsf user.** It is a key in the dsf-owned `config.json` (DCS `Settings.cs:68`), and only DCS, which runs as dsf, enforces it. So dsf can allow root plugins and system packages for itself. The root DPS could take that decision from its own root-owned settings instead.
- **A confined plugin can make DWS read files outside its profile.** For `HttpResponseType.File` DWS opens the path the plugin returns as it is (DWS `Middleware/CustomEndpointMiddleware.cs:284-289`). DWS runs as dsf without a profile.
- **`pipInstall2.py` builds shell commands from requirements files without a check.** Lines of a requirements file in the plugin are not checked by DCS, the `git+` form accepts any text, and every command runs with `shell=True`. The installer runs as the user that runs the plugin, and pip runs package build code anyway, so this is no escalation, but it runs outside the plugin's profile.
- **Same class as LX6, not verified:** DuetPiManagementPlugin mounts for `M21 P… S…` as root on a mount point that DCS resolves lexically below the virtual SD card, and mount(8) canonicalizes the path; `DuetControlServer -u`, which the reprapfirmware postinst runs as root, writes the `StartErrorFile` named in the dsf-owned `config.json` (`DuetControlServer/Settings.cs:485`, `Program.cs:48`).

## Left out

**Fork code, or only partly upstream:**
- **#144:** fake firmware only. It mirrors RRF #43 (T1), and DSF itself needed no change.
- **#150, 16ee43db:** `SbcRestartTimeout` in the packaged config.json is a fork setting (#102).
- **#154 (a stop of DCS cut off a running firmware update):** the cut-off came with the fork's own 10 s shutdown timeout (ST4).
  - The upstream part: c5e3214b dropped v3.6's wait for a running update (`v3.6-dev Program.cs:178`, `:432`), and `LinkInterface.WaitForUpdate()` (`LinkInterface.cs:469-475`) has had no caller since.
  - In upstream today a flash still holds up the stop, because `PerformFirmwareUpdate` gets `ApplicationStopped` (`LinkService.cs:303`) and the host waits 30 s. systemd's 15 s limit applies as in v3.6. This part comes from reading the code and was not tested.
  - #154 waits on `FirmwareUpdateLock` for up to 2 min and extends systemd's timeout with `EXTEND_TIMEOUT_USEC` while it waits.
- **#151, 0c1f113a in part:** the fork's first version of `Path.IsInside` resolved its base, which let a symlinked `dsf` directory through. That was fork code. The upstream part is in PS7.
- **#151, 7d180870 and 43c33657 (#148):** test seams, no behaviour change.

**Resolved upstream:** H13, `rr_delete` with `recursive` (5130ca43).

## Reproducing

- **Single test:** `dotnet test src/UnitTests --filter "Name=<test>"` with the test names above. All of them run in the fork's default suite.
- **Lab printer:** the tests of G2, T3, I1 and T2 are in `src/LabPrinterTests` (`RefusedMacroPaths`, `CodeBufferReserve`, `PluginPermissions`, `AbortsCrossingMacroRequests`). The run of 2026-11-08 is logged in `design/lab-test-plan.md`, section 14.
