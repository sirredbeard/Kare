# Qualcomm runtime and models

Checked 2026-10-01.

GenieX 0.7.1 is Developer Preview. It publishes Linux ARM64 CLI, benchmark, Python, C SDK, Docker, and OpenAI-compatible server assets.

GenieX uses one C SDK under the CLI, Python, Java or Kotlin, Docker, and server interfaces.

`geniex serve` exposes an OpenAI-compatible endpoint at `http://127.0.0.1:18181/v1`.

GenieX explicitly lists Dragonwing IQ-8275, `QCS8275`, as `qualcomm-qcs8275`.

The runtime detects the VENTUNO Q and maps it to QCS8275.

Qualcomm AI Hub has QCS8275-specific assets for Qwen3 0.6B, 1.7B, 4B, and 8B. The 1.7B and 4B assets include Genie W4A16 bundles.

The Genie bundle is a per-chipset QAIRT artifact. It is not an ONNX Runtime GenAI model directory.

## Measured 2026-10-02, native board build and streamed tool call

The VENTUNO Q has the .NET 11 SDK installed locally. I built the current GenieX-enabled Kare service on the board itself instead of making the x86_64 host link ARM64 code through QEMU.

```
board       Ubuntu 24.04.5, aarch64
SDK         .NET 11.0.100-rc.1.26425.128
publish     self-contained linux-arm64 Native AOT
wall time   89.36 seconds
```

The board-native build produced a working ARM64 Native AOT binary. The same publish through the x86_64 host's emulated ARM64 container was still running after the board build completed, so the board is the default publish location for this project while the target SDK remains installed there. Keep the container script as the reproducible fallback and for clean ARM64 environments.

After starting GenieX 0.7.1 on `127.0.0.1:18181`, Kare selected `GenieXQairt` at priority 100. A streamed required-tool-call request produced the tool-call delta first, followed by the terminal chunk with `finish_reason=tool_calls`, usage, and `kare_route.backend=GenieXQairt`. The service returned `data: [DONE]` and stayed healthy.

The first restart selected ONNX because GenieX was not running when Kare probed it. Kare's backend selection is made during service startup, so the sidecar must be supervised and started before Kare. When the sidecar was ready first, selection and streaming passed.

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

## Checked 2026-10-02, hunting for a Qwen2.5-Coder in ONNX Runtime GenAI format

Searched the Hugging Face API rather than guessing repo names, because guessing wasted time earlier. A 404 on `huggingface.co` returns "Invalid username or password" when you curl it, which reads like an auth problem and is not.

Repos that do not exist, despite being the obvious names:

```
onnx-community/Qwen2.5-Coder-0.5B-Instruct-ONNX
EmbeddedLLM/Qwen2.5-0.5B-Instruct-onnx
```

What actually exists and is in GenAI layout, meaning it ships a `genai_config.json`:

```
9thLevelSoftware/qwen2.5-coder-1.5b-instruct-onnx-genai-int4
9thLevelSoftware/qwen2.5-coder-3b-instruct-onnx-genai-int4
JamshaidF/qwen2.5-coder-1.5b-instruct-cpu-int4-onnx
```

All three have zero downloads, zero likes, and no declared license. I am not going to put an unsigned model of unknown provenance in the inference path of a service that reads source code. They are useful as a sanity check that the loader handles a non-Phi architecture, nothing more.

The `onnx-community/Qwen2.5-Coder-*` repos are real but are Transformers.js exports. Those are not ONNX Runtime GenAI models. Same file extension, different contract. Do not confuse them.

The `amd/Qwen2.5-Coder-*-onnx-ryzenai-*` repos are real GenAI models but are compiled for Ryzen AI NPU. Wrong vendor, wrong backend, no use to us.

The defensible path for a Qwen coder model is to build it ourselves:

```
pip install onnxruntime-genai
python -m onnxruntime_genai.models.builder \
  -m Qwen/Qwen2.5-Coder-1.5B-Instruct \
  -o models/qwen2.5-coder-1.5b-int4 \
  -p int4 -e cpu -c /tmp/hf-cache
```

That gives a model with known provenance, a known license from the upstream repo, and a config we generated. Do this before promoting any Qwen build past a smoke test. Unknown whether the builder output for `-e cpu` is reusable for QNN, or whether QNN needs a separate `-e` run with a different quantization and fixed shapes. That is a gate 3 question and it gets answered on the board.

