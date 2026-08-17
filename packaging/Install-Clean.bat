@echo off
:: One-click elevated install. Double-click this file.
setlocal
cd /d "%~dp0"

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Solicitando Administrador...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

echo.
echo ===============================================================
echo   ExLlamaSharp - instalacao limpa
echo ===============================================================
echo.
echo Log: %%TEMP%%\ExLlamaSharp-Install.log
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Unattended
set ERR=%ERRORLEVEL%

echo.
if %ERR%==0 (
  echo SUCESSO. Abrindo http://127.0.0.1:14563 ...
  start http://127.0.0.1:14563
) else (
  echo Exit code %ERR%. Veja %%TEMP%%\ExLlamaSharp-Install.log
  if exist "%TEMP%\ExLlamaSharp-Install.log" type "%TEMP%\ExLlamaSharp-Install.log"
)

echo.
pause
exit /b %ERR%
