# Arquitectura del Sistema: Screen Recorder

Bienvenido a la documentación de arquitectura de **Screen Recorder**. Este documento explica cómo está construido el sistema por dentro, por qué se tomaron las decisiones que se tomaron y cómo interactúan las distintas piezas para lograr una grabación de pantalla y audio corporativa robusta, desatendida y resistente a fallos.

---

## 1. El Dilema Fundamental: Por Qué el Sistema Está Dividido

A primera vista, uno podría pensar: *"¿Por qué no hacer simplemente un Servicio de Windows que grabe la pantalla?"* o bien *"¿Por qué no hacer una simple aplicación de escritorio en la carpeta de Inicio?"*.

La respuesta está en la propia arquitectura de seguridad de Windows:

1. **El Aislamiento de la Sesión 0 (Session 0 Isolation):**  
   Desde Windows Vista en adelante, los Servicios de Windows corren en la "Sesión 0", un entorno aislado donde no hay escritorio interactivo visible ni acceso al subsistema gráfico de los usuarios (GDI/DirectX). Un servicio ejecutado como `LocalSystem` no puede capturar la pantalla del usuario que está trabajando porque, literalmente, no tiene acceso a su búfer de video.
2. **La Vulnerabilidad de la Sesión de Usuario:**  
   Una aplicación tradicional que corre en la sesión del usuario sí puede ver la pantalla y capturar el audio, pero está a merced del usuario: cualquier operador podría abrir el Administrador de Tareas, pulsar "Finalizar tarea" y detener la auditoría sin que nadie se entere. Además, si el proceso cae por cualquier fallo inesperado, nadie lo vuelve a levantar.

### La Solución Arquitectónica: Modelo Dual Supervisor-Agente

Para resolver esta encrucijada, diseñamos una arquitectura en dos capas complementarias:

```
┌────────────────────────────────────────────────────────────────────────┐
│  SESIÓN 0 (Aislada, permisos SYSTEM, sin pantalla)                     │
│                                                                        │
│   ┌──────────────────────────────────────────────────────────────┐     │
│   │ ScreenRecorder.Service (WSearchHostSvc.exe)                  │     │
│   │ - Watchdog permanente (revisa cada 10 s si el agente vive)   │     │
│   │ - Lanzador vía Win32: WTSQueryUserToken + CreateProcessAsUser│     │
│   │ - Uploader de rescate (sube videos huérfanos a la NUC)       │     │
│   │ - Emisor de Heartbeat en red cada 5 minutos                  │     │
│   └──────────────────────────────┬───────────────────────────────┘     │
└──────────────────────────────────┼─────────────────────────────────────┘
                                   │ Supervisa y levanta
                                   ▼
┌────────────────────────────────────────────────────────────────────────┐
│  SESIÓN INTERACTIVA DEL USUARIO (Sesión 1+, pantalla activa, audio)    │
│                                                                        │
│   ┌──────────────────────────────────────────────────────────────┐     │
│   │ ScreenRecorder.UI (WSearchHost.exe) [Modo Oculto]            │     │
│   │ - Application loop Windows Forms (0x0, sin ventana ni icono) │     │
│   │ - Monitoreo de eventos: Login, Bloqueo (Win+L), Apagado      │     │
│   │ - IPC Named Pipe seguro (\\.\pipe\ScreenRecorderControl)     │     │
│   │ - Cola SQLite local de envíos (uploads.db)                   │     │
│   │ - Coordinador de grabación (RecordingManagerService)         │     │
│   └──────────────────────────────┬───────────────────────────────┘     │
│                                  │ Controla stdin/pipes
│                                  ▼
│   ┌──────────────────────────────────────────────────────────────┐     │
│   │ Motor de Captura: FFmpeg (WmvCoreHost.exe)                   │     │
│   │ - Video: Desktop Duplication (ddagrab) + Intel QuickSync QSV │     │
│   │ - Audio: Stream WASAPI Loopback vía stdin + Micrófono dshow  │     │
│   │ - Salida: MP4 fragmentado continuo en %TEMP%\ScreenRecorder  │     │
│   └──────────────────────────────────────────────────────────────┘     │
└────────────────────────────────────────────────────────────────────────┘
```

Esta separación otorga lo mejor de los dos mundos:
- **El agente tiene ojos y oídos:** Corre dentro de la sesión real del usuario, por lo que captura sus pantallas físicas y su audio con máxima fidelidad.
- **El servicio tiene la autoridad:** Corre con privilegios de `LocalSystem`, por lo que un usuario sin privilegios de administrador no puede detenerlo; si el agente cae o lo intentan matar, el servicio lo resucita en menos de 10 segundos.

