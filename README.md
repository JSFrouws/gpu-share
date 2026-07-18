# gpu-share

On-demand GPU inference engine for an **RTX 3090 (24 GB)** — exposed to the LAN
and optionally to the internet via the GPU PC's own Cloudflare tunnel.

The control surface lives in the **life app** (side menu). The always-on home server
(`server.home`) acts as relay, WOL sender, and schedule enforcer. A **C# agent** on
the GPU PC supervises all inference processes and accepts control commands from
`server.home` over LAN.

```
   ┌──────────────────────────┐
   │  Life app (any device)   │   life-app auth is the public boundary
   │  Machine side menu       │
   │  • status / wake / sleep │
   │  • GPU handler on/off    │
   │  • inference tunnel on/off│
   │  • schedule editor       │
   └────────────┬─────────────┘
                │ in-process call
                ▼
   ┌──────────────────────────┐
   │  server.home (always on) │   HP EliteDesk 800 G5 Mini, Ubuntu, Docker CE
   │  • life-app backend      │
   │  • WOL magic packet      │   only device that can wake the GPU PC
   │  • command forwarder     │   bearer token → C# agent on GPU PC
   │  • schedule executor     │   fires WOL / shutdown on a local timer
   │  • Caddy (this repo) ────┼──► LAN proxy for inference (bearer-gated)
   └────────────┬─────────────┘
                │ LAN · bearer token
                ▼
   ┌────────────────────────────────────────────────────────┐
   │  GPU PC  (Windows, RTX 3090, no Docker)                │
   │  ┌─────────────────────────────────────────────────┐   │
   │  │ C# Agent (tray, user session)                   │   │
   │  │ • NotifyIcon tray + status                      │   │
   │  │ • HttpListener control endpoint (LAN only)      │   │
   │  │ • Supervises ↓                                  │   │
   │  └──────┬──────────────┬──────────────┬────────────┘   │
   │         ▼              ▼              ▼                │
   │   LM Studio      dino-worker      cloudflared         │
   │   port 1234      FastAPI :8000    (inference tunnel)  │
   │   LLM · embed    DINOv3           optional, phase 3   │
   └────────────────────────────────────────────────────────┘
```

> **Why native, no Docker on the GPU PC?** SVM is disabled → no WSL2, no Docker
> Desktop. All inference processes run as native Windows executables / Python venvs
> managed by the C# agent.

---

## Components

### server.home — Caddy proxy (this repo, Docker)

Caddy is the only container in this repo. It runs on `server.home` and provides:
- **Bearer-token auth** for every request (same `SHARED_TOKEN` used across the LAN).
- **Routing** — `/v1/*` → LM Studio on GPU PC; `/dino/*` → dino-worker on GPU PC.
- A single port (`8088`) for the life-app backend, LAN clients, and (Phase 3) the
  cloudflared tunnel.

Built from the official `caddy:2-alpine` image; Watchtower auto-updates it.

### GPU PC — native processes (not containerized)

| Process | Binary | Port | Purpose |
|---|---|---|---|
| **LM Studio** | `LM Studio.exe` | 1234 | LLM (OpenAI-compatible), embeddings |
| **dino-worker** | `uvicorn` (Python venv) | 8000 | DINOv3 image embeddings, lazy-load + idle-evict |
| **cloudflared** | `cloudflared.exe` | — | Inference tunnel (Phase 3, optional) |
| **C# Agent** | `GpuAgent.exe` (tray) | configurable | Supervises above; control endpoint for `server.home` |

> **LM Studio auto-start:** disable LM Studio's built-in "start at login" so the
> C# agent owns the lifecycle — otherwise Studio stays resident during gaming.

### dino-worker Docker image (GHCR)

The `dino-worker/Dockerfile` is built by GitHub Actions and pushed to:

```
ghcr.io/jsfrouws/gpu-share-dino:latest
```

It runs **natively** on the GPU PC right now. The GHCR image is available for
future GPU-capable Linux nodes (e.g., when a Linux headless GPU server is added).

---

## The 24 GB VRAM budget

