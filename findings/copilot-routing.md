# Copilot routing

Checked 2026-10-01 against Copilot CLI 1.0.91 documentation and current GitHub docs.

CLI extensions add tools and slash commands. They run as separate Node.js processes. They do not expose a model transport replacement hook.

CLI hooks include session, prompt, tool, error, and stop events. Prompt hooks can inspect or modify the prompt. Tool hooks can approve, deny, or change arguments.

The Copilot SDK adds `userPromptTransformed`. It runs after generated context is added and before the prompt is persisted or sent to the model. It can replace the model-facing prompt.

No documented extension or hook returns an alternate streamed model response for an existing Copilot TUI turn.

The supported provider redirection point is BYOK:

`COPILOT_PROVIDER_BASE_URL` points Copilot CLI to Kare.

The endpoint must support streaming and tool calling.

BYOK and native HydraFusion are separate provider paths. Do not claim a BYOK session keeps HydraFusion.

The SDK uses the Copilot CLI runtime over JSON-RPC. The .NET package bundles the runtime.

The SDK exposes streaming, permission handlers, prompt and tool hooks, session IDs, resume, cancellation, custom tools, and session storage.

Kare should own the local session and map cloud escalations to a Copilot SDK session. A local BYOK TUI session and a separate SDK session are not automatically one session.

Use hooks for redaction, policy, route metadata, cache fingerprints, and accounting. Use BYOK for provider redirection.

Sources:

https://docs.github.com/en/copilot/concepts/agents/copilot-cli/about-cli-extensions

https://docs.github.com/en/copilot/reference/hooks-reference

https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-byok-models

https://github.com/github/copilot-sdk

https://github.com/github/copilot-sdk/blob/main/docs/hooks/user-prompt-transformed.md

https://github.com/github/copilot-sdk/blob/main/dotnet/README.md

## Built 2026-10-02, the BYOK endpoint

Implemented the OpenAI-compatible surface in `src/Kare.Service` because BYOK is the only documented way to send Copilot CLI model calls through Kare. Notes from doing it rather than reading about it.

The endpoint is `POST /v1/chat/completions` plus `GET /v1/models` and `GET /health`. Streaming is server sent events terminated with `data: [DONE]`. `X-Accel-Buffering: no` is set because buffering a token stream defeats the point.

Message content is a union. OpenAI allows a bare string or an array of typed parts, and Copilot CLI sends the array form. Kare flattens text parts and throws on any other part type. Silently dropping an image part would mean sending the model a prompt the caller did not write, which is worse than a 400.

`stop` is the same kind of union, string or array. Handled with its own converter.

Tools are declared but never executed. Kare subclasses `AIFunctionDeclaration`, which is abstract and public in `Microsoft.Extensions.AI` 10.10, to pass a function name, description, and JSON schema down to the model without giving the gateway anything to invoke. The caller owns the tools, asks the user for permission, and runs them. Executing a caller's tool inside Kare would move a permission decision off the machine that made it. There is a test asserting the declaration is not an `AIFunction`.

Tool arguments are serialized by hand against `Utf8JsonWriter`. Reflection based serialization is off for Native AOT, so the writer handles `JsonElement`, strings, bools, and numbers, and throws `NotSupportedException` on anything else. A mangled tool argument is worse than a failed request.

Finish reason: a model that emitted tool calls and then stopped is reported as `tool_calls`, not `stop`, because that is what an OpenAI client keys on to decide whether to run the tools.

Every response carries a non-standard `kare_route` object with route, backend, reason, billable, and fell_back_from. Streamed responses get it as a final chunk. This is deliberate. A local answer and a billable cloud answer must never be indistinguishable to the caller.

Routing policy today is `LocalOnlyRouteSelector`, which sends everything local and is honest about it. There is no measured basis yet for deciding what deserves a billable cloud call, and guessing would spend Copilot credits for nothing. Cloud escalation becomes a separate selector once the board numbers exist.

Binding is guarded in code. Kare refuses to start on a non-loopback address unless `AllowNonLoopbackBinding` is set, and refuses a non-loopback bind with an empty API key. The key check is `CryptographicOperations.FixedTimeEquals`.

`MinResponseDataRate` is set to null on Kestrel. A local model on a cold cache can take a long time to produce the first token, and the default minimum response rate would abort exactly the requests this project exists to serve.

Errors map to OpenAI shaped bodies: 413 prompt_too_large, 429 capacity_exhausted with Retry-After, 503 no_backend. If the response has already started, the connection is aborted instead of appending a success shaped body to a partial stream.

## Measured 2026-10-02, the BYOK endpoint actually serves

Started `src/Kare.Service` on loopback with the Arm TinyLlama model and ran `build/smoke-service.sh`. Everything passed.

Non-streaming:

```
{"id":"chatcmpl-...","object":"chat.completion","created":1790912878,"model":"kare-local",
 "choices":[{"index":0,"message":{"role":"assistant","content":"[Single word: ready] ..."},
 "finish_reason":"length"}],
 "usage":{"prompt_tokens":30,"completion_tokens":16,"total_tokens":46},
 "kare_route":{"route":"LocalSlm","backend":"OnnxGenAiCpu",
   "reason":"Local only policy. Cloud escalation is not configured.","billable":false}}
```

Streaming produced `chat.completion.chunk` frames, a final usage chunk with `finish_reason: "stop"`, then the `kare_route` trailer chunk, then `data: [DONE]`. The trailer is non-standard and a strict client will ignore the unknown key, which is the behavior I want. The route is always disclosed and never inferred.

`GET /v1/models` returns the configured `ModelId`, not the model directory name. That matters because the BYOK client sends back whatever id it was given.

An empty `messages` array returns 400. Good, because a client that sends no messages has a bug and silently generating from nothing would hide it.

Note on configuration. `ModelPath` is resolved against the content root, not the working directory, so a relative path from the repo root fails when you run with `dotnet run --project`. Use an absolute path, or set it per deployment. The failure is loud:

```
Kare.Core.NoBackendAvailableException: No local inference backend is available.
  OnnxGenAiCpu: Model directory does not exist: models/tinyllama-arm-int4
```

That is the correct behavior. The listener never opens.
