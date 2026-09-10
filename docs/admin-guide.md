# ExLlamaSharp Admin Guide

Operational guide for administrators of the Windows service and Blazor UI.

## Service & data layout

| Path | Purpose |
|------|---------|
| `%ProgramData%\ExLlamaSharp\` | Root |
| `app.db` | SQLite (users, keys, settings, jobs, audit) |
| `models\` | Model weights / imports |
| `backups\` | ZIP exports from BackupService |
| `logs\` | Application logs |
| `adapters\` | LoRA adapter files (when used) |

Service name: **ExLlamaSharp** (`Install.ps1` / `Uninstall.ps1`).

Default bind: `127.0.0.1:14563` (change under Settings; Kestrel also reads `appsettings.json`).

## API keys

1. UI: **API Keys** → create key → copy the secret once.
2. Clients send `Authorization: Bearer <key>` (OpenAI style) to `/v1/*`.
3. Admin UI routes and `/api/v1/*` use the same auth middleware (except public `/health`, `/ready`, `/metrics`, `/api/v1/about`).

Practices:

- One key per app or team; name them clearly.
- Rotate by creating a new key and deleting the old one.
- Prefer least privilege scopes when available.
- Rate limits apply per key; watch `/metrics` and audit logs under load.

## Settings

UI **Settings** maps to `AppSettings` (single row) and `/api/v1/settings`.

Important fields:

| Area | Fields |
|------|--------|
| Network | `BindAddress`, `Port`, `Cors`, optional TLS cert path |
| Scheduler | `MaxNumSeqs`, `MaxChunkSize`, `MaxBatchedTokens`, `GpuMemoryUtilization`, request timeout |
| Startup | `LoadModelOnStartup`, last loaded model id |
| Backup | `AutoBackupSchedule` (`disabled` / `daily` / `weekly`) |
| Webhooks | `WebhookUrl`, `WebhookSecret` |
| Features | content moderation, multi-tenancy, advanced metrics |
| GPU | `CudaVisibleDevices` (PCI indices, e.g. `0,1`), `ParallelismMode` (`none` / `tensor` / `pipeline`), `GpuMemoryUtilization`, optional `GpuSplitGb` |
| Speculative | `SpeculativeEnabled`, `DraftModelId`, `DraftK` |
| Paths | `ModelsPath` |

Saving GPU / parallelism settings recycles the Python worker and reloads the model. Bind address / port still need a Server restart.

## Backup & restore

- **Manual:** UI or `POST /api/v1/backup` — writes a ZIP under `backups\` (settings, users, keys, tenants, models metadata, moderation, A/B stubs).
- **Scheduled:** set auto schedule in Settings; `BackupService` background worker runs exports.
- **Restore:** `POST /api/v1/backup/restore` with the archive path (service must be able to read it). Prefer stopping traffic first.

Backups do **not** include multi‑GB weight files — back up the `models\` folder separately (robocopy / volume snapshot).

## Multi-GPU

Works with any mix of NVIDIA GPUs (equal or different VRAM). No SKU is hardcoded.

1. Confirm cards on **About** (PCI index, name, VRAM, UUID) or `nvidia-smi`.
2. Set `CudaVisibleDevices` to the **PCI / nvidia-smi** indices to use (`0,1` or `0,2`, …). Empty = all.
3. The worker process is started with `CUDA_DEVICE_ORDER=PCI_BUS_ID` and `CUDA_VISIBLE_DEVICES` **reordered** so `cuda:0` is the highest-VRAM GPU in that set.
4. `ParallelismMode`:
   - `none` — load on `cuda:0` only
   - `tensor` — ExLlamaV3 tensor parallelism (`tensor_p=True`, backend `native`)
   - `pipeline` — layer autosplit (`tensor_p=False` + `use_per_device`)
   - `model` is **rejected** (not implemented)
5. Split: auto `VRAM[i] × GpuMemoryUtilization`, or override `GpuSplitGb` in **remapped** order (e.g. `10,4.5` for a 12 GB + 6 GB pair). Count must match visible GPUs.
6. Save Settings (or PATCH `/api/v1/settings`) — the worker is recycled and the loaded model is queued again.

`tensor` / `pipeline` require at least two indices. OOM usually hits the smallest card first — lower util or set `GpuSplitGb` with more reserve on the display GPU.

Examples (docs only — no SKU is hardcoded):

- Two equal 12 GB cards: `CudaVisibleDevices=0,1`, `tensor`, empty `GpuSplitGb`, util `0.85` → about `10.2,10.2`.
- 12 GB + 6 GB: same devices, or `GpuSplitGb=10,4.5` in **remapped** order (`cuda:0` is the 12 GB card even if `nvidia-smi` lists it as index 1).
- Three cards: `0,1,2` and a 3-value split. Same code path.

## Webhooks

When `WebhookUrl` is set, `WebhookService` POSTs JSON:

```json
{ "event": "<name>", "timestamp": "...", "data": { } }
```

Headers:

- `X-ExLlamaSharp-Event`
- `X-ExLlamaSharp-Signature: sha256=<hmac>` using `WebhookSecret`

Retries up to 3 times on failure. Use for job completion, alerts, or SIEM hooks.

## Health endpoints

| URL | Use |
|-----|-----|
| `GET /health` | Component health (DB, engine, inference, disk) |
| `GET /ready` | Readiness for load balancers |
| `GET /metrics` | Prometheus text |
| UI `/diagnostics` | Same checks + common fixes |
| `GET /api/v1/about` | Version, runtime, GPU summary |

## Related docs

- [user-manual.md](user-manual.md)
- [troubleshooting.md](troubleshooting.md)
- [api-reference.md](api-reference.md)
- [architecture.md](architecture.md)
