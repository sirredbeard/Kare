# Kare Copilot instructions

Read `README.md` and `plan.md` before changing the project.

Kare is planned as a .NET 11 service for an Arduino VENTUNO Q running Ubuntu 24.04.5 on aarch64. The service is intended to provide a fast local SLM for coding work, cache useful results, share skills and code context, and route selected requests to GitHub Copilot and Microsoft Foundry.

The project is in the research and planning stage. Do not start production coding until the gates in `plan.md` have been reviewed and the target runtime path has been measured on the device.

Keep documentation simple, direct, and specific. Do not use em dashes. State unknowns as unknowns rather than smoothing them over.

## Hardware and deployment

- Target: Arduino VENTUNO Q with Qualcomm Dragonwing IQ8 / QCS8275.
- Device OS: Ubuntu 24.04.5 LTS.
- Runtime target: `linux-arm64`.
- The device has 16 GB LPDDR5 memory, 64 GB eMMC, an M.2 NVMe slot, a Qualcomm Hexagon NPU, and an Adreno GPU.
- The device currently reports eight aarch64 CPUs across Cortex-A55 and Cortex-A78C clusters.
- Prefer NVMe for models, indexes, logs, and cache data when the expansion is available.
- The service must remain useful when the NPU runtime is absent. CPU inference is the first fallback, not an error path.

## Security

Never commit or print host details, device addresses, usernames, passwords, SSH keys, GitHub tokens, Azure tokens, Lerna auth files, model-provider keys, or copied Copilot settings.

Keep device configuration outside the repository. Use environment variables, protected files, or a secret store. Treat prompts, source code, generated patches, and cache entries as private network data.

Bind the service to the local network only after authentication, request limits, and an explicit allow-list are designed. Do not expose it to the public internet.

## Inference and routing

- Prefer ONNX Runtime GenAI where its model and execution-provider support are confirmed on the device.
- Use Microsoft.Extensions.AI and `IChatClient` as the service abstraction. Keep provider and native runtime details behind adapters registered through dependency injection.
- Validate Qualcomm QNN or QAIRT support before claiming Hexagon NPU acceleration.
- Treat GenieX as a separate Qualcomm runtime option. Do not mix its model format, licensing, or process model into the ONNX Runtime path without a measured reason.
- Start with a small coding model and a short context window. Add larger models only after latency, memory, thermals, and quality are measured.
- Use Microsoft Phi-4-mini INT4 ONNX as the first CPU reference. Candidate follow-ups include Qwen2.5-Coder 0.5B or 1.5B conversions, Qwen3 Qualcomm artifacts, SmolLM3, and Granite. The final choice must come from device benchmarks.
- Do not assume a community ONNX file built for Transformers.js is compatible with ONNX Runtime GenAI.
- Do not start with a 7B model. Include KV cache, context, operating system, service, and concurrency in the memory budget.
- Cache only with an explicit privacy policy, bounded size, versioned keys, and invalidation rules.
- Do not silently change a request from local inference to a billable cloud call. Record the route and require a configured policy.

## Copilot integration

The GitHub Copilot SDK communicates with the Copilot CLI server over JSON-RPC and requires a Copilot subscription unless BYOK is used. HydraFusion is an experimental Copilot orchestration feature and can change.

Lerna intercepts supported HydraFusion model calls and can route mapped calls to Microsoft Foundry. It cannot add model IDs to HydraFusion. Keep Lerna settings and auth outside the repository.

The documented route for sending all Copilot CLI model calls through Kare is Copilot CLI BYOK pointed at Kare's OpenAI-compatible endpoint. That route requires streaming and tool calling.

BYOK and native HydraFusion are separate paths. Do not claim that a Copilot CLI BYOK session retains HydraFusion. Keep a native HydraFusion plus Lerna profile until a tested experimental interceptor proves a combined route.

GitHub's documented CLI extensions add tools and slash commands. Before adding an interceptor, confirm the installed extension API, lifecycle, permissions, cancellation behavior, streaming behavior, and whether it can replace a model request before it leaves the process.

## .NET and native code

- Use .NET 11 and Native AOT only after the native inference dependency loading story is proven.
- Development happens on x86_64 Linux and deployment targets `linux-arm64`. Prefer a pinned Ubuntu 24.04 ARM64 build environment until cross-publishing with the native inference libraries is reproducible.
- Keep the managed service separate from native inference adapters.
- Preserve a clear CPU fallback when QNN, QAIRT, or other accelerator libraries are missing.
- Keep runtime identifiers, native library names, model paths, and provider settings explicit.
- Use compile-time JSON metadata and avoid reflection-based discovery so Native AOT remains viable.
- Bound prompt size, output size, queue depth, concurrent inference, and cache memory.
- Do not add broad exception catches or silent fallback behavior. Log the route and the reason for a fallback without logging secrets or prompt contents.

## Validation

Every performance claim needs a reproducible benchmark on the VENTUNO Q. Measure first-token latency, steady-state tokens per second, total latency, memory, temperature, power if available, cache hit rate, cloud-call reduction, and answer quality on representative coding tasks.

For pull requests, squash commits. Do not add co-authors or co-committers.
