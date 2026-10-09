using ScreenRecorder.Core.Models;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Servicio de gestión de configuración
/// </summary>
public interface ISettingsService
{
    RecordingSettings LoadSettings();
    void SaveSettings(RecordingSettings settings);
    bool IsNetworkPathConfigured();
}
