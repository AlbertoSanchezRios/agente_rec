using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ScreenRecorder.Core.Services;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Implementación del servicio de gestión de archivos
/// </summary>
public class FileManagementService : IFileManagementService
{
    private readonly ILoggerService _logger;
    private readonly ISettingsService _settingsService;

    /// <summary>Ruta destino REAL (ya resuelta) del último upload exitoso. Para mostrar
    /// al usuario exactamente dónde llegó el archivo (puede diferir de la configurada si
    /// se resolvió una letra de unidad a UNC).</summary>
    public string? LastUploadDestination { get; private set; }

    public FileManagementService(ILoggerService logger, ISettingsService settingsService)
    {
        _logger = logger;
        _settingsService = settingsService;
    }

    public async Task UploadFileAsync(string filePath)
    {
        await TryUploadFileAsync(filePath);
    }

    public async Task<bool> TryUploadFileAsync(string filePath, int maxRetries = 3)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning($"Archivo no encontrado para upload: {filePath}");
            return false;
        }

        var settings = _settingsService.LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.NetworkPath))
        {
            _logger.LogWarning("Ruta de red NUC no configurada. El archivo permanecerá en carpeta temporal.");
            return false;
        }

        var fileName = Path.GetFileName(filePath);

        // Resolver la ruta efectiva: si es una letra de unidad no visible para este proceso
        // (p. ej. app elevada) pero su UNC sí es accesible, subir por el UNC.
        var effectiveNetworkPath = ResolveEffectiveNetworkPath(settings.NetworkPath);
        var destinationPath = Path.Combine(effectiveNetworkPath, fileName);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInfo($"Intento {attempt}/{maxRetries} de upload: {fileName}");

                // Verificar si la ruta de red es accesible
                if (!Directory.Exists(effectiveNetworkPath))
                {
                    _logger.LogWarning($"Ruta de red no accesible: {effectiveNetworkPath}");
                    await Task.Delay(5000 * attempt); // Esperar más tiempo en cada reintento
                    continue;
                }

                // Copiar archivo a la ruta de red
                await Task.Run(() => File.Copy(filePath, destinationPath, overwrite: true));
                
                _logger.LogInfo($"Archivo subido exitosamente: {destinationPath}");

                // Esperar un momento para asegurar que el archivo se escribió completamente
                await Task.Delay(1000);

                // Verificar que el archivo existe en destino
                if (File.Exists(destinationPath))
                {
                    LastUploadDestination = destinationPath;
                    // Borrar archivo local después de upload exitoso
                    try
                    {
                        File.Delete(filePath);
                        _logger.LogInfo($"Archivo local eliminado: {filePath}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error al eliminar archivo local: {filePath}", ex);
                    }

                    return true;
                }
                else
                {
                    _logger.LogWarning($"Archivo no encontrado en destino después de copiar: {destinationPath}");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError($"Sin permisos para acceder a la ruta de red: {effectiveNetworkPath}. Diagnóstico: {NetworkPathHelper.BuildInaccessibleDiagnostics(settings.NetworkPath)}", ex);
                break; // No reintentar si es problema de permisos
            }
            catch (IOException ex)
            {
                _logger.LogWarning($"Error de IO en intento {attempt}: {ex.Message}");
                if (attempt < maxRetries)
                {
                    await Task.Delay(5000 * attempt); // Esperar más tiempo en cada reintento
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error en intento {attempt} de upload", ex);
                if (attempt < maxRetries)
                {
                    await Task.Delay(5000 * attempt);
                }
            }
        }

        _logger.LogError($"No se pudo subir el archivo después de {maxRetries} intentos: {filePath}. La cola lo reintentará automáticamente (agente cada 2 min, servicio cada 10 min, y al rearrancar).");
        return false;
    }

    public async Task PreflightNetworkPathAsync()
    {
        var settings = _settingsService.LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.NetworkPath))
        {
            _logger.LogWarning("NUC NO CONFIGURADO: no hay ruta de red configurada. Los videos quedarán en la carpeta temporal. Configure la ruta con el comando 'perro'.");
            return;
        }

        var effectivePath = ResolveEffectiveNetworkPath(settings.NetworkPath);

        if (!Directory.Exists(effectivePath))
        {
            _logger.LogWarning($"NUC NO DISPONIBLE: {NetworkPathHelper.BuildInaccessibleDiagnostics(settings.NetworkPath)}. Los videos quedarán en la carpeta temporal y se subirán automáticamente cuando la ruta esté disponible.");
            return;
        }

        // Prueba de escritura real (lo que la app necesita para subir videos)
        var testFile = Path.Combine(effectivePath, $"__screenrecorder_test_{DateTime.Now:yyyyMMdd_HHmmss}.tmp");
        try
        {
            await Task.Run(() =>
            {
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
            });
            _logger.LogInfo($"NUC OK: '{effectivePath}' accesible y con permisos de escritura (usuario={Environment.UserName}, elevado={NetworkPathHelper.IsProcessElevated()})");
        }
        catch (Exception ex)
        {
            _logger.LogError($"NUC SIN PERMISOS DE ESCRITURA: '{effectivePath}' se abre pero no permite escribir (usuario={Environment.UserName}, elevado={NetworkPathHelper.IsProcessElevated()}). Conceda permisos de escritura a ese usuario en el recurso compartido y en la seguridad NTFS de la carpeta. Error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Devuelve la ruta efectiva para subir. Si la configurada es una letra de unidad no visible
    /// para este proceso (p. ej. app elevada) pero su UNC resuelto sí es accesible, usa el UNC.
    /// Si nada es accesible, loguea el diagnóstico completo y devuelve la ruta original.
    /// </summary>
    private string ResolveEffectiveNetworkPath(string networkPath)
    {
        if (Directory.Exists(networkPath))
            return networkPath;

        var unc = NetworkPathHelper.TryResolveToUnc(networkPath);
        if (unc != null && Directory.Exists(unc))
        {
            _logger.LogInfo($"La unidad '{networkPath}' no es visible en este proceso; usando su nombre de red: {unc}");
            return unc;
        }

        _logger.LogWarning($"Ruta de red no accesible. Diagnóstico: {NetworkPathHelper.BuildInaccessibleDiagnostics(networkPath)}");
        return networkPath;
    }
}
