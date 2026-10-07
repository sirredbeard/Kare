# Kare

Kare is a local gate for GitHub Copilot CLI BYOK. It keeps the device-side config outside the repo, exposes a small OpenAI-compatible `/v1` surface, and routes requests through a local model first before it considers a cloud or signed-in path.

I wanted GitHub Copilot to use a small model running on hardware I own, keep useful coding context nearby, and still have a deliberate route to bigger models when the local answer is not good enough. Kare is that experiment.

The routing design borrows ideas from GitHub's [Project HydraFusion](https://github.blog/ai-and-ml/github-copilot/project-hydrafusion-frontier-quality-via-multi-model-orchestration/) and my [Lerna](https://github.com/sirredbeard/Lerna) project, however Kare owns it's routing, cache, policy, and OpenAI-compatible endpoint. It does not run HydraFusion or Lerna.

This project is still in research and development. The complete measurements are in [`findings/`](findings/), the current architecture is in [`plan.md`](plan.md), and the operational details are in [Copilot instructions](.github/copilot-instructions.md).

## How I got here

The original idea was simple: put a small coding model on the edge, let it answer the cheap and repetitive requests, cache results that are actually safe to reuse, and send the difficult work to GitHub Copilot only when policy says it should.

The first runtime path was ONNX Runtime GenAI because it has a real .NET API, supports Linux ARM64, and keeps the service behind `IChatClient`. It worked. Microsoft Phi-4-mini INT4 loaded on the board and generated tokens, however one measured run needed 26.408 seconds for the first token and decoded at 7.17 tokens per second. Qwen3 1.7B was much better: 1.423 seconds to first token on the short prompt and 14.66 tokens per second.

