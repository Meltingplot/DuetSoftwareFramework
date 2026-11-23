# Firmware

This folder builds the Duet firmware of the CHX 350 next to DSF, so that a DSF change and its RepRapFirmware (RRF) side can be built, compared and tested together. It is fork-only: upstream DSF has no `firmware/` folder.

## Contents

The projects are git submodules of the Meltingplot forks. They were first pinned to the commits that the firmware of the lab printer was built from: RRF `3.7.0+1.mp.8` and Duet3Expansion `3.7.0+1.mp.4`, with the library commits listed in their release notes. Since then the pins have followed the fork branches:

- **RRF release `3.7.0+1.mp.9`** (a7199f29f) added the following to `3.7.0+1.mp.8`:
  - Meltingplot/RepRapFirmware#43: macro request after InvalidateChannel.
  - #44: root check before the SBC hand-off.
  - #45: macro closal after InvalidateChannel.
  - #46: reply of a code that DSF dropped for InvalidateChannel.
  - #47: channel epoch on the SBC link.
- **RRF release `3.7.0+1.mp.10`** (803315451) added the following to `3.7.0+1.mp.9`:
  - #50: order of releasing a string or array handle.
  - #51: order of changing the kinematics.
  - #52: raw pointer check.
- **Duet3D sync of 2026-11-16:**
  - RRF and Duet3Expansion merged Duet3D `3.7-dev` up to e6f2d19c9 and 34d0a513 (Meltingplot/RepRapFirmware#49, Meltingplot/Duet3Expansion#17).
  - CoreN2G merged Duet3D `3.7-dev` up to 0e34cea and keeps the fork's SAMC21 and SAME70 fixes (Meltingplot/CoreN2G#3).
  - CANlib, RRFLibraries and WiFiSocketServerRTOS are at their Duet3D heads (Meltingplot/CANlib#1, Meltingplot/RRFLibraries#1, Meltingplot/WiFiSocketServerRTOS#11).
- **Duet3D sync of 2026-11-21:**
  - RRF merged the fork's `3.7-dev` up to #54, then Duet3D `3.7-dev` up to a507cb8f0 and Duet3D `3.7-ch` up to eed7cf1fc (Duet3D/RepRapFirmware#1335), merged as Meltingplot/RepRapFirmware#49 (3593e30fe).
  - Duet3Expansion merged Duet3D `3.7-dev` up to 02000a7c and `3.7-ch` up to 8cfc9949.
  - CoreN2G merged Duet3D `3.7-dev` up to 45ae60a, which contains its `3.7-ch`.
  - CANlib and RRFLibraries are at their Duet3D heads 7b5866c and 08746c8.
  - All fork PRs of the sync were merged on 2026-11-21, and the submodules point at their merge commits, whose trees equal the tested branch heads.
  - The version strings read `3.7.0+1.mp.10` and `3.7.0+1.mp.4` until the next fork release, so a build from these pins is not the released firmware of that name.
- **Fork branches of 2026-11-23:**
  - RRF 2f2a8af5b: the fork PRs Meltingplot/RepRapFirmware#55 to #88 on top of the sync (`git log --first-parent 3593e30fe..2f2a8af5b`).
  - Duet3Expansion 169c8280: Meltingplot/Duet3Expansion#18 to #26, among them #20 (permanent allocation and malloc in another task) and #24 (step interrupt enabled by a step error inside `Move::AddLinearSegments`), and the pointer and alloc checks.
  - FreeRTOS d3ce7a3: Meltingplot/FreeRTOS#1. The end of a critical section restores the interrupt mask that its caller had, and a task that blocks with a mask or a critical section of its own stops in `vAssertCalled`.
  - RRFLibraries 888b479: Meltingplot/RRFLibraries#2 (a new ReadWriteLock lock record while another task is inside malloc).
  - The version strings still read `3.7.0+1.mp.10` and `3.7.0+1.mp.4`.
- **RRF release `3.7.0+1.mp.11`** (2e31913b0) and **Duet3Expansion release `3.7.0+1.mp.5`** (16c3abf3): the fork branches of 2026-11-23 with the version strings (Meltingplot/RepRapFirmware#89, Meltingplot/Duet3Expansion#27), nothing else. The libraries are unchanged.

| Submodule | Branch | Used by |
|---|---|---|
| [RepRapFirmware](https://github.com/Meltingplot/RepRapFirmware) | `3.7-dev` | main board firmware, SBC link host harness |
| [Duet3Expansion](https://github.com/Meltingplot/Duet3Expansion) | `3.7-dev` | expansion and tool board firmware |
| [CANlib](https://github.com/Meltingplot/CANlib) | `3.7-dev` | both |
| [CoreN2G](https://github.com/Meltingplot/CoreN2G) | `3.7-dev` | both |
| [FreeRTOS](https://github.com/Meltingplot/FreeRTOS) | `3.7-dev` | both |
| [RRFLibraries](https://github.com/Meltingplot/RRFLibraries) | `3.7-dev` | both |
| [LibTinyusb](https://github.com/Meltingplot/LibTinyusb) | `3.7-dev` | main board (has a submodule of its own) |
| [LibMbedTls](https://github.com/Meltingplot/LibMbedTls) | `master` | main board |
| [WiFiSocketServerRTOS](https://github.com/Meltingplot/WiFiSocketServerRTOS) | `master` | main board (headers only) |
| [Qfplib-M0-full](https://github.com/Meltingplot/Qfplib-M0-full) | `3.7-dev` | expansion boards |

The projects find each other as siblings (`LIBRARIES_DIR ?= ..` in both top-level Makefiles), which is why they sit next to each other here.

DSF itself does not need the submodules. Fetch them only for firmware work:

```
git submodule update --init --recursive firmware
```

## Building

`build.sh` runs the build in Docker. The image (`Dockerfile`) is what the `mp-release.yml` workflows of RepRapFirmware and Duet3Expansion set up: Arm GNU Toolchain 15.2.Rel1 and Duet3D's CrcAppender 1.4.0, both pinned by their SHA-256, plus g++ and python3 for the harness and libicu, which CrcAppender needs to embed files (`Duet3_MB6HC_no_SD`). The first run builds the image.

```
firmware/build.sh main                 # Duet3_MB6HC, the lab printer's main board
firmware/build.sh main all             # every configuration of the release (MB6HC, MB6XD, Mini5plus)
firmware/build.sh expansion            # TOOL1LC, EXP1HCL and SZP, the boards of the release
firmware/build.sh expansion TOOL1LC
firmware/build.sh harness              # RepRapFirmware/Tools/sbc-harness: build/sbc-harness-usb1 and -usb0
firmware/build.sh harness check        # its own tests
firmware/build.sh pointer-check        # RepRapFirmware/Tools/pointer-check, see below
firmware/build.sh shell
```

- **Outputs** stay in the submodules (`RepRapFirmware/Duet3_MB6HC/Duet3Firmware_MB6HC.bin`, `Duet3Expansion/TOOL1LC/Duet3Firmware_TOOL1LC.bin`, ...). Their `.gitignore` files cover them.
- **Build times:** about 6 min for the first MB6HC build, including the image, and 40 s for the three expansion boards.
- **Reproducible:** checked on 2026-11-08, these builds equal the release binaries (RRF `3.7.0+1.mp.8` MB6HC, Duet3Expansion `3.7.0+1.mp.4` TOOL1LC, EXP1HCL and SZP). Only the build date and time strings and the appended CRC differ.

## The SBC link against the real RRF code

The harness compiles RRF's `SBC/DataTransfer.cpp` and `Storage/CRC32.cpp` unchanged, plus the text of `SbcInterface::TaskLoop`, for the PC (see its README). `LinkRecovery.PortMatchesRealFirmware` replays the census of the DSF unit tests through it and compares every exchange with `FakeRrfSpi`:

```
firmware/build.sh harness
H=firmware/RepRapFirmware/Tools/sbc-harness/build
RRF_HOST_HARNESS=$H/sbc-harness-usb1 RRF_HOST_POLL=1 RRF_HOST_RULE=1 dotnet test src/UnitTests --filter "FullyQualifiedName~PortMatchesRealFirmware"
RRF_HOST_HARNESS=$H/sbc-harness-usb0 RRF_HOST_RULE=1 dotnet test src/UnitTests --filter "FullyQualifiedName~PortMatchesRealFirmware"
```

- `RRF_HOST_POLL=1` belongs to usb1, the MB6HC variant, which polls while not connected.
- `RRF_HOST_RULE=1` means the RRF has the rule of Meltingplot/RepRapFirmware#41, which `3.7.0+1.mp.8` has.
- On 2026-11-08 both passed, 6 of 6 each, against DSF `v3.7-dev` 9e6449d1. On 2026-11-16 they passed again with the RRF of the Duet3D sync, against DSF `v3.7-dev` 52f9602b. On 2026-11-21 they passed with the RRF of the sync of that day, against DSF `v3.7-dev` 677ccfaa.

### In CI

`.github/workflows/rrf-harness.yml` runs this comparison for every pull request and push to `v3.7-dev`:

- **Checkout:** only `firmware/RepRapFirmware`, without history. The harness needs nothing of the other submodules.
- **Build:** g++ 13 on the runner instead of the Docker image, which would fetch the Arm toolchain for nothing. gcov comes in through the make variables, so the submodule stays unchanged: `make CXX="g++-13 --coverage" CXXFLAGS="-O0 -g"`. The harness Makefile compiles and links with `$(CXX)`, and `-O0` replaces its default `-O1 -g`.
- **Tests:** the two `dotnet test` commands above. The job fails when a test fails, and also when one does not run: without `RRF_HOST_HARNESS` the tests are ignored, and `dotnet test` still exits with 0.
- **Coverage:** gcovr 8.6 reads the counts of both variants. It and its dependencies are pinned by version and wheel hash in `.github/workflows/rrf-harness-requirements.txt`, which says where each hash comes from. The step summary lists the lines and branches of `DataTransfer.cpp`, `CRC32.cpp` and `SbcInterface::TaskLoop` that the comparison reached, for both builds together and for each alone. There is no coverage goal yet.
- **Own tests:** the harness's `make check` runs last, so that its counts stay out of the report.
- **RRF host tests:** after that the job runs `make check` of `Tools/heap-test` (string heap, Meltingplot/RepRapFirmware#50) and `Tools/kinematics-test` (`Move::SetKinematics`, #51) with g++ 13, each plain and with AddressSanitizer and UBSan, and of `Tools/driver-data-test` (driver data of `ExpansionManager`, #54). Like the harness they read only `RepRapFirmware/src`.
- **Artifact:** `rrf-harness-results` holds the TRX files and the gcovr reports (HTML, Cobertura, JSON).

## Raw pointers that can be reseated

`RepRapFirmware/Tools/pointer-check` fails for each raw pointer member or variable outside a function that can be reseated and that its `allowlist.txt` does not name (see its README). It reads the DWARF of a `-g` build with the Arm toolchain's `readelf`, so it runs in the build container:

```
firmware/build.sh pointer-check               # Duet3_MB6HC, Duet3_MB6XD and Duet3Mini5plus, about 3.5 min
firmware/build.sh pointer-check Duet3_MB6HC   # one configuration; allowlist entries of the others show up as notes
```

It rebuilds the configurations with `DEBUG_FLAGS=-g`, which changes only the debug sections, so the outputs in `RepRapFirmware/<config>` are then `-g` builds. The `mp-release.yml` workflow of RepRapFirmware runs the same check before it uploads a release. The DSF CI does not, because it would need every submodule and the Arm toolchain.

## Test builds on the lab printer

- **Version:** a test build needs a version string of its own, because `DuetControlServer -u` flashes a board only when its version differs from the file's. Set it in `RepRapFirmware/src/Version.h` (`MAIN_VERSION`) or `Duet3Expansion/src/Version.h` (`VERSION`), e.g. `3.7.0+1.mp.9-dev.1`.
- **Side-load:** `sideload/fw-sideload.sh` (from the RRF workspace, unchanged) flashes it without an image build:
  - It copies the image's `/opt/dsf/sd/firmware` to `/run`, puts the files in and mounts the copy over it.
  - Then it runs `DuetControlServer -u`. A reboot or `fw-sideload.sh remove` flashes the image's firmware back.
  - What runs on the SBC is in `sideload/fw-sideload-remote.sh`.

```
firmware/sideload/fw-sideload.sh install firmware/RepRapFirmware/Duet3_MB6HC/Duet3Firmware_MB6HC.bin
firmware/sideload/fw-sideload.sh status
firmware/sideload/fw-sideload.sh remove
```

- **Rules:** flashing the lab printer needs Tim's go for each side-load. It resets the main board, and on the fork the lost link triggers an emergency stop that switches ATX off. A build that breaks the SPI link needs Tim on site to recover the board.
- **Hosts and accounts:** they default to the lab printer. `sideload/.fw-sideload.env` overrides them and holds the SSH host keys (`FW_SIDELOAD_HMI_HOSTKEY`, `FW_SIDELOAD_SBC_HOSTKEY`). The file is local and ignored by git.
- **Access:** plink with the hardware token, as for the lab tests (`src/LabPrinterTests/README.md`).

## Changing the firmware

- **Push access:** the submodules fetch over HTTPS. To push, give a submodule an SSH push URL, e.g. `git -C firmware/RepRapFirmware remote set-url --push origin git@github.com:Meltingplot/RepRapFirmware.git`.
- **Fix:** make it on a branch inside the submodule, push it to the fork and open the PR there, against `3.7-dev` (or `master`).
- **Pin:** once the fix is merged in the fork, move the pin here in a DSF PR: `git submodule update --remote firmware/RepRapFirmware`, then commit `firmware/RepRapFirmware`.
- **Releases:** they stay with the forks' `mp-release.yml` workflows, which build from the fork branches, not from these pins.
