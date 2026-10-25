# Frontier model refresh

Rechecked 2026-10-25 against the GitHub changelog, the Microsoft Foundry announcements, and the Claude in Foundry model list. Model IDs and prices only. No endpoints, keys, or account details.

## What the frontier looks like now

GitHub's HydraFusion pool changed on 2026-10-14. The pool is now:

- `claude-opus-5.5` in Copilot, `claude-opus-5-5` in Foundry (Anthropic)
- `gpt-6-astra` (OpenAI flagship, $10 in / $50 out per million)
- `gpt-6.1-sol` (OpenAI, $2 / $10, cached input $0.10)
- `gpt-6-luna` (OpenAI, $0.10 / $0.50)

A second tier, HydraFusion Max, uses the same pool at higher reasoning effort. Cascade can now add a judging step that requests a repair, and Critique runs parallel critics that inspect the workspace with tools. Drafts run at medium effort and reviews at high effort. Kare's cascade shape (local draft, judge, repair, parallel critique) still matches that design. The flags stay off by default in `appsettings.json`, same posture as before.

The rest of the Copilot picker moved too:

- Claude Haiku 5.5 shipped 2026-10-05 and Claude Sonnet 5.5 ($2 / $10) shipped 2026-09-28. Copilot IDs use a dot (`claude-haiku-5.5`, `claude-sonnet-5.5`). Foundry names use a dash (`claude-haiku-5-5`, `claude-sonnet-5-5`). Both are in the Foundry catalog now, hosted on Azure or on Anthropic.
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
| Copilot Fast | `claude-haiku-4.5` | `claude-haiku-5.5` | One generation behind |
| Copilot Fast | `mai-code-1.1-flash` | (current) | Current. No change. |
| Copilot Heavy | `auto` | (current) | Current. `auto` follows GitHub's pool on its own. |

So no, the catalog does not match the new pool yet. The gap is configuration, not code: Kare reads its models from the protected catalog, and nothing in the router hardcodes a frontier model ID.

## What has to happen, in order

1. Create the Foundry deployments: `gpt-6.1-sol`, `gpt-6-luna`, `claude-opus-5-5`, `claude-sonnet-5-5`. Add `gpt-6-astra` only if the Max-style top tier is worth $10 / $50 per million. Confirm each deployment name in the portal at creation time. Foundry names usually match the model ID, and a custom name is fine as long as the catalog `WireModel` matches it.
2. Swap the protected catalog entries to the new deployment names and keep the tier shape: Luna takes the cheap Heavy and Fast slots, Sol takes the top slot, Opus 5.5 and Sonnet 5.5 take Complex. The catalog file lives in protected device configuration. It is never committed.
3. Drop the Copilot `gpt-5.6-terra` entry before 2026-11-16, or let it fail over to `auto` after that date. Point the Copilot Haiku entry at `claude-haiku-5.5`.
4. Restart `kare.service` and run `build/device-geniex-guard.sh` plus one bounded request per swapped route.

Correction, later on 2026-10-25: an earlier draft said the device Azure CLI was not signed in. That was wrong. I checked the default profile only. The default `~/.azure` profile has no accounts. Kare sets `AZURE_CONFIG_DIR` to a protected profile, and that profile is signed in. It lists three subscriptions. A recheck with that profile found one AI Services account in East US 2 and exactly five active deployments, all Succeeded: `gpt-5.6-terra`, `gpt-5.6-sol`, `gpt-5.6-luna`, `claude-sonnet-4-6`, and `claude-opus-5`. No GPT-6 or Claude 5.5 deployment exists yet. Step 1 can start in the portal or with the signed-in Kare profile.

Update, later on 2026-10-25: the swap is done.

Five deployments were created on the same Foundry account, each with the deployment name equal to the model name, SKU `GlobalStandard`:

- `gpt-6.1-sol`, version `2026-09-29`, capacity 250. The GPT-6 GlobalStandard quota in this region is 250 per model, not the 1000 the GPT-5.6 deployments hold.
- `gpt-6-luna`, version `2026-09-22`, capacity 250.
- `gpt-6-astra`, version `2026-09-03`, capacity 250.
- `claude-opus-5-5`, version `2`, capacity 40.
- `claude-sonnet-5-5`, version `2`, capacity 80.

All ten deployments on the account now report Succeeded. The five older deployments stay in place. GlobalStandard is usage billed, so an idle deployment costs nothing, and the old routes remain available as a fallback outside the catalog.

The protected catalog was swapped in place, tier shape and priority numbers kept:

- Foundry Heavy 200: `gpt-5.6-luna` out, `gpt-6-luna` in.
- Foundry Heavy 1000: `gpt-5.6-sol` out, `gpt-6.1-sol` in.
- Foundry Heavy 1100: `gpt-6-astra` added as the top slot.
- Foundry Complex 600: `claude-opus-5` out, `claude-opus-5-5` in.
- Foundry Complex 700: `claude-sonnet-4-6` out, `claude-sonnet-5-5` in.
- Foundry Fast 400: `gpt-5.6-terra` kept. The retirement is Copilot-side only.
- Copilot Fast 300: `gpt-5.6-terra` out, `gpt-6-luna` in. This is ahead of the 2026-11-16 retirement.
- Copilot Fast 100: `claude-haiku-4.5` out, `claude-haiku-5.5` in.
- Copilot Fast 250 (`mai-code-1.1-flash`) and Copilot Heavy 500 (`auto`) did not change.

`kare.service` was restarted on the new catalog and `/health` returns ok. One bounded request per new deployment passed at the Foundry endpoint: the three GPT-6 deployments answered on the Responses wire and both Claude 5.5 deployments answered on the Anthropic wire with the reply text. A backup of the previous catalog sits in the protected config directory next to the live file. It is not in this repository.
