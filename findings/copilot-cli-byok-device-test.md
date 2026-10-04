# Copilot CLI BYOK against Kare on the VENTUNO Q

Measured 2026-10-02 on the board with Copilot CLI 1.0.91. The service ran from the GenieX adapter publish. The workstation reached the board over an SSH local forward, so the service kept its loopback binding and `AllowNonLoopbackBinding` stayed false.

## BYOK contract

`copilot help providers` documents the activation surface. The relevant variables are:

- `COPILOT_PROVIDER_BASE_URL` activates BYOK and must point at Kare's `/v1`.
- `COPILOT_PROVIDER_TYPE=openai` covers any OpenAI-compatible endpoint.
- `COPILOT_PROVIDER_WIRE_API=completions` is the default and is what Kare serves.
- `COPILOT_PROVIDER_API_KEY` carries the shared secret.
- `COPILOT_PROVIDER_WIRE_MODEL` is the name sent on the wire. `COPILOT_PROVIDER_MODEL_ID` is a catalogue name used only for agent configuration and token limits.
- `COPILOT_PROVIDER_MAX_PROMPT_TOKENS` and `COPILOT_PROVIDER_MAX_OUTPUT_TOKENS` override the catalogue limits.

GitHub authentication is not required once a custom provider is configured.

The provider config can also live in a `providers.json` file resolved from `COPILOT_PROVIDERS_CONFIG` or the CLI config directory. The environment path is what was tested.

## What passed

Both local backends passed the smoke suite on the board: health, model listing, empty-messages rejection, non-streaming completion, and streaming completion, each with route metadata attached.

Bounds and admission control behaved exactly as configured:

- An oversized prompt returned 413 with `prompt_too_large` and named both the actual and the configured character count.
- With `MaxConcurrentInference` 1 and `MaxQueueDepth` 8, fourteen concurrent requests produced nine 200 responses and five 429 responses with `capacity_exhausted`. Requests serialised in arrival order.
- Malformed JSON returned 400.
- With an API key set, `/v1/chat/completions` and `/v1/models` returned 401 with no key and with a wrong key, and 200 with the correct key. `/health` stayed open.

GenieX tool calling passed end to end. A `tool_choice: required` request produced a well-formed `tool_calls` entry with `finish_reason` `tool_calls`, the streaming form emitted the same call in a delta, and a follow-up turn carrying an assistant tool call plus a `tool` result message produced a correct final answer.

Copilot CLI reached Kare and returned a real completion. The CPU backend served the turn and the route record shows `LocalSlm` and `OnnxGenAiCpu`.

## What blocks the Copilot CLI route

Neither backend currently serves Copilot CLI usefully, for different reasons.

GenieX Qwen3 1.7B W4A16 has a context of 4096 tokens. `genie_config.json` reports `"context": {"size": 4096}`. Copilot CLI rejected the session before sending anything with: static system messages and tool definitions exceed the model's usable context budget. Copilot CLI's static prompt plus tool definitions alone measured about 4.8k tokens in the runs that did go through. The fast backend with working tool calling cannot hold the client's floor.

Phi-4-mini INT4 has a context of 131072, so Copilot CLI starts cleanly against the ONNX CPU backend. But `OnnxGenAiBackend` never reads `ChatOptions.Tools`. The service translates OpenAI tools into `ChatOptions.Tools` correctly in `OpenAiTranslator`, and then the backend drops them. Asked to read a file in the working directory, the model invented the contents instead of emitting a tool call. There was no tool call in the response and no tool activity in the route log. This is a silent wrong answer, not an error.

GenieX delegates to an OpenAI `IChatClient`, which is why tool calling works there and not on the ONNX path.

Phi-4-mini on CPU is too slow for an interactive client. Two Copilot CLI turns at roughly a 4.9k token prompt recorded first-token latency of 202512 ms and 193688 ms, with total latency of 205847 ms and 207583 ms for 12 and 50 output tokens. GenieX on the same board answered small prompts at 99 ms to 183 ms to first token and about 23 tokens per second.

Qwen3 through GenieX emits `<think>` blocks into `content`. On short output budgets the reasoning block consumes the whole allowance and the answer never arrives. A thinking-mode control or a content filter is needed before this model faces a client.

## Other defects found

The service aborts at startup with `OptionsValidationException: ModelPath: The ModelPath field is required` when GenieX is the selected backend and the ONNX model path is unset. `OnnxGenAiBackend` is constructed eagerly by dependency injection during backend selection, so an unconfigured fallback stops the service even though the chosen backend is fully configured.

Lerna and HydraFusion intercept BYOK sessions. With the normal workstation configuration the first BYOK run was routed by HydraFusion and returned no response. Running with an isolated `COPILOT_HOME` produced a clean BYOK session. This is direct evidence for the existing position that BYOK and native HydraFusion are separate paths.

