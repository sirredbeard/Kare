# Kare

Kare is an authenticated OpenAI-compatible conduit for coding work. It is intended to run on an Arduino VENTUNO Q, use Qwen through GenieX as its default local sidecar, cache safe deterministic results, maintain local skills and context, and route requests through GitHub Copilot or Microsoft Foundry. Kare owns a deterministic HydraFusion-inspired tier selector and Lerna-inspired Foundry deployment mapping. It does not claim to run GitHub's native HydraFusion implementation or Lerna itself.

The project is in the early build stages. The research in `plan.md` still drives the design, and the first service skeleton, inference adapter, device probe, and build tooling now exist.

## Contents

- `plan.md` - research, architecture, risks, and staged build plan
- `findings/` - low-format research notes, measurements, and source links
- `src/Kare.Abstractions` - route, backend, and recording contracts
- `src/Kare.Core` - bounds, admission control, route recording, route selection
- `src/Kare.Inference.GenieX` - QCS8275 GenieX QAIRT adapter behind `IChatClient`
- `src/Kare.Inference.OnnxGenAI` - ONNX Runtime GenAI adapter behind `IChatClient`
- `src/Kare.Service` - OpenAI-compatible HTTP endpoint for Copilot CLI BYOK
- `bench/Kare.DeviceProbe` - `kare-probe`, the device capability and benchmark tool
- `build/` - pinned ARM64 build container and publish script
- `tests/Kare.Tests` - unit tests
- `.github/copilot-instructions.md` - instructions for future Copilot sessions
- `examples/KARE_CONFIG_FILE.example.json` - repository-safe external cloud catalog example

## Target

The target device is an Arduino VENTUNO Q running Ubuntu 24.04.5 LTS on aarch64. The board has a Qualcomm Dragonwing IQ8 / QCS8275 platform, 16 GB LPDDR5 memory, 64 GB eMMC storage, a Hexagon NPU, an Adreno GPU, and an M.2 NVMe slot.

## Protected configuration

Repository defaults keep cloud routing disabled and define no cloud model catalog. Set `KARE_CONFIG_FILE` to an absolute path for a protected JSON override. The schema is demonstrated in `examples/KARE_CONFIG_FILE.example.json`. Copy it to a protected location, replace the placeholders, and do not commit the copy.

The catalog keeps each public model ID separate from its provider wire deployment. GitHub Copilot routes use the signed-in Copilot account. Microsoft Foundry routes support the Responses and Anthropic Messages wires and may use a scoped Azure CLI bearer token. Kare does not run `az` for every model call. The Copilot SDK can request a token before a provider request, but Kare reuses the in-memory token for its remaining lifetime and invokes `az account get-access-token` only on a cold cache or when the token is within its two-minute refresh window. Keep the configuration file, Azure CLI state, endpoints, deployments, and credentials outside the repository with restrictive permissions.

Refresh the catalog from the account, not from the public model list alone. Use `az cognitiveservices account list-models` to find model definitions and `az cognitiveservices account deployment list` to find routes that can be tested immediately. Keep new routes explicit and out of automatic selection until streaming, cancellation, usage, tool behavior, latency, and cost have been measured. Availability is not proof that a model is cheaper.

## Host Copilot CLI through Kare

When Kare is loopback-only on the device, open the protected SSH tunnel with `build/tunnel.sh`. The host launcher at `~/.config/kare/copilot-byok.sh` then exports the documented Copilot CLI OpenAI-compatible provider variables and starts Copilot without changing the host's normal Copilot or Lerna settings:

```bash
build/tunnel.sh
~/.config/kare/copilot-byok.sh
```

The launcher reads `~/.config/kare/device.env`, which must remain mode `600`. Kare must support streaming and tool calls for full Copilot CLI compatibility. The current local GenieX route is suitable for basic text validation, but structured local tool calls remain an open limitation.

## Device iteration

Use framework-dependent JIT builds for normal development on the VENTUNO Q:

```bash
~/.local/bin/kare-sync
cd ~/Kare
build/device-publish.sh --jit --test
```

The device service must have `DOTNET_ROOT` set to the project-local `.NET 11` installation, normally `~/Kare/.dotnet`. The board's global .NET 10 runtime cannot run Kare's `net11.0` build.

Native AOT remains supported, but it is a release validation path rather than the normal edit and test loop:

```bash
build/device-publish.sh --aot --test
```

The JIT and AOT modes deploy to separate versioned release directories and update the same `~/kare/service/current` symlink atomically.


## Related projects

- [Arduino VENTUNO Q](https://www.arduino.cc/product-ventuno-q)
- [Arduino VENTUNO Q documentation](https://docs.arduino.cc/hardware/ventuno-q/)
- [ONNX Runtime GenAI](https://github.com/microsoft/onnxruntime-genai)
- [GitHub Copilot SDK](https://github.com/github/copilot-sdk)
- [Lerna](https://github.com/sirredbeard/Lerna)
- [Project HydraFusion](https://github.blog/ai-and-ml/github-copilot/project-hydrafusion-frontier-quality-via-multi-model-orchestration/)
- [Qualcomm AI Hub](https://aihub.qualcomm.com/)