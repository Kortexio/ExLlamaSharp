#Requires -Version 5.1
<#
.SYNOPSIS
  Hardware proofs: kill Server, kill EXL3 worker, Admin Restart, second exe mutex.
  Reboot is printed as a manual step (not automated).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging\Prove-Resilience.ps1
  powershell -ExecutionPolicy Bypass -File packaging\Prove-Resilience.ps1 -ApiKey sk-...
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\ExLlamaSharp",
    [string]$DataRoot = "$env:ProgramData\ExLlamaSharp",
    [string]$ApiKey = $env:EXLLAMASHARP_API_KEY,
    [int]$RecoverTimeoutSec = 120
)

$ErrorActionPreference = "Continue"
$failed = 0
function Ok($m) { Write-Host "OK   $m" -ForegroundColor Green }
function Fail($m) { Write-Host "FAIL $m" -ForegroundColor Red; $script:failed++ }
function Info($m) { Write-Host "     $m" -ForegroundColor Gray }

function Get-ListenUrl {
    $url = "http://127.0.0.1:14563"
    $listen = Join-Path $DataRoot "listen.json"
    if (Test-Path $listen) {
        try {
            $doc = Get-Content $listen -Raw | ConvertFrom-Json
            if ($doc.url) { $url = $doc.url }
        } catch {}
    }
    return $url.TrimEnd("/")
}

function Wait-Health([int]$Seconds) {
    $url = Get-ListenUrl
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $raw = curl.exe -s --max-time 3 "$url/health"
            if ($raw) { return ($raw | ConvertFrom-Json) }
        } catch {}
        Start-Sleep -Seconds 2
    }
    return $null
}

function Get-ServerProcess {
    return Get-CimInstance Win32_Process -Filter "Name='ExLlamaSharp.Server.exe'" -ErrorAction SilentlyContinue
}

function Get-WorkerProcess {
    return Get-CimInstance Win32_Process -Filter "Name='python.exe' OR Name='pythonw.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and ($_.CommandLine -match 'exl3_worker|exllamav3') }
}

Write-Host "ExLlamaSharp resilience proofs" -ForegroundColor Cyan
$svc = Get-Service ExLlamaSharp -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq "Running") {
    Fail "Windows service ExLlamaSharp is Running - desktop mode must keep it Manual/Stopped"
} else {
    Ok "service not running ($($svc.Status) / $($svc.StartType))"
}

$before = Get-ServerProcess | Select-Object -First 1
if (-not $before) { Fail "Server is not running - start Tray first"; exit 1 }
if ($before.SessionId -eq 0) { Fail "Server is in Session 0 (LocalSystem) - desktop proofs require the user session" }
else { Ok "Server PID $($before.ProcessId) session $($before.SessionId)" }

Write-Host "`n==> Kill Server (Tray recover)" -ForegroundColor Cyan
Stop-Process -Id $before.ProcessId -Force -ErrorAction SilentlyContinue
Start-Sleep 2
$health = Wait-Health $RecoverTimeoutSec
$after = Get-ServerProcess | Select-Object -First 1
if (-not $health -or -not $after) {
    Fail "Tray did not relaunch Server within ${RecoverTimeoutSec}s"
} elseif ($after.SessionId -eq 0) {
    Fail "Recovered Server is Session 0"
} else {
    Ok "Server recovered PID $($after.ProcessId) session $($after.SessionId) health=$($health.status)"
}
$svc = Get-Service ExLlamaSharp -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq "Running") { Fail "Service started itself after Server kill" }
else { Ok "service still stopped after recover" }

