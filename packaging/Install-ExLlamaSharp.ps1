#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Single ExLlamaSharp installer.

.DESCRIPTION
  - Copies payload
  - VC++ Redistributable (if missing)
  - Creates/reuses venv and installs PyTorch CUDA + the official ExLlamaV3 CUDA wheel
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
    [ValidateSet("desktop", "headless")]
    [string]$HostMode = "desktop",
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

function Test-Exl3ExtOk([string]$PythonExe) {
    if (-not (Test-Path $PythonExe)) { return $false }
    & $PythonExe -c "import importlib.util, sys; s=importlib.util.find_spec('exllamav3_ext'); sys.exit(0 if s and s.origin and s.origin.endswith(('.pyd','.so')) else 1)" 2>$null | Out-Null
    return ($LASTEXITCODE -eq 0)
}

function Resolve-OfflineWheelsDir {
    foreach ($c in @(
            (Join-Path $InstallDir "offline-wheels"),
            (Join-Path $PSScriptRoot "offline-wheels"),
            (Join-Path $PSScriptRoot "payload\offline-wheels")
        )) {
        if ((Test-Path $c) -and (Get-ChildItem $c -Filter "*.whl" -ErrorAction SilentlyContinue | Select-Object -First 1)) {
            return $c
        }
    }
    return $null
}

function Resolve-RedistDir {
    foreach ($c in @(
            (Join-Path $InstallDir "redist"),
            (Join-Path $PSScriptRoot "payload\redist"),
            (Join-Path $PSScriptRoot "redist")
        )) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

function Invoke-VenvPip {
    param(
        [Parameter(Mandatory = $true)][string]$PythonExe,
        [Parameter(Mandatory = $true)][string[]]$PipArgs
    )
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        # pip writes progress/notices to stderr; do not let that become a failing exit code
        $output = & $PythonExe -m pip @PipArgs 2>&1
        $exitCode = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
        foreach ($line in @($output)) {
            if ($null -eq $line) { continue }
            if ($line -is [System.Management.Automation.ErrorRecord]) {
                Write-Host ($line.ToString()) -ForegroundColor DarkGray
            } else {
                Write-Host ($line.ToString()) -ForegroundColor Gray
            }
        }
        return $exitCode
    }
    finally {
        $ErrorActionPreference = $prev
    }
}

function Install-ExLlamaV3CudaWheel([string]$PythonExe) {
    Write-Log "ExLlamaV3 CUDA extension (prebuilt wheel, not PyPI source)" "STEP"
    if (Test-Exl3ExtOk $PythonExe) {
        Write-Log "exllamav3_ext already present" "OK"
        & $PythonExe -m pip install --upgrade huggingface_hub ninja | Out-Null
        return
    }

    $pyVer = (& $PythonExe -c "import sys; print(f'{sys.version_info.major}{sys.version_info.minor}')").Trim()
    $mm = (& $PythonExe -c "import torch; print('.'.join(torch.__version__.split('+')[0].split('.')[:2]))").Trim()
    if (-not $pyVer -or -not $mm) {
        throw "Cannot resolve Python/torch versions for the ExLlamaV3 CUDA wheel"
    }

    $wheelName = "exllamav3-1.4.2+cu128.torch${mm}.0-cp$pyVer-cp$pyVer-win_amd64.whl"
    $wheelFile = $null
    $offline = Resolve-OfflineWheelsDir
    if ($offline) {
        $local = Get-ChildItem $offline -Filter "exllamav3-*.whl" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match "cp$pyVer" -and $_.Name -match [regex]::Escape("torch$mm") -and $_.Name -match "win_amd64" } |
            Select-Object -First 1
        if (-not $local) {
            $local = Get-ChildItem $offline -Filter "exllamav3-*.whl" -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match "cp$pyVer" -and $_.Name -match "win_amd64" -and $_.Name -notmatch "py3-none" } |
                Select-Object -First 1
        }
        if ($local) {
            $wheelFile = $local.FullName
            Write-Log "Using bundled $($local.Name)"
        }
    }

    if (-not $wheelFile) {
        $url = "https://github.com/turboderp-org/exllamav3/releases/download/v1.4.2/exllamav3-1.4.2%2Bcu128.torch${mm}.0-cp$pyVer-cp$pyVer-win_amd64.whl"
        $wheelFile = Join-Path $env:TEMP $wheelName
        Write-Log "Downloading $wheelName (~242 MB)"
        Invoke-WebRequest -Uri $url -OutFile $wheelFile -UseBasicParsing
        if (-not (Test-Path $wheelFile) -or ((Get-Item $wheelFile).Length -lt 1MB)) {
            throw "Failed to download ExLlamaV3 CUDA wheel: $url"
        }
    }

    $null = Invoke-VenvPip -PythonExe $PythonExe -PipArgs @("uninstall", "-y", "exllamav3")
    $code = Invoke-VenvPip -PythonExe $PythonExe -PipArgs @("install", "--force-reinstall", "--no-deps", $wheelFile)
    if ($code -ne 0) {
        throw "pip install of ExLlamaV3 CUDA wheel failed"
    }
    $null = Invoke-VenvPip -PythonExe $PythonExe -PipArgs @("install", "--upgrade", "huggingface_hub", "ninja", "triton-windows")

    if (-not (Test-Exl3ExtOk $PythonExe)) {
        throw "exllamav3_ext.pyd missing after wheel install. The PyPI source package is not enough for inference."
    }
    Write-Log "exllamav3 CUDA extension OK" "OK"
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
Write-Host "  ExLlamaSharp Installer v2.2" -ForegroundColor Cyan
Write-Host "  Log: $LogFile" -ForegroundColor DarkGray
Write-Host "===============================================================" -ForegroundColor Cyan

