# Open findings

The fork has no issue tracker, so findings to solve later are kept here. Remove an entry when it is solved.

Status: 2026-11-07. Line numbers refer to the Meltingplot fork: DSF `v3.7-dev` at 4c8b3fec and RRF `3.7-dev` at fc939b3.

## G and M cannot be macro parameters

**Problem:** `M98 P"x.g" G7` does not pass `param.G` to the macro. The parser ends the M98 at `G` and runs `G7` as a separate code after the macro. A value the caller meant as a parameter is executed, so `M98 P"x.g" M3` would run an `M3`. `M98.n` behaves the same, except that the separated code runs on the calling channel while the macro starts on the other one.

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
| `M98.3 P"t.g" G7` in a trigger macro | `M98 P"t.g"` on HTTP, `G7` on Trigger |

The separated code runs after the macro has finished (`M98 P"t.g"`, the codes of the macro, then `G7`).

**Code and documentation:**
- DSF: `StartsNextCode` ends a code at G, M or a second T (`src/DuetAPI/Commands/Code/Parser.cs:727-730`). The same rule is in upstream `v3.7-dev`. For `'G` it keeps the uppercase letter, so that escape does not prevent the split.
- RRF: `StringParser::FindParameters` also ends a command at an unquoted G or M, but not after `'` (`StringParser.cpp:1118`). So `'G` differs between DSF and RRF's text parser.
- Wiki `Gcodes.md:233`: each G or M preceded by a space starts a new command, so the split itself is documented.
- Wiki `Gcodes.md:235`: a command that invokes a macro must be the last on its line, "because any following commands on the same line will not be executed". DSF does send them, after the macro.
- Wiki `Gcodes.md:2398` and `Gcode_meta_commands.md:355`: additional parameters go to the macro as `param.<letter>`, with no exception for G and M.

**Open questions:**
1. Should G and M be parameters of M98 and M98.n when `P` is present? That contradicts `Gcodes.md:233` and differs from upstream, unless upstream changes as well.
2. If not, should DSF refuse a G or M after a macro call on the same line instead of running it? That would match `Gcodes.md:235` and catch the mistake.
3. Should this go to Duet3D, given that the wiki contradicts itself here and DSF and RRF differ for `'G`?

**Workaround:** use another letter. chx350-config passes neither G nor M through M98 today, as counted by its session on 2026-11-06. The same count shows macros reading `param.M` with no M98 call passing it, which is worth a look in that repository.

## Macros do not start on the USB channels without a USB host

