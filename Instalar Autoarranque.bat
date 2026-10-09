@echo off
rem Registra la tarea programada para que ScreenRecorder arranque al iniciar sesion
rem y se reinicie solo ante fallos (nunca muere).
rem IMPORTANTE: ejecutar estando con la sesion del USUARIO que grabara en esta PC.
set "SCRIPT=%~dp0install-autostart.ps1"
if not exist "%SCRIPT%" set "SCRIPT=C:\Program Files\ScreenRecorder\install-autostart.ps1"
if not exist "%SCRIPT%" (echo No se encontro install-autostart.ps1 & pause & exit /b 1)

rem Autoelevacion: relanzar este mismo .bat como administrador
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Solicitando permisos de administrador...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
echo.
pause
