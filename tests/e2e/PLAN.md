# ExLlamaSharp E2E Feature Matrix Plan

## Goal
Exercise every shipped surface (ops, OpenAI `/v1`, admin `/api/v1`, Blazor pages, live logs, auth negatives, stubs) against mock engine, collect pass/fail + polish findings.

## Environment
- Mock engine (native CUDA optional)
- Isolated data root via `EXLLAMASHARP_DATA_ROOT` (temp dir per run)
- Seed key: `sk-exllamasharp-dev`

## Phases

| Phase | Scope | Pass criteria |
|-------|--------|----------------|
| A Ops | `/health`, `/ready`, `/metrics` | 200 (or documented 503), body shape OK |
| B OpenAI | chat, completions, models, embeddings, tokenize, detokenize, metrics, stream, 501 catch-all | 200/501 as expected; 401 without key |
| C Admin | settings, library, load/unload mock, jobs, keys CRUD, users CRUD, moderation, backup, about, logs SSE | 200/201/202; admin scope enforced |
| D Auth | missing key, non-admin scope on `/api/v1`, revoked key | 401/403 |
| E Stubs | `/ab`, `/tenants`, `/adapters` | 200 + stub message (contract only) |
| F UI | GET each Blazor route | 200 HTML (smoke) |
| G Unit | `dotnet test` solution | all green |

## Out of scope (documented stubs)
Real HF pull, real CUDA kernels, real LoRA load, real A/B routing persistence via HTTP, signed MSIX.

## Execution
Automated: `tests/ExLlamaSharp.Server.Tests/E2eFeatureMatrixTests.cs`  
Runner: `dotnet test --filter E2eFeatureMatrix`
