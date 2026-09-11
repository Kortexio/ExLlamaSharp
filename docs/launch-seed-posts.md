# Launch seed posts — ExLlamaSharp

Copy-paste **after** the README hero is pushed and About topics are live.
Space posts by a day so they do not look spammy. Always link the **repo**, not only the .exe.

## Checklist before posting

1. [ ] Push README + `docs/assets/*` to `main`
2. [ ] Run `pwsh ./scripts/Configure-GitHub-Discoverability.ps1` (or manual [github-discoverability.md](github-discoverability.md))
3. [ ] Upload Social preview image on GitHub About
4. [ ] Attach `docs/assets/dashboard.png` (or Models/Chat) to Reddit / LinkedIn

Repo: https://github.com/Kortexio/ExLlamaSharp  
Release: https://github.com/Kortexio/ExLlamaSharp/releases/latest  

Suggested order: **r/LocalLLaMA** (day 0) → **LinkedIn** (day 1) → **Show HN** (day 2–3) → awesome-list PR when you have a few stars.

---

## r/LocalLLaMA

**Title:** Windows EXL3 local server with OpenAI API + admin UI (no Docker) — ExLlamaSharp

```
I built ExLlamaSharp: a Windows service for NVIDIA GPUs that serves EXL3 models behind an OpenAI-compatible /v1 API, with a Blazor admin (models, keys, jobs, multi-GPU).

Aimed at small teams / office GPU boxes that want something Ollama-like without WSL/Docker, plus API keys and audit.

- Setup.exe → http://127.0.0.1:14563
- EXL3 only (not GGUF)
- Multi-GPU pipeline/tensor in the current beta

Repo + installer: https://github.com/Kortexio/ExLlamaSharp

Happy to take feedback from people already on ExLlamaV3 / TabbyAPI who live on Windows.
```

---

## Hacker News — Show HN

**Title:** Show HN: ExLlamaSharp – local OpenAI-compatible LLM server for Windows + NVIDIA

```
ExLlamaSharp is a Windows-native inference server for EXL3 models (ExLlamaV3) with an OpenAI-compatible API and a browser admin UI.

Motivation: small businesses and .NET shops on Windows GPUs often end up in WSL/Docker or desktop-only tools. This installs as a Windows service (Setup.exe), binds localhost:14563, and exposes /v1 plus keys, quotas, jobs, and multi-GPU controls.

Limitations (honest): EXL3 only; OpenAI images/audio stay 501; vision under multi-GPU is text-only for now.

https://github.com/Kortexio/ExLlamaSharp
```

---

## LinkedIn (short)

Reuse [linkedin-group-posts-v1.4.0-beta.md](linkedin-group-posts-v1.4.0-beta.md). Prefer linking the repo root after the README refresh:

https://github.com/Kortexio/ExLlamaSharp

---

## Awesome-list PR blurb

```
- [ExLlamaSharp](https://github.com/Kortexio/ExLlamaSharp) - Windows local LLM server for NVIDIA (EXL3): OpenAI-compatible API, Blazor admin, multi-GPU, no Docker.
```

Target lists such as awesome-local-ai / awesome-dotnet when submitting a PR.