## Open questions

Whether a larger-context model exists in the Qualcomm QCS8275 GenieX catalogue, and what context size the QAIRT bundles support. Unknown.

Whether Copilot CLI's static context floor can be reduced enough to matter. The measured floor of roughly 4.8k tokens already exceeds the 4096-token NPU model, so a small reduction does not help.

Whether ONNX Runtime GenAI can drive tool calling for Phi-4-mini through a chat template, or whether the adapter must format tool declarations and parse tool calls itself. Unknown and untested.

## Resolution, 2026-10-03

The two service defects are fixed in the working tree:

- `OnnxGenAiOptions.ModelPath` may be empty. The backend probe reports the fallback unavailable, so a configured GenieX backend can still start.
- `OnnxGenAiBackend` rejects requests carrying tools with HTTP 503 and `unsupported_backend_capability`. Microsoft now documents that the current ONNX .NET client ignores function tools. Kare will not silently return a success-shaped answer after dropping them.

Manual tool-schema prompt formatting and output parsing were not added. That would create a Kare-specific protocol on top of a backend that is already too slow for Copilot CLI, and it would still need model-specific constrained decoding and device validation.

Qualcomm documents that QAIRT context size is compiled into the model bundle. `--nctx` cannot raise it. `--sliding-window` evicts old conversation tokens but cannot make Copilot CLI's initial prompt fit. No public QCS8275 QAIRT bundle with more than 4096 tokens was verified on 2026-10-03.

GenieX documents a different path for GGUF models: llama.cpp can raise `--nctx` up to the model's trained maximum and can target the NPU. The next device experiment is therefore a small supported GGUF model with an 8192-token window. It must pass required tool calls, streaming tool calls, the five coding tasks, first-token latency, memory, and thermals before Kare changes its default.

The installed GenieX 0.7.1 CLI exposes `--nctx`, `--compute npu`, `--think=false`, and QAIRT `--sliding-window`, so this path is available in the version already on the board. The llama.cpp plugin initially failed to load because `libOpenCL.so.1` was absent. Installing Ubuntu's `ocl-icd-libopencl1` package removed that loader error. This only makes the backend loadable; it is not evidence that the next model is fast or correct.

The CPU governor finding was deployment drift. The repository unit ordered itself after `sysfsutils.service`, but the older installed copy did not. `sysfsutils` reset two policies to `schedutil` after Kare's oneshot service exited. Reapplying the current host-optimization script installed the corrected unit. All three policies now report `performance`, sleep targets are masked, and the script now fails if any policy is not set correctly after restart.

Sources:

- https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/model-providers/onnx
- https://geniex.aihub.qualcomm.com/en/run/cli/reference

## Routed gateway progress, 2026-10-03

The GGUF path cleared the passive-local gate. Qwen3.5 0.8B Q4_0 ran through GenieX on the NPU with `--nctx 8192`. A one-token smoke request reached first token in about 0.4 seconds, and the direct GenieX OpenAI-compatible endpoint emitted a valid tool-call SSE. Kare's basic text, streaming, authentication, and cache paths pass on the board, but the Kare-to-GenieX tool-call translation still needs fixing. Kare should remain a conduit: the local model assists with bounded cache, context, and skill maintenance and must not execute caller tools or silently change routes. This is not yet a representative coding-quality or sustained-load benchmark.

Kare now has explicit wire routes:

- `kare-local` for local Qwen through GenieX
- `kare-fast` for an ordered low-cost Copilot model pool
- `kare-copilot` for the configured heavy Copilot model
- `kare-complex` for a matching configured Foundry deployment
- `kare-auto` for deterministic tier selection, with automatic billable escalation disabled by default

The selector measures request characters, message count, and tool declarations without making a model call. It rewrites the public Kare alias to the selected concrete provider model before dispatch. Foundry deployment URLs and key environment-variable names are administrator configuration and remain outside the repository. This is Kare-owned HydraFusion-inspired routing and Lerna-inspired provider mapping, not GitHub's native HydraFusion implementation or Lerna.

The GitHub Copilot SDK `1.0.16` adapter passed a live GitHub `auto` request with a 30-credit session ceiling. It runs in `CopilotClientMode.Empty`, disables ambient host capabilities, and creates temporary isolated sessions.

Cloud caller-tool forwarding also passed. Kare registers caller tools as declaration-only tools. SDK `1.0.16` did not emit `external_tool.requested` in this test; it emitted the complete call at `tool.execution_start` and then attempted to resolve the declaration itself. Kare now intercepts `tool.execution_start` only for the caller tools it registered and returns the function name, call ID, and arguments without executing the tool on the gateway.

Cloud text streaming now forwards SDK `assistant.message_delta` events. A live probe returned `STREAM` across three response updates. ARM64 Native AOT packaging and live board execution of the bundled Copilot runtime remain deployment gates.
