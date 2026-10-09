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
    
    // Tecnologías implementadas
    public const string Technologies = "C# .NET 10.0, Windows Forms, FFmpeg, DirectShow";
}
