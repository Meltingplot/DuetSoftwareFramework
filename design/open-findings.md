# Open findings

The fork has no issue tracker, so findings to solve later are kept here. Remove an entry when it is solved.

Status: 2026-11-06. Line numbers refer to the Meltingplot fork: DSF `v3.7-dev` at 4c8b3fec and RRF `3.7-dev` at fc939b3.

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

**Next steps:**
1. Prove the cause: send a plain code such as `G4 P1` on the USB channel with no host, and repeat it with a host connected.
2. If it is confirmed, report it to Duet3D with the versions above.
3. Once it is fixed, add `1 => USB` and `7 => USB2` back to the `M98.n` table in `MCodeHandler.cs`.