if (-not (Test-IsAdmin)) {
    Write-Log "Administrator privileges required" "ERR"
    exit 1
}
Write-Log "Admin OK" "OK"

$payload = Resolve-PayloadDir
if (-not $payload) {
    Write-Log "Payload not found (run Build-Installer.ps1 or use the ZIP)" "ERR"
    exit 1
}
Write-Log "Payload: $payload" "OK"

function Refresh-ProcessPath {
    $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
                [Environment]::GetEnvironmentVariable("Path", "User")
}

$pythonCmd = Get-Command python -ErrorAction SilentlyContinue
if (-not $pythonCmd) {
    $pySetup = Get-ChildItem (Join-Path $payload "redist") -Filter "python-*.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($pySetup) {
        Write-Log "Installing bundled Python ($($pySetup.Name))" "STEP"
        $p = Start-Process -FilePath $pySetup.FullName -ArgumentList "/quiet InstallAllUsers=1 PrependPath=1 Include_pip=1 Include_test=0 SimpleInstall=1" -Wait -PassThru
        Refresh-ProcessPath
        $pythonCmd = Get-Command python -ErrorAction SilentlyContinue
        if (-not $pythonCmd) {
            Write-Log "Bundled Python installer finished (exit $($p.ExitCode)) but python is still not on PATH" "ERR"
            exit 1
        }
    }
}
if (-not $pythonCmd) {
    Write-Log "Python 3.10+ not found on PATH and no bundled installer in payload\redist" "ERR"
    exit 1
}
Write-Log ("Python: " + (& python --version 2>&1)) "OK"

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
    # Skip venv if it already exists - will be validated/reused below
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
cmd /c "icacls `"$dataDir`" /grant Users:(OI)(CI)M /T" | Out-Null
Get-ChildItem $dataDir -Filter "app.db*" -ErrorAction SilentlyContinue | ForEach-Object {
    cmd /c "icacls `"$($_.FullName)`" /grant Users:M" | Out-Null
}
@{ mode = $HostMode; written_utc = [DateTime]::UtcNow.ToString("o") } |
    ConvertTo-Json | Set-Content (Join-Path $dataDir "host-mode.json") -Encoding UTF8
