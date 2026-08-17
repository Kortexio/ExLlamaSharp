# Download / install CUDA Toolkit + LibTorch (Windows)

## Already done by agent (when successful)

| Dep | Path | Status |
|-----|------|--------|
| LibTorch 2.8+cu128 | `third_party/libtorch/` | Downloaded & extracted |
| CUDA Toolkit | needs **Admin / UAC** | Installer may be under `packaging/cuda_*.exe` |

## LibTorch (no admin)

```powershell
# If missing, re-run:
$dest = "C:\Users\vitor\source\repos\ExLlamaSharp\third_party"
$url = "https://download.pytorch.org/libtorch/cu128/libtorch-win-shared-with-deps-2.8.0%2Bcu128.zip"
Invoke-WebRequest $url -OutFile "$dest\libtorch-cu128.zip"
Expand-Archive "$dest\libtorch-cu128.zip" $dest -Force
```

## CUDA Toolkit (Admin required)

Winget / NVIDIA installers **must elevate**. If UAC was cancelled:

```powershell
# Option A — winget (approve UAC)
winget install --id Nvidia.CUDA --accept-package-agreements --accept-source-agreements

# Option B — local installer (CUDA 12.8 matches LibTorch cu128)
Start-Process -FilePath packaging\cuda_12.8.1_windows.exe -Verb RunAs
# or silent:
Start-Process -FilePath packaging\cuda_12.8.1_windows.exe -ArgumentList "-s" -Verb RunAs -Wait
```

After install, open a **new** terminal and check:

```powershell
nvcc --version
# expect Cuda compilation tools release 12.8 (or 13.x)
```

## Build native with CUDA

```powershell
$libtorch = "C:\Users\vitor\source\repos\ExLlamaSharp\third_party\libtorch"
cmake -S native/exllamasharp -B native/exllamasharp/build `
  -DEXL_STUB=OFF -DEXL_LINK_EXLLAMAV3=ON `
  -DCMAKE_PREFIX_PATH="$libtorch"
cmake --build native/exllamasharp/build --config Release
```

Driver note: your machine already has NVIDIA driver with CUDA 13.3 UMD — that is enough to **run** CUDA apps. The **Toolkit** (`nvcc`) is only needed to **compile** kernels.
