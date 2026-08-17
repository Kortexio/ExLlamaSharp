# ExLlamaSharp — Installation

## Quick install

1. Build: `powershell -ExecutionPolicy Bypass -File packaging\Build-Installer.ps1`
2. Run `publish\ExLlamaSharp-Setup-win-x64.exe` as Administrator
3. Open http://localhost:14563

The Setup.exe embeds the official ExLlamaV3 CUDA wheel, worker deps, Python 3.12 (if missing), and VC++. PyTorch CUDA is downloaded during install.

## What gets installed

- App under `C:\Program Files\ExLlamaSharp` (includes `offline-wheels\` and `redist\`)
- Data under `%ProgramData%\ExLlamaSharp`
- Python venv + PyTorch CUDA 12.8 + `exllamav3_ext.pyd`
- Windows Service `ExLlamaSharp`
- Firewall rule, shortcuts, Tray app

## Slim build (app only)

```powershell
powershell -ExecutionPolicy Bypass -File packaging\Build-Installer.ps1 -SkipBundleWheels
Install.ps1 -SkipPyTorch
```

## Repair GPU runtime

```powershell
# From install dir — uses bundled offline-wheels when present
.\Setup-Exl3Python.bat
```

## Uninstall

Run `Uninstall.bat` as Administrator.

## Requirements

- Windows 10/11 x64
- NVIDIA GPU + driver for CUDA 12.8 (Python is bundled if missing)

See also: [vs2025-cuda-issue.md](vs2025-cuda-issue.md) if someone tries to JIT-compile ExLlamaV3 (not needed with the bundled wheel).
