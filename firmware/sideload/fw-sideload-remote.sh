#!/bin/bash
# Runs on the CHX 350 SBC as root; fw-sideload.sh sends it with the firmware files and starts it.
# Flashes a Duet firmware build for testing without an image build:
#
#   install FILE...  copy the image's /opt/dsf/sd/firmware (part of the read-only root) to /run, put
#                    FILE... in, mount the copy read-only over the image's and run
#                    DuetControlServer -u, which flashes every board whose firmware version differs
#                    from its file. A later install keeps the files of the earlier ones
#   status           the firmware folder and, per board, the running version against its file's
#   remove           take the copy down and run DuetControlServer -u, which flashes the image's
#                    firmware back
#
# Nothing outlives a reboot: the copy lives in /run, and on every boot mp-dsf-firmware (the image's
# own DuetControlServer -u) flashes the image's firmware back.
#
# DuetControlServer -u compares version strings only (the last word of the firmware identifier):
# a build that carries the version a board runs already is never flashed, so a test build needs a
# version string of its own.
set -euo pipefail

HERE=$(dirname "$(readlink -f "$0")")
FW=/opt/dsf/sd/firmware
RUN=/run/fw-sideload
COPY=$RUN/firmware
LIST=$RUN/sideloaded
DCS=/opt/dsf/bin/DuetControlServer
MODE=${1:-status}
shift || true

say() { printf '\n== %s\n' "$*"; }
die() { echo "fw-sideload: $*" >&2; exit 1; }

[ "$(id -u)" = 0 ] || die "run as root"

# DSF's HTTP API on the SBC itself
api() {  # method path
    local key
    key=$(curl -s --max-time 5 "http://127.0.0.1/machine/connect?password=reprap" |
        python3 -c 'import json,sys; print(json.load(sys.stdin)["sessionKey"])' 2>/dev/null) || return 0
    curl -s --max-time 30 -X "$1" -H "X-Session-Key: $key" "http://127.0.0.1/machine/$2" || true
    curl -s --max-time 5 -H "X-Session-Key: $key" "http://127.0.0.1/machine/disconnect" >/dev/null || true
}

model_field() {  # a Python expression over the object model m; nothing when DSF cannot be asked
    api GET model | python3 -c "import json,sys; m=json.load(sys.stdin); print($1)" 2>/dev/null || true
}

boot_time() {  # when RepRapFirmware last started, in seconds since the epoch; nothing if unknown
    local up
    up=$(model_field "int(m['state']['upTime'])")
    [[ $up =~ ^[0-9]+$ ]] && echo $(($(date +%s) - up))
    return 0
}

# DuetControlServer -u flashes; while the image's firmware check after boot still runs, both would
# flash at once
require_idle() {
    local status
    ! systemctl is-active -q mp-dsf-firmware || die "mp-dsf-firmware (the image's firmware check after boot) still runs; try again when it is done"
    status=$(model_field 'm["state"]["status"]')
    [ "$status" = idle ] || die "the machine is '${status:-unreachable}', not idle; flashing resets the mainboard"
}

# A file's firmware identifier and version, read the way DuetControlServer reads them
# (Utility/Firmware.cs): the identifier's address at 0x20, the load address at 0x24
VERSION_PY='
import struct
def identifier(path):
    data = open(path, "rb").read()
    if len(data) < 0x28:
        return None
    address, load = struct.unpack_from("<II", data, 0x20)
    if not (address > load and address - load < len(data)):
        return None
    text = data[address - load:address - load + 129].split(b"\0")[0].decode("utf-8", "replace")
    return text if 0 < len(text) <= 128 else None
def version(path):
    text = identifier(path)
    return text.split(" ")[-1] if text else None
'

# DuetControlServer -u until every board is up to date, three passes at most. It returns while the
# mainboard restarts after a flash (or after the reset it sends when only expansion boards were
# flashed), so each further pass waits for RepRapFirmware to report a later start and to be idle
# again, as mp-dsf-firmware does.
flash() {
    local pass out boot now
    for pass in 1 2 3; do
        require_idle
        boot=$(boot_time)
        say "DuetControlServer -u (pass $pass of 3)"
        out=$("$DCS" -u < /dev/null 2>&1) || { printf '%s\n' "$out"; die "DuetControlServer -u failed"; }
        printf '%s\n' "$out"
        grep -q 'up-to-date' <<< "$out" && return 0
        say "waiting for the mainboard to restart and to be idle again"
        for _ in $(seq 60); do
            sleep 5
            now=$(boot_time)
            [ -n "$now" ] && [ "$now" -gt $((${boot:-0} + 10)) ] &&
                [ "$(model_field 'm["state"]["status"]')" = idle ] && continue 2
        done
        die "the mainboard did not come back idle within 5 minutes"
    done
    die "boards still differ from their files after 3 passes"
}

