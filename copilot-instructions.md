# Technical Reference for Kare

This document covers hardware, deployment, configuration, and development details. For the project overview, see README.md.

## Target device

The Arduino VENTUNO Q runs Ubuntu 24.04.5 LTS on aarch64. It has a Qualcomm Dragonwing IQ8 / QCS8275 platform with a Hexagon Tensor AI Processor, Adreno GPU, 16 GB LPDDR5 memory, 64 GB eMMC, and an M.2 NVMe slot.

Prefer NVMe for models, indexes, logs, and cache data when available.

## Configuration and cloud routing

Kare keeps cloud routing disabled by default. Repository defaults define no cloud model catalog.

Set `KARE_CONFIG_FILE` to an absolute path for a protected JSON override. The schema is demonstrated in `examples/KARE_CONFIG_FILE.example.json`. Copy it to a protected location, replace the placeholders, and do not commit the copy.

Each public model ID is separate from its provider wire deployment. GitHub Copilot routes use the signed-in Copilot account. Microsoft Foundry routes support the Responses and Anthropic Messages wires and may use a scoped Azure CLI bearer token.

Kare does not run `az` for every model call. The Copilot SDK can request a token before a provider request; Kare reuses the in-memory token for its remaining lifetime and invokes `az account get-access-token` only on a cold cache or when the token is within its two-minute refresh window.

Keep the configuration file, Azure CLI state, endpoints, deployments, and credentials outside the repository with restrictive permissions.

Refresh the catalog from the account, not from the public model list alone. Use `az cognitiveservices account list-models` to find model definitions and `az cognitiveservices account deployment list` to find routes that can be tested immediately. Keep new routes explicit and out of automatic selection until streaming, cancellation, usage, tool behavior, latency, and cost have been measured. Availability is not proof that a model is cheaper.

## OpenAI-compatible endpoint

When Kare is loopback-only on the device, open the protected SSH tunnel with `build/tunnel.sh`. The host launcher at `~/.config/kare/copilot-byok.sh` exports the documented Copilot CLI OpenAI-compatible provider variables and starts Copilot without changing the host's normal Copilot or Lerna settings.

```bash
build/tunnel.sh
~/.config/kare/copilot-byok.sh
```

The launcher reads `~/.config/kare/device.env`, which must remain mode `600`. Kare must support streaming and tool calls for full Copilot CLI compatibility. The current local GenieX route is suitable for basic text validation, but structured local tool calls remain an open limitation.

## Device development

Use framework-dependent JIT builds for normal development on the VENTUNO Q:

```bash
~/.local/bin/kare-sync
cd ~/Kare
build/device-publish.sh --jit --test
```

The device service must have `DOTNET_ROOT` set to the project-local .NET 11 installation, normally `~/Kare/.dotnet`. The board's global .NET 10 runtime cannot run Kare's `net11.0` build.

Native AOT remains supported, but it is a release validation path rather than the normal edit and test loop:

```bash
build/device-publish.sh --aot --test
```

JIT and AOT modes deploy to separate versioned release directories and update the same `~/kare/service/current` symlink atomically.

## Inference and routing strategy

Prefer ONNX Runtime GenAI where it's model and execution-provider support are confirmed on the device. Use Microsoft.Extensions.AI and `IChatClient` as the service abstraction. Keep provider and native runtime details behind adapters registered through dependency injection.

Validate Qualcomm QNN or QAIRT support before claiming Hexagon NPU acceleration. Treat GenieX as a separate Qualcomm runtime option. Do not mix it's model format, licensing, or process model into the ONNX Runtime path without a measured reason.

Start with a small coding model and a short context window. Add larger models only after latency, memory, thermals, and quality are measured.

Use Microsoft Phi-4-mini INT4 ONNX as the first CPU reference. Candidate follow-ups include Qwen2.5-Coder 0.5B or 1.5B conversions, Qwen3 Qualcomm artifacts, SmolLM3, and Granite. The final choice must come from device benchmarks.

Do not assume a community ONNX file built for Transformers.js is compatible with ONNX Runtime GenAI. Do not start with a 7B model. Include KV cache, context, operating system, service, and concurrency in the memory budget.

Cache only with an explicit privacy policy, bounded size, versioned keys, and invalidation rules. Do not silently change a request from local inference to a billable cloud call. Record the route and require a configured policy.

## .NET and native code

Use .NET 11 and Native AOT only after the native inference dependency loading story is proven.

Development happens on x86_64 Linux and deployment targets `linux-arm64`. Prefer a pinned Ubuntu 24.04 ARM64 build environment until cross-publishing with the native inference libraries is reproducible.

Keep the managed service separate from native inference adapters. Preserve a clear CPU fallback when QNN, QAIRT, or other accelerator libraries are missing.

Keep runtime identifiers, native library names, model paths, and provider settings explicit. Use compile-time JSON metadata and avoid reflection-based discovery so Native AOT remains viable.

Bound prompt size, output size, queue depth, concurrent inference, and cache memory. Do not add broad exception catches or silent fallback behavior. Log the route and the reason for a fallback without logging secrets or prompt contents.

## Validation and benchmarking

Every performance claim needs a reproducible benchmark on the VENTUNO Q. Measure first-token latency, steady-state tokens per second, total latency, memory, temperature, power if available, cache hit rate, cloud-call reduction, and answer quality on representative coding tasks.

For pull requests, squash commits. Do not add co-authors or co-committers.

## Security

Never commit or print host details, device addresses, usernames, passwords, SSH keys, GitHub tokens, Azure tokens, Lerna auth files, model-provider keys, or copied Copilot settings.

Keep device configuration outside the repository. Use environment variables, protected files, or a secret store. Treat prompts, source code, generated patches, and cache entries as private network data.

Bind the service to the local network only after authentication, request limits, and an explicit allow-list are designed. Do not expose it to the public internet.

## Copilot integration notes

The GitHub Copilot SDK communicates with the Copilot CLI server over JSON-RPC and requires a Copilot subscription unless BYOK is used. HydraFusion is an experimental Copilot orchestration feature and can change.

Lerna intercepts supported HydraFusion model calls and can route mapped calls to Microsoft Foundry. It cannot add model IDs to HydraFusion. Keep Lerna settings and auth outside the repository.

The documented route for sending all Copilot CLI model calls through Kare is Copilot CLI BYOK pointed at Kare's OpenAI-compatible endpoint. That route requires streaming and tool calling.

BYOK and native HydraFusion are separate paths. Do not claim that a Copilot CLI BYOK session retains HydraFusion. Keep a native HydraFusion plus Lerna profile until a tested experimental interceptor proves a combined route.

GitHub's documented CLI extensions add tools and slash commands. Before adding an interceptor, confirm the installed extension API, lifecycle, permissions, cancellation behavior, streaming behavior, and whether it can replace a model request before it leaves the process.
