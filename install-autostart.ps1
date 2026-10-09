# Instala/asegura el servicio watchdog "WSearchHostSvc" (LocalSystem, automático).
# El servicio es el ÚNICO lanzador del agente de grabación (WSearchHost.exe):
#   - Al arrancar Windows y cada 10 s vigila que el agente esté corriendo en la sesión
#     del usuario y lo rearranca si desaparece (aunque lo finalicen por la fuerza).
#   - Arranca el agente solo al iniciar sesión (lo levanta en cuanto detecta la sesión).
#   - Respeta 'echate' (parada voluntaria de TI): no resucita mientras exista la bandera.
#   - Además sube los fragmentos pendientes (huérfanos) y escribe heartbeat en la NUC.
#
# NOTA: la antigua tarea programada "cada 1 minuto" YA NO SE USA. Si existe de una
# instalación anterior, este script la elimina.
#
# Ejecutar como administrador. También lo invoca el MSI (custom action CA_RegisterAutostart)
# y la app en el modo /postinstall (al finalizar la instalación).
param(
    [string]$InstallDir = ''
)

$ErrorActionPreference = 'Stop'

# Diagnóstico: volcar toda la salida también a un log para poder diagnosticar fallos.
$__logDir = Join-Path $env:ProgramData 'ScreenRecorder'
New-Item -Force -ItemType Directory -Path $__logDir | Out-Null
Start-Transcript -Path (Join-Path $__logDir 'install-autostart.log') -Force -ErrorAction SilentlyContinue | Out-Null

Write-Output "=== Instalación del servicio watchdog de ScreenRecorder ==="
Write-Output "El servicio (SYSTEM) es ahora el ÚNICO mecanismo de autoarranque del agente."
Write-Output ""

# Auto-deteccion de la carpeta de instalacion (si no se paso -InstallDir)
if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $candidates = @(
        (Split-Path -Parent $MyInvocation.MyCommand.Path),
        'C:\Program Files\ScreenRecorder'
    ) | Select-Object -Unique

    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c 'WSearchHost.exe')) {
            $InstallDir = $c
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($InstallDir)) {
        Write-Output "ERROR: no se encontro WSearchHost.exe en las rutas:"
        $candidates | ForEach-Object { Write-Output "  - $_" }
        Write-Output "Indique la carpeta manualmente, por ejemplo:"
        Write-Output "  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" -InstallDir `"C:\Call\publish\UI`""
        exit 1
    }

    Write-Output "Carpeta de la aplicacion detectada automaticamente: $InstallDir"
}

$exe = Join-Path $InstallDir 'WSearchHost.exe'

if (-not (Test-Path $exe)) {
    Write-Output "ERROR: no se encontro $exe"
    Write-Output "Revise el parametro -InstallDir ('$InstallDir') y reintente."
    exit 1
}

# --- Limpieza del mecanismo antiguo: tarea programada y clave Run ---
$taskName = 'ScreenRecorder'
try {
    $oldTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($oldTask) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
        Write-Output "Tarea programada antigua '$taskName' eliminada (ya no se usa; el servicio es el único lanzador)."
    }
} catch {
    Write-Output "ADVERTENCIA: no se pudo eliminar la tarea programada antigua: $($_.Exception.Message)"
}
$runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
if ((Get-ItemProperty -Path $runKey -Name $taskName -ErrorAction SilentlyContinue) -ne $null) {
    Remove-ItemProperty -Path $runKey -Name $taskName -ErrorAction SilentlyContinue
}

# --- Registro y arranque del servicio watchdog (idempotente) ---
$svcExe = Join-Path $InstallDir 'WSearchHostSvc.exe'
$svcName = 'WSearchHostSvc'
if (-not (Test-Path $svcExe)) {
    Write-Output "ERROR: no se encontró $svcExe"
    Write-Output "Sin el servicio no hay autoarranque (la tarea programada ya no se registra)."
    exit 1
}

try {
    $existing = Get-Service -Name $svcName -ErrorAction SilentlyContinue
    if (-not $existing) {
        New-Service -Name $svcName -BinaryPathName "`"$svcExe`"" `
            -DisplayName 'ScreenRecorder Watchdog' -StartupType Automatic `
            -Description 'Único lanzador del agente de grabación: lo rearranca si desaparece (<=10 s), lo inicia al iniciar sesión, sube fragmentos pendientes a la NUC y escribe heartbeat. No detener.' | Out-Null
        Write-Output "Servicio watchdog '$svcName' registrado."
    } else {
        Set-Service -Name $svcName -StartupType Automatic
    }

    $svc = Get-Service -Name $svcName -ErrorAction Stop
    if ($svc.Status -ne 'Running') { Start-Service -Name $svcName -ErrorAction Stop }
    $svc = Get-Service -Name $svcName -ErrorAction Stop

    Write-Output ""
    Write-Output "OK: servicio watchdog '$svcName': $($svc.Status) (inicio automático)."
    Write-Output "  - Inicia el agente al arrancar la sesión del usuario (<=10-15 s tras el logon)."
    Write-Output "  - Lo rearranca en <=10 s si lo finalizan (crash o 'Finalizar tarea')."
    Write-Output "  - El agente y FFmpeg no los finaliza un usuario estándar (ACL anti-terminación)."
    Write-Output "  - 'echate' detiene de forma voluntaria (solo TI): el servicio NO lo revive hasta 'quieto'/Ajustes."
    Write-Output "  - Sube fragmentos pendientes y escribe heartbeat aunque no haya sesión."
    Write-Output ""
    Write-Output "ADVERTENCIA: si el servicio se detiene, nada lanza al agente (no hay tarea de respaldo)."
    Write-Output "Use 'Verificar Autoarranque.bat' para diagnosticar cualquier problema."
    exit 0
} catch {
    Write-Output "ERROR: no se pudo registrar/arrancar el servicio watchdog: $($_.Exception.Message)"
    exit 1
}
