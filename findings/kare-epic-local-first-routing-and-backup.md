# Kare epic: local-first routing, bounded knowledge, and durable restore

Date: 2026-10-07

Kare already has a working local answer-or-route gate, a conservative response cache, a bounded knowledge registry, explicit cloud targets, and route accounting. The missing piece is a control plane that makes those parts cooperate.

The immediate problem is concrete: `DashboardKnowledgeService.AddLocalContextAsync` adds every enabled authoritative source, every enabled skill body, and every connected MCP server description until the shared 12,000-character limit is full. Registration order can decide what Qwen sees. There is no request-specific retrieval, no progressive skill disclosure, and no MCP tool selection. The route gate is small, however the context feeding it is not selective.

This finding records the present request path, the lessons from HydraFusion and similar routers, and a staged design for issues #4, #5, and #6. It does not add product code.

## Scope

The target is a responsive local-first gateway that:

- uses the VENTUNO Q model for short, safe work it can actually complete;
- reuses validated local state before making another model call;
- selects only relevant code, sources, skills, and MCP material;
- asks GitHub Copilot, Microsoft Foundry, or a later provider only when local work is insufficient;
- records every route, model call, cache decision, and billable leg;
- preserves recoverable state without pretending provider-owned prompt caches can be backed up.

The target is not GitHub's private HydraFusion policy. GitHub has published the broad `single`, `cascade`, and `critique` patterns, but not the internal planner, judge prompts, thresholds, or production model policy.

## Current request path

The path in the repository on 2026-10-07 is:

```text
Copilot CLI or another OpenAI-compatible client
  -> POST /v1/chat/completions
  -> ChatCompletionsEndpoints
      -> normalize messages, tools, and request options
      -> check the conservative response cache
  -> HydraFusionCascadeChatClient
      -> reject cloud targets without the required tool or image capability
      -> route image requests directly to an image-capable cloud target
      -> use a safe cached route target when available
      -> otherwise ask local Qwen for KARE_ANSWER or KARE_ESCALATE
      -> try configured cloud targets in order after a pre-output failure
  -> RouteRecordingChatClient
      -> record route, backend, model, latency, tokens, success, fallback, and billing
  -> OpenAI-compatible response
```

`Program.cs` composes the local model as:

```text
GenieX or ONNX backend
  -> BoundedChatClient
  -> ContextEnrichingChatClient
  -> HydraFusionCascadeChatClient
```

That order matters. Dashboard knowledge is injected only into local model calls. The cloud route receives the original request, not the selected authoritative-source or skill packet.

### Current local route gate

`HydraFusionCascadeChatClient` gives the route gate:

- the final 1,024 request characters;
- compact route and capability metadata;
- no caller tools;
- at most 32 output tokens;
- no GenieX extended thinking.

The gate returns one of:

```text
KARE_ANSWER:<answer>
KARE_ESCALATE:<configured-target>
```

The 1,024-character input and 32-token output are measured limits, not arbitrary defaults. A 6,000-character route request exceeded Kare's 100-second GenieX timeout in the full Copilot CLI wrapper. The route gate must stay smaller than the work it is routing.

Tool-bearing requests currently set `kare.context.skip-knowledge`. This prevents a large source and skill dump from reaching the route gate, but it also means the gate cannot use a small relevant knowledge packet when tools are present.

### Current knowledge path

`DashboardKnowledgeService.AddLocalContextAsync` currently:

1. adds enabled authoritative sources in registration order;
2. adds enabled skill names, descriptions, and complete content in registration order;
3. adds every connected MCP server name and advertised capability;
4. stops when the shared 12,000-character limit is reached.

The service bounds total input, remote pages, downloaded bytes, refresh intervals, and protected snapshots. Those are useful safety controls. They do not answer the relevance question.

The current path has no:

- lexical search;
- symbol or repository search;
- vector search;
- rank fusion;
- per-source or per-skill quota;
- minimum relevance threshold;
- negative filtering;
- request-specific MCP tool discovery;
- provenance packet shared with the cloud route.

This is the main blocker for using the local model aggressively. More registered knowledge can make the local prompt worse.

### Current cache path

`ResponseCache` is deliberately conservative. It accepts only non-streaming deterministic text requests without:

- caller tools;
- tool history;
- non-text content;
- positive temperature;
- truncated output;
- oversized output.

Response keys include the cache version, knowledge context version, model and sampling options, and complete text messages. Route keys include the context version, eligible cloud candidates, tool declarations, and the messages used by the route decision.

