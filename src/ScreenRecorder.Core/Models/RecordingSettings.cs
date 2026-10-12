using System;

namespace ScreenRecorder.Core.Models;

/// <summary>
/// Configuración de grabación de pantalla
/// </summary>
public class RecordingSettings
{
    /// <summary>
    /// Altura objetivo de la grabación en píxeles ("p"): 1080 = 1080p, 720 = 720p.
    /// El ancho se deriva de la proporción real de la región (se escala, no se recorta).
    /// 0 = resolución NATIVA de la pantalla (sin reescalado; recomendado).
    /// </summary>
    public int Resolution { get; set; } = 0;
    public int Fps { get; set; } = 15;
    /// <summary>Qué monitor (o todo el escritorio) se graba.</summary>
    public CaptureTarget CaptureTarget { get; set; } = CaptureTarget.Primary;
    /// <summary>
    /// Modo de captura: 0 = Automática (ddagrab+QSV -> ddagrab+x264 -> GDI),
    /// 1 = GPU (ddagrab), 2 = GDI (gdigrab; sin parpadeo del cursor, más CPU).
    /// </summary>
    public int CaptureBackendPreference { get; set; } = 0;
    /// <summary>
    /// dup_frames=0: solo se codifican frames nuevos cuando la pantalla cambia.
    /// Ahorra muchísima CPU en escritorios de oficina (pantallas mayormente estáticas).
    /// Produce framerate variable (solo con ddagrab; gdigrab no lo soporte y graba a fps fijo).
    /// Activado por defecto; desactívalo si necesitas timestamps constantes.
    /// </summary>
    public bool RecordOnlyOnChanges { get; set; } = true;
    /// <summary>Duración de cada fragmento de grabación en minutos (ej. 30 = fragmentos de 30 min).</summary>
    public int FragmentDurationMinutes { get; set; } = 30;
    public bool AutoDetectAudio { get; set; } = true;
    public bool IncludeSystemAudio { get; set; } = true;
    public bool IncludeMicrophone { get; set; } = true;
    public bool ManualAudioSelection { get; set; } = false;
    public string? SystemAudioDevice { get; set; }
    public string? MicrophoneDevice { get; set; }
    public string? NetworkPath { get; set; }
    public bool UseUsernameInFilename { get; set; } = false;
    public bool UseMachineNameInFilename { get; set; } = false;
    public bool UseUsernameAndMachineNameInFilename { get; set; } = true;
    public bool UseCustomFilename { get; set; } = false;
    public string? CustomFilenamePrefix { get; set; }
}
