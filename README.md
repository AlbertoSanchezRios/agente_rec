# Screen Recorder - Sistema de Grabación de Pantalla y Auditoría

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6.svg)](https://microsoft.com/windows)
[![Architecture](https://img.shields.io/badge/Architecture-x64-blue.svg)]()
[![Packaging](https://img.shields.io/badge/Installer-WiX%20Toolset%204-red.svg)](https://wixtoolset.org/)

**Screen Recorder** es una solución corporativa de alto rendimiento desarrollada en **C# (.NET 10.0)** para la grabación continua, desatendida y silenciosa de pantalla y audio (sistema y micrófono) en estaciones de trabajo de la empresa, cumpliendo con políticas de auditoría interna y seguridad de la información.

---

## 🚀 Aspectos Destacados

- ⚡ **Migrado a .NET 10.0:** Mayor eficiencia de memoria (`InvariantGlobalization`, `DebuggerSupport=false`) y mínima latencia.
- 🎯 **Captura por GPU de Bajo Consumo:** Utiliza la Desktop Duplication API (`ddagrab`) con aceleración por hardware Intel QuickSync (`h264_qsv`), manteniendo el uso de CPU entre un **1.5% y 2.5%** sin parpadeo del cursor.
- 🛡️ **Arquitectura Dual Resiliente:** Separación limpia entre el **Agente Interactivo de Usuario** (`WSearchAgent.exe`) y el **Servicio Supervisor SYSTEM** (`WSearchHostSvc.exe`), superando el aislamiento de la Sesión 0 de Windows y garantizando auto-reinicio ante caídas en menos de 10 segundos.
- 🔊 **Audio Híbrido WASAPI Loopback:** Captura el sonido interno de Windows directamente sin depender de la "Mezcla estéreo" de la tarjeta de sonido, combinándolo con el micrófono y una pista de silencio de respaldo (`anullsrc`).
- 💾 **Fragmentación a Prueba de Apagones:** Generación de fragmentos de 30 minutos en contenedor **MKV** (robusto ante cortes de energía), convertidos y **verificados** a **MP4 reproducible** por un subproceso dedicado antes de subir a la NUC.
- 📦 **Cola Persistente SQLite:** Los envíos a la red corporativa (NUC) se gestionan mediante una base de datos local SQLite (`uploads.db`) en modo WAL, con subida atómica a `.tmp`, verificación de tamaño byte a byte y reintentos continuos ante caídas de conexión.
- 🔒 **Protección Anti-Manipulación:** Restricción de DACL en memoria para denegar la finalización (`PROCESS_TERMINATE`) y suspensión (`PROCESS_SUSPEND_RESUME`) del proceso a usuarios estándar, comunicación IPC segura por Named Pipe exclusivo para administradores y latidos periódicos (*heartbeats*) en la red.

---

## 📂 Estructura del Repositorio

El proyecto ha sido optimizado y organizado según una arquitectura limpia y modular:

```
D:\dev\agente_rec\
├── docs/                             # Suite completa de documentación técnica y operativa
│   ├── arquitectura.md               # Arquitectura dual, modelo de capas y flujos de datos
│   ├── caracteristicas.md            # Especificaciones funcionales y capacidades
│   ├── diseño.md                     # Patrones de diseño, IPC, DPI awareness y memoria
│   ├── operacion.md                  # Compilación, despliegue masivo y comandos de TI
│   ├── mantenimiento.md              # Matriz de diagnóstico, resolución de fallos y logs
│   ├── seguridad.md                  # Modelo de permisos, DACLs, HKLM y anti-tampering
│   └── tecnologias.md                # Stack tecnológico (.NET 10, FFmpeg, SQLite, Win32)
├── src/                              # Código fuente de la solución C#
│   ├── ScreenRecorder.Core/          # Lógica de dominio, servicios de audio, colas, IPC y captura
│   ├── ScreenRecorder.Agent/         # WSearchAgent.exe (agente interactivo, SIN WinForms)
│   ├── ScreenRecorder.UI/            # WSearchCfg.exe (UI de configuración elevada, on-demand)
│   ├── ScreenRecorder.Service/       # WSearchHostSvc.exe (servicio supervisor LocalSystem)
│   ├── Wctl/                         # Wctl.exe (CLI de control)
│   └── ScreenRecorder.Installer/     # Manifiesto y fuentes WiX Toolset 4 (MSI)
├── tools/
│   └── build-ffmpeg.sh               # Build mínima de FFmpeg (MSYS2)
├── AGENTS.md                         # Directrices obligatorias para agentes de IA y desarrolladores
├── README.md                         # Este documento general
├── ScreenRecorder.sln                # Solución principal de .NET 10
├── .gitignore                        # Reglas optimizadas de control de versiones
├── build-msi.ps1                     # Pipeline automatizado de compilación y empaquetado MSI
├── build-ffmpeg.ps1                  # Compila la FFmpeg a medida con MSYS2 (opcional)
├── publish-fase1.ps1                 # Publicación rápida de binarios
├── install-autostart.ps1             # Script de registro y aseguramiento del servicio watchdog
├── verify-autostart.ps1              # Diagnóstico integral del sistema en tiempo real
├── VER_CONFIGURACION.ps1             # Consulta rápida de ajustes guardados en Registry
├── Instalar Autoarranque.bat         # Lanzador directo para personal técnico
└── Verificar Autoarranque.bat        # Lanzador de verificación rápida
```

---

## 📚 Documentación Técnica Detallada

Para consultar guías completas redactadas en tono humano, visita los siguientes enlaces:

- 🏛️ [**Arquitectura del Sistema**](docs/arquitectura.md): Explicación exhaustiva del aislamiento de Sesión 0, el modelo supervisor-agente y el ciclo de vida de grabación.
- ✨ [**Características y Capacidades**](docs/caracteristicas.md): Desglose funcional detallado de audio, video, fragmentación y resiliencia.
- 📐 [**Diseño y Patrones de Ingeniería**](docs/diseño.md): Decisiones técnicas de memoria, DPI multimonitor, tuberías IPC y base de datos SQLite.
- 🛠️ [**Guía de Operación y Despliegue**](docs/operacion.md): Cómo compilar, instalar con MSI (interactivo o silencioso con SCCM/GPO), configurar la NUC y usar los comandos de TI.
- 🔍 [**Mantenimiento y Solución de Problemas**](docs/mantenimiento.md): Matriz de síntomas y soluciones rápidas, análisis de logs y recuperación de colas.
- 🛡️ [**Seguridad y Cumplimiento Normativo**](docs/seguridad.md): Protección de descriptores de seguridad (DACL), permisos HKLM, evidencia forense y privacidad.
- 💻 [**Tecnologías y Stack Técnico**](docs/tecnologias.md): Ficha técnica de .NET 10, FFmpeg, WASAPI, SQLite, APIs Win32 y WiX 4.
- 🤖 [**Directrices para Agentes de IA (`AGENTS.md`)**](AGENTS.md): Reglas e invariantes arquitectónicas estrictas para cualquier asistente de código automatizado.

---

## ⚡ Guía Rápida de Inicio para Administradores de TI

### 1. Instalación Rápida
Ejecuta el instalador generado en la carpeta `dist/`:
```cmd
msiexec /i dist\ScreenRecorder-0.1.1.msi /qn /norestart
```

### 2. Configuración de la Ruta de Red (NUC)
Abre la consola de ajustes como Administrador:
```cmd
"C:\Program Files\ScreenRecorder\Wctl.exe" -config
```
- Escribe la ruta de red en formato **UNC** (por ejemplo: `\\servidor-nuc\grabaciones`).
- Haz clic en **Probar** para validar permisos de escritura.
- Pulsa **Guardar**. El grabador aplicará la nueva configuración al instante sin necesidad de reiniciar el equipo.

### 3. Comandos de Control Diario
Ejecutables con **`Wctl.exe`** desde una consola con permisos de Administrador (se auto-eleva con UAC si hace falta). Añade `--json` para scripting:

| Comando | Acción |
|---|---|
| `Wctl -status` | Estado del agente, procesos, ruta de red y cola de envíos. |
| `Wctl -config` | Abre la ventana de configuración. |
| `Wctl -restart` | Reinicia el ciclo de grabación en caliente (recuperación rápida). |
| `Wctl -pause` / `Wctl -resume` | Pausa o reanuda la grabación sin cerrar el agente. |
| `Wctl -logs [-lines N]` | Muestra las últimas líneas del log. |
| `Wctl -doctor` | Diagnóstico rápido (estado + errores recientes). |
| `Wctl -stop` | Detención voluntaria autorizada (convierte/sube el fragmento en curso y detiene la grabación). |

> Alias legacy: `-perro` = `-config`, `-quieto` = `-restart`, `-echate` = `-stop`.

### 4. Diagnóstico Inmediato
Haz doble clic sobre:
```
Verificar Autoarranque.bat
```
Comprobará el servicio supervisor, los procesos activos, la conexión con la red corporativa, el uso de CPU/RAM y los últimos registros del log.

---

## 🔒 Privacidad y Gobierno Corporativo

Esta aplicación forma parte de los sistemas de auditoría interna de la organización y se instala de forma exclusiva en estaciones de trabajo y portátiles corporativos proporcionados por la empresa. No captura pantallas durante estados de bloqueo de sesión (`Win + L`) y toda transferencia de información se encuentra protegida por los protocolos de seguridad de la red institucional.

El tratamiento de datos personales se detalla en el **[Aviso de Privacidad](PRIVACY.md)**, emitido conforme a la Ley Federal de Protección de Datos Personales en Posesión de los Particulares (LFPDPPP).

---

## ⚖️ Licencia y Titularidad

- **[LICENSE](LICENSE)** — Licencia de software **propietario**. Copyright © 2026 **José Francisco Toledo** y **Alberto Sánchez Ríos**. Todos los derechos reservados. Titularidad de los derechos patrimoniales al **50%** entre ambos autores.
- **[PRIVACY.md](PRIVACY.md)** — Aviso de Privacidad (LFPDPPP).
- **[NOTICE](NOTICE)** — Avisos de componentes de terceros (FFmpeg GPLv3, .NET, WiX, SQLite).
- **[docs/titularidad.md](docs/titularidad.md)** — Declaración de autoría y titularidad.

Este software es de uso interno exclusivo. Se concede únicamente un derecho limitado, no transferible y no sublicenciable de instalación y ejecución en estaciones propiedad de la organización. Los componentes de terceros conservan sus propias licencias.
