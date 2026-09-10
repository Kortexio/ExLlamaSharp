#!/usr/bin/env python3
"""
ExLlamaSharp EXL3 Python worker â€” JSON-lines over stdin/stdout.

Uses local third_party/exllamav3 (official Config/Model/Cache/Tokenizer/Generator).
This is the real CUDA EXL3 GEMM/attention path.

Protocol jsonl-v2: stdin and stdout are independent streams.
  .NET â†’ Python: submit / cancel / load / unload / metrics / tokenize / detokenize
  Python â†’ .NET: RPC replies (ok/error + id) and multiplexed event batches:
      {"events":[...], "stats":{"active":N,"pending":N,"free_pages":N,"max_batch_size":N}}

A reader thread only parses stdin into a queue. The main thread owns the
ExLlamaV3 Generator and calls iterate() so multiple Jobs share one forward pass.
"""
from __future__ import annotations

import json
import os
import queue
import re
import subprocess
import sys
import threading
import time
import traceback
from pathlib import Path
from typing import Any

_LEAK_MARKERS = (
    "<|eot_id|>",
    "<|eom_id|>",
    "<|end_of_text|>",
    "<|start_header_id|>",
    "<|end_header_id|>",
    "<|im_end|>",
    "<|im_start|>",
    "</s>",
    "<end_of_turn>",
    "<eos>",
)
_LEAK_RE = re.compile("|".join(re.escape(m) for m in _LEAK_MARKERS))
_MAX_LEAK_LEN = max(len(m) for m in _LEAK_MARKERS)

# Prefer the pip-installed prebuilt CUDA wheel (avoids JIT nvcc on Windows).
# Only use local third_party/exllamav3 when EXLLAMASHARP_USE_LOCAL_EXL3=1.
_WORKER_DIR = Path(__file__).resolve().parent
_REPO_ROOT = _WORKER_DIR.parent.parent
_EXL3_ROOT = _REPO_ROOT / "third_party" / "exllamav3"
_use_local = os.environ.get("EXLLAMASHARP_USE_LOCAL_EXL3", "").strip() in ("1", "true", "True", "yes")
if _use_local and _EXL3_ROOT.is_dir():
    p = str(_EXL3_ROOT)
    if p not in sys.path:
        sys.path.insert(0, p)

# Optional: EXLLAMAV3_ROOT override (also local / editable)
_env_root = os.environ.get("EXLLAMAV3_ROOT")
if _env_root and Path(_env_root).is_dir():
    p = str(Path(_env_root).resolve())
    if p not in sys.path:
        sys.path.insert(0, p)


_IO_LOCK = threading.Lock()


def _log(msg: str) -> None:
    print(f"[exl3_worker] {msg}", file=sys.stderr, flush=True)


def _reply(obj: dict[str, Any]) -> None:
    line = json.dumps(obj, ensure_ascii=False) + "\n"
    with _IO_LOCK:
        sys.stdout.write(line)
        sys.stdout.flush()


def _progress(phase: str, pct: int, heartbeat: bool = False) -> None:
    pct = max(0, min(100, int(pct)))
    STATE.load_phase = phase
    STATE.load_pct = pct
    _reply({"event": "load_progress", "phase": phase, "progress_pct": pct, "heartbeat": heartbeat})
    if not heartbeat:
        _log(f"load phase={phase} pct={pct}")


def _ok(req_id: Any = None, **kwargs: Any) -> None:
    payload = {"ok": True, **kwargs}
    if req_id is not None:
        payload["id"] = req_id
    _reply(payload)


def _err(message: str, req_id: Any = None, **kwargs: Any) -> None:
    payload = {"ok": False, "error": message, **kwargs}
    if req_id is not None:
        payload["id"] = req_id
    _reply(payload)


class _StreamSanitizer:
    """Hold back suffixes that might still grow into a leak/stop marker."""

    def __init__(self) -> None:
        self._buf = ""
        self.stopped = False

    def push(self, text: str) -> str:
        if self.stopped or not text:
            return ""
        self._buf += text
        m = _LEAK_RE.search(self._buf)
        if m:
            emit = self._buf[: m.start()]
            self._buf = ""
            self.stopped = True
            return emit
        hold = 0
        limit = min(_MAX_LEAK_LEN, len(self._buf))
        for i in range(1, limit + 1):
            suffix = self._buf[-i:]
            if any(marker.startswith(suffix) for marker in _LEAK_MARKERS):
                hold = i
        if hold:
            emit = self._buf[:-hold]
            self._buf = self._buf[-hold:]
            return emit
        emit = self._buf
        self._buf = ""
        return emit

    def flush(self) -> str:
        if self.stopped:
            self._buf = ""
            return ""
        emit = self._buf
        self._buf = ""
        return emit


class WorkerState:
    def __init__(self) -> None:
        self.config = None
        self.model = None
        self.cache = None
        self.tokenizer = None
        self.generator = None
        self.model_path: str | None = None
        self.max_num_tokens: int = 8192
        self.max_batch_size: int = 256
        self.max_chunk_size: int = 2048
        self.prompt_tokens: int = 0
        self.generated_tokens: int = 0
        self.finished: int = 0
        self.load_ts: float | None = None
        self.load_phase: str = ""
        self.load_pct: int = 0
        self.jobs: dict[Any, Any] = {}
        self.sanitizers: dict[Any, _StreamSanitizer] = {}
        self.prompt_lens: dict[Any, int] = {}
        self.t0: dict[Any, float] = {}
        self.loras: dict[str, Any] = {}
        self.draft_model_path: str | None = None
        self.draft_k: int = 5
        self.vision_model = None
        self.vision_capable: bool = False
        self.parallelism_mode: str = "none"

    @property
    def loaded(self) -> bool:
        return self.generator is not None


STATE = WorkerState()
_INBOX: queue.Queue[str | None] = queue.Queue()
_STDIN_CLOSED = False


def _unload_loras() -> None:
    for key, lora in list(STATE.loras.items()):
        try:
            lora.unload()
        except Exception as ex:
            _log(f"LoRA unload {key}: {ex}")
    STATE.loras.clear()


def _unload() -> None:
    import torch

    _unload_loras()
    gen = STATE.generator
    if gen is not None:
        try:
            gen.clear_queue()
        except Exception:
            pass
    STATE.jobs.clear()
    STATE.sanitizers.clear()
    STATE.prompt_lens.clear()
    STATE.t0.clear()
    if STATE.vision_model is not None:
        try:
            STATE.vision_model.unload()
        except Exception as ex:
            _log(f"vision unload: {ex}")
    STATE.vision_model = None
    STATE.vision_capable = False
    STATE.parallelism_mode = "none"
    if STATE.model is not None:
        try:
            STATE.model.unload()
        except Exception:
            pass
    STATE.config = None
    STATE.model = None
    STATE.cache = None
    STATE.tokenizer = None
    STATE.generator = None
    STATE.model_path = None
    STATE.load_ts = None
    STATE.draft_model_path = None
    if torch.cuda.is_available():
        torch.cuda.empty_cache()


