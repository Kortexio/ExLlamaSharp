@echo off
setlocal
cd /d "%~dp0"

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Requesting administrator privileges...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

if exist "%~dp0scripts\Uninstall.ps1" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Uninstall.ps1"
) else if exist "%~dp0Uninstall.ps1" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1"
) else (
  echo ERROR: Uninstall.ps1 not found.
  pause
  exit /b 1
)

echo.
pause
