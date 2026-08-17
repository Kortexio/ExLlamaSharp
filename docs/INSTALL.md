# ExLlamaSharp — Installation

## Quick install

1. Build: `powershell -ExecutionPolicy Bypass -File packaging\Build-Installer.ps1`
2. Unzip `publish\ExLlamaSharp-Setup-win-x64.zip`
3. Run **Install.ps1** (or **Install.bat**) as Administrator
4. Open http://localhost:14563

## What gets installed

- App under `C:\Program Files\ExLlamaSharp`
- Data under `%ProgramData%\ExLlamaSharp`
- Python venv + PyTorch CUDA 12.8 (unless `-SkipPyTorch`)
- Windows Service `ExLlamaSharp`
- Firewall rule, shortcuts, Tray app

## Options

```powershell
Install.ps1 -SkipPyTorch
Install.ps1 -InstallDir "D:\Apps\ExLlamaSharp"
Install.ps1 -Unattended
```

## Repair GPU runtime

```powershell
# From install dir
.\Setup-Exl3Python.bat
```

## Uninstall

Run `Uninstall.bat` as Administrator.

## Requirements

- Windows 10/11 x64
- Python 3.10+
- NVIDIA GPU recommended (CUDA 12.8 wheels)

See also: [vs2025-cuda-issue.md](vs2025-cuda-issue.md) if ExLlamaV3 Python fails to compile (server still works with native backend).
