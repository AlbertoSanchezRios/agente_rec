using System;
using System.IO;
using System.Threading.Tasks;
using ScreenRecorder.Core.Services;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Servicio principal que coordina la grabación y eventos de Windows
/// </summary>
public class RecordingManagerService : IRecordingManagerService
{
    private readonly IRecordingService _recordingService;
    private readonly IWindowsEventMonitorService _eventMonitorService;
    private readonly ILoggerService _logger;
    private readonly ISettingsService _settingsService;

    public bool IsRecordingActive { get; private set; }

    public RecordingManagerService(
        IRecordingService recordingService,
        IWindowsEventMonitorService eventMonitorService,
        ILoggerService logger,
        ISettingsService settingsService)
    {
        _recordingService = recordingService;
        _eventMonitorService = eventMonitorService;
        _logger = logger;
        _settingsService = settingsService;

        // Suscribirse a eventos
        _eventMonitorService.UserLoggedIn += OnUserLoggedIn;
        _eventMonitorService.SessionLocked += OnSessionLocked;
        _eventMonitorService.SessionUnlocked += OnSessionUnlocked;
        _eventMonitorService.SystemShuttingDown += OnSystemShuttingDown;
        _eventMonitorService.SystemSuspending += OnSystemSuspending;
        _eventMonitorService.SystemResumed += OnSystemResumed;
        _eventMonitorService.DisplayChanged += OnDisplayChanged;
    }

    public void Start()
    {
        try
        {
            _logger.LogInfo("Iniciando RecordingManagerService");
            
            // Iniciar monitoreo de eventos
            _eventMonitorService.StartMonitoring();
            
            // Iniciar grabación al iniciar (simula login)
            OnUserLoggedIn(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al iniciar RecordingManagerService", ex);
        }
    }

    public void Stop()
    {
        try
        {
            _logger.LogInfo("Deteniendo RecordingManagerService");

            // Detener grabación y ESPERAR a que FFmpeg termine de cerrar el archivo y a que el
            // último fragmento se suba a la NUC. El presupuesto (90 s) cubre la cadena real
            // (cierre de FFmpeg + File.Copy SMB de cientos de MB); antes eran 15 s, siempre
            // insuficientes, y el fragmento se perdía al salir. Si se agota, el fragmento queda
            // cerrado en temporal y la recuperación de huérfanos lo sube al rearrancar.
            // Es síncrono para que un Start() posterior (reinicio con 'quieto') no entre en carrera.
            if (IsRecordingActive)
            {
                var stopTask = Task.Run(async () => await _recordingService.StopRecordingAsync());
                Task.WhenAny(stopTask, Task.Delay(90000)).Wait();
                IsRecordingActive = false;
            }

            // Detener monitoreo
            _eventMonitorService.StopMonitoring();
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al detener RecordingManagerService", ex);
        }
    }

    private async void OnUserLoggedIn(object? sender, EventArgs e)
    {
        if (!IsRecordingActive)
        {
            try
            {
                _logger.LogInfo("Iniciando grabación por login de usuario");
                IsRecordingActive = true;
                await _recordingService.StartRecordingAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("Error al iniciar grabación por login", ex);
                IsRecordingActive = false;
            }
        }
    }

    private async void OnSessionLocked(object? sender, EventArgs e)
    {
        if (IsRecordingActive)
        {
            try
            {
                _logger.LogInfo("Deteniendo grabación por bloqueo de sesión");
                await _recordingService.StopRecordingAsync();
                IsRecordingActive = false;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error al detener grabación por bloqueo", ex);
            }
        }
    }

    private async void OnSessionUnlocked(object? sender, EventArgs e)
    {
        if (!IsRecordingActive)
        {
            try
            {
                _logger.LogInfo("Reanudando grabación por desbloqueo de sesión");
                IsRecordingActive = true;
                await _recordingService.StartRecordingAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("Error al reanudar grabación por desbloqueo", ex);
                IsRecordingActive = false;
            }
        }
    }

    private async void OnDisplayChanged(object? sender, EventArgs e)
    {
        try
        {
            if (!IsRecordingActive) return;
            // Resolución dinámica: cerrar el fragmento actual y rearrancar el ciclo; el
            // sondeo de pipeline recalcula la geometría física nueva (y re-sondea si la
            // región cambió) sin necesidad de 'quieto' manual.
            _logger.LogInfo("Cambio de pantalla: reiniciando el ciclo de grabación con la nueva geometría");
            await _recordingService.StopRecordingAsync();
            IsRecordingActive = true;
            await _recordingService.StartRecordingAsync();
            _logger.LogInfo("Grabación reiniciada tras el cambio de pantalla");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al reiniciar la grabación tras cambio de pantalla", ex);
            IsRecordingActive = false;
        }
    }

    private async void OnSystemSuspending(object? sender, EventArgs e)
    {
        if (IsRecordingActive)
        {
            try
            {
                // Windows da muy poco tiempo antes de dormir: cierre rápido (fragmento local
                // íntegro, subida aplazada al reanudar). Así NO se graba nada en suspensión.
                _logger.LogInfo("Suspensión: cerrando FFmpeg de inmediato (la grabación no corre mientras el equipo duerme)");
                _recordingService.RequestFastShutdown();
                IsRecordingActive = false;
                await Task.Delay(3000);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error al cerrar la grabación por suspensión", ex);
            }
        }
    }

    private async void OnSystemResumed(object? sender, EventArgs e)
    {
        try
        {
            // No reanudar si hay detención voluntaria legítima ('echate').
            if (Constants.AppConstants.IsStopFlagSet())
            {
                _logger.LogInfo("Reanudado del sistema, pero hay detención voluntaria ('stopped'): no se inicia la grabación.");
                return;
            }
            if (!IsRecordingActive)
            {
                _logger.LogInfo("Reanudado del sistema: reiniciando la grabación");
                IsRecordingActive = true;
                await _recordingService.StartRecordingAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al reanudar la grabación tras suspensión", ex);
            IsRecordingActive = false;
        }
    }

    private void OnSystemShuttingDown(object? sender, EventArgs e)
    {
        try
        {
            // NO detener la grabación: debe cubrir hasta el último instante visible (pantalla
            // de "Apagando" incluida). Solo se marca el flag para que el servicio no arranque
            // fragmentos nuevos y cierre el actual sin subir al final; la recuperación de
            // huérfanos sube el fragmento final al rearrancar. El MP4 fragmentado queda íntegro
            // aunque Windows mate a FFmpeg. Síncrono y sin esperas: la sesión puede morir en
            // cualquier momento a partir de aquí.
            _logger.LogInfo("Apagado/cierre de sesión: la grabación continúa hasta el apagado final");
            _recordingService.NotifySystemEnding();
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al notificar el apagado del sistema", ex);
        }
    }
}
