# GitHub About — discoverability checklist

Run after `gh auth login`. From repo root:

```powershell
pwsh ./scripts/Configure-GitHub-Discoverability.ps1
```

## Manual fallback (GitHub website)

1. Open https://github.com/Kortexio/ExLlamaSharp
2. Click the **gear** next to **About**
3. Set:
   - **Description:** `LLM / AI inference server for Windows + NVIDIA (EXL3/ExLlamaV3). OpenAI-compatible API, multi-GPU, Blazor admin. Ollama-like, no Docker.`
   - **Website:** `https://kortexio.io`
   - **Topics (max 20):** `llm-server`, `ai-server`, `llm-api`, `inference-server`, `local-llm`, `local-ai`, `llm`, `llm-inference`, `openai-compatible`, `openai-api`, `exllama`, `exllamav3`, `exl3`, `nvidia`, `cuda`, `multi-gpu`, `windows`, `self-hosted`, `ollama-alternative`, `dotnet`
4. Upload **Social preview image:** [`docs/assets/social-preview.png`](../docs/assets/social-preview.png) (1280×640)
5. Enable **Discussions** under Settings → General → Features

## Verify search

After topics are live, try GitHub search:

- `topic:llm-server`
- `topic:ai-server`
- `topic:llm-api`
- `topic:exl3`
- `topic:local-llm windows`
- `llm server windows nvidia`
