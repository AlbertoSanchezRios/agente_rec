using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using ScreenRecorder.Core.Services;

namespace ScreenRecorder.Service;

/// <summary>
/// Servicio Windows (LocalSystem) de supervisión de Screen Recorder:
/// 1. Watchdog: vigila que el agente de usuario (WSearchHost.exe) esté corriendo y lo
///    rearranca en la sesión interactiva si desaparece (aunque lo finalicen por la fuerza
///    desde el Administrador de tareas). Respeta la detención voluntaria legítima (bandera
///    'stopped', que solo TI puede establecer: el pipe de control ya es solo-admin).
/// 2. Uploader: al arrancar el sistema y cada 10 min sube los fragmentos huérfanos de la
///    carpeta temporal a la NUC, aunque la sesión de usuario no exista todavía.
/// 3. Tamper-evident: registra cada caída/rearranque del agente y escribe un heartbeat en
///    la NUC para detectar huecos de grabación (p. ej. apagones o manipulaciones).
///
/// Limitación honesta: un administrador malicioso puede detener también este servicio;
/// contra eso solo queda la evidencia que deja (logs + huecos en el heartbeat de la NUC).
/// </summary>
public class WatchdogService : ServiceBase
{
    public const string WatchdogServiceName = "WSearchHostSvc";

