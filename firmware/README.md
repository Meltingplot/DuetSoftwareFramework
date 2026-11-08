# Firmware

This folder builds the Duet firmware of the CHX 350 next to DSF, so that a DSF change and its RepRapFirmware (RRF) side can be built, compared and tested together. It is fork-only: upstream DSF has no `firmware/` folder.

## Contents

The projects are git submodules of the Meltingplot forks. Each one was pinned to the commit that the firmware of the lab printer was built from: RRF `3.7.0+1.mp.8` and Duet3Expansion `3.7.0+1.mp.4`, with the library commits listed in their release notes. RRF has moved on since to `3.7-dev` 9678339c8, which adds Meltingplot/RepRapFirmware#43 (macro request after InvalidateChannel), #44 (root check before the SBC hand-off) and #45 (macro closal after InvalidateChannel) to `3.7.0+1.mp.8`.

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

`build.sh` runs the build in Docker. The image (`Dockerfile`) is what the `mp-release.yml` workflows of RepRapFirmware and Duet3Expansion set up: Arm GNU Toolchain 15.2.Rel1 and Duet3D's CrcAppender 1.4.0, both pinned by their SHA-256, plus g++ and python3 for the harness. The first run builds the image.

```
firmware/build.sh main                 # Duet3_MB6HC, the lab printer's main board
firmware/build.sh main all             # every configuration of the release (MB6HC, MB6XD, Mini5plus)
firmware/build.sh expansion            # TOOL1LC, EXP1HCL and SZP, the boards of the release
firmware/build.sh expansion TOOL1LC
firmware/build.sh harness              # RepRapFirmware/Tools/sbc-harness: build/sbc-harness-usb1 and -usb0
firmware/build.sh harness check        # its own tests
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
- On 2026-11-08 both passed, 6 of 6 each, against DSF `v3.7-dev` 9e6449d1.

### In CI

`.github/workflows/rrf-harness.yml` runs this comparison for every pull request and push to `v3.7-dev`:

- **Checkout:** only `firmware/RepRapFirmware`, without history. The harness needs nothing of the other submodules.
- **Build:** g++ 13 on the runner instead of the Docker image, which would fetch the Arm toolchain for nothing. gcov comes in through the make variables, so the submodule stays unchanged: `make CXX="g++-13 --coverage" CXXFLAGS="-O0 -g"`. The harness Makefile compiles and links with `$(CXX)`, and `-O0` replaces its default `-O1 -g`.
- **Tests:** the two `dotnet test` commands above. The job fails when a test fails, and also when one does not run: without `RRF_HOST_HARNESS` the tests are ignored, and `dotnet test` still exits with 0.
- **Coverage:** gcovr 8.6 reads the counts of both variants. It and its dependencies are pinned by version and wheel hash in `.github/workflows/rrf-harness-requirements.txt`, which says where each hash comes from. The step summary lists the lines and branches of `DataTransfer.cpp`, `CRC32.cpp` and `SbcInterface::TaskLoop` that the comparison reached, for both builds together and for each alone. There is no coverage goal yet.
- **Own tests:** the harness's `make check` runs last, so that its counts stay out of the report.
- **Artifact:** `rrf-harness-results` holds the TRX files and the gcovr reports (HTML, Cobertura, JSON).

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
