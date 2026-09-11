# ExLlamaSharp Troubleshooting

Aligns with the UI page **Diagnostics** (`/diagnostics`) and `GET /health`.

## How to use Diagnostics

1. Open **http://&lt;host&gt;:14563/diagnostics**.
2. Click **Run health check** — overall status and per-component cards.
3. Click **Readiness probe** — whether the host is ready to take traffic.

Components reported by `HealthService`:

| Component | Healthy means | Degraded / unhealthy clues |
|-----------|---------------|----------------------------|
| `database` | SQLite reachable | File locked, bad path, disk full |
| `engine` | Engine object OK (Mock or Native) | Init / native load failure |
| `inference` | Model loaded + metrics OK | **No model loaded** (degraded) |
| `disk` | Enough free space on models drive | &lt;20 GB degraded, &lt;5 GB unhealthy |

## Common issues (same list as Diagnostics UI)

### No model loaded

**Symptom:** Inference degraded; chat/API returns errors or empty capability.

**Fix:** Open **Models**, import or pull a model, then **Load**. Optionally enable load-on-startup in Settings.

### nvidia-smi missing

**Symptom:** About / GPU widgets show mock GPU; real VRAM unknown.

**Fix:** Install current NVIDIA drivers so `nvidia-smi` is on PATH. Reboot if needed. ExLlamaSharp can still run in mock mode for UI/API development (`ForceMockEngine`).

### Database unhealthy

**Symptom:** `/health` → database unhealthy; UI may fail to list keys/models.

**Fix:**

- Ensure `%ProgramData%\ExLlamaSharp` is writable.
- Check disk space on that volume.
- Confirm no other process has an exclusive lock on `app.db`.
- As last resort, restore from a backup ZIP (see admin guide).

### Port in use

**Symptom:** Service fails to start or browser cannot connect.

**Fix:** Change bind port under **Settings → Network**, or stop the other process using 14563. Update firewall rules if LAN access is enabled. Restart the Windows service after changing port.

### API 401

**Symptom:** `/v1/chat/completions` returns unauthorized.

**Fix:** Create a key on **API Keys** and send `Authorization: Bearer …`. Do not use an expired/deleted key. UI session auth is separate from API keys for programmatic clients.

## Other frequent problems

### Service installed but page blank

- `Get-Service ExLlamaSharp` → should be Running.
- Check Event Viewer / `%ProgramData%\ExLlamaSharp\logs`.
- Run `packaging\Check-Requirements.ps1`.

### CUDA / OOM on load

- Use a smaller EXL3/quantized model.
- Lower `GpuMemoryUtilization` slightly.
- Ensure only intended devices in `CudaVisibleDevices`.
- Close other GPU apps (browsers with HW accel, games).

### Tensor parallel: Timed out waiting for worker

**Symptom:** Load with `parallelism_mode=tensor` fails with `TimeoutError: Timed out waiting for worker` (ExLlamaV3 `model_tp.py`). nvidia-smi stays almost idle; leftover `python ... spawn_main` processes sit at ~8 MB.

**Cause:** Tensor parallel starts extra Python processes. On Windows those children re-enter the worker script and never become TP workers when the host is the long-lived JSONL process. A console `python -c` load can succeed while the Admin/Server load fails. Pipeline mode does not spawn those children.

**Fix:** On Windows the server now **coerces tensor → pipeline** on save/load. For Qwen3-32B 4.0bpw on 12 GB + 8 GB use `GpuSplitGb=11,7` (or `10.8,6.2`) and keep **Max batched tokens** at 2048–4096 — 10240 plus the 32B weights does not fit. Recycle the worker after a failed load (Save on Settings, or restart the Server) so zombie `spawn_main` processes are gone.

### VLM + multi-GPU: vision skipped

**Symptom:** Models such as `Qwen3.8-27B-exl3` (`Qwen3_5ForConditionalGeneration` + `vision_config`) used to fail load under tensor/pipeline with `vision models are not supported…`.

**Behaviour now:** The language model still loads across GPUs; the vision tower is skipped (`vision_capable=false`). Text chat works. Image/video inputs need **ParallelismMode=none** (and enough VRAM on one card), then reload.

### Slow tokens / queue buildup

- Check `GET /metrics` (`jobs_waiting`, `tokens_per_second`).
- Reduce concurrent clients or raise capacity settings carefully.
- Prefer native CUDA build over stub/mock for real throughput.

### Webhook not firing

- Confirm `WebhookUrl` / secret in Settings.
- Receiver must return 2xx; service retries 3 times.
- Validate HMAC header if your endpoint verifies signatures.

### Multi-GPU not used

- `ParallelismMode` still `none`, or only one index in `CudaVisibleDevices`.
- `nvidia-smi` index is **not** `cuda:N` after remap — check worker log (`cuda:0` = highest VRAM).
- Production path is the **EXL3 Python worker**, not `exllamasharp_native.dll` / mock.
- After Settings save the worker should recycle automatically; if VRAM is still on one UUID only, reload the model and read `use_per_device` in the worker log.

### OOM on the smaller GPU

- Auto-split is `VRAM[i] × GpuMemoryUtilization`. A 6 GB card with util 0.9 only has ~5.4 GB for weights.
- Display GPU keeps 1.5 GB (others 0.5 GB) folded into `use_per_device` — ExLlamaV3 does not accept use and reserve together. Lower util or set `GpuSplitGb` (remapped order, e.g. `10,4.5`).
- Tensor / pipeline need ≥2 devices. Speculative, vision, and LoRA are rejected under those modes.

## Quick CLI checks

```powershell
# Requirements
.\packaging\Check-Requirements.ps1

# Health JSON
Invoke-RestMethod http://localhost:14563/health

# Ready
Invoke-RestMethod http://localhost:14563/ready
```

## Still stuck?

Gather: Diagnostics screenshot or `/health` JSON, `/api/v1/about` JSON, `nvidia-smi` output, and whether you are on MockEngine or native DLL. See [admin-guide.md](admin-guide.md).
