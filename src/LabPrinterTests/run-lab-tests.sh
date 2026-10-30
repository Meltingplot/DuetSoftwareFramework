#!/bin/bash
# Run the lab printer tests through a plink tunnel, see README.md
#
# Usage: run-lab-tests.sh [--side-load] [dotnet test arguments...]
#   --side-load  publish DCS from this tree and bind-mount its DLLs over the image's copies for the run
#   Example:     run-lab-tests.sh --side-load --filter "TestCategory!=Restart"
set -euo pipefail

PLINK=${PLINK:-"/mnt/c/Program Files/PuTTY/plink.exe"}
JUMP_HOST=${JUMP_HOST:-meltingplot@192.168.172.143}
SBC=${SBC:-10.42.0.2}
SBC_USER=${SBC_USER:-mpadmin}
TUNNEL_PORT=${TUNNEL_PORT:-18080}
SIDE_LOAD_DIR=/var/tmp/lab-tests
SIDE_LOAD_DLLS="DuetAPI.dll DuetControlServer.dll DuetSharedLibrary.dll"

ROOT=$(cd "$(dirname "$0")/../.." && pwd)
export PATH=$HOME/.dotnet:$PATH

side_load=0
if [ "${1:-}" = "--side-load" ]; then
    side_load=1
    shift
fi

# Run a script read from stdin on the SBC
sbc() {
    "$PLINK" -ssh -agent -A -batch "$JUMP_HOST" "ssh -A -o BatchMode=yes $SBC_USER@$SBC bash -s"
}

# After a DCS restart, send M999 if RRF is halted and wait until the printer is idle
reset_firmware() {
    sbc <<'EOF'
status() { curl -s -m 2 localhost/machine/status | python3 -c 'import json, sys; print(json.load(sys.stdin)["state"]["status"])' 2>/dev/null || echo down; }
for i in $(seq 120); do
  s=$(status)
  [ "$s" = halted ] || [ "$s" = idle ] && break
  sleep 1
done
if [ "$s" = halted ]; then
  pid=$(systemctl show -p MainPID --value duetcontrolserver)
  key=$(curl -s -m 5 'localhost/machine/connect?password=reprap' | python3 -c 'import json, sys; print(json.load(sys.stdin)["sessionKey"])')
  curl -s -m 10 -X POST -H "X-Session-Key: $key" --data-binary M999 localhost/machine/code > /dev/null || true
  # DCS exits after sending the reset, wait for the new instance
  for i in $(seq 120); do
    p=$(systemctl show -p MainPID --value duetcontrolserver)
    [ "$p" != 0 ] && [ "$p" != "$pid" ] && break
    sleep 1
  done
fi
for i in $(seq 180); do
  [ "$(status)" = idle ] && exit 0
  sleep 1
done
echo "The printer did not become idle" >&2
exit 1
EOF
}

tunnel_pid=
side_loaded=0
ota_stopped=0
cleanup() {
    status=$?
    if [ -n "$tunnel_pid" ]; then
        kill "$tunnel_pid" 2> /dev/null || true
    fi
    if [ "$side_loaded" = 1 ]; then
        echo "Removing the side-loaded DLLs"
        # A plain umount reports the DLLs as busy
        sbc <<EOF
for dll in $SIDE_LOAD_DLLS; do
  findmnt -n /opt/dsf/bin/\$dll > /dev/null && sudo -n umount -l /opt/dsf/bin/\$dll
done
sudo -n rm -rf $SIDE_LOAD_DIR
sudo -n systemctl restart duetcontrolserver
EOF
        reset_firmware || status=1
    fi
    if [ "$ota_stopped" = 1 ]; then
        echo "sudo -n systemctl start mp-ota-gate.timer" | sbc
    fi
    exit $status
}
trap cleanup EXIT

if ! echo true | sbc; then
    echo "Cannot reach $SBC_USER@$SBC through $JUMP_HOST. Is the hardware token plugged in?" >&2
    exit 1
fi

# An update must not start while the tests run
echo "sudo -n systemctl stop mp-ota-gate.timer" | sbc
ota_stopped=1

if [ "$side_load" = 1 ]; then
    out=$(mktemp -d)
    echo "Publishing DCS from $ROOT"
    dotnet publish "$ROOT/src/DuetControlServer/DuetControlServer.csproj" -c Release -r linux-arm64 --self-contained false -o "$out" --nologo -v quiet
    for dll in $SIDE_LOAD_DLLS; do
        echo "Uploading $dll"
        {
            echo "sudo -n mkdir -p $SIDE_LOAD_DIR"
            echo "base64 -d <<'DLL' | sudo -n tee $SIDE_LOAD_DIR/$dll > /dev/null"
            base64 "$out/$dll"
            echo "DLL"
        } | sbc
    done
    rm -rf "$out"

    sbc <<EOF
for dll in $SIDE_LOAD_DLLS; do
  sudo -n mount --bind $SIDE_LOAD_DIR/\$dll /opt/dsf/bin/\$dll
done
sudo -n systemctl restart duetcontrolserver
EOF
    side_loaded=1
    reset_firmware
    echo "Side-loaded $SIDE_LOAD_DLLS from $(git -C "$ROOT" rev-parse --short HEAD)$(git -C "$ROOT" diff --quiet HEAD -- src || echo ' with uncommitted changes')"
fi

python3 -I "$ROOT/src/LabPrinterTests/tunnel.py" "$PLINK" "$JUMP_HOST" "$SBC:80" "$TUNNEL_PORT" &
tunnel_pid=$!
for i in $(seq 20); do
    curl -s -m 5 -o /dev/null "http://127.0.0.1:$TUNNEL_PORT/machine/status" && break
    if [ "$i" = 20 ]; then
        echo "The tunnel to the printer's HTTP API does not work" >&2
        exit 1
    fi
    sleep 1
done

export LAB_PRINTER_URL="http://127.0.0.1:$TUNNEL_PORT"
LAB_PRINTER_SHELL=$(printf '%q ' "$PLINK" -ssh -agent -A -batch "$JUMP_HOST" "ssh -A -o BatchMode=yes $SBC_USER@$SBC bash -s")
export LAB_PRINTER_SHELL
dotnet test "$ROOT/src/LabPrinterTests/LabPrinterTests.csproj" --logger "console;verbosity=normal" "$@"