The cache can persist eligible response text and route IDs in a protected bounded JSON snapshot. It does not yet carry an explicit repository identity, revision, selected file hashes, selected source IDs, selected skill versions, or selected MCP result hashes.

### Current cloud path

`CopilotSdkBackend` creates isolated temporary GitHub Copilot SDK sessions. It uses `CopilotClientMode.Empty` and disables ambient skills, embeddings retrieval, host Git operations, custom instructions, and config discovery.

Caller tools are sent as declarations and returned to the caller for execution. Kare does not execute those tools on the device. Temporary SDK sessions are deleted after the request.

GitHub account routes and Microsoft Foundry routes use the same catalog boundary, however provider state is not portable:

- reasoning artifacts can be provider-specific;
- prompt cache entries are provider and model specific;
- a warm prompt prefix on one deployment does not warm another deployment;
- switching after streaming output starts would mix providers in one response.

The existing rule is correct: retry or fail over only before output is committed.

## What the public work says

### GitHub Project HydraFusion

GitHub describes three public patterns:

| Pattern | Public shape | Kare use |
| --- | --- | --- |
| `single` | One selected model completes the task. | Validated cache result or one local/cloud model call. |
| `cascade` | An efficient model drafts, then a judge accepts it or sends it to a stronger model. | Add a separate result judge and one bounded repair handoff. |
| `critique` | A second model reviews the draft, then the drafting model revises it. | Reserve for work where the extra calls have a measured quality benefit. |

The judge is part of cascade. It is not a fourth public workflow.

The useful public rules are complete accounting, bounded execution, isolated review, fail-safe application, and validated routing. Kare can adopt those rules without claiming HydraFusion compatibility.

### RouteLLM

RouteLLM routes between an explicit weak and strong model pair. Its base router calculates a strong-model win rate and compares that score with an operator-selected threshold.

The current causal router uses the last turn. The code also documents that its routing assumptions were trained on first-turn data. That is the caution for Kare: route thresholds must be calibrated on Kare's workload, and multi-turn behavior cannot be inferred from a single-turn benchmark.

The useful pattern is not RouteLLM's model. It is the evaluation loop:

1. define weak and strong routes;
2. collect preference or task-quality results;
3. sweep a threshold;
4. compare quality and cost against always-weak and always-strong baselines;
5. choose a threshold from measured work, not prompt intuition.

### Microsoft.Extensions.AI routing

The current `Microsoft.Extensions.AI` source includes experimental `SemanticRoutingChatClient` and `FailoverChatClient` types.

`SemanticRoutingChatClient`:

- embeds app-provided example utterances lazily;
- caches the profile embeddings;
- embeds the last user message;
- selects a client by cosine similarity;
- supports a score threshold and top-k aggregation;
- falls back to a configured default client.

`FailoverChatClient`:

- records each attempt;
- allows another selection after an uncanceled pre-output failure;
- treats committed streaming output as terminal;
- exposes an attempt limit.

These types can remove some plumbing later. They do not provide a quality-gate cascade, a cross-family critique flow, repository-aware retrieval, or sticky provider policy. They are also marked experimental under `MEAI001`, so Kare should keep it's domain policy separate from those concrete types.

### Semantic Router

Semantic Router stores route utterances, function schemas, and route metadata in an index. Its base router uses top-k matches and keeps a SHA-256 hash of the route configuration for synchronization.

Two lessons transfer directly:

- route and retrieval profiles should be content-addressed and versioned;
- a cheap semantic selector can narrow candidates before an LLM sees them.

Kare should not require a cloud embedding call to route a local request. Start with local lexical and metadata scoring. Add local embeddings only when device measurements show a quality lift worth the latency and memory.

### LiteLLM

LiteLLM's router is much broader than Kare needs, however a few code paths are useful comparisons:

- prompt-cache affinity can pin a repeated prompt prefix to the deployment that previously cached it;
- routing checks model capability before dispatch;
- retries and fallbacks are separate from initial model selection;
- deployment health and cooldown state are cached;
- generated streaming content makes the attempt terminal.

Kare should use the same broad split: selection, capability checks, health, affinity, attempts, and accounting are separate decisions.

### Agent Skills

The Agent Skills specification uses progressive disclosure:

1. load the skill name and description into the catalog;
2. load the complete `SKILL.md` only after activation;
3. load referenced scripts, examples, or assets only when needed.

The skill description is the primary activation metadata. The complete skill body is not the catalog.

