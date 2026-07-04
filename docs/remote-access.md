# Using gpu-share from a Remote PC

Two independent options. Option A exposes the OpenAI-compatible API to any
client; Option B is a zero-config personal path for LM Studio users.

## Option A — Cloudflare tunnel (any OpenAI-compatible client)

### How it's wired (already live)

The existing `cloudflared` systemd service on **server.home** carries the
public hostname — nothing runs on the GPU PC:

```
Hostname: inf.frouws-house.com
Service:  http://localhost:8088     (Caddy, on the same host)
```

Never point a hostname directly at LM Studio (`:1234`) — LM Studio has no auth.

**Tunnel toggle** — the life-app tunnel button flips the flag file
`gate/tunnel-enabled` (via `/machine/tunnel/on|off`). Caddy 403s any request
that arrived through Cloudflare (`CF-Connecting-IP` header present) while the
flag is absent. LAN traffic is never gated, and the toggle works even when the
GPU PC is off.

Optional hardening: in **Zero Trust → Access → Applications**, add a
self-hosted app for the hostname with an e-mail policy (browser use) and a
**service token** for scripts. Without it, the `SHARED_TOKEN` bearer check is
the single auth layer.

### On the target PC (client)

Configure any OpenAI-compatible client (openai SDK, curl, LangChain, …):

```
Base URL: https://inf.frouws-house.com/v1
Headers:
  Authorization:           Bearer <SHARED_TOKEN>
  # plus, only if Cloudflare Access is configured:
  CF-Access-Client-Id:     <service-token-id>.access
  CF-Access-Client-Secret: <service-token-secret>
```

Works today: `/v1/chat/completions`, `/v1/completions`, `/v1/embeddings`,
`/v1/models`, `/dino/*`. Not yet: `/v1/audio/transcriptions` — LM Studio has
not shipped its STT endpoint (see TODO.md); the proxy will pass it through
automatically once it exists.

`SHARED_TOKEN` lives in `/home/ubuntu/webapps/gpu-share/.env` on server.home
and is checked by Caddy on every request. The API paths are the standard
OpenAI format (guessable by design) — the two auth layers are the security
boundary, not obscurity.

On the LAN, skip Cloudflare entirely: `http://server.home:8088/v1` with just
the `Authorization` header. See `../../life-os/HowToCallLocalLLM.md`.

Note: the GPU PC must be awake with inference enabled. Wake and toggle it via
the life-app Machine menu, which works from anywhere the life-app works.

## Option B — LM Link (LM Studio to LM Studio, personal use)

1. On the GPU PC: enable **LM Link** in LM Studio (headless/remote serving).
2. On the target PC: install LM Studio, sign in with the same LM Studio
   account, and the GPU PC's models appear as remote models.

No tunnel, no tokens, no DNS. Trade-offs: traffic relays through LM Studio's
cloud, only LM Studio clients can connect (no generic OpenAI API), and the
GPU PC must already be awake with LM Studio running — combine with the
life-app wake button. Coexists fine with Option A; both serve the same
loaded model.
