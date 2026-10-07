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

Keep the checked-out device branch synchronized with Git. Do not copy source trees or credentials through ad hoc deployment commands. `build/deploy.sh` is only for copying an already-published artifact when Git-based device iteration is not available.

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

The service exposes the operations dashboard at `/dashboard`. It is for metadata Kare already owns and must not display or persist prompt text, source code, generated responses, credentials, provider tokens, or protected configuration.

The dashboard may show:

- recent route decisions, model IDs, backends, fallback state, success, billable state, latency, token counts, and decode rate
- queue depth, active local inference, completed requests, and average first-token latency
- bounded cache hashes, timestamps, size, and delete controls
- configured local, GitHub Copilot, and other external model endpoints
- authoritative HTTPS source patterns and refresh status
- skills loaded from explicit device paths or public HTTPS URLs
- configured Streamable HTTP MCP servers, advertised capabilities, connection state, and last connection time

Dashboard state stays bounded and process-local until the persistence design is complete. Repository defaults remain loopback-only. Non-loopback access requires explicit enablement and a trusted CIDR allow-list. The OpenAI-compatible API remains bearer-authenticated.

The dashboard registry must not scan arbitrary home directories or import GitHub Copilot settings. Remote sources and skills need fixed page, byte, refresh, and injected-context limits.

## Validation

Every performance claim needs a reproducible benchmark on the VENTUNO Q. Measure first-token latency, steady-state tokens per second, total latency, memory, temperature, power if available, cache hit rate, cloud-call reduction, and answer quality on representative coding tasks.

For pull requests, squash commits. Do not add co-authors or co-committers.