def _harden_tp_spawn() -> None:
    """Windows JSONL worker + ExLlamaV3 TP children.

    The library default DISPATCH_TIMEOUT is 20s. A spawned child must boot a
    fresh Python, import torch/CUDA and answer the first VRAM probe. That often
    exceeds 20s when stdin/stdout are redirected to the .NET host. Failed loads
    also left TP children alive because STATE.model is only assigned after success.
    """
    import multiprocessing

    multiprocessing.freeze_support()
    try:
        multiprocessing.set_start_method("spawn", force=True)
    except RuntimeError:
        pass

    try:
        multiprocessing.set_executable(sys.executable)
    except Exception:
        pass

    # Route ExLlamaV3 TP logs to stderr so they do not corrupt JSONL stdout.
    try:
        from exllamav3.util import debug as _exl_debug

        def _tp_log(device, t: str) -> None:
            who = "main" if device is None else ("cpu" if device == -1 else f"cuda:{device}")
            _log(f"tp {who}: {t}")

        _exl_debug.log_tp = _tp_log
    except Exception as ex:
        _log(f"could not hook TP logs: {ex}")

    raw = os.environ.get("EXL3_TP_DISPATCH_TIMEOUT", "180").strip()
    try:
        timeout = max(20, int(raw))
    except ValueError:
        timeout = 180
    try:
        from exllamav3.model import model_tp

        old = int(getattr(model_tp, "DISPATCH_TIMEOUT", 20))
        model_tp.DISPATCH_TIMEOUT = max(old, timeout)
        _log(f"TP DISPATCH_TIMEOUT={model_tp.DISPATCH_TIMEOUT}s (library default was {old})")
    except Exception as ex:
        _log(f"could not raise TP dispatch timeout: {ex}")


def _unload_failed_model(model: Any, what: str) -> None:
    """Destroy TP children / CUDA shards after a failed load (not yet in STATE)."""
    if model is None:
        return
    try:
        model.unload()
        _log(f"unloaded {what} after failed load")
    except Exception as ex:
        _log(f"unload {what} after failed load: {ex}")


def _preload_torch_dlls() -> None:
    """Make torch/CUDA DLLs resolvable before importing exllamav3_ext on Windows."""
    if os.name != "nt":
        return
    try:
        import torch

        torch_lib = os.path.join(os.path.dirname(torch.__file__), "lib")
        if os.path.isdir(torch_lib):
            os.environ["PATH"] = torch_lib + os.pathsep + os.environ.get("PATH", "")
            add_dir = getattr(os, "add_dll_directory", None)
            if add_dir is not None:
                add_dir(torch_lib)
        # Force CUDA runtime load so dependent .pyd files resolve cublas/cudart.
        _ = torch.cuda.is_available()
    except Exception as ex:
        _log(f"torch DLL preload skipped: {ex}")


def _parse_gpu_split_gb(raw: Any, device_count: int) -> list[float] | None:
    if raw is None or str(raw).strip() == "":
        return None
    parts = [p.strip() for p in str(raw).split(",") if p.strip()]
    values: list[float] = []
    for p in parts:
        values.append(float(p))
    if len(values) != device_count:
        raise RuntimeError(
            f"gpu_split_gb has {len(values)} value(s) but {device_count} visible CUDA device(s)"
        )
    return values


def _smi_inventory() -> list[dict[str, Any]]:
    try:
        out = subprocess.check_output(
            [
                "nvidia-smi",
                "--query-gpu=index,name,memory.total",
                "--format=csv,noheader,nounits",
            ],
            text=True,
            timeout=4,
        )
    except Exception:
        return []
    rows: list[dict[str, Any]] = []
    for line in out.splitlines():
        parts = [p.strip() for p in line.split(",")]
        if len(parts) < 3 or not parts[0].isdigit():
            continue
        try:
            rows.append(
                {
                    "index": int(parts[0]),
                    "name": parts[1],
                    "memory_mib": float(parts[2]),
                }
            )
        except ValueError:
            continue
    return rows


def _smi_uuid_by_pci() -> dict[int, str]:
    try:
        out = subprocess.check_output(
            ["nvidia-smi", "--query-gpu=index,uuid", "--format=csv,noheader"],
            text=True,
            timeout=4,
        )
    except Exception:
        return {}
    mapping: dict[int, str] = {}
    for line in out.splitlines():
        parts = [p.strip() for p in line.split(",")]
        if len(parts) >= 2 and parts[0].isdigit():
            mapping[int(parts[0])] = parts[1]
    return mapping


def _visible_pci_ids(device_count: int) -> list[int]:
    vis = os.environ.get("CUDA_VISIBLE_DEVICES", "")
    if vis.strip():
        ids = [int(x) for x in vis.split(",") if x.strip().isdigit()]
        return ids[:device_count]
    return list(range(device_count))


def _assert_env_cvd_strongest_first() -> None:
    """Abort before torch if spawn CVD is not VRAM-desc (C# must remap)."""
    try:
        out = subprocess.check_output(
            ["nvidia-smi", "--query-gpu=index,memory.total", "--format=csv,noheader,nounits"],
            text=True,
            timeout=4,
        )
    except Exception as ex:
        _log(f"CVD strongest-first precheck skipped: {ex}")
        return
    mem: dict[int, float] = {}
    for line in out.splitlines():
        parts = [p.strip() for p in line.split(",")]
        if len(parts) >= 2 and parts[0].isdigit():
            mem[int(parts[0])] = float(parts[1])
    if len(mem) < 2:
        return
    vis = os.environ.get("CUDA_VISIBLE_DEVICES", "").strip()
    if not vis:
        raise RuntimeError(
            "cuda0_not_strongest: CUDA_VISIBLE_DEVICES is empty. "
            "The server must spawn the worker with a strongest-first remap."
        )
    ids = [int(x) for x in vis.split(",") if x.strip().isdigit()]
    if len(ids) < 2:
        return
    vis_mems = [mem.get(i, 0.0) for i in ids]
    if any(vis_mems[0] < m for m in vis_mems[1:]):
        raise RuntimeError(
            "cuda0_not_strongest: CUDA_VISIBLE_DEVICES is not strongest-first "
            f"(PCI order {ids}, VRAM MiB {vis_mems})."
        )


def _assert_cuda0_strongest() -> list[float]:
    """VRAM list in CUDA index order. Prefer nvidia-smi so we do not create
    a CUDA context on every device before tensor-parallel children spawn."""
    import torch

    if not torch.cuda.is_available():
        raise RuntimeError("torch.cuda.is_available() is false")
    n = int(torch.cuda.device_count())
    if n < 1:
        raise RuntimeError("no CUDA devices visible")

    uuids = _smi_uuid_by_pci()
    pci_ids = _visible_pci_ids(n)
    smi = _smi_inventory()
    smi_by_pci = {int(g["index"]): g for g in smi}
    mems: list[float] = []
    for i in range(n):
        pci = pci_ids[i] if i < len(pci_ids) else i
        g = smi_by_pci.get(pci)
        if g is not None:
            name = str(g["name"])
            mem = float(g["memory_mib"]) * (1024**2)
        else:
            mem = float(torch.cuda.get_device_properties(i).total_memory)
            name = f"cuda:{i}"
        mems.append(mem)
        uuid = uuids.get(pci, "")
        extra = f" uuid={uuid}" if uuid else ""
        _log(f"cuda:{i} {name} vram={mem / (1024**3):.2f} GiB pci={pci}{extra}")
    if any(mems[0] < m for m in mems[1:]):
        raise RuntimeError(
            "cuda0_not_strongest: cuda:0 is not the highest-VRAM GPU in the visible set. "
            "The server must spawn the worker with CUDA_DEVICE_ORDER=PCI_BUS_ID and "
            "CUDA_VISIBLE_DEVICES remapped strongest-first."
        )
    return mems