Microsoft Phi-4-mini INT4 remains the first reference because it is first party, licensed, and published in GenAI layout by the people who wrote the runtime.

## Measured 2026-10-02 on the VENTUNO Q, gate 1 closed

Ran the Native AOT aarch64 `kare-probe probe` on the board. It ran with no runtime installed, no dependencies, nothing but the binary and three ONNX shared objects. First real device data.

```
host        Ubuntu 24.04.5 LTS
kernel      6.8.0-1084-qcom Arm64
runtime     .NET 11.0.0-rc.1.26425.128 (linux-arm64)
cpus        8 [0xd4b, 0xd05]
memory      total 15284 MiB available 14198 MiB
dsp         /dev/fastrpc-cdsp, /dev/dma_heap/system
qairt       no QNN or QAIRT libraries resolved
onnxgenai   GenAI native loaded. No QNN provider library, so only the config declared provider is usable.
thermal     48 zones, hottest pmm8650au_1_sdram at 38.8 C
```

The CPU part numbers are reported as raw MIDR values because the Qualcomm kernel does not fill in a model name. `0xd4b` is Cortex-A78C and `0xd05` is Cortex-A55. So the earlier guess about the cluster mix was right.

Clock layout is not what I expected and it matters:

```
core 0  schedutil  2112000 kHz   max 2112000
core 1  schedutil  2112000 kHz   max 2112000
core 2  ondemand    940800 kHz   max 2361600
core 3  ondemand    940800 kHz   max 2361600
core 4  schedutil  1958400 kHz   max 1958400
core 5  schedutil  1958400 kHz   max 1958400
core 6  schedutil  1958400 kHz   max 1958400
core 7  schedutil  1958400 kHz   max 1958400
```

Cores 2 and 3 are the fastest cores on the board at 2.36 GHz and they are sitting at 940 MHz under the `ondemand` governor. Every other core reports current equal to max, which means those policies have a single operating point and cannot scale. If we want the board maxed out, cores 2 and 3 need the `performance` governor before any benchmark. Benchmarking without doing that would understate the board by a lot.

There is no swap. Zero. That is the right setting for this workload but it means an over-budget model is an OOM kill, not a slowdown. The memory bound has to be enforced by us.

Storage is 55 GB of ext4 on `/dev/mmcblk0p71` eMMC with 34 GB free. No NVMe installed yet. Models go on eMMC for now, which is fine for a 5 GB model but will matter once we add a cache database and an index.

The accelerator picture is the useful part:

```
/dev/fastrpc-cdsp     present and openable
/dev/dma_heap/system  present and openable
/dev/fastrpc-adsp     absent
/dev/fastrpc-sdsp     absent
/dev/ion              absent
/dev/kgsl-3d0         absent
```

`/dev/fastrpc-cdsp` existing and being openable by the normal user is the single most important result so far. That is the compute DSP FastRPC channel, which is how anything reaches the Hexagon NPU. The kernel side of the NPU path is there. What is missing is entirely userspace:

```
libQnnHtp.so          not resolvable
libQnnHtpV73Stub.so   not resolvable
libQnnHtpV75Stub.so   not resolvable
libQnnHtpV79Stub.so   not resolvable
libQnnSystem.so       not resolvable
libQnnCpu.so          not resolvable
libQnnGpu.so          not resolvable
```

So gate 2 is now a concrete task rather than a question. The hardware channel exists, the QAIRT userspace does not, and installing it is the next step. Note also that `/dev/kgsl-3d0` is absent, so the Adreno GPU is not exposed through the usual KGSL node on this image. Do not plan on a GPU execution provider until that is understood.

48 thermal zones, idle hotspot 38.8 C on `pmm8650au_1_sdram`. Plenty of instrumentation to work with for gate 10.

Gate 1 is closed. The probe answered every question it was built to answer, on the real board, from a 4 MB self-contained binary.

## Measured 2026-10-02 on the VENTUNO Q, gates 1 and 2 closed

Gate 1 asked whether the Qualcomm runtime libraries can be installed and used on Ubuntu 24.04.5 on this board. Yes, and easier than expected. They are in apt on the stock image.

```
qairt-libs           Qualcomm AI Runtime SDK - Libraries
qairt-headers        Qualcomm AI Runtime SDK - Development files
qairt-tools          Qualcomm AI Runtime SDK - Binary tools
qairt-dsp-binaries   Qualcomm AI Runtime SDK - DSP binaries
```

