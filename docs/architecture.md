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
│       LibraryImport → exllamasharp.dll  │
└─────────────────────────────────────────┘
              │  C ABI / P/Invoke
┌─────────────────────────────────────────┐
│  native/exllamasharp (C++ / optional CUDA)│
│  ├─ Scheduler (continuous batching)     │
│  ├─ PageTable (KV pages / prefix cache) │
│  └─ Kernels / EXL3 path (non-stub)      │
└─────────────────────────────────────────┘
```

Optional later: `third_party/exllamav3` git submodule for upstream EXL3 kernels (see `third_party/README.md`).

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
| CUDA | `-DEXL_STUB=OFF` + LibTorch + toolkit | Full path toward EXL3 GEMM / multi-GPU |

Build helper: `packaging/build-native-stub.ps1`. Details: `native/exllamasharp/README.md`.

C ABI (`exllamasharp.h`) is the stability boundary — .NET uses source-generated `LibraryImport` (`NativeMethods`).

## Request path (chat)

1. Client → `POST /v1/chat/completions` with Bearer key.
2. Middleware: auth + rate limit; optional moderation.
3. Chat template formats messages → token ids.
4. `EngineHostService` submits a job to `ExLlamaEngine` (native or mock).
5. Native scheduler batches; tokens stream back as SSE chunks if requested.
6. Audit / webhooks / metrics updated asynchronously.

## Multi-GPU & advanced (stubs → native)

Server-side helpers prepare config for the engine:

- `MultiGpuPlanner` — TP / PP / MP from `CudaVisibleDevices` + `ParallelismMode`
- `SpeculativeDecodingOptions` — draft model + `DraftK`
- `ArchitectureDetector` — llama / qwen / mixtral / llava from `config.json`
- `QuantizationModes` — EXL3, EXL2, INT8, FP8, AWQ, GPTQ, Dynamic
- `LoraAdapterService` — DB-backed adapter registry
- `PythonModelTools` — optional convert/pull via external Python

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
