#Requires -Version 5.1
<#
.SYNOPSIS
  Downloads turboderp/Llama-3.2-1B-Instruct-exl3 (revision 4.0bpw) for smoke tests.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging\Download-DemoModel.ps1
#>
[CmdletBinding()]
param(
    [string]$Dest = "",

    [string]$RepoId = "turboderp/Llama-3.2-1B-Instruct-exl3",

    [string]$Revision = "4.0bpw"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Dest)) {
    $programData = Join-Path $env:ProgramData "ExLlamaSharp\models\Llama-3.2-1B-Instruct-exl3"
    $repoModels = Join-Path $Root "models\Llama-3.2-1B-Instruct-exl3"
    if (Test-Path (Join-Path $programData "config.json")) {
        $Dest = $programData
    }
    else {
        $Dest = $programData
    }
}

function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

Write-Host "ExLlamaSharp demo model download" -ForegroundColor Green
Write-Host "Repo: $RepoId @ $Revision"
Write-Host "Dest: $Dest"

New-Item -ItemType Directory -Force -Path $Dest | Out-Null

# Already present?
$config = Join-Path $Dest "config.json"
$st = Get-ChildItem $Dest -Filter "*.safetensors" -ErrorAction SilentlyContinue | Select-Object -First 1
$tok = Join-Path $Dest "tokenizer.json"
if ((Test-Path $config) -and $st -and (Test-Path $tok)) {
    Write-Host "Model already present at $Dest" -ForegroundColor Green
    Write-Host $Dest
    exit 0
}

function Find-Python {
    $candidates = @(
        (Join-Path $Root ".venv-exl3\Scripts\python.exe"),
        (Join-Path $env:ProgramFiles "ExLlamaSharp\venv\Scripts\python.exe"),
        (Join-Path $env:ProgramData "ExLlamaSharp\venv\Scripts\python.exe")
    )
    if ($env:EXLLAMASHARP_PYTHON) { $candidates = @($env:EXLLAMASHARP_PYTHON) + $candidates }
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    foreach ($c in @("py", "python")) {
        $cmd = Get-Command $c -ErrorAction SilentlyContinue
        if ($cmd) {
            if ($c -eq "py") { return "py" }
            return $cmd.Source
        }
    }
    return $null
}

$py = Find-Python
$usedHf = $false

if ($py) {
    Write-Step "Ensuring huggingface_hub"
    if ($py -eq "py") {
        & py -3 -m pip install -q --upgrade "huggingface_hub>=0.23"
        $hfArgs = @("-3", "-m", "huggingface_hub.commands.huggingface_cli", "download", $RepoId, "--revision", $Revision, "--local-dir", $Dest)
        Write-Step "huggingface-cli download via py -3"
        & py @hfArgs
        if ($LASTEXITCODE -eq 0) { $usedHf = $true }
    }
    else {
        & $py -m pip install -q --upgrade "huggingface_hub>=0.23"
        Write-Step "huggingface_hub snapshot_download"
        $code = @"
from huggingface_hub import snapshot_download
snapshot_download(repo_id='$RepoId', revision='$Revision', local_dir=r'$Dest', local_dir_use_symlinks=False)
print('OK', r'$Dest')
"@
        & $py -c $code
        if ($LASTEXITCODE -eq 0) { $usedHf = $true }
    }
}

if (-not $usedHf) {
    Write-Step "Fallback: huggingface.co resolve URLs via curl/Invoke-WebRequest"
    # Minimal file set commonly present in EXL3 folders
    $base = "https://huggingface.co/$RepoId/resolve/$Revision"
    $files = @(
        "config.json",
        "tokenizer.json",
        "tokenizer_config.json",
        "special_tokens_map.json",
        "generation_config.json"
    )
    # Try to discover safetensors via API
    try {
        $api = Invoke-RestMethod -Uri "https://huggingface.co/api/models/$RepoId/tree/$Revision" -Method Get
        foreach ($entry in $api) {
            if ($entry.path -match '\.(safetensors|json)$') {
                $files += $entry.path
            }
        }
        $files = $files | Select-Object -Unique
    }
    catch {
        Write-Warning "Could not list HF tree; downloading common filenames only"
        $files += @("model.safetensors", "output.safetensors")
    }

    foreach ($f in $files) {
        $url = "$base/$($f.Replace('\','/'))"
        $out = Join-Path $Dest $f
        $dir = Split-Path $out
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        if (Test-Path $out) { continue }
        Write-Host "  GET $f"
        try {
            if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
                & curl.exe -L --fail --retry 3 -o $out $url
                if ($LASTEXITCODE -ne 0) { Remove-Item $out -Force -ErrorAction SilentlyContinue; throw "curl failed" }
            }
            else {
                Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing
            }
        }
        catch {
            Write-Warning "Skip $f : $_"
        }
    }
}

$st = Get-ChildItem $Dest -Filter "*.safetensors" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not (Test-Path $config) -or -not $st) {
    throw "Download incomplete: need config.json + *.safetensors under $Dest"
}

Write-Host ""
Write-Host "Demo model ready: $Dest" -ForegroundColor Green
Write-Host $Dest
