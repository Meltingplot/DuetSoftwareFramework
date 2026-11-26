# Prüfplan: Änderungen und Regression am Lab-Drucker (DUT)

Stand: 2026-11-06. Gilt für DSF `3.7.0+mp.12` und RRF `3.7.0+1.mp.7` auf dem CHX 350 im Labor.

Belege:
- Zeilenangaben beziehen sich auf DSF `v3.7-dev` bei b4207c0f (`src` und `pkg` sind identisch mit Tag `v3.7.0+mp.12`), RRF `3.7-dev` bei fc939b38a und wiki-content bei a106ad4.
- Makrozeilen beziehen sich auf die Dateien auf dem DUT, gelesen am 2026-11-06.
- Laborwerte ohne PR-Angabe stehen nur in den Sitzungsnotizen, nicht im Repo.
- Sollwerte von Tests mit dem Vermerk **neu** stammen aus dem Code und sind auf Hardware noch nicht bestätigt.

## 1. Kurzfassung

Der Plan beantwortet zwei Fragen:
1. Kommen bekannte Fehlerbilder zurück? Dafür gibt es die Regressionsprüfung (RG).
2. Sind die Änderungen im Fork auf echter Hardware belegt? Dafür gibt es die Änderungsprüfung (A) und die Fehlerinjektion (F).

