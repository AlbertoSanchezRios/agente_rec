using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScreenRecorder.Core.Constants;
using ScreenRecorder.Core.Ipc;
using ScreenRecorder.Core.Services;

namespace ScreenRecorder.Agent;

/// <summary>
/// Agente de captura ligero (WSearchAgent.exe). Corre en la sesión interactiva del usuario SIN
/// Windows Forms: Microsoft.Win32.SystemEvents levanta su propia ventana oculta y bomba de
/// mensajes, por lo que no se necesita ningún formulario. La interfaz de ajustes es un proceso
/// aparte (WSearchCfg.exe) que se lanza elevado solo cuando se solicita.
/// </summary>
static class Program
{
    private static Mutex? _instanceMutex;

    [STAThread]
    static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : null;

        // Normaliza los modos: tolera que el argumento llegue como ruta (MSYS/Git Bash).
        if (command != null)
        {
            var normalized = command.Replace('\\', '/');
            if (normalized.EndsWith("/background", StringComparison.OrdinalIgnoreCase)) command = "/background";
            else if (normalized.EndsWith("/config", StringComparison.OrdinalIgnoreCase)) command = "/config";
            else if (normalized.EndsWith("/settings", StringComparison.OrdinalIgnoreCase)) command = "/settings";
            else if (normalized.EndsWith("/postinstall", StringComparison.OrdinalIgnoreCase)) command = "/postinstall";
        }

        var isControlCommand = command is ControlPipe.CommandShowConfig or ControlPipe.CommandStopAll or ControlPipe.CommandRestart;
        var logger = new LoggerService();

        // /settings: este proceso ya no hospeda la UI; solo lanza WSearchCfg elevado.
        if (command == "/settings")
        {
            LaunchSettingsElevated(logger);
            return 0;
        }

        _instanceMutex = ControlPipe.TryAcquireInstanceMutex();
        if (_instanceMutex == null)
        {
            // Ya hay un agente en ejecución.
            if (isControlCommand)
            {
                if (ControlPipe.SendCommandWithRetry(command!, attempts: 15, delayMs: 2000))
                {
                    logger.LogInfo($"Comando '{command}' enviado a la instancia en ejecución");
                }
                else
                {
                    logger.LogError($"No se pudo enviar el comando '{command}' a la instancia en ejecución en 30 s.");
                }
            }
            else if (command is null or "/config" or "/postinstall")
            {
                // Ejecución manual (o fin de instalación) con el agente ya corriendo: abrir ajustes.
                LaunchSettingsElevated(logger);
            }
            else
            {
                logger.LogWarning("Ya existe una instancia del agente. Use Wctl para controlarla.");
            }

            return 0;
        }