Kare currently loads every enabled skill body into local context. That is the opposite of progressive disclosure. The skill library should index metadata globally, score a small candidate set for the current request, and load complete bodies only for selected skills.

### MCP

MCP clients discover tools through `tools/list` and invoke them through `tools/call`. The 2026-07-28 tool specification adds pagination, cache time-to-live hints, cache scope, and tool-list change notifications. It also states that deterministic tool ordering helps client caching and prompt-cache reuse.

Kare currently records server names and advertised capabilities from `initialize`. It does not discover or call tools.

The next design should distinguish two MCP paths:

1. **Caller tools:** remain owned by Copilot or the calling client. Kare forwards declarations and never executes a mutation on the device.
2. **Kare knowledge tools:** optional, administrator-approved, read-only tools that Kare may call before local inference under a separate deadline, result-size limit, privacy class, and cache policy.

The second path is new work. Tool annotations are untrusted. Read-only status must come from Kare's allow-list and policy, not a server's self-description.

### Hybrid retrieval

Azure AI Search documents a useful hybrid pattern: run lexical BM25 and vector search in parallel, then combine the ranked lists with Reciprocal Rank Fusion.

Kare does not need Azure AI Search to use the idea.

The first selector should be cheap:

- exact identifier and path matches;
- heading, title, tag, and description matches;
- BM25 or another local lexical score;
- repository and source scope;
- recency and authority metadata;
- request capability and privacy filters.

A later local embedding score can add recall. Reciprocal Rank Fusion can combine lexical and vector ranks without pretending their raw scores are directly comparable.

### Microsoft Foundry prompt caching

Foundry prompt caching depends on a stable prefix. The documented minimum cacheable prefix is 1,024 tokens, and matching is based on an identical prefix. Current GPT-5.6-class behavior also supports a stable `prompt_cache_key`, explicit breakpoints, and cache-read and cache-write accounting.

This changes route policy:

- stable system instructions, tool schemas, selected skill versions, and selected source summaries should be ordered deterministically;
- volatile user and tool-result content should follow the stable prefix;
- the same provider, model, deployment, wire protocol, and cache key should remain sticky while the conversation is healthy;
- a cross-provider repair should be explicit because provider-owned cache and reasoning state will be lost.

Kare can back up the inputs and accounting around a provider cache. It cannot export or restore the provider's cache entry.

## Proposed control plane

The request path should become:

```text
request
  -> authentication, limits, and privacy policy
  -> request fingerprint and repository fingerprint
  -> deterministic capability checks
  -> validated exact cache
  -> candidate discovery
      -> code and repository records
      -> authoritative source chunks
      -> skill metadata
      -> MCP tool descriptors and cached read-only results
  -> cheap lexical and metadata ranking
  -> optional local vector ranking
  -> rank fusion and context-budget allocation
  -> local answer-or-route gate
      -> single
      -> cascade
      -> critique
  -> selected workflow
  -> one response and one complete route record
  -> bounded local persistence and incremental backup stream
```

### 1. Request and context fingerprints

Every request needs a stable fingerprint packet built in code:

- public wire model;
- normalized messages;
- repository identity and revision;
- selected file and symbol hashes;
- selected source chunk IDs and content hashes;
- selected skill names and versions;
- selected MCP server, tool, and result hashes;
- tool declarations and permission policy;
- model and provider versions;
- route-policy version;
- knowledge-index version;
- privacy class.

The complete packet does not need to be sent to a model. It exists for cache safety, invalidation, route records, and backup.

### 2. Candidate discovery

Discovery should return IDs and metadata, not complete bodies.

**Code and repository records**

- repository instructions;
- file paths, languages, headings, and symbols;
- content-addressed file and symbol summaries;
- recent validated command results;
- patch and validation records when policy permits storage.

**Authoritative sources**

- URL and source pattern;
- title and headings;
- authority class;
- fetched time, source modification time, and content hash;
- chunk IDs with provenance;
- source-level and chunk-level privacy class.

**Skills**

- name;
- description;
- origin and precedence;
- version or content hash;
- declared compatibility and privacy class;
- complete body location;
- referenced resource list.

**MCP**

- server ID and transport;
- last successful initialize and `tools/list`;
- stable ordered tool descriptors;
- Kare-owned read-only policy;
- cached result metadata and expiration;
- last failure and cooldown state.

### 3. Ranking and context budgets

Start with lexical and metadata ranking because it is cheap, inspectable, and easy to benchmark on the board.

Use hard filters before ranking:

- current repository or explicitly shared library;
- permitted privacy class;
- supported request capability;
- enabled source, skill, or MCP server;
- fresh enough for the request;
- within configured byte and item limits.

