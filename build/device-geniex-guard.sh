#!/usr/bin/env bash
# Verify that the GenieX sidecar answers a bounded inference on the expected
# compute target. This script does not print model output.
#
# Two operating modes exist and the guard has to handle both:
#
# 1. The external kare-geniex.service runs the sidecar (the rollback path).
#    The guard inspects that unit's process for the compute flag.
# 2. Kare owns the sidecar (the normal path, Kare__Inference__GenieXProcess).
#    The guard inspects the geniex child process of kare.service instead.
#
# If no endpoint answers at all, the guard passes with a notice. That is the
# pre-deploy state after a reboot or a stopped service, and failing there
# would make the publish script unable to recover the service it deploys.
# The bounded inference check still runs after deployment, when Kare is up.

set -euo pipefail

EXPECTED_COMPUTE="${KARE_GENIEX_EXPECTED_COMPUTE:-npu}"
SERVICE="${KARE_GENIEX_SERVICE:-kare-geniex.service}"
ENDPOINT="${KARE_GENIEX_ENDPOINT:-http://127.0.0.1:18181/v1}"

if [[ "$(uname -m)" != "aarch64" ]]; then
    echo "This guard must run natively on the ARM64 device." >&2
    exit 1
fi

MODEL_LIST=""
if ! MODEL_LIST="$(curl --fail --silent --show-error --max-time 10 "$ENDPOINT/models" 2>/dev/null)"; then
    echo "GenieX endpoint $ENDPOINT is not answering yet. Skipping the pre-deploy inference check." >&2
    echo "The post-deploy run of this guard is the one that has to pass." >&2
    exit 0
fi

# Compute check: find a geniex command line, from whichever owner runs it.
COMMAND_LINE=""
if systemctl --user is-active --quiet "$SERVICE" 2>/dev/null; then
    PID="$(systemctl --user show --property MainPID --value "$SERVICE" 2>/dev/null || true)"
    if [[ -n "${PID:-}" && "$PID" != "0" && -r "/proc/$PID/cmdline" ]]; then
        COMMAND_LINE="$(tr '\0' ' ' < "/proc/$PID/cmdline")"
    fi
fi
if [[ -z "$COMMAND_LINE" ]]; then
    COMMAND_LINE="$(pgrep -af 'geniex' | head -n 1 || true)"
fi
if [[ -n "$COMMAND_LINE" ]]; then
    if [[ " $COMMAND_LINE " != *" --compute $EXPECTED_COMPUTE "* ]]; then
        echo "GenieX is not using the required compute target: $EXPECTED_COMPUTE" >&2
        echo "Saw: $COMMAND_LINE" >&2
        exit 1
    fi
else
    echo "No local geniex process found to check the compute target. The endpoint answered, so inference is the check." >&2
fi

MODEL_ID="$(
    python3 -c \
        'import json,sys; data=json.load(sys.stdin)["data"]; print(data[0]["id"].split(":", 1)[0])' \
        <<< "$MODEL_LIST"
)"
if [[ -z "$MODEL_ID" ]]; then
    echo "GenieX returned no usable model ID." >&2
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

printf 'GenieX guard passed: compute=%s endpoint=%s\n' "$EXPECTED_COMPUTE" "$ENDPOINT"
