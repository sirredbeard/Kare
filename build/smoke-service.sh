#!/usr/bin/env bash
# Smoke test the Kare OpenAI-compatible endpoint against a running service.
# Checks health, model listing, a non-streaming completion, and a streaming completion.
# Usage: build/smoke-service.sh [base-url] [api-key]
set -euo pipefail

BASE_URL="${1:-http://127.0.0.1:5285}"
API_KEY="${2:-${KARE_API_KEY:-}}"

AUTH=()
if [[ -n "$API_KEY" ]]; then
    AUTH=(-H "Authorization: Bearer $API_KEY")
fi

failures=0

check() {
    local name="$1"
    shift
    if "$@"; then
        echo "ok    $name"
    else
        echo "FAIL  $name"
        failures=$((failures + 1))
    fi
}

health() {
    curl -fsS --max-time 10 "$BASE_URL/health" | grep -q .
}

models() {
    local body
    body=$(curl -fsS --max-time 10 "${AUTH[@]}" "$BASE_URL/v1/models")
    echo "$body" | grep -q '"object":"list"'
}

completion() {
    local body
    body=$(curl -fsS --max-time 600 "${AUTH[@]}" \
        -H 'Content-Type: application/json' \
        -d '{"model":"kare-local","messages":[{"role":"user","content":"Reply with the single word: ready"}],"max_tokens":16,"stream":false}' \
        "$BASE_URL/v1/chat/completions")
    echo "$body"
    echo "$body" | grep -q '"chat.completion"' &&
        echo "$body" | grep -q '"kare_route"'
}

streaming() {
    local body
    body=$(curl -fsS --max-time 600 -N "${AUTH[@]}" \
        -H 'Content-Type: application/json' \
        -d '{"model":"kare-local","messages":[{"role":"user","content":"Count from one to five."}],"max_tokens":48,"stream":true}' \
        "$BASE_URL/v1/chat/completions")
    echo "$body" | tail -5
    echo "$body" | grep -q 'chat.completion.chunk' &&
        echo "$body" | grep -q '"kare_route"' &&
        echo "$body" | grep -q 'data: \[DONE\]'
}

rejects_oversized() {
    local status
    status=$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 "${AUTH[@]}" \
        -H 'Content-Type: application/json' \
        -d '{"model":"kare-local","messages":[]}' \
        "$BASE_URL/v1/chat/completions")
    [[ "$status" == "400" ]]
}

echo "Smoke testing $BASE_URL"
check "health" health
check "models" models
check "empty messages rejected with 400" rejects_oversized
check "non-streaming completion" completion
check "streaming completion" streaming

if ((failures > 0)); then
    echo "$failures check(s) failed"
    exit 1
fi

echo "all checks passed"
