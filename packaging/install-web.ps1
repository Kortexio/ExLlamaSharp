#Requires -Version 5.1
<#
.SYNOPSIS
  Bootstrap ExLlamaSharp (Ollama-style one-liner).

.DESCRIPTION
  Downloads the latest Windows Setup.exe and launches it (UAC).

  Usage (from an elevated OR normal PowerShell — Setup requests Admin):
    irm https://YOUR_HOST/install.ps1 | iex

  Or point at a specific release:
    $env:EXLLAMASHARP_SETUP_URL = "https://github.com/vitorcastro78/ExLlamaSharp/releases/download/v1.0.0/ExLlamaSharp-Setup-win-x64.exe"
    irm https://YOUR_HOST/install.ps1 | iex

  Local test (no network):
    powershell -File packaging\install-web.ps1 -SetupExePath .\publish\ExLlamaSharp-Setup-win-x64.exe
#>
[CmdletBinding()]
param(
    # Direct URL to ExLlamaSharp-Setup-win-x64.exe (GitHub Release / CDN / file share)
    [string]$SetupUrl = $env:EXLLAMASHARP_SETUP_URL,

    # Local path (skips download) — for testing
    [string]$SetupExePath,

    [switch]$Silent
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Default release URL — replace ORG/REPO when you publish to GitHub Releases
if ([string]::IsNullOrWhiteSpace($SetupUrl)) {
    $SetupUrl = "https://github.com/vitorcastro78/ExLlamaSharp/releases/latest/download/ExLlamaSharp-Setup-win-x64.exe"
}

Write-Host ""
Write-Host "ExLlamaSharp installer" -ForegroundColor Cyan
Write-Host ""

$dest = Join-Path $env:TEMP "ExLlamaSharp-Setup-win-x64.exe"

if ($SetupExePath -and (Test-Path $SetupExePath)) {
    Write-Host "Using local setup: $SetupExePath" -ForegroundColor Gray
    Copy-Item $SetupExePath $dest -Force
}
else {
    Write-Host "Downloading Setup.exe..." -ForegroundColor Yellow
    Write-Host "  $SetupUrl" -ForegroundColor DarkGray
    try {
        Invoke-WebRequest -Uri $SetupUrl -OutFile $dest -UseBasicParsing
    }
    catch {
        Write-Host ""
        Write-Host "Download failed. Publish the EXE to GitHub Releases (or a CDN) and set:" -ForegroundColor Red
        Write-Host '  $env:EXLLAMASHARP_SETUP_URL = "https://.../ExLlamaSharp-Setup-win-x64.exe"' -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Or install from a local build:" -ForegroundColor Gray
        Write-Host "  .\publish\ExLlamaSharp-Setup-win-x64.exe" -ForegroundColor White
        throw
    }
}

$sizeMb = [math]::Round((Get-Item $dest).Length / 1MB, 1)
Write-Host "OK  $dest ($sizeMb MB)" -ForegroundColor Green
Write-Host ""
Write-Host "Launching Setup (UAC / Admin)..." -ForegroundColor Cyan

$args = @()
if ($Silent) { $args = @("/VERYSILENT", "/NORESTART", "/SUPPRESSMSGBOXES") }

# Inno Setup EXE — always elevate via ShellExecute
Start-Process -FilePath $dest -ArgumentList $args -Verb RunAs -Wait

Write-Host ""
Write-Host "If install succeeded, open: http://127.0.0.1:14563" -ForegroundColor Green
Write-Host ""