$uninstallSrc = Join-Path $PSScriptRoot "Uninstall.ps1"
if (-not (Test-Path $uninstallSrc)) { $uninstallSrc = Join-Path $payload "Uninstall.ps1" }
if (Test-Path $uninstallSrc) {
    Copy-Item $uninstallSrc (Join-Path $InstallDir "Uninstall.ps1") -Force
}
Write-Log "Files copied (host-mode=$HostMode)" "OK"

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
        $vcInstaller = Get-ChildItem (Join-Path $payload "redist") -Filter "vc_redist*.exe" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        $vcPath = $null
        if ($vcInstaller) {
            $vcPath = $vcInstaller.FullName
            Write-Log "Using bundled $($vcInstaller.Name)"
        } else {
            $vcPath = Join-Path $env:TEMP "vc_redist.x64.exe"
            Invoke-WebRequest -Uri "https://aka.ms/vs/17/release/vc_redist.x64.exe" -OutFile $vcPath -UseBasicParsing
        }
        Start-Process $vcPath -ArgumentList "/install /quiet /norestart" -Wait
        if ($vcPath -like "$env:TEMP*") {
            Remove-Item $vcPath -Force -ErrorAction SilentlyContinue
        }
        Write-Log "VC++ installed" "OK"
    }
}

# 4) venv + PyTorch - never delete a venv if torch already works
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
    # Native stderr must not trip $ErrorActionPreference Stop (Windows PowerShell 5.1)
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & python -m venv $venvPath
        $venvCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prevEap
    }
    if ($venvCode -ne 0) { Write-Log "Failed to create venv" "ERR"; exit 1 }
    $pythonExe = Join-Path $venvPath "Scripts\python.exe"
    Write-Log "venv created" "OK"
} else {
    Write-Log "Reusing existing venv" "OK"
}

if (-not $SkipPyTorch) {
    $offline = Resolve-OfflineWheelsDir
    if ($torchOk) {
        Write-Log "PyTorch already present - skipping torch download" "OK"
    } else {
        if ($offline) {
            Write-Log "Upgrading pip from bundled wheels"
            $null = Invoke-VenvPip -PythonExe $pythonExe -PipArgs @("install", "--no-index", "--find-links", $offline, "pip", "wheel", "setuptools")
        } else {
            $null = Invoke-VenvPip -PythonExe $pythonExe -PipArgs @("install", "--upgrade", "pip")
        }
        Write-Log "Downloading PyTorch cu128 from pytorch.org (~2-3 GB)..."
        $torchCode = Invoke-VenvPip -PythonExe $pythonExe -PipArgs @(
            "install", "torch", "torchvision", "torchaudio",
            "--index-url", "https://download.pytorch.org/whl/cu128"
        )
        if ($torchCode -ne 0) {
            Write-Log "PyTorch install failed" "ERR"
            exit 1
        }
        Write-Log "PyTorch ready" "OK"
    }

    if ($offline) {
        Write-Log "Installing worker Python deps from bundled wheels"
        $null = Invoke-VenvPip -PythonExe $pythonExe -PipArgs @(
            "install", "--no-index", "--find-links", $offline,
            "tokenizers", "numpy", "safetensors", "rich", "typing_extensions", "pyyaml", "pillow", "pydantic", "ninja", "huggingface_hub"
        )
        $null = Invoke-VenvPip -PythonExe $pythonExe -PipArgs @("install", "--no-index", "--find-links", $offline, "triton-windows")
    } else {
        $null = Invoke-VenvPip -PythonExe $pythonExe -PipArgs @("install", "--upgrade", "huggingface_hub", "ninja", "triton-windows")
    }

    try {
        Install-ExLlamaV3CudaWheel $pythonExe
    } catch {
        Write-Log $_.Exception.Message "ERR"
        exit 1
    }

    # Single-quoted -c so PowerShell does not parse commas / () inside the Python snippet
    & $pythonExe -c 'import torch; print(torch.__version__, "cuda=", torch.cuda.is_available())'
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

# 6) Windows Service - New-Service (correct quoting with Program Files)
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
        -StartupType $(if ($HostMode -eq "headless") { "Automatic" } else { "Manual" }) | Out-Null
} catch {
    Write-Log "New-Service failed: $($_.Exception.Message) - trying sc.exe" "WARN"
    $svcStart = if ($HostMode -eq "headless") { "delayed-auto" } else { "demand" }
    $createOut = & sc.exe create $ServiceName binPath= $binPath DisplayName= "ExLlamaSharp LLM Server" start= $svcStart 2>&1
    Write-Log ("sc.exe: " + ($createOut | Out-String).Trim())
    if ($LASTEXITCODE -ne 0 -and -not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
        Write-Log "Failed to create service" "ERR"
        exit 1
    }
}
& sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
if ($HostMode -eq "headless") {
    & sc.exe config $ServiceName start= delayed-auto | Out-Null
} else {
    & sc.exe config $ServiceName start= demand | Out-Null
}
# Allow interactive users to Start/Stop/Query so the Tray can reset without UAC
$svcSddl = 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;IU)(A;;CCLCSWRPWPDTLOCRRC;;;AU)(A;;CCLCSWLOCRRC;;;SU)'
& sc.exe sdset $ServiceName $svcSddl | Out-Null
Write-Log "Service registered" "OK"

