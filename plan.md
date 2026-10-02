# Kare plan

Kare should be a fast local service for coding assistance, with a local SLM handling short, common requests and a policy-controlled path to GitHub Copilot or Microsoft Foundry for harder work.

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

## What is still unknown

These questions are gates in the build plan:

1. Can the required Qualcomm runtime libraries be installed and used on Ubuntu 24.04.5 on this board?
2. Does the available QNN or QAIRT release support the exact QCS8275 and HTP architecture?
3. Can the chosen ONNX model be compiled for the HTP backend without unsupported operators or unacceptable CPU fallbacks?
4. Does ONNX Runtime GenAI have a supported .NET 11 ARM64 package, or must Kare build and load native libraries itself?
5. Does GenieX expose a stable local API suitable for a long-running service, or only a command-line workflow?
6. Which Copilot CLI extension lifecycle can intercept or redirect calls before the model request is sent?
7. Can the GitHub Copilot SDK preserve streaming, cancellation, permissions, tool calls, and session identity through Kare?
8. Which calls can be cached without returning stale or unsafe code?
9. How much context can the board process while keeping first-token latency acceptable?
10. What is the actual thermal and power behavior during sustained generation?

## Research gate findings, 2026-10-01

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
| Context, thermals, and power | Thermals look like a non-issue | Sustained CPU generation peaked at 43.3 C. Context cost is the real limit at about 15 ms per prompt token on CPU |

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

9. Available context on the board: this is an unknown that must be measured, not guessed. The board has 16 GB physical memory and roughly 14 GiB usable by the system. Model weights, token cache, service memory, PostgreSQL, and native libraries all compete for that budget. Start at 4K, then measure 8K and 16K with the same prompt and output lengths. Keep raw history, compacted summaries, retrieved source chunks, and the active prompt as separate records. Compaction must preserve decisions, constraints, file and commit identifiers, failed approaches, unresolved questions, and source links. The model's advertised 128K window is not a deployment target for this board.

10. Thermal and power behavior: also unknown until measured. The board has a large heat sink and a power budget that can likely be used aggressively, but the public docs do not give a measured sustained-generation profile for coding models. The first benchmark should run the model for a sustained interval, record first-token latency, steady-state tokens per second, total latency, temperature, throttling, and system power if available, and only then set the real runtime policy. The board should be configured to maximize useful local work, but not by blindly pushing the board until it throttles or fails.

The main conclusion is narrower now: Qualcomm acceleration on QCS8275 is a supported product path, and the fastest proof should use a QCS8275-specific GenieX or AI Hub model. The open work is comparative measurement, an ONNX/QNN C# integration if it still provides value, and the Copilot routing contract. Production coding still waits for the device benchmark.

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

HydraFusion through Lerna is a separate native Copilot path until an experimental interceptor proves Kare can sit in front of it without breaking sessions, streaming, cancellation, or usage accounting.

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

The cloud adapter should call the GitHub Copilot SDK using the user's authenticated Copilot account when the policy allows it. It should preserve the SDK's permission model and usage accounting. SDK provider configuration should remain unset on this route so it does not accidentally bypass GitHub authentication with BYOK.

Lerna should remain the existing HydraFusion to Microsoft Foundry interception layer. Kare should not copy Lerna source into this repository or assume it can add model IDs to HydraFusion. The private HydraFusion flags, Lerna mappings, and Lerna auth file were copied to the device on 2026-10-01 and verified without recording their values here.

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

The release artifact should target `linux-arm64`, carry no .NET runtime requirement, and keep models and Qualcomm libraries outside the main executable. Do not call the deployment a single binary when separate provider libraries are still required.

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

The SDK route should answer:

- Can Kare create a cloud session without provider overrides and preserve GitHub authentication?
- Does the SDK expose the usage and model metadata needed for route accounting?
- Can a cloud session return streamed tokens and tool calls to the BYOK client without changing their meaning?
- Does it preserve tool calls and permissions?
- Does it work across a network boundary?
- What happens when Kare is down?

GitHub's documented CLI extension API adds tools and slash commands. It does not document a general model-request replacement hook. Treat transparent interception through an extension as experimental work, not the base design.

CLI hooks and SDK hooks can modify the submitted or transformed prompt before the model sees it. This is useful for redaction, policy, route metadata, and cache fingerprints. A prompt hook is not a provider hook and cannot supply the model's streamed response.

### HydraFusion and Lerna

