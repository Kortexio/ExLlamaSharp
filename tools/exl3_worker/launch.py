"""Thin __main__ so Windows TP spawn does not re-enter the JSONL server.

ExLlamaV3 starts one Process per extra GPU with multiprocessing spawn.
On Windows the child re-executes this file via run_path with
``__name__ == "__main__"`` *before* running ``mp_model_worker``. If we
called ``main()`` there, the child would start a second JSONL server and
never answer the TP pipe (TimeoutError: Timed out waiting for worker).
"""
from __future__ import annotations

import multiprocessing
import sys


def _is_spawn_prepare() -> bool:
    if any("spawn_main" in a for a in sys.argv):
        return True
    try:
        return bool(getattr(multiprocessing.current_process(), "_inheriting", False))
    except Exception:
        return False


if __name__ == "__main__":
    multiprocessing.freeze_support()
    if _is_spawn_prepare():
        sys.stderr.write("[exl3_worker] launch.py prepare-only (TP child)\n")
        sys.stderr.flush()
    else:
        from worker import main

        main()
