# GenieX inference fails after the QAIRT 2.49 upgrade — DSP session open error 0x72

Date: 2026-10-27
Status: RESOLVED by runtime workaround 2026-10-27 (~00:49 EDT) — Kare restored (see Resolution). The boot-time CDSP firmware-init failure remains an open platform bug and may recur on reboot.
Purpose: working notes for a bug report (QCOM PPA / Arduino firmware / QAI Hub GenieX).

## Environment

- Hardware: Arduino Uno Q (QCM6490), hostname ventunoq
- OS: Ubuntu 24.04.5 LTS, arm64; kernel 6.8.0-1089-qcom
- GenieX: v0.7.1 (bundled QAIRT runtime 2.45) and v0.9.3 (bundled QAIRT 2.50) — both reproduce
- GenieX runtime: /var/lib/kare/runtimes/geniex (v0.9.3 installs flat; v0.7.1 used a versioned subdir)
- Model: unsloth/Qwen3.5-0.8B-GGUF (Q4_0), served at 127.0.0.1:18181, compute npu, context 8192, power mode sustained_high_performance
- Kare: framework-dependent publish on .NET SDK 11.0.100-rc.3. The failure reproduces with the GenieX sidecar standalone, no Kare code involved.

## Timeline (America/New_York)

- 2026-10-25 ~09:28 — GenieX probes return HTTP 200, inference ~600 ms. (An earlier wedge that morning, 05:15, was cleared by cycling the cdsp0 remoteproc — the fix that worked then.)
- 2026-10-26 23:33 — `apt upgrade -y`: qairt-headers, qairt-libs, qairt-tools, qairt-dsp-binaries 2.46.0-0ubuntu1~bpo24.04.1 -> 2.49.0-0ubuntu1~bpo24.04.2; linux-firmware-dragonwing 20260613 -> 20260825; plus unrelated desktop/Arduino packages.
- 2026-10-26 23:38 — `apt autoremove -y`.
- 2026-10-27 ~00:01 — Kare found crash-looping on its GenieX startup probe (systemd restart counter at 215). No successful inference observed after the upgrade.

## Symptoms

- GET /v1/models -> 200; the model enumerates correctly.
- POST /v1/chat/completions -> HTTP 500 in ~17 ms: {"code":-100001,"error":"SDKError(Invalid input parameters or handle)"}
- Kare side: LocalBackendSelector reports the GenieX probe failed (500), throws NoBackendAvailableException, process aborts (core dump); systemd restarts it indefinitely.

## Sidecar log evidence (geniex serve, GENIEX_LOG=debug)

Layer 1 — backend never initializes:

    ggml-hex: failed to load libcdsprpc.so: libcdsprpc.so: cannot open shared object file: No such file or directory
    Device 'HTP0' not found, skipping
    No valid devices found in 'HTP0'

Layer 2 — after restoring the symlink in Finding 1:

    ggml-hex: HTP0 allocating new session : domain 3 phys-idx 0 virt-idx 0
    ggml-hex: HTP0 failed to open session : uri file:///libggml-htp-v73.so?htp_iface_skel_handle_invoke&_modver=1.0&_dom=cdsp&_session=0 error 0x72
    llama_model_load: error loading model: ggml-hex: failed to open session (see log for details)
    -> HTTP 500 {"code":-100201,"error":"SDKError(Model loading failed)"}

## Findings

1. Missing unversioned library symlink. After the upgrade, /usr/lib/aarch64-linux-gnu holds libcdsprpc.so.1.0.0 and the libcdsprpc.so.1 symlink (package qcom-fastrpc1) but no libcdsprpc.so — the name ggml-hex dlopens. `ldconfig -p` still lists libcdsprpc.so (stale cache), so the system looks healthy. Restoring the symlink by hand (+ ldconfig) fixes layer 1 only: HTP0 initializes and tensor loading begins.
2. DSP session open fails (error 0x72) regardless of userspace version. Reproduced identically with GenieX v0.7.1 (QAIRT 2.45) and v0.9.3 (QAIRT 2.50), after two cdsp0 remoteproc stop/start cycles and a full board reboot, with no fastrpc errors in dmesg. A failure invariant across userspace generations and power cycles points at the DSP side of the boundary: qairt-dsp-binaries 2.49 and/or linux-firmware-dragonwing 20260825 against the ggml-hex v73 skeleton session interface. libggml-htp-v73.so itself is present, bundled with GenieX (llama_cpp/).
3. Ruled out: the CDSP shared-memory leak from 2026-10-25 (reboot-invariant, no dmesg fastrpc failures); model files (unchanged since Oct 3, enumerate fine); Kare (reproduces standalone); DSP hardware generally (HTP0 enumerates, session allocation begins).

## Reproduction

    cd /var/lib/kare/runtimes/geniex
    GENIEX_LOG=debug LD_LIBRARY_PATH=$PWD ./geniex serve --data-dir /var/lib/kare/models/geniex --compute npu --power-mode sustained_high_performance --nctx 8192 --skip-update
    curl -X POST 127.0.0.1:18181/v1/chat/completions -H 'Content-Type: application/json' -d '{"model":"unsloth/Qwen3.5-0.8B-GGUF","messages":[{"role":"user","content":"Reply OK. /no_think"}],"max_tokens":1,"temperature":0,"enable_think":false}'
    -> HTTP 500, SDKError(Model loading failed); log shows the session-open error above.

## Mitigations / status

