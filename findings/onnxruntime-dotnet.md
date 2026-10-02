# ONNX Runtime and .NET

Checked 2026-10-01.

ONNX Runtime GenAI 0.17.1 supports C#, Linux, ARM64, CPU, and QNN in the project support matrix.

`Microsoft.ML.OnnxRuntimeGenAI` 0.17.1 contains `runtimes/linux-arm64/native/libonnxruntime-genai.so`.

The package depends on `Microsoft.ML.OnnxRuntimeGenAI.Managed` 0.17.1 and `Microsoft.ML.OnnxRuntime` 1.30.0.

The managed package targets .NET 8 and .NET Standard. A .NET 11 application should use the packaged CPU path first.

The ordinary GenAI NuGet package does not prove QNN support in that Linux ARM64 native library.

The main ONNX Runtime QNN page still documents Android and Windows and says prebuilt packages are Windows only. The newer separate `onnxruntime-qnn` repository now publishes Linux ARM64 inference assets. The documentation is split and not fully synchronized.

There is no Linux QNN NuGet package in the `onnxruntime-qnn` 2.6.0 packaging table.

Kare probably needs a custom QNN-enabled native GenAI build or adapter for C#. Do not build the CPU runtime unless the packaged library fails on the board.

Phi-4-mini-instruct-onnx is the clean CPU reference. Microsoft publishes an MIT-licensed INT4 ONNX Runtime GenAI layout. The advertised 128K context is not a sensible starting point on this 16 GB board.

Qwen, Granite, Phi, and SmolLM3 are supported architectures in GenAI. A supported architecture does not mean a random Hugging Face ONNX conversion is a valid GenAI model or an HTP-compatible graph.

For an ONNX/QNN gate:

1. Build or obtain the exact native QNN-enabled runtime.
2. Record QAIRT, ONNX Runtime, GenAI, compiler, and model versions.
3. Compile the exact graph.
4. Record provider assignment for every node.
5. Run detailed or `optrace` profiling.
6. Reject hidden CPU fallback if it materially affects prompt or decode latency.

Sources:

https://www.nuget.org/packages/Microsoft.ML.OnnxRuntimeGenAI

https://github.com/microsoft/onnxruntime-genai/releases

https://github.com/microsoft/onnxruntime-genai

https://onnxruntime.ai/docs/execution-providers/QNN-ExecutionProvider.html

https://github.com/onnxruntime/onnxruntime-qnn/releases/tag/v2.6.0

https://huggingface.co/microsoft/Phi-4-mini-instruct-onnx

## Measured 2026-10-02, gate 4 build and run

Stopped reading docs and ran it. Results below are from actual builds on this x86_64 workstation plus an emulated aarch64 container, not from a support matrix.

`Microsoft.ML.OnnxRuntimeGenAI` 0.17.1 restores on `net11.0` with no warnings. The managed assembly targets netstandard2.0, net8.0, and some mobile TFMs. It loads on net11.0 without a problem.

`dotnet publish -r linux-arm64 --self-contained` from x86_64 produces aarch64 native payload. Confirmed with `file`:

- `libonnxruntime-genai.so`, 38.5 MB, ELF 64-bit LSB aarch64, with debug_info, not stripped
- `libonnxruntime.so`, 25.1 MB, ELF 64-bit LSB aarch64, stripped
- `libonnxruntime_providers_shared.so`

That is the whole native list. There is no `libonnxruntime_providers_qnn.so` in either package. The string `QNNExecutionProvider` is present inside `libonnxruntime.so`, and `libonnxruntime-genai.so` mentions QNN 121 times, so the runtime knows the provider exists. It just cannot load it. `AppendProvider("qnn")` is necessary and not sufficient. The QNN gate needs a custom native build or a repackaged provider library, and no amount of C# config changes that.

Ran the published ARM64 binary in `docker.io/library/ubuntu:24.04` arm64. That image reports Ubuntu 24.04.5 LTS, which matches the board OS exactly. Output: `arch=Arm64 rid=linux-arm64`, native loaded, then a managed-level `Error opening /nonexistent-model-dir/genai_config.json`. That is the right error. Gate 4 CPU path is closed without building anything from source.

Two container gotchas on this SELinux host. Need `--security-opt label=disable` or the binary exits 126 with permission denied. Need `chmod +x` on the published binary because the bind mount does not preserve it. Also need `libicu74` in the container for anything not using invariant globalization.