---

## 2. Mapa de Proyectos y Responsabilidades

La solución `ScreenRecorder.sln` se estructura en cuatro proyectos bien delimitados, bajo **.NET 10.0**:

### 1. `ScreenRecorder.Core` (`src/ScreenRecorder.Core/`)
Es la espina dorsal del sistema. Es una biblioteca de clases compilada para `net10.0-windows` que contiene toda la lógica de negocio, abstracciones de hardware, acceso a disco y algoritmos de recuperación:
- **`Models/`:** 
  - `RecordingSettings.cs`: Configuración de la aplicación (resolución, FPS, bitrate, rutas de red, audio, retención).
  - `CaptureTarget.cs`: Información de monitores y displays físicos detectados.
  - `AudioDevice.cs`: Modelado de fuentes de sonido (sistema, micrófonos USB/DirectShow).
- **`Audio/`:**
  - `WasapiLoopbackCapture.cs`: Captura nativa de audio del sistema en C# mediante las APIs COM de Windows Audio Session API (WASAPI) en modo loopback. Escribe el PCM directamente al flujo de entrada de FFmpeg.
- **`Services/`:**
  - `RecordingService.cs`: Orquestador directo de la invocación de FFmpeg, división en fragmentos de 30 minutos, manejo de procesos hijo y tiempos de guardia.
  - `RecordingManagerService.cs`: Máquina de estados reactiva. Conecta los eventos del sistema operativo (inicio de sesión, bloqueo de pantalla, apagado) con el ciclo de vida de la grabación.
  - `CapturePipelineProbe.cs`: Sondeo inteligente de capacidades de hardware (GPU Intel QuickSync vs tarjeta dedicada vs CPU libx264; `ddagrab` vs `gdigrab`). Cachea el mejor pipeline en disco para arrancar en menos de un segundo en las ejecuciones posteriores.
  - `ScreenGeometryService.cs`: Cálculo matemático de coordenadas físicas de monitores con soporte DPI multimonitor (Per-Monitor v2).
  - `UploadQueueService.cs`: Cola transaccional duradera basada en SQLite (`uploads.db`) con política de reintentos infinitos, disyuntor ante fallos de base de datos y drenado asíncrono.
  - `FileManagementService.cs`: Gestión del almacenamiento temporal local y carga hacia recursos compartidos SMB (la NUC).
  - `ProcessProtection.cs`: Modificación en tiempo de ejecución de las listas de control de acceso (DACL) del proceso para denegar la terminación forzosa por parte de usuarios estándar.
  - `WindowsEventMonitorService.cs`: Suscripción a `Microsoft.Win32.SystemEvents` para reaccionar ante bloqueos, desbloqueos y cierres de sesión.
  - `SettingsService.cs`: Persistencia y lectura de ajustes desde el Registro de Windows (`HKLM\SOFTWARE\ScreenRecorder`).
  - `LoggerService.cs`: Registro cronológico y rotación de eventos en `C:\ProgramData\ScreenRecorder\Logs`.

### 2. `ScreenRecorder.UI` (`src/ScreenRecorder.UI/`)
Es el ejecutable que corre en el escritorio interactivo. Su nombre de ensamblado discreto es **`WSearchHost.exe`**.
- **Punto de entrada (`Program.cs`):** Valida argumentos CLI, gestiona el mutex global de instancia única (`Global\WSearchHostSingleInstance`), arranca el bucle de mensajes de Windows Forms (imprescindible para recibir los mensajes de ventana de `SystemEvents`) y arranca el servidor Named Pipe.
- **IPC y Control (`ControlPipe.cs`):** Servidor Named Pipe que escucha comandos administrativos (`perro`, `quieto`, `echate`).
- **Formularios (`Forms/`):**
  - `MainForm.cs`: Formulario invisible (0x0 píxeles, sin icono en el área de notificación). Mantiene viva la cola de mensajes de Windows sin que el usuario note su presencia.
  - `SettingsForm.cs`: Ventana completa de configuración, accesible únicamente mediante elevación UAC (administrador).
  - `AboutForm.cs`: Ficha técnica de la aplicación.

