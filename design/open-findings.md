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
| `M98.3 P"t.g" G7` in a trigger macro | `M98 P"t.g"` on HTTP, `G7` on Trigger |

The separated code runs after the macro has finished (`M98 P"t.g"`, the codes of the macro, then `G7`). `M98.n` splits the same way; the separated code runs on the calling channel while the macro starts on the other one.

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

## Findings of the fuzz and coverage tests (2026-11-07)

The fuzz and coverage tests of #121 to #127 found 48 bugs and deviations, the trigger tests of #129 found 9 more, and the stop path tests of #130 another 4.
- **Fixed and removed from this list (2026-11-08):** H1 (#136), G2 (#132), I1 (#135), T2 (#134), T3 (#133), P1 and P2 (#137), the extended-link part of P4 (#138), H13 (upstream 5130ca43 through #143) and T1 (Meltingplot/RepRapFirmware#43 with #144). The lab printer showed each fix, and the stock build showed the bug (`design/lab-test-plan.md`, section 14, 2026-11-08).
- **The others:** each has an `[Explicit]` test in `src/UnitTests` that reproduces it and fails today. Run one by its exact name, e.g. `dotnet test src/UnitTests/bin/Debug/net10.0/UnitTests.dll --filter "FullyQualifiedName=UnitTests.Codes.TriggerMacros.TriggerChannelWorksAfterInvalidationDuringTriggerRequest"`. Once a finding is fixed, remove the `[Explicit]` of its test and this entry.

- **Line numbers:** DSF `v3.7-dev` at c6d225f2. The production code there is the same as at d576ee9e, where the tests started. Entries added on 2026-11-08 cite DSF at 9e6449d1. RRF `3.7-dev` at cde4e4fa1 (3.7.0+1.mp.8).
- **Printer:** "printer" marks findings that were confirmed on the lab printer on 2026-11-07, with DSF 3.7.0+mp.13 and RRF 3.7.0+1.mp.8.
- **Origin:** git blame puts all of them on upstream code, except the two DuetHttpClient findings in H14 and H15, the fork's extended SPI link in S1 and S5, and the fork's sequence guard in S4.

### Highest priority

1. **I3** An Executed interceptor with filters blocks its channel for good. Printer.
2. **O2** A value of the wrong type in a firmware response ends the object model update loop.
3. **O3** and **O4** An out-of-range float or a global string with invalid UTF-8 makes the object model unserializable, so model queries, subscriptions and DWS fail.
4. **P4** (plain link) A trigger macro can get lost after a reconnect while RRF keeps running. The fix is case 2 of #57, which is on hold.

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

### HTTP: DuetWebServer and DuetHttpClient (#123)

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
- **G3. Drive numbers map to every mounted file system** (open question, upstream design).
  - `PeriodicUpdateService` lists every mount as a volume (`PeriodicUpdateService.cs:526-583`), and `n:/` resolves to `volumes[n].path` (`FilePathResolver.cs:80-119`).
  - On the CHX that gives 25 volumes, e.g. `0:` = `/`, `4:` = `/home`, `5:` = `/var`, `6:` = `/etc/ssh`.
  - File codes can therefore reach any mount, as far as the rights of the dsf user allow.
  - The root check of the H1 fix (#136) refuses only the root of the drive that a path names (`FilePathResolver.cs:64-77`, at 9e6449d1). On the CHX, plugin directories are mounted below `/opt/dsf/sd`. A recursive delete of such a mount, e.g. `DELETE /machine/file/0%3A%2F<mount>?recursive=true`, can therefore still empty it.
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

- **O1. With a deserialization handler, the two JSON update paths treat a scalar list item differently.** `UpdateFromJson` keeps it, `UpdateFromJsonReader` drops it and shifts the rest (`StaticModelCollection.cs:150-181` vs `220-267`). Test: `Fuzz.ObjectModelJsonFuzz.ScalarListItemIsSkippedTheSameWayByBothUpdateMethods`.
- **O2. A value that does not fit the property type throws FormatException or IndexOutOfRangeException instead of `JsonException`.** `UpdateService.ExecuteAsync` catches only ArgumentException, InvalidOperationException and JsonException (`UpdateService.cs:365-373`), so such a firmware response ends its update loop. Test: `OutOfRangeNumberFailsWithJsonException`.
- **O3. A number beyond the float range is stored as infinity, after which the model cannot be serialized.** GetObjectModel, the subscriptions and DWS then fail. RRF itself reports NaN and infinity as null (`ObjectModel::ReportFloat`). Test: `InfiniteNumberKeepsTheModelSerializable`.
- **O4. A global string with invalid UTF-8 is stored, after which the model cannot be serialized.** RRF copies string bytes as they are (`OutputBuffer::EncodeChar`). Test: `InvalidUtf8KeepsTheModelSerializable`.
- **O5. `UpdateFromJsonReader` stores "[" for an array in a property of type object** and reads the array items as members of the parent (`DuetAPI.SourceGenerators/ObjectModel/ModelObject/UpdateFromJsonReader.cs`). Test: `ObjectPropertyWithArrayIsReadLikeUpdateFromJson`.

### Triggers (#129)

How the CHX 350 uses triggers, read from the lab printer:
- Pin triggers `M581 T2` (external E-stop input) and `T4`/`T9` (doors).
- Expression triggers (`M581.1` T3, T5-T8, T10) that RRF evaluates itself.
- Their macros `sys/triggerN.g` run on the Trigger channel, served by DSF. `SbcTriggerService` is not involved there.

Measured in the fake firmware without link faults:
- DSF starts a trigger macro one transfer after the request.
- trigger2.g's M112 reaches RRF 22-24 transfers after the request. That is 18-22 ms on the WSL VM, with a three-code stand-in for set-led-color.g.

- **T11. A case that the fix of T1 (Meltingplot/RepRapFirmware#43) does not cover.** From reading the RRF code, not tested.
  - daemon.g: at the base level of the Daemon channel, RRF requests daemon.g without waiting (`GCodeBuffer.cpp:1258-1260`). If InvalidateChannel arrives before that request has gone out, RRF closes the file but still sends the request.
  - The second case, a resent InvalidateChannel, was seen on the printer and is fixed by #161 (`design/dsf-findings-upstream.md`, T11 resend).
  - The lab test `AbortsCrossingMacroRequests` (#139) ran again on 2026-11-08 22:37 UTC with RRF 3.7.0+1.mp.9-dev.2 (#43, #45): T12 did not occur in 165 aborts, the run stopped at the resend case. It needs a run with #161 and the RRF fix for T10.
- **T13. On the USB link the abort window of #134 also covers data that RRF wrote after it read the InvalidateChannel.** From reading the code, not tested.
  - The window assumes the order of the SPI link: RRF writes the data of a transfer before it reads DSF's packets of that transfer (`Processor.cs`, doc of `_allFilesAbortedSent`). Over USB the firmware sees DSF's data before it composes its response, says the comment of `USB.ResendPacket` (`USB.cs:717-720`); not checked against the USB transport of RRF.
  - So over USB DSF discards a macro request that RRF makes after the abort in the same exchange, e.g. for a trigger. RRF gives it up after `SbcMaxRequestTime` (3 s, `SbcMessageFormats.h:42`), and the trigger macro does not run.
  - The CHX uses the SPI link ("Connected to controller over SPI" in the lab journal).
- **T4. A missing trigger macro is not shown after DSF has run a code on the Trigger channel.** RRF reports it as a pushed code reply (`GCodes.cpp:3703-3706`), DSF takes it as an out-of-order reply and holds it back (`Processor.cs:1122-1127`, `LinkService.cs:727-733, 762-776`). This fits the field journal line "Trigger: Out-of-order reply: 'Macro file trigger8.g not found'", not proven to be the same event. Test: `Codes.TriggerMacros.MissingTriggerMacroAfterCodeOfDsfOnTriggerChannel`.
- **T5. M581.1 with SBC fields succeeds and creates the trigger although its expression fails** (upstream 4229f552). RRF returns an error and creates nothing (`TriggerItem.cpp:328-339`, wiki M581.1). Test: `Codes.SbcTriggerEvaluation.ErrorInExpressionIsReported`.
- **T6. A later evaluation error of such a trigger is only logged, and the trigger stays enabled.** RRF disables it and reports it (`TriggerItem.cpp:133-149`). Test: `ErrorInLaterEvaluationDisablesTheTrigger`.
- **T7. An SBC trigger T0 runs trigger0.g instead of an emergency stop** (`SbcTriggerService.cs:298-308`; wiki M581; `GCodes.cpp:994-998`). By the code, T1 runs trigger1.g instead of pausing; this is not tested. Test: `TriggerZeroIsAnEmergencyStop`.
- **T8. M582 never checks a trigger that DSF evaluates**, because it is passed straight to RRF. The wiki says M582 works for M581.1 triggers. Test: `M582ChecksTriggerOfDsf`.
- **T9. R1 and R2 of SBC triggers count simulating and cancelling as not printing** (`SbcTriggerService.cs:341-344`). RRF's `IsPrinting` counts both as printing (`GCodes.cpp:3993`, `RepRap.cpp:1961-1968`). Test: `EnableConditionWhilePrinting` with Simulating and Cancelling.
- **T10. Cases that the fix of T10 (Meltingplot/RepRapFirmware#46) does not cover.** From reading the RRF code, not tested.
  - The fix (RRF drops the replies of the code that ran when InvalidateChannel arrived) is in `design/dsf-findings-upstream.md`.
  - A first copy of InvalidateChannel that does not get the buffer mutex while the running code does not wait for a macro (yet): the running code is not flagged, and its reply still goes out.
    - Printer, 2026-11-09 06:25:12 and 06:27:29 UTC (RRF 3.7.0+1.mp.9-dev.3, 2000 aborts): both resends of InvalidateChannel were followed within 2 ms by the out-of-order reply "M98: Macro file … not found". The first copy came while the main task ran the M98 but before `RequestMacroFile` set `isWaitingForMacro`, so it flagged nothing, and the resent copy ended the wait without flagging.
    - Effect: an error message in the console. The reply no longer completes the next code, because #161 holds the channel until RRF has processed the abort.
  - Replies that RRF wrote before it read InvalidateChannel are on their way already. DSF logs them as out-of-order replies, since #161 holds the channel back they no longer complete the next code.
  - M291 S2 at the base level: `AbortFile` does nothing there, the message box stays open in RRF, and its reply after the acknowledgement is stale (DSF cancels the M291, `Processor.cs:794-799`).
  - State machines go on after InvalidateChannel (G28 with the next axis, `GCodes4.cpp:358-413`; tool changes) and can request new macros. InvalidateChannel does not call `AbortStateMachine`, as `AbortPrint` does (`GCodes.cpp:3633`).
  - Messages that codes write with their response type outside HandleReply are not dropped: M122 (`GCodes2.cpp:2292`), `GpInPort.cpp:90`, the SD speed test (`GCodes4.cpp:1894`, `:1906`, `GCodes.cpp:6029`).

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
