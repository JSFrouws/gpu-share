# TODO — requested by Life-OS

Items life-os needs from this repo. Claude works on life-os directly (commits +
pushes there) but only *reports* gpu-share needs here — this repo has its own
pending local edits and its own build/deploy path (manual `dotnet publish` +
replace the running exe on the GPU PC; not part of the GHCR/Watchtower pipeline).

## Open

- **Remote STT (whisper)** — LM Studio 0.4.18 does NOT implement
  `POST /v1/audio/transcriptions` (verified: same "Unexpected endpoint" error
  as a bogus path; upstream feature request lmstudio-ai/lms#320 still open, and
  lmstudio.ai/transcribe says "coming soon"). `whisper-large-v3` appears in
  `/v1/models` but is only usable inside the LM Studio UI. Options, in order
  of preference:
  1. Wait for the LM Studio update — the Caddy proxy forwards all paths, so
     the endpoint will work remotely the moment LM Studio ships it. Zero work.
  2. Run a sidecar STT server on the GPU PC (e.g. `speaches` /
     faster-whisper-server, OpenAI-compatible) supervised by the agent like
     dino-worker, and route `/v1/audio/*` to it in the Caddyfile.

## Done

- **`/power/sleep`** — handler in `ControlServer.cs`, deployed in the running
  exe (2026-06-30). Triggers `rundll32 powrprof.dll,SetSuspendState 0,1,0`.
  If it silently no-ops, see git history of this file for the P/Invoke fallback
  notes (Hybrid Sleep / pending-update causes).
- **`GET /models`** — proxies LM Studio's `GET /v1/models` (port 1234) with the
  agent bearer token; 503 when LM Studio is down. Deployed 2026-06-30. Life-os's
  `GET /machine/models` can now populate the Android model picker.
