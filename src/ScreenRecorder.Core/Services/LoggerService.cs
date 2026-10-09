using System;
using System.IO;
using System.Reflection;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Implementación del servicio de logging
/// </summary>
public class LoggerService : ILoggerService
{
    private readonly string _logDirectory;
    private readonly string _logFile;
    private readonly object _lockObject = new object();

    // Mutex global: el agente (usuario) y el servicio watchdog (SYSTEM) escriben el mismo log.
    // Sin esto, AppendAllText (FileShare.Read) pierde entradas cuando ambos procesos escriben
    // a la vez (la excepción se tragaba silenciosamente).
    private static readonly Mutex LogMutex = new Mutex(initiallyOwned: false, name: @"Global\ScreenRecorderLogMutex");

    public LoggerService()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ScreenRecorder"
        );
        
        _logDirectory = Path.Combine(appDataPath, "Logs");
        Directory.CreateDirectory(_logDirectory);
        
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        _logFile = Path.Combine(_logDirectory, $"ScreenRecorder_{date}.log");
    }

    public void LogInfo(string message)
    {
        WriteLog("INFO", message);
    }

    public void LogWarning(string message)
    {
        WriteLog("WARNING", message);
    }

    public void LogError(string message, Exception? exception = null)
    {
        var errorMessage = message;
        if (exception != null)
        {
            errorMessage += $"\nException: {exception.Message}\nStackTrace: {exception.StackTrace}";
        }
        WriteLog("ERROR", errorMessage);
    }

    public void LogDebug(string message)
    {
        WriteLog("DEBUG", message);
    }

    private void WriteLog(string level, string message)
    {
        lock (_lockObject)
        {
            var gotMutex = false;
            try
            {
                gotMutex = LogMutex.WaitOne(TimeSpan.FromSeconds(5));
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logEntry = $"[{timestamp}] [{level}] {message}";

                File.AppendAllText(_logFile, logEntry + Environment.NewLine);
            }
            catch
            {
                // Silenciar errores de logging para evitar bucles infinitos
            }
            finally
            {
                if (gotMutex)
                {
                    try { LogMutex.ReleaseMutex(); } catch { /* ya liberado */ }
                }
            }
        }
    }
}