        // Somos la instancia principal.
        try
        {
            if (command == "/postinstall")
            {
                if (!IsRunningAsAdministrator())
                {
                    if (RelaunchSelfElevated("/postinstall", logger))
                    {
                        return 0;
                    }
                    logger.LogWarning("Elevación UAC cancelada: autoregistro del autoarranque omitido.");
                    command = "/background";
                }
                else
                {
                    RegisterAutostartFromApp(logger);
                    command = "/config";
                }
            }

            if (command == ControlPipe.CommandStopAll)
            {
                ControlPipe.SetStopFlag();
                logger.LogInfo("echate: no hay ninguna instancia en ejecución; se marca como detenido");
                return 0;
            }
            if (command == ControlPipe.CommandShowConfig || command == ControlPipe.CommandRestart)
            {
                ControlPipe.ClearStopFlag(); // reanudación explícita
                command = command == ControlPipe.CommandShowConfig ? "/config" : "/background";
            }

            if (command == null)
            {
                command = "/config"; // ejecución manual: mostrar configuración
            }

            if (command == "/background")
            {
                if (ControlPipe.IsStopFlagSet())
                {
                    logger.LogInfo("Detención voluntaria activa ('echate'): no se inicia la grabación.");
                    UploadOrphansAndExit(logger);
                    return 0;
                }
            }
            else
            {
                ControlPipe.ClearStopFlag();
            }

            var showConfigOnStart = command == "/config";

            // Servicios
            var settingsService = new SettingsService(logger);
            var audioDetectionService = new AudioDetectionService(logger);
            var fileManagementService = new FileManagementService(logger, settingsService);
            var geometryService = new ScreenGeometryService(logger);
            var pipelineProbe = new CapturePipelineProbe(logger, geometryService);
            var recordingService = new RecordingService(logger, settingsService, audioDetectionService, fileManagementService, pipelineProbe);
            var eventMonitorService = new WindowsEventMonitorService(logger);
            var recordingManager = new RecordingManagerService(recordingService, eventMonitorService, logger, settingsService);

            logger.LogInfo($"Screen Recorder agente iniciado (modo: {command})");

            // Anti-manipulación del agente.
            using (var currentProcess = Process.GetCurrentProcess())
            {
                ProcessProtection.DenyTerminateForUsers(
                    currentProcess, logger, "WSearchAgent.exe (agente de grabación)");
            }

            // Servidor de control (JSON de Wctl + comandos legacy).
            var handler = new AgentControlHandler(recordingManager, settingsService, logger);
            ControlPipe.StartServer(handler.Handle, logger);

            // Iniciar grabación lo antes posible; la limpieza de huérfanos en segundo plano.
            recordingManager.Start();
            _ = Task.Run(() => KillOrphanFfmpegProcesses(logger));

            if (showConfigOnStart)
            {
                LaunchSettingsElevated(logger);
            }

            // Mantener vivo el proceso. SystemEvents ya tiene su propio hilo con bomba de mensajes.
            new ManualResetEvent(false).WaitOne();
            return 0;
        }
        finally
        {
            try { _instanceMutex.ReleaseMutex(); } catch { /* no se tenía */ }
            _instanceMutex.Dispose();
        }
    }

    /// <summary>Lanza WSearchCfg.exe elevado (UAC) para abrir la configuración.</summary>
    internal static void LaunchSettingsElevated(ILoggerService logger)
    {
        try
        {
            var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppConstants.SettingsExeName);
            if (!File.Exists(exe))
            {
                logger.LogWarning($"No se encontró {AppConstants.SettingsExeName} junto al agente ({exe}).");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = exe,
                Arguments = "/settings",
                Verb = "runas"
            });
        }
        catch (Win32Exception)
        {
            logger.LogInfo("El usuario canceló la elevación UAC (configuración).");
        }
        catch (Exception ex)
        {
            logger.LogError("No se pudo abrir la configuración elevada", ex);
        }
    }

    private static bool RelaunchSelfElevated(string arguments, ILoggerService logger)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                FileName = Environment.ProcessPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppConstants.AgentExeName),
                Arguments = arguments,
                Verb = "runas"
            };
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)
        {
            logger.LogInfo($"El usuario canceló la elevación UAC ({arguments})");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError($"No se pudo relanzar el agente con permisos de administrador ({arguments})", ex);
            return false;
        }
    }

    /// <summary>
    /// Ejecuta install-autostart.ps1 (junto al exe) para registrar la tarea programada y el
    /// servicio watchdog. Llamado en el modo /postinstall.
    /// </summary>
    private static void RegisterAutostartFromApp(ILoggerService logger)
    {
        try
        {
            var script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "install-autostart.ps1");
            if (!File.Exists(script))
            {
                logger.LogWarning($"No se encontró install-autostart.ps1 en {AppDomain.CurrentDomain.BaseDirectory}; " +
                                  "autoregistro omitido. Ejecute 'Instalar Autoarranque.bat' manualmente.");
                return;
            }

            logger.LogInfo("Registrando autoarranque desde la app (modo /postinstall)...");
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -InstallDir \"{AppDomain.CurrentDomain.BaseDirectory}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                logger.LogWarning("No se pudo iniciar PowerShell para el autoregistro del autoarranque.");
                return;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(120000);

            foreach (var line in stdout.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                logger.LogInfo($"[install-autostart] {line.Trim()}");
            }
            foreach (var line in stderr.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                logger.LogWarning($"[install-autostart] {line.Trim()}");
            }
            logger.LogInfo($"install-autostart.ps1 terminó con código {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            logger.LogError("Error al registrar el autoarranque (modo /postinstall)", ex);
        }
    }

    /// <summary>
    /// Finaliza procesos FFmpeg huérfanos (captura y conversión) de una instancia anterior.
    /// </summary>
    private static void KillOrphanFfmpegProcesses(ILoggerService logger)
    {
        foreach (var exeName in new[] { AppConstants.FFmpegExeName, AppConstants.CodecExeName })
        {
            try
            {
                var ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName);
                var processName = Path.GetFileNameWithoutExtension(ffmpegPath);
                var processes = Process.GetProcessesByName(processName);
                foreach (var p in processes)
                {
                    using (p)
                    {
                        try
                        {
                            if (string.Equals(p.MainModule?.FileName, ffmpegPath, StringComparison.OrdinalIgnoreCase))
                            {
                                string age;
                                try { age = $" (en ejecución desde {p.StartTime:HH:mm:ss}, hace {DateTime.Now - p.StartTime:hh\\:mm})"; }
                                catch { age = string.Empty; }
                                logger.LogWarning($"Finalizando {exeName} huérfano (PID {p.Id}){age} de una instancia anterior terminada abruptamente");
                                ProcessProtection.ResetDacl(p, logger);
                                p.Kill();
                                p.WaitForExit(3000);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning($"No se pudo finalizar el proceso huérfano PID {p.Id}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Error al limpiar procesos huérfanos ({exeName})", ex);
            }
        }
    }

    /// <summary>
    /// Con la bandera 'stopped' activa no se graba, pero SÍ se recuperan los fragmentos pendientes
    /// en la carpeta temporal: se renombran y se suben a la NUC.
    /// </summary>
    private static void UploadOrphansAndExit(ILoggerService logger)
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "ScreenRecorder");
            if (!Directory.Exists(tempDir)) return;

            var settingsService = new SettingsService(logger);
            var fileManagement = new FileManagementService(logger, settingsService);
            var pipeline = new FragmentPipelineService(logger, fileManagement);
            var cutoff = DateTime.Now.AddMinutes(-2);
            var orphans = Directory.GetFiles(tempDir, "*.mp4")
                .Concat(Directory.GetFiles(tempDir, "*.mkv"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(f => File.GetLastWriteTime(f) < cutoff)
                .ToList();

            if (orphans.Count == 0) return;

            logger.LogInfo($"Subiendo {orphans.Count} fragmento(s) pendiente(s) antes de salir (detención voluntaria activa)");
            var deadline = DateTime.UtcNow.AddSeconds(90);
            foreach (var orphan in orphans)
            {
                if (DateTime.UtcNow > deadline)
                {
                    logger.LogWarning("Tiempo de subida agotado; los fragmentos restantes se subirán en el próximo arranque.");
                    break;
                }

                var path = orphan;
                if (path.EndsWith(".recording.mkv", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".recording.mp4", StringComparison.OrdinalIgnoreCase))
                {
                    var ext = path.EndsWith(".recording.mkv", StringComparison.OrdinalIgnoreCase) ? ".recording.mkv" : ".recording.mp4";
                    var finalPath = path.Substring(0, path.Length - ext.Length) + ext.Substring(".recording".Length);
                    try { File.Move(path, finalPath, overwrite: true); path = finalPath; }
                    catch { /* se sube con el nombre temporal */ }
                }

                pipeline.ProcessAsync(path).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            logger.LogError("Error al subir fragmentos pendientes (modo detenido)", ex);
        }
    }

    private static bool IsRunningAsAdministrator() => NetworkPathHelper.IsProcessElevated();
}
