namespace ScreenRecorder.Core.Models;

/// <summary>
/// Representa un dispositivo de audio detectado (nombre amigable + nombre alternativo dshow para ffmpeg).
/// </summary>
public class AudioDevice
{
    /// <summary>Nombre mostrado (ej. "Micrófono (Realtek)").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Identificador para ffmpeg: audio="Nombre" o audio="@device_cm_...". Preferir nombre alternativo si existe.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Nombre alternativo DirectShow (ej. @device_cm_{GUID}\wave_{GUID}). Más fiable para captura.</summary>
    public string? AlternativeName { get; set; }

    public AudioDeviceType Type { get; set; }
    public bool IsUsb { get; set; }
}

/// <summary>
/// Tipo de dispositivo de audio
/// </summary>
public enum AudioDeviceType
{
    SystemAudio,
    Microphone
}
