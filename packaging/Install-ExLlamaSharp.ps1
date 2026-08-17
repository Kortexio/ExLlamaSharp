#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Single ExLlamaSharp installer.

.DESCRIPTION
  - Copies payload
  - VC++ Redistributable (if missing)
  - Creates/reuses venv and installs PyTorch (does not wipe a working venv)
  - Registers the Windows service via New-Service
  - Firewall, shortcuts, Tray
  - Frees the port and starts with a health check

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Install.ps1
  powershell -ExecutionPolicy Bypass -File Install.ps1 -SkipPyTorch -Unattended
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\ExLlamaSharp",
    [int]$Port = 14563,
    [switch]$SkipPyTorch,
    [switch]$SkipVCRedist,
    [switch]$Unattended,
    [switch]$ForceRecreateVenv
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$ServiceName = "ExLlamaSharp"
$UiUrl = "http://127.0.0.1:$Port"
$LogFile = Join-Path $env:TEMP "ExLlamaSharp-Install.log"

function Write-Log([string]$Message, [string]$Level = "INFO") {
    $line = "[{0}] [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
    switch ($Level) {
        "OK"   { Write-Host "OK   $Message" -ForegroundColor Green }
        "WARN" { Write-Host "WARN $Message" -ForegroundColor Yellow }
        "ERR"  { Write-Host "ERR  $Message" -ForegroundColor Red }
        "STEP" { Write-Host "`n==> $Message" -ForegroundColor Cyan }
        default { Write-Host "     $Message" -ForegroundColor Gray }
    }
}

function Test-IsAdmin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-PayloadDir {
    foreach ($c in @(
            (Join-Path $PSScriptRoot "payload"),
            (Join-Path $PSScriptRoot "..\publish\installer\payload")
        )) {
        $resolved = [IO.Path]::GetFullPath($c)
        if (Test-Path (Join-Path $resolved "ExLlamaSharp.Server.exe")) { return $resolved }
    }
    return $null
}

function Stop-ExLlamaProcesses {
    Get-Process -Name "ExLlamaSharp.Server", "ExLlamaSharp.Tray" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

function Clear-Port([int]$PortNumber) {
    $conns = Get-NetTCPConnection -LocalPort $PortNumber -State Listen -ErrorAction SilentlyContinue
    foreach ($c in $conns) {
        $procId = $c.OwningProcess
        if ($procId -and $procId -gt 0) {
            Write-Log "Freeing port $PortNumber (PID $procId)" "WARN"
            Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
        }
    }
}

function Remove-ExLlamaService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { return }
    Write-Log "Removing existing service..."
    try { Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue } catch {}
    Start-Sleep -Seconds 2
    & sc.exe delete $ServiceName | Out-Null
    # Wait until SCM drops it
    for ($i = 0; $i -lt 20; $i++) {
        if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Seconds 1
    }
}

function Test-TorchOk([string]$PythonExe) {
    if (-not (Test-Path $PythonExe)) { return $false }
    & $PythonExe -c "import torch" 2>$null | Out-Null
    return ($LASTEXITCODE -eq 0)
}

function New-InternetShortcut([string]$Path, [string]$Url, [string]$Icon = "") {
    $body = "[InternetShortcut]`r`nURL=$Url"
    if ($Icon -and (Test-Path $Icon)) {
        $body += "`r`nIconFile=$Icon`r`nIconIndex=0"
    }
    $body | Set-Content -Path $Path -Encoding ASCII
}

function New-AppShortcut([string]$Path, [string]$Target, [string]$Arguments = "", [string]$Icon = "", [string]$WorkDir = "") {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($Path)
    $lnk.TargetPath = $Target
    $lnk.Arguments = $Arguments
    if ($WorkDir) { $lnk.WorkingDirectory = $WorkDir }
    if ($Icon -and (Test-Path $Icon)) { $lnk.IconLocation = "$Icon,0" }
    $lnk.Save()
}

# ---- start ----
"" | Set-Content $LogFile -Encoding UTF8
Write-Host "===============================================================" -ForegroundColor Cyan
Write-Host "  ExLlamaSharp Installer v2.1" -ForegroundColor Cyan
Write-Host "  Log: $LogFile" -ForegroundColor DarkGray
Write-Host "===============================================================" -ForegroundColor Cyan

if (-not (Test-IsAdmin)) {
    Write-Log "Administrator privileges required" "ERR"
    exit 1
}
Write-Log "Admin OK" "OK"

$pythonCmd = Get-Command python -ErrorAction SilentlyContinue
if (-not $pythonCmd) {
    Write-Log "Python 3.10+ not found on PATH" "ERR"
    exit 1
}
Write-Log ("Python: " + (& python --version 2>&1)) "OK"

$payload = Resolve-PayloadDir
if (-not $payload) {
    Write-Log "Payload not found (run Build-Installer.ps1 or use the ZIP)" "ERR"
    exit 1
}
Write-Log "Payload: $payload" "OK"

