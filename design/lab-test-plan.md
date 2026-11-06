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
| Neues DSF-Release oder Image | Block 0, E, RG ohne Side-Load, RG-17, F-1, F-2 |
| Neues RRF-Release | Wie neues Release, dazu `PortMatchesRealFirmware` durch die RRF-Sitzung (`LinkRecovery.cs:838`) |
| Erstdurchlauf des aktuellen Stands | Alle Blöcke unten |

### Blöcke

| Block | Inhalt | Stufe | Vorarbeit | Dauer (ca.) |
|---|---|---|---|---|
| 0 | Offline-Gate | keine | keine | 1 min |
| 1 | E, RG-1 bis RG-16, Auswertung Self-Test | 2 | keine | 10 min |
| 2 | RG-17 Dauerlauf | 1 | keine | 30 min |
| 3 | F-1, F-2 | 2 | keine | 45 min |
| 4 | A-1 bis A-7, A-10 | 1 und 2 | Tests bauen, für A-1 auch den Injektor erweitern | offen |
| 5 | A-8, A-9, A-11 bis A-13 | 3 | Tests bauen, Einzelfreigabe | offen |
| 6 | M-1 bis M-4 | 4 | Termin mit Tim | offen |

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
- `self-test/tick.g:47` startet den Flow mit `M98.3` auf dem HTTP-Kanal, wenn der Drucker idle ist.
- Im Journal lief der Autostart am 2026-11-06 von 18:16:03 bis 18:16:04 UTC.

Die Suite löst selbst Boots aus und nutzt denselben Kanal. Mögliche Folgen:
- `LabTest.cs:65-68` überspringt einen Test, wenn der Drucker 30 s lang nicht idle wird.
- `MessageBoxes.cs:29-36` schließt und zählt jede Box. Der Flow enthält `M291`-Boxen (`maintenance/self-test:94`, `:100`), die der Test mitzählen könnte.
- Im `M98.3`-Fall (RG-14) wird der Aufruf abgelehnt, solange der Flow den HTTP-Kanal belegt.

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
| RG-13 bis RG-16 | `MacrosOnOtherChannels.TriggerKeepsFiringWhileMacroRuns` für `M98.2`, `.3`, `.4`, `.5` | Neue Funktion #108 | Kriterien in `MacrosOnOtherChannels.cs:133-150`, unter anderem richtiger Kanal, Parameter `5,2,5,true`, Trigger feuert weiter |
| RG-17 | `LinkSoak.NoLostConnectionWhileIdle` | TfrRdy-Timeouts alle 15 bis 30 min | 30 min ohne `Lost connection to Duet`, kein RRF-Neustart |

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

### Neue Tests (noch zu bauen)

Alle als fork-eigene Tests in `src/LabPrinterTests`, jeweils mit Aufräumen im `finally`.

