"""DINOv3 image-embedding worker.

Lazy-loads the model on first request and evicts it after an idle timeout so the
GPU is free for GPUStack's models when DINOv3 isn't in use. OpenAI-style bearer
auth (same SHARED_TOKEN the Caddy front door enforces).
"""
import asyncio
import base64
import io
import os
import time

import httpx
import torch
from fastapi import Depends, FastAPI, File, Header, HTTPException, UploadFile
from PIL import Image
from pydantic import BaseModel
from transformers import AutoImageProcessor, AutoModel

MODEL_ID = os.environ.get("DINO_MODEL", "facebook/dinov3-vitb16-pretrain-lvd1689m")
IDLE_EVICT_SECONDS = int(os.environ.get("DINO_IDLE_EVICT_SECONDS", "600"))
SHARED_TOKEN = os.environ.get("SHARED_TOKEN", "")
DEVICE = "cuda" if torch.cuda.is_available() else "cpu"

app = FastAPI(title="dino-worker", version="1.0")

# Model state guarded by a lock; only one load/evict happens at a time.
_lock = asyncio.Lock()
_model = None
_processor = None
_last_used = 0.0


def _require_token(authorization: str = Header(default="")):
    if not SHARED_TOKEN:  # token unset -> auth disabled (LAN-only dev)
        return
    if authorization != f"Bearer {SHARED_TOKEN}":
        raise HTTPException(status_code=401, detail="Unauthorized")


async def _ensure_loaded():
    global _model, _processor, _last_used
    async with _lock:
        if _model is None:
            _processor = AutoImageProcessor.from_pretrained(MODEL_ID)
            _model = AutoModel.from_pretrained(MODEL_ID).to(DEVICE).eval()
        _last_used = time.time()


def _evict():
    global _model, _processor
    _model = None
    _processor = None
    if DEVICE == "cuda":
        torch.cuda.empty_cache()


async def _idle_evictor():
    if IDLE_EVICT_SECONDS <= 0:
        return
    while True:
        await asyncio.sleep(min(60, IDLE_EVICT_SECONDS))
        async with _lock:
            if _model is not None and time.time() - _last_used > IDLE_EVICT_SECONDS:
                _evict()


@app.on_event("startup")
async def _startup():
    asyncio.create_task(_idle_evictor())


def _embed_images(images):
    """Blocking inference; run in a worker thread. Returns list[list[float]]."""
    inputs = _processor(images=images, return_tensors="pt").to(DEVICE)
    with torch.inference_mode():
        out = _model(**inputs)
    # Prefer pooled CLS output; fall back to the CLS token of last_hidden_state.
    if getattr(out, "pooler_output", None) is not None:
        feats = out.pooler_output
    else:
        feats = out.last_hidden_state[:, 0]
    return feats.float().cpu().tolist()


async def _load_image_from_url(url: str) -> Image.Image:
    async with httpx.AsyncClient(timeout=30) as client:
        r = await client.get(url)
        r.raise_for_status()
        return Image.open(io.BytesIO(r.content)).convert("RGB")


class EmbedRequest(BaseModel):
    image_url: str | None = None
    image_b64: str | None = None


@app.get("/healthz")
async def healthz():
    return {
        "status": "ok",
        "model": MODEL_ID,
        "device": DEVICE,
        "loaded": _model is not None,
        "idle_evict_seconds": IDLE_EVICT_SECONDS,
    }


@app.post("/embed", dependencies=[Depends(_require_token)])
async def embed(
    body: EmbedRequest | None = None,
    file: UploadFile | None = File(default=None),
):
    """Embed one image supplied as a multipart file, a URL, or base64."""
    if file is not None:
        img = Image.open(io.BytesIO(await file.read())).convert("RGB")
    elif body and body.image_url:
        img = await _load_image_from_url(body.image_url)
    elif body and body.image_b64:
        raw = base64.b64decode(body.image_b64)
        img = Image.open(io.BytesIO(raw)).convert("RGB")
    else:
        raise HTTPException(status_code=400, detail="Provide file, image_url, or image_b64")

    await _ensure_loaded()
    vectors = await asyncio.get_event_loop().run_in_executor(None, _embed_images, [img])
    return {"model": MODEL_ID, "embedding": vectors[0], "dim": len(vectors[0])}