Then score:

- exact symbol, path, package, command, model, or product matches;
- title, heading, tag, and skill-description matches;
- lexical document relevance;
- source authority;
- repository proximity;
- recency where recency matters;
- prior validated usefulness for the same task class.

Add local vectors only after a representative benchmark shows better selection than lexical search alone. If both are present, fuse ranked lists rather than adding incomparable raw scores.

Allocate separate context budgets. One noisy source must not crowd out every skill or code record:

| Context class | Initial policy |
| --- | --- |
| Request and route metadata | Fixed small packet |
| Repository/code | Largest share for coding work |
| Authoritative sources | Small top-k, per-source cap |
| Skills | Metadata catalog plus complete body for selected skills only |
| MCP | Selected descriptors, then bounded cached read-only results |
| Provenance | IDs, versions, and hashes for every selected item |

The exact sizes remain unknown until measured on the VENTUNO Q.

### 4. Skill selection

The skill path should use two stages.

**Catalog stage**

Give the selector only names, descriptions, origin, version, and compatibility metadata. Project-level skills override shared skills with the same name. Explicit device paths and approved public URLs remain the only discovery roots.

**Activation stage**

Load the complete `SKILL.md` for the top candidate or candidates that clear a measured relevance threshold. Load referenced resources only when the selected instructions call for them.

Record false positives and false negatives. The Agent Skills project recommends trigger evaluation with realistic positive and near-miss negative prompts. Kare needs the same test set for it's own skills, especially because Qwen has less context and weaker trigger judgment than a frontier agent.

### 5. MCP selection

MCP should not become a second unbounded agent loop.

For every configured server:

- cache `initialize` and `tools/list` results;
- preserve deterministic descriptor order;
- refresh by server change notification or bounded expiration;
- keep descriptors outside the model prompt until relevance selection;
- apply health and cooldown state before selection.

For an administrator-approved read-only knowledge tool:

- select it by descriptor relevance and policy;
- call it before model generation;
- use a strict deadline, byte limit, item limit, and cache lifetime;
- store provenance and a result hash;
- never retry after partial output without an idempotency rule;
- never trust a server annotation as the authority for read-only status.

Mutation tools stay with the caller.

### 6. Cache layers

Kare needs several small caches, not one magic response cache.

| Cache | Key | Return policy |
| --- | --- | --- |
| Immutable artifact | Content hash, runtime, architecture | Return directly after integrity validation. |
| Parsed source and skill | Origin, content hash, parser version | Return directly. |
| Lexical index | Knowledge version, analyzer version | Return directly. |
| Embedding | Content hash, embedding model and version | Return directly. |
| MCP descriptors | Server identity, capability version, expiration | Return while healthy and fresh. |
| MCP read-only result | Server, tool, normalized arguments, policy, expiration | Return only within its privacy and freshness policy. |
| Route decision | Request fingerprint, capabilities, route-policy version | Reuse only while target health, budget, and catalog binding remain valid. |
| Final response | Complete context fingerprint and model policy | Keep conservative and off for agentic tool flows by default. |

Provider prompt caching is a separate provider-owned optimization. Kare should record cache reads and writes where the provider reports them, but it should not mix those counters with local response-cache hits.

### 7. Single, cascade, judge, and critique

**Single**

Use one local or cloud model when capability and quality evidence say one call is enough. The best single path is a validated cache hit, followed by a local answer grounded in selected context.

**Cascade**

Keep the current answer-or-route gate. Add a separate result judge after an efficient draft.

Code checks run first:

- provider completed normally;
- output was not truncated;
- required tool calls are present;
- validation results are accounted for;
- citations refer to selected records;
- repository and context versions did not change;
- route and model identity match the catalog.

Only then ask a model for a tiny semantic verdict:

```text
ACCEPT
REPAIR:<reason-code>
```

The judge packet should contain a request tail, answer summary or bounded excerpt, deterministic validation summary, selected provenance IDs, and fingerprints. It should not contain the complete Copilot wrapper.

A repair gets one stronger-model attempt. If repair fails, Kare returns an explicit failure. It does not apply or present a success-shaped fallback.

**Critique**

Critique uses an independent read-only critic from another model family, followed by one revision from the drafter or a configured repair model.

Use it only where a measured benchmark shows enough quality lift to justify two extra calls, such as:

- security-sensitive changes;
- destructive operations;
- storage, deployment, authentication, authorization, or billing changes;
- multi-file patches with incomplete validation;
- work where the local judge cannot reach a confident verdict.

