# Características del Sistema: Screen Recorder

**Screen Recorder** no es una herramienta de captura de pantalla genérica; es una solución de auditoría interna corporativa construida para operar en segundo plano de manera continua, confiable y completamente desatendida. 

A continuación, detallamos cada una de sus características clave, explicando qué hacen y por qué son cruciales para el funcionamiento diario en una empresa.

---

## 1. Operación Completamente Silenciosa e Invisible

Una de las premisas fundamentales del sistema es que **no debe alterar la experiencia de trabajo del usuario ni llamar su atención innecesariamente**:

- **Sin Ventanas ni Iconos en Bandeja:** La aplicación no crea ningún icono en el área de notificación (System Tray) ni barra de tareas durante su ejecución normal. Corre como un proceso en segundo plano de 0x0 píxeles.
- **Identidad Discreta de Procesos:** En el Administrador de Tareas de Windows, los binarios se presentan con nombres orientados al mantenimiento del sistema:
  - Agente de captura interactivo: `WSearchHost.exe`
  - Servicio supervisor del sistema: `WSearchHostSvc.exe`
  - Motor de procesamiento multimedia: `WmvCoreHost.exe` (binario de FFmpeg renombrado)
- **Cero Notificaciones al Usuario:** La aplicación no emite alertas emergentes en pantalla que puedan distraer al operador durante sus tareas habituales.

---

## 2. Motor de Captura de Video por GPU de Alta Eficiencia

Tradicionalmente, las herramientas de captura de escritorio utilizaban `BitBlt` o GDI, lo que provocaba un alto consumo de CPU y un molesto parpadeo del cursor cuando el usuario abría navegadores web o suites ofimáticas. Screen Recorder resuelve esto mediante un motor moderno:

- **Desktop Duplication API (`ddagrab`):** Captura los cuadros directamente desde la memoria de video de la GPU mediante DirectX Graphics Infrastructure (DXGI).
- **Eliminación del Parpadeo de Cursor:** Al capturar por GPU, el cursor del ratón no parpadea ni interfiere con la aceleración por hardware de navegadores como Google Chrome o Microsoft Edge.
- **Aceleración por Hardware Intel QuickSync (`h264_qsv`):**
  - Utiliza el circuito dedicado de codificación de los procesadores Intel modernos.
  - Reduce el uso del procesador a apenas **1.5% - 2.5% de CPU** en resoluciones 1080p a 30 FPS.
- **Sondeo Inteligente de Capacidades (Pipeline Probe):**
  - Al iniciar por primera vez en una máquina, prueba en orden:
    1. GPU completa: `ddagrab` + `h264_qsv`.
    2. GPU con codificación por software: `ddagrab` + `libx264`.
    3. CPU y captura GDI: `gdigrab` + `libx264` (preset `ultrafast`).
  - Cachea el resultado óptimo en `C:\ProgramData\ScreenRecorder\pipeline.txt` para que los futuros arranques sean instantáneos.
- **Soporte Multimonitor Flexible:**
  - Permite configurar qué pantalla grabar: **Monitor Primario**, **Monitor Secundario** o **Ambos Monitores**.
- **Precisión Geométrica DPI (Per-Monitor v2):**
  - Calcula la resolución y escala física real de cada pantalla usando `GetDpiForMonitor` y `EnumDisplaySettings`.
  - Evita el error clásico de grabar "solo un cuarto de pantalla" en monitores de alta densidad (pantallas 4K o laptops con escalado al 125%/150%).

---

## 3. Pipeline de Audio Híbrido y Resiliencia Sonora

El sistema está diseñado para registrar tanto lo que el usuario escucha como lo que habla, garantizando que el archivo resultante tenga siempre una sincronización perfecta:

- **Audio del Sistema vía WASAPI Loopback Nativo:**
  - Captura el sonido interno de Windows directamente desde el bus de audio digital.
  - **No depende de "Mezcla estéreo":** Funciona de inmediato sin importar si la tarjeta de sonido del fabricante tiene deshabilitada esta opción.
  - El flujo PCM crudo se inyecta en tiempo real hacia FFmpeg.
- **Audio de Micrófono con Priorización USB:**
  - Detecta micrófonos conectados al equipo mediante DirectShow y WASAPI.
  - Da prioridad automática a dispositivos de audio USB (como diademas o auriculares con micrófono usados en call centers y soporte).
- **Pista de Audio Silenciosa de Respaldo (`anullsrc`):**
  - Si un equipo no tiene ningún micrófono ni salida de audio conectada, el sistema genera automáticamente una pista de audio inaudible.
  - **Por qué importa:** Muchos reproductores de video (como Windows Media Player o VLC) sufren desincronización o rechazan la reproducción de archivos MP4 que carecen por completo de una pista de audio. Con esta salvaguarda, todo archivo generado es 100% estándar.

---

## 4. Fragmentación Robusta y Protección ante Apagones

La pérdida de grabaciones por cortes de suministro eléctrico o bloqueos repentinos del equipo ha quedado eliminada:

- **Fragmentos Continuos de 30 Minutos:**
  - La grabación no se acumula en un archivo gigante de fin de jornada. Se corta de manera transparente en bloques independientes (configurable entre 1 y 120 minutos; valor predeterminado: 30 minutos).
- **MP4 Fragmentado (`frag_keyframe+empty_moov`):**
  - Los archivos MP4 comunes escriben sus metadatos e índices al final del archivo. Si la PC se apaga a la fuerza, el archivo queda inutilizable.
  - Screen Recorder escribe índices periódicos dentro de cada fragmento. Si la máquina se apaga de golpe, **todo lo escrito en disco hasta el último segundo se reproduce sin problemas**.
