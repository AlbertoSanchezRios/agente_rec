namespace ScreenRecorder.Core.Services;

/// <summary>
/// Servicio de logging para auditoría
/// </summary>
public interface ILoggerService
{
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? exception = null);
    void LogDebug(string message);
}
