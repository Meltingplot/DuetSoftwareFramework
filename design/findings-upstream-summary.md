# Findings in upstream DSF and RRF: summary

- **What:** bugs in upstream DuetSoftwareFramework and RepRapFirmware code that the Meltingplot fork found (CHX 350: Duet 3 MB6HC, Raspberry Pi 5 as SBC over SPI), one row per finding. Details: **DU** = `design/dsf-findings-upstream.md`, **LU** = `design/sbc-link-findings-upstream.md`, **OF** = `design/open-findings.md` (fork repository); **new** = results of 2026-11-11, in no document yet.
- **Date and code:** 2026-11-11. Upstream looked at: DSF `v3.7-dev` 973838bf and f76ffc70 (K32), 77684383 (K34); RRF `3.7-dev` 25ec27aec and 231daaac87, `3.7-ch` e7e30e13f3 and 7e95567976 (K34), f8c8a1d69f (USB input files); DuetIAP 9af72da4. Line numbers follow their source; the commit per source is in the Notes.
- **Count:** 153 findings: DSF 116, RRF 13, both sides of the SBC link 24; 19 more under "Origin not checked". 35 have a sample, 68 a reproducer test, 50 neither.
- **Evidence:** `printer` seen on the fork's lab printer (MB6HC + Pi 5); `upstream test` a test on unchanged upstream code (K32 only); `fork test` a test in the fork (unit tests, fake firmware, SPI census against a C# port of RRF); `read` read in the code only. Sample basis: "from test …", "seen on printer" or "derived, not run".
- **Structure:** the ten most important, then every stop and deadlock at a glance, then how to run the tests and the SPI link in numbers, then the tables: the SBC link first (both sides, then the DSF link code), then the rest of DSF and RRF.
- **Order:** by effect (E1 a stop or safety action that does not reach the machine, E2 deadlock, channel or DCS blocked for good, DCS exits, E3 data loss or a wrong action on the machine, E4 wrong result or error, E5 deviation from the documentation, E6 cosmetic), then by reachability (R1 seen on a printer in normal use, R2 normal codes or clients, R3 rare timing or link fault, R4 malformed input or misbehaving client, R5 not shown), then by evidence; an R5 item never ranks above a reachable item of the next lower effect class, and an R2 item that needs a narrow range of values (O3) is ordered after the other R2 items of its effect class.

## Start here: highest impact