### 3. `ScreenRecorder.Service` (`src/ScreenRecorder.Service/`)
Servicio nativo de Windows (ensamblado **`WSearchHostSvc.exe`**). Corre bajo la cuenta `NT AUTHORITY\SYSTEM`:
- **`WatchdogService.cs`:**
  - Realiza un ciclo de control cada 10 segundos.
  - Si no detecta una detención voluntaria legítima (bandera `stopped`) y el proceso `WSearchHost.exe` no está corriendo en la sesión del usuario activo, obtiene el token de seguridad de la consola con `WTSQueryUserToken` y lo lanza de forma transparente.
  - Cada 10 minutos, escanea la carpeta `%TEMP%\ScreenRecorder` y sube cualquier fragmento huérfano que haya quedado varado por un corte de energía o fallo previo.
  - Cada 5 minutos, deposita un archivo de latido (*heartbeat*) en la ruta de red central (`\\NUC\heartbeats\<equipo>.log`).

### 4. `ScreenRecorder.Installer` (`src/ScreenRecorder.Installer/`)
Proyecto de empaquetado basado en **WiX Toolset 4**:
- Genera el archivo instalador MSI (`ScreenRecorder-<version>.msi`).
- Publica la aplicación como binario **self-contained** para arquitectura `win-x64` (incluye el runtime de .NET 10 y todas sus dependencias; la máquina destino no requiere tener instalado ningún framework previo).
- Registra el servicio `WSearchHostSvc`, crea carpetas de logs con permisos de escritura adecuados y asegura la puesta en marcha desatendida.

---

## 3. Flujo de Datos y Ciclo de Vida de una Grabación

Para entender cómo viaja la información desde el monitor del usuario hasta el servidor central de almacenamiento (NUC), veamos el recorrido paso a paso:

```
[ Monitor / GPU ] ──(DXGI Desktop Duplication)──┐
                                                 ├─► [ FFmpeg: WmvCoreHost.exe ]
[ Audio Sistema ] ──(WASAPI Loopback -> stdin)───┤         │ (Codifica H.264 + AAC)
                                                 │         ▼
[ Micrófono ]     ──(DirectShow / WASAPI)────────┘   [ %TEMP%\ScreenRecorder\ ]
                                                            │
                                              Archivo *.recording.mp4 (en curso)
                                                            │ (cumple 30 min / evento)
                                                            ▼
                                                     Renombrado a *.mp4
                                                            │
                                                     Encolado en SQLite
                                                            │ (uploads.db)
                                                            ▼
                                              [ FileManagementService ]
                                                            │ (Copia SMB segura)
                                                            ▼
                                              [ Servidor NUC: \\servidor\share ]
                                                            │ (Confirmado OK)
                                                            ▼
                                              Eliminado de disco local
```

### Paso 1: El Disparador
Cuando el usuario inicia sesión en Windows, el servicio supervisor detecta la sesión activa e inicia `WSearchHost.exe /background`. El agente inicializa `RecordingManagerService`, verifica que no exista la bandera de detención `stopped` y comienza el bucle de grabación.

### Paso 2: El Sondeo de Hardware (Pipeline Probe)
Para maximizar el rendimiento y no degradar la máquina del empleado:
1. El sistema comprueba si el procesador o GPU soporta aceleración por hardware Intel QuickSync (`h264_qsv`) y captura de GPU (`ddagrab`). Si está disponible, el uso de CPU desciende a un asombroso ~1.5% a 2%.
2. Si la máquina no tiene GPU compatible o corre en un entorno virtualizado sin aceleración DXGI, conmuta automáticamente a `gdigrab` con codificación CPU `libx264` (preset `ultrafast`).
3. El resultado exitoso se cachea en `C:\ProgramData\ScreenRecorder\pipeline.txt` para que los siguientes fragmentos arranquen al instante.

### Paso 3: Captura de Audio de Dos Vías
- **Audio del Sistema:** En lugar de depender de la antigua y conflictiva "Mezcla estéreo" de la tarjeta de sonido (que en muchos equipos viene deshabilitada por el fabricante), el servicio implementa `WasapiLoopbackCapture`. Esta clase captura directamente los paquetes de sonido que el sistema operativo está enviando a los altavoces o auriculares y los escribe en formato PCM crudo hacia el `stdin` (canal estándar de entrada) de FFmpeg.
- **Audio de Micrófono:** Se detecta dinámicamente mediante DirectShow o WASAPI, dando prioridad a micrófonos USB conectados.
- **Pista de Audio Silenciosa de Respaldo (`anullsrc`):** Si en algún momento no se detecta ningún dispositivo de sonido físico, el sistema inyecta una pista de silencio. Esto garantiza que el contenedor MP4 resultante siempre tenga una pista de audio válida y jamás se desincronice el reproductor de video.

