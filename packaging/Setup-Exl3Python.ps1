#Requires -Version 5.1
<#
.SYNOPSIS
  Downloads and installs the latest CUDA PyTorch (pip wheels from pytorch.org) into a venv
  next to the app (%ProgramFiles%\ExLlamaSharp\venv), plus the matching ExLlamaV3 Windows wheel.

  Note: PyTorch does not ship a single official CUDA "Setup.exe". This script downloads the
  current cu128 wheels from https://download.pytorch.org/whl/cu128 (same result as the
  official install instructions). MSI/ZIP stay small; this step is the remote GPU install.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging\Setup-Exl3Python.ps1
  powershell -ExecutionPolicy Bypass -File scripts\Setup-Exl3Python.ps1 -DownloadDemoModel
#>
[CmdletBinding()]
param(
    [string]$VenvPath = "",

    [ValidateSet("cu128", "cu126", "cu124")]
    [string]$CudaIndex = "cu128",

    [switch]$ForceReinstallTorch,

    [switch]$DownloadDemoModel,

    [switch]$SkipPythonBootstrap
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

function Write-DownloadBanner([string]$Title, [string]$Hint) {
    Write-Host ""
    Write-Host "================================================================" -ForegroundColor Yellow
    Write-Host " $Title" -ForegroundColor Yellow
    if ($Hint) { Write-Host " $Hint" -ForegroundColor DarkYellow }
    Write-Host " Do not close this window. Progress lines appear below." -ForegroundColor DarkYellow
    Write-Host "================================================================" -ForegroundColor Yellow
    Write-Host ""
    try { $Host.UI.RawUI.WindowTitle = "ExLlamaSharp - $Title" } catch { }
}

# Runs pip while printing elapsed-time heartbeats so long downloads look alive.
function Invoke-PipWithProgress {
    param(
        [Parameter(Mandatory = $true)][string]$PythonExe,
        [Parameter(Mandatory = $true)][string[]]$PipArgs,
        [Parameter(Mandatory = $true)][string]$Title,
        [string]$Hint = "Large download possible (often 1.5-3 GB / several minutes)."
    )

    Write-DownloadBanner -Title $Title -Hint $Hint

    $env:PYTHONUNBUFFERED = "1"
    $env:PIP_DISABLE_PIP_VERSION_CHECK = "1"
    $env:PIP_PROGRESS_BAR = "on"

    $outLog = Join-Path $env:TEMP ("exl3-pip-{0}.log" -f [Guid]::NewGuid().ToString("N").Substring(0, 8))
    $errLog = "$outLog.err"
    Remove-Item $outLog, $errLog -Force -EA SilentlyContinue

    # Ensure progress bar even when stdout is redirected
    $allArgs = @("-m", "pip") + $PipArgs
    if ($PipArgs -notcontains "--progress-bar") {
        $allArgs += @("--progress-bar", "on")
    }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $PythonExe
    $psi.Arguments = ($allArgs | ForEach-Object {
            if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
        }) -join " "
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = (Get-Location).Path

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $null = $proc.Start()

    $outHandler = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -Action {
        if ($null -ne $EventArgs.Data) {
            Write-Host $EventArgs.Data
            [System.IO.File]::AppendAllText($Event.MessageData, $EventArgs.Data + [Environment]::NewLine)
        }
    } -MessageData $outLog
    $errHandler = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -Action {
        if ($null -ne $EventArgs.Data) {
            Write-Host $EventArgs.Data
            [System.IO.File]::AppendAllText($Event.MessageData, $EventArgs.Data + [Environment]::NewLine)
        }
    } -MessageData $errLog

    $proc.BeginOutputReadLine()
    $proc.BeginErrorReadLine()

    $lastBeat = -15
    while (-not $proc.HasExited) {
        Start-Sleep -Seconds 2
        $sec = [int]$sw.Elapsed.TotalSeconds
        if (($sec - $lastBeat) -ge 15) {
            $lastBeat = $sec
            $mins = [math]::Floor($sec / 60)
            $rem = $sec % 60
            Write-Host ("--- still working: {0}m {1:D2}s elapsed (downloading/installing) ---" -f $mins, $rem) -ForegroundColor DarkCyan
        }
    }

    Start-Sleep -Milliseconds 400
    Unregister-Event -SourceIdentifier $outHandler.Name -EA SilentlyContinue
    Unregister-Event -SourceIdentifier $errHandler.Name -EA SilentlyContinue
    Remove-Job $outHandler, $errHandler -Force -EA SilentlyContinue

    $sw.Stop()
    Write-Host ("Finished in {0:N1} minutes (exit {1})." -f $sw.Elapsed.TotalMinutes, $proc.ExitCode) -ForegroundColor $(if ($proc.ExitCode -eq 0) { "Green" } else { "Yellow" })
    return $proc.ExitCode
}

function Invoke-WebDownloadWithProgress {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$OutFile,
        [string]$Title = "Downloading file"
    )
    Write-DownloadBanner -Title $Title -Hint $Uri
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        # Prefer BITS when available (shows transfer in UI / better resume); fallback to IWR
        if (Get-Command Start-BitsTransfer -EA SilentlyContinue) {
            Write-Host "Using BITS transfer..." -ForegroundColor DarkGray
            Start-BitsTransfer -Source $Uri -Destination $OutFile -DisplayName $Title -Description "ExLlamaSharp GPU runtime"
        }
        else {
            $ProgressPreference = "Continue"
            Invoke-WebRequest -Uri $Uri -OutFile $OutFile -UseBasicParsing
        }
        $mb = [math]::Round((Get-Item $OutFile).Length / 1MB, 1)
        Write-Host ("Downloaded {0} MB in {1:N1} min." -f $mb, $sw.Elapsed.TotalMinutes) -ForegroundColor Green
        return $true
    }
    catch {
        Write-Warning "Download failed: $($_.Exception.Message)"
        return $false
    }
}

