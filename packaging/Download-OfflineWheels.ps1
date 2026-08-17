#Requires -Version 5.1
<#
.SYNOPSIS
  Downloads GPU runtime extras into packaging\offline-wheels and packaging\redist
  so Build-Installer.ps1 can embed them in Setup.exe.

  Default: ExLlamaV3 CUDA wheel, worker deps, Python 3.12 installer, VC++ redist.
  PyTorch is NOT bundled (too large); the installer downloads it from pytorch.org.
  Pass -IncludePytorch only for a fully offline cache.
#>
[CmdletBinding()]
param(
    [string]$OutDir = "",

    [ValidateSet("cu128", "cu126", "cu124")]
    [string]$CudaIndex = "cu128",

    [ValidateSet("312", "311", "313")]
    [string]$PythonTag = "312",

    [string]$ExLlamaV3Version = "1.4.2",

    [string]$PythonInstallerVersion = "3.12.10",

    [string]$TorchMinor = "2.11",

    [switch]$IncludePytorch
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $Root "packaging\offline-wheels"
}
$RedistDir = Join-Path $Root "packaging\redist"

function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

Write-Host "ExLlamaSharp full runtime download" -ForegroundColor Green
Write-Host "Wheels: $OutDir"
Write-Host "Redist: $RedistDir"
Write-Host "CUDA index: $CudaIndex | cp$PythonTag | win_amd64"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
New-Item -ItemType Directory -Force -Path $RedistDir | Out-Null

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

function Get-FileIfMissing([string]$Url, [string]$Dest, [string]$Label) {
    if ((Test-Path $Dest) -and ((Get-Item $Dest).Length -gt 1MB)) {
        Write-Host "Already have $Label"
        return
    }
    Write-Host "GET $Label"
    Invoke-WebRequest -Uri $Url -OutFile $Dest -UseBasicParsing
    if (-not (Test-Path $Dest) -or ((Get-Item $Dest).Length -lt 1MB)) {
        throw "Download failed: $Url"
    }
}

function Invoke-PipDownload {
    param(
        [Parameter(Mandatory = $true)][string]$PythonExe,
        [Parameter(Mandatory = $true)][string[]]$PipArgs
    )
    $argList = @("-m", "pip", "download") + $PipArgs
    $p = Start-Process -FilePath $PythonExe -ArgumentList $argList -Wait -PassThru -NoNewWindow
    return $p.ExitCode
}

$py = Find-Python
if (-not $py) { throw "Python required to download wheels (pip download)." }

Write-Step "pip / wheel / setuptools"
$code = Invoke-PipDownload -PythonExe $py -PipArgs @("pip", "wheel", "setuptools", "-d", $OutDir, "--only-binary=:all:")
if ($code -ne 0) {
    $null = Invoke-PipDownload -PythonExe $py -PipArgs @("pip", "wheel", "setuptools", "-d", $OutDir)
}

Write-Step "pip download torch / torchvision / torchaudio ($CudaIndex)"
if (-not $IncludePytorch) {
    Write-Host "Skipping PyTorch wheels (downloaded at install time from pytorch.org). Use -IncludePytorch for a full offline cache."
} else {
    $existingTorch = Get-ChildItem $OutDir -Filter "torch-*.whl" -File -EA SilentlyContinue |
        Where-Object { $_.Name -notmatch "torchvision|torchaudio" -and $_.Length -gt 500MB } |
        Select-Object -First 1
    if ($existingTorch) {
        Write-Host "Already have $($existingTorch.Name) ($([math]::Round($existingTorch.Length/1GB, 2)) GB)"
    } else {
        $code = Invoke-PipDownload -PythonExe $py -PipArgs @(
            "torch", "torchvision", "torchaudio",
            "-d", $OutDir,
            "--index-url", "https://download.pytorch.org/whl/$CudaIndex",
            "--python-version", $PythonTag,
            "--platform", "win_amd64",
            "--only-binary=:all:"
        )
        if ($code -ne 0) { throw "pip download torch failed (exit $code)" }
    }
}

$mm = $TorchMinor
$torchWhl = Get-ChildItem $OutDir -Filter "torch-*.whl" -File -EA SilentlyContinue |
    Where-Object { $_.Name -notmatch "torchvision|torchaudio" } |
    Select-Object -First 1
if ($torchWhl -and $torchWhl.Name -match 'torch-(\d+\.\d+)') {
    $mm = $Matches[1]
}
Write-Host "Matching ExLlamaV3 wheel to torch $mm"

