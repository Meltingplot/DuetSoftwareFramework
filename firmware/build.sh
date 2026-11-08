#!/bin/bash
# Build the firmware in this folder inside Docker, the way the release workflows of the Meltingplot forks do, see README.md
#
#   firmware/build.sh main [CONFIG...]       RepRapFirmware, default Duet3_MB6HC; "all" builds the configurations of the release
#   firmware/build.sh expansion [BOARD...]   Duet3Expansion, default the boards of the release: TOOL1LC EXP1HCL SZP
#   firmware/build.sh harness [TARGET...]    host harness of the SBC link (RepRapFirmware/Tools/sbc-harness), default its "all"
#   firmware/build.sh shell                  a shell in the build container
#
# DSF_FIRMWARE_IMAGE names the Docker image (default dsf-firmware-build)
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
IMAGE=${DSF_FIRMWARE_IMAGE:-dsf-firmware-build}
MODE=${1:-}
[ $# -gt 0 ] && shift

usage() {
    sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//' >&2
    exit 2
}

[ -f "$HERE/RepRapFirmware/src/Version.h" ] ||
    { echo "The submodules are missing: git submodule update --init --recursive firmware" >&2; exit 1; }

# The Dockerfile copies nothing, so it is built without a context instead of sending the submodules to the daemon
docker build -q -t "$IMAGE" - < "$HERE/Dockerfile" > /dev/null

run() {  # directory below firmware/, command...
    local tty=()
    [ -t 0 ] && [ -t 1 ] && tty=(-it)
    docker run --rm "${tty[@]}" --user "$(id -u):$(id -g)" -e HOME=/tmp -v "$HERE:/work" -w "/work/$1" "$IMAGE" "${@:2}"
}

jobs=$(nproc)
case "$MODE" in
    main)
        [ $# -gt 0 ] || set -- Duet3_MB6HC
        for config in "$@"; do
            run RepRapFirmware make -j"$jobs" "$config"
        done
        ls -l "$HERE"/RepRapFirmware/*/Duet3Firmware_*.bin 2>/dev/null || true
        ;;
    expansion)
        [ $# -gt 0 ] || set -- TOOL1LC EXP1HCL SZP
        # The top-level Makefile of Duet3Expansion includes one board makefile per invocation
        for board in "$@"; do
            run Duet3Expansion make -j"$jobs" "$board"
        done
        ls -l "$HERE"/Duet3Expansion/*/Duet3Firmware_*.bin 2>/dev/null || true
        ;;
    harness)
        run RepRapFirmware/Tools/sbc-harness make -j"$jobs" "$@"
        ls -l "$HERE"/RepRapFirmware/Tools/sbc-harness/build/sbc-harness-* 2>/dev/null || true
        ;;
    shell)
        run . bash
        ;;
    *)
        usage
        ;;
esac
