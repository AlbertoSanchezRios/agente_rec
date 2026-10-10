using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Procesa un fragmento hasta dejarlo entregado en la NUC: si es MKV lo convierte a MP4
/// verificado y lo sube (borrando el MKV solo tras el éxito); si ya es MP4, lo sube directamente.
/// Encapsula la conversión + subida para reutilizarla desde el agente y el watchdog.
/// </summary>
public sealed class FragmentPipelineService
{
    private readonly ConversionService _conversion;
    private readonly IFileManagementService _fileManagement;
    private readonly ILoggerService _logger;

    public FragmentPipelineService(ILoggerService logger, IFileManagementService fileManagement)
    {
        _logger = logger;
        _fileManagement = fileManagement;
        _conversion = new ConversionService(logger);
    }

    public bool ConversionAvailable => _conversion.IsAvailable;

    /// <summary>
    /// Convierte (si hace falta) y sube el fragmento. Devuelve true solo si quedó entregado en la
    /// NUC. En caso de fallo, conserva el archivo local para reintentar.
    /// </summary>
    public async Task<bool> ProcessAsync(string path, int maxRetries = 3, CancellationToken cancellationToken = default)
    {
        if (path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
        {
            var mp4 = await _conversion.ConvertMkvToMp4Async(path, cancellationToken);
            if (mp4 == null)
            {
                return false; // el MKV se conserva; se reintentará
            }

            var ok = await _fileManagement.TryUploadFileAsync(mp4, maxRetries);
            if (ok)
            {
                TryDelete(path); // el MKV solo se borra tras verificar y subir el MP4
            }
            return ok;
        }

        return await _fileManagement.TryUploadFileAsync(path, maxRetries);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* no crítico */ }
    }
}