[HydraFusion](https://github.blog/ai-and-ml/github-copilot/project-hydrafusion-frontier-quality-via-multi-model-orchestration/) is an experimental Copilot orchestration feature. It chooses a workflow such as single-model execution, cascade, or critique. The feature, model IDs, and behavior can change.

[Lerna](https://github.com/sirredbeard/Lerna) intercepts supported HydraFusion model calls and can route selected model IDs to Microsoft Foundry. Lerna currently maps only model IDs that HydraFusion already accepts. It does not add models to HydraFusion.

The supported routes should be documented separately:

```text
Copilot CLI BYOK
  -> Kare
  -> local SLM or Copilot SDK cloud session
```

```text
Copilot CLI native HydraFusion
  -> Lerna interception
  -> Microsoft Foundry for mapped models
```

The desired combined route remains a research gate:

```text
Copilot CLI native HydraFusion
  -> experimental interceptor
  -> Kare cache and policy
  -> HydraFusion and Lerna
```

Do not build the combined route until the supported extension lifecycle and request interception API are confirmed from the installed Copilot CLI version.

Kare should record:

- Local, Copilot, or Foundry route
- Model identifier
- Cache hit or miss
- Request and response token counts when available
- Latency per route
- Cancellation and error reason
- Estimated billable call avoided or made

Do not claim that a local cache reduces Copilot billing until the Copilot call is actually avoided and the result is verified against the provider's accounting.

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

The host's HydraFusion and Lerna settings were copied to the device as a private operational setup. They must never be copied into this Git repository. Rotate credentials if the device is repurposed or the password is reused.

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

Progress as of 2026-10-02. Stage 0 is done. Stage 1 has a tool but no device run. Stage 2 is half done, with the CPU half of the runtime proof closed off-device. Stage 4 has a skeleton that is structurally complete and has never served a real token.

| Stage | State | What exists |
| --- | --- | --- |
| 0 protect the boundary | Done | Ignore rules, no device detail in the repository, plan reviewed |
| 1 device inventory | Closed | Ran on the board. 8 cores A78C plus A55, 15 GB, no swap, 34 GB free eMMC, `/dev/fastrpc-cdsp` present and openable, no QAIRT userspace, 48 thermal zones idle at 38.8 C |
| 2 runtime proof | CPU and QNN plumbing closed, useful NPU model open | QAIRT 2.46.0 installs from apt. Hexagon V75 confirmed. Official QNN plugin registers from .NET and executes a profiled HTP partition. Phi-4-mini now runs on the board. GenieX remains untested |
| 3 model selection | TinyLlama CPU measured, TinyLlama QNN rejected, Phi-4-mini measured | Arm TinyLlama 1.1B int4 loads and generates, but only a trivial mask subgraph reaches HTP and performance regresses. Phi-4-mini INT4 loads through the .NET 11 ARM64 path but takes 26.4 seconds to first token at 256 prompt tokens. Final choice needs a Qualcomm model benchmark |
| 4 service skeleton | Serving | `build/smoke-service.sh` passes end to end. Streaming and non-streaming completions, real token usage, bounds, admission control, route disclosure, local only policy. AOT published for ARM64 |
| 5 cache and shared knowledge | Not started | |
| 6 Copilot integration | Endpoint ready | BYOK endpoint serves. Copilot CLI has not been pointed at it yet |
| 7 Lerna and Foundry | Not started | |

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
- Compare the published Q4_0 CPU, GPU, and NPU paths.
- Build a QNN-enabled ONNX Runtime GenAI native path only if the comparison still justifies it.
- Capture reproducible benchmark output.

### Stage 3: model selection

- Benchmark the first candidate set.
- Compare quality on coding tasks.
- Select one default local model and one optional fallback.
- Record model provenance, license, checksum, quantization, context, and runtime.

### Stage 4: service skeleton

- Add the .NET 11 service.
- Add health, authentication, request limits, streaming, cancellation, and structured metrics.
- Add one local inference adapter.
- Do not add cloud routing or a cache until the local path is observable.

### Stage 5: cache and shared knowledge

- Add versioned request keys.
- Add bounded cache storage.
- Add repository and skill metadata.
- Add invalidation and deletion.
- Measure cache hit quality and stale-context failures.

### Stage 6: Copilot integration

- Point Copilot CLI BYOK at a minimal Kare-compatible endpoint.
- Verify streaming, tool calls, cancellation, and offline mode.
- Add the Copilot SDK cloud route without provider overrides.
- Preserve permissions, streaming, cancellation, and session behavior.
- Add explicit local versus cloud policy.
- Test an extension or plugin interceptor only as a separate experimental track.

### Stage 7: Lerna and Foundry

- Validate the existing HydraFusion and Lerna settings on the device.
- Test each mapped model with a known request.
- Record route, latency, and usage.
- Do not put Lerna source or auth files in Kare.

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
| Copilot CLI BYOK and HydraFusion cannot share one provider path | Local routing cannot transparently retain HydraFusion | Keep documented BYOK and native HydraFusion profiles separate |
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
