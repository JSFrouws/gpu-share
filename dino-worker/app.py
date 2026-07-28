"""DINOv3 image-embedding + Whisper speech-to-text worker.

Lazy-loads each model on first request and evicts it after an idle timeout so
the GPU is free for LM Studio's models when not in use. OpenAI-style bearer
auth (same SHARED_TOKEN the Caddy front door enforces).

Whisper (faster-whisper) fills the gap until LM Studio ships its own
/v1/audio/transcriptions — the endpoint here is OpenAI-compatible so life-os
can switch backends without changes once that lands. `POST /whisper/preload`
warms the model into memory and PINS it (the idle evictor leaves pinned models
alone) so speech-to-text stays instant alongside the chat model; it self-gates
on free VRAM. `POST /whisper/evict` releases it again — the GPU agent calls
that before loading a chat model, so the LLM always gets first claim on VRAM.
"""
import asyncio
import base64
import glob
import io
import os
import site
import time

if os.name == "nt":
    # ctranslate2 (faster-whisper) needs cuBLAS/cuDNN DLLs at runtime. On this
    # Windows box they come from the pip packages (nvidia-cudnn-cu12) and
    # torch's bundled libs — register those dirs for DLL resolution up front,
    # before the first lazy faster_whisper import.
    for _sp in site.getsitepackages():
        for _pattern in (os.path.join(_sp, "nvidia", "*", "bin"),
                         os.path.join(_sp, "torch", "lib")):
            for _d in glob.glob(_pattern):
                try:
                    os.add_dll_directory(_d)
                except OSError:
                    pass

import httpx
import torch
from fastapi import Depends, FastAPI, File, Form, Header, HTTPException, UploadFile
from PIL import Image
from pydantic import BaseModel
from transformers import AutoImageProcessor, AutoModel

MODEL_ID = os.environ.get("DINO_MODEL", "facebook/dinov3-vitb16-pretrain-lvd1689m")
IDLE_EVICT_SECONDS = int(os.environ.get("DINO_IDLE_EVICT_SECONDS", "600"))
SHARED_TOKEN = os.environ.get("SHARED_TOKEN", "")
DEVICE = "cuda" if torch.cuda.is_available() else "cpu"

# large-v3 (~2.9 GB CT2 fp16): best accuracy, chosen over the ~1.6 GB turbo
# distil — a 3090 decodes short voice commands near-instantly either way.
WHISPER_MODEL = os.environ.get("WHISPER_MODEL", "large-v3")
WHISPER_COMPUTE = os.environ.get(
    "WHISPER_COMPUTE", "int8_float16" if DEVICE == "cuda" else "int8"
)
WHISPER_IDLE_EVICT_SECONDS = int(os.environ.get("WHISPER_IDLE_EVICT_SECONDS", "600"))
# What decides whether whisper can live next to the chat model is FREE VRAM,
# not the file size — so the preload gate measures the card *after* the LLM has
# claimed its share. 3000 MB of headroom on a 24 GB 3090 is the same thing as
# "the chat model stays under ~21 GB", which holds for the 26B/31B quants we
# run. WHISPER_PRELOAD_MAX_MB is an optional extra disk-size cap; 0 = off.
WHISPER_PRELOAD_MAX_MB = int(os.environ.get("WHISPER_PRELOAD_MAX_MB", "0"))
WHISPER_MIN_FREE_VRAM_MB = int(os.environ.get("WHISPER_MIN_FREE_VRAM_MB", "3000"))

app = FastAPI(title="dino-worker", version="1.1")

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


# ── Whisper (speech-to-text) ────────────────────────────────────────────────
# Same lazy-load + idle-evict pattern as DINO, its own lock so a transcription
# never blocks an embedding (and vice versa).
_whisper_lock = asyncio.Lock()
_whisper = None
_whisper_last_used = 0.0
# Pinned = preloaded on purpose to sit next to the chat model; the idle evictor
# leaves it alone so speech-to-text keeps answering instantly.
_whisper_pinned = False


def _free_vram_mb() -> float | None:
    """Free memory on the active CUDA device, or None on CPU / when unavailable."""
    if DEVICE != "cuda":
        return None
    try:
        free, _total = torch.cuda.mem_get_info()
        return free / (1024 * 1024)
    except Exception:  # noqa: BLE001
        return None


def _whisper_available() -> bool:
    try:
        import faster_whisper  # noqa: F401
        return True
    except ImportError:
        return False


def _whisper_model_dir_mb() -> float | None:
    """Size of the locally downloaded model snapshot, or None if not downloaded.
    Used for the preload gate — big models stay lazy-loaded."""
    try:
        from faster_whisper.utils import download_model
        path = download_model(WHISPER_MODEL, local_files_only=True)
    except Exception:
        return None
    total = 0
    for root, _dirs, files in os.walk(path):
        for f in files:
            try:
                total += os.path.getsize(os.path.join(root, f))
            except OSError:
                pass
    return total / (1024 * 1024)


def _load_whisper_blocking():
    from faster_whisper import WhisperModel
    return WhisperModel(WHISPER_MODEL, device=DEVICE, compute_type=WHISPER_COMPUTE)


async def _ensure_whisper():
    global _whisper, _whisper_last_used
    async with _whisper_lock:
        if _whisper is None:
            _whisper = await asyncio.get_event_loop().run_in_executor(
                None, _load_whisper_blocking
            )
        _whisper_last_used = time.time()


def _evict_whisper():
    global _whisper
    _whisper = None
    if DEVICE == "cuda":
        torch.cuda.empty_cache()


