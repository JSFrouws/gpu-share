# TODO — requested by Life-OS

Items life-os needs from this repo. Claude works on life-os directly (commits +
pushes there) but only *reports* gpu-share needs here — this repo has its own
pending local edits and its own build/deploy path (manual `dotnet publish` +
replace the running exe on the GPU PC; not part of the GHCR/Watchtower pipeline).

_No open items._

## Done

- **`/power/sleep`** — handler in `ControlServer.cs`, deployed in the running
  exe (2026-06-30). Triggers `rundll32 powrprof.dll,SetSuspendState 0,1,0`.
  If it silently no-ops, see git history of this file for the P/Invoke fallback
  notes (Hybrid Sleep / pending-update causes).
- **`GET /models`** — proxies LM Studio's `GET /v1/models` (port 1234) with the
  agent bearer token; 503 when LM Studio is down. Deployed 2026-06-30. Life-os's
  `GET /machine/models` can now populate the Android model picker.
