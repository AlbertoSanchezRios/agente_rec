# AGENTS.md: Directrices para Agentes de IA y Desarrolladores

Bienvenido al manual operativo para **Agentes de Inteligencia Artificial** y desarrolladores que interactúen con el repositorio **Screen Recorder** (`agente_rec`).

Este documento define la verdad arquitectónica, las restricciones inviolables del sistema, los patrones de diseño establecidos y las precauciones que debes considerar antes de modificar cualquier línea de código.

---

## 1. Misión y Visión General del Proyecto

- **Nombre del Producto:** Screen Recorder (Auditoría Corporativa).
- **Stack Base:** C# / .NET 10.0 (`net10.0-windows`), Win32 P/Invoke, FFmpeg, SQLite, WiX Toolset 4.
- **Propósito:** Captura continua, transparente y desatendida de video de pantalla y audio (sistema + micrófono) en computadoras corporativas de la empresa, subiendo los archivos fragmentados a un almacenamiento central en red (NUC).
- **Público Objetivo:** Operación silenciosa en puestos de trabajo de empleados. Mantenimiento y control restringidos exclusivamente al personal técnico de TI.

---

## 2. Invariantes Arquitectónicas (Reglas Inviolables)

Cualquier cambio que propongas o implementes **debe respetar estrictamente estas 7 reglas**:

### Regla 1: El Desacoplamiento Sesión 0 vs. Sesión Interactiva
- **NUNCA intentes mover la captura de pantalla al servicio de Windows.** Por diseño de seguridad de Windows (Aislamiento de la Sesión 0), un servicio `LocalSystem` no tiene acceso al escritorio de usuario.
- **El Servicio (`WSearchHostSvc.exe`):** Es únicamente un supervisor externo (watchdog) y subidor de rescate de videos huérfanos. Corre completamente desacoplado de Windows Forms (cero DLLs de WinForms en su publicación self-contained), utilizando APIs Win32 P/Invoke puras para cualquier cálculo de pantalla.
- **El Agente (`WSearchAgent.exe`):** Corre en la sesión activa del usuario y es el único responsable de interactuar con la GPU, la pantalla y los dispositivos de audio.

### Regla 2: Operación Oculta y Discreción de Procesos
- **Cero Interfaces Visibles en Ejecución Normal:** No agregues iconos en la bandeja del sistema (`NotifyIcon`), ventanas emergentes ni alertas al usuario estándar.
- **El agente NO usa Windows Forms:** `WSearchAgent.exe` corre sin WinForms. `Microsoft.Win32.SystemEvents` (paquete propio) levanta su **propia ventana oculta y bomba de mensajes** en un hilo dedicado, por lo que no se necesita `Application.Run()` ni un formulario 0x0 para recibir bloqueo/apagado. La interfaz de ajustes vive en un proceso aparte (`WSearchCfg.exe`), elevado y solo bajo demanda.
- **Nombres de Proceso:** Mantén los nombres discretos de los binarios:
  - `WSearchAgent.exe` (Agente interactivo, sin WinForms)
  - `WSearchHostSvc.exe` (Servicio supervisor SYSTEM)
  - `WmvCoreHost.exe` (FFmpeg de captura)
  - `WCodecHost.exe` (FFmpeg de conversión/verificación MKV→MP4)
  - `WSearchCfg.exe` (UI de configuración, elevada, on-demand)
  - `Wctl.exe` (CLI de control, on-demand)

### Regla 3: Blindaje Anti-Manipulación de Procesos
- En `Program.cs` y al iniciar FFmpeg, se aplica la modificación de DACL en memoria (`ProcessProtection.DenyTerminateForUsers`).
- **NUNCA elimines ni debilites esta protección:** Deniega explícitamente `PROCESS_TERMINATE` (0x0001) y `PROCESS_SUSPEND_RESUME` (0x0800) al grupo de usuarios estándar de Windows, impidiendo que el empleado mate o suspenda la grabación desde el Administrador de Tareas o Resource Monitor.

