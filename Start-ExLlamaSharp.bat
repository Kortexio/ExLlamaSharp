@echo off
REM ExLlamaSharp - Iniciar Serviço
REM Execute este arquivo como Administrador (right-click -> Run as administrator)

echo.
echo ======================================
echo  ExLlamaSharp - Iniciar Servico
echo ======================================
echo.

net session >nul 2>&1
if %errorLevel% NEQ 0 (
    echo ERRO: Este script precisa ser executado como Administrador!
    echo.
    echo Por favor:
    echo   1. Right-click neste arquivo
    echo   2. Selecione "Executar como administrador"
    echo.
    pause
    exit /b 1
)

echo Iniciando servico ExLlamaSharp...
sc start ExLlamaSharp

timeout /t 10 /nobreak >nul

echo.
echo Verificando status...
sc query ExLlamaSharp

echo.
echo Testando health endpoint...
powershell -Command "try { $r = Invoke-RestMethod 'http://localhost:14563/health' -TimeoutSec 5; Write-Host 'V Servidor funcionando! Status:' $r.status -ForegroundColor Green } catch { Write-Host 'X Servidor nao respondeu ainda. Aguarde mais alguns segundos.' -ForegroundColor Yellow }"

echo.
echo ======================================
echo  Acesse: http://localhost:14563
echo  Usuario: admin
echo  Senha: changeme
echo ======================================
echo.
pause
