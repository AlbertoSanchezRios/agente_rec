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
   dist\ScreenRecorder-1.2.0.msi
   ```

---

## 2. Métodos de Instalación y Despliegue

### Método A: Instalación Interactiva (Recomendada para Pruebas o Equipos Individuales)
1. Ejecuta el archivo `ScreenRecorder-1.2.0.msi` con doble clic.
2. Acepta el cuadro de elevación de privilegios (UAC).
3. El instalador copiará los archivos a `C:\Program Files\ScreenRecorder\` y registrará el servicio `WSearchHostSvc`.
4. Al finalizar, el asistente abrirá automáticamente la ventana de **Ajustes de Grabación** (`/postinstall`).
5. Escribe la ruta de red (NUC), haz clic en **Probar** para verificar conectividad y luego en **Guardar**.
6. La ventana se cerrará y la máquina ya estará grabando en modo silencioso.

### Método B: Despliegue Silencioso Masivo (Active Directory GPO / SCCM / Intune)
Para desplegar la aplicación en decenas o cientos de computadoras sin intervención del usuario:

```cmd
msiexec /i ScreenRecorder-1.2.0.msi /qn /norestart
```

*Nota:* Al instalar en modo silencioso (`/qn`), el servicio supervisor se registra y arranca de inmediato, pero no se mostrará ninguna interfaz gráfica en pantalla.

### Método C: Desinstalación Completa
Existen dos formas estándar de desinstalar el software:

1. **Vía Wrapper Interactivo (`Desinstalar.bat`):**  
   Ejecuta con doble clic `Desinstalar.bat` (desde la raíz del proyecto o desde `C:\Program Files\ScreenRecorder\`).  
   El script se auto-eleva como Administrador, establece la bandera `stopped`, detiene el servicio y los procesos, ejecuta la desinstalación formal del MSI vía `msiexec`, limpia el registro y purga las carpetas residuales en `ProgramData` y `Program Files`.
2. **Vía Línea de Comandos / SCCM / Intune:**  
   ```cmd
   msiexec /x ScreenRecorder-1.2.0.msi /qn /norestart
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
   "C:\Program Files\ScreenRecorder\WSearchHost.exe" perro
   ```
3. Se abrirá la ventana de Ajustes.
4. Ajusta los parámetros deseados.
5. Haz clic en **Guardar**. La grabación se reiniciará sola aplicando la nueva configuración de inmediato.

---

## 4. Comandos de Control para Administradores de TI

La aplicación escucha comandos por tubería con nombre (Named Pipe). Los comandos deben ejecutarse desde un contexto con permisos de Administrador:

```cmd
cd "C:\Program Files\ScreenRecorder"
```

| Comando | Efecto Práctico |
|---|---|
| `WSearchHost.exe perro` | Abre la ventana de configuración. Al guardar, reinicia la grabación para aplicar los cambios. |
| `WSearchHost.exe quieto` | Fuerza el reinicio inmediato del ciclo de grabación. Es útil si se conectó una nueva pantalla o si se desea empezar un fragmento nuevo en el acto. |
| `WSearchHost.exe echate` | **Detención voluntaria autorizada.** Cierra ordenadamente FFmpeg, sube el fragmento en curso a la red, escribe la bandera de detención `stopped` y termina el proceso. El servicio supervisor respetará esta bandera y **no resucitará** la aplicación hasta nueva orden. |

### Cómo Reanudar la Grabación tras un `echate`:
Para reactivar la grabación en un equipo que fue detenido con `echate`, ejecuta cualquiera de estas acciones:
1. `WSearchHost.exe quieto` (elimina la bandera y arranca la captura).
2. Abrir la configuración con `WSearchHost.exe perro` y pulsar **Guardar**.
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
   - `WSearchHost.exe` (Agente interactivo).
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
| **Binarios Principales** | `C:\Program Files\ScreenRecorder\` | Ejecutables (`WSearchHost.exe`, `WSearchHostSvc.exe`, `WmvCoreHost.exe`) y DLLs. |
| **Archivos de Registro (Logs)** | `C:\ProgramData\ScreenRecorder\Logs\` | Archivos de texto diarios `ScreenRecorder_YYYY-MM-DD.log`. |
| **Cola Local de Subida** | `C:\ProgramData\ScreenRecorder\uploads.db` | Base de datos transaccional SQLite de videos encolados. |
| **Bandera de Pausa** | `C:\ProgramData\ScreenRecorder\stopped` | Archivo indicador de parada legítima de TI. |
| **Caché de Detección de GPU** | `C:\ProgramData\ScreenRecorder\pipeline.txt` | Especificación del pipeline acelerado por hardware elegido. |
| **Fragmentos Temporales** | `%TEMP%\ScreenRecorder\` | Buffer de video en curso (`*.recording.mp4`) y completados antes de transferir. |
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