The critic receives no mutation tools and cannot apply a patch.

### 8. Cloud enrichment

"Enrich the SLM with cloud responses" should not mean every cloud request is followed by another local rewrite. That spends more and adds latency.

There are three useful shapes:

1. **Direct cloud completion:** the selected cloud model returns the answer. Best when one strong call is enough.
2. **Cloud repair:** local or efficient cloud draft fails the judge, then one stronger model repairs it.
3. **Narrow cloud evidence:** an approved cloud model answers a small factual or review question, then local Qwen combines that result with local authoritative context.

The third shape must beat a direct cloud completion on cost or quality in a representative benchmark. Otherwise it is clever-looking overhead.

### 9. Sticky routing and budgets

Initial selection should account for:

- text, image, and tool capability;
- context fit;
- local queue depth and measured latency;
- request risk;
- selected knowledge confidence;
- route health and cooldown;
- Copilot AI credit cost;
- Foundry token and cache-write cost;
- provider prompt-cache affinity;
- user or administrator policy.

After selection, keep the provider, model, deployment, and stable prefix sticky while the route is healthy. Cross-provider repair remains available, but it must be recorded as a cache-breaking handoff.

Budget pressure can change future route selection. It must not terminate or silently downgrade a request after output has started.

### 10. Persistence and restore

Issue #6 should back up the state that makes local routing useful:

- source registrations, fetched content, chunks, hashes, and index metadata;
- skill registrations, versions, selected remote bodies, and resource metadata;
- MCP registrations, descriptors, health metadata, and eligible cached read-only results;
- response and route cache records allowed by policy;
- route-policy and model-catalog versions without secrets;
- route, usage, cache, and validation accounting;
- internal prompt templates and versions;
- encrypted logs and prompt-bearing records only when the explicit backup policy permits them.

The backup stream should not include:

- provider access tokens;
- copied Copilot settings;
- provider-owned prompt-cache state;
- temporary SDK session credentials;
- unbounded raw source trees;
- secrets from protected configuration.

The device change stream, six-hour manifests, 14-day application restore points, and Azure PostgreSQL point-in-time recovery proposed in #6 fit this control plane. Restore validation must rebuild indexes from hashes where possible and verify route-policy, parser, tokenizer, and model compatibility before serving cached results.

## Workstream dependency order

### A. Baseline and benchmark corpus

Create a representative corpus before changing policy:

- short factual and command questions;
- repository navigation and explanation;
- simple code edits;
- tool-bearing requests;
- authoritative-source questions;
- skill-trigger and near-miss prompts;
- MCP knowledge lookups;
- multi-file and high-risk changes;
- stale-context and provider-failure cases.

Record always-local, always-light-cloud, and always-strong-cloud baselines.

### B. Issue #4: bounded context broker

Issue #4 should first add:

- normalized source, skill, and MCP records;
- content hashes and versions;
- lexical and metadata indexing;
- progressive skill disclosure;
- MCP descriptor caching;
- request-specific ranking;
- provenance and context-budget allocation.

This produces the selected context packet consumed by routing.

### C. Cache safety

Add repository, file, source, skill, MCP, policy, and model fingerprints to the relevant caches. Split artifact, retrieval, route, response, and provider-cache accounting.

### D. Issue #5: workflow policy

Issue #5 consumes the context broker:

- local single;
- answer-or-route gate;
- deterministic checks;
- result judge;
- one repair;
- bounded critique;
- sticky provider policy;
- complete per-leg accounting.

### E. Issue #6: backup and restore

Back up the versioned records from the earlier workstreams. Test point-in-time restore and new-device clone against a known manifest.

### F. Tune from evidence

Tune thresholds, top-k values, context shares, timeouts, and model priorities from the benchmark corpus and device telemetry. Do not encode the research subagent's unmeasured timing guesses as requirements.

## Measurements

Every experiment should record:

- local resolution rate;
- validated local cache hit rate;
- route-cache hit rate;
- cloud calls avoided;
- Copilot AI credits per task class;
- Foundry input, output, cache-read, and cache-write tokens;
- first-token and total latency by leg;
- local queue wait, prompt processing, and decode rate;
- retrieval hit rate for known relevant records;
- skill trigger precision and recall;
- MCP descriptor and result-cache hit rate;
- judge accept, repair, and critique rates;
- answer quality and task completion against the baseline;
- stale-context, false-local, and false-escalation failures.