Harmless warning under emulation: `onnxruntime cpuid_info warning: Unknown CPU vendor. cpuinfo_vendor value: 0`.

## The package already has an IChatClient

Dumped the public API by reflection instead of guessing. `Microsoft.ML.OnnxRuntimeGenAI.OnnxRuntimeGenAIChatClient : IChatClient, IDisposable` already ships. No hand-written tokenizer and generator loop needed.

Constructors: `(string modelPath, OnnxRuntimeGenAIChatClientOptions)`, `(Model, bool ownsModel, options)`, `(Config, bool ownsConfig, options)`.

It implements `IChatClient.GetService` explicitly, so calling it needs a cast to `IChatClient`. This is a real compile error if you forget.

`OnnxRuntimeGenAIChatClientOptions` has `StopSequences`, `PromptFormatter`, and `EnableCaching`. Default `PromptFormatter` relies on the tokenizer chat template. Leaving it unset is the right call. The default value of `EnableCaching` is still unconfirmed.

`Config` exposes `AppendProvider(string)`, `ClearProviders()`, `SetProviderOption(provider, option, value)`, `Overlay(json)`, `AddModelData`, `RemoveModelData`, and hardware device setters for device id, device type, and vendor id. Execution provider selection is reachable from C# without editing `genai_config.json` on disk.

Kare calls `ClearProviders()` before `AppendProvider(...)`. Otherwise the runtime can silently fall back to whatever the config file declared, and Kare would report an accelerated backend it never used. That is exactly the kind of silent route lie the project is supposed to prevent.

Still unverified: whether `OnnxRuntimeGenAIChatClient` reports `UsageContent` with token counts. If it does not, the benchmark reports decode rate as unknown rather than inventing a number.

## Measured 2026-10-02, first real tokens out of the stack

Benchmarked on the dev box, not the board. The dev box is an Intel i5-6300U, two Skylake cores from 2015, four threads, 20 GB RAM. It is slower than the VENTUNO Q is likely to be. Treat these as a working proof, not a prediction.

Model was `Arm/tinyllama-1-1b-chat-onnx-genai-int4-kquantlast-emb-int8-graviton-g4`. Apache-2.0, published by Arm, 763 MB of weights, llama architecture, 2048 context, 22 layers, 32 heads, 4 KV heads. I picked it because it is a real GenAI layout model from a named vendor, it is small enough to download in half an hour, and it is quantized for aarch64, which makes it interesting later.

```
kare-probe bench --model models/tinyllama-arm-int4 --prompt-tokens 512 \
  --max-output-tokens 128 --iterations 3 --warmup 1

model       tinyllama-arm-int4 via OnnxGenAiCpu
provider    genai_config.json default
prompt      ~512 tokens, max output 128
iterations  3 measured, 0 failed, 1 warmup
ttft        median 13969 ms p95 15511 ms
decode      median 16.09 tok/s
total       median 21833 ms
thermal     start 59.0 C peak 75.0 C on x86_pkg_temp
```

Fourteen seconds to first token on a 512 token prompt is bad, and it is the prefill that is bad, not the decode. Decode at 16 tok/s on two 2015 cores is fine. Prefill is compute bound and this CPU has no useful vector width for int4 GEMM. The board has eight cores and much newer vector units. Do not carry this number forward.

The important result is that the whole chain works. `Microsoft.ML.OnnxRuntimeGenAI` 0.17.1, loaded through `OnnxRuntimeGenAIChatClient`, wrapped in our bounded client, driven by our benchmark harness, produced tokens and a complete report with thermal sampling.

Answered an open question: the backend does emit usage. The service reported `"usage":{"prompt_tokens":30,"completion_tokens":16,"total_tokens":46}` on a real completion. So `UsageContent` comes through `IChatClient` and we do not need to count tokens ourselves.

One trap worth writing down. `bench/Kare.DeviceProbe` sets `RuntimeIdentifier` to `linux-arm64` by default. On the x86_64 dev box `dotnet run` then builds an aarch64 apphost, binfmt hands it to qemu, and you get:

```
qemu-aarch64-static: Could not open '/lib/ld-linux-aarch64.so.1': No such file or directory
```

That is not a broken install. Pass `-r linux-x64` when running locally.

Second trap. `build/publish-arm64.sh` mounts the repo at `/src` and builds in place, which leaves aarch64 `bin` and `obj` directories behind that poison the next local build. Fixed by pointing the container build at `artifacts/arm64-build/` for both `BaseOutputPath` and `BaseIntermediateOutputPath`.

