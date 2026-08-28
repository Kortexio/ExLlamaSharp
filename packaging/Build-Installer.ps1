#Requires -Version 5.1
<#
.SYNOPSIS
  Builds a distributable ExLlamaSharp installer package (publish + stage + zip).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging\Build-Installer.ps1
  powershell -ExecutionPolicy Bypass -File packaging\Build-Installer.ps1 -SkipBundleWheels
#>
[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [switch]$SelfContained = $true,

    [switch]$SkipNative,

    [switch]$SkipZip,

    [switch]$SkipExe,

    # Default: embed ExLlamaV3 CUDA wheel + worker deps + Python/VC. PyTorch downloads at install.
    # Pass -SkipBundleWheels only for a slim app-only Setup.exe.
    [switch]$SkipBundleWheels,

    # Kept for compatibility; bundling is now the default.
    [switch]$BundlePytorch,

    [ValidateSet("cu128", "cu126", "cu124")]
    [string]$CudaIndex = "cu128",

    [ValidateSet("312", "311", "313")]
    [string]$PythonTag = "312"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Stage = Join-Path $Root "publish\installer"
$Payload = Join-Path $Stage "payload"
$OutZip = Join-Path $Root "publish\ExLlamaSharp-Setup-win-x64.zip"

function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

Write-Host "ExLlamaSharp installer build" -ForegroundColor Green
Write-Host "Root: $Root"

# --- native DLL (CUDA preferred, stub fallback) ---
if (-not $SkipNative) {
    Write-Step "Building native CUDA DLL (falls back to stub if needed)"
    $cudaScript = Join-Path $PSScriptRoot "build-native-cuda.ps1"
    $stubScript = Join-Path $PSScriptRoot "build-native-stub.ps1"
    $builtCuda = $false
    if (Test-Path $cudaScript) {
        try {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $cudaScript
            if ($LASTEXITCODE -eq 0) { $builtCuda = $true }
        }
        catch {
            Write-Warning "CUDA native build failed: $_"
        }
    }
    if (-not $builtCuda -and (Test-Path $stubScript)) {
        Write-Step "Building native stub DLL (no CUDA toolkit required)"
        & powershell -NoProfile -ExecutionPolicy Bypass -File $stubScript
    }
}

# --- clean stage ---
Write-Step "Staging $Payload"
if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Payload | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $Stage "scripts") | Out-Null

# --- publish server ---
Write-Step "dotnet publish ExLlamaSharp.Server ($Configuration, win-x64, self-contained=$SelfContained)"
$publishArgs = @(
    "publish", (Join-Path $Root "src\ExLlamaSharp.Server\ExLlamaSharp.Server.csproj"),
    "-c", $Configuration,
    "-r", "win-x64",
    "-o", $Payload,
    "/p:PublishSingleFile=false",
    "/p:IncludeNativeLibrariesForSelfExtract=true"
)
if ($SelfContained) {
    $publishArgs += @("--self-contained", "true")
}
else {
    $publishArgs += @("--self-contained", "false")
}

Push-Location $Root
try {
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}

# --- tray companion (system notification area) ---
Write-Step "dotnet publish ExLlamaSharp.Tray (.NET 9 single-file)"
$trayProj = Join-Path $Root "src\ExLlamaSharp.Tray\ExLlamaSharp.Tray.csproj"
$trayOut = Join-Path $env:TEMP "exllamasharp-tray-publish"
if (Test-Path $trayOut) { Remove-Item $trayOut -Recurse -Force }

# Publish como .NET 9 single-file self-contained
& dotnet publish $trayProj -c $Configuration -r win-x64 -o $trayOut
if ($LASTEXITCODE -ne 0) { throw "Tray publish failed ($LASTEXITCODE)" }

# Copy single-file EXE
$trayExe = Join-Path $trayOut "ExLlamaSharp.Tray.exe"
if (Test-Path $trayExe) {
    Copy-Item $trayExe (Join-Path $Payload "ExLlamaSharp.Tray.exe") -Force
    $sizeMB = [math]::Round((Get-Item $trayExe).Length / 1MB, 1)
    Write-Step "Included ExLlamaSharp.Tray.exe (.NET 9 single-file, $sizeMB MB)"
} else {
    throw "Tray exe not found after publish"
}

