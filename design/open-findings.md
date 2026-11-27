# Open findings

The fork has no issue tracker, so findings to solve later are kept here. Remove an entry when it is solved.

Status: 2026-11-07. Line numbers refer to the Meltingplot fork: DSF `v3.7-dev` at 4c8b3fec and RRF `3.7-dev` at fc939b3.

## Codes after a macro call on the same line still run

**Documented, not a bug:** G and M cannot be macro parameters. Wiki `Gcode_meta_commands.md:374-376`: P and R cannot be used as macro parameters. A custom G-code macro must not get G, M, N or T, and a custom M-code macro not G, M or N, because "RRF will treat the parameter as being the start of the next command". This entry first said the wiki has no such exception; Tim pointed out these lines on 2026-11-08.

So `M98 P"x.g" G7` passes no `param.G`, and the `G7` becomes a code of its own, as documented. What stays open is that DSF then runs this code, and that DSF and RRF split differently after `'`.

**Observed** with the DSF parser against the fake firmware, not on hardware:

| Input | Sent to the firmware |
|---|---|
| `M98 P"t.g" G7` | `M98 P"t.g"`, then `G7` |
| `M98 P"t.g" M7` | `M98 P"t.g"`, then `M7` |
| `M98 P"t.g" S1 G7 S2` | `M98 P"t.g" S1`, then `G7 S2` |
| `M98 P"t.g" 'G7` | `M98 P"t.g"`, then `G7` |
| `M98 P"t.g" 'g7` | one code, with the lowercase parameter `g7` |
| `M98 P"t.g" G"x"` | error: Failed to parse major G-code number (x) |
| `M98 P"t.g" G{7}` | error: Dynamic command numbers are only supported for T-codes |
| `M98 P"t.g" T7` | one code, `T` is a parameter |
| `M98 P"t.g" T7 T8` | `M98 P"t.g" T7`, then `T8` |
| `M98.1 S0 P"t.g" G7` in a trigger macro | `M98 P"t.g"` on HTTP, `G7` on Trigger |

The separated code runs after the macro has finished (`M98 P"t.g"`, the codes of the macro, then `G7`). `M98.1` splits the same way; the separated code runs on the calling channel while the macro starts on the other one.

**Code and documentation:**
- DSF: `StartsNextCode` ends a code at G, M or a second T (`src/DuetAPI/Commands/Code/Parser.cs:727-730`). The same rule is in upstream `v3.7-dev`. For `'G` it keeps the uppercase letter, so that escape does not prevent the split.
- RRF: `StringParser::FindParameters` also ends a command at an unquoted G or M, but not after `'` (`StringParser.cpp:1118-1166`).
- Wiki `Gcodes.md:233`: each G or M preceded by a space starts a new command, so the split itself is documented.
- Wiki `Gcodes.md:235`: a command that invokes a macro must be the last on its line, "because any following commands on the same line will not be executed". DSF does send them, after the macro. What RRF does without an SBC is not checked.
- The other letters that the wiki rules out still reach the macro in both parsers. That needs no change, as far as the parsers go:
  - RRF leaves only `P` out of M98's parameters (`StringParser.cpp:2115`, `BinaryParser.cpp:980`). So `R` arrives as `param.R` when `P` is present; without `P`, `M98 R` marks the macro restartable (`GCodes2.cpp:1724-1732`).
  - `N` is a line number only at the start of a line (DSF `Parser.cs:441-443`, RRF `StringParser.cpp:135-142`). Later in the line it is a parameter.
  - `T` in a custom G-code is a parameter in both, unless the code already has a `T` (`Parser.cs:730`).

**Open questions:**
1. Should DSF refuse a G or M after a macro call on the same line instead of running it? That would match `Gcodes.md:235` and catch the mistake.
2. Should the `'G` difference between DSF and RRF go to Duet3D?

**Note for chx350-config:** a session there counted on 2026-11-06 that no M98 call passes G or M. The same count found macros that read `param.M`. By `Gcode_meta_commands.md:376` such a macro cannot get `param.M`, neither through M98 nor as a custom M-code, so those branches never run.

## Macros do not start on the USB channels without a USB host

