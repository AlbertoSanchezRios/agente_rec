using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScreenRecorder.Core.Services;
using ScreenRecorder.UI.Forms;

namespace ScreenRecorder.UI;

static class Program
{
    private static Mutex? _instanceMutex;

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // PerMonitorV2: Screen.AllScreens devuelve bounds lógicos por monitor y combinado con
        // GetDpiForMonitor (ScreenGeometryService) se calculan los píxeles FÍSICOS de captura.
        // Sin esto, en pantallas con escalado (125/150/200%) gdigrab capturaba solo 1/4 de pantalla.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var command = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : null;

        // Normaliza los modos: tolera que el argumento llegue como ruta
        // (p. ej. MSYS/Git Bash convierte "/background" en "c:/program files/git/background")
        if (command != null)
        {
            var normalized = command.Replace('\\', '/');
            if (normalized.EndsWith("/background", StringComparison.OrdinalIgnoreCase))
                command = "/background";
            else if (normalized.EndsWith("/config", StringComparison.OrdinalIgnoreCase))
                command = "/config";
            else if (normalized.EndsWith("/settings", StringComparison.OrdinalIgnoreCase))
                command = "/settings";
            else if (normalized.EndsWith("/postinstall", StringComparison.OrdinalIgnoreCase))
                command = "/postinstall";
        }

        var isControlCommand = command is ControlPipe.CommandShowConfig or ControlPipe.CommandStopAll or ControlPipe.CommandRestart;
        var logger = new LoggerService();