# 1) Stop leftovers
Write-Log "Cleaning processes / port / service" "STEP"
Stop-ExLlamaProcesses
Clear-Port $Port
Remove-ExLlamaService
Write-Log "Cleanup finished" "OK"

# 2) Copy files (preserve existing venv directory during copy)
Write-Log "Copying files to $InstallDir" "STEP"
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
$venvPath = Join-Path $InstallDir "venv"
# Copy everything except wiping a good venv: copy payload items individually
Get-ChildItem $payload -Force | ForEach-Object {
    $dest = Join-Path $InstallDir $_.Name
    # Skip venv if it already exists — will be validated/reused below
    if ($_.Name -eq "venv" -and (Test-Path $dest)) {
        Write-Log "Keeping existing venv" "OK"
        return
    }
    if ($_.PSIsContainer) {
        Copy-Item $_.FullName $dest -Recurse -Force
    } else {
        Copy-Item $_.FullName $dest -Force
    }
}
$dataDir = Join-Path $env:ProgramData "ExLlamaSharp"
@($dataDir, (Join-Path $dataDir "logs"), (Join-Path $dataDir "models"), (Join-Path $dataDir "backups")) | ForEach-Object {
    if (-not (Test-Path $_)) { New-Item -ItemType Directory -Path $_ -Force | Out-Null }
}
Write-Log "Files copied" "OK"

# 3) VC++
if (-not $SkipVCRedist) {
    Write-Log "Visual C++ Redistributable" "STEP"
    $vcKey = "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"
    $vcOk = $false
    if (Test-Path $vcKey) {
        $ver = (Get-ItemProperty $vcKey -Name Version -ErrorAction SilentlyContinue).Version
        if ($ver) { $vcOk = $true; Write-Log "Already installed ($ver)" "OK" }
    }
    if (-not $vcOk) {
        $vcInstaller = Join-Path $env:TEMP "vc_redist.x64.exe"
        Invoke-WebRequest -Uri "https://aka.ms/vs/17/release/vc_redist.x64.exe" -OutFile $vcInstaller -UseBasicParsing
        Start-Process $vcInstaller -ArgumentList "/install /quiet /norestart" -Wait
        Remove-Item $vcInstaller -Force -ErrorAction SilentlyContinue
        Write-Log "VC++ installed" "OK"
    }
}

# 4) venv + PyTorch — never delete a venv if torch already works
Write-Log "Python venv / PyTorch" "STEP"
# $venvPath already defined in step 2
$pythonExe = Join-Path $venvPath "Scripts\python.exe"

$torchOk = Test-TorchOk $pythonExe
if ($ForceRecreateVenv -and (Test-Path $venvPath)) {
    Write-Log "ForceRecreateVenv: removing venv" "WARN"
    Remove-Item $venvPath -Recurse -Force
    $torchOk = $false
}

if (-not (Test-Path $pythonExe)) {
    Write-Log "Creating venv..."
    & python -m venv $venvPath
    if ($LASTEXITCODE -ne 0) { Write-Log "Failed to create venv" "ERR"; exit 1 }
    $pythonExe = Join-Path $venvPath "Scripts\python.exe"
    Write-Log "venv created" "OK"
} else {
    Write-Log "Reusing existing venv" "OK"
}

if (-not $SkipPyTorch) {
    if ($torchOk) {
        Write-Log "PyTorch already present — skipping download" "OK"
    } else {
        Write-Log "Installing PyTorch cu128 (~2-3 GB)..."
        & $pythonExe -m pip install --upgrade pip --quiet
        & $pythonExe -m pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu128
        if ($LASTEXITCODE -ne 0) {
            Write-Log "PyTorch install failed — retry with -SkipPyTorch and run Setup-Exl3Python.bat later" "ERR"
            exit 1
        }
        # Optional exllamav3 — never fail install
        & $pythonExe -m pip install huggingface_hub 2>&1 | Out-Null
        & $pythonExe -m pip install exllamav3 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { Write-Log "exllamav3 OK" "OK" }
        else { Write-Log "Optional exllamav3 failed (VS/CUDA) — native backend still OK" "WARN" }

        & $pythonExe -c "import torch; print(torch.__version__, 'cuda=', torch.cuda.is_available())"
        Write-Log "PyTorch ready" "OK"
    }
} else {
    Write-Log "PyTorch skipped (-SkipPyTorch)" "WARN"
}

# 5) runtime json
@{
    runtime = "python"
    venv    = $venvPath
    python  = $pythonExe
} | ConvertTo-Json | Set-Content (Join-Path $InstallDir "exl3-runtime.json") -Encoding UTF8
Write-Log "exl3-runtime.json" "OK"

# 6) Windows Service — New-Service (correct quoting with Program Files)
Write-Log "Registering Windows Service" "STEP"
$serverExe = Join-Path $InstallDir "ExLlamaSharp.Server.exe"
if (-not (Test-Path $serverExe)) { Write-Log "Missing $serverExe" "ERR"; exit 1 }