# --- copy native DLL if present (MUST be exllamasharp_native.dll - not ExLlamaSharp.dll) ---
# Windows FS is case-insensitive; naming the native lib "exllamasharp.dll" overwrites managed ExLlamaSharp.dll.
$dllCandidates = @(
    (Join-Path $Root "src\ExLlamaSharp\runtimes\win-x64\native\exllamasharp_native.dll"),
    (Join-Path $Root "src\ExLlamaSharp\runtimes\win-x64\native\exllamasharp.dll"),
    (Join-Path $Root "native\exllamasharp\build-cuda\bin\Release\exllamasharp.dll"),
    (Join-Path $Root "native\exllamasharp\build-stub\bin\Release\exllamasharp.dll"),
    (Join-Path $Root "native\exllamasharp\build\bin\Release\exllamasharp.dll")
)
foreach ($dll in $dllCandidates) {
    if (Test-Path $dll) {
        $destNative = Join-Path $Payload "exllamasharp_native.dll"
        Copy-Item $dll $destNative -Force
        Write-Step "Included native: $dll -> exllamasharp_native.dll"
        # Remove accidental case-collision overwrite of managed assembly
        $managed = Join-Path $Payload "ExLlamaSharp.dll"
        if ((Test-Path $managed) -and ((Get-Item $managed).Length -lt 50000 -or -not ([System.Reflection.AssemblyName]::GetAssemblyName($managed)))) {
            Write-Warning "Managed ExLlamaSharp.dll looks corrupt after native copy - republish managed DLL"
        }
        break
    }
}

# Never delete "exllamasharp.dll" by name — on Windows that removes managed ExLlamaSharp.dll too.
# Native must always be published as exllamasharp_native.dll only.
if (-not (Test-Path (Join-Path $Payload "ExLlamaSharp.dll"))) {
    throw "Managed ExLlamaSharp.dll missing from payload after publish."
}

# --- installer scripts into package ---
$scripts = @(
    "Check-Requirements.ps1",
    "Uninstall.ps1",
    "Setup-Exl3Python.ps1",
    "Repair-Exl3Ext.ps1",
    "Download-DemoModel.ps1",
    "Cleanup-BrokenInstall.ps1"
)
$stageScripts = Join-Path $Stage "scripts"
$payloadScripts = Join-Path $Payload "scripts"
New-Item -ItemType Directory -Force -Path $stageScripts | Out-Null
New-Item -ItemType Directory -Force -Path $payloadScripts | Out-Null
foreach ($s in $scripts) {
    $src = Join-Path $PSScriptRoot $s
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $stageScripts $s) -Force
        Copy-Item $src (Join-Path $payloadScripts $s) -Force
    }
}
$gpuBat = Join-Path $PSScriptRoot "Setup-Exl3Python.bat"
if (Test-Path $gpuBat) {
    Copy-Item $gpuBat (Join-Path $Payload "Setup-Exl3Python.bat") -Force
    Copy-Item $gpuBat (Join-Path $Stage "Setup-Exl3Python.bat") -Force
}

# EXL3 Python worker (real CUDA inference path)
$workerSrc = Join-Path $Root "tools\exl3_worker"
$workerDst = Join-Path $Payload "tools\exl3_worker"
if (Test-Path $workerSrc) {
    New-Item -ItemType Directory -Force -Path $workerDst | Out-Null
    Copy-Item (Join-Path $workerSrc "*") $workerDst -Recurse -Force
    Write-Step "Included tools/exl3_worker"
}

# Bundle ExLlamaV3 CUDA wheel + worker deps + Python/VC. PyTorch is downloaded at install time.
$bundleWheels = -not $SkipBundleWheels
$offlineSrc = Join-Path $PSScriptRoot "offline-wheels"
$redistSrc = Join-Path $PSScriptRoot "redist"
if ($bundleWheels) {
    Write-Step "Bundling ExLlamaV3 CUDA wheel, worker deps, Python installer, VC++ (PyTorch stays a download)"
    $dl = Join-Path $PSScriptRoot "Download-OfflineWheels.ps1"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $dl -OutDir $offlineSrc -CudaIndex $CudaIndex -PythonTag $PythonTag
    if ($LASTEXITCODE -ne 0) { throw "Download-OfflineWheels.ps1 failed" }
    $hasExt = @(Get-ChildItem $offlineSrc -Filter "exllamav3-*.whl" -File -EA SilentlyContinue |
        Where-Object { $_.Name -notmatch "py3-none-any" }).Count
    if ($hasExt -lt 1) {
        throw "offline-wheels is incomplete (need prebuilt exllamav3 CUDA wheel)"
    }
    $payloadOffline = Join-Path $Payload "offline-wheels"
    if (Test-Path $payloadOffline) { Remove-Item $payloadOffline -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $payloadOffline | Out-Null
    Get-ChildItem $offlineSrc -File | Where-Object {
        $_.Name -notmatch '^(torch-|torchvision-|torchaudio-)'
    } | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $payloadOffline $_.Name) -Force
    }
    $copied = @(Get-ChildItem $payloadOffline -File)
    $copiedGb = [math]::Round((($copied | Measure-Object Length -Sum).Sum) / 1GB, 2)
    Write-Step ("Included offline-wheels without PyTorch ({0} files, {1} GB)" -f $copied.Count, $copiedGb)

    if (Test-Path $redistSrc) {
        $payloadRedist = Join-Path $Payload "redist"
        if (Test-Path $payloadRedist) { Remove-Item $payloadRedist -Recurse -Force }
        Copy-Item $redistSrc $payloadRedist -Recurse -Force
        Write-Step "Included redist (Python installer + VC++)"
    }
}

