# Frontier model refresh

Rechecked 2026-10-25 against the GitHub changelog, the Microsoft Foundry announcements, and the Claude in Foundry model list. Model IDs and prices only. No endpoints, keys, or account details.

## What the frontier looks like now

GitHub's HydraFusion pool changed on 2026-10-14. The pool is now:

- `claude-opus-5-5` (Anthropic, in Copilot and in Foundry)
- `gpt-6-astra` (OpenAI flagship, $10 in / $50 out per million)
- `gpt-6.1-sol` (OpenAI, $2 / $10, cached input $0.10)
- `gpt-6-luna` (OpenAI, $0.10 / $0.50)

A second tier, HydraFusion Max, uses the same pool at higher reasoning effort. Cascade can now add a judging step that requests a repair, and Critique runs parallel critics that inspect the workspace with tools. Drafts run at medium effort and reviews at high effort. Kare's cascade shape (local draft, judge, repair, parallel critique) still matches that design. The flags stay off by default in `appsettings.json`, same posture as before.

The rest of the Copilot picker moved too:

- Claude Haiku 5.5 (`claude-haiku-5-5`) shipped 2026-10-05. Claude Sonnet 5.5 (`claude-sonnet-5-5`, $2 / $10) shipped 2026-09-28. Both are in the Foundry catalog now, hosted on Azure or on Anthropic.
- Gemini 4 Argon reached GA in Copilot on 2026-10-13. It is a Copilot-side model. Foundry does not carry it, so Kare reaches it only through the Copilot SDK route.
- GPT-6 Astra, GPT-6.1 Sol, and GPT-6 Luna are GA in Microsoft Foundry with context windows around 1.05 million tokens. Claude Opus 5.5 is in Foundry as well.

Retirements that touch the catalog:

- 2026-11-16: Copilot removes Claude Opus 5, Claude Opus 4.8, and GPT-5.6 Terra from all Copilot experiences. GitHub points Terra users at GPT-6.1 Sol or GPT-6 Luna, and Opus users at Opus 5.5.
- The 2026-10-19 batch already left: GPT-5.5, GPT-5.4 and its mini, GPT-5 mini, Gemini 3.7 Flash, Grok 4.5.

## Where the Kare catalog stands

The protected catalog still targets the GPT-5.6 and Claude 5 generation. Catalog entries, from `plan.md` and the device catalog (IDs only):

| Route | Catalog now | Frontier equivalent | State |
| --- | --- | --- | --- |
| Foundry top (Heavy, priority 1000) | `gpt-5.6-sol` | `gpt-6.1-sol` | One generation behind |
| Foundry Heavy | `gpt-5.6-luna` | `gpt-6-luna` | One generation behind, 20x the price of Luna |
| Foundry Fast | `gpt-5.6-terra` | `gpt-6-luna` or `gpt-6.1-sol` | Terra stays on Foundry. The retirement is Copilot-side. |
| Foundry Complex | `claude-opus-5` | `claude-opus-5-5` | One generation behind |
| Foundry Complex | `claude-sonnet-4-6` | `claude-sonnet-5-5` | Two point releases behind |
| Copilot Fast | `gpt-5.6-terra` | `gpt-6-luna` | Dead on Copilot after 2026-11-16 |
| Copilot Fast | `claude-haiku-4.5` | `claude-haiku-5-5` | One generation behind |
| Copilot Fast | `mai-code-1.1-flash` | (current) | Current. No change. |
| Copilot Heavy | `auto` | (current) | Current. `auto` follows GitHub's pool on its own. |

So no, the catalog does not match the new pool yet. The gap is configuration, not code: Kare reads its models from the protected catalog, and nothing in the router hardcodes a frontier model ID.

## What has to happen, in order

1. Create the Foundry deployments: `gpt-6.1-sol`, `gpt-6-luna`, `claude-opus-5-5`, `claude-sonnet-5-5`. Add `gpt-6-astra` only if the Max-style top tier is worth $10 / $50 per million. Confirm each deployment name in the portal at creation time. Foundry names usually match the model ID, and a custom name is fine as long as the catalog `WireModel` matches it.
2. Swap the protected catalog entries to the new deployment names and keep the tier shape: Luna takes the cheap Heavy and Fast slots, Sol takes the top slot, Opus 5.5 and Sonnet 5.5 take Complex. The catalog file lives in protected device configuration. It is never committed.
3. Drop the Copilot `gpt-5.6-terra` entry before 2026-11-16, or let it fail over to `auto` after that date. Point the Copilot Haiku entry at `claude-haiku-5-5`.
4. Restart `kare.service` and run `build/device-geniex-guard.sh` plus one bounded request per swapped route.

I could not verify from the device which deployments exist in the Foundry account today: the Azure CLI on the device is not signed in, and Kare reaches Foundry with a runtime token, not the CLI. Step 1 starts in the portal or after an `az login` on the device.
