#!/usr/bin/env bash
# Copy a published artifact directory to the device and optionally run a command there.
# Device configuration lives outside the repository. Create ~/.config/kare/device.env with:
#   KARE_DEVICE_HOST=...
#   KARE_DEVICE_USER=...
#   KARE_DEVICE_PASS=...        (or use an SSH key and leave this unset)
#   KARE_DEVICE_DIR=/home/<user>/kare
# chmod 600 that file. Never commit it.
#
# Usage:
#   build/deploy.sh <local-dir> <remote-subdir> [command to run on the device...]
set -euo pipefail

CONFIG="${KARE_DEVICE_CONFIG:-$HOME/.config/kare/device.env}"
if [[ ! -f "$CONFIG" ]]; then
    echo "Missing $CONFIG. See the header of this script." >&2
    exit 1
fi

set -a
# shellcheck disable=SC1090
. "$CONFIG"
set +a

: "${KARE_DEVICE_HOST:?set KARE_DEVICE_HOST}"
: "${KARE_DEVICE_USER:?set KARE_DEVICE_USER}"
KARE_DEVICE_DIR="${KARE_DEVICE_DIR:-/home/$KARE_DEVICE_USER/kare}"

LOCAL_DIR="${1:?usage: deploy.sh <local-dir> <remote-subdir> [command...]}"
REMOTE_SUB="${2:?usage: deploy.sh <local-dir> <remote-subdir> [command...]}"
shift 2

TARGET="$KARE_DEVICE_USER@$KARE_DEVICE_HOST"
REMOTE_DIR="$KARE_DEVICE_DIR/$REMOTE_SUB"

ssh_run() {
    if [[ -n "${KARE_DEVICE_PASS:-}" ]]; then
        sshpass -p "$KARE_DEVICE_PASS" ssh -o StrictHostKeyChecking=accept-new -o ConnectTimeout=15 "$TARGET" "$@"
    else
        ssh -o StrictHostKeyChecking=accept-new -o ConnectTimeout=15 "$TARGET" "$@"
    fi
}

scp_push() {
    if [[ -n "${KARE_DEVICE_PASS:-}" ]]; then
        sshpass -p "$KARE_DEVICE_PASS" scp -o StrictHostKeyChecking=accept-new -r "$@"
    else
        scp -o StrictHostKeyChecking=accept-new -r "$@"
    fi
}

echo "Deploying $LOCAL_DIR to device:$REMOTE_SUB"
ssh_run "mkdir -p '$REMOTE_DIR'"
scp_push "$LOCAL_DIR"/. "$TARGET:$REMOTE_DIR/"
ssh_run "chmod -R u+rwX '$REMOTE_DIR' && find '$REMOTE_DIR' -maxdepth 1 -type f ! -name '*.so' ! -name '*.json' ! -name '*.dbg' -exec chmod +x {} +"

if (($# > 0)); then
    echo "Running on device: $*"
    ssh_run "cd '$REMOTE_DIR' && $*"
fi