### Regla 4: Canal IPC Seguro por Named Pipe
- El servidor de tubería con nombre (`\\.\pipe\WctlControl`) debe tener siempre una DACL que permita la conexión **únicamente a Administradores Locales y a la cuenta SYSTEM**.
- Un usuario no administrativo jamás debe tener acceso al pipe, pues podría enviar el comando `stop` para detener la auditoría.
- El protocolo es JSON de petición/respuesta (cliente `Wctl.exe`); se mantienen los comandos legacy `perro`/`quieto`/`echate` como alias.

### Regla 5: Grabación en MKV y Conversión Verificada a MP4
- La captura se escribe en contenedor **MKV** (`-f matroska`), robusto ante cortes eléctricos y terminaciones abruptas (no requiere índice al final).
- El archivo en curso se nombra temporalmente con la extensión `*.recording.mkv` y se renombra a `*.mkv` solo al cerrarse de forma limpia.
- Cada fragmento MKV se convierte a MP4 mediante el subproceso `WCodecHost.exe` (re-encode con el encoder de hardware disponible; `libx264 ultrafast` como respaldo) y **se verifica que el MP4 resultante sea reproducible** (pasada de decode `-v error -xerror -i salida.mp4 -f null -`) antes de encolarlo para subir. El MKV local solo se borra tras subir el MP4 verificado.
- **Por qué:** el MKV nunca queda corrupto por un apagón; la conversión + verificación garantizan que la NUC reciba siempre MP4 reproducibles, incluso reparando fragmentos truncados (`-err_detect ignore_err -fflags +genpts`).
- La conversión se ejecuta a baja prioridad y nunca compite en primer plano con la captura.

### Regla 6: Pipeline de Audio Híbrido y Red de Seguridad
- El audio del sistema se captura en C# mediante `WasapiLoopbackCapture.cs` (usando punteros `unsafe`, `ArrayPool<byte>.Shared` y liberación COM determinista) y se inyecta por el `stdin` de FFmpeg (`pipe:0`). No intentes reemplazar esto por "Mezcla estéreo" de DirectShow (frecuentemente ausente o deshabilitada en hardware corporativo).
- Si no hay micrófonos presentes, se debe conservar el filtro `anullsrc` para garantizar que el archivo MP4 siempre tenga pista de audio válida.

