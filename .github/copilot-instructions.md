# Kare Copilot instructions

Read `README.md`, this file, and `plan.md` before changing the project.

Kare is a .NET 11 local gateway for GitHub Copilot CLI BYOK on the VENTUNO Q. It is not a replacement for GitHub's private orchestration layer. It is a bounded local gate, a cache and route policy, and a small OpenAI-compatible service.

Keep documentation simple and specific. Do not use em dashes. State unknowns as unknowns instead of smoothing them over.

## Hardware and deployment

- Target: Arduino VENTUNO Q with Qualcomm Dragonwing IQ8 / QCS8275.
- Device OS: Ubuntu 24.04.5 LTS.
- Runtime target: `linux-arm64`.
- The service must keep working when the NPU runtime is absent. CPU inference is the first fallback, not an error path.
- Prefer NVMe for models, indexes, logs, and cache data when it is available.

## Security

Never commit or print host details, device addresses, usernames, passwords, SSH keys, GitHub tokens, Azure tokens, model-provider keys, or copied Copilot settings.

Keep service config outside the repo. Use environment variables, protected config files, or a secret store. Treat prompts, source code, generated patches, and cache entries as private network data.

Do not expose Kare to the public internet. Bind to the local network only after authentication, request limits, and a trusted allow-list are in place.

Do not commit or store local IP addresses, Azure deployment names, credentials, or PATs in the repo. The policy belongs here, not in the README.

## Protected configuration

Device connection state belongs outside the repository in `~/.config/kare/device.env`, or at the absolute path in `KARE_DEVICE_CONFIG`. Keep the directory mode `700` and the file mode `600`.

The launcher reads these values when present:

- `KARE_DEVICE_HOST`
- `KARE_DEVICE_USER`
- `KARE_DEVICE_PASS`, only when an SSH key is unavailable
- `KARE_API_KEY`
- `KARE_COPILOT_HOME`
- `KARE_TUNNEL_LOCAL_PORT`
- `KARE_TUNNEL_REMOTE_PORT`
- `KARE_TUNNEL_READY_TIMEOUT`
- `KARE_MODEL_ID`
- `KARE_WIRE_MODEL`
- `KARE_MAX_PROMPT_TOKENS`
- `KARE_MAX_OUTPUT_TOKENS`
- `KARE_LOG_DIRECTORY`
- `KARE_LOG_FILE_BYTES`
- `KARE_LOG_TOTAL_BYTES`

The launcher writes the last healthy device address to `last-device-host` beside the protected config. It must write the file only after `/health` succeeds and use mode `600` on Unix.

Keep service settings, cloud model catalogs, provider endpoints, deployment names, authentication state, and device-specific paths in protected external files. `examples/KARE_CONFIG_FILE.example.json` is a schema example, not a deployable configuration.

## Local launch path

The supported launch flow is the .NET launcher in `src/Kare.CopilotLauncher`. It accepts a device IP as the first argument and remembers the last known IP when it is not provided.

Use this shape:

```bash
./artifacts/copilot-kare/copilot-kare YOUR_DEVICE_IP -i "Review this repository"
./artifacts/copilot-kare/copilot-kare -i "Review this repository"
```

The launcher should:

- read `~/.config/kare/device.env` or `KARE_DEVICE_CONFIG`
- reuse the last working address from `~/.config/kare/last-device-host` when available
- open an SSH tunnel to the device
- wait for `/health` on `http://127.0.0.1:5285`
- export the required BYOK env vars for Copilot CLI
- start Copilot with an isolated `COPILOT_HOME`

The default launcher profile advertises 31744 prompt tokens and reserves 1024 output tokens. This is a routed 32768-token Copilot context, not a claim that the local GenieX sidecar receives the full wrapper. The local gate remains bounded to selected request text and authoritative context.

The local cascade gate uses at most the final 1024 request characters and 32 output tokens. Keep this routing request small. The full Copilot wrapper took more than two minutes when the gate admitted 6000 characters.

`--kare-verbose` adds Copilot CLI debug logging under protected local configuration. `--kare-log-dir PATH` selects the directory. `--kare-minimal-context` selects the earlier 8192-token offline diagnostic profile with builtin MCP servers and repository instructions disabled and only `bash` exposed.

The repo does not depend on a shell script as the primary interface. The .NET launcher is the canonical path.

## Device iteration

Development happens on x86_64 Linux and the service runs on Ubuntu 24.04 ARM64. Keep a project-local .NET 11 SDK at `.dotnet` in the device checkout because the board's global runtime may be an older major version.

The repo does not install the device service unit yet. Create `~/.config/systemd/user/kare.service` outside the checkout:

```ini
[Unit]
Description=Kare local coding gateway
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=%h/Kare
ExecStart=%h/kare/service/current/kare --urls http://127.0.0.1:5285
Environment=DOTNET_ROOT=%h/Kare/.dotnet
EnvironmentFile=-%h/.config/kare/service.env
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=default.target
```

Keep `service.env` mode `600`. It may point `KARE_CONFIG_FILE` at the protected service JSON and set native runtime paths, but it must not contain values copied into issues, logs, or the repository.

