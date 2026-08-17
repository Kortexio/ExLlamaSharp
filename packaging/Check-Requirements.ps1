<#
.SYNOPSIS
  Preflight checks for ExLlamaSharp (CUDA driver, VRAM, disk, OS).

.DESCRIPTION
  Exit code 0 = all required checks pass (warnings allowed).
  Exit code 1 = one or more required checks failed.
#>
[CmdletBinding()]
param(
    [Parameter()]
    [int]$MinVramGb = 8,

    [Parameter()]
    [int]$MinDiskGb = 50,

    [Parameter()]
    [string]$ModelsPath = ""
)

$ErrorActionPreference = "Continue"
$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

function Pass([string]$Name, [string]$Detail) {
    Write-Host "[PASS] $Name - $Detail" -ForegroundColor Green
}
function Warn([string]$Name, [string]$Detail) {
    Write-Host "[WARN] $Name - $Detail" -ForegroundColor Yellow
    $script:warnings.Add("$Name: $Detail") | Out-Null
}
function Fail([string]$Name, [string]$Detail) {
    Write-Host "[FAIL] $Name - $Detail" -ForegroundColor Red
    $script:failures.Add("$Name: $Detail") | Out-Null
}

Write-Host "ExLlamaSharp requirements check" -ForegroundColor Cyan
Write-Host ""

# --- OS ---
$os = Get-CimInstance Win32_OperatingSystem
$caption = $os.Caption
$build = [int]$os.BuildNumber
# Windows 10 20H1 = build 19041; Windows 11 = 22000+
if ($build -ge 19041) {
    Pass "OS" "$caption (build $build)"
}
else {
    Fail "OS" "$caption (build $build). Need Windows 10 20H1+ (19041) or Windows 11."
}

if (-not [Environment]::Is64BitOperatingSystem) {
    Fail "Architecture" "64-bit Windows required."
}
else {
    Pass "Architecture" "x64"
}

# --- .NET ---
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) {
    $sdks = & dotnet --list-sdks 2>$null
    $hasNet10 = $sdks | Where-Object { $_ -match "^10\." }
    if ($hasNet10) {
        Pass ".NET SDK" ($hasNet10 | Select-Object -First 1)
    }
    else {
        Warn ".NET SDK" "dotnet found but no .NET 10 SDK listed (runtime may still be bundled with installer)."
    }
}
else {
    Warn ".NET SDK" "dotnet not on PATH (OK for installed service; needed for development)."
}

# --- NVIDIA / CUDA driver ---
$smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if (-not $smi) {
    Fail "NVIDIA driver" "nvidia-smi not found. Install a recent Game Ready / Studio / Data Center driver."
}
else {
    try {
        $driver = (& nvidia-smi --query-gpu=driver_version --format=csv,noheader 2>$null | Select-Object -First 1).Trim()
        $cuda = (& nvidia-smi | Select-String -Pattern "CUDA Version:\s*([\d.]+)" | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1)
        if ($cuda) {
            $cudaMajorMinor = [version]$cuda
            if ($cudaMajorMinor -ge [version]"12.8") {
                Pass "CUDA (driver-reported)" "CUDA $cuda (driver $driver)"
            }
            elseif ($cudaMajorMinor -ge [version]"12.0") {
                Warn "CUDA (driver-reported)" "CUDA $cuda (driver $driver). Recommend 12.8+ for this project."
            }
            else {
                Fail "CUDA (driver-reported)" "CUDA $cuda too old. Need driver supporting CUDA 12.8+."
            }
        }
        else {
            Warn "CUDA (driver-reported)" "Driver $driver detected; could not parse CUDA version from nvidia-smi."
        }

        $gpus = & nvidia-smi --query-gpu=index,name,memory.total --format=csv,noheader,nounits 2>$null
        $bestVramGb = 0.0
        foreach ($line in $gpus) {
            $parts = $line.Split(",", [System.StringSplitOptions]::TrimEntries)
            if ($parts.Length -ge 3) {
                $vramMb = 0.0
                [void][double]::TryParse($parts[2], [ref]$vramMb)
                $vramGb = $vramMb / 1024.0
                if ($vramGb -gt $bestVramGb) { $bestVramGb = $vramGb }
                Write-Host "  GPU $($parts[0]): $($parts[1]) - $([math]::Round($vramGb,1)) GB"
            }
        }

        if ($bestVramGb -ge $MinVramGb) {
            Pass "VRAM" ("{0:N1} GB available (min {1} GB)" -f $bestVramGb, $MinVramGb)
        }
        elseif ($bestVramGb -ge 4) {
            Warn "VRAM" ("{0:N1} GB on best GPU - below recommended {1} GB; smaller models / higher quant still OK." -f $bestVramGb, $MinVramGb)
        }
        elseif ($bestVramGb -gt 0) {
            Fail "VRAM" ("{0:N1} GB on best GPU; need at least ~4 GB." -f $bestVramGb)
        }
        else {
            Fail "VRAM" "No GPU memory reported by nvidia-smi."
        }
    }
    catch {
        Fail "NVIDIA driver" "nvidia-smi failed: $_"
    }
}

# --- Disk ---
$checkPath = if ($ModelsPath) { $ModelsPath } else { Join-Path $env:ProgramData "ExLlamaSharp\models" }
if (-not (Test-Path $checkPath)) {
    New-Item -ItemType Directory -Path $checkPath -Force | Out-Null
}
$root = [System.IO.Path]::GetPathRoot((Resolve-Path $checkPath).Path)
$drive = Get-PSDrive -Name $root.TrimEnd('\').TrimEnd(':') -ErrorAction SilentlyContinue
if (-not $drive) {
    $di = New-Object System.IO.DriveInfo($root)
    $freeGb = [math]::Round($di.AvailableFreeSpace / 1GB, 1)
}
else {
    $freeGb = [math]::Round($drive.Free / 1GB, 1)
}

if ($freeGb -ge $MinDiskGb) {
    Pass "Disk" "$freeGb GB free on $root (models path: $checkPath)"
}
elseif ($freeGb -ge 20) {
    Warn "Disk" "$freeGb GB free on $root - below recommended $MinDiskGb GB."
}
else {
    Fail "Disk" "$freeGb GB free on $root - need at least ~20 GB (recommend $MinDiskGb GB)."
}

# --- Summary ---
Write-Host ""
if ($warnings.Count -gt 0) {
    Write-Host "Warnings ($($warnings.Count)):" -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host "  - $_" }
}
if ($failures.Count -gt 0) {
    Write-Host "Failures ($($failures.Count)):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
    Write-Host "Requirements NOT met." -ForegroundColor Red
    exit 1
}

Write-Host "All required checks passed." -ForegroundColor Green
exit 0
