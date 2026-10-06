#!/usr/bin/env bash
set -euo pipefail

CONFIG="${KARE_DEVICE_CONFIG:-$HOME/.config/kare/device.env}"
LOCAL_PORT="${KARE_TUNNEL_LOCAL_PORT:-5285}"
REMOTE_PORT="${KARE_TUNNEL_REMOTE_PORT:-5285}"
READY_TIMEOUT="${KARE_TUNNEL_READY_TIMEOUT:-15}"
BASE_URL="http://127.0.0.1:${LOCAL_PORT}"

if [[ ! -f "$CONFIG" ]]; then
    echo "Missing protected device configuration: $CONFIG" >&2
    exit 1
fi

# shellcheck disable=SC1090
. "$CONFIG"

: "${KARE_DEVICE_HOST:?KARE_DEVICE_HOST is not set}"
: "${KARE_DEVICE_USER:?KARE_DEVICE_USER is not set}"
: "${KARE_API_KEY:?KARE_API_KEY is not set}"

for command in curl copilot ssh; do
    if ! command -v "$command" >/dev/null 2>&1; then
        echo "Required command is not installed: $command" >&2
        exit 1
    fi
done

ssh_command=(ssh)
if [[ -n "${KARE_DEVICE_PASS:-}" ]]; then
    if ! command -v sshpass >/dev/null 2>&1; then
        echo "sshpass is required when KARE_DEVICE_PASS is set." >&2
        exit 1
    fi
    ssh_command=(env "SSHPASS=$KARE_DEVICE_PASS" sshpass -e ssh)
fi

ssh_log=$(mktemp "${TMPDIR:-/tmp}/kare-tunnel.XXXXXX")
echo "Forwarding $BASE_URL to Kare on the configured device..."
"${ssh_command[@]}" \
    -o ExitOnForwardFailure=yes \
    -o ConnectTimeout=15 \
    -o ServerAliveInterval=30 \
    -o ServerAliveCountMax=3 \
    -o StrictHostKeyChecking=accept-new \
    -N \
    -L "${LOCAL_PORT}:127.0.0.1:${REMOTE_PORT}" \
    "${KARE_DEVICE_USER}@${KARE_DEVICE_HOST}" 2>"$ssh_log" &
tunnel_pid=$!

# shellcheck disable=SC2329
cleanup() {
    if kill -0 "$tunnel_pid" 2>/dev/null; then
        kill "$tunnel_pid" 2>/dev/null || true
        wait "$tunnel_pid" 2>/dev/null || true
    fi
    rm -f "$ssh_log"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

deadline=$((SECONDS + READY_TIMEOUT))
until curl -fsS --max-time 1 "$BASE_URL/health" >/dev/null 2>&1; do
    if ! kill -0 "$tunnel_pid" 2>/dev/null; then
        status=0
        wait "$tunnel_pid" || status=$?
        ((status != 0)) || status=1
        echo "SSH tunnel exited before Kare became ready (status $status)." >&2
        tail -20 "$ssh_log" >&2
        exit "$status"
    fi

    if ((SECONDS >= deadline)); then
        echo "Kare did not become ready through $BASE_URL within ${READY_TIMEOUT}s." >&2
        tail -20 "$ssh_log" >&2
        exit 1
    fi

    sleep 0.5
done

: >"$ssh_log"
echo "Kare is ready. Starting Copilot..."

export COPILOT_PROVIDER_BASE_URL="$BASE_URL/v1"
export COPILOT_PROVIDER_TYPE=openai
export COPILOT_PROVIDER_WIRE_API=completions
export COPILOT_PROVIDER_API_KEY="$KARE_API_KEY"
export COPILOT_PROVIDER_WIRE_MODEL="${KARE_WIRE_MODEL:-kare}"
export COPILOT_PROVIDER_MODEL_ID="${KARE_MODEL_ID:-kare}"
export COPILOT_MODEL="${KARE_MODEL_ID:-kare}"
export COPILOT_PROVIDER_MAX_PROMPT_TOKENS="${KARE_MAX_PROMPT_TOKENS:-7168}"
export COPILOT_PROVIDER_MAX_OUTPUT_TOKENS="${KARE_MAX_OUTPUT_TOKENS:-1024}"
export COPILOT_HOME="${KARE_COPILOT_HOME:-$HOME/.config/kare/copilot-home}"

mkdir -p "$COPILOT_HOME"
status=0
copilot "$@" || status=$?
if ((status != 0)) && [[ -s "$ssh_log" ]]; then
    echo "SSH tunnel diagnostics:" >&2
    tail -20 "$ssh_log" >&2
fi
exit "$status"
