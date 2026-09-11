# Configure GitHub About (topics, description, Discussions)
# Prerequisites: gh auth login
# Run from repo root:  powershell -File ./scripts/Configure-GitHub-Discoverability.ps1

$ErrorActionPreference = "Stop"

$Description = "LLM / AI inference server for Windows + NVIDIA (EXL3/ExLlamaV3). OpenAI-compatible API, multi-GPU, Blazor admin. Ollama-like, no Docker."
$Homepage = "https://kortexio.io"

# Max 20 topics. Prefer high-intent search terms over broad tags like "ai".
$Topics = @(
    "llm-server",
    "ai-server",
    "llm-api",
    "inference-server",
    "local-llm",
    "local-ai",
    "llm",
    "llm-inference",
    "openai-compatible",
    "openai-api",
    "exllama",
    "exllamav3",
    "exl3",
    "nvidia",
    "cuda",
    "multi-gpu",
    "windows",
    "self-hosted",
    "ollama-alternative",
    "dotnet"
)

Write-Host "Checking gh auth..."
gh auth status
if ($LASTEXITCODE -ne 0) {
    Write-Error "Run: gh auth login"
}

Write-Host "Updating description + homepage..."
gh repo edit --description $Description --homepage $Homepage

Write-Host "Replacing topics (exact set of $($Topics.Count))..."
$tmp = Join-Path $env:TEMP "exllamasharp-topics.json"
$json = '{"names":[' + (($Topics | ForEach-Object { '"' + $_ + '"' }) -join ',') + ']}'
[System.IO.File]::WriteAllText($tmp, $json)
gh api -X PUT repos/Kortexio/ExLlamaSharp/topics -H "Accept: application/vnd.github+json" --input $tmp
Remove-Item $tmp -ErrorAction SilentlyContinue

Write-Host "Enabling Discussions..."
gh repo edit --enable-discussions

Write-Host ""
Write-Host "Verify: gh api repos/Kortexio/ExLlamaSharp --jq .topics"
Write-Host "Still do manually in GitHub UI:"
Write-Host "  1. About (gear) -> Upload Social preview: docs/assets/social-preview.png"
Write-Host "  2. Confirm topics under the description"
