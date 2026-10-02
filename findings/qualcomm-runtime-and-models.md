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

Gate 3 is now the blocker, and it is narrow: no `libonnxruntime_providers_qnn.so` ships in either ONNX Runtime NuGet package for linux-arm64, so we have to build ONNX Runtime with the QNN execution provider ourselves against this QAIRT install. The headers are already on the board from `qairt-headers`.
