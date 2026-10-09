@echo off
setlocal
cd /d "%~dp0"

echo =======================================================
echo   Screen Recorder - Desinstalacion Automatica
echo =======================================================
echo.

rem Autoelevacion: relanzar este mismo .bat como administrador si no lo es
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Solicitando permisos de administrador...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs"
    exit /b
)

set "SCRIPT=%~dp0uninstall.ps1"
if not exist "%SCRIPT%" set "SCRIPT=C:\Program Files\ScreenRecorder\uninstall.ps1"
if not exist "%SCRIPT%" (
    echo [ERROR] No se encontro el script de desinstalacion uninstall.ps1 en:
    echo         %~dp0uninstall.ps1
    echo.
    pause
    exit /b 1
)

echo Ejecutando desinstalacion y limpieza completa...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if %EXIT_CODE% equ 0 (
    echo [EXITO] Screen Recorder se ha desinstalado satisfactoriamente.
) else (
    echo [ADVERTENCIA] El proceso finalizo con codigo de salida %EXIT_CODE%.
)
echo.
pause
exit /b %EXIT_CODE%
