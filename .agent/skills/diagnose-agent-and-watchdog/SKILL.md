---
name: diagnose-agent-and-watchdog
description: Diagnostica integralmente el estado del agente interactivo (WSearchHost), el servicio supervisor watchdog (WSearchHostSvc), la tarea programada y la persistencia en el sistema.
---

# Skill: Diagnose Agent and Watchdog

Esta skill proporciona el protocolo técnico para auditar el estado del agente interactivo en sesión de usuario y el servicio supervisor watchdog en `LocalSystem`, detectando caídas, bloqueos de procesos, paradas voluntarias o desconfiguración.

---

## 1. Mapeo de Componentes del Sistema

| Componente | Proceso / Nombre | Contexto de Ejecución | Responsabilidad |
|---|---|---|---|
| **Agente Grabador** | `WSearchHost.exe` | Sesión interactiva del usuario (Sesión 1+) | Captura GPU, audio WASAPI, inyección a FFmpeg |
| **Encoder FFmpeg** | `WmvCoreHost.exe` | Proceso hijo de `WSearchHost.exe` | Codificación de video (NVENC/QSV/AMF) a MP4 |
| **Servicio Watchdog** | `WSearchHostSvc.exe` | Sesión 0 (`LocalSystem`) | Supervisa vida del agente, sube fragmentos huérfanos |
| **Tarea Programada** | `ScreenRecorderTask` | Usuario interactivo | Arranca al iniciar sesión y repite cada minuto si cae |
| **Pipe de Control** | `\\.\pipe\ScreenRecorderControl` | Servidor en `WSearchHost` | Recibe comandos administrativos (`perro`, `quieto`, `echate`) |

---

## 2. Pasos de Diagnóstico Rápido

### Paso 1: Ejecutar Diagnóstico Oficial No Destructivo
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\verify-autostart.ps1
```
O ejecutar el envoltorio:
```cmd
"Verificar Autoarranque.bat"
```

### Paso 2: Verificar la Bandera de Parada Voluntaria
Si el administrador o el sistema emitió el comando `echate`, se habrá creado el archivo de detención:
```powershell
$stopped = Test-Path "C:\ProgramData\ScreenRecorder\stopped"
if ($stopped) {
    Write-Warning "El agente tiene la bandera de parada voluntaria activa en C:\ProgramData\ScreenRecorder\stopped."
} else {
    Write-Host "Bandera de parada libre: el agente tiene permiso para grabar." -ForegroundColor Green
}
```

### Paso 3: Estado de Procesos en Ejecución
Verificar la presencia concurrente del agente y su encoder:
```powershell
Get-Process -Name WSearchHost, WmvCoreHost, WSearchHostSvc -ErrorAction SilentlyContinue |
    Select-Object Id, ProcessName, SessionId, WorkingSet64, StartTime | Format-Table -AutoSize
```
*Criterio de salud:*
- `WSearchHost` debe tener `SessionId > 0` (sesión interactiva).
- `WmvCoreHost` debe tener `SessionId > 0` (hijo del agente).
- `WSearchHostSvc` debe tener `SessionId = 0` (servicio en LocalSystem).

### Paso 4: Estado del Servicio Supervisor
```powershell
Get-Service WSearchHostSvc | Select-Object Name, Status, StartType
```
Si el servicio está detenido y no hay bandera `stopped`:
```powershell
Start-Service WSearchHostSvc
```

### Paso 5: Estado de la Tarea Programada de Autoarranque
```powershell
Get-ScheduledTask -TaskName "ScreenRecorderTask" -ErrorAction SilentlyContinue |
    Select-Object TaskName, State, @{N='Author';E={$_.Principal.UserId}}
```
*Criterio de salud:* El estado debe ser `Ready` o `Running`. Si no existe, registrar con `install-autostart.ps1`.

### Paso 6: Inspección de Logs en Vivo
Consultar las últimas 50 líneas del log más reciente:
```powershell
$latestLog = Get-ChildItem "C:\ProgramData\ScreenRecorder\Logs\*.log" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($latestLog) {
    Write-Host "Mostrando últimas líneas de $($latestLog.FullName):" -ForegroundColor Cyan
    Get-Content $latestLog.FullName -Tail 50
} else {
    Write-Warning "No se encontraron logs en C:\ProgramData\ScreenRecorder\Logs."
}
```

### Paso 7: Comprobación del Canal IPC Named Pipe
Enviar el comando de consulta o reactivación según sea necesario:
- **`perro`**: Abre la ventana de configuración (si se ejecuta interactivamente como admin).
- **`quieto`**: Borra la bandera `stopped` y reinicia el ciclo de grabación en curso.
- **`echate`**: Detiene de forma ordenada la grabación actual y fija la bandera `stopped`.
```powershell
# Ejemplo: enviar 'quieto' para reanudar/reiniciar fragmento
& "C:\Program Files\ScreenRecorder\WSearchHost.exe" quieto
```

---

## 3. Matriz de Síntomas y Resolución

| Síntoma | Causa Frecuente | Acción Correctiva |
|---|---|---|
| `WSearchHost` no arranca al inicio | La tarea programada no está creada o no coincide el usuario | Ejecutar `Instalar Autoarranque.bat` bajo la sesión del usuario real |
| `WmvCoreHost` muere a los 2 segundos | Conflicto en pipeline de video o audio no disponible | Revisar log: buscar `Impossible to convert` o error en dispositivo audio |
| El servicio watchdog marca error de acceso | Servicio intentó interactuar con el escritorio | Invariante violada: el watchdog nunca debe lanzar captura, solo supervisar |
| Tarea programada en `Disabled` | Deshabilitada manualmente por TI o usuario admin | Ejecutar `Enable-ScheduledTask ScreenRecorderTask` |