| # | ID | Repo | Finding | Eff | Reach | Evidence |
|---|---|---|---|---|---|---|
| 1 | [T7](#t7) | DSF | An SBC-evaluated trigger T0 runs trigger0.g instead of an emergency stop | E1 | R2 | fork test |
| 2 | [K32](#k32) | DSF | An M112 that crosses the end of a macro never reaches RRF; M112 reports success, DCS exits | E1 | R3 | upstream test |
| 3 | [K6](#k6) | DSF | Over USB, M112 can report success although RRF never got the stop | E1 | R3 | read |
| 4 | [G1](#g1) | DSF | `M122 P{0}` never replies and blocks its channel until DCS restart and M999 | E2 | R2 | printer |
| 5 | [I3](#i3) | DSF | An Executed interceptor with filters and AutoFlush blocks its channel for good | E2 | R2 | printer |
| 6 | [O4](#o4) | DSF | A global string with invalid UTF-8 makes the object model unserializable; DWC cannot connect | E2 | R2 | fork test |
| 7 | [O3](#o3) | DSF | A tool offset just below the float maximum is read as infinity; DCS stays up, but GetObjectModel, DWC and every model subscriber fail until the value changes | E2 | R2 (narrow) | printer |
| 8 | [F8](#f8) | DSF | After a lost connection DSF keeps sending its header with a stale checksum, and the link does not recover | E2 | R3 | printer |
| 9 | [F3](#f3) | both | One bit error desynchronises the link phases: livelock until DCS restarts | E2 | R3 | printer |
| 10 | [F9](#f9) | both | A recovered link fault can leave TfrRdy low, so a new DCS fails its first start | E2 | R3 | printer |

## Stops and deadlocks at a glance

Every finding of the two highest effect classes, in the order of the header. The rows link to the tables below. The link faults with a lesser effect are in the SBC link tables, which come first.

### A stop that does not reach the machine (E1, 3 findings)

| ID | Repo | Component | Seen as | Reach | Evidence |
|---|---|---|---|---|---|
| [T7](#t7) | DSF | DuetControlServer / Triggers | no emergency stop | R2 | fork test |
| [K32](#k32) | DSF | DuetControlServer / Link | M112 replies success, status halted, RRF never gets the stop; DCS exits | R3 | upstream test |
| [K6](#k6) | DSF | DuetControlServer / Link | M112 reports success, RRF never got the stop | R3 | read |

### Deadlock, a channel, the link or DCS blocked for good, DCS exits (E2, 32 findings)

| ID | Repo | Component | Seen as | Reach | Evidence |
|---|---|---|---|---|---|
| [G1](#g1) | DSF | DuetControlServer / Codes and files | no reply (DWS 503); the next code on the channel hangs until DCS restart and M999 | R2 | printer |
| [I3](#i3) | DSF | DuetControlServer / IPC | M115 on Telnet never answered, also after the client exited; nothing logged; only a DCS restart helps | R2 | printer |
| [O4](#o4) | DSF | Model | GetObjectModel and new subscriptions fail; DWS closes every new WebSocket with 1001 "DCS has been stopped" (DWC unusable after a reload) until the global changes | R2 | fork test (RRF side read) |
| [O3](#o3) | DSF | Model | DCS alive but unusable: every GetObjectModel fails ("… infinity cannot be written as valid JSON", 424 times in under 3 min), `GET /machine/model` 500, DWC's WebSocket closed again and again, model subscribers end (a plugin restarted 91 times in 5 min); status idle, link up; until the value changes | R2 (narrow) | printer |
| [F8](#f8) | DSF | DuetControlServer / Link | link does not recover, 6,000-10,000 "bad header checksum" lines (4 of 5 watchdog resets) | R3 | printer |
| [F3](#f3) | both | SPI link | link stuck until DCS restarts; about 20,000 warnings per second in the journal | R3 | printer |
| [F9](#f9) | both | SPI link | the new DCS fails its first start ("Connection timeout while waiting for TfrRdy pin") | R3 | printer |
| [T11 (resend)](#t11-resend) | both | Channels, macros and aborts | "Resending packet #0 (request InvalidateChannel)", "… none was requested"; next code never answered; channel blocked until DCS restarts | R3 | printer |
| [F1](#f1) | DSF | DuetControlServer / Link | DCS stops (20 of 4142 census faults) | R3 | fork test |
| [T2](#t2) | DSF | DuetControlServer / Link | "Macro file has been started … but none was requested"; channel blocked until DCS restarts | R3 | fork test |
| [T1](#t1) | RRF | SBC interface | DSF runs a triggerN.g that RRF does not; every later trigger macro is discarded until DCS restarts | R3 | fork test |
| [T11 (daemon.g)](#t11-daemon) | RRF | SBC interface | Daemon channel blocked (43 per 2000 aborts in the fake firmware) | R3 | fork test |
| [P4 (OF)](#of-p4) | both | SPI link | every later trigger is held back while that trigger file stays open | R3 | fork test |
| [S2 (OF)](#of-s2) | both | SPI link | link stuck for good | R3 | fork test |
| [F4](#f4) | both | SPI link | connection resets and livelocks | R3 | fork test |
| [F5](#f5) | both | SPI link | deadlock at connect; needs a reset | R3 | fork test |
| [S6 (OF)](#of-s6) | DSF | DuetControlServer / Link | another packet with the same id is copied, or an ArgumentException ends LinkService and DCS | R3 | read |
| [K5](#k5) | both | Channels, macros and aborts | "Stack underrun"; the link thread and DCS end | R3 | read |
| [K34](#k34) | both | Channels, macros and aborts | a late MacroFileClosed gives "Stack underrun" and ends the link thread; a late ExecuteMacro starts at the base level; a late reply completes a newer code | R3 | read |
| [G2](#g2) | DSF | DuetControlServer / Link | DCS stops; RRF halted until M999 | R4 | printer |
| [O6](#o6) | DSF | Model | the process ends with a stack overflow | R4 | fork test |
| [SU1](#su1) | DSF | Startup, shutdown and logging | DCS does not start ("Failed to initialize environment") | R4 | fork test |
| [SU6](#su6) | DSF | Startup, shutdown and logging | DCS does not start | R4 | fork test |
| [O2 (rest)](#o2-rest) | DSF | Model | its update loop ends | R4 | read |
| [D3](#d3) | DSF | DuetPluginService and plugin handling in DCS | DPS does not start without the plugin directory; `--Key=value` refused; lowercase `logLevel` | R4 | read |
| [K24](#k24) | both | Channels, macros and aborts | DSF resends forever, the channel stays blocked | R4 | read |
| [[USB no host]](#usb-no-host) | RRF | G-code input and expressions | a macro on USB or USB2 never starts, the channel stays busy, DSF reported success | R5 | printer |
| [T12](#t12) | both | Channels, macros and aborts | "Stack underrun on channel Telnet"; DCS exits | R5 | printer |
| [K1](#k1) | DSF | DuetControlServer / Link | link thread deadlocked; RRF declares a link loss after 4 s; DCS hangs | R5 | read |
| [K2](#k2) | DSF | DuetControlServer / Link | joins K1 | R5 | read |
| [K3](#k3) | DSF | DuetControlServer / Link | deadlock, unbounded on the link thread | R5 | read |
| [K13](#k13) | both | Channels, macros and aborts | the channel takes no further text input | R5 | read |

## Running the tests

Many findings have a test in the fork: 68 are named in the "Reproducers" lines under the tables, more in the samples. The tests need no hardware.

- **Where:** repository `Meltingplot/DuetSoftwareFramework`, branch `v3.7-dev`, project `src/UnitTests` (NUnit, .NET 10). The tests run the real DCS, DWS and DPS code against a C# fake firmware (`src/UnitTests/FakeFirmware`) and, for the SPI link, against a C# port of RRF's side of the link (`src/UnitTests/SPI/FakeRrfSpi.cs`).
- **Whole suite:** `dotnet test src/UnitTests/UnitTests.csproj`. About 2500 tests in about 3 minutes.
- **One finding:** the tests of findings that are still open are `[Explicit]` and fail today; run one by its exact name, e.g. `dotnet test src/UnitTests/UnitTests.csproj --filter "FullyQualifiedName=UnitTests.Codes.TriggerMacros.MissingTriggerMacroAfterCodeOfDsfOnTriggerChannel"`.
- **Fuzz tests** (`src/UnitTests/Fuzz`): `SpiLinkFuzz` (two bit errors on the SPI link), `StopPathFuzz` (emergency stop and trigger macros under link faults), `TriggerFuzz` (triggers interleaved with codes, macros, aborts and a job), `PacketReaderFuzz`, `CodeParserFuzz`, `ExpressionFuzz`, `FilePathFuzz`, `GCodeExecutionFuzz`, `GCodeFileFuzz`, `ObjectModelJsonFuzz`. Every run is reproducible: `DSF_FUZZ_SEED` selects the seed (default 1), `DSF_FUZZ_ITERATIONS` the number of inputs per test (default 5000). A failing input is minimized and printed with its seed and iteration. Example: `DSF_FUZZ_SEED=3 DSF_FUZZ_ITERATIONS=50000 dotnet test src/UnitTests/UnitTests.csproj --filter "FullyQualifiedName~UnitTests.Fuzz"`.
- **SPI link:** `SPI.LinkRecovery.SingleCorruptedExchange` is the census of every single corrupted exchange with its outcome; `Fuzz.SpiLinkFuzz` adds a second bit error. `Link.ChannelEpochStress` runs aborts that cross macros, replies and requests.
- **Against RRF's own link code:** `firmware/build.sh harness` (Docker) compiles RRF's `SBC/DataTransfer.cpp` and `Storage/CRC32.cpp` unchanged for the PC, and `LinkRecovery.PortMatchesRealFirmware` replays the census through it; the commands are in `firmware/README.md`.
- **On unchanged upstream code:** the tests are fork-only files and use a few fork-only members of the link adapter. K32 was run on upstream f76ffc70 by copying the fake firmware and the test into `src/UnitTests` and removing the fork's link capabilities and one parameter of `ResendPacket` from the fake firmware; the product code stayed as it was. `design/k32-upstream/reproduce.sh` repeats that run.
- **CI:** `.github/workflows/unit-tests.yml` runs the suite with the fuzz tests for every pull request and push to `v3.7-dev` (seed 1, so that a failure can be reproduced) and once a week with a new seed and more inputs; `rrf-harness.yml` runs the comparison with RRF's link code.

## The SPI link in numbers

How the stock link behaves under faults, measured before the fork changed anything. This is what led to the fork's fixes and to the guards of its negotiated link extension. Source: `design/sbc-link-findings-upstream.md` (LU), which has the method and every case.

**Method.** The single-fault census (`SPI.LinkRecovery.SingleCorruptedExchange`) runs upstream's `SPI.cs` (changed only to take the SPI device and the TfrRdy pin as interfaces) against a C# port of RRF's `DataTransfer` and `SbcInterface`, in virtual time, at DSF `v3.7-dev` 1f72a780 and RRF `3.7-dev` 231daaac87. It injects exactly one fault per run: every bit of every exchange, special values of each field and whole-word patterns, in both directions, in transfers with data on both sides, on one side or on none. A run passes only if the link recovers through the protocol's own retries; a connection reset counts as a failure, because DSF then invalidates everything pending, and so does a stop of DCS. The port was checked against a host build of the real `DataTransfer.cpp` and `SbcInterface.cpp` (0 differences over 4189 replayed cases) and on a Duet 3 MB6HC with a Pi 5 (526 injected faults, every outcome as the census predicted, livelocks included).

**The link is robust where it has a checksum.** Headers and data carry a CRC-32, which detects every error of one or two bits (`Fuzz.SpiLinkFuzz.ChecksumsDetectEveryErrorOfUpToTwoBits`), and the retries repair them.

| Stock link, single faults | Result |
|---|---|
| Faults injected | 4142 |
| Repaired by the protocol's retries | 4017 (97.0 %) |
| Break the link | **125**: connection reset 82, DCS stops 20, livelock 16, DCS has to restart 7 |
| Single-bit errors that break the link, transfer without data | **34 of 320 bits**, all in the 4-byte response to the header; all 32 bits of the response DSF sends |
| Single-bit errors that break the link, transfer with data | 1 to 3 of 720 to 768 bits, all in the header response |
| RRF restarts, 128 | 6 not noticed by DSF (F6) |
| DCS restarts, 42 | 2 need a second start of DCS |
| Faults that are repaired but leave TfrRdy low, of 3234 (F9) | 661: a DCS that starts right after one fails its first start |
| RRF watchdog resets on the MB6HC (F10) | about 40 % take 4 s longer to recover |
| Full transfer restarts in the census (F11) | 2416, of them 1831 with a data retry |

**Where it breaks.** Every failing bit sits in one of the 4-byte response words, which have no checksum, and whose codes are one bit apart (Success 1, BadFormat 2, BadProtocolVersion 3, BadDataLength 4). The risk per transfer is about the bit error rate times the number of fatal bit positions, which is 34 for an empty transfer, so the empty transfers of an idle link dominate.

| What the census showed | Count | Finding | What the fork did |
|---|---|---|---|
| One bit turns Success into a refusal, and DCS stops | 20 stops, 5 DCS restarts at connect | [F1](#f1) | DSF only: a refusal restarts the transfer (#91); extended header: response codes 16 bits apart, up to 3 flipped bits corrected |
| An acknowledgement seen by one side only resets the connection | most of the 82 resets | [F2](#f2) | DSF #92; sequence guard in RRF |
| Phase desync without any timeout: livelock | 16 | [F3](#f3) | DSF #92, #102 (DCS restart after 60 s), #137; phase guards in RRF |
| An exchange replaced by the exact BadResponse word | not counted separately | [F4](#f4) | phase guards in RRF; extended header |
| Deadlock at connect | 1, only without polling while disconnected | [F5](#f5) | open |
| A restart noticed only through sequence numbers | 6 of 128 RRF restarts | [F6](#f6) | extended header: 5-bit sequence counter with an 11-bit session status |

**After the fixes.**
- With the DSF-only fixes, which work with stock RRF, the census goes from 125 to 27 single faults that break the link; 4 of the 27 are livelocks, 2 of them from the exact BadResponse word.
- With the link extension active (phase guards and sequence guard in RRF, the extended header on both sides, switched on per connection by a capability handshake), no single fault after the activation breaks the link in the census, and every RRF and DCS restart is noticed. On the MB6HC, 672 of 672 injected faults and 15 of 15 replayed ESD events were repaired by retries (2026-11-06). A stock RRF or a stock DSF on the other side behaves as today.
- The stop path under faults (`Fuzz.StopPathFuzz`, virtual time): on the extended link an emergency stop of DSF reaches RRF with a median of 0.05 ms and at most 0.25 ms; on the plain link with noise the median is the same, but the worst case was 502 ms, and 5 of 1000 stops took over 100 ms (measured before #137).

**Under random bit errors.** A sweep of the bit error rate on the simulated link, run on 2026-11-11 with the fork's DSF (`v3.7-dev`, with its DSF-only fixes; stock DSF was not swept): 20000 transfers per point with one message in each direction, every bit of every exchange flipped with the given probability, one random seed. "Survives" means no connection reset and no message lost or duplicated.

| Link | Transfer size | Survives up to | First failure at |
|---|---|---|---|
| Extension active | about 850 bits | 1e-3 | 2e-3 (transfer 366) |
| Extension active | about 17,000 bits | 3e-5 | 1e-4 (transfer 17) |
| Plain link, DSF fixes only | about 850 bits | 3e-5 | 1e-4 (transfer 6409) |
| Plain link, DSF fixes only | about 17,000 bits | 1e-6 | 1e-5 (transfer 1689, stuck) |

With the extension the link survived 0.5 and 0.85 flipped bits per transfer on average and failed at 1.7, which is where the retries no longer get a transfer through within the retry limit of 10. The plain link survived a bit error rate about 30 times lower: it held at 0.02 to 0.03 flipped bits per transfer and failed at 0.09 to 0.17, through the response words without a checksum.

The census and the fuzz tests can be run as described under "Running the tests"; `LinkRecovery.TraceCase` prints the exchanges of a single case.

## DSF and RRF (both sides of the SBC link)

### SPI link (RRF DataTransfer ↔ DSF SPI.cs)

| ID | Function / file:line | What goes wrong | Seen as | Reached by | Eff | Reach | Evidence | Fork fix | Detail |
|---|---|---|---|---|---|---|---|---|---|
| <a id="of-p4"></a>P4 (OF) | RRF `GCodes.cpp:423-431`, `:999`; DSF repeats a transfer with the same number after "SPI data transfer failed" | After a reconnect on the plain link a trigger macro gets lost while RRF keeps running; its open trigger file keeps IsTriggerBusy true. | every later trigger is held back while that trigger file stays open | two bit flips | E2 | R3 | fork test | open (case 2 of #57, on hold) | OF Stop path |
| <a id="f3"></a>F3 | DSF `SPI.cs:1663` before `:1675`; case of DU: RRF `DataTransfer.cpp:959-965`, `:1005`, `:1025-1029`, DSF `SPI.cs:1655`, `:1688-1692`, `:1763-1765` | DSF in the header phase reads RRF's data as a header and adopts its protocol version before the CRC check; both sides keep exchanging, so no timeout fires. | link stuck until DCS restarts; about 20,000 warnings per second in the journal | one bit error (16 livelocks in the census; 6 header-response cases on the printer) | E2 | R3 | printer | #102 (DCS restart after 60 s), #137 (header-response case) | LU F3; DU F3 (case) |
| <a id="f9"></a>F9 | RRF `DataTransfer.cpp:353-354`, `:1232-1235`; DSF `SPI.cs:1525-1537`, `Settings.cs:173` | A recovery that arms an odd number of extra exchanges leaves TfrRdy low; a new DCS expects it high and gives up after 500 ms. | the new DCS fails its first start ("Connection timeout while waiting for TfrRdy pin") | DCS start after a recovered fault (661 of 3234 faults) | E2 | R3 | printer | #99 | LU F9 |
| <a id="of-s2"></a>S2 (OF) | RRF `DataTransfer.cpp:1258`, `:1434-1438`, `SbcInterface.cpp:171`; DSF `SPI.cs:1693-1711` | RRF resets the connection without a full reset, so TfrRdy stays low; DSF waits for it to go high, RRF waits without a timeout. | link stuck for good | two bit errors; builds without polling while disconnected (Duet 2 SBC; the MB6HC polls) | E2 | R3 | fork test | open | OF SBC link |
| <a id="f4"></a>F4 | exact BadResponse word 0xFEFEFEFE | That word in place of a header, data or a response sends the two sides down different paths. | connection resets and livelocks | that exact 32-bit pattern (very unlikely) | E2 | R3 | fork test | RRF #35 + #93 (negotiated) | LU F4 |
| <a id="f5"></a>F5 | RRF `SbcInterface.cpp:165` (`TimeoutUnlimited`) | While not connected RRF waits without a timeout with TfrRdy low, and a new DCS waits for TfrRdy high. | deadlock at connect; needs a reset | builds without polling while disconnected | E2 | R3 | fork test | open | LU F5 |
| <a id="f2"></a>F2 | RRF `DataTransfer.cpp:859`, `DataTransfer.h:192-196` | A bit error in DSF's header response makes RRF repeat the transfer while DSF moves on, so RRF sees a sequence mismatch. | connection reset; DSF invalidates everything pending | one bit error in DSF's response (each of its 32 bits) | E3 | R3 | fork test | #92 | LU F2 |
| <a id="f6"></a>F6 | `DataTransfer` constructor, `ResetConnection(true)`; `DataTransfer.cpp:921`, `:872-874` | A restarted RRF numbers from 1; if DSF expects 1, the restart is not noticed. | DSF's packets of that transfer lost, unnoticed by both sides | RRF restart right after the 16-bit wrap (about 1 in 65536) | E3 | R3 | fork test | RRF #35 + #93 (negotiated) | LU F6 |
| <a id="k7"></a>K7 | DSF `SPI.cs:442-477`, `:383-390`; RRF `SbcInterface.cpp:184-186`, `:257-288`, `DataTransfer.cpp:839-853`, `:1396-1443` | After a lost connection DSF sends the same TX again, and RRF processes the next transfer whose number follows, whichever session wrote it. | codes, InvalidateChannel or MacroStarted of the old session run after DSF cancelled them | lost connection | E3 | R3 | read | open | OF v7 contract |
| <a id="of-s3"></a>S3 (OF) | DSF `SPI.cs:1655-1670`, `Writer.cs:729`; RRF `SbcInterface.cpp:1298-1310` | DSF writes a StringHeader before the key, RRF reads the key from the start of the data. | RRF never recognises ObjectModelKeyChanged; `NetworkUpdated()`, `VolumesUpdated()` never called from the SBC | every ObjectModelKeyChanged | E4 | R2 | fork test | open | OF SBC link |
| <a id="f10"></a>F10 | RRF `DataTransfer.cpp:451`, `SbcInterface.cpp:120-121` | After an RRF reset DSF takes the falling TfrRdy edge in the boot window as the signal for its pending exchange. | about 40 % of watchdog recoveries take 4 s longer (a second reset) | RRF reset | E4 | R3 | printer | RRF #36 | LU F10 |
| <a id="of-p2"></a>P2 (OF) | plain link, stop path | Two bit errors delay the stop through RRF's transfer timeout and disconnect. | the stop reaches RRF after about 500 ms | two bit errors on the plain link during M112 | E4 | R3 | fork test | #137 | OF Stop path (text in OF at 384138dd) |
| <a id="of-s8"></a>S8 (OF) | DSF writes the next code before it reads the resend request | A code that RRF refused for lack of buffer space can be overtaken by a later code of its channel. | codes of a channel out of order | not shown | E3 | R5 | read | open | OF SBC link |

Reproducers: P4 (OF) `TriggerMacroRunsOrRrfStops`; F3 `SingleCorruptedExchange` (case: lab F-3 with `--plain-link`); F9 `DcsStartsAfterRecoveredFault`; S2 (OF) `Fuzz.SpiLinkFuzz.StuckWithoutPolling`; F4, F5, F2 `SingleCorruptedExchange`; F6 `RrfRestart`; S3 (OF) `SPI.LinkServiceOverSpi.ObjectModelKeyChangedIsReadByRrf`; F10 `ExtendedLinkNoticesEveryRebootQuickly`; P2 (OF) `SPI.StopPath.EmergencyStopReachesRrfWithin100Ms`.
Neither: K7, S8 (OF).

### Channels, macros and aborts (RRF SbcInterface ↔ DSF Processor and LinkService)

| ID | Function / file:line | What goes wrong | Seen as | Reached by | Eff | Reach | Evidence | Fork fix | Detail |
|---|---|---|---|---|---|---|---|---|---|
| <a id="t11-resend"></a>T11 (resend) | RRF `GCodes.cpp:540`, `GCodeBuffer.cpp:1252`, `:1263`, `SbcInterface.cpp:45`, `:857`, `:860`, `:1300`; DSF `Processor.cs:867-870`, `SPI.cs:737` | During a macro wait RRF cannot lock the buffer within 10 ms and asks for InvalidateChannel again; DSF has gone on, so the copy drops later codes or ends a later macro wait. | "Resending packet #0 (request InvalidateChannel)", "… none was requested"; next code never answered; channel blocked until DCS restarts | abort while an M98 waits for its macro | E2 | R3 | printer | #161 | DU T11 |
| <a id="k5"></a>K5 | DSF `Processor.cs:358-364`, `:1587-1609`, `LinkService.cs:466` (@412e5ff6) | A MacroFileClosed set just before a link loss is written in the first exchange of the new connection, and DSF pops its base level. | "Stack underrun"; the link thread and DCS end | a macro closes in the last exchange before a link loss (e.g. a DCS restart) | E2 | R3 | read | RRF #47 | OF v7 contract |
| <a id="k34"></a>K34 | DSF `MCodeHandler.cs:104-120`, `:123-126`, `LinkInterface.cs:573-582`, `Processor.cs:1750-1763` (@412e5ff6, upstream 77684383); RRF `GCodes3.cpp:1414-1432` (7e95567976) | The cancel of a held job pops DSF's job channels with PrintPaused before RRF has done anything, and no InvalidateChannel draws a boundary. | a late MacroFileClosed gives "Stack underrun" and ends the link thread; a late ExecuteMacro starts at the base level; a late reply completes a newer code | M0/M1/M2 from another channel while the job is held (RRF feed hold, 3.7-ch e7e30e13f3), with packets on their way | E2 | R3 | read | open | OF epoch reviews |
| <a id="k24"></a>K24 | RRF `SbcMessageFormats.h:31`, `SbcInterface.cpp:358-364`; DSF `Settings.cs:245-248` | RRF refuses a Code over 384 bytes on every copy without an error; DSF allows a larger `MaxCodeBufferSize`. | DSF resends forever, the channel stays blocked | `MaxCodeBufferSize` above 384 in config.json | E2 | R4 | read | open | OF v7 contract |
| <a id="k18"></a>K18 | DSF `Processor.cs:914-915` (@412e5ff6) | Replies of a code that RRF still ran can follow RRF's own untagged abort of all files. | they complete DSF's next code or are logged as out of order | RRF aborts all files while a code runs | E3 | R3 | read | #167 + RRF #47 (both sides) | OF v7 contract |
| <a id="k17"></a>K17 | DSF `Processor.cs:1383-1388`; RRF `SbcInterface.cpp:1389`, `:1514-1518`, `DataTransfer.cpp:1491-1496`, `:1545-1549` | DSF takes `BufferedCodes[0]` as the requester, but RRF can write the smaller ExecuteMacro while an earlier reply does not fit. | the macro is attributed to the wrong code | 19 to 31 free bytes (narrow window) | E3 | R3 | read | open | OF v7 contract |
| <a id="k22"></a>K22 | RRF `SbcInterface.cpp:1145-1285`, `:2331-2336`; DSF `LinkService.cs:1152-1494` | File results carry no id; after RRF's 3 s timeout a late result answers the next request. | wrong file result | slow file system (DSF serves file requests on the link thread) | E3 | R3 | read | open | OF v7 contract |
| <a id="k23"></a>K23 | RRF `GCodeBuffer.cpp:1263-1274`, `SbcInterface.cpp:850-853`, `:383`, `:567-576` | After RRF's 3 s macro timeout, late answers act on the current level. | "none was requested"; late codes run at the caller's level; DSF stays one level deeper | macro start slower than 3 s | E3 | R3 | read | open | OF v7 contract |
| <a id="k4"></a>K4 | RRF `SbcInterface.cpp:326-330`; DSF `LinkService.cs:611-701` | Unknown request types are dropped without an error: RRF ends its read loop, DSF's switch has no default. | the rest of the transfer, codes included, is neither processed nor refused | unknown request type (32 or more, not 0x4D50) | E3 | R4 | read | open | OF v7 contract |
| <a id="t12"></a>T12 | DSF `Processor.AbortAllFilesAsync` `:722`, `:770`, `:1218-1232`; RRF `SbcInterface.cpp:865-866`, `:1450`, `GCodes.cpp:722` | The abort pops all macro files at once, while RRF still reports a macro that it closed just before. | "Stack underrun on channel Telnet"; DCS exits | `abort` while RRF closes a macro on the same channel; not shown upstream (printer case used the IPC command) | E2 | R5 | printer | #158 + RRF #45 | DU T12 |
| <a id="k13"></a>K13 | DSF `Executed.cs:170-230` | DSF answers a DoCode only when it has a result; RRF's GB stays "executing on SBC" without a timeout. | the channel takes no further text input | not shown (which cancellations reach it is not listed) | E2 | R5 | read | RRF #47 (abort, link loss); rest open | OF v7 contract (and its correction) |

Reproducers: T11 (resend) `TriggerFuzz.CodesAfterAnAbortWaitUntilRrfHasProcessedIt`, `MacroRequestBetweenTwoCopiesOfAnAbortIsDiscarded`; T12 `TriggerFuzz.DcsKeepsRunningWhenAnAbortCrossesAMacroFileClosal`.
Neither: K5, K34, K24, K18, K17, K22, K23, K4, K13.

## DSF

### DuetControlServer / Link (LinkService, channel Processor, SPI and USB adapters, LinkInterface)

| ID | Function / file:line | What goes wrong | Seen as | Reached by | Eff | Reach | Evidence | Fork fix | Detail |
|---|---|---|---|---|---|---|---|---|---|
| <a id="k32"></a>K32 | `LinkService.Execute` `:266-276`, `:358-376`; `Processor.cs:1228-1243`, `Pop` `:176-181` (@f76ffc70) | In the cycle of an M112 DSF pops all stacks and writes the stop, then processes RRF's MacroFileClosed; `Pop` throws and ends the link thread before the stop is transferred. | M112 replies success, status halted, RRF never gets the stop; DCS exits | M112 while a macro ends on any channel (one-transfer window) | E1 | R3 | upstream test | #170 | DU K32 |
| <a id="k6"></a>K6 | `USB.cs:363-404`, `:270`; `LinkService.cs:454-456`, `:599` | A failed USB transfer returns an empty RX, so the halt of that TX completes and the reconnect drops it. | M112 reports success, RRF never got the stop | failed transfer during M112, SBC over USB | E1 | R3 | read | open | OF v7 contract |
| <a id="f8"></a>F8 | `SPI.cs:332` | After a lost connection DSF resets `_txHeader.ProtocolVersion` but does not rewrite the header buffer and its CRC. | link does not recover, 6,000-10,000 "bad header checksum" lines (4 of 5 watchdog resets) | lost connection after a version was adopted from a bad header (F3) | E2 | R3 | printer | #59 | LU F8 |
| <a id="f1"></a>F1 | `SPI.ExchangeHeader` `SPI.cs:1757-1762`; `TransferResponse.cs:14-44` | The response codes are one bit apart, so one flipped bit turns Success into a refusal and DSF throws "RepRapFirmware refused …". | DCS stops (20 of 4142 census faults) | one bit error in a header response | E2 | R3 | fork test | #91 | LU F1 |
| <a id="t2"></a>T2 | `Processor.cs:870`; `DoMacroFile` `:1270-1310` | DSF clears `_allFilesAborted` once InvalidateChannel is written and starts a macro request that RRF wrote before it read the abort. | "Macro file has been started … but none was requested"; channel blocked until DCS restarts | abort right after RRF requested the macro of an M98 | E2 | R3 | fork test | #134 | DU T2 |
| <a id="of-s6"></a>S6 (OF) | `USB.ResendPacket` `USB.cs:717-721`, `:746`; `LinkService.cs:446-450` | Over USB DSF looks for a packet that RRF asks for again in the previous TX buffer, but RRF answers one transfer behind. | another packet with the same id is copied, or an ArgumentException ends LinkService and DCS | resend request over USB | E2 | R3 | read | open | OF SBC link |
| <a id="g2"></a>G2 | `Processor.DoMacroFile` `Processor.cs:1309`; `FilePathResolver.cs:69`, `:91`, `:94`; `LinkService.cs:369-375` | `ToPhysical` throws, outside any try, for a path that leaves its volume or names an unknown drive; the packet loop catches only ArgumentOutOfRangeException. | DCS stops; RRF halted until M999 | M98 with such a path | E2 | R4 | printer | #132 | DU G2 |
| <a id="t3"></a>T3 | `LinkInterface.cs:40-46`, `:564`; `Processor.GetMaxBufferSpacePerChannel` `:1012-1014` | The limit is per channel, so codes buffered ahead on all channels together fill RRF's single 4096-byte code buffer. | the codes of a macro that RRF starts on another channel, e.g. a trigger macro, wait until the buffer drains (on the printer the M98 of a trigger macro and the code after it did not go out: 0 runs in 5 s) | a waiting job plus daemon.g reading ahead | E3 | R2 | printer | #133 | DU T3 |
| <a id="k15"></a>K15 | `Processor.cs:1260`; RRF `GCodes2.cpp:182-186`, `:2272-2274`, `GCodes7.cpp:101-115` | DSF ignores a second WaitForAcknowledgement on a channel that already waits. | its MessageAcknowledged pops the first box | M120 and M291 S2 while a box is open | E3 | R2 | read | open | OF v7 contract |
| <a id="k19"></a>K19 | `Processor.cs:1449-1485`, `:1536`; doc `DuetAPI/Commands/Generic/InvalidateChannel.cs:5-7` | InvalidateChannel and `abort` cancel only buffered and Firmware-stage codes; base-level codes in the Start to Post stages go on. | codes still run after an abort (the doc says "all pending codes") | abort while codes are in the early stages | E3 | R3 | read | open | OF v7 contract |
| <a id="k1"></a>K1 | `MCodeHandler.cs:245-252`, `:508-515`; `JobProcessor.cs:356`, `:825-846`; `LinkService.cs:403-410`, `:907`; `Processor.cs:753-758` | M23/M32/M37 hold the job lock while they wait for SetPrintFileInfo, which only the link thread writes; the link thread takes the job lock for PrintPaused or AbortFile. | link thread deadlocked; RRF declares a link loss after 4 s; DCS hangs | not shown | E2 | R5 | read | open | OF v7 contract |
| <a id="k2"></a>K2 | `LinkInterface.cs:557-563`; `Processor.cs:828`; `Manager.cs:80` | `AbortAllAsync` holds the channel lock while it waits for the job lock, and the link thread waits for that channel lock. | joins K1 | not shown | E2 | R5 | read | open | OF v7 contract |
| <a id="k3"></a>K3 | `MCodeHandler.cs:1133-1135`, `JobProcessor.cs:397`, `LinkInterface.cs:315-324` vs `LinkService.cs:880`, `Processor.cs:755` | M606 S1 takes the job lock before the channel locks; the link thread and `AbortAllAsync` take them the other way round. | deadlock, unbounded on the link thread | not shown (likelier: IPC InvalidateChannel of File/File2 during the fork) | E2 | R5 | read | open | OF v7 contract |
| <a id="t4"></a>T4 | `Processor.cs:1122-1127`; `LinkService.cs:727-733`, `:762-776`; RRF `GCodes.cpp:3703-3706` | RRF reports a missing trigger macro as a pushed code reply, which DSF takes as an out-of-order reply and holds back. | "Macro file triggerN.g not found" is not shown | missing trigger macro after DSF ran a code of its own on the Trigger channel | E4 | R2 | fork test | open | OF Triggers |
| <a id="k8"></a>K8 | `KeywordHandler.cs:170`; `LinkInterface.cs:557-563`; `MacroFile.cs:340`, `:236`; `Processor.cs:778-823` | `abort` in a macro passes the macro's token, which the abort cancels, so the wait releases the channel lock while the abort still pops levels. | the abort runs without the lock, concurrently with the link thread | `abort` inside a macro | E4 | R2 | read | open | OF v7 contract |
| <a id="of-p3"></a>P3 (OF) | LinkService, completion of the halt | LinkService completes the halt on RRF's Success for the data; DSF's own last Success is never acknowledged. | M112 replies up to 0.05 ms before RRF has processed the stop | one bit flip on the plain link | E4 | R3 | fork test | open | OF Stop path |
| <a id="k9"></a>K9 | `LinkService.cs:985`, `:993`, `:1161`, `:1169`, `:1193`; `Processor.cs:1368`, `:1393` | The link thread ignores whether file results, FileChunk or the refusal of a system macro were written. | RRF reports "Timeout while …" after 3 s | full TX, or a resend of the same type pending | E4 | R3 | read | open | OF v7 contract |
| <a id="k10"></a>K10 | `SPI.cs:1666-1683`; `LinkService.cs:498-513` | SetLastCodeResult has no resend guard and completes when written; if RRF refuses it, the next code is written after it. | the next code runs with the old `result` | RRF's GB mutex busy | E4 | R3 | read | open | OF v7 contract |
| <a id="k11"></a>K11 | `LinkService.cs:837`, `:850-864`, `:897`, `:906-916` (@412e5ff6) | `_partialLogMessage` and `_partialGenericMessage` are not cleared by an invalidation. | partial message prepended to the next one | invalidation while such a message is split | E4 | R3 | read | #167 for code replies; rest open | OF v7 contract |
| <a id="k14"></a>K14 | `Processor.cs:212-215`, `:848`; `SPI.cs:821-856`; RRF `GCodes2.cpp:5017`, `:5112-5116` | DSF fails a dropped M505 lock request but still copies its Lock packet when RRF asks again; the later Locked finds no request and no Unlock follows. | RRF keeps the movement lock until a code of that GB ends | pop or abort of an M505 level plus a resend | E4 | R3 | read | open | OF v7 contract |
| <a id="k28"></a>K28 | `Processor.cs:1781-1784`, `:375-378`; `MCodeHandler.cs:957-990` (@412e5ff6) | At an abort DSF resolves a queued Unlock false and never writes it. | RRF keeps the movement lock until the GB's next code ends | abort while the Unlock of M505 is queued | E4 | R3 | read | RRF #47 (idle GB); rest open | OF v7 contract |
| <a id="f7"></a>F7 | `ReadNextPacket` `SPI.cs:378`, `USB.cs:439-442` | Packets are read from the whole receive buffer, so a length past this transfer's data takes bytes of an earlier one. | packet assembled from leftover bytes | packet length past the end of the transfer | E4 | R4 | fork test | #89 | LU F7 |
| <a id="lu-p2"></a>P2 (LU) | `DuetControlServer/Link/Protocol/Reader.cs` | An array result is allocated before its item count is checked against the packet; a malformed DateTime throws FormatException. | huge allocations; the packet dump is skipped | malformed packet | E4 | R4 | fork test | #85 | LU parsers table |
| <a id="f11"></a>F11 | `SPI.cs:1830-1833`, `:1838-1850` | After its own bad data CRC32 DSF takes RRF's Success as unexpected and restarts the whole transfer. | full transfer restart instead of a data retry; no other outcome | bit error only in RRF's data to DSF | E6 | R3 | printer | #105 | LU F11 |

Samples:
- **G2** · seen on printer (2026-11-11) · expected: error "Macro file … not found" · actual: DCS stops, RRF halted until M999
  ```
  M98 P"99:/macros/x.g"
  ```

Reproducers: T3 `TriggerMacros.TriggerMacroGetsSpareCodeBufferRoom` (lab `CodeBufferReserve`); K32 `SPI.EmergencyStopCrossingMacroEnd.EmergencyStopWhileTheClosalOfAMacroIsPending`; F1 `SingleCorruptedExchange`; T2 `TriggerFuzz.ChannelWorksAfterAbortCrossingMacroRequest`; T4 `Codes.TriggerMacros.MissingTriggerMacroAfterCodeOfDsfOnTriggerChannel`; P3 (OF) `M112RepliesOnlyOnceRrfIsStopped`; F11 `BadDataOnlyDsfGotIsExchangedAgain`.
Neither: K6, F8, S6 (OF), K15, K19, K1, K2, K3, K8, K9, K10, K11, K14, K28, F7, P2 (LU).

### DuetControlServer / Codes and files (handlers, meta keywords, jobs, paths)

| ID | Function / file:line | What goes wrong | Seen as | Reached by | Eff | Reach | Evidence | Fork fix | Detail |
|---|---|---|---|---|---|---|---|---|---|
| <a id="g1"></a>G1 | `MCodeHandler.CodeExecutedAsync` `:1432`; `Executed.cs:76-97`; `PipelineStackItem.cs:85-88` | M122 reads P with GetInt while P is still an unevaluated expression; the exception in the Executed stage is only logged, and M122 is unbuffered. | no reply (DWS 503); the next code on the channel hangs until DCS restart and M999 | `M122 P{0}` | E2 | R2 | printer | open (the fork's #149 turns the hang into an error reply; RRF's report is still lost) | OF G-code |
| <a id="h1"></a>H1 | `FilePathResolver.CombineContained` `:67`; deletes in `MachineEndpoints.cs:362-366`, `RepRapFirmwareEndpoints.cs:362-366`, `MCodeHandler.cs:817-823`, `LinkService.cs:1087-1090`; RRF `MassStorage.cpp:822-836` | CombineContained accepts the drive root and every delete passes it to `Directory.Delete(path, recursive)`; in SBC mode RRF hands the delete over before its root check. | 204, the whole virtual SD card deleted, config.g included | recursive delete of `0:/`, `0:`, `/`, `0:/sys/..` (HTTP, rr_delete, M472) | E3 | R2 | fork test | #136, RRF #44 | DU H1 |
| <a id="g7"></a>G7 | `FileInfoParser.cs:931-944` | The seek to the end is skipped for files shorter than 64 bytes. | the M37 simulation overwrites the start of the file | M37 P on a file under 64 bytes | E3 | R2 | fork test | open | OF G-code |
| <a id="g9"></a>G9 | `JobProcessor.Cancel` `JobProcessor.cs:315-326` | M32 inside a job closes the first job but still runs the codes read ahead; RRF chains to the new file (`GCodes2.cpp:1106`). | lines after M32 of the first file run | M32 in a job file | E3 | R2 | fork test | open | OF G-code |
| <a id="k25"></a>K25 | `MCodeHandler.cs:798-805`; `CodeFile.cs:651-671`; RRF `SbcInterface.cpp:1131-1140`, `Platform.cpp:2503-2524` | M260/M261 with V delete a stale variable without a flush, so the deletion can reach RRF while an earlier code of the level (e.g. an M98) still runs. | a variable of the macro is deleted; M261 then fails with "variable '…' already exists" | M260/M261 V after an M98 in the same file | E3 | R2 | read | open | OF v7 contract |
| <a id="g10"></a>G10 | DSF `fileread` vs RRF `ExpressionParser.cpp:1192-1358` | `fileread` allows 50 elements instead of 40, refuses length 0, reads no hex, accepts `'ab`, does not check skipped elements and joins `1 2` into 12. | other results than RRF | `fileread` | E4 | R2 | printer | open | OF G-code |
| <a id="g3"></a>G3 | `PeriodicUpdateService.cs:526-583`; `FilePathResolver.cs:80-119` | Every mount is listed as a volume and `n:/` resolves to `volumes[n].path` (open question, upstream design). | file codes reach any mount the dsf user may write (25 volumes on the lab printer) | file codes with a drive number | E4 | R2 | read | open | OF G-code |
| <a id="k26"></a>K26 | `Code.cs:266-272`, `:276-281`, `:286-293`; `Pre.cs:30-35`; RRF `GCodes.cpp:4432-4438` | SetLastCodeResult of a code that DSF resolves itself is sent without a flush, so an earlier code still running in RRF overwrites `lastResult`. | `result` shows the earlier code's result | e.g. M122 "DSF" while a move or an M98 still runs | E4 | R2 | read | open | OF v7 contract |
| <a id="new-s5"></a>S5 (new) | `KeywordHandler.cs:227` | DSF accepts variable names with Unicode letters (`char.IsLetterOrDigit`). | RRF expressions cannot reference them | `var` or `global` with a non-ASCII letter | E4 | R2 | printer | open | new |
| <a id="new-s6"></a>S6 (new) | `MetaExpressionParser.cs:261-267`, `:829-836`, `:1093-1097` | DSF's evaluator counts UTF-16 units, RRF counts bytes, so one expression gives different results depending on who evaluates it. | `echo #"é"` prints 1, `var n = #"é"` stores 2 | string length or index on non-ASCII text | E4 | R2 | printer | open (planned) | new |
| <a id="k27"></a>K27 | `KeywordHandler.cs:244-248`, `:272-276`, `:279`; `LinkService.cs:551-558` | `var` completes the deletion of a stale variable when it is written and queues the creation, which RRF can accept before the copy of a refused deletion. | "variable '…' already exists" | redeclared name while RRF's GB mutex is busy | E4 | R3 | read | open | OF v7 contract |
| <a id="g11"></a>G11 | `MCodeHandler.cs:419`, `:850`, `:907` (@9e6449d1) | M30, M470 and M472 resolve their path before their try. | an exception instead of an error reply ("Failed to execute code … on internal processing stage") | a path that DSF refuses | E4 | R4 | printer | open | OF G-code |
| <a id="g4"></a>G4 | `FilePathResolver.cs:46` | The drive regex has no Singleline option. | a line feed after the drive prefix truncates the path | path with a line feed | E4 | R4 | fork test | open | OF G-code |
| <a id="g5"></a>G5 | `MetaExpressionParser.cs:1345`, `:1379` | Number parsing throws FormatException or OverflowException instead of CodeParserException. | raw exception to the client | `1e`, `2E-`, `0x` with more than 16 digits | E4 | R4 | printer | open | OF G-code |
| <a id="g6"></a>G6 | `FileInfoParser.cs:149`, `:205`, `:221`; `JobProcessor.cs:311` | FileInfoParser throws on one malformed line; RRF scans for keys and catches errors. | the file cannot be printed with M32; M36 returns `err:1` (unterminated string, `0.5 E2`) or an error reply (`G1 Z`) | unterminated string, `0.5 E2` or `G1 Z` in a file | E4 | R4 | printer | open | OF G-code |
| <a id="g8"></a>G8 | `JobProcessor.cs:507` | `readCode` is null, so `file.LineNumber` is used. | a read error names the next line | read error in a job file | E4 | R4 | fork test | open | OF G-code |
| <a id="g13"></a>G13 | `Parameter.cs`; RRF `NumericConverter.cpp:244`, `SafeStrtod.cpp:96-117` | RRF reads `S31e9` as 31 and clamps `S99999999999`; DSF refuses both for an int parameter. | an error where RRF accepts the code | integer parameter with exponent or out of range | E4 | R4 | read | open | OF G-code |

Samples:
- **G1** · seen on printer (2026-11-11); run against the fake firmware at fea02d3a, the parent of fork commit 472daabd (#149) · expected: the M122 report (as `M122 P0`) · actual: no reply, and the next code on the channel hangs until DCS restart and M999; other channels go on. Since #149 the fork completes the code with the exception instead: `Error: Cannot convert P parameter to Int32 (value {0})`, and RRF's report is lost. Upstream still hangs (`PipelineStackItem.cs:85-88`).
  ```
  M122 P{0}
  ```
- **G7** · seen on printer (2026-11-11); run against the fake firmware at a99c81f1 with `job.lastDuration` 3600 · expected: the file keeps its line and the comment is appended · actual: the file holds only the comment afterwards. With a dwell of one hour the file was 33 bytes: a UTF-8 byte order mark, a line feed, `; Simulated print time: 3600` and a line feed; the dwell line was gone.
  - DSF writes `"\n; Simulated print time: <s>"` plus a line feed through a `StreamWriter` with `Encoding.UTF8` (`Files/Parser/FileInfoParser.cs:887`, `:947-950`).
  - For a file under 64 bytes the seek to the end is skipped (`:930-944`). The write then starts at offset 0, and only there does the writer put the byte order mark in front.
  - The written block is 29 bytes plus the digits, so 33 bytes for 3600 s. A shorter file holds only the block afterwards.
  - A file of 33 to 63 bytes keeps its length: its first 33 bytes are replaced and its tail stays (run with a 42-byte file).
  - A file of 64 bytes or more gets the comment appended at the end, without a byte order mark.
  - The file must simulate for at least 1 s: DSF writes the time only if `job.lastDuration` is above 0 (`JobProcessor.cs:812`), and a dwell counts as simulated time (RRF `GCodes.cpp:4058-4063`, `:4995`).
  - With a file of only `M117 "a"`, or a file that was already overwritten, the simulation takes 0 s and nothing is written: "Failed to update simulation time because it was not set in the object model".
  ```
  ; 0:/gcodes/short.g holds one line: G4 S3600
  M37 P"0:/gcodes/short.g"
  ; then download 0:/gcodes/short.g and look at its content
  ```
- **G9** · run against the fake firmware at a99c81f1 (test `Codes.JobFiles.SelectFileFromJobStopsFirstJob`, M117 instead of its G1); RRF side read · expected: `M117 "2"` does not run (RRF chains, `GCodes2.cpp:1105-1106`) · actual: it runs. RRF got `M117 "1"`, `M32 "0:/gcodes/b.g"`, `M117 "2"`, `M117 "3"`, `M400`. In SBC mode RRF's M32 only starts the print (`GCodes2.cpp:1129-1162`, `GCodes.cpp:3952-4022`) and runs what DSF sends next. Needs a real job: in a simulated file DSF passes M32 on without selecting the file (`MCodeHandler.cs:76-80`). Each print start runs `sys/start.g` (`GCodes.cpp:4019-4022`).
  ```
  ; 0:/gcodes/a.g:  M117 "1"  /  M32 "0:/gcodes/b.g"  /  M117 "2"
  ; 0:/gcodes/b.g:  M117 "3"
  M32 "0:/gcodes/a.g"
  ```
- **G10** · seen on printer (2026-11-11); DSF side from test `Codes.MetaExpressions.FileReadSpaceInsideNumber`; run at a99c81f1: DSF sends RRF the expression `{12,}`; RRF side read · expected: the command is aborted (wiki `Gcode_meta_commands.md:499`: an element that does not conform) · actual (read): `{12}`. RRF evaluates the one-element array `{12,}` (wiki `:397-401`) and returns it as JSON `[12]` (`SbcInterface.cpp:792-805`); DSF prints the array with braces (`Expressions.cs:609`).
  ```
  ; 0:/sys/data.csv holds: 1 2
  echo fileread("0:/sys/data.csv", 0, 1, ',')
  ```
- **S5 (new)** · seen on printer (2026-11-11); DSF side run at a99c81f1; RRF side read · expected: one rule for variable names on both sides. Standalone RRF refuses `var ä = 0` with "expected a new variable name" (`StringParser.cpp:717-721`). · actual: DSF accepts `var ä` and RRF stores it, but `G4 P{var.ä}` fails with "Error: G4: unknown variable ''". RRF's identifier ends at the first byte of "ä" (`ExpressionParser.cpp:1763`, `:2501`).
  ```
  var ä = 0
  G4 P{var.ä}
  ```
- **S6 (new)** · seen on printer (2026-11-11); derived, not run · expected: 1 in both cases (wiki `Gcode_meta_commands.md:419`: number of characters) · actual (read): 1 from DSF, 2 from RRF
  ```
  echo #"é"        ; DSF evaluates
  var n = #"é"     ; RRF evaluates
  echo var.n
  ```
- **G11** · seen on printer (2026-11-11); run at a99c81f1 · expected: an error reply like DSF's other M472 errors, e.g. `Error: M472: Failed to delete file or directory …` (`MCodeHandler.cs:939`, `:957`; prefix added in `Executed.cs:103-106`), and no error in the journal · actual: the bare exception message `Error: Invalid drive index` without `M472:`, and the journal shows "Failed to execute code M472 P"99:/x" on internal processing stage" with the ArgumentException (`ProcessInternally.cs:36`). Needs a drive number that is not configured.
  ```
  M472 P"99:/x"
  ```
- **G5** · seen on printer (2026-11-11); from test `Machine.MetaExpressionParserErrors.ExponentWithoutDigits`; run at a99c81f1 · expected: a parse error reply · actual: FormatException. POST /machine/code (DWC) answers 500 "The input string '1e' was not in a correct format.", and DCS logs "Failed to execute code echo 1e on internal processing stage System.FormatException".
  ```
  echo 1e
  ```
- **G6** · seen on printer (2026-11-11); run at a99c81f1; content from test `File.InfoParserErrors.MalformedLine` · expected: file info with `err:0`, because RRF scans for keys and catches such lines · actual: `{"err":1,"fileName":"0:/gcodes/m.g"}`. With `G1 Z` as the line, M36 returns `Error: M36: Cannot convert n/a parameter to Single (value )` instead, because the catch of M36 leaves that exception out.
  ```
  ; 0:/gcodes/m.g holds one line whose quote is not closed: M117 "unterminated
  M36 "0:/gcodes/m.g"
  ```

Reproducers: H1 `SdRootDeletionTests.RootCannotBeDeleted` (no sample: it deletes the SD card); G4 `File.PathResolution.LineFeedAfterDrive`; G8 `ErrorInJobNamesTheFailingLine`.
Neither: K25 (needs an I2C device), G3, K26, K27, G13.

### DuetControlServer / Triggers (SbcTriggerService: M581.1 with `sbc.*` fields)

| ID | Function / file:line | What goes wrong | Seen as | Reached by | Eff | Reach | Evidence | Fork fix | Detail |
|---|---|---|---|---|---|---|---|---|---|
| <a id="t7"></a>T7 | `SbcTriggerService.cs:298-308` | Trigger T0 runs trigger0.g instead of an emergency stop (wiki M581; RRF `GCodes.cpp:994-998`); by the code T1 runs trigger1.g instead of pausing (not tested). | no emergency stop | M581.1 T0 with SBC fields | E1 | R2 | fork test | open | OF Triggers |
| <a id="t5"></a>T5 | M581.1 with SBC fields (upstream 4229f552); RRF `TriggerItem.cpp:328-339` | M581.1 succeeds and creates the trigger although its expression fails. | no error; trigger created | M581.1 with a failing expression | E4 | R2 | printer | open | OF Triggers |
| <a id="t6"></a>T6 | SBC trigger evaluation; RRF `TriggerItem.cpp:133-149` | A later evaluation error is only logged and the trigger stays enabled; RRF disables and reports it. | error only in the log | expression that fails later | E4 | R2 | printer | open | OF Triggers |
| <a id="t8"></a>T8 | M582 (no case in `MCodeHandler.cs`) | M582 goes to RRF, which does not know a trigger that DSF evaluates; the wiki says M582 works for M581.1 triggers. | M582 does nothing | M582 for such a trigger | E4 | R2 | printer | open | OF Triggers |
| <a id="t9"></a>T9 | `SbcTriggerService.cs:341-344`; RRF `GCodes.cpp:3993`, `RepRap.cpp:1961-1968` | R1 and R2 count simulating and cancelling as not printing; RRF's IsPrinting counts both as printing. | wrong enable condition | R1/R2 trigger while simulating or cancelling | E4 | R2 | printer | open | OF Triggers |

Samples:
- **T7** · from test `Codes.SbcTriggerEvaluation.TriggerZeroIsAnEmergencyStop`; this variant run at a99c81f1 · expected: emergency stop · actual: trigger0.g runs, and `echo global.t0ran` gives 1. Before registering, DSF sends `M581 T0 P-1` to RRF (`SbcTriggerService.cs:147-153` at f76ffc70), which deletes a trigger 0 that RRF evaluates, such as an emergency stop input (upstream RRF `TriggerItem.cpp:136-146`). Do not try this on a machine whose trigger 0 is in use.
  ```
  ; 0:/sys/trigger0.g holds: set global.t0ran = 1
  global t0ran = 0
  global t7 = 0
  M581.1 T0 P"sbc.cpu.avgLoad >= 0 && global.t7 == 1"
  set global.t7 = 1
  echo global.t0ran
  ```
- **T5** · seen on printer (2026-11-11); from test `ErrorInExpressionIsReported` · expected: error, no trigger (wiki M581.1) · actual: success, trigger 20 created
  ```
  M581.1 T20 P"sbc.cpu.noSuchField == 1"
  ```
- **T6** · seen on printer (2026-11-11); from test `ErrorInLaterEvaluationDisablesTheTrigger`; this variant run at a99c81f1 · expected: error to all channels, trigger disabled · actual: only a journal warning "Failed to evaluate SBC trigger expression …". `M581.1 T20` still answers "Trigger 20 fires when expression {global.t6 == 0 || sbc.cpu.noSuchField == 1} becomes true".
  ```
  global t6 = 0
  M581.1 T20 P"global.t6 == 0 || sbc.cpu.noSuchField == 1"
  set global.t6 = 1
  M581.1 T20
  ```
- **T8** · seen on printer (2026-11-11); from test `M582ChecksTriggerOfDsf`; this variant run at a99c81f1 · expected: trigger20.g runs, because the expression is true · actual: nothing runs; `echo global.t20ran` gives 0. M582 goes to RRF, whose slot 20 DSF has cleared.
  ```
  ; 0:/sys/trigger20.g holds: set global.t20ran = 1
  global t20ran = 0
  global t8 = 1
  M581.1 T20 P"sbc.cpu.avgLoad >= 0 && global.t8 == 1"
  M582 T20
  echo global.t20ran
  ```
- **T9** · seen on printer (2026-11-11); run at a99c81f1 with RRF's `state.status` "simulating" (`RepRap.cpp:1966-1967`) · expected: fires during the simulation, because RRF's R1 counts a simulation as printing (`GCodes.cpp:3993`) · actual: does not fire (`SbcTriggerService.cs:341-344`); `echo global.t9` gives 0. With `M581.1 T20 R0`, and also with R2, the same run fires and gives 1. Not run: whether a simulation of this length lasts long enough on a printer for DSF to see the status; the R0 run is the control.
  ```
  ; 0:/sys/trigger20.g holds: set global.t9 = global.t9 + 1
  ; 0:/gcodes/t9.g holds two lines: while iterations < 1000  /    G4 P1
  global t9 = 0
  M581.1 T20 P"sbc.cpu.avgLoad >= 0 && state.status == ""simulating""" R1
  M37 P"0:/gcodes/t9.g" F0
  echo global.t9
  ```

### DuetControlServer / IPC

| ID | Function / file:line | What goes wrong | Seen as | Reached by | Eff | Reach | Evidence | Fork fix | Detail |
|---|---|---|---|---|---|---|---|---|---|
| <a id="i3"></a>I3 | `CodeInterception.cs:263`; `ChannelProcessor.FlushAsync` `ChannelProcessor.cs:288` | The interceptor's flush waits for the Executed stage, which is busy with this very code. | M115 on Telnet never answered, also after the client exited; nothing logged; only a DCS restart helps | Executed interceptor with filters and AutoFlush (DuetAPIClient default) | E2 | R2 | printer | #181 | OF IPC (fixed list of #181) |
| <a id="i5"></a>I5 | `CodeStream.cs:169`, `:212-219`; `CodeProcessor.cs:277-285` | The code object is reused, so a failing code in a code stream cancels the next code. | next code cancelled, its reply lost | failing code in a code stream | E3 | R2 | fork test | open | OF IPC |
| <a id="i2"></a>I2 | `CodeInterception.cs:242-243` | A wildcard filter longer than the code throws; the interceptor stays registered after its client exited until a matching code comes. | comments and codes like `G4 P0` fail with ArgumentOutOfRangeException | filter `M10*` | E4 | R2 | printer | open | OF IPC |
| <a id="i8"></a>I8 | `Command.SupportedCommands` `IPC/Processors/Command.cs:23-56`; `IPC/Connection.cs:655` | InvalidateChannel is missing from the supported commands, which the Intercept mode uses as well. | "unsupported command InvalidateChannel"; `InvalidateChannel(Async)` of DuetAPIClient always fail | any client calling it | E4 | R2 | printer | #158 | DU I8 |
| <a id="i4"></a>I4 | `CodeStream.cs:179-220` | Replies are not kept in the order of the codes. | replies out of order | code stream with BufferSize above 1 | E4 | R2 | fork test | open | OF IPC |
| <a id="i6"></a>I6 | `ModelSubscription.cs:495` | `nameof(Layers)` is compared with the JSON name "layers". | `job.layers` reaches clients of API version 10 or older | old subscriber | E4 | R2 | fork test | open | OF IPC |
| <a id="i10"></a>I10 | Intercept mode | A failing command while intercepting ends the connection instead of returning an error response. | connection closed | failing command in Intercept mode | E4 | R2 | fork test | open | OF IPC |
| <a id="i15"></a>I15 | subscriptions | Subscribers of SBC, Daemon, Trigger and other non-generic channels never get the messages for their channel. | messages missing | subscription with such a channel | E4 | R2 | fork test | open | OF IPC |
| <a id="i7"></a>I7 | `PluginService.cs:279`; `Command.cs:126-130` | A DPS crash during a command closes the client's connection without a response. | no response | DPS crash | E4 | R3 | fork test | open | OF IPC |
| <a id="i12"></a>I12 | plugin service | The plugins of a terminated plugin service keep Pid 0 (being shut down) instead of -1 (not started). | wrong plugin state | DPS terminates | E4 | R3 | fork test | open | OF IPC |
| <a id="i13"></a>I13 | StopPlugins | StopPlugins starts with the processor's token, which is already cancelled. | remaining plugins not stopped | DPS terminates | E4 | R3 | fork test | open | OF IPC |
| <a id="i14"></a>I14 | subscriptions | A subscription stays registered if its success response cannot be sent. | stale subscription | client gone before the response | E4 | R3 | fork test | open | OF IPC |
| <a id="i1"></a>I1 | `RequiredPermissionsAttribute.Check` `DuetAPI/Utility/RequiredPermissionsAttribute.cs:25-39`; `SbcPermissions.cs:17` | `Check` also tests `None = 0`, and `HasFlag(None)` is always true. | DCS never refuses a command or connection mode to a plugin (`ipc.md:39-41` says otherwise) | plugin using commands outside its manifest | E4 | R4 | fork test | #135 | DU I1 |
| <a id="i9"></a>I9 | code stream constructor | A code stream refused for its buffer size stays registered. | stale stream | invalid buffer size | E4 | R4 | fork test | open | OF IPC |
| <a id="i11"></a>I11 | Intercept mode | An undefined interception mode is acknowledged and then ends with KeyNotFoundException. | connection error | undefined mode | E4 | R4 | fork test | open | OF IPC |

Samples (DuetAPIClient, C#):
- **I3** · seen on printer (2026-11-11); run at 01ea6897, the parent of #181 · expected: M115 answered · actual: no reply, also after the client exited. Without a channel list the interceptor covers every channel, so every M115 blocks its channel.
  ```
  var c = new InterceptConnection();
  await c.ConnectAsync(InterceptionMode.Executed, filters: ["M115"]);   // AutoFlush stays true
  // then send M115 on Telnet
  ```
- **I2** · seen on printer (2026-11-11); run at a99c81f1 · expected: `G4 P0` runs · actual: `Error: Index and length must refer to a location within the string. (Parameter 'length')` (ArgumentOutOfRangeException in `CodeInterception.cs:242-243`). Every code whose short string is shorter than 3 characters fails on every channel: G0-G4, M0-M9, T0-T9 and comments. This continues after the client exits, until a code that matches the filter is sent. Send the code only after the interceptor is registered, which takes a moment after ConnectAsync.
  ```
  var c = new InterceptConnection();
  await c.ConnectAsync(InterceptionMode.Pre, filters: ["M10*"]);
  // then send G4 P0 on HTTP
  ```
- **I8** · seen on printer (2026-11-11); from test `IpcClientApi.InvalidateChannel` · expected: success · actual: "unsupported command InvalidateChannel"
  ```
  var c = new CommandConnection();
  await c.ConnectAsync();
  await c.InvalidateChannelAsync(CodeChannel.Telnet);
  ```

Reproducers: I5 `FailedCodeKeepsTheReplyOfTheNextCode`; I4 `IpcCodeStreamReplies.RepliesStayInTheOrderOfTheCodes`; I6 `IpcSubscriptionConsistency.JobLayersAreNotSentToOutdatedClients`; I10 `IpcInterception.FailingCommandWhileInterceptingIsReported`; I15 `IpcSubscription.TargetedMessagesReachSubscribersOfOtherChannels`; I7 `IpcPluginServiceLifecycle.CommandFailsWithAnErrorWhenThePluginServiceDiesWhileRunningIt`; I12 `IpcPluginService.PluginsOfATerminatedServiceAreNotStarted`; I13 `IpcPluginService.RemainingPluginsAreStoppedWhenAServiceTerminates`; I14 `IpcSubscription.SubscriptionIsUnregisteredWhenTheClientIsGoneBeforeTheResponse`; I1 `IPC/RequiredPermissions.cs`, `IPC/IpcPluginPermissions.cs`; I9 `IpcCodeStream.RefusedStreamIsNotRegistered`; I11 `IpcInterception.RefusesUndefinedInterceptionMode`.

### Model (DCS update service, DuetAPI object model)

| ID | Function / file:line | What goes wrong | Seen as | Reached by | Eff | Reach | Evidence | Fork fix | Detail |
|---|---|---|---|---|---|---|---|---|---|
| <a id="o4"></a>O4 | `JsonModelDictionaryConverter.Write` `DuetAPI/ObjectModel/Base/JsonModelDictionary.cs:384` | A `global` (or plugin data) string that is not valid UTF-8 is stored unchecked; `GetRawText()` then throws, so the whole model cannot be serialized. | GetObjectModel and new subscriptions fail; DWS closes every new WebSocket with 1001 "DCS has been stopped" (DWC unusable after a reload) until the global changes | valid codes, because RRF cuts strings by bytes: `"é"[0]`, `take("é",1)`, `^` beyond 255 bytes | E2 | R2 | printer | open (in work) | OF Object model; new |
| <a id="o3"></a>O3 | generated float and double updates (DuetAPI.SourceGenerators) | A number beyond the float range is read as infinity and stored, after which the model cannot be serialized. | DCS alive but unusable: every GetObjectModel fails ("… infinity cannot be written as valid JSON", 424 times in under 3 min), `GET /machine/model` 500, DWC's WebSocket closed again and again, model subscribers end (a plugin restarted 91 times in 5 min); status idle, link up; until the value changes | normal codes, narrow value range: a result in the top 0.01 % below the float maximum that RRF reports with 3 decimals, e.g. a computed tool offset; an overflow to infinity is reported as `null` and does not trigger it; otherwise PatchObjectModel | E2 | R2 (narrow) | printer | #184 | OF Object model (fixed list of #184); lab run 2026-11-11 (new) |
| <a id="o6"></a>O6 | `UpdateFromJsonReader` (Kinematics, FilamentMonitor, DirectDisplayScreen) | An object with two different type keys recurses without end. | the process ends with a stack overflow | e.g. two `name` keys in `move.kinematics` | E2 | R4 | fork test | #177 | OF fuzz fixed list |
| <a id="o2-rest"></a>O2 (rest) | `UpdateService.ExecuteAsync` `UpdateService.cs:373-376` | UpdateService logs a JsonException and throws it again. | its update loop ends | a value that fails and is not skipped, e.g. a list item (O1 (OF)) | E2 | R4 | read | open | OF fuzz fixed list (O2) |
| <a id="lu-o1"></a>O1 (LU) | `LinkService.cs:1432`, `:1442`; `LinkInterface.cs:645`; `ObjectModel.cs:869`; `ModelQueryRequest.cs:30`; `UpdateService.cs:252`, `:374` | After a reset the cancelled model query can start the next update before `ConnectionLost` clears the sequence numbers, so the verbose fields are not read. | `boards[0]` firmware and IAP file names empty; M997 refuses, `-u` skips the main board | RRF reset (9 of 10 watchdog resets on the printer) | E4 | R3 | printer | #115 | LU O1 |
| <a id="o2"></a>O2 | generated updates (DuetAPI.SourceGenerators) | A value that does not fit the property type throws FormatException or IndexOutOfRangeException instead of JsonException. | wrong exception; DCS's skip handler is not used | value of the wrong type or range | E4 | R4 | fork test | #176 | OF fuzz fixed list |
| <a id="of-o1"></a>O1 (OF) | `StaticModelCollection.cs:150-181` vs `:220-267` | With a deserialization handler, UpdateFromJson keeps a scalar list item, UpdateFromJsonReader drops it and shifts the rest. | list items shifted | invalid list item | E4 | R4 | fork test | open | OF Object model |
| <a id="o5"></a>O5 | `UpdateFromJsonReader.cs` (DuetAPI.SourceGenerators) | An array or object in a property of type object is stored as "[" or read as members of the parent. | e.g. `state.messageBox.default` set to a copy of the box ends as null | array or object value in such a property | E4 | R4 | fork test | open | OF Object model |
| <a id="o7"></a>O7 | type key handling (FilamentMonitor, DirectDisplayScreen) | A type key in another case or given as its number got the base class while `Type`/`Controller` got the value. | own properties missing; a model made from its JSON differs | type key in another case or as a number | E4 | R4 | fork test | #179 | OF fuzz fixed list |
| <a id="o4-side"></a>[O4 side bug] | `ObjectModel` DeserializationFailedHandler `DuetControlServer/Model/ObjectModel.cs:184` | The handler logs `e.JsonValue.GetRawText()`, which throws for invalid UTF-8, inside an exception filter. | the first such value per type escapes ("Failed to merge JSON due to internal error"), later ones are skipped silently | value with invalid UTF-8 that fails to deserialize | E4 | R4 | fork test | open (with O4) | new |

Samples:
- **O4** · seen on printer (2026-11-11); DSF side run at 14cd4690, the parent of #185; RRF side read · needs a DSF without #185, e.g. 3.7.0+mp.13. If `global.c` exists, use `set global.c = "é"[0]`. · expected: DWC loads after a reload · actual: every new WebSocket is closed with 1001 "DCS has been stopped", and GET /machine/model gives 500 "The object or value could not be serialized. Path: $.Global.", until the global changes (`set global.c = ""`).
  ```
  global c = "é"[0]     ; or: global t = take("é", 1)
  ; then reload DWC
  ```
- **O3** · seen on printer (2026-11-11) · expected: the model stays serializable · actual: `M409 K"tools[0].offsets"` gives `[3.403e+38,0,0,0]` (RRF prints `%.3g` of large values as `%.3e`, `RRFLibraries SafeVsnprintf.cpp:479-490`), which .NET reads as infinity; every model request fails until `G10 P0 X0`, after which the model is served again within a second. Realistic because a macro can compute a tool offset in RRF and set it; the literal `G10 P0 X3.4026e38` should behave the same (derived, not run). Limit: only a result between about 3.4025e38 and the float maximum 3.4028235e38 triggers it; an overflow gives infinity, which RRF reports as `null` (`ObjectModel.cpp:1634-1648`). Needs tool 0, otherwise RRF answers `Error: G10: Invalid tool number` (`GCodes.cpp:4085-4088`). Sets an absurd tool offset, and if T0 is selected an absurd user position (`GCodes.cpp:4113`, read): do not try it on a machine that moves afterwards, and restore the offset that was set before (`echo tools[0].offsets[0]`) rather than 0. Reproducers: `Fuzz.ObjectModelJsonFuzz.ValueThatDoesNotFitFailsWithJsonException` (case "float that RRF rounds beyond the range"), `InfiniteNumberKeepsTheModelSerializable` (#184).
  ```
  G10 P0 X{1.7013e38 * 2}      ; sent as an expression (Writer.cs:228), RRF evaluates it as a 32-bit float (BinaryParser.cpp:205-209)
  M409 K"tools[0].offsets"
  G10 P0 X0                    ; restores the model (use the offset that was set before)
  ```

Reproducers: O1 (LU) `VerboseFieldsAfterReset`; O1 (OF) `Fuzz.ObjectModelJsonFuzz.ScalarListItemIsSkippedTheSameWayByBothUpdateMethods`; O5 `ObjectPropertyWithArrayIsReadLikeUpdateFromJson`.
Neither: O6, O2 (rest), O2, O7, [O4 side bug].

### DuetWebServer

| ID | Function / file:line | What goes wrong | Seen as | Reached by | Eff | Reach | Evidence | Fork fix | Detail |
|---|---|---|---|---|---|---|---|---|---|
| <a id="h3"></a>H3 | `RepRapFirmwareEndpoints.cs:261-285` | rr_upload writes into the target; RRF and DWS's own PUT use a `.part` file. | a failed upload deletes the file it was meant to replace | rr_upload with a wrong crc32 | E3 | R3 | printer | open | OF HTTP |
| <a id="h2"></a>H2 | `SessionStorage.cs:404`, `:420-422`; DCS `Settings.cs:116` | Each expiry opens its own IPC connection; the DCS backlog is 4 and there is no retry. | expired sessions stay in DCS ("Failed to unregister … (11) Resource temporarily unavailable") | many sessions expiring at once (40 logins left 4) | E4 | R2 | printer | open | OF HTTP |
| <a id="h5"></a>H5 | `MachineEndpoints.cs:294`, `:504` | `UrlPathEncode` stops at `?`, so the Location header throws. | unhandled 500, the item is created anyway | PUT /machine/directory or /machine/file with `?` and a non-ASCII character | E4 | R2 | printer | open | OF HTTP |
| <a id="h6"></a>H6 | `WebSocketEndpoint.cs:283-287`; `CustomEndpointMiddleware.cs:156-160` | DWS never answers a WebSocket close frame. | clients see 1006 | client closes the WebSocket | E4 | R2 | printer | open | OF HTTP |
| <a id="h10"></a>H10 | rr_gcode | Returns `{"bufferSpace":255,"err":0}` instead of `{"buff":N}` (RRF `HttpResponder.cpp:566`). | the PollConnector cannot send codes to DWS | rr_gcode | E4 | R2 | printer | open | OF HTTP |
| <a id="h11"></a>H11 | rr_disconnect | Returns 204 instead of `{"err":0}`. | clients expecting RRF's reply | rr_disconnect | E4 | R2 | printer | open | OF HTTP |
| <a id="h7"></a>H7 | `WebSocketEndpoint.cs:223-227`, `:240-243` | A cancelled task is not faulted. | no close message on the WebSocket when DWS stops | DWS stop | E4 | R2 | fork test | open | OF HTTP |
| <a id="h8"></a>H8 | `ModelObserver.cs:120` vs `:178` | rr_ endpoints registered while DWS runs end up at `/machine/rr_/{path}`. | endpoint at the wrong URL | plugin registers an rr_ endpoint at run time | E4 | R2 | fork test | open | OF HTTP |
| <a id="h9"></a>H9 | `RepRapFirmwareEndpoints.cs:112` | rr_connect always reports `sessionTimeout` 8000 instead of the setting. | clients that rely on it lose their session | rr_connect | E4 | R2 | printer | open | OF HTTP |
| <a id="h4"></a>H4 | `MachineEndpoints.cs:397` | `ReadFormAsync` is outside the try. | unhandled 500 | POST /machine/file/move with an invalid form | E4 | R4 | printer | open | OF HTTP |
| <a id="h12"></a>H12 | rr_connect (`RepRapFirmwareEndpoints.cs:88-89`) | Every rr_connect adds another IP-based session; RRF does not register a second IP-based session for a client (`HttpResponder.cpp:803-808`). This may be intended. | one more session per call in the session list (5 → 8 on the printer) | repeated rr_connect from one client | E5 | R2 | printer | open | OF HTTP |

Samples (with a session; `{key}` = the session key):
- **H3** · seen on printer (2026-11-11); run at a99c81f1 · expected: `0:/gcodes/keep.g` unchanged, `{"err":1}` · actual: `{"err":1}`, the file is gone, and `GET /rr_upload` gives `{"err":1}`
  ```
  ; 0:/gcodes/keep.g exists, e.g. holding: original
  POST /rr_upload?name=0:/gcodes/keep.g&crc32=00000000    body: replacement   (its CRC32 is 54d5f44a)
  ```
- **H5** · seen on printer (2026-11-11) · expected: 201 · actual: 500, directory created
  ```
  PUT /machine/directory/0%3A%2Fgcodes%2Fwhat%3F%C3%A4     (X-Session-Key: {key})
  ```
- **H10** · seen on printer (2026-11-11) · expected: `{"buff":N}` · actual: `{"bufferSpace":255,"err":0}`
  ```
  GET /rr_gcode?gcode=M115
  ```
- **H11** · seen on printer (2026-11-11) · expected: 200 `{"err":0}` · actual: 204
  ```
  GET /rr_disconnect
  ```
- **H12** · seen on printer (2026-11-11); run at a99c81f1 · RRF: a client keeps one IP-based session (`HttpResponder.cpp:803-808`) · DSF: one more session per call. With a password set, three calls give 3 more entries in `sbc.dsf.userSessions` with the client's IP; with no password 4, because the authentication handler adds a session for the IP before rr_connect adds its own (`SessionKeyAuthenticationHandler.cs`, `RepRapFirmwareEndpoints.cs:88-89`). Later requests from that IP without a key refresh only its oldest session (`SessionStorage.GetTicketFromIpAddress`). The others expire 8 to 9 s after they were created (SessionTimeout 8000 ms, checked once per second). `rr_disconnect` removes only the session the request was authenticated with. Possibly intended.
  ```
  GET /rr_connect?password=secret    (three times from the same client within 8 s)
  GET /rr_model?key=sbc.dsf.userSessions
  ```
- **H9** · seen on printer (2026-11-11); from test `WebServer.WebServerFindings.RrConnectReportsConfiguredSessionTimeout` · needs `SessionTimeout` in `/opt/dsf/conf/http.json` set to anything but 8000, e.g. 3000, and a DWS restart; 8000 is the default of DWS (`DuetWebServer/Settings.cs:46`), and with it the reply is right. `password=secret` works only with no password set or the password "secret". · expected: the configured SessionTimeout · actual: 8000
  ```
  GET /rr_connect?password=secret
  ```
- **H4** · seen on printer (2026-11-11); run at a99c81f1 · expected: an error status · actual: unhandled 500 with an empty body; Kestrel logs "InvalidOperationException: Incorrect Content-Type: text/plain…". The same happens with no Content-Type. curl's `--data` sends a form type by itself, so the header must be set.
  ```
  POST /machine/file/move    Content-Type: text/plain    body "from=a&to=b"    (X-Session-Key: {key})
  ```

Reproducers: H2 `WebServer.WebServerFindings.ExpiredSessionsAreRemovedFromDcs`; H6 `ClientCloseIsAnswered`; H7 `ShutdownClosesTheSocket`; H8 `RrEndpointRegisteredAtRuntime`.

### DuetPluginService and plugin handling in DCS

| ID | Function / file:line | What goes wrong | Seen as | Reached by | Eff | Reach | Evidence | Fork fix | Detail |
|---|---|---|---|---|---|---|---|---|---|
| <a id="d3"></a>D3 | DPS `Program.cs:84`, `:69`; `PluginService.cs:39` | DPS has the startup deviations that SU1, SU3 and SU6 fixed in DCS. | DPS does not start without the plugin directory; `--Key=value` refused; lowercase `logLevel` | install without the directory, and the cases of SU1/SU3 | E2 | R4 | read | open | OF FW/plugin/startup; DU SU1, SU6 |
| <a id="ps3"></a>PS3 | DPS `InstallPlugin.cs:83-89` | For a manifest without id, `Path.Combine(PluginDirectory, plugin.Id)` is the plugin directory, which is deleted recursively. | whole plugin directory deleted | DPS directly (DCS refuses such a manifest); ReloadPlugin accepts it | E3 | R4 | fork test | #151 | DU PS3 |
| <a id="ps4"></a>PS4 | DPS `UninstallPlugin.cs:95-128`, `:67` | `DwcFiles` and `SdFiles` are deleted without a check that they stay inside 0:/www and 0:/. | files outside deleted, as root for root plugins | manifest with paths that leave the directory | E3 | R4 | fork test | #151 | DU PS4 |
| <a id="ps5"></a>PS5 | DPS `InstallPlugin.cs:84`, `:162-167`, `:185` | An upgrade keeps the old directory; the zip-slip check is lexical and `FileMode.Create` follows a symlink left there. | the upgrade writes through symlinks | plugin leaves a symlink, then is upgraded | E3 | R4 | fork test | #151 | DU PS5 |
| <a id="ps6"></a>PS6 | DPS `Settings.cs:112-117`, `plugins.json:22-23`, `InstallPlugin.cs:300-306` | Python plugins start through `bash -c "… {command} {args}"`; the check refuses only `"`, `$`, `` ` ``, `\` and line breaks. | `;`, `\|`, `&`, `<`, `>` in the arguments are run by bash | manifest with such arguments | E3 | R4 | fork test | #151 | DU PS6 |
| <a id="ps9"></a>PS9 | DPS `StartPlugin.cs:113-116`, `:149-158`, `StopPlugin.cs:57-77`, `UninstallPlugin.cs:72`, `IPC/CommandService.cs:47-73`; DCS `IPC/Processors/PluginService.cs:221` | Six smaller bugs: a failed SetPluginProcess leaves the process registered, DPS runs on without DCS, children outlive StopPlugin, uninstall without manifest throws, a closed service stays registered, a receive failure is logged against the previous command. | e.g. a plugin that never starts again; children keep running | see the DU table | E4 | R2 | fork test | #151 | DU PS9 |
| <a id="ps1"></a>PS1 | DCS `Commands/Plugins/InstallPlugin.cs:60`, `PeriodicUpdateService.cs:94-111`, `StartPlugin.cs:140`; DPS `StartPlugin.cs:58` | Only installation checks RootPluginSupport; load, reload and start do not. | a superUser manifest in the plugin directory runs as root at the next start | manifest written by the dsf user | E4 | R4 | fork test | #151 | DU PS1 |
| <a id="ps2"></a>PS2 | DCS `InstallPlugin.cs:199-202`; DPS `InstallPlugin.cs:295-298`; `PeriodicUpdateService.cs:94-111` | The reserved ServicePlugins permission is refused only at installation. | a manifest on disk gets ServicePlugins | manifest written by the dsf user | E4 | R4 | fork test | #151 | DU PS2 |
| <a id="ps7"></a>PS7 | DPS `StartPlugin.cs:84-87`, `:94-100` | An absolute `SbcExecutable` passes `Path.Combine` unchanged, and `LinkTarget` checks only the last path component. | the symlink check meant to keep plugins in their AppArmor profile is bypassed | absolute executable or symlinked `dsf` directory | E4 | R4 | fork test | #151 | DU PS7 |
| <a id="ps10"></a>PS10 | DPS `InstallPlugin.cs:338`, `:375` | `EnvironmentVariables.Add` throws when DPS already has the variable (the default holds DEBIAN_FRONTEND). | package installation of a plugin fails | DPS started with DEBIAN_FRONTEND set | E4 | R4 | fork test | #153 | DU PS10 |
| <a id="ps8"></a>PS8 | duetcontrolserver `postinst:19-21` | The whole plugin tree belongs to dsf, and the regular DPS writes the files of root plugins too. | the dsf user can change what the root service runs | anything running as dsf | E4 | R4 | read | #151 (design) | DU PS8 |

Reproducers: PS3 `DpsInstallPlugin.ManifestWithoutIdIsRefused`; PS4 `DpsUninstallReloadPlugin.UninstallDeletesOnlyFilesInsideTheSdDirectory`; PS5 `DpsInstallPlugin.UpgradeDoesNotWriteThroughSymlinks`; PS6 `DpsStartStopPlugin.PythonPluginArgumentsDoNotRunShellCommands`; PS9 the six tests in the DU table; PS1 `DpsThroughDcs.RootPluginIsNotStartedWithoutRootPluginSupport`; PS2 `DpsUninstallReloadPlugin.ReservedPermissionIsNotLoadedAtStart`; PS7 `DpsStartStopPlugin.ExecutableOutsideThePluginDirectoryIsNotStarted`; PS10 `DpsInstallPlugin.VariableOfTheServiceIsOverridden`.
Neither: D3, PS8.

### DuetAPI (code parser, parameters)

| ID | Function / file:line | What goes wrong | Seen as | Reached by | Eff | Reach | Evidence | Fork fix | Detail |
|---|---|---|---|---|---|---|---|---|---|
| <a id="lu-p1"></a>P1 (LU) | `Code.Parse` (`DuetAPI/Commands/Code/Parser.cs`) | It reads the first character of a following code instead of peeking, which a TextReader cannot undo (regression since 721e5df0). | file info fails for a footer with several codes on a line | several codes on one line | E4 | R2 | fork test | #84 | LU parsers table |
| <a id="lu-p3"></a>P3 (LU) | `Parser.cs`, `ParserAsync.cs` | `Code.Length` is one byte too long for a code followed by another on its line and for the last code of a file without a final newline. | JobProcessor's fallback pause position is off | pause when RRF reports no position | E4 | R2 | fork test | #86 | LU parsers table |
| <a id="g12"></a>G12 | `DuetAPI/Commands/Code/Parameter.cs:309-311` | The int, uint and long conversions call `Convert` for every float, which throws OverflowException. | OverflowException instead of an error reply | out-of-range number for an integer parameter | E4 | R4 | printer | #162 | DU G12 |
| <a id="macro-line"></a>[macro line] | `StartsNextCode` `DuetAPI/Commands/Code/Parser.cs:727-730`; RRF `StringParser.cpp:1118-1166` | A G or M code after a macro call on the same line runs after the macro, and DSF splits after `'G` where RRF does not. | the following code runs (wiki `Gcodes.md:235`: it is not executed) | M98 with a code after it on the line | E5 | R2 | printer | open | OF "Codes after a macro call on the same line still run" |

Samples:
- **G12** · seen on printer (2026-11-11); from test `Codes.FileCodes.ListFilesWithTypeOutOfRangeIsAnError` · expected: "Error: M20: …" · actual: `System.OverflowException`
  ```
  M20 S31e9 P"0:/macros"
  ```
- **[macro line]** · seen on printer (2026-11-11); run against the fake firmware at a99c81f1 (RRF executes what DSF sends: `M98 P"0:/macros/empty.g"`, then `M117 "after"`) · expected: `M117 "after"` not executed · actual: it runs after the macro
  ```
  ; 0:/macros/empty.g is empty
  M98 P"0:/macros/empty.g" M117 "after"
  ```

Neither: P1 (LU), P3 (LU).

### Startup, shutdown and logging

| ID | Function / file:line | What goes wrong | Seen as | Reached by | Eff | Reach | Evidence | Fork fix | Detail |
|---|---|---|---|---|---|---|---|---|---|
| <a id="su1"></a>SU1 | `Settings.cs:483-484`, `:492` | The log level key is read case-insensitively but removed from the binding case-sensitively. | DCS does not start ("Failed to initialize environment") | `"logLevel"` in config.json | E2 | R4 | printer | #150 | DU SU1 |
| <a id="su6"></a>SU6 | `Model/PeriodicUpdateService.cs:86-88` | `Directory.GetFiles` of a missing plugin directory throws. | DCS does not start | install without the plugin directory (the packages create it) | E2 | R4 | fork test | #150 | DU SU5 and SU6 |
| <a id="st3"></a>ST3 | `DuetSharedLibrary/AsyncLock.cs:48`, `:60`; `Logger.cs:59`; `Executed.cs:115-128`; `PipelineStackItem.cs:85-88` | AsyncLock refuses a free lock for a cancelled token, so the event log throws in the Executed stage outside the try that completes the code. | "Failed to process code in stage Executed"; the code is never completed | DCS stop with a code in the Executed stage | E4 | R1 | printer | #149 | DU ST3 |
| <a id="lu-l1"></a>L1 (LU) | `CommonLogFormatter.cs:26-46` | The level is written only as colored text, without a syslog prefix. | every journal line has priority 6; a flood of warnings also drops errors | every log line | E4 | R1 | printer | #118 | LU L1 |
| <a id="st2"></a>ST2 | `Codes/Pipelines/PipelineStackItem.cs:63`; `LinkService.cs:240`; `IPC/Server.cs:123` | The stages stop reading at ApplicationStopping, before LinkService cancels the link's codes into them. | a client waits for a code that is never completed; the IPC server waits until ApplicationStopped | DCS stop, or M999, with a code pending | E4 | R2 | fork test | #149 | DU ST2 |
| <a id="su2"></a>SU2 | `Settings.cs:429` (`PostConfigure`) | A restored config.json gets the settings of the current start. | after `-u` (run by the postinst) every later start logs only errors | config.json missing at a start with command-line values | E4 | R2 | fork test | #150 | DU SU2 |
| <a id="su3"></a>SU3 | `Program.cs:160` | Unmatched tokens go to AddCommandLine, but the root command still treats them as errors. | `--Key=value` refused ("Unrecognized command or argument") | settings on the command line | E4 | R2 | fork test | #150 | DU SU3 |
| <a id="st1"></a>ST1 | `LinkService.cs:285`, `:518` | PerformFullTransfer gets ApplicationStopped, which fires only after all services have stopped. | the stop hangs until systemd's SIGKILL after 15 s | stop while transfers do not complete | E4 | R3 | printer | #149 | DU ST1 |
| <a id="st4"></a>ST4 | `HostOptions.ShutdownTimeout` not set; `duetcontrolserver.service:8` | The host waits 30 s for its services, systemd kills DCS after 15 s. | SIGKILL instead of a clean stop | a service that takes over 15 s to stop | E4 | R3 | fork test | #149 | DU ST4 |
| <a id="su4"></a>SU4 | `Program.cs:156-169`, `:190`, `:197-199` | The try around AddJsonFile never fires; the file is read in Build(). | exit 71 "Failed to initialize environment" instead of 78 | invalid config.json | E4 | R4 | fork test | #150 | DU SU4 |
| <a id="su5"></a>SU5 | `Program.cs:46` (`Terminate`) | Terminate throws when the directory of the start error file is missing. | exit code 1 | start failure without `/run/dsf` (it exists on packaged systems) | E4 | R4 | fork test | #150 | DU SU5 and SU6 |
| <a id="l6"></a>L6 | `Executed.cs:235` | The catch logs only when `(e is OperationCanceledException) != ApplicationStopping.IsCancellationRequested` (since v3.6). | interceptor errors in the Executed stage are not logged; cancellations are logged as errors | interceptor error | E4 | R4 | read | open | OF FW/plugin/startup |

Samples:
- **SU1** · seen on printer (2026-11-11); run as a DCS process at 0c06ff81, the parent of #150 · expected: DCS starts with level info · actual: exit code 71, "[fatal] Failed to initialize environment: Failed to convert configuration value 'info' at 'LogLevel' to type 'Microsoft.Extensions.Logging.LogLevel'."
  ```
  /opt/dsf/conf/config.json: rename the key "LogLevel" to "logLevel", keep the value "info"
  systemctl restart duetcontrolserver
  ```
- **L1 (LU)** · seen on printer · expected: the errors of DCS · actual: no DCS line
  ```
  journalctl -u duetcontrolserver -p err
  ```

Reproducers: ST3 `AsyncLockCancelledToken.FreeLockIsTaken`, `ExecutedStageShutdown.CodeInExecutedStageCompletesWhileDcsStops`; ST2 `ShutdownHang.StopsWhileAnIpcCodeIsPending`; SU2 `SettingsParsing.RestoredConfigFileHoldsTheDefaults`; SU3 `DcsProcess.SettingsCanBeGivenOnTheCommandLine`; ST1 `ShutdownHang.LinkServiceStopsWhileRrfDoesNotAnswer`; ST4 `ShutdownHang.ShutdownTimeoutIsShorterThanTheStopTimeoutOfSystemd`; SU4 `DcsProcess.InvalidConfigFileIsAConfigurationError`.
Neither: SU6, SU5, L6.

### Firmware update (M997, `DuetControlServer -u`)

Nothing here ran on hardware; the tests emulate DuetIAP 3.7-dev 9af72da4. No samples: each would flash a board.

| ID | Function / file:line | What goes wrong | Seen as | Reached by | Eff | Reach | Evidence | Fork fix | Detail |
|---|---|---|---|---|---|---|---|---|---|
| <a id="du-u1"></a>U1 (DU) | `MCodeHandler.cs:1179`; `Utility/Firmware.cs:117` | The `.uf2` extension is compared case-sensitively; DuetIAP compares case-insensitively (`iap_sd.cpp:133`). | the UF2 container is flashed as the image | firmware file named `.UF2` | E3 | R2 | fork test | #148 | DU U1 |
| <a id="du-u4"></a>U4 (DU) | `FirmwareUpdater.cs:51`, `:65-68`, `:77-79` | A board whose firmware file is missing keeps version null and counts as outdated. | M997 B, a 3-minute wait and M999 for that board | `-u` with a missing firmware file | E3 | R2 | fork test | #148 | DU U4 |
| <a id="du-u6"></a>U6 (DU) | `FirmwareUpdater.cs:173`, `:397`, `:229-247`, `:454` | The reply of M997 B is not checked. | a 3-minute wait, then M999 resets the main board although no update started | RRF refuses M997 B | E3 | R2 | fork test | #148 | DU U6 |
| <a id="du-u3"></a>U3 (DU) | `MCodeHandler.cs:1153-1187`; DuetIAP `iap.cpp:370`, `:437` | M997 never checks the firmware file; IAP erases the flash before it receives the image. | the main board's flash is erased by an empty file | empty firmware file | E3 | R4 | fork test | #148 | DU U3 |
| <a id="du-u7"></a>U7 (DU) | `MCodeHandler.cs:1111` | DCS takes M997 whenever S contains 0 and drops the other modules. | `M997 S0:4` updates only module 0, silently | M997 with module 0 and others (the wiki documents `S0:1`) | E4 | R2 | fork test | #148 | DU U7 |
| <a id="du-u2"></a>U2 (DU) | `SPI.cs:1575`; `LinkService.cs:306-310`; `MCodeHandler.cs:1194` | The TfrRdy timeout is an OperationCanceledException, which M997's catch excludes. | M997 ends as cancelled: no error, no start error file, DCS does not stop | IAP program stops answering | E4 | R3 | fork test | #148 | DU U2 |
| <a id="du-u5"></a>U5 (DU) | `FirmwareUpdater.cs:67`, `:299` | GetFirmwareVersionAsync throws for an invalid UF2 file and nothing catches it. | `-u` stops for every board | one unreadable firmware file | E4 | R4 | fork test | #148 | DU U5 |

Reproducers: U1 `FirmwareUpdateFlashing.UpdateWithUf2FileInUpperCase`; U4 `UpdateOnlyMode.BoardWithoutFirmwareFileIsLeftAlone`; U6 `UpdateOnlyMode.ExpansionUpdateErrorIsReported`; U3 `FirmwareUpdateFlashing.UpdateRefusesEmptyFirmwareFile`; U7 `FirmwareUpdates.UpdateOfMainBoardWithOtherModules`; U2 `FirmwareUpdateFlashing.UpdateFailsWhenTheIapProgramStopsAnswering`; U5 `UpdateOnlyMode.InvalidFirmwareFileLeavesOnlyItsBoardAlone`.

## RRF

### SBC interface (SbcInterface, DataTransfer, BinaryParser)

| ID | Function / file:line | What goes wrong | Seen as | Reached by | Eff | Reach | Evidence | Fork fix | Detail |
|---|---|---|---|---|---|---|---|---|---|
| <a id="t1"></a>T1 | `SbcInterface.cpp:855-858`, `:1457`, `:1473`, `:1540-1553` | InvalidateChannel ends a macro wait with `ResolveMacroRequest(true, false)` but leaves `requestedMacroFile` set, so the request is still sent. | DSF runs a triggerN.g that RRF does not; every later trigger macro is discarded until DCS restarts | abort on the Trigger channel outside a macro while a macro request is pending | E2 | R3 | fork test | RRF #43 | DU T1 |
| <a id="t11-daemon"></a>T11 (daemon.g) | daemon.g request after InvalidateChannel (fork fix at `SbcInterface.cpp:1611-1648`, `:1967` @5c0cf3b8f) | A daemon.g request, which has no wait to end, written one transfer after InvalidateChannel reaches DSF after its abort window. | Daemon channel blocked (43 per 2000 aborts in the fake firmware) | abort on the Daemon channel | E2 | R3 | fork test | RRF #47 | OF Triggers T11; DU T1 "Not covered" |
| <a id="t10"></a>T10 | `SbcInterface.cpp:857`, `:867` (`InvalidateBufferedCodes`); `GCodes2.cpp:1713`; DSF `Processor.HandleReply` `:1073-1086` | InvalidateChannel drops only the pending codes; the running code still replies, and DSF matches replies by order. | the next code gets "Error: M98: Macro file … not found" | abort while a code runs or an M98 waits for its macro | E3 | R3 | printer | RRF #46 + #163; with the epoch #167 + RRF #47 | DU T10; OF Triggers T10 |
| <a id="of-s7"></a>S7 (OF) | `SbcInterface.h:155`; `OutputMemory.h:15-19`; `OutputMemory.cpp:487-506`; DSF `Processor.cs:1108-1117` | One 64-entry OutputStack holds the replies of all channels; on overflow `Push` drops the reply. | later replies of that channel shift by one | 64 replies pending | E3 | R3 | read | open | OF SBC link |
| <a id="k20"></a>K20 | `GCodes.cpp:490-499`, `:990-999`, `:1065-1070`; `GCodes2.cpp:1953-1955`; `GCodeBuffer.cpp:128-150` | DoEmergencyStop calls `GCodes::Reset`, which pops every stack and clears the abort flags, so no AbortFile reaches DSF and the SBC ring is not purged. | DSF keeps its macro levels and buffered codes; RRF answers them with "stopped" | trigger 0, PanelDue's M112, an M112 that RRF executes | E4 | R2 | read | open | OF v7 contract |
| <a id="new-s2"></a>S2 (new) | `DataTransfer.cpp:719`, `:744` | In SBC mode an expression text over 256 bytes is truncated silently. | RRF may evaluate a different expression | expression over 256 bytes | E4 | R2 | read | open | new |
| <a id="new-s3"></a>S3 (new) | `BinaryParser.cpp:495`, `:503` | In SBC mode string parameters are truncated silently; standalone raises "string too long". | truncated string | long string parameter | E4 | R2 | read | open | new |
| <a id="k21"></a>K21 | `SbcInterface.cpp:1041-1056`, `:1070`, `:1086`, `:1092`, `:1024-1030` | SetVariable evaluates and assigns, then may refuse the packet for lack of an OutputBuffer or TX room. | the copy fails with "variable '…' already exists" | no free OutputBuffer or TX room | E4 | R3 | read | open | OF v7 contract |

Reproducers: T1 `TriggerMacros.TriggerChannelWorksAfterInvalidationDuringTriggerRequest`; T11 (daemon.g) `ChannelEpochStress.Measure` (v7 model); T10 `TriggerFuzz.NextCodeGetsItsOwnReplyAfterAbortWhileM98WaitsForItsMacro`, `NextCodeGetsItsOwnReplyAfterAbortWhileACodeRuns`.
Neither: S7 (OF), K20, S2 (new), S3 (new), K21.

### G-code input and expressions

| ID | Function / file:line | What goes wrong | Seen as | Reached by | Eff | Reach | Evidence | Fork fix | Detail |
|---|---|---|---|---|---|---|---|---|---|
| <a id="k37"></a>K37 | SBC path of an abort (standalone fixed with `AbortStateMachine`, RRF 1e1eb34e0) | An abort from outside leaves the state variables of the removed levels set. | e.g. suspended heaters after an abort inside a probing code of a macro | abort from outside while a state machine runs | E3 | R3 | read | open (LA-25) | OF epoch reviews |
| <a id="usb-no-host"></a>[USB no host] | `GCodes.cpp:509-514`; `GCodeInput.cpp:249-266`; `StringParser.cpp:53-59` | Without a USB host `BufferedStreamGCodeInput::Spin` calls DiscardPartialLine, which also resets a code that came from DSF. | a macro on USB or USB2 never starts, the channel stays busy, DSF reported success | seen with the fork's M98.1/M98.7; upstream path (a priority code moved to an idle USB channel, `CodeProcessor.cs:223-236`) not shown | E2 | R5 | printer | open | OF "Macros do not start on the USB channels without a USB host" |
| <a id="byte-count"></a>[byte count] | `ExpressionParser.cpp` `#` 1602-1608, index 348-367, `take` 1082-1098, `drop` 1159-1177, `find` 1670-1684, `^` 804-808 | String operations count bytes where the wiki says characters (`Gcode_meta_commands.md:407`, `:419`); results can be invalid UTF-8, which the JSON writer emits unchanged (`SafeVsnprintf.cpp` PutJson, `OutputMemory.cpp:268`). | `#"é"` is 2; invalid UTF-8 in the object model (leads to O4) | string operations on non-ASCII text | E4 | R2 | printer | open (planned) | new |
| <a id="new-s4"></a>S4 (new) | `ExpressionParser.cpp:2463-2469` | Indexing a string variable directly is refused. | "Cannot index into variable … of non-array type"; only `(var.s)[0]` works | `var.s[0]` | E4 | R2 | printer | open | new |
| <a id="new-s1"></a>S1 (new) | `Configuration.h:140` (`MaxTitleLength`), `MessageBox.h:71` | The M291 title is stored in 61 bytes; the wiki says 100 characters (`Gcodes.md:3875`). | the title is cut | M291 with a long title | E5 | R2 | printer | open | new |

Samples:
- **K37** · derived, not run (cf. LA-25); RRF read · expected: the abort also ends the tool change of the macro level it removes and clears its state, as standalone `AbortPrint` does with `AbortStateMachine` (RRF 1e1eb34e0, `GCodes.cpp:3646`, `GCodes4.cpp:2028-2054`); `state.status` is "idle" again · actual (read): InvalidateChannel pops the levels without `AbortStateMachine` (`SbcInterface.cpp:843-871` upstream), so `doingToolChange` stays set (`GCodes4.cpp:417`) and `state.status` stays "changingTool" until M999 (`RepRap.cpp:1966-1969`). Needs tool 0 defined and selected. tfree0.g aborts before RRF puts the tool on standby, so no heater and no axis is touched. A T-code typed at the console does not show the finding: it runs at the base level, where standalone RRF carries on as well.
  ```
  ; 0:/sys/tfree0.g holds: abort "stop"     0:/macros/k37.g holds: T-1
  M98 P"0:/macros/k37.g"
  echo state.status
  ```
- **[byte count]** · seen on printer (2026-11-11); DSF side run at a99c81f1; RRF side read · expected: 1, and the M291 accepted (3 characters) · actual (read): 2, and `Error: M291: default value is outside limits` (6 bytes; `MessageBox.cpp:216-219` takes `strlen`, `:226-229` compares with H, fork RRF cde4e4fa1)
  ```
  var m = #"é"
  echo var.m
  M291 S7 P"Name" H3 F"äöü"
  ```
- **S4 (new)** · seen on printer (2026-11-11); derived, not run · expected: `var c` is "a" · actual (read): "Cannot index into variable … of non-array type"
  ```
  var s = "abc"
  var c = var.s[0]
  var d = (var.s)[0]     ; works
  ```
- **S1 (new)** · seen on printer (2026-11-11); derived, not run · expected: 70 (wiki: up to 100 characters) · actual (read): 61 (title read into 100 bytes, `GCodes7.cpp`, copied into `String<61>`, `MessageBox.cpp:85`)
  ```
  M291 S1 T0 R"1234567890123456789012345678901234567890123456789012345678901234567890" P"x"
  echo #state.messageBox.title
  ```

Neither: [USB no host] (the upstream path is not shown).

## Origin not checked

The sources do not say whose code these are (OF, sections "FW/plugin/startup" and "epoch implementation/reviews", unless noted).

- **U1 (OF)** remote wait of `-u` for an expansion board ignores its timeout while no patch arrives (`FirmwareUpdater.cs:416`).
- **U2 (OF)** the M999 of the remote `-u` has no channel (`FirmwareUpdater.cs:464-470`).
- **U3 (OF)** DuetPiManagementPlugin intercepts every M997 whose S contains 2 and runs only S2 (`Program.cs:679-680`).
- **U4 (OF)** an RRF build with `NO_S_CURVE` reports a version with spaces, which DSF reads as "motion)" (`Firmware.cs:169-173`).
- **L1 (OF)** a code that reaches the Firmware stage after LinkService has stopped waits for the shutdown timeout.
- **L2 (OF)** M999 and M997 may not stop DCS when their code faults after the reset (`MCodeHandler.cs:1380-1385`, `:1324-1329`).
- **L3 (OF)** a start failure leaves the IPC socket file behind.
- **L4 (OF)** `-u` listens on the IPC socket without accepting and deletes an existing socket file.
- **L5 (OF)** `-l` with an unknown level falls back to Information.
- **D1 (OF)** a parent directory swapped for a symlink between check and write (TOCTOU).
- **D2 (OF)** the www link of a root plugin's web file is created before the root service writes the file.
- **D4 (OF)** deviations without a test: `PLUGINS.md:27`, StopPlugin's message, `"` and `\` refused in all plugin arguments.
- **H14, H15 (OF)** DuetHttpClient: RestConnector reconnects every PingInterval; PollConnector never loads keys with sequence number 0. OF's blame line excludes them from upstream code without naming their origin.
- **K30** the "Macro file not found" warning of an aborted trigger request is prepended to the next reply; OF: not checked whether upstream RRF has the same condition (`GCodes.cpp:3719-3727`).
- **K31** a DoCode that flushes its channel (M929) waits forever while DSF has codes buffered on that channel.
- **K35** DSF gives up a connection after 500 ms within a transfer, RRF after 4 s.
- **K36** `macroJustStarted` stayed set when an abort popped a macro before its first code (fixed in RRF #47).
- **K38** an untagged packet of the previous connection reaches the next one (lab: "Daemon: Out-of-order reply" at DCS starts).

## Notes

- **Line numbers per source:** DU: upstream DSF 973838bf, RRF 25ec27aec, DuetIAP 9af72da4 (K32: DSF f76ffc70). LU: upstream DSF 1f72a780 (link files unchanged at 973838bf), RRF 231daaac87 (`src/SBC` unchanged at 25ec27aec3). OF, where git blame puts the lines on upstream code: fork DSF c6d225f2 (entries of 2026-11-08: 9e6449d1) and fork RRF cde4e4fa1 for the fuzz, trigger, stop path, SBC link, HTTP, G-code, IPC and object model entries; fork DSF 4c8b3fec and RRF fc939b3 for the two sections before them; fork DSF f70a6f16 for "FW/plugin/startup"; fork DSF 7f26d0ca and RRF 0dccb9efe (tree of `3.7-dev` ddec2d001) for the K entries. "@412e5ff6" marks lines checked on 2026-11-11 at fork DSF 412e5ff6 and fork RRF 5c0cf3b8f. New items: fork DSF 412e5ff6; the RRF commit was not recorded. Sample checks: fork RRF cde4e4fa1, RRFLibraries 869f6bd, wiki a106ad4.
- **IDs:** where the sources reuse an id, the source is added: (DU), (LU), (OF), (new). Names in [brackets] are ours, for entries without an id. Short section names: "OF v7 contract" = "Found by the v7 contract reverse engineering (2026-11-09)"; "OF epoch reviews" = "Found by the reviews of the channel epoch"; "OF FW/plugin/startup" = "Findings of the firmware update, plugin service and startup tests (2026-11-08)"; "OF fuzz fixed list" = the fixed entries under "Findings of the fuzz and coverage tests (2026-11-07)"; "OF SBC link", "HTTP", "G-code", "IPC", "Object model", "Triggers", "Stop path" are its subsections.
- **Sources that disagree** (the newer or the more specific one is used):
  - OF says the stock build showed the bug on the printer for H1, G2, I1, T2, T3, P1, P2, P4 and T1. DU and the lab protocol (`lab-test-plan.md` §14, 2026-11-08) show the stock counter-check only for G2, T3 and the 6 P1/F3 cases; T2 did not run, H1 was shown in-process, RRF #43 (T1) was only built. The evidence column follows DU.
  - K32: OF's fixed list calls the upstream lines "read, not run"; DU (written later the same day) reports a test on unchanged upstream f76ffc70. DU is used.
  - T10: DU names RRF #46 and #163 as the fix; OF (2026-11-11) names #167 + RRF #47 and lists what stays open without the epoch. OF is used for the status.
  - T11: DU covers the resend case; OF (2026-11-11) adds the daemon.g case. Two rows.
  - T4 and K30: OF counts T4 as upstream by blame; K30 says it was not checked whether upstream RRF has the condition that T4 rests on.
  - P2 (OF): the current OF names it only as fixed by #137; its text is from OF at 384138dd. P1 (OF) is the F3 case of DU and has no row of its own.
- **Fork PRs:** #181 (I3) and #184 (O3) are merged in the fork's `v3.7-dev`; #185 (O4 and the handler log) is open.
- **Trigger macros:** the effect class says what the code does, not what a machine's macro does with it. A trigger macro (M581 T2 and higher) is a user-defined reaction, so T3, T1 and P4 (OF) are rated as a delayed or blocked macro, although on a machine whose trigger macro stops it they keep a stop from happening. Trigger 0, the emergency stop that RRF makes itself, is T7 and K20.
- **Samples on the printer:** on 2026-11-11 the samples marked "seen on printer (2026-11-11)" were typed in on the lab printer as they stand here (DSF 3.7.0+mp.13 and RRF 3.7.0+1.mp.8, fork builds without the fork fixes named in the rows, except #118). 29 samples were confirmed that way; G7 and O3 had been seen there earlier that day. I3, G1, G2 and SU1 each end with a DCS that has to be restarted. Not tried on the printer: G9 (needs a real print job), T7 (registers a trigger 0), K37 (needs a selected tool) and L1 (fixed in that build).
- **O3 on the printer:** lab printer, 2026-11-11 09:29:48 to 09:34:56 UTC, DSF 3.7.0+mp.13 and RRF 3.7.0+1.mp.8 (fork builds; at the lines involved the code is upstream's). Nothing recovered by itself; `G10 P0 X0` sent with POST /machine/code restored the model within a second, and the subscribing plugin stayed up.

## Left out

- **Fork code:** S1, S4, S5 (OF; the fork's extended link and sequence guard); the extended-link part of P4 (#138); K16 (the fork's key check); K29 (a gap in fork RRF #46; upstream has T10 and T11); U5 (OF; fork #154); D5 (OF; CHX image); the restart case of the extended link (LU); #144, #150 16ee43db, #154 and parts of #151 (DU "Left out", no ids).
- **Fixed upstream:** H13 (5130ca43).
- **No finding:** T13 (OF: probably no finding); G and M as macro parameters (documented, OF).
- **Merged into other rows:** F3 (case, DU) and P1 (OF) into F3; T10 and T11 of DU and OF into T10, T11 (resend) and T11 (daemon.g); O4 (OF) with the results of 2026-11-11.
- **Held back until a test shows them (fork decision):** K12, K33.
- **Open questions without an id** (OF HTTP, G-code, IPC, Triggers): not included.
