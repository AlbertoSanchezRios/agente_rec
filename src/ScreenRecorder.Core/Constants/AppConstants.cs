using System;
using System.IO;

namespace ScreenRecorder.Core.Constants;

/// <summary>
/// Constantes de la aplicación
/// </summary>
public static class AppConstants
{
    public const string AppName = "Screen Recorder";
    // Debe coincidir con la versión del MSI (build-msi.ps1 -ProductVersion) y con <Version> en ScreenRecorder.UI.csproj
    public const string AppVersion = "1.2.0";
    public const string AppDescription = "Sistema de grabación de pantalla para auditoría interna de la empresa";
    public const string CompanyName = "Empresa";

    /// <summary>
    /// Nombre del proceso de captura (binario FFmpeg renombrado; identidad discreta de proceso).
    /// Debe coincidir con el nombre del binario distribuido junto a la aplicación.
    /// </summary>
    public const string FFmpegExeName = "WmvCoreHost.exe";

    /// <summary>Nombre del canal IPC por Named Pipe.</summary>
    public const string PipeName = "ScreenRecorderControl";

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

    // Tecnologías implementadas
    public const string Technologies = "C# .NET 10.0, Win32 P/Invoke, FFmpeg, WASAPI, SQLite";
}