    // Vigilancia cada 10 s: rearranca el agente tras una caída en <=10 s (la antigua tarea
    // programada lo hacía en ~1 min; el servicio es ahora el UNICO lanzador del agente).
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UploadInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);

    private readonly ILoggerService _logger;
    private readonly ISettingsService _settingsService;
    private readonly IFileManagementService _fileManagementService;
    private readonly UploadQueueService _uploadQueue;
    private readonly string _tempDirectory;
    private readonly string _agentPath;

    private System.Threading.Timer? _timer;
    private DateTime _lastUpload = DateTime.MinValue;
    private DateTime _lastHeartbeat = DateTime.MinValue;
    private DateTime _lastTaskCleanup = DateTime.MinValue;
    private readonly DateTime _startedAt = DateTime.Now;
    private bool _agentSeenRunning;
    private int _restartAttempts;
    private volatile bool _stopping;
    // "Agente vivo pero no grabando": desde cuándo se ve el agente y cuándo se mandó el último 'quieto'.
    private DateTime _agentAliveSince = DateTime.MinValue;
    private DateTime _lastQuietCommandAt = DateTime.MinValue;
    private bool _heartbeatFailLogged;
    // Estado expuesto en watchdog-status.json para diagnóstico inmediato (Verificar bat).
    private string _lastLaunchError = string.Empty;
    private string _lastLaunchInfo = string.Empty;
    private string _lastQueueError = string.Empty;
    private string _lastUploadInfo = string.Empty;

    public WatchdogService(ILoggerService logger)
    {
        _logger = logger;
        _settingsService = new SettingsService(logger);
        _fileManagementService = new FileManagementService(logger, _settingsService);
        _uploadQueue = new UploadQueueService(logger);
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ScreenRecorder");
        _agentPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WSearchHost.exe");

        ServiceName = WatchdogServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false; // usamos nuestro LoggerService
    }

    protected override void OnStart(string[] args)
    {
        _logger.LogInfo("=== Servicio watchdog WSearchHostSvc iniciado (SYSTEM) ===");
        // Primer tick a los 2 s: levanta el agente de inmediato si ya hay una sesión.
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), WatchdogInterval);
    }

    protected override void OnStop()
    {
        _stopping = true;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _logger.LogInfo("=== Servicio watchdog WSearchHostSvc detenido ===");
    }

    protected override void OnShutdown()
    {
        // Apagado de Windows: no rearrancar nada; solo registrar.
        _stopping = true;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _logger.LogInfo("Apagado del sistema: watchdog detenido.");
    }

    // --- Modo consola (depuración) ---
    public void StartConsole() => OnStart(Array.Empty<string>());
    public void StopConsole() => OnStop();

    private void Tick()
    {
        if (_stopping) return;
        try { WatchAgent(); } catch (Exception ex) { _logger.LogError("Error en watchdog del agente", ex); }
        try { RecoverRecordingIfStale(); } catch (Exception ex) { _logger.LogError("Error en recuperación de grabación", ex); }
        try { if (DateTime.Now - _lastTaskCleanup >= TimeSpan.FromMinutes(10)) CleanupLegacyTask(); } catch (Exception ex) { _logger.LogError("Error en limpieza de tarea heredada", ex); }
        try { if (DateTime.Now - _lastUpload >= UploadInterval) UploadOrphans(); } catch (Exception ex) { _logger.LogError("Error en upload de huérfanos", ex); }
        try { if (DateTime.Now - _lastHeartbeat >= HeartbeatInterval) WriteHeartbeat(); } catch (Exception ex) { _logger.LogError("Error en heartbeat", ex); }
        try { WriteStatusFile(); } catch { /* nunca romper el tick */ }
    }

    /// <summary>Elimina la tarea programada heredada de versiones antiguas (el servicio es el
    /// único lanzador). Corre como SYSTEM: tiene permisos. Idempotente y con log.</summary>
    private void CleanupLegacyTask()
    {
        _lastTaskCleanup = DateTime.Now;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Query /TN \"ScreenRecorder\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var query = Process.Start(psi);
            if (query == null) return;
            query.WaitForExit(10000);
            if (query.ExitCode != 0) return; // no existe: nada que limpiar

            var del = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Delete /TN \"ScreenRecorder\" /F",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            del?.WaitForExit(10000);
            _logger.LogInfo("Tarea programada heredada 'ScreenRecorder' ELIMINADA por el servicio (ya no se usa).");
        }
        catch { /* la limpieza es opcional */ }
    }

    /// <summary>
    /// Si el agente lleva >3 min vivo pero FFmpeg no corre (WmvCoreHost=0), le manda 'quieto'
    /// por el pipe (SYSTEM supera la ACL solo-admin) para reiniciar el ciclo de grabación.
    /// Máximo 1 vez cada 10 minutos.
    /// </summary>
    private void RecoverRecordingIfStale()
    {
        var agentRunning = Process.GetProcessesByName("WSearchHost").Length > 0;
        if (!agentRunning)
        {
            _agentAliveSince = DateTime.MinValue;
            return;
        }
        if (_agentAliveSince == DateTime.MinValue) _agentAliveSince = DateTime.Now;

        var ffmpegRunning = Process.GetProcessesByName("WmvCoreHost").Length > 0;
        if (ffmpegRunning || IsStopFlagSet()) return;
        if (DateTime.Now - _agentAliveSince < TimeSpan.FromMinutes(3)) return;
        if (DateTime.Now - _lastQuietCommandAt < TimeSpan.FromMinutes(10)) return;

        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", "ScreenRecorderControl", System.IO.Pipes.PipeDirection.Out);
            pipe.Connect(2000);
            var bytes = System.Text.Encoding.UTF8.GetBytes("quieto");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            _lastQuietCommandAt = DateTime.Now;
            _logger.LogWarning("Agente vivo sin grabar (>3 min sin FFmpeg): se envió 'quieto' por el pipe para reiniciar la grabación.");
            _lastLaunchInfo += " | recuperación: quieto enviado";
        }
        catch (Exception ex)
        {
            _lastQuietCommandAt = DateTime.Now;
            _logger.LogWarning($"No se pudo enviar 'quieto' al agente: {ex.Message}");
        }
    }

    /// <summary>Escribe watchdog-status.json: estado actual legible sin abrir logs.</summary>
    private void WriteStatusFile()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ScreenRecorder", "watchdog-status.json");
        var agentRunning = Process.GetProcessesByName("WSearchHost").Length > 0;
        var ffmpegRunning = Process.GetProcessesByName("WmvCoreHost").Length > 0;
        var stopped = IsStopFlagSet();
        var json =
            "{\n" +
            $"  \"ultimoTick\": \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\",\n" +
            $"  \"servicioIniciado\": \"{_startedAt:yyyy-MM-dd HH:mm:ss}\",\n" +
            $"  \"agenteCorriendo\": {(agentRunning ? "true" : "false")},\n" +
            $"  \"ffmpegCorriendo\": {(ffmpegRunning ? "true" : "false")},\n" +
            $"  \"banderinStopped\": {(stopped ? "true" : "false")},\n" +
            $"  \"intentosRearranque\": {_restartAttempts},\n" +
            $"  \"pendientesEnvio\": {_uploadQueue.CountPending()},\n" +
            $"  \"ultimoErrorCola\": \"{JsonEscape(_lastQueueError)}\",\n" +
            $"  \"ultimoEnvio\": \"{JsonEscape(_lastUploadInfo)}\",\n" +
            $"  \"ultimoLanzamiento\": \"{JsonEscape(_lastLaunchInfo)}\",\n" +
            $"  \"ultimoError\": \"{JsonEscape(_lastLaunchError)}\"\n" +
            "}";
        File.WriteAllText(path, json);
    }

    private static string JsonEscape(string s) =>
        (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Si el agente de usuario no está corriendo y no hay detención voluntaria legítima,
    /// lo rearranca en la sesión interactiva activa. Registra la caída (tamper-evident).
    /// </summary>
    private void WatchAgent()
    {
        var running = Process.GetProcessesByName("WSearchHost").Length > 0;

        if (running)
        {
            if (!_agentSeenRunning && _restartAttempts > 0)
            {
                _logger.LogInfo("Agente de grabación de nuevo en ejecución.");
            }
            _agentSeenRunning = true;
            _restartAttempts = 0;
            return;
        }

        _agentSeenRunning = false;

        if (IsStopFlagSet())
        {
            // Detención voluntaria legítima (solo TI puede activarla): no resucitar.
            return;
        }

        // El agente NO está corriendo y nadie lo detuvo de forma legítima: caída o manipulación.
        _restartAttempts++;
        _logger.LogWarning($"Agente de grabación NO en ejecución sin orden de parada legítima (intento de rearranque {_restartAttempts}). " +
                           "Esto puede deberse a una finalización forzosa desde el Administrador de tareas.");

        if (!File.Exists(_agentPath))
        {
            _lastLaunchError = $"no se encuentra el agente: {_agentPath}";
            _logger.LogError($"No se encuentra el agente para rearrancarlo: {_agentPath}");
            return;
        }

        if (UserSessionLauncher.TryLaunchInActiveSession(_agentPath, "/background", _logger, out var info, out var error))
        {
            _lastLaunchError = string.Empty;
            _lastLaunchInfo = $"sesion {info!.SessionId}, PID {info.ProcessId}";
            _logger.LogInfo($"Agente lanzado en la sesión {info.SessionId} (PID {info.ProcessId}).");
            // Vigilancia de arranque: si el agente muere en sus primeros segundos (p. ej. un
            // crash de inicialización), registrar el código de salida en el log en vez de
            // dejar que el watchdog solo lo "reintente" sin explicación.
            var handle = info.ProcessHandle;
            _ = Task.Run(() =>
            {
                try
                {
                    if (WaitForSingleObject(handle, 15000) == 0) // WAIT_OBJECT_0
                    {
                        if (GetExitCodeProcess(handle, out var exitCode))
                        {
                            _lastLaunchInfo += $" (MURIÓ a los pocos segundos, código {exitCode})";
                            _logger.LogError($"El agente lanzado por el servicio MURIÓ en sus primeros segundos. " +
                                             $"Código de salida: {exitCode}. Revise el log del agente para el detalle.");
                        }
                    }
                }
                catch { /* diagnóstico: nunca romper el watchdog */ }
                finally { CloseHandle(handle); }
            });
        }
        else
        {
            _lastLaunchError = error;
            _logger.LogWarning($"No se pudo rearrancar el agente todavía ({error}). Se reintentará en {WatchdogInterval.TotalSeconds} s " +
                               "(es normal antes del primer inicio de sesión).");
        }
    }

    /// <summary>Encola y drena la cola de envíos. Escanéa el TEMP de TODOS los usuarios
    /// (el servicio corre como SYSTEM; los fragmentos viven en el TEMP del usuario que graba).</summary>
    private void UploadOrphans()
    {
        _lastUpload = DateTime.Now;
        _lastQueueError = string.Empty;
        try
        {
            var allOrphans = new List<string>();
            foreach (var tempDir in EnumerateUserTempDirs())
            {
                var cutoff = DateTime.Now.AddMinutes(-2);
                var orphans = Directory.GetFiles(tempDir, "*.mp4")
                    .Concat(Directory.GetFiles(tempDir, "*.recording.mp4"))
                    .Where(f => File.GetLastWriteTime(f) < cutoff)
                    .ToList();

                foreach (var orphan in orphans)
                {
                    var path = orphan;
                    if (path.EndsWith(".recording.mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        var finalPath = path.Substring(0, path.Length - ".recording.mp4".Length) + ".mp4";
                        try { File.Move(path, finalPath, overwrite: true); path = finalPath; }
                        catch { /* se sube con el nombre temporal */ }
                    }
                    allOrphans.Add(path);
                }
            }

            foreach (var orphan in allOrphans)
            {
                _uploadQueue.Enqueue(orphan);
            }

            var subidos = 0;
            if (_uploadQueue.IsAvailable)
            {
                subidos = DrainQueue();
            }
            else
            {
                // Subida directa (mecanismo de respaldo cuando SQLite no está disponible)
                foreach (var orphan in allOrphans)
                {
                    try
                    {
                        if (!File.Exists(orphan)) continue;
                        var fi = new FileInfo(orphan);
                        if (fi.Length < 1024)
                        {
                            try { File.Delete(orphan); } catch { }
                            continue;
                        }
                        var ok = _fileManagementService.TryUploadFileAsync(orphan, maxRetries: 1).GetAwaiter().GetResult();
                        if (ok) { subidos++; _lastUploadInfo = $"{DateTime.Now:HH:mm:ss} {Path.GetFileName(orphan)}"; }
                    }
                    catch (Exception ex)
                    {
                        _lastQueueError = ex.Message;
                    }
                }
            }

            // Latido del drenado: formato estándar para diagnóstico remoto
            _logger.LogInfo($"[watchdog] Drenado: huérfanos={allOrphans.Count} colaDisponible={_uploadQueue.IsAvailable} pendientes={_uploadQueue.CountPending()} subidos={subidos}");
        }
        catch (Exception ex)
        {
            _lastQueueError = ex.Message;
            _logger.LogError("Error al procesar la cola de envíos (watchdog)", ex);
        }
    }

    private int DrainQueue()
    {
        var subidos = 0;
        _uploadQueue.PruneMissingFiles();
        var pending = _uploadQueue.GetPending(20);
        foreach (var path in pending)
        {
            try
            {
                if (!File.Exists(path)) { _uploadQueue.MarkSent(path); continue; }
                var fi = new FileInfo(path);
                if (fi.Length < 1024) // inválido: no subir basura a la NUC
                {
                    _logger.LogError($"Pendiente inválido descartado por el watchdog: {path} ({fi.Length} bytes)");
                    try { File.Delete(path); } catch { }
                    _uploadQueue.MarkSent(path);
                    continue;
                }
                var ok = _fileManagementService.TryUploadFileAsync(path, maxRetries: 1).GetAwaiter().GetResult();
                if (ok)
                {
                    _uploadQueue.MarkSent(path);
                    subidos++;
                    _lastUploadInfo = $"{DateTime.Now:HH:mm:ss} {Path.GetFileName(path)}";
                }
                else _uploadQueue.MarkFailed(path);
            }
            catch (Exception ex)
            {
                _lastQueueError = ex.Message;
                _uploadQueue.MarkFailed(path);
            }
        }
        return subidos;
    }

    private static IEnumerable<string> EnumerateUserTempDirs()
    {
        var dirs = new List<string>();
        var usersRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "..");
        try
        {
            foreach (var userDir in Directory.GetDirectories(usersRoot))
            {
                var temp = Path.Combine(userDir, "AppData", "Local", "Temp", "ScreenRecorder");
                if (Directory.Exists(temp)) dirs.Add(temp);
            }
        }
        catch { /* best effort */ }
        // Incluir también el TEMP propio del servicio por si acaso.
        var own = Path.Combine(Path.GetTempPath(), "ScreenRecorder");
        if (Directory.Exists(own) && !dirs.Contains(own)) dirs.Add(own);
        return dirs;
    }

    /// <summary>Heartbeat en la NUC: una línea periódica para detectar huecos de grabación.</summary>
    private void WriteHeartbeat()
    {
        _lastHeartbeat = DateTime.Now;
        var settings = _settingsService.LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.NetworkPath)) return;

        try
        {
            var agent = Process.GetProcessesByName("WSearchHost").Length > 0 ? "corriendo" : "CAÍDO";
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {Environment.MachineName} | agente={agent} | stopped={(IsStopFlagSet() ? "si" : "no")}{Environment.NewLine}";
            try
            {
                var beatsDir = Path.Combine(settings.NetworkPath, "heartbeats");
                Directory.CreateDirectory(beatsDir);
                File.AppendAllText(Path.Combine(beatsDir, $"{Environment.MachineName}.log"), line);
            }
            catch
            {
                // Reserva: escribir directamente en la raíz del NUC (por si la subcarpeta no se puede crear).
                File.AppendAllText(Path.Combine(settings.NetworkPath, $"{Environment.MachineName}.heartbeat.txt"), line);
            }
        }
        catch (Exception ex)
        {
            if (!_heartbeatFailLogged)
            {
                _heartbeatFailLogged = true;
                _logger.LogWarning($"Heartbeat no enviado: el servicio (SYSTEM) no tiene escritura en la ruta NUC configurada " +
                                   $"('{settings.NetworkPath}'). Concede permiso de escritura a la cuenta de equipo (<pc>$) en ese recurso " +
                                   "para que el servicio pueda subir huérfanos y heartbeat. Detalle: " + ex.Message);
            }
        }
    }

    private static bool IsStopFlagSet() => ScreenRecorder.Core.Constants.AppConstants.IsStopFlagSet();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Arranca un proceso en la sesión interactiva activa usando el token del usuario
