# Stack de Tecnologías: Screen Recorder

Este documento detalla todas las herramientas, lenguajes, bibliotecas, códecs y APIs de Windows que componen **Screen Recorder**. Se explica la versión utilizada de cada componente y la razón técnica por la que fue seleccionado.

---

## 1. Plataforma de Desarrollo y Runtime

### **C# y .NET 10.0 (`net10.0-windows`)**
El núcleo de la solución ha sido actualizado a la versión más moderna y eficiente de la plataforma de Microsoft:
- **¿Por qué .NET 10?**
  - Ofrece las últimas optimizaciones en el recolector de basura (GC) y asignación cero de memoria (*zero-allocation*), reduciendo drásticamente la frecuencia de pausas en la recolección de memoria mientras se procesan flujos de audio y video.
  - Mejoras de rendimiento en el compilador JIT (Tiered PGO - Profile-Guided Optimization).
  - Soporte de largo plazo (LTS) y total compatibilidad con las APIs de Windows Forms y P/Invoke en Windows 10 y Windows 11.
- **Configuración de Publicación Self-Contained:**  
  La aplicación se compila con el parámetro `--self-contained true` para la arquitectura `win-x64`. Esto empaqueta el motor de ejecución de .NET 10 junto con los binarios, eliminando la necesidad de que los equipos de los empleados tengan instalado previamente ningún runtime.

---

## 2. Motor de Procesamiento Multimedia

### **FFmpeg (Distribuido bajo la identidad discreta `WmvCoreHost.exe`)**
El motor de captura, filtrado y compresión de video es una compilación optimizada **FFmpeg Essentials** de 64 bits para Windows (~105 MB, reduciendo más de 115 MB de huella frente a compilaciones completas):

#### Encoders y Módulos de Captura Utilizados:
1. **Captura de Pantalla DXGI (`ddagrab`):**
   - API de duplicación de escritorio de DirectX Graphics Infrastructure.
   - Lee los cuadros directamente de la memoria VRAM de la tarjeta gráfica.
   - Elimina la necesidad de operaciones GDI lentas (`BitBlt`) y erradica el parpadeo del cursor.
2. **Aceleración por Hardware Intel QuickSync (`h264_qsv`):**
   - Utiliza el silicio dedicado de las CPUs Intel para codificar video H.264 en tiempo real.
   - Registra un consumo mínimo de CPU (~1.5% a 2%).
3. **Aceleración por Hardware NVIDIA NVENC (`h264_nvenc`) y AMD AMF (`h264_amf`):**
   - Soporte automático para tarjetas gráficas dedicadas NVIDIA y AMD.
4. **Encoder por Software de Respaldo (`libx264`):**
   - Preset `ultrafast`, `crf 28`. Proporciona un plan B ultraestable para computadoras sin GPU compatible o en entornos virtualizados (RDP / VDI).
5. **Captura por Software de Respaldo (`gdigrab`):**
   - Respaldo para escenarios multimonitor combinados o donde DXGI no está disponible.
6. **Multiplexor de Archivos MP4 Fragmentados (`-movflags frag_keyframe+empty_moov`):**
   - Escribe átomos de índice periódicos en el archivo MP4.
   - Garantiza que los videos no se corrompan ante apagados bruscos o fallos de corriente.

---

## 3. Subsistema de Captura de Audio

Para capturar simultáneamente el sonido del sistema operativo y la voz del empleado sin colisiones:

### 1. **Windows Audio Session API (WASAPI) en C#**
- Implementado en `WasapiLoopbackCapture.cs`.
- Interactúa directamente mediante COM P/Invoke con las interfaces del subsistema de audio de Windows:
  - `IMMDeviceEnumerator` (enumeración de tarjetas de sonido por defecto).
  - `IAudioClient` (inicialización en modo bucle de retorno o loopback).
  - `IAudioCaptureClient` (lectura de buffers PCM sin latencia).
- **Optimización Extrema de Rendimiento:**
  - Conversión de muestras float IEEE a PCM 16-bit mediante punteros nativos `unsafe` (`float*` a `short*`), eliminando llamadas de marshalling (`Marshal.PtrToStructure`).
  - Uso de `ArrayPool<byte>.Shared.Rent/Return` para el buffer de intercambio, eliminando alocaciones recurrentes en el montículo del GC.
  - Liberación determinista de objetos COM con `Marshal.ReleaseComObject` en el descarte para evitar retenciones de recursos del subsistema de audio.
- El sonido se entrega en formato PCM estéreo de 16 bits a 48.000 Hz directamente hacia el `stdin` de FFmpeg (`pipe:0`).
- **Ventaja decisiva:** No requiere habilitar "Mezcla estéreo" ni instalar drivers de audio virtuales como Virtual Audio Cable.

### 2. **DirectShow (dshow)**
- Módulo nativo de Windows utilizado por FFmpeg para consultar y abrir dispositivos de micrófono físico (especialmente auriculares USB y micrófonos integrados).