Write-Step "Copying installers"
$iconSrc = Join-Path $PSScriptRoot "assets\exllamasharp.ico"
if (Test-Path $iconSrc) {
    Copy-Item $iconSrc (Join-Path $Payload "exllamasharp.ico") -Force
    Copy-Item $iconSrc (Join-Path $Stage "exllamasharp.ico") -Force
    Write-Host "  Included exllamasharp.ico" -ForegroundColor Gray
}

Copy-Item (Join-Path $PSScriptRoot "Install-ExLlamaSharp.ps1") (Join-Path $Stage "Install.ps1") -Force
Copy-Item (Join-Path $PSScriptRoot "Install.bat") (Join-Path $Stage "Install.bat") -Force
Copy-Item (Join-Path $PSScriptRoot "Uninstall.bat") (Join-Path $Stage "Uninstall.bat") -Force -ErrorAction SilentlyContinue
Write-Host "  Included Install.ps1 / Install.bat" -ForegroundColor Gray

# README for the zip
@"
# ExLlamaSharp Setup (Windows x64)

## Install (recommended)

1. Right-click Install.ps1 -> Run with PowerShell (Admin)
   OR right-click Install.bat -> Run as administrator
2. PyTorch CUDA downloads during install (~2-3 GB). ExLlamaV3 .pyd and other deps are already in the package.
3. Open http://localhost:14563

Installed automatically:
- Server + Windows Service
- Python 3.12 (if missing) + VC++ Redistributable (bundled)
- Python venv + PyTorch CUDA 12.8 (downloaded)
- ExLlamaV3 CUDA extension (bundled official wheel)
- Firewall + shortcuts + Tray app

## Options

  Install.ps1 -SkipPyTorch
  Install.ps1 -InstallDir "D:\Apps\ExLlamaSharp"
  Install.ps1 -Unattended

## Uninstall

Run Uninstall.bat as Administrator.

## GPU repair (optional)

Setup-Exl3Python.bat — reinstall PyTorch into Program Files\ExLlamaSharp\venv
"@ | Set-Content -Path (Join-Path $Stage "README.txt") -Encoding UTF8

$version = "1.2.1-beta"
$info = @{
    product = "ExLlamaSharp"
    version = $version
    builtUtc = [DateTime]::UtcNow.ToString("o")
    selfContained = [bool]$SelfContained
    runtime = "win-x64"
}
$info | ConvertTo-Json | Set-Content (Join-Path $Payload "installer-manifest.json") -Encoding UTF8

if (-not $SkipZip) {
    Write-Step "Creating zip $OutZip"
    if (Test-Path $OutZip) { Remove-Item $OutZip -Force }
    Compress-Archive -Path (Join-Path $Stage "*") -DestinationPath $OutZip -Force
    Write-Host ("Package: {0} ({1} MB)" -f $OutZip, [math]::Round((Get-Item $OutZip).Length / 1MB, 1)) -ForegroundColor Green
}

# --- Inno Setup EXE ---
$OutExe = Join-Path $Root "publish\ExLlamaSharp-Setup-win-x64.exe"
if (-not $SkipExe) {
    Write-Step "Building Setup.exe (Inno Setup)"
    $isccCandidates = @(
        (Join-Path $env:LocalAppData "Programs\Inno Setup 6\ISCC.exe")
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )
    $iscc = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) {
        Write-Warning "ISCC.exe not found — ZIP only. Install Inno Setup 6 or pass -SkipExe."
    }
    else {
        Write-Host "  ISCC: $iscc" -ForegroundColor Gray
        $iss = Join-Path $PSScriptRoot "ExLlamaSharp.iss"
        if (-not (Test-Path (Join-Path $Payload "ExLlamaSharp.Server.exe"))) {
            throw "Stage payload missing Server.exe — cannot compile Setup.exe"
        }
        Copy-Item (Join-Path $PSScriptRoot "Install-Clean.bat") (Join-Path $Stage "Install-Clean.bat") -Force -ErrorAction SilentlyContinue

        $p = Start-Process -FilePath $iscc -ArgumentList @($iss) -Wait -PassThru -NoNewWindow
        if ($p.ExitCode -ne 0) { throw "ISCC failed with exit $($p.ExitCode)" }
        if (Test-Path $OutExe) {
            Write-Host ("Setup.exe: {0} ({1} MB)" -f $OutExe, [math]::Round((Get-Item $OutExe).Length / 1MB, 1)) -ForegroundColor Green
        }
        else {
            throw "ISCC succeeded but Setup.exe not found at $OutExe"
        }
    }
}

Write-Host ""
Write-Host "Staged at: $Stage" -ForegroundColor Green
Write-Host "ZIP:  $OutZip" -ForegroundColor Green
if (Test-Path $OutExe) {
    Write-Host "EXE:  $OutExe" -ForegroundColor Green
    Write-Host "Install: double-click ExLlamaSharp-Setup-win-x64.exe (Admin / UAC)" -ForegroundColor Green
}
else {
    Write-Host "Install with: right-click Install.bat -> Run as administrator" -ForegroundColor Green
}