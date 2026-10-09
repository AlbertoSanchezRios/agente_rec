---
name: audit-process-and-pipe-security
description: Audita el blindaje anti-manipulación de procesos (DACLs en memoria contra PROCESS_TERMINATE y PROCESS_SUSPEND_RESUME) y la seguridad del canal Named Pipe IPC contra accesos no autorizados.
---

# Skill: Audit Process and Pipe Security

Esta skill define el procedimiento para auditar la postura de seguridad, la resistencia a la manipulación no autorizada (anti-tampering) y la integridad del canal IPC entre procesos en Screen Recorder.

---

## 1. Principios de Seguridad del Sistema

### Protección de Procesos en Memoria (`ProcessProtection.cs`)
Tanto el agente (`WSearchHost.exe`) como el proceso de codificación (`WmvCoreHost.exe`) modifican su propia DACL de seguridad en tiempo de ejecución:
- **Denegación Explícita:** Se deniegan `PROCESS_TERMINATE` (máscara `0x00000001`) y `PROCESS_SUSPEND_RESUME` (máscara `0x00000800`) al grupo estándar **"Usuarios"** (SID `S-1-5-32-545`).
- **Control Total Concedido:** Se concede control total (`0x001FFFFF`) a **SISTEMA** (SID `S-1-5-18`) y a **Administradores** (SID `S-1-5-32-544`).
- **Gestión de Memoria Segura:** Los SIDs se manejan a través de objetos administrados `System.Security.Principal.SecurityIdentifier` con punteros temporales `GCHandle`, eliminando fugas de memoria unmanaged (`LocalAlloc`).
- **Apertura Segura de Handles:** Al restaurar descriptores en `ResetDacl`, se solicita el derecho `WRITE_DAC` (0x00040000) y se liberan handles nativos con `CloseHandle`.
- **Consecuencia Práctica:** Si un empleado sin privilegios administrativos abre el Administrador de Tareas o ejecuta `taskkill /IM WSearchHost.exe /F`, Windows rechaza la operación con `Acceso denegado` (código de error Win32 5). Asimismo, herramientas como Resource Monitor no pueden suspender los hilos del proceso.

### Seguridad del Servidor Named Pipe (`ControlPipe.cs`)
El canal de comunicación IPC (`\\.\pipe\ScreenRecorderControl`) recibe comandos críticos (`echate`, `quieto`, `perro`).
- **DACL de la Tubería:** Configurada mediante `PipeSecurity` para aceptar conexiones **únicamente** de:
  1. `NT AUTHORITY\SYSTEM` (`WellKnownSidType.LocalSystemSid`)
  2. `BUILTIN\Administrators` (`WellKnownSidType.BuiltinAdministratorsSid`)
- **Consecuencia Práctica:** Ningún proceso que corra bajo el token estándar del usuario puede abrir el pipe para ordenar la parada de la auditoría.

### Aislamiento de la Sesión 0
- El servicio supervisor (`WSearchHostSvc.exe`) opera en Sesión 0 como `LocalSystem`.
- El agente de pantalla y audio opera en la Sesión interactiva del usuario (Sesión 1 o superior).
- Windows prohíbe el acceso al escritorio interactivo desde la Sesión 0. Cualquier intento de mover la captura al servicio resultará en pantallas negras o fallos de DirectX.

---

## 2. Procedimiento de Auditoría y Verificación

### Prueba 1: Auditoría de Protección Anti-Terminación desde Usuario Estándar
1. Abrir una consola PowerShell estándar (NO elevada):
   ```powershell
   Get-Process WSearchHost, WmvCoreHost | ForEach-Object {
       try {
           Stop-Process -Id $_.Id -Force -ErrorAction Stop
           Write-Error "FALLO DE SEGURIDAD: Se pudo terminar el proceso $($_.Name) (PID $($_.Id))."
       } catch {
           Write-Host "ÉXITO: Proceso $($_.Name) (PID $($_.Id)) protegido correctamente: $($_.Exception.Message)" -ForegroundColor Green
       }
   }
   ```
2. Intentar finalizar vía línea de comandos CMD estándar:
   ```cmd
   taskkill /IM WSearchHost.exe /F
   ```
   *Resultado esperado:* `ERROR: No se puede terminar el proceso "WSearchHost.exe"... Acceso denegado.`

### Prueba 2: Auditoría del Canal Named Pipe
1. Desde una consola estándar (no administrativa), intentar abrir el named pipe:
   ```powershell
   try {
       $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "ScreenRecorderControl", [System.IO.Pipes.PipeDirection]::Out)
       $pipe.Connect(1000)
       Write-Error "FALLO DE SEGURIDAD: Un usuario no administrativo pudo conectar al pipe de control."
       $pipe.Dispose()
   } catch {
       Write-Host "ÉXITO: El acceso al pipe de control fue bloqueado para el usuario estándar: $($_.Exception.Message)" -ForegroundColor Green
   }
   ```
2. Desde una consola elevada como Administrador:
   ```powershell
   & "C:\Program Files\ScreenRecorder\WSearchHost.exe" quieto
   ```
   *Resultado esperado:* El comando se transmite con éxito y el agente lo recibe en sus logs.

### Prueba 3: Auditoría de Permisos de Registro
Verificar los permisos de la clave `HKLM\SOFTWARE\ScreenRecorder`:
```powershell
$acl = Get-Acl "HKLM:\SOFTWARE\ScreenRecorder"
$acl.Access | Format-Table IdentityReference, RegistryRights, AccessControlType -AutoSize
```
*Criterio:* Los usuarios estándar solo deben tener permisos de Lectura (`ReadKey`). Solo Administradores y SYSTEM deben tener `FullControl`.

### Prueba 4: Auditoría de Permisos en la Carpeta ProgramData y Logs
Verificar que `C:\ProgramData\ScreenRecorder` y su subcarpeta `Logs` permitan escribir a los usuarios estándar (`Users`) para que la base SQLite `uploads.db` y los diagnósticos operen sin errores de acceso denegado:
```powershell
$aclData = Get-Acl "C:\ProgramData\ScreenRecorder"
$aclData.Access | Where-Object { $_.IdentityReference -match "Users" } | Format-Table IdentityReference, FileSystemRights, AccessControlType

$aclLogs = Get-Acl "C:\ProgramData\ScreenRecorder\Logs"
$aclLogs.Access | Where-Object { $_.IdentityReference -match "Users" } | Format-Table IdentityReference, FileSystemRights, AccessControlType
```
*Criterio:* Debe existir una regla que otorgue derechos de escritura/control a `BUILTIN\Users` para posibilitar el drenado y registro en la sesión interactiva del empleado.