Do not make `kare.service` require `kare-geniex.service`. A hard `Requires=` dependency stops Kare when GenieX is stopped for recovery, which prevents Kare from warning the client or completing the recovery. Until Kare owns the GenieX process directly, use `Wants=kare-geniex.service` with `After=kare-geniex.service`.

Enable the unit once:

```bash
systemctl --user daemon-reload
systemctl --user enable kare.service
```

Publish and test the device service with:

```bash
./build/device-publish.sh --test
```

`build/device-publish.sh` must run on the ARM64 device. It publishes into a commit-specific release directory, updates the `current` symlink atomically, restarts `kare.service`, and waits for the loopback health endpoint.

The publish script runs `build/device-geniex-guard.sh` before and after deployment. The guard fails when `kare-geniex.service` is not using the expected `npu` compute target or when a bounded inference fails. Use `KARE_GENIEX_EXPECTED_COMPUTE` only for an intentional measured comparison.

Kare's runtime health monitor probes the preferred accelerator every five minutes and immediately after a local inference failure. It may gracefully stop and start `kare-geniex.service` once per hour, with a cleanup delay between operations. It must not reboot the device. Return to NPU only after consecutive successful probes. Use a configured and already validated ONNX CPU backend when one exists. Otherwise warn in Copilot chat that no CPU fallback is configured and let the existing route policy decide whether a cloud route is available.

Keep the checked-out device branch synchronized with Git. Do not copy source trees or credentials through ad hoc deployment commands. `build/deploy.sh` is only for copying an already-published artifact when Git-based device iteration is not available.

Use this workflow for normal device changes:

1. Commit locally without co-authors or co-committers.
2. Push the branch to GitHub.
3. SSH to the device checkout and run `git pull --ff-only`.
4. Stop `kare.service`.
5. Run `./build/device-publish.sh --jit --test`.
6. Confirm `kare.service` and `kare-geniex.service` are active.
7. Check `http://127.0.0.1:5285/health` and `http://127.0.0.1:18181/v1/models`.

Do not rebuild from an uncommitted source copy on the device. Do not use Native AOT for the normal iteration path.

## Device storage layout

The VENTUNO Q boots from eMMC. The installed OSCOO PCIe 512GB drive is `/dev/nvme0n1p1`, formatted as ext4, and mounted at `/var/lib/kare` with `noatime`.

Keep these items on eMMC:

- The operating system and boot files.
- The Kare checkout at `%h/Kare`.
- Published Kare releases under `%h/kare/service`.
- Protected configuration under `%h/.config/kare`.
- User systemd units under `%h/.config/systemd/user`.

Keep these items on NVMe:

- `/var/lib/kare/models/geniex` - GenieX model data.
- `/var/lib/kare/runtimes/geniex` - the GenieX Linux ARM64 runtime and native libraries.
- `/var/lib/kare/logs` - bounded rotating service logs.
- `/var/lib/kare/cache` - bounded response and route cache snapshots.
- `/var/lib/kare/state` - authoritative source, remote skill, context version, and MCP registration snapshots.

The `kare-geniex.service` user unit uses `GENIEX_DATADIR=/var/lib/kare/models/geniex` and loads its executable and libraries from `/var/lib/kare/runtimes/geniex`. Do not move the Kare service checkout or release symlink without a separate rollback plan.

Set `KARE_STATE_DIRECTORY=/var/lib/kare/state`, `KARE_LOG_DIRECTORY=/var/lib/kare/logs`, `Kare__Cache__Responses__PersistenceEnabled=true`, and `Kare__Cache__Responses__PersistencePath=/var/lib/kare/cache/responses.json` in the protected service environment. Keep these directories mode `700` and snapshot files mode `600`.

Response snapshots are limited by entry count, expiration, response size, and `MaxPersistentBytes`. They store opaque SHA-256 cache keys, route IDs, eligible generated text, token counts, and timestamps. They do not store raw prompts or source code. Truncated answers, tool calls, and nondeterministic requests are not persisted. The active cache file keeps three bounded local backups by default.

The dashboard registry persists configured source and skill registrations, bounded fetched content, context version, MCP registrations, and last-known MCP capabilities. It reloads that snapshot before the first network refresh. A failed refresh keeps the stale last-known content available and records the failure.

These `.bak1` through `.bak3` files are local rollback snapshots, not disaster recovery. Do not describe them as off-device backups. PostgreSQL, pgvector, encrypted remote archives, and cloud restore remain later work.

The drive currently negotiates PCIe Gen4 x1 even though the root port advertises x4. Record that fact in performance notes and do not describe the storage path as a full Gen4 x4 path until firmware, device tree, kernel, and physical seating checks explain it.

Verbose Kare service logs live on NVMe under `/var/lib/kare/logs`. Set `KARE_LOG_DIRECTORY` to that absolute path and `Logging__LogLevel__Default=Debug` in the protected service environment. Kare rolls files at 25 MiB and retains no more than 250 MiB by default. `KARE_LOG_FILE_BYTES` and `KARE_LOG_TOTAL_BYTES` may lower those limits.

