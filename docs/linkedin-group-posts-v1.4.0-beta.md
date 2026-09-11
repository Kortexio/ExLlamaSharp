# LinkedIn group posts — ExLlamaSharp v1.4.0-beta

Copy-paste per group. First person, English unless noted.

Do **not** post in Scrum.org (off-topic). Space the .NET-family posts by a day or two so they do not look copy-pasted.

Release: https://github.com/Kortexio/ExLlamaSharp/releases/tag/v1.4.0-beta

---

## Artificial Intelligence Exchange

```
Hi everyone,

I just shipped ExLlamaSharp v1.4.0-beta — a local LLM inference server for Windows + NVIDIA, with an OpenAI-compatible API (no Docker / no Linux-only stack).

This beta is about Multi-GPU in production-ish conditions: pipeline and tensor parallelism, VRAM split, strongest GPU remapped to cuda:0, and combined-VRAM fit in the admin UI. Oversized prompts now fail fast instead of hanging.

If you run local models for research, demos, or internal tools and you’re on Windows GPUs, I’d love feedback.

Release + Setup:
https://github.com/Kortexio/ExLlamaSharp/releases/tag/v1.4.0-beta
```

---

## Agentic AI, Generative AI, LLM & AI Agents

```
Hi folks,

I built ExLlamaSharp as a local OpenAI-compatible endpoint for agent stacks on Windows (NVIDIA). v1.4.0-beta just went out.

What’s new: real Multi-GPU (pipeline / tensor), configurable GPU split, and a fail-fast prompt_too_long instead of a 408 hang — useful when agents send long histories.

Point your agent at http://127.0.0.1:14563/v1 with a Bearer key. On Windows multi-GPU I recommend pipeline. Vision models stay text-only under multi-GPU.

Happy to discuss how it behaves with tool-calling / long agent loops.

https://github.com/Kortexio/ExLlamaSharp/releases/tag/v1.4.0-beta
```

---

## .NET Developers

```
Hi .NET folks,

I released ExLlamaSharp v1.4.0-beta: a .NET host + Blazor admin that talks OpenAI /v1, with EXL3 inference on NVIDIA GPUs. One Setup.exe, no Docker.

This beta adds real Multi-GPU from Settings (pipeline / tensor, GpuSplitGb, CUDA remap so the biggest card is cuda:0). Combined VRAM shows up on Dashboard / Models.

If you’re wiring local LLMs into C# apps, this is the “localhost:14563” drop-in.

https://github.com/Kortexio/ExLlamaSharp/releases/tag/v1.4.0-beta
```

---

## .NET People

```
Hello everyone,

Sharing a project I maintain: ExLlamaSharp — local LLM server on Windows for small teams. .NET + Blazor admin, OpenAI-compatible API.

v1.4.0-beta adds Multi-GPU (pipeline/tensor) and combined VRAM in the UI, plus faster errors when the prompt exceeds context.

Setup.exe here:
https://github.com/Kortexio/ExLlamaSharp/releases/tag/v1.4.0-beta
```

---

## Microsoft Developers

```
Hi all,

If you want a local ChatGPT-style API on a Windows box with NVIDIA GPUs, I just published ExLlamaSharp v1.4.0-beta.

It’s a .NET/Blazor admin + OpenAI /v1. The new beta is Multi-GPU: split VRAM across cards, remap the strongest GPU first, and fail fast on oversized prompts.

Feedback from Windows / Azure-adjacent folks welcome.

https://github.com/Kortexio/ExLlamaSharp/releases/tag/v1.4.0-beta
```

---

## C# Developers / Architects

```
Hi everyone,

Architecture note + release: ExLlamaSharp v1.4.0-beta.

.NET 10 host, Blazor admin, JSONL worker to ExLlamaV3. Clients stay on standard OpenAI chat/completions. This beta wires real Multi-GPU (pipeline/tensor), GPU split, and device remap so cuda:0 is the highest-VRAM card.

Happy to talk trade-offs (pipeline vs tensor on Windows, context vs VRAM).

https://github.com/Kortexio/ExLlamaSharp/releases/tag/v1.4.0-beta
```

---

## ASP.NET Developers

```
Hi ASP.NET folks,

ExLlamaSharp is an ASP.NET / Blazor server that exposes OpenAI-compatible /v1 on Windows. I just cut v1.4.0-beta.

New: Multi-GPU settings (pipeline/tensor), combined VRAM in the admin pages, and prompt_too_long instead of a client timeout.

If you already build APIs in ASP.NET, this is meant to be the local inference box behind your app.

https://github.com/Kortexio/ExLlamaSharp/releases/tag/v1.4.0-beta
```

---

## Microsoft .Net Developer Brasil (Portuguese)

```
Olá pessoal,

Lancei o ExLlamaSharp v1.4.0-beta: servidor LLM local no Windows (NVIDIA), API compatível com OpenAI, admin em Blazor/.NET. Sem Docker.

O beta traz Multi-GPU de verdade (pipeline/tensor), split de VRAM e a GPU mais forte como cuda:0. Prompts grandes falham logo, em vez de 408.

Se estiverem a meter LLM em apps .NET, o endpoint é /v1.

https://github.com/Kortexio/ExLlamaSharp/releases/tag/v1.4.0-beta
```

---

## Skip

- **Scrum.org Group** — not the audience; looks like spam.