Write-Step "pip download worker deps"
$code = Invoke-PipDownload -PythonExe $py -PipArgs @(
    "tokenizers>=0.21.1", "numpy>=1.26", "safetensors>=0.3.2",
    "rich", "typing_extensions", "pyyaml", "pillow", "pydantic", "ninja", "huggingface_hub",
    "-d", $OutDir,
    "--python-version", $PythonTag,
    "--platform", "win_amd64",
    "--only-binary=:all:"
)
if ($code -ne 0) {
    $null = Invoke-PipDownload -PythonExe $py -PipArgs @(
        "tokenizers>=0.21.1", "numpy", "safetensors", "rich", "typing_extensions", "pyyaml", "pillow", "pydantic", "ninja", "huggingface_hub",
        "-d", $OutDir
    )
}

Write-Step "pip download triton-windows"
$null = Invoke-PipDownload -PythonExe $py -PipArgs @("triton-windows", "-d", $OutDir)

Write-Step "Download ExLlamaV3 CUDA wheel (prebuilt .pyd, not PyPI source)"
$name = "exllamav3-$ExLlamaV3Version+cu128.torch${mm}.0-cp$PythonTag-cp$PythonTag-win_amd64.whl"
$url = "https://github.com/turboderp-org/exllamav3/releases/download/v$ExLlamaV3Version/exllamav3-$ExLlamaV3Version%2Bcu128.torch${mm}.0-cp$PythonTag-cp$PythonTag-win_amd64.whl"
$dest = Join-Path $OutDir $name
$gotExl = $false
if ((Test-Path $dest) -and ((Get-Item $dest).Length -gt 10MB)) {
    $gotExl = $true
    Write-Host "Already have $name"
}
else {
    try {
        Write-Host "GET $name"
        Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
        if ((Test-Path $dest) -and ((Get-Item $dest).Length -gt 10MB)) { $gotExl = $true }
    }
    catch {
        Remove-Item $dest -Force -EA SilentlyContinue
    }
}

if (-not $gotExl) {
    foreach ($fallback in @("2.11", "2.10", "2.9", "2.8", "2.7")) {
        if ($fallback -eq $mm) { continue }
        $fname = "exllamav3-$ExLlamaV3Version+cu128.torch${fallback}.0-cp$PythonTag-cp$PythonTag-win_amd64.whl"
        $furl = "https://github.com/turboderp-org/exllamav3/releases/download/v$ExLlamaV3Version/exllamav3-$ExLlamaV3Version%2Bcu128.torch${fallback}.0-cp$PythonTag-cp$PythonTag-win_amd64.whl"
        $fdest = Join-Path $OutDir $fname
        try {
            Write-Host "Fallback GET $fname"
            Invoke-WebRequest -Uri $furl -OutFile $fdest -UseBasicParsing
            if ((Test-Path $fdest) -and ((Get-Item $fdest).Length -gt 10MB)) {
                $gotExl = $true
                break
            }
        }
        catch {
            Remove-Item $fdest -Force -EA SilentlyContinue
        }
    }
}

if (-not $gotExl) {
    throw "Could not download a prebuilt ExLlamaV3 CUDA wheel for cp$PythonTag / torch $mm"
}

# Remove the tiny PyPI source wheel if pip pulled it by accident
Get-ChildItem $OutDir -Filter "exllamav3-*-py3-none-any.whl" -EA SilentlyContinue | Remove-Item -Force

Write-Step "Python $PythonInstallerVersion installer + VC++ redistributable"
$pyExeName = "python-$PythonInstallerVersion-amd64.exe"
Get-FileIfMissing `
    -Url "https://www.python.org/ftp/python/$PythonInstallerVersion/$pyExeName" `
    -Dest (Join-Path $RedistDir $pyExeName) `
    -Label $pyExeName
Get-FileIfMissing `
    -Url "https://aka.ms/vs/17/release/vc_redist.x64.exe" `
    -Dest (Join-Path $RedistDir "vc_redist.x64.exe") `
    -Label "vc_redist.x64.exe"

$manifest = @{
    cudaIndex = $CudaIndex
    pythonTag = $PythonTag
    torchWheel = $torchWhl.Name
    exllamav3Version = $ExLlamaV3Version
    pythonInstaller = $pyExeName
    builtUtc = [DateTime]::UtcNow.ToString("o")
    files = @(Get-ChildItem $OutDir -File | Select-Object -ExpandProperty Name)
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir "manifest.json") -Encoding UTF8

$sizeGb = [math]::Round(((Get-ChildItem $OutDir -File | Measure-Object Length -Sum).Sum) / 1GB, 2)
Write-Host ""
Write-Host ("Offline wheels ready: {0} ({1} GB)" -f $OutDir, $sizeGb) -ForegroundColor Green
Write-Host "Redist: $RedistDir" -ForegroundColor Green
Write-Host "Build installer with: packaging\Build-Installer.ps1" -ForegroundColor Green
