#!/usr/bin/env python3
"""Smoke ExLlamaV3 multi-GPU load on whatever NVIDIA devices are visible.

Exit 0 = both tensor and pipeline loads succeeded with VRAM on every visible UUID.
Exit 77 = skip (fewer than 2 GPUs, or no EXL3 model). Not a pass.
Exit 2 = failed assertion (not strongest-first, a UUID stayed idle, load error).
"""
from __future__ import annotations

import os
import subprocess
import sys
import time
from pathlib import Path

SKIP = 77
FAIL = 2


def _smi_inventory() -> list[dict[str, object]]:
    try:
        out = subprocess.check_output(
            [
                "nvidia-smi",
                "--query-gpu=index,name,memory.total,uuid",
                "--format=csv,noheader,nounits",
            ],
            text=True,
            timeout=8,
        )
    except Exception as ex:
        print(f"nvidia-smi failed: {ex}")
        return []
    rows: list[dict[str, object]] = []
    for line in out.splitlines():
        parts = [p.strip() for p in line.split(",")]
        if len(parts) < 4:
            continue
        try:
            idx = int(parts[0])
            mem = float(parts[2])
        except ValueError:
            continue
        rows.append({"index": idx, "name": parts[1], "memory_mib": mem, "uuid": parts[3]})
    return rows


def _smi_used() -> dict[str, int]:
    try:
        out = subprocess.check_output(
            ["nvidia-smi", "--query-gpu=uuid,memory.used", "--format=csv,noheader,nounits"],
            text=True,
            timeout=8,
        )
    except Exception as ex:
        print(f"nvidia-smi used failed: {ex}")
        return {}
    used: dict[str, int] = {}
    for line in out.splitlines():
        parts = [p.strip() for p in line.split(",")]
        if len(parts) >= 2:
            used[parts[0]] = int(float(parts[1]))
    return used


def _remap_strongest_first(inv: list[dict[str, object]], requested: list[int]) -> list[int]:
    by_idx = {int(g["index"]): g for g in inv}
    valid = [i for i in requested if i in by_idx]
    valid = list(dict.fromkeys(valid))
    valid.sort(key=lambda i: (-float(by_idx[i]["memory_mib"]), i))
    return valid