**Problem:** with no USB host connected, a macro sent to the USB or USB2 channel never starts and the channel stays busy. DSF reported success for the call. That is why `M98.n` leaves out USB and USB2 (#108).

**Observed** on the lab printer (MB6HC, RRF 3.7.0+1.mp.7, DSF side-loaded from 5ffadeca) with the former `M98.1` and `M98.7`, during about 10 s per channel:
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

**Wanted (Tim, 2026-11-06):** with a USB host connected, the USB channels should be usable with `M98.n`.

- By the code above this should already work while the host has the port open, because the reset only runs while `IsConnected()` is false. Not tested: the lab printer has no USB cable to the SBC.
- `inputs[]` does not say whether a host is connected. Only the text reply of `M575 P0` and `M575 P1` does (`Platform.cpp:2412-2425`), so DSF would have to send that code and parse its reply before every `M98.1` or `M98.7`.

**Proposed fix, not built or tested:** in RRF, leave codes from DSF alone in `GCodeBuffer::DiscardPartialLine`, in the style of `GCodeBuffer::Enable`:

```cpp
IF_NOT_BINARY(stringParser.DiscardPartialLine());
```

A partial line can only be a text line, because `GCodeBuffer::Put` clears `isBinaryBuffer` (`GCodeBuffer.cpp:295`). The USB channels would then run codes from DSF with or without a host, so `M98.n` would not have to depend on that.

**Next steps:**
1. Build the fix and repeat the lab test on USB and USB2 without a host. Repeat it with a host that has the port open, which needs a USB cable.
2. Add `1 => USB` and `7 => USB2` back to the `M98.n` table in `MCodeHandler.cs`. Do this only together with an RRF release that has the fix, otherwise a call without a host returns success and the channel stays busy.
3. Report it to Duet3D with the versions above, as the files are unchanged against upstream.

## A firmware restart can leave DSF and RRF one exchange apart until the connection is lost

**Problem:** on one firmware restart, DSF did not notice the restart the usual way (`RepRapFirmware has left the extended link`). DSF and RRF ran one exchange apart until DSF ran out of retries and lost the connection. The link came back 500 ms later, without a DCS restart.

**Observed** on 2026-11-07 on the lab printer (DSF 3.7.0+mp.12 from the image, no side-load, no fault injection, RRF 3.7.0+1.mp.7), in run 4 of 5 of `FirmwareRestart.LinkRecoversAfterWatchdogReset`:

| Time (UTC) | Journal |
|---|---|
| 07:59:38.180 | `Restarting full transfer because a bad header response was received (0x00000000)` |
| 07:59:38.191 to .202 | 99 x `Bad header CRC16 (expected 0x0000, got 0x1760)`, each with `Note: RepRapFirmware didn't receive valid data either (code 0x0007005f)`, and 10 x `Restarting full transfer because the number of maximum retries has been exceeded` |
| 07:59:38.203 | `Lost connection to Duet (Maximum number of SPI transfer retries exceeded)` |
| 07:59:38.702 | `Connection to Duet established`, then `Connection to controller has been reset` |
| 07:59:38.768 | `Link capabilities All are active` |

- The test passed. The printer was idle again after 12.6 s, the other runs took 12.0 to 12.8 s. M122 of DSF afterwards: full transfer restarts 16, header retries 90, extended link left 5, lost connections 1, connection resets 6.
- It is the only `Maximum number of SPI transfer retries exceeded` without fault injection in the journal, which starts on 2026-10-29. The journal has 39 x `has left the extended link`, the first on 2026-11-02, so this was about 1 in 40 restarts. The same suite ran without it earlier that morning and in block 1 on 2026-11-06.

**What the values show:**
- `0x0007005f` is `5F 00 07 00`, the start of a valid RRF header (`Consts.cs:12`, `:27`, `TransferHeader.cs:16-29`), read where DSF expects the response.
- `0x1760` is the CRC16 of `05 00 00 00` and six zero bytes, computed offline with the table in `CRC16.cs`. That is the response `BadHeaderChecksum` (`TransferResponse.cs:34`), read where DSF expects the header.
- The protocol version in that "header" is 0, so `SPI.cs:1848-1858` sets DSF's own version to 0 without a log line. The check then takes the CRC16 path (`SPI.cs:1882-1885`), which explains a CRC16 message on a link with protocol 7.

**Cause, shown later on 2026-11-07** (census, real RRF code in the host harness, RRF review):
1. RRF resets after an exchange has completed and before it sets up the next one, so TfrRdy keeps its level. `M122 P1001` resets go through `SoftwareReset`, which writes the reset data to flash with the interrupts off (reset code `0x80a3`), so an exchange can complete by DMA in that time. `M999` takes the same path.
2. DSF takes the falling edge of `DataTransfer::Init` for its pending exchange and reads 0 from the disabled SPI. With data in the transfer, it answers with `BadResponse`, extended as `0xCCCCCCCC` (`SPI.cs:2008`, `:2027`).
3. That word lands in the first header of the booted RRF, which decodes extended codes only while the extended link is active (RRF `DataTransfer.cpp:873`, `:1086`). It answers `BadHeaderChecksum`, and both sides stay one exchange apart.

The census showed it once a restart between two exchanges was added (`SpiFault.RestartBeforeArm`): the same lines, `0x1760` and 99 x `Bad header CRC16`. 9 combinations of variant and phase lose the connection, all on the extended link. The regular link does not have it.

**Fix (not merged):** RRF decodes extended codes while it is not connected (Meltingplot/RepRapFirmware branch `fix/sbc-decode-header-unconnected`, one condition in `DecodeResponse`). DSF branch `feature/census-restart-before-arm` has the census cases and the harness check. Results:
- In the host harness, the build with the condition matches the port with the rule and the build without it matches the port without it, 0 differences each, on usb0 and usb1.
- On the lab printer (side-load RRF 3.7.0+1.mp.7-dev.1, 2026-11-07): suite 30 of 30, A-1/A-2/F-1, and 40 watchdog restarts without a lost connection (12 of them through the boot window). That is a hint, not a proof, at about 1 in 40.

**Still open:** the duration of the flash write in `SoftwareReset` is unmeasured. Whether `M999` hits the same window has not been shown.

**Next steps:** the PRs of both branches, then an RRF release with the condition. Remove this entry once that is on the printer.

## DCS logs "Failed to process code in stage Executed" while it shuts down

**Problem:** while DCS stopped for a restart, a code was still in the Executed stage and logged an error.

**Observed** on the lab printer (DSF 3.7.0+mp.12):
- 2026-11-07 11:24:14 UTC, during the DCS restart of `run-lab-tests.sh` that removes a side-load. Right after `Application is shutting down...`, `Failed to process code in stage Executed` with `TaskCanceledException` in `AsyncLock.LockAsync`, called from `EventLogger.LogAsync` and `Executed.ProcessCodeAsync`.
- 2026-11-06 09:36:49 UTC, same message, but the stack does not have `EventLogger.LogAsync`. Context not checked yet.
- These are the only two in the journal since 2026-10-25.

**Likely cause, read from the stack and not proven:** the Executed stage writes to the event log with the token that is cancelled on shutdown, and the cancellation is reported as a failure of the code.

**Next step:** check both occurrences in the code and decide whether a cancelled log write on shutdown should be quiet.

## Findings of the fuzz and coverage tests (2026-11-07)

The fuzz and coverage tests of #121 to #127 found 48 bugs and deviations. None is fixed. Each has an `[Explicit]` test in `src/UnitTests` that reproduces it and fails today. Run one by its exact name, e.g. `dotnet test src/UnitTests/bin/Debug/net10.0/UnitTests.dll --filter "FullyQualifiedName=UnitTests.WebServer.SdRootDeletionTests.RootCannotBeDeleted"`. Once a finding is fixed, remove the `[Explicit]` of its test and this entry.

- **Line numbers:** DSF `v3.7-dev` at c6d225f2. The production code there is the same as at d576ee9e, where the tests started. RRF `3.7-dev` at cde4e4fa1 (3.7.0+1.mp.8).
- **Printer:** "printer" marks findings that were confirmed on the lab printer on 2026-11-07, with DSF 3.7.0+mp.13 and RRF 3.7.0+1.mp.8.
- **Origin:** git blame puts all of them on upstream code, except the two DuetHttpClient findings in H14 and H15 and the fork's extended SPI link in S1.

### Highest priority

1. **H1** One request deletes the whole virtual SD card, `config.g` included.
2. **G2** M98 with a path that DSF refuses stops DCS. Printer.
3. **I1** DCS never refuses a missing permission.
4. **I3** An Executed interceptor with filters blocks its channel for good. Printer.
5. **O2** A value of the wrong type in a firmware response ends the object model update loop.
6. **O3** and **O4** An out-of-range float or a global string with invalid UTF-8 makes the object model unserializable, so model queries, subscriptions and DWS fail.

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

### HTTP: DuetWebServer and DuetHttpClient (#123)

- **H1. A recursive DELETE of the SD root deletes the whole virtual SD card.**
  - `DELETE /machine/file/0%3A%2F?recursive=true` answers 204 and removes every file and the directory itself. The same works with `0:`, `0:/.`, `/` and `0:/sys/..`.
  - Cause: `FilePathResolver.CombineContained` accepts the root itself (`FilePathResolver.cs:62-66`), and `DeleteFileOrDirectory` passes `recursive` on (`MachineEndpoints.cs:362-367`).
  - RRF refuses this with "Delete root folder is not allowed" (`MassStorage.cpp:840-858`). In SBC mode RRF hands the delete to DSF before that check (`:822-836`), and `LinkService.HandleDeleteFileOrDirectory` has no check either (`LinkService.cs:1113-1137`).
  - On the CHX, plugin directories are mounted below `/opt/dsf/sd`.
  - Confirmed only in-process, not on the printer.
  - Test: `WebServer.SdRootDeletionTests.RootCannotBeDeleted`.
- **H2. Expired sessions stay in DCS for good when many expire at once.** Printer: 40 parallel logins left 4, each logged "Failed to unregister … (11) Resource temporarily unavailable".
  - Each expiry opens its own IPC connection (`SessionStorage.cs:404, 420-422`). The DCS backlog is 4 (`Settings.cs:116`, also in the printer's `config.json`), and there is no retry.
  - Test: `WebServer.WebServerFindings.ExpiredSessionsAreRemovedFromDcs`.
- **H3. A failed `rr_upload` (wrong crc32) deletes the file it was meant to replace.** Printer.
  - DWS writes into the target (`RepRapFirmwareEndpoints.cs:261-285`). RRF and DWS's own PUT use a `.part` file (`UploadingNetworkResponder.cpp:59-62, 106-128`, `MachineEndpoints.cs:269-291`).
  - Test: `FailedUploadKeepsExistingFile`.
- **H4. POST /machine/file/move with an invalid form gives an unhandled 500.** Printer. `ReadFormAsync` sits outside the try (`MachineEndpoints.cs:397`). Test: `MoveWithInvalidFormIsHandled`.
- **H5. PUT /machine/directory or /machine/file with `?` plus a non-ASCII character gives an unhandled 500, but creates the item anyway.** Printer. `UrlPathEncode` stops at `?` (`MachineEndpoints.cs:294, 504`). Test: `CreatedLocationIsEncoded`.
- **H6. DWS never answers a WebSocket close frame, so clients see 1006.** Printer. `WebSocketEndpoint.cs:283-287`, `CustomEndpointMiddleware.cs:156-160`. Test: `ClientCloseIsAnswered`.
- **H7. No close message on the WebSocket when DWS stops.** A cancelled task is not faulted (`WebSocketEndpoint.cs:223-227, 240-243`). Test: `ShutdownClosesTheSocket`.
- **H8. rr_ endpoints registered while DWS runs end up at /machine/rr_/{path}.** `ModelObserver.cs:120` vs `:178`. Test: `RrEndpointRegisteredAtRuntime`.
- **H9. rr_connect always reports `sessionTimeout` 8000** instead of the setting (`RepRapFirmwareEndpoints.cs:112`). Test: `RrConnectReportsConfiguredSessionTimeout`.
- **H10. rr_gcode returns `{"bufferSpace":255,"err":0}` instead of `{"buff":N}`.** Printer. RRF: `HttpResponder.cpp:566`. This is why the PollConnector cannot send codes to DWS. Test: `RrGcodeReportsBuff`.
- **H11. rr_disconnect returns 204 instead of `{"err":0}`.** Printer. Test: `RrDisconnectReturnsErr`.
- **H12. Every rr_connect adds another session for the same client.** Printer: 5 → 8. RRF keeps one per IP (`HttpResponder.cpp:803-808`). Test: `RrConnectKeepsOneIpSession`.
- **H13. rr_delete ignores `recursive=yes`** (`RepRapFirmwareEndpoints.cs:354-383`). Fix it only together with H1, or rr_delete gets the same problem. Test: `RrDeleteDeletesRecursively`.
- **H14. DuetHttpClient's RestConnector reconnects with a new session every PingInterval while the model is quiet.** Cancelling the receive for the PING aborts the WebSocket (`RestConnector.cs:294`). Test: `WebServer.DuetHttpClientTests.RestKeepsAQuietWebSocket`.
- **H15. DuetHttpClient's PollConnector never loads keys whose sequence number is still 0** (`PollConnector.cs:320-330`). RRF starts every sequence number at 0 (`RepRap.cpp:450`). Test: `PollLoadsKeysWithSequenceZero`.

Open questions:
- A missing or invalid session key gives 401, in the code and on the printer. The REST API wiki says 403.
- DWS session keys are not bound to the client's IP address, as RRF's are.
- Requests to custom endpoints reach the plugin without a session (SessionId -1), which `ReceivedHttpRequest` documents.

### G-code, macros and jobs (#124)

- **G1. `M122 P{0}` never gets a reply, and the channel stays blocked.** Printer: DWS answered 503, and the next code on HTTP hung until a DCS restart and M999.
  - `MCodeHandler.CodeExecutedAsync` reads P with `GetInt` while P is still an unevaluated expression (`MCodeHandler.cs:1432`). The exception in the Executed stage is only logged (`Executed.cs:76-97`, `PipelineStackItem.cs:85-88`).
  - M122 is unbuffered (`CodeProcessor.cs:188-191`), so every later code waits in `Start.cs:224-232`.
  - Test: `Codes.ExecutedHandlerErrors.DiagnosticsWithExpressionParameter`.
- **G2. M98 with a path that `FilePathResolver` refuses stops DCS.** Affected are paths like `../../x.g`, `0:/../x.g`, or a drive that does not exist.
  - Printer, with `M98 P"99:/macros/…"`:
    - the journal logged `ArgumentException: Invalid drive index` and then `BackgroundServiceExceptionBehavior is configured to StopHost`;
    - systemd killed DCS after the 15 s stop timeout and restarted it;
    - RRF stayed halted until M999.
  - Cause: `Processor.DoMacroFile` calls `ToPhysical` without a try (`Processor.cs:1334`), and nothing in LinkService catches the exception (`LinkService.cs:378-394, 781-796, 176-228`). RRF passes the name on unchanged (`GCodes.cpp:3697-3699`).
  - Test: `Codes.MacroPaths.MacroPathRefusedByResolver`.
- **G3. Drive numbers map to every mounted file system** (open question, upstream design).
  - `PeriodicUpdateService` lists every mount as a volume (`PeriodicUpdateService.cs:526-583`), and `n:/` resolves to `volumes[n].path` (`FilePathResolver.cs:80-119`).
  - On the CHX that gives 25 volumes, e.g. `0:` = `/`, `4:` = `/home`, `5:` = `/var`, `6:` = `/etc/ssh`.
  - File codes can therefore reach any mount, as far as the rights of the dsf user allow.
- **G4. A line feed after the drive prefix truncates the path.** The drive regex has no Singleline option (`FilePathResolver.cs:46`). Test: `File.PathResolution.LineFeedAfterDrive`.
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
- **G9. M32 inside a job still runs the codes that were read ahead.** `JobProcessor.Cancel` (`JobProcessor.cs:315-326`). RRF chains to the new file directly (`GCodes2.cpp:1106`). Test: `SelectFileFromJobStopsFirstJob`.
- **G10. `fileread` differs from RRF's `ReadArrayFromFile`.** RRF: `ExpressionParser.cpp:1192-1358`, `Configuration.h:281`.
  - It allows 50 elements instead of 40.
  - It refuses length 0.
  - It reads no hex values.
  - It accepts `'ab` without the closing quote.
  - It does not check skipped elements.
  - It joins `1 2` into 12.
  - Tests: `Codes.MetaExpressions.FileRead*` (6).

Open questions:
- `fileread` and `echo` file errors reach the client as raw IOException (`Functions.cs:111`, `KeywordHandler.cs:149`).
- `SimpleCode` returns at the first exception while later codes keep running (`SimpleCode.cs:173-201`).
- `FunctionsInitializer` is never registered.
- M36.1 and M36.2 copy file bytes into the JSON unescaped, as RRF does (`RepRap.cpp:1648, 1661`).

### IPC server (#125)

- **I1. `RequiredPermissionsAttribute.Check` grants every permission.**
  - It loops over every value of `SbcPermissions`, including `None = 0`, and `HasFlag(None)` is always true (`RequiredPermissionsAttribute.cs:25-38`).
  - So plugins get every command and the Intercept, Subscribe and CodeStream modes without the permission (`Server.cs:243-261`).
  - Tests: `IPC.RequiredPermissions.RefusedWithoutRequiredFlag`, `IpcPluginPermissions.PluginIsRefusedModesItHasNoPermissionFor`, and in `IpcCommandMode`: `RefusesCommandsWithoutPermission`, `PluginIsRefusedCommandsItHasNoPermissionFor`, `PluginWithoutPermissionsIsRefusedTheCommandMode`.
- **I2. A wildcard filter longer than the code breaks short codes.** Printer.
  - With the filter `M10*`, comments and codes like `G4 P0` fail with `ArgumentOutOfRangeException` (`CodeInterception.cs:242-243`).
  - The interceptor stayed registered after its client had exited, until a code matching the filter came along.
  - Tests: `IpcInterceptionStages.WildcardFilterLongerThanAComment`, `WildcardFilterLongerThanTheCodeOutlivesTheClient`, `IpcInterception.WildcardFilterLongerThanTheCode`.
- **I3. An Executed interceptor with filters and AutoFlush (the DuetAPIClient default) deadlocks its channel for good.**
  - Printer: M115 on Telnet got no reply, even after the client had exited. Nothing was logged, and only a DCS restart helped.
  - Cause: `CodeInterception.cs:263` flushes the channel, and `ChannelProcessor.cs:288` waits for the Executed stage, which is busy with this very code.
  - Tests: `IpcInterceptionStages.ExecutedInterceptorWithAutoFlushBlocksTheChannelAfterTheClientHasGone`, `IpcInterception.ExecutedCodesWithAutoFlush`.
- **I4. Code stream replies come out of order when BufferSize is above 1** (`CodeStream.cs:179-220`). Test: `IpcCodeStreamReplies.RepliesStayInTheOrderOfTheCodes`.
- **I5. A failing code in a code stream cancels the next code, and its reply is lost.** The code object is reused (`CodeStream.cs:169, 212-219`, `CodeProcessor.cs:277-285`). Test: `FailedCodeKeepsTheReplyOfTheNextCode`.
- **I6. `job.layers` still reaches subscribers with API version 10 or older.** `nameof(Layers)` is compared with the JSON name "layers" (`ModelSubscription.cs:495`). Test: `IpcSubscriptionConsistency.JobLayersAreNotSentToOutdatedClients`.
- **I7. A DPS crash during a command closes the client's connection without a response** (`PluginService.cs:279`, `Command.cs:126-130`). Test: `IpcPluginServiceLifecycle.CommandFailsWithAnErrorWhenThePluginServiceDiesWhileRunningIt`.
- **I8. `InvalidateChannel` is not in `Command.SupportedCommands`,** so the DuetAPIClient calls always fail. Test: `IpcClientApi.InvalidateChannel`.
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

### Object model (#125)

- **O1. With a deserialization handler, the two JSON update paths treat a scalar list item differently.** `UpdateFromJson` keeps it, `UpdateFromJsonReader` drops it and shifts the rest (`StaticModelCollection.cs:150-181` vs `220-267`). Test: `Fuzz.ObjectModelJsonFuzz.ScalarListItemIsSkippedTheSameWayByBothUpdateMethods`.
- **O2. A value that does not fit the property type throws FormatException or IndexOutOfRangeException instead of `JsonException`.** `UpdateService.ExecuteAsync` catches only ArgumentException, InvalidOperationException and JsonException (`UpdateService.cs:365-373`), so such a firmware response ends its update loop. Test: `OutOfRangeNumberFailsWithJsonException`.
- **O3. A number beyond the float range is stored as infinity, after which the model cannot be serialized.** GetObjectModel, the subscriptions and DWS then fail. RRF itself reports NaN and infinity as null (`ObjectModel::ReportFloat`). Test: `InfiniteNumberKeepsTheModelSerializable`.
- **O4. A global string with invalid UTF-8 is stored, after which the model cannot be serialized.** RRF copies string bytes as they are (`OutputBuffer::EncodeChar`). Test: `InvalidUtf8KeepsTheModelSerializable`.
- **O5. `UpdateFromJsonReader` stores "[" for an array in a property of type object** and reads the array items as members of the parent (`DuetAPI.SourceGenerators/ObjectModel/ModelObject/UpdateFromJsonReader.cs`). Test: `ObjectPropertyWithArrayIsReadLikeUpdateFromJson`.