def _reserve_per_device(device_count: int) -> list[float]:
    """1.5 GiB on display GPUs (or all if unknown), 0.5 GiB otherwise."""
    display_by_index: dict[int, bool] = {}
    try:
        import subprocess

        out = subprocess.check_output(
            [
                "nvidia-smi",
                "--query-gpu=index,display_active",
                "--format=csv,noheader",
            ],
            text=True,
            timeout=4,
        )
        vis = os.environ.get("CUDA_VISIBLE_DEVICES", "")
        pci_ids = [int(x) for x in vis.split(",") if x.strip().isdigit()] if vis.strip() else list(range(device_count))
        flags: dict[int, bool] = {}
        for line in out.splitlines():
            parts = [p.strip() for p in line.split(",")]
            if len(parts) < 2:
                continue
            idx = int(parts[0])
            flags[idx] = parts[1].lower().startswith("enabled")
        for vis_i, pci in enumerate(pci_ids[:device_count]):
            display_by_index[vis_i] = bool(flags.get(pci))
    except Exception as ex:
        _log(f"display_active probe skipped: {ex}")
    if not display_by_index:
        return [1.5] * device_count
    return [1.5 if display_by_index.get(i) else 0.5 for i in range(device_count)]


def _build_weight_load_kwargs(
    mode: str,
    gpu_split_gb: Any,
    util: float,
    max_chunk_size: int,
) -> dict[str, Any]:
    mode = (mode or "none").strip().lower()
    mems = _assert_cuda0_strongest()
    n = len(mems)
    util = min(1.0, max(0.05, float(util)))
    override = _parse_gpu_split_gb(gpu_split_gb, n)
    reserve = _reserve_per_device(n)
    # ExLlamaV3 forbids use_per_device and reserve_per_device together — fold reserve into use.
    if override is not None:
        use = override
    else:
        use = [
            max(0.25, m / (1024**3) * util - r)
            for m, r in zip(mems, reserve)
        ]
    _log(f"parallelism_mode={mode} use_per_device={use} reserve_folded={reserve} util={util}")

    if mode in ("none", "single", ""):
        return {"device": 0}
    if mode in ("model", "mp"):
        raise RuntimeError("parallelism_mode=model is not supported")
    if n < 2:
        raise RuntimeError(f"parallelism_mode={mode} requires at least 2 visible CUDA devices")
    if mode in ("tensor", "tp"):
        return {
            "tensor_p": True,
            "tp_backend": "native",
            "tp_output_device": 0,
            "use_per_device": use,
            "max_chunk_size": int(max_chunk_size),
        }
    if mode in ("pipeline", "pipe", "pp"):
        return {
            "tensor_p": False,
            "use_per_device": use,
            "max_chunk_size": int(max_chunk_size),
        }
    raise RuntimeError(f"unknown parallelism_mode={mode}")


def _load(
    path: str,
    max_num_tokens: int = 8192,
    max_batch_size: int = 256,
    max_chunk_size: int = 2048,
    parallelism_mode: str = "none",
    gpu_memory_utilization: float = 0.90,
    gpu_split_gb: Any = None,
) -> None:
    _preload_torch_dlls()
    # Import here is safe only after warm-import in main(); keep for clarity.
    from exllamav3 import Config, Model, Cache, Tokenizer, Generator

    mode_norm = (parallelism_mode or "none").strip().lower()
    if mode_norm in ("tensor", "tp"):
        _harden_tp_spawn()

    _unload()
    path = str(Path(path).resolve())
    if not Path(path).is_dir():
        raise FileNotFoundError(f"Model directory not found: {path}")

    max_num_tokens = max(256, int(max_num_tokens))
    max_batch_size = max(1, int(max_batch_size))
    max_chunk_size = max(1, int(max_chunk_size))

    multi_gpu = mode_norm in ("tensor", "tp", "pipeline", "pipe", "pp")
    speculative = bool(STATE.draft_model_path)
    draft_path = STATE.draft_model_path
    draft_k = max(1, int(STATE.draft_k or 5))
    if multi_gpu and speculative:
        raise RuntimeError(
            "speculative decoding is not supported with tensor/pipeline parallelism"
        )

    _log(
        f"Loading EXL3 model from {path} "
        f"(max_num_tokens={max_num_tokens} max_batch_size={max_batch_size} "
        f"max_chunk_size={max_chunk_size}"
        f"{f' speculative draft={draft_path} k={draft_k}' if speculative else ''})"
    )
    t0 = time.perf_counter()
    hb_stop = threading.Event()

    def _hb() -> None:
        while not hb_stop.wait(1.5):
            _progress(STATE.load_phase or "weights", STATE.load_pct or 40, heartbeat=True)

    # Heartbeat writes JSONL on a side thread. On Windows that races multiprocessing
    # spawn of TP children and they freeze in spawn_main (~8 MB, never import torch).
    start_heartbeat = mode_norm not in ("tensor", "tp")
    if start_heartbeat:
        hb = threading.Thread(target=_hb, daemon=True)
        hb.start()
    else:
        hb = None
        _log("TP load: heartbeat disabled until weights are on the GPUs")
    model = None
    draft_model = None
    try:
        _progress("config", 10)
        config = Config.from_directory(path)
        _progress("model", 20)
        model = Model.from_config(config)
        _progress("cache", 25)
        # Cache must exist before model.load so TP workers receive split cache layers.
        cache = Cache(model, max_num_tokens=max_num_tokens)
        _progress("weights", 30)
        load_kw = _build_weight_load_kwargs(
            parallelism_mode, gpu_split_gb, gpu_memory_utilization, max_chunk_size
        )
        _try_load_weights(model, phase="weights", base=30, span=45, load_kwargs=load_kw)
        if hb is None:
            hb = threading.Thread(target=_hb, daemon=True)
            hb.start()
        _progress("tokenizer", 85)
        tokenizer = Tokenizer.from_config(config)

        draft_model = None
        draft_cache = None
        if speculative and draft_path:
            draft_path = str(Path(draft_path).resolve())
            if not Path(draft_path).is_dir():
                raise FileNotFoundError(f"Draft model directory not found: {draft_path}")
            _progress("draft", 86)
            _log(f"Loading draft model from {draft_path}")
            draft_config = Config.from_directory(draft_path)
            draft_model = Model.from_config(draft_config)
            draft_cache = Cache(draft_model, max_num_tokens=max_num_tokens)
            _try_load_weights(draft_model, phase="draft", base=86, span=4, load_kwargs={"device": 0})

        gen_kwargs = dict(
            model=model,
            cache=cache,
            tokenizer=tokenizer,
            max_batch_size=max_batch_size,
            max_chunk_size=max_chunk_size,
        )

        if draft_model is not None:
            draft_attempts = [
                dict(draft_model=draft_model, draft_cache=draft_cache, draft_k=draft_k),
                dict(draft_model=draft_model, draft_cache=draft_cache, num_draft_tokens=draft_k),
                dict(draft_model=draft_model, draft_cache=draft_cache),
            ]
            last_type_error: TypeError | None = None
            generator = None
            for extra in draft_attempts:
                try:
                    generator = Generator(**gen_kwargs, **extra)
                    _log(f"Generator accepted speculative kwargs: {list(extra.keys())}")
                    break
                except TypeError as te:
                    last_type_error = te
                    continue
            if generator is None:
                raise RuntimeError(
                    "speculative_enabled but exllamav3 Generator does not accept draft model kwargs "
                    f"(tried draft_model/draft_cache/draft_k). Last TypeError: {last_type_error}"
                )
        else:
            generator = Generator(**gen_kwargs)

        vision_model = None
        vision_capable = False
        try:
            _progress("vision", 90)
            vision_model = Model.from_config(config, component="vision")
        except Exception as ex:
            vision_model = None
            vision_capable = False
            _log(f"No vision component (text-only): {ex}")
        else:
            if multi_gpu:
                # Text tower can still use tensor/pipeline; vision shards are not TP-safe here.
                try:
                    vision_model.unload()
                except Exception:
                    pass
                vision_model = None
                vision_capable = False
                _log(
                    "Skipping vision tower under tensor/pipeline parallelism "
                    "(text chat works; multimodal needs ParallelismMode=none)."
                )
            else:
                _try_load_weights(vision_model, phase="vision", base=90, span=8)
                vision_capable = True
                _log("Vision component loaded (multimodal capable)")

        STATE.config = config
        STATE.model = model
        STATE.cache = cache
        STATE.tokenizer = tokenizer
        STATE.generator = generator
        STATE.vision_model = vision_model
        STATE.vision_capable = vision_capable
        STATE.parallelism_mode = mode_norm
        STATE.model_path = path
        STATE.max_num_tokens = max_num_tokens
        STATE.max_batch_size = max_batch_size
        STATE.max_chunk_size = max_chunk_size
        STATE.load_ts = time.time()
        _progress("ready", 100)
        _log(f"Loaded in {time.perf_counter() - t0:.2f}s vision_capable={vision_capable}")
        _warmup_generator()
    except Exception:
        _unload_failed_model(draft_model, "draft")
        _unload_failed_model(model, "model")
        raise
    finally:
        hb_stop.set()


