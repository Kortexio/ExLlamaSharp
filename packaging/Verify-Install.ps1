#Requires -Version 5.1
<#
.SYNOPSIS
  Verify an installed ExLlamaSharp trio (self-contained Server, one process, ACL, listen.json).
  Desktop mode fails if the Server is in Session 0. isLoaded=false is a warning, not a failure.
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\ExLlamaSharp",
    [string]$DataRoot = "$env:ProgramData\ExLlamaSharp"
)

$ErrorActionPreference = "Continue"
$failed = 0
function Ok($m) { Write-Host "OK   $m" -ForegroundColor Green }
function Fail($m) { Write-Host "FAIL $m" -ForegroundColor Red; $script:failed++ }
function Warn($m) { Write-Host "WARN $m" -ForegroundColor Yellow }

$runtime = Join-Path $InstallDir "ExLlamaSharp.Server.runtimeconfig.json"
if (Test-Path $runtime) {
    $txt = Get-Content $runtime -Raw
    if ($txt -match "includedFrameworks") { Ok "self-contained runtimeconfig" }
    else { Fail "runtimeconfig is framework-dependent (copying bin/ over Program Files breaks the host)" }
} else { Fail "missing $runtime" }

$mode = "desktop"
$hostModePath = Join-Path $DataRoot "host-mode.json"
if (Test-Path $hostModePath) {
    Ok "host-mode.json exists"
    try {
        $hm = Get-Content $hostModePath -Raw | ConvertFrom-Json
        if ($hm.mode) { $mode = [string]$hm.mode }
    } catch {}
} else { Fail "host-mode.json missing" }

$servers = @(Get-CimInstance Win32_Process -Filter "Name='ExLlamaSharp.Server.exe'" -ErrorAction SilentlyContinue)
if ($servers.Count -eq 1) {
    $s = $servers[0]
    Ok "one Server process (PID $($s.ProcessId) session $($s.SessionId))"
    if ($mode -eq "desktop" -and $s.SessionId -eq 0) {
        Fail "desktop host-mode but Server is Session 0. Tray must start it in the user session."
    }
} elseif ($servers.Count -eq 0) {
    Fail "Server is not running"
} else {
    Fail "multiple Server processes ($($servers.Count)) - mutex should prevent this"
}

$svc = Get-Service ExLlamaSharp -ErrorAction SilentlyContinue
if ($mode -eq "desktop") {
    if ($svc -and $svc.Status -eq "Running") {
        Fail "desktop mode: Windows service must be Manual/Stopped (now $($svc.Status))"
    } elseif ($svc) {
        Ok "service $($svc.Status) / $($svc.StartType)"
    }
}

$listen = Join-Path $DataRoot "listen.json"
if (Test-Path $listen) { Ok "listen.json exists" } else { Fail "listen.json missing" }

$db = Join-Path $DataRoot "app.db"
if (Test-Path $db) {
    $acl = icacls $db 2>$null | Out-String
    if ($acl -match "Users") { Ok "app.db has Users ACL" } else { Fail "app.db ACL may be LocalSystem-only" }
} else { Fail "app.db missing" }

$url = "http://127.0.0.1:14563"
try {
    $doc = Get-Content $listen -Raw | ConvertFrom-Json
    if ($doc.url) { $url = $doc.url }
} catch {}

try {
    $h = Invoke-RestMethod "$url/health" -TimeoutSec 5
    if ($h.components.engine.data.isMock -eq $true) {
        Fail "engine is Mock - production requires EXL3 worker"
    } else {
        Ok "health status=$($h.status) mock=$($h.components.engine.data.isMock) hostMode=$($h.components.engine.data.hostMode)"
    }
    if ($h.components.engine.data.sessionZero -eq $true -and $mode -eq "desktop") {
        Fail "health reports sessionZero=true in desktop mode"
    }
    if ($h.components.engine.data.isLoaded -ne $true) {
        Warn "no model loaded - Load once on Models so LastLoadedModelId persists across restart"
    } else {
        Ok "model loaded"
    }
} catch {
    Fail "health: $($_.Exception.Message)"
}

if ($failed -eq 0) { Write-Host "Verify-Install passed." -ForegroundColor Green; exit 0 }
Write-Host "Verify-Install failed ($failed)." -ForegroundColor Red
exit 1
