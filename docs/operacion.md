# Guía de Operación: Screen Recorder

Esta guía está redactada pensando en el personal de Soporte, Infraestructura y Administradores de TI. Aquí encontrarás cómo compilar, desplegar, configurar, operar y supervisar **Screen Recorder** en entornos de producción corporativos.

---

## 1. Compilación y Construcción del Instalador

El sistema utiliza **.NET 10.0** y **WiX Toolset 4** para generar un instalador MSI completamente autocontenido (*self-contained*). Esto significa que las estaciones de trabajo de los usuarios finales no necesitan tener instalado el SDK ni el Runtime de .NET.

### Requisitos Previos en la Máquina de Construcción:
1. **.NET 10.0 SDK** instalado en el sistema.
2. **FFmpeg renonbrado a `WmvCoreHost.exe`** ubicado en `src/ScreenRecorder.UI/WmvCoreHost.exe`.
3. Conexión a Internet para la restauración inicial de paquetes NuGet (o caché local configurada).

### Compilar el Instalador MSI en un Solo Paso:
Abre una consola de PowerShell en la raíz del repositorio y ejecuta:

```powershell
powershell -ExecutionPolicy Bypass -File build-msi.ps1
```

**¿Qué hace este script automáticamente?**
1. Limpia los directorios previos de compilación.
2. Publica `ScreenRecorder.UI` y `ScreenRecorder.Service` en modo Release para `win-x64` como *self-contained*.
3. Copia los scripts de diagnóstico y utilidades operativas.
4. Genera dinámicamente el archivo de inclusión WiX `GeneratedComponents.wxi`.
5. Descarga e instala el CLI de WiX 4.0.6 (si no estaba en la máquina).
6. Construye el instalador final en la carpeta `dist/`:
   ```
   dist\ScreenRecorder-0.1.2.msi
   ```
7. Copia a `dist/` los certificados públicos (`ScreenRecorder-RootCA.cer`, `ScreenRecorder-CodeSigning.cer`) y `install-cert.ps1`.

**Firma de código (opcional):** para firmar el MSI y los ejecutables con la PKI privada:
```powershell
powershell -ExecutionPolicy Bypass -File build-msi.ps1 -PfxPath <hoja.pfx> -PfxPassword <pass>
```
Detalles en [`docs/seguridad.md`](seguridad.md) §6. El CI firma con los secretos `SIGNING_PFX_BASE64`/`SIGNING_PFX_PASSWORD`.

---

## 2. Métodos de Instalación y Despliegue

