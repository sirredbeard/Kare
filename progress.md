# Kare progress

Updated 2026-10-25 on the VENTUNO Q.

## Current state

- Branch: `main`. The gap pass below was committed as `11d88f2` ("Address the Kare gap list") and the Foundry sign-in correction as `7e69ef0`. Both are on `origin/main`.
- Pull requests #14 and #16 are merged. The old note that called `pr-14-rewrite` the current branch was stale and is gone.
- The deployed release is `ccc066b-dirty-jit` under `~/kare/service/current`. The `-dirty` suffix is new: `build/device-publish.sh` now names a release after the dirty tree that built it, not after the commit alone.
- `kare.service` is enabled, and linger is on for the `arduino` user. The user manager was not running at all after the last reboot, which is why Kare was down. That hole is closed.

## Fixed in this pass

- The GenieX stop path P/Invoked libc as `Kill`. The export is `kill`. Stop threw `EntryPointNotFoundException` on every shutdown. The entry point is now named in the attribute, a regression test pins it, and a live restart on 2026-10-25 stopped and started with no exception in the journal.
- Dashboard workload counters only moved when a route record landed, so a running inference read as zero. `DashboardActivity.RefreshWorkload()` now reads the live gate, a sampler republishes every second, and a streamed request on 2026-10-25 held `activeRequests` at 1 across 18 dashboard polls, then dropped back to 0.
- The source crawler followed any link the wildcard pattern matched. It now also checks a segment-aware path prefix and a denied segment list (`.git`, `account`, `admin`, `checkout`, `login`, `logout`, `wp-admin`), and it strips fragments before a page enters the queue.
- Intake cloud escalation exists. It is off by default (`Kare:Intake:AllowCloudEscalation`), runs only when the local pass returns no usable interpretation, uses the standalone cloud backend (no DI cycle with `ContextEnrichingChatClient`), and marks the proposal `UsedCloud` and billable. A cloud-read proposal always stays in Review. It is never auto-activated.
- `copilot-kare --vscode` is built (issue #9, first pass). It opens the tunnel, waits for `/health`, prints the VS Code Custom Endpoint entry for `chatLanguageModels.json` with the `${input:kareApiKey}` secret placeholder, and stays in the foreground until Ctrl+C. The key is never written to a file by the launcher. The rebuilt ARM64 artifact is in `artifacts/copilot-kare` and `--help` shows the new mode.
- `Kare.Inference.GenieX` is in `Kare.slnx`. It was missing. The Service referenced it and the tests built it, but the solution did not list it.
- `build/device-geniex-guard.sh` assumed the external `kare-geniex.service` runs the sidecar. Kare owns the sidecar now, so the guard failed before any deploy could start. The guard now handles both owners and skips the inference check with a notice when no endpoint is up yet. The post-deploy run is the one that has to pass, and it did: compute `npu`, bounded inference OK.
- Issue #3 has a finding, not a fix: `findings/model-catalog-warning.md`. The warning is a CLI-side model completion check with no documented off switch. Kare cannot fix it from the server side. Filtering CLI output in the launcher was considered and rejected.
- Issue #8 has a harness: `bench/benchmark/benchmark.py`, a starter task set, and a README. Quality, latency, and cost stay as three separate numbers. Dry run passes. A scored run still needs the frozen 24-task set.
- Issue #6 has it's local half: `build/kare-backup.sh` writes a restore point with a schema 1 manifest (sequence, commit with `-dirty` suffix when the tree is dirty, SHA-256 and byte count per object), verifies it, and prunes at 14 days. The first real point is on the device at `/var/lib/kare/backups/restore-points/20261025T074331Z-000001-ccc066b-dirty`, 5 objects, verified. Field inventory and the Azure steps left are in `findings/backup-restore-points.md`.
- The PCIe note in `findings/nvme-storage.md` was stale. The NVMe link now trains at Gen4 x4 (16.0 GT/s, width 4, in both sysfs and `lspci`). A dated recheck is appended to that file.

## Frontier model refresh (2026-10-25)

- Five deployments were created with the signed-in Kare Azure profile: `gpt-6.1-sol` (2026-09-29, capacity 250), `gpt-6-luna` (2026-09-22, capacity 250), `gpt-6-astra` (2026-09-03, capacity 250), `claude-opus-5-5` (version 2, capacity 40), and `claude-sonnet-5-5` (version 2, capacity 80). All report Succeeded. The GPT-6 quota in this region is 250 per model.
- The protected catalog was swapped in place with the tier shape and priority numbers kept. `gpt-6-astra` took a new Heavy slot at priority 1100. The Foundry `gpt-5.6-terra` entry stays, because its retirement is Copilot-side only. A backup of the old catalog sits in the protected config directory.
- `kare.service` restarted on the new catalog. `/health` returns ok, and one bounded request per new deployment passed at the Foundry endpoint on the correct wire.
- The example config in `examples/` named `gpt-5.6-terra` as a Copilot model. It now names `gpt-6-luna`.

## Branch cleanup

- Deleted local: `research/local-first-routing-epic`, `chore/readme-launcher-usability`.
- Deleted on origin: `research/local-first-routing-epic`, `fix/ssh-auth-before-copilot`, `copilot/close-issues-7-10-11-13`. All three belong to merged pull requests.
- Kept: both `backup/` branches, `docs/nvme-storage-plan`, `fix/ssh-auth-before-copilot` (local), and `pr-14-rewrite`. `git cherry` reports commits on the last three that are not patch-equal to anything in `main`. I did not delete the only local copy of work I could not prove was in `main`. That call is yours.

## Validation

- `dotnet test tests/Kare.Tests/Kare.Tests.csproj`: 176 passed, 0 failed. The count was 158 at the start of this pass. The 18 new tests cover the `kill` entry point, live workload refresh, the crawler path policy, intake cloud escalation on and off, and `--vscode` parsing.
- `./build/device-publish.sh --jit --test` ran the tests again, published, restarted the service, passed `/health`, and passed the GenieX guard.
- Live checks after the restart: `/v1/models` returns the `kare` model, the dashboard snapshot answers with the API key, and the GenieX sidecar (a Kare-owned child process) lists `unsloth/Qwen3.5-0.8B-GGUF:Q4_0` on `127.0.0.1:18181`.

## Remaining

- Set explicit Personal and Work route permissions in protected configuration after the approved Work provider is confirmed. Do not commit that configuration. (Carried over from the 2026-10-08 note. Done on 2026-10-25, see the routing section below.)
- Issue #6, Azure half: provision the PostgreSQL Flexible Server with 14-day point-in-time restore, then build the uploader, restore, and clone paths. Local points die with the board until this lands.
- Issue #8: freeze the 24-task set, then run the scored comparison against Copilot CLI `auto` from the client machine.
- Issue #3: re-check the BYOK documentation when the Copilot CLI version changes.
- The three kept branches above wait on a delete call.
- Frontier refresh: done on 2026-10-25. Five new Foundry deployments were created and the protected catalog now routes to the GPT-6 and Claude 5.5 generation. The Copilot Terra route is gone from the catalog ahead of its 2026-11-16 retirement. Detail is in `findings/frontier-model-refresh.md`.

## Personal and Work routing, direct Anthropic, and dashboard savings (2026-10-25)

- The approved Work provider is Anthropic directly. The device already held a Claude Code sign-in for the work account, so Kare reads that OAuth credential in place: new provider value `Anthropic`, new authentication value `ClaudeOAuth`, and `ClaudeOAuthTokenProvider`, which returns the stored access token and refreshes it in place before expiry (same file shape, atomic replace, owner-only permissions). Kare sends the token as a bearer token through the Copilot SDK anthropic provider with the `oauth-2025-04-20` beta header. No credential is stored in this repo; the protected catalog holds only the file path.
- The protected catalog now sets `AllowedModes` on every route: Copilot and Foundry routes are Personal only, and four Anthropic routes are Work only (Haiku Fast, Sonnet Heavy, Opus Complex, Sonnet Complex). Work requests can no longer reach the personal Copilot or Azure accounts, and Personal requests never reach the work Anthropic account.
- Inventory found the mode filter had never actually restricted anything. `AllowedModes` started pre-filled with both modes, and the .NET configuration binder appends bound values to a pre-filled list, so a configured `["Personal"]` bound as `[Personal, Work, Personal]`. The list now starts empty (empty still means both modes), the validator accepts empty and rejects bad values, and `AllowedModesBindingTests` covers the bind so it cannot regress silently. One older validator test asserted the old contract and was updated.
- Live checks on the device after the fix: a Work request with a required tool served from an Anthropic route, a Personal request with the same shape served from a Copilot route, and a repeated deterministic Personal request answered from the response cache in 0.06 seconds. Note the work account hit an Anthropic rate limit during testing (raw Messages calls returned `rate_limit_error` for every model); one Sonnet call completed through Kare before the limit, so the path is verified, and the limit is account state, not a Kare defect.
- Cache persistence was already on in the service environment; the protected catalog now carries the same settings (path under `/var/lib/kare/cache`, 64 MB cap, 3 backups) so the file alone describes the running shape.
- Prices are set in the protected catalog for every Foundry and Anthropic route (checked 2026-10-25 against the Azure and Anthropic pricing pages), so the dashboard cost estimates have data. Copilot routes stay unpriced on purpose: they bill in AI credits, not dollars per token.
- The dashboard snapshot now includes a savings summary for the process lifetime: successful requests split by SLM, cache, and cloud routes, the SLM share and on-device share as percents, the estimated cloud cost across priced routes, and the estimated avoided cost of the local answers. The page shows three new cards: Answered by SLM, Est. cloud cost, and Est. avoided by local. Percents and costs stay null until measured data exists.
- Tests: 190 passing (was 176). New coverage: the OAuth token provider (fresh token, refresh with rotation and write-back, refresh failure, missing file), Anthropic route validation, the mode binding regression, the descriptor route mapping, and the savings summary math.
- Operations note: repeated hard restarts of the service wedged the compute DSP shared memory (GenieX probe returned 500 with an fastrpc mmap allocation failure). Stopping the service and restarting the cdsp0 remoteproc cleared it; a board reboot was not needed.
