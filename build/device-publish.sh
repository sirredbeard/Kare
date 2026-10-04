#!/usr/bin/env bash
# Publish and deploy Kare from its Git checkout on the ARM64 target device.
#
# Usage:
#   build/device-publish.sh [--jit|--aot] [--test]
#
# --jit is the default iteration path. It produces a framework-dependent ARM64
# deployment and relies on DOTNET_ROOT being configured for the systemd service.
# --aot preserves the slower self-contained Native AOT release validation path.

set -euo pipefail

MODE="jit"
RUN_TESTS=false
for argument in "$@"; do
    case "$argument" in
        --jit) MODE="jit" ;;
        --aot) MODE="aot" ;;
        --test) RUN_TESTS=true ;;
        *)
            echo "Unknown argument: $argument" >&2
            echo "usage: build/device-publish.sh [--jit|--aot] [--test]" >&2
            exit 2
            ;;
    esac
done

if [[ "$(uname -m)" != "aarch64" ]]; then
    echo "This script must run natively on the ARM64 device." >&2
    exit 1
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${KARE_DOTNET:-$REPO_ROOT/.dotnet/dotnet}"
DEPLOY_ROOT="${KARE_DEPLOY_ROOT:-$HOME/kare/service}"
PROJECT="$REPO_ROOT/src/Kare.Service/Kare.Service.csproj"
COMMIT="$(git -C "$REPO_ROOT" rev-parse --short HEAD)"
OUTPUT="$REPO_ROOT/artifacts/kare-service-arm64-$MODE"
RELEASE="$DEPLOY_ROOT/releases/$COMMIT-$MODE"
CURRENT="$DEPLOY_ROOT/current"

if [[ ! -x "$DOTNET" ]]; then
    echo "Missing project-local .NET SDK: $DOTNET" >&2
    exit 1
fi

if [[ "$OUTPUT" != "$REPO_ROOT/artifacts/"* ||
      "$RELEASE" != "$DEPLOY_ROOT/releases/"* ||
      "$CURRENT" != "$DEPLOY_ROOT/current" ]]; then
    echo "Refusing to use an unexpected publish or release path." >&2
    exit 1
fi

if [[ "$RUN_TESTS" == true ]]; then
    "$DOTNET" test "$REPO_ROOT/tests/Kare.Tests/Kare.Tests.csproj" \
        --no-restore \
        --verbosity minimal
fi

rm -rf "$OUTPUT"
if [[ "$MODE" == "aot" ]]; then
    "$DOTNET" publish "$PROJECT" \
        -c Release \
        -r linux-arm64 \
        --self-contained true \
        -p:PublishAot=true \
        -p:StripSymbols=true \
        -o "$OUTPUT" \
        --verbosity minimal
else
    "$DOTNET" publish "$PROJECT" \
        -c Release \
        -r linux-arm64 \
        --self-contained false \
        -p:PublishAot=false \
        -o "$OUTPUT" \
        --verbosity minimal
fi

rm -rf "$RELEASE"
mkdir -p "$RELEASE"
cp -a "$OUTPUT"/. "$RELEASE"/
chmod -R u+rwX "$RELEASE"
find "$RELEASE" -maxdepth 1 -type f \
    ! -name '*.so' ! -name '*.json' ! -name '*.dbg' \
    -exec chmod +x {} +

if [[ -e "$CURRENT" && ! -L "$CURRENT" ]]; then
    LEGACY="$DEPLOY_ROOT/releases/legacy-current-$(date -u +%Y%m%d%H%M%S)"
    mv "$CURRENT" "$LEGACY"
fi

NEXT="$DEPLOY_ROOT/current.next"
rm -f "$NEXT"
ln -s "$RELEASE" "$NEXT"
mv -Tf "$NEXT" "$CURRENT"
systemctl --user restart kare.service
curl --fail --silent \
    --retry 30 \
    --retry-delay 1 \
    --retry-connrefused \
    --max-time 2 \
    http://127.0.0.1:5285/health
printf '\nDeployed %s from %s\n' "$MODE" "$COMMIT"
