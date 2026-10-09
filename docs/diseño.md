# Diseño del Sistema: Screen Recorder

Este documento profundiza en las decisiones de diseño técnico, los patrones de arquitectura y las consideraciones de ingeniería que dan forma a **Screen Recorder**. Se explica la razón técnica detrás de cada elección, los problemas que resolvieron y cómo se estructuraron las capas para garantizar un software predecible, ligero y mantenible en el tiempo.

---

## 1. Filosofía de Diseño: Las Cuatro Reglas de Oro

Durante el desarrollo de Screen Recorder se establecieron cuatro principios rectores no negociables:

1. **Invisibilidad Absoluta:** La herramienta no debe manifestarse visualmente en la rutina del usuario. Cualquier ventana accidental, recuadro de aviso o parpadeo se considera un defecto de diseño.
2. **Impacto Mínimo en Recursos:** Las estaciones de trabajo de los empleados son para trabajar; el grabador no puede ralentizar hojas de cálculo de Excel, videoconferencias ni navegación web.
3. **Persistencia y Resiliencia Extrema:** Todo video grabado debe llegar a la red, sin importar si hay cortes eléctricos, caídas de WiFi o apagados forzados de Windows.
4. **Seguridad y Control Restringido:** Solo el personal técnico autorizado (TI) debe tener la capacidad de configurar, pausar o desinstalar la herramienta.

---

## 2. Optimización de Memoria y Rendimiento en .NET 10

Dado que tanto el agente interactivo (`WSearchHost.exe`) como el servicio de supervisión (`WSearchHostSvc.exe`) corren continuamente, el consumo de memoria RAM era una métrica crítica. En los archivos de proyecto `.csproj` se implementaron directivas de recorte que reducen sustancialmente la huella en memoria:

```xml
<!-- Recorte de memoria: sin tablas ICU ni anclaje de depurador -->
<InvariantGlobalization>true</InvariantGlobalization>
<DebuggerSupport>false</DebuggerSupport>
```

### ¿Por qué estas directivas?
- **`InvariantGlobalization=true`:** Por defecto, el runtime de .NET carga en memoria las bibliotecas ICU (International Components for Unicode) para manejar formateo cultural de fechas y monedas. Dado que Screen Recorder opera con formatos técnicos estandarizados ISO y strings fijos, desactivar ICU **ahorra de 15 a 20 MB de memoria RAM por cada proceso**.
- **`DebuggerSupport=false`:** Remueve la infraestructura interna de depuración del runtime, reduciendo el tamaño del binario y acelerando el tiempo de compilación JIT inicial.
- **Prioridad de Proceso `BelowNormal` en FFmpeg:**  
  Al arrancar el proceso de grabación, el código ejecuta de inmediato:
  ```csharp
  _currentRecordingProcess.PriorityClass = ProcessPriorityClass.BelowNormal;
  ```
  Esto garantiza que el programador de tareas del núcleo de Windows priorice siempre las aplicaciones con las que el empleado está interactuando, eliminando cualquier sensación de "lag" en el ratón o lentitud en el teclado.

---

## 3. El Bucle de Mensajes Oculto (Windows Forms Silencioso)

Una pregunta habitual para un desarrollador que revisa el repositorio es:  
*“Si la aplicación no muestra ninguna ventana en pantalla, ¿por qué el proyecto UI referencia Windows Forms y ejecuta `Application.Run()`?”*

### La Razón Técnica: Despacho de Eventos de Windows
El servicio necesita reaccionar inmediatamente cuando el usuario bloquea el equipo (`Win + L`), cierra sesión o apaga la máquina. En el ecosistema de Windows, estos eventos son notificados por el sistema operativo mediante mensajes de ventana Win32 (`WM_WTSSESSION_CHANGE`, `WM_QUERYENDSESSION`, `WM_ENDSESSION`).

La clase de .NET `Microsoft.Win32.SystemEvents` **requiere obligatoriamente una cola de mensajes activa** (un hilo STA con un *message pump*). Si se hubiera implementado como una simple aplicación de consola en bucle (`while(true)`), los eventos de bloqueo y apagado no se recibirían a tiempo o fallarían.

### La Solución Implementada:
`MainForm.cs` se crea con dimensiones de 0x0 píxeles, visibilidad desactivada y sin asociar ningún icono en la bandeja del sistema (`NotifyIcon`). De este modo:
- Se mantiene vivo el hilo de mensajes que alimenta a `SystemEvents`.
- La aplicación consume prácticamente 0% de CPU en reposo.
- Para el usuario es completamente indetectable.

---

## 4. Comunicación Inter-Procesos (IPC) y Seguridad de Tuberías

La aplicación debe permitir que un administrador de TI que se conecta a la máquina pueda abrir la configuración o reiniciar la grabación sin tener que detener procesos bruscamente. Para lograrlo, se diseñó un canal IPC ligero y seguro en `ControlPipe.cs`:

```
[ Administrador / Script de TI ]
              │
              │  Ejecuta: WSearchHost.exe perro (elevado con UAC)
              ▼
[ Instancia Cliente Efímera ]
              │  1. Comprueba si Global\WSearchHostSingleInstance está ocupado.
              │  2. Se conecta al Named Pipe local.
              │  3. Envía el comando en texto plano.
              ▼
    ╔══════════════════════════════════════════════════════╗
    ║ Named Pipe: \\.\pipe\ScreenRecorderControl           ║
    ║ DACL: Solo administradores locales y cuenta SYSTEM   ║
    ╚══════════════════════════════════════════════════════╝
              ▲
              │  Recibe: "perro"
              │
[ Instancia Principal de Grabación ]
              │  Abre SettingsForm en la sesión activa.
```