# scripts\ next to Install.bat, or packaging\ in the repo
$ScriptsDir = $PSScriptRoot
$Parent = Split-Path -Parent $ScriptsDir
$PayloadCandidate = Join-Path $Parent "payload"
$InstallDirCandidate = Join-Path $env:ProgramFiles "ExLlamaSharp"

# Repo root if third_party exists; else treat as installer layout
$RepoRoot = $null
foreach ($c in @($Parent, (Split-Path -Parent $Parent))) {
    if (Test-Path (Join-Path $c "third_party\exllamav3")) {
        $RepoRoot = $c
        break
    }
}

$Exl3 = if ($RepoRoot) { Join-Path $RepoRoot "third_party\exllamav3" } else { $null }
$Req = if ($Exl3) { Join-Path $Exl3 "requirements.txt" } else { $null }

if ([string]::IsNullOrWhiteSpace($VenvPath)) {
    # App-local venv (same folder as ExLlamaSharp.Server.exe) - product install
    # Dev: reuse repo .venv-exl3 when present
    $appDirs = @(
        $InstallDirCandidate,
        $Parent,
        (Join-Path $Parent "payload"),
        (Split-Path -Parent $Parent)
    ) | Select-Object -Unique
    $appDir = $null
    foreach ($d in $appDirs) {
        if ($d -and (Test-Path (Join-Path $d "ExLlamaSharp.Server.exe"))) {
            $appDir = $d
            break
        }
    }
    $appVenv = if ($appDir) { Join-Path $appDir "venv" } else { Join-Path $InstallDirCandidate "venv" }
    $devVenv = if ($RepoRoot) { Join-Path $RepoRoot ".venv-exl3" } else { $null }
    $legacyVenv = Join-Path $env:ProgramData "ExLlamaSharp\venv"

    if ($devVenv -and (Test-Path (Join-Path $devVenv "Scripts\python.exe"))) {
        $VenvPath = $devVenv
    }
    elseif (Test-Path (Join-Path $appVenv "Scripts\python.exe")) {
        $VenvPath = $appVenv
    }
    elseif (Test-Path (Join-Path $legacyVenv "Scripts\python.exe")) {
        $VenvPath = $legacyVenv
    }
    else {
        $VenvPath = $appVenv
    }
}

Write-Host ""
Write-Host "ExLlamaSharp - PyTorch / EXL3 runtime setup" -ForegroundColor Green
Write-Host "Venv: $VenvPath"
Write-Host "PyTorch index: https://download.pytorch.org/whl/$CudaIndex"
Write-Host "Uses bundled offline-wheels\ when present; otherwise downloads ~2+ GB."
Write-Host "NOTE: This is NOT part of the MSI - run after install via Start Menu." -ForegroundColor DarkYellow
Write-Host ""

