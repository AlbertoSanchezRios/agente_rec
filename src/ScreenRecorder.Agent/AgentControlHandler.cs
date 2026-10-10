using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScreenRecorder.Core.Constants;
using ScreenRecorder.Core.Ipc;
using ScreenRecorder.Core.Services;
using ScreenRecorder.Core.State;

namespace ScreenRecorder.Agent;

/// <summary>
/// Resuelve los comandos de control (JSON de Wctl + legacy perro/quieto/echate) sin depender de
/// Windows Forms. Los comandos que necesitan interfaz (config) lanzan WSearchCfg.exe elevado.
/// </summary>
internal sealed class AgentControlHandler
{
    private readonly IRecordingManagerService _recordingManager;
    private readonly ISettingsService _settingsService;
    private readonly ILoggerService _logger;

    public AgentControlHandler(IRecordingManagerService recordingManager,
        ISettingsService settingsService, ILoggerService logger)
    {
        _recordingManager = recordingManager;
        _settingsService = settingsService;
        _logger = logger;
    }

    public ControlResponse Handle(ControlRequest request)
    {
        var cmd = (request.Cmd ?? string.Empty).Trim().ToLowerInvariant();
        switch (cmd)
        {
            case "perro":
            case "config":
            case "settings":
                Program.LaunchSettingsElevated(_logger);
                return ControlResponse.Success(new { mensaje = "ventana de configuración solicitada" });

            case "quieto":
            case "restart":
            case "start":
                ControlPipe.ClearStopFlag();
                _logger.LogInfo($"Reiniciando ciclo de grabación (comando '{cmd}')");
                _recordingManager.Stop();
                _recordingManager.Start();
                _logger.LogInfo("Ciclo de grabación reiniciado");
                return ControlResponse.Success(new { mensaje = "grabación reiniciada" });

            case "pause":
                _recordingManager.Stop();
                _logger.LogInfo("Grabación pausada (comando 'pause')");
                return ControlResponse.Success(new { mensaje = "grabación pausada" });

            case "resume":
                ControlPipe.ClearStopFlag();
                _recordingManager.Start();
                _logger.LogInfo("Grabación reanudada (comando 'resume')");
                return ControlResponse.Success(new { mensaje = "grabación reanudada" });

            case "echate":
            case "stop":
                _logger.LogInfo($"Deteniendo grabación y cerrando la aplicación (comando '{cmd}')");
                ControlPipe.SetStopFlag();
                _ = Task.Run(async () =>
                {
                    await Task.Delay(600); // dar tiempo a que la respuesta salga por el pipe
                    try { _recordingManager.Stop(); }
                    catch (Exception ex) { _logger.LogError("Error al detener la grabación (comando 'stop')", ex); }
                    Environment.Exit(0);
                });
                return ControlResponse.Success(new { mensaje = "detención voluntaria solicitada" });

            case "status":
                return ControlResponse.Success(BuildStatus());

            case "doctor":
                return ControlResponse.Success(BuildDoctor());

            case "logs":
                return ControlResponse.Success(BuildLogs(request));

            case "version":
                return ControlResponse.Success(new { version = AppConstants.AppVersion });

            case "upload":
            case "orphans":
                return ControlResponse.Success(new { pendientesEnvio = SafePendingCount() });

            case "help":
                return ControlResponse.Success(new
                {
                    comandos = new[]
                    {
                        "status", "config", "start", "stop", "restart", "pause",
                        "resume", "upload", "orphans", "logs", "doctor", "version", "help"
                    }
                });

            default:
                _logger.LogWarning($"Comando desconocido recibido por pipe: {cmd}");
                return ControlResponse.Fail($"comando desconocido: {cmd}");
        }
    }

    private object BuildStatus()
    {
        var state = AgentStateStore.Get();
        var settings = _settingsService.LoadSettings();
        return new
        {
            version = AppConstants.AppVersion,
            estado = state.Estado.ToString(),
            estadoDetalle = state.Detalle,
            estadoActualizado = state.ActualizadoUtc.ToString("o"),
            grabando = _recordingManager.IsRecordingActive,
            agenteEnEjecucion = ProcessHelper.IsProcessRunning(AppConstants.AgentProcessName),
            servicioEnEjecucion = ProcessHelper.IsProcessRunning(AppConstants.ServiceProcessName),
            ffmpegEnEjecucion = ProcessHelper.IsProcessRunning(AppConstants.FfmpegProcessName),
            banderinStopped = ControlPipe.IsStopFlagSet(),
            rutaRed = settings.NetworkPath,
            usuario = Environment.UserName,
            equipo = Environment.MachineName,
            elevado = NetworkPathHelper.IsProcessElevated(),
            pendientesEnvio = SafePendingCount()
        };
    }

    private object BuildDoctor()
    {
        var state = AgentStateStore.Get();
        return new
        {
            estado = state.Estado.ToString(),
            detalle = state.Detalle,
            servicioEnEjecucion = ProcessHelper.IsProcessRunning(AppConstants.ServiceProcessName),
            agenteEnEjecucion = ProcessHelper.IsProcessRunning(AppConstants.AgentProcessName),
            ffmpegEnEjecucion = ProcessHelper.IsProcessRunning(AppConstants.FfmpegProcessName),
            codecEnEjecucion = ProcessHelper.IsProcessRunning(AppConstants.CodecProcessName),
            banderinStopped = ControlPipe.IsStopFlagSet(),
            pendientesEnvio = SafePendingCount(),
            problemasRecientes = ReadRecentLogIssues(30)
        };
    }

    private object BuildLogs(ControlRequest request)
    {
        var lines = 50;
        if (request.Args != null && request.Args.TryGetValue("lines", out var raw) && int.TryParse(raw, out var n))
        {
            lines = Math.Clamp(n, 1, 2000);
        }

        var latest = LatestLogFile();
        if (latest == null)
        {
            return new { archivo = (string?)null, lineas = Array.Empty<string>() };
        }

        var all = File.ReadAllLines(latest.FullName);
        var tail = all.Length <= lines ? all : all[^lines..];
        return new { archivo = latest.FullName, lineas = tail };
    }

    private string[] ReadRecentLogIssues(int max)
    {
        try
        {
            var latest = LatestLogFile();
            if (latest == null) return Array.Empty<string>();
            var all = File.ReadAllLines(latest.FullName);
            return all
                .Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("WARNING", StringComparison.OrdinalIgnoreCase))
                .TakeLast(max)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static FileInfo? LatestLogFile()
    {
        try
        {
            var logDir = Path.Combine(AppConstants.ProgramDataDirectory, "Logs");
            if (!Directory.Exists(logDir)) return null;
            return new DirectoryInfo(logDir).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private int SafePendingCount()
    {
        try
        {
            var queue = new UploadQueueService(_logger);
            return queue.IsAvailable ? queue.CountPending() : -1;
        }
        catch
        {
            return -1;
        }
    }
}