`sudo apt-get install -y qairt-libs qairt-headers qairt-tools qairt-dsp-binaries` installed QAIRT 2.46.0 and pulled in `qcom-fastrpc-dev`. No Qualcomm developer account, no manual SDK download, no license click-through. That is a much better story than the usual QAIRT experience.

Libraries land in `/usr/lib`, not `/usr/lib/aarch64-linux-gnu`. Worth knowing before you go looking for them. HTP stubs shipped:

```
libQnnHtpV68Stub.so  libQnnHtpV69Stub.so  libQnnHtpV73Stub.so
libQnnHtpV75Stub.so  libQnnHtpV79Stub.so  libQnnHtpV81Stub.so
```

Plus `libQnnHtp.so`, `libQnnHtpPrepare.so`, `libQnnSystem.so`, `libQnnCpu.so`, `libQnnGpu.so`, `libQnnLpai.so`, and `libQnnGenAiTransformer.so`. That last one is interesting and I have not looked at it yet.

Tools: `qnn-net-run`, `qnn-context-binary-generator`, `qnn-context-binary-utility`, `qnn-platform-validator`, `qnn-profile-viewer`, `qnn-throughput-net-run`.

### The SoC

```
/sys/devices/soc0/soc_id    675
/sys/devices/soc0/machine   QCS8275
/sys/devices/soc0/family    Snapdragon
/proc/device-tree/model     Qualcomm Technologies, Inc. Monaco Monza addons
```

QCS8275, soc_id 675, Monaco family.

### Gate 2, the HTP architecture question

`qairt-dsp-binaries` ships cdsp skeleton sets for three platforms and QCS8275 is not one of them by name:

```
/usr/share/qcom/qcm6490/Thundercomm/RB3gen2/dsp/cdsp     V68
/usr/share/qcom/qcs8300/Qualcomm/QCS8300-RIDE/dsp/cdsp   V75
/usr/share/qcom/sa8775p/Qualcomm/SA8775P-RIDE/dsp/cdsp
```

QCS8275 and QCS8300 are both Monaco, so I pointed `ADSP_LIBRARY_PATH` at the QCS8300 set and ran the validator. It worked.

```
export ADSP_LIBRARY_PATH=/usr/share/qcom/qcs8300/Qualcomm/QCS8300-RIDE/dsp/cdsp
qnn-platform-validator --backend dsp --coreVersion --libVersion --testBackend

Backend DSP Prerequisites: Present.
Core Version of the backend DSP: Hexagon Architecture V75
Loading sample stub: libQnnHtpV75CalculatorStub.so
Successfully loaded DSP library - 'libQnnHtpV75CalculatorStub.so'
Success in executing the sum function
Unit Test on the backend DSP: Passed.
QNN is supported for backend DSP on the device.

Backend Hardware  : Supported
Backend Libraries : Found
Library Version   : Not Found
Core Version      : Hexagon Architecture V75
Unit Test         : Passed
```

That is not a capability string read out of a table. It loaded the V75 stub, pushed work across FastRPC onto the Hexagon DSP, got the right answer back, and returned. Code ran on the NPU.

So the answers we needed are now facts, not guesses:

```
htp_arch              75
ADSP_LIBRARY_PATH     /usr/share/qcom/qcs8300/Qualcomm/QCS8300-RIDE/dsp/cdsp
QAIRT version         2.46.0-0ubuntu1~bpo24.04.1
QNN stub              libQnnHtpV75Stub.so
```

"Library Version: Not Found" is benign. The validator says the fastRPC library version query "is not implemented yet". It is a gap in the tool, not a missing library.

Gate 1 is closed. Gate 2 is closed. The remaining unknown is the ONNX Runtime QNN execution provider `soc_model` value for QCS8275, which is a different identifier from `htp_arch` and still has to be determined.

Gate 3 is now the blocker, and it is narrow: no `libonnxruntime_providers_qnn.so` ships in either ONNX Runtime NuGet package for linux-arm64. The official separate QNN plugin package is the first path to test before building ONNX Runtime.

## Measured 2026-10-02, QNN plugin works and stock TinyLlama fails gate 3

The official QNN plugin saved us from building ONNX Runtime. It still has three versions that must match:

```
ONNX Runtime GenAI      0.17.1
ONNX Runtime core       1.24.4 for this test
QNN plugin              2.2.0
QAIRT                   2.46.0
```

