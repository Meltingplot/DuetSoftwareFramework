#!/usr/bin/env bash
# Runs the tests for finding K32 (design/dsf-findings-upstream.md) against the code of Duet3D/DuetSoftwareFramework.
# Usage: design/k32-upstream/reproduce.sh [directory for the checkout]
# See README.md in this folder for what the run shows.
set -euo pipefail

# Duet3D/DuetSoftwareFramework v3.7-dev as checked for the report
UPSTREAM=f76ffc7078c61d19009fc54d9867c6196dd55ca6
# Fork commit the test files are taken from: the last one before the channel epoch (#167), which upstream does not have
FORK=38a601c63e1337aab4b75541e2cb372dad25409f

here=$(cd "$(dirname "$0")" && pwd)
repo=$(git -C "$here" rev-parse --show-toplevel)
work=${1:-$(mktemp -d)/dsf-upstream}

if ! git -C "$repo" cat-file -e "$UPSTREAM^{commit}" 2>/dev/null; then
    git -C "$repo" fetch --no-tags https://github.com/Duet3D/DuetSoftwareFramework.git v3.7-dev
fi
git -C "$repo" worktree add --detach "$work" "$UPSTREAM"

# The fake firmware and the test of the fork, unchanged. The tests of the fake firmware itself stay behind
git -C "$repo" archive "$FORK" \
    src/UnitTests/DcsServices.cs \
    src/UnitTests/FakeFirmware/FakeCode.cs \
    src/UnitTests/FakeFirmware/FakeExpressionEvaluator.cs \
    src/UnitTests/FakeFirmware/FakeFirmware.cs \
    src/UnitTests/FakeFirmware/FakeFirmwareHost.cs \
    src/UnitTests/FakeFirmware/FakeFirmwareIap.cs \
    src/UnitTests/FakeFirmware/FakeGCodeBuffer.cs \
    src/UnitTests/FakeFirmware/FakeGCodes.cs \
    src/UnitTests/FakeFirmware/FakeMachineState.cs \
    src/UnitTests/FakeFirmware/FakeObjectModel.cs \
    src/UnitTests/FakeFirmware/FakePacket.cs \
    src/UnitTests/FakeFirmware/FakeReply.cs \
    src/UnitTests/FakeFirmware/FakeSbcRequest.cs \
    src/UnitTests/SPI/EmergencyStopCrossingMacroEnd.cs \
    src/UnitTests/Utility/FirmwareImage.cs \
    | tar -x -C "$work"

# What upstream cannot compile: the link capabilities, one parameter of ResendPacket, and the test that needs the fork's SPI hooks
git -C "$work" apply "$here/fake-firmware-for-upstream.patch"

# The same case in the generic host
cp "$here/K32UpstreamHostOutcome.cs" "$work/src/UnitTests/SPI/"

# The product code is the one of upstream: no tracked file may differ
if [ -n "$(git -C "$work" status --porcelain --untracked-files=no)" ]; then
    echo "Tracked files of upstream were changed:" >&2
    git -C "$work" status --porcelain --untracked-files=no >&2
    exit 2
fi

status=0
dotnet test "$work/src/UnitTests" \
    --filter "FullyQualifiedName~EmergencyStopCrossingMacroEnd|FullyQualifiedName~K32UpstreamHostOutcome" \
    --logger "console;verbosity=normal" || status=$?

cat <<EOF

Checkout: $work (remove it with: git -C "$repo" worktree remove --force "$work")
dotnet test ended with $status.
With the finding present, the run reports 1 failed and 2 passed:
  Failed EmergencyStopWhileTheClosalOfAMacroIsPending
  Passed EmergencyStopWhenRrfClosesAMacroInTheTransferOfTheStop
  Passed M112OutcomeInGenericHostWhenTheClosalOfAMacroIsPending
EOF
