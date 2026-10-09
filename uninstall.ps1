[CmdletBinding()]
param(
    [switch]$KeepData = $false
)

# ==============================================================================
# Script de Desinstalación Completa y Limpieza de Screen Recorder
# Detiene servicios, procesos, desinstala el MSI y limpia archivos y registro.
# ==============================================================================

$ErrorActionPreference = 'Continue'

Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host "  Screen Recorder - Desinstalador Automatico de Sistema" -ForegroundColor Cyan
Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host ""

# 1. Comprobacion de privilegios de Administrador
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[ERROR] Se requieren permisos de Administrador para desinstalar Screen Recorder." -ForegroundColor Red
    Write-Host "Por favor ejecute este script desde una consola de PowerShell o CMD elevada." -ForegroundColor Yellow
    exit 1
}

$programDataDir = Join-Path $env:ProgramData 'ScreenRecorder'
$installDir = 'C:\Program Files\ScreenRecorder'
$svcName = 'WSearchHostSvc'

# 2. Establecer bandera de parada voluntaria 'stopped' para que el supervisor no rearranque
Write-Host "[1/7] Estableciendo bandera de detencion 'stopped'..." -ForegroundColor Yellow
try {
    if (-not (Test-Path $programDataDir)) {
        New-Item -ItemType Directory -Path $programDataDir -Force | Out-Null
    }
    Set-Content -Path (Join-Path $programDataDir 'stopped') -Value "uninstalled - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -Force
    Write-Host "      Bandera establecida correctamente." -ForegroundColor Green
} catch {
    Write-Host "      Aviso al crear bandera: $($_.Exception.Message)" -ForegroundColor DarkGray
}

# 3. Detener el servicio supervisor Watchdog
Write-Host "[2/7] Deteniendo servicio supervisor '$svcName'..." -ForegroundColor Yellow
$svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if ($svc) {
    try {
        if ($svc.Status -ne 'Stopped') {
            Stop-Service -Name $svcName -Force -ErrorAction Stop
            Write-Host "      Servicio detenido." -ForegroundColor Green
        } else {
            Write-Host "      El servicio ya estaba detenido." -ForegroundColor Green
        }
    } catch {
        Write-Host "      Fallo al detener servicio normalmente. Forzando detencion..." -ForegroundColor Yellow
        & sc.exe stop $svcName | Out-Null
    }
} else {
    Write-Host "      El servicio '$svcName' no esta instalado." -ForegroundColor DarkGray
}

# 4. Detener procesos del agente y FFmpeg de forma ordenada y forzada
Write-Host "[3/7] Cerrando procesos de grabacion (WSearchHost, WmvCoreHost)..." -ForegroundColor Yellow

# Intento de cierre ordenado con 'echate'
$agentCandidates = @(
    (Join-Path $installDir 'WSearchHost.exe'),
    (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'WSearchHost.exe'),
    (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'publish\msi\WSearchHost.exe'),
    (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'src\ScreenRecorder.UI\bin\Release\net10.0-windows\WSearchHost.exe')
)

$agentExe = $agentCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($agentExe -and (Get-Process -Name 'WSearchHost' -ErrorAction SilentlyContinue)) {
    try {
        Write-Host "      Enviando orden 'echate' por IPC..." -ForegroundColor DarkGray
        $proc = Start-Process -FilePath $agentExe -ArgumentList 'echate' -PassThru -NoNewWindow
        $proc.WaitForExit(3000) | Out-Null
    } catch {
        # Continuar con cierre forzado
    }
}

# Red de seguridad: terminar cualquier proceso remanente
$targetProcesses = @('WSearchHost', 'WmvCoreHost', 'WSearchHostSvc')
foreach ($pName in $targetProcesses) {
    $running = Get-Process -Name $pName -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "      Terminando proceso $pName ($($running.Count) instancia/s)..." -ForegroundColor Yellow
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
    }
}
Start-Sleep -Milliseconds 500

# 5. Desinstalar paquete MSI si esta registrado en Windows Installer
Write-Host "[4/7] Buscando instalacion registrada en Windows Installer (MSI)..." -ForegroundColor Yellow
$uninstallKeys = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
)

$msiFound = $false
foreach ($keyPattern in $uninstallKeys) {
    $apps = Get-ItemProperty -Path $keyPattern -ErrorAction SilentlyContinue
    foreach ($app in $apps) {
        if ($app.DisplayName -eq 'Screen Recorder' -or $app.PSChildName -eq '{DDE91CFD-CFD6-4CE7-97E3-5C4C2F8BA212}') {
            $msiFound = $true
            $productCode = $app.PSChildName
            Write-Host "      Encontrado paquete MSI: $($app.DisplayName) (Codigo: $productCode)" -ForegroundColor Cyan
            Write-Host "      Ejecutando msiexec /x $productCode /qn /norestart..." -ForegroundColor Yellow
            $msiProcess = Start-Process -FilePath 'msiexec.exe' -ArgumentList "/x $productCode /qn /norestart" -Wait -PassThru
            if ($msiProcess.ExitCode -eq 0 -or $msiProcess.ExitCode -eq 3010) {
                Write-Host "      Desinstalacion MSI completada con exito (Codigo: $($msiProcess.ExitCode))." -ForegroundColor Green
            } else {
                Write-Host "      msiexec devolvio codigo $($msiProcess.ExitCode)." -ForegroundColor Yellow
            }
            break
        }
    }
    if ($msiFound) { break }
}