**Problem:** with no USB host connected, a macro sent to the USB or USB2 channel never starts and the channel stays busy. DSF reported success for the call. That is why `M98.1` leaves out USB and USB2 (#108).

**Observed** on the lab printer (MB6HC, RRF 3.7.0+1.mp.7, DSF side-loaded from 5ffadeca) with the former `M98.1` and `M98.7` (USB and USB2 as numbered by `M118 P`, before `M98.1 S`), during about 10 s per channel:
- The trigger macro ran 19 times on USB and 20 times on USB2.
- The first call returned `result` 0, all later ones `result` 1. The macro never ran, and `inputs[3]` and `inputs[14]` never showed a stack depth above 0.
- Aux, HTTP, Telnet and Aux2 worked in the same run.
- Not checked: whether the channel recovers by itself after a longer time. The DCS restart at the end of the run cleared it.

**Likely cause, read from the code and not proven:**
- `GCodes::Spin` calls `usbInput->Spin()` and `usb2Input->Spin()` on every pass (`GCodes.cpp:509-514`).
- While no host is connected, `BufferedStreamGCodeInput::Spin` calls `gb.DiscardPartialLine()` (`GCodeInput.cpp:249-266`).
- `StringParser::DiscardPartialLine` calls `Init()` unless the buffer is `parseNotStarted` or `ready` (`StringParser.cpp:53-59`). That would also reset a code that came from DSF, although it is not a partial line, and DSF would then wait for a reply that never comes.

`GCodeInput.cpp`, `StringParser.cpp` and `GCodeBuffer.cpp` are identical in the fork and in upstream `3.7-dev` at f8c8a1d69f, so this is not fork code.

**Possible wider effect, not observed:** any code DSF sends on these channels may hang, not only M98. DSF moves a priority code to any idle channel if none with the same compatibility is idle (`CodeProcessor.cs:223-236`), which can include USB.

**Wanted (Tim, 2026-11-06):** with a USB host connected, the USB channels should be usable with `M98.1`.

- By the code above this should already work while the host has the port open, because the reset only runs while `IsConnected()` is false. Not tested: the lab printer has no USB cable to the SBC.
- `inputs[]` does not say whether a host is connected. Only the text reply of `M575 P0` and `M575 P1` does (`Platform.cpp:2412-2425`), so DSF would have to send that code and parse its reply before every `M98.1` or `M98.7`.

**Proposed fix, not built or tested:** in RRF, leave codes from DSF alone in `GCodeBuffer::DiscardPartialLine`, in the style of `GCodeBuffer::Enable`:

```cpp
IF_NOT_BINARY(stringParser.DiscardPartialLine());
```

A partial line can only be a text line, because `GCodeBuffer::Put` clears `isBinaryBuffer` (`GCodeBuffer.cpp:295`). The USB channels would then run codes from DSF with or without a host, so `M98.1` would not have to depend on that.

**Next steps:**
1. Build the fix and repeat the lab test on USB and USB2 without a host. Repeat it with a host that has the port open, which needs a USB cable.
2. Add `CodeChannel.USB` and `CodeChannel.USB2` (`S3` and `S14`) back to the channels `M98.1` accepts in `MCodeHandler.cs`. Do this only together with an RRF release that has the fix, otherwise a call without a host returns success and the channel stays busy.
3. Report it to Duet3D with the versions above, as the files are unchanged against upstream.

## M98.1 from PanelDue or a trigger action does not reach DSF

**Problem:** DSF implements `M98.1`, so only codes that pass through DSF can use it: macros that DSF runs (trigger macros and `daemon.g` in SBC mode) and codes from DSF clients such as DWC. A code that RRF reads itself never gets there, e.g. one typed on a PanelDue (Aux) or written as the action of an `M581.1` trigger. RRF then answers `M98.1: Command is not supported` with `result` 1, while the macro starts nowhere.

**From the code** (RRF fork `3.7-dev` at 0d4295065):
- RRF passes M98 to DSF only with `R` (`GCodes2.cpp:766`).
- Any other M-code with a fraction that RRF does not implement goes to `TryMacroFile` (`GCodes2.cpp:797-810`). That looks for `sys/M98.1.g` and otherwise returns `warningNotSupported` (`GCodes2.cpp:4946-4966`).
- `warningNotSupported` replies `Command is not supported` (`GCodes2.cpp:5122`) and gives `result` 1 (`ExpressionParser.cpp:1863`).

Upstream `3.7-dev` at 371c71412 has the same lines (`GCodes2.cpp:710`, `:753`, `:4848-4868`, `:5024`).

**Wanted:** Duet3D/RepRapFirmware#1337 asks for RRF to forward `M98.1` to DSF in SBC mode "the same way `M98 R` is today". Tim (2026-11-25): do it later in its own RRF PR, not together with `M98.1 W`.

**Next steps:**
1. In the RRF fork, add `M98.1` to the codes passed to DSF in `GCodes2.cpp:766`.
2. Check that a forwarded code from a trigger action or from Aux keeps its caller context. Its expressions must be evaluated in that context.
3. Lab test from PanelDue and from an `M581.1` action.

## Findings of the fuzz and coverage tests (2026-11-07)

The fuzz and coverage tests of #121 to #127 found 48 bugs and deviations, the trigger tests of #129 found 9 more, and the stop path tests of #130 another 4.
- **Fixed and removed from this list (2026-11-08):** H1 (#136; replaced by upstream's `FilePathResolver.IsVolumeRoot` with the merge of 61df011e on 2026-11-13, see UP7), G2 (#132), I1 (#135), T2 (#134), T3 (#133), P1 and P2 (#137), the extended-link part of P4 (#138), H13 (upstream 5130ca43 through #143) and T1 (Meltingplot/RepRapFirmware#43 with #144). The lab printer showed each fix, and the stock build showed the bug (`design/lab-test-plan.md`, section 14, 2026-11-08).
- **Fixed and removed from this list (2026-11-10):** O2 (#176; a value that does not fit the property type threw FormatException or IndexOutOfRangeException instead of `JsonException`). What stays: DCS skips such a property and logs it once per type (`DuetControlServer/Model/ObjectModel.cs:173-185`), but an item of a value list is not skipped in `UpdateFromJsonReader` (O1), and `UpdateService.ExecuteAsync` logs a JsonException and throws it again (`UpdateService.cs:373-376`), which ends its update loop. O6 (#177; an object with two different type keys, e.g. two `name` in `move.kinematics`, ended the process with a stack overflow in `UpdateFromJsonReader`. The last key decides now, as in `UpdateFromJson`; Kinematics, FilamentMonitor and DirectDisplayScreen). O7 (#179; a filament monitor or screen whose type key was spelled in another case or given as its number got the base class, while its `Type` or `Controller` property got the value, so its own properties were missing and a model made from its JSON differed. The class now follows the value that the property reads).
- **Fixed and removed from this list (2026-11-11):** I3 (#181; an Executed interceptor with filters and AutoFlush, the DuetAPIClient default, blocked its channel for good, also after its client had exited; seen on the printer. The flush for a code in the Executed stage waited for that stage itself, `ChannelProcessor.FlushAsync`. It waits for nothing now, because nothing is ahead of such a code; this also covers the Flush command of an Executed interceptor).
- **The others:** each has an `[Explicit]` test in `src/UnitTests` that reproduces it and fails today. Run one by its exact name, e.g. `dotnet test src/UnitTests/bin/Debug/net10.0/UnitTests.dll --filter "FullyQualifiedName=UnitTests.Codes.TriggerMacros.TriggerChannelWorksAfterInvalidationDuringTriggerRequest"`. Once a finding is fixed, remove the `[Explicit]` of its test and this entry.

- **Line numbers:** DSF `v3.7-dev` at c6d225f2. The production code there is the same as at d576ee9e, where the tests started. Entries added on 2026-11-08 cite DSF at 9e6449d1. RRF `3.7-dev` at cde4e4fa1 (3.7.0+1.mp.8).
- **Printer:** "printer" marks findings that were confirmed on the lab printer on 2026-11-07, with DSF 3.7.0+mp.13 and RRF 3.7.0+1.mp.8.
- **Origin:** git blame puts all of them on upstream code, except the two DuetHttpClient findings in H14 and H15, the fork's extended SPI link in S1 and S5, and the fork's sequence guard in S4.

### Highest priority

1. **O8** A DCS that cannot serialize its object model keeps running but cannot be used, and nothing ends that state. Printer.
2. **P4** (plain link) A trigger macro can get lost after a reconnect while RRF keeps running. The fix is case 2 of #57, which is on hold.

### SBC link (#122)

- **S1. Two bit errors in the first transfer after the activation need a reconnect, which RRF follows with an emergency stop.**
  - In that transfer DSF still answers with regular response codes. A second flip turns BadHeaderChecksum (5) into 4, and RRF answers BadResponse (`DataTransfer.cpp:1173`).
  - DSF then gives up (`SPI.cs:1832-1836`) and leaves the extended link without telling RRF (`EndSession`, `SPI.cs:479-483`). RRF cannot notice, because DSF has not sent an extended sequence number yet (`DataTransfer.cpp:838-852`).
  - Only transfers with data are affected. The design claims single faults only for this transfer.
  - Tests: `Fuzz.SpiLinkFuzz.TwoBitErrorsInRrfSwitchTransfer`, `RrfSwitchTransferRepairsTwoBitErrors`.
- **S2. An RRF that does not poll while disconnected can stay stuck for good after two bit errors.**
  - RRF resets the connection without a full reset, so TfrRdy stays low (`DataTransfer.cpp:1258`, `1434-1438`).
  - DSF then waits for TfrRdy high (`SPI.cs:1693-1711`), and the RRF waits without a timeout (`SbcInterface.cpp:171`).
  - The MB6HC polls (`SUPPORTS_SBC_OVER_USB`, `Pins_Duet3_MB6HC.h:43`) and is not affected. Duet 2 SBC builds are (`Pins_DuetNG.h:45`).
  - Test: `Fuzz.SpiLinkFuzz.StuckWithoutPolling`.
- **S3. RRF never recognises `ObjectModelKeyChanged`.**
  - DSF writes a `StringHeader` before the key (`SPI.cs:1655-1670`, `Writer.cs:729`). RRF reads the key from the start of the data (`SbcInterface.cpp:1298-1310`).
  - As a result, `NetworkUpdated()` and `VolumesUpdated()` are never called from the SBC.
  - Test: `SPI.LinkServiceOverSpi.ObjectModelKeyChangedIsReadByRrf`.
- **S4. A lost connection while the sequence guard resends a transfer can send a transfer of the old session.** From reading the code, not tested.
  - The guard saves the pending transfer and sets `_resending` (`SPI.cs:1989-2002`). Only a completed transfer clears it again, after it has put the pending transfer back (`:408-416`).
  - Neither the lost-connection catch (`:442-470`) nor `EndSession` (`:501-512`) clears it. After the reconnect, the first completed transfer therefore swaps in the pending header of the old session, with its extended sequence number, although DSF has left the extended link.
- **S5. A lost connection in the switch window of the extended link gives RRF a regular number it takes in sequence.** From reading the code, not tested.
  - In the window, RRF has switched and DSF has not (`_extendedRequested` without `_extendedTx`, `SPI.cs:422-433`).
  - The catch advances the sequence number only when DSF was on the extended link (`leavingExtendedLink = _extendedTx`, `:454`), so the fix of #138 does not apply there. This is the same cause as the plain-link part of P4.
- **Fixed and removed from this list (2026-11-13):** S6 (upstream 61df011e, merged 2026-11-13; over USB, DSF looked for a packet that RRF asked for again in the wrong TX buffer). Upstream also gave USB the resend guard of SPI; S8 below is not changed by it.
- **S7. RRF drops a code reply when its shared reply FIFO is full, and the later replies of that channel shift by one.** From reading the code, not observed.
  - One 64-entry `OutputStack` holds the replies of all channels (`RRF/SBC/SbcInterface.h:155`, `RRF/Platform/OutputMemory.h:15-19`). On a full stack, `Push` releases the buffer and logs `OutputStackOverflow` (`RRF/Platform/OutputMemory.cpp:487-506`).
  - DSF matches replies by position (`Processor.cs:1108-1117`). Catalogue NEW-2; not addressed by the channel-epoch requirements (Q2, evidence gate EG-1).
- **S8. A code that RRF refuses for lack of buffer space can be overtaken by a later code of its channel.** From reading the code, not shown.
  - DSF writes the next code into the following transfer before it reads the resend request of the refused one, and RRF may accept the later one. Whether RRF can refuse a code that DSF's buffer estimate allowed is not shown. Catalogue NEW-3; evidence gate EG-3 of the channel-epoch requirements.

### HTTP: DuetWebServer and DuetHttpClient (#123)

- **Fixed and removed from this list (2026-11-24):** H3 (PR #222; a failed `rr_upload` with a wrong crc32 deleted the file it was meant to replace, and an upload under the name of the file being printed truncated it under the job, census FS2). rr_upload writes `name.part` and moves it over the file only on success, as PUT /machine/file and RRF do (`RepRapFirmwareEndpoints.cs:258-298`, `MachineEndpoints.cs:269-291`, RRF `UploadingNetworkResponder.cpp:59-62, 108-128`); a running job keeps reading its old file. Tests: `WebServer.WebServerFindings.FailedUploadKeepsExistingFile`, no longer explicit, and `UploadKeepsContentOfOpenFile`.
- **H2. Expired sessions stay in DCS for good when many expire at once.** Printer: 40 parallel logins left 4, each logged "Failed to unregister … (11) Resource temporarily unavailable".
  - Each expiry opens its own IPC connection (`SessionStorage.cs:404, 420-422`). The DCS backlog is 4 (`Settings.cs:116`, also in the printer's `config.json`), and there is no retry.
  - Test: `WebServer.WebServerFindings.ExpiredSessionsAreRemovedFromDcs`.
- **H4. POST /machine/file/move with an invalid form gives an unhandled 500.** Printer. `ReadFormAsync` sits outside the try (`MachineEndpoints.cs:397`). Test: `MoveWithInvalidFormIsHandled`.
- **H5. PUT /machine/directory or /machine/file with `?` plus a non-ASCII character gives an unhandled 500, but creates the item anyway.** Printer. `UrlPathEncode` stops at `?` (`MachineEndpoints.cs:294, 504`). Test: `CreatedLocationIsEncoded`.
- **H6. DWS never answers a WebSocket close frame, so clients see 1006.** Printer. `WebSocketEndpoint.cs:283-287`, `CustomEndpointMiddleware.cs:156-160`. Test: `ClientCloseIsAnswered`.
- **H7. No close message on the WebSocket when DWS stops.** A cancelled task is not faulted (`WebSocketEndpoint.cs:223-227, 240-243`). Test: `ShutdownClosesTheSocket`.
- **H8. rr_ endpoints registered while DWS runs end up at /machine/rr_/{path}.** `ModelObserver.cs:120` vs `:178`. Test: `RrEndpointRegisteredAtRuntime`.
- **H9. rr_connect always reports `sessionTimeout` 8000** instead of the setting (`RepRapFirmwareEndpoints.cs:112`). Test: `RrConnectReportsConfiguredSessionTimeout`.
- **H10. rr_gcode returns `{"bufferSpace":255,"err":0}` instead of `{"buff":N}`.** Printer. RRF: `HttpResponder.cpp:566`. This is why the PollConnector cannot send codes to DWS. Test: `RrGcodeReportsBuff`.
- **H11. rr_disconnect returns 204 instead of `{"err":0}`.** Printer. Test: `RrDisconnectReturnsErr`.
- **H12. Every rr_connect adds another session for the same client.** Printer: 5 → 8. RRF keeps one per IP (`HttpResponder.cpp:803-808`). Test: `RrConnectKeepsOneIpSession`.
- **H14. DuetHttpClient's RestConnector reconnects with a new session every PingInterval while the model is quiet.** Cancelling the receive for the PING aborts the WebSocket (`RestConnector.cs:294`). Test: `WebServer.DuetHttpClientTests.RestKeepsAQuietWebSocket`.
- **H15. DuetHttpClient's PollConnector never loads keys whose sequence number is still 0** (`PollConnector.cs:320-330`). RRF starts every sequence number at 0 (`RepRap.cpp:450`). Test: `PollLoadsKeysWithSequenceZero`.

Open questions:
- A missing or invalid session key gives 401, in the code and on the printer. The REST API wiki says 403.
- DWS session keys are not bound to the client's IP address, as RRF's are.
- Requests to custom endpoints reach the plugin without a session (SessionId -1), which `ReceivedHttpRequest` documents.

### G-code, macros and jobs (#124)

- **Fixed and removed from this list (2026-11-13):** G1 (upstream 61df011e, merged 2026-11-13; `M122 P{0}` got no reply and blocked its channel). Test: `Codes.ExecutedHandlerErrors.DiagnosticsWithExpressionParameter`, no longer explicit.
- **Fixed and removed from this list (2026-11-23):** G9 (upstream d108e9767, merged 2026-11-23 with cacdcaf3d; M32 inside a job still ran the codes that were read ahead). `Code.ExecuteAsync` keeps the job's token now, so `JobProcessor.Cancel` cancels them. Test: `Codes.JobFiles.SelectFileFromJobStopsFirstJob`, no longer explicit.
  - The entry had warned that the job's token would also cancel the codes before a line that cannot be read, because the read catch of `DoFilePrint` aborted the job at once. The merge showed it: a job with `else` in line 5 ran only `M400` of lines 1-4 (3 of 3 runs; 3 of 3 passed before the merge). Fixed in the same PR as for macros in #204: `DoFilePrint` stops reading and aborts the job once the codes before that line are done. Test: `Codes.JobFiles.CodesBeforeLineThatCannotBeReadStillRun`.
- **G3. Drive numbers map to every mounted file system** (open question, upstream design).
  - `PeriodicUpdateService` lists every mount as a volume (`PeriodicUpdateService.cs:526-583`), and `n:/` resolves to `volumes[n].path` (`FilePathResolver.cs:80-119`).
  - On the CHX that gives 25 volumes, e.g. `0:` = `/`, `4:` = `/home`, `5:` = `/var`, `6:` = `/etc/ssh`.
  - File codes can therefore reach any mount, as far as the rights of the dsf user allow.
  - The root check of the H1 fix (#136) refuses only the root of the drive that a path names (`FilePathResolver.cs:64-77`, at 9e6449d1). On the CHX, plugin directories are mounted below `/opt/dsf/sd`. A recursive delete of such a mount, e.g. `DELETE /machine/file/0%3A%2F<mount>?recursive=true`, can therefore still empty it.
- **Fixed and removed from this list (2026-11-13):** G4 (upstream 61df011e, merged 2026-11-13; a line feed after the drive prefix truncated the path). Test: `File.PathResolution.LineFeedAfterDrive`, no longer explicit; `Fuzz.FilePathFuzz` no longer leaves such paths out.
- **G5. `MetaExpressionParser` throws FormatException or OverflowException instead of `CodeParserException`.**
  - Inputs: `1e`, `2E-`, or `0x` with more than 16 digits (`MetaExpressionParser.cs:1345, 1379`).
  - The client then gets a raw exception.
  - Tests: `Machine.MetaExpressionParserErrors.ExponentWithoutDigits`, `HexNumberTooLong`.
- **G6. `FileInfoParser` throws on a single malformed line, so the file cannot be printed with M32, and M36 returns `err:1`.**
  - Affected lines: an unterminated string, `0.5 E2`, or `G1 Z` without a value (`FileInfoParser.cs:149, 205, 221`). `JobProcessor.cs:311` has no catch.
  - RRF scans for keys and catches errors (`FileInfoParser.cpp:425-455, 911-918`).
  - Test: `File.InfoParserErrors.MalformedLine`.
- **G7. The M37 simulation overwrites files shorter than 64 bytes.** The seek to the end is skipped (`FileInfoParser.cs:931-944`). Test: `Codes.JobFiles.SimulationOfShortFile`.
- **G8. A read error in a job names the next line.** `readCode` is null, so `file.LineNumber` is used (`JobProcessor.cs:507`). Test: `ErrorInJobNamesTheFailingLine`.
  - The same happens in a macro: the read catch of `MacroFile.RunAsync` also reports the file's `LineNumber` (`MacroFile.cs:418-419` at 3db68bd7). An `else` in line 11 is reported as line 12 (seen in `Codes.MacroCodeAfterError`, 2026-11-23).
- **G10. `fileread` differs from RRF's `ReadArrayFromFile`.** RRF: `ExpressionParser.cpp:1192-1358`, `Configuration.h:281`.
  - It allows 50 elements instead of 40.
  - It refuses length 0.
  - It reads no hex values.
  - It accepts `'ab` without the closing quote.
  - It does not check skipped elements.
  - It joins `1 2` into 12.
  - Tests: `Codes.MetaExpressions.FileRead*` (6).
- **G13. DSF refuses integer parameters that RRF reads.** From reading the code, not tested on RRF.
  - RRF reads an integer with `StrToI32`, which takes no exponent (`NumericConverter.cpp:244`, only with `AcceptFloat`) and clamps values that are too large to `Int32Max` or `Int32Min` (`SafeStrtod.cpp:96-117`). So `S31e9` is 31 and `S99999999999` is 2147483647 (`StringParser.cpp:2039-2052`).
  - DSF parses `31e9` as a float and `99999999999` as a long, and refuses both for an int parameter (`Parameter.cs`, G12 in `dsf-findings-upstream.md`).
- **G11. M30, M470 and M472 fail with an exception when DSF refuses their path.** Same kind of bug as the fixed G2, but the channel goes on.
  - They resolve the path before their try (`MCodeHandler.cs:419`, `:850`, `:907`, with the try at `:426`, `:851`, `:914`, at 9e6449d1). M471 resolves inside its try (`:869-871`).
  - The exception then ends in the catch of the internal processing stage (`ProcessInternally.cs:32-39`): it is logged as "Failed to execute code … on internal processing stage", and the client gets the exception instead of an error reply.
  - Test (passes and pins today's behaviour): `Codes.FileCodes.DeleteRootDirectory`, which expects the `ArgumentException` "Delete root folder is not allowed" from M472.

Open questions:
- `fileread` and `echo` file errors reach the client as raw IOException (`Functions.cs:111`, `KeywordHandler.cs:149`).
- `SimpleCode` returns at the first exception while later codes keep running (`SimpleCode.cs:173-201`).
- `FunctionsInitializer` is never registered.
- M36.1 and M36.2 copy file bytes into the JSON unescaped, as RRF does (`RepRap.cpp:1648, 1661`).

### IPC server (#125)

- **Fixed and removed from this list (2026-11-13):** I2 (upstream 61df011e, merged 2026-11-13; a wildcard filter longer than the code made short codes fail). Tests: `IpcInterceptionStages.WildcardFilterLongerThanAComment`, `WildcardFilterLongerThanTheCodeOutlivesTheClient`, `IpcInterception.WildcardFilterLongerThanTheCode`, no longer explicit.
- **I4. Code stream replies come out of order when BufferSize is above 1** (`CodeStream.cs:179-220`). Test: `IpcCodeStreamReplies.RepliesStayInTheOrderOfTheCodes`.
- **I5. A failing code in a code stream cancels the next code, and its reply is lost.** The code object is reused (`CodeStream.cs:169, 212-219`, `CodeProcessor.cs:277-285`). Test: `FailedCodeKeepsTheReplyOfTheNextCode`.
- **I6. `job.layers` still reaches subscribers with API version 10 or older.** `nameof(Layers)` is compared with the JSON name "layers" (`ModelSubscription.cs:495`). Test: `IpcSubscriptionConsistency.JobLayersAreNotSentToOutdatedClients`.
- **I7. A DPS crash during a command closes the client's connection without a response** (`PluginService.cs:279`, `Command.cs:126-130`). Test: `IpcPluginServiceLifecycle.CommandFailsWithAnErrorWhenThePluginServiceDiesWhileRunningIt`.
- **I9. A code stream that is refused for its buffer size stays registered.** The constructor registers it first. Test: `IpcCodeStream.RefusedStreamIsNotRegistered`.
- **I10. A failing command while intercepting ends the connection** instead of returning an error response like the Command mode. Test: `IpcInterception.FailingCommandWhileInterceptingIsReported`.
- **I11. An undefined interception mode is acknowledged and then ends with a KeyNotFoundException.** Test: `IpcInterception.RefusesUndefinedInterceptionMode`.
- **I12. The plugins of a terminated plugin service keep Pid 0 (being shut down) instead of -1 (not started).** Test: `IpcPluginService.PluginsOfATerminatedServiceAreNotStarted`.
- **I13. When a plugin service terminates, its remaining plugins are not stopped.** StopPlugins starts with the processor's token, which is already cancelled. Test: `IpcPluginService.RemainingPluginsAreStoppedWhenAServiceTerminates`.
- **I14. A subscription stays registered if its success response cannot be sent.** Test: `IpcSubscription.SubscriptionIsUnregisteredWhenTheClientIsGoneBeforeTheResponse`.
- **I15. Subscribers of the SBC, Daemon, Trigger and other non-generic channels never get the messages for their channel.** Test: `IpcSubscription.TargetedMessagesReachSubscribersOfOtherChannels`.

Open questions:
- A code stream sends no reply line for exceptions other than `CodeParserException` and `ArgumentException`, e.g. `if true` (`CodeStream.cs:195`).
- An Executed-stage Cancel is logged as an error (`Executed.cs:235-238`).
- Upstream doc error: the command table of `src/Documentation/articles/ipc.md` lists `CheckPassword` under `CommandExecution`. The command needs `ObjectModelRead` or `ObjectModelReadWrite` (`IPC.RequiredPermissions.EveryCommandRequiresTheDocumentedPermissions`). #135 corrected only the package row of that table.

### Object model (#125)

- **Fixed and removed from this list (2026-11-11):** O3 (#184; a number beyond the range of a float was read as infinity and stored, after which the model could not be serialized, so GetObjectModel, new subscriptions and the WebSocket of DWS failed until the property got a finite value. Since the merge of upstream 61df011e (2026-11-13) the update clamps such a float to ±float.MaxValue as upstream does (`JsonFloatExtensions.GetFiniteSingle`); a double is still not clamped, see UP5. From RRF it comes for a value in the top 0.01 % below the float maximum (about 3.4025e38 and above) that is reported with 3 decimals, e.g. a tool offset, which RRF rounds to "3.403e+38" (`RepRapFirmware.cpp:168-195`, `Tool.cpp:83-88`), otherwise through PatchObjectModel. Seen on the lab printer on 2026-11-11 with `G10 P0 X{1.7013e38 * 2}`, see O8).
- **Fixed and removed from this list (2026-11-11):** O4 (#185; a global or plugin data value with a string that is not valid UTF-8 was stored as it came, after which the model could not be serialized: GetObjectModel, new subscriptions and every new WebSocket of DWS failed until the value changed. The dictionaries now write a value with `WriteTo`, as the patches of a subscription do, so the invalid bytes come out as the replacement character U+FFFD. In the same PR: the handler of DCS for values that fail to be deserialized threw while it logged a string with invalid UTF-8, so the first such value per type ended the update of its key and later ones were skipped without a log line, `DuetControlServer/Model/ObjectModel.cs`). RRF still produces such strings from valid input, because its expressions cut strings by bytes: `"é"[0]`, `take("é",1)` and a concatenation beyond 255 bytes (`RRF/GCodes/GCodeBuffer/ExpressionParser.cpp:346-367`, `:1082-1098`, `:803-808`; read, not run on RRF).
- **O1. With a deserialization handler, the two JSON update paths treat a scalar list item differently.** `UpdateFromJson` keeps it, `UpdateFromJsonReader` drops it and shifts the rest (`StaticModelCollection.cs:150-181` vs `220-267`). Test: `Fuzz.ObjectModelJsonFuzz.ScalarListItemIsSkippedTheSameWayByBothUpdateMethods`.
- **O8. A DCS that cannot serialize its object model keeps running but cannot be used, and nothing ends that state.** Requirement of Tim, 2026-11-11: DCS must run into a timeout in such a state, because the machine cannot be used. Not designed yet.
  - Printer, 2026-11-11 09:29:48 to 09:34:56 UTC (DSF 3.7.0+mp.13, RRF 3.7.0+1.mp.8): `G10 P0 X{1.7013e38 * 2}` set a tool offset that RRF reported as `3.403e+38` (`M409 K"tools[0].offsets"`: `[3.403e+38,0,0,0]`), which DSF read as infinity (O3).
  - DCS kept running: same process, link up, RRF idle, and codes still went through `POST /machine/code`. Every serialization of the model threw "…infinity cannot be written as valid JSON": 424 errors in under 3 minutes, as "Failed to execute GetObjectModel" and "Terminating connection due to unexpected exception".
  - `GET /machine/model` gave HTTP 500, and DWS closed every WebSocket again, so DWC could not be used.
  - The QualityAssurance plugin ended with "DSF closed the connection" at its object model subscription and was restarted by DCS every 3 s, 91 times in 5 minutes.
  - Nothing recovered by itself: no timeout, no restart. `G10 P0 X0` restored it; the model was served again within a second.
  - The two known causes are fixed (O3 by #184, O4 by #185). What is open is the state itself: any other value that makes the model unserializable has the same effect. Open questions for the design: what detects the state (a failed serialization of the full model, repeated failures of model requests), what the timeout does (DCS stops, so that systemd restarts it and RRF notices the lost connection; DCS drops the key that cannot be written; DCS reports an error state), and how that fits the rule that a stop must still reach RRF.
- **O5. `UpdateFromJsonReader` stores "[" for an array in a property of type object** and reads the array items as members of the parent (`DuetAPI.SourceGenerators/ObjectModel/ModelObject/UpdateFromJsonReader.cs`). Test: `ObjectPropertyWithArrayIsReadLikeUpdateFromJson`. With an object as the value its members are read as members of the parent as well: `state.messageBox.default` set to a copy of the message box ends as null, because the copy's own `default` is null. `ObjectModelJsonFuzz.StructurallyMutatedJson` with seed 6 and 50000 inputs fails with it (iteration 6770, 2026-11-10); the fuzz test only lets the cases through that end with "[" or "{".

### Triggers (#129)

How the CHX 350 uses triggers, read from the lab printer:
- Pin triggers `M581 T2` (external E-stop input) and `T4`/`T9` (doors).
- Expression triggers (`M581.1` T3, T5-T8, T10) that RRF evaluates itself.
- Their macros `sys/triggerN.g` run on the Trigger channel, served by DSF. `SbcTriggerService` is not involved there.

Measured in the fake firmware without link faults:
- DSF starts a trigger macro one transfer after the request.
- trigger2.g's M112 reaches RRF 22-24 transfers after the request. That is 18-22 ms on the WSL VM, with a three-code stand-in for set-led-color.g.

- **T11. The daemon.g request one transfer after InvalidateChannel is fixed in RRF by Meltingplot/RepRapFirmware#47; the lab test has no result for it yet.**
  - Checked on 2026-11-11 against DSF `v3.7-dev` 412e5ff6 and RRF `3.7-dev` 5c0cf3b8f: RRF writes nothing for a channel between the record and the apply of an abort (`RRF/SBC/SbcInterface.cpp:1611-1648`), and the routine of the apply voids an unsent macro request (`:1967`). This holds for every DSF. A DSF with the epoch also drops a stale ExecuteMacro by its tag (`LinkService.cs:930-935`).
  - Tests against the fake firmware: `RecordApplyWithDsf.DaemonChannelWorksAfterAnAbortCrossingADaemonRequest`, `ChannelEpochStress.DaemonCaseHasNoSymptoms`. Blocks of the Daemon channel per 2000 aborts in the fake firmware (`ChannelEpochStress.Measure`, 2026-11-11): 43 with the v7 model, 0 with the record/apply model without tags and with the epoch pair.
  - Open: the lab test `AbortsCrossingMacroRequests` (#139) has to count the requests that are dropped by their tag before a run with the epoch tells anything (`design/lab-test-plan.md`, protocol of 2026-11-10).
  - The second case, a resent InvalidateChannel, is fixed by #161 (`design/dsf-findings-upstream.md`, T11 resend); RRF with #47 no longer refuses InvalidateChannel at all (`RRF/SBC/SbcInterface.cpp:976-1018`).
- **T13. Probably no finding: the abort window of #134 fits the USB link as well.** From reading the code.
  - T13 assumed that over USB RRF answers a transfer only after it has read DSF's packets of that transfer, as the comment of `USB.ResendPacket` says (`USB.cs:717-720`).
  - RRF's `DoTransferUsb` sends the TX buffer that the previous `ExchangeData` composed (`RRF/SBC/DataTransfer.cpp:1303-1357`), so over USB RRF answers one transfer later, as over SPI. The comment is wrong, see S6.
- **T4. A missing trigger macro is not shown after DSF has run a code on the Trigger channel.** RRF reports it as a pushed code reply (`GCodes.cpp:3703-3706`), DSF takes it as an out-of-order reply and holds it back (`Processor.cs:1122-1127`, `LinkService.cs:727-733, 762-776`). This fits the field journal line "Trigger: Out-of-order reply: 'Macro file trigger8.g not found'", not proven to be the same event. Test: `Codes.TriggerMacros.MissingTriggerMacroAfterCodeOfDsfOnTriggerChannel`.
- **T5. M581.1 with SBC fields succeeds and creates the trigger although its expression fails** (upstream 4229f552). RRF returns an error and creates nothing (`TriggerItem.cpp:328-339`, wiki M581.1). Test: `Codes.SbcTriggerEvaluation.ErrorInExpressionIsReported`.
- **T6. A later evaluation error of such a trigger is only logged, and the trigger stays enabled.** RRF disables it and reports it (`TriggerItem.cpp:133-149`). Test: `ErrorInLaterEvaluationDisablesTheTrigger`.
- **Fixed and removed from this list (2026-11-13):** T7 (upstream 61df011e, merged 2026-11-13; SBC trigger 0 ran trigger0.g instead of an emergency stop, trigger 1 ran trigger1.g instead of pausing). DSF now requests the stop itself for trigger 0 and sends `M582 T<n> S1` for the others, so RRF pauses or starts the macro. Test: `TriggerZeroIsAnEmergencyStop`, no longer explicit; the fake firmware emulates M582 S1.
- **T8. M582 never checks a trigger that DSF evaluates**, because it is passed straight to RRF. The wiki says M582 works for M581.1 triggers. Test: `M582ChecksTriggerOfDsf`.
- **T9. R1 and R2 of SBC triggers count simulating and cancelling as not printing** (`SbcTriggerService.cs:341-344`). RRF's `IsPrinting` counts both as printing (`GCodes.cpp:3993`, `RepRap.cpp:1961-1968`). Test: `EnableConditionWhilePrinting` with Simulating and Cancelling.
- **T10. What is left of the replies of a code that DSF dropped for InvalidateChannel.** Checked on 2026-11-11 against DSF `v3.7-dev` 412e5ff6 and RRF `3.7-dev` 5c0cf3b8f. The RRF side is read in the code, the tests run against the fake firmware.
  - Fixed when both sides have the channel epoch (#167, Meltingplot/RepRapFirmware#47): a reply carries the epoch of its code (`RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1394-1403`), and DSF takes a reply of an older epoch as stale (`Processor.cs:184-208`, `:1402-1464`). It no longer completes another code. A complete stale reply with text is shown as a message of its own (D4, `Processor.cs:1456-1463`), so the "M98: Macro file … not found" of the printer case of 2026-11-09 is still shown, as a warning without a code. Tests: `ChannelEpochWithDsf.ReplyOfACodeThatRanIntoTheAbortIsShownAsAMessage`, `TriggerFuzz.NextCodeGetsItsOwnReplyAfterAbortWhileM98WaitsForItsMacro`.
  - M291 S2 at the base level, fixed in RRF for every DSF: the routine of the abort closes the box as cancelled without the abort flag and writes no MessageAcknowledged (`RRF/SBC/SbcInterface.cpp:1979-1982`). The box stays in RRF's list until M292 or its timeout (LA-24). A DSF without the epoch still gets the M291's final reply as the reply of its next code (Q37; `RecordApplyWithDsf.NextCodeRunsAfterTheAbortOfABoxAtTheBaseLevel` pins this).
  - Still open with an RRF or a DSF without the epoch: everything as before #167, with the gate of #161.
  - Decided on 2026-11-25 (Tim: take upstream 0ca1dff6c, which reverses D3/D-A of 2026-11-10): the routine of the abort calls `AbortStateMachine` before `AbortFile(true)` (`RRF/SBC/SbcInterface.cpp:1993-1996` at RRF a10384abe), as upstream's InvalidateChannel does (upstream 3.7-dev `SbcInterface.cpp:878`). A T-code whose macro the abort popped ends without its other macros (`RRF/GCodes/GCodes4.cpp:2064-2075`). A G28 whose homing file the abort popped ends with "Failed to home axes" without axis letters, also when the popped file homed an axis, because `AbortStateMachine` clears `toBeHomed` (`:2059-2062`) and homing2 then finds it disjoint from the homed axes (`:398-405`); DSF trims the trailing space of the message. Behaviour change, the same as upstream: an abort from outside during a probing move in flight ends probing during the move (`:2088-2097`, LA-25 risk (1)), see K39. The fork also runs the routine when the connection is lost; upstream's `InvalidateResources` does not call `AbortStateMachine`. Tests: `ChannelEpochWithDsf.ToolChangeEndsWhenAnAbortPopsItsMacro`, `HomingCodeEndsWithAnErrorWhenAnAbortPopsItsHomingFile`, `HomingCodeAbortedAfterHomingAnAxisEndsWithAnError`, `FakeFirmwareEpoch.ToolChangeEndsAfterAnAbortOfItsMacro`, `ToolChangeOfAnEndedConnectionEndsWithoutAReplyOfItsOwn`.
  - Not tested: messages that codes write with their response type outside HandleReply (M122, `GpInPort.cpp:90`, the SD speed test). They carry the epoch of the code now, because they use `GetResponseMessageType()`, so an epoch DSF handles them as stale replies; RRF does not suppress them.

Open question: the wiki for M581 says polling for further triggers is suspended while a trigger macro runs. RRF keeps checking and marks the trigger as pending (`GCodes.cpp:983-989`).

### Stop path under SPI faults (#130)

R10 of `design/sbc-link-requirements.md` asks that a software stop reaches RRF within 100 ms while the link is disturbed. The table there marks R10 as not implemented.

**How it was measured:**
- The real path: M112 through MCodeHandler, `EmergencyStopAsync` and LinkService, against the SPI port of RRF (`FakeRrfSpi`).
- The fault models of the SBC link fuzzers: two bit flips, errors on both lines, and noise.
- Times are virtual and count the link's share only.

**Results:**

| Case | Median | Max | Stops over 100 ms |
|---|---|---|---|
| Extended link | 0.05 ms | 0.25 ms (noise) | none |
| RRF idle, pausing 25 ms between transfers (M576 S default) | 38 ms | 50 ms | none |
| Plain link with noise | 0.05 ms | 502 ms | 5 of 1000, plus 2 stuck (measured before #137) |

- **Trigger macros:** the first code of a trigger macro reaches RRF within 2.5 ms on the extended link.
- **RRF restart just before the stop:** the restarted RRF gets the stop in only about half of the cases, because it drops its first transfer by a connection reset. The restart is a stop itself (R6), but the restarted RRF is not halted. Whether that matters is open.
- **What R10 does not hold yet:** a stop waits behind a long code transfer that noise keeps failing. The worst case was about 503 ms, on both links.

Findings:

- **P3. M112 can reply before RRF has processed the stop.**
  - LinkService completes the halt on RRF's Success for the data, and DSF's own last Success is never acknowledged.
  - Inputs:
    - plain link: one flip, `Stop None | Stop 5 ToRrf 0`;
    - extended link: four bits in one response.
  - The lead was at most 0.05 ms in every case found. This affects the meaning of the reply as #88 intended it, not the delivery of the stop.
  - Test: `M112RepliesOnlyOnceRrfIsStopped`.
- **P4. A trigger macro can get lost after a reconnect on the plain link while RRF keeps running.**
  - Input: two flips, `Trigger None | Code 0 ToDsf 0 | Code 1 ToRrf 0`. DSF repeats a transfer with the same number after "SPI data transfer failed". The fix is case 2 of #57, which is on hold.
  - The extended-link part (a lost connection in a transfer with counter value 0) is fixed by #138.
  - **Effect:** the open trigger file keeps `IsTriggerBusy` true (`GCodes.cpp:423-431`) and holds back later triggers, trigger2.g included (`GCodes.cpp:999`).
    - Shown on the lab printer on 2026-11-08 with the extended-link variant without #138: RRF kept the session, and trigger30.g did not start again for 46 s, although its trigger fires twice per second.
    - In that state RRF also answered no M409 of a global variable from HTTP. Why has not been checked.
  - Rests on the SPI port; not replayed against the RRF host build.
  - Test: `TriggerMacroRunsOrRrfStops` (plain-link input, explicit).
- **Observation:** a trigger request that crosses an emergency stop starts its macro after the stop, but its codes are held back, because the buffer space is 0 after `InvalidateCodes` (`LinkInterface.cs:615`).

## Findings of the firmware update, plugin service and startup tests (2026-11-08)

The tests of #145, #146 and #147 found bugs that #148 to #151, #153 and #154 fixed on the same day. These points are still open. Line numbers are DSF `v3.7-dev` at f70a6f16 unless an entry says otherwise. None has an `[Explicit]` test yet unless one is named.

### Firmware update (#145, #148)

- **U1. The remote wait of `DCS -u` for an expansion board ignores its timeout while no object model patch arrives.** It blocks in `GetObjectModelPatchAsync` (`FirmwareUpdater.cs:416`), and patches come only when something below `boards/**`, `directories/**` or `state/status` changes. Shown with a scratch test (100 ms timeout); whether a real machine keeps sending patches (e.g. mcuTemp) is not checked. Documented in `UpdateOnlyMode.ExpansionUpdateTimesOut`.
- **U2. The M999 of the remote `DCS -u` update has no channel**, while the local one runs on the Trigger channel (`FirmwareUpdater.cs:464-470` vs `:248-252`).
- **U3. DuetPiManagementPlugin intercepts every M997 whose S contains 2 and runs only S2** (`DuetPiManagementPlugin/Program.cs:679-680`), so `M997 S0:2` drops module 0 silently while the plugin runs.
- **U4. An RRF build with `NO_S_CURVE` reports a version with spaces**, "(no 3rd order motion)" (RRF `Version.h:18-19`). DSF takes the last space-delimited word of the identifier as version (`Firmware.cs:169-173`), "motion)", so such a board would always look outdated. Not checked which version string RRF reports for such a build.
- **U5. Open questions of #154:** the 2-minute bound of the wait for a firmware update at a stop rests only on the 11 s main board flash of the lab printer. An M997 sent after the stop has begun waits for the update lock until the stop ends.

### Plugin service (#146, #151)

- **D1. A parent directory swapped for a symlink between the check and the write** is not covered (TOCTOU). The sticky bit of `/opt/dsf/plugins` (root:dsf 1775) only narrows this for root-owned entries.
- **D2. The www link of a root plugin's web file is created by the regular service before the root service writes the file.** If creating the symlink fails, the copy fallback finds no source file.
- **D3. DPS has the startup deviations that #150 fixed in DCS:** the LogLevel key is filtered case-sensitively (`DuetPluginService/Program.cs:84`), `AddCommandLine` with the unmatched tokens (`:69`) never runs because unmatched tokens are still errors, and `Directory.GetFiles` of the plugin directory is not guarded (`PluginService.cs:39`), so DPS fails to start without that directory.
- **D4. Deviations without a test:**
  - PLUGINS.md:27 says DWC files go to 0:/www/\<PluginName\>, the code puts them directly below 0:/www.
  - StopPlugin's error says "Wrong plugin service to start this plugin" (`StopPlugin.cs:56`).
  - `"` and `\` are refused in the arguments of every plugin, so a plugin that is not a Python plugin cannot pass an argument with a space (.NET splits only at double quotes).
- **D5. CHX image, for Tim (not DSF code):** the image build chowns the whole plugin tree to dsf (`rpi-image-gen/meltingplot/layer/mp-dsf.d/bin/mp-dsf-plugins:251`), so `/opt/dsf/plugins` is dsf-owned on the image. Nothing breaks today, because the image ships no superUser plugin. A root plugin in the image would be refused by the root service (#151) unless the script keeps the directory root:dsf 1775 and the plugin's files root-owned.

### Startup and shutdown (#147, #149, #150, #154)

- **L1. A code that reaches the Firmware stage after LinkService has stopped stays there until the 10 s shutdown timeout.** Mainly an IPC `Code` command, which runs with the token its caller passes (`Commands/Generic/Code.cs:119-125`), the stop token of the IPC server, not one that LinkService cancels. Not reproduced.
- **L2. M999 and M997 may not stop DCS when their code faults after the reset.** `StopApplication` runs in a continuation after `await task` (`MCodeHandler.cs:1380-1385` for M999, `:1324-1329` for M997), so a faulted or cancelled code task, e.g. from an Executed interceptor, leaves DCS running with the link reset. From reading the code, not tested.
- **L3. A start failure leaves the IPC socket file behind:** the host is disposed without StopAsync, and the socket is deleted only at `IPC/Server.cs:136`.
- **L4. `DCS -u` binds and listens on the IPC socket but never accepts** (`IPC/Server.cs:66-67`, `:81`), and it deletes an existing socket file (`:57`).
- **L5. `-l` with an unknown level silently falls back to Information** (`LogLevelHelper.ParseLogLevel`, DuetSharedLibrary `LogLevelHelper.cs:49`).
- **L6. An interceptor error in the Executed stage is not logged while DCS runs:** the catch logs only when `(e is OperationCanceledException) != ApplicationStopping.IsCancellationRequested` (`Executed.cs:235`), which is false for a real error while DCS runs, and true for a cancellation, which is then logged as an error instead (the open question in the IPC server section). Upstream since v3.6.

## Found by the v7 contract reverse engineering (2026-11-09)

These come from reading the code for [sbc-link-v7-contract.md](sbc-link-v7-contract.md), the v7 contract that the link requirements are checked against. Each entry names its item there (`G-n` DSF gap, `X-n` RRF gap, `C-n` cycle) and what the channel-epoch requirements ([sbc-link-v8-requirements.md](sbc-link-v8-requirements.md), draft 8) do with it. Line numbers: DSF `v3.7-dev` at 7f26d0ca, RRF fork at 0dccb9efe (tree of `3.7-dev` ddec2d001). Unless an entry says otherwise, nothing was run and every line involved is upstream code.

- **Fixed and removed from this list (2026-11-13):** K1 (upstream 61df011e, merged 2026-11-13; M23, M32 and M37 waited for SetPrintFileInfo under the job lock). No test.
- **K2. An abort of File or File2 can join K1 (C-2).** From reading the code, not tested.
  - `AbortAllAsync` holds the channel lock while it waits for the job lock (`LinkInterface.cs:557-563`, `Processor.cs:828`). If the job lock holder waits as in K1, the link thread waits for that channel lock in Spin (`Manager.cs:80`).
  - Bounded only by the abort's token: the IPC connection, or for `abort` in a macro the macro's own token, which the abort cancels (K8). Reachability is not shown. As K1: a reachability test in E0 (Q14 of the channel-epoch requirements).
- **Fixed and removed from this list (2026-11-13):** K3 (upstream 61df011e, merged 2026-11-13; M606 S1 took the job lock before the channel locks). No test.
- **K4. Unknown request types are dropped without an error on both sides (X-18, G-16).** From reading the code, not tested.
  - RRF: a request number of 32 or more, other than 0x4D50, is an internal error that ends the read loop, so the rest of the transfer, codes included, is neither processed nor refused (`SbcInterface.cpp:326-330`; `InvalidRequest` = 32, `SbcMessageFormats.h:346`).
  - DSF: an RRF request type it does not know is ignored silently, the switch has no default (`LinkService.cs:611-701`).
  - This is why the channel epoch adds no packet type (CE-5 of the channel-epoch requirements). DSF's side is VF-1; RRF's side gets a test in milestone E0 first (Q21, decided).
- **K5. A stale MacroFileClosed of an RRF without Meltingplot/RepRapFirmware#47 stops DCS after a reconnect (X-12).** Checked on 2026-11-11 against DSF `v3.7-dev` 412e5ff6 and RRF `3.7-dev` 5c0cf3b8f.
  - Fixed in RRF for every DSF: a lost connection records an abort on every channel (`RRF/SBC/SbcInterface.cpp:1911-1919`). Nothing of a recorded channel is written before the main task has applied the abort (`:1611-1648`). The apply clears `macroFileClosed`, `invalidated` and `reportPause` (`:2056-2064`) and runs the routine that voids an unsent macro request, ends a DoCode, sent or not, and closes a message box without an acknowledgement (`:1965-1989`). Test against the fake firmware: `FakeFirmwareRecordApply.LinkLossLeavesNoStaleEvent`. The RRF side is read, not run; pause and PrintPaused are not in the fake firmware.
  - What stays, DSF unchanged: with an RRF without #47 (3.7.0+1.mp.8 and older, upstream) a MacroFileClosed that was set just before the link loss is written in the first exchange of the new connection. It is untagged, so DSF pops its base level: "Stack underrun" (`Processor.cs:358-364`, `:1587-1609`) ends the link thread and DCS, because the packet loop catches only `ArgumentOutOfRangeException` (`LinkService.cs:466`). Window: a macro closes in the last exchange before the loss, e.g. a DCS restart at the end of a macro.
- **Fixed and removed from this list (2026-11-13):** K6 (upstream 61df011e, merged 2026-11-13; a failed USB transfer completed the halt of the stop it carried). The halt now completes only when the transfer has succeeded, else the stop is written again. No test.
- **K7. Over SPI, RRF can process a transfer of the old session in the new one (G-3).** From reading the code, not tested.
  - After a lost connection DSF exchanges the same TX again and invalidates its own state only after that transfer (`SPI.cs:442-477`, `:383-390`).
  - RRF skips the transfer that shows the reset, but after a connection reset (not a full one) it processes the next transfer whose number follows, whichever session wrote it (`SbcInterface.cpp:184-186`, `:257-288`; `DataTransfer.cpp:839-853`, `:1396-1443`). Codes, InvalidateChannel or MacroStarted of the old session can then run while DSF has cancelled them.
  - The stop path tests saw the other half: a restarted RRF drops its first transfer in about half of the cases (section "Stop path under SPI faults"). Fix: VF-7 (separate v7 work in the channel-epoch requirements).
- **K8. `abort` inside a macro can release the channel lock while the abort still runs (G-4).** From reading the code, not tested.
  - `KeywordHandler.cs:170` passes the macro code's token to `AbortAllAsync`, which waits with it while it holds the channel lock (`LinkInterface.cs:557-563`). Macro codes carry the macro's token (`MacroFile.cs:340`), and aborting the macro cancels it (`MacroFile.cs:236`).
  - The wait then throws and releases the lock while `AbortAllFilesAsync` still pops levels and sets `_allFilesAborted` without it (`Processor.cs:778-823`), concurrently with the link thread.
- **K9. File results, FileChunk and the refusal of a system macro can be lost (G-6).** From reading the code, not tested.
  - The link thread ignores whether these packets were written (`LinkService.cs:985`, `:993`, `:1161`, `:1169`, `:1193` and the other file results; `Processor.cs:1368`, `:1393`). If the TX is full, or on SPI a resend of the same type is in it (guards from `SPI.cs:1468`), the packet is neither written nor retried.
  - RRF then waits for its 3 s timeout and reports "Timeout while …" (`SbcInterface.cpp:2331-2336`, `GCodeBuffer.cpp:1266-1273`). Not addressed by the channel-epoch requirements; a test in milestone E0 first (Q21, decided).
- **K10. SetLastCodeResult can be overtaken by the next code (G-7).** From reading the code, not tested.
  - It has no resend guard (`SPI.cs:1666-1683`) and completes when written (`LinkService.cs:498-513`). If RRF refuses it because the GB mutex is busy (`SbcInterface.cpp:1287-1316`), the next code of the channel, written after it, runs with the old `result`. Same pattern as S8.
  - The channel epoch keeps this v7 order; a test in milestone E0 first (Q21 of the channel-epoch requirements, decided).
- **K11. A partial log message or generic message survives an invalidation and is prepended to the next one (G-8).** Checked on 2026-11-11 against DSF `v3.7-dev` 412e5ff6 and RRF `3.7-dev` 5c0cf3b8f.
  - Fixed for the partial code reply of a channel (#167): it is discarded at each epoch change (`Processor.cs:251-256`) and at a reset of the epochs (`:261-273`); a tagged head of the aborted work moves to the stale reply (`:276-289`). Tests: `ChannelEpochWithDsf.StaleReplyIsShownWithTheHeadThatCameBeforeTheAbort`, `IncompleteStaleReplyEndsWithTheConnection`. An untagged head stays as in v7.
  - Open: `_partialLogMessage` (`LinkService.cs:837`, `:850-864`) and `_partialGenericMessage` (`:897`, `:906-916`) are never cleared by an invalidation. The test with a partial head at an emergency stop (Q21) does not exist yet.
- **K12. After DSF's M112, codes wait for a buffer update that RRF need not send (G-11).** From reading the code; the fake firmware showed the held-back codes (observation in "Stop path under SPI faults"), the RRF side is not tested.
  - `InvalidateCodes` sets DSF's view of RRF's buffer space to 0 (`LinkInterface.cs:613`), so DSF sends no code until RRF reports space.
  - RRF sends CodeBufferUpdate only when its ring changes (`SbcInterface.cpp:1391-1400`); the EmergencyStop packet does not touch the ring (`SbcInterface.cpp:336-338`, `RepRap.cpp:1049-1103`). With an empty ring no update comes, so a code after M112, e.g. M122, waits until something else changes the ring or M999 resets. Not addressed by the channel-epoch requirements; a test in milestone E0 first (Q21, decided), safety-relevant, reported once a test proves it (FD-13).
- **K13. A DoCode that DSF cancels without an abort of its channel leaves RRF's channel busy (G-12).** Checked on 2026-11-11 against DSF `v3.7-dev` 412e5ff6 and RRF `3.7-dev` 5c0cf3b8f.
  - DSF answers a DoCode with a targeted message only when the code has a result; a cancelled one gets nothing (`Executed.cs:170-230`). RRF's GB stays "executing on SBC" with no timeout and takes no further text input.
  - Fixed in RRF for an abort of the channel and for a lost connection, for every DSF (Meltingplot/RepRapFirmware#47): the routine that the main task runs at the apply ends the GB's DoCode, sent or not (`RRF/SBC/SbcInterface.cpp:1965-1971`). A DSF with the epoch does not run a DoCode of an older epoch (`LinkService.cs:1144-1148`) and does not send the result of one (`:642-648`). Tests against the fake firmware: `FakeFirmwareRecordApply.ApplyEndsACodePassedOnToDsf`, `RecordApplyWithDsf.ChannelIsFreeAfterAnAbortWhileDsfRunsACodePassedOn`, `ChannelEpochWithDsf.ResultOfADoCodeOfAnOlderEpochIsNotSent`.
  - Open: a cancellation that reaches the DoCode without a channel abort or link loss still leaves the GB busy. Which cancellations can reach it is not listed (see the correction below: a channel abort in DSF does not cancel a DoCode).
  - Open with a DSF without the epoch: a result that still comes after the abort is printed to its target and finishes nothing; if the GB has passed on a newer DoCode by then, the late result finishes that one early (Q37). In the fake firmware's record/apply model without tags this happened 86 times per 2000 aborts (`ChannelEpochStress.Measure(RecordApply)`, 2026-11-11).
- **K14. A lock request that DSF drops is not withdrawn (G-13).** From reading the code, not tested.
  - When DSF pops or invalidates the level of an M505, it fails the lock request (`Processor.cs:212-215`), but it keeps copying the Lock packet whenever RRF asks for it again (`SPI.cs:821-856` copies any id). The later Locked finds no request (`Processor.cs:848`), and no Unlock follows.
  - RRF keeps the movement lock until it releases it by itself; `UnlockAll(gb)` when a code of that GB finishes in the normal state (`GCodes2.cpp:5017`, `:5112-5116`; re-read on 2026-11-09). Fix: VF-5 (Lock as a pending request, separate v7 work in the channel-epoch requirements); the Unlock for a Locked without request is logic (LA-9 in the logic adaptations). The abort case is covered by the channel epoch when the GB is idle: the routine that RRF runs at the abort then releases the channel's locks (CE-25, LA-22); with a code of the GB running, the code releases them when it ends.
- **K15. DSF ignores a second message box on a channel that already waits (G-14).** From reading the code, not tested.
  - RRF can send a second WaitForAcknowledgement while the first box is open: M120 is allowed on the box level and pushes a level on which M291 S2 runs normally (`GCodes2.cpp:182-186`, `:2272-2274`; `GCodes7.cpp:101-115`; `SbcInterface.cpp:1580-1590`).
  - DSF ignores the second request (`Processor.cs:1260`), so its MessageAcknowledged pops the first box.
- **K16. An object model answer for another key stops the model updates (G-18).** From reading the code, not tested.
  - The fork's key check discards the answer but leaves the query at the head marked as sent (`LinkService.cs:714-723`, `:470-478`). If the right answer never comes, no further query is sent until a reset, and a halt of RRF never shows in the object model. Fix: VF-8 (separate v7 work), rule CE-17 (b) of the channel-epoch requirements.
- **K17. ExecuteMacro can overtake an earlier reply of its channel (G-10).** From reading the code, not tested.
  - DSF takes `BufferedCodes[0]` as the code that requested the macro (`Processor.cs:1383-1388`). RRF writes the reply FIFO before ExecuteMacro (`SbcInterface.cpp:1389`, `:1514-1518`) but does not drain it: a FIFO entry that does not fit as a whole stays while the smaller request still fits, with 19 to 31 free bytes for "homeall.g" or "pause.g" (`DataTransfer.cpp:1491-1496`, `:1545-1549`). Fork RRF #29 removed its own drains before ExecuteMacro again.
  - A narrow window. A test in milestone E0 first (Q21 of the channel-epoch requirements, decided); code identity only on evidence (EG-1).
- **K18. With a DSF or an RRF without the channel epoch, replies of a code that RRF still ran can follow RRF's own abort of all files (G-20).** Checked on 2026-11-11 against DSF `v3.7-dev` 412e5ff6 and RRF `3.7-dev` 5c0cf3b8f.
  - Fixed when both sides have the epoch (#167, Meltingplot/RepRapFirmware#47): RRF writes AbortFile(all) with the tag of the channel and drops the channel's packets until DSF answers (`RRF/SBC/SbcInterface.cpp:1705-1711`). DSF answers a tagged AbortFile(all) with InvalidateChannel(e+1) (`Processor.cs:913-919`, `:1100-1112`), so the later replies carry the older epoch. They no longer complete DSF's next code; one with text is shown as a message of its own (D4, `Processor.cs:1402-1464`). Test: `ChannelEpochWithDsf.TaggedAbortOfAllFilesIsAnsweredWithInvalidateChannel`.
  - Open when either side does not tag: an untagged AbortFile(all) is handled as in v7 (`Processor.cs:914-915`), and such a reply completes DSF's next code of the channel or is logged as out of order. The windows that the epoch leaves are listed in section 10.9 of the channel-epoch requirements.
- **K19. An abort leaves base-level codes that are still in the early pipeline stages (G-21).** From reading the code, not tested.
  - InvalidateChannel and `abort` cancel the buffered and Firmware-stage codes (`Processor.cs:1449-1485`), but only `Invalidate` cancels the channel's token (`Processor.cs:1536`), so base-level codes in the Start to Post stages go on. The DuetAPI doc says "all pending codes" (`src/DuetAPI/Commands/Generic/InvalidateChannel.cs:5-7`).
- **K20. An emergency stop that RRF makes by itself leaves DSF's stacks behind (X-5).** From reading the code, not tested.
  - Trigger 0, PanelDue's M112 bytes, an M112 that RRF executes and a trigger action with M112 run `DoEmergencyStop`, which calls `GCodes::Reset` after the stop (`GCodes.cpp:490-499`, `:990-999`, `:1065-1070`; `GCodes2.cpp:1953-1955`). `Reset` pops every stack and clears the abort flags that `AbortPrint` has just set (`GCodeBuffer.cpp:128-150`, `:145`), so no AbortFile reaches DSF and the codes in the SBC ring are not purged.
  - DSF keeps its macro levels and buffered codes; RRF answers the codes left in its ring at the base level with "stopped". DSF's own M112 aborts the files on both sides (`SbcInterface.cpp:336-338`, `GCodes.cpp:1073-1095`). A STOPPED packet is deferred until a test in milestone E0 shows this finding (Q27 of the channel-epoch requirements, decided); DSF learns the halt from the object model (CE-17); DSF's reaction is logic (LA-7 in the logic adaptations).
- **K21. SetVariable assigns before it can be refused (X-6).** From reading the code, not tested.
  - RRF evaluates and assigns (`SbcInterface.cpp:1041-1056`), then writes VariableResult; without a free OutputBuffer or TX room it refuses the packet after the assignment (`:1070`, `:1086`, `:1092`). The copy of a `var` or `global` then fails with "variable '…' already exists" (`:1024-1030`). Fix: VF-4 (separate v7 work in the channel-epoch requirements).
- **K22. A late file result answers the next file request (X-8).** From reading the code, not tested.
  - File results carry no id; RRF matches them by the state of its single file slot (`SbcInterface.cpp:1145-1285`) and clears the slot after its 3 s timeout (`:2331-2336`). A result of the same type that comes later is taken as the answer to the next request.
  - DSF serves file requests with blocking I/O on the link thread (`LinkService.cs:1152-1494`), so a slow file system makes it late. A test in milestone E0 first (Q21 of the channel-epoch requirements, decided); request ids only on evidence (EG-4).
- **K23. Late answers after RRF's 3 s macro timeout act on the wrong level (X-9, X-25).** From reading the code, not tested.
  - After `SbcMaxRequestTime` RRF reports "Timeout while waiting for macro file" and goes on as "not found" (`GCodeBuffer.cpp:1263-1274`), but the request had gone out. A late MacroStarted gives "none was requested" (`SbcInterface.cpp:850-853`), a late Code of the macro runs at the caller's level (`:383`), a late MacroCompleted with error pops whatever level is current (`:567-576`). DSF has pushed the macro's level and stays one level deeper. Not addressed by the channel epoch; whether RRF requests an abort after an expired wait is logic, open (LA-8 in the logic adaptations).
- **K24. A code longer than RRF's limit is refused for good (G-17, X-19).** From reading the code, not tested.
  - RRF refuses a Code over 384 bytes (`SbcMessageFormats.h:31`, `SbcInterface.cpp:358-364`) on every copy and never ends it with an error, so DSF resends it forever and the channel stays blocked. DSF lets `MaxCodeBufferSize` in `config.json` go above that (`Settings.cs:245-248`). A test in milestone E0 first (Q21 of the channel-epoch requirements, decided).
- **K25. M260 and M261 with V delete a stale variable without a flush, so during a macro call the deletion is applied to the macro's variables.** From reading the code, not tested. Corrected on 2026-11-09: the first version blamed the end of a `while` iteration, which does flush.
  - Code blocks flush before they end: a block with local variables before it ends (`CodeFile.cs:379-384`), a `while` iteration without them before its next iteration (`:398-403`). A closed block keeps its variables (`:722-731`); they are deleted when the name is declared again (`DeleteStaleVariableAsync`, `:651-671`). `var` flushes before that deletion (`KeywordHandler.cs:244-248`, deletion `:272-276`).
  - M260 and M261 with V delete such a stale variable without a flush (`MCodeHandler.cs:798-805`, upstream 363e87439), because RRF declares the result variable itself and refuses a name that exists (RRF `Platform/Platform.cpp:2503-2524`). The DeleteLocalVariable can therefore reach RRF while an earlier code of the level still runs, e.g. an M98 of the same file.
  - RRF deletes from the GB's current variables (`SbcInterface.cpp:1138-1140`, `GCodeBuffer.cpp:1488-1496`). Once RRF has pushed the macro's level, those are the macro's. During the macro wait RRF refuses the deletion, because the M98 holds the GB mutex (`SbcInterface.cpp:1131-1136`; `GCodes.cpp:579-639`, `GCodeBuffer.cpp:1253`, `:1263-1274`), and applies the copy later, by then to the macro's level. The caller's stale variable stays, a variable of that name in the macro is deleted instead, and the M261 fails with "variable '…' already exists" after the macro (`Platform.cpp:2519`).
  - Logic finding outside the link work (Tim, 2026-11-09; LA-16 in the [logic adaptations](sbc-link-v8-logic-adaptations.md)): the logic has to order the deletion after the earlier codes of the level, as `var` does with its flush. The request is one of those that RRF can refuse during a macro wait; the channel epoch does not change that.
- **K26. The result of a code that DSF handles itself can be overwritten by an earlier code, so `result` shows that earlier code's result.** From reading the code, not tested.
  - For a G, M or T code that DSF resolves itself (e.g. M122 "DSF", `MCodeHandler.cs:789-794`), that an interceptor resolves, or that fails with a parameter error, DSF sends SetLastCodeResult (`Commands/Generic/Code.cs:266-272`, `:276-281`, `:286-293`; `Codes/Pipelines/Pre.cs:30-35`). None of these paths flushes first; only a code with SBC fields does (`Code.cs:242-245`).
  - RRF keeps one `lastResult` per GB (`GCodeBuffer.h:92-93`, `:384`), sets it when it reads SetLastCodeResult (`SbcInterface.cpp:1288-1316`) and overwrites it with every code it completes (`HandleReply`, `GCodes.cpp:4432-4438`). If an earlier code of the level still runs in RRF, e.g. a move or an M98, it completes after the SetLastCodeResult, and the next code that reads `result` (`ExpressionParser.cpp:1822-1845`) gets the earlier code's result.
  - All DSF lines involved are upstream code (`git blame`). Logic finding outside the link work (Tim, 2026-11-09; LA-17 in the logic adaptations). Related: K10, where a refused SetLastCodeResult is overtaken by the next code.
- **K27. A `var` that declares a stale name again can fail with "variable '…' already exists", because v7 lets the creation overtake a refused deletion.** From reading the code, not tested.
  - `var` flushes (`KeywordHandler.cs:244-248`), deletes a stale variable of the same name (`:272-276`, `CodeFile.cs:651-671`) and then creates the new one (`KeywordHandler.cs:279`). The deletion completes when it is written (`LinkService.cs:551-558`); only then is the creation queued, so it goes into a later transfer.
  - If RRF refuses the deletion because the GB mutex is busy (`SbcInterface.cpp:1131-1136`), DSF copies it into a later transfer (V7-8). RRF can accept the creation before the copy arrives, or in the same transfer after it refused the copy again. It then still finds the old variable and answers "variable '…' already exists" (`SbcInterface.cpp:1022-1030`).
  - After the flush no code of the level runs, so the mutex is busy only while RRF's main task holds it for another reason (V7-87); how often that happens is not known.
  - A v7 reordering of two requests of one channel (V7-10). The channel epoch keeps this v7 order; a test in milestone E0 first (Q21 of the channel-epoch requirements, decided).
- **K28. At an abort DSF drops an Unlock that is queued but not yet written, so RRF keeps the movement lock until the GB's next code ends.** Checked on 2026-11-11 against DSF `v3.7-dev` 412e5ff6 and RRF `3.7-dev` 5c0cf3b8f. Found by the independent review of draft 6 of the channel-epoch requirements (2026-11-09).
  - DSF, unchanged: `InvalidateRegular` and `Pop` resolve every request of the lock queue false, the Unlock included, and never write it (`Processor.cs:1781-1784`, `:375-378`). The only user is M505 (`MCodeHandler.cs:957-990`).
  - Fixed in RRF for an idle GB (Meltingplot/RepRapFirmware#47): the routine that the main task runs at the abort releases the channel's locks if the GB is idle (`RRF/SBC/SbcInterface.cpp:1984-1988`), which it is after M505, because M505 flushes first. Tests against the fake firmware: `FakeFirmwareRecordApply.ApplyReleasesTheLocksOfAnIdleChannel`, `ApplyKeepsTheLocksWhileACodeRuns`. There is no test on the DSF side with a dropped Unlock.
  - Open: if a code of the GB still runs at the apply, the lock stays until that code ends, as in v7. With an RRF without #47 the finding stays as it was (LA-22). Not checked: `PrintPaused` also runs `InvalidateRegular` without an InvalidateChannel (see K34).

## Found by the first implementation of the channel epoch (2026-11-10)

From the abort stress test against the fake firmware (`src/UnitTests/Link/ChannelEpochStress.cs` on the branch
`feature/sbc-channel-epoch`, seed 20261109, 2000 aborts). The fake firmware is a C# model of RRF; nothing here was seen on a
printer unless it says so.

- **Fixed and removed from this list (2026-11-11):** K29 (Meltingplot/RepRapFirmware#47; after a refused InvalidateChannel the reply of the code that still ran completed the next code, a gap in RRF #46. RRF no longer refuses InvalidateChannel and takes no mutex for it, `RRF/SBC/SbcInterface.cpp:976-1018`, and the apply flags a code from the SBC that still runs, `:2073-2076`. Fake firmware, "probe of RunningCode got the reply of another code" per 2000 aborts (`ChannelEpochStress.Measure`, 2026-11-11): 70 with the v7 model, 0 with the record/apply model without tags and with the epoch pair). Not analysed: in the record/apply model without tags, 2 per 2000 aborts still end as "M98 got the reply of another code"; with the epoch pair none.
- **K30. The "Macro file not found" warning of a trigger request that an abort ended is prepended to the next code's reply when DSF has no channel epoch.** Checked on 2026-11-11 against DSF `v3.7-dev` 412e5ff6 and RRF `3.7-dev` 5c0cf3b8f.
  - Cause, unchanged in RRF: `DoFileMacro` sends the warning as a pushed code reply whenever the last code of the GB came from the SBC and the request is not a helper macro (`isCodeReply`, `RRF/GCodes/GCodes.cpp:3719-3727`). A trigger request (`AsyncSystemMacroCode`) is not excluded there, although it is not started by a code. Not checked: whether upstream RRF has the same condition. T4 rests on the same condition.
  - With the epoch on both sides the warning carries the epoch of the code, and DSF keeps a stale pushed reply apart from the replies of the current epoch (`Processor.cs:1448-1453`), so it is not prepended to another reply. Whether it is shown at all then has no test.
  - Fake firmware, "probe of TriggerRequest got 'Macro file not found'" per 2000 aborts (`ChannelEpochStress.Measure`, 2026-11-11): 68 with the v7 model, 132 with the record/apply model without tags, 0 with the epoch pair.
- **K31. A code that RRF passes on to DSF (DoCode) and that flushes its channel waits forever if DSF has codes buffered on the same channel.** Seen as a hang of the test harness, DSF side read in the code; not seen on hardware.
  - M929 flushes its channel before it runs (`MCodeHandler.cs:1150`); as a DoCode it runs in DSF's own pipeline (`Processor.cs`, `DoFirmwareCode`).
  - While the DoCode is open, RRF's GB is "executing on the SBC" and takes no further code (K13), so the buffered codes that the flush waits for never finish.
  - Needs DSF codes on a channel whose input RRF reads as text (USB, Aux, PanelDue). How likely that is in the field is not known. Not addressed by the channel epoch.
- **K13, correction.** A channel abort in DSF does not cancel a DoCode: it runs in DSF's own pipeline, outside the channel's stack. K13 therefore needs a cancellation that reaches the `SimpleCode`; which ones do is still not listed.

## Found by the reviews of the channel epoch (2026-11-10)

Two independent reviews: the DSF production diff of `feature/sbc-channel-epoch`, and the second round of the RRF port.
The decisions are in `sbc-link-v8-requirements.md`, section 10.9.

- **Fixed and removed from this list (2026-11-11):**
  - K32 (#170 and, with the epoch, D12 in #167; an emergency stop of DSF that crossed the end of a macro never reached RRF, and DCS stopped. DSF now writes the stop first, processes the data RRF had sent against the stack as it was, and transfers the stop even if that fails, `LinkService.cs:328-339`, `:452-478`, `:489-495`. Tests: `SPI.EmergencyStopCrossingMacroEnd`, `Link.EmergencyStopEpoch`). Upstream has the order from before #170 (`upstream/v3.7-dev` f76ffc70, `LinkService.cs:266-276`, `:358-376`, `Processor.cs:176-181`, `:1228-1243`; read, not run). The report to Christian Hammacher is open.
  - K36 (Meltingplot/RepRapFirmware#47, commit 67e3c69f0; `macroJustStarted` stayed set when an abort popped a macro before its first code, so the next reply of the code below went out as an incomplete one. `AbortFile` clears it now, `RRF/GCodes/GCodeBuffer/GCodeBuffer.cpp:1199-1206`. Tests against the fake firmware: `HomingCodeEndsWithAnErrorAfterAnAbort`; not shown on hardware).

- **K33. DSF's own abort of a job channel marks the job aborted only after it has cancelled the codes.** Upstream fixed
  this order in `Processor.FilesAborted` (77684383: a job that RRF aborted near its end was reported as finished).
  `Processor.AbortAllAsync` still cancels first (`InvalidateRegular`), then awaits the job lock, then calls
  `JobProcessor.Abort`. Not shown to be reachable: with `abort` in the job file itself the job task waits for the
  abort; with `abort` in a macro of the job it is unclear. Needs a test before it is reported.
- **K34. The cancel of a held job (feed hold) ends the work of the job channels in DSF without a boundary on the link.** Analysed on 2026-11-11 at DSF 412e5ff6; read, nothing run.
  - Not reachable on the fork today: the fork's RRF (`3.7-dev` 5c0cf3b8f) has no feed hold, so the status "held" never appears. It becomes reachable when the fork takes upstream RRF e7e30e13f3 ("Implemented feed hold", `3.7-ch`, the pair of DSF 77684383) or later.
  - DSF (upstream 77684383): M0, M1 or M2 from a channel other than the job's, with a job selected and the status `Held`, calls `LinkInterface.InvalidateJobChannelsAsync` (`MCodeHandler.cs:104-120`, `LinkInterface.cs:573-582`). That runs `Processor.PrintPaused()` on File and File2, which cancels the buffered codes and pops the macro levels (`Processor.cs:1750-1763`). Then DSF cancels the job (`MCodeHandler.cs:123-126`), and only then the M0 goes to RRF.
  - `PrintPaused` was written for RRF's own pause, where RRF pops its levels before it reports the pause ("When we get here, RRF has done the same", `Processor.cs:1755`). Here RRF has done nothing yet. It drops its side when it executes the M0: `DiscardJobReadAhead` pops the macro levels of the file channels without a packet and purges the code ring (upstream RRF `GCodes3.cpp:1414-1432` at 7e95567976; read from GitHub, not built).
  - No InvalidateChannel is written, and the epoch of File and File2 does not change; DSF raises it only after an abort of all files (`Processor.cs:1100-1112`) and at a reset. So whatever RRF still sends for the dropped levels carries the current epoch, and DSF takes it as current (`Processor.cs:184-208`). The abort windows do not apply either, they act only after an abort of all files (`Processor.cs:1590`, `:1664`).
  - What follows from the code, none of it run, window sizes not measured:
    - A MacroFileClosed of a macro that ends in RRF after DSF has popped it pops DSF's base level: "Stack underrun", the link thread ends (`Processor.cs:358-364`, `:1587-1609`).
    - An ExecuteMacro that DSF reads after the pop starts a macro at the base level, which RRF then drops without a packet.
    - A late reply completes a newer code of the channel or is logged as out of order.
    - Between the pop and the cancel of the job (`MCodeHandler.cs:118`, then `:123`) the job loop can still send a code, which RRF purges without a reply.
    - With upstream RRF 7e95567976 the file channels do not run codes while held (upstream `GCodes.cpp:612-623`), which leaves only packets that are already on their way for the first three.
  - The path that draws the boundary today is an abort: InvalidateChannel from DSF, or RRF's AbortFile(all), which DSF answers with InvalidateChannel(e+1) (K18).
  - No test: the fake firmware has no pause, no feed hold and no M0. The wiki does not document M25.1 or the status "held" yet.
- **K35. DSF and RRF give up a connection at different times.** DSF gives up when RRF does not answer within a transfer for 500 ms (`SbcTransferTimeout`, `Settings.cs:178`, `SPI.cs:1779`); between transfers it waits 4 s as RRF does (`SbcConnectionTimeout`, `Settings.cs:183`; RRF `SbcMessageFormats.h:45`). So DSF can start a new connection while RRF still holds the old one's codes, macros and globals. Checked on 2026-11-11 against DSF `v3.7-dev` 412e5ff6 and RRF `3.7-dev` 5c0cf3b8f.
  - With the epoch on both sides (D2): DSF marks the first packet of each connection, and RRF ends a connection that it still runs as a lost one when it reads such a marker, "Lost connection to SBC (the SBC has started a new connection)" (`RRF/SBC/SbcInterface.cpp:336-345`). Behaviour change: a DCS that restarts faster than RRF's 4 s stops the machine as a lost connection does. Tests: `ChannelEpochWithDsf.ConnectionThatDsfEndsWhileRrfRunsItEndsAsALostOne`, `FakeFirmwareEpoch.MarkerOnARunningConnectionEndsItAsALostOne`.
  - Open with a DSF without the epoch: as in v7, the old work goes on.
- **K37. In SBC mode an abort from outside leaves the state variables of the removed levels set,** e.g. suspended
  heaters after an abort inside a probing code of a macro; upstream fixed the standalone path with `AbortStateMachine`
  (RRF 1e1eb34e0). Fixed for the SBC path on 2026-11-25: the routine of the abort calls `AbortStateMachine` (upstream 0ca1dff6c,
  RRF a10384abe), see T10 and LA-25.
- **K38. An untagged packet that RRF wrote for the connection before reaches the next connection.** Checked on 2026-11-11 against DSF `v3.7-dev` 412e5ff6 and RRF `3.7-dev` 5c0cf3b8f.
  - Fixed for tagged packets (D15 in Meltingplot/RepRapFirmware#47, D16 in #167): RRF writes no reply of a code of an ended connection, and DSF drops a tagged packet that RRF wrote before it read the first marker of the connection (`EpochConnection.cs:62-94`, `LinkService.cs:680-687`). Lab, 2026-11-10: 0 "Out-of-order reply" in 200 DCS starts, after 13 in 102 restarts without D16. Tests: `SPI.PacketsOfAnOlderConnection`, `ChannelEpochWithDsf.ReplyThatRrfWroteBeforeItReadTheMarkerIsDropped`.
  - Open: untagged packets are processed as on v7 (`PacketsOfAnOlderConnection.UntaggedPacketsThatComeWithTheConnectAreProcessed`). That is every packet of an RRF without the epoch (lab, 2026-11-10: one "Daemon: Out-of-order reply ''" at a DCS start against RRF 3.7.0+1.mp.8) and, with the epoch, the packets without a channel such as generic messages, the object model answer and file requests. Not tested over USB.
- **K39. An SBC abort ends probing while the probing move is still running.** Checked on 2026-11-25 against RRF fork 0c446d62c (Meltingplot/RepRapFirmware#117) and upstream `3.7-ch` dc57fe098, Duet3Expansion fork 40006836 and upstream `3.7-ch` fc3180fa; the functions below are the same in fork and upstream. Read in the code, not run. Entry for upstream: K39 in `design/findings-upstream-summary.md`, with a sample.
  - RRF's InvalidateChannel calls `AbortStateMachine` for every level of the channel (upstream `SbcInterface.cpp:877-878`; fork `RRF/SBC/SbcInterface.cpp:1995`). For a probing state that calls `SuspendHeaters(false)` and `SetProbing(false)` (`RRF/GCodes/GCodes4.cpp:2088-2097`). The level that stays keeps its state, and a probing move that already runs goes on with its endstop check; its state machine ends probing itself when the move has ended (e.g. `:1425`).
  - Standalone RRF does not get there with a move in flight: `AbortPrint` is called on the channel itself, after a wait for standstill or after the queued motion has been discarded (`RRF/GCodes/GCodes4.cpp:164-168`, `RRF/GCodes/GCodeBuffer/StringParser.cpp:905`, `RRF/GCodes/GCodes3.cpp:1548-1562`), or by the emergency stop (`RRF/GCodes/GCodes.cpp:1120-1142`). The SBC abort comes from outside the channel, e.g. as the IPC command InvalidateChannel (`DuetControlServer/Commands/Generic/InvalidateChannel.cs:36`), which DSF passes on to RRF (`Processor.cs:1100-1110`; upstream `Processor.cs:788`, `:880`).
  - What ending probing does during the move, by probe type (RRF and Duet3Expansion code):
    - `M558 B1`: the suspended heaters switch on again (`RRF/Heating/Heat.cpp:1223-1232`, `LocalHeater.cpp:1031-1046`).
    - A local probe of type 5 or 8 drives its modulation output low (`RRF/Endstops/LocalZProbe.cpp:111-119`). Whether the probe still triggers then depends on what that output does in the hardware; not read.
    - A remote probe goes to the inactive report spacing of 25 ms instead of 2 ms (`RemoteZProbe.cpp:138`, `RemoteZProbe.h:64-65`). The board spaces the reports of a handle by it but sends a single state change at once when the last report is older (Duet3Expansion `InputMonitor.cpp:536-563`), so a trigger comes late only if the handle reported in the 25 ms before it. On an LDC1612 the change also stops touch mode (`InputMonitor.cpp:478-484`), which standard mode does not use.
    - A remote load cell also switches to baseline tracking during the approach (`RemoteZProbe.cpp:126-130`, Duet3Expansion `InputMonitor.cpp:507-517`).
    - A scanning probe in touch mode clears `touchTriggered` and sends nothing to the board (`RemoteZProbe.cpp:98-107`); the next touch report sets it again (`:323-326`), and `Stopped()` reads it (`ZProbe.cpp:357-363`).
  - CHX 350 (SZP, `M558 K0 P11`, no `B1`): by this reading the probing move still stops at the trigger in touch mode and in standard mode; in standard mode it comes late only after a report of the handle in the 25 ms before. Lab EP-17 checks it.
  - Decided (Tim, 2026-11-25): upstream's behaviour stays, the abort ends probing at once; no fork change, and no report to upstream for now. The emergency stop of the fork on a lost connection is not affected: it disables the drivers, switches the heaters off and stops the motion and CAN before the main task runs `AbortPrint` (`RRF/Platform/RepRap.cpp:1050-1117`, `RRF/GCodes/GCodes.cpp:1120-1142`, `:471-482`).

## Open again after taking upstream's fixes (2026-11-13)

Tim (2026-11-13): upstream code is the gold standard, and the fork diverges from it only for a good reason. Where upstream 61df011e fixed a finding that the fork had fixed in another way, the merge took upstream's fix. These cases that the fork fixes covered are open again:

- **Fixed and removed from this list (2026-11-13):** UP1 (#194; the stop is written and transferred before an error in RRF's data ends the transfer thread) UP2 (#194; a transferred reset completes a pending stop, because RRF performs an emergency stop before it resets, `SbcInterface.cpp:377-380`) and UP3 (#195; an object model answer that came with the transfer of the stop is from before the stop, so DSF asks again instead of applying it in a race with the M112 handler; the halted status was replaced in 5 of 20 runs of `Link.EmergencyStopStatus` before, in none of 50 after).
- **UP4. M997 flashes a firmware file of 1 to 3 bytes** for the main board, the only board DSF flashes itself (M997 S0 without B, `MCodeHandler.cs:1239`; expansion boards keep their bootloader and fetch the firmware from the main board, Tim 2026-11-13). Upstream refuses only an empty file. RRF itself refuses an image whose first word is not the top of RAM, the initial stack pointer (`RepRap.cpp:2063-2083` at RRF a7199f29f), which DSF cannot check without the RAM layout of the board. Test: `FirmwareUpdateFlashing.UpdateWithShortestFirmwareFile`. The UF2 part is fixed (2026-11-13, #197): M997 unpacks a UF2 file before it stops the plugins and refuses an invalid one, or one without a firmware image, like an empty file; before, it failed the update and DCS restarted.
- **UP5. Doubles are not clamped.** A JSON number beyond the double range (e.g. `1e400`) is stored as infinity, and the model cannot be serialized (O8). RRF sends no such number, only an IPC client that patches the model can. Upstream clamps floats only. Not fixed, Tim's decision (2026-11-13): RRF has no doubles, and DSF does not support them either.
- **UP6. A code cancelled while DCS stops is logged** as "Failed to process code in stage Executed" (`PipelineStackItem.cs`); the fork did not log such a cancellation (472daabd). `Startup.ExecutedStageShutdown.CodeWhoseHandlerIsCancelledWhileDcsStopsIsCompleted` accepts the log entry.
- **Fixed and removed from this list (2026-11-13):** UP7 (#196; with a base directory or volume path that ends with `/`, `0:/sys/..` and `0:/.` were reported as escaping the root, because `Path.GetFullPath` keeps the separator of the root but drops the one of a path that ends with `..` or `.`; `FilePathResolver.CombineContained` and `TryGetRelativeToVolume` normalize the root with `NormalizePath` now, as `IsVolumeRoot` does).

## Found by the census of the RRF pointer-check allowlist (2026-11-21)

Line numbers refer to RRF fork `3.7-dev` at 9a585f644. The census itself and its RRF fixes are in Meltingplot/RepRapFirmware (PRs #54-#63, report under `Tools/pointer-check/`).

- **K16. A late MacroCompleted with error pops the level of the caller.** Tim (2026-11-21): this goes into the design of SBC link v8 and is fixed in RRF and DSF together, not by a fork fix now.
  - RRF waits 3 s for a macro request (`SbcMaxRequestTime`, `SbcMessageFormats.h:42`). On a timeout `RequestMacroFile` clears `isWaitingForMacro` but leaves `requestedMacroFile` set (`GCodeBuffer.cpp:1324-1333`). `DoFileMacro` then reports the macro as not found and pushes no level (`GCodes.cpp:3719-3733`).
  - If DSF answers later with MacroCompleted and an error (it cannot open or refuses the macro, `Processor.cs:1157-1181`, `:1738-1745`), the packet misses the branch for a waiting channel and RRF runs `CloseFile`, `PopState` and `Init` on whatever level is on top (`SbcInterface.cpp:610-631`). That is the level of the macro that ran M98; at the base level of the File channel, `CloseFile` clears the job's file. DSF has popped its own level already, so the two stacks no longer match.
  - Example on the CHX: once the level of `trigger2.g` is popped, `IsTriggerBusy` (`GCodes.cpp:426`) can be false while DSF still runs `trigger2.g`, so the next trigger can be lost.
  - RRF #58 moves the pop into the main task. In the variant where the late error arrives while the Trigger channel waits on a request that has not gone out yet, it then pops the trigger macro that was pushed in the meantime, where the old code popped the idle base. The PR states this.
  - Upstream has the same code (upstream `3.7-ch`, `GCodeBuffer.cpp:1280-1287`, `SbcInterface.cpp:549-567`).
  - A fix only in RRF was considered: on a timeout, void a request that has not gone out, and push the level of one that has, as for `daemon.g`. It removes the 3 s timeout for requests that have gone out, which conflicts with the requirement that a DCS that cannot serve must time out.
- **K19. A late OpenFileResult overwrites `fileOffset`** in any state (`SbcInterface.cpp:1292`). Incoming packets are handled before the posted request is sent in the same transfer, so a seek or file list request posted after an open timed out can go out with the late file length as its offset or start index. It needs DSF to answer more than 3 s late. Found from the code, not tested. No decision yet.
- **K18. The return value of `WriteLocked` is ignored** (`SbcInterface.cpp:689`). Without room in the transmit buffer the Lock packet still counts as processed, and the Locked answer is lost while the main task holds the movement lock. Upstream does the same. Found from the code, not tested. No decision yet.


## Found by the lab tests of RRF #57 (2026-11-23)

Line numbers refer to DSF `v3.7-dev` at 22a94eb6.

- **Fixed and removed from this list (2026-11-23):** L7 (#204; a code after a failing code of a macro still ran).
  - Seen on the lab printer on 2026-11-23: RRF refused `var heaters = heat.heaters`, and the `set global` in the next line still ran.
  - The cause was two upstream defects:
    - Since upstream 2cbc04ae, `Code.ExecuteAsync()` gave the read-ahead codes the channel's token instead of the macro's, so "Aborted macro file" cancelled none of them.
    - `MacroFile.RunAsync` aborted the macro only when it awaited the failed code. By then the pipeline had gone on with the next codes.
  - `Code.SetException` now aborts the macro of a failed code. After a line that cannot be read, the lines before it still finish.
  - Tests: `Codes.MacroCodeAfterError`. Lab test A-14 checks a line after the refused assignment again; not run on the printer yet.
  - For an upstream report, use a line that upstream RRF refuses too, e.g. `set var.missing = 1` (`SbcInterface.cpp:1137-1143`).

Line numbers below refer to DSF `v3.7-dev` at 3db68bd7 and RRF fork `3.7-dev` at 7bfe6ad81.

- **L8. A failed code in a job does not stop the job.**
  - `JobProcessor.DoFilePrint` only logs an exception of a code and goes on with the next one (`JobProcessor.cs:553-561`). It aborts the job only for a line that cannot be read (`:510-527`).
  - RRF in standalone mode aborts the print when a meta command fails (`GCodes.cpp:875-882`, `AbortPrint`). In SBC mode RRF does not run meta commands; DSF does.
  - So a `set` or `var` that fails in a job file stops a macro (L7) but not the job.
  - Found from the code while fixing L7, not tested. No decision yet.

## Found by the lock census of RRF and Duet3Expansion (2026-11-23)

Census of error class 4: lock order, waits under a lock, priority inversion. Line numbers refer to RRF fork `3.7-dev` at 6c45e07a7 and Duet3Expansion fork `3.7-dev` at a325d93e. Upstream Duet3D RRF `3.7-dev` at 371c71412 has the same code in LC2 and LC5. No lock-order cycle was found in RRF (MB6HC) or Duet3Expansion (TOOL1LC, EXP1HCL, SZP). The census itself is not in a repository.

- **Fixed and not listed:** C1, the CAN receiver finishing the accelerometer file of a restarted board under `boardsLock` (RRF #112, which replaced #91; not merged yet).
- **Rule for the class (RRF #112, open):** no task waits for the SBC task while it holds a `ReadWriteLock` for writing. `SbcInterface::WaitForFileSlot` and `GCodeBuffer::RequestMacroFile` report an internal error when the caller holds one, counted per task by RRFLibraries #4. Read locks are not counted, so LC5 needs its own fix. Host test `Tools/sbc-wait-lock-test`.
- **LC2. M558 C, M950 H Q and M308 wait for the SBC task under write locks the SBC task needs.**
  - A parameter expression that calls `fileexists()` or `fileread()` waits up to 3 s for the SBC task in SBC mode (`MassStorage.cpp:1261-1265`, `SbcInterface.cpp:2362`, `:2682-2697`).
  - MAIN parses these parameters while it holds the write lock of the list. The locks and parse points are:
    - M558: `zProbesLock`, `EndstopsManager.cpp:750` -> `:794`;
    - M950 H: `heatersLock`, `Heat.cpp:572` -> `:575`;
    - M308: `sensorsLock`, `Heat.cpp:1090` -> `:1095`.
  - The fork's `ObjectModel::lifetimeLock` is held there as well (K22, 704b6a7e7).
  - `global` and `set global.` evaluate their expression under the write lock of the global variables (`StringParser.cpp:748` -> `:760`, `:778` -> `:825/843`, `GlobalVariables.cpp:74`; local variables have no lock). There the SBC task blocks only when it reads `global` or evaluates an expression with a global variable, because only `seqs.global` is live (`RepRap.cpp:264`, `:393`).
  - The live poll of DSF (every 100 ms) reads `heat.heaters`, `sensors.analog` and `sensors.probes` under these locks (`Heat.cpp:92-146`, `EndstopsManager.cpp:50-109`). The SBC task then blocks, the reply to the file request is not processed, and both tasks stall for 3 s. `fileexists()` returns false.
  - Only codes that RRF parses itself reach this: USB and PanelDue/aux in SBC mode (`GCodes.cpp:723-735`), and fork trigger actions (M581.1). DSF evaluates `fileexists()` and `fileread()` in the codes it sends (DSF `Code.cs:238`, `Functions.cs:26-28`).
  - An SBC-side fix (`ConditionalReadLocker` on `lifetimeLock`) was tried and dropped, because the list locks still block the poll (Tim 2026-11-23). Parsing the parameters before the write locks would change several upstream handlers; M308 hands the parameters to the sensor's `Configure` under the lock (`Heat.cpp:1114`).
  - Fix (Tim 2026-11-24, option A): RRF #112 (open) sends such a G, M or T command of text to DSF in `GCodes::ActOnCode`, as upstream sends other commands that DSF handles, and a `global` or `set global.` with them in `StringParser::ProcessConditionalGCode`. DSF substitutes the calls and sends the rest back. Needs DSF #237: DSF did not answer a firmware code that failed in DSF, so the RRF channel stayed busy until an abort (same code upstream).
  - DSF looked for a relative name in `fileexists()` in `0:/`, RRF in the system directory; `fileread()` reads from `0:/` in both. DSF #240 (open) makes DSF use the system directory, so with it the fix does not change the meaning of a relative name.
  - Open decision: a trigger action (`M581.1 U`) whose `set global.`/`global` or command calls one of these functions now goes to DSF, while the rule for actions (RRF 794e78625) is that M581.1 rejects what SBC mode passes to DSF. Triggers that DSF evaluates (`sbc.*`) are not affected: they run `triggerN.g` through DSF.
  - Found from the code and a host test of the SBC side, not seen on the printer. Upstream report candidate.
- **LC5. `fileexists()` with a heap string argument waits for the SBC task under `Heap::heapLock`.**
  - It keeps the read lock of `StringHandle::Get` (`StringHandle.cpp:104`) around `SysFileExists` (`ExpressionParser.cpp:2183-2187`). The comment there assumes that the call does not need the heap.
  - The SBC task needs the write lock when a packet it handles creates a heap string or array, e.g. a `set` (`StringHandle.cpp:23/39/59`, `Variable.cpp:152/166/186`). Then both tasks stall for 3 s. The live poll needs only the read lock.
  - `fileread()` already copies the name and releases the lock before it opens the file (`ExpressionParser.cpp:1219-1226`). `fileexists()` could do the same.
  - Fix: RRF #112 (open), `Platform::SysFileExists(const StringHandle&)` copies the name and releases the lock before it asks the SBC. The host test shows the 3 s stall with the old order.
  - Same trigger as LC2: codes that RRF parses itself in SBC mode. Found from the code. Upstream report candidate.
- **LC6. Creating an input monitor on a SAME5x expansion board delays all input change reports of that board by at least 2 ms.**
  - `InputMonitor::Create` (main task, priority 1) holds `listLock` for writing (`InputMonitor.cpp:369`) through `Activate` (`:406`), which waits `delay(2)` for the debouncer (`:75`, upstream fe8c1978).
  - The CAN async sender (priority 5) needs the read lock in `AddStateChanges` (`:539`, called at `CanInterface.cpp:1003`) before it sends input changes and stall endstop notifications. `ReadWriteLock` has no priority inheritance, so every task of priority 2 to 4 that runs meanwhile adds to the wait.
  - RRF waits for standstill before M574 and M558 create a monitor (`EndstopsManager.cpp:416`, `:470`, `:744` at RRF 6c45e07a7). M950 J does not (`GpInPort.cpp:75` -> `:150`), so only an M950 J during a move on another channel can delay an endstop or stall report of that move. SAMC21 boards have no delay there.
  - `Change` already calls `Activate` under the read lock (`InputMonitor.cpp:446`). `Create` could insert the monitor under the write lock and activate it under a read lock, because only the main task deletes monitors.
  - Low impact. Tim (2026-11-23): record it, no fix now. Upstream report candidate.
- **LC7. RRF drops the commands after one it sends to DSF on the same line of text.**
  - `GCodeBuffer::SentToSbc` calls `StringParser::Init` (`GCodeBuffer.h:488-493`), which sets `gcodeLineEnd` to 0, and RRF sends only the current command (`DataStart`/`DataLength`). So in `M503 G1 X5` from USB in SBC mode, where RRF sends `M503` to DSF (`GCodes2.cpp:738-759`), the `G1` is lost.
  - Upstream behaviour for every command that RRF sends to DSF. With RRF #112 it also applies to commands that call `fileexists()` or `fileread()`.
  - Found from the code while fixing LC2, not tested. No decision yet. Upstream report candidate.
- **LC8. The fake firmware holds the binary codes of a channel while its text code is with DSF; RRF takes them.**
  - `FakeGCodes.cs:596-600` holds them; RRF takes a binary code of such a channel (`GCodes.cpp:721-762` -> `SbcInterface::FillBuffer`), which ends `executingOnSbc`. So DSF tests cannot show a DoCode that DSF sends on to RRF finishing (`FirmwareCodeFileFunctions.CodeForRrfFinishesTheDoCode` is `[Explicit]`).
  - A trial alignment of the fake passed both cases and the 211 FakeFirmware/Link tests, but `Link/ChannelEpochStress.cs:837-844` relies on the holding (K13). Test infrastructure only. No decision yet.

## Found by the censuses of error classes 7, 5 and 6 (2026-11-23)

Census of error class 7 (barriers, DMA and caches, flash and NVM) and of classes 5 and 6 (shared non-pointer data, re-entrancy). Line numbers refer to RRF fork `3.7-dev` at 6c45e07a7, CoreN2G fork `3.7-dev` at 7d41892 and Duet3Expansion fork `3.7-dev` at a325d93e. MB6HC ELF addresses are from 6c45e07a7 plus RRF #91. Upstream Duet3D RRF `3.7-dev` at 371c71412, CoreN2G `3.7-dev` at 45ae60a and Duet3Expansion `3.7-dev` at 8bff6768 have the same code where a finding says upstream. The censuses themselves are not in a repository.

- **Fixed and not listed:** dma-cache F1, USB direct-read buffers not invalidated after the DMA (CoreN2G #4, test in RRF #92); dma-cache F3 and barriers F1, SAME70 `Cache::Flush` without a DSB (CoreN2G #5); barriers F2, LED strip DMA without a flush (RRF #93, with the ELF check `Tools/dma-barrier-check`); flash F7, the length in `EraseAndReset` (CoreN2G #6, test in RRF #94); flash F4, SAMC21 `EnsureWritten` returns before the last page is written (Duet3Expansion #30); class 5/6 F1, F4, F5 and F6, a stale "now" in `RemoteHeater`, `FilamentMonitor`, `RemoteFan` and `LocalFan` (RRF #95); class 5/6 F2, `UpdateSyncLockState` (Duet3Expansion #29); flash F6, the user row rewrite fills the page buffer with interrupts on (CoreN2G #7, test in Duet3Expansion #31); the class 5/6 F4 side item, a stale "now" in `PulsedFilamentMonitor` (RRF #95). None of these PRs is merged yet.
- **B7-1. Erasing the user signature on the MB6HC runs with the caches on** (flash census F1).
  - `NonVolatileMemory::EnsureWritten` erases with the caches on (`NonVolatileMemory.cpp:58-65`) and turns them off only around the write (`:70-75`). The erase waits in `efc_perform_fcr` without cache maintenance (CoreN2G `efc.c:383-394`).
  - The read sequence has the fork's I-cache invalidate after it (`efc.c:349-357`, CoreN2G #1, 5e43635). The erase has none. If instruction fetches during the erase return non-code data, an I-cache line filled then holds garbage until `Cache::Disable` at `:70` (SAME70 EEFC; datasheet, not checked here).
  - Trigger: M308 H or L thermistor calibration outside config.g (`Thermistor.cpp:120-125`, `:171-176`). The crash handler turns the caches off first (`ExceptionHandlers.cpp:38`).
  - Upstream code. A fix would turn the caches off around the erase as well, as `EnsureWritten` does for the write and CoreN2G `UniqueIdBase.cpp:56-61` does for the unique ID. Not done here: the code gives no reason for turning the caches off around the write (`NonVolatileMemory.cpp:70-75`; the comment in `Flash::WriteUserSignature`, CoreN2G `Flash.cpp:543`, is about the interrupts), so the write path says nothing about the erase.
  - Found from the code. Not seen on a board.
- **B7-2. After a user signature read the D-cache keeps signature data over the first 512 bytes of flash** (flash census F2).
  - `Flash::ReadUserSignature` invalidates the 512 bytes before the read only (CoreN2G `Flash.cpp:522-525`). The copy loop reads them through the D-cache (`efc.c:331-333`), because MPU region 1 makes them cacheable, write-through (`Cache.cpp:256-260`).
  - Flash there holds `exception_table` (0x00400000-0x00400167 in the ELF) and code up to 0x004001FF. VTOR points at it (CoreN2G `SAME70/Startup.cpp:149-153`). The firmware CRC check reads the range before the caches are on (`Tasks.cpp:187-213`, `:265-266`). That no other run-time data read of it exists is from the census, not re-checked.
  - Whether exception vector fetches look up the D-cache is in the Cortex-M7 TRM (datasheet, not checked here). Latent: code with a literal pool in that range would load signature data after the first read.
  - Trigger: every user signature read, e.g. M122 (`Platform.cpp:1313`, `:1330` -> `NonVolatileMemory.cpp:27`) and thermistor setup (`Thermistor.cpp:215-217`). Upstream code. An invalidate after the read removes it. Upstream stopped turning the caches off for the read on purpose (RRF c5ad761d1, "Don't disable cache when reading NVram").
  - Found from the code and the ELF. Not seen on a board.
- **B7-3. The bootloader update of an expansion board does not detect a failed erase or write** (flash census F5).
  - `UpdateBootloaderTask` erases the 16 KB bootloader in place and writes it again (Duet3Expansion `Tasks.cpp:896-922`). `flash_erase` and `flash_append` return `ERR_NONE` after the command whatever NVMCTRL reports (CoreN2G `SAME5x/hal/src/hal_flash.c:144-146`, `:168-170`, the same in SAMC21). The NVMCTRL error flags are used only as interrupt enables (SAME5x `hpl_nvmctrl.c:52-54`, `:360-362`).
  - Nothing reads the flash back. The verify code under `#if 0` (`Tasks.cpp:815-831`, `:834-851`) is in `UpdateFirmwareTask`, which is built for the RP2040 only (`#if RP2040` at `:705`, `#else` at `:854`, task choice at `:366-370`). The bootloader path of the SAME5x and SAMC21 boards has no verify code, disabled or not.
  - A silent write error, or a power cut between the erase (`:906`) and the end of the write (`:910`), leaves an erased or partial bootloader. That the board then needs SWD is from the census, not re-checked. The checks before the erase are the CRC of the image (`:880`) and Vin (`:884-894`).
  - Open: whether a read-back has to invalidate the NVMCTRL read cache first (left on, CoreN2G `SAME5x/Config/hpl_nvmctrl_config.h:22`, `:29`, SAMC21 `:31`), and on the SAME5x the CMCC when `USE_CACHE` is set (`Tasks.cpp:343-346`) (datasheet, not checked here).
  - Upstream code (David Crocker). Decided without Tim (2026-11-23): record only. Upstream report candidate.
  - Found from the code. Not seen on a board.
- **B7-4. Nothing checks that the IAP image stays below the handler stack of the MB6HC** (flash census F8).
  - `IAP_IMAGE_START` is 0x20458000 (`Pins_Duet3_MB6HC.h:14`). The handler stack is 0x2045FC00-0x20460000 (`_sstack`, `_estack` in the ELF; MSP set at CoreN2G `SAME70/Startup.cpp:138`). `PrepareToLoadIap` turns off the caches and the MPU but not the interrupts (`RepRap.cpp:2168-2202`). The SD path loads the image at `:2131`, the SBC path at `SbcInterface.cpp:1818-1831`.
  - An image larger than 31744 bytes (0x7C00) would overlap the stack that the interrupts use meanwhile. The IAP files of 3.7.0 are smaller: `Duet3_SBCiap32_MB6HC.bin` 20216 bytes (Duet3D package `reprapfirmware_3.7.0-1_all.deb`), `Duet3_SDiap32_MB6HC.bin` 20692 bytes (GitHub release 3.7.0).
  - The fork builds no IAP: DSF takes `reprapfirmware` from Duet3D (`pkg/firmware-version`, `pkg/deb/build.sh:82-88`), and DuetIAP is not a submodule (`.gitmodules`). Which IAP file is on the lab printer was not checked.
  - So there is no overlap today, only a limit that no build step checks. Upstream code. Found from the code and the ELF.
- **B7-5. The GMAC null descriptors of priority queues 1 to 5 are in cached RAM** (dma-cache census F2).
  - `gs_tx_desc_null` and `gs_rx_desc_null` have no `__nocache` (`GmacInterface.cpp:103`, `:105`). The ELF puts them at 0x20414920 and 0x20414928, above the non-cached area that ends at 0x20412000.
  - The CPU writes them once (`:785-801`), and no clean follows. Until the line is evicted, RAM keeps the startup zero, which marks a TX descriptor as owned by the GMAC. The census quotes DS60001527J §37.7.1.2-3 for this and for the GMAC reading all queues (datasheet, not checked here). The effect, a TX error or a frame from address 0, is from the census, not re-checked.
  - Only standalone mode with Ethernet. SBC mode never starts the network (`RepRap.cpp:623-641`) and refuses network codes (`GCodes.cpp:5784-5791`).
  - Upstream code (David Crocker, 4a0ee2e48, da9c9f5f5). The fix is `__nocache` on both. Upstream report candidate.
  - Found from the code and the ELF. Not seen on a board.
- **B7-6. XDMAC channels are reprogrammed right after the disable, without waiting until they have stopped** (dma-cache census F4).
  - Sites: HSMCI (CoreN2G `hsmci.c:1080`, `:1146`, `:1184`, `:1248`), TMC51xx (`TMC51xx.cpp:1671-1682`), LED strips (`LocalLedStrip.cpp:341-365`), SBC SPI (`DataTransfer.cpp:121-122`, then `:194`, `:249`), WiFi (`WiFiInterface.cpp:2029`, `:2044`).
  - The SBC task already polls `XDMAC_GS` before it reads the data (`DataTransfer.cpp:146-149`, `:1058-1061`).
  - In normal operation the previous transfer has ended. It matters only after an abort or a timeout. Whether a channel is still active then is in the datasheet (DS60001527J §35.5.4.4; datasheet, not checked here).
  - Upstream code. Found from the code. Not seen on a board.
- **B7-7. `DmacManager::DisableChannel` can fail on the SAME5x and SAMC21, and most callers ignore it** (dma-cache census F5).
  - It returns false when ENABLE has not cleared after 20 polls (CoreN2G `SAME5x_C21/DmacManager.cpp:207-244`). Only RRF WiFi checks it (`WiFiInterface.cpp:2448-2451`).
  - Callers that ignore it: Duet3Expansion `TMC51xx.cpp:1708-1709`, `:1747-1748`, `TMC22xx.cpp:962-963`, `:1060-1061`, `:1915-1916`, `LocalLedStrip.cpp:296`, `:305`; CoreN2G `SharedI2CMaster.cpp:243`, `:302`, `:410`, `SpiDevice.cpp:159`, `:230`, SAMC21 `AnalogIn.cpp:119`, `:168`, `:273`, `:339`, `:437`.
  - The next `EnableChannel` then sets ENABLE on a channel that still runs (`DmacManager.cpp:183-201`). That the DMAC keeps the old descriptor then is from the census (datasheet, not checked here). How often the disable fails is unknown.
  - Upstream code (David Crocker). Found from the code. Not seen on a board.
- **B7-8. `hsmciIdle` reads `XDMAC_CIS` and can take the block-end flag that the HSMCI driver waits for** (dma-cache census F6).
  - `hsmciIdle` reads the register (`RepRap.cpp:128-131`), and the driver reads it again for `XDMAC_CIS_BIS` (CoreN2G `hsmci.c:1160`, `:1259`). Reading clears it (datasheet, not checked here), so the wait sleeps until XFRDONE or the 200 ms timeout (`RepRap.cpp:142-146`).
  - The BIS check runs only when a command needs more than one DMA start (`checkDmaEnded`, `hsmci.c:1133`, `:1235`). RRF starts one per command (`sd_mmc_mem.cpp:112`, `:131`), so this is latent.
  - Upstream code (David Crocker, 2018). Found from the code. Not seen on a board.
- **B7-9. On the EXP1HCL, Cortex-M4 erratum 838869 could run the ADC interrupt twice, and the ADC handler does not check its flag** (barriers census F3).
  - The SAME51 is a Cortex-M4 r0p1 (CoreN2G `same51n19a.h:552`). ARM's errata notice could not be read (both documentation-service.arm.com links gave 404). TI SWRZ118A (CC1311P3, entry CPU_01) reproduces erratum 838869: while handler A runs and B is pending, a store with an immediate offset to a bufferable location followed by "A BX instruction ... that causes an exception return", and an interrupt C of a priority between A and B, can make the processor clear C and run B twice. Which Cortex-M4 revisions are affected is not in that copy; `pop {pc}` is not named.
  - The A role exists on the EXP1HCL: `DMAC_0_Handler` (priority 3) stores to the DMAC interrupt flags with `strb.w r1, [r3, #78]` (0x3100c) and returns with `bx lr` (0x3101a) when no callback is set. CAN (4) fits C and the ADC (5) fits B (`EXP1HCL.h:63-69`). Whether a store to a peripheral counts as bufferable here is in the ARM documents (datasheet, not checked here).
  - `AdcClass::ResultReadyInterrupt` reads `RESULT` and acts on `state` only, without a check of `INTFLAG.RESRDY` (CoreN2G `SAME5x/AnalogIn.cpp:339-362`). A second run while `state` is `converting` stores `RESULT` again for the next channel (`:347`, `:356`) and starts the conversion again (`:360`), so one scan of thermistor and Vin values is shifted by one channel.
  - The other EXP1HCL handlers re-check their flags (`CanDevice.cpp:1255`, `DmacManager.cpp:360-361`, Duet3Expansion `StepTimer.cpp:426-427`). ARM's workaround in the TI copy is a DSB between the store and the BX.
  - Upstream code (David Crocker). Found from the code and the ELF. Not seen on a board.
- **SD-1. A step error can lose its heater shutdown and its message when two producers use the generic debug buffer** (class 5/6 census F3).
  - One buffer and two flags (`Platform.h:530-536`) have several producers: the step interrupt (`Move.cpp:2918-2923`, `DriveMovement.cpp:471-484`) and the Move task (`Move.cpp:1934-1939`, `:2191-2200`). MAIN prints the message, checks `shouldTurnOffHeaters` and clears `hasGenericDebug` (`Platform.cpp:788-796`). The comment at `Platform.h:530-533` assumes one producer.
  - Example: the Move task posts a zero-duration message without the heater flag (`Move.cpp:1939`). MAIN reads `shouldTurnOffHeaters` as false (`Platform.cpp:791`). The step interrupt then logs a step error and sets both flags. MAIN clears `hasGenericDebug` (`:795`), so the step error is not shown and `SwitchOffAll` does not run for it. A producer that writes while MAIN prints garbles the message.
  - After a step error the Move task ends (`Move.cpp:682-685`; `hadStepError` is never cleared, `Move.h:512`, `:841`). The machine cannot move until a reset, and the heaters stay on. That heater fault protection still runs is from the census, not re-checked.
  - Upstream code (David Crocker 2024-2025, Christian Hammacher 2026; the step interrupt part 1db5c6755). Upstream report candidate.
  - Found from the code. Not seen on a board.
- **SD-2. The fork's M122 value "longest abort apply" can wrap to 4294967295** (class 5/6 census F7).
  - The SBC task stores `whenAbortRecorded = millis()` without the GB mutex (`SbcInterface.cpp:1964`, comment at `:2070`). MAIN computes `millis() - whenAbortRecorded` after its critical section (`:2104`, section at `:2076-2091`). The ELF loads the stamp after the clock call (0x492756, 0x49275a).
  - An abort recorded between the two gives 0xFFFFFFFF, and `maxAbortApplyDelay` in M122 (`:2133-2134`) keeps it until a reset. One field serves all channels (`SbcInterface.h:104`), so a second channel's record also shortens the delay measured for the first.
  - Diagnostic only. Fork code (0ae497a19, 9b0047f0a, 2026-11-10). The real fix is a stamp per channel. Decided without Tim (2026-11-23): record only.
  - Found from the code and the ELF. Not seen on a board.
- **SD-3. `TaskGetRunTimeTicks` can count the 32-bit wrap twice** (class 5/6 census F8).
  - It keeps `msw` and `ticksAtLastCall` in statics without a critical section (`Tasks.cpp:334-343`). The context switch calls it (FreeRTOS `FreeRTOSConfig.h:393`, `tasks.c:5140`), and so does M122 in MAIN through `TaskResetRunTimeCounter` (`Tasks.cpp:346-354`, `:373`).
  - A context switch in MAIN between the wrap check (`:340`) and the store (`:341`) makes both increment `msw`. The run time then jumps by 2^32 step clocks, about 95 minutes (comment at `:332`), and one M122 report shows wrong CPU shares. No control effect.
  - Upstream code (David Crocker, bcffe0cbd; the same in Duet3Expansion `Tasks.cpp:955-975`, 1f0a99b72). Found from the code. Not seen on a board.
- **SD-4. M111 debug output can lose or mix characters** (class 5/6 census M1). `isrDebugBuffer` is a `RingBuffer` for "max 1 getter and 1 putter" (RRFLibraries `RingBuffer.h:59`), but `debugPrintf` puts into it from any task or interrupt (`RepRapFirmware.cpp:211-218`, `Platform.cpp:385-394`). Debug only. Upstream code, found from the code, not seen on a board.
- **SD-5. An emergency pause can be reported with a stale second file position** (class 5/6 census M2). `SetEmergencyPauseReason` sets the fields without the `TaskCriticalSectionLocker` of `SetPauseReason` (`SbcInterface.h:201-208` vs `:192-199`). MAIN calls it (`GCodes.cpp:1504-1509`), and the SBC task reads the fields (`SbcInterface.cpp:1811-1815`). The ELF stores `reportPause` before `pauseFilePosition2` (0x4250b6, 0x4250ba; which field is which offset is from the census, not re-checked). Very low. Upstream code (Christian Hammacher), found from the code and the ELF, not seen on a board.
- **SD-6. Print time accounting updates several fields without the lock of the snapshot** (class 5/6 census M3). `PrintMonitor.cpp:257-282` updates them without the lock that the snapshot takes at `:292-298`. The getters at `:309`, `:315` and `:493` read them, the first two for the object model (`:81`, `:84`). Cosmetic times. Upstream code, found from the code, not seen on a board.
- **SD-7. A temperature read can pair a new error code with an old value** (class 5/6 census M4). `SetResult` stores `lastResult` before `lastTemperature` (`TemperatureSensor.cpp:182-183`, `:194-196`), and `GetLatestTemperature` reads them the other way round (`:69-70`). The ELF order was not checked. Low. Upstream code, found from the code, not seen on a board.
- **SD-8. M307 can mix old and new heater parameters for one control cycle** (class 5/6 census M5). MAIN updates the model and the PID constants (`Heater.cpp:241-244`, `FOPDT.cpp:231-243`) while the Heat task uses them (`LocalHeater.cpp:474-498`). Negligible. Upstream code, found from the code, not seen on a board.
- **Notes, no defect:** flash F3 (the I-cache invalidate of CoreN2G #1, `efc.c:349-357`, is fork only); barriers F4 (CMSIS 5.4.0 `ARM_MPU_Enable` and `ARM_MPU_Disable` have their barriers before the MPU write, `mpu_armv7.h:188-208`); barriers F5 and F7, architectural only (GMAC descriptor order, `GmacInterface.cpp:392-403`, `:488-495`, where CoreN2G #5 adds a DSB at `:395`; MCAN RX acknowledge, CoreN2G `CanDevice.cpp:895-898`); barriers F6 (the TMC51xx DMA start relies on the DSB in `ClearCurrentTaskNotifyCount`, `TMC51xx.cpp:1994`); dma-cache F7 (`TMC51xx.cpp:1681` reads the Rx status in the Tx block; `SpiDevice.cpp:169` ignores a timeout). That F4 is harmless at its call sites, the DSB of F6 and that no board in scope uses the `SpiDevice` branch are from the census, not re-checked.

## Found by the census of CAN receive (error class 3, 2026-11-24)

A read-only census of what RRF and Duet3Expansion do with the data of a CAN message, against the forks' `3.7-dev` (RRF 6c45e07a7,
Duet3Expansion a325d93e, CANlib 608ac48). It covers the checks upstream added on 2026-11-13 (RRF 43a6b4fa8, Duet3Expansion 162803de,
CANlib cf53186). Every finding is upstream code, and Duet3D `3.7-dev` and `3.7-ch` have the same code. None was seen on a board.

- **Fixed and not listed:**
  - CAN-R1: in expansion mode, set-driver-states wrote `driverState[]` beyond its end on the Mini 5+ for drivers 12 to 15, and
    stop-drivers stopped drives that are not local drivers (RRF #101, as Duet3Expansion 162803de).
  - CAN-E1: on the EXP1HCL, M23CL and TOOLINDX, `M915 P<address>.5 R1` and then `M915 P<address>.5` read `dms[]` beyond its end
    (Duet3Expansion #32, which refuses the request as the main board refuses its own drivers).
  - CAN-R2: the driver data accessors of `ExpansionManager` (`ExpansionManager.cpp:426-458`) indexed `driverData`, allocated with the
    announced `numDrivers` (`:226-238`), with the G-code driver number unchecked. RRF #111 treats a driver at or beyond the count like
    a board without driver data, as `SmartDrivers::GetDriverMode` does for local drivers. A write needs a board whose M569 reply and
    announcement disagree; a read a configuration mistake, e.g. the Hangprinter's `GetDirectionValue` for a remote driver (from the
    census, not traced again).
  - None of these PRs is merged yet.
- **CAN-R3. Accelerometer data with a resolution of 0 or 1 bits shift by more than 31.**
  - `GetBitsAfterPoint` returns `dataResolution - 2` as `unsigned int` (`Accelerometers.cpp:148-151`), and the receiver uses the
    resolution of the message in `1u << GetBitsAfterPoint(receivedResolution)` and `1u << (receivedResolution - 1)` (`:770-778`).
  - Undefined behaviour, no memory access: the frame length check at `:716` holds.
- **CAN-G1. Most receivers read the fields of a frame without checking them against its length.**
  - CANlib aea376e (dc42, 2026-11-18) added `ValidMessageLength` to the message structs (56 places in `CanMessageFormats.h`), but
    nothing in RRF or Duet3Expansion calls it yet.
  - The CoreN2G driver copies only the received bytes into the buffer (`CanDevice.cpp:785-850`, from the census), so a short frame gives
    the bytes of the previous message: wrong data, never outside the buffer.
  - Upstream is adding these checks, so this is left to upstream (CLAUDE.md, bug fixes 8).
- **Minor, from the census, not traced again:** division by a value from the message in the revert-position code (infinity, no memory
  effect); the board type string of a firmware request goes unfiltered into a file name; Duet3Expansion stores CAN timing from a
  message without checking it; on the main board a G-code driver number up to 255 goes into a 16-bit bitmap with `SetBit`
  (`CanDriversData.cpp:48`), possibly a shift beyond the width.
- Not reached by the census: the generic configure handlers of RRF in expansion mode, M655, the diagnostic tests and the heater model
  of Duet3Expansion, ATE-only code.

## Found by the fuzz targets of error class 3 (2026-11-24)

Error class 3 covers bounds and undefined behaviour at trust boundaries. The boundary fuzzed here is the SBC link from DSF to RRF. Two
host fuzz targets cover it, both in RRF #96, with AddressSanitizer and UBSan:
- `Tools/sbc-packet-fuzz`: the read side of `DataTransfer` and the packet loop of `SbcInterface`.
- `Tools/binary-code-fuzz`: `BinaryParser`, from `Put` to the getters, `WriteParameters` and `AddParameters`.

The finding IDs below are those of the two READMEs. Line numbers refer to RRF fork `3.7-dev` at 6c45e07a7. All defect lines are upstream
code, and every defect here is in upstream Duet3D RRF `3.7-dev` at 371c71412 as well, there at other line numbers.

The fork already has some checks of Christian Hammacher from upstream `3.7-ch` that `3.7-dev` lacks (43a6b4fa8, 2026-11-13, "Counts,
bitmaps and lengths received over CAN or from the SBC are checked before they index fixed-size arrays"):
- the header check in `ReadPacket`;
- the limit of `numFilaments` to the array in `ReadPrintStartedInfo`;
- the `ULong` step in `WriteParameters`.

Upstream is working on this area. When its fix for a finding here comes, it replaces ours (CLAUDE.md, bug fixes 8).

A DSF that writes what `Writer.cs` writes reaches only B6, B8 and B9. The others need one of these:
- a DSF or SBC process that writes inconsistent lengths;
- a corruption that the CRC32 of SPI misses;
- on USB, any corruption that reaches RRF.

- **Fixed and not listed:**
  - P1 to P5: lengths inside an SBC packet were not checked against the packet (RRF #99).
  - B1 to B5 and B7: `numParameters` and the value lengths of a binary code were not checked against the code (RRF #97).
  - B6: INT_MIN printed with `%d` (RRFLibraries #3, test in RRF #98).
  - B8: `GetIPAddress` and `GetMacAddress` parsed beyond the string value; they parse a null-terminated copy now, as `StringParser` does,
    and refuse a value of more than 20 characters, as `StringParser` refuses a quoted one (RRF #100). Its host test shows a second
    effect of B8: before the fix, `10.0.0.1` (8 characters, so no null after it) was refused after a code that left `.10` behind it in
    the buffer, so a correct DSF could get "invalid IP address" depending on the code before. Host only, not seen on a board.
  - B9: `StringHandle::InternalAssign` read one byte beyond a string that is not null-terminated (RRF #100).
  - RV-1, found by the review of #97, #99 and #100: `ObjectModelKeyChanged` compared the `StringHeader` that DSF writes before the key
    (`SPI.cs:1730-1737`, `USB.cs:1819-1827`, `Writer.cs:729-744`) with "network" and "volumes" (`SbcInterface.cpp:1454-1462`, upstream
    d3c4957e4), so `NetworkUpdated` and `VolumesUpdated` never ran in SBC mode and `seqs.network` and `seqs.volumes` of RRF did not
    change. RRF #105 reads the key after its header with its length, as far as it lies within the packet.
  - RV-2, found by the same review: `take` and `drop` of a heap string held a raw pointer into the heap across the allocation of
    their result (`ExpressionParser.cpp:1111-1117`, `:1190-1196`). The allocation can garbage-collect, and the collection moves the
    strings with `memmove` and adjusts only the handles (`Heap.cpp:314-337`, `:133-135`), so the result had wrong text. RRF #106
    copies through the handle after allocating, and `make take-drop` in `Tools/expression-fuzz` reproduces it with the real parser.
  - #97 and #99 contain the commits of #96, #100 those of #97, and #106 those of #103 (which has those of #96). #105 is
    independent. None of these PRs is merged yet.
- **FZ-P6. SBC packets that are shorter than what their reader reads.**
  - Most of these reads stay within the transfer buffer and get stale bytes of an earlier transfer. The fuzz target shows them with
    `FUZZ_POISON_STALE=1`:
    - `ReadDataHeader` reads the whole header of a packet that is shorter than it (`DataTransfer.cpp:635-640`).
    - `ReadSetVariable` and `ReadDeleteLocalVariable` read their 8-bit lengths without a check against the packet (`:735-759`).
    - `ObjectModelKeyChanged` compared the key as a null-terminated string (`SbcInterface.cpp:1455`); RRF #105 (RV-1) reads it with
      its length now.
  - Open question for Tim: what the protocol layer does with a packet that is too short. RRF #99 cuts a value at the packet end,
    following the existing pattern in `DataTransfer`. For a header there is no such pattern. The options are to cut it (zero the
    rest), to refuse the packet, or to report it to the logic, which is the link v8 design.
  - Found by the fuzz target. Not seen on a board.
- **FZ-P7. Bool fields of SBC packets load any byte.** `ReadBoolean` and the other readers with a bool field load a packet byte other
  than 0 or 1 as `bool` (`DataTransfer.cpp:642-646`). UBSan reports it with `UBSAN_BOOL=1`. What the firmware build makes of such a
  byte was not checked. Found by the fuzz target. Not seen on a board.
- **Notes, no defect:**
  - `WriteParameters` steps over the values of `BoolArray`, `ULong`, `Bitmap64` and `DateTime` parameters, but `Seen` and
    `AddParameters` do not. The `ULong` and `Bitmap64` step is from 43a6b4fa8; upstream `3.7-dev` does not step over them. So a code with one of them before a string or array finds the wrong value. DSF does not send these types
    (`Writer.cs:175-236`). The check of RRF #97 follows the steps of `WriteParameters`, so the reads of `Seen` and `AddParameters` stay
    within the code either way.
  - `GetIPAddress` accepts anything after the fourth number that does not start with a dot (`BinaryParser.cpp:399-406`), as before.

## Found by the expression parser fuzz target (error class 3, 2026-11-24)

An expression is user input from every channel: `echo {...}`, `if`, `var`, `set`, and every parameter in `{...}`, from DWC and DSF,
macros, job files, USB and PanelDue. The host fuzz target `Tools/expression-fuzz` (RRF #103) runs the real `ExpressionParser`, object
model code, string and array heap and `SafeStrptime` with AddressSanitizer and UBSan, and models the board stack with the frame sizes of
the MB6HC ELF.

The IDs are those of its README with the prefix FZ-. Line numbers are RRF fork `3.7-dev` 6c45e07a7. All defect lines are upstream code
(David Crocker 2020-2026), and origin `3.7-dev` and `3.7-ch` have the same code. Each one is reached by the text of an expression
alone. The way from a G-code line to the parser was not part of the target, and only FZ-E6 was seen on a board.

- **Fixed and not listed:**
  - FZ-E5 (high, board only): `vector(n, v)` with n of 2^29 or more wrapped the size `4 + n * 8` in the 32-bit `size_t` of
    `ArrayHandle::Allocate` (`ArrayHandle.cpp:29-42`). It then wrote n elements from the start of a 2 KB heap block, over the whole RAM.
    Confirmed in the MB6HC disassembly, not run on a board. RRF #102 refuses such a size with "Array too large", the message for a
    size that does not wrap.
  - FZ-E6: `SafeStrptime` indexed `_DAYS_BEFORE_MONTH[12]` with a `%m` outside 1 to 12 (`strptime.cpp:144`, `:237`, `:273`), reached by
    `datetime("2026-99-01T00:00:00")`, `M905 P` and the HTTP time in standalone mode. RRF #104 refuses such a month. The month only;
    FZ-E7 stays open.
    - Board (lab printer, SBC mode, 2026-11-24, firmware without #104): `global dtTest = "2026-99-01T00:00:00"`, then
      `echo {datetime(global.dtTest)}` gave `2034-03-01T00:00:00`. The read of `_DAYS_BEFORE_MONTH[98]` did not fault, and `mktime`
      turned month 98 into 8 years and 2 months. With #104 the reply should be "string is not a valid date and time"
      (`ExpressionParser.cpp:2441`); not run on a board yet.
    - In SBC mode only a string from a variable reaches RRF. DSF evaluates `echo {datetime("2026-99-01T00:00:00")}` itself and refuses
      it with "can't convert value to DateTime" (DSF `Codes/Meta/Parsing/MetaExpressionParser.cs:1119-1138` at 8507c98c0). When
      DuetPiManagementPlugin runs, it checks the date of `M905 P` first and refuses it with the same text as RRF, "Invalid date format"
      (`DuetPiManagementPlugin/Program.cs:610-623`).
  - FZ-E3: the float to int32 range checks of `floor`, `ceil`, `round` (`ExpressionParser.cpp:2034-2036`) and `pow` (`:2293-2295`)
    tested `<= (float)INT32_MAX`, which is 2^31, so 2^31 reached the conversion: INT_MIN on the host, 2147483647 on the MB6HC
    (`vcvt.s32.f32` saturates; read from the disassembly). RRF #110 (stacked on #108) converts only below 2^31; `pow(-2, 31)` stays
    INT_MIN. Behaviour change: the float 2147483648 instead of an int.
  - FZ-O3 and FZ-E7a: `first_day` (`strptime.cpp:76-84`) looped once per year since 1970, in the main task, for any year of `%Y`:
    about 2.1e9 times for 2147483647, 20 s on the host. On the MB6HC this loop has 15 instructions per year (300 MHz), so I estimate one
    to two minutes, more than the 20 s after which `RepRap::Tick` resets with `stuckInSpin` (`RepRap.h:211`); not measured. Every caller
    passes the result to newlib's `mktime`, which returns -1 for a `tm_year` beyond +/- 10000. RRF #109 (stacked on #104) refuses a
    year outside 1900 +/- 10000, which also removes the overflow of `ret - 1900` near INT_MIN (FZ-E7a). Behaviour change: such a year
    is an error instead of the date-time -1.
  - FZ-E8 (medium): a deeply nested expression that ends in a quoted string threw with less than `StackUsage::Throw` left.
    `ParseInternal` called `ParseQuotedString` (312-byte frame) without `CheckStack`, against the rule in the comment of
    `CheckStack`, and `ParseGeneralArray` called `ParseInternal` without `CheckStack(StackUsage::ParseInternal)`, unlike every other
    call. RRF #107 adds both checks; a sweep of the board stack budget from 2000 to 6000 bytes went from 27 short throws to none.
  - FZ-E4: `ParseArray`, `take` and `drop` read a nested object model array (e.g. `move.axes[1].drivers`) without its parent index,
    which `ApplyObjectModelArrayIndex`, `Concat`, `#` and `AppendAsString` put into the context, so they gave the elements of axis 0
    after an internal error. RRF #108 adds the same `AddIndex(param >> 8)`.
- **FZ-E1. Signed overflow of the int32 operators.** Unary minus (`ExpressionParser.cpp:210`, "TODO overflow check" in the code), `+`
  (`:531`), `-` (`:567`) and `*` (`:581`), e.g. `{65536 * 65536}`. Undefined behaviour; that it wraps on the board was not checked.
  Low.
- **FZ-E2. `mod(INT_MIN, -1)`.** The check before `rslt.iVal %= nextOperand.iVal` (`:2060`) only excludes 0. On x86 it traps. On the
  MB6HC the `%` is `sdiv` + `mls` (`0x41a25c` in the MB6HC build of RRF #108). Per the SDIV pseudocode of the ARMv7-M ARM, the
  quotient keeps its low 32 bits (0x80000000), so the remainder is 0, the right value; undefined behaviour in C++ only, not run on a
  board. Low.
- **FZ-E7b. Signed overflow in `SafeStrptime`.** `%d` of INT_MAX (`strptime.cpp:285`). Wrong date. Low. (FZ-E7a, `%Y` of INT_MIN at
  `:186`, is fixed by RRF #109, see above.)
- **Observations without a sanitizer report:**
  - FZ-O1: `ObjectModel.cpp:80-84` ignores a failure of `gmtime_r`, so a date-time beyond its year range prints uninitialised `tm`
    fields (host: `datetime(0) - 1`). Host only: newlib's `gmtime_r` in the MB6HC build has one exit, which returns `res`
    (`0x4d5ad0`), and no NULL return. A date-time far out of range gives a wrong date on the board, not uninitialised fields.
    `Platform.cpp:1394` and `:4102` check the return of `gmtime_r`; the other callers do not.
  - FZ-O2: two stack figures quoted in `ExpressionParser.cpp:48` and `ObjectModel.cpp` are 8 bytes below the ELF figures, within
    `StackUsage::Margin`.
  - FZ-O4: `BinaryParser.cpp:533-611` calls the array parsers without checking for `{`, which `StringParser.cpp:1370` does. A correct
    DSF does not send such a parameter: DuetAPI marks a parameter as an expression only when it starts with `{` and ends with `}`
    (`Parameter.cs:96-99`, `:176-181`), and only then `Writer.cs:228` sends `DataType.Expression`. Only a broken SBC reaches it; the
    fuzz target saw no sanitizer report there.
  - `labs(INT_MIN)` at `ExpressionParser.cpp:1937` is undefined only where `long` has 32 bits, as on the board; the host cannot show it.
  - FZ-O5 (found by reading, while checking FZ-E3): `Kinematics::RoundToInt32` (`Kinematics.cpp:366`) and
    `Move::MotorMovementToSteps` (`Move.cpp:1276`) compare with `(float)(INT32_MAX - 10)`, which is 2^31 in float, so the margin of 10
    steps is lost and a position of exactly 2^31 microsteps passes; `lrintf` then saturates to INT32_MAX on the MB6HC (see FZ-E3).
    Motion code: what this does to the moves that follow was not checked. Recorded only.

## Found by the error class census of DSF (2026-11-24)

Census of DSF for lock order and waits under locks, re-entrancy (A); shared data and memory ordering (B); stack depth and the failure paths of async code and hosted services (C); bounds, unsafe code and untrusted lengths (D); and the time base, timeouts measured with the wall clock. Line numbers refer to DSF `v3.7-dev` at 8507c98c0. Paths are relative to `src/`; a path without a project folder is in `DuetControlServer/`. RRF lines refer to the RRF fork `3.7-dev` at 6c45e07a7. Each finding was compared with upstream `v3.7-dev` at dbf9b0447, which 8507c98c0 contains; a finding is upstream code unless it says fork. The IDs are those of the census: SD1 to SD7 are not SD-1 to SD-8 of the RRF census above, and T1 to T5 are not the trigger findings T1 to T3. The findings are from the code; where an effect was measured, the census did it in a scratch program with the fork's DLLs, as stated. The census itself is not in a repository.

A second round covered the classes that exist because DSF is a long-running .NET process on Linux: resources and long uptime (E), the thread pool and scheduling (F), culture, text encoding and number formats (G), the file system and storage (H1), and the integration with Linux processes and systemd services (H2). Its IDs are RL (E), TP (F), CU (G), FS (H1) and LX (H2). Line numbers and paths follow the rules above. RRF lines of round 2 refer to the RRF fork `3.7-dev` at 6c45e07a7 (H1), cde4e4fa1 (F and H2) or 2e31913b0, 3.7.0+1.mp.11 (G); for E the census read `3.7-dev` without naming a commit. Each finding was compared with `upstream/v3.7-dev` as in round 1. Where an effect was measured, the census did it in scratch programs that copy the shape of the code, on .NET 10.0.12 on x86_64 WSL; aarch64 was not measured.

- **Fixed and not listed:**
  - Time base: the session timeout of `SessionStorage`, the wait of `FirmwareUpdater` for an expansion board and the detection of a time change for M905 in `PeriodicUpdateService` were measured with the wall clock (DSF #215).
  - DL1: M581.1 with SBC fields in a macro, config.g or job hung or failed (DSF #216).
  - SD1: the busy flag of a pipeline stage was lost, so flushes returned early (DSF #217).
  - ST1: a deeply nested expression ended DCS with a stack overflow (DSF #218). Upstream 7bdfc31c9 (2026-11-25) added the same check and text and the 255-element array limit of DSF #239; the fork took upstream's code and keeps the short trace of LT3 (DSF #251).
  - UB1, UB2, UB3 and SD6: job file info that does not fit the SetPrintFileInfo packet, the allocation for a filament index from the file, file positions over 4 GiB, and the unlocked read of the job file info (DSF #219).
  - DL4, DL5, AS1 and AS2: the job processor ended for good, or DCS stopped, after a cancelled or refused request in the rewind or the file selection, or after a failed write of the simulated time (DSF #221).
  - AS3: a code that RRF passes to DSF and that DSF cannot parse or that fails in DSF was never answered, so RRF's channel stayed busy (DSF #220).
  - FS1 and FS3: files that RRF writes through DSF (config-override.g after M500, resurrect.g on a pause or power failure) were not synced when RRF closed them, and a full disk was answered as a successful write, so they could be lost or left empty; and with event logging on, a full disk stopped DCS at the next logged message, which aborted the running print (DSF #226 and #227).
  - FS2: rr_upload wrote into the target, so a failed upload deleted the file it replaced (H3 of #123) and an upload under the name of the printed file truncated it under the job (DSF #222).
  - CU1: a value beyond the float range in the last Z move or the filament comments of a job file made its file info, and once the file was selected the whole object model, unserializable (DSF #223).
  - CU9: `datetime()` of a string that DSF evaluated itself refused strings that RRF accepts, such as fields without leading zeros or a day out of range, and gave another error than RRF for a bad date; such strings now go to RRF (DSF #238). The formats that only .NET accepts (date only, `12/01/2026`, a space instead of `T`) and the conversion of a time zone to local time stay a feature of the SBC (Tim, 2026-11-24), so such a string still works as a literal and not from a variable. Found by the board test of RRF #104, not by the census.
  - TP3: the link thread waited for the disk read of the job reader when RRF asked for a macro or an M291 acknowledgement for a buffered job code, so a storage stall of 4 s or more lost the SBC link, and the fork's RRF then performed an emergency stop (DSF #228).
  - LX4: duetwebserver.service had no `Restart=`, so the web interface stayed down until a reboot after DWS ended unexpectedly (DSF #225).
  - runonce.g was not run in SBC mode since upstream c5e3214be dropped the 3.6 code (DSF #224).
  - Security, published as normal PRs by Tim's decision (2026-11-24: the printers run in company networks behind firewalls, so no embargo; Duet3D gets the report after the PRs):
    - UB5: the root plugin service unpacked a combined system package into a directory in /tmp named after the upload file and installed every file it found there, so a local process could get its own packages or `update.sh` run as root (DSF #231).
    - FS4 and LX2: the root plugin service ran the commands of `plugins.json` and loaded profiles made from `apparmor.conf`, which the packages gave to dsf, and it ran in /opt/dsf/plugins, where dsf may create files that it read as configuration (`appsettings.json`) or that Python imported as root (`venv.py`, `pip.py`). So the dsf user could choose commands that run as root (DSF #234).
    - FS5: DCS had CAP_DAC_READ_SEARCH and CAP_SYS_PTRACE in its effective set, so M36.2 and the other file codes returned files only root may read, e.g. the SSH host keys of the /etc/ssh volume on the CHX (DSF #232).
    - LX1: Python plugins with dependencies ran without their AppArmor profile, because the profile attached by path and such a plugin is started through /bin/bash and the interpreter its venv links to (DSF #233).
    - LX5, partly: every confined plugin could create, replace and delete every file in /run/dsf; now only `dcs.sock` and, with RegisterHttpEndpoints, the endpoint sockets (DSF #233; the rest is below).
    - LX6: programs that run as root followed symlinks that the dsf user can put in /opt/dsf/conf or below /opt/dsf/sd: gen-https-cert.sh and the http.json write of DuetPiManagementPlugin (M586), and the maintainer scripts of reprapfirmware, duetwebserver and duetpimanagementplugin. So dsf could become the owner of any file, or have root write into it (DSF #235).
  - None of these PRs is merged yet.
- **DL2 (medium). A code that a Post or Executed interceptor sends on the channel of the code it intercepts never runs.**
  - Post: the interception runs inside `Code.ProcessInternally` (`DuetControlServer/Commands/Generic/Code.cs:280-289`) and blocks the ProcessInternally task of the stack item. The interceptor's new code enters at Pre (`Codes/CodeProcessor.cs:274-279`) and then queues behind the intercepted code in that task (`Codes/Pipelines/ProcessInternally.cs:28-30`).
  - Executed: the stage has one stack item (`Codes/Pipelines/Executed.cs:72`, `:244-253`), and the interception runs inside it (`:167`). The completion of the new code queues behind the intercepted code.
  - Effect: the channel and the interceptor wait for each other until the client disconnects, which wakes the waiter (`IPC/Processors/CodeInterception.cs:207-208`). I3 (#181) was the Flush variant of the Executed case; this is the Code variant.
  - Upstream code. Upstream report candidate.
  - Fix sketch: run the Post interception in the Post stage, which `Codes/Pipelines/Post.cs:24-40` implements, and send codes of a Post interceptor past ProcessInternally as `ProcessInternally.cs:43-44` does. For Executed, the new code must not finish behind the intercepted one; that is a design decision and a behaviour change.
  - Found from the code, not run.
- **DL3 (medium). An intercepted code is not released when it is cancelled.**
  - The wait for the client uses the `cancellationToken` parameter (`IPC/Processors/CodeInterception.cs:289`), which every caller leaves at its default (`Codes/Pipelines/Pre.cs:30`, `Post.cs:28`, `Commands/Generic/Code.cs:282`, `Executed.cs:167`). The code's own token is used only to enter the lock and the monitor (`:269`, `:271`).
  - Trigger: a connected interceptor that does not answer. An abort, emergency stop or invalidation cancels the code's token (`Codes/CodeProcessor.cs:321-333`), but the stage task stays blocked until the client disconnects or DCS stops.
  - Effect: the channel stays blocked after the abort or emergency stop that should free it. A case of the requirement in O8 (Tim, 2026-11-11) that a DCS that runs but cannot serve must run into a timeout.
  - Upstream code. Upstream report candidate.
  - Fix sketch: wait with `code.CancellationToken` at `:289`, as `:269` and `:271` do, and keep the entry check on the caller's token (`:338`). A late answer of the client must not apply to the next code (`_interceptionResult` is reset before each pulse, `:283`); that needs a test. A timeout for a silent client belongs to the O8 design.
  - Found from the code, not run.
- **DL6 (low to medium). A plugin service that stays connected but does not answer blocks every plugin command.**
  - `EnqueueCommandAsync` uses the caller's token only to enter the monitor (`IPC/Processors/PluginService.cs:107`); `return await tcs.Task` (`:128`) ignores it. The service loop waits for the answer of DPS with the connection's token only (`:253`, `:259`), and DPS handles one command at a time (`DuetPluginService/IPC/CommandService.cs:47-57`).
  - Trigger: DPS stopped (e.g. SIGSTOP) or stuck in a command.
  - Effect: StartPlugin (`Commands/Plugins/StartPlugin.cs:164`), StopPlugin (`StopPlugin.cs:69`) and the other plugin commands hang without an error, also after their caller cancels. A plugin that connects while another one starts waits for DPS before its init message (`IPC/Connection.cs:113-115` -> `:249-254`, no token). Related: I7, I13.
  - A case of the O8 requirement.
  - Upstream code. Upstream report candidate.
  - Fix sketch: `await tcs.Task.WaitAsync(cancellationToken)` in `EnqueueCommandAsync`. Whether DCS also gives up on a DPS that does not answer belongs to the O8 design.
  - Found from the code, not run.
- **DL7 (low to medium). StartPlugins waits without a bound for a plugin with `sbcNotifyStarted`, so dsf-config.g does not run while such a plugin runs without notifying.**
  - The loop waits while a plugin has `Pid > 0 && SbcNotifyStarted && !Started` (`Commands/Plugins/StartPlugins.cs:89-118`), under `_startLock` (`:54`). PluginService starts it without a token (`IPC/Processors/PluginService.cs:228`).
  - Effect: `state.pluginsStarted` stays false (`:123`) and dsf-config.g is not run (`:126-138`). A later StartPlugins waits for `_startLock`, and StopPlugins returns at once while `pluginsStarted` is false (`StopPlugins.cs:42`).
  - The manifest documents the flag only as "Plugin notifies DSF when it is fully started" (`DuetAPI/ObjectModel/Plugins/PluginManifest.cs:200`). The wiki has no entry for it (wiki-content 45fda89).
  - A case of the O8 requirement.
  - Upstream code. Upstream report candidate.
  - Fix sketch: bound the wait; when it expires, log the plugin that did not notify and run dsf-config.g.
  - Found from the code, not run.
- **ST2 (low to medium). Plugin data that DCS accepts can make the object model unreadable for DuetAPIClient and M409, a new cause of O8.**
  - `SetPluginData` stores any `JsonElement` for a registered key (`Commands/Plugins/SetPluginData.cs:50-54`), and so does the manifest's `data` (`DuetAPI/ObjectModel/Plugins/PluginManifest.cs:268`). It is reached through `PATCH /machine/plugin` (`DuetWebServer/Endpoints/MachineEndpoints.cs:615-641`) or over IPC by the plugin.
  - No JSON reader in DSF sets a depth (grep for `MaxDepth`), so the .NET default of 64 applies. A value nested N times sits at depth 4 + N in the model (root, plugins, plugin, data). Measured by the census with the fork's DuetAPI build: from N = 60 `GetObjectModelAsync` of DuetAPIClient fails (`DuetAPIClient/BaseConnection.cs:243`); from N = 61 the subscription of DuetAPIClient fails (`DuetAPIClient/ModelSubscription/SubscribeConnection.cs:221`, computed); from N = 62 M409 (`Commands/ObjectModel/QueryObjectModel.cs:29`, `:61`) and rr_model (`DuetWebServer/Endpoints/RepRapFirmwareEndpoints.cs:503`) fail.
  - Effect: rr_connect (`RepRapFirmwareEndpoints.cs:92`) fails, and so does `PATCH /machine/plugin` itself (`MachineEndpoints.cs:624`), so the value cannot be reset through the endpoint that set it. The ModelObserver of DWS retries for good (`DuetWebServer/Services/ModelObserver.cs:187-190`). The WebSocket of DWC keeps working, because the raw writer allows depth 1000 (measured by the census). The state lasts until the key changes or DCS restarts.
  - Upstream code. Upstream report candidate.
  - Fix sketch: refuse a value nested too deeply where it is produced, in `SetPluginData` and for the manifest at install, with a fixed limit such as 32. Raising the depth of the readers would have to cover every client.
  - Found from the code and the measurement above, not run end to end through DWS and DCS.
- **AS4 (low to medium). A failed exit report of DPS leaves DCS with the PID of a dead plugin.**
  - DPS reports the exit over a new connection inside a `Task.Run` with `try`/`finally` and no `catch` (`DuetPluginService/Commands/StartPlugin.cs:186-224`, connect and report at `:212-213`). An exception there is lost.
  - Trigger: the connect fails, e.g. when the backlog of 4 is full (`DuetControlServer/Settings.cs:116`, `IPC/Server.cs:67`; H2 shows EAGAIN for parallel connects). A plugin that crashes with a traceback causes such a burst through AS5.
  - Effect: DPS has removed the process (`DuetPluginService/Commands/StartPlugin.cs:201`), but DCS keeps the PID. There is no auto-restart (`Commands/Plugins/SetPluginProcess.cs:40`), a start is skipped (`Commands/Plugins/StartPlugin.cs:101`), and during the start of a plugin with `sbcNotifyStarted` StartPlugins waits for good (DL7).
  - Upstream code. Upstream report candidate. EAGAIN on this connection was not shown.
  - Fix sketch: report over one connection per plugin process, opened at start (see AS5), or retry the connect while DPS runs; at least catch and log.
- **RE1 (low). CodeStream writes the error reply without `_outputLock`.**
  - The normal reply is written under `_outputLock` (`IPC/Processors/CodeStream.cs:241-245`). The catch for `CodeParserException` and `ArgumentException` writes "Error:", "ok" and the flush without it (`:248-259`). With a buffer size above 1, several continuations run at once (`:145`, `:232`, `:272`).
  - Effect: two async writes on one `StreamWriter`. The exception that follows (from the census, .NET behaviour not checked here) is swallowed by the fire-and-forget continuation, so the "Error:" line and, in Marlin or NanoDLP mode, its "ok" are lost; a host that counts "ok" stalls. Related to I4 and I5.
  - Upstream code. Upstream report candidate. Fix sketch: take `_outputLock` around `:250-259`, as `:241` does.
- **DL8 (low). A hung `ip` or `iwgetid` stops the periodic SBC, network and volume updates for good.**
  - The gather is awaited with `stoppingToken` only (`Model/PeriodicUpdateService.cs:149-160`), and so are the two process waits (`:302`, `:354`).
  - Effect: `network`, `volumes` and `sbc` stay as they are, `CleanMessages` stops (`:170`), and the hostname, time and IP codes stop (`:172-232`). DCS otherwise keeps serving. A case of the O8 requirement.
  - Upstream code. Upstream report candidate. The trigger (e.g. `iwgetid` in a stuck wireless driver) was not shown.
  - Fix sketch: a timeout linked with `stoppingToken` on both process waits; on expiry, kill the process and take the existing failure paths.
- **SD2 (low). Patch subscriptions serialise live model objects outside the model lock.**
  - `SerializeJsonChanges(_patch)` runs under `lock(_patch)` only (`IPC/Processors/ModelSubscription.cs:273-282`); Full mode serialises under the read lock (`:286-289`). The patch holds live objects (`:486`, `:551`), and after an ack with a non-empty patch the subscriber does not wait for an update (`:248-257`).
  - Example: UpdateService adds a layer (`Model/UpdateService.cs:533`) and changes it in the same write lock (`:540-550`) while a subscriber serialises it. The enumeration of `Filament` throws, or the patch carries a half-updated layer. The WebSocket of DWC and the ModelObserver of DWS use Patch mode (`DuetWebServer/Endpoints/WebSocketEndpoint.cs:166`, `DuetWebServer/Services/ModelObserver.cs:95`).
  - Effect: the connection ends and the client reconnects, or a patch mixes old and new values.
  - Upstream code. Upstream report candidate. Not reproduced.
  - Fix sketch: take `AccessReadOnlyAsync` around that block, as Full mode does. The order model lock, then `_patch`, is the recorder's order (`:468`).
- **SD3 (low). `Code.ExecuteAsync` reads the channel's cancellation source without the lock under which `CancelPending` disposes it.**
  - `Commands/Generic/Code.cs:129` reads it unlocked; `ResetCancellationToken` reads it under the lock (`:105-111`). `CancelPending` cancels, disposes and replaces it under the lock (`Codes/CodeProcessor.cs:321-333`), called by the link thread on invalidation (`Link/Channel/Processor.cs:1859`).
  - Effect: in a window of a few instructions, `.Token` of the disposed source throws ObjectDisposedException, so an IPC code or a code of a handler fails with it instead of being cancelled. Job codes bring their own token (`Files/JobProcessor.cs:499`).
  - Upstream code. Upstream report candidate. Fix sketch: read it through `ResetCancellationToken`.
- **SD4 (low). `PipelineBase.CurrentStackItem` peeks the stack without `lock(_stack)`.**
  - `Codes/Pipelines/PipelineBase.cs:78`, used by `FlushAsync(bool)` without the lock (`:166`); the other readers lock (`:155`, `:176`, `:197`). The link thread pops a macro level under the lock (`:266-275`, from `Link/Channel/Processor.cs:367`, `:449`).
  - Trigger: the IPC Flush command of a client that does not intercept (`Commands/Generic/Flush.cs:37` -> `Codes/CodeProcessor.cs:135`) while a macro level ends. `Stack<T>.Pop` clears the slot (checked by the census), so the peek can return null.
  - Effect: the Flush command fails with NullReferenceException. No state is damaged.
  - Upstream code. Upstream report candidate. Fix sketch: take `lock(_stack)` there, as `IsIdle` does.
- **SD5 (low). DWS changes the origins of the shared CORS policy while request threads enumerate them.**
  - The policy is static (`DuetWebServer/Services/ModelObserver.cs:33-36`) and the default policy (`DuetWebServer/Program.cs:74`). The observer task changes its origins without a lock (`:111`, `:156-160`). `CheckWebSocketOrigin` enumerates them (`:66`) for WebSocket and custom endpoint requests (`DuetWebServer/Endpoints/WebSocketEndpoint.cs:71`, `DuetWebServer/Middleware/CustomEndpointMiddleware.cs:87`).
  - Trigger: `M586 C"<site>"` (`DuetControlServer/Codes/Handlers/MCodeHandler.cs:1116-1120`) while a cross-origin request is checked. The enumeration throws, and the request fails with an unhandled exception. Whether the CORS middleware reads the list as well was not traced.
  - Upstream code. Upstream report candidate. Fix sketch: build a new policy on a change and publish it with one reference write.
- **SD7 (low). `CodeFile` reads its block stack without the lock under which the rewind of M26 changes it.**
  - The reads come after the file lock is released (`Files/CodeFile.cs:388`, `:470`, `:551`, `:562`; lock scope `:363-384`). `RewindAsync` pops and pushes under `lock(_codeBlocks)` (`:165-208`).
  - Trigger: `M26 S<pos>` from another channel while the job runs, at the moment the job reads a `while`, `break` or `continue`. M26 checks only that a file is selected (`Codes/Handlers/MCodeHandler.cs:339`).
  - Effect: InvalidOperationException in `ReadCodeAsync`; the job is aborted after the codes before it (`Files/JobProcessor.cs:511-530`).
  - Upstream code. Upstream report candidate. Not reproduced.
  - Fix sketch: refuse or defer M26 while the job is not paused (a behaviour change), or take the lock for the reads. Which one upstream intends is open.
- **AS5 (low). DPS opens one IPC connection per line of plugin output.** An `async void` handler connects, writes and disposes for every stdout or stderr line (`DuetPluginService/Commands/StartPlugin.cs:248-269`), so a burst runs its connects in parallel against the backlog of 4. Lines are lost with one generic warning or arrive out of order, and other clients that connect meanwhile can fail (H2, AS4). Upstream code. Upstream report candidate. Fix sketch: one connection per plugin process with a single writer.
- **AS6 (low). DWS stops following the object model when DCS answers a command with a cancellation.**
  - The inner catch of `ModelObserver` retries on anything but an OperationCanceledException (`DuetWebServer/Services/ModelObserver.cs:187`); the outer catch ends `ExecuteAsync` on one (`:195`). DCS sends a cancelled command back as an error (`DuetControlServer/IPC/Processors/Command.cs:132-153`), and DuetAPIClient throws TaskCanceledException for it (`DuetAPIClient/BaseConnection.cs:192-194`).
  - Trigger: DCS stops between the connect of DWS and the answer to `ResolvePathAsync` (`ModelObserver.cs:129`).
  - Effect: DWS keeps running without the observer. Plugin endpoints registered later are not added, CORS and web directory changes are not taken over, and messages are not cached for rr_reply (`:134-137`).
  - Upstream code. Upstream report candidate. Fix sketch: end only on the stop of DWS, `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)`.
- **AS8 (low). `PATCH /machine/plugin` reads a body of any size into memory.** It lifts the size limit (`DuetWebServer/Endpoints/MachineEndpoints.cs:618`) and deserialises the body (`:621`); the other endpoints that lift it stream to a file (`:251`, `:534`, `:723`, `DuetWebServer/Endpoints/RepRapFirmwareEndpoints.cs:241`). A ReadWrite session can make DWS grow until it fails or the OOM killer acts. Upstream code. Upstream report candidate. Fix sketch: keep Kestrel's limit there.
- **AS9 (low). A code stream whose client stops reading collects one waiting task per firmware message.** Each message for the channel starts a task that waits for `_outputLock` (`IPC/Processors/CodeStream.cs:750-763`, `:770-788`, from `Link/LinkService.cs:926`), and the holder blocks once the socket buffer is full. The tasks grow until the client disconnects. Needs a local client in group dsf. Upstream code. Upstream report candidate. Fix sketch: one writer task per stream reading a bounded channel.
- **AS10 (low). DuetPiManagementPlugin aborts with an unhandled exception when it stops during a kiosk close request.** `OnRequestReceived` is `async void` and its catch excludes OperationCanceledException (`DuetPiManagementPlugin/Browser.cs:70-95`), and `Program.CancellationToken` is cancelled on exit (`DuetPiManagementPlugin/Program.cs:78`, `:184-196`). An OCE that escapes `async void` ends the process with rc 134 (measured by the census). Cosmetic: the plugin was stopping anyway. Upstream code. Upstream report candidate. Fix sketch: a plain `catch (OperationCanceledException)`.
- **UB4 (low). The plugin preflight that protects config.g, config-override.g and `firmware/` compares entry names as strings.**
  - DCS refuses only the exact names and `StartsWith("sd/firmware/")` (`DuetControlServer/Commands/Plugins/InstallPlugin.cs:47-56`). DPS writes to `Path.Combine(sdPath, name[3..])` (`DuetPluginService/Commands/InstallPlugin.cs:183`), and its containment check normalises (`:200`, `DuetSharedLibrary/PathExtensions.cs:46-60`).
  - Input: a plugin ZIP with `sd/sys/./config.g`, `sd/sys//config.g` or `sd/./firmware/<file>`. The install overwrites that file (`DuetPluginService/Commands/InstallPlugin.cs:226-227`), and the uninstall deletes it (`DuetPluginService/Commands/UninstallPlugin.cs:134-148`).
  - Low, because a plugin's DWC code can already write config.g through the HTTP API in the user's session (from the census).
  - Upstream code. Upstream report candidate. Fix sketch: compare the normalised path relative to the SD directory, or refuse an entry whose name differs from its normalised form.
- **UB6 (low). UF2 unpacking tests the "not main flash" flag by value and ignores the target addresses.** `Utility/Firmware.cs:79` includes a block unless its flags equal `FlagNoFlash` (`:33`), so a block flagged 0x2001 goes into the image, and `TargetAddr` (`:20`) is never read, so a gap or blocks out of order shift the image. RRF's `Tools/uf2conv/uf2conv.py` tests the bit (`:53`), pads gaps and refuses blocks out of order (`:59-76`). M997 S0 (`Codes/Handlers/MCodeHandler.cs:1327`) then flashes a wrong image. That the UF2 files of RRF are contiguous and use 0x2000 is from the census, not re-checked. Upstream code. Upstream report candidate. Fix sketch: test the bit and refuse addresses that are not contiguous and ascending, as #197 refuses invalid files.
- **UB7 (low, fork). A bad line in the SPI fault-injection control file stops DCS.** Only in builds with `SPI_FAULT_INJECTION` (`Link/Adapter/SPI.cs:113-132`). `SpiFaultSpec.Parse` applies the mutation once (`Link/Adapter/SpiFaultInjection.cs:155`); a negative bit passes the check of `flip` (`:175-178`), `Field` takes negative or overflowing bounds (`:213`), and `long.Parse` overflows (`:215-216`). `CheckControlFile` catches only IOException, UnauthorizedAccessException and FormatException (`:411`), on the link thread (`:279`). Fork code (34a710caf). Fix sketch: `TryParse`, refuse negative values, and bound the field with `Math.Min(size, data.Length - offset)`.
- **UB8 (low). The IPC JSON receive has no size limit.** `ReceiveUtf8JsonAsync` appends until the braces balance (`DuetAPI/Utility/JsonHelper.cs:187-264`), into one stream per connection (`DuetControlServer/IPC/Connection.cs:358`, `:367-368`). Any process of user or group dsf can connect (`UMask=0002`, `pkg/common/duetcontrolserver/usr/lib/systemd/system/duetcontrolserver.service:12-14`; no chmod at `IPC/Server.cs:65-67`) and make DCS grow until the stream fails at 2 GB or the OOM killer acts. Upstream code. Upstream report candidate. Fix sketch: a maximum length for `ReceiveUtf8JsonAsync`; the connection closes as on any protocol error.
- **UB9 (low). The capacity of the thumbnail buffer comes from the file.** `ImageParser` parses the length of `; thumbnail begin WxH N` (`Files/Parser/ImageProcessing/ImageParser.cs:58`) and presizes a `StringBuilder` with it when the content is read (`:70`), which the file selection (`Files/JobProcessor.cs:311`) and GetFileInfo (`Commands/Files/GetFileInfo.cs:19`) request. N = 1000000000 asks for a 2 GB array; an OutOfMemoryException is caught and the thumbnail skipped (`Files/Parser/FileInfoParser.cs:720`). Upstream code. Upstream report candidate. Fix sketch: do not presize, or cap it by the rest of the file.
- **UB10 (low). WebSocket messages to plugin endpoints are split at the receive buffer.** Each `ReceiveAsync` result becomes its own body (`DuetWebServer/Middleware/CustomEndpointMiddleware.cs:148-175`, `:171`) without a check of `EndOfMessage`; the buffer has 8192 bytes (`DuetWebServer/Settings.cs:66`). A longer message reaches the plugin as two bodies, and a multi-byte character at the split becomes U+FFFD. Upstream code. Upstream report candidate. Fix sketch: collect until `EndOfMessage`, with a size limit, then decode once.
- **The name of a job file is cut by RRF after 120 characters (low).**
  - RRF keeps the name from SetPrintFileInfo in a `String<MaxFilenameLength>` (RRF `SbcInterface.cpp:536-538`), as M23 and M32 do in standalone mode (`GCodes2.cpp:1168-1169`). `MaxFilenameLength` is 120, "including the path" (`Config/Configuration.h:143`). `ReadPrintStartedInfo` copies the name with `StringRef::copy` (`DataTransfer.cpp:685`), which cuts it to the capacity (RRFLibraries `StringRef.cpp:95-103` at 869f6bd). The wiki documents 120 characters for full paths (`Gcodes.md:266`, `:1824`, wiki-content 45fda89).
  - DSF accepts and sends names up to 254 bytes (`Link/Protocol/Writer.cs:495-498`; #219 refuses longer ones at the selection). For a name of 121 to 254 bytes, RRF's `job.file.fileName` is cut.
  - Whether the cut name then overwrites DSF's model through UpdateService was not checked at run time. Whether DSF should refuse names over 120 is open for Tim.
  - Upstream code on both sides. Upstream report candidate.
- **The age of a message is measured with the wall clock (low, time base).** `CleanMessages` compares `DateTime.Now` with `Message.Time` (`Model/PeriodicUpdateService.cs:635`), which is `DateTime.Now` at creation (`DuetAPI/ObjectModel/Messages/Message.cs:37`). When the system time changes, messages are dropped early or kept too long. Not part of #215. `Message.Time` is also shown, so a fix needs a monotonic stamp besides it. Upstream code. Upstream report candidate.
- **The SPI and USB transfer rates of the diagnostics divide by a wall-clock difference (low, time base).** `Link/Adapter/SPI.cs:220-221`, `:237-238` and `USB.cs:197-198`, `:214-215`, printed at `SPI.cs:285-286` and `USB.cs:243-244`. A change of the system time between two reports gives a wrong or negative rate. Diagnostics only. Upstream code. Upstream report candidate.
- **Theoretical, no trigger shown (upstream code unless marked fork):**
  - A: the model read lock is held while a priority code is moved to another channel (`Codes/CodeProcessor.cs:225-241`, write at `:237`); it blocks only if the target fills between `IsIdle` and the write.
  - A: CodeStream writes "ok" under the model read lock (`IPC/Processors/CodeStream.cs:251-256`, `:303-308`); it reaches the socket only when the writer's buffer fills (no AutoFlush, `:110`) and the client stops reading.
  - A: `CloseConnection` of the DWC WebSocket runs outside `sendLock` (`DuetWebServer/Endpoints/WebSocketEndpoint.cs:233-246`, `:291`, `:297`) and can overlap a send of `WriteToClient` (`:217-219`); whether .NET allows that was not checked. H6 and H7 are the known close-path findings.
  - A: an IPC client that connects and never sends its init message keeps its task until DCS stops (`IPC/Server.cs:155-161`); only that connection is affected.
  - T1 (fork, 4f0744501): `ReportOrChangeConditionAsync` evaluates outside `lock(_triggers)` (`Model/SbcTriggerService.cs:187`) and then writes the state it read before (`:189-191`), which a concurrent M581.1 may have replaced.
  - T2: `_service = this` (`IPC/Processors/PluginService.cs:211`) comes before `GetPeerCredentials` (`:212`). If that throws, the disposed processor (`:173`) stays registered, and the next registration cancels its disposed source in `TerminateIfGone` (`:375`). No case was found in which it throws.
  - T3: the SPI and USB diagnostic counters are written by the link thread and read and reset by the diagnostics without a lock (`Link/Adapter/SPI.cs:70-71`, `:213-251`, `USB.cs:42-43`); lost counts in the diagnostics only.
  - T4 and AS11: `PrintDiagnosticsAsync` lists the buffered codes and the stack without the channel lock when its 2 s lock wait expires (`Link/Channel/Processor.cs:622-683`, `:631`), and the lock it took is not released if the listing throws (`:683`, not in a `finally`).
  - T5: `BytesReserved` and `BufferSpace` (`Link/LinkInterface.cs:38`) are written by the link thread (`:600-601`) and by `InvalidateCodesAsync` at shutdown (`:764`).
  - AS7: the IPC accept loops end for good on any SocketException (`IPC/Server.cs:117`, `DuetAPIClient/HttpEndpoints/HttpEndpointUnixSocket.cs:134-138`); `ExecuteAsync` then returns normally, so DCS runs without new IPC connections. EMFILE needs more descriptors than the limit the census measured (1048576); other errors were not provoked.
  - UB11: `NumPackets` is a byte (`Link/Adapter/SPI.cs:318`, `:791`, `USB.cs:308`, `:691`), and `CanWritePacket` checks bytes only (`SPI.cs:1745-1748`). RRF's code buffer of 4096 bytes (RRF `SbcMessageFormats.h:47`) keeps a transfer far below 255 packets.
  - UB12: `ResendPacket` scans to the end of the buffer, not to the length of that transfer (`SPI.cs:825-861`, `USB.cs:725-762`); only a resend request for an Id that was not sent reaches stale bytes, and it ends in an exception (`SPI.cs:860`).
  - UB13: a FileReadResult that does not fit is dropped without an answer after the file position has moved (`Link/LinkService.cs:1534-1540`, `SPI.cs:1604-1608`, `LinkService.cs:1696-1702`). RRF caps a request at the buffer size (RRF `DataTransfer.cpp:2211`); that no RRF caller reads more than about 8000 bytes in SBC mode is from the census.
  - UB14: the header is taken over (`SPI.cs:1974`, fork 061f5a94d1) before its length is checked (`:2005-2008`); a length that is too large throws a plain exception that stops DCS. Upstream checks in the same order. Only a wrong header that passes its CRC reaches it.
  - UB15: `SendMessage` checks characters, not bytes (`Link/LinkInterface.cs:543`). The Executed stage splits replies by bytes (`Codes/Pipelines/Executed.cs:186-207`), so it throws only if `MaxMessageLength` is set far above its default.
  - UB16: `MissedEdges` adds `uint` differences to an `int` (`DuetSharedLibrary/InputGpioPin.cs:248`); it needs more than 2^31 missed edges.

Findings of the second round that are not fixed:

- **RL1 (medium). A layer comment with a large number makes DCS create one placeholder layer per skipped layer under the model write lock.**
  - DSF passes whole-line comments with "layer" to RRF (`Settings.cs:266-278`, `Commands/Generic/Code.cs:198-199`). RRF takes any layer number of 0 or more (RRF `GCodes3.cpp:1322-1330`) and reports it as `job.layer` (`PrintMonitor.cpp:79`).
  - `UpdateLayers` runs under the model write lock (`Model/UpdateService.cs:289-297`) and adds one `Layer` per skipped layer (`:491-513`). Each Add allocates and scans an array of the whole list in the observer (`Model/Observer/ModelCollectionObserver.cs:33`, `:96`), which is attached with or without subscribers (`Model/Observer/Observer.cs:44-46`), so the cost is O(N²).
  - Input: one line such as `;LAYER:100000` in a crafted, corrupted or post-processed job file. Slicer output that counts up by one is not affected.
  - Measured by the census with the handler's shape: N = 100,000 took 6.6 s and allocated 4.8 GiB, N = 300,000 took 51.6 s and 43 GiB. `;LAYER:2000000000` cannot finish and ends out of memory; systemd then restarts DCS mid-print (`Restart=always`).
  - Meanwhile the link thread stops at the next generic firmware message, which it writes to the model under the same lock (`Link/LinkService.cs:918`, `:952` -> `Model/ObjectModel.cs:848`, `:784`, `:803`), and all model readers wait. How RRF reacts to the stalled link during a print was not traced.
  - Upstream code. Upstream report candidate.
  - Fix sketch: `UpdateLayers` adds at most one entry for a jump of the layer number. Whether `job.layers` is bounded that way, which changes what the model shows, is a design decision for Tim.
  - Found from the code and the measurement above, not run in DCS.
- **RL2 (medium). A job file keeps the block-local variables of every closed block as hidden variables in RRF until the print ends.**
  - For job files, `EndCodeBlockAsync` keeps each closed block (`Files/CodeFile.cs:764-785`; jobs are rewindable, `:106`, macros not, `Files/MacroFile.cs:52`) and renames its variables in RRF to `var.dsfClosedBlock{FilePosition}x{i}` (`:657`, `:666-677`). They are deleted only on a rewind before the block, a reopen or a new close of the same block (`:190-199`, `:204-208`, `:774-782`), or when RRF ends the print (RRF `GCodes.cpp:5092-5094` -> `GCodeMachineState.cpp:390-398`).
  - Input: many distinct blocks that declare `var`, e.g. layer-change G-code of the slicer with `if ... { var ... }`. Loops do not grow.
  - RRF keeps each in its heap (RRF `Variable.cpp:250-256`, `Heap.cpp:19-20`, `:336-342`), and an exhausted heap resets RRF (CoreN2G `syscalls.h:50-62`, RRF `ExceptionHandlers.cpp:90-92`). The census estimates 60 to 72 bytes per numeric variable; with the 95,868 bytes that the lab MB6HC never used (M122 of 2026-11-09), RRF runs out after about 1,300 to 1,500 such blocks per print. Estimate, not measured.
  - Effect: an out-of-memory reset of RRF in the middle of a long print. In DCS, `_closedBlocks.RemoveAll` scans the whole list at each close (`:773-782`), O(N²).
  - Upstream code, new with d108e9767 (merged by #212) on top of 21bee5dc1c. Upstream report candidate: the bound belongs to upstream's design, so report it to Chris with these numbers instead of a fork fix.
  - Fix sketch (census): keep a closed block and its hidden variables only while a pause can still rewind into it, that is until RRF has executed past its end position.
  - Found from the code of DSF and RRF, not run.
- **TP2 (medium). The dedicated link thread waits for the thread pool, so a pool starvation of 4 s or more ends the SBC link, and the fork's RRF then performs an emergency stop.**
  - Hand-off: `Manager.Spin` takes every channel lock on every iteration (`Link/Channel/Manager.cs:81`), and pool tasks queue on the same locks for every code sent (`Link/LinkInterface.cs:302`). A release passes the lock to the queued waiter (`DuetSharedLibrary/AsyncLock.cs:48-55`), so the next `Lock()` of the link thread waits until the pool has run that waiter. The same holds for the Nito lock of the job processor (`Link/LinkService.cs:1038`, `:1732`).
  - Cancelable waits: `Lock(ct)` waits through `WaitAsync(ct).GetAwaiter().GetResult()` (`AsyncLock.cs:48-55`, on purpose per the comment at `:44-47`), and its wake-up needs a pool thread. The link thread calls it for `FirmwareActionLock`, `FirmwareUpdateLock` and `PrintStateLock` on every iteration (`LinkService.cs:325`, `:467`, `:353`, `:403`) and for the macro and event logger locks (`Link/Channel/Processor.cs:388`, `:850`, `Utility/Logger.cs:224`). The `File.Lock` of `Commands/Generic/Code.cs:166` was TP3.
  - Measured by the census on a copy of the lock with a pool minimum of 4: with 8 blocked pool items the link thread waited 4.9 s (hand-off) or 4.7 s (wake-up), more than RRF's 4000 ms (RRF `SbcMessageFormats.h:45`). RRF then reports "Lost connection to SBC (timeout)", and the fork's RRF performs an emergency stop (RRF `SbcInterface.cpp:216-230`, fork 13fa7c2dd). With 4 blocked items the link pauses for about 0.9 s.
  - Trigger: a storage stall (SD card retry, USB stick reset). On Linux each `FileStream` async read blocks a pool thread (measured by the census): the job reader, macro readers, file info, `fileread()`, event log writes and uploads. Not shown in a DCS process. The effect of SCHED_FIFO (`duetcontrolserver.service:15`) on the pool was not measured. No code sets `SetMinThreads`.
  - Upstream code (the hand-off by design, the cancelable wait since bb5edd5ef); the emergency stop is the fork's RRF. Upstream report candidate.
  - Fix sketch: the link thread passes no token to its synchronous `Lock` calls, which restores the wake-up of the Nito locks before bb5edd5ef, and `Spin` tries each channel lock with `Wait(0)` and skips a busy one, as it skips channels with `skipChannels` (`LinkService.cs:324`, `:348`, `:485`). Question for Chris: may `AsyncLock.Lock(ct)` use `SemaphoreSlim.Wait(ct)` instead, and does that keep the order that `UnitTests/Utility/AsyncLock.cs` (`WaitersAreServedInOrder`) checks? A higher minimum of pool threads only moves the threshold.
- **FS1, the part that #226 does not cover (medium). `echo >` and M28/M29 write files in place without a sync.**
  - `Codes/Handlers/KeywordHandler.cs:149`, `Codes/Handlers/MCodeHandler.cs:412`, `:436-440`. Standalone RRF closes an `echo` file with `f_close`, which syncs it (RRF `StringParser.cpp:912-922`); for M28/M29 the census states only that RRF writes in place too.
  - Effect: a state file written with `echo >` or a file stored with M28/M29 keeps its old content, or is empty, after a power cut within the ext4 commit interval (default 5 s, `ext4(5)`). Write errors already reach the code as errors. The on-disk outcome rests on the ext4 defaults, not on a measurement.
  - Upstream code. Upstream report candidate. Fix sketch: `Flush(true)` before the close, as #226 does for the files RRF writes.
- **TP1 (medium). DWS connects synchronously to the HTTP endpoint socket of a plugin, so a plugin that does not accept pins one pool thread per request, without a timeout.**
  - `DuetWebServer/Middleware/CustomEndpointMiddleware.cs:66` calls the blocking `Connect` (`DuetWebServer/Middleware/HttpEndpointConnection.cs:28-32`). The plugin listens with a backlog of 4 (`DuetAPIClient/HttpEndpoints/HttpEndpointUnixSocket.cs:18`, `:72`).
  - Trigger: a plugin with an HTTP endpoint that hangs, is stopped, or blocks in a synchronous handler, which runs inline in its accept loop (`HttpEndpointUnixSocket.cs:125-160`), while DWC or a plugin page polls the endpoint.
  - Effect: each request beyond the backlog blocks a pool thread in `connect(2)`. Each newly blocked thread delays all other DWS work by about 0.5 to 2 s until the pool adds a thread, including the WebSocket updates and the code request that carries M112 from DWC. Measured by the census: with 12 requests, 7 threads were still blocked after 11.4 s, and an unrelated work item waited 1.6 to 2.1 s.
  - The requests that reach the plugin or its backlog now end when the client aborts (DSF #229, which passes `context.RequestAborted` to the send and receive at `:279-280`).
  - `ConnectAsync` with `context.RequestAborted` is not a fix: a non-blocking `connect(2)` to a UNIX socket with a full backlog fails at once with EAGAIN (connect(2)). Measured through DWS with a healthy plugin (backlog 4, 20 rounds, .NET 10.0.12 on x86_64 WSL): 1 to 4 of 120 requests failed at 6 in parallel and 45 to 95 of 240 at 12, against none with the blocking connect. Same class as H2. Linux gives no event for free backlog space, so a waiting async connect would have to poll. Part of the O8 design, together with whether DWS gives up on a plugin that stays silent.
  - Upstream code (679d87047). Upstream report candidate.
- **CU6 (low to medium). DPS, DuetPiManagementPlugin and the tools need the system ICU, but no package depends on it.**
  - DCS and DWS run in invariant mode (`DuetControlServer/DuetControlServer.csproj:14`, `DuetWebServer/DuetWebServer.csproj:16`). DPS (`DuetPluginService/DuetPluginService.csproj:2-10`), DuetPiManagementPlugin and the tools such as CodeConsole and PluginManager do not set it. The apps are self-contained (`pkg/common/common.functions:105`), and neither `duetruntime` (`pkg/deb/duetruntime/DEBIAN/control:6`) nor the other packages depend on libicu.
  - Effect: on a base system without ICU, DPS and the tools cannot start, so there are no plugins. The CHX image installs libicu itself; DuetPi has ICU from its base system. That the tools abort at startup without ICU was not measured.
  - Upstream code. Upstream report candidate.
  - Fix sketch: set `System.Globalization.Invariant` in these projects, as DCS and DWS do; that also fixes CU5. Behaviour change: their culture-specific output becomes invariant. The alternative is libicu in `Depends:` of duetruntime.
- **CU8 (low). `NumberStyles.Any` accepts a trailing sign, parentheses, exponents and thousands separators where RRF does not.**
  - G-code parameters (`DuetAPI/Commands/Code/Parameter.cs:138`, `:148`, `:153`): the tokenizer lets only the trailing sign through (`DuetAPI/Commands/Code/Parser.cs:14`, `:396-403`), so `G1 Z0.2-` gives Z = -0.2 and `M104 S200-` gives -200 (measured by the census). In SBC mode RRF gets that value as a binary parameter; standalone RRF ignores the trailing sign (RRF `StringParser.cpp:2050-2064`) and moves to +0.2. Needs a typo in a macro, not slicer output; the move stays within the axis limits unless M564 S0.
  - Arguments on the fallback path of expressions (`Codes/Meta/Expressions.cs:831`, `:837`, `:899-903`), which every expression with `fileread` or `fileexists` takes (`:1136-1142`): `(3)` gives -3, `5-` gives -5 and `2.0` gives the integer 2 (measured). `fileread(…, 0, (3), ',')` then fails with "fileread requires a positive number of elements" (`Codes/Meta/Functions.cs:96-99`).
  - Upstream code. Upstream report candidate.
  - Fix sketch: the styles RRF accepts (`AllowLeadingSign` with white space for integers, `NumberStyles.Float` for floats) in both places, and the `!value.Contains('.')` guard of `Parameter.cs:138` in `attemptToEvaluate`. Whether `G1 Z0.2-` gives 0.2 or is refused should follow upstream.
- **RL3 (low, medium with a chatty message source). DWS caches every message for each IP session that never calls rr_reply, without limit.**
  - DWS passes every model message to `SessionStorage.CacheMessage` (`DuetWebServer/Services/ModelObserver.cs:133-138`, `DuetWebServer/Singletons/SessionStorage.cs:434-442`), which appends it for every session with an IP address (`:132-140`). Only rr_reply clears it (`:143-150`, `DuetWebServer/Endpoints/RepRapFirmwareEndpoints.cs:203`).
  - IP sessions come from rr_connect (`RepRapFirmwareEndpoints.cs:88-89`) and, without a password, from any request without `X-Session-Key` (`DuetWebServer/Authorization/SessionKeyAuthenticationHandler.cs:56-77`). Each request refreshes the session (`SessionStorage.cs:283-289`), so a client that keeps polling never expires (`:385-403`).
  - Input: a monitoring or home-automation client that polls `rr_model` or `/machine/*` and never calls rr_reply. About 230 KB per day at one message of 80 characters per minute, about 14 MB per day with an `echo` per second (census estimate).
  - RRF drops unread replies after the session timeout (RRF `Networking/HttpResponder.cpp:1600-1645`).
  - Upstream code. Upstream report candidate. Fix sketch: drop cached messages older than the session timeout in `MaintainSessions`, as RRF does.
- **RL4 (low). A patch subscriber that stops acknowledging collects every new message in its pending patch.** After a patch, `ModelSubscription` waits for the acknowledgement without a timeout (`IPC/Processors/ModelSubscription.cs:233-243`) and clears `_patch` only after it (`:271-282`). Meanwhile every message is appended to `_patch["messages"]` (`:560-603`, `Link/LinkService.cs:927`); other keys are overwritten in place. Trigger: a plugin with a patch subscription that is stopped (SIGSTOP) or blocked in its handler; DuetAPIClient acknowledges only when the client asks for the next patch (`DuetAPIClient/ModelSubscription/SubscribeConnection.cs:254-257`, `:296-298`). About 20 MB per day in DCS per stuck subscriber at one message of 100 characters per second. Same shape as AS9. Upstream code. Upstream report candidate. Fix sketch: keep only the newest messages in a pending patch, as the model ages its own (`Model/PeriodicUpdateService.cs:631-640`).
- **RL5 (low). A lost or late OpenFileResult leaves the file open in DCS until the link is reset.** `HandleOpenFile` registers the stream in `_openFiles` (`Link/LinkService.cs:1500-1505`) before it writes the result (`:1508`), and a failed write is ignored (`:1696-1702`). RRF gives up after its timeout and ignores a late result (RRF `SbcInterface.cpp:2493-2498`, `:1296-1310`), so nothing closes the handle until `Invalidate` (`:1843-1847`) or the stop of DCS. A failing `SetLength` (`:1494-1497`) leaves the stream to the finalizer. Trigger: K9 or K22, whose rate is unknown. Effect: one open stream and descriptor per case, which keeps the space of a deleted file and blocks unmounting a USB stick; the descriptor limit is not the issue, because .NET raises the soft limit to the hard one (census: 1048576). Upstream code. Upstream report candidate. Fix sketch: write the result first and register the handle only if it was written, else dispose it; dispose it also when `SetLength` fails.
- **RL6 (low). Every DWS restart leaves its HTTP sessions in `sbc.dsf.userSessions` until DCS restarts.** DCS removes a session only on `RemoveUserSession` (`Commands/UserSessions/AddUserSession.cs:24-33`, `Commands/UserSessions/RemoveUserSession.cs:21-25`). DWS keeps its sessions in memory (`DuetWebServer/Singletons/SessionStorage.cs:158`) and removes them in DCS on logout or expiry (`:415-428`, `DuetWebServer/Endpoints/MachineEndpoints.cs:121`, `DuetWebServer/Endpoints/RepRapFirmwareEndpoints.cs:151`), but not when it stops, and it does not clean up at start (`DuetWebServer/Services/ModelObserver.cs:83-199`). DuetPiManagementPlugin restarts DWS on HTTP changes (`DuetPiManagementPlugin/Network/Protocols/HTTP.cs:127-128`, `:166-172`). Effect: the model lists users who are gone. The restart variant of H2. Upstream code. Upstream report candidate. Fix sketch: at its first model connection, DWS removes the HTTP sessions in DCS that it does not know.
- **FS6 (low). Two overlapping PUT uploads of the same name write into one .part file.** PUT opens the .part file with the default `FileShare.Read` (`DuetWebServer/Endpoints/MachineEndpoints.cs:269-279`), which on Linux does not stop a second writer (measured by the census); M559/M560 use `FileShare.None` for this (`IPC/Processors/CodeStream.cs:654-659`). With two tabs or a client retry, the first upload renames the shared file and answers 201 while the second keeps writing into it and then fails at the move. Measured: the target held parts of both bodies. Upstream code. Upstream report candidate. Fix sketch: `FileShare.None` on the .part file, as CodeStream does.
- **FS7 (low). plugins.txt is rewritten in place on every plugin start, auto-restarts included, without a sync, and concurrent starts fail after the plugin has started.**
  - Writers: `Commands/Plugins/StartPlugin.cs:63-76` (`FileShare.None`), `Commands/Plugins/StopPlugin.cs:72-87`, `Commands/Plugins/UninstallPlugin.cs:74-86`. `SaveState` defaults to true (`DuetAPI/Commands/Plugins/StartPlugin.cs:19`), and the auto-restart sends a plain StartPlugin (`Commands/Plugins/SetPluginProcess.cs:52`), so every auto-restart (after 2000 ms, `Settings.cs:83`) rewrites the file.
  - A power cut within the ext4 commit interval after a start or stop leaves the previous or an empty list, so the plugins come up in the old state at the next boot (`Commands/Plugins/StartPlugins.cs:66-87`). From `ext4(5)`, not measured.
  - Two starts at once: the second open throws (measured by the census) after its plugin was started, so the caller gets an error or the auto-restart logs "Failed to auto-restart plugin" for a running plugin, and the first writer's snapshot can miss it.
  - Upstream code. Upstream report candidate. Fix sketch: `SaveState = false` in the auto-restart, and write the list to a temporary file, `Flush(true)` and rename, under one lock instead of `FileShare.None`.
- **FS8 (low). DuetPiManagementPlugin rewrites system configuration in place without a sync.** In `DuetPiManagementPlugin/`: `SetLength` and an overwrite for sshd_config and proftpd.conf (`Network/Protocols/SSH.cs:150-236`, `Network/Protocols/FTP.cs:69-105`) and, without NetworkManager, for dhcpcd.conf and wpa_supplicant.conf (`Network/DHCP.cs:215-310`, `Network/WpaSupplicant.cs:211-301`); `FileMode.Create` for http.json, /etc/hosts, hostapd and dnsmasq (`Network/Protocols/HTTP.cs:151`, `Program.cs:360`, `Network/AccessPoint.cs:141`, `:159`). The overwrite is not a pattern that ext4's `auto_da_alloc` protects (`ext4(5)`). A user who switches off right after a network change can be left with a truncated or mixed sshd_config or dhcpcd.conf, which leaves a headless printer without SSH or network, or with an empty http.json, with which DWS cannot start. Not measured. Upstream code. Upstream report candidate. Fix sketch: one helper that writes `<file>.new`, `Flush(true)` and renames it, keeping owner and mode.
- **LX3 (low). The WiFi scan of DuetPiManagementPlugin hangs for good when `iw dev wlan0 scan` prints more than the pipe holds.** `WifiScan` waits for the exit (`DuetPiManagementPlugin/Network/WifiScan.cs:85`) before it reads stdout (`:93`), so `iw` blocks on a full pipe: 64 KiB with 4 KiB pages (measured by the census), 256 KiB with the 16 KiB pages of the Pi 5 kernel (not measured). `_scanning` stays set (`:37-48`), so every later M587.1 answers "scan is still in progress" and M587.2 F1 answers `{"err":1}` until the plugin restarts. Printing is not affected. Whether a busy site produces that much output was not measured. Upstream code (7b73dea01). Upstream report candidate. Fix sketch: start reading before `WaitForExitAsync`; `DuetPiManagementPlugin/Command.cs:22-43` has the same order, so far only with small outputs.
- **CU2 (low). Non-ASCII text in RRF messages is garbled when RRF splits a message inside a UTF-8 sequence.** Each Message packet is decoded on its own (`Link/Protocol/Reader.cs:89-105`) and the parts are joined as strings (`Link/Channel/Processor.cs:1505`, `Link/LinkService.cs:887`, `:944`). RRF splits by the free space of the transfer in bytes (RRF `DataTransfer.cpp:1488-1541`). "Tür offen" split after byte 2 becomes "T��r offen" (measured by the census), in the console, the logs, code replies to IPC clients and string values of M409 replies. Relevant for German texts on the CHX. Not seen on the printer. Not UB10. Upstream code. Upstream report candidate. Fix sketch: keep the partial message as bytes on the fields that already hold it and decode once when the packet without PushFlag arrives.
- **CU3 (low). `M905 P"yyyy-mm-dd"` without S sets the SBC clock to midnight, and RRF never gets the code.** The P branch of DuetPiManagementPlugin runs `timedatectl set-time yyyy-MM-dd` but does not set `seen` (`DuetPiManagementPlugin/Program.cs:610-625`; only A, S and T do, `:607`, `:636`, `:653`), so the code is answered locally (`:666-669`). systemd reads a date without a time as 00:00:00 (`systemd.time(7)`; checked by the census with `systemd-analyze`, timedatectl not run), while RRF keeps the time of day (RRF `GCodes3.cpp:688-700`). With #215, DCS then sees the jump and gives RRF the midnight time as well. The wiki does not say what P alone does (`GcodeDictionaryFull.md:6227-6246`), so this is a deviation from RRF. Upstream code (cf11a4cd00). Upstream report candidate. Fix sketch: one `set-time "yyyy-MM-dd HH:mm:ss"` with the current time of day when S is missing, and `seen = true` in the P branch.
- **CU4 (low). A change of the time zone at runtime is not seen by DCS, DWS or DuetPiManagementPlugin.** `TimeZoneInfo.Local` is cached per process, and nothing calls `ClearCachedData` (grep by the census); `DateTime.Now` stays in the old zone (measured). Input: `M905 T"…"` (`DuetPiManagementPlugin/Program.cs:645-653`) or `timedatectl set-timezone`. The detection of a time change of #215 compares local times in the cached zone, so RRF keeps the old local time until DCS restarts and config.g sends M905 (`Files/MacroFile.cs:319-329`); message times, file times and the M929 log stay in the old zone as well. `M905 P S T` applies P and S in the old zone and then changes the zone (`Program.cs:610-653`), so the SBC shows S plus the difference of the offsets while RRF got S (`:664`). The wiki does not say in which zone P and S are meant (`GcodeDictionaryFull.md:6227-6246`). Not relevant on the CHX, where the zone is fixed (from the census). Upstream code (cf11a4cd00, 75eb65361b). Upstream report candidate. Fix sketch: DuetPiManagementPlugin applies T first; DCS calls `TimeZoneInfo.ClearCachedData()` before the check of #215.
- **CU5 (low). DuetPiManagementPlugin formats the timedatectl arguments with the current culture.** `$"set-time {date:yyyy-MM-dd}"` and `{time:HH:mm:ss}` (`DuetPiManagementPlugin/Program.cs:614`, `:631`, reply `:668`) use the culture's time separator and calendar, and the plugin inherits LANG from its plugin service (CU6). Measured by the census: under `fi_FI`, `M905 S"10:05:07"` gives `set-time 10.05.07`, which systemd refuses; under `th_TH`, `M905 P"2026-11-24"` gives `2569-11-24`, which systemd accepts, so the clock jumps 543 years ahead. `de_DE`, `en_*` and `C.UTF-8` are not affected. Upstream code (50c301c527). Upstream report candidate. Fix sketch: format with `CultureInfo.InvariantCulture`, or the invariant mode of CU6.
- **CU7 (low). Expressions evaluated on the SBC print floats differently from RRF.** Since upstream e3a136b27, DSF evaluates expressions itself when it can (`Codes/Meta/Expressions.cs:1009-1025`), `echo` included (`Codes/Handlers/KeywordHandler.cs:54-172`), and prints floats in the shortest round-trip form (`Codes/Meta/Parsing/MetaExpressionParser.cs:1299-1300`). RRF prints the decimals as written, or 7 after a division (RRF `ExpressionParser.cpp:1672-1688`, `:585-590`, `RepRapFirmware.cpp:168-203`). Examples (DSF measured by the census, RRF from reading): `echo 1.0` gives `1` against `1.0`, `echo 1/3` gives `0.33333334` against `0.3333333`. Text only: echo output and files, M117/M118, comparisons of such strings, and names built with `^` (`M98 P{"step" ^ 1.0 ^ ".g"}` looks for `step1.g`). Numbers passed as parameters round-trip (measured). Upstream code. Upstream report candidate. Fix sketch: carry RRF's number of decimals with the value in `MetaExpressionParser` (`ParseNumber`, `:1340-1385`) and port `GetFloatFormatString`.
- **RL7 (low). The full-transfer counter of the SPI link wraps after about 14 days without M122.** `_numMeasuredTransfers` is an `int` (`Link/Adapter/SPI.cs:70`), incremented per full transfer (`:400`) and reset only by M122 (`:213-224`, `:285`). At 1,780 transfers per second when idle (`design/lab-test-plan.md:461`, `:852`), 2^31 is reached after 14.0 days, while printing after about 15 to 16. M122 then shows a negative or wrong "Full transfers per second". The same code is in `Link/Adapter/USB.cs:42`, rate not known. Diagnostics only; related to T3. Upstream code. Upstream report candidate. Fix sketch: a `long`, or a rate per window.
- **TP4 (low). `BuildServiceProvider` in AddIPC and AddFiles gives two static loggers their own console logger, so their lines are out of order in the journal.** `IPC/Extensions.cs:20-22` and `Files/Extensions.cs:20-22` build extra containers for `CodeInterception.SetLogger` and `IconImageParser.SetLogger`, and `Link/Extensions.cs:32-33` a third one for the settings, inside `ConfigureServices` (`Program.cs:179-188`). Each has its own console provider and writer thread, so CodeInterception lines appear before or after link and pipeline lines that came first (measured by the census, 3 of 3 runs), and they are not flushed at shutdown. No functional effect. Upstream code. Upstream report candidate. Fix sketch: set the static loggers from the host's container after `Build()` (`Program.cs:193-195`) and read the settings from `IConfiguration`.
- **FS9 (low). The watcher of DuetPiManagementPlugin for /etc/default/crda stops after the first garbage collection.** The `FileSystemWatcher` is held only by a local variable (`DuetPiManagementPlugin/Network/CountryCodeUpdater.cs:32-42`), so it is collected; the census saw events before a collection and none after, in two independent probes. A change of the WiFi country is then not noticed. Used only without NetworkManager (`:74-80`). Upstream code. Upstream report candidate. Fix sketch: keep the watcher in a static field.
- **Additions to G5 and G10 (#124) from round 2:**
  - G5: `abs(-2147483647 - 1)` and `mod(-2147483647 - 1, -1)` throw OverflowException as well (`Codes/Meta/Parsing/MetaExpressionParser.cs:990-1000`, `Math.Abs(int)`, and `:1003-1010`; measured by the census). RRF uses `labs` and `%` (RRF `ExpressionParser.cpp:1933`, `:2045`); their results on ARM, INT_MIN and 0, are from reading.
  - G10: `fileread` reads the token `5-` as -5, and `1,000` with a delimiter other than ',' as 1000 (`Codes/Meta/Functions.cs:188`, `:192`, `NumberStyles.Any`, see CU8; measured by the census). RRF reports "format error" for both (RRF `ExpressionParser.cpp:1365-1378`).
- **Security, not fixed (round 1 and 2).** Upstream code unless marked otherwise. Found from the code; the PRs above name their probes.
  - **LX5 rest (medium). Every plugin may still delete or replace `/run/dsf/dcs.sock`.**
    - AppArmor mediates sockets with a path by file rules, and the `w` that a plugin needs to connect also allows the unlink (apparmor.d(5), "Unix socket rules" and "Access Modes Details"). Plugins, DCS and the socket share the uid dsf, so the directory permissions do not help either.
    - Effect: DWS can no longer connect to DCS, also not for the M112 of DWC's emergency button; or a plugin binds a socket of its own there and gets what DWS sends.
    - CHX: the image's own profile gives the bundled plugins `/run/dsf/* rw` (rpi-image-gen `opt.dsf.bin.DuetPluginService:239-240`).
    - Options, both a change of packaging and image: plugins under their own uid with `dcs.sock` in a directory they cannot write, or a socket that systemd creates in a root-owned directory. Design decision.
  - **The namespace of an HTTP endpoint is not tied to the plugin (low to medium).** A plugin with RegisterHttpEndpoints may register in any namespace that is not reserved (`DuetControlServer/Commands/HttpEndpoints/AddHttpEndpoint.cs:27-31`), and the AppArmor rule of #233 lets it delete and replace every endpoint socket, also those of other plugins. The CHX image ties them through per-namespace rules in its own profile.
  - **A confined plugin can make DWS read files outside its profile (low to medium).** A plugin with RegisterHttpEndpoints answers `HttpResponseType.File` with any path, and DWS opens it as it is (`DuetWebServer/Middleware/CustomEndpointMiddleware.cs:284-289`). DWS runs as dsf without a profile, so this bypasses the plugin's AppArmor profile, not the file permissions. Fix sketch: serve only files below the plugin's directory; that needs the endpoint tied to its plugin (item above).
  - **`RootPluginSupport` is controlled by the dsf user (design).** The setting is in the dsf-owned `config.json` (`DuetControlServer/Settings.cs:68`), and only DCS, which runs as dsf, enforces it (`DuetControlServer/Commands/Plugins/InstallPlugin.cs:44`, `StartPlugin.cs:141`, `ReloadPlugin.cs:51`, `Commands/Packages/InstallSystemPackage.cs:24-27`). So the dsf user can allow root plugins and system packages for itself, and a SuperUser plugin runs as root by design. #234 does not change this. Option: the root plugin service takes the decision from its own root-owned settings. Question for Tim and Christian Hammacher.
  - **#234 keeps earlier tampering.** An upgrade makes `plugins.json` root's but keeps its content; the PR lists the keys an administrator should check.
  - **CHX image and #234 (image repository, not changed).** `mp-dsf-configure:293-299` gives /opt/dsf/conf and `plugins.json` to dsf (2770/0660). With #234 the root plugin service then logs at every start that it refuses its settings, and refuses what would use them; plugin installs keep working, because the image has no root plugins and turns AppArmor off. The image should keep /opt/dsf/conf root:dsf 1775 and `plugins.json` and `apparmor.conf` root:root 0644, also where it restores /opt/dsf/conf at boot (not checked).
  - **#232 and the volume list.** A mount below a directory that dsf cannot search drops out of `volumes`, and the later volumes move down by one; according to the image source there is no such mount on the CHX. Raising CAP_DAC_READ_SEARCH around the `statfs` calls would keep the numbering but list volumes that DCS cannot read; not done.
  - **pipInstall2.py builds shell commands from requirements files without a check (low).** It runs every command with `shell=True` (`pkg/common/duetpluginservice/opt/dsf/bin/pipInstall2.py:412`) and puts module names into them unquoted (`:530`) or in double quotes (`:570`, `:572`). DCS checks the characters of `sbcPythonDependencies` (`DuetControlServer/Commands/Plugins/InstallPlugin.cs:110-120`), but not the lines of a requirements file in the plugin (`pipInstall2.py:463-476`), and the `git+` form accepts any text (`:150`). No escalation: the installer runs as the user of the service that runs the plugin (`DuetPluginService/Commands/InstallPlugin.cs:402-406`), and pip runs the build code of a package anyway. Like every pip build, it runs outside the plugin's AppArmor profile.
  - **Same class as LX6, not fixed (from LX6, not run):**
    - DuetPiManagementPlugin mounts for `M21 P… S…` as root on a mount point that DCS resolves lexically below the virtual SD card; mount(8) canonicalizes the path, so a symlink of dsf chooses the mount point.
    - `DuetControlServer -u` runs as root (reprapfirmware postinst) with the dsf-owned `config.json`, whose `StartErrorFile` it writes (`DuetControlServer/Settings.cs:485`, `Program.cs:48`, `:231-235`).
    - dpkg follows a symlink that dsf puts in place of a directory below /opt/dsf/sd and unpacks the package's files there as root (dpkg(1), `*.dpkg-new`); dsf chooses the directory, not the names or the content. No maintainer script can prevent that.
    - The reprapfirmware package of the Duet3D archive runs the same `chown` on `sd/firmware/*.bin` and `*.uf2`; the archive packages give `./opt/dsf/` uid/gid 1001.
  - **CHX image: mp-dsf-seed follows symlinks of dsf as root at every boot (image repository, not changed).**
    - `mp-dsf-seed` runs as root before DCS (`mp-dsf-seed.service`). `MP_PRINTER_NAME_FILE` is `/persistent/shared/opt/dsf/sd/sys/overrides/printer-name.g` (`mp-common.sh:26`), below the dsf-owned, slot-shared `/opt/dsf/sd/sys` (`slot-shared.d/dsf.conf`).
    - If dsf replaces `printer-name.g` (or `overrides`) with a symlink to an existing file, `[ ! -f … ]` is false and only `chown dsf:dsf "$MP_PRINTER_NAME_FILE"` runs (`mp-dsf-seed:123`), which follows it: at the next boot dsf owns the target. Targets are files on writable persistent storage, e.g. the SSH host keys of the slot-shared /etc/ssh. With a dangling symlink, `cat >` and `chmod 0660` (`:56-63`) also write a new file with the printer name where dsf points. Found by reading the image source, not run on a printer.
    - `chown -h` is not enough, because the path runs through directories that dsf may replace. Fix sketch, as in #235: create the file with the rights of dsf (`setpriv --reuid=dsf --regid=dsf --clear-groups`) and drop the chown.
    - `chown -R dsf:dsf "$DSF/conf" "$DSF/plugins"` (`mp-dsf-seed:122`) gives `plugins.json` back to dsf at every boot, so with #234 the image change above must cover mp-dsf-seed too.
  - **Checks that need hardware or a package build:** the start of a plugin through aa-exec, bash and python under its profile on a kernel with AppArmor (#233; the CHX turns DSF's profiles off, so it needs a stock DuetPi); before #232 is merged, `sudo -u dsf find /opt/dsf \( ! -readable -o -type d ! -executable \) -print` on the lab printer (expected: nothing) and the lab test `ControlServerCapabilities` with a package build; the RPM specs of #234 were not built.
- **Theoretical, no trigger shown, round 2 (upstream code):**
  - H1: config.json of DCS is written only when it is missing (`Settings.cs:430-434`), without a sync; a power cut during that first write would leave a file that stops every start of DCS. The package ships it as a conffile.
  - H1: DWS watches http.json (`DuetWebServer/Program.cs:45`) and its content root with inotify, and the dsf user shares `max_user_instances` with DCS, DPS and the plugins. No count near the limit was found.
  - H1: M471 D1 (`Codes/Handlers/MCodeHandler.cs:907-912`) and `POST /machine/file/move` with force (`DuetWebServer/Endpoints/MachineEndpoints.cs:438-450`) delete the destination before the move, so with the same source and destination the file is lost. RRF does the same (RRF `MassStorage.cpp:1231-1244`).
  - E: counters that wrap only after years or 2^31 events: `Connection.Id` (`IPC/Connection.cs:41-46`), the session id (`Commands/UserSessions/AddUserSession.cs:16`), the file handle counter (`Link/LinkService.cs:61`, which skips `NoFileHandle`, `:1500-1503`) and `CodeBlock.Iterations` (`Files/CodeFile.cs:426`).
  - G: an integer array parameter given as floats is converted without a range check and rounded half to even, where RRF truncates (`DuetAPI/Commands/Code/Parameter.cs:620`, `:624`); no code that reads such a parameter with fractions was found.

## Found while writing the lab tests of the fork fixes (2026-11-25)

Line numbers refer to DSF `v3.7-dev` at 2ee61ca1d and to upstream `v3.7-dev` at dbf9b0447. Paths are relative to `src/`; a path without a project folder is in `DuetControlServer/`. RRF lines refer to the RRF fork `3.7-dev` at 0d4295065. "The lab run" is the run of PR #245 on 2026-11-25: image DSF 3.7.0+mp.15, RRF 3.7.0+1.mp.12-dev.2, DCS and DWS of `v3.7-dev` side-loaded.

- **LT1 (low, RRF part). RRF wraps a date and time before 1970 to one about 2^56 s after 1970.**
  - RRF subtracts an integer from a DateTime without a range check (RRF `src/GCodes/GCodeBuffer/ExpressionParser.cpp:539-553`) and keeps it in 56 bits (RRF `src/ObjectModel/ObjectModel.h:167-170`). From reading: `datetime(0) - 1` gives 2^56 - 1 s. What RRF prints for that value (`gmtime_r`, RRF `src/ObjectModel/ObjectModel.cpp:80-88`) was not run.
  - The DSF part is fixed (see the fixed list below): DSF answers such a value with an error instead of stopping.
  - Upstream code. Upstream report candidate (Tim, 2026-11-25).
- **Fixed and removed from this list (2026-11-25):** LT2 (`floor`, `ceil`, `round` and `pow` of an expression that DSF evaluates gave the int 2147483647 instead of the float 2147483648 for 2^31). `FloatToIntOrFloat` and `Pow` take the bound of RRF #110 (RRF 33e6a184e, `ExpressionParser.cpp:2038`, `:2293`): an int only below 2^31 and from -2^31 on. Upstream still has the old bound. Test: `Machine.MetaExpressionIntegerRange`.
- **Fixed and removed from this list (2026-11-25):** LT1, DSF part (DCS stopped when RRF answered with a date and time that .NET cannot parse, e.g. a year beyond 9999 from `datetime("9999-12-31T23:59:59") + 1`). Reader returns a CodeParserException as the result, as it does for expression content it cannot read (`Link/Protocol/Reader.cs:443-447`), so only the code that asked gets the error "date and time … is out of range" and the link goes on. Tests: `SPI.LinkServiceOverSpi.DateTimeOutOfRangeFailsOnlyItsRequest`, `SPI.ReaderRobustness.EvaluationResultDateTime`.
- **Fixed and removed from this list (2026-11-25):** LT3 (a refused deeply nested expression was logged with a stack frame per nesting level, about 4,000 journal lines per refusal, so journald dropped further lines of DCS for up to 30 s). Both parsers throw the refusal again at their top (`MetaExpressionParser.Parse`, the first `eatExpression` call in `Expressions.EvaluateExpressionToValueAsync`), so the logged trace has 5 to 8 frames instead of 1,285 to 1,878 on the 1 MB test stack. The text is `ExpressionErrors.NestingTooDeep`, as RRF's `CheckStack`. Tests: `Machine.ExpressionNesting` (frame limit); lab tests `ExpressionEvaluation.DeeplyNestedExpressionIsRefused` and `FileInfoReplies.DeeplyNestedCustomInfoIsSkipped` (journal line limit).

## Found on the lab printer: TOOL1LC thermistor offset (2026-11-26)

Line numbers refer to the CoreN2G fork `3.7-dev` at 9dc5b6acd and to the Duet3Expansion fork `3.7-dev` at 7dc11034. The board is the TOOL1LC (CAN address 20) of the lab printer, running Duet3Expansion 3.7.0+1.mp.6-dev.1 (78127f2e with CoreN2G 9dc5b6acd). The test was on 2026-11-26 with heaters off and was agreed with the sync session.

- **TL1. The TOOL1LC thermistor reading is several degrees low in some builds, because ADC0 and the SDADC convert the Vref and Vssa pins at the same time.** Forum thread [39227](https://forum.duet3d.com/topic/39227) reported it on 3.6.3.
  - On mp.6-dev.1 the PT1000 on 20.temp0 read 8.8 C while the room was about 18-20 C (Tim). The ADC0 filters read Vssa 1087 counts and Vref 41700 (filter sum / 8, read with `M122 B20 P1007`).
  - The SDADC always converts PA07/PA06 (Duet3Expansion `src/Platform/Platform.cpp:888`, `src/Config/TOOL1LC.h:202-203`). ADC0 converts the same pins as Vref and Vssa. The SAMC21 `AnalogIn::TaskLoop` started each converter whenever it was free (CoreN2G `src/SAME5x_C21/SAMC21/AnalogIn.cpp:559-606`).
  - A/B test in the same image:

    | | Vssa | Vref | Temperature |
    |---|---|---|---|
    | SDADC restarts stopped (`numChannelsEnabled` of the SDADC object cleared with `M122 B20 P1007`) | 104 | 42147 | 24.5 C |
    | Restarts restored | 1087 | 41700 | 8.8 C |

    Two runs gave the same result. AnalogIn reported 0 timeouts and 0 errors.
  - It is not the DMA and not the barriers:
    - every transfer completed;
    - the SAMC21 has no data cache (CoreN2G `src/Cache.h:35-42`);
    - 054412b and ad2f483 are in the build.
  - Why ADC0 reads wrong while the SDADC converts the same pins is not established.
  - The fault depends on the layout. Both converters run from the 48 MHz clock (`AnalogIn.cpp:619`, `:624`), so the software start timing decides how the two conversions line up.
    - The forum thread shows that builds without the fault exist. Which builds have it was not surveyed.
    - The tagged Duet3Expansion mp.6 has a different layout from mp.6-dev.1, so this result does not carry over to it.
  - Tim decided on 2026-11-26 to build the fix as a CoreN2G branch and PR, first without hardware.
  - Workaround seen on the lab printer (Tim, 2026-11-26): `M308 S2 P"*20.temp0" Y"pt1000" A"left"` removes the offset. The `*` goes before the board address.
    - It puts temp0 on SDADC channel 1: PB09 (INP1) against PB08 (INN1), `TOOL1LC.h:238-239`, through `alternateConfig` (Duet3Expansion `src/Hardware/IoPorts.cpp:302-305`, `Platform.cpp:1340-1346`).
    - The reading then uses the SDADC Vref filter and no Vssa filter (`Platform.cpp:1381-1398`), so the ADC0 Vref and Vssa readings no longer enter it.
    - It does not help temp1. PA02 has no SDADC input (`TOOL1LC.h:198`), so temp1 still uses the ADC0 Vref and Vssa filters.
    - The SDADC path has its own calibration slot, filter 4 (`Thermistor.cpp:97-99`), so the L:-11 H:-1 of slot 0 does not apply to it.
    - Not recorded yet: the temperature it reads, compared with the room.
  - Fix in CoreN2G #9: the converters take turns. Host test in Duet3Expansion #36 passes 5 of 5 scenarios with the fix and 1 of 5 without it.
    - Behaviour change: both converters convert less often. The rate on the board has not been measured.
    - Neither PR is merged, and the fix has not run on hardware.
  - Open:
    - a lab run of the fixed build (the same readout plus the conversion rate), which needs Tim's go;
    - 24.5 C with the SDADC stopped against the 18-20 C room: check with a reference thermometer;
    - the DSF firmware pin after the merge, and the Duet3Expansion release;
    - an upstream report. Duet3D CoreN2G `3.7-dev` c9b23d7 has the same loop.
- **TL2. Candidate cause for the spikes of forum thread 39227: SAMC21 errata DS80000740T 1.10.4, "Concurrent channels triggers".**
  - The errata says the DMAC write-back descriptors may get corrupted when channels are triggered concurrently. The workaround it gives is to sequence multiple transfers only with linked descriptors on a single channel. It lists the E/G/J devices as affected in silicon revisions E, F and H.
  - The TOOL1LC uses five DMA channels (Duet3Expansion `src/Config/TOOL1LC.h:52-56`).
  - Not checked:
    - whether the erratum applies to channels without linked descriptors;
    - the silicon revision of the lab board;
    - whether spikes still occur on 3.7.
  - Record only.

## Found in the release check of 2026-11-26

Line numbers refer to DSF `v3.7-dev` at a7211cf33 and to the RRF fork `3.7-dev` at 323b5b185. Paths are relative to `src/DuetControlServer/`. The run: DCS and DWS of #255 (e28eb04ce) side-loaded on image DSF 3.7.0+mp.15, RRF 3.7.0+1.mp.12-dev.3, expansion boards 3.7.0+1.mp.6-dev.1.

- **LT4 (low, upstream code). When DSF evaluates an expression itself, a DateTime plus or minus seconds beyond the range of .NET fails the code with the .NET text, where RRF gives a date.**
  - DSF first tries to evaluate the whole expression on the SBC and forwards it only when it reaches something it cannot resolve (`Codes/Meta/Expressions.cs:1021-1036`, `Codes/Meta/Parsing/MetaExpressionParser.cs:74-86`). The parser works from left to right and applies an operator as soon as both operands are known (`MetaExpressionParser.cs:433-443`). So in `datetime("9999-12-31T23:59:59") + 1 + 0 * state.upTime` it adds the 1 before it reaches `state.upTime`, and in `datetime("9999-12-31T23:59:59") + 1` there is nothing to forward at all.
  - `DateTime.AddSeconds` (`MetaExpressionParser.cs:455-464`, minus `:478-491`) throws an ArgumentOutOfRangeException, and the code fails with "The added or subtracted value results in an un-representable DateTime. (Parameter 'value')". DCS goes on.
  - RRF adds and subtracts without a range check (RRF `src/GCodes/GCodeBuffer/ExpressionParser.cpp:508-522`, `:539-553`) and answers `10000-01-01T00:00:00`. When that answer reaches DSF, Reader refuses it with "date and time 10000-01-01T00:00:00 is out of range" (LT1, DSF part, `Link/Protocol/Reader.cs:443-447`). The same expression thus fails with two different texts, depending on whether DSF or RRF evaluates it.
  - Seen in the lab test `ExpressionEvaluation.DateBeyondYear9999IsAnError`, whose expression DSF evaluated itself; the test now puts `state.upTime` before the `+ 1`.
  - Fix options: (a) the parser forwards an operation whose result DateTime cannot hold, so the code gets the answer of the forwarded path, as #238 forwards datetime() strings DSF cannot read exactly; (b) the parser throws a CodeParserException with the text of LT1. (a) follows the rule of aligning with RRF (Tim, 2026-11-24). Not decided.
  - Upstream code (`MetaExpressionParser` came with upstream e3a136b27). Upstream report candidate.

## Found in the upstream sync of 2026-11-27

The sync takes Duet3D 3.7.1-rc.1 into DSF (#261), RRF (Meltingplot/RepRapFirmware#121), Duet3Expansion (Meltingplot/Duet3Expansion#39), CoreN2G (Meltingplot/CoreN2G#12) and DWC (Meltingplot/DuetWebControl#30). Line numbers refer to DSF `v3.7-dev` 41187e19a and to the RRF branch `chore/sync-upstream-2026-11-27` 3a45478e5. DSF paths are relative to `src/DuetControlServer/`.

- **FL1 (high). The MB6HC image no longer fits in the flash after the upstream merge, and the linker did not report it.**
  - With the merge and the trigger fix, the image is 1,048,872 B. The CRC brings it to 1,048,876 B, against 1,048,576 B of flash (`RRF/Hardware/SAME70/same70q20b_flash.ld:54`): 300 B too large. Fork `3.7-dev` 5c2ddc01f is 1,047,948 B, which leaves 624 B.
  - `.relocate : AT (_etext)` has no memory region for its load address, so `ld` checks the code but not the initialised data and the CRC behind it. The merge image linked with `.relocate` loaded at 0x004ffeb8 to 0x00500090, past the end of the flash. Only the SAM4E script has upstream's check (e983896d5).
  - Fix of the check: Meltingplot/RepRapFirmware#120 copies it to the SAM4S, SAME5x, SAME70 and PIC32CZ scripts, with `Tools/flash-overflow-test` (36/36, 16 fail without it). With #120 the sync tree stops at the link.
  - MB6XD (970,972 B) and Mini5plus (988,692 + 328 B) fit.
  - The same tree with upstream's `src` and the fork libraries builds to 1,038,964 B: the fork's code is 9,908 B more than upstream's. The largest parts are `SbcInterface.o` +2,192, `DataTransfer.o` +1,464 and `TriggerItem.o` +1,364.
  - Options, each measured by a full build against 1,048,872 B (placement padding varies by about ±40 B). Neither of the first two changes what the firmware does:
    - (a) `noinline` on `DataTransfer::CanWritePacket` and `WritePacketHeader` (`RRF/SBC/DataTransfer.h:208-210`, inline body `:250-253`; 27 and 28 call sites): −2,368 B. One more call per packet in the SBC task. Upstream has the same helpers.
    - (b) `-Os` for `FileInfoParser.cpp` and `PrintMonitor.cpp`: −2,472 B. File-info parsing and print estimates get slower; in SBC mode DSF parses file info itself. (a) and (b) together: −4,776 B.
    - (c) `-Os` for parts of LibMbedTls (TLS, X.509, ASN.1 parsing; the crypto stays at -O2): −7,936 B. This affects only standalone HTTPS. Chris chose O2 for it (LibMbedTls 947bb9bce).
    - Further `-Os` candidates are measured but closer to motion, probing or the link: GCodes2 −7,916 B, lwIP −8,616 B, Fatfs −5,256 B, DataTransfer and SbcInterface −5,788 B.
    - Removing fork code is worth little: the M122 "Channel epochs" lines −296 B, upstream's text for the "output buffers full" message −180 B. Replacing the M112 action line with K0 saves only −128 B and would change the safety behaviour.
  - **Decided (Tim, 2026-11-27): leave the Hangprinter kinematics out of the MB6HC build.** None of the options above is taken.
    - RRF 37bd104db on Meltingplot/RepRapFirmware#121: `SUPPORT_HANGPRINTER 0` in `RRF/Config/Pins_Duet3_MB6HC.h`, as upstream does for Duet 2 and PCCB (`Pins_DuetNG.h:87`, `Pins_Pccb.h:68`). The MB6HC has a second CAN port, so the ODrive parts of M569.3, M569.4 and M569.8 (`RRF/CAN/CanInterface.cpp:1442`, `:1467`, `:1554`) now need `SUPPORT_HANGPRINTER` as well; no board had a second CAN port without Hangprinter before.
    - MB6HC: 1,031,428 B, 17,444 B less, which leaves 17,144 B after the CRC. MB6XD (970,972 B) and Mini5plus keep Hangprinter and do not change.
    - Behaviour change on the MB6HC: `M669 K6` and `K"hangprinter"` are refused with "Unknown kinematics type" (`RRF/GCodes/GCodes2.cpp:4325`, `Movement/Move.cpp:949-953`) and the kinematics stays as it was. M569.3, M569.4 and M569.8 answer as they already did on a machine that is not a Hangprinter. The wiki lists `hangprinter` for M669 without a board limit (`Gcodes.md`, M669 K).
- **T14 (medium, upstream code). DSF does not support M581 K for triggers it evaluates itself.**
  - DSF takes over every M581.1 whose expression has an `sbc.*` field (`Model/SbcTriggerService.cs:127-131`). It sends `M581 T<n> P-1` to RRF (`:144`), which resets the trigger's K action in RRF (`RRF/GCodes/TriggerItem.cpp:20-34`), and it does not keep K itself.
  - When the trigger fires, DSF stops the machine for trigger 0 and sends `M582 T<n> S1` for all others (`:306-322`). RRF then does the default action of the number (`TriggerItem.cpp:47-54`). So `K3` on such a trigger runs `trigger<n>.g` instead of a feed hold, and trigger 0 with `K2` stops the machine instead of running `trigger0.g`.
  - `M581 T<n> K<k>` without other parameters deletes DSF's trigger, because only T, R and S count as a report or a change of the enable condition (`:121`, `:129`).
  - DSF's report has no `(K<n>, <action>)` part (`:198-205`), unlike RRF's (`TriggerItem.cpp:392-394`).
  - The service came with upstream 4229f5527 and K with upstream RRF 5f54bb621, so upstream has the same gap. Upstream report candidate. Not fixed in the fork yet.
- **T15 (low, fork code, there before K). A trigger with an action line that an expression error has disabled stays disabled until a restart.**
  - `Check` disables a trigger whose expression cannot be evaluated, unless it is an emergency stop (`TriggerItem.cpp:171-185`). A trigger with an action line is locked (`:56-60`), and the lock refuses `R0` as well (`:199-211`). The trigger can be enabled again only by a restart.
  - That matters for a safety trigger that is not an emergency stop, such as a door check with an action line: one failed evaluation switches it off for good.
  - Found by the host test of the trigger fix (`Tools/trigger-action-test`, probe). Same on the merge commit, so not caused by K. Not decided: fire as well (like an emergency stop), or let `R` through the lock.
- **K40 (medium, fork epoch code, test models disagree). A restarted RRF stops the machine when it reads a packet that DSF wrote before it noticed the restart.**
  - Found while fixing the flake of `MacrosOnOtherChannels.ResetCancelsWaitingCall` (DSF fix: fake firmware only, `IsResetPending` until DSF has handled the reset). With forced delays the test failed 2 times in about 45 runs with `G4: Machine is halted`; without delays 0 in 120 resets.
  - Order in the fake: the reset lands after LinkService checked `HadReset()` (`Link/LinkService.cs:380`) and before the transfer that carries the packets of that iteration (`:652`). The restarted fake reads them, so `_sbcPacketRead` is set, and DSF's first marker in the next transfer ends the connection with an emergency stop (`UnitTests/FakeFirmware/FakeFirmware.cs:254-275`).
  - RRF does the same: a restarted RRF starts with `sbcPacketRead` false (`RRF/SBC/SbcInterface.cpp:1898-1900`), any packet it reads sets it (`:370`), and a first marker after that calls `EndConnection` (`:346-355`), which is an emergency stop (`:294-298`).
  - On SPI the window exists as well: after a timeout DSF exchanges its kept transfer again in a new session (`Link/Adapter/SPI.cs:451-459`), so the restarted RRF reads packets written before the restart, and DSF sets `_hadReset` only after that exchange (`:383-390`). `sbc-link-v8-requirements.md` ("Windows that are left") calls the related adopt case "not shown to occur"; a restarted RRF drops its first transfer in about half of the cases (section "Stop path under SPI faults").
  - The tests disagree: `JobProcessorFailures` expects the stop (`UnitTests/Codes/JobProcessorFailures.cs:293-295`, `:409-410`), `ChannelEpochWithDsf.MarkerOfAConnectionToARestartedRrfStopsNothing` (`UnitTests/Link/ChannelEpochWithDsf.cs:971-996`) says decision D-B does not stop the machine after a restart, which holds only while no stale packet comes first.
  - Not seen on hardware; a lab check (RRF reset while DSF has packets queued, then `state.status`) is open. Options: (a) RRF does not count a packet without the first marker as the start of a connection after boot, so D-B holds after a restart (RRF and fake together); (b) the tests accept the halt; (c) the fake resets only right after a transfer. Not decided.