function Find-Python {
    if ($env:EXLLAMASHARP_PYTHON -and (Test-Path $env:EXLLAMASHARP_PYTHON)) {
        return $env:EXLLAMASHARP_PYTHON
    }
    foreach ($c in @("py", "python", "python3")) {
        $cmd = Get-Command $c -ErrorAction SilentlyContinue
        if (-not $cmd) { continue }
        try {
            if ($c -eq "py") {
                $ver = & py -3.12 -c "import sys; print(sys.executable)" 2>$null
                if ($LASTEXITCODE -ne 0 -or -not $ver) {
                    $ver = & py -3 -c "import sys; print(sys.executable)" 2>$null
                }
                if ($LASTEXITCODE -eq 0 -and $ver) { return $ver.Trim() }
            }
            else {
                $ver = & $c -c "import sys; print(sys.executable)" 2>$null
                if ($LASTEXITCODE -eq 0 -and $ver) { return $ver.Trim() }
            }
        }
        catch { }
    }
    return $null
}

function Install-PythonViaWinget {
    Write-Step "Python not found - installing Python 3.12 via winget"
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw "Python 3.11+ is required. Install from https://www.python.org/downloads/ (check 'Add to PATH') and re-run."
    }
    & winget install --id Python.Python.3.12 -e --accept-package-agreements --accept-source-agreements --disable-interactivity
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("Path", "User")
    Start-Sleep -Seconds 2
}

$basePy = Find-Python
if (-not $basePy -and -not $SkipPythonBootstrap) {
    Install-PythonViaWinget
    $basePy = Find-Python
}
if (-not $basePy) {
    throw "Python 3 not found. Install Python 3.11+ (Add to PATH) and re-run this script."
}
Write-Step "Base Python: $basePy"

$pyVenv = Join-Path $VenvPath "Scripts\python.exe"
if (-not (Test-Path $pyVenv)) {
    Write-Step "Creating venv at $VenvPath"
    New-Item -ItemType Directory -Force -Path $VenvPath | Out-Null
    & $basePy -m venv $VenvPath
    if ($LASTEXITCODE -ne 0) { throw "venv creation failed" }
}
if (-not (Test-Path $pyVenv)) {
    throw "venv python missing: $pyVenv"
}

Write-Step "Upgrading pip"
& $pyVenv -m pip install --upgrade pip wheel setuptools
if ($LASTEXITCODE -ne 0) { throw "pip upgrade failed" }

function Test-TorchCuda {
    $probe = Join-Path $env:TEMP "exl3-torch-probe.py"
    @'
import sys
try:
    import torch
    ok = torch.cuda.is_available()
    print("VERSION=" + torch.__version__)
    print("CUDA=" + str(ok))
    print("DEVICE=" + (torch.cuda.get_device_name(0) if ok else ""))
    sys.exit(0 if ok else 2)
except Exception as e:
    print("ERR=" + str(e))
    sys.exit(1)
'@ | Set-Content -Path $probe -Encoding UTF8
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $out = & $pyVenv $probe 2>&1
        $exit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prev
    }
    Write-Host (($out | ForEach-Object { "$_" }) -join "`n")
    return ($exit -eq 0)
}

function Find-OfflineWheels {
    $candidates = @(
        (Join-Path $ScriptsDir "..\offline-wheels"),
        (Join-Path $Parent "offline-wheels"),
        (Join-Path $InstallDirCandidate "offline-wheels"),
        (Join-Path $env:ProgramFiles "ExLlamaSharp\offline-wheels"),
        (Join-Path $PSScriptRoot "offline-wheels")
    )
    if ($RepoRoot) {
        $candidates = @((Join-Path $RepoRoot "packaging\offline-wheels")) + $candidates
    }
    foreach ($c in $candidates) {
        try {
            $full = [IO.Path]::GetFullPath($c)
            if ((Test-Path $full) -and (Get-ChildItem $full -Filter "*.whl" -EA SilentlyContinue | Select-Object -First 1)) {
                return $full
            }
        }
        catch { }
    }
    return $null
}

$offline = Find-OfflineWheels
if ($offline) {
    Write-Step "Bundled PyTorch wheels found: $offline"
}