### Paso 4: Generación de Fragmentos Resistentes (Fragmented MP4)
Grabar un video de 8 horas en un solo archivo tradicional es una trampa mortal: los archivos MP4 convencionales escriben el encabezado de índices (`moov atom`) al final. Si se corta la luz tras 7 horas y 59 minutos de grabación, el archivo queda corrupto e inservible.

Por esa razón, Screen Recorder utiliza **MP4 Fragmentado**:
- Opciones de multiplexor: `-movflags frag_keyframe+empty_moov`.
- Cada fragmento dura exactamente **30 minutos**.
- Mientras se graba, el archivo se llama `Nombre_DD-MM-AA-HH-MM-SS.recording.mp4`.
- Si la máquina sufre un pantallazo azul o un corte de energía, cada segundo de video que se alcanzó a escribir en disco **es reproducible de inmediato**.
- Al cumplirse los 30 minutos (o al detener la grabación ordenadamente), el archivo se renombra a su extensión definitiva `.mp4`.

### Paso 5: Cola Persistente SQLite y Subida Segura
Una vez que el archivo `.mp4` está cerrado:
1. Se registra en la base de datos local SQLite (`C:\ProgramData\ScreenRecorder\uploads.db`).
2. El servicio de carga intenta transferir el video hacia la ruta UNC de la NUC (`\\servidor\carpeta`).
3. Si la red corporativa está caída, el archivo permanece seguro en el disco local y la cola programa reintentos automáticos continuos con retroceso exponencial.
4. **Solo cuando la NUC confirma la integridad de la copia**, el archivo temporal local es eliminado para liberar espacio en disco.

---

## 4. Comunicación Inter-Procesos (IPC) y Comandos de Administración

Para evitar que existan dos instancias del grabador peleando por la misma pantalla o dispositivos de audio, el sistema implementa un esquema de control mediante primitivas de sincronización del sistema operativo:

### 1. Mutex de Instancia Única
El proceso interactivo adquiere el Mutex global:
```csharp
Global\WSearchHostSingleInstance
```
Si un segundo proceso `WSearchHost.exe` es invocado (por ejemplo, porque un técnico de TI hizo doble clic sobre el ejecutable), este detecta que el Mutex ya está tomado. En lugar de fallar o lanzar un segundo grabador, pasa a comportarse como un **cliente de control**.

### 2. Named Pipe Seguro
La instancia que está grabando mantiene activo un servidor de tubería con nombre:
```
\\.\pipe\ScreenRecorderControl
```
**Seguridad Crítica:** La lista de control de acceso (DACL) de este pipe está rigurosamente protegida. Únicamente cuentas con privilegios de **Administrador Local** o **SYSTEM** tienen permiso para conectarse a él. Si un usuario sin privilegios intenta escribir en el pipe, Windows rechaza la conexión con `Access Denied`.

### 3. Vocabulario de Comandos de TI
A través de este pipe viajan los comandos de operación remota y mantenimiento:

| Comando | Función | Acción de la instancia principal |
|---|---|---|
| `perro` | Mostrar Configuración | Abre la ventana `SettingsForm`. Al pulsar Guardar, aplica los cambios y reinicia el ciclo de grabación. |
| `quieto` | Reinicio en Caliente | Cierra el FFmpeg actual, finaliza el fragmento y levanta una nueva captura (útil si se cambió de monitor o se desatascó un driver). |
| `echate` | Detención Voluntaria | Crea la bandera de detención `stopped` en `ProgramData`, detiene FFmpeg, sube el fragmento actual y termina el proceso. El watchdog respeta esta bandera y no lo revive. |

---

## 5. Resumen de Decisiones de Arquitectura

| Decisión | Razón de Ingeniería |
|---|---|
| **Separación Servicio / UI** | Supera el aislamiento de Sesión 0; permite capturar pantalla real con la invulnerabilidad de un servicio SYSTEM. |
| **MP4 Fragmentado (`empty_moov`)** | Elimina por completo la pérdida de grabaciones ante cortes eléctricos o apagados forzados. |
| **Cola SQLite (`uploads.db`)** | Los envíos de video sobreviven a caídas de red, reinicios de la máquina y desconexiones de cable. |
| **WASAPI Loopback nativo** | Graba el audio de Windows sin depender de hardware o configuración de "Mezcla estéreo". |
| **DPI Per-Monitor v2** | Evita el error clásico de grabar solo 1/4 de pantalla en monitores con escalado al 125% o 150%. |
| **Self-Contained .NET 10** | Instalación limpia y predecible; no requiere instalar runtimes externos en los equipos de los empleados. |
