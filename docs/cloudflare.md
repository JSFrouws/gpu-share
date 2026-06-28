# Exposing gpu-share via Cloudflare (Phase 3)

The inference tunnel is **Phase 3** — optional until you need public access to the
GPU from outside the LAN. Control (wake/sleep/GPU on-off) always flows through
`server.home` regardless of this tunnel.

## Architecture

```
internet  →  Cloudflare Access (SSO)  →  CF edge
                                            │
                                            ▼ encrypted tunnel
                                    cloudflared.exe (GPU PC)
                                            │
                    ┌───────────────────────┴───────────────────┐
                    ▼                                           ▼
             Caddy :8088 (server.home)              or direct to ports
             → /v1/* LM Studio :1234                   LM Studio :1234
             → /dino/* dino-worker :8000               dino-worker :8000
```

`cloudflared` runs **on the GPU PC**, supervised by the C# agent (toggle
"inference tunnel on/off" from the life-app menu or the tray). It is independent
of the GPU handler — the two can be toggled separately.

## 1. Install cloudflared on the GPU PC

Download the Windows binary from Cloudflare's release page and place it somewhere
on the `PATH` (e.g., `C:\Tools\cloudflared.exe`). The C# agent will launch it as a
child process; do **not** install it as a Windows service.

## 2. Route a hostname to Caddy

Point the tunnel at Caddy on `server.home` — this keeps the single auth boundary
intact and avoids exposing LM Studio's API directly.

In the Cloudflare Zero Trust dashboard (tunnel → Public Hostnames):

```
Hostname: api.frouws-house.com
Service:  http://server.home:8088
```

Or in `config.yml` if you use a file-based config:

```yaml
ingress:
  - hostname: api.frouws-house.com
    service: http://server.home:8088
  - service: http_status:404
```

> Do **not** route port 8088 directly to `0.0.0.0` or the internet — Caddy
> already handles the bearer token check; the tunnel adds the Cloudflare Access
> layer on top.

## 3. Protect with Cloudflare Access

In **Zero Trust → Access → Applications**, add a self-hosted app for
`api.frouws-house.com` with a policy matching your identity (email, group, etc.).

For programmatic clients (scripts, life-app backend), create an **Access service
token** and send its headers on every request:

```
CF-Access-Client-Id: <id>.access
CF-Access-Client-Secret: <secret>
Authorization: Bearer <SHARED_TOKEN>
```

## 4. Test

```bash
# LLM via the public endpoint
curl https://api.frouws-house.com/v1/chat/completions \
  -H "CF-Access-Client-Id: $CF_ID" \
  -H "CF-Access-Client-Secret: $CF_SECRET" \
  -H "Authorization: Bearer $SHARED_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"model":"gemma-3-27b","messages":[{"role":"user","content":"hi"}]}'

# DINOv3 embedding
curl https://api.frouws-house.com/dino/embed \
  -H "CF-Access-Client-Id: $CF_ID" \
  -H "CF-Access-Client-Secret: $CF_SECRET" \
  -H "Authorization: Bearer $SHARED_TOKEN" \
  -F "file=@/path/to/image.jpg"
```

## States

The C# agent tracks four inference states:

| GPU handler | Tunnel | Effect |
|---|---|---|
| Off | Off | Gaming — full VRAM, no exposure |
| On | Off | LAN inference only (via Caddy on server.home) |
| On | On | Remote inference (CF Access + bearer) |
| Off | On | Agent warns and auto-corrects (pointless) |

## Notes

- Cloudflare's free tier has a **100 MB request body limit** and ~100 s timeout.
  Large audio for STT may need chunking or direct LAN access.
- For LAN access (server.home backend, local scripts), skip Cloudflare entirely:
  hit `http://server.home:8088` with just the bearer token.
- A **long random hostname** is obscurity only — defeated by CT logs within hours.
  Never rely on it as a security boundary; Cloudflare Access is the boundary.
