#!/usr/bin/env python3
"""Fake EXL3 worker for demux / continuous-batching protocol tests (no GPU)."""
from __future__ import annotations

import json
import queue
import sys
import threading
import time

_INBOX: queue.Queue[str | None] = queue.Queue()
_JOBS: dict[int, int] = {}  # id -> remaining tokens
_TOKENS_PER_JOB = 3


def _reply(obj: dict) -> None:
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def _reader() -> None:
    for line in sys.stdin:
        _INBOX.put(line)
    _INBOX.put(None)


def _stats() -> dict:
    return {
        "active": len(_JOBS),
        "pending": 0,
        "free_pages": 64,
        "max_batch_size": 32,
    }


def _tick() -> None:
    if not _JOBS:
        return
    events = []
    done = []
    for job_id, remaining in list(_JOBS.items()):
        events.append(
            {
                "id": job_id,
                "stage": "streaming",
                "text": f"t{remaining} ",
                "eos": False,
                "token_ids": [remaining],
                "ok": True,
            }
        )
        remaining -= 1
        if remaining <= 0:
            events.append(
                {
                    "id": job_id,
                    "stage": "streaming",
                    "text": "",
                    "eos": True,
                    "eos_reason": "max_new_tokens",
                    "new_tokens": _TOKENS_PER_JOB,
                    "completion_tokens": _TOKENS_PER_JOB,
                    "prompt_tokens": 2,
                    "token_ids": [],
                    "ok": True,
                }
            )
            done.append(job_id)
        else:
            _JOBS[job_id] = remaining
    for job_id in done:
        _JOBS.pop(job_id, None)
    if events:
        _reply({"events": events, "stats": _stats()})


def _handle(msg: dict) -> None:
    req_id = msg.get("id")
    cmd = (msg.get("cmd") or "").strip().lower()
    if cmd in ("ping", "health"):
        _reply({"ok": True, "id": req_id, "pong": True, "loaded": True})
        return
    if cmd == "load":
        _reply(
            {
                "ok": True,
                "id": req_id,
                "loaded": True,
                "path": msg.get("path"),
                "max_num_tokens": msg.get("max_num_tokens") or 8192,
                "max_batch_size": msg.get("max_batch_size") or 32,
            }
        )
        return
    if cmd == "unload":
        _JOBS.clear()
        _reply({"ok": True, "id": req_id, "unloaded": True})
        return
    if cmd in ("submit", "generate", "chat"):
        if req_id is None:
            _reply({"ok": False, "error": "id required"})
            return
        _JOBS[int(req_id)] = _TOKENS_PER_JOB
        _reply({"ok": True, "id": req_id, "accepted": True, "streaming": True})
        return
    if cmd == "cancel":
        if req_id is not None:
            _JOBS.pop(int(req_id), None)
        _reply({"ok": True, "id": req_id, "cancelled": True})
        return
    if cmd == "metrics":
        _reply({"ok": True, "id": req_id, **_stats(), "loaded": True})
        return
    _reply({"ok": False, "id": req_id, "error": f"unknown cmd: {cmd}"})


def main() -> None:
    threading.Thread(target=_reader, daemon=True).start()
    _reply({"ok": True, "ready": True, "protocol": "jsonl-v2"})
    while True:
        idle = len(_JOBS) == 0
        try:
            line = _INBOX.get(timeout=0.02 if not idle else None)
        except queue.Empty:
            _tick()
            continue
        if line is None:
            break
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            _reply({"ok": False, "error": "invalid json"})
            continue
        if isinstance(msg, dict):
            _handle(msg)
        _tick()
        time.sleep(0.01)


if __name__ == "__main__":
    main()
