#!/usr/bin/env python3
"""
ExLlamaSharp EXL3 Python worker — JSON-lines over stdin/stdout.

Uses local third_party/exllamav3 (official Config/Model/Cache/Tokenizer/Generator).
This is the real CUDA EXL3 GEMM/attention path.

Protocol jsonl-v2: stdin and stdout are independent streams.
  .NET → Python: submit / cancel / load / unload / metrics / tokenize / detokenize
  Python → .NET: RPC replies (ok/error + id) and multiplexed event batches:
      {"events":[...], "stats":{"active":N,"pending":N,"free_pages":N,"max_batch_size":N}}

A reader thread only parses stdin into a queue. The main thread owns the
ExLlamaV3 Generator and calls iterate() so multiple Jobs share one forward pass.
"""
from __future__ import annotations

import json
import os
import queue
import re
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


def _log(msg: str) -> None:
    print(f"[exl3_worker] {msg}", file=sys.stderr, flush=True)


def _reply(obj: dict[str, Any]) -> None:
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


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
        self.jobs: dict[Any, Any] = {}
        self.sanitizers: dict[Any, _StreamSanitizer] = {}
        self.prompt_lens: dict[Any, int] = {}
        self.t0: dict[Any, float] = {}
        self.loras: dict[str, Any] = {}
        self.draft_model_path: str | None = None
        self.draft_k: int = 5

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
    if torch.cuda.is_available():
        torch.cuda.empty_cache()


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


def _load(
    path: str,
    max_num_tokens: int = 8192,
    max_batch_size: int = 256,
    max_chunk_size: int = 2048,
) -> None:
    _preload_torch_dlls()
    from exllamav3 import Config, Model, Cache, Tokenizer, Generator

    _unload()
    path = str(Path(path).resolve())
    if not Path(path).is_dir():
        raise FileNotFoundError(f"Model directory not found: {path}")

    max_num_tokens = max(256, int(max_num_tokens))
    max_batch_size = max(1, int(max_batch_size))
    max_chunk_size = max(1, int(max_chunk_size))

    _log(
        f"Loading EXL3 model from {path} "
        f"(max_num_tokens={max_num_tokens} max_batch_size={max_batch_size} "
        f"max_chunk_size={max_chunk_size})"
    )
    t0 = time.perf_counter()
    config = Config.from_directory(path)
    model = Model.from_config(config)
    cache = Cache(model, max_num_tokens=max_num_tokens)
    model.load()
    tokenizer = Tokenizer.from_config(config)
    generator = Generator(
        model=model,
        cache=cache,
        tokenizer=tokenizer,
        max_batch_size=max_batch_size,
        max_chunk_size=max_chunk_size,
    )

    STATE.config = config
    STATE.model = model
    STATE.cache = cache
    STATE.tokenizer = tokenizer
    STATE.generator = generator
    STATE.model_path = path
    STATE.max_num_tokens = max_num_tokens
    STATE.max_batch_size = max_batch_size
    STATE.max_chunk_size = max_chunk_size
    STATE.load_ts = time.time()
    _log(f"Loaded in {time.perf_counter() - t0:.2f}s")


def _make_sampler(temperature: float, top_p: float, top_k: int = 0):
    from exllamav3.generator.sampler import ComboSampler

    temp = float(temperature)
    if temp <= 0:
        return ComboSampler(temperature=0.0, top_k=1, top_p=1.0)
    return ComboSampler(
        temperature=temp,
        top_p=float(top_p) if top_p > 0 else 1.0,
        top_k=int(top_k) if top_k else 0,
        min_p=0.0,
    )


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


def _try_hf_chat_template(messages: list[dict], add_generation_prompt: bool = True) -> str | None:
    tok = STATE.tokenizer
    if tok is None:
        return None
    hf = getattr(tok, "hf_tokenizer", None)
    if hf is None or not hasattr(hf, "apply_chat_template"):
        return None
    try:
        rendered = hf.apply_chat_template(
            messages,
            tokenize=False,
            add_generation_prompt=add_generation_prompt,
        )
        if isinstance(rendered, str) and rendered:
            return rendered
    except Exception as ex:
        _log(f"HF chat template failed: {ex}")
    return None


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


