#!/usr/bin/env bash
set -euo pipefail

CONFIG="${KARE_DEVICE_CONFIG:-$HOME/.config/kare/device.env}"
LOCAL_PORT="${KARE_TUNNEL_LOCAL_PORT:-5285}"
REMOTE_PORT="${KARE_TUNNEL_REMOTE_PORT:-5285}"

if [[ ! -f "$CONFIG" ]]; then
    echo "Missing protected device configuration: $CONFIG" >&2
    exit 1
fi

set -a
# shellcheck disable=SC1090
. "$CONFIG"
set +a

: "${KARE_DEVICE_HOST:?KARE_DEVICE_HOST is not set}"
: "${KARE_DEVICE_USER:?KARE_DEVICE_USER is not set}"
: "${KARE_DEVICE_PASS:?KARE_DEVICE_PASS is not set}"

export SSHPASS="$KARE_DEVICE_PASS"
exec sshpass -e ssh \
    -o ExitOnForwardFailure=yes \
    -o ServerAliveInterval=30 \
    -o ServerAliveCountMax=3 \
    -o StrictHostKeyChecking=accept-new \
    -N \
    -L "${LOCAL_PORT}:127.0.0.1:${REMOTE_PORT}" \
    "${KARE_DEVICE_USER}@${KARE_DEVICE_HOST}"