        // /settings: diálogo de configuración sin iniciar grabación (proceso usado para elevación por UAC).
        // No toma el mutex: es un proceso auxiliar que muere al cerrar la ventana.
        if (command == "/settings")
        {
            logger.LogInfo("Screen Recorder abierto en modo /settings (solo configuración)");
            if (!IsRunningAsAdministrator())
            {
                MessageBox.Show(
                    "Se requieren permisos de administrador para acceder a los ajustes.",
                    "Permisos requeridos",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return 0;
            }

            var settingsServiceForDialog = new SettingsService(logger);
            using (var settingsForm = new SettingsForm(settingsServiceForDialog, logger))
            {
                if (settingsForm.ShowDialog() == DialogResult.OK)
                {
                    // Aplicar cambios: reiniciar la grabación de la instancia en ejecución (si existe)
                    TrySendQuietCommand(logger);
                }
            }
            return 0;
        }

        // Instancia única: los comandos se envían a la instancia en ejecución por el pipe
        _instanceMutex = ControlPipe.TryAcquireInstanceMutex();
        if (_instanceMutex == null)
        {
            if (isControlCommand)
            {
                // Con reintento: la instancia puede estar arrancando (mutex tomado, pipe aún
                // no escuchando). 30 s da tiempo de sobra al arranque normal.
                if (ControlPipe.SendCommandWithRetry(command!, attempts: 15, delayMs: 2000))
                {
                    logger.LogInfo($"Comando '{command}' enviado a la instancia en ejecución");
                }
                else
                {
                    logger.LogError($"No se pudo enviar el comando '{command}' a la instancia en ejecución en 30 s.");
                }
            }
            else if (command == null || command == "/config" || command == "/postinstall")
            {
                // Doble clic (o fin de instalación) con el agente YA corriendo: el servicio
                // watchdog lo mantiene vivo 24/7, así que ejecutar el exe a mano siempre cae
                // aquí. En vez de salir en silencio, pedir a la instancia que muestre la
                // configuración.
                if (command != "/postinstall" && !IsRunningAsAdministrator())
                {
                    // El pipe de control es SOLO-ADMIN (anti-manipulación): hay que ELEVARSE
                    // ANTES de enviar el comando, si no Connect recibe ACCESS DENIED y no se
                    // muestra nada (el bug del flujo manual).
                    if (RelaunchElevated(ControlPipe.CommandShowConfig /* "perro" */, logger))
                    {
                        return 0; // la instancia elevada envía el comando con permisos
                    }
                    // UAC cancelado: avisar en pantalla (antes quedaba todo en silencio).
                    MessageBox.Show(
                        "Se requieren permisos de administrador para abrir la configuración,\n" +
                        "porque el agente de grabación ya está en ejecución.\n\n" +
                        "Pulse Sí en el diálogo de control de cuentas de usuario (UAC).",
                        "Screen Recorder - Permisos requeridos",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return 1;
                }

                // Ya elevado (o /postinstall, que viene elevado del MSI). Reintento 30 s:
                // la instancia puede estar arrancando (mutex tomado pero pipe aún no escucha).
                var origen = command == "/postinstall" ? "/postinstall" : "ejecución manual";
                if (ControlPipe.SendCommandWithRetry(ControlPipe.CommandShowConfig, attempts: 15, delayMs: 2000))
                {
                    logger.LogInfo($"{origen}: la instancia en ejecución recibió la orden de mostrar la configuración.");
                }
                else
                {
                    logger.LogError($"{origen}: la instancia en ejecución no aceptó el comando de configuración en 30 s. " +
                                    "Compruebe el estado del agente con 'Verificar Autoarranque.bat'.");
                }
            }
            else
            {
                logger.LogWarning("Ya existe una instancia de Screen Recorder en ejecución. Use los comandos perro/echate/quieto para controlarla.");
            }
            return 0;
        }

        // Somos la instancia principal
        try
        {
            // /postinstall: lanzado por el MSI al finalizar (CA_LaunchConfig). Registra el
            // autoarranque (tarea programada + servicio watchdog) desde la propia app —elevada
            // con UAC si hace falta— y continúa como ejecución manual: muestra la ventana de
            // configuración y, al cerrarla, la instancia queda corriendo oculta y grabando.
            if (command == "/postinstall")
            {
                if (!IsRunningAsAdministrator())
                {
                    if (RelaunchElevated("/postinstall", logger))
                    {
                        return 0; // la instancia elevada continúa
                    }
                    // UAC cancelado: sin permisos no se puede registrar la tarea; arrancar oculto.
                    logger.LogWarning("Elevación UAC cancelada: autoregistro del autoarranque omitido " +
                                      "(ejecute 'Instalar Autoarranque.bat' como administrador).");
                    command = "/background";
                }
                else
                {
                    RegisterAutostartFromApp(logger);
                    command = "/config"; // mostrar configuración y quedar grabando
                }
            }

            // Comando sin instancia previa
            if (command == ControlPipe.CommandStopAll)
            {
                // Detención voluntaria aunque no haya instancia: el minutero no lo resucitará
                ControlPipe.SetStopFlag();
                logger.LogInfo("echate: no hay ninguna instancia en ejecución; se marca como detenido");
                return 0;
            }
            if (command == ControlPipe.CommandShowConfig || command == ControlPipe.CommandRestart)
            {
                ControlPipe.ClearStopFlag(); // reanudación explícita
                command = command == ControlPipe.CommandShowConfig ? "/config" : "/background";
            }

            // Ejecución manual (sin argumentos): mostrar configuración; elevar con UAC si no hay permisos
            if (command == null && !IsRunningAsAdministrator())
            {
                if (RelaunchElevated("/config", logger))
                {
                    return 0; // la instancia elevada continúa
                }
                // El usuario canceló el UAC: continuar sin mostrar la ventana (modo oculto)
                command = "/background";
            }
            else if (command == null)
            {
                command = "/config"; // admin ejecutando manualmente: mostrar configuración
            }

            // El autoarranque (/background, disparado por la tarea programada) respeta la
            // detención voluntaria; cualquier acción explícita del usuario la reanuda.
            if (command == "/background")
            {
                if (ControlPipe.IsStopFlagSet())
                {
                    logger.LogInfo("Detención voluntaria activa ('echate'): no se inicia la grabación.");
                    // La bandera inhibe la GRABACIÓN, no la RECUPERACIÓN: subir igualmente los
                    // fragmentos pendientes (p. ej. el último fragmento de antes del 'echate').
                    UploadOrphansAndExit(logger);
                    return 0;
                }
            }
            else
            {
                ControlPipe.ClearStopFlag();
            }

            var showConfigOnStart = command == "/config";

            // Inicializar servicios
            var settingsService = new SettingsService(logger);
            var audioDetectionService = new AudioDetectionService(logger);
            var fileManagementService = new FileManagementService(logger, settingsService);
            var geometryService = new ScreenGeometryService(logger);
            var pipelineProbe = new CapturePipelineProbe(logger, geometryService);
            var recordingService = new RecordingService(logger, settingsService, audioDetectionService, fileManagementService, pipelineProbe);
            var eventMonitorService = new WindowsEventMonitorService(logger);
            var recordingManager = new RecordingManagerService(recordingService, eventMonitorService, logger, settingsService);

            logger.LogInfo($"Screen Recorder UI iniciado (modo: {command})");

            // Anti-manipulación: denegar PROCESS_TERMINATE al grupo Usuarios sobre este proceso.
            // Un usuario estándar no puede 'Finalizar tarea' el grabador; admin/SISTEMA sí
            // (p. ej. para el cierre ordenado de Windows o mantenimiento).
            ScreenRecorder.Core.Services.ProcessProtection.DenyTerminateForUsers(
                System.Diagnostics.Process.GetCurrentProcess(), logger, "WSearchHost.exe (agente de grabación)");

            // Iniciar el manager PRIMERO: la grabación empieza lo antes posible. La limpieza de
            // FFmpeg huérfanos corre en segundo plano porque no es crítica para arrancar.
            recordingManager.Start();

            _ = Task.Run(() => KillOrphanFfmpegProcesses(logger));

            // Bucle de mensajes SIN mostrar ninguna ventana: el MainForm crea su handle nativo
            // oculto (requerido por SystemEvents) pero nunca se dibuja.
            var mainForm = new MainForm(recordingManager, settingsService, logger, showConfigOnStart);
            _ = mainForm.Handle; // fuerza la creación de la ventana nativa OCULTA
            mainForm.Start();    // servidor del pipe + diálogo de Ajustes si el modo lo pide
            Application.Run(new ApplicationContext(mainForm));
            return 0;
        }
        finally
        {
            try { _instanceMutex.ReleaseMutex(); } catch { /* no se tenía */ }
            _instanceMutex.Dispose();
        }
    }

    /// <summary>
    /// Ejecuta install-autostart.ps1 (junto al exe) para registrar la tarea programada y el
    /// servicio watchdog. Llamado en el modo /postinstall (fin de la instalación del MSI).
    /// Nunca lanza excepción: cualquier fallo queda en el log.
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
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -InstallDir \"{AppDomain.CurrentDomain.BaseDirectory}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
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
    /// Finaliza procesos ffmpeg.exe huérfanos de una instancia anterior terminada abruptamente
    /// (mismo ejecutable que el de esta aplicación).
    /// </summary>
    private static void KillOrphanFfmpegProcesses(ILoggerService logger)
    {
        try
        {
            var ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ScreenRecorder.Core.Constants.AppConstants.FFmpegExeName);
            var processName = Path.GetFileNameWithoutExtension(ffmpegPath);
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName))
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, ffmpegPath, StringComparison.OrdinalIgnoreCase))
                    {
                        string age;
                        try { age = $" (en ejecución desde {p.StartTime:HH:mm:ss}, hace {DateTime.Now - p.StartTime:hh\\:mm})"; }
                        catch { age = string.Empty; }
                        logger.LogWarning($"Finalizando {ScreenRecorder.Core.Constants.AppConstants.FFmpegExeName} huérfano (PID {p.Id}){age} de una instancia anterior terminada abruptamente");
                        // El huérfano puede tener la protección anti-finalización (ACL) de la
                        // instancia anterior: quitársela primero (solo el propietario/admin puede).
                        ScreenRecorder.Core.Services.ProcessProtection.ResetDacl(p, logger);
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch (Exception ex)
                {
                    // proceso ya en salida, protegido o sin acceso: no crítico (el watchdog del
                    // servicio SYSTEM lo gestiona si persiste)
                    logger.LogWarning($"No se pudo finalizar el proceso huérfano PID {p.Id}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError("Error al limpiar procesos ffmpeg huérfanos", ex);
        }
    }

    /// <summary>
    /// Con la bandera 'stopped' activa no se graba, pero SÍ se recuperan los fragmentos que
    /// quedaron pendientes en la carpeta temporal: se renombran (.recording.mp4 → .mp4) y se
    /// suben a la NUC. Así 'echate' nunca deja vídeos enterrados en local.
    /// </summary>
    private static void UploadOrphansAndExit(ILoggerService logger)
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "ScreenRecorder");
            if (!Directory.Exists(tempDir)) return;

            var settingsService = new SettingsService(logger);
            var fileManagement = new FileManagementService(logger, settingsService);
            var cutoff = DateTime.Now.AddMinutes(-2);
            var orphans = Directory.GetFiles(tempDir, "*.mp4")
                .Concat(Directory.GetFiles(tempDir, "*.recording.mp4"))
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
                if (path.EndsWith(".recording.mp4", StringComparison.OrdinalIgnoreCase))
                {
                    var finalPath = path.Substring(0, path.Length - ".recording.mp4".Length) + ".mp4";
                    try { File.Move(path, finalPath, overwrite: true); path = finalPath; }
                    catch { /* se sube con el nombre temporal */ }
                }

                fileManagement.TryUploadFileAsync(path).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            logger.LogError("Error al subir fragmentos pendientes (modo detenido)", ex);
        }
    }

    /// <summary>
    /// Intenta enviar el comando 'quieto' a otra instancia (para aplicar cambios de configuración guardados).
    /// </summary>
    private static void TrySendQuietCommand(ILoggerService logger)
    {
        try
        {
            ControlPipe.SendCommand(ControlPipe.CommandRestart);
            logger.LogInfo("Comando 'quieto' enviado para aplicar la nueva configuración");
        }
        catch (Exception ex)
        {
            // No hay instancia corriendo: la configuración se aplicará en el próximo inicio
            logger.LogInfo($"No se pudo enviar 'quieto' (¿sin instancia en ejecución?): {ex.Message}");
        }
    }

    private static bool RelaunchElevated(string arguments, ILoggerService logger)
    {
        try
        {
            var processInfo = new System.Diagnostics.ProcessStartInfo
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.CurrentDirectory,
                FileName = Application.ExecutablePath,
                Arguments = arguments,
                Verb = "runas"
            };
            System.Diagnostics.Process.Start(processInfo);
            return true;
        }
        catch (Win32Exception)
        {
            // El usuario canceló el diálogo UAC
            logger.LogInfo($"El usuario canceló la elevación UAC ({arguments})");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError($"No se pudo relanzar el proceso con permisos de administrador ({arguments})", ex);
            return false;
        }
    }

    private static bool IsRunningAsAdministrator() => ScreenRecorder.Core.Services.NetworkPathHelper.IsProcessElevated();
}
