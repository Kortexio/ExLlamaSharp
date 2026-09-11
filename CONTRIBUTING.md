# Contributing to ExLlamaSharp

Thanks for helping. This project is a Windows-first local LLM server (EXL3 + OpenAI-compatible API + Blazor admin).

## Ways to contribute

- Bug reports and reproducible failures (GPU load, installer, API compatibility)
- Docs / README clarity for new Windows NVIDIA users
- Small, focused PRs (UI polish, tests, packaging fixes)

Please open an issue before large refactors or new inference formats (GGUF etc. are out of scope for now).

## Development setup (short)

Prerequisites as needed: .NET 10 SDK, Python 3.11+ for the EXL3 worker, NVIDIA GPU + drivers for real inference.

```powershell
dotnet restore ExLlamaSharp.slnx
dotnet build ExLlamaSharp.slnx -c Debug
dotnet run --project src/ExLlamaSharp.Server/ExLlamaSharp.Server.csproj
```

Full packaging / CUDA notes: [packaging/README.md](packaging/README.md), [docs/architecture.md](docs/architecture.md).

## Pull requests

1. Keep diffs focused; match existing code style.
2. Mention how you tested (UI page, API call, installer).
3. Do not commit secrets, local `venv`, or large model weights.

## Code of conduct

Be respectful. Harassment or abusive behavior is not welcome.