QNN plugin 2.6.0 registers with ONNX Runtime 1.30.0, however it requires QAIRT 2.50.40. On this board it fails device creation with `QNN_DEVICE_ERROR_INVALID_CONFIG`. QNN plugin 2.2.0 is the exact QAIRT 2.46.0 match, however it's older plugin ABI does not load under ONNX Runtime 1.30.0. Pairing it with the declared ONNX Runtime 1.24.4 core works. GenAI asks for ORT API 26, then 25, then runs on API 24.

That stack loaded HTP over FastRPC, prepared a V75 graph, used four HVX threads, and returned tokens. Detailed profiling proves the only HTP partition was this:

```
/model/attn_mask_reformat/attn_mask_subgraph/Shape
/model/attn_mask_reformat/attn_mask_subgraph/Gather
/model/attn_mask_reformat/attn_mask_subgraph/Gather/Cast
```

The transformer, matmuls, attention, KV cache, and token generation stayed on CPU. The HTP graph moved 2 KB through DDR. This is technically HTP execution and practically useless.

Measured at 64 prompt tokens and 8 output tokens:

```
CPU only, earlier run       TTFT 1.696 s, decode about 23 tok/s
partial QNN partition       TTFT 4.683 s, decode 17.31 tok/s
```

The stock Arm TinyLlama CPU graph fails gate 3. The CPU fallback is unacceptable and QNN makes it slower. Stop trying to force this graph onto HTP.

The next accelerator model must be prepared for QNN or delivered as a QCS8275 QAIRT or EPContext artifact. Qwen3 1.7B W4A16 through Qualcomm's QCS8275 bundle remains the right next NPU test. Phi-4-mini stays the CPU reference until there is a QNN-specific conversion worth testing.

## Measured 2026-10-02, Qwen3 1.7B W4A16 on the VENTUNO Q

The Qualcomm AI Hub `v0.63.0` QCS8275 asset is real and public:

```
runtime       geniex_qairt
precision     w4a16
chipset       qualcomm-qcs8275
archive       1.54 GB
sha256        f97da1ab223df9ceb2c2df4f246ef576bf9045c4cb6b10d8caed8bd4356932db
```

GenieX v0.7.1 was unpacked on the board. The CLI reports its bundled QAIRT runtime as 2.45. The archive contains four context-binary weight parts, tokenizer files, `genie_config.json`, and the QCS8275 HTP configuration. The system QAIRT 2.46 installation is not needed for this bundle.

The first inference used `compute=npu`, `power-mode=high_performance`, `think=false`, and a 64-token output. The QNN logs show HTP V75 detection, QNN device creation, context-binary loading, and all four token graph partitions executing.

```
GenieX request, compute=npu   26.3 tok/s, 64 output tokens, 0.1 s first token
GenieX request, compute=cpu   20.3 tok/s, 64 output tokens, 0.1 s first token
```

Do not call the second number a clean CPU baseline yet. The asset is a compiled QAIRT bundle, and the log still showed QNN token graphs executing after `--compute cpu`. The flag did not turn this into the same model running through a separate CPU graph. The server later reported 483.87 prompt tokens/s and 27.90 predicted tokens/s for a 28-token prompt and 16-token output on the measured QNN path.

The apples-to-apples runtime comparison is now a separate gate. `onnx-community/Qwen3-1.7B-ONNX` publishes an ONNX Runtime GenAI CPU INT4 layout based on the same `Qwen/Qwen3-1.7B` model. It is not the same quantization as Qualcomm's W4A16 bundle, however it gives us the same architecture, tokenizer family, prompt, output length, and board. Benchmark that before choosing GenieX over ONNX as the default runtime.

GenieX also exposed the promised local server:

```
GET /v1/models                 passed
POST /v1/chat/completions      passed
stream=true                    passed, data:[DONE]
```

The served model id is `qualcomm/qwen3_1_7b:w4a16`, however requests must use `qualcomm/qwen3_1_7b` without the precision suffix in this build. The non-streaming response returned an empty model field and the default Qwen3 reasoning output even with a short prompt. Kare's adapter needs to normalize the model id, set the model's thinking policy, and restore the requested model name in its own response envelope.

The CLI also logs that the llama.cpp OpenCL plugin cannot load because `libOpenCL.so.1` is absent. That does not block the QNN/QAIRT path. It blocks the separate OpenCL llama.cpp plugin until the board's GPU userspace is installed, so do not call the llama.cpp GPU path proven yet.

