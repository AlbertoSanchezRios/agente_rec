using System;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Servicio de monitoreo de eventos de Windows
/// </summary>
public interface IWindowsEventMonitorService
{
    event EventHandler? UserLoggedIn;
    event EventHandler? SessionLocked;
    event EventHandler? SessionUnlocked;
    event EventHandler? SystemShuttingDown;
    /// <summary>El sistema va a suspenderse: cerrar la grabación YA (sin subida SMB).</summary>
    event EventHandler? SystemSuspending;
    /// <summary>El sistema se reanudó desde la suspensión: reiniciar la grabación.</summary>
    event EventHandler? SystemResumed;
    /// <summary>Cambió la configuración de pantalla (resolución, monitores, escalado): re-sondear geometría.</summary>
    event EventHandler? DisplayChanged;
    void StartMonitoring();
    void StopMonitoring();
}
