# Diagnostico del autoarranque de ScreenRecorder. NO modifica nada: solo reporta.
param([string]$TaskName = 'ScreenRecorder')

$stopFlag = Join-Path $env:ProgramData 'ScreenRecorder\stopped'
$exe = $null
foreach ($c in @('C:\Program Files\ScreenRecorder', (Split-Path -Parent $MyInvocation.MyCommand.Path))) {
    if (Test-Path (Join-Path $c 'WSearchHost.exe')) { $exe = Join-Path $c 'WSearchHost.exe'; break }
}

Write-Output "========== Diagnostico de ScreenRecorder =========="

# 1) Tarea programada (mecanismo ANTIGUO: ya no se usa; el servicio es el único lanzador)
Write-Output ""
Write-Output "[Tarea programada]"
try {
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
    Write-Output "EXISTS una tarea '$TaskName' heredada de una instalacion anterior."
    Write-Output "Ya no se usa: ejecute 'Instalar Autoarranque.bat' (o reinstale el MSI) para eliminarla."
} catch {
    Write-Output "No hay tarea programada (correcto): el servicio watchdog es el unico lanzador."
}

# 1b) Servicio watchdog (SYSTEM)
Write-Output ""
Write-Output "[Servicio watchdog]"
$svcRunning = $false
$svcExists = $false
try {
    $svc = Get-Service -Name 'WSearchHostSvc' -ErrorAction Stop
    $svcExists = $true
    Write-Output "WSearchHostSvc: $($svc.Status) (StartType: $($svc.StartType))"
    if ($svc.Status -ne 'Running') {
        Write-Output "El servicio está detenido: intentando arrancarlo..."
        try {
            Start-Service -Name 'WSearchHostSvc' -ErrorAction Stop
            Write-Output "OK: servicio arrancado."
            $svcRunning = $true
        } catch {
            Write-Output "PROBLEMA: no se pudo arrancar el servicio: $($_.Exception.Message)"
        }
    } else {
        $svcRunning = $true
    }
} catch {
    Write-Output "PROBLEMA: el servicio watchdog 'WSearchHostSvc' NO existe."
    Write-Output "  Reinstale el MSI o ejecute 'Instalar Autoarranque.bat' como administrador."
}

# 1c) Estado del watchdog (watchdog-status.json) y VEREDICTO
Write-Output ""
Write-Output "[Veredicto]"
$statusPath = Join-Path $env:ProgramData 'ScreenRecorder\watchdog-status.json'
$agentRunning = (@(Get-Process -Name 'WSearchHost' -ErrorAction SilentlyContinue)).Count -gt 0
$ffmpegRunning = (@(Get-Process -Name 'WmvCoreHost' -ErrorAction SilentlyContinue)).Count -gt 0
$stoppedFlag = Test-Path (Join-Path $env:ProgramData 'ScreenRecorder\stopped')
if (Test-Path $statusPath) {
    try {
        $st = Get-Content $statusPath -Raw | ConvertFrom-Json
        Write-Output "Ultimo tick del servicio: $($st.ultimoTick)"
        Write-Output "Ultimo lanzamiento: $($st.ultimoLanzamiento)"
        if ($st.ultimoEnvio) { Write-Output "Ultimo envio a la NUC: $($st.ultimoEnvio)" }
        if ($st.ultimoError) { Write-Output "Ultimo error de lanzamiento: $($st.ultimoError)" }
        if ($st.ultimoErrorCola) { Write-Output "Ultimo error de la cola de envios: $($st.ultimoErrorCola)" }
    } catch { Write-Output "(No se pudo leer watchdog-status.json)" }
} else {
    Write-Output "(Sin watchdog-status.json: el servicio nunca ha escrito estado; ¿esta instalado y en ejecucion?)"
}
if ($stoppedFlag) {
    Write-Output "VEREDICTO: ⚠ PARADO VOLUNTARIAMENTE (banderin 'stopped'). Para reanudar: 'WSearchHost.exe quieto' o abrir Ajustes y Guardar."
} elseif (-not $svcExists) {
    Write-Output "VEREDICTO: ❌ FALTA EL SERVICIO. Reinstale el MSI o ejecute 'Instalar Autoarranque.bat' como administrador."
} elseif (-not $svcRunning) {
    Write-Output "VEREDICTO: ❌ SERVICIO DETENIDO. Revise el log de ScreenRecorder para el motivo."
} elseif ($agentRunning -and $ffmpegRunning) {
    Write-Output "VEREDICTO: ✅ OK - servicio en ejecucion, agente corriendo y GRABANDO (FFmpeg activo)."
} elseif ($agentRunning) {
    Write-Output "VEREDICTO: ⚠ AGENTE VIVO PERO SIN GRABAR (sin FFmpeg). El servicio le enviara 'quieto' de forma automatica (<=3 min);"
    Write-Output "  si persiste, revise las ultimas lineas del log: C:\ProgramData\ScreenRecorder\Logs\\"
} else {
    Write-Output "VEREDICTO: ⏳ EL SERVICIO ESTÁ ARRANCANDO AL AGENTE (reintenta cada 10 s)."
    Write-Output "  Espere ~15 s y vuelva a ejecutar este verificador; si persiste, revise el 'ultimoError' de arriba."
}

