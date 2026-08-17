# Build native stub DLL (no CUDA) for Windows
# Usage: pwsh packaging/build-native-stub.ps1

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Native = Join-Path $Root "native\exllamasharp"
$Build = Join-Path $Native "build-stub"

function Find-CMake {
    $cmd = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        "${env:ProgramFiles}\CMake\bin\cmake.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

$cmake = Find-CMake
if (-not $cmake) {
    Write-Warning "CMake not found. Skipping native stub build. Install CMake or VS C++ CMake tools, then re-run."
    Write-Host "ExLlamaV3 local tree: $(Test-Path (Join-Path $Root 'third_party\exllamav3\exllamav3\exllamav3_ext'))"
    exit 0
}

Write-Host "Using CMake: $cmake"
New-Item -ItemType Directory -Force -Path $Build | Out-Null

& $cmake -S $Native -B $Build -DEXL_STUB=ON -DEXL_LINK_EXLLAMAV3=OFF
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }

& $cmake --build $Build --config Release
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

$dll = Get-ChildItem $Build -Recurse -Filter exllamasharp.dll -ErrorAction SilentlyContinue | Select-Object -First 1
if ($dll) {
    $out = Join-Path $Root "src\ExLlamaSharp\runtimes\win-x64\native"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    Copy-Item $dll.FullName (Join-Path $out "exllamasharp_native.dll") -Force
    Write-Host "Copied $($dll.FullName) -> $(Join-Path $out 'exllamasharp_native.dll')"
} else {
    Write-Warning "exllamasharp.dll not found under $Build"
}

Write-Host "Native stub build done."
