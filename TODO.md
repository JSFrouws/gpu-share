# TODO — requested by Life-OS

Items life-os needs from this repo. Claude works on life-os directly (commits +
pushes there) but only *reports* gpu-share needs here — this repo has its own
pending local edits and its own build/deploy path (manual `dotnet publish` +
replace the running exe on the GPU PC; not part of the GHCR/Watchtower pipeline).

## Open

- **Remote STT (whisper) — deploy step on the GPU PC.** The dino-worker now
  implements `POST /v1/audio/transcriptions` (faster-whisper, OpenAI-compatible,
  lazy-load + idle-evict) plus `POST /whisper/preload` (called by life-os right
  after inference-on; only preloads models ≤ `WHISPER_PRELOAD_MAX_MB`, default
  1500). Life-os reaches it through the EXISTING Caddy `/dino/*` route
  (`/dino/v1/audio/transcriptions`), so no Caddy or C# agent change is needed.
  Remaining manual step on the GPU PC:
  ```bat
  cd gpu-share & git pull
  .venv\Scripts\activate
  pip install -r dino-worker\requirements.txt   # adds faster-whisper
  ```
  then restart the dino-worker (tray agent inference off/on, or kill uvicorn).
  Env knobs (worker): `WHISPER_MODEL` (default `large-v3-turbo`),
  `WHISPER_COMPUTE` (default `int8_float16` on cuda), `WHISPER_IDLE_EVICT_SECONDS`
  (600), `WHISPER_PRELOAD_MAX_MB` (1500). When LM Studio ships its own
  `/v1/audio/transcriptions` (lmstudio-ai/lms#320), life-os can point
  `GPU_STT_URL` back at the plain `/v1` route — same request/response shape.
  (LM Studio 0.4.18 verified NOT to implement the endpoint; whisper-large-v3
  in /v1/models is UI-only.)

## Done

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