| Model | Variant | ~VRAM |
|---|---|---|
| LM Studio LLM | Gemma 3 27B Q3_K_M (recommended) | ~13 GB |
| LM Studio LLM | Gemma 3 27B Q4_K_M | ~16–17 GB |
| Embeddings | bge-m3 (in LM Studio) | ~1.3 GB |
| Whisper | medium int8 (in LM Studio / vox-box) | ~1.5 GB |
| TTS | CosyVoice2-0.5B | ~1 GB |
| DINOv3 | ViT-B/16 (dino-worker) | ~0.5 GB |

**Recommended (everything-warm):** Gemma Q3_K_M + lean models ≈ 18–19 GB, leaving
headroom for KV cache. Load/evict around gaming sessions using the C# agent's GPU
handler toggle.

---

## Setup

### server.home (Caddy proxy)

```bash
cp .env.example .env   # fill in SHARED_TOKEN and GPU_PC_HOST
docker compose up -d
```

Watchtower (running on `server.home`) auto-updates `gpu-share-caddy` on every
5-minute poll.

### GPU PC (native)

1. **LM Studio** — install from [lmstudio.ai](https://lmstudio.ai), load your models,
   enable *LM Studio Server* on port 1234. Disable the "start at login" option so
   the C# agent controls the lifecycle.

2. **dino-worker** — Python venv, no Docker required:
   ```bat
   python -m venv .venv
   .venv\Scripts\activate
   pip install -r dino-worker\requirements.txt
   set HF_TOKEN=<your-token>
   uvicorn app:app --host 0.0.0.0 --port 8000 --app-dir dino-worker
   ```
   The C# agent will manage this process; the above is for manual testing.

3. **C# Agent** — `GpuAgent.exe` (Phase 1 deliverable). Launched via Task Scheduler
   at logon. Requires auto-login enabled (Sysinternals Autologon) for WOL headless
   boots. Supervises LM Studio, dino-worker, and cloudflared; exposes a
   bearer-token-protected HttpListener endpoint on the LAN interface.

4. **cloudflared** (Phase 3) — install `cloudflared.exe`, configure it to tunnel
   port 8088 (Caddy on `server.home`) or directly to the GPU PC ports. See
   [docs/cloudflare.md](docs/cloudflare.md).

---

## Deployment — build → push → pull

```
git push main   →   GitHub Actions builds dino-worker → ghcr.io/jsfrouws/gpu-share-dino
                    (server.home's Watchtower pulls on next 5-min poll)
```

Caddy (`caddy:2-alpine`) auto-updates the same way via Watchtower.

---

## Endpoints (via Caddy on `server.home:8088`)

| Path | Backend | Purpose |
|---|---|---|
| `POST /v1/chat/completions` | LM Studio | LLM chat (incl. vision/OCR with a multimodal model) |
| `POST /v1/embeddings` | LM Studio | Text embeddings |
| `POST /v1/audio/speech` | LM Studio | TTS |
| `POST /dino/embed` | dino-worker | DINOv3 image embeddings |
| `POST /dino/v1/audio/transcriptions` | dino-worker | STT (faster-whisper; OpenAI-compatible — LM Studio doesn't ship STT yet) |
| `POST /dino/whisper/preload` | dino-worker | Warm whisper into memory (≤ `WHISPER_PRELOAD_MAX_MB` only) |
| `GET /dino/healthz` | dino-worker | Worker health / loaded state (dino + whisper) |

All requests require `Authorization: Bearer <SHARED_TOKEN>`.

---

## Security

Two layers:

1. **Life-app authentication** — public boundary. Control commands (wake, shutdown,
   GPU on/off) only originate from an authenticated life-app session.
2. **Bearer token (LAN)** — every request to Caddy carries `SHARED_TOKEN`. The C#
   agent endpoint is additionally protected by its own token and binds to the LAN
   interface only (never `0.0.0.0`).

For the public inference tunnel (Phase 3):
- **Cloudflare Access** is the real boundary; unauthenticated requests never reach
  the origin.
- See [docs/cloudflare.md](docs/cloudflare.md) for setup.

---

## Phasing

- **Phase 1 (current):** life-app menu + `server.home` relay + C# agent + GPU handler.
  Manual wake/sleep/GPU on-off and live status.
- **Phase 2:** schedule executor on `server.home`, schedule editor in the life app.
- **Phase 3:** `cloudflared` inference tunnel + Cloudflare Access for public model access.
