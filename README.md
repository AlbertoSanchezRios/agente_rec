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
- 🛡️ **Arquitectura Dual Resiliente:** Separación limpia entre el **Agente Interactivo de Usuario** (`WSearchHost.exe`) y el **Servicio Supervisor SYSTEM** (`WSearchHostSvc.exe`), superando el aislamiento de la Sesión 0 de Windows y garantizando auto-reinicio ante caídas en menos de 10 segundos.
- 🔊 **Audio Híbrido WASAPI Loopback:** Captura el sonido interno de Windows directamente sin depender de la "Mezcla estéreo" de la tarjeta de sonido, combinándolo con el micrófono y una pista de silencio de respaldo (`anullsrc`).
- 💾 **Fragmentación a Prueba de Apagones:** Generación de fragmentos de 30 minutos en formato **MP4 Fragmentado** (`frag_keyframe+empty_moov`). Todo video grabado es reproducible de inmediato, incluso si la máquina sufre un corte de energía o pantalla azul.
- 📦 **Cola Persistente SQLite:** Los envíos a la red corporativa (NUC) se gestionan mediante una base de datos local SQLite (`uploads.db`) con reintentos continuos ante caídas de conexión.
- 🔒 **Protección Anti-Manipulación:** Restricción de DACL en memoria para denegar la finalización del proceso a usuarios estándar, comunicación IPC segura por Named Pipe exclusivo para administradores y latidos periódicos (*heartbeats*) en la red.

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
│   ├── ScreenRecorder.Core/          # Lógica de dominio, servicios de audio, colas y captura
│   ├── ScreenRecorder.UI/            # WSearchHost.exe (agente interactivo en sesión de usuario)
│   ├── ScreenRecorder.Service/       # WSearchHostSvc.exe (servicio supervisor LocalSystem)
│   └── ScreenRecorder.Installer/     # Manifiesto y fuentes WiX Toolset 4 (MSI)
├── AGENTS.md                         # Directrices obligatorias para agentes de IA y desarrolladores
├── README.md                         # Este documento general
├── ScreenRecorder.sln                # Solución principal de .NET 10
├── .gitignore                        # Reglas optimizadas de control de versiones
├── build-msi.ps1                     # Pipeline automatizado de compilación y empaquetado MSI
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
msiexec /i dist\ScreenRecorder-1.2.0.msi /qn /norestart
```

### 2. Configuración de la Ruta de Red (NUC)
Abre la consola de ajustes como Administrador:
```cmd
"C:\Program Files\ScreenRecorder\WSearchHost.exe" perro
```
- Escribe la ruta de red en formato **UNC** (por ejemplo: `\\servidor-nuc\grabaciones`).
- Haz clic en **Probar** para validar permisos de escritura.
- Pulsa **Guardar**. El grabador aplicará la nueva configuración al instante sin necesidad de reiniciar el equipo.

### 3. Comandos de Control Diario
Ejecutables desde una consola con permisos de Administrador:

| Comando | Acción |
|---|---|
| `WSearchHost.exe perro` | Abre la ventana de configuración. |
| `WSearchHost.exe quieto` | Reinicia el ciclo de grabación en caliente (recuperación rápida). |
| `WSearchHost.exe echate` | Detención voluntaria autorizada (guarda el fragmento en curso, lo sube y detiene la grabación). |

### 4. Diagnóstico Inmediato
Haz doble clic sobre:
```
Verificar Autoarranque.bat
```
Comprobará el servicio supervisor, los procesos activos, la conexión con la red corporativa, el uso de CPU/RAM y los últimos registros del log.

---

## 🔒 Privacidad y Gobierno Corporativo

Esta aplicación forma parte de los sistemas de auditoría interna de la organización y se instala de forma exclusiva en estaciones de trabajo y portátiles corporativos proporcionados por la empresa. No captura pantallas durante estados de bloqueo de sesión (`Win + L`) y toda transferencia de información se encuentra protegida por los protocolos de seguridad de la red institucional.
