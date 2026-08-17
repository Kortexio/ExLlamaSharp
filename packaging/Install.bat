@echo off
:: ExLlamaSharp installer launcher — Admin then Install.ps1
setlocal
cd /d "%~dp0"

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Requesting administrator privileges...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

echo.
echo ExLlamaSharp Setup
echo.

if exist "%~dp0Install.ps1" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
) else if exist "%~dp0Install-ExLlamaSharp.ps1" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-ExLlamaSharp.ps1"
) else (
  echo ERROR: Install.ps1 not found.
  pause
  exit /b 1
)

echo.
pause
