# Script para ver la configuración guardada en Registry
# Ejecutar como Administrador

Write-Host "=== Configuración de Screen Recorder ===" -ForegroundColor Cyan
Write-Host ""

$registryPath = "HKLM:\SOFTWARE\ScreenRecorder"
$valueName = "Settings"

try {
    $settingsJson = Get-ItemProperty -Path $registryPath -Name $valueName -ErrorAction Stop | Select-Object -ExpandProperty $valueName
    
    if ($settingsJson) {
        Write-Host "Configuración encontrada:" -ForegroundColor Green
        Write-Host ""
        
        # Intentar formatear el JSON
        try {
            $settingsObj = $settingsJson | ConvertFrom-Json
            Write-Host "Configuración (formateada):" -ForegroundColor Yellow
            $settingsJson | ConvertFrom-Json | ConvertTo-Json -Depth 10 | Write-Host
        }
        catch {
            Write-Host "JSON raw:" -ForegroundColor Yellow
            Write-Host $settingsJson
        }
        
        Write-Host ""
        Write-Host "=== Análisis de Configuración ===" -ForegroundColor Cyan
        Write-Host ""
        
        if ($settingsObj) {
            Write-Host "Resolución: $($settingsObj.Resolution)p" -ForegroundColor White
            Write-Host "FPS: $($settingsObj.Fps)" -ForegroundColor White
            Write-Host "Duración de fragmento: $($settingsObj.FragmentDurationMinutes) minutos" -ForegroundColor White
            Write-Host ""
            Write-Host "Audio:" -ForegroundColor Yellow
            Write-Host "  - Detección automática: $($settingsObj.AutoDetectAudio)" -ForegroundColor White
            Write-Host "  - Incluir audio del sistema: $($settingsObj.IncludeSystemAudio)" -ForegroundColor White
            Write-Host "  - Incluir micrófono: $($settingsObj.IncludeMicrophone)" -ForegroundColor White
            Write-Host "  - Selección manual: $($settingsObj.ManualAudioSelection)" -ForegroundColor White
            
            if ($settingsObj.SystemAudioDevice) {
                Write-Host "  - Dispositivo sistema (manual): $($settingsObj.SystemAudioDevice)" -ForegroundColor White
            }
            if ($settingsObj.MicrophoneDevice) {
                Write-Host "  - Dispositivo micrófono (manual): $($settingsObj.MicrophoneDevice)" -ForegroundColor White
            }
            Write-Host ""
            Write-Host "Ruta NUC: $($settingsObj.NetworkPath)" -ForegroundColor White
        }
    }
    else {
        Write-Host "No se encontró configuración guardada. Se usará la configuración por defecto." -ForegroundColor Yellow
    }
}
catch {
    Write-Host "Error al leer configuración: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "Posibles causas:" -ForegroundColor Yellow
    Write-Host "1. La aplicación nunca se ha ejecutado con permisos de administrador"
    Write-Host "2. No se ha guardado configuración aún"
    Write-Host "3. El registro no existe en: $registryPath"
}

Write-Host ""
Write-Host "Para modificar la configuración, ejecuta la aplicación y ve a: Ajustes (requiere admin)" -ForegroundColor Cyan
