# Qualcomm runtime and models

Checked 2026-10-01.

GenieX 0.7.1 is Developer Preview. It publishes Linux ARM64 CLI, benchmark, Python, C SDK, Docker, and OpenAI-compatible server assets.

GenieX uses one C SDK under the CLI, Python, Java or Kotlin, Docker, and server interfaces.

`geniex serve` exposes an OpenAI-compatible endpoint at `http://127.0.0.1:18181/v1`.

GenieX explicitly lists Dragonwing IQ-8275, `QCS8275`, as `qualcomm-qcs8275`.

The runtime detects the VENTUNO Q and maps it to QCS8275.

Qualcomm AI Hub has QCS8275-specific assets for Qwen3 0.6B, 1.7B, 4B, and 8B. The 1.7B and 4B assets include Genie W4A16 bundles.

The Genie bundle is a per-chipset QAIRT artifact. It is not an ONNX Runtime GenAI model directory.

The separate `onnxruntime-qnn` 2.6.0 release publishes Linux ARM64 inference assets and uses QAIRT 2.50.40. It does not publish a Linux NuGet package.

Published VENTUNO Q Q4_0 GenieX llama.cpp numbers:

Qwen3 1.7B, 512 context: CPU 79.97 prefill tokens/s, 11.03 decode tokens/s. NPU 500.37 prefill tokens/s, 3.79 decode tokens/s.

Qwen3 1.7B, 4096 context: CPU 45.08 prefill tokens/s, 2.79 decode tokens/s. NPU 578.27 prefill tokens/s, 3.28 decode tokens/s.

Qwen3 4B, 512 context: CPU 32.26 prefill tokens/s, 5.82 decode tokens/s. NPU 283.26 prefill tokens/s, 2.83 decode tokens/s.

Qwen3 4B, 4096 context: CPU 17.85 prefill tokens/s, 1.11 decode tokens/s. NPU 284.36 prefill tokens/s, 2.26 decode tokens/s.

The NPU wins prompt processing. It does not automatically win decode. The W4A16 QAIRT path remains the important test because the public performance file does not include matching VENTUNO Q QAIRT numbers.

First device order:

1. Phi-4-mini INT4 through packaged ONNX Runtime GenAI CPU.
2. Qwen3 1.7B W4A16 through GenieX QAIRT.
3. Qwen3 4B W4A16 if memory, temperature, and latency permit.
4. Q4_0 CPU, GPU, and HTP comparisons through GenieX llama.cpp.
5. Custom ONNX Runtime GenAI QNN only if it adds a useful model or service advantage.

Sources:

https://github.com/qualcomm/GenieX

https://github.com/qualcomm/GenieX/blob/main/docs/en/get-started/platforms.mdx

https://github.com/qualcomm/ai-hub-models/tree/main/src/qai_hub_models/models/qwen3_1_7b

https://github.com/qualcomm/ai-hub-models/tree/main/src/qai_hub_models/models/qwen3_4b

https://github.com/onnxruntime/onnxruntime-qnn/releases/tag/v2.6.0