# BinaryPathName must quote paths with spaces
$binPath = "`"$serverExe`""
try {
    New-Service -Name $ServiceName `
        -BinaryPathName $binPath `
        -DisplayName "ExLlamaSharp LLM Server" `
        -Description "Local LLM server (OpenAI-compatible API + Admin UI)" `
        -StartupType Automatic | Out-Null
} catch {
    Write-Log "New-Service failed: $($_.Exception.Message) — trying sc.exe" "WARN"
    $createOut = & sc.exe create $ServiceName binPath= $binPath DisplayName= "ExLlamaSharp LLM Server" start= auto 2>&1
    Write-Log ("sc.exe: " + ($createOut | Out-String).Trim())
    if ($LASTEXITCODE -ne 0 -and -not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
        Write-Log "Failed to create service" "ERR"
        exit 1
    }
}
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Write-Log "Service registered" "OK"

# 7) Firewall
Write-Log "Firewall" "STEP"
Get-NetFirewallRule -DisplayName "ExLlamaSharp Server" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName "ExLlamaSharp Server" `
    -Direction Inbound -Program $serverExe -Action Allow -Profile Any -Enabled True | Out-Null
Write-Log "Rule created" "OK"

# 8) Shortcuts with branded icon
Write-Log "Shortcuts" "STEP"
$iconFile = Join-Path $InstallDir "exllamasharp.ico"
if (-not (Test-Path $iconFile)) {
    foreach ($c in @(
        (Join-Path $PSScriptRoot "assets\exllamasharp.ico"),
        (Join-Path $PSScriptRoot "exllamasharp.ico"),
        (Join-Path $payload "exllamasharp.ico")
    )) {
        if (Test-Path $c) {
            Copy-Item $c $iconFile -Force
            break
        }
    }
}

$desktop = [Environment]::GetFolderPath("CommonDesktopDirectory")
if (-not $desktop) { $desktop = [Environment]::GetFolderPath("Desktop") }
New-InternetShortcut (Join-Path $desktop "ExLlamaSharp.url") $UiUrl $iconFile

$startMenu = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\ExLlamaSharp"
New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
Get-ChildItem $startMenu -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
New-AppShortcut (Join-Path $startMenu "ExLlamaSharp.lnk") `
    "$env:SystemRoot\System32\cmd.exe" "/C start $UiUrl" $iconFile $InstallDir
New-InternetShortcut (Join-Path $startMenu "ExLlamaSharp.url") $UiUrl $iconFile

$trayExe = Join-Path $InstallDir "ExLlamaSharp.Tray.exe"
if (Test-Path $trayExe) {
    New-AppShortcut (Join-Path $startMenu "ExLlamaSharp Tray.lnk") $trayExe "" $iconFile $InstallDir
    $startup = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\StartUp"
    if (-not (Test-Path $startup)) { $startup = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Startup" }
    New-AppShortcut (Join-Path $startup "ExLlamaSharp Tray.lnk") $trayExe "" $iconFile $InstallDir
}
Write-Log "Shortcuts OK" "OK"

# 9) Start service
Write-Log "Starting service" "STEP"
Clear-Port $Port
Stop-ExLlamaProcesses
Start-Service -Name $ServiceName
Start-Sleep -Seconds 4

$service = Get-Service -Name $ServiceName
if ($service.Status -ne "Running") {
    Write-Log "Service did not reach Running ($($service.Status)). Trying console..." "WARN"
    # Fallback: start as process so user is not stuck
    Start-Process -FilePath $serverExe -WorkingDirectory $InstallDir -WindowStyle Hidden
    Start-Sleep -Seconds 5
}

$ready = $false
for ($i = 0; $i -lt 18; $i++) {
    try {
        $r = Invoke-WebRequest -Uri "$UiUrl/health" -UseBasicParsing -TimeoutSec 3
        if ($r.StatusCode -eq 200) { $ready = $true; break }
    } catch {
        Start-Sleep -Seconds 2
    }
}

if ($ready) {
    Write-Log "Health OK ($UiUrl/health)" "OK"
} else {
    Write-Log "Health did not respond — see Event Viewer / $LogFile" "WARN"
}

if ((Test-Path $trayExe) -and -not (Get-Process -Name "ExLlamaSharp.Tray" -ErrorAction SilentlyContinue)) {
    Start-Process $trayExe -WorkingDirectory $InstallDir
    Write-Log "Tray started" "OK"
}

Write-Host ""
Write-Host "===============================================================" -ForegroundColor Green
Write-Host "  Installation complete" -ForegroundColor Green
Write-Host "===============================================================" -ForegroundColor Green
Write-Host "  UI:      $UiUrl"
Write-Host "  Dir:     $InstallDir"
Write-Host "  Log:     $LogFile"
Write-Host "  Service: $((Get-Service $ServiceName -EA SilentlyContinue).Status)"
Write-Host ""

if (-not $Unattended -and $ready) {
    Start-Process $UiUrl
}

if ($ready) { exit 0 } else { exit 2 }
