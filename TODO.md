# TODO — requested by Life-OS

Items life-os needs from this repo. Claude works on life-os directly (commits +
pushes there) but only *reports* gpu-share needs here — this repo has its own
pending local edits and its own build/deploy path (manual `dotnet publish` +
replace the running exe on the GPU PC; not part of the GHCR/Watchtower pipeline).

## Open

(none)

## Done

- **Remote STT (whisper)** — deployed 2026-07-18. The dino-worker implements
  `POST /v1/audio/transcriptions` (faster-whisper `large-v3`, int8_float16 on
  cuda, lazy-load + 600s idle-evict) plus `POST /whisper/preload` (fired by
  life-os after inference-on; refuses models > `WHISPER_PRELOAD_MAX_MB` 1500 —
  large-v3 is 2948 MB so it JIT-loads on first use). Life-os reaches it via the
  existing Caddy `/dino/*` route; no Caddy/C# changes. On the GPU PC: venv at
  `gpu-share\.venv` (py3.12, torch 2.5.1+cu124 via manually-fetched wheel —
  pip's own TLS stream kept failing on the 2.4 GB download — plus
  nvidia-cudnn/cublas-cu12 wheels registered through an `os.add_dll_directory`
  bootstrap in app.py), model in the HF cache (`HF_HUB_DISABLE_SYMLINKS=1`
  needed on Windows), agent's `dist/agent/appsettings.json` dinoWorker block
  filled with the real paths so inference-on/off supervises the worker.
  Verified live: cold transcription 8.5 s (JIT load), warm 1.4 s, correct
  Dutch text; preload gate refusal confirmed; Caddy 401-gates the route.
  When LM Studio ships STT (lmstudio-ai/lms#320; 0.4.18 verified without it),
  life-os can point `GPU_STT_URL` at the plain `/v1` base — same shape.

- **`GET /models/state`** — proxies LM Studio's native `GET /api/v0/models`
  (verified working on 0.4.18), which includes per-model
  `"state": "loaded"|"not-loaded"` plus arch/quant/context info. Deployed
  2026-07-04. Life-os forwards it as `GET /machine/models/state`; the app can
  replace its ~60s client-side timer with the real load state.
- **`/power/sleep`** — handler in `ControlServer.cs`, deployed in the running
  exe (2026-06-30). Triggers `rundll32 powrprof.dll,SetSuspendState 0,1,0`.
  If it silently no-ops, see git history of this file for the P/Invoke fallback
  notes (Hybrid Sleep / pending-update causes).
- **`GET /models`** — proxies LM Studio's `GET /v1/models` (port 1234) with the
  agent bearer token; 503 when LM Studio is down. Deployed 2026-06-30. Life-os's
  `GET /machine/models` can now populate the Android model picker.
