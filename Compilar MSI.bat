@echo off
rem Compila y empaqueta el instalador MSI de Screen Recorder con WiX Toolset.
rem Requiere .NET 10 SDK y el CLI de WiX 4.0.6 (se instala automaticamente si falta).
set "SCRIPT=%~dp0build-msi.ps1"
if not exist "%SCRIPT%" (echo No se encontro build-msi.ps1 & pause & exit /b 1)

powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
echo.
pause