def _warmup_generator() -> None:
    """Compile Triton/CUDA kernels once so the first real chat is not a 5s+ silent stall."""
    from exllamav3 import Job

    gen = STATE.generator
    tok = STATE.tokenizer
    if gen is None or tok is None:
        return
    try:
        t0 = time.perf_counter()
        ids = tok.encode("Hi", encode_special_tokens=True)
        try:
            job = Job(input_ids=ids, max_new_tokens=1, decode_special_tokens=False, identifier="warmup")
        except TypeError:
            job = Job(input_ids=ids, max_new_tokens=1, identifier="warmup")
        gen.enqueue(job)
        deadline = time.perf_counter() + 60
        while gen.num_remaining_jobs() > 0 and time.perf_counter() < deadline:
            gen.iterate()
        _log(f"warmup done in {time.perf_counter() - t0:.2f}s")
    except Exception as ex:
        _log(f"warmup skipped: {ex}")
        try:
            # Best-effort cancel leftover warmup job
            while gen is not None and gen.num_remaining_jobs() > 0:
                gen.iterate()
        except Exception:
            pass


def _try_load_weights(
    model: Any,
    phase: str,
    base: int,
    span: int,
    load_kwargs: dict[str, Any] | None = None,
) -> None:
    """Load tensors; use a progress callback when ExLlamaV3 exposes one."""
    extra = dict(load_kwargs or {})

    def cb(*args: Any) -> None:
        pct = base
        if len(args) >= 2 and isinstance(args[0], (int, float)) and isinstance(args[1], (int, float)) and args[1]:
            pct = base + int(span * float(args[0]) / float(args[1]))
        elif len(args) >= 1 and isinstance(args[0], (int, float)) and 0 <= float(args[0]) <= 1:
            pct = base + int(span * float(args[0]))
        _progress(phase, min(base + span, pct))

    try:
        model.load(callback=cb, **extra)
        return
    except TypeError:
        pass
    model.load(**extra)


_MAX_IMAGE_BYTES = 16 * 1024 * 1024


def _load_pil_image(url_or_data: str):
    """Decode data: URL or fetch http(s) into a PIL Image."""
    import base64
    import io
    import urllib.request

    from PIL import Image

    s = (url_or_data or "").strip()
    if not s:
        raise ValueError("empty image url")

    if s.startswith("data:"):
        # data:image/png;base64,....
        comma = s.find(",")
        if comma < 0:
            raise ValueError("invalid data URL")
        header = s[:comma]
        payload = s[comma + 1 :]
        if ";base64" not in header:
            raise ValueError("only base64 data URLs are supported")
        raw = base64.b64decode(payload, validate=False)
        if len(raw) > _MAX_IMAGE_BYTES:
            raise ValueError(f"image exceeds {_MAX_IMAGE_BYTES} bytes")
        return Image.open(io.BytesIO(raw)).convert("RGB")

    if s.startswith("http://") or s.startswith("https://"):
        req = urllib.request.Request(s, headers={"User-Agent": "ExLlamaSharp-exl3-worker/1.0"})
        with urllib.request.urlopen(req, timeout=30) as resp:
            raw = resp.read(_MAX_IMAGE_BYTES + 1)
        if len(raw) > _MAX_IMAGE_BYTES:
            raise ValueError(f"image exceeds {_MAX_IMAGE_BYTES} bytes")
        return Image.open(io.BytesIO(raw)).convert("RGB")

    # Local filesystem path
    p = Path(s)
    if p.is_file():
        return Image.open(p).convert("RGB")

    raise ValueError(f"unsupported image reference (need data:/http(s):/path): {s[:80]}")


def _encode_images(urls: list[str]) -> list[Any]:
    if not STATE.vision_capable or STATE.vision_model is None or STATE.tokenizer is None:
        raise RuntimeError(
            "vision_not_supported: model has no loaded vision component. "
            "Load an EXL3 VLM (e.g. Qwen3-VL / Gemma VL)."
        )
    embeddings = []
    for u in urls:
        pil = _load_pil_image(str(u))
        ie = STATE.vision_model.get_image_embeddings(tokenizer=STATE.tokenizer, image=pil)
        embeddings.append(ie)
    return embeddings


