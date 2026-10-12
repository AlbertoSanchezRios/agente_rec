# Registro de Cambios (Changelog)

Todos los cambios notables de **Screen Recorder** se documentan en este archivo.

El formato está basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y este proyecto se adhiere al [Versionado Semántico](https://semver.org/lang/es/).
Las versiones `0.x` se consideran **beta** y se publican como *prerelease*.

## [0.1.2] - 2026-10-12

### Agregado
- **Firma de código propio (Authenticode).** PKI privada (root CA + hoja) con la que se firman
  el MSI y los ejecutables; el certificado raíz público se adjunta al release y el MSI lo
  instala automáticamente en *Entidades de certificación raíz de confianza* (y la hoja en
  *Editores de confianza*). Script `install-cert.ps1` para despliegue por Intune/manual.
  Se elimina el aviso "Editor desconocido" (UAC) en los equipos que confían en el root.
- **Licencia y titularidad.** Licencia propietaria ([`LICENSE`](LICENSE), co-titularidad 50/50 de
  José Francisco Toledo y Alberto Sánchez Ríos), [`NOTICE`](NOTICE) con avisos de terceros y
  [`docs/titularidad.md`](docs/titularidad.md).
- **Aviso de Privacidad** ([`PRIVACY.md`](PRIVACY.md)) conforme a la LFPDPPP.
- **Metadatos de autoría** en los binarios (Company/Product/Copyright) y versión centralizada
  en `Directory.Build.props`.

### Cambiado
- **Rediseño de la interfaz de configuración** (`WSearchCfg`): ventana rectangular con pestañas
  (Video, Audio, Almacenamiento, Nombres), barra inferior fija con estado de la grabación y
  botones **Reanudar/Detener** conectados por IPC (`resume`/`pause`). Tema visual propio.

### Corregido
- **El botón "Probar" valida la ruta escrita** (no la guardada) mediante una sobrecarga de
  `TryUploadFileAsync` con ruta explícita.
- **Mensajes de audio** alineados con WASAPI loopback (ya no mencionan "Mezcla estéreo" como requisito).
- **`ARPNOMODIFY` duplicado** en el instalador (rompía el build del MSI).
- **Opción "Notificaciones PopUp"** eliminada (sin efecto y contraria a la operación silenciosa).

## [0.1.1] - 2026-10-10

Primera versión beta pública.

### Corregido
- **La grabación no se reanudaba tras detener el agente.** Al guardar la configuración
  desde `WSearchCfg.exe` cuando el agente no estaba en ejecución (modo `/settings`, sin
  una instancia de grabación activa), `ApplyConfigurationToRecording` retornaba de
  inmediato antes de eliminar la bandera de detención voluntaria (`stopped`). Como el
  servicio watchdog respeta esa bandera por diseño, `WSearchAgent.exe` nunca volvía a
  arrancar y la captura quedaba **inactiva de forma permanente**, incluso aunque TI
  reabriera Ajustes y guardara.
  Ahora la limpieza de la bandera (`ControlPipe.ClearStopFlag`) se ejecuta siempre al
  aplicar la configuración, garantizando que guardar en Ajustes reanude la grabación en
  cualquier escenario.
