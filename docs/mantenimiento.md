# Guía de Mantenimiento y Resolución de Problemas: Screen Recorder

Toda infraestructura corporativa se enfrenta a imprevistos: fallos de red, actualizaciones de controladores de video, corrupción de discos o cambios de permisos. Esta guía reúne el conocimiento acumulado de mantenimiento y resolución de incidencias para mantener **Screen Recorder** operando al 100%.

---

## 1. Matriz de Diagnóstico Rápido

Antes de profundizar en logs extensos, consulta esta tabla con las causas más habituales y su solución inmediata:

| Síntoma Observado | Causa Frecuente | Solución Aplicada |
|---|---|---|
| **La aplicación no inicia ni graba.** | Existe la bandera de parada voluntaria `stopped`. | Ejecuta `WSearchHost.exe quieto` o elimina el archivo `C:\ProgramData\ScreenRecorder\stopped`. |
| **El servicio no revive al agente.** | El servicio `WSearchHostSvc` está detenido o deshabilitado en Windows. | Abre `services.msc`, localiza `WSearchHostSvc` y asegúrate de que esté en ejecución y en inicio Automático. |
| **FFmpeg inicia pero se cierra a los 2-3 segundos.** | Conflicto en el dispositivo de audio seleccionado o fallo en el driver de video. | Revisa el archivo de log. Si el problema es audio, activa "Detección automática de audio" en Ajustes. Si es video, cambia el modo de captura a GDI. |
| **Los videos se quedan en local y no llegan a la NUC.** | La ruta configurada usa una letra de unidad (p. ej. `Z:\`) o el recurso SMB no tiene permisos de escritura. | Configura una ruta UNC (`\\servidor\carpeta`) y verifica que la cuenta de la máquina tenga permisos de modificación en la carpeta compartida. |
| **El cursor del ratón parpadea al abrir Chrome o Excel.** | Interferencia de la aceleración por hardware de GPUs integradas Intel con DXGI Desktop Duplication. | En Ajustes (`WSearchHost.exe perro`), cambia el **Modo de Captura a GDI** o actualiza el controlador de gráficos Intel a la versión más reciente. |
| **El video grabado se reproduce pero no tiene sonido.** | Ningún dispositivo de audio generó sonido y no había micrófono conectado. | Normal si no hubo actividad sonora. Comprueba que WASAPI loopback y el micrófono estén habilitados en Ajustes. |
| **El Administrador de Tareas muestra varios procesos `WmvCoreHost.exe`.** | Quedó un proceso huérfano de una sesión anterior tras un cierre abrupto. | Ejecuta `WSearchHost.exe echate`, mata los procesos sobrantes y reanuda con `WSearchHost.exe quieto`. |

---

## 2. Inspección y Análisis de Logs

El sistema mantiene un registro exhaustivo de sus acciones en:
```
C:\ProgramData\ScreenRecorder\Logs\ScreenRecorder_YYYY-MM-DD.log
```

### Comandos Útiles de PowerShell para Diagnóstico en Vivo:

```powershell
# Ver las últimas 40 líneas del log del día en tiempo real:
Get-Content (Get-Item "C:\ProgramData\ScreenRecorder\Logs\ScreenRecorder_*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName -Wait -Tail 40

# Filtrar únicamente los errores y advertencias:
Get-Content (Get-Item "C:\ProgramData\ScreenRecorder\Logs\ScreenRecorder_*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName | Select-String -Pattern "ERROR|WARNING"

# Comprobar si la ruta de red responde correctamente:
Get-Content (Get-Item "C:\ProgramData\ScreenRecorder\Logs\ScreenRecorder_*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName | Select-String -Pattern "NUC|Upload|Red"
```

### Mensajes Clave en el Log y su Significado:
- `NUC OK: '\\servidor\share' accesible y con permisos de escritura:`  
  Todo funciona a la perfección. La estación de trabajo se comunica con el servidor central.
- `NUC NO DISPONIBLE:`  
  El equipo no puede alcanzar la dirección IP o nombre del servidor. Verifica conectividad física, VPN o servidor caído.
- `Streams de audio confirmados: 2/2:`  
  Tanto el audio del sistema como el micrófono están siendo codificados satisfactoriamente en el video.
- `FFmpeg murió justo después del arranque. Código: 1:`  
  FFmpeg rechazó los argumentos de entrada. Las líneas inmediatamente anteriores en el log mostrarán la salida de error estándar (`stderr`) detallando si falló un filtro, un codec o un dispositivo.

---

## 3. Mantenimiento de la Base de Datos SQLite (`uploads.db`)

La cola de envíos reside en `C:\ProgramData\ScreenRecorder\uploads.db`. En condiciones normales, el sistema se autorrepara automáticamente:
- **Disyuntor Integrado:** Si se presentan 3 fallos consecutivos en operaciones sobre SQLite, el sistema pasa automáticamente al *Modo Directo* (sube archivos examinando el disco y prescinde de la base).
- **Autorreparación:** El servicio intentará recrear la base de datos eliminando archivos temporales `-wal` o `-shm` corruptos.

### Procedimiento de Reseteo Manual (Si la Base se Corrompe Definitivamente):
Si sospechas que la base de datos se corrompió debido a un apagón severo:
1. Detén el grabador:
   ```cmd
   "C:\Program Files\ScreenRecorder\WSearchHost.exe" echate
   net stop WSearchHostSvc
   ```
2. Elimina los archivos de base de datos:
   ```powershell
   Remove-Item "C:\ProgramData\ScreenRecorder\uploads.db*" -Force
   ```
3. Vuelve a iniciar el servicio:
   ```cmd
   net start WSearchHostSvc
   "C:\Program Files\ScreenRecorder\WSearchHost.exe" quieto
   ```
4. El sistema creará un archivo `uploads.db` nuevo y el escáner de huérfanos re-descubrirá automáticamente cualquier video pendiente en disco.

---

## 4. Gestión de Espacio en Disco y Archivos Huérfanos

### ¿Dónde se Guardan los Fragmentos?
Los fragmentos se almacenan en `%TEMP%\ScreenRecorder`.
- Los archivos en curso llevan la extensión `*.recording.mp4`.
- Los archivos terminados llevan la extensión `*.mp4`.

### Limpieza Automática:
El sistema está diseñado para mantener el disco limpio:
- Cada fragmento que se sube con éxito a la NUC **es eliminado de inmediato del disco local**.
- Al arrancar y cada 10 minutos, tanto el agente como el servicio supervisor buscan archivos que hayan quedado sin subir de sesiones anteriores y los envían a la red.

### ¿Qué Hacer si se Acumulan Archivos en `%TEMP%`?
Si un equipo estuvo semanas sin conexión de red o con la ruta NUC caída, es posible que la carpeta `%TEMP%\ScreenRecorder` contenga varios gigabytes acumulados:
1. Restablece la conectividad hacia la ruta NUC.
2. El sistema comenzará a subir los fragmentos en segundo plano de forma progresiva.
3. Puedes acelerar el proceso ejecutando `WSearchHost.exe quieto`.

---

## 5. Procedimiento de Actualización de Versión

Para actualizar una máquina a una versión superior (por ejemplo, al compilar un nuevo MSI):

1. **No es necesario desinstalar la versión anterior manualmente:**  
   El MSI incluye la directiva `MajorUpgrade` con `AllowSameVersionUpgrades="yes"`.
2. Al ejecutar el nuevo instalador:
   - Una acción personalizada (`CustomAction`) envía la orden de parada ordenada por el Named Pipe.
   - Espera a que los archivos se liberen para evitar el diálogo "Archivos en uso".
   - Reemplaza los ejecutables por la nueva versión.
   - Borra la bandera de parada y arranca la nueva versión con el build actualizado.
3. **La configuración previa se preserva al 100%:**  
   Dado que los ajustes residen en `HKLM\SOFTWARE\ScreenRecorder`, la nueva versión arrancará de inmediato utilizando la misma ruta NUC, resolución y opciones configuradas originalmente.