### 3. **Generador de Audio Silencioso (`anullsrc`)**
- Filtro interno de FFmpeg que sintetiza una señal de audio muda si la máquina carece de micrófonos físicos, asegurando que el contenedor MP4 final contenga siempre dos canales de audio válidos.

---

## 4. Persistencia, Base de Datos y Serialización

### **SQLite (`Microsoft.Data.Sqlite`)**
- **Uso:** Gestiona la cola duradera de envíos de video hacia la red (`C:\ProgramData\ScreenRecorder\uploads.db`).
- **Ventajas:**
  - Base de datos relacional transaccional embebida en un único archivo.
  - Soporta transacciones atómicas seguras contra fallos (ACID).
  - Configurado en modo **WAL (Write-Ahead Logging)** con:
    ```sql
    PRAGMA journal_mode = WAL;
    PRAGMA synchronous = NORMAL;
    PRAGMA busy_timeout = 30000;
    ```
    Permite lecturas concurrentes sin bloqueo por escrituras y elimina los conflictos de concurrencia entre el agente de usuario y el servicio supervisor.

### **Registro de Windows (`Microsoft.Win32.Registry`)**
- **Uso:** Almacén central de configuración en `HKLM\SOFTWARE\ScreenRecorder`.
- **Ventajas:** Integrado en el sistema operativo, resistente a borrados accidentales de archivos por parte de usuarios y protegido por las listas de control de acceso de Windows.

### **System.Text.Json**
- Serialización y deserialización nativa de alto rendimiento de .NET 10 para `RecordingSettings`, con validación estricta de esquemas y sin dependencias de paquetes de terceros.

---

## 5. Integración con el Sistema Operativo y Win32 P/Invoke

El sistema utiliza varias bibliotecas dinámicas del sistema operativo (DLLs de Windows) para ejecutar operaciones de bajo nivel sin depender de Windows Forms en el núcleo ni en el servicio:

| Biblioteca Win32 | Funciones Utilizadas | Propósito en Screen Recorder |
|---|---|---|
| **`wtsapi32.dll`** | `WTSQueryUserToken`, `WTSGetActiveConsoleSessionId`, `WTSFreeMemory` | Permite al servicio SYSTEM obtener el token de seguridad de la sesión activa de escritorio para lanzar el agente interactivo. |
| **`advapi32.dll`** | `CreateProcessAsUser`, `SetKernelObjectSecurity`, `GetKernelObjectSecurity` | Crea procesos en el escritorio del usuario y modifica las listas de control de acceso (DACL) para impedir que el usuario finalice el proceso. |
| **`user32.dll`** | `EnumDisplayMonitors`, `GetMonitorInfo`, `EnumDisplaySettings`, `GetSystemMetrics` | Enumeración nativa y cálculo geométrico de monitores físicos conectados sin requerir el runtime de Windows Forms. |
| **`shcore.dll`** | `GetDpiForMonitor` | Determina el factor de escala real (DPI) de cada pantalla para calcular coordenadas de captura sin recortes. |
| **`System.ServiceProcess`** | `ServiceBase`, `ServiceController` | Implementación nativa del servicio supervisor de Windows (`WSearchHostSvc`). |

---

## 6. Empaquetado y Distribución Empresarial

### **WiX Toolset 4.0.6**
El instalador MSI se genera con WiX Toolset 4 produciendo un paquete optimizado de ~63 MB:
- Empaquetado estructurado mediante el archivo de manifiesto `Package.wxs`.
- Inclusión dinámica de componentes harvesteados mediante `GeneratedComponents.wxi`.
- Uso de extensiones oficiales:
  - `WixToolset.UI.wixext`: Asistente gráfico de bienvenida y licencia.
  - `WixToolset.Util.wixext`: Creación segura de carpetas compartidas y asignación de permisos `GenericAll` a usuarios locales (`AppDataComponents`) en `ProgramData\ScreenRecorder` y `Logs`, permitiendo la creación y escritura de `uploads.db` y archivos de log desde la sesión interactiva no elevada.
- Acciones personalizadas diferidas (`CustomAction`) que garantizan la actualización limpia de binarios sin bloqueos de archivos en uso.

---

## 7. Automatización y Herramientas de Soporte

- **PowerShell 7 / Windows PowerShell 5.1:**  
  Scripts automatizados para el ciclo de vida del software:
  - `build-msi.ps1`: Automatización del pipeline de publicación, cosecha WiX y compilación MSI.
  - `install-autostart.ps1`: Registro del servicio supervisor y configuración del entorno.
  - `verify-autostart.ps1`: Diagnóstico integral de procesos, servicios, red y logs.
  - `VER_CONFIGURACION.ps1`: Extracción y formateo de la configuración desde HKLM.
- **Archivos de Comandos por Lotes (.BAT):**  
  Lanzadores de doble clic con solicitud automática de privilegios administrativos para el personal técnico en campo.