async def _whisper_idle_evictor():
    if WHISPER_IDLE_EVICT_SECONDS <= 0:
        return
    while True:
        await asyncio.sleep(min(60, WHISPER_IDLE_EVICT_SECONDS))
        async with _whisper_lock:
            if _whisper_pinned:
                continue      # deliberately resident next to the chat model
            if _whisper is not None and time.time() - _whisper_last_used > WHISPER_IDLE_EVICT_SECONDS:
                _evict_whisper()


def _transcribe_blocking(
    audio: bytes, language: str | None, prompt: str | None, words: bool = False
) -> dict:
    """Transcribe one clip. With `words`, whisper also returns per-word times.

    Word timestamps are what speaker diarization needs to attribute text: segment
    boundaries straddle turn boundaries, so joining on segments throws away the
    resolution the diarizer produced. vad_filter restores original timestamps, so
    the times stay on the same timebase as the diarizer's.
    """
    segments, _info = _whisper.transcribe(
        io.BytesIO(audio),
        language=language or None,
        initial_prompt=prompt or None,
        vad_filter=True,
        word_timestamps=bool(words),
    )
    texts: list[str] = []
    out_words: list[dict] = []
    for seg in segments:
        texts.append(seg.text.strip())
        for w in (getattr(seg, "words", None) or []):
            out_words.append({"w": w.word.strip(), "start": float(w.start),
                              "end": float(w.end), "p": round(float(w.probability), 3)})
    result = {"text": " ".join(t for t in texts if t).strip()}
    if words:
        result["words"] = out_words
    return result


@app.on_event("startup")
async def _startup():
    asyncio.create_task(_idle_evictor())
    asyncio.create_task(_whisper_idle_evictor())


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
        "free_vram_mb": round(_free_vram_mb()) if _free_vram_mb() is not None else None,
        "whisper": {
            "available": _whisper_available(),
            "model": WHISPER_MODEL,
            "compute": WHISPER_COMPUTE,
            "loaded": _whisper is not None,
            "pinned": _whisper_pinned,
            "idle_evict_seconds": WHISPER_IDLE_EVICT_SECONDS,
            "min_free_vram_mb": WHISPER_MIN_FREE_VRAM_MB,
        },
    }


@app.post("/v1/audio/transcriptions", dependencies=[Depends(_require_token)])
async def transcriptions(
    file: UploadFile = File(...),
    model: str = Form(""),          # accepted for OpenAI compatibility; ignored
    language: str = Form("nl"),
    prompt: str = Form(""),
    word_timestamps: bool = Form(False),
):
    """OpenAI-compatible speech-to-text (faster-whisper). Lazy-loads the model
    on first use; response shape matches /v1/audio/transcriptions ({"text"}).

    `word_timestamps` additionally returns a flat "words" list [{w,start,end,p}] —
    life-os's speaker pipeline joins those against diarization turns."""
    if not _whisper_available():
        raise HTTPException(
            status_code=501,
            detail="faster-whisper is not installed in this worker's venv "
                   "(pip install -r requirements.txt and restart).",
        )
    audio = await file.read()
    if not audio:
        raise HTTPException(status_code=400, detail="Empty audio")
    await _ensure_whisper()
    return await asyncio.get_event_loop().run_in_executor(
        None, _transcribe_blocking, audio, language, prompt, word_timestamps
    )


@app.post("/whisper/preload", dependencies=[Depends(_require_token)])
async def whisper_preload():
    """Warm whisper into VRAM and pin it there, so it answers instantly next to
    the chat model. Called by the GPU agent once the chat model has finished
    loading (and by life-os right after inference is enabled). Gated on the
    free VRAM left on the card at that moment — if the chat model took nearly
    everything, whisper stays lazy-loaded instead of fighting it for memory."""
    global _whisper_pinned
    if not _whisper_available():
        return {"preloading": False, "reason": "faster-whisper not installed"}
    size_mb = _whisper_model_dir_mb()
    if size_mb is None:
        return {"preloading": False, "reason": "model not downloaded yet (loads on first use)"}
    if WHISPER_PRELOAD_MAX_MB and size_mb > WHISPER_PRELOAD_MAX_MB:
        return {
            "preloading": False,
            "reason": f"model is {size_mb:.0f} MB > {WHISPER_PRELOAD_MAX_MB} MB cap "
                      "(loads on first use)",
        }
    if _whisper is not None:
        _whisper_pinned = True     # keep an already-loaded model resident
        return {"preloading": False, "pinned": True, "reason": "already loaded"}
    free_mb = _free_vram_mb()
    if free_mb is not None and free_mb < WHISPER_MIN_FREE_VRAM_MB:
        return {
            "preloading": False,
            "free_vram_mb": round(free_mb),
            "reason": f"only {free_mb:.0f} MB VRAM free, need "
                      f"{WHISPER_MIN_FREE_VRAM_MB} MB (loads on first use)",
        }
    _whisper_pinned = True
    asyncio.create_task(_ensure_whisper())
    return {"preloading": True, "pinned": True, "model": WHISPER_MODEL,
            "size_mb": round(size_mb),
            "free_vram_mb": round(free_mb) if free_mb is not None else None}


@app.post("/whisper/evict", dependencies=[Depends(_require_token)])
async def whisper_evict():
    """Unpin and drop whisper from VRAM. The GPU agent calls this before loading
    a chat model so the LLM gets first claim on the card; whisper is preloaded
    again afterwards if there's room left."""
    global _whisper_pinned
    async with _whisper_lock:
        was_loaded = _whisper is not None
        _whisper_pinned = False
        if was_loaded:
            _evict_whisper()
    return {"evicted": was_loaded}


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
