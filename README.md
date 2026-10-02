# Kare

Kare is a local AI gateway for coding work. It is intended to run on an Arduino VENTUNO Q, serve a responsive local small language model, cache useful context, share skills and code knowledge, and route selected requests through GitHub Copilot or Microsoft Foundry.

The project is in the early build stages. The research in `plan.md` still drives the design, and the first service skeleton, inference adapter, device probe, and build tooling now exist.

## Contents

- `plan.md` - research, architecture, risks, and staged build plan
- `findings/` - low-format research notes, measurements, and source links
- `src/Kare.Abstractions` - route, backend, and recording contracts
- `src/Kare.Core` - bounds, admission control, route recording, route selection
- `src/Kare.Inference.OnnxGenAI` - ONNX Runtime GenAI adapter behind `IChatClient`
- `src/Kare.Service` - OpenAI-compatible HTTP endpoint for Copilot CLI BYOK
- `bench/Kare.DeviceProbe` - `kare-probe`, the device capability and benchmark tool
- `build/` - pinned ARM64 build container and publish script
- `tests/Kare.Tests` - unit tests
- `.github/copilot-instructions.md` - instructions for future Copilot sessions

## Target

The target device is an Arduino VENTUNO Q running Ubuntu 24.04.5 LTS on aarch64. The board has a Qualcomm Dragonwing IQ8 / QCS8275 platform, 16 GB LPDDR5 memory, 64 GB eMMC storage, a Hexagon NPU, an Adreno GPU, and an M.2 NVMe slot.


## Related projects

- [Arduino VENTUNO Q](https://www.arduino.cc/product-ventuno-q)
- [Arduino VENTUNO Q documentation](https://docs.arduino.cc/hardware/ventuno-q/)
- [ONNX Runtime GenAI](https://github.com/microsoft/onnxruntime-genai)
- [GitHub Copilot SDK](https://github.com/github/copilot-sdk)
- [Lerna](https://github.com/sirredbeard/Lerna)
- [Project HydraFusion](https://github.blog/ai-and-ml/github-copilot/project-hydrafusion-frontier-quality-via-multi-model-orchestration/)
- [Qualcomm AI Hub](https://aihub.qualcomm.com/)