@echo off
rem Diagnostica el autoarranque de ScreenRecorder (no modifica nada).
set "SCRIPT=%~dp0verify-autostart.ps1"
if not exist "%SCRIPT%" set "SCRIPT=C:\Program Files\ScreenRecorder\verify-autostart.ps1"
if not exist "%SCRIPT%" (echo No se encontro verify-autostart.ps1 & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
echo.
pause
