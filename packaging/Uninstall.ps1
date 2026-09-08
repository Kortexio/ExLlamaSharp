#Requires -RunAsAdministrator
#Requires -Version 5.1
<#
.SYNOPSIS
  Uninstalls ExLlamaSharp (service, files, shortcuts, tasks, firewall). Does not delete models by default.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "${env:ProgramFiles}\ExLlamaSharp",
    [string]$ServiceName = "ExLlamaSharp",
    [int]$Port = 14563,
    [switch]$RemoveData
)

$ErrorActionPreference = "Continue"

function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

Write-Host "ExLlamaSharp uninstall" -ForegroundColor Yellow

Write-Step "Stopping processes / service"
Get-Process -Name "ExLlamaSharp.Tray","ExLlamaSharp.Server" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq "Running") { Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

Write-Step "Removing scheduled tasks"
foreach ($tn in @("ExLlamaSharpTrayLogon", "ExLlamaSharpUserSession")) {
    Unregister-ScheduledTask -TaskName $tn -Confirm:$false -ErrorAction SilentlyContinue
}

Write-Step "Removing autostart / shortcuts"
try {
    Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "ExLlamaSharpTray" -ErrorAction SilentlyContinue
} catch {}
@(
    (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\ExLlamaSharp"),
    (Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "ExLlamaSharp.lnk"),
    (Join-Path ([Environment]::GetFolderPath("Desktop")) "ExLlamaSharp.lnk"),
    (Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "ExLlamaSharp.url"),
    (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\StartUp\ExLlamaSharp Tray.lnk"),
    (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Startup\ExLlamaSharp Tray.lnk")
) | ForEach-Object {
    if (Test-Path $_) { Remove-Item $_ -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Step "Removing firewall rule"
Get-NetFirewallRule -DisplayName "ExLlamaSharp Server" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
Get-NetFirewallRule -DisplayName "ExLlamaSharp HTTP $Port" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue

Write-Step "Removing $InstallDir"
if (Test-Path $InstallDir) {
    Get-Process -Name "ExLlamaSharp.Server","ExLlamaSharp.Tray" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($RemoveData) {
    $data = Join-Path $env:ProgramData "ExLlamaSharp"
    Write-Step "Removing data $data"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    Write-Host "Kept data under %ProgramData%\ExLlamaSharp (use -RemoveData to wipe models/logs)." -ForegroundColor Cyan
}

Write-Host "Uninstall complete." -ForegroundColor Green
