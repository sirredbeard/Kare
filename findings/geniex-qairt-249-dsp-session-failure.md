# GenieX inference fails after the QAIRT 2.49 upgrade — DSP session open error 0x72

Date: 2026-10-27
Status: OPEN — local inference down; kare.service left stopped.
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
- kare.service left stopped (deployed build: main @ ddfcd14). kare.env GenieXProcess paths still reference the removed v0.7.1 directory and must be repointed to the flat v0.9.3 layout when service resumes.
- Candidate owners for the report: ubuntu-qcom-iot/qcom-ppa (QAIRT packaging), Arduino (linux-firmware-dragonwing), QAI Hub GenieX (skeleton/QAIRT compatibility).