## Measured 2026-10-02 on the VENTUNO Q, CPU inference is prefill bound

First benchmarks on real hardware. Arm TinyLlama 1.1B int4, CPU only, no QAIRT installed.

Before measuring anything I set every core to the `performance` governor. Cores 2 and 3 were idling at 940 MHz under `ondemand` with a 2361 MHz ceiling, so a default benchmark would have been measuring the governor rather than the board.

```
cpu0 performance 2112000
cpu1 performance 2112000
cpu2 performance 2361600
cpu3 performance 2361600
cpu4 performance 1958400
cpu5 performance 1958400
cpu6 performance 1958400
cpu7 performance 1958400
```

Baseline, 512 prompt tokens, 128 output, 3 iterations, 1 warmup:

```
ttft        median 8794 ms
decode      median 22.88 tok/s
total       median 14354 ms
thermal     start 39.0 C peak 43.3 C on cpu-0-2-1-thermal
```

The board beats the 2015 dev box on both numbers, 8.8 s against 14.0 s on first token and 22.9 tok/s against 16.1 tok/s on decode. Thermals are a non-issue so far. Peak 43.3 C on a sustained run with a large heat sink. Gate 10 is not going to be the problem I expected.

### Thread count

Swept `intra_op_num_threads` in `genai_config.json`, 512 prompt, 64 output:

```
threads=4   ttft 8755 ms   decode 23.09 tok/s
threads=6   ttft 7740 ms   decode 22.91 tok/s
threads=8   ttft 7856 ms   decode 11.54 tok/s
```

Six is the answer. Eight halves decode throughput. That is almost certainly the ONNX Runtime thread pool spilling onto the A55 little cores and then waiting on them at every barrier, so the whole thing runs at little core speed. Do not set threads to core count on a heterogeneous board. This has to be a configured value, not `Environment.ProcessorCount`.

### The real problem

Swept prompt length at 6 threads, 32 output tokens:

```
prompt=64     ttft 1696 ms
prompt=128    ttft 2555 ms
prompt=256    ttft 4488 ms
prompt=512    ttft 7723 ms
prompt=1024   ttft 16010 ms
```

That is linear. About 15 ms per prompt token plus roughly 700 ms fixed. Prefill runs at about 66 tokens per second.

That number is the whole story. Decode is 23 tok/s and prefill is 66 tok/s, so prefill is only about three times faster than decode. On a healthy implementation prefill should be ten to fifty times faster than decode, because prefill is one big batched matrix multiply and decode is a sequence of tiny ones. Getting 3x means the prompt is effectively being walked rather than batched, or the int4 kernels in this build have no batched path worth the name on Cortex-A78C.

The consequence is concrete and bad. Copilot CLI's static system prompt plus tool definitions already overflowed a 2048 token context. Call it 10,000 to 20,000 tokens in practice. At 15 ms per prompt token that is 150 to 300 seconds before the first token of every single turn. Nobody will use that.

So CPU-only local inference does not clear the bar for the Copilot CLI BYOK path. Three ways out, in the order I will try them:

1. Reuse the prefill. The static system prompt and tool definitions are byte for byte identical across every turn of every session. If the KV cache for that prefix can be computed once and restored, the per-turn prefill drops to just the conversation delta. This is the highest value fix by a wide margin and it is pure software. It needs ONNX Runtime GenAI to expose a way to save and restore KV state, which is unverified.
2. Get the HTP backend working. `/dev/fastrpc-cdsp` is present, so the kernel path exists. If QAIRT installs and the model compiles for HTP without falling back, prefill should improve by a large factor. That is gate 2 and gate 3.
3. Cut the prompt. Kare can strip tool definitions the local model will never use and keep a much smaller static context for local turns, escalating to Copilot when the full tool surface is actually needed. This is a routing decision and it is in scope anyway.

Also worth testing Microsoft Phi-4-mini INT4 before concluding anything about the runtime. It is RTN block-32 with acc-level-4 and Microsoft tunes those `MatMulNBits` kernels. The Arm model is "kquantlast" with int8 embeddings, built and benchmarked for Graviton g4, which is Neoverse with SVE. The A78C has NEON only. A quantization chosen for SVE may simply have no good NEON path, and that alone could explain a 3x prefill.

Do not generalize the 15 ms per token until Phi-4-mini has been measured on the same board.