Then I tested the same Qwen3 1.7B base model family through [Qualcomm GenieX](https://github.com/qualcomm/GenieX) on the Hexagon NPU. On the longer prompt, ONNX needed 5.208 seconds before the first token. GenieX processed the prompt in about 0.229 seconds and decoded about 1.6 times faster.

That made the runtime decision pretty easy. GenieX is the primary local path. ONNX Runtime GenAI stays as the CPU fallback because a local service should remain useful when the accelerator runtime is missing.

The failures shaped Kare just as much:

- The stock TinyLlama ONNX graph offloaded only a small Shape/Gather/Cast subgraph to HTP. First-token latency regressed from 1.696 seconds on CPU to 4.683 seconds, and decode fell from about 23 to 17.31 tokens per second.
- The first QAIRT Qwen bundle is fixed at 4096 tokens. Copilot CLI's starting prompt is already larger than that.
- The five-prompt coding smoke test produced two exact answers from ONNX and two from GenieX. Neither model should write unreviewed patches.
- ONNX sometimes emitted malformed fences. GenieX sometimes emitted empty reasoning tags. Kare has to normalize protocol details without quietly rewriting model output.
- Copilot CLI extensions can add tools and commands, but the documented extension API cannot replace the model transport. BYOK is the supported route.

So Kare is deliberately conservative: local first, bounded context, explicit escalation, no silent billable fallback, and no pretending a small model is a frontier coding agent.

## Design decisions

- `Microsoft.Extensions.AI` and `IChatClient` are the service boundary. Native runtimes stay behind adapters.
- GenieX is the default local runtime because it won the measured latency comparison on this board.
- ONNX Runtime GenAI remains the CPU fallback because fallback is a normal operating mode, not an error.
- GitHub Copilot CLI connects through BYOK to Kare's OpenAI-compatible endpoint.
- GitHub Copilot SDK sessions are a separate cloud route. Kare does not claim they are the same session as a Copilot CLI TUI session.
- Cache entries need model, prompt, repository revision, file hashes, policy, and version context. Raw prompt text is not a safe cache key.
- Tool calls remain caller-owned. The board can return a requested tool call, but it does not get permission to edit the caller's repository.
- Prompts, source code, generated responses, tokens, and provider credentials stay out of the dashboard and normal logs.

## Hardware

I am running Kare on an [Arduino VENTUNO Q](https://www.arduino.cc/product-ventuno-q) with:

- Ubuntu 24.04.5 on ARM64.
- Qualcomm Dragonwing IQ8 / QCS8275.
- Eight Cortex-A55 and Cortex-A78C CPU cores.
- Qualcomm Hexagon V75 NPU and Adreno 623 GPU.
- 16 GB LPDDR5 memory.
- 64 GB eMMC.
- An M.2 NVMe slot for models, indexes, logs, and cache data.

The service is .NET 11. GitHub Copilot CLI runs on the workstation, `copilot-kare` opens a protected SSH tunnel to the board, and Kare listens on loopback.

```text
GitHub Copilot CLI
        |
        | BYOK, OpenAI-compatible API
        v
Kare on the VENTUNO Q
        |
        +-- bounded cache and context
        +-- Qwen through GenieX
        +-- ONNX CPU fallback
        +-- explicit GitHub Copilot or configured cloud route
```

## What works today

- OpenAI-compatible chat completions, streaming, usage, route metadata, and tool-call metadata.
- Qwen3 through GenieX on the VENTUNO Q.
- Qwen3 and Phi-4-mini through ONNX Runtime GenAI on ARM64.
- A bounded local answer-or-route decision.
- GitHub Copilot SDK text and caller-owned tool-call routes.
- A response cache with bounded metadata.
- An operations dashboard for routes, latency, cache metadata, skills, sources, and MCP status.
- The cross-platform `copilot-kare` launcher.

The main open problem is context. The next device experiment is a small GenieX GGUF model with an 8192-token context. It still has to pass tool-call, quality, latency, memory, and thermal checks.

## Build the server for the Arduino

This is the setup I use today. It is a development setup, not an installer.

On the VENTUNO Q:

1. Install Git.
2. Clone Kare.
3. Install the .NET 11 SDK into `.dotnet` inside the checkout.
4. Put service settings outside the repository and create the `kare.service` user unit described in [Copilot instructions](.github/copilot-instructions.md#device-iteration).
5. Publish, test, deploy, restart, and health-check the service:

```bash
git clone https://github.com/sirredbeard/Kare.git
cd Kare
./build/device-publish.sh --test
```

The script creates a commit-specific release, switches the `current` symlink, restarts the user service, and waits for `/health`.

## Build `copilot-kare`

The launcher is a trimmed, compressed, self-contained .NET 11 application. Build it on a workstation with the .NET 11 SDK:

```bash
dotnet publish src/Kare.CopilotLauncher/Kare.CopilotLauncher.csproj \
  -c Release \
  -r linux-x64 \
  -o artifacts/copilot-kare-linux-x64
```

Supported runtime identifiers:

- `linux-x64`
- `linux-arm64`
- `win-x64`
- `win-arm64`
- `osx-x64`
- `osx-arm64`

Replace `linux-x64` in the publish command with the target runtime identifier. The Windows output is `copilot-kare.exe`; Linux and macOS use `copilot-kare`.

Put the device connection and API key in the external config described in [Copilot instructions](.github/copilot-instructions.md#protected-configuration). Do not put them in this repository.

Start GitHub Copilot through Kare with the device address:

```bash
./artifacts/copilot-kare-linux-x64/copilot-kare YOUR_DEVICE_ADDRESS -i "Review this repository"
```

After the first healthy connection, the launcher remembers the last working address:

```bash
./artifacts/copilot-kare-linux-x64/copilot-kare -i "Review this repository"
```

`copilot-kare` opens the SSH tunnel, waits for Kare, supplies the Copilot BYOK environment, starts GitHub Copilot CLI, and cleans up the tunnel when Copilot exits.

## Operations dashboard

Kare includes a local dashboard at `/dashboard`. Open `http://127.0.0.1:5285/dashboard` on the Arduino itself, or forward port `5285` through the same SSH connection used by `copilot-kare` and open the forwarded local address in a browser.

The dashboard stores operational metadata, not prompts, source code, generated responses, tokens, or protected config. The access rules and feature list are in [Copilot instructions](.github/copilot-instructions.md#operations-dashboard).

## Related projects

- [Arduino VENTUNO Q](https://www.arduino.cc/product-ventuno-q)
- [Project HydraFusion](https://github.blog/ai-and-ml/github-copilot/project-hydrafusion-frontier-quality-via-multi-model-orchestration/)
- [Lerna](https://github.com/sirredbeard/Lerna)
- [GitHub Copilot CLI](https://github.com/github/copilot-cli)
- [GitHub Copilot SDK](https://github.com/github/copilot-sdk)
- [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai)
- [Qualcomm GenieX](https://github.com/qualcomm/GenieX)
- [ONNX Runtime GenAI](https://github.com/microsoft/onnxruntime-genai)
- [Qualcomm AI Hub](https://aihub.qualcomm.com/)