/// (WTSQueryUserToken + CreateProcessAsUser). LocalSystem tiene el privilegio necesario.
/// </summary>
internal static class UserSessionLauncher
{
    public sealed class LaunchInfo
    {
        public int ProcessId { get; set; }
        public uint SessionId { get; set; }
        public IntPtr ProcessHandle { get; set; }
    }

    public static bool TryLaunchInActiveSession(string exePath, string arguments, ILoggerService logger,
        out LaunchInfo? info, out string error)
    {
        info = null;
        error = string.Empty;

        // Sesiones candidatas: la de consola primero; si no hay token, TODAS las sesiones
        // activas (fallback imprescindible en RDP/escritorio remoto, donde la consola puede
        // no corresponderse con la sesión del usuario).
        var candidates = new List<uint>();
        var consoleId = WTSGetActiveConsoleSessionId();
        if (consoleId != 0xFFFFFFFF) candidates.Add(consoleId);
        foreach (var id in GetActiveSessionIds())
        {
            if (!candidates.Contains(id)) candidates.Add(id);
        }
        if (candidates.Count == 0)
        {
            error = "no hay ninguna sesión activa";
            return false;
        }

        IntPtr token = IntPtr.Zero;
        uint sessionUsed = 0;
        foreach (var id in candidates)
        {
            if (WTSQueryUserToken(id, out token))
            {
                sessionUsed = id;
                break;
            }
            token = IntPtr.Zero;
        }
        if (token == IntPtr.Zero)
        {
            error = $"WTSQueryUserToken falló en todas las sesiones activas ({string.Join(",", candidates)}) (errno {Marshal.GetLastWin32Error()})";
            return false;
        }
        logger.LogInfo($"Lanzando agente en la sesión {sessionUsed} (consola={consoleId}, candidatas={string.Join(",", candidates)}).");

        IntPtr envBlock = IntPtr.Zero;
        try
        {
            // Entorno del USUARIO (USERPROFILE, TEMP, AppData...). Sin esto, el proceso
            // hereda el entorno de SYSTEM (TEMP=C:\Windows\Temp) y el agente muere al
            // instante al no poder escribir su carpeta temporal con el token del usuario.
            uint creationFlags = 0;
            if (CreateEnvironmentBlock(out envBlock, token, false))
            {
                creationFlags = CREATE_UNICODE_ENVIRONMENT;
            }
            else
            {
                logger.LogWarning($"CreateEnvironmentBlock falló (errno {Marshal.GetLastWin32Error()}); " +
                                  "se lanza el agente sin bloque de entorno del usuario.");
            }

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf<STARTUPINFO>();
            si.lpDesktop = "winsta0\\default";

            if (!CreateProcessAsUser(token, exePath, $"\"{exePath}\" {arguments}", IntPtr.Zero, IntPtr.Zero,
                    false, creationFlags, envBlock, Path.GetDirectoryName(exePath) ?? "C:\\", ref si, out var pi))
            {
                error = $"CreateProcessAsUser falló (errno {Marshal.GetLastWin32Error()})";
                return false;
            }

            info = new LaunchInfo { ProcessId = pi.dwProcessId, SessionId = sessionUsed, ProcessHandle = pi.hProcess };
            CloseHandle(pi.hThread);
            return true;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            CloseHandle(token);
        }
    }

    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version,
        out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPTStr)] public string pWinStationName;
        public int State; // WTS_CONNECTSTATE_CLASS: 0 = WTSActive
    }

    /// <summary>Ids de las sesiones en estado Active (para el fallback cuando la consola no da token).</summary>
    private static List<uint> GetActiveSessionIds()
    {
        var ids = new List<uint>();
        try
        {
            if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var pInfo, out var count) && pInfo != IntPtr.Zero)
            {
                try
                {
                    var size = Marshal.SizeOf<WTS_SESSION_INFO>();
                    for (int i = 0; i < count; i++)
                    {
                        var si = Marshal.PtrToStructure<WTS_SESSION_INFO>(IntPtr.Add(pInfo, i * size));
                        if (si.State == 0 /* WTSActive */ && si.SessionId > 0)
                        {
                            ids.Add((uint)si.SessionId);
                        }
                    }
                }
                finally
                {
                    WTSFreeMemory(pInfo);
                }
            }
        }
        catch { /* el fallback es opcional */ }
        return ids;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(IntPtr hToken, string lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