$needTorch = $ForceReinstallTorch -or -not (Test-TorchCuda)
if ($needTorch) {
    $fromOffline = $false
    if ($offline) {
        $code = Invoke-PipWithProgress -PythonExe $pyVenv -Title "Installing PyTorch from offline-wheels" -Hint "Local wheels - no network download." -PipArgs @(
            "install", "--no-index", "--find-links", $offline, "torch", "torchvision", "torchaudio"
        )
        if ($code -eq 0 -and (Test-TorchCuda)) { $fromOffline = $true }
        else { Write-Warning "Offline torch install incomplete; will try online" }
    }
    if (-not $fromOffline) {
        $indexUrl = "https://download.pytorch.org/whl/$CudaIndex"
        $code = Invoke-PipWithProgress -PythonExe $pyVenv `
            -Title "Downloading PyTorch CUDA ($CudaIndex) from pytorch.org" `
            -Hint "Typically 1.5-3 GB. On a slow link this can take 10-30+ minutes." `
            -PipArgs @("install", "--upgrade", "torch", "torchvision", "torchaudio", "--index-url", $indexUrl)
        if ($code -ne 0) {
            Write-Warning "$CudaIndex failed; trying cu126"
            $null = Invoke-PipWithProgress -PythonExe $pyVenv `
                -Title "Downloading PyTorch CUDA (cu126 fallback)" `
                -Hint "Fallback index after cu128 failed." `
                -PipArgs @("install", "--upgrade", "torch", "--index-url", "https://download.pytorch.org/whl/cu126")
        }
    }
    if (-not (Test-TorchCuda)) {
        Write-Warning "torch installed but CUDA not available. Install/update the NVIDIA driver and re-run."
    }
}
else {
    Write-Step "Reusing existing CUDA torch in venv"
}