Write-Host "`n==> Kill EXL3 worker (watchdog)" -ForegroundColor Cyan
$workers = @(Get-WorkerProcess)
if ($workers.Count -eq 0) {
    Info "No worker process (model not loaded). Load a model, then re-run this proof."
} else {
    $workers | ForEach-Object {
        Info "KILL python $($_.ProcessId)"
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep 4
    $health = Wait-Health 90
    if (-not $health) { Fail "health gone after worker kill" }
    elseif ($health.components.engine.data.isMock -eq $true) { Fail "engine fell back to Mock after worker kill" }
    else {
        Ok "engine still worker mock=$($health.components.engine.data.isMock) loaded=$($health.components.engine.data.isLoaded) lastError=$($health.components.engine.data.lastError)"
    }
}

Write-Host "`n==> Admin Restart (restart.request / Tray)" -ForegroundColor Cyan
$url = Get-ListenUrl
$restarted = $false
if ($ApiKey) {
    $code = curl.exe -s -o C:\Temp\exllama-restart.json -w "%{http_code}" -X POST "$url/api/v1/restart" -H "Authorization: Bearer $ApiKey"
    if ($code -eq "200") {
        $restarted = $true
        Ok "POST /api/v1/restart accepted"
    } else {
        Info "POST restart HTTP $code - writing restart.request"
    }
}
if (-not $restarted) {
    $req = Join-Path $DataRoot "restart.request"
    Set-Content $req (Get-Date -Format o) -Encoding UTF8
    Get-Process ExLlamaSharp.Server -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Ok "wrote restart.request and stopped Server (same Tray path as Admin Restart)"
}
$health = Wait-Health $RecoverTimeoutSec
$after = Get-ServerProcess | Select-Object -First 1
if (-not $health -or -not $after) { Fail "Tray did not relaunch after Restart" }
elseif ($after.SessionId -eq 0) { Fail "Restarted Server is Session 0" }
else { Ok "Restart recovered PID $($after.ProcessId) session $($after.SessionId) health=$($health.status)" }

Write-Host "`n==> Second Server.exe (mutex)" -ForegroundColor Cyan
$exe = Join-Path $InstallDir "ExLlamaSharp.Server.exe"
if (-not (Test-Path $exe)) { Fail "missing $exe" }
else {
    $beforeCount = @(Get-Process ExLlamaSharp.Server -ErrorAction SilentlyContinue).Count
    $p = Start-Process -FilePath $exe -WorkingDirectory $InstallDir -PassThru -WindowStyle Hidden -RedirectStandardError "C:\Temp\exllama-second-exe.err"
    Start-Sleep 3
    $alive = $false
    try { $alive = $p -and -not $p.HasExited } catch { $alive = $false }
    $afterCount = @(Get-Process ExLlamaSharp.Server -ErrorAction SilentlyContinue).Count
    $err = ""
    if (Test-Path "C:\Temp\exllama-second-exe.err") { $err = Get-Content "C:\Temp\exllama-second-exe.err" -Raw }
    if ($alive) {
        Fail "second exe stayed alive (PID $($p.Id))"
        try { Stop-Process -Id $p.Id -Force } catch {}
    } elseif ($afterCount -ne $beforeCount) {
        Fail "process count changed $beforeCount -> $afterCount"
    } elseif ($err -match "already running|Global\\ExLlamaSharp.Server") {
        Ok "second exe exited; mutex message present"
    } else {
        Ok "second exe exited immediately (count=$afterCount)"
        if ($err) { Info $err.Trim() }
    }
}

Write-Host "`n==> Reboot (manual)" -ForegroundColor Cyan
Write-Host "After reboot + logon:" -ForegroundColor Yellow
Write-Host "  1. ExLlamaSharpTrayLogon starts Tray + Server in the user session (SessionId -ne 0)"
Write-Host "  2. Windows service ExLlamaSharp stays Stopped"
Write-Host "  3. Last loaded model comes back if LoadModelOnStartup is on"
Write-Host "  4. packaging\Verify-Install.ps1 passes"

if ($failed -eq 0) {
    Write-Host "`nProve-Resilience passed (reboot not executed)." -ForegroundColor Green
    exit 0
}
Write-Host "`nProve-Resilience failed ($failed)." -ForegroundColor Red
exit 1
