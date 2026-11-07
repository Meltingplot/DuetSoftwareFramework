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

## A stuck link floods the journal, so the reason for the DCS restart gets lost

**Problem:** while the SPI link is stuck, DCS logs warnings so fast that journald drops them. The line that names the reason for the restart, `No transfer with RepRapFirmware completed within 60000 ms` (`SPI.cs:311`), is missing afterwards. Whoever reads the journal after a stuck link sees `Lost connection to Duet`, warnings, a gap, and a stop without a reason. The restart itself works.

**Observed** on 2026-11-07 on the lab printer (DSF 3.7.0+mp.12 with the fault injection build from c866895b, RRF 3.7.0+1.mp.7), in `SpiFaultRestarts.StuckLinkRestartsDcs`:
- The fault hit at 07:45:23.753, and systemd reported the stop at 07:46:23.783, 60.03 s later.
- journald reported 1,204,179 suppressed messages (159679, 609061, 435439). The visible lines are `Bad header CRC16` (`SPI.cs:1885`), `Note: RepRapFirmware didn't receive valid data either` (`SPI.cs:1894`) and `Restarting full transfer because the number of maximum retries has been exceeded` (`SPI.cs:2015`). The last visible one is from 07:46:01.
- The limit on the image is `RateLimitInterval=30s`, `RateLimitBurst=2000` (`/etc/systemd/journald.conf.d/persistent.conf`).
- The line is not in `0:/sys/eventlog.log` either.
- This instance of DCS used 53.7 s of CPU time over its 84 s, and the service runs with `CPUSchedulingPolicy=fifo` (`duetcontrolserver.service:15-16`).
- The repeat at 07:53:05 gave the same picture: stop after 60.03 s, 1,192,457 suppressed messages, no such line in the journal.

**Not proven:** that DCS logged the line and journald dropped it. The stop after 60.03 s fits only `SPI.cs:309-311`, and it falls into the time in which journald suppressed messages.

**Open questions:**
1. Should DSF report repeated link warnings once or at a limited rate while the link is stuck? That would also shrink the journal writes on the SD card during the 60 s.
2. Should the reason for the stop reach the journal by another route, for example after DCS has stopped retrying?

`StuckLinkRestartsDcs` therefore measures the stop at the line of systemd.

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

**Not proven:**
- What shifted the two sides by one exchange. The reset hits a running transfer, but this has not been reproduced.
- Whether the silent change of the protocol version keeps the two sides apart.
- Whether the same can happen without a restart. Only this one case was seen.

**Differs from the census:** `ExtendedLinkNoticesEveryRebootQuickly` (`LinkRecovery.cs:409`) expects DSF to notice every restart within 50 ms of the boot. It restarts the fake firmware at every exchange, but this case is not among its results.

**Related:** the same pattern of two sides out of step lasts 60 s in `StuckLinkRestartsDcs`, see the finding above. Here, the retry limit and the reconnect ended it after 23 ms and 500 ms.

**Next steps:**
1. Tim decides whether this is worth a fix, given that the link recovers by itself and it was seen once during a restart.
2. If so, try to reproduce it in the census with a restart inside an exchange, as the lab test cannot force it.