# 7) Firewall — only if the API is intended to leave loopback (operator can add later)
Write-Log "Firewall skipped (default bind is 127.0.0.1)" "OK"
Get-NetFirewallRule -DisplayName "ExLlamaSharp Server" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue

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

$startMenu = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\ExLlamaSharp"
New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
Get-ChildItem $startMenu -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

$trayExe = Join-Path $InstallDir "ExLlamaSharp.Tray.exe"
if (Test-Path $trayExe) {
    # Primary shortcuts: one click starts Tray + server
    New-AppShortcut (Join-Path $desktop "ExLlamaSharp.lnk") $trayExe "" $iconFile $InstallDir
    New-AppShortcut (Join-Path $startMenu "ExLlamaSharp.lnk") $trayExe "" $iconFile $InstallDir
    New-AppShortcut (Join-Path $startMenu "ExLlamaSharp Tray.lnk") $trayExe "" $iconFile $InstallDir
    New-AppShortcut (Join-Path $startMenu "Open Admin UI.lnk") `
        "$env:SystemRoot\System32\cmd.exe" "/C start $UiUrl" $iconFile $InstallDir
    Get-ChildItem @(
        (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\StartUp\ExLlamaSharp Tray.lnk"),
        (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Startup\ExLlamaSharp Tray.lnk")
    ) -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    try {
        Unregister-ScheduledTask -TaskName "ExLlamaSharpUserSession" -Confirm:$false -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName "ExLlamaSharpTrayLogon" -Confirm:$false -ErrorAction SilentlyContinue
        if ($HostMode -eq "desktop") {
            $action = New-ScheduledTaskAction -Execute $trayExe -WorkingDirectory $InstallDir
            $trigger = New-ScheduledTaskTrigger -AtLogOn
            $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
            Register-ScheduledTask -TaskName "ExLlamaSharpTrayLogon" -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
            Write-Log "Logon task ExLlamaSharpTrayLogon registered" "OK"
        }
    } catch {
        Write-Log "Scheduled task: $($_.Exception.Message)" "WARN"
    }
} else {
    New-InternetShortcut (Join-Path $desktop "ExLlamaSharp.url") $UiUrl $iconFile
    New-AppShortcut (Join-Path $startMenu "ExLlamaSharp.lnk") `
        "$env:SystemRoot\System32\cmd.exe" "/C start $UiUrl" $iconFile $InstallDir
}
Write-Log "Shortcuts OK" "OK"

# 9) Start host
Write-Log "Starting ($HostMode)" "STEP"
Clear-Port $Port
Stop-ExLlamaProcesses
if ($HostMode -eq "headless") {
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 4
    $service = Get-Service -Name $ServiceName
    if ($service.Status -ne "Running") {
        Write-Log "Service did not reach Running ($($service.Status))" "WARN"
    }
} else {
    Write-Log "Desktop mode: service is Manual; Tray starts Server in the user session" "OK"
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
    Write-Log ('Health OK ({0}/health)' -f $UiUrl) "OK"
} else {
    Write-Log "Health did not respond - see Event Viewer / $LogFile" "WARN"
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
