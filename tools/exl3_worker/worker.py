#!/usr/bin/env python3
"""
ExLlamaSharp EXL3 Python worker — JSON-lines over stdin/stdout.

Uses local third_party/exllamav3 (official Config/Model/Cache/Tokenizer/Generator).
This is the real CUDA EXL3 GEMM/attention path.

Protocol: one JSON object per line on stdin; one JSON response per line on stdout.
Stderr is for logs only.
"""
from __future__ import annotations

import json
import os
import re
import sys
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


class WorkerState:
    def __init__(self) -> None:
        self.config = None
        self.model = None
        self.cache = None
        self.tokenizer = None
        self.generator = None
        self.model_path: str | None = None
        self.max_num_tokens: int = 8192
        self.prompt_tokens: int = 0
        self.generated_tokens: int = 0
        self.finished: int = 0
        self.load_ts: float | None = None

    @property
    def loaded(self) -> bool:
        return self.generator is not None


STATE = WorkerState()


def _unload() -> None:
    import torch

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


def _load(path: str, max_num_tokens: int = 8192) -> None:
    _preload_torch_dlls()
    from exllamav3 import Config, Model, Cache, Tokenizer, Generator

    _unload()
    path = str(Path(path).resolve())
    if not Path(path).is_dir():
        raise FileNotFoundError(f"Model directory not found: {path}")

    _log(f"Loading EXL3 model from {path} (max_num_tokens={max_num_tokens})")
    t0 = time.perf_counter()
    config = Config.from_directory(path)
    model = Model.from_config(config)
    cache = Cache(model, max_num_tokens=int(max_num_tokens))
    model.load()
    tokenizer = Tokenizer.from_config(config)
    generator = Generator(model=model, cache=cache, tokenizer=tokenizer)

    STATE.config = config
    STATE.model = model
    STATE.cache = cache
    STATE.tokenizer = tokenizer
    STATE.generator = generator
    STATE.model_path = path
    STATE.max_num_tokens = int(max_num_tokens)
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


def _generate(
    prompt: str,
    max_new_tokens: int = 256,
    temperature: float = 0.7,
    top_p: float = 0.9,
    top_k: int = 0,
    stop: Any = None,
) -> dict[str, Any]:
    if not STATE.loaded:
        raise RuntimeError("No model loaded")

    sampler = _make_sampler(temperature, top_p, top_k)
    stops = _stop_conditions(stop)
    t0 = time.perf_counter()

    prompt_ids = STATE.tokenizer.encode(prompt, encode_special_tokens=True)
    if hasattr(prompt_ids, "numel"):
        n_prompt = int(prompt_ids.numel())
    elif hasattr(prompt_ids, "shape"):
        n_prompt = int(prompt_ids.shape[-1])
    else:
        n_prompt = len(prompt_ids)

    gen_kwargs = dict(
        prompt=prompt,
        max_new_tokens=int(max_new_tokens),
        sampler=sampler,
        stop_conditions=stops or None,
        encode_special_tokens=True,
        decode_special_tokens=False,
        add_bos=False,
        completion_only=True,
    )
    try:
        text = STATE.generator.generate(**gen_kwargs, stop_on_loop=(16, 3))
    except TypeError:
        text = STATE.generator.generate(**gen_kwargs)
    if isinstance(text, list):
        text = text[0] if text else ""
    text = _sanitize_completion(str(text))

    # Approximate completion token count via encode
    try:
        comp_ids = STATE.tokenizer.encode(text, encode_special_tokens=False)
        if hasattr(comp_ids, "numel"):
            n_comp = int(comp_ids.numel())
        elif hasattr(comp_ids, "shape"):
            n_comp = int(comp_ids.shape[-1])
        else:
            n_comp = len(comp_ids)
        token_ids = comp_ids.tolist() if hasattr(comp_ids, "tolist") else list(comp_ids)
        if token_ids and isinstance(token_ids[0], list):
            token_ids = token_ids[0]
    except Exception:
        n_comp = max(1, len(text.split()))
        token_ids = []

    STATE.prompt_tokens += n_prompt
    STATE.generated_tokens += n_comp
    STATE.finished += 1
    elapsed = time.perf_counter() - t0
    tps = n_comp / elapsed if elapsed > 0 else 0.0

    return {
        "text": text,
        "prompt_tokens": n_prompt,
        "completion_tokens": n_comp,
        "token_ids": token_ids,
        "duration_ms": round(elapsed * 1000, 2),
        "tokens_per_second": round(tps, 2),
    }