The epic should set final numeric gates only after the baseline corpus has run on the VENTUNO Q. The intended result is a material reduction in billable calls on basic work without a material quality loss. "Material" needs a number from the baseline, not a slogan.

## Unknowns

- Qwen's reliable skill-trigger accuracy with only metadata is unknown.
- The best local lexical analyzer for code, prose, paths, and commands is unknown.
- Local embedding latency and memory on the VENTUNO Q are unknown.
- The number of source chunks, skills, and MCP descriptors Qwen can use before quality drops is unknown.
- Whether narrow cloud evidence plus local synthesis beats one direct cloud call is unknown.
- The best judge model, packet size, and threshold are unknown.
- Copilot model availability, credit cost, and SDK compatibility remain account and version dependent.
- Foundry deployment price, capacity, prompt-cache behavior, and tool support remain deployment dependent.
- Restore time for the complete selected state is unknown until #6 has a measured dataset.

## Sources

Kare:

- [`src/Kare.Service/Program.cs`](../src/Kare.Service/Program.cs)
- [`src/Kare.Service/Routing/HydraFusionCascadeChatClient.cs`](../src/Kare.Service/Routing/HydraFusionCascadeChatClient.cs)
- [`src/Kare.Service/Dashboard/ContextEnrichingChatClient.cs`](../src/Kare.Service/Dashboard/ContextEnrichingChatClient.cs)
- [`src/Kare.Service/Dashboard/DashboardKnowledgeService.cs`](../src/Kare.Service/Dashboard/DashboardKnowledgeService.cs)
- [`src/Kare.Service/Cache/ResponseCache.cs`](../src/Kare.Service/Cache/ResponseCache.cs)
- [`src/Kare.Service/Api/ChatCompletionsEndpoints.cs`](../src/Kare.Service/Api/ChatCompletionsEndpoints.cs)
- [`src/Kare.Cloud.Copilot/CopilotSdkBackend.cs`](../src/Kare.Cloud.Copilot/CopilotSdkBackend.cs)
- [`src/Kare.Core/Inference/RouteRecordingChatClient.cs`](../src/Kare.Core/Inference/RouteRecordingChatClient.cs)
- [`findings/copilot-routing.md`](copilot-routing.md)
- [`findings/copilot-cli-byok-device-test.md`](copilot-cli-byok-device-test.md)
- [`findings/cost-routing.md`](cost-routing.md)
- [`findings/persistence-context-cache.md`](persistence-context-cache.md)

External:

- [GitHub: Project HydraFusion](https://github.blog/ai-and-ml/github-copilot/project-hydrafusion-frontier-quality-via-multi-model-orchestration/)
- [GitHub Copilot CLI BYOK](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-byok-models)
- [GitHub Copilot CLI hooks](https://docs.github.com/en/copilot/reference/hooks-configuration)
- [RouteLLM paper](https://arxiv.org/abs/2406.18665)
- [RouteLLM router source](https://github.com/lm-sys/RouteLLM/blob/main/routellm/routers/routers.py)
- [Microsoft.Extensions.AI semantic router source](https://github.com/dotnet/extensions/blob/main/src/Libraries/Microsoft.Extensions.AI/ChatRouting/SemanticRoutingChatClient.cs)
- [Microsoft.Extensions.AI failover source](https://github.com/dotnet/extensions/blob/main/src/Libraries/Microsoft.Extensions.AI/ChatRouting/FailoverChatClient.cs)
- [Semantic Router base implementation](https://github.com/aurelio-labs/semantic-router/blob/main/semantic_router/routers/base.py)
- [LiteLLM prompt-cache deployment affinity](https://github.com/BerriAI/litellm/blob/main/litellm/router_utils/pre_call_checks/prompt_caching_deployment_check.py)
- [Agent Skills client integration and progressive disclosure](https://agentskills.io/client-implementation/adding-skills-support)
- [Agent Skills description triggering](https://agentskills.io/skill-creation/optimizing-descriptions)
- [MCP tools specification, 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/server/tools)
- [Azure AI Search hybrid search](https://learn.microsoft.com/azure/search/hybrid-search-overview)
- [Azure AI Search Reciprocal Rank Fusion](https://learn.microsoft.com/azure/search/hybrid-search-ranking)
- [Azure OpenAI prompt caching](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/prompt-caching)
- [Lerna HydraFusion field reference](https://github.com/sirredbeard/Lerna/blob/main/experiments/hydrafusion-reference.md)
- [Lerna request and cache research](https://github.com/sirredbeard/Lerna/blob/main/RESEARCH.md)