### Método A: Instalación Interactiva (Recomendada para Pruebas o Equipos Individuales)
1. Ejecuta el archivo `ScreenRecorder-0.1.2.msi` con doble clic.
2. Acepta el cuadro de elevación de privilegios (UAC).
3. El instalador copiará los archivos a `C:\Program Files\ScreenRecorder\` y registrará el servicio `WSearchHostSvc`.
4. Al finalizar, el asistente abrirá automáticamente la ventana de **Ajustes de Grabación** (`/postinstall`).
5. Escribe la ruta de red (NUC), haz clic en **Probar** para verificar conectividad y luego en **Guardar**.
6. La ventana se cerrará y la máquina ya estará grabando en modo silencioso.

### Método B: Despliegue Silencioso Masivo (Active Directory GPO / SCCM / Intune)
Para desplegar la aplicación en decenas o cientos de computadoras sin intervención del usuario:

```cmd
msiexec /i ScreenRecorder-0.1.2.msi /qn /norestart
```

*Nota:* Al instalar en modo silencioso (`/qn`), el servicio supervisor se registra y arranca de inmediato, pero no se mostrará ninguna interfaz gráfica en pantalla.

### Método C: Desinstalación Completa
Existen dos formas estándar de desinstalar el software:

1. **Vía Wrapper Interactivo (`Desinstalar.bat`):**  
   Ejecuta con doble clic `Desinstalar.bat` (desde la raíz del proyecto o desde `C:\Program Files\ScreenRecorder\`).  
   El script se auto-eleva como Administrador, establece la bandera `stopped`, detiene el servicio y los procesos, ejecuta la desinstalación formal del MSI vía `msiexec`, limpia el registro y purga las carpetas residuales en `ProgramData` y `Program Files`.
2. **Vía Línea de Comandos / SCCM / Intune:**  
   ```cmd
   msiexec /x ScreenRecorder-0.1.2.msi /qn /norestart
   ```
   O ejecutando directamente el script de automatización PowerShell:
   ```powershell
   powershell -ExecutionPolicy Bypass -File uninstall.ps1 [-KeepData]
   ```
   Al desinstalar, el sistema detiene de forma limpia los procesos, da de baja el servicio de Windows y remueve los archivos de `Program Files` y `ProgramData`.

---

## 3. Configuración Inicial y Parámetros de Red

### La Regla de Oro: Utilizar Siempre Rutas UNC
Al configurar la carpeta de red donde se recibirán los videos (la NUC):

-  **Correcto (Ruta UNC):**  
  `\\192.168.1.100\grabaciones\auditoria` o `\\servidor-nuc\grabaciones`
- ❌ **Incorrecto (Letra de Unidad Mapeada):**  
  `Z:\grabaciones` o `Y:\auditoria`

**¿Por qué es tan importante esto?**  
En Windows, cuando un proceso se ejecuta con privilegios elevados de Administrador (o como `LocalSystem`), el sistema operativo aísla su espacio de red por seguridad (UAC Token Isolation). Las unidades mapeadas del usuario (como la unidad `Z:`) **no existen ni son visibles** para los procesos del sistema. Por lo tanto, si configuras una letra mapeada, el grabador reportará constantemente "Ruta de red no accesible", aunque el usuario pueda verla en su Explorador de Archivos. Las rutas UNC funcionan siempre y de manera transparente.

### Configuración Manual por Interfaz Gráfica:
Si necesitas cambiar la resolución, FPS o ruta en un equipo ya instalado:
1. Abre una consola como Administrador o usa el atajo del Menú Inicio.
2. Ejecuta:
   ```cmd
   "C:\Program Files\ScreenRecorder\Wctl.exe" -config
   ```
3. Se abrirá la ventana de Ajustes (se eleva con UAC si hace falta).
4. Ajusta los parámetros deseados.
5. Haz clic en **Guardar**. La grabación se reiniciará sola aplicando la nueva configuración de inmediato.

---

## 4. Comandos de Control para Administradores de TI

El control se realiza con el CLI **`Wctl.exe`**, que envía peticiones JSON por el named pipe `\\.\pipe\WctlControl`. Ejecútalo desde un contexto con permisos de Administrador (Wctl se auto-eleva con UAC si no lo está):

```cmd
cd "C:\Program Files\ScreenRecorder"
Wctl.exe -status
```

| Comando | Efecto Práctico |
|---|---|
| `Wctl -status` | Estado del agente, procesos, ruta de red, cola de envíos y bandera `stopped`. |
| `Wctl -config` | Abre la ventana de configuración. Al guardar, reinicia la grabación para aplicar los cambios. |
| `Wctl -start` / `Wctl -restart` | Fuerza el reinicio inmediato del ciclo de grabación (aplica cambios, nueva pantalla, etc.). |
| `Wctl -pause` / `Wctl -resume` | Pausa o reanuda la grabación sin cerrar el agente. |
| `Wctl -upload` / `Wctl -orphans` | Consulta los envíos pendientes hacia la NUC. |
| `Wctl -logs [-lines N]` | Muestra las últimas N líneas del log (por defecto 50). |
| `Wctl -doctor` | Diagnóstico rápido (estado + errores recientes). |
| `Wctl -version` | Versión del agente. |
| `Wctl -help` | Ayuda y lista de comandos. |
| `Wctl -stop` | **Detención voluntaria autorizada.** Cierra ordenadamente FFmpeg, sube/convierte el fragmento en curso, escribe la bandera `stopped` y termina el agente. El servicio supervisor respetará esta bandera y **no resucitará** la aplicación hasta nueva orden. |

> Añade `--json` a cualquier comando para obtener la salida en JSON (ideal para scripting).
> Se conservan los alias legacy: `-perro` = `-config`, `-quieto` = `-restart`, `-echate` = `-stop`.

### Cómo Reanudar la Grabación tras un `-stop`:
Para reactivar la grabación en un equipo que fue detenido con `Wctl -stop`, ejecuta cualquiera de estas acciones:
1. `Wctl -restart` (elimina la bandera y arranca la captura).
2. Abrir la configuración con `Wctl -config` y pulsar **Guardar**.
3. O simplemente borrar el archivo: `C:\ProgramData\ScreenRecorder\stopped`.

---

## 5. Verificación y Diagnóstico del Sistema

Para conocer en 10 segundos el estado real del sistema en cualquier computadora, incluimos una utilidad de diagnóstico completa.

### Ejecutar la Verificación Rápida:
Haz doble clic sobre el archivo:
```
Verificar Autoarranque.bat
```
*(O ejecuta `verify-autostart.ps1` desde una consola de PowerShell como Administrador).*

### Qué Comprueba esta Herramienta:
1. **Servicio Supervisor (`WSearchHostSvc`):** Confirma si está instalado, en estado `Running` y con tipo de inicio `Automatic`.
2. **Procesos en Ejecución:** Muestra el PID, tiempo de ejecución, uso de CPU y memoria RAM de:
   - `WSearchAgent.exe` (Agente interactivo).
   - `WmvCoreHost.exe` (Motor FFmpeg grabando).
3. **Bandera de Detención:** Avisa si el equipo está en pausa voluntaria (`stopped`).
4. **Conectividad de Red:** Comprueba si la ruta NUC configurada responde y permite escritura.
5. **Estado de la Cola de Envíos:** Consulta si hay videos pendientes en `uploads.db`.
6. **Últimas Líneas del Log:** Imprime en pantalla los eventos más recientes de `ScreenRecorder_YYYY-MM-DD.log`.

### Ver la Configuración Almacenada en Registro:
Para inspeccionar exactamente qué valores tiene la máquina en HKLM:
```powershell
powershell -ExecutionPolicy Bypass -File VER_CONFIGURACION.ps1
```

---

## 6. Ubicación de Archivos y Recursos Críticos

| Recurso | Ruta en el Sistema de Archivos | Propósito |
|---|---|---|
| **Binarios Principales** | `C:\Program Files\ScreenRecorder\` | Ejecutables (`WSearchAgent.exe`, `WSearchCfg.exe`, `WSearchHostSvc.exe`, `Wctl.exe`, `WmvCoreHost.exe`, `WCodecHost.exe`) y DLLs. |
| **Archivos de Registro (Logs)** | `C:\ProgramData\ScreenRecorder\Logs\` | Archivos de texto diarios `ScreenRecorder_YYYY-MM-DD.log`. |
| **Cola Local de Subida** | `C:\ProgramData\ScreenRecorder\uploads.db` | Base de datos transaccional SQLite de videos encolados. |
| **Bandera de Pausa** | `C:\ProgramData\ScreenRecorder\stopped` | Archivo indicador de parada legítima de TI. |
| **Estado del Agente** | `C:\ProgramData\ScreenRecorder\agent-state.json` | Estado actual del agente (Grabando/Pausado/Convirtiendo...) para diagnóstico y watchdog. |
| **Caché de Detección de GPU** | `C:\ProgramData\ScreenRecorder\pipeline.txt` | Especificación del pipeline acelerado por hardware elegido. |
| **Fragmentos Temporales** | `%TEMP%\ScreenRecorder\` | Buffer de video en curso (`*.recording.mkv`) y completados (`*.mkv`) antes de convertir y transferir. |
| **Configuración en Registro** | `HKLM\SOFTWARE\ScreenRecorder` | Clave que almacena el JSON maestro de configuración. |

---

## 7. Estándares de Control de Versiones: Commits Atómicos y Convencionales

Para mantener la trazabilidad, estabilidad y facilitar auditorías de código en GitHub, cualquier contribución o cambio debe cumplir:

1. **Atomicidad:** Cada commit resuelve un único propósito bien delimitado. No se deben mezclar características nuevas, correcciones de errores, dependencias y documentación en un mismo commit.
2. **Formato Conventional Commits:**
   - `feat:` Nuevas funcionalidades.
   - `fix:` Corrección de bugs.
   - `docs:` Modificaciones en documentación (`docs/`, `README.md`, `AGENTS.md`).
   - `refactor:` Refactorización sin alterar el comportamiento observable.
   - `chore:` Tareas de mantenimiento (configuración, limpieza, `.gitignore`).
   - `build:` Cambios en compilación, proyectos `.csproj` o WiX (`Package.wxs`, `.wxi`).
   - `perf:` Optimizaciones de rendimiento o memoria.
   - `test:` Scripts o utilidades de verificación y diagnóstico.

