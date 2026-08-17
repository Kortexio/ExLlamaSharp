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

### Slow tokens / queue buildup

- Check `GET /metrics` (`jobs_waiting`, `tokens_per_second`).
- Reduce concurrent clients or raise capacity settings carefully.
- Prefer native CUDA build over stub/mock for real throughput.

### Webhook not firing

- Confirm `WebhookUrl` / secret in Settings.
- Receiver must return 2xx; service retries 3 times.
- Validate HMAC header if your endpoint verifies signatures.

### Multi-GPU not used

- Mode still `none`, or only one device in `CudaVisibleDevices`.
- Stub/mock builds ignore real TP/PP — need CUDA native `exllamasharp.dll`.
- Restart after Settings changes.

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