## Copilot integration

The supported path is GitHub Copilot CLI BYOK pointed at Kare's OpenAI-compatible `/v1` endpoint. That requires streaming and tool-call metadata on the wire.

[Project HydraFusion](https://github.blog/ai-and-ml/github-copilot/project-hydrafusion-frontier-quality-via-multi-model-orchestration/) documents GitHub's experimental single, cascade, and critique orchestration patterns. [Lerna](https://github.com/sirredbeard/Lerna) demonstrates explicit provider mapping for supported HydraFusion model IDs.

Kare borrows those broad ideas but owns a smaller bounded answer-or-route decision. Do not claim that a Copilot CLI BYOK session keeps GitHub's native HydraFusion or Lerna behavior. BYOK is a separate path from native Copilot orchestration, and Lerna is a reference project rather than a Kare runtime dependency.

Use hooks and policy for redaction, route metadata, cache fingerprints, and accounting. Do not build a second model-interception layer on top of the CLI.

## .NET and native code

- Target .NET 11.
- Publish the small `copilot-kare` launcher as a trimmed, compressed, self-contained single file so all release targets can be built from the Linux development machine.
- Keep the service release optimizations in project and build configuration rather than user-facing documentation.
- Keep the managed service separate from native inference adapters.
- Keep a clear CPU fallback whenever QNN, QAIRT, or another accelerator library is missing.
- Use explicit runtime identifiers, native library names, model paths, and provider settings.
- Keep JSON metadata compile-time and avoid reflection-based discovery.
- Bound prompt size, output size, queue depth, concurrent inference, and cache memory.
- Do not add broad exception catches or silent fallback behavior. Log the route and the reason for fallback without logging secrets or prompt contents.

## Inference and routing

- Keep GenieX as the default local runtime unless measurements show a different path is better.
- Keep ONNX Runtime GenAI as a slow, measured fallback only. It was tested and ruled out as the default for the VENTUNO Q because it is materially slower than the local GenieX path.
- Use Microsoft.Extensions.AI and `IChatClient` as the service boundary.
- Treat GenieX as a separate local runtime option. Do not mix its model format or process model into the ONNX Runtime path without a measured reason.
- Start with a small coding model and a short context window. Add larger models only after latency, memory, thermals, and quality are measured.
- Cache only with a privacy policy, bounded size, versioned keys, and a clear invalidation rule.
- Do not silently turn a local request into a billable cloud call. Record the route and require a configured policy.

## Operations dashboard

The service exposes the operations dashboard at `/dashboard`. Open `http://127.0.0.1:5285/dashboard` on the device, or forward port `5285` through SSH and open the forwarded local address in a browser. The dashboard is local by default. Do not make it non-loopback until authentication, request limits, and an explicit trusted CIDR allow-list are configured.

The dashboard is for metadata Kare already owns. It must not display prompt text, source code, generated responses, credentials, provider tokens, or protected configuration. The separate protected response-cache snapshot may retain eligible generated text under the bounded cache policy above.

The dashboard may show:

- Recent route decisions, model IDs, backends, fallback state, success, billable state, latency, token counts, and decode rate.
- Queue depth, active local inference, completed requests, and average first-token latency.
- Bounded cache hashes, timestamps, size, and delete controls.
- Configured local, GitHub Copilot, and other external model endpoints.
- Authoritative HTTPS source patterns and refresh status.
- Skills loaded from explicit device paths or public HTTPS URLs.
- Configured Streamable HTTP MCP servers, advertised capabilities, connection state, and last connection time.

Dashboard metrics remain process-local. The bounded knowledge registry is restart-safe on NVMe. Repository defaults remain loopback-only. Non-loopback access requires explicit enablement and a trusted CIDR allow-list. The OpenAI-compatible API remains bearer-authenticated.

The dashboard registry must not scan arbitrary home directories or import GitHub Copilot settings. Remote sources and skills need fixed page, byte, refresh, and injected-context limits.

## Repository layout

- `src/Kare.Service` - HTTP service, dashboard, and OpenAI-compatible endpoint.
- `src/Kare.Core` - routing policy, limits, cache, and request handling.
- `src/Kare.Cloud.Copilot` - GitHub Copilot SDK and configured cloud routes.
- `src/Kare.Inference.GenieX` - local GenieX adapter.
- `src/Kare.Inference.OnnxGenAI` - measured CPU fallback.
- `src/Kare.CopilotLauncher` - cross-platform `copilot-kare` launcher.
- `bench/Kare.DeviceProbe` - device and native runtime probe.
- `findings/` - dated research and measured device results.
- `plan.md` - architecture, open gates, and staged work.
- `tests/Kare.Tests` - focused automated checks.

## Validation

Every performance claim needs a reproducible benchmark on the VENTUNO Q. Measure first-token latency, steady-state tokens per second, total latency, memory, temperature, power if available, cache hit rate, cloud-call reduction, and answer quality on representative coding tasks.

For pull requests, squash commits. Do not add co-authors or co-committers.
