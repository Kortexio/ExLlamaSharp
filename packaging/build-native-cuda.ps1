#Requires -Version 5.1
<#
.SYNOPSIS
  Builds native exllamasharp.dll WITH CUDA + LibTorch (EXL_STUB=OFF).

.DESCRIPTION
  Uses VsDevCmd + CMake. Tries -DEXL_LINK_EXLLAMAV3=ON first; on failure rebuilds with OFF
  (metadata load + LibTorch/cudart still linked). Production EXL3 inference uses the Python worker.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging\build-native-cuda.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipExLlamaV3Link,

    [string]$LibTorchPath = ""
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Native = Join-Path $Root "native\exllamasharp"
$Build = Join-Path $Native "build-cuda"

function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

function Find-CMake {
    $cmd = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        "${env:ProgramFiles}\CMake\bin\cmake.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

function Find-VsDevCmd {
    # Prefer VS 2022 for CUDA 12.8 (VS 18 / cl 19.5x crashes cudafe++).
    $candidates = @(
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\Common7\Tools\VsDevCmd.bat",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat",
        "${env:ProgramFiles}\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

# Refresh PATH for CUDA / cmake
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
            [System.Environment]::GetEnvironmentVariable("Path", "User")
$cudaBin = "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8\bin"
if (Test-Path $cudaBin) {
    $env:Path = "$cudaBin;$env:Path"
    $env:CUDA_PATH = "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8"
}

if ([string]::IsNullOrWhiteSpace($LibTorchPath)) {
    $LibTorchPath = Join-Path $Root "third_party\libtorch"
}
if (-not (Test-Path (Join-Path $LibTorchPath "share\cmake\Torch\TorchConfig.cmake"))) {
    throw "LibTorch not found at $LibTorchPath (see packaging/install-cuda-libtorch.md)"
}

$cmake = Find-CMake
if (-not $cmake) {
    Write-Step "Installing CMake via winget"
    winget install --id Kitware.CMake -e --accept-package-agreements --accept-source-agreements
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("Path", "User")
    $cmake = Find-CMake
}
if (-not $cmake) { throw "CMake not found after install attempt" }

$vsDev = Find-VsDevCmd
if (-not $vsDev) { throw "VsDevCmd.bat not found (need VS C++ workload)" }

Write-Host "Native CUDA build" -ForegroundColor Green
Write-Host "CMake: $cmake"
Write-Host "VsDevCmd: $vsDev"
Write-Host "LibTorch: $LibTorchPath"

New-Item -ItemType Directory -Force -Path $Build | Out-Null

function Invoke-CmakeInDevCmd([string[]]$CmakeArgs) {
    $argLine = ($CmakeArgs | ForEach-Object {
        if ($_ -match '\s') { '"{0}"' -f ($_ -replace '"', '\"') } else { $_ }
    }) -join ' '
    $cudaPath = if ($env:CUDA_PATH) { $env:CUDA_PATH } else { "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8" }
    $bat = @"
@echo off
call "$vsDev" -arch=x64 >nul
set "CUDA_PATH=$cudaPath"
set "CUDA_PATH_V12_8=$cudaPath"
set "CudaToolkitDir=$cudaPath"
set "PATH=$cudaPath\bin;%PATH%"
"$cmake" $argLine
exit /b %ERRORLEVEL%
"@
    $tmp = Join-Path $env:TEMP "exl-cmake-$([guid]::NewGuid().ToString('n')).bat"
    Set-Content -Path $tmp -Value $bat -Encoding ASCII
    try {
        # Start-Process avoids polluting the PowerShell success stream with cmake stdout
        $p = Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"$tmp`"" -Wait -PassThru -NoNewWindow
        return [int]$p.ExitCode
    }
    finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
}

$linkFlag = if ($SkipExLlamaV3Link) { "OFF" } else { "ON" }
Write-Step "Configure (EXL_STUB=OFF EXL_LINK_EXLLAMAV3=$linkFlag)"

# Force VS 2022 generator - VS 18 2026 lacks CUDA Visual Studio integration for CUDA 12.8
$generator = "Visual Studio 17 2022"
$cfgArgs = @(
    "-S", $Native,
    "-B", $Build,
    "-G", $generator,
    "-A", "x64",
    "-DEXL_STUB=OFF",
    "-DEXL_LINK_EXLLAMAV3=$linkFlag",
    "-DCMAKE_PREFIX_PATH=$LibTorchPath",
    "-DCMAKE_CUDA_COMPILER=$env:CUDA_PATH\bin\nvcc.exe"
)
if (-not (Test-Path "$env:CUDA_PATH\bin\nvcc.exe")) {
    $cfgArgs = $cfgArgs | Where-Object { $_ -notlike "-DCMAKE_CUDA_COMPILER=*" }
}

$cfgExit = Invoke-CmakeInDevCmd $cfgArgs

if ($cfgExit -ne 0 -and $linkFlag -eq "ON") {
    Write-Warning "Configure with EXL_LINK_EXLLAMAV3=ON failed; falling back to OFF"
    $linkFlag = "OFF"
    $cfgArgs = @(
        "-S", $Native,
        "-B", $Build,
        "-G", $generator,
        "-A", "x64",
        "-DEXL_STUB=OFF",
        "-DEXL_LINK_EXLLAMAV3=OFF",
        "-DCMAKE_PREFIX_PATH=$LibTorchPath"
    )
    $cfgExit = Invoke-CmakeInDevCmd $cfgArgs
}

if ($cfgExit -ne 0) { throw "cmake configure failed ($cfgExit)" }

Write-Step "Build Release"
$buildExit = Invoke-CmakeInDevCmd @("--build", $Build, "--config", "Release", "--parallel")
if ($buildExit -ne 0 -and $linkFlag -eq "ON") {
    Write-Warning "Build with kernels failed; reconfigure EXL_LINK_EXLLAMAV3=OFF"
    $linkFlag = "OFF"
    $cfgExit = Invoke-CmakeInDevCmd @(
        "-S", $Native,
        "-B", $Build,
        "-G", "Visual Studio 17 2022",
        "-A", "x64",
        "-DEXL_STUB=OFF",
        "-DEXL_LINK_EXLLAMAV3=OFF",
        "-DCMAKE_PREFIX_PATH=$LibTorchPath"
    )
    if ($cfgExit -ne 0) { throw "cmake reconfigure failed" }
    $buildExit = Invoke-CmakeInDevCmd @("--build", $Build, "--config", "Release", "--parallel")
}

if ($buildExit -ne 0) { throw "cmake build failed ($buildExit)" }

$dll = Get-ChildItem $Build -Recurse -Filter exllamasharp.dll -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $dll) { throw "exllamasharp.dll not found under $Build" }

$out = Join-Path $Root "src\ExLlamaSharp\runtimes\win-x64\native"
New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item $dll.FullName (Join-Path $out "exllamasharp_native.dll") -Force
Write-Host "Copied $($dll.FullName) -> $(Join-Path $out 'exllamasharp_native.dll')" -ForegroundColor Green
Write-Host "EXL_LINK_EXLLAMAV3=$linkFlag (production text generation uses Python worker)" -ForegroundColor Yellow
Write-Host "Native CUDA build done."
