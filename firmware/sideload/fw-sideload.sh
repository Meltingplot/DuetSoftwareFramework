#!/bin/bash
# Flash a Duet firmware build on a CHX 350 without an image build, the way scripts/sideload.sh of
# dwc-quality-assurance sideloads QA: sends the firmware files with fw-sideload-remote.sh through the
# HMI to the SBC in one SSH connection and runs the remote part there as root, through sudo of the
# SBC's administrator. What the remote part does: fw-sideload-remote.sh.
#
#   Tools/fw-sideload.sh install FILE...   e.g. ReprapFirmware/Duet3_MB6HC/Duet3Firmware_MB6HC.bin
#   Tools/fw-sideload.sh status|remove
#
# Each FILE keeps its name on the SBC, and that has to be the one RepRapFirmware derives from the
# board type (Duet3Firmware_<board>.bin). Runs in WSL or Git Bash. Hosts and accounts default to
# the lab printer; .fw-sideload.env next to this script overrides them and holds the host keys:
#   FW_SIDELOAD_HMI_USER=meltingplot   FW_SIDELOAD_HMI=192.168.172.143   FW_SIDELOAD_HMI_HOSTKEY=SHA256:…
#   FW_SIDELOAD_USER=mpadmin           FW_SIDELOAD_SBC=10.42.0.2         FW_SIDELOAD_SBC_HOSTKEY=SHA256:…
#   FW_SIDELOAD_PLINK=<plink.exe>      # PuTTY's plink with Pageant (gpg-agent with the hardware key)
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
MODE=${1:-}
[ $# -gt 0 ] && shift
case "$MODE" in
    install) [ $# -gt 0 ] || MODE="" ;;
    status|remove) [ $# -eq 0 ] || MODE="" ;;
    *) MODE="" ;;
esac
[ -n "$MODE" ] || { echo "usage: $0 install FILE... | status | remove" >&2; exit 2; }

[ -f "$HERE/.fw-sideload.env" ] && . "$HERE/.fw-sideload.env"
: "${FW_SIDELOAD_HMI_USER:=meltingplot}" "${FW_SIDELOAD_HMI:=192.168.172.143}"
: "${FW_SIDELOAD_USER:=mpadmin}" "${FW_SIDELOAD_SBC:=10.42.0.2}"
if [ -z "${FW_SIDELOAD_PLINK:-}" ]; then
    for FW_SIDELOAD_PLINK in "/mnt/c/Program Files/PuTTY/plink.exe" "/c/Program Files/PuTTY/plink.exe"; do
        [ -f "$FW_SIDELOAD_PLINK" ] && break
    done
fi
[ -f "$FW_SIDELOAD_PLINK" ] || { echo "no plink.exe; set FW_SIDELOAD_PLINK" >&2; exit 1; }

winpath() {
    if command -v wslpath >/dev/null; then wslpath -w "$1"; else cygpath -w "$1"; fi
}

remote() {  # command on the SBC; stdin goes along
    # plink talks to Pageant (gpg-agent with the hardware key); the proxy command runs on Windows,
    # hence its Windows path. No MSYS path conversion of the arguments under Git Bash.
    local proxy
    proxy="\"$(winpath "$FW_SIDELOAD_PLINK")\" -batch ${FW_SIDELOAD_HMI_HOSTKEY:+-hostkey $FW_SIDELOAD_HMI_HOSTKEY} -l $FW_SIDELOAD_HMI_USER -nc $FW_SIDELOAD_SBC:22 $FW_SIDELOAD_HMI"
    (cd /tmp && MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' "$FW_SIDELOAD_PLINK" -batch \
        ${FW_SIDELOAD_SBC_HOSTKEY:+-hostkey "$FW_SIDELOAD_SBC_HOSTKEY"} \
        -proxycmd "$proxy" -l "$FW_SIDELOAD_USER" "$FW_SIDELOAD_SBC" "$1")
}

bundle=$(mktemp -d)
trap 'rm -rf "$bundle"' EXIT
cp "$HERE/fw-sideload-remote.sh" "$bundle/"
names=()
for file in "$@"; do
    name=$(basename "$file")
    [[ $name =~ ^Duet3Firmware_[A-Za-z0-9_]+\.bin$ ]] ||
        { echo "$file: the SBC needs it as Duet3Firmware_<board>.bin, the name RepRapFirmware asks for" >&2; exit 1; }
    [ -f "$file" ] || { echo "no $file" >&2; exit 1; }
    cp "$file" "$bundle/$name"
    names+=("$name")
    echo "sideloading $name ($(stat -c %s "$file") bytes, dated $(date -r "$file" '+%F %T'))"
done
sums=$(cd "$bundle" && sha256sum -- *)

# one connection: check for sudo, unpack into ~/fw-sideload, check the checksums, run the remote
# part as root. sudo -n, so an account without sudo fails at once instead of waiting for a password
# nobody can type: on the CHX 350 image only the administrator mpadmin has sudo.
tar -czf - -C "$bundle" . | remote "{ sudo -n true 2>/dev/null || { echo \"fw-sideload: \$(id -un) has no sudo on the SBC; set FW_SIDELOAD_USER to its administrator (mpadmin)\" >&2; exit 1; }; } \
    && rm -rf ~/fw-sideload && mkdir -p ~/fw-sideload && tar -xzf - -C ~/fw-sideload \
    && cd ~/fw-sideload && printf '%s\n' '$sums' | sha256sum --quiet -c - \
    && sudo -n bash ~/fw-sideload/fw-sideload-remote.sh $MODE ${names[*]:-}"
