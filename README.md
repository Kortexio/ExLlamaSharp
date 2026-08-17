# ExLlamaSharp

**Local LLM server for Windows with NVIDIA GPUs.**

Aimed at small businesses (roughly 5–50 people) that want an Ollama-like experience, OpenAI-compatible APIs, and a full admin UI—without Docker or Linux-only stacks.

Inspired by:

- **Ollama** — simple UX and model workflow
- **vLLM** — multi-user serving ideas (scheduler / paging)
- **ExLlamaV3** — fast EXL3 inference on NVIDIA
- **Open WebUI** — browser-based administration

**Current release: 1.1.1** (stable). **Beta: 1.2.0-beta** — continuous batching via the ExLlamaV3 worker (GitHub pre-release; not `latest`). Setup.exe bundles the ExLlamaV3 CUDA `.pyd`, worker deps, Python installer and VC++. PyTorch CUDA is downloaded during install. Admin → Models shows a VRAM fit badge (Fits / Tight / Too large).

Default after install: **http://127.0.0.1:14563**

| Role | Default |
|------|---------|
| Admin UI | `admin` / `changeme` — change this before production |
| API key | `sk-exllamasharp-dev` (scopes include `admin`) |

---

## Download (Windows x64)

[**ExLlamaSharp-Setup-win-x64.exe**](https://github.com/vitorcastro78/ExLlamaSharp/releases/latest/download/ExLlamaSharp-Setup-win-x64.exe) — latest GitHub Release.

One-liner (downloads Setup and launches UAC):

```powershell
irm https://raw.githubusercontent.com/vitorcastro78/ExLlamaSharp/main/packaging/install-web.ps1 | iex
```

Double-click the Setup.exe → allow UAC → PyTorch CUDA downloads into the venv, ExLlamaV3 extension installs from the package → open **http://127.0.0.1:14563**.

---

## Supported models

ExLlamaSharp runs **EXL3** models only (the [ExLlamaV3](https://github.com/turboderp-org/exllamav3) quantized format) on an **NVIDIA GPU**.

A model is a **folder** that looks like a Hugging Face snapshot:

| Required | Typical files |
|----------|----------------|
| Config | `config.json` (usually mentions `exl3` / `quant_method`) |
| Weights | one or more `*.safetensors` |
| Tokenizer | `tokenizer.json` (and friends: `tokenizer_config.json`, `special_tokens_map.json`) |

How to get one:

1. Admin UI → **Models → Library** (default search `exl3`) → **Download**
2. Or import a local folder that already has those files
3. **My Models → Load**

Hugging Face repos often keep the actual weights on a **bitrate branch** such as `4.00bpw` / `4.0bpw`, not on `main`. The server resolves that revision automatically and will not treat a README-only clone as a successful download.

**Examples that work:**

- `turboderp/MiniCPM5-1B-exl3`
- `turboderp/Llama-3.2-1B-Instruct-exl3` (revision `4.0bpw`) — small demo / first-run smoke test

Search Hugging Face for `exl3` (many IDs end in `-exl3`). Admin → **Models** shows a **Fits / Tight / Too large** badge against this machine’s GPU VRAM (estimate from weight size; it does not auto-select a model).

**Not supported** (will not load for real inference):

| Format | Examples |
|--------|----------|
| GGUF / llama.cpp | Ollama blobs, LM Studio GGUF, `*.gguf` |
| Unquantized Hugging Face | FP16 / BF16 / FP32 `.safetensors` without EXL3 |
| Other quant formats | EXL2, AWQ, GPTQ, bitsandbytes, INT8/FP8 packs that are not EXL3 |
| Convert-in-place | **Models → Quantize** is simulated only — there is no EXL3 quantizer in this product yet |

LoRA adapters can be registered in the UI as **metadata**; they are not applied at generation time.

Chat uses the tokenizer’s Hugging Face chat template when present (Llama 3 / ChatML fallbacks otherwise).

---

## Features

This section describes **every product surface** in the current build: installer, tray, Windows service, Admin UI pages, and APIs. Where something is still a stub, that is called out.

### Windows installer and service

The Setup.exe (Inno Setup) installs a self-contained .NET host. End users do **not** need the .NET SDK, CUDA Toolkit, or LibTorch.

| Piece | What it does |
|-------|----------------|
| **Windows Service `ExLlamaSharp`** | Starts at boot, binds Kestrel to `127.0.0.1:14563` by default, serves the Admin UI and APIs |
| **Start Menu shortcuts** | Admin UI, data folder, optional GPU Python repair |
| **Program Files payload** | Server binaries, tray app, `tools/exl3_worker/worker.py`, `offline-wheels\`, `redist\` |
| **`%ProgramData%\ExLlamaSharp`** | SQLite `app.db`, `models\`, `logs\`, `backups\`, UI onboarding state |
| **GPU Python venv** | `%ProgramFiles%\ExLlamaSharp\venv\` — PyTorch CUDA downloaded at install; ExLlamaV3 `.pyd` comes from the Setup package |
| **ZIP fallback** | Slim builds only (`-SkipBundleWheels`). Full Setup.exe is the supported installer |
| **Uninstall** | Removes the service, shortcuts, and Program Files (data under ProgramData can be kept) |

Firewall rule and Start Menu icon are created by the installer. The service listens on **14563** so it does not collide with common local ports (8080, 8787).

### System tray

`ExLlamaSharp.Tray.exe` lives in the notification area (single instance). It:

- Polls service + `http://127.0.0.1:14563/health` every few seconds
- Shows **green** (healthy), **yellow** (service up but health failed), or **grey** (stopped)
- **Open Admin UI** (double-click or menu)
- **Open data folder** (`%ProgramData%\ExLlamaSharp`)
- **Start / Stop / Restart** the Windows service
- Registers itself in **HKCU Run** so it starts with the user session

### Inference backends

| Backend | When it is used | Notes |
|---------|-----------------|--------|
| **ExLlamaV3 worker** | Folder looks like EXL3 (`config.json` + `.safetensors` + tokenizer) and a Python venv is available | Real CUDA path: `tools/exl3_worker/worker.py` → ExLlamaV3. Chat uses the model’s Hugging Face chat template when possible |
| **Native `exllamasharp_native.dll`** | Optional CUDA/stub build | Scheduler / page table; validates EXL3 directories. Generation still uses the worker for real models |
| **Mock engine** | `mock://…`, `ForceMockEngine`, or no worker/DLL | Deterministic fake tokens for CI and UI smoke tests |

Python resolution order: `EXLLAMASHARP_PYTHON`, `exl3-runtime.json`, the app `venv`, or a repo `.venv-exl3`. Set `EXL3_BC_DSA=0` for current ExLlamaV3 workers.

Chat templates: the worker prefers `tokenizer.apply_chat_template`. If that is missing, Llama 3 special tokens are used when present; otherwise ChatML (`<|im_start|>` / `<|im_end|>`). Special tokens are stripped from streamed replies.

### OpenAI-compatible API (`/v1`)

Use any OpenAI SDK. Point `base_url` at `http://127.0.0.1:14563/v1` and send `Authorization: Bearer <key>`.

| Endpoint | Status |
|----------|--------|
| `POST /v1/chat/completions` | **Working** — streaming SSE supported |
| `POST /v1/completions` | **Working** |
| `GET /v1/models`, `GET /v1/models/{id}` | **Working** |
| `POST /v1/tokenize`, `POST /v1/detokenize` | **Working** |
| `GET /v1/metrics` | **Working** (JSON) |
| `POST /v1/embeddings` | **Stub** — deterministic vectors for client wiring only |
| Other OpenAI routes (images, audio, …) | **501** OpenAI-shaped error |

Auth: API keys with scopes (`chat`, `completions`, `embeddings`, `admin`). Per-key RPM/TPM limits return **429**.

### Admin API (`/api/v1`)

| Area | Endpoints | Status |
|------|-----------|--------|
| Settings | GET / POST / PATCH `/settings` | Working |
| Models | library search, load / unload, pull, alias, modelfile | Pull downloads real HF snapshots (see Jobs) |
| Jobs | list, status, cancel | Working for pull; progress from folder bytes |
| API keys | create / list / revoke | Working |
| Users | create / list / patch / delete | Working |
| Moderation rules | CRUD | Stored; enforcement is optional |
| Logs | `GET /logs/stream` (SSE) | Working |
| Backup / restore | POST | Working (SQLite + settings) |
| Soft restart | POST `/restart` | Working |
| About | `GET /about` (public) | Working |
| A/B tests | `/ab*` | **Stub JSON** |
| HTTP tenants | `/tenants*` | **Stub** (UI can still write DB tenants) |
| HTTP LoRA adapters | `/adapters*` | **Stub** (metadata UI only) |
| Quantize job | `POST /models/quantize` | **Simulated progress** — no real quantizer yet |

Ops (no API key): `GET /health`, `GET /ready`, `GET /metrics` (Prometheus).

---

### Admin UI (Blazor)

Design system: Kortexio theme (DM Sans / Fraunces, teal accent). Login cookie + the built-in Admin API key are used so Chat and library calls work from the browser.

#### Workspace

**Dashboard (`/`)**  
Server overview: process status, requests today, loaded model name, GPU utilization / name. Optional advanced cards: tokens/sec, jobs waiting/running, VRAM. Toggle “Show advanced metrics”. First-run onboarding state is stored in `%ProgramData%\ExLlamaSharp\ui-state.json`.

**Chat (`/chat`)**  
Playground that streams `POST /v1/chat/completions`. Shows whether a real model is loaded or the mock engine is answering. Enter sends; Shift+Enter is not required (single-line input). If nothing is loaded, the page tells you to open Models → My Models → Load.

**Models (`/models`)**  
Three tabs:

- **Library** — live Hugging Face search (default query `exl3`). Shows name, repo id, parameter label, size, and **Download**. Gated repos need a token in Settings → Hugging Face. For EXL3 repos that keep weights on branches such as `4.00bpw` (not `main`), the server **resolves the revision automatically** and refuses a “success” that only downloaded a README.
- **My Models** — folders under the models path that contain `config.json` (scanned from disk). **Load** puts that model on the GPU.
- **Import** — register an existing local folder + alias (does not copy files).

**Jobs (`/jobs`)**  
Queue for pull / quantize / import. Cards for active, waiting, and recent (completed / failed / cancelled). Polls every 2 seconds. Pull jobs show downloaded/total bytes and parameter label. **Cancel** is available while a job is pending or running. Use **Refresh** if the Blazor poll looks stuck.

**API Keys (`/keys`)**  
Create named keys with scopes. The plaintext secret is shown **once**. List and revoke existing keys. Use these from apps, curl, or the OpenAI SDK.

**Usage (`/usage`)**  
Business view of the audit trail: requests in the last 7 days, prompt/completion tokens, estimated cost, and a recent activity table (endpoint, tokens, status, latency).

**Team (`/team`)**  
Lists users who can manage the server (username, role, tenant, last active). Create the first admin in Setup if the list is empty.

#### Advanced (sidebar toggle)

**Adapters (`/adapters`)**  
Table of registered LoRA adapters (name, path, rank, alpha). **Metadata only** — applying a LoRA at inference time is not shipped yet.

**Metrics (`/dashboard/metrics`)**  
Live tokens/sec and job counts. Chart placeholders for TPS / latency. **A/B** tab is UI scaffolding only.

**Logs (`/logs`)**  
Live in-memory tail (start/stop), min level, text filter, plus refresh of persisted audit rows.

**Diagnostics (`/diagnostics`)**  
Runs `/health` and `/ready`. Component cards (database, engine, inference, disk, …) and a short list of common fixes (no model loaded, missing `nvidia-smi`, port in use, API 401).

**Tenants (`/admin/tenants`)**  
Create/list tenants in SQLite (id, name, subdomain). HTTP `/api/v1/tenants` remains a stub; isolation at the request layer is not complete.

#### System

**Settings (`/settings`)**  
Persisted server settings:

| Tab | Controls |
|-----|----------|
| Network | Bind address, port, CORS, TLS cert path |
| Performance | Max sequences, chunk size, batched tokens, GPU memory util, request timeout |
| Multi-GPU | `CUDA_VISIBLE_DEVICES`, parallelism mode (UI only today) |
| Speculative | Enable + draft K (UI only today) |
| Startup | Load last model on startup, models path |
| Hugging Face | Optional `hf_…` token (also reads `HF_TOKEN`) |
| Backup | Auto backup schedule (disabled / daily / weekly) |
| Webhooks | URL + secret |
| Moderation | Enable content moderation flag |
| Advanced | Multi-tenancy flag, show advanced metrics by default |

**Setup (`/setup`)**  
Five-step wizard: welcome + GPU detect → create admin → models path → network (localhost vs LAN) → finish / optional starter model. Re-runnable from the sidebar.

**API Guide (`/api`)**  
Copy-paste curl examples against the live base URL (`/v1/models`, chat completions, …).

**About (`/about`)**  
Version, build date, .NET/OS, engine (mock/loaded/path/TPS), GPU name and VRAM.

**Login (`/login`)**  
Username/password for the Admin UI. Default seed on a fresh database: `admin` / `changeme`.

---

### Platform and ops

- Windows Service + tray autostart
- API key auth and per-key rate limits
- Async audit trail (SQLite)
- Scheduled / on-demand backup
- Live log tail (UI + SSE)
- SignalR dashboard hub (subscribe / ping)
- PWA manifest / service worker on the Admin UI
- Self-contained publish (no .NET SDK on the target PC)

### Still stub / partial

These exist in the UI or HTTP surface but are **not** full production features:

- HTTP A/B testing (`/api/v1/ab*`)
- HTTP tenants (`/api/v1/tenants*`) — DB rows exist; request isolation is incomplete
- HTTP LoRA adapters (`/api/v1/adapters*`) — list/register metadata only
- Quantize jobs — simulated progress (no EXL3 quantizer pipeline)
- Multi-GPU tensor/pipeline parallelism and speculative decoding — settings only
- Tools / JSON-schema tool calling — not end-to-end
- Embeddings — deterministic stub vectors
- Metrics charts — placeholders
- OpenAI extras (images, audio, …) — **501**

---

## Quick start

### End users

1. Install from the [latest Release](https://github.com/vitorcastro78/ExLlamaSharp/releases/latest) (or the one-liner above).
2. Open **http://127.0.0.1:14563** and sign in (`admin` / `changeme`).
3. **Models → Library** — search `exl3` (for example `turboderp/MiniCPM5-1B-exl3`) and Download. Watch **Jobs** until completed.
4. **Models → My Models → Load**.
5. Open **Chat** and send a message.

If inference fails after a custom Python repair, run `Setup-Exl3Python.bat` in the install folder. The Setup.exe already installs the official CUDA wheel — do not `pip install exllamav3` from PyPI.

### Developers (run from source)

```powershell
dotnet restore ExLlamaSharp.slnx
dotnet build ExLlamaSharp.slnx -c Release
dotnet run --project src/ExLlamaSharp.Server/ExLlamaSharp.Server.csproj
```

Open **http://127.0.0.1:14563**.

### Build the installer

```powershell
powershell -ExecutionPolicy Bypass -File packaging\Build-Installer.ps1
# → publish\ExLlamaSharp-Setup-win-x64.exe
# → publish\ExLlamaSharp-Setup-win-x64.zip
```

Details: [docs/INSTALL.md](docs/INSTALL.md) · [packaging/README.md](packaging/README.md).

---

## Requirements

| Scenario | Needs |
|----------|--------|
| **Mock / UI / API smoke** | Windows 10 20H1+ or Windows 11 (x64) |
| **Installer (self-contained)** | NVIDIA driver recommended; **no** .NET SDK, CUDA Toolkit, or LibTorch for end users |
| **Real EXL3 inference** | NVIDIA GPU (6 GB+ VRAM recommended), Python 3.11+ venv via `Setup-Exl3Python.ps1`, EXL3 model folder |
| **Compile native CUDA DLL** | CUDA Toolkit 12.8+, CMake, MSVC, LibTorch under `third_party/libtorch` |

Data directory: `%ProgramData%\ExLlamaSharp\` (`app.db`, `models\`, `logs\`, `backups\`).

Override for tests: environment variable `EXLLAMASHARP_DATA_ROOT`.

---

## Architecture

```
┌─────────────────────────────────────────┐
│  Browser PWA (any PC on the LAN)        │
│  Blazor UI (Kortexio theme)             │
└─────────────────────────────────────────┘
              ↓ HTTP
┌─────────────────────────────────────────┐
│  Windows Service / console (GPU host)   │
│  ┌───────────────────────────────────┐  │
│  │ Kestrel .NET 10                   │  │
│  │ ├─ OpenAI /v1                     │  │
│  │ ├─ Admin /api/v1                  │  │
│  │ └─ Blazor Server + PWA            │  │
│  └───────────────────────────────────┘  │
│  ┌───────────────────────────────────┐  │
│  │ ExLlamaV3WorkerEngine (Python)    │  │
│  │ tools/exl3_worker → ExLlamaV3     │  │
│  │ Real EXL3 CUDA kernels            │  │
│  └───────────────────────────────────┘  │
│              ↓ optional P/Invoke         │
│  ┌───────────────────────────────────┐  │
│  │ exllamasharp_native.dll (C++/CUDA)│  │
│  │ Scheduler, PageTable, EXL3 check  │  │
│  └───────────────────────────────────┘  │
│  SQLite · audit · backup · live logs    │
└─────────────────────────────────────────┘
```

More detail: [docs/architecture.md](docs/architecture.md).

**Important:** Do **not** push changes to upstream `turboderp-org/exllamav3`. Keep a local `third_party/exllamav3` tree only (not in this git repo).

---

## Documentation

- [User Manual](docs/user-manual.md)
- [Admin Guide](docs/admin-guide.md)
- [API Reference](docs/api-reference.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Architecture](docs/architecture.md)
- [Packaging](packaging/README.md)
- Swagger UI in Development: `/swagger`

---

## Comparison

| Feature | ExLlamaSharp | Ollama | vLLM | LM Studio |
|---------|--------------|--------|------|-----------|
| Windows-native service | Yes | No | No | Yes |
| Multi-user API keys / audit | Yes | Limited | Yes | Limited |
| Web admin UI | Yes | No | No | Yes |
| OpenAI-compatible API | Yes | Yes | Yes | Yes |
| No Docker required | Yes | No* | No* | Yes |
| EXL3 on NVIDIA | Yes (worker) | No | No | Partial |
| Non-technical setup wizard | Yes | Yes | No | Yes |

\*Typical production installs often use containers or Linux hosts.

---

## Development

```powershell
# Prerequisites (as needed)
# - .NET 10 SDK
# - Visual Studio 2022+ with C++ (native builds)
# - CUDA Toolkit 12.8+ (native CUDA DLL)
# - CMake 3.25+
# - Python 3.11+ (real EXL3 worker)

dotnet restore ExLlamaSharp.slnx
dotnet build ExLlamaSharp.slnx -c Release

# Native stub (CI / no GPU toolkit)
.\packaging\build-native-stub.ps1

# Native CUDA (optional; validates EXL3 dirs)
.\packaging\build-native-cuda.ps1

# Real EXL3 path
.\packaging\Setup-Exl3Python.ps1
.\packaging\Download-DemoModel.ps1

dotnet run --project src/ExLlamaSharp.Server/ExLlamaSharp.Server.csproj
dotnet test ExLlamaSharp.slnx

# E2E feature matrix
dotnet test --filter FullyQualifiedName~E2eFeatureMatrix -c Release
```

Publish + Windows install:

```powershell
.\packaging\Build-Installer.ps1
```

See [third_party/README.md](third_party/README.md) and [packaging/install-cuda-libtorch.md](packaging/install-cuda-libtorch.md).

---

## License

Apache 2.0

## Acknowledgments

- [ExLlamaV3](https://github.com/turboderp-org/exllamav3) — EXL3 CUDA kernels and model format
- [vLLM](https://github.com/vllm-project/vllm) — serving / scheduler inspiration
- [Ollama](https://ollama.ai) — UX inspiration
