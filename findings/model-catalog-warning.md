# Model catalog ownership warning

Checked 2026-10-25 against the GitHub BYOK documentation and the launcher code in this repo.

## The warning

Copilot CLI prints this line when it starts through `copilot-kare`:

```text
Model catalog ownership cannot be verified for the active credentials; omitting model ID completions
```

## What it means

The warning is about model ID completions in the CLI, not about the request path.

Copilot CLI cannot verify that the active credentials own the model catalog behind a BYOK provider. Kare is a BYOK provider, so the check has nothing to verify against. The CLI drops model ID completions and keeps working. Requests still route through Kare. Answers, tool calls, and streaming are not affected. I have not seen a case where this warning changed a result.

## What I checked

- The GitHub BYOK documentation (Using your own LLM models in GitHub Copilot CLI) lists environment variables for the provider type, base URL, API key, and model. It does not list a setting, a flag, or an environment variable that turns this warning off.
- The launcher sets the documented variables only. It does not set anything that claims catalog ownership.
- Kare cannot fix this from the server side. The check runs in the CLI before the first request, and the `/v1/models` response does not carry an ownership proof the CLI would accept.

## Options I considered

1. Leave it. The warning is one line at startup and the cost is zero. This is my pick for now.
2. Filter the line out of the CLI output in the launcher. I do not want to do this. The launcher would have to sit between the user and every line of CLI output, and the next real warning would get filtered by the same code path sooner or later.
3. Wait for a CLI setting. If GitHub adds one, the launcher can set it in `StartCopilot` next to the other provider values.

## Status

No code change. Issue #3 stays open as a watch item, not a bug I can fix in this repo. Re-check the BYOK documentation when the CLI version changes.