- **Nomenclatura Atómica de Archivos:**
  - Mientras se graba, el archivo lleva el sufijo temporal `.recording.mp4`.
  - Una vez cerrado y validado, se renombra a `.mp4`.
  - De este modo, los procesos de subida y el personal de TI jamás intentan reproducir o transferir un archivo a medio escribir.

---

## 5. Cola Persistente SQLite y Resiliencia de Red

Los videos deben llegar a un almacenamiento de red central (la NUC o servidor corporativo), pero la red de una oficina puede sufrir interrupciones, cables desconectados o caídas del servidor:

- **Búfer de Almacenamiento Local:**
  - Los fragmentos se guardan inicialmente en `%TEMP%\ScreenRecorder` (con rutas de respaldo en `%LOCALAPPDATA%` y `ProgramData`).
- **Cola SQLite Duradera (`uploads.db`):**
  - Cada fragmento completado se encola en una base de datos local SQLite.
  - La cola sobrevive a reinicios de la máquina, cierres de sesión y cortes de energía.
- **Reintentos Infinitos con Retroceso Exponencial:**
  - Si la red a la NUC no responde, el grabador no se detiene ni descarta videos: continúa grabando en local y reintenta la subida periódicamente cada 2 minutos.
- **Disyuntor de Seguridad (Circuit Breaker):**
  - Si por alguna razón la base SQLite sufre un bloqueo o corrupción, el sistema conmuta automáticamente al "Modo Directo" (subida directa sin base de datos) para no acumular archivos en el limbo.
- **Eliminación Local con Confirmación Doble:**
  - El archivo local solo se borra del disco del usuario cuando la subida a la red ha terminado con éxito y se ha verificado el tamaño final en el destino.

---

## 6. Integración con Eventos del Sistema Operativo

El sistema es plenamente consciente del estado de la sesión de Windows y actúa con lógica humana:

- **Inicio Automático al Entrar:** Cuando el usuario inicia sesión (`Logon`), la grabación comienza de inmediato sin requerir ninguna acción.
- **Pausa Automática al Bloquear Pantalla (`Win + L`):**
  - Si el usuario bloquea el equipo para ir a comer o a una reunión, la grabación se detiene para ahorrar espacio en disco y respetar los descansos del personal.
- **Reanudación Instantánea al Desbloquear:** En cuanto se introduce la contraseña de Windows, el grabador reanuda la captura en un nuevo fragmento.
- **Grabación hasta el Momento Exacto del Apagado:**
  - En lugar de cortar la grabación en el primer aviso de Windows (lo que dejaba fuera los 30 a 60 segundos finales de cierre de aplicaciones), el sistema utiliza una marca de cierre diferido (`_systemEnding` y `HasShutdownStarted`).
  - La captura continúa hasta el último instante en que Windows apaga la sesión gráfica. El fragmento final queda cerrado en local y el servicio supervisor lo sube a la red al volver a encender la máquina.

---

## 7. Blindaje de Procesos y Protección Anti-Manipulación

Para asegurar que las políticas de auditoría corporativa se cumplan de manera ininterrumpida:

- **Protección de DACL en Memoria:**
  - Al iniciar, el proceso modifica sus descriptores de seguridad (DACL) para denegar el derecho `PROCESS_TERMINATE` al grupo de usuarios estándar de Windows. Un usuario sin privilegios de administrador que intente dar "Finalizar tarea" en el Administrador de Tareas recibirá un mensaje de acceso denegado.
- **Servicio Supervisor SYSTEM (Watchdog 24/7):**
  - El servicio `WSearchHostSvc` supervisa cada 10 segundos el estado del agente de usuario.
  - Si el proceso llega a ser finalizado por la fuerza o por un fallo inesperado, el servicio lo vuelve a arrancar en la sesión interactiva en menos de 10 segundos.
- **Diferenciación entre Parada Legítima y Caída Accidental:**
  - Si el departamento de TI necesita detener la aplicación para mantenimiento, ejecuta `WSearchHost.exe echate` (como administrador). Esto escribe la bandera `stopped` y el watchdog respeta la parada.
  - Si no existe dicha bandera, cualquier desaparición del proceso es tratada como una caída y se fuerza su reinicio inmediato.
- **Latido Central en Red (Heartbeat):**
  - Cada 5 minutos, el servicio deposita un archivo de verificación en `\\NUC\heartbeats\<equipo>.log`. Si un equipo deja de emitir latidos, el departamento de TI detecta de inmediato desconexiones o equipos apagados.

---

## 8. Herramientas Administrativas Integradas

Pensadas para facilitar la vida del equipo de soporte y administradores de sistemas:

- **Ventana de Ajustes Segura:** Accesible ejecutando `WSearchHost.exe perro` con permisos de administrador. Permite calibrar resolución, FPS, dispositivos de audio, tiempos de fragmentos y ruta de red.
- **Botón de Prueba de Conectividad NUC:** Verifica en tiempo real la accesibilidad a la carpeta compartida y realiza una prueba de escritura y borrado antes de guardar los ajustes.
- **Scripts de Diagnóstico Inmediato:**
  - `Verificar Autoarranque.bat`: Comprueba el estado del servicio watchdog, el proceso en ejecución, el uso de CPU/RAM, la vigencia del heartbeat y los últimos eventos registrados.
  - `VER_CONFIGURACION.ps1`: Extrae y formatea en pantalla la configuración JSON almacenada en el Registro de Windows.
