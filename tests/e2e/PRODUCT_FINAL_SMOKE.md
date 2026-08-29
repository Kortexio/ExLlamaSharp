# GPU smoke checklist (product-final)

Run against a real EXL3 install (not mock):

1. [ ] Install / start service → Admin login → change password from Setup if first run
2. [ ] Models → Library pull small EXL3 → Jobs complete → My Models → Load
3. [ ] Chat multi-turn with history; model picker; stream tokens
4. [ ] `curl` `/v1/chat/completions` with `tools` → `finish_reason: tool_calls`
5. [ ] Register LoRA → `X-Adapter-Id` changes output vs baseline
6. [ ] Speculative: enable + draft model → load succeeds or clear error
7. [ ] Multi-GPU mode `tensor`/`pipeline`/`model` → Settings save **rejects**
8. [ ] Webhook URL → Test webhook + complete a job → POST received
9. [ ] Embeddings without ONNX → 503; with ONNX → 200 + `X-Embedding-Backend: onnx`
10. [ ] Vision VLM loaded + `image_url` / data URL → real multimodal answer
11. [ ] Text-only model + `image_url` → **400** `vision_not_supported` (not silently ignored)
12. [ ] Tenant multi-tenancy on → other tenant model → forbidden
13. [ ] CLI `exllamasharp chat --model <exl3-dir>` uses worker; responses include `X-ExLlamaSharp-Engine: worker`
14. [ ] Completions show `X-ExLlamaSharp-Engine: mock` when ForceMock / no GPU path
15. [ ] `seed` / non-zero penalties: applied or clear inference error (never silently dropped)

Media `/v1/images` and `/v1/audio*` remain 501 by design.
