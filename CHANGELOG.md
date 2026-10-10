# Registro de Cambios (Changelog)

Todos los cambios notables de **Screen Recorder** se documentan en este archivo.

El formato está basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y este proyecto se adhiere al [Versionado Semántico](https://semver.org/lang/es/).
Las versiones `0.x` se consideran **beta** y se publican como *prerelease*.

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