def _make_sampler(
    temperature: float,
    top_p: float,
    top_k: int = 0,
    min_p: float = 0.0,
    presence_penalty: float = 0.0,
    frequency_penalty: float = 0.0,
    seed: int | None = None,
):
    from exllamav3.generator.sampler import ComboSampler

    temp = float(temperature)
    if temp <= 0:
        return ComboSampler(temperature=0.0, top_k=1, top_p=1.0)
    kwargs = dict(
        temperature=temp,
        top_p=float(top_p) if top_p > 0 else 1.0,
        top_k=int(top_k) if top_k else 0,
        min_p=float(min_p) if min_p else 0.0,
    )
    want_penalties = abs(float(presence_penalty or 0.0)) > 1e-9 or abs(float(frequency_penalty or 0.0)) > 1e-9
    want_seed = seed is not None
    try:
        full = dict(kwargs)
        if want_penalties:
            full["presence_penalty"] = float(presence_penalty or 0.0)
            full["frequency_penalty"] = float(frequency_penalty or 0.0)
        if want_seed:
            full["seed"] = int(seed)
        return ComboSampler(**full)
    except TypeError as te:
        if want_penalties or want_seed:
            raise RuntimeError(
                "Sampler rejected presence_penalty/frequency_penalty/seed "
                f"(this exllamav3 ComboSampler build does not support them): {te}"
            ) from te
        return ComboSampler(**kwargs)


_ADAPTER_LOCK = threading.Lock()


def _stop_conditions(stop: Any) -> list:
    stops: list = []
    tok = STATE.tokenizer
    cfg = STATE.config
    if cfg is not None:
        eos = getattr(cfg, "eos_token_id", None)
        if eos is not None:
            stops.append(int(eos))
        eos_list = getattr(cfg, "eos_token_id_list", None)
        if eos_list:
            stops.extend(int(x) for x in eos_list if x is not None)
    if tok is not None:
        for piece in _LEAK_MARKERS:
            try:
                ids = tok.single_id(piece) if hasattr(tok, "single_id") else None
                if ids is not None:
                    stops.append(int(ids))
            except Exception:
                pass
    stops.extend(_LEAK_MARKERS)
    if isinstance(stop, str) and stop:
        stops.append(stop)
    elif isinstance(stop, list):
        for s in stop:
            if s is not None and s != "":
                stops.append(s)
    seen = set()
    out = []
    for s in stops:
        key = ("i", s) if isinstance(s, int) else ("s", s)
        if key not in seen:
            seen.add(key)
            out.append(s)
    return out


def _sanitize_completion(text: str) -> str:
    if not text:
        return text
    m = _LEAK_RE.search(text)
    if m:
        text = text[: m.start()]
    return text.rstrip()


def _looks_like_llama3() -> bool:
    tok = STATE.tokenizer
    if tok is None or not hasattr(tok, "single_id"):
        return False
    try:
        return tok.single_id("<|eot_id|>") is not None
    except Exception:
        return False


def _format_chatml(messages: list[dict], add_generation_prompt: bool = True) -> str:
    parts: list[str] = []
    for m in messages:
        role = (m.get("role") or "user").strip().lower()
        content = _message_text(m)
        parts.append(f"<|im_start|>{role}\n{content}<|im_end|>\n")
    if add_generation_prompt:
        parts.append("<|im_start|>assistant\n")
    return "".join(parts)


def _message_text(m: dict) -> str:
    content = m.get("content") or ""
    if isinstance(content, list):
        return " ".join(
            str(p.get("text", p)) if isinstance(p, dict) else str(p) for p in content
        )
    return str(content)


def _format_llama3_chat(messages: list[dict], add_generation_prompt: bool = True) -> str:
    parts = ["<|begin_of_text|>"]
    for m in messages:
        role = (m.get("role") or "user").strip().lower()
        content = _message_text(m)
        parts.append(f"<|start_header_id|>{role}<|end_header_id|>\n\n{content}<|eot_id|>")
    if add_generation_prompt:
        parts.append("<|start_header_id|>assistant<|end_header_id|>\n\n")
    return "".join(parts)


def _model_looks_qwen() -> bool:
    path = (STATE.model_path or "").lower()
    return "qwen" in path


def _try_hf_chat_template(messages: list[dict], add_generation_prompt: bool = True) -> str | None:
    tok = STATE.tokenizer
    if tok is None:
        return None
    hf = getattr(tok, "hf_tokenizer", None)
    if hf is None or not hasattr(hf, "apply_chat_template"):
        return None
    attempts = (
        dict(tokenize=False, add_generation_prompt=add_generation_prompt, enable_thinking=False),
        dict(tokenize=False, add_generation_prompt=add_generation_prompt),
    )
    last_err: Exception | None = None
    for kwargs in attempts:
        try:
            rendered = hf.apply_chat_template(messages, **kwargs)
            if isinstance(rendered, str) and rendered:
                return rendered
        except TypeError as ex:
            last_err = ex
            continue
        except Exception as ex:
            _log(f"HF chat template failed: {ex}")
            return None
    if last_err is not None:
        _log(f"HF chat template failed: {last_err}")
    return None


def _skip_qwen_thinking(prompt: str) -> str:
    """Qwen3 spends max_new_tokens inside <think> (often decoded as empty text)."""
    if not _model_looks_qwen():
        return prompt
    if "<think>" in prompt and "</think>" in prompt:
        return prompt
    if prompt.endswith("<|im_start|>assistant\n"):
        return prompt + "<think>\n</think>\n"
    return prompt


def _format_messages(messages: list[dict]) -> str:
    prompt = _try_hf_chat_template(messages, add_generation_prompt=True)
    used = "hf"
    if prompt is None:
        if _looks_like_llama3():
            used = "llama3"
            prompt = _format_llama3_chat(messages, add_generation_prompt=True)
        else:
            used = "chatml"
            prompt = _format_chatml(messages, add_generation_prompt=True)
    before = prompt
    prompt = _skip_qwen_thinking(prompt)
    if prompt != before:
        used = f"{used}+nothink"
    _log(f"chat template={used} chars={len(prompt)}")
    return prompt


def _tensor_to_list(value: Any) -> list[int]:
    if value is None:
        return []
    try:
        if hasattr(value, "detach"):
            value = value.detach().cpu()
        if hasattr(value, "tolist"):
            value = value.tolist()
        if isinstance(value, list) and value and isinstance(value[0], list):
            value = value[0]
        return [int(x) for x in value]
    except Exception:
        return []


def _stats() -> dict[str, int]:
    gen = STATE.generator
    if gen is None:
        return {
            "active": 0,
            "pending": 0,
            "free_pages": 0,
            "max_batch_size": STATE.max_batch_size,
        }
    free = 0
    try:
        free = int(gen.pagetable.num_unreferenced_pages())
    except Exception:
        pass
    return {
        "active": int(gen.num_active_jobs()),
        "pending": int(gen.num_pending_jobs()),
        "free_pages": free,
        "max_batch_size": int(getattr(gen, "max_batch_size", STATE.max_batch_size) or STATE.max_batch_size),
    }


def _prompt_len(ids: Any) -> int:
    if hasattr(ids, "numel"):
        return int(ids.numel())
    if hasattr(ids, "shape"):
        return int(ids.shape[-1])
    try:
        return len(ids)
    except TypeError:
        return 0


def _page_size() -> int:
    for obj in (getattr(STATE, "generator", None), getattr(STATE, "cache", None)):
        if obj is None:
            continue
        for attr in ("page_size", "max_page_size"):
            try:
                v = int(getattr(obj, attr, 0) or 0)
                if v > 0:
                    return v
            except Exception:
                pass
        try:
            pt = getattr(obj, "pagetable", None)
            if pt is not None:
                v = int(getattr(pt, "page_size", 0) or 0)
                if v > 0:
                    return v
        except Exception:
            pass
    return 256


