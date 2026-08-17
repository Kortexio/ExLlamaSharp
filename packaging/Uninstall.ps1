#Requires -RunAsAdministrator
#Requires -Version 5.1
<#
.SYNOPSIS
  Uninstalls ExLlamaSharp (service, files, shortcuts, firewall). Does not delete models by default.
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

Write-Step "Stopping / removing service"
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq "Running") { Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

Write-Step "Removing Start Menu / Desktop shortcuts"
$startMenu = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\ExLlamaSharp"
if (Test-Path $startMenu) { Remove-Item $startMenu -Recurse -Force -ErrorAction SilentlyContinue }
$desk = Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "ExLlamaSharp.url"
if (Test-Path $desk) { Remove-Item $desk -Force -ErrorAction SilentlyContinue }

Write-Step "Removing firewall rule"
Get-NetFirewallRule -DisplayName "ExLlamaSharp Server" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
Get-NetFirewallRule -DisplayName "ExLlamaSharp HTTP $Port" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
Get-Process -Name "ExLlamaSharp.Tray" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Step "Removing $InstallDir"
if (Test-Path $InstallDir) {
    # ensure exe unlocked
    Get-Process -Name "ExLlamaSharp.Server" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
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