# Lightweight deps (no full third_party required)
Write-Step "Installing EXL3 worker Python deps"
$prevEa = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    if ($offline) {
        & $pyVenv -m pip install --no-index --find-links $offline `
            tokenizers numpy safetensors rich typing_extensions pyyaml pillow pydantic ninja 2>$null
    }
    if ($Req -and (Test-Path $Req)) {
        $tmpReq = Join-Path $env:TEMP "exl3-req-notorch.txt"
        Get-Content $Req | Where-Object { $_ -notmatch '^\s*torch' } | Set-Content $tmpReq -Encoding UTF8
        & $pyVenv -m pip install -r $tmpReq
    }
    else {
        & $pyVenv -m pip install --upgrade `
            "tokenizers>=0.21.1" "numpy>=1.26" "safetensors>=0.3.2" `
            "rich" "typing_extensions" "pyyaml" "pillow" "pydantic" "ninja" "huggingface_hub"
    }
}
finally {
    $ErrorActionPreference = $prevEa
}

Write-Step "Installing ExLlamaV3 CUDA wheel"
$pyVer = (& $pyVenv -c "import sys; print(f'{sys.version_info.major}{sys.version_info.minor}')").Trim()
$torchFull = (& $pyVenv -c "import torch; print(torch.__version__.split('+')[0])").Trim()
$mm = ($torchFull -split '\.')[0..1] -join '.'
$wheelName = "exllamav3-1.4.2+cu128.torch${mm}.0-cp$pyVer-cp$pyVer-win_amd64.whl"
$wheelOk = $false

if ($offline) {
    $localWhl = Get-ChildItem $offline -Filter "exllamav3-*.whl" -EA SilentlyContinue |
        Where-Object { $_.Name -match "cp$pyVer" -or $_.Name -match "py3-none" } |
        Select-Object -First 1
    if ($localWhl) {
        Write-Host "Offline: $($localWhl.Name)"
        & $pyVenv -m pip uninstall -y exllamav3 2>$null
        & $pyVenv -m pip install --no-index --find-links $offline --force-reinstall --no-deps $localWhl.FullName
        if ($LASTEXITCODE -eq 0) { $wheelOk = $true }
    }
}

if (-not $wheelOk) {
    $wheelUrl = "https://github.com/turboderp-org/exllamav3/releases/download/v1.4.2/exllamav3-1.4.2%2Bcu128.torch${mm}.0-cp$pyVer-cp$pyVer-win_amd64.whl"
    $wheelFile = Join-Path $env:TEMP $wheelName
    Write-Host "Wheel: $wheelName"
    $dlOk = Invoke-WebDownloadWithProgress -Uri $wheelUrl -OutFile $wheelFile -Title "Downloading ExLlamaV3 CUDA wheel"
    if ($dlOk) {
        & $pyVenv -m pip uninstall -y exllamav3 2>$null | Out-Null
        $code = Invoke-PipWithProgress -PythonExe $pyVenv -Title "Installing ExLlamaV3 wheel" -Hint $wheelName -PipArgs @(
            "install", "--force-reinstall", "--no-deps", $wheelFile
        )
        if ($code -eq 0) { $wheelOk = $true }
    }
    else {
        Write-Warning "Exact wheel download failed"
    }
}

if (-not $wheelOk) {
    Write-Step "Trying py3-none-any + note (may need local compile)"
    try {
        & $pyVenv -m pip install "exllamav3==1.4.2"
        $wheelOk = ($LASTEXITCODE -eq 0)
    }
    catch { }

    if (-not $wheelOk -and $Exl3 -and (Test-Path $Exl3)) {
        Write-Warning "Falling back to editable third_party/exllamav3 (needs VS2022 + nvcc)"
        Push-Location $Exl3
        try {
            & $pyVenv -m pip install -e .
            if ($LASTEXITCODE -ne 0) { throw "pip install -e exllamav3 failed" }
            $wheelOk = $true
        }
        finally { Pop-Location }
    }
}

if (-not $wheelOk) {
    throw "Could not install exllamav3. Check Python version (3.11-3.13) and torch CUDA build."
}

if ($env:OS -match "Windows") {
    Write-Step "Installing triton-windows (required for EXL3 attention on Windows)"
    $ErrorActionPreference = "Continue"
    try { & $pyVenv -m pip install --upgrade "triton-windows" ninja } finally { $ErrorActionPreference = "Stop" }
}

Write-Step "Verifying imports"
$verifyFile = Join-Path $env:TEMP "exl3-verify-import.py"
[System.IO.File]::WriteAllText($verifyFile, @"
from exllamav3 import Config, Model, Cache, Tokenizer, Generator
import torch
print("exllamav3 OK; cuda=", torch.cuda.is_available(), "; torch=", torch.__version__)
"@)
$env:Path = "$(Join-Path $VenvPath 'Scripts');$env:Path"
& $pyVenv $verifyFile
if ($LASTEXITCODE -ne 0) { throw "exllamav3 import verification failed" }

# Persist path for the Windows Service / UI (app dir + ProgramData pointer)
$cfgObj = @{
    python = $pyVenv
    venv = $VenvPath
    cudaIndex = $CudaIndex
    builtUtc = [DateTime]::UtcNow.ToString("o")
}
$cfgJson = $cfgObj | ConvertTo-Json

$appRoot = Split-Path -Parent $pyVenv
if ((Split-Path -Leaf $appRoot) -eq "Scripts") {
    $appRoot = Split-Path -Parent $appRoot  # ...\venv
    $appRoot = Split-Path -Parent $appRoot  # install / repo root
}
foreach ($cfgDir in @($appRoot, (Join-Path $env:ProgramData "ExLlamaSharp"))) {
    if (-not $cfgDir) { continue }
    try {
        New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
        Set-Content (Join-Path $cfgDir "exl3-runtime.json") $cfgJson -Encoding UTF8
    }
    catch {
        Write-Warning "Could not write exl3-runtime.json under $cfgDir : $_"
    }
}
$cfgPath = Join-Path $env:ProgramData "ExLlamaSharp\exl3-runtime.json"

try {
    [Environment]::SetEnvironmentVariable("EXLLAMASHARP_PYTHON", $pyVenv, "Machine")
}
catch {
    [Environment]::SetEnvironmentVariable("EXLLAMASHARP_PYTHON", $pyVenv, "User")
}
$env:EXLLAMASHARP_PYTHON = $pyVenv

$marker = Join-Path $VenvPath "exllamasharp-exl3.ok"
@(
    "python=$pyVenv"
    "exllamav3=prebuilt-wheel"
    "builtUtc=$([DateTime]::UtcNow.ToString('o'))"
) | Set-Content $marker -Encoding UTF8

Write-Host ""
Write-Host "PyTorch / EXL3 runtime ready." -ForegroundColor Green
Write-Host "  Python : $pyVenv"
Write-Host "  Config : $cfgPath"
Write-Host "  Env    : EXLLAMASHARP_PYTHON set"

if ($DownloadDemoModel) {
    $demo = Join-Path $ScriptsDir "Download-DemoModel.ps1"
    if (Test-Path $demo) {
        Write-Step "Downloading demo EXL3 model"
        & powershell -NoProfile -ExecutionPolicy Bypass -File $demo
    }
}

Write-Host ""
Write-Host "Next: restart the ExLlamaSharp service (if installed), then load an EXL3 model in the Admin UI." -ForegroundColor Cyan
