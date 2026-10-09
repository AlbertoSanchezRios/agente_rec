# Seguridad y Cumplimiento: Screen Recorder

La grabación de pantalla y audio en un entorno corporativo exige los más altos estándares de control, integridad y cumplimiento normativo. Este documento detalla cómo está blindado **Screen Recorder**, qué mecanismos de protección impiden la manipulación no autorizada y cómo se equilibra la necesidad de auditoría con la privacidad del usuario.

---

## 1. El Modelo de Permisos y Roles de Windows

El sistema opera bajo una estricta jerarquía de privilegios de Windows basada en el principio de mínimo privilegio:

```
┌────────────────────────────────────────────────────────────────────────┐
│ NIVEL 1: NT AUTHORITY\SYSTEM (Servicio WSearchHostSvc)                 │
│ - Máxima autoridad en el sistema operativo local.                      │
│ - Supervisa al agente de grabación e inyecta procesos interactivos.    │
│ - Invulnerable a cierres forzados por cualquier usuario estándar.      │
├────────────────────────────────────────────────────────────────────────┤
│ NIVEL 2: ADMINISTRADORES LOCALES (TI / Soporte Autorizado)             │
│ - Capacidad de modificar ajustes en HKLM\SOFTWARE\ScreenRecorder.      │
│ - Conexión permitida a la tubería IPC (\\.\pipe\ScreenRecorderControl).│
│ - Capacidad de enviar órdenes de detención voluntaria ('echate').      │
├────────────────────────────────────────────────────────────────────────┤
│ NIVEL 3: USUARIOS ESTÁNDAR (Operadores / Empleados Auditados)          │
│ - Ejecutan sus tareas normales en la sesión interactiva.               │
│ - Denegado: Finalizar procesos del grabador (Access Denied).           │
│ - Denegado: Conectar al Named Pipe de control.                         │
│ - Denegado: Modificar o eliminar la configuración en el Registro.      │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Blindaje Anti-Manipulación de Procesos

Un problema común en el software de auditoría es que un usuario experimentado abra el Administrador de Tareas, seleccione el proceso y haga clic en *"Finalizar tarea"*.

Screen Recorder aborda este reto mediante dos capas de protección complementarias:

### Capa 1: Restricción de DACL en Memoria (`ProcessProtection.cs`)
Al arrancar tanto el agente de usuario (`WSearchHost.exe`) como el motor de captura (`WmvCoreHost.exe`), el sistema ejecuta una modificación de los descriptores de seguridad (DACL) del proceso:
- Identifica el token de seguridad del grupo local **Usuarios** (`BuiltinUsersSid`).
- Agrega una entrada de control de acceso explícita de **DENEGACIÓN (Access Denied)** para los siguientes derechos Win32:
  - `PROCESS_TERMINATE` (0x0001): Prohíbe terminar el proceso.
  - `PROCESS_SUSPEND_RESUME` (0x0800): Prohíbe pausar los hilos del proceso.
- **Resultado Práctico:** Si un usuario sin privilegios de administrador intenta finalizar el proceso desde el Administrador de Tareas, Process Explorer o PowerShell, Windows rechaza la solicitud de inmediato mostrando un error de *“Acceso denegado”*.

### Capa 2: El Perro Guardián SYSTEM (Watchdog Service)
¿Qué ocurre si el usuario es administrador local de la máquina?  
Un usuario que posee privilegios de administrador completo cuenta con el privilegio `SeDebugPrivilege` de Windows, lo que técnicamente le permitiría saltarse cualquier DACL. 

Para este escenario, la defensa es el **Servicio Supervisor `WSearchHostSvc`**:
- El servicio corre como `LocalSystem`.
- Monitorea cada 10 segundos la existencia del agente.
- Si el proceso desaparece sin una orden legítima de parada (la bandera `stopped`), el servicio lo **resucita en menos de 10 segundos** en la sesión activa mediante `WTSQueryUserToken` y `CreateProcessAsUser`.
- Además, el intento queda registrado cronológicamente en los logs forenses.

### Evidencia de Manipulación (Tamper-Evidence):
Si alguien llegara a desenchufar el cable de red o apagar el servicio supervisor a la fuerza:
- El servicio central de la empresa (NUC) espera un **archivo de latido (*heartbeat*) cada 5 minutos**.
- La ausencia de latidos alerta de inmediato al departamento de seguridad informática sobre una posible desconexión deliberada de la estación de trabajo.

---

## 3. Seguridad en la Comunicación Inter-Procesos (IPC)

La tubería con nombre (Named Pipe) `\\.\pipe\ScreenRecorderControl` es la única vía para interactuar con el grabador mientras está en ejecución. Para evitar que un usuario no autorizado envíe órdenes falsas:

1. **Lista de Acceso Restringida:**  
   El pipe se inicializa mediante la API de seguridad de .NET con un `PipeSecurity` restrictivo:
   ```csharp
   var pipeSecurity = new PipeSecurity();
   // Solo administradores y la cuenta del sistema pueden leer o escribir:
   pipeSecurity.AddAccessRule(new PipeAccessRule(
       new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
       PipeAccessRights.ReadWrite, AccessControlType.Allow));
   pipeSecurity.AddAccessRule(new PipeAccessRule(
       new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
       PipeAccessRights.ReadWrite, AccessControlType.Allow));
   ```
2. **Rechazo de Comandos:**  
   Si un usuario estándar intenta escribir un comando `echate` hacia el pipe para detener la grabación, la conexión es abortada a nivel de kernel por Windows.

---

## 4. Protección y Validación en Tres Capas de la Configuración

Para evitar que un usuario manipule los ajustes (como cambiar la resolución a 1 FPS o desactivar el micrófono):

1. **Capa de Almacenamiento (HKLM):**  
   Los ajustes residen en `HKEY_LOCAL_MACHINE\SOFTWARE\ScreenRecorder`. Por diseño del sistema operativo, las ramas HKLM solo pueden ser escritas por procesos elevados (UAC).
2. **Capa de Entrada (Punto de Partida):**  
   Al solicitar la apertura de la ventana de configuración, la aplicación comprueba `IsRunningAsAdministrator()`. Si el proceso no está elevado, solicita la aprobación de credenciales administrativas de Windows mediante UAC. Si el usuario cancela, la ventana jamás se muestra.
3. **Capa de Inicialización y Guardado:**  
   Dentro de `SettingsForm.cs`, tanto el constructor como el evento de clic en el botón *Guardar* vuelven a validar de manera independiente que el hilo tenga privilegios de administrador antes de escribir un solo byte en el Registro.

---

## 5. Seguridad en el Manejo de Grabaciones y Privacidad

### Protección de Archivos Locales en Tránsito:
- Los fragmentos de video se guardan temporalmente en directorios del sistema protegidos (`%TEMP%` o `ProgramData`).
- Solo tienen acceso de lectura la cuenta que inició sesión y los administradores locales.

### Transferencia Segura hacia la NUC:
- El envío de archivos hacia el servidor central se realiza mediante el protocolo estándar SMB (bloque de mensajes del servidor) de Windows, beneficiándose de:
  - Autenticación integrada de dominio de Windows (Kerberos / NTLMv2).
  - Cifrado en tránsito de SMB 3.0+ si está habilitado en el servidor corporativo.
- **Borrado Seguro tras Confirmación:** El video local se borra únicamente cuando el archivo ha completado su transferencia a la NUC y se ha verificado su tamaño exacto.

### Consideraciones de Privacidad y Cumplimiento Normativo:
- **Respeto a las Pausas del Empleado:** Cuando el trabajador bloquea la estación (`Win + L`) para salir a su descanso o almuerzo, la grabación se detiene automáticamente. No se capturan sesiones vacías ni se monitoriza la pantalla durante ausencias justificadas.
- **Propiedad Corporativa Exclusiva:** Este software está concebido exclusivamente para estaciones de trabajo propiedad de la empresa, bajo las políticas internas de uso de activos tecnológicos y de conformidad con la legislación laboral aplicable.
