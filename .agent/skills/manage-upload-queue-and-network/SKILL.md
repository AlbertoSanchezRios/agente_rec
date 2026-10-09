---
name: manage-upload-queue-and-network
description: Administra la cola duradera de subida (SQLite), la transferencia resiliente de videos MP4 fragmentados hacia el servidor central (NUC), la validación UNC y la recuperación de fragmentos huérfanos.
---

# Skill: Manage Upload Queue and Network Transfers

Esta skill define los protocolos para auditar, gestionar y resolver incidencias en la transferencia de grabaciones desde el equipo local hacia el almacenamiento central en red (NUC / SMB), así como el mantenimiento de la cola duradera SQLite y los fragmentos huérfanos.

---

## 1. Principios de Almacenamiento y Red

### Invariante de Rutas de Red: Rutas UNC Obligatorias
- **Regla:** En la configuración (`NetworkPath`), usar **SIEMPRE rutas UNC** del estilo `\\nuc_servidor\grabaciones\`.
- **Por qué evitar letras mapeadas (`Z:\`):** Windows implementa aislamiento de tokens de UAC. Una unidad de red mapeada en la sesión no elevada del usuario no es visible para procesos que corren con privilegios administrativos o bajo la cuenta `LocalSystem`.

### Formato de Video Fragmentado y Red de Seguridad
- La línea de argumentos de FFmpeg utiliza obligatoriamente:
  ```text
  -movflags frag_keyframe+empty_moov
  ```
- **Ciclo de vida del archivo:**
  1. Durante la captura: el archivo se nombra `[nombre].recording.mp4`.
  2. Al completar el fragmento de forma limpia: se renombra a `[nombre].mp4`.
  3. Si ocurre un corte de luz o apagón brusco: el archivo `.recording.mp4` sigue siendo reproducible hasta el último segundo capturado gracias al contenedor fragmentado. Al volver a encender, el servicio supervisor o el agente lo detecta, lo renombra a `.mp4` y lo sube.
- **Validación de Integridad (`IsValidMp4`):**
  Un archivo solo se transfiere si tiene un tamaño superior a 1024 bytes y los bytes 4 al 7 corresponden a la firma ASCII `ftyp`. Fragmentos corruptos o vacíos se descartan para no ensuciar el almacenamiento central.

### Cola Duradera SQLite (`uploads.db`)
- Ubicación: `C:\ProgramData\ScreenRecorder\uploads.db`.
- Almacena rutas de fragmentos pendientes de subida con control de reintentos exponenciales y modo WAL.
- Si la máquina se desconecta de la red de la empresa (o el usuario sale de viaje), los videos se acumulan localmente y el drenador en caliente los sube automáticamente cada 2 minutos en cuanto se restablece la ruta de red.

### Protocolo de Apagado Rápido de Windows
- Durante un evento de apagado (`SessionEnding`/`WM_ENDSESSION`), Windows concede un presupuesto de tiempo de apenas ~5 segundos.
- Intentar transferir un archivo grande por SMB en ese instante causará que el sistema operativo corte el proceso bruscamente.
- `RequestFastShutdown` finaliza FFmpeg de inmediato, renombra el archivo localmente y **aplaza la subida por red** hasta el siguiente arranque del equipo, donde el servicio supervisor lo transfiere de forma segura.

---

## 2. Procedimientos Operativos

### Paso 1: Comprobar Conectividad y Permisos con la NUC
Ejecutar prueba de escritura remota usando el helper del sistema:
```powershell
$nucPath = (Get-ItemProperty "HKLM:\SOFTWARE\ScreenRecorder" -Name Settings -ErrorAction SilentlyContinue).Settings | ConvertFrom-Json | Select-Object -ExpandProperty NetworkPath

if (-not [string]::IsNullOrWhiteSpace($nucPath)) {
    Write-Host "Comprobando acceso a NUC: $nucPath" -ForegroundColor Cyan
    if (Test-Path $nucPath) {
        $testFile = Join-Path $nucPath ".probe_$([guid]::NewGuid().ToString('N')).tmp"
        try {
            Set-Content -Path $testFile -Value "test" -Force
            Remove-Item -Path $testFile -Force
            Write-Host "ÉXITO: Conectividad y permisos de escritura confirmados en la NUC." -ForegroundColor Green
        } catch {
            Write-Error "ERROR: La ruta NUC es accesible pero NO tiene permisos de escritura: $($_.Exception.Message)"
        }
    } else {
        Write-Warning "AVISO: La ruta NUC no es alcanzable actualmente (posible desconexión de red o VPN inactiva)."
    }
} else {
    Write-Warning "La ruta de red no está configurada en el registro."
}
```

### Paso 2: Inspección de Fragmentos Temporales y Huérfanos
Consultar el estado de la carpeta temporal local:
```powershell
$tempDir = Join-Path $env:TEMP "ScreenRecorder"
if (Test-Path $tempDir) {
    Get-ChildItem $tempDir -File |
        Select-Object Name, Length, LastWriteTime |
        Format-Table -AutoSize
} else {
    Write-Host "Carpeta temporal limpia o no inicializada."
}
```

### Paso 3: Inspeccionar la Cola SQLite de Subidas
```powershell
$dbPath = "C:\ProgramData\ScreenRecorder\uploads.db"
if (Test-Path $dbPath) {
    Write-Host "Base de datos de subidas presente: $([math]::Round((Get-Item $dbPath).Length / 1KB, 2)) KB"
}
```

### Paso 4: Drenado Manual de Huérfanos
Si se requiere forzar la subida de fragmentos acumulados inmediatamente sin reiniciar el equipo:
```powershell
# Enviar 'quieto' por el named pipe para que el agente ejecute el ciclo de drenado
& "C:\Program Files\ScreenRecorder\WSearchHost.exe" quieto
```
O reiniciar el servicio supervisor en Sesión 0:
```powershell
Restart-Service WSearchHostSvc
```

---

## 3. Matriz de Diagnóstico de Red

| Mensaje en Log | Diagnóstico | Solución |
|---|---|---|
| `NUC: NO DISPONIBLE` | Máquina desconectada de LAN o servidor NUC apagado | Verificar conexión de red / Wi-Fi corporativa |
| `NUC: SIN PERMISOS` | Cuenta de equipo o usuario no tiene permisos NTFS en el recurso compartido | Conceder permisos de Modificar/Escritura en el recurso SMB |
| `Fragmento INVÁLIDO descartado` | FFmpeg terminó antes de escribir el primer keyframe | Proceso fue matado prematuramente; validar parámetros de hardware |
| `Drenado: huérfanos=X subidos=Y` | Drenado normal de archivos recuperados tras apagado | Comportamiento esperado y saludable del sistema |
