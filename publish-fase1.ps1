# Compila y publica la Fase 1 (captura GPU + multimonitor + QSV) en publish/UI.
# Requiere el .NET 10 SDK. Ejecutar desde la raíz del repo:
#   powershell -ExecutionPolicy Bypass -File publish-fase1.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# Asegurar que dotnet esté en el PATH
$dotnetDir = Join-Path $env:ProgramFiles 'dotnet'
if ((Test-Path $dotnetDir) -and ($env:Path -notlike "*$dotnetDir*")) {
    $env:Path = "$dotnetDir;$env:Path"
}

Write-Output "=== Restaurando paquetes ==="
dotnet restore
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR en restore"; exit 1 }

Write-Output "=== Compilando (Release) ==="
dotnet build --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR en build"; exit 1 }

Write-Output "=== Publicando Agente + UI de configuración ==="
dotnet publish src/ScreenRecorder.Agent/ScreenRecorder.Agent.csproj -c Release -r win-x64 --self-contained false -o publish/UI
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR en publish del agente"; exit 1 }

dotnet publish src/ScreenRecorder.UI/ScreenRecorder.UI.csproj -c Release -r win-x64 --self-contained false -o publish/UI
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR en publish de la UI"; exit 1 }

# Servicio watchdog (misma carpeta: sus DLLs se comparten por nombre con los de la UI)
dotnet publish src/ScreenRecorder.Service/ScreenRecorder.Service.csproj -c Release -r win-x64 --self-contained false -o publish/UI
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR en publish del servicio"; exit 1 }

# Wctl: CLI de control (comparte los DLLs de la UI)
dotnet publish src/Wctl/Wctl.csproj -c Release -r win-x64 --self-contained false -o publish/UI
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR en publish de Wctl"; exit 1 }

# Scripts de operación junto al binario
# 'Detener ScreenRecorder.bat' ya NO se distribuye (la parada voluntaria es solo de TI).
foreach ($f in @('Instalar Autoarranque.bat','Verificar Autoarranque.bat','install-autostart.ps1','verify-autostart.ps1')) {
    if (Test-Path $f) { Copy-Item $f publish/UI -Force }
}

Write-Output ""
Write-Output "OK: publicado en $root\publish\UI"
Write-Output "Verifique que WSearchAgent.exe, WSearchCfg.exe, WSearchHostSvc.exe, Wctl.exe, WmvCoreHost.exe y WCodecHost.exe estén presentes:"
Get-ChildItem publish/UI | Where-Object { $_.Name -in @('WSearchAgent.exe','WSearchCfg.exe','WSearchHostSvc.exe','Wctl.exe','WmvCoreHost.exe','WCodecHost.exe') } | ForEach-Object { "  - $($_.Name)" }
