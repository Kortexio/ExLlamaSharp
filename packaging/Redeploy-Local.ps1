#Requires -Version 5.1
<#
.SYNOPSIS
  Publish self-contained Server+Tray and copy into an existing Program Files install.

.DESCRIPTION
  Never copies framework-dependent bin/ over a self-contained install (that breaks hostfxr).
  Does not start the Windows service. Desktop mode: Tray / logon task start Server in the user session.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging\Redeploy-Local.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\ExLlamaSharp",
    [string]$DataRoot = "$env:ProgramData\ExLlamaSharp",
    [ValidateSet("desktop", "headless")]
    [string]$HostMode = "desktop",
    [switch]$SkipPublish,
    [switch]$SkipStart
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$PubServer = Join-Path $Root "artifacts\publish-local"
$PubTray = Join-Path $Root "artifacts\publish-tray-local"

function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
    [System.Environment]::GetEnvironmentVariable("Path", "User")

if (-not $SkipPublish) {
    Write-Step "dotnet publish Server (win-x64 self-contained)"
    & dotnet publish (Join-Path $Root "src\ExLlamaSharp.Server\ExLlamaSharp.Server.csproj") `
        -c Release -r win-x64 --self-contained true -o $PubServer /p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw "Server publish failed" }

    $runtime = Join-Path $PubServer "ExLlamaSharp.Server.runtimeconfig.json"
    $txt = Get-Content $runtime -Raw
    if ($txt -notmatch "includedFrameworks") {
        throw "Publish is framework-dependent. Refuse to copy over Program Files."
    }

    Write-Step "dotnet publish Tray (win-x64 self-contained)"
    & dotnet publish (Join-Path $Root "src\ExLlamaSharp.Tray\ExLlamaSharp.Tray.csproj") `
        -c Release -r win-x64 --self-contained true -o $PubTray
    if ($LASTEXITCODE -ne 0) { throw "Tray publish failed" }
}

New-Item -ItemType Directory -Force -Path "C:\Temp" | Out-Null
$elevPath = "C:\Temp\exllama-redeploy-local-elev.ps1"
$elevLines = @(
    '$ErrorActionPreference = "Continue"'
    '$log = "C:\Temp\exllama-redeploy-local.txt"'
    'function Log($m) { "$(Get-Date -Format o) $m" | Tee-Object -FilePath $log -Append }'
    'Log "BEGIN"'
    'Get-Process ExLlamaSharp.Server, ExLlamaSharp.Tray -ErrorAction SilentlyContinue | ForEach-Object {'
    '    Log "KILL $($_.ProcessName) $($_.Id)"'
    '    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue'
    '}'
    'Get-CimInstance Win32_Process -Filter "Name=''python.exe'' OR Name=''pythonw.exe''" | ForEach-Object {'
    '    if ($_.CommandLine -and ($_.CommandLine -match "exl3_worker|ExLlamaSharp|exllamav3")) {'
    '        Log "KILL python $($_.ProcessId)"'
    '        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue'
    '    }'
    '}'
    'sc.exe stop ExLlamaSharp | Out-Null'
    'sc.exe config ExLlamaSharp start= demand | Out-Null'
    'Start-Sleep 2'
    'Get-Process ExLlamaSharp.Server -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue'
    'Start-Sleep 1'
    '$pub = "' + $PubServer + '"'
    '$dest = "' + $InstallDir + '"'
    'if (-not (Test-Path $pub)) { throw "Publish folder missing: $pub" }'
    'Log "COPY $pub -> $dest"'
    '& robocopy $pub $dest /E /IS /IT /R:2 /W:1 /NFL /NDL /NJH /NJS /XF web.config /XD venv'
    'if ($LASTEXITCODE -ge 8) { throw "robocopy failed $LASTEXITCODE" }'
    '$tray = Join-Path "' + $PubTray + '" "ExLlamaSharp.Tray.exe"'
    'if (Test-Path $tray) { Copy-Item $tray (Join-Path $dest "ExLlamaSharp.Tray.exe") -Force; Log "copied Tray.exe" }'
    '$workerSrc = Join-Path "' + $Root + '" "tools\exl3_worker\worker.py"'
    '$workerDstDir = Join-Path $dest "tools\exl3_worker"'
    'if (Test-Path $workerSrc) {'
    '    New-Item -ItemType Directory -Force -Path $workerDstDir | Out-Null'
    '    Copy-Item $workerSrc (Join-Path $workerDstDir "worker.py") -Force'
    '    Log "copied worker.py"'
    '}'
    '$data = "' + $DataRoot + '"'
    'New-Item -ItemType Directory -Force -Path $data | Out-Null'
    'Set-Content -Path (Join-Path $data "host-mode.json") -Value ''{"mode":"' + $HostMode + '"}'' -Encoding UTF8'
    'Log "host-mode=' + $HostMode + '"'
    'Log "OK"'
)
Set-Content -Path $elevPath -Value $elevLines -Encoding UTF8
Write-Step "Elevated copy to $InstallDir (UAC)"
Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',$elevPath -Wait
if (Test-Path "C:\Temp\exllama-redeploy-local.txt") {
    Get-Content "C:\Temp\exllama-redeploy-local.txt"
}

if ($SkipStart) {
    Write-Host "SkipStart: service left stopped; start Tray yourself." -ForegroundColor Yellow
    return
}

if ($HostMode -eq "headless") {
    Write-Step "Headless: not starting LocalSystem service (use GPU account)"
    return
}

Write-Step "Starting Tray / user-session Server (not the Windows service)"
$started = $false
foreach ($tn in @("ExLlamaSharpTrayLogon", "ExLlamaSharpUserSession", "ExLlamaSharpTrayOnce")) {
    $task = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
    if ($task) {
        Start-ScheduledTask -TaskName $tn
        $started = $true
        Write-Host "Started task $tn"
        break
    }
}
if (-not $started) {
    $trayExe = Join-Path $InstallDir "ExLlamaSharp.Tray.exe"
    if (Test-Path $trayExe) {
        Start-Process $trayExe -WorkingDirectory $InstallDir
    }
}

Write-Host "Redeploy-Local done. Service stays Manual/Stopped. Health: http://127.0.0.1:14563/health" -ForegroundColor Green