- libcdsprpc.so symlink restored by hand (fixes layer 1). In the current package state the symlink is not package-owned; a future package operation may remove it again.
- No downgrade path: the QCOM PPA pool (pool/main/q/qairt-sdk) now carries only 2.49 builds (2.49.0-0ubuntu1~bpo24.04.2 and 2.49.0-0ubuntu2); the 2.46 .debs have been purged from the pool, and linux-firmware-dragonwing 20260613 is likewise absent from the current index.
- (Superseded by the Resolution below: paths repointed, Kare running.)
- Candidate owners for the report: ubuntu-qcom-iot/qcom-ppa (QAIRT packaging), Arduino (linux-firmware-dragonwing), QAI Hub GenieX (skeleton/QAIRT compatibility).
## Resolution (2026-10-27, ~00:49 EDT) — root cause refined; Kare restored

The session-open failure was NOT a DSP/skel incompatibility. On the first boot after the upgrade (boot at 00:17), the CDSP remoteproc failed its boot-time firmware initialization and never came up:

    [    9.977696] remoteproc remoteproc1: Booting fw image qcom/qcs8300/cdsp0.mbn, size 3174400
    [    9.993803] qcom_q6v5_pas 26300000.remoteproc: error -22 initializing firmware qcom/qcs8300/cdsp0.mbn
    [   10.077500] remoteproc remoteproc1: can't start rproc 26300000.remoteproc: -22

Chain: remoteproc1 (26300000, cdsp0) left offline -> /dev/fastrpc-cdsp and /dev/fastrpc-cdsp-secure never created -> cdsprpcd.service skipped by its ConditionPathExists (it had been running under the previous boot) -> no CDSP fastrpc channel -> ggml-hex session open fails with error 0x72 for every userspace version. HTP0 still enumerated because device discovery does not need the fastrpc node.

Proof it is a boot-time failure, not bad firmware: at runtime, starting remoteproc1 booted the SAME cdsp0.mbn (identical path/size, /usr/lib/firmware/updates/qcom/qcs8300/cdsp0.mbn) successfully ("remote processor 26300000.remoteproc is now up"); the fastrpc nodes appeared, cdsprpcd started, and the standalone serve probe returned a real chat completion. Kare was then started against GenieX v0.9.3 with kare.env GenieXProcess paths repointed to the flat layout: /health ok, build/device-geniex-guard.sh passed, restart count stable.

Board identity for the report: SoC QCS8275 (soc_id 675), firmware resolved under qcom/qcs8300/, DSP skel dir populated from /usr/share/qcom/qcs8300/Qualcomm/QCS8300-RIDE/dsp/cdsp/.

What remains open: why PAS init fails at boot-time auto-start but succeeds on runtime start, first observed on the first boot after the QAIRT 2.46 -> 2.49 and linux-firmware-dragonwing 20260613 -> 20260825 upgrade. Recurrence on reboot is untested; a boot that repeats the -22 needs the same runtime remedy (start remoteproc1, then cdsprpcd) before Kare can probe.

## Verification and evidence pack (2026-10-27, ~01:10 EDT)

- Kare restarted cleanly against the fixed stack: active, NRestarts 0 after the restart, /health "ok".
- build/device-geniex-guard.sh passed: compute=npu, endpoint http://127.0.0.1:18181/v1. A direct completion probe returned the expected token ("ready", finish_reason stop). The fix holds across a Kare restart; a board reboot has NOT been tested yet.
- Kernel: 6.8.0-1089-qcom, version_signature "Ubuntu 6.8.0-1089.94-qcom 6.8.12"; binary packages linux-image-6.8.0-1089-qcom 6.8.0-1089.94 and linux-image-6.8.0-1084-qcom 6.8.0-1084.89 both installed (linux-image-qcom meta still 6.8.0-1084.89).
- Packages: linux-firmware-dragonwing 20260825, qcom-fastrpc1 1.0.15+repack2.
- sha256 /usr/lib/firmware/updates/qcom/qcs8300/cdsp0.mbn = 13e9a9f82850c4d6a6b70f6a3e65b971df3ece819c741ea674c2ff3b42214d09 (3174400 bytes). No base copy exists under /usr/lib/firmware/qcom/qcs8300/ (topology .bin files only), and no dragonwing/qairt .deb is cached in /var/cache/apt/archives — a 20260613 bisect needs the old file from elsewhere.
- Kernel-source check (from the follow-up research): the "error -22 initializing firmware" line is printed after qcom_mdt_pas_init() calls qcom_scm_pas_init_image(); the -22 is the secure world's returned result to the PAS_INIT_IMAGE SMC, not a file, metadata-parse, or DMA failure (those log differently or return -ENOMEM).

## Reboot test (2026-10-27, 01:15 EDT) — did not recur

The board was rebooted to test recurrence. New boot (boot_id 97d72894-4b8d-4b17-92c5-00ecf32c6c97): no error -22 in the kernel log; cdsp0.mbn booted at startup ("remote processor 26300000.remoteproc is now up"); /dev/fastrpc-cdsp and /dev/fastrpc-cdsp-secure present; cdsprpcd active without intervention; kare.service active with NRestarts 0 and /health "ok"; device-geniex-guard passed. Tally: the PAS boot failure has occurred on 1 of 2 boots since the upgrade — intermittent, consistent with a boot-time race that the first post-upgrade boot lost. The Launchpad draft was updated with this data point. A boot guard (a systemd oneshot that starts remoteproc1 if it is offline, then starts cdsprpcd) remains an open option; it is not installed.
