# Using gpu-share from a Remote PC

Two independent options. Option A exposes the OpenAI-compatible API to any
client; Option B is a zero-config personal path for LM Studio users.

## Option A — Cloudflare tunnel (any OpenAI-compatible client)

### One-time setup on the GPU PC (tunnel host)

1. Download `cloudflared.exe` from Cloudflare's release page to
   `C:\Tools\cloudflared.exe` (the path in `agent/appsettings.json`).
2. In **Cloudflare Zero Trust → Networks → Tunnels**, create a tunnel and copy
   its token.
3. Add a public hostname to the tunnel:
   ```
   Hostname: api.frouws-house.com
   Service:  http://server.home:8088
   ```
   This targets Caddy on server.home, keeping the bearer-token boundary intact.
   Never point it directly at LM Studio (`:1234`) — LM Studio has no auth.
4. Put the token in `agent/appsettings.json`:
   ```json
   "cloudflared": {
     "executable": "C:\\Tools\\cloudflared.exe",
     "args": ["tunnel", "run", "--token", "<TUNNEL_TOKEN>"]
   }
   ```
   Restart the tray agent. The "Enable Public Tunnel" toggle (tray or life-app)
   now starts/stops the tunnel.
5. In **Zero Trust → Access → Applications**, add a self-hosted app for the
   hostname. Add a policy for your e-mail (browser use) and create a
   **service token** for scripts.

### On the target PC (client)

Configure any OpenAI-compatible client (openai SDK, curl, LangChain, …):

```
Base URL: https://api.frouws-house.com/v1
Headers:
  CF-Access-Client-Id:     <service-token-id>.access
  CF-Access-Client-Secret: <service-token-secret>
  Authorization:           Bearer <SHARED_TOKEN>
```

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
