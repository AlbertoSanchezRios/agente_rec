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
    private readonly object _lockObject = new object();
    private DateTime _lastPurgeDate = DateTime.MinValue;

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
    }

    private string GetCurrentLogFile()
    {
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        return Path.Combine(_logDirectory, $"ScreenRecorder_{date}.log");
    }

    private void PurgeOldLogs()
    {
        if ((DateTime.Now - _lastPurgeDate).TotalHours < 12) return;
        _lastPurgeDate = DateTime.Now;
        try
        {
            var cutoff = DateTime.Now.AddDays(-14);
            var dirInfo = new DirectoryInfo(_logDirectory);
            if (!dirInfo.Exists) return;
            foreach (var file in dirInfo.GetFiles("ScreenRecorder_*.log"))
            {
                if (file.LastWriteTime < cutoff)
                {
                    try { file.Delete(); } catch { }
                }
            }
        }
        catch { }
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
                try
                {
                    gotMutex = LogMutex.WaitOne(TimeSpan.FromSeconds(5));
                }
                catch (AbandonedMutexException)
                {
                    // Si el proceso anterior murió con el mutex adquirido, Windows concede
                    // la posesión lanzando AbandonedMutexException. Marcar gotMutex = true
                    // asegura que se use y que el bloque finally siempre lo libere.
                    gotMutex = true;
                }

                PurgeOldLogs();

                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logEntry = $"[{timestamp}] [{level}] {message}";
                var logFile = GetCurrentLogFile();

                File.AppendAllText(logFile, logEntry + Environment.NewLine);
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