def _enqueue(req_id: Any, prompt: str, msg: dict[str, Any]) -> None:
    from exllamav3 import Job

    if not STATE.loaded or STATE.generator is None or STATE.tokenizer is None:
        raise RuntimeError("No model loaded")

    images = msg.get("images")
    image_embeddings: list[Any] = []
    if isinstance(images, list) and len(images) > 0:
        image_embeddings = _encode_images([str(u) for u in images if u])
        placeholders = "\n".join(ie.text_alias for ie in image_embeddings) + "\n"
        prompt = placeholders + str(prompt)

    seed_raw = msg.get("seed")
    seed = int(seed_raw) if seed_raw is not None else None
    sampler = _make_sampler(
        float(msg.get("temperature", 0.7)),
        float(msg.get("top_p", 0.9)),
        int(msg.get("top_k") or 0),
        float(msg.get("min_p") or 0.0),
        float(msg.get("presence_penalty") or 0.0),
        float(msg.get("frequency_penalty") or 0.0),
        seed,
    )
    stops = _stop_conditions(msg.get("stop"))
    encode_kwargs: dict[str, Any] = dict(encode_special_tokens=True)
    if image_embeddings:
        encode_kwargs["embeddings"] = image_embeddings
    t_enc = time.perf_counter()
    try:
        input_ids = STATE.tokenizer.encode(prompt, **encode_kwargs)
    except TypeError:
        if image_embeddings:
            raise RuntimeError(
                "vision_not_supported: tokenizer.encode does not accept embeddings= "
                "(upgrade exllamav3 multimodal build)"
            )
        input_ids = STATE.tokenizer.encode(prompt, encode_special_tokens=True)
    n_prompt = _prompt_len(input_ids)
    _log(f"encode id={req_id} prompt_tokens={n_prompt} encode_ms={int((time.perf_counter()-t_enc)*1000)}")

    # Fail fast: oversized prompts used to block forever inside generator.enqueue → client 408.
    max_ctx = max(256, int(STATE.max_num_tokens or 8192))
    reserve = 64
    if n_prompt >= max_ctx - reserve:
        raise RuntimeError(
            f"prompt_too_long: prompt_tokens={n_prompt} max_ctx={max_ctx}. "
            "Start a new chat or raise Max batched tokens (num_ctx) and reload the model."
        )

    requested_new = int(msg.get("max_new_tokens") or 256)
    max_new = max(1, min(requested_new, max_ctx - n_prompt - 8))
    if max_new < requested_new:
        _log(f"clamp max_new {requested_new} -> {max_new} (ctx={max_ctx} prompt={n_prompt})")

    st = _stats()
    page = _page_size()
    pages_needed = max(1, (n_prompt + max_new + page - 1) // page)
    free_pages = int(st.get("free_pages") or 0)
    if free_pages > 0 and pages_needed > free_pages:
        raise RuntimeError(
            f"kv_cache_full: need ~{pages_needed} pages for prompt+reply "
            f"(prompt_tokens={n_prompt} max_new={max_new}) but free_pages={free_pages}. "
            "Wait for other jobs, start a new chat, or reload with larger Max batched tokens."
        )

    kwargs: dict[str, Any] = dict(
        input_ids=input_ids,
        max_new_tokens=max_new,
        sampler=sampler,
        stop_conditions=stops or None,
        decode_special_tokens=False,
        identifier=req_id,
    )
    if image_embeddings:
        kwargs["embeddings"] = image_embeddings
    try:
        job = Job(**kwargs, stop_on_loop=(16, 3))
    except TypeError:
        try:
            job = Job(**kwargs)
        except TypeError as te:
            if image_embeddings:
                raise RuntimeError(
                    f"vision_not_supported: Job rejected embeddings= ({te})"
                ) from te
            # strip embeddings and retry text-only only if we somehow got here without images
            kwargs.pop("embeddings", None)
            job = Job(**kwargs)

    STATE.jobs[req_id] = job
    STATE.sanitizers[req_id] = _StreamSanitizer()
    STATE.prompt_lens[req_id] = n_prompt
    STATE.t0[req_id] = time.perf_counter()
    STATE.prompt_tokens += n_prompt
    t_eq = time.perf_counter()
    STATE.generator.enqueue(job)
    st = _stats()
    _log(
        f"enqueue id={req_id} prompt_tokens={n_prompt} max_new={kwargs['max_new_tokens']} "
        f"pending={st.get('pending')} active={st.get('active')} free_pages={st.get('free_pages')} "
        f"enqueue_ms={int((time.perf_counter()-t_eq)*1000)}"
    )


def _cancel_job(req_id: Any) -> bool:
    job = STATE.jobs.pop(req_id, None)
    STATE.sanitizers.pop(req_id, None)
    STATE.prompt_lens.pop(req_id, None)
    STATE.t0.pop(req_id, None)
    if job is None or STATE.generator is None:
        return False
    try:
        STATE.generator.cancel(job)
    except Exception as ex:
        _log(f"cancel failed id={req_id}: {ex}")
        return False
    return True


def _event_from_result(result: dict[str, Any]) -> dict[str, Any] | None:
    ident = result.get("identifier")
    stage = result.get("stage") or ""
    eos = bool(result.get("eos"))
    ev: dict[str, Any] = {
        "id": ident,
        "stage": stage,
        "eos": eos,
        "serial": result.get("serial"),
        "ok": True,
    }
    if stage == "prefill":
        ev["curr_progress"] = result.get("curr_progress")
        ev["max_progress"] = result.get("max_progress")
        if result.get("max_progress") is not None:
            ev["prompt_tokens"] = int(result["max_progress"])
    if stage == "started":
        ev["prompt_tokens"] = STATE.prompt_lens.get(ident, 0)

    text = result.get("text") or ""
    san = STATE.sanitizers.get(ident)
    stopped_early = False
    if stage == "streaming" and text:
        if san is not None:
            text = san.push(text)
        ev["text"] = text
        ev["token_ids"] = _tensor_to_list(result.get("token_ids"))
        if san is not None and san.stopped and not eos:
            ev["eos"] = True
            ev["eos_reason"] = "stop_string"
            eos = True
            stopped_early = True
    elif stage == "streaming":
        ev["text"] = ""
        ev["token_ids"] = _tensor_to_list(result.get("token_ids"))

    if eos:
        extra = san.flush() if san is not None else ""
        if extra:
            ev["text"] = (ev.get("text") or "") + extra
        n_new = int(result.get("new_tokens") or 0)
        full = result.get("full_completion")
        if isinstance(full, str) and full:
            ev["full_completion"] = _sanitize_completion(full)
        ev["eos_reason"] = ev.get("eos_reason") or result.get("eos_reason")
        ev["new_tokens"] = n_new
        ev["completion_tokens"] = n_new
        ev["prompt_tokens"] = STATE.prompt_lens.get(ident, 0)
        t0 = STATE.t0.get(ident)
        if t0 is not None and n_new > 0:
            elapsed = time.perf_counter() - t0
            if elapsed > 0:
                ev["tokens_per_second"] = round(n_new / elapsed, 2)
        STATE.generated_tokens += n_new
        STATE.finished += 1
        job = STATE.jobs.pop(ident, None)
        STATE.sanitizers.pop(ident, None)
        STATE.prompt_lens.pop(ident, None)
        STATE.t0.pop(ident, None)
        if stopped_early and job is not None and STATE.generator is not None:
            try:
                STATE.generator.cancel(job)
            except Exception as ex:
                _log(f"early-stop cancel failed id={ident}: {ex}")
    return ev


def _emit_batch(results: list) -> None:
    events: list[dict[str, Any]] = []
    for result in results:
        if not isinstance(result, dict):
            continue
        ev = _event_from_result(result)
        if ev is not None:
            events.append(ev)
    if not events:
        return
    _reply({"events": events, "stats": _stats()})


def _fail_all_jobs(message: str) -> None:
    events = []
    for ident in list(STATE.jobs.keys()):
        events.append(
            {
                "id": ident,
                "stage": "streaming",
                "eos": True,
                "ok": False,
                "error": message,
            }
        )
    STATE.jobs.clear()
    STATE.sanitizers.clear()
    STATE.prompt_lens.clear()
    STATE.t0.clear()
    if STATE.generator is not None:
        try:
            STATE.generator.clear_queue()
        except Exception:
            pass
    if events:
        _reply({"events": events, "stats": _stats()})


def _normalize_messages(raw: Any) -> list[dict]:
    norm = []
    if not isinstance(raw, list):
        return norm
    for m in raw:
        if not isinstance(m, dict):
            continue
        norm.append(
            {
                "role": (m.get("role") or "user"),
                "content": m.get("content") or "",
            }
        )
    return norm


def handle(msg: dict[str, Any]) -> None:
    req_id = msg.get("id")
    cmd = (msg.get("cmd") or msg.get("op") or "").strip().lower()

    try:
        if cmd in ("ping", "health"):
            _ok(req_id, pong=True, loaded=STATE.loaded, model_path=STATE.model_path)
            return

        if cmd == "metrics":
            st = _stats()
            _ok(
                req_id,
                loaded=STATE.loaded,
                model_path=STATE.model_path,
                max_num_tokens=STATE.max_num_tokens,
                max_batch_size=STATE.max_batch_size,
                max_chunk_size=STATE.max_chunk_size,
                prompt_tokens=STATE.prompt_tokens,
                generated_tokens=STATE.generated_tokens,
                finished=STATE.finished,
                load_ts=STATE.load_ts,
                is_mock=False,
                vision_capable=bool(STATE.vision_capable),
                **st,
            )
            return

        if cmd == "load":
            path = msg.get("path")
            if not path:
                _err("path required", req_id)
                return
            max_tok = int(msg.get("max_num_tokens") or msg.get("max_tokens") or 8192)
            max_batch = int(msg.get("max_batch_size") or msg.get("max_num_seqs") or 256)
            max_chunk = int(msg.get("max_chunk_size") or 2048)
            env_cvd = os.environ.get("CUDA_VISIBLE_DEVICES")
            _log(
                f"CUDA_VISIBLE_DEVICES={env_cvd!r} CUDA_DEVICE_ORDER={os.environ.get('CUDA_DEVICE_ORDER')!r} "
                f"(load payload cuda_visible_devices={msg.get('cuda_visible_devices')!r} — env from spawn wins)"
            )
            mode = (msg.get("parallelism_mode") or "none").lower()
            util = float(msg.get("gpu_memory_utilization") or 0.90)
            split = msg.get("gpu_split_gb")
            if mode in ("tensor", "tp", "pipeline", "pipe", "pp") and msg.get("speculative_enabled"):
                _err(
                    "speculative decoding is not supported with tensor/pipeline parallelism",
                    req_id,
                )
                return
            if msg.get("speculative_enabled"):
                draft = msg.get("draft_model_path")
                draft_k = int(msg.get("draft_k") or 5)
                if not draft:
                    _err("speculative_enabled requires draft_model_path", req_id)
                    return
                STATE.draft_model_path = str(draft)
                STATE.draft_k = draft_k
                _log(f"Speculative decoding enabled draft={draft} k={draft_k}")
            else:
                STATE.draft_model_path = None
                STATE.draft_k = 5
            _load(
                path,
                max_tok,
                max_batch,
                max_chunk,
                parallelism_mode=mode,
                gpu_memory_utilization=util,
                gpu_split_gb=split,
            )
            _ok(
                req_id,
                loaded=True,
                path=STATE.model_path,
                max_num_tokens=STATE.max_num_tokens,
                max_batch_size=STATE.max_batch_size,
                max_chunk_size=STATE.max_chunk_size,
                speculative=bool(STATE.draft_model_path),
                vision_capable=bool(STATE.vision_capable),
            )
            return

        if cmd == "unload":
            _unload()
            _ok(req_id, unloaded=True)
            return

        if cmd == "load_adapter":
            if not STATE.loaded or STATE.model is None:
                _err("No model loaded", req_id)
                return
            if STATE.parallelism_mode in ("tensor", "tp", "pipeline", "pipe", "pp"):
                _err(
                    "LoRA adapters are not supported with tensor/pipeline parallelism",
                    req_id,
                )
                return
            path = msg.get("path")
            if not path:
                _err("path required", req_id)
                return
            adapter_id = (msg.get("adapter_id") or Path(path).name).strip()
            scaling = float(msg.get("scaling") or msg.get("lora_scaling") or 1.0)
            try:
                from exllamav3.model.lora import LoRA
            except Exception as ex:
                _err(f"LoRA API unavailable: {ex}", req_id)
                return
            if adapter_id in STATE.loras:
                try:
                    STATE.loras[adapter_id].unload()
                except Exception:
                    pass
                del STATE.loras[adapter_id]
            lora = LoRA.from_directory(STATE.model, str(Path(path).resolve()), lora_scaling=scaling)
            STATE.loras[adapter_id] = lora
            _ok(req_id, adapter_id=adapter_id, path=str(path), scaling=scaling, loaded=True)
            return

        if cmd == "unload_adapter":
            adapter_id = msg.get("adapter_id")
            if adapter_id:
                lora = STATE.loras.pop(str(adapter_id), None)
                if lora is not None:
                    try:
                        lora.unload()
                    except Exception as ex:
                        _log(f"unload_adapter: {ex}")
                _ok(req_id, unloaded=True, adapter_id=adapter_id)
            else:
                _unload_loras()
                _ok(req_id, unloaded=True, all=True)
            return

        if cmd == "list_adapters":
            items = [
                {"adapter_id": k, "path": getattr(v, "directory", None) or getattr(v, "config_path", None)}
                for k, v in STATE.loras.items()
            ]
            _ok(req_id, adapters=items)
            return

        if cmd == "tokenize":
            if not STATE.loaded:
                _err("No model loaded", req_id)
                return
            text = msg.get("text") or ""
            ids = STATE.tokenizer.encode(text, encode_special_tokens=bool(msg.get("special", True)))
            _ok(req_id, tokens=_tensor_to_list(ids))
            return

        if cmd == "detokenize":
            if not STATE.loaded:
                _err("No model loaded", req_id)
                return
            tokens = msg.get("tokens") or []
            text = STATE.tokenizer.decode(tokens, decode_special_tokens=bool(msg.get("special", False)))
            _ok(req_id, text=text if isinstance(text, str) else str(text))
            return

        if cmd == "cancel":
            cancelled = _cancel_job(req_id)
            _ok(req_id, cancelled=cancelled)
            return

        if cmd in ("submit", "generate", "chat"):
            if not STATE.loaded:
                _err("No model loaded", req_id)
                return
            images = msg.get("images")
            if isinstance(images, list) and len(images) > 0 and not STATE.vision_capable:
                _err(
                    "vision_not_supported: loaded model has no vision component. "
                    "Load an EXL3 VLM (Qwen3-VL, Gemma VL, etc.).",
                    req_id,
                    type="vision_not_supported",
                )
                return
            # LoRA: ExLlamaV3 applies adapters globally â€” serialize swaps vs concurrent submits.
            with _ADAPTER_LOCK:
                adapter_path = msg.get("adapter_path")
                try:
                    from exllamav3.model.lora import LoRA

                    if adapter_path:
                        aid = str(msg.get("adapter_id") or Path(adapter_path).name)
                        scaling = float(msg.get("adapter_scaling") or msg.get("scaling") or 1.0)
                        for key in list(STATE.loras.keys()):
                            if key != aid:
                                try:
                                    STATE.loras[key].unload()
                                except Exception as ex:
                                    _log(f"unload previous lora {key}: {ex}")
                                del STATE.loras[key]
                        if aid not in STATE.loras:
                            STATE.loras[aid] = LoRA.from_directory(
                                STATE.model, str(Path(adapter_path).resolve()), lora_scaling=scaling
                            )
                    else:
                        _unload_loras()
                except Exception as ex:
                    _err(f"adapter load failed: {ex}", req_id)
                    return
                messages = _normalize_messages(msg.get("messages"))
                prompt = msg.get("prompt")
                if messages:
                    prompt = _format_messages(messages)
                if prompt is None:
                    _err("prompt or messages required", req_id)
                    return
                try:
                    _enqueue(req_id, str(prompt), msg)
                except Exception as ex:
                    err_type = "vision_not_supported" if "vision_not_supported" in str(ex) else type(ex).__name__
                    _log(f"submit failed id={req_id} type={err_type}: {ex}")
                    # Also emit a stream event so .NET OpenStream clients fail fast (not 408).
                    _reply(
                        {
                            "events": [
                                {
                                    "id": req_id,
                                    "ok": False,
                                    "eos": True,
                                    "eos_reason": "error",
                                    "error": str(ex),
                                    "type": err_type,
                                }
                            ],
                            "stats": _stats(),
                        }
                    )
                    _err(str(ex), req_id, type=err_type)
                    return
                _ok(req_id, accepted=True, streaming=True)
            return

        _err(f"unknown cmd: {cmd}", req_id)
    except Exception as ex:
        _log(traceback.format_exc())
        _err(str(ex), req_id, type=type(ex).__name__)


def _reader() -> None:
    global _STDIN_CLOSED
    try:
        for line in sys.stdin:
            _INBOX.put(line)
    except Exception as ex:
        _log(f"stdin reader stopped: {ex}")
    finally:
        _STDIN_CLOSED = True
        _INBOX.put(None)


def _drain_inbox(*, block: bool) -> bool:
    """Dispatch pending stdin messages. Returns False when stdin has closed."""
    if block:
        line = _INBOX.get()
        if line is None:
            return False
        _dispatch_line(line)
    while True:
        try:
            line = _INBOX.get_nowait()
        except queue.Empty:
            return True
        if line is None:
            return False
        _dispatch_line(line)


def _dispatch_line(line: str) -> None:
    line = line.strip()
    if not line:
        return
    try:
        msg = json.loads(line)
    except json.JSONDecodeError as ex:
        _err(f"invalid json: {ex}")
        return
    if not isinstance(msg, dict):
        _err("message must be a JSON object")
        return
    handle(msg)


def serve() -> None:
    threading.Thread(target=_reader, daemon=True, name="exl3-stdin").start()
    _log("serve loop jsonl-v2")
    pending_since: float | None = None
    while True:
        gen = STATE.generator
        idle = gen is None or gen.num_remaining_jobs() == 0
        if not _drain_inbox(block=idle):
            break
        gen = STATE.generator
        if gen is None or gen.num_remaining_jobs() == 0:
            pending_since = None
            continue
        if gen.num_pending_jobs() and not gen.num_active_jobs():
            if pending_since is None:
                pending_since = time.perf_counter()
            elif time.perf_counter() - pending_since > 15:
                st = _stats()
                _fail_all_jobs(
                    "Job never started (KV cache / batch full). "
                    f"pending={st.get('pending')} free_pages={st.get('free_pages')}. "
                    "Lower max_tokens or reload with a larger Max batched tokens."
                )
                pending_since = None
                continue
        else:
            pending_since = None
        try:
            t0 = time.perf_counter()
            results = gen.iterate()
            elapsed = time.perf_counter() - t0
            if elapsed > 2:
                _log(f"iterate {elapsed:.1f}s remaining={gen.num_remaining_jobs()}")
        except Exception as ex:
            _log(traceback.format_exc())
            _fail_all_jobs(str(ex))
            continue
        if results:
            _emit_batch(results)
        else:
            _reply({"events": [], "stats": _stats()})


def main() -> None:
    # Warm-import CUDA/torch BEFORE the stdin reader thread starts.
    # On Windows, first-time torch import while another thread is blocked on
    # stdin/pipe I/O can deadlock and leave Admin UI stuck on "Loading…".
    try:
        _assert_env_cvd_strongest_first()
        _preload_torch_dlls()
        import torch  # noqa: F401
        from exllamav3 import Config, Model, Cache, Tokenizer, Generator  # noqa: F401
        cuda_ok = bool(torch.cuda.is_available())
        vis = os.environ.get("CUDA_VISIBLE_DEVICES")
        n = int(torch.cuda.device_count()) if cuda_ok else 0
        _log(f"warm import ok torch={getattr(torch, '__version__', '?')} cuda={cuda_ok} devices={n} CUDA_VISIBLE_DEVICES={vis!r}")
        if not cuda_ok and os.environ.get("EXLLAMASHARP_ALLOW_CPU", "").strip() != "1":
            _err(
                "torch.cuda.is_available() is false "
                f"(CUDA_VISIBLE_DEVICES={vis!r}). "
                "Use Settings → Multi-GPU device index 0 (not 1) on a single GPU, "
                "and run the Server from the Tray (user session), not LocalSystem."
            )
            raise SystemExit(2)
    except SystemExit:
        raise
    except Exception as ex:
        _log(f"warm import failed: {ex}")
        _err(str(ex))
        raise SystemExit(2)

    _log(f"ready repo={_REPO_ROOT} exl3={_EXL3_ROOT.is_dir()}")
    _ok(ready=True, protocol="jsonl-v2")
    serve()


if __name__ == "__main__":
    import multiprocessing

    multiprocessing.freeze_support()
    # Windows spawn re-runs this file as __main__ inside the TP child.
    inheriting = bool(getattr(multiprocessing.current_process(), "_inheriting", False))
    spawn_argv = any("spawn_main" in a for a in sys.argv)
    if inheriting or spawn_argv:
        _log(f"prepare-only TP child inheriting={inheriting} argv0={sys.argv[:2]!r}")
    else:
        main()
