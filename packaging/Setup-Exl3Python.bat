@echo off
:: Downloads latest CUDA PyTorch wheels + ExLlamaV3 (Admin recommended)
setlocal
cd /d "%~dp0"
title ExLlamaSharp - GPU Runtime (PyTorch)

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Requesting administrator privileges...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

echo.
echo ================================================================
echo  ExLlamaSharp - GPU Runtime (PyTorch + ExLlamaV3)
echo ================================================================
echo.
echo  This is SEPARATE from the MSI install.
echo  The MSI does NOT download PyTorch.
echo.
echo  This step downloads ~2 GB from pytorch.org into:
echo    %~dp0venv\
echo.
echo  Typical time: 5-30 minutes depending on your network.
echo  You will see progress messages every ~15 seconds.
echo ================================================================
echo.

if exist "%~dp0scripts\Setup-Exl3Python.ps1" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Setup-Exl3Python.ps1" -VenvPath "%~dp0venv" -DownloadDemoModel
) else if exist "%~dp0Setup-Exl3Python.ps1" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-Exl3Python.ps1" -VenvPath "%~dp0venv" -DownloadDemoModel
) else (
  echo ERROR: Setup-Exl3Python.ps1 not found.
  pause
  exit /b 1
)

echo.
pause
