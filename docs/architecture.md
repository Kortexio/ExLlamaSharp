# ExLlamaSharp Architecture

## Stack overview

```
┌─────────────────────────────────────────┐
│  Browser PWA (any PC on LAN / localhost)│
│  Blazor Server UI + SignalR dashboard   │
└─────────────────────────────────────────┘
              │  HTTP (Kestrel)
┌─────────────────────────────────────────┐
│  Windows Service — ExLlamaSharp.Server  │
│  .NET 10 (Server GC, tiered compilation)│
│  ├─ OpenAI /v1/*                        │
│  ├─ Admin /api/v1/* + /health|/ready    │
│  ├─ EF Core + SQLite (ProgramData)      │
│  ├─ Auth (API keys), rate limit, audit  │
│  └─ ExLlamaSharp C# library             │
│       EXL3 Python worker (production)   │
└─────────────────────────────────────────┘
              │  JSONL stdin/stdout
┌─────────────────────────────────────────┐
│  tools/exl3_worker + ExLlamaV3          │
│  ├─ Model.load (tensor_p / autosplit)   │
│  ├─ Cache / Generator / Tokenizer       │
│  └─ CUDA kernels from the venv          │
└─────────────────────────────────────────┘
```

Production inference is the **EXL3 Python worker**, not `exllamasharp_native.dll` (that stub is disabled). Multi-GPU TP / layer autosplit goes through `model.load`.

## .NET 10 host

| Piece | Role |
|-------|------|
| `ExLlamaSharp.Server` | Kestrel host, Windows Service, Blazor, endpoints |
| `ExLlamaSharp` | Engine facade, tokenizer, chat templates, native bindings |
| `ExLlamaSharp.Cli` | CLI utilities |
| SQLite under `%ProgramData%\ExLlamaSharp` | Durable config, keys, jobs, audit |
| Mock engine | `ForceMockEngine` / missing DLL for CI and UI work |

Performance knobs: Server GC, sustained low-latency mode at startup, in-memory key cache, async audit writer, bounded Kestrel concurrency.

## Native layer

| Mode | CMake | Behavior |
|------|-------|----------|
| Stub | `-DEXL_STUB=ON` | No CUDA/LibTorch; deterministic fake generate; real scheduler ABI |
| CUDA | `-DEXL_STUB=OFF` + LibTorch + toolkit | Optional native experiment — **not** the production multi-GPU path |

Build helper: `packaging/build-native-stub.ps1`. Details: `native/exllamasharp/README.md`.

The native DLL is **disabled** at runtime (`EngineHostService`). Multi-GPU TP / autosplit is ExLlamaV3 `model.load` in the Python worker.

C ABI (`exllamasharp.h`) is the leftover native boundary — .NET still has `LibraryImport` (`NativeMethods`) but the Server does not load that DLL for inference.

## Request path (chat)

1. Client → `POST /v1/chat/completions` with Bearer key.
2. Middleware: auth + rate limit; optional moderation.
3. Chat template formats messages → token ids.
4. `EngineHostService` submits a job to `ExLlamaV3WorkerEngine` (or mock in Development).
5. The Python worker iterates the ExLlamaV3 Generator; tokens stream back as SSE chunks if requested.
6. Audit / webhooks / metrics updated asynchronously.

## Multi-GPU & advanced

Server-side helpers prepare config for the engine:

- `MultiGpuPlanner` — validates `none` / `tensor` / `pipeline` + device list + optional `GpuSplitGb`; worker spawn remaps `CUDA_VISIBLE_DEVICES` strongest-first and `model.load` gets `tensor_p` / `use_per_device`
- `SpeculativeDecodingOptions` — draft model + `DraftK` (forwarded to EXL3 worker)
- `ArchitectureDetector` — llama / qwen / mixtral / llava from `config.json`
- `QuantizationModes` — EXL3 convert via `exllamav3.conversion.convert_model`
- `LoraAdapterService` — DB registry + worker `load_adapter` / `X-Adapter-Id`
- `AbTestRouter` — consistent-hash A/B + load-on-demand for the selected variant
- `PythonModelTools` — HF pull + EXL3 convert
- `EmbeddingService` — ONNX sentence embeddings when `model.onnx` is present

## Solution layout

```
src/ExLlamaSharp/           # library
src/ExLlamaSharp.Server/    # service + UI
src/ExLlamaSharp.Cli/
native/exllamasharp/
tests/ExLlamaSharp.Tests/
tests/ExLlamaSharp.Server.Tests/
packaging/                  # service scripts, MSIX notes
docs/
```

## Related

- [admin-guide.md](admin-guide.md)
- [api-reference.md](api-reference.md)
- [comparison.md](comparison.md)
