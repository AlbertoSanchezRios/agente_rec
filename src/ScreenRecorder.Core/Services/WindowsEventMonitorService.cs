using System;
using System.Threading;
using Microsoft.Win32;
using ScreenRecorder.Core.Services;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Implementación del servicio de monitoreo de eventos de Windows
/// </summary>
public class WindowsEventMonitorService : IWindowsEventMonitorService
{
    private readonly ILoggerService _logger;
    private bool _isMonitoring;

    public event EventHandler? UserLoggedIn;
    public event EventHandler? SessionLocked;
    public event EventHandler? SessionUnlocked;
    public event EventHandler? SystemShuttingDown;
    public event EventHandler? SystemSuspending;
    public event EventHandler? SystemResumed;
    public event EventHandler? DisplayChanged;

    public WindowsEventMonitorService(ILoggerService logger)
    {
        _logger = logger;
    }

    public void StartMonitoring()
    {
        if (_isMonitoring)
        {
            return;
        }

        try
        {
            // Monitorear eventos de sesión y de energía de Windows
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.SessionEnding += OnSessionEnding;
            SystemEvents.SessionEnded += OnSessionEnded;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            _isMonitoring = true;
            _logger.LogInfo("Monitoreo de eventos de Windows iniciado (sesión + energía)");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al iniciar monitoreo de eventos de Windows", ex);
        }
    }

    public void StopMonitoring()
    {
        if (!_isMonitoring)
        {
            return;
        }

        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.SessionEnding -= OnSessionEnding;
            SystemEvents.SessionEnded -= OnSessionEnded;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

            _isMonitoring = false;
            _logger.LogInfo("Monitoreo de eventos de Windows detenido");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al detener monitoreo de eventos de Windows", ex);
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                _logger.LogInfo("Sesión bloqueada");
                SessionLocked?.Invoke(this, EventArgs.Empty);
                break;

            case SessionSwitchReason.SessionUnlock:
                _logger.LogInfo("Sesión desbloqueada");
                SessionUnlocked?.Invoke(this, EventArgs.Empty);
                break;

            case SessionSwitchReason.SessionLogon:
                _logger.LogInfo("Usuario inició sesión");
                UserLoggedIn?.Invoke(this, EventArgs.Empty);
                break;

            case SessionSwitchReason.SessionLogoff:
                _logger.LogInfo("Usuario cerró sesión");
                break;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            _logger.LogInfo("Cambió la configuración de pantalla (resolución/monitores/escalado)");
            DisplayChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al manejar el cambio de pantalla", ex);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        try
        {
            if (e.Mode == PowerModes.Suspend)
            {
                _logger.LogInfo("El sistema se va a SUSPENDER: cerrando la grabación ahora");
                SystemSuspending?.Invoke(this, EventArgs.Empty);
            }
            else if (e.Mode == PowerModes.Resume)
            {
                _logger.LogInfo("El sistema se REANUDÓ desde la suspensión");
                SystemResumed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al manejar el evento de energía", ex);
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        // SystemShutdown (apagar/reiniciar) Y Logoff (cerrar sesión): en ambos casos la sesión
        // gráfica desaparece. La grabación NO se cierra aquí: continúa hasta que Windows mate
        // los procesos de la sesión, para cubrir el apagado hasta el último instante visible.
        if (e.Reason == Microsoft.Win32.SessionEndReasons.SystemShutdown ||
            e.Reason == Microsoft.Win32.SessionEndReasons.Logoff)
        {
            _logger.LogInfo($"Sesión terminando ({e.Reason}): la grabación continúa hasta el final");
            SystemShuttingDown?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSessionEnded(object sender, SessionEndedEventArgs e)
    {
        // WM_ENDSESSION: el apagado/cierre de sesión es irrevocable y quedan ~5 s antes de que
        // Windows mate la sesión. Refuerzo por si SessionEnding no llegó (alguna apps lo
        // omiten); NotifySystemEnding es idempotente.
        if (e.Reason == Microsoft.Win32.SessionEndReasons.SystemShutdown ||
            e.Reason == Microsoft.Win32.SessionEndReasons.Logoff)
        {
            _logger.LogInfo($"Sesión terminada ({e.Reason}): confirmado; la grabación sigue hasta el apagado final");
            SystemShuttingDown?.Invoke(this, EventArgs.Empty);
        }
    }
}