Die größten Lücken heute:
- **Release-Stand:** Regression, Dauerlauf und Fehlerinjektion auf DSF mp.12 mit RRF mp.7 sind am 2026-11-06 bestanden ([Abschnitt 14](#14-durchgeführte-läufe)). Die neuen Tests der Änderungsprüfung und die Vor-Ort-Prüfungen stehen noch aus.
- **Nie auf Hardware:** #102 (DCS-Neustart nach 60 s) und #67 (M581.1 ersetzt SBC-Trigger) laut ihren PRs. Auch für die Fehlerfälle der Zeilenprüfsummen ist kein Hardwarelauf dokumentiert.
- **Self-Test:** Seit dem aktuellen Image startet der DUT nach jedem Boot einen Self-Test auf dem HTTP-Kanal. Die Suite nutzt denselben Kanal. Im ersten Lauf hat das nicht gestört, siehe [Abschnitt 5](#5-self-test-und-suite).

## 2. Prüfling

Gelesen am 2026-11-06 gegen 18:50 UTC, nur lesend.

| Merkmal | Wert |
|---|---|
| Maschine | CHX 350 `meltingplot-chx-350-002-sbc`, Pi 5 und MB6HC, erreichbar über den HMI-Jump-Host |
| DSF | 3.7.0+mp.12 aus dem Image, kein Side-Load |
| DWC | 3.7.0+mp.7 laut Release-Notiz von mp.12, am DUT nicht gelesen |
| RRF | MB6HC 3.7.0+1.mp.7 |
| Erweiterungen | TOOL1LC (20), EXP1HCL (50 bis 53), SZP (60), alle 3.7.0+1.mp.4 |
| Link | `Link capabilities All are active` nach jedem DCS-Start |
| `config.json` | `MaxSbcRetries` 10, Timeouts 500/500/4000 ms, `SbcRestartTimeout` nicht gesetzt, also 60000 ms (`Settings.cs:188`), `LogLevel` info |
| Konfiguration | chx350-config, laut rpi-image-gen #144 Stand 85e2c0c, am DUT nicht per Hash geprüft |
| Trigger | `trigger2.g` bis `trigger10.g` belegt, Testtrigger 30 frei |
| Zustand | idle, kein Job |

## 3. Freigabestufen

| Stufe | Inhalt | Freigabe | Grundlage |
|---|---|---|---|
| 1 | Lesen: Status, Journal, `M122`, `M409` und andere nur lesende Codes. Dazu `M291`/`M292`, Testmakros in `0:/macros/lab-tests`, Globals `labTest...` | Tim wird vor dem Lauf informiert | `src/LabPrinterTests/README.md:22` |
| 2 | Ohne Aufsicht erlaubt: `M122 P1001`, DCS-Neustart mit `M999`, Testtrigger 30, Side-Load, Fehlerinjektion | Tim gibt den Lauf frei | README `:23-25`, `SpiFaultInjection.cs:21` |
| 3 | Alles außerhalb dieser Liste: `M112`, `M111`, `M929`, `M606`, Testdateien in `0:/gcodes` | Tim pro Punkt | nicht von README `:22-26` abgedeckt |
| 4 | Druckjob, Bewegung, Heizen, Panel, ESD | Nur mit Tim vor Ort | README `:27` |

Regeln für jeden Lauf:
- Die Suite läuft immer gegen die aktuelle Firmware des Prüflings, weil sie ein Ende-zu-Ende-Test ist (Tim, 2026-11-06). Für einen Test wird keine andere RRF-Version geflasht. Side-Loads betreffen nur DSF-Builds.
- Tim vorher informieren. Während des Laufs bedient niemand den Drucker von Hand. Am 2026-11-02 traf Handbedienung auf den Watchdog-Test und führte zu einer falschen Fehlerdeutung.
- Die RRF-Sitzung vor langen Läufen ausdrücklich bitten, bis zur Freimeldung nicht zu flashen. Am 2026-11-02 machte ein Flash mitten im Lauf 153 Fälle ungültig.
- Für den Zugang muss Tims Hardware-Token stecken (README `:11`).

## 4. Umfang und Ablauf

### Umfang je Anlass

| Anlass | Umfang |
|---|---|
| PR im Link-, Code- oder Trigger-Pfad | Block 0, E, RG mit `--side-load`, dazu die betroffenen A- und F-Prüfungen |
| Neues DSF-Release oder Image | Block 0, E, RG ohne Side-Load, F-1, F-2 |
| Neues RRF-Release | Wie neues Release, dazu `PortMatchesRealFirmware` durch die RRF-Sitzung (`LinkRecovery.cs:838`) |
| Änderung am Warten auf TfrRdy in DSF (`Link/Adapter/SPI.cs`), Feldbericht eines Verbindungsverlusts im Leerlauf | RG-17 |
| Änderung an `Heap`, String- oder Array-Handles oder am Schließen von Makros, Feldbericht eines Resets in `Heap::DeleteSlot` | RG-18 |
| Erstdurchlauf des aktuellen Stands | Alle Blöcke unten |

RG-17 und RG-18 laufen nur auf Abruf und gehören nicht zum Release-Umfang (Tim, 2026-11-21). Beide warten im Leerlauf und finden seltene Fehler nur statistisch. RG-18 erkennt in 60 min die Rate von mp.9 mit etwa 90 %, den Fix selbst prüft `Tools/heap-test` in RepRapFirmware deterministisch.

### Blöcke

| Block | Inhalt | Stufe | Vorarbeit | Dauer (ca.) |
|---|---|---|---|---|
| 0 | Offline-Gate | keine | keine | 1 min |
| 1 | E, RG-1 bis RG-16, Auswertung Self-Test | 2 | keine | 10 min |
| 2 | RG-17 und RG-18 Dauerläufe, nur auf Abruf | 1 (RG-17), 2 (RG-18, Testtrigger 30) | keine | 30 min und 60 min |
| 3 | F-1, F-2 | 2 | keine | 45 min |
| 4 | A-1 bis A-7, A-10 | 1 und 2 | Tests aus `feature/lab-tests-for-changes` | Suite 11,2 min, A-1 und A-2 etwa 5 min |
| 5 | A-8, A-9, A-11 bis A-13 | 3 | Tests aus `feature/lab-tests-for-changes`, Einzelfreigabe | offen |
| 6 | M-1 bis M-4 | 4 | Termin mit Tim | M-1 75 min (Testdruck am 2026-11-23), M-2 bis M-4 offen |

Gemessen am 2026-11-06: Suite in Block 1 8,3 min, Block 3 44 min einschließlich Side-Load (F-1 3 min, F-2 41 min).

### Block 0: Offline-Gate

```
export PATH=$HOME/.dotnet:$PATH
dotnet test src/UnitTests/UnitTests.csproj
dotnet test src/LabPrinterTests/LabPrinterTests.csproj --list-tests
```

Soll: 0 fehlgeschlagen. Die Liste enthält 19 Lab-Testfälle, davon 16 im Standardlauf. Referenz vom 2026-11-06 auf diesem Stand: 818 bestanden, 0 fehlgeschlagen, 6 übersprungen.

### Bestehensregel

- Ein Lauf ist bestanden, wenn alle ausgewählten Fälle bestehen und die [Auswertung](#9-auswertung) keinen Fehlerbefund zeigt.
- Ein übersprungener oder nicht injizierter Fall zählt als nicht geprüft, nicht als bestanden. `LabTest.cs:65-68` überspringt Tests, wenn der Drucker nicht idle ist. `SpiFaultInjection.cs:58-62` lässt nicht injizierte Fälle nicht fehlschlagen.
- Für den aktuellen Stand gelten E, RG, F-1, F-2 und A-1 bis A-3 als Mindestumfang. Eine Abweichung davon entscheidet Tim.

## 5. Self-Test und Suite

Die Kurzstufe des Self-Tests startet nach jedem RRF-Boot bei `state.upTime` von etwa 60 s:
- `sys/meltingplot/globals.g:573` setzt `self_test_done` bei jedem Boot auf -1, und `globals.g:584` setzt die erste Prüfung auf 60 s.
- `self-test/store.g` speichert die Kurzstufe bewusst nicht.
- `self-test/tick.g:47` startet den Flow mit `M98.1 S0` auf dem HTTP-Kanal (vor der Umstellung `M98.3`), wenn der Drucker idle ist.
- Im Journal lief der Autostart am 2026-11-06 von 18:16:03 bis 18:16:04 UTC.

Die Suite löst selbst Boots aus und nutzt denselben Kanal. Mögliche Folgen:
- `LabTest.cs:65-68` überspringt einen Test, wenn der Drucker 30 s lang nicht idle wird.
- `MessageBoxes.cs:29-36` schließt und zählt jede Box. Der Flow enthält `M291`-Boxen (`maintenance/self-test:94`, `:100`), die der Test mitzählen könnte.
- Im HTTP-Fall (RG-14, `M98.1 S0`) sieht der Trigger den Kanal in `inputs[]` belegt und lässt den Aufruf aus, solange der Flow den HTTP-Kanal belegt.

Vorgehen (Tim, 2026-11-06):
1. Den ersten Lauf von Block 1 unverändert als Messung fahren und Testzeiten mit den Self-Test-Zeilen im Journal vergleichen.
2. Nur wenn sich eine Störung zeigt: `LabTest` wartet nach einem Boot, bis die Kurzstufe durch ist. Der Drucker bleibt so im Serienzustand.
3. Den Autostart für die Suite nicht abschalten, weil dann ein anderer Zustand geprüft würde als der gelieferte.

Messung vom 2026-11-06, ein Lauf:
- Der Autostart lief von 19:39:29 bis 19:39:30 UTC, 69 s nach dem letzten Reboot (19:38:20).
- Er traf die Lücke zwischen den Testfällen von RG-13 und RG-14. Keine Störung, die Wartelogik aus Schritt 2 wird vorerst nicht gebaut.
- Ein einzelner Lauf schließt eine Kollision nicht aus. Die Box `Selbsttest bestanden` (`maintenance/self-test:100`, 5 s) stand noch, als RG-14 begann. RG-14 wertet keine Boxen aus, und RG-2 lief erst später. Bei anderen Laufzeiten oder einer anderen Testreihenfolge könnte RG-2 sie mitzählen.
- In F-1 (20:44:03) und F-2 (20:46:38) lief die Kurzstufe während der Injektion, ohne Abweichung vom Census.
- Deshalb bleibt die Self-Test-Zeile im Protokoll Pflicht.

## 6. Eingangsprüfung (Stufe 1)

| ID | Prüfung | Soll |
|---|---|---|
| E-1 | Versionen, Side-Load: `findmnt -n /opt/dsf/bin/DuetControlServer.dll` | Versionen wie in Abschnitt 2 oder wie im Anlass angegeben; `findmnt` ohne Ausgabe |
| E-2 | Journal nach dem letzten DCS-Start | `Link capabilities All are active` (`LinkService.cs:1497`) |
| E-3 | `/opt/dsf/conf/config.json` lesen (nur mit `sudo`) | `MaxSbcRetries` 10 (`pkg/common/duetcontrolserver/opt/dsf/conf/config.json:25`) |
| E-4 | `M122` als Ausgangswert | Zähler von DSF (`SPI.cs:269-271`) und RRF notieren. CAN-Fehler zählen erst ab dem zweiten `M122` nach einem Boot (Tim, 2026-11-04) |
| E-5 | Testressourcen | `sys/trigger30.g` fehlt, `M581 T30` antwortet `is not configured` |
| E-6 | Maschinenzustand | idle, kein Job, alle sechs Erweiterungen `running` (`Lab.cs:122-124`) |
| E-7 | `global.self_test_*` | Werte notieren, `self_test_found` und `self_test_failed` leer |

## 7. Regressionsprüfung

```
src/LabPrinterTests/run-lab-tests.sh                                          # RG-1 bis RG-16, Image
src/LabPrinterTests/run-lab-tests.sh --side-load                              # dasselbe mit dem Stand dieses Baums
src/LabPrinterTests/run-lab-tests.sh --filter "FullyQualifiedName~LinkSoak"   # RG-17
src/LabPrinterTests/run-lab-tests.sh --filter "FullyQualifiedName~MacroCloseStress"   # RG-18, Dauer mit LAB_MACRO_CLOSE_MINUTES
```

| ID | Test | Altes Fehlerbild | Soll |
|---|---|---|---|
| RG-1 | `Diagnostics.RepeatedM122` | Hard Fault in `M122` (RRF rc.2+1-mp.3) | 300 Antworten mit `=== Diagnostics ===`, kein RRF-Neustart, kein `Connection to controller has been reset` |
| RG-2 | `MessageBoxes.OneBoxAfterM122` | Mehrere Boxen nach drei `M122` | Genau eine Box |
| RG-3 | `MacroReturns.NestedM99KeepsCallerVariables` | Spätes `M99`-Reply | Kein `Error`, `global.labTestOutcome` ist `done` |
| RG-4, RG-5 | `UnbufferedCodes.M409AnsweredByDsfDuringMacro`, `M409ForwardedToRrfDuringMacro` | `M409`-Deadlock (mp.7) | Makro und `M409` antworten innerhalb von 20 s, Key stimmt |
| RG-6 | `FirmwareRestart.LinkRecoversAfterWatchdogReset` | Link erholt sich nicht nach RRF-Neustart | 5 x `M122 P1001` (Wiki `Gcodes.md:2920`): idle nach höchstens 90 s, je 50 `M409` mit richtigem Key und Wert |
| RG-7 | `ControlServerRestart.PluginServicesStartWithControlServer` | Plugin-Dienste bleiben aus | Beide Dienste `active` |
| RG-8 | `ControlServerRestart.ResetDoesNotHangShutdown` | DCS hängt 15 s beim Beenden (offen) | `Resetting controller`, kein `stop-sigterm`. Laut `ControlServerRestart.cs:44-46` beweist ein Bestehen nicht, dass der Hänger behoben ist |
| RG-9 | `TriggerMacros.TriggerMacroCallingMacro` | Verstümmelte Makronamen | In 2 min mindestens 60 Läufe, keine Makro- oder `Out-of-order`-Meldung. Laut `TriggerMacros.cs:76-78` beweist ein sauberer Lauf dieser Länge wenig |
| RG-10 | `TriggerMacros.DeleteFirmwareTriggerWithM581Point1` | `P-1` löscht nicht (#61) | Danach `is not configured` |
| RG-11 | `TriggerMacros.ReportFirmwareTriggerWithM581Point1` | Bericht ohne `P` abgelehnt (#63) | Bericht und `R-1` kommen bei RRF an |
| RG-12 | `TriggerMacros.ReportSbcTriggerWithM581` | Bericht löscht SBC-Trigger (#63) | Trigger bleibt erhalten, `P-1` löscht ihn |
| RG-13 bis RG-16 | `MacrosOnOtherChannels.TriggerKeepsFiringWhileMacroRuns` für `M98.1 S4`, `S0`, `S1`, `S10`, seit `M98.1 W` auch `S8` (noch nicht am Drucker gelaufen) | Neue Funktion #108, `M98.1 S`, `M98.1 W` | Kriterien in `MacrosOnOtherChannels.cs`, unter anderem richtiger Kanal, Parameter `5,2,5,true,false,false` (ohne `S` und `W`), Trigger feuert weiter |
| RG-17 | `LinkSoak.NoLostConnectionWhileIdle` | TfrRdy-Timeouts alle 15 bis 30 min | 30 min ohne `Lost connection to Duet`, kein RRF-Neustart |
| RG-18 | `MacroCloseStress.NoResetWhileMacrosCloseOnTheTriggerChannel` | RRF-Reset im Leerlauf, `MemoryProtectionFault` in `Heap::DeleteSlot` kurz nach dem Schließen eines Trigger-Makros, etwa einmal je 40.000 Schließvorgänge (RRF mp.9, Upstream-Code) | 60 min mit mindestens 10 Schließvorgängen je Sekunde: `state.upTime` fällt nie, `Last software reset` in `M122` unverändert, `Heap OK`, keine Neustartzeile aus `PER_REBOOT` im Journal |

Zusatzauswertung für RG-6, weil der Test selbst erst nach 90 s fehlschlägt:
- **Erholzeit je Reboot:** Referenz 11,8 bis 12,2 s (RRF #36, 15 Reboots; Abnahme vom 2026-11-03: 11,8 bis 12,2 s). Mehr als das ist eine Abweichung und wird analysiert, auch wenn der Test besteht. Der Wert wird auf dem Client über den Tunnel gestoppt.
- **Link-seitige Referenz im Journal:** Reset bis Ende von `config.g` 9,48 bis 9,57 s (2026-11-06). Mit `journalctl -o short-iso-precise` auslesen. Dieser Wert zeigt, ob eine Mehrzeit im Link oder Boot liegt.
- **Journal je Reboot:** genau ein `Connection to controller has been reset`, ein `RepRapFirmware has left the extended link` und ein Neustart der Übertragung; kein `Lost connection to Duet`.
- **`M122`-Differenz über die 5 Reboots:** `Full transfer restarts`, `extended link left by RRF` und `connection resets` je +5, `lost connections` +0. Die Zähler laufen seit dem letzten DCS-Start. Für eine saubere Differenz RG-6 einzeln fahren und `M122` davor und danach lesen: `--filter "FullyQualifiedName~FirmwareRestart"`.

## 8. Änderungsprüfung

### Abdeckung

„Keiner“ heißt: Kein Hardwarelauf ist dokumentiert. Mit „(PR)“ steht das ausdrücklich in der PR-Beschreibung.

| Änderung | Bisheriger Hardwarebeleg | Prüfung |
|---|---|---|
| #59 Link nach RRF-Neustart | Suite, Abnahme mp.9 | RG-6 |
| #61, #63 `M581` und `M581.1` | Side-Load (#61, #63) | RG-10 bis RG-12, A-7 |
| #67 `M581.1` ersetzt SBC-Trigger | Keiner (PR) | A-6 |
| #70 `M111 O1` | Keiner | A-9 |
| #72 `M36`, #82 `M39` | Keiner | A-10 |
| #74, #80 `M30`, `M472` | Keiner | A-11 |
| #76 `M606` | Keiner | A-13 |
| #81 `M929` | Keiner | A-12 |
| #88 `M112` | Keiner | A-8 |
| #91 bis #93, RRF #35 Guards, erweiterter Link | Suite und Injektion auf RRF mp.5 und dev.3 (#93, #95), RRF mp.7 am 2026-11-06 | E-2, F-2 |
| #98 `MaxSbcRetries` 10 | A/B per Side-Load | E-3, F-2 |
| #99 erste TfrRdy-Wartezeit | Nur RRF mp.5 (#99) | A-2 |
| #102 DCS-Neustart nach 60 s | Keiner (PR) | A-1 |
| #103 Zähler in `M122` | Abnahme mp.9, RG-6 am 2026-11-06 | RG-6 |
| #105 Daten-Retry | Side-Load, RRF mp.6, 15 von 15 (#106, #107), RRF mp.7 am 2026-11-06 | F-1 |
| #108 `M98.n` | Side-Load von 5ffadeca auf RRF mp.7 (PR), Release-Build am 2026-11-06 | RG-13 bis RG-16, A-3, A-4 |
| Zeilenprüfsummen und Zeilenfolge | Nur der Gutfall, die Konfiguration trägt `N` und CRC | A-5 |
| RRF #36 Boot-Verzögerung | A/B mit 15 Reboots (RRF #36) | RG-6 |
| RRF mp.7 (Duet3D-Sync, RRF #39) | Regression, Dauerlauf und Injektion am 2026-11-06 | RG, F-2 |
| RRF #50 Freigabe von String- und Array-Handles | Side-Load von 3.7.0+1.mp.10-dev.1 am 2026-11-21, Host-Test `Tools/heap-test` | RG-18 |
| Upstream 30ea08be | Laufende Suite seit der Übernahme | RG-4, RG-5 |
| Upstream 9acd058f | Keiner (README `:66`) | M-2 |
| DWC mp.6 und mp.7 | USB-Stick als Side-Load am 2026-11-04, #25 nicht belegt | M-4 |

Bewusst nur offline:
- #73 `M20` braucht einen Kanal im Marlin-Modus.
- #75 `M505` würde den Ordner umschalten, aus dem die Makros der Maschine laufen.
- #77 braucht einen Job mit `M606`.
- #78 braucht ein I²C-Gerät; ob am DUT eines verfügbar ist, ist nicht geprüft.
- #84 und #86 sind reine Parserlogik.
- #85 und #89 brauchen fehlerhafte Pakete von RRF.

### Fehlerinjektion (Stufe 2, keine Vorarbeit)

Beide Prüfungen laufen mit dem Injektions-Build per Side-Load. Die Fallzahlen sind am 2026-11-06 offline erzeugt.

**F-1: ESD-Replay, belegt #105 und #103 auf RRF mp.7**

```
LAB_SPI_FAULT_CASES=/tmp/esd.json dotnet test src/UnitTests/UnitTests.csproj --filter "Name=WriteEsdCases"
LAB_SPI_FAULT_CASES=/tmp/esd.json LAB_SPI_FAULT_SELECT=all src/LabPrinterTests/run-lab-tests.sh --side-load --fault-injection --filter "FullyQualifiedName~SpiFaultInjection"
```

- Soll: 15 Fälle, alle `Retry`, 0 Abweichungen.
- Soll im Journal, wie im Lauf vom 2026-11-04 (#106): 10 x `Bad data CRC32` (`SPI.cs:2067`), 5 x `Bad header CRC32` (`SPI.cs:1866`), kein `Restarting full transfer because`, kein `Lost connection to Duet`.
- **Zähler:** Der Runner startet DCS direkt nach dem Test neu, um den Side-Load zu entfernen. Die DSF-Zähler aus `M122` sind danach weg. Den Sollwert aus `LinkRecovery.cs:1022-1024` (`data retries` +10, `header retries` +5) kann der Lauf nur prüfen, wenn der Test `M122` vor dem letzten Fall selbst ausgibt. Das ist eine kleine Erweiterung von `SpiFaultInjection.cs`.
- **Falle:** Ohne `LAB_SPI_FAULT_SELECT=all` wählt der Filter keinen der 15 Fälle aus, und der Test besteht leer (`SpiFaultInjection.cs:41-45`, `:83`). Die `Outcomes`-Zeile muss 15 Fälle zeigen.

**F-2: Einzelfehler auf dem erweiterten Link**

```
LAB_SPI_FAULT_CASES=/tmp/ext.json dotnet test src/UnitTests/UnitTests.csproj --filter "Name=WriteHardwareCases_Extended"
LAB_SPI_FAULT_CASES=/tmp/ext.json src/LabPrinterTests/run-lab-tests.sh --side-load --fault-injection --filter "FullyQualifiedName~SpiFaultInjection"
```

- Soll: 672 von 3234 Fällen ausgewählt, alle `Retry`, 0 Abweichungen vom Census.
- Referenz: 678 von 679 `Retry` und 1 nicht injiziert auf RRF dev.3, noch ohne #98, #99, #102 und #105 (#95).
- Nicht injizierte Fälle einzeln nennen und nachholen.

**F-3: Einzelfehler auf dem Plain-Link, belegt #137 (P1)**

```
LAB_SPI_FAULT_CASES=/tmp/plain.json dotnet test src/UnitTests/UnitTests.csproj --filter "Name=WriteHardwareCases_RrfOfToday"
LAB_SPI_FAULT_CASES=/tmp/p1.json LAB_SPI_FAULT_SELECT=all src/LabPrinterTests/run-lab-tests.sh --side-load --fault-injection --plain-link --filter "FullyQualifiedName~SpiFaultInjection"
```

- **Plain-Link:** RRF mp.8 bietet die Capabilities an (`DataTransfer.cpp:476-479`), DSF aktiviert sie immer (`LinkService.cs:1482`, `:1505`), und `Settings.cs` hat keinen Schalter dafür. `--plain-link` legt `/var/tmp/lab-tests/spi-plain-link` an, dann meldet der Injektions-Build keine Capabilities (`SPI.cs`, nur unter `SPI_FAULT_INJECTION`) und DSF fragt sie nicht ab. Ohne das Define baut `SPI.cs` byte-gleich zu `origin/v3.7-dev` (am 2026-11-08 verglichen), und den Injektor erzeugt DCS nur unter dem Define (`SPI.cs:113-117`).
- **RRF ohne Aktivierung:** Phase Guard, Sequence Guard und die erweiterten Codes wirken nur mit `activeLinkCapabilities` (`DataTransfer.cpp:841`, `:857`, `:890`, `:926`, `:964`, `:1068`, `:1125`, `:1385`), das nur die Aktivierungsanfrage setzt (`:826-835`, `SbcInterface.cpp:1313-1321`). Ohne sie bleiben gegenüber dem Port (RRF 03a87245b) zwei Regeln, die der Port schon nachbildet: das Dekodieren des ersten Header-Worts ohne Verbindung (`DataTransfer.cpp:873-874`, `RrfRule.DecodeHeaderWordUnconnected`) und `lastTransferNumber` bei Sequenznummer 0xFFE0 nach einem Neustart ohne Daten (`:1018-1020`, `ExtendedLink.Previous` in `FakeRrfSpi.RestartTransfer`).
- **Fälle:** Mit `RrfOfToday` (Polling) brechen 28 von 3234 Fällen den Link, mit #137 noch 20. Die 8 Unterschiede sind Fehler in der Antwort von RRF auf den Header bei Daten von DSF (6 verschiedene Specs, `BothData`/`DsfData HeaderResponse ToDsf` mit `flip 2`, `word 0x00000005`, `word 0xfefefefe`). Dazu 4 Kontrollen, die in beiden Ständen `Retry` sind, und 2, die in beiden den Link brechen.
- **Soll mit #137:** 10 `Retry`, 2 brechen den Link, 0 Abweichungen. **Ohne #137:** die 6 P1-Fälle `NoRecovery` (Link hängt, `RecoverAsync`), insgesamt 4 `Retry`, 0 Abweichungen. Der Test bricht ab, wenn DCS den erweiterten Link nutzt (`Lab.GetLinkModeAsync`).

**F-4: Verbindungsverlust auf dem erweiterten Link im Triggermakro, belegt #138 (P4)**

```
src/LabPrinterTests/run-lab-tests.sh --side-load --fault-injection --filter "FullyQualifiedName~SpiFaultLostConnection"
```

- **Auslöser:** Kein Einzelfehler führt auf dem erweiterten Link zum Verbindungsverlust (`ExtendedLinkRecoversFromEverySingleFault`). Der Injektor kann jetzt mit `times=N` mehrere Austausche derselben Übertragung treffen und mit `counter=N` den Zählerwert wählen. `Any Header ToRrf times=1000 counter=0 flip 96` verfälscht jeden Header von DSF auf dem Weg zu RRF. RRF antwortet jedes Mal `BadHeaderChecksum` (`DataTransfer.cpp:1099-1108`), DSF gibt nach 10 x 11 Header-Austauschen auf (`SPI.cs` `ExchangeHeader`, `PerformFullTransfer`, `MaxSbcRetries` 10) und verliert die Verbindung.
- **Soll mit #138:** RRF setzt die Verbindung zurück und macht einen Not-Halt (`DataTransfer.cpp:1125-1131`, `DataTransfer.h:212-215`, `SbcInterface.cpp:194-230`), Status `halted`, für Zählerwert 0 und 16. **Ohne #138:** Zählerwert 0 schlägt fehl, RRF bleibt in der Sitzung, DSF bricht das Triggermakro ab (`LinkService.cs:333-340`), und RRF hält weitere Triggermakros zurück (`GCodes.cpp:423-433`, `:1000`). Zählerwert 16 besteht.
- **Offline:** `LinkRecovery.LabFaultLosesConnectionAndRrfStops` prüft den Fehler für alle 32 Zählerwerte und vier Übertragungsarten. Den Teil mit dem zurückgehaltenen Trigger bildet der Port nicht nach.
- **Kosten:** Je Fall ein Not-Halt (am Fork auch ATX aus) und `M999`. Bleibt RRF in der Sitzung, DCS-Neustart und `M999`.

### Neue Tests

A-1 bis A-7 und A-10 liefen am 2026-11-07 auf Hardware ([Abschnitt 14](#14-durchgeführte-läufe)), A-8, A-9 und A-11 bis A-16 noch nicht. `src/LabPrinterTests/README.md` nennt zu jedem Test die Klasse und den Aufruf.

| ID | Änderung | Stufe | Ablauf | Soll |
|---|---|---|---|---|
| A-1 | #102 | 2 | Fehler `Activation DataResponse ToRrf word 0x00000006` scharf schalten, `M122 P1001` senden. DCS verbindet neu und aktiviert den Link, dabei trifft der Fehler | `SPI fault injected: ...`, 55 bis 70 s danach `Deactivated successfully` von systemd (`SPI.cs:309-311`, `duetcontrolserver.service:9-10`), neue DCS-Instanz, `Link capabilities All are active`, nach `M999` idle |
| A-2 | #99 | 2 | Fehler injizieren, DCS stoppen, `gpioget --numeric -c gpiochip0 25` lesen, DCS sofort starten | Pin liest 0, sonst ist der Fall ungültig. Verbunden nach höchstens 4,5 s (`SPI.cs:1709`), kein `timeout while waiting for TfrRdy pin` (`SPI.cs:1747`) |
| A-3 | #108 | 2 | Nach einem Boot den Autostart abwarten, `M409` senden, sobald der Flow sichtbar ist | `Starting macro file ... on channel HTTP`, `Self-test: quick check passed`, `M409` antwortet, Maschinen-Tick läuft weiter, `inputs[0]` wieder Tiefe 0, kein `Out-of-order reply` |
| A-4 | #108, `M98.1 S`, `M98.1 W` | 1 | `M98.1` mit `S3`, `S14`, `S5`; `S0` über HTTP; `S4` mit fehlender Datei; ohne `S`; `W-1` | `Invalid code channel number n`, `Use M98 to start a macro on the same code channel`, `Macro file ... not found`, `Missing S parameter`, `Invalid wait time -1` (alle in `MCodeHandler.cs`, case 98) |
| A-5 | Zeilenprüfsummen | 1 | Drei Testmakros mit falschem CRC, Sprung in der Zeilenfolge und fehlendem CRC, jedes setzt je Zeile ein Global | Makro stoppt vor der fehlerhaften Zeile. Journal: `CRC error on line` (`ParserChecksum.cs:370`), `Expected line number` (`ParserAsync.cs:227`), `Missing CRC on line` (`ParserAsync.cs:116`) |
| A-6 | #67 | 2 | SBC-Trigger T30 mit laufendem Zähler, dann `M581.1 T30` mit nie wahrem RRF-Ausdruck | Bericht zeigt den neuen Ausdruck, der Zähler bleibt stehen (Wiki `Gcodes.md:7266`) |
| A-7 | #63 | 2 | SBC-Trigger T30 mit wahrem Ausdruck und `R-1` | Trigger feuert nicht, Gegenprobe mit `R0` feuert |
| A-8 | #88 | 3 | `M112`, Status 5 s beobachten, `M999` | Kein Fehler, `halted` springt nicht zurück, nach `M999` idle (`MCodeHandler.cs:749-770`, Wiki `Gcodes.md:2674`) |
| A-9 | #70 | 3 | `M111 P-1 O1`, einige Codes, `M111 P-1 O0` | DCS-PID unverändert, kein Stack Overflow im Journal (`MCodeHandler.cs:714-735`) |
| A-10 | #72, #82 | 1 | `M36` mit fehlender Datei, `M39`, `M39 S2` | Gültiges JSON `{"err":1,"fileName":...}` (`MCodeHandler.cs:477`), `GB` und `cluster size` (`MCodeHandler.cs:588`), `clsize` (`SDInfoResponse.cs:45`) |
| A-11 | #74, #80 | 3 | Testdatei in `0:/gcodes`, `M30` mit relativem Namen, dann `M30` und `M472` mit fehlendem Ziel | Datei gelöscht, `Failed to delete file ...: file not found` (`MCodeHandler.cs:423`), `Failed to delete file or directory ...: file or directory not found` (`MCodeHandler.cs:911`) |
| A-12 | #81 | 3 | `state.logLevel` und `state.logFile` lesen, `M929 S4`, `M929 S-1`, Zustand wiederherstellen | `parameter 'S' too high` (`MCodeHandler.cs:1195`), `expected non-negative integer after 'S'` (`MCodeHandler.cs:1191`), Protokollierung danach wie vorher |
| A-13 | #76 | 3 | `M606 S1` über HTTP ohne Job | Warnung `this command is valid only when running a job from a stored file` (RRF `GCodes5.cpp:213-216`), `File2` bleibt unverändert, Stack-Tiefe 0 |
| A-14 | RRF #57, DSF #204 | 1 | Testmakro: `#heat.heaters` in ein Global, `global labTestAfterRefusal` auf `false`, dann `global labTestHeaterObjects = heat.heaters`, danach `set global.labTestAfterRefusal = true` | Das Global `labTestHeaterObjects` entsteht nicht, im Journal steht `Cannot assign a value of type 'object' to a variable` (RRF `Variable.cpp:91`), die Zahl der Heizungen ist größer 0. `labTestAfterRefusal` bleibt `false`: Die Zeile nach der abgelehnten Zuweisung läuft nicht (`design/open-findings.md`, L7, ab DSF #204) |
| A-15 | RRF #57 | 1 | Testmakro: eigene Box mit `M291 S1 T0`, `state.messageBox.message` in ein Global, Box mit `M292` schließen, zweite Box öffnen und schließen | Das Global hält danach den Text der ersten Box (RRF `MessageBox.cpp:24`, CString). Zeigt die Maschine eine eigene Box, wird der Fall übersprungen |
| A-16 | RRF #57 | 1 | Testmakro: `port` des ersten Temperatursensors mit Port in ein Global, dann `#` darauf | Das Global hält den Pin-Namen, den `M409` für den Sensor meldet, und `#` liefert dessen Länge (RRF `SensorWithPort.cpp:21`) |

Hinweise:
- **A-1, Fehlerfall:** Auf dem erweiterten Link sagt der Census nach der Aktivierung keinen hängenden Fall voraus (`ExtendedLinkRecoversFromEverySingleFault`). Hängen kann es nur in der Übertragung, die den Link aktiviert (`LinkRecovery.cs:650-654`, 23 Fälle laut #102). Davon kann der Injektor nur `DataResponse ToRrf word 0x00000006` treffen. Dass dieser Fehler mit und ohne Polling zum DCS-Neustart führt, prüft `LabFaultInActivationRestartsDcs` offline.
- **A-1, Injektor:** Die neue Art `Activation` erkennt die Übertragung am Aktivierungspaket in den Daten, die DSF sendet. Sie trifft daher nur die Daten und die Antwort darauf. `SPI.cs` bleibt unverändert. Der Test ist eigenständig, weil der vorhandene Injektionstest nach 2 s selbst wiederherstellt.
- **A-1, Auslöser:** Einen Fehler von vor dem DCS-Start verwirft der Injektor (`SpiFaultInjection.cs:300-305`). Deshalb wird er bei laufendem DCS scharf geschaltet. `M122 P1001` startet nur RRF neu, und DCS fragt die Capabilities nach jedem Verbindungsaufbau und Reset neu ab (`design/sbc-link-requirements.md:123`).
- **A-1, Vorhersage:** Der Hänger trat am 2026-11-07 am DUT wie vom Census vorhergesagt auf. Hängt der Link am DUT einmal nicht, ist das eine Abweichung vom Census. Sie wird als Befund festgehalten. Einen zweiten hängenden Fall, den der Injektor treffen kann, gibt es nicht.
- **A-1, Journal:** Der Test misst den Stopp an der Zeile von systemd. Die Meldung `No transfer with RepRapFirmware completed` (`SPI.cs:311`) stand am 2026-11-07 nicht im Journal, weil journald in dieser Minute über 1,2 Millionen Zeilen unterdrückte und alle Zeilen von DCS dieselbe Priorität hatten. Seit DCS jeder Zeile ihre Syslog-Priorität mitgibt, steht sie als Error im Journal (Priorität 3, eigenes Kontingent bei journald). `Lost connection to Duet` und die Warnungen des hängenden Links sind in A-1 erwartet.
- **A-2, Fehlerfall:** #99 nutzte `RrfData Data ToDsf flip 0`. Laut Census lässt dieser Fall den Pin seit #105 nicht mehr auf LOW. Der Test nutzt `RrfData HeaderResponse ToDsf zero 0 4` und zwei weitere Fälle (`LabFaultLeavesTfrRdyLow`). Am DUT entscheidet die Pinmessung, ohne LOW-Pin ist der Lauf ohne Aussage. Mit dem Fix ist die Prüfung bisher nur auf RRF mp.5 gelaufen, nie auf dem erweiterten Link.
- **A-3, Journal:** Der Autostart endet mit `Aborted macro file`, ein Lauf ohne `A1` mit `Finished macro file`. Das ist kein Fehler: Der automatische Pfad endet mit `M99` (`maintenance/self-test:99-101`). `M99` erscheint im Journal als Abbruch der letzten Datei (`MacroFile.cs:241`), genauso bei den Testmakros von RG-3.
- **A-3, Abdeckung:** Der Lauf vom 2026-11-06 zeigt bereits Start, `quick check passed` und kein `Out-of-order reply`. Offen ist der Fall mit `M409` während des Flows.
- **A-3, `M409`:** Der Test sendet vorher keinen Code auf HTTP, weil der Trigger `M98.1` sonst auslassen würde und der Flow erst eine Minute später käme. Der Flow dauert etwa 1 s, `M409` kann also auch danach ankommen. Sicher abgedeckt ist `M409` während eines Makros durch RG-4 und RG-5.
- **A-4:** `M98.1` steht nicht im Wiki. Das Soll stammt nur aus dem Code und #108.
- **A-5, Makrozeilen:** Ausgaben eines Makros erreichen die `M98`-Antwort nicht (`LabTest.cs:103`). Der Test wertet daher Globals und Journal aus.
- **A-14 bis A-16, RRF #57:** Vor dem Fix hielt eine Variable einen rohen Zeiger ins Objektmodell. Auf dem CHX betraf das `sys/daemon.g:37` (`var fil_prev = move.extruders[0].filament`): Der Vergleich sah einen Filamentwechsel nie. Die Tests nutzen Werte, die sie ohne Eingriff in die Maschine ändern können, statt eines Filamentwechsels. `Tools/variable-test` in RepRapFirmware prüft jede Art von Wert offline. Eine Gegenprobe auf alter Firmware gibt es nicht, weil für einen Test keine andere RRF-Version geflasht wird.

### Kanal-Epoche (Meilensteine E1 und E2), geplant

Stand 2026-11-10: noch nicht gelaufen. Die Änderung liegt auf den Branches `feature/sbc-channel-epoch` in DSF und in RRF und ist auf keinem Drucker. Alle Sollwerte sind **neu**: Sie stammen aus dem Code und aus der Fake-Firmware der Unit-Tests ([sbc-link-v8-requirements.md](sbc-link-v8-requirements.md), Abschnitte 10.8 und 10.9). Der RRF-Teil lief bisher nirgends, er ist nur gebaut und gelesen. Jeder Lauf braucht einen Firmware-Side-Load mit eigener Versionsangabe und Tims Freigabe.

E1 wird mit dem heutigen DSF geprüft (RRF neu), E2 mit beiden Seiten neu.

| ID | Meilenstein | Stufe | Ablauf | Soll |
|---|---|---|---|---|
| EP-1 | E1 | 3 | `AbortsCrossingMacroRequests` mit 2000 Abbrüchen | Keine Neusendung von InvalidateChannel, kein blockierter Kanal. `M122`: "longest abort apply" und "Slowest loop" notieren (offene Frage Q38, Sollgrenze unter 3 s) |
| EP-2 | E1 | 3 | Abbruch, während RRF auf ein Makro wartet: einmal aus einem Trigger, einmal während der Haupt-Task auf das Makro eines anderen Kanals wartet | Kein `Timeout while waiting for macro file` nach 3 s |
| EP-3 | E1 | 4 | Druck läuft auf File. Auf HTTP eine `M291 S2`-Box offen lassen, dann HTTP abbrechen | Der Druck läuft weiter, `M291` antwortet abschließend, kein MessageAcknowledged |
| EP-4 | E1 | 4 | Abbruch eines ruhenden Kanals, der die Bewegungssperre hält; danach Bewegung auf einem anderen Kanal. Zweiter Fall: Abbruch während `G28` zwischen zwei Homing-Dateien | Fall 1: der andere Kanal kann sofort fahren. Fall 2: `G28` behält Sperre und Homing-Liste, kein anderer Kanal fährt dazwischen |
| EP-5 | E1 | 2 | Verbindung trennen (DCS beenden), je einmal mit einem offenen Zustand: geschlossenes Makro noch nicht gemeldet, offene Box, offene Quittung, DoCode noch nicht gesendet, Pause noch nicht gemeldet. Auf SPI und auf USB | Nach dem Neuverbinden kein `Stack underrun` und kein Ereignis der alten Verbindung (Befund K5) |
| EP-6 | E1 | 4 | `M25` kreuzt einen Jobabbruch | Kein PrintPaused nach dem Abbruch, neue Jobcodes werden angenommen, der Pausenzustand ist abgeschlossen |
| EP-7 | E1 | 3 | DoCode vom PanelDue kreuzt einen Abbruch von Aux; `daemon.g` kreuzt einen Abbruch von Daemon | Beide Kanäle arbeiten danach weiter (T11) |
| EP-8 | E2 | 3 | `AbortsCrossingMacroRequests` mit 2000 Abbrüchen, beide Seiten mit Epoche | 0 Out-of-order replies, 0 Neusendungen, 0 blockierte Kanäle. Verworfene Pakete erscheinen nur in den Zählern beider Seiten |
| EP-9 | E2 | 2 | RRF-Neustart, den DSF nicht bemerkt (`M122 P1001`), und DCS-Neustart, den RRF nicht bemerkt. Auf SPI und auf USB | RRF-Neustart, den DSF nicht bemerkt: `config.g` läuft, kein toter Kanal, danach auf jedem benutzten Kanal dieselbe Epoche auf beiden Seiten. DCS-Neustart, den RRF nicht bemerkt: RRF meldet `Lost connection to SBC (the SBC has started a new connection)` und hält an wie bei einem Verbindungsverlust, bis zum Reset (Anforderungen Abschnitt 10.9, D2) |
| EP-10 | E2 | 3 | Ein Abbruch aller Dateien durch RRF selbst kreuzt einen Abbruch durch DSF | Kein Kanal bleibt gesperrt, der Zähler der Sperre ist plausibel |
| EP-11 | E2 | 2 | SPI-Bytes mitschneiden | `config.g` wird nach dem Boot erst nach dem ersten Paket von DSF angefordert und trägt die Epoche 0 (Abschnitt 10.9, D1). DSFs erstes Paket der Verbindung trägt Bit 14. Code-Antworten und lange `M122`-Teile tragen das Tag, im Nachrichtentyp sind die Bits 18 bis 23 und 26 null. PanelDue, USB und Telnet zeigen dieselben Texte wie vorher |
| EP-12 | E2 | 3 | Heutiges DSF gegen das E2-RRF, Regressionsblock 1 | Verhalten wie v7, bis auf die Punkte von E1 |
| EP-13 | E2 | 3 | `M596`/`M606` mit File2, dann Abbruch von File2 | Tags und PrintPaused wie in CE-12 der Anforderungen beschrieben; der Fork von File2 (`GCodeBuffer::ForkFrom`) ist ungeprüft |
| EP-14 | E2 | – | MB6XD und Mini5plus | Nur gebaut, kein Lauf möglich (kein Gerät im Labor) |
| EP-15 | E2 | 4 | `T`-Code an der Konsole, dessen `tfree` ein `abort` enthält | `tpre` und `tpost` laufen danach noch, in dieser Reihenfolge. Kein Code bekommt eine fremde Antwort. `M122` zeigt verworfene Pakete und die Zeile "last dropped" (D3, D10) |
| EP-16 | E2 | 3 | Abbruch direkt nach dem Start einer Homing-Datei, vor ihrer ersten Zeile; je einmal bevor und nachdem eine Achse referenziert ist | Vorher: `Failed to home axes` erscheint als eine vollständige Meldung. Nachher: `G28` läuft mit den übrigen Achsen weiter. Nichts hängt (D3, D4) |
| EP-17 | E2 | 4 | Abbruch des Kanals von außen (IPC `InvalidateChannel`) während der Antastbewegung eines `G30` an der Konsole | Seit 2026-11-25 (upstream 0ca1dff6c, LA-25 entschieden) ruft der Abbruch `AbortStateMachine` auf, und `SetProbing(false)` beendet das Antasten während der Bewegung (open-findings K39). Erwartet nach Code-Lesung: die Antastbewegung endet trotzdem am Taster, im Touch- und im Standard-Modus des SZP; im Standard-Modus gilt danach ein Meldeabstand von 25 statt 2 ms, der nur wirkt, wenn das Handle in den 25 ms davor gemeldet hat. Prüfen: zuerst Standard-Modus mit F200, dann Touch-Modus, je Auslösehöhe mit und ohne Abbruch vergleichen. Nur mit Z referenziert und kleinem Weg |
| EP-18 | E2 | 2 | DCS für 1 bis 3 s anhalten (`SIGSTOP`, dann `SIGCONT`), ohne Druck | DSF meldet `Lost connection to Duet` und baut neu auf. RRF hält an wie bei einem Verbindungsverlust. Nach `M999` läuft `config.g`, die Epochen sind 0 (D2) |
| EP-19 | E2 | 3 | Auf HTTP `M120`, dann Abbruch des Kanals, dann `M121` | `M121` schließt ab |
| EP-20 | E2 | 2 | Boot von RRF bei laufendem DSF, zehnmal | Kein `Timeout while waiting for macro file`. Zeit vom Verbindungsaufbau bis zur Anforderung von `config.g` notieren (D1) |
| EP-21 | E2 | 3 | `M112`, während ein kurzes Makro endet, in einer Schleife mit Reset dazwischen | Jeder Stop kommt bei RRF an, DCS läuft weiter, kein `Stack underrun` im Journal. Nach dem Stop schreibt jeder Kanal InvalidateChannel (D12, K32) |
| EP-22 | E2 | 3 | Abbruch, während ein Code mit Textausgabe läuft | Der Text erscheint als Meldung ohne Code. `Out-of-order replies` bleibt 0, DSFs Diagnose zählt "stale replies shown" (D4) |
| EP-23 | E2 | 2 | DCS hundertmal neu starten, während RRF läuft, je mit und ohne `M999` danach (`ControlServerRestart.NoReplyOfTheOldConnectionAfterRestart`, `LAB_DCS_RESTARTS=100`) | Kein `Out-of-order reply` im Journal nach einem DCS-Start. Mit Debug-Journal: `Dropped Message of an older connection` in den Fällen, in denen RRF noch eine Antwort der alten Verbindung im Transfer hatte (D15, D16) |

Bestehensregel für E2 aus den Anforderungen (Abschnitt 7.2): 2000 Abbrüche mit 0 Out-of-order replies, 0 Neusendungen und 0 Blockaden. Vergleichswert vom 2026-11-09 mit dem erweiterten v7-Link (Abschnitt 14): 2 Neusendungen und 249 leere Out-of-order replies.

### Manuelle Prüfungen (Stufe 4, Tim vor Ort)

| ID | Änderung | Ablauf | Soll |
|---|---|---|---|
| M-1 | Link im Druck, RRF mp.7 | Kurzer Testdruck, `M122` und `M409` in festem Takt, `M122` vorher und nachher | Job endet normal, `lost connections` und `connection resets` +0. `Full transfers per second` als Ausgangswert für R4 notieren (`design/sbc-link-requirements.md:117`) |
| M-2 | 9acd058f | Job bis zum Ende laufen lassen, danach zweiter Job mit `M32` aus einer Jobdatei | `stop.g` läuft, kein `Out-of-order reply`, zweiter Job startet |
| M-3 | #108 | Makro mit `M98.1 S0` starten, dann `M32` senden | `M32` wartet hinter dem Makro, das ist so gewollt (Tim, 2026-11-06) |
| M-4 | DWC mp.6, mp.7 | Am Panel: CHX-Oberfläche, Lüfterdialog (#20), USB-Stick mit Kopieren und Starten (#23), Flow-Panel mit passender Box (#25) | Funktionen wie in den DWC-PRs beschrieben. Starten vom Stick öffnet nur die Jobprüfung, Job nicht freigeben außer in M-1 oder M-2 |

Ein fälliger Wochentest läuft vor dem Job und kann ihn bis zu 2 h für das Abkühlen und bis zu 4 min für den Test verzögern (rpi-image-gen #144). Vor dem Termin `global.self_test_due` lesen.

ESD-Läufe gehören nicht zum Regelumfang und nur auf Tims Entscheidung. Direkte Treffer auf den HMI-USB-Port sind ausgeschlossen (Tim, 2026-11-04).

## 9. Auswertung

Nach jedem Lauf das Journal ab dem Cursor des Laufs lesen und auch auf fehlgeschlagene Starts prüfen.

**Fehlerbefund, sofern der Prüffall ihn nicht erwartet:**
- `Lost connection to Duet` (`SPI.cs:430`, `:446`)
- `Maximum number of SPI transfer retries exceeded` (`SPI.cs:319`)
- `timeout while waiting for TfrRdy pin` (`SPI.cs:1747`)
- `No transfer with RepRapFirmware completed` (`SPI.cs:311`)
- `Failed to start macro file` (`FileFactory.cs:81`), `Macro file ... not found`, `Out-of-order reply` (`Processor.cs:1125`)
- `Failed to process code in stage` (`PipelineStackItem.cs:87`), `DivideByZeroException`
- Neue Datei `sys/transferDump.bin` (`SPI.cs:726`)
- `Connection to controller has been reset` (`LinkService.cs:336`) ohne Neustart oder Reboot

**Erwartet je RRF-Reboot:** je einmal Reset und `RepRapFirmware has left the extended link` (`SPI.cs:1943`), dazu ein `Restarting full transfer because ...` (`SPI.cs:1843`, `:1870`, `:1889`). Einmal, am 2026-11-07, verlor DSF dabei stattdessen die Verbindung (`design/open-findings.md`). Das bleibt ein Fehlerbefund.

**Erwartet in bestimmten Testfällen:**
- RG-3: 20 x `Failed to find corresponding state for code M291 P"never" S3` (`PipelineBase.cs:243`). Das `M291` steht im Testmakro hinter einem `M99` und wird absichtlich nie erreicht (`MacroReturns.cs:20`, `:54`, `:65`).
- RG-7 und jeder andere DCS-Neustart, auch beim Side-Load: `Machine is halted`, bis `M999` folgt (`Lab.cs:280-290`).
- `RefusedMacroPaths` (G2, #132): je verweigertem Pfad `Failed to start macro file 99:/macros/lab-tests/refused/...` oder `... 0:/../lab-tests-refused-...` und `Macro file ... not found`. `run-lab-tests.py` zählt diese Zeilen hier und nicht als Fehlerbefund (`REFUSED_PATH_TESTS`).
- Fehlerinjektion: `Restarting full transfer because ...`, `Bad header CRC32`, `Bad data CRC32`, `Sending transfer #... again` und ähnliche Meldungen, sofern der Fall als `Retry` endet.

**Bekannt, Ursache oder Bewertung offen.** Anzahl notieren; diese Meldungen werden nicht als unbedenklich gewertet:
- `State 'stop-sigterm' timed out` nach dem Boot-`M999`, zuletzt 2026-11-06 um 18:15:14 UTC
- `Failed to find query for object model response` (`LinkService.cs:673`) bei DCS-Neustarts
- `Daemon: Aborting orphaned macro file daemon.g` bei RRF-Resets, 6 am 2026-11-06
- `Daemon: Out-of-order reply ...: ''` (`Processor.cs:1125`) direkt nach einem DCS-Start. Seit 2026-10-29 steht die Meldung 18-mal im Journal, bei 430 DCS-Starts, 17-mal innerhalb von 2 s nach dem Start und immer als leere Antwort auf dem Daemon-Kanal. Vermutlich gehört die Antwort zu einem Code der alten DCS-Instanz, das ist aber nicht belegt. Auf einem anderen Kanal, mit Text oder ohne vorherigen DCS-Start bleibt es ein Fehlerbefund.

**Außerhalb dieses Plans, nur notieren:**
- `IPC#...: Client with outdated protocol version connected (got 12, want 13)` (`IPC/Server.cs:230`) und `dsf-python's ... has no value ...` von den Plugins
- `M307: Heater ... predicted maximum temperature ...` aus der Konfiguration

Das Journal des DUT zeigt zurzeit UTC (`+00:00`). Protokollzeiten immer in UTC angeben.

Keinen Befund ohne Beleg als „bekannt“ einstufen. Ohne Journal-, Code- oder Census-Bezug gilt er als offen.

## 10. Abbruch und Rückbau

Sofort stoppen, wenn:
- ein Job läuft oder jemand am Drucker arbeitet,
- `Lab.RecoverAsync` (`Lab.cs:221-228`) den Drucker nicht zurückbringt. Der Link braucht dann Tim vor Ort, eine Board-Wiederherstellung sogar USB-Kabel und ERASE-Jumper,
- ein Testfall fehlschlägt. Erst das Journal sichern und analysieren, dann wiederholen. Einen Fehler nicht ohne Beleg als sporadisch abtun.

Rückbau prüfen, auch wenn `run-lab-tests.sh` ihn erledigt. Die Auswertung am Ende des Laufs prüft die folgenden Punkte bis auf `M581 T30`, die Testdateien und die Schleifen:
- `findmnt` zeigt keinen Side-Load, `/var/tmp/lab-tests` ist weg.
- `mp-ota-gate.timer` ist aktiv.
- `sys/trigger30.g` und `0:/macros/lab-tests` sind weg, `M581 T30` antwortet `is not configured`.
- Status idle, Versionen wie vor dem Lauf.
- Testdateien und geänderte Zustände aus A-9, A-11 und A-12 sind zurückgesetzt.
- `labTest...`-Globals bleiben bis zum nächsten Reset (README `:22`).
- Keine Schleife läuft weiter. Ein beendetes lokales `plink` stoppt ein entferntes `bash -s` nicht, das muss auf dem SBC beendet werden.

## 11. Protokoll

Ablage in der PR-Beschreibung, bei Prüfungen eines Release-Stands in [Abschnitt 14](#14-durchgeführte-läufe). Neue Befunde kommen nach `design/open-findings.md`.

```
Datum, Zeit (UTC):
Anlass, PR, Commit:
DSF: Version, Side-Load ja/nein, Commit:
RRF: Version:
Stufen und Freigabe durch:
IDs: bestanden / fehlgeschlagen / übersprungen / nicht injiziert:
RG-6, Erholzeit je Reboot:
M122 DSF vorher/nachher: Full transfer restarts, header retries, data retries, sent again, requested again, extended left, lost connections, connection resets:
M122 RRF vorher/nachher: failed transfers, checksum errors, underruns, overruns, disconnects, timeouts:
Self-Test im Laufzeitraum (Uhrzeit, Ergebnis):
Journalbefunde mit Uhrzeit:
Abweichungen und Folgeaufgabe:
Rückbau geprüft:
```

## 12. Nicht abgedeckt

- **Anforderungen:** R3, R4, R5 sowie R7 bis R14 sind nicht umgesetzt oder nicht gemessen (`design/sbc-link-requirements.md:116-122`). Der Plan prüft sie nicht.
- **Kompatibilität:** Fork-DSF mit RRF ohne Capabilities oder mit Archiv-RRF wird am DUT nicht geprüft, weil die Suite nur gegen die aktuelle Firmware läuft. Das deckt nur der Census ab. Mit Archiv-RRF lief diese Kombination nie auf Hardware (#104). Mini 5+ und MB6XD sind nicht am DUT.
- **Bekannte offene Punkte ohne Test:**
  - USB-Kanäle und G/M als Makroparameter stehen in `design/open-findings.md`.
  - Stall vom 2026-11-02 um 22:13 UTC, TfrRdy-Flanken-Census, 2 von 42 DCS-Neustarts mit Stock-RRF und Hänger nach Firmware-Update sind ungeklärt.
  - Zwei parallele `M122`-Schleifen lösten am 2026-11-04 eine `DivideByZeroException` aus. Die Division ist unverändert (`SPI.cs:218`) und steht noch nicht in `design/open-findings.md`.
- **RRF-Fixes, die am CHX nicht erreichbar sind** (Stand RRF `3.7.0+1.mp.11-dev.1`):
  - RRF #79 (Binärcode über 256 Bytes nach einem Text-Code): Text-Codes kommen im SBC-Modus nur von USB, Aux und von RRF selbst, am CHX nur aus Trigger-Aktionen (`M581.1 … U`, RRF `GCodes.cpp:1044-1054`). Der CHX hat keine, seine Trigger starten Makrodateien. Geprüft durch `Tools/sbc-code-length-test` in RepRapFirmware.
  - RRF #58 (fehlgeschlagenes Makro, auf das kein Kanal wartet): Nur der Daemon-Kanal wartet nicht auf ein angefordertes Makro (RRF `GCodeBuffer.cpp:1334`), und DSF meldet einen Makrofehler nur für eine fehlende Datei (`Processor.cs:1730`). Der CHX hat eine `daemon.g`. Ein Test müsste sie entfernen, das wäre Stufe 3 und träfe den Fehler bei einem Versuch alle 10 s nur statistisch.
  - Races mit `malloc` und dem Objektmodell (RRF #70, #72 bis #77, RRFLibraries #2, Duet3Expansion #20): Nur durch Host-Tests und alloc-check deterministisch geprüft, am DUT nur als "kein Reset".
  - Bewegung, Feed Hold, Endstops, Z-Sonde und Emergency Pause (RRF #56, #63, #66, #83, #84, Duet3Expansion #24) brauchen Stufe 4. Am 2026-11-23 liefen davon #66 (als Regressionsprüfung) und #83 (Abschnitt 14). #84 braucht Power-Fail oder Stall-Pause, #56 eine Antastfahrt ohne Auslösung mit `M558 P` und einer Änderung eines CAN-Eingangs, #63 `M672`, das der CHX nicht nutzt.
- **Andere Repos:** Inhalt des Self-Tests (chx350-config) und USB-Guard, OTA sowie Panel-Fehlerbilder (rpi-image-gen) brauchen eigene Prüfpläne. Hier wird nur die Schnittstelle geprüft.

## 13. Offene Entscheidungen

| Nr. | Frage | Empfehlung |
|---|---|---|
| 1 | Wann ist der Termin für die Stufe-4-Prüfungen? | Nach Block 1 bis 3 |

Entschieden (Tim, 2026-11-06): Alle Stufe-3-Tests werden gebaut (A-8, A-9, A-11, A-12, A-13).

## 14. Durchgeführte Läufe

### 2026-11-06: Block 1, bestanden

```
Datum, Zeit (UTC): 2026-11-06, 19:34:46 bis 19:43:29
Anlass, PR, Commit: Erstdurchlauf des Release-Stands, Tag v3.7.0+mp.12 (b4207c0f)
DSF: 3.7.0+mp.12 aus dem Image, kein Side-Load
RRF: 3.7.0+1.mp.7
Stufen und Freigabe durch: 1 und 2, Tim
IDs: E-1 bis E-7 bestanden; RG-1 bis RG-16: 16 bestanden, 0 fehlgeschlagen, 0 übersprungen (8,3 min)
RG-6, Erholzeit je Reboot: 12,9 / 12,1 / 12,0 / 12,0 / 12,0 s
M122 DSF vorher: alle Zähler 0
M122 DSF nachher (seit DCS-Start 19:36:21): Full transfer restarts 5, header retries 0, data retries 0, sent again 0, requested again 0, extended left 5, lost connections 0, connection resets 5
M122 RRF vorher und nachher (je seit dem letzten Boot): failed transfers 0, checksum errors 0, underruns 0, overruns 0, disconnects 0, timeouts 0
Self-Test im Laufzeitraum: 19:39:29 bis 19:39:30, quick check passed, keine Störung (Abschnitt 5)
Journalbefunde: kein Fehlerbefund aus Abschnitt 9. Je Reboot 1 Reset, 1 "left the extended link", 1 Neustart der Übertragung (3 x 0xff, 2 x 0x00000000). 0 x stop-sigterm. 20 x M291 P"never" (RG-3), 4 x "Machine is halted" (RG-7), 6 x "Aborting orphaned macro file daemon.g"
Abweichungen und Folgeaufgabe: Run 1 lag mit 12,9 s über der Referenz. Im Journal ist Run 1 unauffällig: Reset bis Ende config.g 9,57 s (übrige 9,48 bis 9,54 s), Abstand der Resets 17,2 bis 17,4 s. Die Mehrzeit liegt also außerhalb dieses Intervalls. Die Ursache ist nicht belegt, der Wert wird am Client über den Tunnel gestoppt. Beim nächsten Lauf beobachten
Rückbau geprüft: ja, 19:43:58 (kein Side-Load, OTA-Timer aktiv, Trigger 30 und Testmakros weg, idle)
```

Weitere Werte:
- **`M98.n`:** Aux, Telnet und Aux2 je 11 Trigger-Läufe, 1 Makro, 10 Ablehnungen, HTTP-Code in 0,1 s. HTTP 21 Trigger-Läufe, 1 Makro, 20 Ablehnungen, HTTP-Code in 4,8 s. Parameter überall `5,2,5,true`.
- **RG-9:** 242 Trigger-Läufe.
- **Link:** `Full transfers per second` 1803 vor und 1774 nach dem Lauf, jeweils idle.

### 2026-11-06: Block 2 und 3, bestanden

```
Datum, Zeit (UTC): 2026-11-06, 20:11:36 bis 21:26:07
Anlass, PR, Commit: Erstdurchlauf des Release-Stands. RG-17 auf dem Image, F-1 und F-2 mit dem Injektions-Build von b4207c0f als Side-Load
DSF: 3.7.0+mp.12
RRF: 3.7.0+1.mp.7
Stufen und Freigabe durch: 1 und 2, Tim
IDs: RG-17 bestanden (30 min). F-1: 15 von 15 Retry, 0 nicht injiziert, 0 Abweichungen. F-2: 672 von 672 Retry, 0 nicht injiziert, 0 Abweichungen (39,5 min)
M122 DSF, RG-17 vorher und nachher unverändert: Full transfer restarts 5, header retries 0, data retries 0, sent again 0, requested again 0, extended left 5, lost connections 0, connection resets 5 (Stand aus Block 1)
M122 DSF, F-1 und F-2: nicht auswertbar, der Runner startet DCS beim Rückbau neu
M122 RRF, RG-17 nachher: failed transfers 0, checksum errors 0, underruns 0, overruns 0, disconnects 0, timeouts 0
Self-Test im Laufzeitraum: F-1 20:44:03 bis 20:44:05, F-2 20:46:38 bis 20:46:39, je während der Injektion, quick check passed, keine Abweichung. In RG-17 kein Lauf
Journalbefunde RG-17: keine Warnung, kein Fehler
Journalbefunde F-1: 15 x injiziert, 10 x Bad data CRC32, 5 x Bad header CRC32, 0 x Restarting full transfer, 0 x Lost connection, 0 x Reset
Journalbefunde F-2: 672 x injiziert, 118 x Restarting full transfer, 22 x Bad header CRC, 8 x Bad data CRC, 26 x Sending transfer again, 0 x Lost connection, 0 x Reset, 0 x left the extended link, 0 x TfrRdy-Timeout, 0 x stop-sigterm
Abweichungen und Folgeaufgabe: 1 x "Daemon: Out-of-order reply (flags DaemonMessage, BinaryCodeReplyFlag): ''" um 21:25:42,541, 11 ms nach dem Verbindungsaufbau der neuen DCS-Instanz beim Rückbau. Die letzte Injektion war 15 s vorher (21:25:27). Gleiches Muster wie im Journal seit 2026-10-29, siehe Abschnitt 9. Ursache nicht belegt
Rückbau geprüft: ja, 21:26:04 (kein Side-Load, /var/tmp/lab-tests weg, OTA-Timer aktiv, idle, alle Erweiterungen running, Versionen wie vorher)
```

### 2026-11-07: Block 4, A-Prüfungen bestanden, ein offener Fehlerbefund aus RG-6

Nach der Bestehensregel ([Abschnitt 4](#bestehensregel)) ist kein Lauf des Standardumfangs bestanden: Lauf 1 hat einen fehlgeschlagenen Fall, Lauf 2 einen Fehlerbefund im Journal. Der Fehlschlag war ein Testfehler und ist behoben. Den Fehlerbefund entscheidet Tim.

```
Datum, Zeit (UTC): 2026-11-07, 07:14:17 bis 08:07:57
Anlass, PR, Commit: Änderungsprüfung A-1 bis A-7 und A-10 aus #114. Testcode: Lauf 1 9b3a70c5, Injektion 1 c866895b, Injektion 2 und Lauf 2 26bf5a7c
DSF: 3.7.0+mp.12. Standardläufe aus dem Image ohne Side-Load, A-1 und A-2 mit dem Injektions-Build als Side-Load
RRF: 3.7.0+1.mp.7
Stufen und Freigabe durch: 1 und 2, Tim
IDs: Block 0: 827 bestanden, 0 fehlgeschlagen, 6 übersprungen. E-1 bis E-7 bestanden
IDs, Lauf 1 (07:16:51 bis 07:28:16, 11,2 min): 30 Fälle, 29 bestanden, 1 fehlgeschlagen (RG-13, Testfehler), 0 übersprungen
IDs, Wiederholung RG-13 bis RG-16 (07:31:55 bis 07:42:13): 6 Durchgänge, 24 von 24 bestanden
IDs, Injektion 1 (07:42:47 bis 07:47:46): A-2 bestanden, A-1 fehlgeschlagen (Testfehler)
IDs, Injektion 2 (07:50:22 bis 07:55:36): A-1 und A-2 bestanden
IDs, Lauf 2 (07:56:29 bis 08:07:57, 11,2 min): 30 Fälle, 30 bestanden, 0 übersprungen, 1 Fehlerbefund im Journal
RG-6, Erholzeit je Reboot: Lauf 1 12,8 / 12,0 / 12,0 / 12,0 / 12,0 s. Lauf 2 12,8 / 12,1 / 12,0 / 12,6 / 12,1 s
M122 DSF vorher: jeweils alle Zähler 0
M122 DSF, Lauf 1 nachher: Full transfer restarts 6, header retries 0, data retries 0, sent again 0, requested again 0, extended left 6, lost connections 0, connection resets 6
M122 DSF, Lauf 2 nachher: Full transfer restarts 16, header retries 90, data retries 0, sent again 0, requested again 0, extended left 5, lost connections 1, connection resets 6
M122 DSF, Injektion: nicht auswertbar, der Runner startet DCS beim Rückbau neu
M122 RRF nach beiden Läufen (seit dem letzten Boot): failed transfers 0, checksum errors 0, underruns 0, overruns 0, disconnects 0, timeouts 0
Self-Test im Laufzeitraum: Lauf 1 07:24:35 bis 07:24:37. Lauf 2 08:03:05 bis 08:03:06 und 08:04:17 bis 08:04:18. Jeweils quick check passed, keine Störung
Journalbefunde Lauf 1: kein Fehlerbefund aus Abschnitt 9. 20 x M291 P"never" (RG-3), 2 x "Machine is halted" (RG-7), 7 x "Aborting orphaned macro file daemon.g", 3 x "Error while reading code from macro" (A-5), 0 x stop-sigterm, 0 x Out-of-order
Journalbefunde Lauf 2: Fehlerbefund 1 x "Lost connection to Duet (Maximum number of SPI transfer retries exceeded)" um 07:59:38,203 beim Reboot von RG-6 Run 4, ohne Injektion, Verbindung nach 500 ms wieder da. Sonst wie Lauf 1
Journalbefunde Injektion 2: A-1 wie erwartet 1 x Lost connection, 1,19 Millionen unterdrückte Zeilen. 1 x "Failed to find query for object model response" beim Rückbau (Abschnitt 9)
Abweichungen und Folgeaufgabe: zwei Befunde in design/open-findings.md ("A stuck link floods the journal", "A firmware restart can leave DSF and RRF one exchange apart"). Entscheidung liegt bei Tim
Rückbau geprüft: ja, zuletzt 08:13:28 (kein Side-Load, /var/tmp/lab-tests weg, OTA-Timer aktiv, Trigger 30 und Testmakros weg, idle, alle Erweiterungen running, Versionen wie vorher)
```

Ergebnisse je Prüfung:
- **A-1:** Der Link hing wie vom Census vorhergesagt. Zweimal stoppte DCS 60,03 s nach der Injektion, systemd startete es neu, nach `M999` idle. Die Meldung `No transfer with RepRapFirmware completed` fehlte beide Male im Journal.
- **A-2:** Beim ersten Fehlerfall `RrfData HeaderResponse ToDsf zero 0 4` stand der Pin auf LOW. Der neue DCS verband sich nach 3,84 und 3,88 s, ohne TfrRdy-Timeout. Damit lief der Fix von #99 erstmals auf dem erweiterten Link.
- **A-3:** Der Autostart lief, `M409` wurde beantwortet, `trigger8.g` lief weiter. Ob `M409` während des Flows ankam, zeigt der Lauf nicht, siehe die Hinweise zu A-3.
- **A-4, A-5, A-6, A-7, A-10:** bestanden wie in der Tabelle beschrieben.

Testfehler, behoben in #114:
- **RG-13 (`busy False`):** Der Test las den Status einmal, sobald das Makro in `inputs[]` stand. DSF holt den Live-Status vor dem Schlüssel `inputs` (`UpdateService.cs:282`, `:296`), `stackDepth` ist kein Live-Feld (RRF `GCodeBuffer.cpp:76`), und der Trigger-Kanal zählt nicht als busy (RRF `GCodes.cpp:381`). Der Test wartet jetzt bis zu 2 s und schreibt den ersten Status mit. Gemessen: in 28 Stichproben zweimal zuerst `Idle`, `Busy` 102 ms später.
- **RG-13 bis RG-16, nur bei Wiederholung:** `MacrosOnOtherChannels` und `TriggerMacros` teilten sich die Global `labTestTriggerRuns`, sodass `reset.g` nach den Trigger-Tests ohne Reboot die übrigen Globals nicht anlegte. Die Kanal-Tests haben jetzt eine eigene Global.
- **A-1:** Der Test suchte die Meldung von `SPI.cs:311`, die journald verwarf. Er misst den Stopp jetzt an der Zeile von systemd.

Weitere Werte:
- **`M98.n`:** in allen Läufen Parameter `5,2,5,true`, 1 Makro je Fall, Trigger-Läufe = Makro + Ablehnungen.
- **RG-9:** 242 und 241 Trigger-Läufe.

### 2026-11-07: RRF-Fix für den Neustart vor dem nächsten Austausch, Side-Load, bestanden

Prüfung des RRF-Fixes aus `fix/sbc-decode-header-unconnected` (Befund „A firmware restart can leave DSF and RRF one exchange apart“, `design/open-findings.md`) vor dem PR, auf Tims Wunsch per Side-Load.

```
Datum, Zeit (UTC): 2026-11-07, 11:13:53 bis 11:40:25
Anlass, PR, Commit: RRF-Fix vor dem PR. DSF aus tmp/lab-restart-fix (lokal, #114 4e28df23 + feature/census-restart-before-arm 1220f18d), Produktionscode wie v3.7-dev
DSF: 3.7.0+mp.12, Side-Load je Schritt, für A-1/A-2/F-1 der Injektions-Build
RRF: 3.7.0+1.mp.7-dev.1 (19de9c0e6, CI-Run 42365694522, sha256 9ff56cc0...00b4), Side-Load der RRF-Sitzung mit fw-sideload.sh
Stufen und Freigabe durch: 1 und 2, Tim (RRF-Side-Load in der RRF-Sitzung freigegeben)
IDs: Standardlauf 30 von 30 bestanden (11:13:53 bis 11:24:35). Injektions-Build 3 von 3 (11:24:36 bis 11:29:25): A-1, A-2, F-1. LinkRecoversAfterWatchdogReset mit LAB_WATCHDOG_RESTARTS=40, 1 von 1 (11:29:26 bis 11:40:25)
RG-6, Erholzeit je Reboot: Standardlauf 12,5 / 12,1 / 12,2 / 12,0 / 12,0 s. 40 Neustarts: 12,0 bis 12,5 s, Median 12,1 s
M122 DSF: nicht auswertbar, der Runner startet DCS beim Rückbau neu
M122 RRF nach den 40 Neustarts: failed transfers 0, checksum errors 0, underruns 0, overruns 0, disconnects 0, timeouts 0
Self-Test im Laufzeitraum: 11:20:57 bis 11:20:58, quick check passed. In den 40 Neustarts kein Lauf, sie folgen sich alle 17 s
Journalbefunde 40 Neustarts: je Neustart genau 1 Reset, 1 "left the extended link" und 1 Neustart der Übertragung (28 x 0xff, 12 x 0x00000000). 0 x Lost connection, 0 x Bad header CRC16, 0 x Out-of-order, 0 x stop-sigterm
Journalbefunde Standardlauf: 6 Resets, 20 x M291 P"never" (RG-3), 4 x "Machine is halted", 7 x "Aborting orphaned macro file daemon.g", 1 x "Failed to find query" (Abschnitt 9). Dazu 1 Fehlerbefund: "Failed to process code in stage Executed" um 11:24:14 beim Rückbau, siehe unten
Journalbefunde Injektions-Build: A-1 wie erwartet (1 x Lost connection, Stopp 60,03 s nach der Injektion), A-2 TfrRdy LOW, verbunden nach 3,88 s, F-1 15 von 15 Retry, 0 Abweichungen
Abweichungen und Folgeaufgabe: "Failed to process code in stage Executed" (TaskCanceledException in EventLogger.LogAsync) beim Herunterfahren von DCS, Befund in design/open-findings.md
Rückbau geprüft: ja, 11:40:25 (kein DSF-Side-Load, /var/tmp/lab-tests weg, OTA-Timer aktiv, idle). Der RRF-Side-Load bleibt bis zum nächsten SBC-Boot oder fw-sideload.sh remove, Entscheidung bei Tim
```

Bewertung: Ohne den Fix traf die Schleife etwa einen von 40 Neustarts. 40 saubere Neustarts mit 12 Läufen über das Boot-Fenster sind ein Hinweis, kein Beweis. Den Fix zeigen der Census und der Abgleich mit dem echten RRF-Code (`PortMatchesRealFirmwareAfterRestartBeforeArm`).

### 2026-11-08: Fixes #132 bis #138 und F-3/F-4, Side-Load, bestanden

Prüfung der Fixes der Fuzz-Befunde vor dem Merge, auf Tims Wunsch auf dem Kombi-Stand. Neue Tests aus #139 (G2, T3, I1) und #140 (F-3, F-4), jeweils mit und ohne Fix.

```
Datum, Zeit (UTC): 2026-11-08, 11:38:00 bis 12:56:36
Anlass, PR, Commit: #131 bis #138 vor dem Merge. Kombi-Stand bdab8815 (v3.7-dev 9eef94b6 + #131 bis #138, ohne Konflikte), dazu #139 (f17fe3a3, Wiederholung 607d9875) und #140 (mit Fix 407ca63b = #140 + #137 + #138, ohne Fix 540f360c, Wiederholung 8481db06)
DSF: 3.7.0+mp.13 im Image, Side-Load je Lauf, für F-3/F-4 der Injektions-Build, für F-3 mit --plain-link
RRF: 3.7.0+1.mp.8 (Image), kein RRF-Side-Load
Stufen und Freigabe durch: 2, Tim (Side-Load und Fehlerinjektion "jetzt, Kombi-Stand"; Stock-Gegenprobe für G2 und T3)
Lauf 1, Standardlauf mit Kombi-Stand (11:38:00 bis 11:50:54): 30 von 30 bestanden
RG-6, Erholzeit je Reboot: 12,9 / 12,0 / 12,0 / 12,1 / 12,1 s
M122 DSF und RRF: nicht auswertbar, der Rückbau startet DCS neu und setzt RRF mit M999 zurück (letzter Reset 20 s vor dem Auslesen)
Self-Test im Laufzeitraum: 2 gestartet, 2 bestanden
Journalbefunde Lauf 1: 6 Resets, 6 x "left the extended link", 5 Neustarts der Übertragung, 20 x M291 P"never" (RG-3), 6 x "Machine is halted", 7 x "Aborting orphaned macro file daemon.g", 1 x "Failed to find query", 2 x "Daemon: Out-of-order reply" je innerhalb 300 ms nach einem DCS-Start (bekanntes Muster, auch mit dem Image-DCS). Keine Rechte-Ablehnung im Journal von DCS und DPS. DPS: 16 x "Failed to send console message to DCS", nur während DCS nach M999 gestoppt war (StartPlugin.cs:220-228)
F-3 mit #137 (12:17:13 bis 12:21:06): 12 Fälle, Retry 10, Reconnect 2 (die zwei Kontrollen, die in beiden Ständen brechen), 0 Abweichungen vom Census
F-3 ohne #137 (12:21:10 bis 12:31:28): Reconnect 8, Retry 4, 0 Abweichungen. Die 6 P1-Fälle (BothData/DsfData HeaderResponse ToDsf flip 2, word 0x00000005, word 0xfefefefe) brechen ohne Fix und laufen mit Fix per Retry weiter
F-4 mit #138 (12:31:42 bis 12:34:44): Zähler 0 und 16 bestanden, RRF hält an, Trigger 30 läuft nach M999 wieder
F-4 ohne #138 (12:34:48 bis 12:38:48, Wiederholung 12:52:11 bis 12:56:36): Zähler 0 durchgefallen wie vorhergesagt: "RRF did not stop after DSF lost the connection: DSF aborted the trigger macro, M409 of the trigger's globals got no answer, trigger30.g started 0x since the reconnect, status Idle". Zähler 16 bestanden
#139 mit Kombi-Stand (12:39:16 bis 12:43:05, Wiederholung G2 12:49:36 bis 12:52:07): T3 bestanden (Trigger 10 x in 5 s, 496 / 468 Bytes frei), I1 2 von 2 (CHX350, Vigil, QualityAssurance wieder gestartet, 36 von 36 Endpunkten, 0 Ablehnungen; DELETE /machine/systemPackage: 500 "Root plugin support has been disabled"), G2 3 von 3 in der Wiederholung
#139 gegen das Image (12:43:09 bis 12:46:57): G2 3 von 3 durchgefallen (DCS gestoppt, PID 0 danach), T3 durchgefallen (Trigger 0 x in 5 s, 324 Bytes frei)
Abweichungen und Folgeaufgabe: zwei Testfehler, beide behoben und wiederholt: RefusedPathInMacroEndsLikeMissingFile erwartete den Fehler des inneren M98 in der HTTP-Antwort (c149ac9b liest ihn aus dem Journal), F-4 endete ohne Fix mit dem HTTP-Timeout statt mit dem Befund (8481db06). Neu und offen: ohne #138 beantwortete RRF auch kein M409 auf HTTP, solange es das verworfene Triggermakro hielt (design/open-findings.md, P4)
Rückbau geprüft: ja, nach jedem Lauf (kein Side-Load, /var/tmp/lab-tests weg, OTA-Timer aktiv, sys/trigger30.g und 0:/macros/lab-tests weg, idle, Versionen wie vorher)
```

Bewertung: Jeder Fix zeigt sich auf der Hardware, und jede Gegenprobe ohne Fix zeigt den Fehler. T2 (#134) lief nicht: RRF mp.8 hat T1 noch, dessen Fenster direkt vor dem von T2 liegt. Tim: erst nach dem T1-Fix in RRF. Dass bei T3 mit Fix weniger Platz frei war als geschätzt, passt dazu, dass RRF den größten zusammenhängenden freien Block meldet (SbcInterface.cpp:1376); gemessen ist das nicht.

### 2026-11-08 abends: Shutdown-Probe und Prüfung von #149, bestanden

Ursache des 15-s-Hängers beim Stoppen von DCS (#147) auf der Hardware gesucht, dann den Fix (#149) geprüft. Probe `ShutdownHangProbe`: ein temporärer Labortest, nicht eingecheckt. Er lässt DCS über eine bind-gemountete Kopie von `config.json` mit `LogLevel` debug laufen und misst jeden Stopp von DCS.

```
Datum, Zeit (UTC): 2026-11-08, 17:15:36 bis 17:17:28 (Lauf 1, abgebrochen), 17:20:15 bis 17:29:36 (Lauf 2), 19:56:29 bis 20:05:45 (Lauf 3, mit #149)
Anlass, PR, Commit: Ursache des Hängers (#147), dann #149. Lauf 1 und 2: Injektions-Build von v3.7-dev 9e6449d1. Lauf 3: Injektions-Build von fix/shutdown b2a6affd
DSF: 3.7.0+mp.13 im Image, Side-Load je Lauf
RRF: 3.7.0+1.mp.8 (Image)
Stufen und Freigabe durch: 2, Tim (Debug-Lauf "ja, jetzt", Prüfung von #149)
Ablauf je Lauf: 3 x systemctl restart, 3 x M999, 2 x festhängender Link ("Activation DataResponse ToRrf word 0x00000006" + M122 P1001) mit systemctl restart 5 s danach
Lauf 1: abgebrochen nach 4 s. Das Skript las config.json ohne sudo. Die Debug-Kopie blieb gemountet, der Abschnitt-10-Check des Runners kennt keine Config-Mounts. Von Hand entfernt (umount, DCS-Neustart, M999), danach LogLevel info und 0 Debug-Zeilen des laufenden DCS geprüft
Lauf 2, ohne Fix: restart 0,03 / 0,05 / 0,05 s, M999 1,04 / 1,05 / 1,05 s, festhängender Link 15,14 / 15,18 s mit "State 'stop-sigterm' timed out. Killing." und SIGKILL. DCS meldete im Stopp etwa alle 12 ms "Lost connection to Duet / Maximum number of SPI transfer retries exceeded" aus SPI.PerformFullTransfer
Lauf 3, mit #149: restart 0,04 / 0,05 / 0,05 s, M999 1,04 / 1,05 / 1,07 s, festhängender Link 0,03 / 0,03 s, nie abgeschossen
Journal: journald unterdrückte in Lauf 2 etwa 1,4 Millionen Zeilen (Debug-Flut des festhängenden Links). "Application is shutting down..." steht für den zweiten festhängenden Stopp im Journal, für den ersten vermutlich unterdrückt
Abweichungen und Folgeaufgabe: Lauf 1 (siehe oben). Ein Labortest, der etwas mountet, muss seinen Rückbau selbst prüfen, der Runner prüft nur die DLL-Mounts
Rückbau geprüft: ja, nach Lauf 2 und 3 (config.json nicht gemountet, LogLevel info, /var/tmp/lab-tests weg, DCS aktiv, idle)
```

Bewertung: Auf der Hardware zeigt sich von den drei offline gezeigten Mechanismen des Hängers nur der festhängende Link: Die Übertragung wartete auf `ApplicationStopped` statt auf das Stopp-Token. Mit #149 endet der Stopp sofort. Die beiden anderen Mechanismen (ein Code eines anderen Clients beim Stopp, ein Code in der Sekunde nach M999) traten in diesen Läufen nicht auf. Das Warten auf ein laufendes Firmware-Update beim Stoppen (#154) ist nicht auf der Hardware geprüft, dazu bräuchte es einen Flash.

### 2026-11-08 nachts: T2 nach dem I8-Fix, nicht bestanden (Fund T12)

Wiederholung des T2-Labortests `AbortsCrossingMacroRequests` mit dem RRF-Testbuild, der T1 enthält. Der erste Lauf mit diesem RRF hatte nichts abgebrochen, weil DCS `InvalidateChannel` über IPC ablehnte (I8).

```
Datum, Zeit (UTC): 2026-11-08, 22:00:01 bis 22:02:36
Anlass, PR, Commit: I8-Fix und T2 (#134), fix/ipc-invalidate-channel 701bc21d
DSF: 3.7.0+mp.13 im Image, Side-Load von 701bc21d
RRF: 3.7.0+1.mp.9-dev.1 (3.7-dev fd4324368 mit RRF #43 und #44), Firmware-Side-Load
Stufen und Freigabe durch: 2 und 3 (M111 P-1 des Tests), Tim ("I8 fixen, T2 jetzt wiederholen")
Ablauf: run-lab-tests.sh --side-load, Filter ChannelWorksAfterAbortsCrossingMacroRequests, Telnet, short.g = G4 P1
Ergebnis: FAIL nach 47 s. Das Skript brach mit BrokenPipeError ab, weil DCS um 22:01:21.49 stoppte: "BackgroundService failed, System.InvalidOperationException: Stack underrun on channel Telnet" in Processor.Pop ← MacroFileClosed ← LinkService.HandleMacroFileClosed. systemd startete DCS neu, der Test erholte sich mit Neustart und M999
Vorher im Journal: mindestens ein verworfener Makro-Request des T2-Fixes ("was requested before RRF got the abort of all files"), kein "none was requested", G4 P0 nach einem Abbruch endete mit "Error: M98: Macro file 0:/macros/lab-tests/abort/short.g not found" (T10), 15 Out-of-order replies, 2 x "Failed to find suitable stack level for flush request"
Journal: ab 22:01:19.36 fehlen die Debug-Zeilen von DCS, nur Info und Warnungen sind da (vermutlich die Ratenbegrenzung von journald), die Zählungen sind daher untere Grenzen
M122: alle Link-Zähler 0 -> 0
Rückbau geprüft: ja (kein Side-Load, /var/tmp/lab-tests weg, OTA-Timer aktiv, sys/trigger30.g und 0:/macros/lab-tests weg, idle, Versionen wie vorher)
```

Bewertung: Der T2-Fix wirkt auf der Hardware, und T1 trat nicht auf. Der Lauf fand T12: Die Meldung einer geschlossenen Makrodatei (`MacroFileClosed`), die einen Abbruch kreuzt, lässt den Stack von DSF unterlaufen, und die Ausnahme stoppt DCS (`design/dsf-findings-upstream.md`, T12). Fix in DSF (Fenster von #134) und RRF #45. Der Test muss mit einem RRF mit #45 wiederholt werden. T10 ist nicht nur kosmetisch: Die Antwort des abgebrochenen M98 ging an den nächsten Code.

### 2026-11-08 nachts: T2 mit RRF #45, nicht bestanden (Fund T11, Neusendung)

```
Datum, Zeit (UTC): 2026-11-08, 22:36 Flash, 22:37:13 bis 22:40:34 Lauf
Anlass, PR, Commit: Wiederholung von T2 nach #158 und RRF #45, DSF v3.7-dev 071c2b8f
DSF: 3.7.0+mp.13 im Image, Side-Load von 071c2b8f
RRF: 3.7.0+1.mp.9-dev.2 (3.7-dev 9678339c8 mit RRF #43, #44, #45), Firmware-Side-Load (DuetControlServer -u, Erweiterungen unverändert mp.4)
Stufen und Freigabe durch: 2 und 3 (M111 P-1 des Tests), Tim ("yes both": dev.2 side-loaden und T2 wiederholen)
Ablauf: run-lab-tests.sh --side-load, Filter ChannelWorksAfterAbortsCrossingMacroRequests, Telnet, short.g = G4 P1
Ergebnis: FAIL nach 1 min 7 s. 165 Abbrüche liefen durch (Histogramm 0 bis 11,1 ms, alle "done"), in Durchlauf 165 (1,4 ms) bekam der Probe-Code G4 P0 keine Antwort. Kein Absturz von DCS mehr (T12 behoben)
Journal: 22:39:02.728 "Resending packet #0 (request InvalidateChannel)", in derselben Millisekunde "Starting macro file ... short.g on channel Telnet", 22:39:02.730 RRF "Macro file has been started on channel Telnet but none was requested" und "M98: Macro file ... not found". 67 Out-of-order replies (T10). Debug-Zeilen wieder lückenhaft (journald)
Rückbau geprüft: ja (kein Side-Load gemountet, idle, Versionen wie vorher). RRF dev.2 bleibt auf dem Board (Tim: bis zum nächsten Testbuild)
```

Bewertung: T12 ist auf der Hardware behoben. Der neue Block ist der zweite Fall von T11: Während ein M98 auf sein Makro wartet, hält die Hauptaufgabe von RRF die Pufferfreigabe, InvalidateChannel wartet nur 10 ms darauf und lässt sich neu senden. Die Kopie verwirft den Code, den DSF dazwischen geschickt hat. Fix in DSF (Kanal wartet, bis RRF den Abbruch angenommen hat), dazu der RRF-Fix für T10. Danach Wiederholung mit dem nächsten Testbuild.

### 2026-11-09 morgens: T2 mit RRF #46 (dev.3), bestanden

```
Datum, Zeit (UTC): 2026-11-09, 06:19 Flash, 06:20:15 bis 06:23:13 Lauf 1 (600 Abbrüche), 06:23:59 bis 06:28:59 Lauf 2 (2000 Abbrüche, LAB_ABORT_ITERATIONS=2000)
Anlass, PR, Commit: Prüfung von #161 (T11 Neusendung) und RRF #46 (T10), DSF v3.7-dev 808d56fa
DSF: 3.7.0+mp.13 im Image, Side-Load von 808d56fa
RRF: 3.7.0+1.mp.9-dev.3 (3.7-dev 9678339c8 + RRF #46 0dccb9efe), Firmware-Side-Load, Erweiterungen unverändert mp.4
Stufen und Freigabe durch: 2 und 3 (M111 P-1 des Tests), Tim ("Keep dev.2" bis zum nächsten Testbuild, dieser ersetzt es)
Lauf 1: PASS, 600 Abbrüche alle "done", kein Block. DCS verwarf 3 kreuzende Makro-Requests, 0 x "none was requested", 0 Neusendungen, 0 x "Macro file ... not found", 73 leere Out-of-order replies
Lauf 2: PASS, 2000 Abbrüche alle "done", kein Block. DCS verwarf 17 kreuzende Makro-Requests, 0 x "none was requested", 0 x Stack underrun, 2 Neusendungen von InvalidateChannel (06:25:12, 06:27:29), danach je ein Out-of-order reply "M98: Macro file ... not found", 249 leere Out-of-order replies
Rückbau geprüft: ja, nach beiden Läufen (kein Side-Load gemountet, /var/tmp/lab-tests weg, OTA-Timer aktiv, idle, Versionen wie vorher). RRF dev.3 bleibt auf dem Board
```

Bewertung: T2 (#134), T12 (#158, RRF #45) und die Neusendung von T11 (#161) sind auf der Hardware behoben, der Kanal blieb bei beiden Neusendungen frei. T10 (RRF #46) wirkt: Die Antwort "Macro file ... not found" landet nicht mehr beim nächsten Code. Offen bleibt der Fall, in dem die erste Kopie vor dem Warten auf das Makro kommt (`open-findings.md`, T10): dann erscheint die Fehlermeldung als Out-of-order reply in der Konsole. Die leeren Out-of-order replies sind Antworten, die RRF vor dem Lesen des Abbruchs geschrieben hat.


### 2026-11-10 mittags: Kanal-Epoche, erster Lauf auf der Hardware, bestanden mit offenen Prüfungen

```
Datum, Zeit (UTC): 2026-11-10, 11:43 Flash, 11:44:48 bis 11:57:56 Lauf 1, 12:02:49 bis 12:15:20 Lauf 2, 14:00 Flash erneut, 14:01:30 bis 14:06:16 Lauf 3, 14:07:06 bis 14:09:16 Lauf 4, 14:09 Rückbau
Anlass, PR, Commit: Kanal-Epoche, DSF #167 (81f31216) und RRF #47 (c1a9df6b6), Prüfungen EP-1, EP-8, EP-12 und EP-21 in Teilen
DSF: 3.7.0+mp.13 im Image; in den Läufen 2 bis 4 Side-Load von 81f31216
RRF: 3.7.0+1.mp.9-dev.4 (RRF #47 c1a9df6b6, sha256 c919109f6cefff12...), Firmware-Side-Load, Erweiterungen unverändert mp.4
Stufen und Freigabe durch: 2 und 3, Tim ("you can sideload dsf and rrf on the lab printer and test it there when the unit tests are all green"); M112 der Kategorie Approval für diesen Lauf freigegeben ("ja")
Link: erweiterter Link in allen Läufen aktiv ("Link capabilities All are active" nach jedem Verbindungsaufbau)
Lauf 1, Standard-Suite, neue RRF gegen das DSF des Images: 31 bestanden, 5 fehlgeschlagen, 2 übersprungen. Watchdog-Neustarts 5 x, wieder da nach 12,0 bis 12,4 s. Journal: 0 Lost connection, 0 Out-of-order reply, 0 in den Fehlerkategorien
Lauf 2, Standard-Suite, beide Seiten neu: 36 bestanden, 0 fehlgeschlagen, 2 übersprungen. Watchdog-Neustarts 5 x, 12,1 bis 12,4 s. Journal: 0 Lost connection, 1 x "Daemon: Out-of-order reply ''" 7 ms nach dem Start des Image-DCS beim Entfernen des Side-Loads
Lauf 3, AbortsCrossingMacroRequests mit 2000 Abbrüchen, beide Seiten neu: alle 2000 "done", kein Block, 0 Lost connection; im Journal kein Out-of-order reply und keine Neusendung von InvalidateChannel gefunden. Das Journal war in diesem Lauf gedrosselt (siehe unten), fehlende Zeilen sind also kein Beweis. Das v7-Fenster verwarf 0 Requests; der Test endet deshalb als "inconclusive", weil er nur dessen Zeilen zählt. Im Debug-Journal (journald unterdrückte 263908 Zeilen, die Zahlen sind Untergrenzen): DSF verwarf über das Tag 45 x Message, 15 x ExecuteMacro, 3 x MacroFileClosed auf Telnet ("Dropped ... of epoch N, current epoch is N")
Lauf 4, IntrusiveCodes.M112StaysHaltedUntilM999, beide Seiten neu: bestanden. M112 beantwortet, Status halted bis M999, kein "Stack underrun"
Unterbrechung: Ein erster Versuch von Lauf 3 um 12:16 endete mit "Network error: Connection timed out", weil der Drucker ausgeschaltet wurde (Tim). Nach dem Neustart des SBC lief wieder die Image-Firmware mp.8; die Test-RRF wurde um 14:00 erneut geflasht
Rückbau geprüft: ja, 14:11 (fw-sideload.sh remove, RRF 3.7.0+1.mp.8, kein Side-Load gemountet, /var/tmp/lab-tests weg, 0:/macros/lab-tests und sys/trigger30.g weg, OTA-Timer aktiv, idle, Heizungen aus, Erweiterungen laufen)
```

Bewertung:
- Die RRF aus #47 lief zum ersten Mal. Sie startet, führt `config.g` aus und hält den erweiterten Link, gegen das DSF des Images (das nie taggt) und gegen das DSF aus #167.
- Die fünf Fehlschläge von Lauf 1 kommen nicht von der neuen RRF:
  - `RefusedMacroPaths` (3 Tests) und `CodeBufferReserve`: die Fixes DSF #132 und #133 sind nicht im Tag `v3.7.0+mp.13`. Mit dem DSF aus dem Branch bestehen alle vier (Lauf 2).
  - `MessageBoxes.OneBoxAfterM122`: Kollision mit dem Selbsttest der Maschine, der um 11:49:55 auf HTTP startete und selbst zwei Boxen öffnet (`check-quick.g`, Zeile 23; `maintenance/self-test`, Zeile 100). Der Test zählt jede Box im Objektmodell. Einzeln wiederholt: 3 von 3 bestanden. Der Test sollte nur seine eigene Box zählen.
- EP-8: mit der Epoche auf beiden Seiten kein Out-of-order reply und keine Neusendung im (gedrosselten) Journal bei 2000 Abbrüchen, und kein blockierter Kanal. Vergleichswert vom 2026-11-09 auf dem erweiterten v7-Link: 249 leere Out-of-order replies und 2 Neusendungen. Die verworfenen Pakete im Debug-Journal zeigen, dass RRF taggt und DSF über das Tag filtert.
- Die Zeile "Daemon: Out-of-order reply ''" nach dem DCS-Start ist das bekannte Muster. Sie passt zu dem offenen Punkt, dass eine Antwort der alten Verbindung in der neuen ankommt (Anforderungen, Abschnitt 10.9; Entscheidung offen). Bewiesen ist die Ursache nicht.
- Nicht geprüft: die Zeile "Channel epochs" im `M122` der RRF (die Zähler sind nach dem Reset am Ende eines Laufs weg), EP-9, EP-18 bis EP-20 und EP-22 als eigene Prüfungen, alles mit Bewegung (EP-15 bis EP-17), kein Druck. `AbortsCrossingMacroRequests` sollte die über das Tag verworfenen Requests zählen, damit der Test mit der Epoche ein Ergebnis hat.

### 2026-11-10 nachmittags und abends: Kanal-Epoche, gesamter Stack und Befund D16, bestanden

```
Datum, Zeit (UTC): 2026-11-10, 15:48 Flash, 15:50 bis 20:09 Lauf A (11 Blöcke), 20:23 bis 22:16 Lauf B (5 Blöcke), 22:17 Rückbau
Anlass, PR, Commit: Kanal-Epoche vor dem Merge, DSF #167 und RRF #47 (78a997f74). Lauf A mit DSF e9bbff84, Lauf B mit DSF ae501ea1 (D16). Prüfungen EP-1, EP-8, EP-12, EP-21, EP-23 und der Regressionsblock
DSF: 3.7.0+mp.13 im Image; in jedem Block Side-Load des genannten Commits, mit Fehlerinjektion wo genannt
RRF: 3.7.0+1.mp.9-dev.5 (RRF #47 78a997f74, sha256 bdc659c73e17dd9d...), Firmware-Side-Load, Erweiterungen unverändert mp.4
Stufen und Freigabe durch: 2 und 3, Tim ("teste offline und auf dem labor drucker mit dem neusten stand von dsf und rrf den gesamten teststack inkl dem fuzz tests"); die Approval-Tests liefen in Lauf A mit
Lauf A, Block 1, Standard-Suite: 36 bestanden, 0 fehlgeschlagen, 2 übersprungen. Journal: 2 x "Daemon: Out-of-order reply ''"
Lauf A, Block 2, AbortsCrossingMacroRequests mit 2000 Abbrüchen: bestanden, alle 2000 "done", kein Block, 18 Makro-Requests über die Epoche verworfen; journald unterdrückte 263875 Zeilen
Lauf A, Block 3, IntrusiveCodes (Approval): 5 von 5
Lauf A, Block 4 und 5, SpiFaultLostConnection und SpiFaultRestarts: je 2 von 2
Lauf A, Block 6, Fehlerinjektion mit den 15 ESD-Fällen: 15 x Retry, 0 Abweichungen vom Census
Lauf A, Block 7, Fehlerinjektion auf dem erweiterten Link: 672 von 3234 Fällen, 672 x Retry, 0 Abweichungen, 38 min 54 s
Lauf A, Block 8, 100 DCS-Neustarts (EP-23): nicht bestanden. 204 DCS-Starts, 13 x "Daemon: Out-of-order reply ''", jede 0 bis 14 ms nach "Connected to controller over SPI", alle nach einem DCS-Neustart bei laufender RRF (13 von 102), keine nach M999 (0 von 102)
Lauf A, Block 9, LinkSoak: bestanden, 30 min 7 s im Leerlauf, 0 Lost connection, alle Link-Zähler 0
Lauf A, Block 10, Fehlerinjektion auf dem einfachen Link: 679 von 3234 Fällen, 659 x Retry, 20 x Reconnect, 0 Abweichungen, 1 h 15 min. Journal: 4 x Out-of-order reply
Lauf A, Block 11, TriggerMacros.StringLiteralInExpression: bestanden in 4 s
Lauf B, Block 1, 100 DCS-Neustarts mit D16 (EP-23): bestanden. 0 Out-of-order reply in den 200 DCS-Starts des Tests
Lauf B, Block 2, Standard-Suite: 36 bestanden, 0 fehlgeschlagen. Journal: 0 Out-of-order reply
Lauf B, Block 3 und 4, SpiFaultLostConnection und SpiFaultRestarts: je 2 von 2
Lauf B, Block 5, Fehlerinjektion auf dem einfachen Link, nur die Fälle, die laut Census die Verbindung brechen: 35 Fälle, 19 x Reconnect, 16 x Retry, 0 Abweichungen. Journal: 0 Out-of-order reply bei 46 DCS-Starts
Rückbau geprüft: ja, nach jedem Block (Side-Load weg, /var/tmp/lab-tests weg, OTA-Timer aktiv, Testmakros und sys/trigger30.g weg, idle, Erweiterungen laufen) und am Ende 22:17:53 (fw-sideload.sh remove, RRF 3.7.0+1.mp.8, DSF 3.7.0+mp.13, idle, kein Job, Heizungen aus)
```

Bewertung:
- Mit beiden Seiten aus den PRs besteht der gesamte Stack: Standard-Suite, 2000 Abbrüche, Approval-Tests, Fehlerinjektion auf beiden Links ohne Abweichung vom Census, 30 Minuten Leerlauf ohne Verbindungsverlust.
- Block 8 von Lauf A hat den offenen Punkt aus dem Lauf vom Mittag belegt: Kommt DCS nach 0,4 s wieder, hat RRF das Ende der Verbindung noch nicht bemerkt (`SbcConnectionTimeout` 4 s), und der Transfer des Verbindungsaufbaus bringt, was RRF für die alte Verbindung geschrieben hat. D15 (RRF schreibt keine Antwort eines Codes einer beendeten Verbindung) ändert daran nichts, weil die Antwort vor dem Ende geschrieben wurde. Tim hat D16 entschieden: DSF verwirft ein getaggtes Paket, das RRF vor dem Lesen des ersten Markers geschrieben hat, nie aber bei einer Neusendung oder einem wiederholten Transfer (Anforderungen, Abschnitt 10.9).
- Lauf B zeigt D16 auf der Hardware: 0 von 200 statt 13 von 102, und kein Out-of-order reply mehr in der Standard-Suite und bei den Verbindungsabbrüchen auf dem einfachen Link.
- In Lauf B steht je eine Zeile "Daemon: Out-of-order reply ''" am Ende von Block 1 und Block 3, beim ersten DCS-Start nach dem Entfernen des Side-Loads. Das passt zum DCS des Images, das D16 nicht hat. Aus dem Journal ist das nicht zu belegen, weil Image und Side-Load dieselbe Version melden. Der Runner sollte das Entfernen im Journal markieren.
- Ungetaggte Pakete der alten Verbindung kommen weiter an: "Failed to find query for object model response" 8 x in Block 1 von Lauf B. DSF wendet sie nicht an, weil keine Abfrage offen ist.
- `StringLiteralInExpression` besteht (RRF 59a04159b) und ist seit #175 ein regulärer Test.
- Der Debug-Text "Dropped ... of an older connection" wurde nicht gesehen, weil das Journal auf info steht. Der Beleg für D16 ist die Zählung.
- Nicht geprüft: die Zeile "Channel epochs" im `M122` der RRF, EP-9, EP-18 bis EP-20 und EP-22 als eigene Prüfungen, alles mit Bewegung (EP-15 bis EP-17), kein Druck, das neue DSF gegen die RRF des Images (mp.8) allein.

### 2026-11-21: RG-18, Kalibrierung mit 3 min, bestanden

```
Datum, Zeit (UTC): 2026-11-21, 10:09:29 bis 10:12:51
Anlass, PR, Commit: neuer Test MacroCloseStress (RG-18) für RRF #50, Branch feature/lab-test-macro-close-stress, vor dem Commit
DSF: 3.7.0+mp.14 aus dem Image, kein Side-Load
RRF: 3.7.0+1.mp.10-dev.1 (RRF #50, Firmware-Side-Load seit 09:47), Erweiterungen unverändert mp.4
Stufen und Freigabe durch: 2 (Testtrigger 30). Auftrag von Tim, den Test zu schreiben; kein eigenes Go für diesen Lauf
IDs: RG-18 mit LAB_MACRO_CLOSE_MINUTES=3 bestanden (3 min 6 s)
Rate: 901 Trigger-Läufe, 4505 Schließvorgänge in 3,0 min, 25,0 je Sekunde, also 5 Läufe je Sekunde wie vom Ausdruck begrenzt
M122 DSF und RRF vorher und nachher: alle Link-Zähler 0. Last reset 00:25:33 vor dem Ende, das ist der Side-Load um 09:47
Self-Test im Laufzeitraum: kein Lauf
Journalbefunde: kein Fehlerbefund aus Abschnitt 9, keine Neustartzeile. 9150 Zeilen in 3,4 min (etwa 45 je Sekunde), journald unterdrückte nichts
Rückbau geprüft: ja (kein Side-Load, /var/tmp/lab-tests weg, OTA-Timer aktiv, sys/trigger30.g und Testmakros weg, idle, Erweiterungen laufen, Versionen wie vorher)
```

Bewertung:
- Die Rate reicht: 60 min sind etwa 90.000 Schließvorgänge. Bei der Feldrate von etwa einem Reset je 40.000 wären auf mp.9 rund zwei Resets zu erwarten, die Wahrscheinlichkeit für keinen läge bei etwa 10 %. Das ist eine Hochrechnung, kein Messwert.
- Ob der Test den Fehler auf mp.9 findet, zeigt der nächste Lauf.

### 2026-11-21: RG-18 auf RRF mp.9, Fehler gefunden (Gegenprobe)

```
Datum, Zeit (UTC): 2026-11-21, 10:21:44 bis 10:54:37
Anlass, PR, Commit: Gegenprobe zu RG-18, ob der Test den Fehler ohne RRF #50 findet. PR #199 (3aec3fcc)
DSF: 3.7.0+mp.14 aus dem Image, kein Side-Load
RRF: 3.7.0+1.mp.9 aus dem Image (fw-sideload.sh remove direkt vor dem Lauf), Erweiterungen unverändert mp.4
Stufen und Freigabe durch: 2, Tim ("flash the old mp.9 back and validated the testcase")
IDs: RG-18 mit 60 min, nicht bestanden wie erwartet: RRF-Reset nach 31,9 min
Rate: 9299 Trigger-Läufe, 46.496 Schließvorgänge bis zum Reset, 25,0 je Sekunde
M122 RRF nach dem Reset: Last software reset at 2026-11-21 10:54, reason: MemoryProtectionFault mmarValid daccViol, Gcodes spinning, slot 1
  Software reset code 0x0163 HFSR 0x00000000 CFSR 0x00000082 ICSR 0x00400804 BFAR 0x00000000 SP 0x2041b550 Task MAIN Freestk 2206 ok
  Stack: 00000000 20418fe8 00000000 20418fdc 00000324 00481f69 00481fb4 61070000 204371c8 0048af45 a5a5a5a5 2042a634 204371b8 00000000 204255a8 00420cd9 00000000 00000001 20424ba8 2042a8e8 00000000 0041c613 00000000 2042a8ec 2042a8e8 004283d3 00000001
M122 DSF vorher und nachher: Full transfer restarts 0 -> 1, extended left 0 -> 1, connection resets 0 -> 1, lost connections 0 -> 0, alle übrigen 0
Self-Test im Laufzeitraum: 1 Lauf, passed
Journalbefunde: kein Fehlerbefund aus Abschnitt 9. Je einmal Reset, "left the extended link" und Neustart der Übertragung (der eine Reset). 1 x "Aborting orphaned macro file daemon.g", 3 x "predicted maximum temperature" (außerhalb des Plans)
Rückbau geprüft: ja (Testmakros und sys/trigger30.g weg, idle, Erweiterungen laufen). Danach um 10:56 wieder 3.7.0+1.mp.10-dev.1 per fw-sideload.sh install (sha256 5c95be5a1590dcfe), Stand wie vor der Gegenprobe
```

Bewertung:
- Der Test findet den Fehler. Die Signatur gleicht dem Feld-Reset von 05:28 (`M122` vor der Gegenprobe): gleicher Grund, Code, CFSR, ICSR, SP und Task, PC `00481fb4` (`Heap::DeleteSlot`+0x54) mit LR `00481f69`, und dieselben Rücksprungadressen `0048af45`, `00420cd9`, `0041c613`, `004283d3`. Es ist derselbe Fehler, nicht ein anderer Reset.
- 46.496 Schließvorgänge bis zum Reset passen zur Feldrate von etwa einem Reset je 40.000. Ein einzelner Treffer ist keine Rate.
- Mit RRF #50 lief derselbe Test vorher 3 min ohne Befund (Eintrag oben). Ein bestandener 60-min-Lauf auf mp.10-dev.1 steht noch aus.

### 2026-11-21: Release-Prüfung RRF 3.7.0+1.mp.10, bestanden

```
Datum, Zeit (UTC): 2026-11-21, 13:23 Firmware-Side-Load, 13:25 bis 13:28 Block 0, 13:28:56 bis 13:39:33 Block 1, 13:40:01 bis 13:42:53 RG-17 (abgebrochen), 13:44:24 bis 13:47:36 F-1, 13:47:36 bis 14:21:28 F-2
Anlass, PR, Commit: Release RRF 3.7.0+1.mp.10 von 3.7-dev e5b7878a1 (RRF #50, #51, #52, ohne den Upstream-Merge #49), Umfang "Neues RRF-Release"
DSF: 3.7.0+mp.14 im Image. Block 0 auf 0436289c, F-1 und F-2 mit Side-Load von 9ed2e721 mit Fehlerinjektion (gleicher Code, dazwischen änderte sich nur dieser Plan)
RRF: 3.7.0+1.mp.10-dev.2 (Baum von e5b7878a1, sha256 8ae386d9468029ec...), Firmware-Side-Load, Erweiterungen unverändert mp.4
Stufen und Freigabe durch: 2, Tim ("1 und 2 go, release vom heutigen 3.7-dev", Side-Load von dev.2 freigegeben)
Block 0: 2506 bestanden, 0 fehlgeschlagen, 6 übersprungen. Die Liste enthält 51 Lab-Testfälle
Block 1 (E, RG-1 bis RG-16): 37 bestanden, 0 fehlgeschlagen, 2 übersprungen, 12 explizit nicht gelaufen. Watchdog-Neustarts 5 x, wieder da nach 12,0 bis 12,5 s (Median 12,1 s)
RG-17: nach 2,9 min abgebrochen, weil Tim RG-17 aus dem Release-Umfang genommen hat (9ed2e721). Rückbau von Hand geprüft (idle, Heizungen aus, Erweiterungen laufen, kein neuer Reset)
F-1: 15 von 15 ESD-Fällen, 15 x Retry, 0 nicht injiziert, 0 Abweichungen vom Census
F-2: 672 von 3234 Fällen auf dem erweiterten Link, 672 x Retry, 0 nicht injiziert, 0 Abweichungen, 32 min 29 s
PortMatchesRealFirmware: lokal wie in rrf-harness.yml, Harness aus RRF 8004517c1 (e5b7878a1 und der Versions-Commit) mit g++ 13, Unit-Tests von DSF 5d49b5a5. usb1 (RRF_HOST_POLL=1, RRF_HOST_RULE=1) 6 von 6, usb0 (RRF_HOST_RULE=1) 6 von 6, je 0 Fälle mit Abweichung
M122: RRF-Link-Zähler in allen Blöcken 0. DSF in Block 1: Full transfer restarts, extended left und connection resets je 0 -> 1, lost connections 0. Nach F-1 und F-2 Last reset 18 s vor dem Ende, cause software: das M999 nach dem DCS-Neustart beim Entfernen des Side-Loads (Abschnitt 9)
Self-Test im Laufzeitraum: Block 1 2 Läufe, F-1 1 Lauf, F-2 1 Lauf, alle passed
Journalbefunde: in keinem Block ein Fehlerbefund aus Abschnitt 9. Block 1: 1 x "Failed to find query for object model response", 7 x "Aborting orphaned macro file daemon.g" (bekannt). F-1: 15 x injiziert, 5 x Bad header CRC, 10 x Bad data CRC, 4 x Machine is halted. F-2: 672 x injiziert, 118 x Restarting full transfer, 22 x Bad header CRC, 8 x Bad data CRC, 26 x Sending transfer # again, 2 x Machine is halted
Rückbau geprüft: ja, nach jedem Block (kein DSF-Side-Load, /var/tmp/lab-tests weg, OTA-Timer aktiv, sys/trigger30.g und 0:/macros/lab-tests weg, idle, kein Job, Erweiterungen laufen, Versionen wie vorher). Der Firmware-Side-Load von dev.2 bleibt nach dem Lauf drauf
```

Bewertung:
- Der Release-Umfang für RRF ist ohne Befund durchgelaufen: Block 0, E, RG ohne RG-17 und RG-18, F-1, F-2 und `PortMatchesRealFirmware`.
- Der Link verhält sich wie mit mp.9: F-2 gleicht dem Lauf vom 2026-11-10 (672 x Retry, 0 Abweichungen), F-1 ebenso.
- #50 und #51 prüft dieser Lauf nur indirekt: Jeder Boot und jedes `M999` führt `config.g` und damit `M669 K5` aus, also `SetKinematics` (RRF #51). Geprüft sind beide Fixes deterministisch durch `Tools/heap-test` und `Tools/kinematics-test` in RepRapFirmware. Ein bestandener 60-min-Lauf von RG-18 auf mp.10 steht weiter aus und gehört nicht zum Release-Umfang.
- Nicht geprüft: Beide Fixes schützen nur Leser mit höherer Priorität als der Main-Task, also den SBC-Task. Ohne SBC liest der Netzwerk-Task das Objektmodell mit derselben Priorität wie der Main-Task (`TaskPriorities.h:15`, `Tasks.cpp:283`, `Network.cpp:519`), dort bleibt das Fenster offen. Die CHX läuft mit SBC und aktiviert das Netzwerk der RRF nicht (`RepRap.cpp:637-645`).

### 2026-11-23: Fixes von RRF #55 bis #88, D3E #18 bis #26 und FreeRTOS #1 als Side-Load, bestanden mit neuem DSF-Befund

```
Datum, Zeit (UTC): 2026-11-23, 10:55 Side-Load D3E, 11:00 Side-Load RRF, 11:13:47 bis 11:17:23 Block 0, 11:17:57 bis 11:28:18 Block 1, 11:29:11 bis 11:31:53 F-1, 11:31:55 bis 12:04:50 F-2, 12:05 A-14 bis A-16 (erster Lauf), 12:10:33 A-14 bis A-16 (zweiter Lauf)
Anlass, PR, Commit: gezielte Prüfung der gemergten Fork-Fixes vor dem Pin (DSF #198): RRF 3.7-dev 2f2a8af5b, D3E 169c8280, FreeRTOS d3ce7a3, RRFLibraries 888b479. Neue Tests A-14 bis A-16 für RRF #57 auf feature/lab-tests-rrf-fixes
DSF: 3.7.0+mp.14 im Image. Block 0 und Block 1 auf 22a94eb6 (v3.7-dev), F-1 und F-2 mit Side-Load von 22a94eb6 mit Fehlerinjektion, A-14 bis A-16 ohne Side-Load
RRF: 3.7.0+1.mp.11-dev.1 (sha256 8cdcfa0547331a14...), Firmware-Side-Load. Erweiterungen 3.7.0+1.mp.5-dev.1 (CAN 50 bis 53 EXP1HCL, 60 SZP), Firmware-Side-Load über den Bootloader
Stufen und Freigabe durch: 2, Tim (Side-Load "starte mit D3E ... erst wenn D3E läuft, laden wir RRF nach", Lauf "1. und 2. umsetzten RG-18 nicht umsetzten ... RG-18 für den Release-Lauf")
Block 0: 2506 bestanden, 0 fehlgeschlagen, 6 übersprungen. Die Liste enthält 51 Lab-Testfälle
Block 1 (E, RG-1 bis RG-16): 37 bestanden, 0 fehlgeschlagen, 2 übersprungen, 12 explizit nicht gelaufen. Watchdog-Neustarts 5 x, wieder da nach 12,0 bis 12,4 s (Median 12,1 s). RG-17 und RG-18 nicht gelaufen (Tim: RG-18 im Release-Lauf)
F-1: 15 von 15 ESD-Fällen, 15 x Retry, 0 nicht injiziert, 0 Abweichungen vom Census
F-2: 672 von 3234 Fällen auf dem erweiterten Link, 672 x Retry, 0 nicht injiziert, 0 Abweichungen
A-14 bis A-16: erster Lauf A-14 fehlgeschlagen, A-15 und A-16 bestanden. RRF lehnte die Zuweisung ab, aber die Zeile danach lief trotzdem (Befund L7 in design/open-findings.md). Test umgebaut, zweiter Lauf 3 von 3 bestanden
PortMatchesRealFirmware: CI von DSF #198 (Pins wie oben), usb1 und usb0 je 6 von 6, 0 Fälle mit Abweichung. Host-Tests von RRF 2f2a8af5b lokal 22 von 22 bestanden
M122: RRF-Link-Zähler in allen Blöcken 0. DSF in Block 1: Full transfer restarts, extended left und connection resets je 0 -> 1, lost connections 0. Nach F-1 und F-2 Last reset 18 s vor dem Ende, cause software: das M999 nach dem DCS-Neustart beim Entfernen des Side-Loads (Abschnitt 9)
Self-Test im Laufzeitraum: Block 1 2 Läufe, F-1 1 Lauf, F-2 1 Lauf, alle passed
Journalbefunde: in keinem Block ein Fehlerbefund aus Abschnitt 9. Block 1: 1 x "Failed to find query for object model response", 7 x "Aborting orphaned macro file daemon.g" (bekannt). F-1: 15 x injiziert, 5 x Bad header CRC, 10 x Bad data CRC, 6 x Machine is halted. F-2: 672 x injiziert, 118 x Restarting full transfer, 22 x Bad header CRC, 8 x Bad data CRC, 26 x Sending transfer # again, 2 x Machine is halted. A-14 bis A-16: keiner
Rückbau geprüft: ja, nach jedem Block (kein DSF-Side-Load, /var/tmp/lab-tests weg, OTA-Timer aktiv, sys/trigger30.g und 0:/macros/lab-tests weg, idle, kein Job, Erweiterungen laufen, Versionen wie vorher). Der Firmware-Side-Load von RRF mp.11-dev.1 und D3E mp.5-dev.1 bleibt bis zum nächsten Neustart des SBC drauf
```

Bewertung:
- Mit den gemergten Fixes ist Block 0, E, RG-1 bis RG-16, F-1 und F-2 ohne Befund durchgelaufen. F-2 gleicht den Läufen vom 2026-11-10 und 2026-11-21 (672 x Retry, 0 Abweichungen).
- FreeRTOS #1 lässt eine Task, die mit einer eigenen Interrupt-Maske blockiert, in `vAssertCalled` laufen, also in einen Software-Reset. In Block 1 gehen alle RRF-Neustarts auf Testaktionen zurück (5 x RG-6, 1 x Self-Test-Autostart). In F-1 und F-2 zeigt `M122` am Ende das `M999` des Rückbaus als letzten Reset, die Gründe der Resets nach den DCS-Neustarts davor (`Lab.cs:280-290`) sind nicht einzeln geprüft. Das zeigt nur, dass die Pfade dieser Blöcke keine Maske über einen blockierenden Aufruf halten, nicht, dass es keinen solchen Pfad gibt.
- RRF #57 ist end to end geprüft (A-14 bis A-16). Die übrigen Fixes sind am CHX nicht erreichbar oder brauchen Bewegung (Abschnitt 12). Sie sind offline durch die Host-Tests geprüft.
- Neuer DSF-Befund L7: Eine Zeile nach einem fehlgeschlagenen Code eines Makros läuft noch, obwohl DSF das Makro abbricht. Der Code ist upstream, siehe design/open-findings.md.
- Offen: Stufe 4 vor Ort, RG-18 im Release-Lauf.

### 2026-11-23 mittags: Stufe 4 mit Tim vor Ort, Referenzfahrt und Feed Hold, bestanden mit Befund (nicht weiter verfolgt)

```
Datum, Zeit (UTC): 2026-11-23, 12:22:42 Ausgangszustand, 12:23:32 bis 12:26:06 G28, 12:28:36 FH-1, 12:29:18 FH-2, 12:30:10 G28 Y, 12:30:29 FH-3, 12:30:52 FH-1 wiederholt, 12:31:28 Endzustand
Anlass, PR, Commit: Stufe-4-Teil der gezielten Prüfung der Fork-Fixes (RRF #66, #83, FreeRTOS #1, D3E #24), Firmware wie im Lauf davor
DSF: 3.7.0+mp.14 im Image, kein Side-Load. Codes einzeln über HTTP durch den Tunnel, ohne Lab-Runner
RRF: 3.7.0+1.mp.11-dev.1, Erweiterungen 3.7.0+1.mp.5-dev.1 (Firmware-Side-Load vom Vormittag)
Stufen und Freigabe durch: 4, Tim vor Ort am Not-Aus, Türen zu und geprüft. Freigabe "1. G28 komplett, 2. Feed Hold bei Referenzfahrt, 3. Feed Hold bei G1-Fahrt", M-1 bis M-4 nicht
Ausgangszustand: idle, keine Achse referenziert, Heizungen aus, B50 bis B53 0 Step-Fehler, Link 0 Fehler
G28: homeall.g in 2:34 min, alle Achsen referenziert (X 440, Y 211, Z 200, U 422), global.result 0. X, Y und U referenzieren über Treiber auf den EXP1HCL mit Encoder-Endstops (config.g:108-111 auf dem Drucker), Z über die SZP
FH-1: G28 Y von Y 211, nach 1,3 s M25.1. Status held, Y steht bei 102,188. M24 nach 5,8 s: G28 Y endet, Y 0 referenziert, result 0. Wiederholung 12:30:52: Y steht bei 94,903, sonst gleich
FH-2: wie FH-1, aber M25.1 S1 und kein M24. G28 Y endet sofort, Y steht bei 100,434 und ist nicht referenziert, X, Z und U bleiben referenziert, Status idle. Journal "Aborted macro file homey.g", "Failed to home axes". Danach G28 Y ohne Hold in 4 s, ohne Fehler
FH-3: G1 Y20 F3000 von Y 211, nach 1,3 s M25.1. Status held bei Y 140,631. M24: die Fahrt endet auf Y 20
Y blieb unter 211, weil U auf 422 steht und Y und U in derselben Richtung fahren. homez.g:39 auf dem Drucker stellt Y auf max/2, während U auf max steht
M122: kein Reset auf dem Mainboard und den Erweiterungen während des Termins (Mainboard Last reset 12:04, das M999 beim Rückbau von F-2), Heap OK, B20 und B50 bis B53 0 Step-Fehler, lost connections und connection resets 0
Journalbefunde: in beiden FH-1-Läufen "Error: Y endstop did not trigger" und "Warning: Y homing - retry" von homey.g (12:28:38, 12:30:54), sonst keine Fehler
Endzustand: idle, alle Achsen referenziert (X 440, Y 0, Z 200, U 422), Motoren bestromt, Heizungen aus
```

Bewertung:
- RRF #66: Die Referenzfahrten von X, Y und U laufen nur über Treiber auf den Erweiterungen und hielten an ihren Endstops wie vorher. Das ist die Regressionsprüfung für "Moves executed only by remote drivers are still stopped". Den veralteten Eintrag in `activeEndstops`, den #66 behebt, hat der Termin nicht gezielt erzeugt. Das prüft `Tools/endstop-check-test` in RepRapFirmware.
- RRF #83 und FreeRTOS #1: Hold und Stopp mit einer Referenzfahrt in Bewegung (FH-1, FH-2) und ohne (FH-3) liefen ohne Reset. Ob der Bereich vorher mit freigegebenem Schrittinterrupt lief, zeigt der Termin nicht, das zeigt `make scan-unmask`.
- D3E #24 nur indirekt: 0 Step-Fehler auf den Erweiterungen.
- Befund FH-1: Bei `M25.1` während der Referenzfahrt von `homey.g` kam das `M400` nach der unterbrochenen `G1 H1`-Fahrt schon im Hold zurück (Fehlermeldung 0,5 s nach `M25.1`, `M24` erst 5 s später). `homey.g` prüfte die Position, meldete den Fehler und verbrauchte einen seiner drei Versuche. Nach `M24` war Y korrekt referenziert. Nach dem Code soll GCodes auf den Rest der Fahrt warten (RRF `DDARing.cpp:919`, `:922`, `CutHomingMove`). Tim (2026-11-23): Ein Feed Hold muss während einer Referenzfahrt nicht weiterlaufen. Deshalb nicht weiter untersucht und nicht in `design/open-findings.md`.
- Nicht geprüft: RRF #84, #56, #63 (Abschnitt 12) und M-1 bis M-4.

### 2026-11-23 nachmittags: M-1 Testdruck mit Tim vor Ort, mit Feed Hold im Druck, bestanden mit Befund (Y-Treiberfehler beim Start)

```
Datum, Zeit (UTC): 2026-11-23, 12:38:17 Start, 12:45:44 Treiberfehler Y, 12:50:44 pausiert, 12:51:48 G28 Y, 12:52:01 M24, 13:48:34 FH-4, 13:51:19 Ende
Anlass, PR, Commit: M-1 nach dem Stufe-4-Termin vom Mittag, vor dem Release von RRF 3.7.0+1.mp.11 und D3E 3.7.0+1.mp.5
DSF: 3.7.0+mp.14 im Image, kein Side-Load. Codes einzeln über HTTP durch den Tunnel, ohne Lab-Runner
RRF: 3.7.0+1.mp.11-dev.1, Erweiterungen 3.7.0+1.mp.5-dev.1 (Side-Load vom Vormittag, Quellen bis auf die Versionszeile gleich den Releases)
Stufen und Freigabe durch: 4, Tim vor Ort. Freigabe "lass uns den druck ... als testdruck starten", nach dem Treiberfehler "Weiterlaufen lassen", nach der Pause "home die y achse mit G28 Y und reusme dann den druck", danach "wir können während des drucks einmal feedhold testen"
Job: 0:/gcodes/M277_CHC_Linear_Rail_Adapter_L0.18mm_N0.4_PLA_Meltingplot CHX 350_50m44s.gcode, PLA, Bett 60 °C, Düse 220 °C, Druckzeit laut DSF 1h 11m
Takt: Objektmodell über HTTP alle 30 s, M122 alle 5 min, M122 vorher und nachher
Treiberfehler: 12:45:43 "Driver 52.0 warning: position tolerance exceeded", 12:45:44 "Driver 52.0 error: failed to maintain position" (Y), in der Startsequenz bei bed.g:34 auf dem Drucker (G1 X{max/2} Y{max/2} U{max} F60000). driver-error.g:20 auf dem Drucker schickt M25, die Pause griff erst um 12:50:32 nach dem Ende der Startmakros (Messung 840 Punkte bis 12:50:02), pause.g bis 12:50:44, pausiert bei Byte 19498
Fortsetzen: G28 Y (homey.g 12:51:48 bis 12:51:50, ohne Fehler), M24 12:52:01, resume.g bis 12:52:19. Danach kein weiterer Treiberfehler
FH-4: in Lage 206 bei 99,2 % M25.1 (ohne S) um 13:48:34.350. Erste Abfrage 13:48:35.556 held bei X 454,288 Y 194,959 Z 37,300, dann 5 Abfragen held mit gleicher Position und Dateiposition. M24 um 13:48:39.116, 13:48:40.009 wieder processing, Lage 207 um 13:48:41. Kein resume.g, keine Meldung. Die Trigger-Makros (trigger8.g) liefen im Hold weiter
Tim hat im Druck zusätzlich selbst M25.1 und M24 gesendet, Halt und Weiterlaufen gesehen. Zeitpunkt und Eingang sind nicht erfasst, der 30-s-Takt hat den Hold nicht getroffen
Ende: "Finished printing file", stop.g 13:51:19 bis 13:51:20, um 13:51:40 idle
M122 Mainboard: kein Reset während des Drucks (Last reset 12:04, das M999 beim Rückbau von F-2), Heap OK, Scheduled moves 229699, completed 229699, lost connections 0, connection resets 0, failed transfers 0, checksum errors 0 in allen 17 Abfragen
Full transfers per second: 1780,96 im Leerlauf vorher, 1478,69 bis 1572,71 im Druck (14 Abfragen), 1515,25 und 1564,32 im Leerlauf nachher
M122 B50 bis B53 nach dem Druck: Last reset 12:04 (power up, wie nach jedem Reset des Mainboards), step errors 0, hiccups 0. B52 Driver 0 closed loop, "no error"
Journalbefunde: außer den Meldungen von driver-warning.g und driver-error.g keine Fehler und Warnungen, kein "Out-of-order", 0 Treffer für "unexpected" und "invalid"
Endzustand: idle, Heizungen aus durch stop.g
```

Bewertung:
- M-1 bestanden: Der Job lief bis zum Ende, `lost connections` und `connection resets` blieben 0. Ausgangswert für R4 (`design/sbc-link-requirements.md:117`): 1480 bis 1570 Full Transfers/s im Druck, 1515 bis 1780 im Leerlauf.
- Die Pause griff knapp fünf Minuten nach dem `M25` von `driver-error.g`, weil der Job gerade Makros ausführte. Das ist dokumentiert: "If a pause is commanded while a macro is being executed, the pause will be deferred until the macro has completed" (wiki Gcodes.md:1876).
- Befund Y-Treiber 52.0: Tim vermutet, dass die Feed-Hold-Tests vom Mittag (FH-1 bis FH-3) Y in einem falschen Zustand hinterlassen hatten. Nach G28 Y lief der Druck ohne weiteren Treiberfehler. Die Ursache ist nicht untersucht.
- Feed Hold im Druck (FH-4): Hold spätestens 1,2 s nach `M25.1`, Position stabil, Freigabe mit `M24` ohne `resume.g`, wie wiki Gcodes.md:1898 und :1850 und RRF `GCodes2.cpp:1207-1216` es beschreiben.
- M-2 nur teilweise: `stop.g` lief und kein "Out-of-order". Der zweite Job mit `M32` wurde nicht gestartet. M-3 und M-4 nicht geprüft.

### 2026-11-26: Release-Prüfung RRF 3.7.0+1.mp.12, D3E 3.7.0+1.mp.6 und DSF 3.7.0+mp.16, automatischer Teil bestanden, Stufe 4 offen

```
Datum, Zeit (UTC): 2026-11-26, 06:35:25 bis 06:36:45 Firmware-Side-Load D3E, 06:38:23 bis 06:39:06 Firmware-Side-Load RRF, 06:39:27 bis 07:08:14 Standardlauf, 07:11:58 bis 07:14:50 F-1, 07:14:50 bis 09:02:18 F-2 (erster Lauf, abgebrochen), 09:02 bis 09:04 Rückbau von Hand, 09:05:38 bis 09:07:45 Wiederholung DateBeyondYear9999IsAnError, 09:07:45 bis 09:41:24 F-2 (zweiter Lauf)
Anlass, PR, Commit: Release RRF 3.7.0+1.mp.12 (Meltingplot/RepRapFirmware#118 auf 3.7-dev 323b5b185), D3E 3.7.0+1.mp.6 (Meltingplot/Duet3Expansion#35 auf 3.7-dev 7dc11034b) und DSF 3.7.0+mp.16 (#255, e28eb04ce), Umfang "Neues RRF-Release". RRF #112 braucht DSF #237, deshalb mit dem DSF des Releases als Side-Load
DSF: 3.7.0+mp.15 im Image. Standardlauf mit Side-Load von DCS und DWS aus e28eb04ce, F-1 und F-2 mit Side-Load von e28eb04ce mit Fehlerinjektion. Wiederholung mit Side-Load von 2aea949e5 (fix/lab-date-beyond-9999-operand-order; Produktcode gleich e28eb04ce ohne die Versionszeile)
RRF: 3.7.0+1.mp.12-dev.3 (Baum von 323b5b185 plus Versionszeile, sha256 314f5de87c927ba7...), Firmware-Side-Load. Erweiterungen 3.7.0+1.mp.6-dev.1 (Baum von 7dc11034b plus Versionszeile; TOOL1LC sha256 94653977dfda77b7..., EXP1HCL 1172327ca997187b..., SZP 8878e1fe89bce0d4...), Firmware-Side-Load, zuerst D3E, dann RRF, beide mit firmware/sideload/fw-sideload.sh
Stufen und Freigabe durch: 2, Tim ("Yes run the lab test first"). F-2 lief ohne Aufsicht
Block 0: Unit-Tests auf a7211cf33 (e28eb04ce ohne die Versionszeile) 2760 bestanden, 0 fehlgeschlagen, 6 übersprungen. Die Liste von e28eb04ce enthält 149 Lab-Testfälle
Standardlauf (E, RG-1 bis RG-16, A-14 bis A-16, Tests der Fork-Fixes): 100 bestanden, 1 fehlgeschlagen (DateBeyondYear9999IsAnError, Testfehler, siehe Bewertung), 10 übersprungen, 38 explizit nicht gelaufen. LAB_ASSUME_FIXES="RRF#102 RRF#104 RRF#105 RRF#106 RRF#108 RRF#109 RRF#110 RRF#112 RRF#113 RRF#114 RRF#115 D3E#30 D3E#32". Watchdog-Neustarts 5 x, wieder da nach 12,0 bis 12,1 s (Median 12,1 s)
Übersprungen im Standardlauf: 4 x, weil die Pakete des Images vor #225, #232 und #234 liegen (WebServerUnitRestartsIt, ControlServerRunsWithoutEffectiveCapabilities, FileReadsHaveTheRightsOfTheDsfUser, RootPluginServiceSettingsBelongToRoot); 3 x Freigabe je Lauf (SzpCoilKeepsUpdatingDuringAccelerometerRuns, RestartDuringAccelerometerRunKeepsLinkAndResetData, WebServerComesBackAfterAKill); 2 x viele Neustarts oder Abbrüche (NoReplyOfTheOldConnectionAfterRestart, ChannelWorksAfterAbortsCrossingMacroRequests); 1 x DeletingCommandsDoNotStallAssignments, weil gerade eine Box der Maschine offen war. Diese zehn sind nicht geprüft
F-1: 15 von 15 ESD-Fällen, 15 x Retry, 0 nicht injiziert, 0 Abweichungen vom Census
F-2, erster Lauf: nach 364 von 672 Injektionen (letzte um 07:37:05) abgebrochen, weil der SSH-Zugang wegfiel ("No supported authentication methods available", Token). Bis dahin im Journal kein "Lost connection", kein TfrRdy-Timeout, kein "Out-of-order". Der Runner konnte nicht zurückbauen, der Side-Load mit Fehlerinjektion blieb eingehängt und mp-ota-gate.timer gestoppt. Rückbau von Hand mit den Schritten des Runners: je DLL eine Bind-Mount-Lage gelöst, /var/tmp/lab-tests entfernt (spi-fault war leer), DCS und DWS neu gestartet, M999, idle, mp-ota-gate.timer gestartet
F-2, zweiter Lauf: 672 von 3234 Fällen auf dem erweiterten Link, 672 x Retry, 0 nicht injiziert, 0 Abweichungen, 33 min 15 s
Wiederholung DateBeyondYear9999IsAnError mit dem korrigierten Test: bestanden
PortMatchesRealFirmware: lokal, Harness aus RRF 323b5b185 von Grund auf neu gebaut (firmware/build.sh harness), Unit-Tests von a7211cf33. usb1 (RRF_HOST_POLL=1, RRF_HOST_RULE=1) 6 von 6, usb0 (RRF_HOST_RULE=1) 6 von 6
M122: RRF-Link-Zähler in allen Blöcken 0, DSF-Zähler in allen Blöcken 0 -> 0. Nach jedem Block Last reset 18 bis 20 s vor dem Ende, cause software: das M999 nach dem DCS-Neustart beim Entfernen des Side-Loads (Abschnitt 9)
Self-Test im Laufzeitraum: Standardlauf 6 Läufe, F-1 1 Lauf, F-2 (zweiter Lauf) 1 Lauf, alle passed
Journalbefunde: Standardlauf 1 x "Failed to process code in stage Executed" (OperationCanceledException in Code.UpdateNextFilePositionAsync über CodeFile.LockAsync) um 06:39:51 in der Image-DCS 3.7.0+mp.15 vor dem Side-Load, beim Abbruch des Self-Test-Autostarts nach dem Neustart durch den RRF-Flash, gleich nach "Failed to find suitable stack level for flush request" (H2). In den Instanzen von e28eb04ce lief derselbe Ablauf 5 x ohne den Fehler. Sonst im Standardlauf: 444 x "Resending packet #" (421 im Zeitraum von OutputBufferPressure 06:54:24 bis 06:55:35, 18 in RepeatedM122, 5 einzeln), 21 x "Cannot store excessively long object model response", 7 x "Aborting orphaned macro file daemon.g" (bekannt). F-1: 15 x injiziert, 5 x Bad header CRC, 10 x Bad data CRC, 4 x Machine is halted. F-2 (zweiter Lauf): 672 x injiziert, 118 x Restarting full transfer, 22 x Bad header CRC, 8 x Bad data CRC, 26 x Sending transfer # again, 2 x Machine is halted. Wiederholung: 1 x "Failed to find query for object model response" (bekannt)
Rückbau geprüft: ja, nach jedem Block, nach dem ersten F-2-Lauf von Hand (siehe oben). machine_mode danach "default" statt vorher "automatic", weil das M999 nach dem Side-Load config.g ausführt. Der Firmware-Side-Load von RRF mp.12-dev.3 und D3E mp.6-dev.1 bleibt bis zum nächsten Neustart des SBC drauf
```

Bewertung:
- Der automatische Teil des Release-Umfangs ist gegen den Kandidaten ohne Befund durchgelaufen: Block 0, E, RG ohne RG-17 und RG-18, A-14 bis A-16, F-1, F-2 und `PortMatchesRealFirmware`. Dazu liefen die Lab-Tests der Fork-Fixes mit der Firmware und dem DSF des Releases, darunter #247 (`PluginServicesStartWithControlServer`), `TwoToThe31IsAFloat` und die beiden Tests der tiefen Verschachtelung, die am 2026-11-25 noch nicht auf der Hardware gelaufen waren. F-1 und F-2 gleichen den Läufen vom 2026-11-10, 2026-11-21 und 2026-11-23.
- `DateBeyondYear9999IsAnError` schlug im Standardlauf wegen eines Testfehlers fehl: DSF wertete den Ausdruck selbst aus, weil `state.upTime` hinter dem `+ 1` stand, und antwortete mit dem Text von `DateTime.AddSeconds` (Befund LT4 in `design/open-findings.md`). Mit `state.upTime` vor dem `+ 1` bestanden, damit ist #250 auf der Hardware geprüft. DCS lief in beiden Fällen weiter.
- Der erste F-2-Lauf zeigte eine Lücke im Runner: Fällt der SSH-Zugang weg, bleibt der Side-Load eingehängt, und ein neuer Lauf hängt darüber ein und löst am Ende nur eine Lage (`src/LabPrinterTests/run-lab-tests.py:469`, `:507`). Deshalb vor dem zweiten Lauf der Rückbau von Hand.
- Die Release-Binärdateien unterscheiden sich von den geprüften Builds nur in der Versionszeile, die 6 Bytes kürzer ist.
- Offen für den Release-Umfang: Stufe 4 mit Tim vor Ort (Referenzfahrt, Feed Hold und Stopp in der Referenzfahrt und in einer G1-Bewegung, EP-17) und `HugeJobFileIsRefused` (Approval, Automatikmodus). Tim meldet, dass das TOOL1LC 8,3 °C bei 18 bis 20 °C Raumtemperatur liest. Eine andere Sitzung hat das um 09:46 UTC auf mp.6-dev.1 gemessen: Vssa-Filter 1087 Counts statt etwa 190, Vref 41700 statt etwa 42100, Kalibrierung klein (M308 S2 L-11 H-1). Mit gestoppten SDADC-Neustarts (ein 32-Bit-Wort per M122 P1007, danach zurückgeschrieben und geprüft) Vssa 104, Vref 42147, Sensor 2 bei 24,5 °C, zweimal reproduziert. Ursache nach dieser Messung: Der SDADC wandelt PA07/PA06 (Vref/Vssa, Duet3Expansion `src/Platform/Platform.cpp:888`), während ADC0 dieselben Pins abtastet, und die Phase zwischen beiden hängt vom Code-Timing ab, also vom Layout (gleiche Signatur wie Forum-Thread 39227 auf 3.6.3). Keine funktionale Änderung seit mp.5 im Analogpfad. Ob mp.5 es auf dem CHX zeigte, ist nicht gemessen. Ein Ergebnis auf mp.6-dev.1 sagt nichts über das Release-Binary, dessen Versionszeile 6 Bytes kürzer ist.
- Entscheidung Tim, 2026-11-26 nach dem Sync vom selben Tag: Das Release enthält den Sync (DSF #259 und #260, Meltingplot/RepRapFirmware#119, Meltingplot/Duet3Expansion#37, Meltingplot/CoreN2G#10, Meltingplot/RRFLibraries#6). Die Tags v3.7.0+mp.16, 3.7.0+1.mp.12 und 3.7.0+1.mp.6 kommen erst nach einer neuen Laborprüfung, denn diese Prüfung hier lief auf Bäumen ohne den Sync. Zwei Änderungen des Syncs brauchen die Hardware:
  - `GetMallocMutex` nimmt die Mutex jetzt, sobald der Scheduler gestartet ist (RRF `src/Platform/Tasks.cpp:115`, D3E `src/Platform/Tasks.cpp:144`, aus Duet3D d85662412 und 0f63349f). Heap-Nutzung bei angehaltenem Scheduler endet damit in der Assertion von FreeRTOS `src/queue.c:1682` und einem Reset mit `assertCalled` (RRF `src/Hardware/ExceptionHandlers.cpp:228`) statt in einem unbemerkt beschädigten Heap.
  - Meltingplot/CoreN2G#9 (TL1 in `design/open-findings.md`: ADC0 und SDADC wandeln nicht mehr gleichzeitig) kommt mit diesem Pin zum ersten Mal in ein Release.
  - Geplant für die Prüfung: Builds der Release-Bäume RRF aba5ca711 und D3E 70e52f01 mit den Versionszeilen des Releases, damit das Layout dem Release-Binary gleicht (siehe den Punkt zum TOOL1LC oben). `DuetControlServer -u` flasht sie, weil die Boards eine andere Version haben.

### 2026-11-26 nachmittags: Release-Prüfung auf den Release-Bäumen, automatischer Teil bestanden, neuer Befund TL3 am TOOL1LC

```
Datum, Zeit (UTC): 2026-11-26, 13:49:46 TOOL1LC-Auslesung vor dem Flash, um 13:50 Firmware-Side-Load D3E, um 13:52 Firmware-Side-Load RRF, 13:53:00 TOOL1LC-Auslesung (TL1), 13:54:49 bis 14:18:53 Standardlauf, 14:19:34 bis 14:21:52 Zähler AnalogIn, 14:22:22 bis 14:24:47 F-1, 14:24:50 bis 14:58:47 F-2, danach bis 15:32:40 Diagnose am TOOL1LC (TL3)
Anlass, PR, Commit: Release RRF 3.7.0+1.mp.12 (Meltingplot/RepRapFirmware#118 auf aba5ca711), D3E 3.7.0+1.mp.6 (Meltingplot/Duet3Expansion#35 auf 70e52f01) und DSF 3.7.0+mp.16 (#255, 5f0c0ff46), Umfang "Neues RRF-Release", mit dem Sync vom 2026-11-26 (Entscheidung Tim, siehe den vorigen Lauf)
DSF: 3.7.0+mp.15 im Image. Side-Load von DCS und DWS aus 5f0c0ff46, für F-1 und F-2 mit Fehlerinjektion
RRF: 3.7.0+1.mp.12 (Baum aba5ca711 mit der Versionszeile des Releases, sha256 e479fe28f84f0896...), Firmware-Side-Load. Erweiterungen 3.7.0+1.mp.6 (Baum 70e52f01 mit der Versionszeile des Releases; TOOL1LC sha256 0df88812e4f3d729..., EXP1HCL 86278329e9dbe32f..., SZP 6e2f061b65e0bd95...), Firmware-Side-Load, zuerst D3E, dann RRF, beide mit firmware/sideload/fw-sideload.sh. Danach am TOOL1LC allein die Diagnose-Builds 3.7.0+1.mp.6-dev.2 (sha256 8949c57f...) und dev.3 (sha256 1fd47acb...), Firmware-Side-Load
Stufen und Freigabe durch: 2, Tim vor Ort ("go i'm on site"), Diagnose-Builds ("2nd diag build now")
Standardlauf (E, RG-1 bis RG-16, A-14 bis A-16, Tests der Fork-Fixes): 102 bestanden, 0 fehlgeschlagen, 9 übersprungen, 38 explizit nicht gelaufen. Watchdog-Neustarts 5 x, wieder da nach 12,1 bis 12,2 s (Median 12,2 s)
Übersprungen im Standardlauf: 4 x, weil die Pakete des Images vor #225, #232 und #234 liegen (WebServerUnitRestartsIt, ControlServerRunsWithoutEffectiveCapabilities, FileReadsHaveTheRightsOfTheDsfUser, RootPluginServiceSettingsBelongToRoot); 3 x Freigabe je Lauf (SzpCoilKeepsUpdatingDuringAccelerometerRuns, RestartDuringAccelerometerRunKeepsLinkAndResetData, WebServerComesBackAfterAKill); 2 x viele Neustarts oder Abbrüche (NoReplyOfTheOldConnectionAfterRestart, ChannelWorksAfterAbortsCrossingMacroRequests). Diese neun sind nicht geprüft
F-1: 15 von 15 ESD-Fällen, 15 x Retry, 0 nicht injiziert, 0 Abweichungen vom Census
F-2: 672 von 3234 Fällen auf dem erweiterten Link, 672 x Retry, 0 nicht injiziert, 0 Abweichungen, 33 min 57 s
M122: RRF-Link-Zähler und DSF-Zähler in allen Blöcken 0 -> 0
Self-Test im Laufzeitraum: Standardlauf 5 Läufe, F-1 1 Lauf, F-2 1 Lauf, alle passed
Journalbefunde: Standardlauf in keiner Befundkategorie. Erwartet: je 6 x "Connection to controller has been reset", "RepRapFirmware has left the extended link", "Restarting full transfer because"; 10 x "Machine is halted", 17 x "Cannot store excessively long object model response", 3 x "Expression nesting too deep", 3 x "Failed to start macro file" der Refused-Tests. Bekannt: 1 x "Failed to find query for object model response", 7 x "Aborting orphaned macro file daemon.g". Nur notiert: 433 x "Resending packet #", 42 x "Client with outdated protocol version", 37 x "has no value", 33 x "predicted maximum temperature". F-1: 15 x injiziert, 5 x Bad header CRC, 10 x Bad data CRC, 2 x Machine is halted. F-2: 672 x injiziert, 118 x Restarting full transfer, 22 x Bad header CRC, 8 x Bad data CRC, 26 x Sending transfer # again, 2 x Machine is halted
Rückbau geprüft: ja, nach jedem Block. Der Firmware-Side-Load bleibt bis zum nächsten Neustart des SBC drauf, am TOOL1LC mit dem Diagnose-Build dev.3
```

Bewertung:
- Der automatische Teil des Release-Umfangs ist auf den Release-Bäumen ohne Befund gegen DSF, RRF und den Link durchgelaufen: Block E, RG ohne RG-17 und RG-18, A-14 bis A-16, die Tests der Fork-Fixes, F-1 und F-2. Die Journalkategorien gleichen dem Lauf vom Vormittag. Die beiden Änderungen des Syncs, die die Hardware brauchten (`GetMallocMutex` und CoreN2G #9), liefen in allen Blöcken mit, und die Erweiterungen liefen nach jedem Block. Ob ein Board zwischendurch mit `assertCalled` neu startete, zeigt der Runner nicht, denn er gibt nur "cause software" aus, das auch das M999 des Rückbaus erzeugt.
- TL1 auf dem Release-Build behoben (`design/open-findings.md`, TL1): 20.temp0 21,7 °C gegen 21 °C am Referenzthermometer von Tim, Vssa-Filter 270 Counts, Vref 42136. Vor dem Flash las mp.6-dev.1 um 13:49:46 17,15 °C.
- Neuer Befund TL3 (`design/open-findings.md`): Auf dem Release-Build laufen am TOOL1LC etwa 3,5 Wandlungen pro Minute in den Timeout, auf mp.6-dev.1 eine in Stunden. Die Diagnose-Builds zeigen, dass der DMAC die ersten 8 Bytes des Write-Back-Deskriptors des ADC-Kanals mit dem Zustand des TMC-Sendekanals überschreibt, wie Erratum 1.10.4 (DS80000740T, Seite 40) es beschreibt. Die Platine ist ein ATSAMC21G18A der Revision F (DSU DID 0x11010505), die das Erratum nennt. Im selben Build hat der TMC2209 des TOOL1LC etwa 3 Lesefehler pro Minute, nicht zeitgleich mit den Timeouts.
- Entscheidungen Tim, 2026-11-26:
  - TL3 ist ein Fehler. Stufe 4 entfällt für dieses Release: "we don't need to run stage 4 if we already have found a bug in the tool1lc".
  - Es kommen keine Tags, bis der Fix für TL3 im Labor geprüft ist ("Wait for the D3E fix"). Danach kommen DSF v3.7.0+mp.16, RRF 3.7.0+1.mp.12 und D3E 3.7.0+1.mp.6 zusammen.
- Offen für den Release-Umfang: der Fix für TL3 und seine Laborprüfung, `HugeJobFileIsRefused` (Approval).

### 2026-11-26 abends: A/B-Prüfung des TL3-Fixes am TOOL1LC (dev.4), ohne Timeouts

```
Datum, Zeit (UTC): 2026-11-26, 16:33:35 bis 16:34:48 Firmware-Side-Load TOOL1LC, 16:35:18 Kontrolle der Adressen, 16:35:43 bis 16:44:48 A/B-Lauf: Mutex an bis 16:38:45, aus bis 16:41:47, wieder an bis 16:44:48
Anlass, PR, Commit: Fix TL3, Meltingplot/CoreN2G#11 (33f1329) und Meltingplot/Duet3Expansion#38 (cb86a703)
DSF, RRF, EXP1HCL, SZP: wie nach dem vorigen Lauf, nicht geändert (RRF 3.7.0+1.mp.12, Erweiterungen 3.7.0+1.mp.6)
TOOL1LC: 3.7.0+1.mp.6-dev.4, die beiden Fix-Zweige plus RAM-Schalter dmaLockOff (0x20002bcc, 0 = Mutex an; nicht committet), sha256 a7b93449f80c8389..., Firmware-Side-Load mit firmware/sideload/fw-sideload.sh
Stufen und Freigabe durch: Tim ("yes"; "3/3/3", "do not have 30 mins right now"). Schalter mit M122 B20 P1007 A536882124 V1 und V0
Messung: je Phase 181 s, eine Auslesung pro Minute: RAM-Zähler (AnalogIn kumuliert, TMC seit dem vorigen M122), dann M122 B20
```

| Phase | ADC-Timeouts | ADC-Fehler | Sweeps/s | TMC-Lesungen/s | TMC-Lese-, Schreibfehler, Timeouts | CPU AIN | CPU TMC | temp0 |
|---|---|---|---|---|---|---|---|---|
| Mutex an | 0 | 0 | 501 | 499 | 0, 0, 0 | 7,5 % | 5,2 % | 22,86 °C |
| Mutex aus | 0 | 0 | 992 | 499 | 0, 0, 0 | 9,0 % | 4,2 % | 22,42 bis 22,47 °C |
| Mutex wieder an | 0 | 0 | 501 | 499 | 0, 0, 0 | 7,5 % | 5,2 % | 22,78 bis 22,84 °C |

Bewertung:
- In keiner Phase gab es einen ADC-Timeout, auch ohne Mutex nicht. Auf dem Release-Build (etwa 3,5 pro Minute) wären je Phase etwa 10 zu erwarten gewesen. Schon das Takten auf höchstens einen Sweep pro Tick senkt die Rate also stark. Ob sie ohne Mutex bei null bleibt, zeigen 3 Minuten nicht: mp.6-dev.1 lief ebenfalls mit etwa 1000 Sweeps/s und hatte einen Timeout in Stunden.
- Der TMC2209 hatte in den 9 Minuten keinen Lesefehler, auch ohne Mutex. Auf dev.3 waren es etwa 3 pro Minute.
- Mit Mutex halbiert sich die Sweep-Rate auf 501/s, im Gleichtakt mit den 499 TMC-Lesungen pro Sekunde. Jeder Wandler macht damit etwa 250 Sweeps/s.
- CPU: AIN 7,5 % mit Mutex, 9,0 % ohne, 10,2 % auf dev.3. TMC 5,2 % mit Mutex, 4,2 % ohne, 4,1 % auf dev.3. AIN und TMC zusammen 12,7 % mit Mutex, 13,2 % ohne, 14,3 % auf dev.3.
- temp0 las ohne Mutex 0,4 °C weniger als mit, und der Unterschied verschwand beim Zurückschalten wieder. Die Ursache ist nicht geklärt, denn mit dem Mutex ändert sich auch die Sweep-Rate. Das Referenzthermometer wurde nicht abgelesen.
- Der TOOL1LC bleibt bis zum nächsten Neustart des SBC auf dev.4, mit Mutex an.
- Entscheidungen Tim, 2026-11-26, nach dem Lauf:
  - Der Fix bleibt wie in CoreN2G#11 und Duet3Expansion#38, mit Takten und Mutex ("ja").
  - Für den Versatz von 0,4 °C braucht es keine Referenzmessung, denn temp0 wird pro Drucker kalibriert ("passt, wird per drucker kalibriert").
  - Es folgt eine Stunde Dauermessung, nur lesend, mit Mutex an ("ja").
