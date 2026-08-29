# ExLlamaSharp API Reference

Overview of the HTTP surface. Interactive OpenAPI/Swagger is available in Development (`/swagger`). The Blazor UI also includes an API guide page.

Base URL default: `http://localhost:14563`

Authentication for protected routes: `Authorization: Bearer <api_key>`.

JSON for OpenAI routes uses **snake_case** field names where applicable.

---

## OpenAI-compatible (`/v1`)

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/v1/chat/completions` | Chat completions (streaming SSE when `stream: true`) |
| `POST` | `/v1/completions` | Text completions |
| `GET` | `/v1/models` | List models known to the server |
| `GET` | `/v1/models/{id}` | Model detail |
| `POST` | `/v1/embeddings` | Embeddings (ONNX required; **503** if missing unless CI fallback env) |
| `POST` | `/v1/tokenize` | Encode text → token ids |
| `POST` | `/v1/detokenize` | Decode token ids → text |
| `GET` | `/v1/metrics` | Engine metrics (JSON) |

Unimplemented `/v1/**` paths (including **images/audio generation**) return **501** with an OpenAI-shaped error object (`not_implemented_error`).

Chat supports OpenAI `tools` / `tool_choice` (response may include `tool_calls` + `finish_reason: tool_calls`), `response_format` / JSON schema, and multimodal `image_url` when the loaded EXL3 model has a vision component (Qwen3-VL, Gemma VL, etc.). Text-only models return **400** `vision_not_supported`. Unsupported fields such as `logit_bias`, `logprobs`, and `n > 1` return **400**. Responses include header `X-ExLlamaSharp-Engine: worker|mock`.

### Chat completions (sketch)

```http
POST /v1/chat/completions
Authorization: Bearer sk-...
Content-Type: application/json

{
  "model": "office-assistant",
  "messages": [
    { "role": "system", "content": "You are helpful." },
    { "role": "user", "content": "Hello" }
  ],
  "temperature": 0.7,
  "max_tokens": 512,
  "stream": false
}
```

Streaming responses use `text/event-stream` chunks compatible with OpenAI clients.

---

## Ops (no API key)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/health` | Component health report |
| `GET` | `/ready` | Readiness (`ready`, `model_loaded`, `engine_running`) |
| `GET` | `/metrics` | Prometheus exposition format |

---

## Admin (`/api/v1`)

Requires API key (except `GET /api/v1/about`, which is public for version/GPU discovery).

### Settings

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/v1/settings` | Current settings |
| `POST` | `/api/v1/settings` | Replace settings |
| `PATCH` | `/api/v1/settings` | Partial update |

### Models & jobs

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/v1/models/library` | Curated / cached library entries |
| `POST` | `/api/v1/models/library` | Add library entry |
| `POST` | `/api/v1/models/load` | Load model into engine |
| `POST` | `/api/v1/models/unload` | Unload |
| `POST` | `/api/v1/models/pull` | Start pull job |
| `POST` | `/api/v1/models/quantize` | Start quantize job |
| `POST` | `/api/v1/models/import` | Import from path |
| `POST` | `/api/v1/models/alias` | Set alias |
| `GET`/`PUT` | `/api/v1/models/{id}/modelfile` | Modelfile metadata |
| `GET` | `/api/v1/models/jobs/{job_id}` | Job status |
| `GET` | `/api/v1/jobs` | List jobs |
| `POST` | `/api/v1/jobs/{id}/cancel` | Cancel job |

### Keys & users

| Method | Path | Description |
|--------|------|-------------|
| `GET`/`POST` | `/api/v1/keys` | List / create API keys |
| `DELETE` | `/api/v1/keys/{id}` | Revoke key |
| `GET`/`POST` | `/api/v1/users` | List / create users |
| `PATCH`/`DELETE` | `/api/v1/users/{id}` | Update / delete user |

### Moderation, backup, ops

| Method | Path | Description |
|--------|------|-------------|
| `GET`/`POST` | `/api/v1/moderation/rules` | List / create rules |
| `DELETE` | `/api/v1/moderation/rules/{id}` | Delete rule |
| `GET` | `/api/v1/about` | Version, runtime, engine, GPU |
| `GET` | `/api/v1/logs/stream` | Log stream (SSE-style) |
| `POST` | `/api/v1/backup` | Export backup ZIP |
| `POST` | `/api/v1/backup/restore` | Restore from ZIP |
| `POST` | `/api/v1/restart` | Request process restart |

| `GET`/`POST` | `/api/v1/ab` | List / create A/B tests |
| `GET` | `/api/v1/ab/{id}` | Get A/B test |
| `POST` | `/api/v1/ab/vote` | Preview route assignment (`request_id` + optional `preferred`) |
| `GET`/`POST` | `/api/v1/tenants` | List / create tenants |
| `GET` | `/api/v1/tenants/{id}` | Get tenant |
| `GET`/`POST` | `/api/v1/adapters` | List / register LoRA adapter metadata |
| `GET`/`DELETE` | `/api/v1/adapters/{id}` | Get / delete adapter |

OpenAI chat/completions accept `X-Ab-Test-Id` or `model: "ab:<guid>"` to assign a variant (headers `X-Ab-Variant` / `X-Ab-Test-Id` on the response; audit stores `AbTestId`). GPU weights are not swapped automatically.

---

## Errors

OpenAI routes return:

```json
{
  "error": {
    "message": "...",
    "type": "...",
    "code": "..."
  }
}
```

Common HTTP codes: `400` validation, `401` auth, `404` missing, `429` rate limit, `501` not implemented, `503` unhealthy/not ready.

## Related

- [admin-guide.md](admin-guide.md) — keys, webhooks, multi-GPU
- UI: `/diagnostics`, in-app API guide
