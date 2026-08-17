#Requires -RunAsAdministrator
#Requires -Version 5.1
<#
.SYNOPSIS
  Removes a broken ExLlamaSharp Windows Service / leftover folders that block reinstall.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging\Cleanup-BrokenInstall.ps1
#>
[CmdletBinding()]
param(
    [switch]$AlsoRemoveProgramFiles
)

$ErrorActionPreference = "Continue"
Write-Host "ExLlamaSharp broken-install cleanup" -ForegroundColor Yellow

# Kill server processes
Get-CimInstance Win32_Process -EA SilentlyContinue |
    Where-Object { $_.Name -eq "ExLlamaSharp.Server.exe" -or ($_.CommandLine -and $_.CommandLine -like "*ExLlamaSharp.Server*") } |
    ForEach-Object {
        Write-Host "Stopping PID $($_.ProcessId)"
        Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue
    }

$svc = Get-Service ExLlamaSharp -EA SilentlyContinue
if ($svc) {
    Write-Host "Service status: $($svc.Status)"
    try { Stop-Service ExLlamaSharp -Force -EA Stop } catch { }
    Start-Sleep 2
    sc.exe stop ExLlamaSharp | Out-Null
    Start-Sleep 2
    sc.exe delete ExLlamaSharp
    Start-Sleep 2
    if (Get-Service ExLlamaSharp -EA SilentlyContinue) {
        Write-Warning "Service still present - reboot may be required to clear StartPending"
    }
    else {
        Write-Host "Service deleted." -ForegroundColor Green
    }
}
else {
    Write-Host "No ExLlamaSharp service registered."
}

$dirs = @(
    "${env:ProgramFiles}\ExLlamaSharp",
    "${env:ProgramFiles(x86)}\ExLlamaSharp"
)
foreach ($d in $dirs) {
    if (Test-Path $d) {
        if ($AlsoRemoveProgramFiles) {
            Write-Host "Removing $d"
            Remove-Item $d -Recurse -Force -EA SilentlyContinue
        }
        else {
            Write-Host "Left in place (pass -AlsoRemoveProgramFiles to delete): $d"
        }
    }
}

Write-Host "Done. Rebuild with packaging\Build-Installer.ps1 then run Install.ps1 / Install.bat as Admin." -ForegroundColor Cyan