| ID | Änderung | Stufe | Ablauf | Soll |
|---|---|---|---|---|
| A-1 | #102 | 2 | Injektor erweitern, siehe unten. Fehler `Activation HeaderResponse ToDsf flip 2` scharf schalten, `M122 P1001` senden. DCS verbindet neu und aktiviert den Link, dabei trifft der Fehler | `SPI fault injected: ...`, etwa 60 s danach `No transfer with RepRapFirmware completed within 60000 ms` (`SPI.cs:311`), neuer DCS nach 5 s (`duetcontrolserver.service:9-10`), `Link capabilities All are active`, nach `M999` idle |
| A-2 | #99 | 2 | Fehler injizieren, DCS stoppen, `gpioget --numeric -c gpiochip0 25` lesen, DCS sofort starten | Pin liest 0, sonst ist der Fall ungültig. Verbunden nach höchstens 4,5 s (`SPI.cs:1709`), kein `timeout while waiting for TfrRdy pin` (`SPI.cs:1747`) |
| A-3 | #108 | 2 | Nach einem Boot den Autostart beobachten, währenddessen `M409` senden | `Starting macro file ... on channel HTTP`, `Self-test: quick check passed`, `M409` antwortet, Maschinen-Tick läuft weiter, `inputs[0]` wieder Tiefe 0, kein `Out-of-order reply` |
| A-4 | #108 | 1 | `M98.1`, `M98.7`, `M98.6`; `M98.3` über HTTP; `M98.2` mit fehlender Datei | `Invalid code channel number n` (`MCodeHandler.cs:613`), `Use M98 to start a macro on the same code channel` (`:617`), `Macro file ... not found` (`:648`) |
| A-5 | Zeilenprüfsummen | 1 | Drei Testmakros mit falschem CRC, Sprung in der Zeilenfolge und fehlendem CRC, jedes setzt je Zeile ein Global | Makro stoppt vor der fehlerhaften Zeile. Journal: `CRC error on line` (`ParserChecksum.cs:370`), `Expected line number` (`ParserAsync.cs:227`), `Missing CRC on line` (`ParserAsync.cs:116`) |
| A-6 | #67 | 2 | SBC-Trigger T30 mit laufendem Zähler, dann `M581.1 T30` mit nie wahrem RRF-Ausdruck | Bericht zeigt den neuen Ausdruck, der Zähler bleibt stehen (Wiki `Gcodes.md:7266`) |
| A-7 | #63 | 2 | SBC-Trigger T30 mit wahrem Ausdruck und `R-1` | Trigger feuert nicht, Gegenprobe mit `R0` feuert |
| A-8 | #88 | 3 | `M112`, Status 5 s beobachten, `M999` | Kein Fehler, `halted` springt nicht zurück, nach `M999` idle (`MCodeHandler.cs:749-770`, Wiki `Gcodes.md:2674`) |
| A-9 | #70 | 3 | `M111 P-1 O1`, einige Codes, `M111 P-1 O0` | DCS-PID unverändert, kein Stack Overflow im Journal (`MCodeHandler.cs:714-735`) |
| A-10 | #72, #82 | 1 | `M36` mit fehlender Datei, `M39`, `M39 S2` | Gültiges JSON `{"err":1,"fileName":...}` (`MCodeHandler.cs:477`), `GB` und `cluster size` (`MCodeHandler.cs:588`), `clsize` (`SDInfoResponse.cs:45`) |
| A-11 | #74, #80 | 3 | Testdatei in `0:/gcodes`, `M30` mit relativem Namen, dann `M30` und `M472` mit fehlendem Ziel | Datei gelöscht, `Failed to delete file ...: file not found` (`MCodeHandler.cs:423`), `Failed to delete file or directory ...: file or directory not found` (`MCodeHandler.cs:911`) |
| A-12 | #81 | 3 | `state.logLevel` und `state.logFile` lesen, `M929 S4`, `M929 S-1`, Zustand wiederherstellen | `parameter 'S' too high` (`MCodeHandler.cs:1195`), `expected non-negative integer after 'S'` (`MCodeHandler.cs:1191`), Protokollierung danach wie vorher |
| A-13 | #76 | 3 | `M606 S1` über HTTP ohne Job | Warnung `this command is valid only when running a job from a stored file` (RRF `GCodes5.cpp:213-216`), `inputs[12]` bleibt `unused` |

