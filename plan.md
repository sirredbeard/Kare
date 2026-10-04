# Kare plan

Kare should be a fast authenticated OpenAI-compatible conduit for coding assistance. Qwen through GenieX is the bounded local answer-or-route gate for cache assistance, authoritative context, skill maintenance, and cloud selection. Kare owns an ordered cascade across local Qwen, GitHub Copilot, and Microsoft Foundry routes. The design is inspired by the public HydraFusion orchestration patterns and Lerna's provider mapping, but it does not claim to run GitHub's native HydraFusion implementation or Lerna itself.

This document is a plan, not a claim that the design has been proven. The service should not be coded until the device runtime, model quality, Copilot integration point, and cloud accounting have been tested.

## Contents

- [What we know](#what-we-know)
- [What is still unknown](#what-is-still-unknown)
- [Technology choices](#technology-choices)
- [Proposed system](#proposed-system)
- [Model and runtime plan](#model-and-runtime-plan)
- [Performance and deployment plan](#performance-and-deployment-plan)
- [Copilot and cloud routing](#copilot-and-cloud-routing)
- [Caching and shared knowledge](#caching-and-shared-knowledge)
- [Operations dashboard](#operations-dashboard)
- [Persistence and context memory](#persistence-and-context-memory)
- [Security](#security)
- [Performance targets](#performance-targets)
- [Build stages](#build-stages)
- [Risks](#risks)
- [Sources](#sources)

## What we know

### VENTUNO Q hardware

Arduino describes the VENTUNO Q as an edge AI board built around the Qualcomm Dragonwing IQ8 / QCS8275 platform. The published product information lists:

- Octa-core Kryo Gen 6 CPU, up to 2.36 GHz
- Qualcomm Hexagon Tensor AI Processor, up to 40 dense TOPS
- Adreno 623 GPU
- Qualcomm Spectra 690 ISP
- 16 GB LPDDR5 shared memory
- 64 GB industrial eMMC
- M.2 NVMe expansion over PCIe Gen4
- Ubuntu Linux on the main processor
- STM32H5F5 Cortex-M33 microcontroller running Zephyr for real-time control
- 2.5 Gb Ethernet, Wi-Fi 6, Bluetooth 5.3, USB 3, HDMI, camera inputs, CAN-FD, and Arduino headers

The actual device was checked over SSH during this planning session. It reported:

- Ubuntu 24.04.5 LTS
- Linux `6.8.0-1084-qcom`
- `aarch64`
- Eight online CPUs across Cortex-A55 and Cortex-A78C clusters
- 14 GiB available system memory
- No swap
- 59.3 GiB eMMC block device, with the root filesystem on a 55.9 GiB partition
- Qualcomm FastRPC device nodes for CDSP, GDSP, and ADSP
- 34 GiB free on the root filesystem during the 2026-10-01 inventory

The published product naming and the Linux CPU report do not line up perfectly. Use the board's runtime facts for tuning and the product documentation for the platform feature set.

### ONNX Runtime GenAI

[ONNX Runtime GenAI](https://github.com/microsoft/onnxruntime-genai) provides the generation loop around ONNX models, including tokenization, sampling, logits processing, KV cache handling, constrained decoding, and tool-call grammar support.

Its current support matrix lists:

- Linux and arm64
- C# and C/C++
- CPU and QNN execution providers
- Model families including Llama, Phi, Qwen, Gemma, Mistral, DeepSeek, Granite, SmolLM3, and others

The support matrix is not proof that every model and provider combination works on this board. The C# and native Linux ARM64 path must be tested on the target.

The official build documentation describes a source build with CMake and .NET for C# support. The current documentation still calls out .NET 6 as the C# prerequisite. Kare should target .NET 11, but the native binding and NuGet package compatibility must be checked rather than inferred.

Release `0.17.1` of `Microsoft.ML.OnnxRuntimeGenAI` contains `runtimes/linux-arm64/native/libonnxruntime-genai.so` and depends on `Microsoft.ML.OnnxRuntime` `1.30.0`. This gives Kare a packaged CPU path on .NET 11 through the package's .NET 8 and .NET Standard managed targets. It does not give Kare a packaged Linux ARM64 C# QNN path.

The separate `onnxruntime-qnn` releases publish Linux ARM64 inference wheels and native `.tgz` files. Version matching is strict. Release `2.6.0` uses QAIRT `2.50.40`; release `2.2.0` uses the board's QAIRT `2.46.0`. Neither publishes a Linux NuGet package. Kare can register the plugin through a small native bridge and does not need to rebuild ONNX Runtime for the first QNN proof.

### Qualcomm acceleration

The ONNX Runtime QNN Execution Provider uses Qualcomm AI Engine Direct, formerly called QNN SDK, and can target the QNN HTP backend for NPU execution. The main ONNX Runtime QNN page still describes Android and Windows as the supported platforms and lists older tested SoCs. The separate official `onnxruntime-qnn` project now publishes Linux ARM64 inference packages.

Qualcomm's current GenieX platform table explicitly maps Dragonwing IQ-8275, `QCS8275`, to `qualcomm-qcs8275`, and its device detection code maps the VENTUNO Q to that chipset. Qualcomm AI Hub model assets also include QCS8275-specific Genie bundles. This closes the broad SoC support question for GenieX and AI Hub artifacts. It does not prove that Kare's chosen ONNX Runtime GenAI graph will compile and run entirely on HTP.

Arduino and Qualcomm also document GenieX for local language and vision-language models on the VENTUNO Q. GenieX is a separate runtime from ONNX Runtime GenAI. GenieX `0.7.1` provides Linux ARM64 CLI, Python, C SDK, Docker, benchmark, and OpenAI-compatible server assets. It is still marked Developer Preview, so Kare should pin the version and keep it behind an adapter or process boundary.

## Research findings

These are the working findings after reviewing the public Qualcomm, Arduino, ONNX Runtime, GitHub Copilot, and Copilot CLI documentation available on 2026-10-01. They are not a replacement for device benchmarks. They are a gate memo to keep the project honest while we prepare the first runtime proof on the VENTUNO Q.

| Gate | Status | Decision |
| --- | --- | --- |
| Qualcomm runtime installation | Closed on the board | QAIRT 2.46.0 installs straight from apt on the stock image |
| QCS8275 support | Closed on the board | Hexagon V75, soc_id 675, validator unit test executed on the DSP |
| Full HTP model execution | TinyLlama failed, QNN-specific model open | Plugin registration and V75 execution work. Stock TinyLlama offloads only Shape/Gather/Cast and is slower than CPU |
| .NET 11 ARM64 CPU package | Closed on the board | `Microsoft.ML.OnnxRuntimeGenAI` `0.17.1` generates tokens on the VENTUNO Q from a Native AOT binary |
| .NET 11 ARM64 QNN package | Closed through native plugin | Official Linux ARM64 QNN plugin loads through Kare's C++ registration bridge |
| GenieX service API | Available, Developer Preview | Use the C SDK or OpenAI-compatible server behind an adapter |
| Copilot CLI transparent interception | Not supported by extensions | Use BYOK for provider redirection |
| Copilot SDK cloud escalation | Feasible, not transparent | Kare owns route and session correlation |
| Cache safety | Policy design required | Cache deterministic artifacts first |
| Context, thermals, and power | Context is the active blocker | Sustained CPU generation peaked at 43.3 C. The measured QAIRT bundle is fixed at 4096 tokens, below Copilot CLI's roughly 4.8k-token static prompt |

1. Qualcomm runtime libraries on Ubuntu 24.04.5: yes, packages now exist for the required operating system and architecture. GenieX `0.7.1` publishes Linux ARM64 CLI, SDK, benchmark, and Python assets, and bundles compatible QAIRT libraries by default. `onnxruntime-qnn` `2.6.0` publishes Linux ARM64 inference artifacts for QAIRT `2.50.40`. Installation is no longer the research question. The device proof must verify library loading, FastRPC access, model loading, and inference on the VENTUNO Q image.

2. QNN or QAIRT support for QCS8275 and HTP: yes. GenieX explicitly lists Dragonwing IQ-8275, `QCS8275`, as `qualcomm-qcs8275`, Qualcomm AI Hub publishes per-chipset assets for that target, and the board validator executed on Hexagon V75. The installed headers identify the matching Monaco target as QCS8300 `soc_model=82`, `htp_arch=75`, however live inference works without forcing those values when the plugin and QAIRT versions match.

3. ONNX model compile path for HTP: failed for the stock Arm TinyLlama CPU graph. The matched QNN `2.2.0`, QAIRT `2.46.0`, and ORT `1.24.4` stack prepared and executed a V75 graph, however detailed profiling showed only the attention-mask Shape/Gather/Cast subgraph on HTP. TTFT regressed from 1.696 seconds on CPU to 4.683 seconds and decode dropped from about 23 to 17.31 tokens per second. Stop forcing this graph. Keep Microsoft Phi-4-mini INT4 as the CPU reference. Use Qwen3 1.7B W4A16 as the first Qualcomm accelerator reference, followed by Qwen3 4B W4A16, because Qualcomm publishes QCS8275-specific Genie assets for both. A QNN model passes only when profiling proves the useful transformer graph runs on HTP without unacceptable fallback.

4. ONNX Runtime GenAI .NET 11 ARM64 support: closed for CPU, and this is now measured rather than inferred. `Microsoft.ML.OnnxRuntimeGenAI` `0.17.1` restores on `net11.0`, cross-publishes self-contained to `linux-arm64` from an x86_64 workstation, and ships aarch64 `libonnxruntime-genai.so` and `libonnxruntime.so`. The published Kare device probe was executed inside an aarch64 `ubuntu:24.04` container that reports Ubuntu 24.04.5 LTS, the same release as the board. It loaded the native library and reached a managed model-path error, which is the expected result with no model present. Kare does not need to build the CPU runtime.

   Three details change the design. First, the package already ships `Microsoft.ML.OnnxRuntimeGenAI.OnnxRuntimeGenAIChatClient`, which implements `IChatClient`, so Kare does not hand-write a tokenizer and generator loop. It implements `IChatClient.GetService` explicitly, so the adapter has to cast. Second, `Config` exposes `AppendProvider`, `ClearProviders`, `SetProviderOption`, and `Overlay`, so execution provider selection is reachable from C# without touching JSON on disk. Third, neither `Microsoft.ML.OnnxRuntimeGenAI` nor `Microsoft.ML.OnnxRuntime` ships `libonnxruntime_providers_qnn.so`, however the official `onnxruntime-qnn` Linux ARM64 package does. Kare registers it through `OgaRegisterExecutionProviderLibrary` behind a small C++ exception boundary. The remaining risk is model compatibility, not provider loading.

   Native AOT is the other half of this gate, and it is now closed too. ILCompiler produces correct aarch64 objects when cross-publishing, but the link step fails on an x86_64 host with `ld.bfd: unrecognised emulation mode: aarch64linux`. Linking inside an aarch64 Ubuntu 24.04 image removes the problem, which is what `build/Containerfile.arm64` and `build/publish-arm64.sh` do. The result is a 4.1 MB stripped aarch64 PIE executable that starts on Ubuntu 24.04.5 aarch64 and loads the ONNX Runtime GenAI native library. This confirms the pinned ARM64 build environment is a requirement, not a preference.

5. GenieX service API: yes, with a stability caveat. GenieX exposes one C SDK through CLI, Python, Java or Kotlin, Docker, and an OpenAI-compatible server. `geniex serve` listens on `127.0.0.1:18181/v1` by default. This is suitable for a long-running local process and is the shortest NPU service proof. GenieX is marked Developer Preview, so Kare should not bind its domain model directly to that API. Pin the GenieX release, run it as a supervised local process or use the C SDK in a narrow native adapter, and keep the CPU adapter independent.

6. Copilot CLI extension lifecycle and interception: extensions add tools and slash commands. They do not expose a model transport replacement hook. CLI and plugin hooks can inspect or modify prompts and tool calls, and the Copilot SDK adds `userPromptTransformed` before the model send for SDK-owned sessions. None of these documented hooks return an alternate streamed model response or replace the provider connection for an existing TUI request. The supported redirection point is BYOK through `COPILOT_PROVIDER_BASE_URL`. Use hooks for policy, redaction, cache hints, and accounting, not as the foundation for model interception.

7. GitHub Copilot SDK and session identity: reframe this gate around cloud escalation. The local SLM and Kare own the local session, context, cache, route decision, and correlation ID. When a task crosses the cloud threshold, Kare opens or resumes a Copilot SDK session and maps that session to the local request. The SDK exposes streaming, permission handlers, tool events, hooks, cancellation tokens, explicit session IDs, resume, and automatic context compaction. It does not make a BYOK Copilot TUI session and a separate SDK session the same session. Test each capability at the adapter boundary and store the mapping rather than promising transparent identity.

8. Cache safety: the project should cache only deterministic, policy-safe calls. The right cache keys include model and provider, system instructions, normalized user request, repository identity and revision, relevant file hashes, permission policy, and tokenizer/model version. A response should not be cached only by raw prompt text. For code generation, the cache should be conservative: default to short-lived cache records, repository-hash validation, and stale-context warnings or local revalidation before returning a patch. Do not assume a code answer is safe to reuse just because the prompt text is similar.

9. Available context on the board: the first QAIRT result is closed. The Qwen3 1.7B bundle is compiled for 4096 tokens, and Qualcomm documents that a QAIRT bundle's context cannot be raised at runtime. Sliding window eviction does not help when Copilot CLI's initial static prompt is already larger than the bundle. No larger-context QCS8275 QAIRT bundle was verified in the public catalogue on 2026-10-03. GenieX's GGUF path can raise `--nctx` up to the model's trained maximum, so the next experiment is a small supported GGUF model at 8192 tokens through the llama.cpp HTP path. It must pass the same tool-call, quality, latency, memory, and thermal gates before replacing the QAIRT default.

10. Thermal and power behavior: also unknown until measured. The board has a large heat sink and a power budget that can likely be used aggressively, but the public docs do not give a measured sustained-generation profile for coding models. The first benchmark should run the model for a sustained interval, record first-token latency, steady-state tokens per second, total latency, temperature, throttling, and system power if available, and only then set the real runtime policy. The board should be configured to maximize useful local work, but not by blindly pushing the board until it throttles or fails.

The main conclusion is narrower now: Qualcomm acceleration on QCS8275 works, but the measured QAIRT model cannot host Copilot CLI because its compiled context is too small. The next local-client proof is a small GenieX GGUF model with an 8192-token context. The ONNX CPU path remains useful for plain local prompts, but it must reject tool-bearing requests because the current .NET client ignores tools.

## Technology choices

Kare spans several .NET AI branches, each with a different job:

- ONNX Runtime GenAI for local model inference.
- Microsoft.Extensions.AI as the service-level `IChatClient` abstraction.
- GitHub Copilot SDK for sessions that must use the Copilot agent runtime.
- Microsoft.Extensions.VectorData abstractions for repository and skill retrieval.
- A small ASP.NET Core API for the authenticated local-network gateway.

Microsoft Agent Framework should not be in the first build. Copilot already owns the agent loop for Copilot sessions, and Kare should not add a second hand-written loop around it. Add Agent Framework later only if Kare gains an independent, bounded workflow that requires tool orchestration.

Provider SDKs and native inference libraries belong behind adapters registered through dependency injection and configuration. Business logic should depend on `IChatClient`, not a mix of raw provider HTTP calls.

## Proposed system

Kare should be split into small boundaries:

```text
Copilot CLI or Kare-aware client
        |
        | BYOK provider endpoint or supported SDK integration
        v
Kare gateway
        |
        +-- request authentication and policy
        +-- prompt normalization and context fingerprinting
        +-- cache lookup
        +-- local model adapter
        +-- cloud model adapter
        +-- response and usage accounting
        v
local SLM, GitHub Copilot SDK, or Microsoft Foundry
```

Native HydraFusion through Lerna remains useful only as a comparison and fallback profile. Kare's supported architecture implements its own bounded routing and provider mapping directly, without requiring a Copilot CLI or Lerna process behind the gateway.

### Gateway

The gateway should expose a small authenticated local-network API. It should support:

- Streaming text responses
- Cancellation and request deadlines
- Request IDs and route metadata
- Bounded request and response sizes
- Model and route selection policy
- Health and readiness endpoints without prompt data
- Structured metrics without prompt or source-code contents

The first external API should provide the OpenAI Chat Completions surface required by Copilot CLI BYOK. GitHub documents BYOK support for OpenAI-compatible endpoints, including local endpoints, with streaming and tool calling required and a 128K context window recommended.

Kare can advertise only capabilities it can actually provide. A small local model on 16 GB cannot be assumed to handle a 128K agentic coding session. Requests beyond the tested local context, tool-call quality, or memory budget must be rejected or explicitly escalated by policy.

### Local model adapter

The first adapter should load one fixed, quantized model and provide one generation path. It should report whether execution used CPU, GPU, NPU, or a mixed fallback.

The adapter should not expose arbitrary native library loading to network clients. Model selection belongs to a local policy file or service configuration.

Use two adapters in the runtime proof:

- `OnnxRuntimeGenAiChatClient` for the packaged Linux ARM64 CPU baseline.
- `GenieXChatClient` for the QCS8275 NPU path, initially through the loopback OpenAI-compatible server and later through the C SDK only if measurements justify the added native integration.

Both adapters should provide the same `IChatClient` boundary and emit route, model, context, prompt-processing, decode, and fallback metrics.

### Cloud adapter

The cloud adapter uses one provider-neutral model catalog. GitHub Copilot entries use the signed-in account and leave SDK provider configuration unset. Microsoft Foundry entries use an administrator-configured HTTPS resource endpoint, a separate well-known model ID and wire deployment name, either the Responses or Anthropic Messages wire, and a scoped Entra bearer token. Kare acquires Entra tokens outside the repository and never persists access tokens in its model catalog.

Lerna is a reference implementation for provider mapping, wire adaptation, and Cognitive Services token scope. It is not a Kare runtime dependency. Protected Lerna mappings may be transformed into Kare's external catalog for local testing, but Lerna settings, auth state, resource details, deployments, and endpoints must not enter the repository.

### Cloud model inventory findings

The signed-in GitHub Copilot account currently exposes the public model families documented by GitHub, including GPT-5.6 Luna, Sol, and Terra, Claude Haiku 4.5, Claude Sonnet 5, Claude Opus 5, Gemini 3.8 Flash, MAI-Code-1.1-Flash, Kimi K3, and Grok 4.7. The exact list remains plan- and client-dependent, so public documentation is not enough to enable a route. Validate the installed Copilot CLI and account before changing automatic selection.

The protected Azure account currently exposes 163 Foundry model definitions and five active deployments. The active deployments are `gpt-5.6-terra`, `gpt-5.6-sol`, `gpt-5.6-luna`, `claude-sonnet-4-6`, and `claude-opus-5`. `gpt-5.6-sol` and `claude-sonnet-4-6` are now catalog candidates. The available model list includes cheaper-looking small models such as `gpt-5.4-mini`, `gpt-5.4-nano`, `Phi-4-mini-instruct`, and `qwen3-32b`, but pricing, quota, deployment capacity, tool support, and latency are not yet verified. Do not call a model cheaper until Azure pricing or measured account cost confirms it.

The protected catalog includes explicit routes for the deployed `gpt-5.6-sol` and `claude-sonnet-4-6` models. Kare exposes one public `kare` model and orders eligible catalog entries by configured priority. New routes must pass streaming, cancellation, usage, tool, and failure tests before they receive cascade priority.

## Model and runtime plan

### First local candidates

The first benchmark set should be small and limited:

| Candidate | Runtime evidence | Fit on 16 GB | Main question |
| --- | --- | --- | --- |
| Microsoft Phi-4-mini instruct, 3.8B | Microsoft publishes an MIT-licensed INT4 ONNX Runtime GenAI CPU/mobile layout with a 128K advertised context | Strong first CPU baseline. INT4 weights are roughly 2 GB before runtime, KV cache, and context overhead | Does it provide acceptable coding quality at a measured 4K to 16K working context? |
| Qwen3 1.7B W4A16 Qualcomm artifact | Qualcomm publishes a QCS8275-specific Genie bundle | Best first NPU candidate because it is small and explicitly compiled for this chipset | What are prompt and decode rates at 4K, 8K, and 16K, and is coding quality useful? |
| Qwen3 4B W4A16 Qualcomm artifact | Qualcomm publishes a QCS8275-specific Genie bundle | Likely to fit, but KV cache and service headroom must be measured | Does the quality lift justify lower speed and memory headroom? |
| Qwen2.5-Coder 0.5B or 1.5B | Apache-2.0 code models exist, however common community ONNX files target Transformers.js or another vendor NPU | Very likely to fit and respond quickly | Can Kare produce and validate a GenAI-compatible conversion without relying on an unverified community QNN artifact? |
| SmolLM3 3B | ONNX Runtime GenAI lists the architecture as supported | Likely to fit when quantized | Is it useful as a small general fallback, or does it lose too much coding quality? |
| Granite 3.x small variants | ONNX Runtime GenAI lists Granite architectures as supported | Model-dependent | Is there a maintained GenAI conversion and license-compatible artifact worth testing? |

The final model is a benchmark result, not a preference. The model license, redistribution terms, tokenizer, context length, quantization method, and runtime support must be recorded.

Do not benchmark 7B or larger models first. They may fit at 4-bit weight precision, however model weights are not the complete memory budget. Ubuntu, the service, native runtimes, KV cache, prompt context, and concurrent requests all share the same 14 GiB visible memory.

Qualcomm's published Q4_0 GenieX llama.cpp results for the VENTUNO Q are a warning against treating NPU as automatically faster. Qwen3 1.7B at 512 tokens reports about 11.03 decode tokens per second on CPU and 3.79 on NPU, while NPU prefill is much faster. At 4096 tokens, CPU and NPU decode are both about 3 tokens per second, with NPU again far ahead on prefill. Qwen3 4B is slower. The QCS8275 W4A16 QAIRT bundles are the more interesting NPU path, however the public `perf.yaml` does not provide equivalent VENTUNO Q results for that runtime. Measure both prompt processing and decode.

### Runtime order

1. Install and measure packaged ONNX Runtime GenAI CPU on `linux-arm64`.
2. Install GenieX and measure the QCS8275 Qwen3 1.7B W4A16 bundle.
3. Measure Qwen3 4B W4A16 if the 1.7B path leaves enough memory and thermal headroom.
4. Build the ONNX Runtime GenAI QNN path only if it can reuse a model worth comparing or provides a service advantage over GenieX.
5. Compare CPU, QNN HTP, GenieX QAIRT, and GenieX llama.cpp HTP where available.
6. Keep the fastest reliable route as the default and retain CPU as the fallback.

### Native AOT

Native AOT is a deployment goal, not the first experiment. First prove:

- Managed API behavior
- Native library discovery
- Model loading
- Streaming generation
- Cancellation
- Provider selection
- Diagnostics

Then publish a self-contained `linux-arm64` AOT binary and validate it on a clean Ubuntu image. Native AOT may require explicit native library packaging and linker configuration. It must not hide runtime provider failures.

### Model storage

The initial 64 GB eMMC is enough for a small benchmark set, but model files, caches, logs, and system updates compete for space. Add M.2 NVMe before keeping several model variants or a large code index.

The board does not take 2280 drives. It wants a 2230 M-key NVMe module. The exact, trusted part is more important than the marketing label on the Amazon listing. For a production-minded cache and PostgreSQL path, prefer a named drive with a printed part number, a real seller warranty, and a clear throughput and endurance rating.

Use checksummed, versioned model directories. Do not download models into the Git repository.

## Performance and deployment plan

### Build path

Development happens on an x86_64 Linux host, while the deployment target is Ubuntu 24.04 ARM64. Native AOT supports Linux cross-architecture builds when the ARM64 compiler, linker, sysroot, and native dependencies are available, however native inference libraries make that path more brittle.

Use this order:

1. Build and test the managed service on x86_64.
2. Build the inference adapter natively on the VENTUNO Q.
3. Prove a framework-dependent ARM64 build on the device.
4. Prove a self-contained ARM64 build.
5. Publish Native AOT on the device or in a pinned Ubuntu 24.04 ARM64 build environment.
6. Attempt x86_64 to ARM64 cross-publishing only after the native dependency set is reproducible.

The board-native path is faster than emulated ARM64, but Native AOT code generation still makes routine iterations unnecessarily slow. Use the framework-dependent ARM64 JIT path in `build/device-publish.sh --jit` for active development. It runs with the project-local .NET 11 runtime because the board's global runtime is .NET 10. Use `build/device-publish.sh --aot` for release candidates and periodic AOT compatibility checks. Keep `build/publish-arm64.sh` as the reproducible container fallback.

The release artifact should target `linux-arm64`, carry no .NET runtime requirement, and keep models and Qualcomm libraries outside the main executable. Do not call the deployment a single binary when separate provider libraries are still required.

The host tuning repository must be applied after it is updated. On 2026-10-03 the checked-in service unit correctly ordered itself after `sysfsutils.service`, but the installed copy was older and ran first. `sysfsutils` then reset two of the three CPU policies to `schedutil`. Reapplying the current optimization script fixed the ordering, set all policies to `performance`, masked sleep targets, and added a post-apply governor check. Benchmark preflight must verify live policy values, not only that the service reports success.

### Managed service

The service should use the ASP.NET Core Native AOT path with:

- Compile-time JSON serialization metadata
- No runtime assembly scanning
- No reflection-based plugin discovery
- Bounded channels for queued requests
- One local generation at a time for the first release
- Server-sent event streaming for the OpenAI-compatible response
- Pooled buffers for token and network data where measurement supports it
- Explicit request cancellation passed into native generation
- Startup model loading with a readiness gate
- Fixed upper bounds for prompt tokens, output tokens, concurrent requests, and cache memory

Native AOT should be measured against a normal self-contained build. Keep AOT only if it improves startup, memory, or deployment without making native inference failures harder to diagnose.

As of 2026-10-01, .NET 11 is at RC 1 and general availability is expected in November 2026. Use the RC only for research. Pin the GA SDK before production work.

Useful .NET 11 features for Kare include runtime-native async, Native AOT interface dispatch improvements, Arm64 FP16 instructions for `Half`, no-copy memory stream wrappers, expanded source-generated `System.Text.Json`, process signaling and exit status APIs for the GenieX sidecar, in-process crash reports, async validation, and built-in `MemoryCache` OpenTelemetry metrics. Use them where measurements show a benefit, not just because they are new.

EF Core 11 has better split-query SQL, removes unnecessary joins and ordering, strips no-op casts that can block indexes, and adds more query translations. Kare should still use projections, `AsNoTracking`, bounded result sets, and explicit indexes. Framework improvements do not rescue a bad query.

### Runtime tuning

The first tuning pass should test:

- CPU affinity across the performance and efficiency clusters
- ONNX Runtime intra-op and inter-op thread counts
- Prompt processing separately from token generation
- Memory-mapped model files
- KV cache size at 4K, 8K, and 16K working contexts
- Thermal throttling over at least 30 minutes
- Cold boot, cold model load, warm model load, and steady-state requests
- QNN `burst`, `high_performance`, and `sustained_high_performance` modes
- Maximum safe board power profile with the production heat sink and power supply
- Whether maximum clocks improve completed tokens per joule or only create earlier throttling

Do not enable swap as a performance fix. Reject work before memory pressure turns a coding request into storage benchmarking.

## Copilot and cloud routing

### Supported CLI-wide route

The documented route for sending Copilot CLI model traffic through Kare is Copilot CLI BYOK:

```text
Copilot CLI
  -> COPILOT_PROVIDER_BASE_URL points to Kare
  -> Kare OpenAI-compatible API
  -> local model or explicit cloud escalation
```

This is the first integration to prove. Kare must support streaming and tool calling, and should be tested with Copilot CLI offline mode before cloud escalation is enabled.

BYOK and native HydraFusion are different model paths. A Copilot CLI process pointed at Kare as its provider should not be expected to run GitHub's HydraFusion orchestration at the same time.

### GitHub Copilot SDK

The [GitHub Copilot SDK](https://github.com/github/copilot-sdk) exposes the same agent runtime used by Copilot CLI through SDKs for .NET and other languages. The SDK communicates with a Copilot CLI server over JSON-RPC and supports GitHub authentication, BYOK, custom agents, skills, and tools.

The SDK is not automatically a transparent proxy for an existing Copilot TUI session. Kare needs a supported plugin or client integration that can send requests through Kare while preserving the TUI's session behavior.

The SDK route now uses `GitHub.Copilot.SDK` `1.0.16` behind a Kare cloud adapter. The adapter:

- starts the runtime in `CopilotClientMode.Empty` so ambient host tools, skills, Git operations, and shared sessions are unavailable
- uses GitHub `auto` with an explicit Auto V2 tier by default
- applies a per-request timeout and the SDK's minimum 30-credit session ceiling
- forwards caller-owned tools as declaration-only tools and returns requested calls without executing them on the board
- can use GitHub Copilot authentication or direct Microsoft Foundry BYOK
- deletes the temporary SDK session after each request

A live GitHub Copilot `auto` request returned successfully. A live required-tool request also returned the requested `read_file` function call without executing it. In SDK `1.0.16`, declaration-only tools produced `tool.execution_start` rather than `external_tool.requested`, so Kare intercepts the former for tools it registered. The ARM64 package and device runtime still need validation.

Cloud text streaming now forwards `assistant.message_delta` events as `ChatResponseUpdate` values. A live probe received `STREAM` across three updates. Tool calls terminate the stream with `tool_calls`, and deleting the temporary session stops the SDK run after Kare returns caller-owned work. Cancellation and timeout still need device validation under an active ARM64 request.

GitHub's documented CLI extension API adds tools and slash commands. It does not document a general model-request replacement hook. Treat transparent interception through an extension as experimental work, not the base design.

CLI hooks and SDK hooks can modify the submitted or transformed prompt before the model sees it. This is useful for redaction, policy, route metadata, and cache fingerprints. A prompt hook is not a provider hook and cannot supply the model's streamed response.

### Kare-owned orchestration and provider mapping

[HydraFusion](https://github.blog/ai-and-ml/github-copilot/project-hydrafusion-frontier-quality-via-multi-model-orchestration/) publicly describes orchestration patterns such as single-model execution, cascade, and critique. GitHub's implementation is experimental and is not exposed as a stable Copilot SDK model or reusable routing library.

[Lerna](https://github.com/sirredbeard/Lerna) demonstrates explicit model-to-provider mapping for supported HydraFusion model IDs. Kare uses the same broad boundary, but performs its own administrator-configured mapping instead of loading Lerna or copied Lerna settings.

Kare's supported route is:

```text
OpenAI-compatible client
  -> Kare authentication, cache, bounds, and deterministic request scoring
  -> local Qwen/GenieX, a concrete GitHub Copilot model, or a configured Foundry deployment
```

Kare exposes one public wire model ID, `kare`. The bounded cascade first checks a safe cached cloud target, then asks local Qwen to return either an authoritative answer or one configured cloud target. The Qwen gate receives only compact request metadata and the latest user text, without caller tools, and disables GenieX extended thinking so the marker is returned within the bounded decision budget. Dashboard-managed authoritative sources and skills are injected into this local decision. Connected MCP names and advertised capabilities are advisory metadata only and are not tool execution.

If Qwen escalates or fails before answering, Kare calls the selected cloud target and advances through later configured targets only when a provider fails before returning output. Once a streaming provider emits output, Kare does not switch models because mixed-provider output would be unsafe. Tool-bearing requests consider only catalog entries with confirmed tool support.

This is HydraFusion-inspired routing, not GitHub HydraFusion. It is a bounded answer-or-route decision, not a second agent loop. Copilot or the calling client still owns tool execution, permissions, and repository mutation.

Kare should record:

- Local, Copilot, or Foundry route
- Model identifier
- Cache hit or miss
- Request and response token counts when available
- Latency per route
- Cancellation and error reason
- Estimated billable call avoided or made

Do not claim that a local cache reduces Copilot billing until the Copilot call is actually avoided and the result is verified against the provider's accounting.

Legacy route aliases remain configuration compatibility fields but are not advertised or used by the service runtime. Protected deployment configuration enables the cascade and defines the priority order. Repository defaults keep cloud routing disabled and loopback-only.

### Cost policy

Copilot Max includes 20,000 AI credits per month as of 2026-10-01. Credits reset at the start of each calendar month and do not roll over. Standard Copilot CLI and SDK calls consume AI credits. Local BYOK inference and Foundry BYOK use the configured provider instead of GitHub authentication, so Kare must account for those routes separately.

Use this order:

1. Return a validated local cache hit.
2. Use the local SLM for bounded work it can do well.
3. Use a lightweight Copilot model for cloud work that does not need a frontier model.
4. Use an expensive Copilot model for tasks where the expected quality lift is worth the credits.
5. Use Foundry when policy, model fit, remaining Visual Studio Azure credit, or Copilot credit pressure makes it the better cloud route.

The router should know the remaining monthly Copilot and Azure budgets, model token prices, current context size, and estimated task complexity. It should never spend simply to empty a monthly allowance, however unused allowances have no value after reset. Prefer high-value deferred work near the end of the month rather than wasting credits on repeated low-value prompts.

Visual Studio Enterprise Standard currently includes $150 of Azure Dev/Test credit each month, Professional includes $50, and MSDN Platforms includes $100. The credit stops usage at the cap when no payment method is attached and is for development and testing. Do not design a production dependency around it.

## Caching and shared knowledge

### Request cache

Use a versioned cache key built from:

- Model and provider
- System instructions
- Normalized user request
- Relevant repository identity and revision
- Selected file content hashes
- Tool and permission policy
- Model and tokenizer version

Do not cache a response only by user text. Code answers depend on repository state, instructions, tools, permissions, model versions, and prior turns.

Cache entries should have:

- Expiration
- Maximum size
- LRU or similar eviction
- Provenance
- Model and repository fingerprints
- A privacy classification
- An explicit invalidation path

Final answer caching should be off by default for agentic and tool-using requests. The first useful cache is the boring cache:

- Tokenized immutable prompt prefixes
- Model files and compiled accelerator artifacts
- Embeddings
- File and symbol summaries keyed by content hash
- Skill and instruction payloads keyed by version
- Validated command results with short expiration

The first response cache is implemented but disabled by default. It is memory-only, bounded by entry count, lifetime, and response size, and uses versioned SHA-256 keys. It accepts only non-streaming deterministic text requests with no tool declarations or tool history. It does not persist prompts or responses and disappears on restart.

This cache intentionally does not yet cache coding tool flows. Its keys do not include repository revision or file hashes, so enabling it should be limited to context-independent deterministic requests until repository fingerprinting is implemented.

### Code and skill cache

Share reusable material as indexed, content-addressed records:

- Repository instructions
- Skills and their versions
- File summaries
- Symbol and dependency summaries
- Tested command results
- Prior patches with validation status

Prefer metadata and hashes over storing complete source files by default. A local user should be able to delete all stored content.

Use `IEmbeddingGenerator`, cache embeddings, preserve source attribution, and query through Microsoft.Extensions.VectorData abstractions. Chunk code by symbols and semantic boundaries rather than fixed token counts. Every retrieval result needs a minimum relevance score.

### Quality gate

A cache hit should be returned directly only for deterministic, safe requests. For code generation, Kare should consider a local validation step, a stale-context warning, or a short local review before returning a cached patch.

The SLM can recommend cloud escalation, however cache and route safety must be enforced by code. Do not make prompt wording the only control between private source, a stale cache entry, and a billable cloud call.

## Operations dashboard

Kare should expose a small local operations dashboard from the service process. The dashboard is for observing and controlling metadata that Kare already owns. It must not display or persist prompt text, source code, generated responses, credentials, provider tokens, or protected configuration.

The first dashboard surface should show:

- recent route decisions, model IDs, backends, fallback status, success, billable status, first-token latency, total latency, output token counts, and decode rate
- queue depth, active local inference, approximate utilization, completed request count, and average first-token latency
- response-cache hashes, creation and last-access times, size, and a delete action
- registered routing decision URLs and whether each prefers local or cloud work
- local skills with their paths and modification times
- a visual request breakdown across configured local, Copilot, and Foundry model endpoints
- configured MCP servers, advertised capabilities, connection state, and last connection time
- recent route and fallback activity without prompt or response contents

Dashboard state should be bounded and process-local until PostgreSQL persistence is designed. The page and API remain behind Kare's explicit CIDR network allow-list but do not require a bearer token on the trusted LAN. The OpenAI-compatible API remains bearer-authenticated. Repository defaults must remain loopback-only and must not contain a development password, device subnet, or API key.

Kare now owns bounded registries for authoritative HTTPS source patterns, skills loaded from explicit absolute device paths or public HTTPS URLs, and Streamable HTTP MCP endpoints. Source patterns and remote skills are periodically refreshed with fixed page, byte, and injected-context limits. Enabled source and skill content is added only to local inference. MCP status comes from Kare's own initialize probes. Kare does not scan arbitrary home directories or copy Copilot settings to populate these registries.

## Persistence and context memory

Use PostgreSQL with `pgvector` on the VENTUNO Q. Use EF Core through Npgsql for relational data and `Pgvector.EntityFrameworkCore` for vector fields once the package versions support the selected EF Core major version.

PostgreSQL is the better fit than SQLite or a document database here:

- Concurrent request, cache, context, and accounting writes do not share SQLite's single-writer limit.
- JSONB stores provider-specific metadata without giving up relational constraints.
- PostgreSQL full-text search and `pgvector` support hybrid keyword and semantic retrieval.
- HNSW indexes are available when the corpus is large enough to justify them.
- The same schema can be restored into Azure Database for PostgreSQL Flexible Server, where the `vector` extension is supported.
- Npgsql and pgvector have direct .NET and EF Core support.

Do not use the database as a token cache or a dumping ground for complete repositories. Keep model files, compiled graphs, large immutable blobs, and disposable tokenized prefixes on NVMe. Store metadata, hashes, provenance, compacted context, embeddings, route accounting, validation results, and bounded cache records in PostgreSQL.

Use separate records for:

- Session and turn identity
- Raw local turn text, when retention policy allows it
- Immutable content-addressed source chunks
- Compacted session checkpoints
- Embeddings and embedding model version
- Cache keys, expiration, validation, and provenance
- Route decisions, token usage, latency, and estimated cloud cost
- Model, runtime, tokenizer, skill, and instruction versions

Backups should be boring and testable:

1. Run local PostgreSQL on NVMe with checksums, WAL limits, and bounded retention.
2. Create encrypted `pg_dump` archives on a schedule and after schema changes.
3. Upload the archives to a private Azure Storage container with lifecycle rules.
4. Test restore into Azure Database for PostgreSQL Flexible Server with the `vector` extension enabled.
5. Add logical replication to Azure only if a measured recovery-point requirement justifies the permanent cloud database and egress cost.

Azure Database for PostgreSQL's own point-in-time backups apply after data is restored or replicated into Azure. They do not back up the board automatically.

Target .NET 11, but pin the latest mutually compatible EF Core, Npgsql, and pgvector packages. EF Core providers do not generally work across major versions. Do not adopt EF Core 11 APIs until Npgsql and `Pgvector.EntityFrameworkCore` publish matching support.

For read paths, project only required columns, use `AsNoTracking`, compile measured hot queries, stream large result sets, and keep vector candidate counts bounded. For writes, batch where ordering permits, use optimistic concurrency for mutable summaries, and keep model inference outside database transactions.

## Security

The service is intended for a trusted local network, not an unauthenticated broadcast service.

The design must include:

- Device and client authentication
- TLS or a documented trusted-network boundary
- Per-client rate limits
- Maximum prompt and context sizes
- Separate permissions for local inference and cloud routing
- Secret storage outside the repository
- Redaction of tokens, source code, and prompts from logs
- A clear retention and deletion command
- Audit records that do not contain prompt contents
- Safe behavior when cloud credentials expire

Non-loopback startup now requires all three of the following: explicit non-loopback enablement, an API key, and at least one allowed CIDR. Loopback remains allowed. LAN HTTP still has no transport confidentiality, so use a trusted private network or an SSH tunnel until HTTPS certificate deployment is configured.

A protected Kare cloud catalog was generated from the host's existing Lerna mappings for device testing. It contains route metadata but no bearer tokens, and it remains outside the repository. The Azure CLI credential cache used for token acquisition is also protected operational state. Rotate or remove both if the device is repurposed.

## Performance targets

These are proposed acceptance targets. Adjust them after the first benchmark, but keep measured values for every change:

| Measure | Initial target |
| --- | --- |
| Health response | Under 50 ms on the local network |
| Local cache hit | Under 100 ms before streaming |
| Local model first token | Under 2 seconds for a short coding request |
| Local generation | At least 8 tokens/second for the first candidate |
| Concurrent short requests | Two without an unbounded latency increase |
| Idle service memory | Under 512 MiB excluding model pages |
| Model load | Under 30 seconds after a warm filesystem cache |
| Cloud fallback | Explicit route, bounded timeout, cancellable |
| Cache accounting | Every hit and miss has a reason |

Measure first-token latency, steady-state tokens per second, total latency, resident memory, CPU use, accelerator use, temperature, throttling, and power if the board exposes it.

Use a fixed coding benchmark with:

- Small code completion
- Error explanation
- Test generation
- Refactoring suggestion
- Repository instruction following
- Tool-call or structured-output request

Quality needs human review and automated checks. A faster wrong patch is still wrong, just faster.

## Build stages

Progress as of 2026-10-03. The service and both measured backends run on the board. The active gate is finding a local backend that combines enough context, tool calling, acceptable quality, and interactive latency.

| Stage | State | What exists |
| --- | --- | --- |
| 0 protect the boundary | Done | Ignore rules, no device detail in the repository, plan reviewed |
| 1 device inventory | Closed, storage pressure increasing | Ran on the board. 8 cores A78C plus A55, 15 GB, no swap, `/dev/fastrpc-cdsp` present and openable, and 48 thermal zones idle at 38.8 C. After staging four model sets, eMMC free space fell to about 11 GB. Add NVMe before expanding the model matrix |
| 2 runtime proof | Closed for first QCS8275 NPU candidate | QAIRT 2.46.0 installs from apt. Hexagon V75 confirmed. GenieX v0.7.1 loads the Qualcomm Qwen3 1.7B W4A16 QCS8275 bundle and serves it through QNN/HTP. Kare now defaults to the validated GenieX sidecar, with ONNX retained only as an explicit CPU fallback |
| 3 model selection | GGUF conduit candidate passed the passive-local gate | A Qwen3.5 0.8B Q4_0 GGUF model ran through GenieX on the NPU with an 8192-token context. Short text and cache paths work on the board. Tool-call forwarding through Kare still needs fixing; the direct GenieX endpoint can emit tool-call SSE. Representative coding quality, sustained thermals, and full Copilot CLI behavior remain open |
| 4 service skeleton | Routing and LAN boundary implemented | The service now exposes explicit local, cloud, and automatic model IDs; records actual routes; rejects unsupported ONNX tool calls; enforces API-key plus CIDR requirements for non-loopback binding; and retains bounded inference admission |
| 5 cache and shared knowledge | Conservative response cache implemented | The opt-in memory cache accepts deterministic non-streaming text-only requests and has bounded size, lifetime, and response length. Repository fingerprints, durable shared knowledge, embeddings, invalidation commands, and cache-quality measurements remain open |
| 6 Copilot integration | Tiered routing implemented; cloud device validation open | Copilot CLI 1.0.91 reached Kare. Local text, streaming, authentication, and cache behavior pass on the board, but Kare-to-GenieX tool-call translation remains open. GitHub Copilot SDK `auto` returned a live bounded response on x64, cloud text arrived as real deltas, and declaration-only required tools were returned without execution |
| 7 Kare orchestration and Foundry | Unified catalog and inventory closed; route validation open | Kare selects local, low-cost Copilot, heavy Copilot, or Foundry tiers from explicit aliases and deterministic request shape. The protected external catalog separates model IDs from wire deployments, supports Responses and Anthropic routes, and uses scoped Azure CLI bearer tokens. The signed-in Copilot account and Azure Foundry resource were inventoried. New Sol and Sonnet routes are explicit but not automatic until live validation passes |

### Stage 0: protect the boundary

- Keep device access and credentials outside Git.
- Add repository ignore rules.
- Record the target platform without private addresses or credentials.
- Review this plan before code starts.

### Stage 1: device inventory

- Record CPU topology, memory, storage, kernel, temperature interfaces, and accelerator devices.
- Check available compiler, CMake, Python, .NET, Docker, and Qualcomm runtime packages.
- Add NVMe if the benchmark set will exceed eMMC headroom.

### Stage 2: runtime proof

- Install the pinned ONNX Runtime GenAI NuGet package and verify Linux ARM64 native loading.
- Run Phi-4-mini INT4 on CPU as the reference model.
- Record Phi-4-mini prompt and decode behavior before choosing the local default.
- Install pinned GenieX Linux ARM64 assets.
- Run Qwen3 1.7B W4A16 through the QCS8275 QAIRT bundle.
- Verify the GenieX OpenAI-compatible server and record model-id and thinking-mode quirks before adding a Kare adapter.
- Compare the published Q4_0 CPU, GPU, and NPU paths.
- Build a QNN-enabled ONNX Runtime GenAI native path only if the comparison still justifies it.
- Capture reproducible benchmark output.

### Stage 3: model selection

- Benchmark the first candidate set.
- Compare quality on coding tasks.
- Select one default local model and one optional fallback.
- Record model provenance, license, checksum, quantization, context, and runtime.
- Test one small GenieX GGUF model at 8192 tokens through llama.cpp HTP. Reject it if tool calls, latency, memory, or quality fail.

### Stage 4: service skeleton

- Add the .NET 11 service.
- Add health, authentication, request limits, streaming, cancellation, and structured metrics.
- Keep the service as a conduit. The default local sidecar is Qwen through GenieX; ONNX/Phi is an explicit fallback, not the primary route.
- Keep the local model's passive responsibilities bounded to cache assistance, context preparation, and skill metadata maintenance. It must not execute caller tools or silently escalate requests.
- Implement only documented HydraFusion-inspired strategies such as bounded single, cascade, and critique flows. Do not claim compatibility with GitHub's private HydraFusion implementation.
- Treat Lerna as a provider-mapping reference, not a runtime dependency.
- Do not add cloud routing or a cache until the local path is observable.

Measured on the VENTUNO Q on 2026-10-02:

```
health                 passed
model listing          passed
non-stream completion  passed, Phi-4-mini returned tokens and usage
streaming              passed, SSE completed with data: [DONE]
empty messages         rejected with HTTP 400
route                  LocalSlm / OnnxGenAiCpu / billable false
runtime                project-local .NET 11 RC on the board
```

### Stage 5: cache and shared knowledge

- Add versioned request keys. Done for the conservative response cache.
- Add bounded cache storage. Done in memory with entry, lifetime, and response-size limits.
- Add repository and skill metadata.
- Add invalidation and deletion.
- Measure cache hit quality and stale-context failures.

### Stage 6: Copilot integration

- Point Copilot CLI BYOK at a minimal Kare-compatible endpoint. Done on 2026-10-02 over an SSH local forward, which keeps the loopback binding.
- Measure Copilot CLI's static context floor before choosing a local default. It was about 4.8k tokens with tool definitions, which rules out the 4096-token GenieX Qwen3 bundle.
- Keep the ONNX CPU path out of tool-bearing routes. The current .NET client does not support function calling, so Kare returns `unsupported_backend_capability` rather than dropping tools.
- Test a small GenieX GGUF model at 8192 tokens. QAIRT context is compiled into the bundle and sliding-window eviction cannot fit an oversized initial prompt.
- Verify streaming, tool calls, cancellation, and offline mode.
- Run client-side BYOK tests with an isolated `COPILOT_HOME` so unrelated local plugins cannot change the request.
- Add the Copilot SDK cloud route. GitHub account routes leave provider configuration unset. Foundry routes use explicit external provider configuration. GitHub `auto` was live-tested on x64.
- Preserve permissions, streaming, cancellation, and session behavior. Caller tools are declaration-only and are not executed on the board. Cloud text deltas now stream; active-request cancellation still needs board validation.
- Replace explicit client-selected modes with one public `kare` model. Implemented as a bounded cache, Qwen gate, and ordered cloud cascade. Protected deployment configuration must opt in to cloud escalation.
- Test an extension or plugin interceptor only as a separate experimental track.

### Stage 7: Kare orchestration and Foundry

- Keep cloud routes, tool support, and escalation priority explicit in the protected catalog.
- Validate every configured Copilot model against the account before enabling automatic billable routing.
- Recheck the official Copilot model list and the installed CLI account during each catalog refresh. Public availability is not proof of entitlement or SDK compatibility.
- Configure each Foundry deployment outside the repository with an HTTPS resource base URL, a well-known model ID, a separate wire deployment name, the Responses or Anthropic Messages wire, and an authentication mode.
- Inventory Foundry with `az cognitiveservices account list-models` and `az cognitiveservices account deployment list`. Treat model definitions as candidates and active deployments as the only immediately testable routes.
- Record model version, deployment SKU, capacity, tool support, streaming behavior, latency, and observed cost before changing route priority. Availability alone is not a price comparison.
- Prefer scoped Entra bearer tokens. Kare currently obtains Azure access tokens from a protected Azure CLI profile with a bounded command timeout and in-memory reuse by scope. The SDK may ask for a token before each provider request, but `az account get-access-token` runs only on a cold cache or during the two-minute refresh window. Managed identity is a later option if the deployment environment supports it.
- Test each mapped model with a known request and verify streaming, cancellation, caller-owned tool calls, route metadata, latency, and usage. Keep Foundry tool support disabled in the catalog until those tests pass.
- Keep critique and ensemble strategies out until they have explicit provider-call and budget limits. The implemented cascade retries only provider failures that occur before output starts.
- Do not put Lerna source, settings, or auth files in Kare.

### Stage 8: AOT release

- Publish a self-contained `linux-arm64` Native AOT binary.
- Package native inference libraries and model metadata separately.
- Test on a clean board environment.
- Add systemd service hardening, log limits, restart policy, and a rollback path.

## Risks

| Risk | Effect | Response |
| --- | --- | --- |
| The QCS8275 runtime fails on the installed board image | No accelerated production path | Keep CPU, pin the tested image, and report the runtime mismatch |
| Native GenAI bindings do not package cleanly for .NET 11 AOT | Delayed deployment | Isolate the adapter and ship a non-AOT proof first |
| A model is fast but poor at coding | Bad local suggestions | Use a quality gate and cloud escalation policy |
| Cache returns stale repository advice | Incorrect code changes | Include revision and file hashes, add invalidation |
| Copilot CLI has no model transport interception hook | No transparent native provider proxy | Use BYOK for the TUI and the SDK for separate cloud escalation |
| Copilot CLI BYOK does not retain native HydraFusion | Clients cannot rely on GitHub's private orchestration through Kare | Use Kare-owned bounded routing and keep native HydraFusion only as a separate comparison profile |
| Protected Azure CLI state expires or is removed | Foundry routes fail authentication | Return an explicit route failure, keep GitHub and local routes available, and require a deliberate device login refresh |
| Cloud fallback becomes opaque or expensive | Unexpected billing | Require explicit policy and log route/accounting |
| 16 GB memory is consumed by model plus cache | Swapping or crashes | Use quantized models, cap cache, add NVMe |
| No swap and sustained load causes instability | Service interruption | Watch memory and temperature, reject work early |
| Experimental HydraFusion behavior changes | Integration breakage | Pin tested versions and keep the adapter optional |

## Sources

### Arduino and Qualcomm

- [Arduino VENTUNO Q product page](https://www.arduino.cc/product-ventuno-q)
- [Arduino VENTUNO Q hardware documentation](https://docs.arduino.cc/hardware/ventuno-q/)
- [Arduino VENTUNO Q NPU guide](https://docs.arduino.cc/tutorials/ventuno-q/npu-guide/)
- [Arduino local LLM guide](https://docs.arduino.cc/tutorials/ventuno-q/llama-cpp/)
- [Arduino GenieX guide](https://docs.arduino.cc/tutorials/ventuno-q/geniex/)
- [Qualcomm IQ-8275](https://www.qualcomm.com/internet-of-things/products/iq8-series/iq-8275)
- [Qualcomm AI Hub](https://aihub.qualcomm.com/)
- [Qualcomm AI Hub Models](https://github.com/qualcomm/ai-hub-models)
- [Qualcomm AI Runtime SDK](https://www.qualcomm.com/developer/software/qualcomm-ai-runtime-sdk-qairt)
- [Qualcomm GenieX](https://github.com/qualcomm/GenieX)
- [Qualcomm Qwen3 1.7B model](https://github.com/qualcomm/ai-hub-models/tree/main/src/qai_hub_models/models/qwen3_1_7b)
- [Qualcomm Qwen3 4B model](https://github.com/qualcomm/ai-hub-models/tree/main/src/qai_hub_models/models/qwen3_4b)

### Microsoft and GitHub

- [.NET Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [.NET Native AOT cross-compilation](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile)
- [ONNX Runtime GenAI](https://github.com/microsoft/onnxruntime-genai)
- [ONNX Runtime GenAI NuGet](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntimeGenAI)
- [ONNX Runtime QNN packages](https://github.com/onnxruntime/onnxruntime-qnn)
- [ONNX Runtime GenAI build from source](https://onnxruntime.ai/docs/genai/howto/build-from-source.html)
- [ONNX Runtime QNN Execution Provider](https://onnxruntime.ai/docs/execution-providers/QNN-ExecutionProvider.html)
- [Microsoft Phi-4-mini ONNX](https://huggingface.co/microsoft/Phi-4-mini-instruct-onnx)
- [Qwen2.5-Coder ONNX community model](https://huggingface.co/onnx-community/Qwen2.5-Coder-0.5B-Instruct)
- [GitHub Copilot SDK](https://github.com/github/copilot-sdk)
- [GitHub Copilot CLI BYOK](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-byok-models)
- [GitHub Copilot SDK BYOK and provider behavior](https://docs.github.com/en/copilot/how-tos/copilot-sdk/auth/byok)
- [GitHub Copilot CLI extensions](https://docs.github.com/en/copilot/concepts/agents/copilot-cli/about-cli-extensions)
- [GitHub Copilot hooks reference](https://docs.github.com/en/copilot/reference/hooks-reference)
- [GitHub Copilot AI credit billing](https://docs.github.com/en/copilot/concepts/billing/usage-based-billing-for-individuals)
- [GitHub Copilot model pricing](https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing)
- [Project HydraFusion](https://github.blog/ai-and-ml/github-copilot/project-hydrafusion-frontier-quality-via-multi-model-orchestration/)
- [Lerna](https://github.com/sirredbeard/Lerna)
- [.NET 11 overview](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview)
- [EF Core 11 changes](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-11.0/whatsnew)
- [Azure Database for PostgreSQL pgvector](https://learn.microsoft.com/en-us/azure/postgresql/extensions/how-to-use-pgvector)
- [Visual Studio Azure credit eligibility](https://learn.microsoft.com/en-us/visualstudio/subscriptions/vs-azure-eligibility)

The cited documentation is current as of 2026-10-01. Experimental products, model catalogs, package feeds, and board tutorials can change. Recheck them at each build gate.
