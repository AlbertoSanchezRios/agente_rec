using System.Threading.Tasks;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Servicio de gestión de archivos (temporal y upload a NUC)
/// </summary>
public interface IFileManagementService
{
    Task UploadFileAsync(string filePath);
    Task<bool> TryUploadFileAsync(string filePath, int maxRetries = 3);

    /// <summary>
    /// Sube un archivo forzando la ruta de red destino (<paramref name="overrideNetworkPath"/>).
    /// Si es null o vacío, usa la ruta configurada en el registro.
    /// </summary>
    Task<bool> TryUploadFileAsync(string filePath, string? overrideNetworkPath, int maxRetries = 3);

    /// <summary>
    /// Verificación temprana (al iniciar la grabación) de la ruta NUC configurada:
    /// existencia y permiso de escritura real, en el contexto del proceso que graba.
    /// Loguea un veredicto único para diagnóstico remoto.
    /// </summary>
    Task PreflightNetworkPathAsync();
}
