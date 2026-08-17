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
| GPU | `CudaVisibleDevices` (e.g. `0` or `0,1`), `ParallelismMode` (`none` / `tensor` / `pipeline` / `model`) |
| Speculative | `SpeculativeEnabled`, `DraftModelId`, `DraftK` |
| Paths | `ModelsPath` |

After changing GPU / parallelism / bind address, restart the Windows service so the process picks up the new host environment.

## Backup & restore

- **Manual:** UI or `POST /api/v1/backup` — writes a ZIP under `backups\` (settings, users, keys, tenants, models metadata, moderation, A/B stubs).
- **Scheduled:** set auto schedule in Settings; `BackupService` background worker runs exports.
- **Restore:** `POST /api/v1/backup/restore` with the archive path (service must be able to read it). Prefer stopping traffic first.

Backups do **not** include multi‑GB weight files — back up the `models\` folder separately (robocopy / volume snapshot).

## Multi-GPU

1. Confirm GPUs with `nvidia-smi` and **About** / Diagnostics.
2. Set `CudaVisibleDevices` to the device indices to expose (e.g. `0,1`).
3. Set `ParallelismMode`:
   - `none` — single GPU
   - `tensor` (TP) — split layers across GPUs (latency / large models)
   - `pipeline` (PP) — pipeline stages across GPUs
   - `model` (MP) — whole models on different devices (routing / multi-model)
4. Restart service and load the model again.

Server helpers: `MultiGpuPlanner` validates device lists and maps modes for the native engine config. NCCL / full TP-PP production paths depend on the native CUDA build (not stub).

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