report() {
    say "firmware folder $FW"
    if mountpoint -q "$FW"; then
        echo "sideloaded copy, until the next boot or remove:"
        cat "$LIST" 2>/dev/null || true
    else
        echo "the image's"
    fi
    say "boards (running version, version of its file in $FW)"
    api GET model | python3 -c "$VERSION_PY"'
import json, os, sys
fw = sys.argv[1]
for board in json.load(sys.stdin).get("boards", []):
    name = board.get("firmwareFileName") or ""
    path = os.path.join(fw, name)
    file = version(path) if name and os.path.isfile(path) else None
    running = board.get("firmwareVersion")
    state = "up to date" if file == running else "no file" if file is None else "differs, -u flashes it"
    can, kind = board.get("canAddress") or 0, board.get("shortName") or board.get("name")
    print(f"{can:>3}  {kind}  {running}  file {file}  ({state})")
' "$FW" 2>/dev/null || echo "(DSF does not answer)"
}

do_install() {
    local name version
    [ $# -gt 0 ] || die "no firmware files"
    for name in "$@"; do
        [[ $name =~ ^Duet3Firmware_[A-Za-z0-9_]+\.bin$ ]] || die "$name is no Duet3Firmware_<board>.bin"
        [ -f "$HERE/$name" ] || die "no $HERE/$name"
        [ -f "$FW/$name" ] || die "the image has no $name; RepRapFirmware flashes only the file named after the board type"
    done
    # every file: built for the board its name says, used by a board here, and of a version that
    # board does not run yet, else DuetControlServer -u would skip it
    api GET model | python3 -c "$VERSION_PY"'
import json, os, re, sys
here, names = sys.argv[1], sys.argv[2:]
boards = json.load(sys.stdin).get("boards", [])
failed = False
for name in names:
    text = identifier(os.path.join(here, name))
    board_type = re.fullmatch(r"Duet3Firmware_(\w+)\.bin", name).group(1)
    users = [b for b in boards if b.get("firmwareFileName") == name]
    new = version(os.path.join(here, name))
    problem = None
    if text is None:
        problem = "has no firmware identifier"
    elif board_type not in text.split(" "):
        problem = f"is a build for \"{text}\", not for {board_type}"
    elif not users:
        problem = "is used by no board on this machine"
    elif all(b.get("firmwareVersion") == new for b in users):
        problem = (f"carries {new}, the version its board(s) run already, and DuetControlServer -u "
                   "would skip it: give the build a version string of its own")
    if problem:
        print(f"fw-sideload: {name} {problem}", file=sys.stderr)
        failed = True
    else:
        running = ", ".join(sorted({b.get("firmwareVersion") or "?" for b in users}))
        print(f"{name}: {new} for {len(users)} board(s) running {running}")
sys.exit(1 if failed else 0)
' "$HERE" "$@" || die "refused, nothing changed"
    require_idle

    say "firmware folder: a copy in $RUN with $*, mounted read-only over the image's"
    rm -rf "$RUN.new"
    mkdir -p "$RUN.new"
    cp -a "$FW" "$RUN.new/firmware"  # the image's, or the copy of an earlier install
    cp -a "$LIST" "$RUN.new/sideloaded" 2>/dev/null || true
    for name in "$@"; do
        install -o dsf -g dsf -m 0644 "$HERE/$name" "$RUN.new/firmware/$name"
        sed -i "/^$name /d" "$RUN.new/sideloaded" 2>/dev/null || true
        version=$(python3 -c "$VERSION_PY"'
import sys; print(version(sys.argv[1]))' "$HERE/$name")
        echo "$name $version sha256:$(sha256sum < "$HERE/$name" | cut -c1-16) $(date '+%F %T')" >> "$RUN.new/sideloaded"
    done
    ! mountpoint -q "$FW" || umount "$FW"
    rm -rf "$RUN"
    mv "$RUN.new" "$RUN"
    mount --bind "$COPY" "$FW"
    mount -o remount,bind,ro "$FW"
    cat "$LIST"

    flash
    report
}

do_remove() {
    mountpoint -q "$FW" || { echo "nothing sideloaded, $FW is the image's"; report; return 0; }
    require_idle
    say "firmware folder: the image's again"
    umount "$FW"
    rm -rf "$RUN"
    flash
    report
}

case "$MODE" in
    install) do_install "$@" ;;
    status) report ;;
    remove) do_remove ;;
    *) die "usage: $0 install FILE...|status|remove" ;;
esac
