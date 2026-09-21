@echo off
REM ExLlamaSharp - Start Service
REM Run this file as Administrator (right-click -> Run as administrator)

echo.
echo ======================================
echo  ExLlamaSharp - Start Service
echo ======================================
echo.

net session >nul 2>&1
if %errorLevel% NEQ 0 (
    echo ERROR: This script must be run as Administrator!
    echo.
    echo Please:
    echo   1. Right-click this file
    echo   2. Select "Run as administrator"
    echo.
    pause
    exit /b 1
)

echo Starting ExLlamaSharp service...
sc start ExLlamaSharp

timeout /t 10 /nobreak >nul

echo.
echo Checking status...
sc query ExLlamaSharp

echo.
echo Testing health endpoint...
powershell -Command "try { $r = Invoke-RestMethod 'http://localhost:14563/health' -TimeoutSec 5; Write-Host 'OK Server is running! Status:' $r.status -ForegroundColor Green } catch { Write-Host 'Server has not responded yet. Wait a few more seconds.' -ForegroundColor Yellow }"

echo.
echo ======================================
echo  Open: http://localhost:14563
echo  User: admin
echo  Password: changeme
echo ======================================
echo.
pause
