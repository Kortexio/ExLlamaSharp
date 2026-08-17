#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs the prebuilt ExLlamaV3 CUDA extension into the product venv.
  Fixes: "DLL load failed while importing exllamav3_ext" / JIT ninja compile.
#>
[CmdletBinding()]
param(
    [string]$VenvPython = "C:\Program Files\ExLlamaSharp\venv\Scripts\python.exe",
    [string]$DonorPyd = ""
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

if (-not (Test-Path $VenvPython)) {
    throw "venv python not found: $VenvPython"
}

$site = & $VenvPython -c "import site; print(site.getsitepackages()[0])"
if (-not $site) { throw "Could not resolve site-packages" }
Write-Step "site-packages: $site"

if ([string]::IsNullOrWhiteSpace($DonorPyd)) {
    $DonorPyd = Join-Path $PSScriptRoot "..\.venv-exl3\Lib\site-packages\exllamav3_ext.cp312-win_amd64.pyd"
    $DonorPyd = [IO.Path]::GetFullPath($DonorPyd)
}

$destPyd = Join-Path $site "exllamav3_ext.cp312-win_amd64.pyd"
$copied = $false
if (Test-Path $DonorPyd) {
    Write-Step "Copying prebuilt extension ($([math]::Round((Get-Item $DonorPyd).Length/1MB)) MB)"
    Copy-Item -LiteralPath $DonorPyd -Destination $destPyd -Force
    $copied = $true
}

if (-not $copied) {
    Write-Step "Donor .pyd missing; downloading official wheel"
    $pyVer = (& $VenvPython -c "import sys; print(f'{sys.version_info.major}{sys.version_info.minor}')").Trim()
    $mm = (& $VenvPython -c "import torch; print('.'.join(torch.__version__.split('+')[0].split('.')[:2]))").Trim()
    $wheelName = "exllamav3-1.4.2+cu128.torch${mm}.0-cp$pyVer-cp$pyVer-win_amd64.whl"
    $url = "https://github.com/turboderp-org/exllamav3/releases/download/v1.4.2/exllamav3-1.4.2%2Bcu128.torch${mm}.0-cp$pyVer-cp$pyVer-win_amd64.whl"
    $wheelFile = Join-Path $env:TEMP $wheelName
    Write-Host "URL: $url"
    Invoke-WebRequest -Uri $url -OutFile $wheelFile -UseBasicParsing
    & $VenvPython -m pip uninstall -y exllamav3 | Out-Null
    & $VenvPython -m pip install --force-reinstall --no-deps $wheelFile
    if ($LASTEXITCODE -ne 0) { throw "wheel install failed" }
}

Write-Step "Verifying import"
$verify = Join-Path $env:TEMP "exl3-ext-verify.py"
@'
import os, sys
import torch
torch_lib = os.path.join(os.path.dirname(torch.__file__), "lib")
os.environ["PATH"] = torch_lib + os.pathsep + os.environ.get("PATH", "")
if hasattr(os, "add_dll_directory"):
    os.add_dll_directory(torch_lib)
print("torch", torch.__version__, "cuda", torch.cuda.is_available())
from exllamav3.ext import exllamav3_ext
print("ext", getattr(exllamav3_ext, "__file__", exllamav3_ext))
from exllamav3 import Config, Model, Cache, Tokenizer, Generator
print("exllamav3 OK")
'@ | Set-Content $verify -Encoding UTF8
$env:Path = "$(Split-Path $VenvPython);$env:Path"
& $VenvPython $verify
if ($LASTEXITCODE -ne 0) { throw "exllamav3_ext import still failing" }

$repoWorker = Join-Path $PSScriptRoot "..\tools\exl3_worker\worker.py"
$repoWorker = [IO.Path]::GetFullPath($repoWorker)
$pfWorker = "C:\Program Files\ExLlamaSharp\tools\exl3_worker\worker.py"
if ((Test-Path $repoWorker) -and (Test-Path (Split-Path $pfWorker))) {
    Write-Step "Updating installed worker.py"
    Copy-Item -LiteralPath $repoWorker -Destination $pfWorker -Force
}

Write-Step "Restarting ExLlamaSharp service"
$svc = Get-Service ExLlamaSharp -ErrorAction SilentlyContinue
if ($svc) {
    Restart-Service ExLlamaSharp -Force
    Start-Sleep -Seconds 2
    Get-Service ExLlamaSharp | Format-Table Name, Status
}

Write-Host "Repair complete." -ForegroundColor Green
