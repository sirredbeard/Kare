# Cost routing

Checked 2026-10-01.

Copilot Max costs $100 per month and includes 20,000 AI credits: 10,000 base and 10,000 flex.

One AI credit is $0.01.

Credits reset at 00:00 UTC on the first day of each calendar month. They do not roll over.

Copilot Chat, CLI, cloud agent, Spaces, Spark, and third-party coding agents consume credits.

Code completions and next edit suggestions do not consume AI credits on paid plans.

Auto model selection receives a 10 percent model-cost discount on paid plans.

Model cost is token based. Context and agent loops matter as much as the selected model.

The standard Copilot SDK route uses Copilot billing. SDK BYOK bypasses GitHub authentication and bills the configured provider. Local BYOK has no model-provider charge. Verify actual usage in GitHub and Azure accounting before calling a route free.

Visual Studio Enterprise Standard includes $150 monthly Azure Dev/Test credit. Professional includes $50. MSDN Platforms includes $100.

Azure credit stops usage at the cap when no payment method is attached. It is development and test credit, not a production budget.

Routing order:

Validated cache.

Local SLM.

Lightweight Copilot model.

Powerful Copilot model.

Foundry when model fit, remaining Azure credit, or Copilot pressure makes it the better route.

Record actual input, cached input, cache write, and output tokens when available. Store the published model price version used for the estimate.

Do not spend simply to empty an allowance. Unused monthly credit has no value after reset, so move high-value deferred work forward near month end when capacity remains.

Sources:

https://docs.github.com/en/copilot/concepts/billing/usage-based-billing-for-individuals

https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing

https://github.com/github/copilot-sdk/blob/main/docs/auth/byok.md

https://learn.microsoft.com/en-us/visualstudio/subscriptions/vs-azure-eligibility
