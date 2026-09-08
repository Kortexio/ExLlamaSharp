#Requires -Version 5.1
<#
.SYNOPSIS
  Verify an installed ExLlamaSharp trio (self-contained Server, one process, ACL, listen.json).
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\ExLlamaSharp",
    [string]$DataRoot = "$env:ProgramData\ExLlamaSharp"
)

$ErrorActionPreference = "Continue"
$failed = 0
function Ok($m) { Write-Host "OK   $m" -ForegroundColor Green }
function Fail($m) { Write-Host "FAIL $m" -ForegroundColor Red; $script:failed++ }

$runtime = Join-Path $InstallDir "ExLlamaSharp.Server.runtimeconfig.json"
if (Test-Path $runtime) {
    $txt = Get-Content $runtime -Raw
    if ($txt -match "includedFrameworks") { Ok "self-contained runtimeconfig" }
    else { Fail "runtimeconfig is framework-dependent (copying bin/ over Program Files breaks the host)" }
} else { Fail "missing $runtime" }

$servers = @(Get-Process -Name "ExLlamaSharp.Server" -ErrorAction SilentlyContinue)
if ($servers.Count -eq 1) { Ok "one Server process (PID $($servers[0].Id))" }
elseif ($servers.Count -eq 0) { Fail "Server is not running" }
else { Fail "multiple Server processes ($($servers.Count)) — mutex should prevent this" }

$listen = Join-Path $DataRoot "listen.json"
if (Test-Path $listen) { Ok "listen.json exists" } else { Fail "listen.json missing" }

$hostMode = Join-Path $DataRoot "host-mode.json"
if (Test-Path $hostMode) { Ok "host-mode.json exists" } else { Fail "host-mode.json missing" }

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
    if ($h.components.engine.data.isMock -eq $true) { Fail "engine is Mock — production requires EXL3 worker" }
    else { Ok "health status=$($h.status) mock=$($h.components.engine.data.isMock)" }
} catch {
    Fail "health: $($_.Exception.Message)"
}

if ($failed -eq 0) { Write-Host "Verify-Install passed." -ForegroundColor Green; exit 0 }
Write-Host "Verify-Install failed ($failed)." -ForegroundColor Red
exit 1