def handle(msg: dict[str, Any]) -> None:
    req_id = msg.get("id")
    cmd = (msg.get("cmd") or msg.get("op") or "").strip().lower()

    try:
        if cmd in ("ping", "health"):
            _ok(req_id, pong=True, loaded=STATE.loaded, model_path=STATE.model_path)
            return

        if cmd == "metrics":
            _ok(
                req_id,
                loaded=STATE.loaded,
                model_path=STATE.model_path,
                max_num_tokens=STATE.max_num_tokens,
                prompt_tokens=STATE.prompt_tokens,
                generated_tokens=STATE.generated_tokens,
                finished=STATE.finished,
                load_ts=STATE.load_ts,
                is_mock=False,
            )
            return

        if cmd == "load":
            path = msg.get("path")
            if not path:
                _err("path required", req_id)
                return
            max_tok = int(msg.get("max_num_tokens") or msg.get("max_tokens") or 8192)
            _load(path, max_tok)
            _ok(req_id, loaded=True, path=STATE.model_path, max_num_tokens=STATE.max_num_tokens)
            return

        if cmd == "unload":
            _unload()
            _ok(req_id, unloaded=True)
            return

        if cmd == "tokenize":
            if not STATE.loaded:
                _err("No model loaded", req_id)
                return
            text = msg.get("text") or ""
            ids = STATE.tokenizer.encode(text, encode_special_tokens=bool(msg.get("special", True)))
            if hasattr(ids, "tolist"):
                ids = ids.tolist()
            if ids and isinstance(ids[0], list):
                ids = ids[0]
            _ok(req_id, tokens=[int(x) for x in ids])
            return

        if cmd == "detokenize":
            if not STATE.loaded:
                _err("No model loaded", req_id)
                return
            tokens = msg.get("tokens") or []
            text = STATE.tokenizer.decode(tokens, decode_special_tokens=bool(msg.get("special", False)))
            _ok(req_id, text=text if isinstance(text, str) else str(text))
            return

        if cmd == "generate":
            prompt = msg.get("prompt")
            if prompt is None:
                _err("prompt required", req_id)
                return
            result = _generate(
                prompt=str(prompt),
                max_new_tokens=int(msg.get("max_new_tokens") or 256),
                temperature=float(msg.get("temperature", 0.7)),
                top_p=float(msg.get("top_p", 0.9)),
                top_k=int(msg.get("top_k") or 0),
                stop=msg.get("stop"),
            )
            _ok(req_id, **result)
            return

        if cmd == "chat":
            messages = msg.get("messages")
            if not isinstance(messages, list) or not messages:
                _err("messages required", req_id)
                return
            # Normalize roles/content
            norm = []
            for m in messages:
                if not isinstance(m, dict):
                    continue
                norm.append(
                    {
                        "role": (m.get("role") or "user"),
                        "content": m.get("content") or "",
                    }
                )
            prompt = _try_hf_chat_template(norm, add_generation_prompt=True)
            used = "hf"
            if prompt is None:
                if _looks_like_llama3():
                    used = "llama3"
                    prompt = _format_llama3_chat(norm, add_generation_prompt=True)
                else:
                    used = "chatml"
                    prompt = _format_chatml(norm, add_generation_prompt=True)
            _log(f"chat template={used} chars={len(prompt)}")
            result = _generate(
                prompt=prompt,
                max_new_tokens=int(msg.get("max_new_tokens") or 256),
                temperature=float(msg.get("temperature", 0.7)),
                top_p=float(msg.get("top_p", 0.9)),
                top_k=int(msg.get("top_k") or 0),
                stop=msg.get("stop"),
            )
            _ok(req_id, prompt=prompt, **result)
            return

        _err(f"unknown cmd: {cmd}", req_id)
    except Exception as ex:
        _log(traceback.format_exc())
        _err(str(ex), req_id, type=type(ex).__name__)


def main() -> None:
    _log(f"ready repo={_REPO_ROOT} exl3={_EXL3_ROOT.is_dir()}")
    _ok(ready=True, protocol="jsonl-v1")
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError as ex:
            _err(f"invalid json: {ex}")
            continue
        if not isinstance(msg, dict):
            _err("message must be a JSON object")
            continue
        handle(msg)


if __name__ == "__main__":
    main()