The first quiet coding sample was coherent:

```
prompt       write IsValidMessage(string input)
output       48 tokens before the test cap
decode       23.1 tok/s
first token  0.1 s
```

The response started with a correct `string.IsNullOrWhiteSpace`-style validation method. The max-token cap cut it off before the closing code fence, so this is a smoke-quality result, not a quality verdict. The next quality pass needs a larger output cap and a fixed coding task set.

## Measured 2026-10-02, Qwen3 GenieX versus ONNX Runtime GenAI

Ran the same base model family on the same board. This closes the comparison that the earlier TinyLlama and Phi results could not close.

ONNX path:

```
source        onnx-community/Qwen3-1.7B-ONNX
layout        onnxruntime/cpu_and_mobile/cpu-int4-kld-block-128
base model    Qwen/Qwen3-1.7B
runtime       Microsoft.ML.OnnxRuntimeGenAI 0.17.1 NuGet
native ORT    packaged Linux ARM64 ORT 1.30, CPU
model size    1.409 GB
model sha256  9fddc5a0a7f9c51132c376db8fe44774b17a8e42d721c4d289ade16af87da0bd
```

GenieX path:

```
source        Qualcomm AI Hub Models 0.63.0
layout        QCS8275 geniex_qairt W4A16 context binaries
base model    Qwen/Qwen3-1.7B
runtime       GenieX 0.7.1, bundled QAIRT 2.45
device        QNN HTP V75
```

The quantization and runtime layouts differ. This is the closest maintained same-model comparison available, not a bit-identical model file comparison.

Short generated C# review prompt, 64 output tokens, one warmup and three measured ONNX iterations:

```
ONNX CPU      TTFT 1.423 s, decode 14.66 tok/s, total 5.732 s
GenieX QNN    prompt processing about 0.058 to 0.068 s on stable runs,
              decode median about 25.8 tok/s
```

Longer generated C# review prompt:

```
ONNX CPU      TTFT 5.208 s, decode 13.20 tok/s, total 10.657 s
GenieX QNN    377 prompt tokens in median 0.229 s, about 1647 prompt tok/s,
              decode median 21.39 tok/s
```

GenieX wins the measured runtime comparison. The useful difference is prompt processing, roughly 0.23 seconds versus 5.21 seconds on the longer prompt. Decode is also about 1.6 times faster. That is enough to choose GenieX QNN as the default local runtime and retain ONNX Runtime GenAI CPU as the no-NPU fallback.

Quality is not settled by speed. A five-prompt coding smoke set produced:

```
case            ONNX GenAI                         GenieX QNN
whitespace      correct                            correct
off by one      correct                            correct
cancellation    syntax error, wrong shape          wrong signature
dispose         did not dispose response            did not dispose response
null count      did not handle null                 did not handle null
```

Both paths solved two of five exact tasks. Neither is ready to generate unreviewed patches. GenieX returned results in 0.8 to 2.2 seconds after model load; ONNX took 2.5 to 10.8 seconds. Keep validation and cloud escalation in the design.

The OpenAI server path emits empty `<think></think>` tags even with `/no_think` in the system message. The ONNX chat-template path sometimes emits malformed closing fences or a stray no-think marker. Kare needs output cleanup for empty reasoning tags, but must not hide non-empty reasoning or silently rewrite code.

## Measured 2026-10-02, GenieX behind Kare

Kare now has a `GenieXBackend` behind `IChatClient`. It uses Microsoft's OpenAI protocol adapter against the loopback GenieX endpoint. It does not call OpenAI or require an OpenAI account. The configured endpoint must be loopback, the model id must omit the `:w4a16` suffix, and the backend probes `/v1/models` before it can be selected.

On the board:

```
GenieX available     Kare selected GenieXQairt at priority 100
GenieX absent        Kare selected OnnxGenAiCpu at priority 0
non-streaming        passed with usage and GenieXQairt route metadata
streaming            passed with usage, route metadata, and data: [DONE]
required tool call   passed, get_weather("Seattle")
streamed tool call   passed
tool result turn     passed
client cancellation  request stopped, /health remained responsive
```

The OpenAI adapter can report `finish_reason=tool_calls` before the streamed tool-call delta. Kare now holds terminal metadata until the final SSE chunk, so tool arguments arrive before the finish reason. This is protocol normalization, not content rewriting.
