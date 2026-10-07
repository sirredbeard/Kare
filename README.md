# Kare

Kare is a local gateway and orchestrator for GitHub Copilot. It starts on the edge, keeps useful coding context nearby, and then fans out to GitHub Copilot, Microsoft Foundry, or other configured endpoints when the task is too big, too risky, or too expensive for the local model.

Kare is built for the [Arduino VENTUNO Q](https://www.arduino.cc/product-ventuno-q), runs on Arm, and is written in the latest .NET 11 RC so the hot path stays tight. It keeps a local cache, stored skills, MCP server state, authoritative sources, and a small OpenAI-compatible `/v1` layer so GitHub Copilot CLI can talk to it over BYOK without pretending to be a full GitHub-native orchestration stack.

The routing design borrows ideas from GitHub's [Project HydraFusion](https://github.blog/ai-and-ml/github-copilot/project-hydrafusion-frontier-quality-via-multi-model-orchestration/) and my [Lerna](https://github.com/sirredbeard/Lerna) project, however Kare owns its routing, cache, policy, and OpenAI-compatible endpoint. It does not run HydraFusion or Lerna. It supports multiple endpoints, not just one provider, and it keeps the decision path explicit instead of silently billing into the cloud.

This project is still in research and development. Things I learn along the way are stored in [`findings/`](findings/), the current architecture is in [`plan.md`](plan.md), and the operational details are in [Copilot instructions](.github/copilot-instructions.md).

## Experiment

I wanted GitHub Copilot to use a small model running on hardware I own, keep useful coding context nearby, and still have a deliberate route to bigger models when the local answer is not good enough. Kare is that experiment.

## How I got here

The original idea was simple: put a small coding model on the edge, let it answer the cheap and repetitive requests, cache results that are actually safe to reuse, and send the difficult work to GitHub Copilot only when policy says it should.

The first runtime path was ONNX Runtime. It worked. Microsoft Phi-4-mini INT4 loaded on the board and generated tokens, however one measured run needed 26.408 seconds for the first token and decoded at 7.17 tokens per second. Qwen3 1.7B was much better though: 1.423 seconds to first token on the short prompt and 14.66 tokens per second.

Then I tested the same Qwen3 1.7B base model family through [Qualcomm GenieX](https://github.com/qualcomm/GenieX) on the Hexagon NPU. On the longer prompt, ONNX needed 5.208 seconds before the first token. GenieX processed the prompt in about 0.229 seconds and decoded about 1.6 times faster.

That made the runtime decision pretty easy. GenieX is the primary local path. ONNX Runtime GenAI is still implemented as a CPU fallback.

The failures shaped Kare just as much:

- The stock TinyLlama ONNX graph offloaded only a small Shape/Gather/Cast subgraph to HTP. First-token latency regressed from 1.696 seconds on CPU to 4.683 seconds, and decode fell from about 23 to 17.31 tokens per second.
- The first QAIRT Qwen bundle is fixed at 4096 tokens. Copilot CLI's starting prompt is already larger than that.
- The five-prompt coding smoke test produced two exact answers from ONNX and two from GenieX. Neither model should write unreviewed patches.
- ONNX sometimes emitted malformed fences. GenieX sometimes emitted empty reasoning tags. Kare has to normalize protocol details without quietly rewriting model output.

Kare is deliberately conservative: local first, bounded context, explicit escalation, no silent billable fallback, and no pretending a small model is a frontier coding agent.

## Design decisions

- GenieX is the default local runtime because it won the measured latency comparison on this board.
- ONNX Runtime GenAI remains the CPU fallback.
- GitHub Copilot CLI connects through BYOK to Kare's OpenAI-compatible endpoint.
- GitHub Copilot SDK sessions are a separate cloud route. Kare does not claim they are the same session as a Copilot CLI TUI session.
- Cache entries need model, prompt, repository revision, file hashes, policy, and version context. Raw prompt text is not a safe cache key.
- Tool calls remain caller-owned. The board can return a requested tool call, but it does not get permission to edit the caller's repository.
- Prompts, source code, generated responses, tokens, and provider credentials stay out of the dashboard and normal logs.

## Hardware

I am running Kare on an [Arduino VENTUNO Q](https://www.arduino.cc/product-ventuno-q) with:

- Ubuntu 24.05 on Arm
- Qualcomm Dragonwing IQ8 / QCS8275
- Eight Cortex-A55 and Cortex-A78C CPU cores
- Qualcomm Hexagon V75 NPU and Adreno 623 GPU
- 16 GB LPDDR5 memory
- 64 GB eMMC
- A 512 GB PCIe NVMe drive

The service is .NET 11. GitHub Copilot CLI runs on a number of devices in my lab, `copilot-kare` opens a protected SSH tunnel to the board, and Kare listens on loopback.

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

## NVMe storage

The board boots from eMMC, however Kare keeps models, native runtimes, logs, cached answers, authoritative sources, remote skills, and restart snapshots on a separate 512 GB NVMe drive. That keeps the write-heavy work away from the boot device and lets Kare come back warm after a restart instead of rebuilding everything from zero.

The measured layout, retention rules, and current PCIe link investigation live in [`findings/nvme-storage.md`](findings/nvme-storage.md) and [Copilot instructions](.github/copilot-instructions.md#device-storage-layout).

## What works today

- OpenAI-compatible chat completions, streaming, usage, route metadata, and tool-call metadata.
- Qwen3 through GenieX on the VENTUNO Q.
- Qwen3 and Phi-4-mini through ONNX Runtime GenAI on ARM64.
- A bounded local answer-or-route decision.
- GitHub Copilot SDK text and caller-owned tool-call routes.
- A response cache with bounded metadata.
- An operations dashboard for routes, latency, cache metadata, skills, sources, and MCP status.
- The cross-platform `copilot-kare` launcher.

The first context problem is solved. Qwen3.5 0.8B Q4_0 now runs through GenieX with a measured 24576-token window, enough for Copilot CLI 1.0.92 to load all 26 tools, repository instructions, and the builtin GitHub MCP server. The model still has to pass representative coding quality, sustained latency, and thermal checks.

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

The board runs Qwen3.5 0.8B Q4_0 through GenieX with a 24576-token context. `copilot-kare` advertises 23552 prompt tokens and reserves 1024 output tokens, which fits Copilot CLI 1.0.92 with all 26 tools, repository instructions, and the builtin GitHub MCP server enabled.

A full test prompt completed through Kare in 43 seconds with 22.9k input tokens. Kare limits the local cascade decision to the last 1024 request characters and 32 output tokens so Copilot's static wrapper does not spend two minutes in the routing gate.

Use `--kare-minimal-context` for the earlier offline diagnostic profile. It disables builtin MCP servers and repository instructions, exposes only `bash`, advertises 7936 prompt tokens, and reserves 256 output tokens.

Capture Copilot CLI debug logs in protected local storage with:

```bash
copilot-kare --kare-verbose -p "Test Kare"
```

The launcher prints the log directory. Use `--kare-log-dir PATH` when you need a specific protected location.

## Operations dashboard

Kare includes a local web dashboard on port `5285` at `/dashboard`. Open `http://127.0.0.1:5285/dashboard` on the Arduino itself, or forward port `5285` through the same SSH connection used by `copilot-kare` and open the forwarded local address in a browser.

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
