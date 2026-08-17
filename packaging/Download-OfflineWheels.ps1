#Requires -Version 5.1
<#
.SYNOPSIS
  Downloads CUDA PyTorch (+ friends) and ExLlamaV3 wheels into a folder for offline install.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging\Download-OfflineWheels.ps1
  powershell -ExecutionPolicy Bypass -File packaging\Download-OfflineWheels.ps1 -OutDir publish\installer\offline-wheels
#>
[CmdletBinding()]
param(
    [string]$OutDir = "",

    [ValidateSet("cu128", "cu126", "cu124")]
    [string]$CudaIndex = "cu128",

    [ValidateSet("312", "311", "313")]
    [string]$PythonTag = "312",

    [string]$ExLlamaV3Version = "1.4.2"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $Root "packaging\offline-wheels"
}

function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

Write-Host "ExLlamaSharp offline wheel download" -ForegroundColor Green
Write-Host "Out: $OutDir"
Write-Host "CUDA index: $CudaIndex | cp$PythonTag | win_amd64"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Find-Python {
    if ($env:EXLLAMASHARP_PYTHON -and (Test-Path $env:EXLLAMASHARP_PYTHON)) { return $env:EXLLAMASHARP_PYTHON }
    foreach ($c in @("py", "python")) {
        $cmd = Get-Command $c -EA SilentlyContinue
        if (-not $cmd) { continue }
        if ($c -eq "py") {
            $v = & py -3 -c "import sys; print(sys.executable)" 2>$null
            if ($LASTEXITCODE -eq 0 -and $v) { return $v.Trim() }
        }
        else {
            $v = & $c -c "import sys; print(sys.executable)" 2>$null
            if ($LASTEXITCODE -eq 0 -and $v) { return $v.Trim() }
        }
    }
    return $null
}

$py = Find-Python
if (-not $py) { throw "Python required to download wheels (pip download)." }

Write-Step "pip download torch / torchvision / torchaudio ($CudaIndex)"
& $py -m pip download `
    torch torchvision torchaudio `
    --destination $OutDir `
    --index-url "https://download.pytorch.org/whl/$CudaIndex" `
    --python-version $PythonTag `
    --platform win_amd64 `
    --only-binary=:all:
if ($LASTEXITCODE -ne 0) { throw "pip download torch failed" }

Write-Step "pip download worker deps"
& $py -m pip download `
    "tokenizers>=0.21.1" "numpy>=1.26" "safetensors>=0.3.2" `
    rich typing_extensions pyyaml pillow pydantic ninja `
    --destination $OutDir `
    --python-version $PythonTag `
    --platform win_amd64 `
    --only-binary=:all:
# some packages are pure py - allow source if needed
if ($LASTEXITCODE -ne 0) {
    & $py -m pip download `
        "tokenizers>=0.21.1" numpy safetensors rich typing_extensions pyyaml pillow pydantic ninja `
        --destination $OutDir
}

# ExLlamaV3: try common torch minors for this Python
Write-Step "Download ExLlamaV3 prebuilt Windows wheels"
$torchMinors = @("2.11", "2.10", "2.9", "2.8", "2.7")
$gotExl = $false
foreach ($mm in $torchMinors) {
    $name = "exllamav3-$ExLlamaV3Version+cu128.torch${mm}.0-cp$PythonTag-cp$PythonTag-win_amd64.whl"
    $url = "https://github.com/turboderp-org/exllamav3/releases/download/v$ExLlamaV3Version/exllamav3-$ExLlamaV3Version%2Bcu128.torch${mm}.0-cp$PythonTag-cp$PythonTag-win_amd64.whl"
    $dest = Join-Path $OutDir $name
    if (Test-Path $dest) { $gotExl = $true; Write-Host "Already have $name"; continue }
    try {
        Write-Host "GET $name"
        Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
        $gotExl = $true
    }
    catch {
        Remove-Item $dest -Force -EA SilentlyContinue
    }
}
if (-not $gotExl) {
    Write-Warning "No ExLlamaV3 win wheel downloaded - online install may still fetch it later."
}

# triton-windows (best effort)
Write-Step "pip download triton-windows"
& $py -m pip download triton-windows --destination $OutDir 2>$null

$manifest = @{
    cudaIndex = $CudaIndex
    pythonTag = $PythonTag
    builtUtc = [DateTime]::UtcNow.ToString("o")
    files = @(Get-ChildItem $OutDir -File | Select-Object -ExpandProperty Name)
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir "manifest.json") -Encoding UTF8

$sizeGb = [math]::Round(((Get-ChildItem $OutDir -File | Measure-Object Length -Sum).Sum) / 1GB, 2)
Write-Host ""
Write-Host ("Offline wheels ready: {0} ({1} GB)" -f $OutDir, $sizeGb) -ForegroundColor Green
Write-Host "Build installer with: packaging\Build-Installer.ps1 -BundlePytorch"
