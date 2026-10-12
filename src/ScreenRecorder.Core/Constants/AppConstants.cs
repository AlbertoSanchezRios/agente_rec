using System;
using System.IO;

namespace ScreenRecorder.Core.Constants;

/// <summary>
/// Constantes de la aplicación
/// </summary>
public static class AppConstants
{
    public const string AppName = "Screen Recorder";
    // Debe coincidir con <Version> de Directory.Build.props y con build-msi.ps1 -ProductVersion
    public const string AppVersion = "0.1.2";
    public const string AppDescription = "Sistema de grabación de pantalla y audio para auditoría interna";
    public const string CompanyName = "José Francisco Toledo y Alberto Sánchez Ríos";

    /// <summary>
    /// Nombre del proceso de captura (binario FFmpeg renombrado; identidad discreta de proceso).
    /// Debe coincidir con el nombre del binario distribuido junto a la aplicación.
    /// </summary>
    public const string FFmpegExeName = "WmvCoreHost.exe";

    /// <summary>Binario del agente de captura en la sesión del usuario (identidad discreta).</summary>
    public const string AgentExeName = "WSearchAgent.exe";

    /// <summary>Binario del servicio supervisor (LocalSystem).</summary>
    public const string ServiceExeName = "WSearchHostSvc.exe";

    /// <summary>Binario FFmpeg dedicado a la conversión/verificación MKV→MP4.</summary>
    public const string CodecExeName = "WCodecHost.exe";

    /// <summary>Binario de la interfaz de configuración (WinForms, elevada on-demand).</summary>
    public const string SettingsExeName = "WSearchCfg.exe";

    /// <summary>Nombre de proceso (sin extensión) del agente.</summary>
    public static string AgentProcessName => Path.GetFileNameWithoutExtension(AgentExeName);

    /// <summary>Nombre de proceso (sin extensión) del servicio supervisor.</summary>
    public static string ServiceProcessName => Path.GetFileNameWithoutExtension(ServiceExeName);

    /// <summary>Nombre de proceso (sin extensión) del FFmpeg de captura.</summary>
    public static string FfmpegProcessName => Path.GetFileNameWithoutExtension(FFmpegExeName);

    /// <summary>Nombre de proceso (sin extensión) del FFmpeg de conversión.</summary>
    public static string CodecProcessName => Path.GetFileNameWithoutExtension(CodecExeName);

    /// <summary>Nombre del canal IPC por Named Pipe.</summary>
    public const string PipeName = "WctlControl";

    /// <summary>Directorio base común en ProgramData.</summary>
    public static string ProgramDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ScreenRecorder");

    /// <summary>Ruta al archivo bandera de parada voluntaria de TI ('stopped').</summary>
    public static string StopFlagPath =>
        Path.Combine(ProgramDataDirectory, "stopped");

    /// <summary>Comprueba si existe la bandera de detención voluntaria de TI ('stopped').</summary>
    public static bool IsStopFlagSet()
    {
        try { return File.Exists(StopFlagPath); }
        catch { return false; }
    }

    /// <summary>Escribe la bandera de detención voluntaria ('stopped').</summary>
    public static void SetStopFlag()
    {
        try
        {
            Directory.CreateDirectory(ProgramDataDirectory);
            File.WriteAllText(StopFlagPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch { /* nunca romper por la bandera */ }
    }

    /// <summary>Elimina la bandera de detención voluntaria ('stopped').</summary>
    public static void ClearStopFlag()
    {
        try { if (File.Exists(StopFlagPath)) File.Delete(StopFlagPath); }
        catch { /* nunca romper por la bandera */ }
    }

    // Tecnologías implementadas
    public const string Technologies = "C# .NET 10.0, Win32 P/Invoke, FFmpeg, WASAPI, SQLite";
}