Hinweise:
- **A-1, Fehlerfall:** Auf dem erweiterten Link sagt der Census nach der Aktivierung keinen hängenden Fall voraus (`ExtendedLinkRecoversFromEverySingleFault`). Hängen kann es nur in der Übertragung, die den Link aktiviert (`LinkRecovery.cs:650-654`, 23 Fälle laut #102). Darunter ist der Einzelbitfehler `flip 2` in der Header-Antwort an DSF. Weitere Fälle sind `HeaderResponse ToDsf word 0x00000005` und `word 0xfefefefe` sowie `DataResponse ToRrf word 0x00000006`.
- **A-1, Injektor:** Er trifft heute nur die nächste Übertragung einer Art (`SpiFaultInjection.cs:238`, `:276-283`). Nötig sind:
  - eine neue Art `Activation`,
  - ein Hinweis aus `WriteActivateLinkCapabilities` (`SPI.cs:902-924`) an den Injektor, weil dieser den Paketinhalt erst in der Datenphase sieht (`SpiFaultInjection.cs:220-234`). Der Hinweis gilt nur im Test-Build (`SPI.cs:113`), Release-Builds bleiben unverändert,
  - ein eigener Test, weil der vorhandene nach 2 s selbst wiederherstellt (`SpiFaultInjection.cs:103`, `:134-138`).
- **A-1, Auslöser:** Einen Fehler von vor dem DCS-Start verwirft der Injektor (`SpiFaultInjection.cs:300-305`). Deshalb wird er bei laufendem DCS scharf geschaltet. `M122 P1001` startet nur RRF neu, und DCS fragt die Capabilities nach jedem Verbindungsaufbau und Reset neu ab (`design/sbc-link-requirements.md:123`).
- **A-1, Vorhersage:** Der Hänger ist bisher nur eine Census-Vorhersage für die Aktivierung. Hängt der Link am DUT nicht, ist das eine Abweichung vom Census. Sie wird als Befund festgehalten, dann werden die anderen Fälle versucht.
- **A-2, Fehlerfall:** #99 nutzte `RrfData Data ToDsf flip 0`. #105 hat genau diesen Pfad geändert, deshalb entscheidet die Pinmessung. Mit dem Fix ist die Prüfung bisher nur auf RRF mp.5 gelaufen, nie auf dem erweiterten Link.
- **A-3, Journal:** Der Autostart endet mit `Aborted macro file`, ein Lauf ohne `A1` mit `Finished macro file`. Das ist kein Fehler: Der automatische Pfad endet mit `M99` (`maintenance/self-test:99-101`). `M99` erscheint im Journal als Abbruch der letzten Datei (`MacroFile.cs:241`), genauso bei den Testmakros von RG-3.
- **A-3, Abdeckung:** Der Lauf vom 2026-11-06 zeigt bereits Start, `quick check passed` und kein `Out-of-order reply`. Offen ist der Fall mit `M409` während des Flows.
- **A-4:** `M98.n` steht nicht im Wiki. Das Soll stammt nur aus dem Code und #108.
- **A-5, Makrozeilen:** Ausgaben eines Makros erreichen die `M98`-Antwort nicht (`LabTest.cs:103`). Der Test wertet daher Globals und Journal aus.

### Manuelle Prüfungen (Stufe 4, Tim vor Ort)

| ID | Änderung | Ablauf | Soll |
|---|---|---|---|
| M-1 | Link im Druck, RRF mp.7 | Kurzer Testdruck, `M122` und `M409` in festem Takt, `M122` vorher und nachher | Job endet normal, `lost connections` und `connection resets` +0. `Full transfers per second` als Ausgangswert für R4 notieren (`design/sbc-link-requirements.md:117`) |
| M-2 | 9acd058f | Job bis zum Ende laufen lassen, danach zweiter Job mit `M32` aus einer Jobdatei | `stop.g` läuft, kein `Out-of-order reply`, zweiter Job startet |
| M-3 | #108 | Makro mit `M98.3` starten, dann `M32` senden | `M32` wartet hinter dem Makro, das ist so gewollt (Tim, 2026-11-06) |
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

**Erwartet je RRF-Reboot:** je einmal Reset und `RepRapFirmware has left the extended link` (`SPI.cs:1943`), dazu ein `Restarting full transfer because ...` (`SPI.cs:1843`, `:1870`, `:1889`).

**Erwartet in bestimmten Testfällen:**
- RG-3: 20 x `Failed to find corresponding state for code M291 P"never" S3` (`PipelineBase.cs:243`). Das `M291` steht im Testmakro hinter einem `M99` und wird absichtlich nie erreicht (`MacroReturns.cs:20`, `:54`, `:65`).
- RG-7 und jeder andere DCS-Neustart, auch beim Side-Load: `Machine is halted`, bis `M999` folgt (`Lab.cs:218-220`).
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

Rückbau prüfen, auch wenn `run-lab-tests.sh` ihn erledigt (`:69-91`):
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
  - `TriggerMacros.StringLiteralInExpression` hängt RRF auf und wird erst mit einem RRF-Fix ausgeführt.
  - USB-Kanäle und G/M als Makroparameter stehen in `design/open-findings.md`.
  - Stall vom 2026-11-02 um 22:13 UTC, TfrRdy-Flanken-Census, 2 von 42 DCS-Neustarts mit Stock-RRF und Hänger nach Firmware-Update sind ungeklärt.
  - Zwei parallele `M122`-Schleifen lösten am 2026-11-04 eine `DivideByZeroException` aus. Die Division ist unverändert (`SPI.cs:218`) und steht noch nicht in `design/open-findings.md`.
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