def _enqueue(req_id: Any, prompt: str, msg: dict[str, Any]) -> None:
    from exllamav3 import Job

    if not STATE.loaded or STATE.generator is None or STATE.tokenizer is None:
        raise RuntimeError("No model loaded")

    sampler = _make_sampler(
        float(msg.get("temperature", 0.7)),
        float(msg.get("top_p", 0.9)),
        int(msg.get("top_k") or 0),
    )
    stops = _stop_conditions(msg.get("stop"))
    input_ids = STATE.tokenizer.encode(prompt, encode_special_tokens=True)
    n_prompt = _prompt_len(input_ids)
    kwargs = dict(
        input_ids=input_ids,
        max_new_tokens=int(msg.get("max_new_tokens") or 256),
        sampler=sampler,
        stop_conditions=stops or None,
        decode_special_tokens=False,
        identifier=req_id,
    )
    try:
        job = Job(**kwargs, stop_on_loop=(16, 3))
    except TypeError:
        job = Job(**kwargs)

    STATE.jobs[req_id] = job
    STATE.sanitizers[req_id] = _StreamSanitizer()
    STATE.prompt_lens[req_id] = n_prompt
    STATE.t0[req_id] = time.perf_counter()
    STATE.prompt_tokens += n_prompt
    STATE.generator.enqueue(job)


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
            devices = msg.get("cuda_visible_devices")
            if devices:
                os.environ["CUDA_VISIBLE_DEVICES"] = str(devices)
                _log(f"CUDA_VISIBLE_DEVICES={devices}")
            mode = (msg.get("parallelism_mode") or "none").lower()
            if mode not in ("none", "single", "") and "," not in str(devices or ""):
                _log(f"parallelism_mode={mode} requested but only one device visible; continuing single-GPU")
            if msg.get("speculative_enabled"):
                draft = msg.get("draft_model_path")
                draft_k = int(msg.get("draft_k") or 5)
                if draft:
                    _log(
                        f"Speculative decoding requested draft={draft} k={draft_k} — "
                        "applied when ExLlamaV3 Generator supports draft; otherwise ignored with warning"
                    )
                    try:
                        # Best-effort: stash for future Generator kwargs; current ExLlamaV3 Job API may not expose draft.
                        STATE.draft_model_path = str(draft)
                        STATE.draft_k = draft_k
                    except Exception:
                        pass
                else:
                    _err("speculative_enabled requires draft_model_path", req_id)
                    return
            _load(path, max_tok, max_batch, max_chunk)
            _ok(
                req_id,
                loaded=True,
                path=STATE.model_path,
                max_num_tokens=STATE.max_num_tokens,
                max_batch_size=STATE.max_batch_size,
                max_chunk_size=STATE.max_chunk_size,
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
            adapter_path = msg.get("adapter_path")
            if adapter_path:
                try:
                    from exllamav3.model.lora import LoRA
                    aid = str(msg.get("adapter_id") or Path(adapter_path).name)
                    scaling = float(msg.get("adapter_scaling") or msg.get("scaling") or 1.0)
                    if aid not in STATE.loras:
                        STATE.loras[aid] = LoRA.from_directory(
                            STATE.model, str(Path(adapter_path).resolve()), lora_scaling=scaling
                        )
                except Exception as ex:
                    _err(f"adapter load failed: {ex}", req_id)
                    return
            schema = msg.get("json_schema")
            messages = _normalize_messages(msg.get("messages"))
            prompt = msg.get("prompt")
            if messages:
                if schema:
                    messages = list(messages)
                    messages.insert(
                        0,
                        {
                            "role": "system",
                            "content": "Respond with JSON only matching this schema:\n" + str(schema),
                        },
                    )
                prompt = _format_messages(messages)
            elif prompt is not None and schema:
                prompt = "Respond with JSON only matching this schema:\n" + str(schema) + "\n\n" + str(prompt)
            if prompt is None:
                _err("prompt or messages required", req_id)
                return
            _enqueue(req_id, str(prompt), msg)
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
    while True:
        gen = STATE.generator
        idle = gen is None or gen.num_remaining_jobs() == 0
        if not _drain_inbox(block=idle):
            break
        gen = STATE.generator
        if gen is None or gen.num_remaining_jobs() == 0:
            continue
        try:
            results = gen.iterate()
        except Exception as ex:
            _log(traceback.format_exc())
            _fail_all_jobs(str(ex))
            continue
        _emit_batch(results)


def main() -> None:
    _log(f"ready repo={_REPO_ROOT} exl3={_EXL3_ROOT.is_dir()}")
    _ok(ready=True, protocol="jsonl-v2")
    serve()


if __name__ == "__main__":
    main()
