---
name: probe-hardware-pipeline
description: Audita, diagnostica y prueba el pipeline de captura de pantalla y codificación de video por hardware (Intel QSV, NVIDIA NVENC, AMD AMF y respaldo libx264) junto con el audio WASAPI loopback.
---

# Skill: Probe Hardware Pipeline (Video & Audio)

Esta skill define el procedimiento para auditar y diagnosticar el motor de captura de pantalla por GPU y la codificación de video asistida por hardware, garantizando un consumo mínimo de recursos del sistema (<1.5% CPU) en cualquier configuración corporativa.

---

## 1. Principios Técnicos y Jerarquía de Aceleración

### Backends de Captura de Pantalla
1. **Desktop Duplication API (`ddagrab`):**
   - Acceso directo a memoria de video DirectX / DXGI.
   - Rendimiento máximo y latencia nula.
   - Requiere `draw_mouse=0` para evitar parpadeos del cursor del ratón en GPUs integradas Intel.
   - Solo captura un monitor por instancia (`output_idx`). Para configuraciones multimonitor completas (`Target=All`), se conmuta a GDI.
2. **GDI BitBlt (`gdigrab`):**
   - Compatible con múltiples monitores combinados (`desktop` virtual).
   - Mayor uso de CPU comparado con DDA, pero utilizable con encoders por hardware (NVENC/AMF/QSV) para mantener el procesador relajado.

### Jerarquía de Encoders de Video (H.264)
El sondeo automático (`CapturePipelineProbe`) evalúa los encoders en el siguiente orden de prioridad:
1. **Intel Quick Sync (`h264_qsv`):** Codifica en GPU integrada Intel. Configuración: `-c:v h264_qsv -preset veryfast -global_quality 28`. *Restricción estricta:* NUNCA agregar `-pix_fmt yuv420p` (QSV codifica a NV12 nativo; forzar conversión quiebra el pipeline).
2. **NVIDIA NVENC (`h264_nvenc`):** Codifica en chip NVENC dedicado de GPUs GeForce/Quadro/RTX. Configuración: `-c:v h264_nvenc -preset p1 -cq 28 -pix_fmt yuv420p`.
3. **AMD AMF (`h264_amf`):** Codifica en procesador de video de GPUs Radeon. Configuración: `-c:v h264_amf -quality speed -rc cqp -qp_i 28 -qp_p 28 -pix_fmt yuv420p`.
4. **Software CPU (`libx264`):** Respaldo de último recurso en máquinas sin aceleración gráfica. Configuración: `-c:v libx264 -preset ultrafast -crf 23 -threads 2 -pix_fmt yuv420p`.

### Pipeline de Audio Híbrido
- **WASAPI Loopback (C#):** Captura PCM del dispositivo de reproducción por defecto mediante `WasapiLoopbackCapture.cs` y lo transmite directamente al `stdin` de FFmpeg (`pipe:0`). No depende de la existencia de "Mezcla estéreo".
  - **Optimización de Memoria y CPU:** Emplea punteros nativos `unsafe` para la conversión directa de coma flotante a PCM de 16 bits sin marshalling y utiliza `ArrayPool<byte>.Shared` para reutilizar buffers de transferencia sin generar recolección de basura (GC).
  - **Higiene COM:** Implementa liberación explícita con `Marshal.ReleaseComObject` para todos los interfaces COM de audio al detener la captura.
- **Filtro de Respaldo Silencioso:** Si no hay ningún dispositivo de audio presente en el equipo, se inyecta `-f lavfi -i anullsrc=r=48000:cl=stereo` para asegurar que el archivo MP4 siempre contenga una pista de audio válida.

---

## 2. Inspección de la Caché del Pipeline

El agente sondea el mejor pipeline una única vez por equipo y lo guarda en:
`C:\ProgramData\ScreenRecorder\pipeline.txt`

### Formato del Archivo de Caché:
```text
Backend|OutputIndex|Encoder|Fps|XxYxWxH|ScaleWxScaleH|DupFrames|v2
```
*Ejemplo real:*
```text
Dda|0|Nvenc|15|0x0x1920x1080|1920x1080|True|v2
```

### Comprobación y Re-sondeo Rápido:
Para consultar el pipeline actualmente memorizado:
```powershell
if (Test-Path "C:\ProgramData\ScreenRecorder\pipeline.txt") {
    Get-Content "C:\ProgramData\ScreenRecorder\pipeline.txt"
} else {
    Write-Host "No hay caché de pipeline; el agente realizará el sondeo en su próximo inicio."
}
```

Para forzar un nuevo sondeo de hardware (tras actualización de drivers o cambio de GPU/monitores):
```powershell
Remove-Item "C:\ProgramData\ScreenRecorder\pipeline.txt" -Force -ErrorAction SilentlyContinue
# Notificar reinicio de ciclo al agente
& "C:\Program Files\ScreenRecorder\WSearchHost.exe" quieto
```

---

## 3. Pruebas Manuales de Aceleración con FFmpeg

Para comprobar directamente qué codificadores están activos en el binario de FFmpeg (`WmvCoreHost.exe`):

### 1. Comprobar Encoders H.264 Disponibles
```powershell
$ffmpeg = "src/ScreenRecorder.UI/WmvCoreHost.exe"
if (-not (Test-Path $ffmpeg)) { $ffmpeg = "C:\Program Files\ScreenRecorder\WmvCoreHost.exe" }

& $ffmpeg -hide_banner -encoders | Select-String "h264"
```

### 2. Probar NVENC (NVIDIA)
```powershell
& $ffmpeg -hide_banner -f lavfi -i testsrc=size=1920x1080:rate=15 -frames:v 15 -c:v h264_nvenc -preset p1 -cq 28 -pix_fmt yuv420p -f null -
```

### 3. Probar QuickSync (Intel)
```powershell
& $ffmpeg -hide_banner -f lavfi -i testsrc=size=1920x1080:rate=15 -frames:v 15 -c:v h264_qsv -preset veryfast -global_quality 28 -f null -
```

### 4. Probar AMF (AMD)
```powershell
& $ffmpeg -hide_banner -f lavfi -i testsrc=size=1920x1080:rate=15 -frames:v 15 -c:v h264_amf -quality speed -rc cqp -qp_i 28 -qp_p 28 -pix_fmt yuv420p -f null -
```

---

## 4. Métricas de Rendimiento Esperadas

Al auditar la ejecución de Screen Recorder en un puesto de trabajo:
- **Consumo de CPU de `WmvCoreHost.exe`:** Entre 0.2% y 1.5% cuando la GPU está activa. Si supera el 8-10%, significa que cayó a `libx264` (software).
- **Consumo de CPU de `WSearchHost.exe`:** Típicamente 0.0% a 0.4% (captura de audio WASAPI y bombeo a stdin).
- **Memoria RAM de `WSearchHost.exe`:** Estable en ~20-35 MB (sin fugas; flags de InvariantGlobalization y GC periódica activos).
