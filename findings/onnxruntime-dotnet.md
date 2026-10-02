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
