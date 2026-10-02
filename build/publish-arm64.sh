#!/usr/bin/env bash
# Publishes a Kare project for the VENTUNO Q inside the pinned aarch64 Ubuntu 24.04 image.
#
# Usage:
#   build/publish-arm64.sh <project path> [output dir] [--aot|--no-aot]
#
# Examples:
#   build/publish-arm64.sh bench/Kare.DeviceProbe artifacts/kare-probe --aot
#   build/publish-arm64.sh bench/Kare.DeviceProbe artifacts/kare-probe --no-aot
#
# Native AOT is linked inside the container because an x86_64 host ld.bfd cannot link
# aarch64 objects. On an x86_64 workstation this runs under qemu and is slow.

set -euo pipefail

PROJECT="${1:?usage: publish-arm64.sh <project path> [output dir] [--aot|--no-aot]}"
OUTPUT="${2:-artifacts/$(basename "$PROJECT")}"
MODE="${3:---aot}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGE="kare-build-arm64:24.04"

ENGINE="${CONTAINER_ENGINE:-}"
if [[ -z "$ENGINE" ]]; then
  if command -v podman >/dev/null 2>&1; then
    ENGINE=podman
  elif command -v docker >/dev/null 2>&1; then
    ENGINE=docker
  else
    echo "Neither podman nor docker is available." >&2
    exit 1
  fi
fi

case "$MODE" in
  --aot)    AOT=true  ;;
  --no-aot) AOT=false ;;
  *) echo "Unknown mode: $MODE. Expected --aot or --no-aot." >&2; exit 2 ;;
esac

if [[ "$(uname -m)" != "aarch64" ]]; then
  # binfmt_misc must already have an aarch64 handler registered, otherwise the
  # container starts and then fails on the first instruction.
  if [[ ! -e /proc/sys/fs/binfmt_misc/qemu-aarch64 ]]; then
    echo "aarch64 emulation is not registered. Install qemu-user-static and binfmt support." >&2
    exit 1
  fi
  echo "Building aarch64 under emulation. This is slow but reproducible."
fi

echo "Building $IMAGE"
"$ENGINE" build --arch arm64 -t "$IMAGE" -f "$REPO_ROOT/build/Containerfile.arm64" "$REPO_ROOT/build"

# The host package cache is shared with the container. Re-downloading the ILCompiler,
# runtime packs, and the 100 MB ONNX Runtime GenAI package over an emulated network stack
# is slow enough to time out, and the packages are architecture tagged so sharing is safe.
NUGET_CACHE="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
mkdir -p "$NUGET_CACHE"

mkdir -p "$REPO_ROOT/$OUTPUT"

echo "Publishing $PROJECT to $OUTPUT (aot=$AOT)"
"$ENGINE" run --rm --arch arm64 \
  --security-opt label=disable \
  -v "$REPO_ROOT":/src \
  -v "$NUGET_CACHE":/nuget \
  -w /src \
  "$IMAGE" \
  dotnet publish "$PROJECT" \
    -c Release \
    -r linux-arm64 \
    --self-contained true \
    -p:PublishAot="$AOT" \
    -o "/src/$OUTPUT"

echo
echo "Output in $OUTPUT"
file "$REPO_ROOT/$OUTPUT"/* 2>/dev/null | grep -E 'ELF|shared object' | head -20 || true
