#!/usr/bin/env bash
# Builds the small C ABI bridge that safely exposes ONNX Runtime GenAI plugin
# registration to .NET. Run this natively on the deployment architecture.
#
# Usage:
#   build/build-genai-bridge.sh [GenAI NuGet package directory] [output directory]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PACKAGE_DIR="${1:-$HOME/.nuget/packages/microsoft.ml.onnxruntimegenai/0.17.1}"
OUTPUT_DIR="${2:-$REPO_ROOT/artifacts/genai-bridge}"
INCLUDE_DIR="$PACKAGE_DIR/build/native/include"
RID="linux-arm64"

case "$(uname -m)" in
  aarch64|arm64) RID="linux-arm64" ;;
  x86_64|amd64) RID="linux-x64" ;;
  *) echo "Unsupported architecture: $(uname -m)" >&2; exit 2 ;;
esac

NATIVE_DIR="$PACKAGE_DIR/runtimes/$RID/native"
if [[ ! -f "$INCLUDE_DIR/ort_genai_c.h" ]]; then
  echo "Missing ONNX Runtime GenAI headers under $INCLUDE_DIR." >&2
  exit 1
fi

if [[ ! -f "$NATIVE_DIR/libonnxruntime-genai.so" ]]; then
  echo "Missing ONNX Runtime GenAI native library under $NATIVE_DIR." >&2
  exit 1
fi

mkdir -p "$OUTPUT_DIR"
g++ \
  -std=c++20 \
  -O2 \
  -fPIC \
  -fvisibility=hidden \
  -shared \
  "$REPO_ROOT/native/kare_onnxruntime_genai_bridge.cpp" \
  -I"$INCLUDE_DIR" \
  -L"$NATIVE_DIR" \
  -Wl,-rpath,'$ORIGIN' \
  -lonnxruntime-genai \
  -o "$OUTPUT_DIR/libkare_onnxruntime_genai_bridge.so"

file "$OUTPUT_DIR/libkare_onnxruntime_genai_bridge.so"
