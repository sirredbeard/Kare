#!/usr/bin/env bash
# Verify that the device GenieX sidecar is running on the expected compute target
# and can complete a bounded inference. This script does not print model output.

set -euo pipefail

EXPECTED_COMPUTE="${KARE_GENIEX_EXPECTED_COMPUTE:-npu}"
SERVICE="${KARE_GENIEX_SERVICE:-kare-geniex.service}"
ENDPOINT="${KARE_GENIEX_ENDPOINT:-http://127.0.0.1:18181/v1}"

if [[ "$(uname -m)" != "aarch64" ]]; then
    echo "This guard must run natively on the ARM64 device." >&2
    exit 1
fi

systemctl --user is-active --quiet "$SERVICE" || {
    echo "$SERVICE is not active." >&2
    exit 1
}

PID="$(systemctl --user show --property MainPID --value "$SERVICE")"
if [[ -z "$PID" || "$PID" == "0" || ! -r "/proc/$PID/cmdline" ]]; then
    echo "Unable to inspect the $SERVICE process." >&2
    exit 1
fi

COMMAND_LINE="$(tr '\0' ' ' < "/proc/$PID/cmdline")"
if [[ " $COMMAND_LINE " != *" --compute $EXPECTED_COMPUTE "* ]]; then
    echo "$SERVICE is not using the required compute target: $EXPECTED_COMPUTE." >&2
    exit 1
fi

MODEL_LIST="$(curl --fail --silent --show-error --max-time 10 "$ENDPOINT/models")"
MODEL_ID="$(
    python3 -c \
        'import json,sys; data=json.load(sys.stdin)["data"]; print(data[0]["id"].split(":", 1)[0])' \
        <<< "$MODEL_LIST"
)"
if [[ -z "$MODEL_ID" ]]; then
    echo "$SERVICE returned no usable model ID." >&2
    exit 1
fi

PAYLOAD="$(
    MODEL_ID="$MODEL_ID" python3 -c \
        'import json,os; print(json.dumps({"model":os.environ["MODEL_ID"],"messages":[{"role":"user","content":"Reply OK. /no_think"}],"temperature":0,"max_tokens":1,"enable_think":False}))'
)"
curl --fail --silent --show-error --max-time 30 \
    "$ENDPOINT/chat/completions" \
    -H 'Content-Type: application/json' \
    --data-binary "$PAYLOAD" \
    >/dev/null

printf 'GenieX guard passed: compute=%s service=%s\n' "$EXPECTED_COMPUTE" "$SERVICE"