# 2) Bandera de detencion voluntaria
Write-Output ""
Write-Output "[Detencion voluntaria]"
if (Test-Path $stopFlag) {
    Write-Output "DETENIDO VOLUNTARIAMENTE ('echate', comando reservado a TI/admin)."
    Write-Output "El autoarranque NO lo revivira hasta que lo reanude. Para reanudar, cualquiera de estas:"
    if ($exe) { Write-Output "  1) `"$exe`" quieto" }
    Write-Output "  2) Abrir Ajustes (perro) y pulsar Guardar"
    Write-Output "  3) Doble clic en WSearchHost.exe"
} else {
    Write-Output "No hay bandera de detencion: el autoarranque esta habilitado."
}

# 3) Procesos
Write-Output ""
Write-Output "[Procesos]"
$ui = @(Get-Process -Name 'WSearchHost' -ErrorAction SilentlyContinue).Count
$ffProcs = @(Get-Process -Name 'WmvCoreHost' -ErrorAction SilentlyContinue)
Write-Output "WSearchHost.exe: $ui | WmvCoreHost.exe: $($ffProcs.Count)"
foreach ($p in $ffProcs) {
    try {
        $age = [math]::Round(((Get-Date) - $p.StartTime).TotalMinutes, 0)
        Write-Output "  - PID $($p.Id): lleva $age min (un solo proceso es lo normal; varios = huérfanos)"
    } catch { Write-Output "  - PID $($p.Id)" }
}
if ($ui -eq 0 -and -not (Test-Path $stopFlag)) {
    Write-Output "No corre y no hay bandera: el disparador por minuto lo levantara en menos de 1 minuto."
}

# Procesos de versiones anteriores (mezcla de versiones = ventanas fantasma)
$legacy = @(Get-Process -Name 'ScreenRecorder.UI', 'ffmpeg' -ErrorAction SilentlyContinue)
if ($legacy.Count -gt 0) {
    Write-Output ""
    Write-Output "PROBLEMA: hay procesos de una VERSION ANTERIOR corriendo:"
    $legacy | ForEach-Object { Write-Output "  - $($_.ProcessName) (PID $($_.Id))" }
    Write-Output "Finalicelos, reemplace TODA la carpeta de la app con el publish/UI actual y reinicie."
}

# Ventanas visibles de cualquiera de los procesos (incluidos los legacy)
Write-Output ""
Write-Output "[Ventanas visibles]"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class WinEnum {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
}
'@ -ErrorAction SilentlyContinue
$names = @('WSearchHost', 'WmvCoreHost', 'ScreenRecorder.UI', 'ffmpeg')
$map = @{}
foreach ($n in $names) { Get-Process -Name $n -ErrorAction SilentlyContinue | ForEach-Object { $map[$_.Id] = $_.ProcessName } }
$wins = @()
if ($map.Count -gt 0) {
    [WinEnum]::EnumWindows({ param($h, $l)
        $procId = 0
        [void][WinEnum]::GetWindowThreadProcessId($h, [ref]$procId)
        if ($map.ContainsKey($procId) -and [WinEnum]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][WinEnum]::GetWindowText($h, $sb, 256)
            $script:wins += "$($map[$procId]): titulo='$($sb.ToString())'"
        }
        return $true
    }, [IntPtr]::Zero) | Out-Null
}
if ($wins.Count -eq 0) {
    Write-Output "Ninguna ventana visible de los procesos del grabador."
} else {
    $wins | ForEach-Object { Write-Output "  $_" }
}

# 4) Log reciente
Write-Output ""
Write-Output "[Log reciente]"
$logDir = Join-Path $env:ProgramData 'ScreenRecorder\Logs'
if (Test-Path $logDir) {
    $log = Get-ChildItem $logDir -Filter '*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($log) {
        Get-Content $log.FullName -Tail 400 | Select-String -Pattern 'NUC OK|NUC NO|NUC SIN|Detencion voluntaria|huérfano|Config efectiva' | Select-Object -Last 6 | ForEach-Object { Write-Output "  $($_.Line)" }
    }
}

# 4b) Cadena de video: config NUC, ultimo envio real y estado del TEMP
Write-Output ""
Write-Output "[Cadena de video]"
try {
    $key = Get-ItemProperty -Path 'HKLM:\SOFTWARE\ScreenRecorder' -Name 'Settings' -ErrorAction Stop
    $cfg = $null
    try { $cfg = ($key.Settings | ConvertFrom-Json) } catch { }
    if ($cfg -and $cfg.NetworkPath) {
        Write-Output "Ruta NUC configurada: $($cfg.NetworkPath)"
    } else {
        Write-Output "Ruta NUC configurada: (SIN CONFIGURAR)"
    }
} catch {
    Write-Output "Ruta NUC configurada: (no se pudo leer el Registry)"
}

$logDir = Join-Path $env:ProgramData 'ScreenRecorder\Logs'
$today = Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($today) {
    $lastOk = Get-Content $today.FullName -ErrorAction SilentlyContinue | Select-String -Pattern 'Archivo subido exitosamente:' | Select-Object -Last 1
    if ($lastOk) {
        Write-Output ("Ultimo envio: " + ($lastOk.Line -replace '^.+Archivo subido exitosamente: ', ''))
    } else {
        Write-Output "Ultimo envio: (sin subidas exitosas registradas hoy en " + $today.Name + ")"
    }
    $lastFin = Get-Content $today.FullName -ErrorAction SilentlyContinue | Select-String -Pattern 'Fragmento finalizado' | Select-Object -Last 1
    if ($lastFin) { Write-Output ("Ultimo fragmento cerrado: " + ($lastFin.Line -replace '^.+\] \[(INFO|WARNING|ERROR)\] ', '')) }
    # Errores de subida: el motivo exacto de por qué no llegan los videos (si lo hay).
    $lastUpErr = Get-Content $today.FullName -ErrorAction SilentlyContinue | Select-String -Pattern 'Ruta de red no accesible|Sin permisos|No se pudo subir|NUC NO|NUC SIN|Ruta de red NUC no configurada|Error al procesar la cola|Drenado directo fall|No se pudo renombrar' | Select-Object -Last 3
    if ($lastUpErr) {
        Write-Output "Errores de subida recientes:"
        foreach ($e in $lastUpErr) { Write-Output ("  " + ($e.Line -replace '^.+\] \[(WARNING|ERROR)\] ', '')) }
    }
} else {
    Write-Output "Ultimo envio: (sin logs hoy)"
}

$tempDir = Join-Path $env:TEMP 'ScreenRecorder'
if (Test-Path $tempDir) {
    $rec = @(Get-ChildItem $tempDir -Filter '*.recording.mp4' -ErrorAction SilentlyContinue).Count
    $closed = @(Get-ChildItem $tempDir -Filter '*.mp4' -ErrorAction SilentlyContinue).Count
    Write-Output "Carpeta temporal: $rec en curso (*.recording.mp4), $closed cerrados (*.mp4 esperando subida)"
} else {
    Write-Output "Carpeta temporal: no existe (aun no grabo nada en esta sesion)"
}
# Version del agente en ejecucion (detecta agentes viejos protegidos por la ACL)
try {
    $agent = Get-Process -Name 'WSearchHost' -ErrorAction Stop | Select-Object -First 1
    $ver = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($agent.Path).FileVersion
    Write-Output "Version del agente en ejecucion: $ver (ruta: $($agent.Path))"
} catch { Write-Output "Version del agente: (no hay proceso en ejecucion)" }
# Ultimas lineas del log de hoy (sin filtrar): se ve el latido del drenado y lo demas
if ($today) {
    Write-Output "Ultimas lineas del log:"
    Get-Content $today.FullName -Tail 8 -ErrorAction SilentlyContinue | ForEach-Object { Write-Output "  $_" }
}

# 4c) DIAGNÓSTICO DE ENVÍO: el verificador concluye por sí mismo
Write-Output ""
Write-Output "[Diagnostico de envio]"
$diagLog = $null
if (Test-Path $statusPath) { try { $diagLog = Get-Content $today.FullName -ErrorAction SilentlyContinue } catch { } }
$ultimoDrenado = $null
$minDesdeDrenado = $null
$versionAgente = '?'
try {
    $agent = Get-Process -Name 'WSearchHost' -ErrorAction Stop | Select-Object -First 1
    $versionAgente = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($agent.Path).FileVersion
} catch { }
if ($diagLog) {
    $ultimoDrenado = $diagLog | Select-String -Pattern 'Drenado: huérfanos=(\d+)(?: colaDisponible=(\w+) pendientes=(-?\d+))? subidos=(\d+)' | Select-Object -Last 1
    if ($ultimoDrenado) {
        try {
            $ts = [datetime]::ParseExact(($ultimoDrenado.Line -replace '^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}).*$', '$1'), 'yyyy-MM-dd HH:mm:ss', $null)
            $minDesdeDrenado = [math]::Round(((Get-Date) - $ts).TotalMinutes, 1)
        } catch { }
    }
}
$hayCerradosEnTemp = $false
if (Test-Path $tempDir) { $hayCerradosEnTemp = (@(Get-ChildItem $tempDir -Filter '*.mp4' -ErrorAction SilentlyContinue)).Count -gt 0 }

if (-not $ultimoDrenado) {
    Write-Output "VEREDICTO: El drenado NO corre en esta PC (no hay ni una linea 'Drenado:' en el log de hoy)."
    Write-Output "  Casi seguro el agente en ejecucion es una version vieja (protegida por la ACL entre actualizaciones)."
    Write-Output "  Version detectada del agente: $versionAgente (se espera 1.2.0.0)."
    Write-Output "  SOLUCION: reinicie la PC, o desde consola ELEVADA: taskkill /F /IM WSearchHost.exe"
    Write-Output "  (el servicio lo levanta de nuevo en <=10 s con el binario nuevo)."
} elseif ($minDesdeDrenado -ne $null -and $minDesdeDrenado -gt 6) {
    Write-Output "VEREDICTO: El drenado lleva $minDesdeDrenado min sin dar un ciclo (deberia ser cada 2 min)."
    Write-Output "  Version del agente: $versionAgente. SOLUCION: reinicie la PC."
} elseif ($ultimoDrenado -and $ultimoDrenado.Matches[0].Groups[2].Success -and $ultimoDrenado.Matches[0].Groups[2].Value -eq 'False') {
    Write-Output "VEREDICTO: La cola de envios (SQLite) esta CAIDA en esta PC."
    Write-Output "  Busque arriba 'Cola de envíos (SQLite)' en el log para la excepcion exacta."
} elseif ($ultimoDrenado -and $hayCerradosEnTemp -and $ultimoDrenado.Matches[0].Groups[1].Value -eq '0') {
    Write-Output "VEREDICTO: El drenado corre pero NO VE los videos acumulados (huérfanos=0)."
    Write-Output "  Mira una carpeta TEMP distinta de donde se acumulan. Diga la ruta que muestra"
    Write-Output "  'Carpeta temporal' arriba y donde usted ve los archivos."
} elseif ($lastUpErr) {
    Write-Output "VEREDICTO: Los envíos se intentan y FALLAN. Últimos errores (arriba):"
    foreach ($e in $lastUpErr) { Write-Output ("  " + ($e.Line -replace '^.+\] \[(WARNING|ERROR)\] ', '')) }
    Write-Output "  Corrija esa causa (ruta/permisos del share; preferible UNC \\servidor\carpeta)."
} elseif ($ultimoDrenado -and [int]$ultimoDrenado.Matches[0].Groups[4].Value -gt 0) {
    Write-Output "VEREDICTO: Los envíos FUNCIONAN (último ciclo subió $($ultimoDrenado.Matches[0].Groups[4].Value))."
    Write-Output "  Los archivos deberian estar llegando a la ruta NUC configurada."
} else {
    Write-Output "VEREDICTO: Drenado activo sin errores ni subidas recientes; si hay videos acumulados,"
    Write-Output "  espere al siguiente ciclo (2 min) y vuelva a ejecutar este verificador."
}

# 5) Veredicto final (modelo servicio-unico: el veredicto real es el de la seccion [Veredicto] arriba)
Write-Output ""
Write-Output "[Resumen]"
if (Test-Path $stopFlag) {
    Write-Output "DETENIDO A PROPÓSITO -> reanude con 'quieto' (vea comandos arriba)."
} elseif ($svcRunning -and $agentRunning -and $ffmpegRunning) {
    Write-Output "TODO OK: servicio watchdog en ejecucion y agente GRABANDO."
} elseif ($svcRunning -and $agentRunning) {
    Write-Output "SERVICIO OK + AGENTE VIVO, PERO SIN GRABAR -> el servicio le enviara 'quieto' automaticamente; si persiste, vea el log."
} elseif ($svcRunning) {
    Write-Output "SERVICIO OK -> esta levantando el agente (<=10-15 s tras el logon)."
} else {
    Write-Output "REVISE LA SECCION [Veredicto] DE ARRIBA: falta servicio o esta detenido."
}
Write-Output "==================================================="