### Regla 7: Almacenamiento y Rutas de Red
- En configuraciones para la NUC, promueve siempre el uso de **rutas UNC** (`\\servidor\carpeta`) y desaconseja el uso de letras de unidad mapeadas (como `Z:\`). Debido al aislamiento de tokens de UAC en Windows, los procesos administrativos no ven las unidades mapeadas del usuario.
- La copia hacia la NUC se realiza de forma atómica a un archivo temporal único (`<destino>.<guid>.tmp`), verificando la igualdad exacta de tamaño de bytes (`destInfo.Length == srcInfo.Length`) y renombrando atómicamente a `.mp4` antes de borrar el archivo local. La cola coordina transferencias concurrentes mediante reservas de leases atómicos (`ClaimPending`/`TryClaim` con estado `en_curso`), evitando colisiones de envío entre hilos y procesos.

---

## 3. Estructura de Carpetas del Proyecto

```
D:\dev\agente_rec\
├── docs/                             # Documentación exhaustiva en tono humano
│   ├── arquitectura.md               # Modelo supervisor/agente y flujos
│   ├── caracteristicas.md            # Especificaciones funcionales detalladas
│   ├── diseño.md                     # Patrones, IPC, DPI y recorte de memoria
│   ├── operacion.md                  # Compilación, MSI, despliegue y CLI de TI
│   ├── mantenimiento.md              # Diagnóstico, recuperación y troubleshooting
│   ├── seguridad.md                  # DACLs, permisos HKLM, aislamiento Sesión 0
│   └── tecnologias.md                # .NET 10, FFmpeg, SQLite, APIs Win32
├── src/
│   ├── ScreenRecorder.Core/          # Lógica compartida, audio, modelos, servicios, IPC
│   ├── ScreenRecorder.Agent/         # WSearchAgent.exe (agente interactivo, SIN WinForms)
│   ├── ScreenRecorder.UI/            # WSearchCfg.exe (UI de configuración elevada, on-demand)
│   ├── ScreenRecorder.Service/       # WSearchHostSvc.exe (servicio supervisor SYSTEM)
│   ├── Wctl/                         # Wctl.exe (CLI de control, on-demand)
│   └── ScreenRecorder.Installer/     # Package.wxs (empaquetado WiX Toolset 4)
├── tools/
│   └── build-ffmpeg.sh               # Build mínima de FFmpeg (MSYS2) para WmvCoreHost/WCodecHost
├── AGENTS.md                         # Este documento de directrices para agentes IA
├── README.md                         # Portal principal del repositorio
├── ScreenRecorder.sln                # Solución Visual Studio / .NET 10
├── .gitignore                        # Exclusiones de control de versiones
├── build-msi.ps1                     # Script de compilación y empaquetado del instalador MSI
├── build-ffmpeg.ps1                  # Compila la FFmpeg a medida con MSYS2 (opcional)
├── publish-fase1.ps1                 # Script de compilación y publicación directa de binarios
├── install-autostart.ps1             # Registro y configuración del servicio watchdog
├── verify-autostart.ps1              # Diagnóstico exhaustivo del estado del sistema
├── uninstall-autostart.ps1 / uninstall.ps1 # Desinstalación y purga completa del sistema
├── VER_CONFIGURACION.ps1             # Consulta de la configuración guardada en HKLM
├── Compilar MSI.bat                  # Wrapper interactivo para compilar y empaquetar MSI
├── Instalar Autoarranque.bat         # Wrapper interactivo para administradores
├── Verificar Autoarranque.bat        # Wrapper de comprobación rápida
└── Desinstalar.bat                   # Wrapper interactivo de desinstalación limpia
```

---

## 4. Guía de Tareas Habituales para Agentes

### Cuando se te pida compilar o empaquetar el proyecto:
- El comando estándar de empaquetado es:
  ```powershell
  powershell -ExecutionPolicy Bypass -File build-msi.ps1
  ```
- **Nota técnica sobre WiX:** En este entorno, el SDK de WiX tiende a descartar los `<Fragment>`. Por esa razón, `Package.wxs` incluye todo dentro del elemento `<Package>` y el build usa el CLI de WiX 4.0.6. No intentes descomponer `Package.wxs` en múltiples fragmentos sin verificar el MSI generado.
- **FFmpeg a medida y respaldo Essentials:** `WmvCoreHost.exe` (captura) y `WCodecHost.exe` (conversión) pueden ser una build mínima propia (~20-35 MB) compilada con `build-ffmpeg.ps1`/`tools/build-ffmpeg.sh` (MSYS2), con solo ddagrab/gdigrab/qsv/nvenc/amf/x264/aac y muxers matroska/mov. Si no existe `tools/ffmpeg-build/ffmpeg.exe`, `build-msi.ps1` descarga el **FFmpeg Essentials** oficial (~95 MB) como respaldo. El instalador resultante ronda los ~63 MB.
- **Permisos de SQLite:** `Package.wxs` y los scripts de instalación conceden permisos explícitos de `GenericAll` a `Users` en `C:\ProgramData\ScreenRecorder` (`AppDataComponents`), asegurando que la base de datos `uploads.db` y sus archivos WAL operen sin denegaciones de acceso bajo cuentas de usuario interactivo estándar.

### Cuando se te pida diagnosticar un problema:
1. Comprueba la presencia de la bandera de parada voluntaria:
   ```powershell
   Test-Path "C:\ProgramData\ScreenRecorder\stopped"
   ```
2. Revisa el estado del servicio supervisor:
   ```powershell
   Get-Service WSearchHostSvc
   ```
3. Lee las últimas líneas del archivo de log más reciente:
   ```powershell
   Get-ChildItem "C:\ProgramData\ScreenRecorder\Logs\*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content -Tail 50
   ```
4. Inspecciona la configuración persistida en el Registro:
   ```powershell
   (Get-ItemProperty "HKLM:\SOFTWARE\ScreenRecorder" -Name Settings).Settings
   ```

### Cuando se te pida añadir una nueva opción de configuración:
1. Agrega la propiedad en `ScreenRecorder.Core/Models/RecordingSettings.cs` con un valor por defecto sensato.
2. Añade el control de interfaz correspondiente en `ScreenRecorder.UI/Forms/SettingsForm.cs` (diseño, enlace de datos al abrir y guardado).
3. Si la opción afecta a la línea de comandos de FFmpeg, modifícala en `ScreenRecorder.Core/Services/RecordingService.cs`.
4. Recuerda que al guardar en `SettingsForm`, la aplicación envía automáticamente `quieto` por el Named Pipe para reiniciar el ciclo y aplicar la nueva configuración sin reiniciar la computadora.

---

## 5. Trampas Conocidas (Pitfalls a Evitar)

1. **No agregues `-pix_fmt yuv420p` en la codificación con Intel QuickSync (`h264_qsv`):**  
   Fuerza una conversión en espacio de color que el encoder QSV de Intel rechaza en varias GPUs, provocando el cierre inmediato de FFmpeg.
2. **No dependas del cierre síncrono de subidas en apagados de Windows:**  
   Windows otorga un presupuesto de apenas ~5 segundos durante el evento de apagado (`SessionEnding`/`WM_ENDSESSION`). Intentar subir un video de varios cientos de megabytes por SMB en ese instante causará que Windows corte el proceso de golpe. La estrategia correcta (ya implementada) es cerrar FFmpeg de inmediato, asegurar el archivo local y dejar que el servicio supervisor lo suba al volver a encender la máquina.
3. **No cierres el agente con `Environment.Exit()` si hay un fragmento abierto:**  
   Usa la secuencia de parada ordenada de `RecordingManagerService` (`StopRecordingAsync`) para que FFmpeg cierre el contenedor MKV antes de salir. `Wctl -stop`/`echate` ya lo hace así (detiene y luego sale).

---

## 6. Política Obligatoria de Commits: Commits Atómicos y Convencionales

Todos los commits en este repositorio **deben ser estrictamente atómicos** y seguir la especificación **Conventional Commits**.

### Reglas Inviolables de Control de Versiones:
1. **Atomicidad Absoluta:**
   - Un commit debe representar **un único cambio lógico, indivisible y coherente**.
   - **NUNCA mezcles:**
     - Nuevas características con refactorizaciones.
     - Cambios en lógica de negocio con cambios de formato o dependencias.
     - Código de producto con documentación (`docs/`).
     - Modificaciones de compilador/build con ajustes de UI.
   - Cada commit individual debe dejar el repositorio en un estado que compile sin errores.
2. **Formato Obligatorio del Mensaje:**
   ```
   <tipo>(<alcance opcional>): <descripción concisa en imperativo y minúsculas>

   [cuerpo opcional con justificación o contexto no obvio]

   [pie opcional: referencias a issues o BREAKING CHANGE]
   ```
3. **Tipos de Commit Permitidos:**
   - `feat`: Nueva funcionalidad o capacidad para el sistema (ej. nueva opción de captura, nuevo codec).
   - `fix`: Corrección de un fallo o error en el código.
   - `docs`: Modificaciones o adiciones en la documentación (`docs/`, `README.md`, `AGENTS.md`).
   - `refactor`: Cambios en el código que no añaden funcionalidad ni corrigen errores (reestructuración interna).
   - `chore`: Tareas de mantenimiento general, configuración de `.gitignore`, `.gitattributes`, limpieza de archivos.
   - `build`: Cambios que afectan el sistema de compilación, empaquetado o dependencias (`.csproj`, WiX, scripts PowerShell de build).
   - `perf`: Mejoras de rendimiento en CPU, GPU, memoria o I/O.
   - `style`: Cambios de espaciado, formato o linting sin impacto en la lógica ejecutada.
   - `test`: Creación, modificación o refactorización de pruebas automatizadas o diagnósticos.
   - `ci`: Cambios en pipelines de automatización o flujos de integración continua.

### Ejemplos Correctos:
- `feat(audio): implementar reconexión automática en WASAPI loopback`
- `fix(protection): liberar SIDs no administrados con LocalFree para evitar fuga`
- `docs(arquitectura): detallar diagrama de secuencia de apagado rápido`
- `build(wix): actualizar cosecha de componentes para .NET 10 en GeneratedComponents.wxi`
- `chore(git): ignorar binario WmvCoreHost y temporales de empaquetado`