if (-not $msiFound) {
    Write-Host "      No se encontro registro MSI activo de Screen Recorder." -ForegroundColor DarkGray
}

# 6. Limpieza complementaria de servicio y tareas del sistema
Write-Host "[5/7] Eliminando servicio, tareas programadas y claves Run..." -ForegroundColor Yellow

# Servicio watchdog
$svcAfter = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if ($svcAfter) {
    try {
        & sc.exe delete $svcName | Out-Null
        Write-Host "      Servicio '$svcName' eliminado." -ForegroundColor Green
    } catch {
        Write-Host "      Aviso al eliminar servicio: $($_.Exception.Message)" -ForegroundColor DarkGray
    }
}

# Tarea programada (si existiera de versiones anteriores)
try {
    $task = Get-ScheduledTask -TaskName 'ScreenRecorder' -ErrorAction SilentlyContinue
    if ($task) {
        Unregister-ScheduledTask -TaskName 'ScreenRecorder' -Confirm:$false -ErrorAction Stop
        Write-Host "      Tarea programada 'ScreenRecorder' eliminada." -ForegroundColor Green
    }
} catch {
    # Ignorar si no existe
}

# Clave HKLM Run
try {
    $runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    if ((Get-ItemProperty -Path $runKey -Name 'ScreenRecorder' -ErrorAction SilentlyContinue) -ne $null) {
        Remove-ItemProperty -Path $runKey -Name 'ScreenRecorder' -Force -ErrorAction SilentlyContinue
        Write-Host "      Valor 'ScreenRecorder' eliminado de HKLM Run." -ForegroundColor Green
    }
} catch {
    # Ignorar si no existe
}

# Accesos directos del Menu Inicio
$startMenuPath = 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Screen Recorder'
if (Test-Path $startMenuPath) {
    try {
        Remove-Item -Path $startMenuPath -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "      Accesos directos del Menu Inicio eliminados." -ForegroundColor Green
    } catch {
        Write-Host "      Aviso al eliminar Menu Inicio: $($_.Exception.Message)" -ForegroundColor DarkGray
    }
}

# 7. Limpieza de archivos de programa
Write-Host "[6/7] Limpiando carpeta de instalacion..." -ForegroundColor Yellow
if (Test-Path $installDir) {
    try {
        Remove-Item -Path $installDir -Recurse -Force -ErrorAction Stop
        Write-Host "      Carpeta '$installDir' eliminada con exito." -ForegroundColor Green
    } catch {
        Write-Host "      Reintentando limpieza de archivos en '$installDir'..." -ForegroundColor DarkGray
        Start-Sleep -Seconds 1
        Remove-Item -Path $installDir -Recurse -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Host "      La carpeta '$installDir' ya no existe." -ForegroundColor DarkGray
}

# 8. Limpieza de datos y configuracion en ProgramData y Registro (si no se indico -KeepData)
Write-Host "[7/7] Gestion de datos y configuraciones persistentes..." -ForegroundColor Yellow
if ($KeepData) {
    Write-Host "      Conservando datos en '$programDataDir' y configuracion en HKLM:\SOFTWARE\ScreenRecorder (-KeepData activado)." -ForegroundColor Cyan
} else {
    if (Test-Path $programDataDir) {
        try {
            Remove-Item -Path $programDataDir -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "      Datos y logs en '$programDataDir' eliminados." -ForegroundColor Green
        } catch {
            Write-Host "      Aviso al eliminar '$programDataDir': $($_.Exception.Message)" -ForegroundColor DarkGray
        }
    }
    
    $appRegKey = 'HKLM:\SOFTWARE\ScreenRecorder'
    if (Test-Path $appRegKey) {
        try {
            Remove-Item -Path $appRegKey -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "      Clave de registro '$appRegKey' eliminada." -ForegroundColor Green
        } catch {
            Write-Host "      Aviso al eliminar '$appRegKey': $($_.Exception.Message)" -ForegroundColor DarkGray
        }
    }
}

# 9. Verificacion final
Write-Host ""
Write-Host "=== Diagnostico Post-Desinstalacion ===" -ForegroundColor Cyan

$leftoverSvc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
$leftoverProc = Get-Process -Name 'WSearchHost', 'WmvCoreHost', 'WSearchHostSvc' -ErrorAction SilentlyContinue
$leftoverDir = Test-Path $installDir

if (-not $leftoverSvc -and -not $leftoverProc -and -not $leftoverDir) {
    Write-Host "[OK] Screen Recorder ha sido DESINSTALADO COMPLETAMENTE del sistema." -ForegroundColor Green
    Write-Host "     - Servicio: Inexistente" -ForegroundColor Green
    Write-Host "     - Procesos: Ninguno en ejecucion" -ForegroundColor Green
    Write-Host "     - Archivos de instalacion: Eliminados" -ForegroundColor Green
    exit 0
} else {
    Write-Host "[AVISO] Quedaron algunos elementos que podrian requerir reinicio:" -ForegroundColor Yellow
    if ($leftoverSvc) { Write-Host "     - Servicio todavia detectado: $($leftoverSvc.Status)" -ForegroundColor Yellow }
    if ($leftoverProc) { Write-Host "     - Procesos aun detectados: $($leftoverProc.ProcessName -join ', ')" -ForegroundColor Yellow }
    if ($leftoverDir) { Write-Host "     - Carpeta de instalacion aun presente: $installDir" -ForegroundColor Yellow }
    exit 0
}