def main() -> int:
    os.environ["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID"
    inv = _smi_inventory()
    if len(inv) < 2:
        print(f"SKIP: need at least 2 NVIDIA GPUs (found {len(inv)})")
        return SKIP

    raw = os.environ.get("CUDA_VISIBLE_DEVICES", "").strip()
    requested = [int(x) for x in raw.split(",") if x.strip().isdigit()] if raw else [int(g["index"]) for g in inv]
    remapped = _remap_strongest_first(inv, requested)
    if len(remapped) < 2:
        print(f"SKIP: visible set after remap has {len(remapped)} GPU(s); tensor/pipeline need ≥2")
        return SKIP

    os.environ["CUDA_VISIBLE_DEVICES"] = ",".join(str(i) for i in remapped)
    by_idx = {int(g["index"]): g for g in inv}
    visible_uuids = [str(by_idx[i]["uuid"]) for i in remapped]
    print(f"CUDA_VISIBLE_DEVICES={os.environ['CUDA_VISIBLE_DEVICES']} (strongest-first PCI)")
    for cuda_i, pci in enumerate(remapped):
        g = by_idx[pci]
        print(
            f"  cuda:{cuda_i} = PCI {pci} {g['name']} {float(g['memory_mib']) / 1024:.2f} GiB uuid={g['uuid']}"
        )

    import torch

    n = int(torch.cuda.device_count()) if torch.cuda.is_available() else 0
    print(f"torch.cuda.device_count={n}")
    if n < 2:
        print("FAIL: torch sees fewer than 2 devices after remap")
        return FAIL

    mems = [int(torch.cuda.get_device_properties(i).total_memory) for i in range(n)]
    for i in range(n):
        print(f"torch cuda:{i} {torch.cuda.get_device_name(i)} {mems[i] / (1024**3):.2f} GiB")
    if any(mems[0] < m for m in mems[1:]):
        print("FAIL cuda0_not_strongest")
        return FAIL

    model = os.environ.get("EXLS_SMOKE_MODEL", "").strip()
    if not model:
        data = os.environ.get("ProgramData", r"C:\ProgramData")
        root = Path(data) / "ExLlamaSharp" / "models"
        if root.is_dir():
            dirs = [p for p in root.iterdir() if p.is_dir() and (p / "config.json").is_file()]
            if dirs:
                model = str(dirs[0])
    if not model or not Path(model).is_dir():
        print("SKIP: set EXLS_SMOKE_MODEL to an EXL3 directory")
        return SKIP

    from exllamav3 import Config, Model, Cache, Tokenizer, Generator, Job

    util = float(os.environ.get("EXLS_GPU_UTIL", "0.85"))
    override = os.environ.get("EXLS_GPU_SPLIT_GB", "").strip()
    if override:
        use = [float(x) for x in override.split(",") if x.strip()]
        if len(use) != n:
            print(f"FAIL EXLS_GPU_SPLIT_GB has {len(use)} value(s) but {n} visible device(s)")
            return FAIL
    else:
        # Fold display reserve into use (ExLlamaV3 rejects use + reserve together).
        use = [max(0.25, m / (1024**3) * util - 1.5) for m in mems]
    print(f"loading {model} use_per_device={use}")

    def _run(label: str, tensor_p: bool) -> None:
        import gc

        gc.collect()
        torch.cuda.empty_cache()
        before = _smi_used()
        cfg = Config.from_directory(model)
        mdl = Model.from_config(cfg)
        cache = Cache(mdl, max_num_tokens=2048)
        run_use = list(use)
        spill = os.environ.get("EXLS_PIPELINE_SPILL_GB", "").strip()
        if not tensor_p and spill:
            run_use[0] = min(run_use[0], float(spill))
            print(f"{label} spill cap use_per_device={run_use}")
        kw: dict = dict(
            use_per_device=run_use,
            max_chunk_size=2048,
            tensor_p=tensor_p,
        )
        if tensor_p:
            kw["tp_backend"] = "native"
            kw["tp_output_device"] = 0
        t0 = time.perf_counter()
        mdl.load(**kw)
        print(f"{label} load {time.perf_counter() - t0:.1f}s")
        tok = Tokenizer.from_config(cfg)
        gen = Generator(model=mdl, cache=cache, tokenizer=tok, max_batch_size=1, max_chunk_size=2048)
        ids = tok.encode("Hi", encode_special_tokens=True)
        try:
            job = Job(input_ids=ids, max_new_tokens=4, identifier="smoke")
        except TypeError:
            job = Job(input_ids=ids, max_new_tokens=4)
        gen.enqueue(job)
        deadline = time.perf_counter() + 120
        while gen.num_remaining_jobs() > 0 and time.perf_counter() < deadline:
            gen.iterate()
        after = _smi_used()
        if not after:
            raise RuntimeError(f"{label}: nvidia-smi did not return per-UUID VRAM")
        idle: list[str] = []
        for uuid in visible_uuids:
            used = after.get(uuid, 0)
            delta = used - before.get(uuid, 0)
            print(f"  uuid {uuid} used {used} MiB (delta {delta})")
            if delta <= 64:
                idle.append(uuid)
        if idle:
            raise RuntimeError(f"{label}: VRAM did not grow on UUID(s) {idle}")
        try:
            mdl.unload()
        except Exception:
            pass
        del gen, cache, mdl

    _run("tensor", True)
    _run("pipeline", False)
    print("OK")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except SystemExit:
        raise
    except Exception as ex:
        print(f"FAIL {ex}")
        raise SystemExit(FAIL) from ex