### Protección contra Manipulación en el Named Pipe
Un fallo común en aplicaciones que usan Named Pipes es dejarlos abiertos con permisos para `Everyone`. Un usuario sin privilegios podría enviar un comando de apagado y eludir la auditoría. 

En Screen Recorder, el Named Pipe se crea con una lista de control de acceso (DACL) explícita:
- Concede acceso total a `Administrators` y `LocalSystem`.
- Deniega la conexión a usuarios estándar.
- Si un usuario no administrador intenta conectarse, la API de Windows devuelve inmediatamente `ERROR_ACCESS_DENIED`.

---

## 5. Geometría de Pantalla y Soporte Multimonitor Consciente de DPI

Uno de los problemas más complejos en la captura de escritorio en Windows es el escalado de pantalla (DPI). Con la llegada de monitores 2K y 4K, es muy común que Windows aplique un escalado del 125%, 150% o 200%.

### El Error Común: Coordenadas Lógicas vs. Físicas
Si una aplicación no está configurada como DPI-Aware, Windows virtualiza las coordenadas. Una pantalla de 1920x1080 al 150% se reporta a la aplicación como 1280x720. Si luego se pasa ese valor a FFmpeg, este captura únicamente un recorte de 1280x720 (la esquina superior izquierda de la pantalla), dejando el resto fuera de cuadro.

### Solución Diseñada en `ScreenGeometryService.cs`:
1. La aplicación activa el modo **`HighDpiMode.PerMonitorV2`** en `Program.cs`.
2. Al calcular el área de captura, se combinan las dimensiones de `Screen.AllScreens` con la llamada Win32 a `GetDpiForMonitor`.
3. Se calculan las coordenadas y resolución en **píxeles físicos reales**.
4. En lugar de recortar el video con `-video_size`, el comando de FFmpeg toma el área completa y la escala limpiamente con el filtro `scale`:
   ```bash
   -vf "scale=-2:1080"
   ```
   Esto garantiza que el video final tenga exactamente la altura deseada (1080p, 720p, etc.) preservando la relación de aspecto original sin recortes extraños.

---

## 6. Diseño de la Cola Persistente SQLite (`UploadQueueService`)

La subida de archivos de video a la NUC requería un mecanismo transaccional que no dependiera de la memoria volátil del proceso:

```
[ Fragmento finalizado: video.mp4 ]
                  │
                  ▼
┌────────────────────────────────────────────────────────┐
│ SQLite: ProgramData\ScreenRecorder\uploads.db          │
│ ────────────────────────────────────────────────────── │
│ Columnas:                                              │
│ - Id (INTEGER PRIMARY KEY)                             │
│ - FilePath (TEXT UNIQUE)                               │
│ - CreatedAt (DATETIME)                                 │
│ - Attempts (INTEGER)                                   │
│ - LastAttempt (DATETIME)                               │
│ - NextAttempt (DATETIME)                               │
└────────────────────────────────────────────────────────┘
                  │
                  ├── Intento de subida a la NUC
                  ├── ¿Éxito? ──► Elimina registro de SQLite y borra archivo local.
                  └── ¿Fallo? ──► Incrementa Attempts, calcula backoff exponencial.
```

### Características de Robustez en la Base de Datos:
- **Concurrencia entre Procesos:** Tanto el agente de usuario como el servicio supervisor pueden interactuar con la base de datos. Para evitar errores de bloqueo (`SQLITE_BUSY`), la cadena de conexión implementa:
  ```csharp
  "Data Source=uploads.db;Default Timeout=30;Cache=Shared"
  ```
- **Disyuntor Integrado (Circuit Breaker):** Si la base de datos sufre 3 fallos consecutivos (por ejemplo, si el archivo SQLite sufre corrupción en disco por una falla de sectores), el sistema conmuta automáticamente al **Modo Directo**. En modo directo, el servicio omite la base de datos y escanea los archivos directamente en el disco para subirlos, asegurando que ningún video se quede atrapado.

---

## 7. Estructura de Persistencia en el Registro

La configuración del grabador se almacena de forma centralizada por máquina en la clave del Registro:
```
HKEY_LOCAL_MACHINE\SOFTWARE\ScreenRecorder
```

### Estructura del JSON Persistido (`Settings`):
```json
{
  "Resolution": 1080,
  "Fps": 30,
  "FragmentDurationMinutes": 30,
  "NetworkPath": "\\\\servidor-nuc\\auditoria\\grabaciones",
  "AutoDetectAudio": true,
  "IncludeSystemAudio": true,
  "IncludeMicrophone": true,
  "SelectedMonitor": 0,
  "CaptureMode": "GPU",
  "ShowPopups": false,
  "FileNameFormat": "UserAndMachine"
}
```

### Razones de Diseño:
- Al residir en `HKEY_LOCAL_MACHINE` (HKLM), la configuración es compartida por cualquier usuario que inicie sesión en esa computadora física.
- Requiere permisos de Administrador para ser modificada, impidiendo que usuarios estándar alteren la resolución, desactiven el micrófono o cambien la ruta de envío.
