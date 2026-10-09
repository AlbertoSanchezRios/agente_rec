using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScreenRecorder.Core.Audio;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Implementación del servicio de grabación usando ffmpeg
/// </summary>
public class RecordingService : IRecordingService
{
    private readonly ILoggerService _logger;
    private readonly ISettingsService _settingsService;
    private readonly IAudioDetectionService _audioDetectionService;
    private readonly IFileManagementService _fileManagementService;
    private readonly ICapturePipelineProbe _pipelineProbe;
    private readonly UploadQueueService _uploadQueue;
    private readonly string _ffmpegPath;
    private readonly string _tempDirectory;

    private Process? _currentRecordingProcess;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _recordingLoopTask;
    private System.Threading.Timer? _orphanUploadTimer;
    private bool _orphanUploadInProgress;
    private DateTime _recordingStartTime;
    private CapturePipeline? _currentPipeline;
    // true cuando se está apagando Windows: cerrar FFmpeg ya, renombrar fragmento y
    // posponer la subida SMB (ver RequestFastShutdown).
    private bool _fastShutdown;
    // true cuando Windows comenzó el apagado/cierre de sesión (SessionEnding/SessionEnded).
    // A diferencia de _fastShutdown NO detiene la grabación: FFmpeg sigue capturando hasta
    // que Windows mata el proceso; solo evita arrancar fragmentos nuevos y, cuando el
    // fragmento en curso termina (o lo mata Windows), lo cierra sin subir.
    private bool _systemEnding;

    public bool IsRecording { get; private set; }

    public event EventHandler<string>? RecordingCompleted;

    public RecordingService(
        ILoggerService logger,
        ISettingsService settingsService,
        IAudioDetectionService audioDetectionService,
        IFileManagementService fileManagementService,
        ICapturePipelineProbe pipelineProbe)
    {
        _logger = logger;
        _settingsService = settingsService;
        _audioDetectionService = audioDetectionService;
        _fileManagementService = fileManagementService;
        _pipelineProbe = pipelineProbe;
        _uploadQueue = new UploadQueueService(logger);

        _ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.AppConstants.FFmpegExeName);
        _tempDirectory = ResolveTempDirectory();
        Directory.CreateDirectory(_tempDirectory);
    }

    /// <summary>
    /// Carpeta temporal de fragmentos: %TEMP%\ScreenRecorder, con respaldo en
    /// %LOCALAPPDATA%\Temp\ScreenRecorder si el TEMP heredado no es escribible por este
    /// proceso (p. ej. entorno heredado de SYSTEM cuando el servicio lanza al agente).
    /// </summary>
    private static string ResolveTempDirectory()
    {
        var preferred = Path.Combine(Path.GetTempPath(), "ScreenRecorder");
        try
        {
            Directory.CreateDirectory(preferred);
            var probe = Path.Combine(preferred, $".probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return preferred;
        }
        catch
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp", "ScreenRecorder");
            try
            {
                Directory.CreateDirectory(fallback);
                return fallback;
            }
            catch
            {
                // Último recurso: ProgramData (siempre escribible por el agente elevado).
                var last = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ScreenRecorder", "TempFragments");
                Directory.CreateDirectory(last);
                return last;
            }
        }
    }

    public Task StartRecordingAsync()
    {
        if (IsRecording)
        {
            _logger.LogWarning("Intento de iniciar grabación cuando ya está en curso");
            return Task.CompletedTask;
        }

        try
        {
            var settings = _settingsService.LoadSettings();
            _recordingStartTime = DateTime.Now;
            _fastShutdown = false;
            _systemEnding = false;
            _cancellationTokenSource = new CancellationTokenSource();

            IsRecording = true;
            _logger.LogInfo("Iniciando grabación de pantalla");

            // La resolución del pipeline (sondeo una vez por máquina) ocurre DENTRO del loop en
            // un hilo de fondo: no se bloquea el hilo de la UI ni se retrasa el arranque.
            _recordingLoopTask = Task.Run(() => RecordInFragmentsAsync(settings, _cancellationTokenSource.Token));

            // Verificación temprana de la ruta NUC (veredicto en el log: NUC OK / NO DISPONIBLE / SIN PERMISOS)
            _ = Task.Run(() => _fileManagementService.PreflightNetworkPathAsync());

            // Subir fragmentos huérfanos de corridas anteriores (quedaron en temporal por errores/apagados)
            _ = Task.Run(() => UploadOrphanFragmentsAsync());

            // Drenado en caliente de la cola de envíos (SQLite): si la red a la NUC falla,
            // los videos pendientes se suben solos cada 2 minutos en cuanto vuelve la ruta.
            _orphanUploadTimer = new System.Threading.Timer(
                _ =>
                {
                    if (_orphanUploadInProgress) return;
                    _ = UploadOrphanFragmentsAsync();
                },
                null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
            
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            IsRecording = false;
            _logger.LogError("Error al iniciar grabación", ex);
            throw;
        }
    }

    public void RequestFastShutdown()
    {
        if (!_fastShutdown)
        {
            _logger.LogInfo("Apagado del sistema: cerrando FFmpeg de inmediato (la subida se aplaza al rearranque)");
        }
        _fastShutdown = true;
        StopLoopback();
        try { _cancellationTokenSource?.Cancel(); } catch { /* ya cancelado */ }
    }

    /// <summary>
    /// Aviso de que Windows está apagándose o cerrando la sesión. La grabación NO se detiene:
    /// continúa hasta que el sistema mate a FFmpeg (así el video cubre hasta el último instante
    /// visible, incluida la pantalla de "Apagando"). El flag solo hace que, cuando el fragmento
    /// en curso termine, no se arranque uno nuevo y se cierre sin subir (la subida la hará la
    /// recuperación de huérfanos al rearrancar). Si el apagado se cancela, la grabación sigue.
    /// </summary>
    public void NotifySystemEnding()
    {
        if (_systemEnding) return;
        _systemEnding = true;
        _logger.LogInfo("Apagado/cierre de sesión detectado: la grabación continúa hasta el apagado final; " +
                        "el fragmento en curso se cerrará sin subir y se recuperará al rearrancar.");
    }

    private void StopLoopback()
    {
        try { _loopback?.Dispose(); } catch { /* nunca romper */ }
        _loopback = null;
    }

    /// <summary>Si el fragmento falló usando el loopback propio, contar; tras 2 fallos se
    /// deshabilita para la sesión y el siguiente fragmento usa el respaldo dshow.</summary>
    private void NoteLoopbackFailure()
    {
        if (_loopback == null) return;
        _loopbackFailures++;
        if (_loopbackFailures >= 2)
        {
            _logger.LogWarning("WASAPI loopback falló 2 veces seguidas: se deshabilita para esta sesión " +
                               "y se usa el respaldo dshow ('Mezcla estéreo').");
            StopLoopback();
        }
    }

    public async Task StopRecordingAsync()
    {
        if (!IsRecording)
        {
            return;
        }

        try
        {
            _logger.LogInfo("Deteniendo grabación de pantalla");
            _cancellationTokenSource?.Cancel();

            // Esperar a que el ciclo de fragmentos cierre FFmpeg y finalice/suba el último fragmento.
            // Presupuesto acorde con RecordingManagerService.Stop() (90 s): la subida del último
            // fragmento por SMB puede tardar decenas de segundos; si se agota, el fragmento queda
            // en temporal con su nombre final y la recuperación de huérfanos lo sube al rearrancar.
            if (_recordingLoopTask != null)
            {
                await Task.WhenAny(_recordingLoopTask, Task.Delay(90000));
            }

            // Respaldo: si quedó algún proceso de FFmpeg, forzar su cierre
            await StopCurrentFfmpegProcessAsync();
            IsRecording = false;
            _currentPipeline = null; // la próxima sesión vuelve a sondear (respeta cambios de fps/dup_frames)
            _logger.LogInfo("Grabación detenida");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al detener grabación", ex);
            IsRecording = false;
            _currentPipeline = null;
        }
    }

    /// <summary>
    /// Registra en una sola línea la configuración efectiva con la que arranca la grabación,
    /// para diagnóstico remoto sin necesidad de abrir el Registry.
    /// </summary>
    private void LogEffectiveConfiguration(RecordingSettings settings, CapturePipeline pipeline)
    {
        try
        {
            var audio = new List<string>();
            if (settings.IncludeSystemAudio) audio.Add("sistema");
            if (settings.IncludeMicrophone) audio.Add("mic");
            _logger.LogInfo(
                $"Config efectiva: v{Constants.AppConstants.AppVersion} | " +
                $"usuario={Environment.UserName} | elevado={NetworkPathHelper.IsProcessElevated()} | " +
                $"NUC='{settings.NetworkPath ?? "(sin configurar)"}' | " +
                $"fragmento={Math.Clamp(settings.FragmentDurationMinutes, 1, 120)}min | " +
                $"target={settings.CaptureTarget} region={pipeline.Region.Size} | " +
                $"modo={(settings.CaptureBackendPreference == 2 ? "GDI" : settings.CaptureBackendPreference == 1 ? "GPU" : "Auto")} | " +
                $"{pipeline.Describe()} | " +
                $"audio: {(audio.Count > 0 ? string.Join("+", audio) : "no")}");
        }
        catch
        {
            // Es solo diagnóstico: nunca debe romper el inicio de la grabación
        }
    }

    /// <summary>
    /// Auto-encola los fragmentos huérfanos de corridas anteriores (apagados/cortes) y a
    /// continuación DRENA la cola de envíos (sube pendientes con reintento). La cola vive
    /// en SQLite, así que nada depende de reiniciar para reintentar un envío fallido.
    /// </summary>
    private async Task UploadOrphanFragmentsAsync()
    {
        if (_orphanUploadInProgress) return;

        try
        {
            _orphanUploadInProgress = true;
            var cutoff = DateTime.Now.AddMinutes(-2);
            // *.mp4 = fragmentos cerrados que nunca se subieron; *.recording.mp4 = restos de
            // un corte de luz/proceso matado (se renombran a .mp4 antes de encolar).
            var orphans = Directory.GetFiles(_tempDirectory, "*.mp4")
                .Concat(Directory.GetFiles(_tempDirectory, "*.recording.mp4"))
                .Where(f => File.GetLastWriteTime(f) < cutoff)
                .ToList();
            var subidos = 0;

            foreach (var orphan in orphans)
            {
                var path = orphan;
                if (path.EndsWith(".recording.mp4", StringComparison.OrdinalIgnoreCase))
                {
                    var finalPath = path.Substring(0, path.Length - ".recording.mp4".Length) + ".mp4";
                    try
                    {
                        File.Move(path, finalPath, overwrite: true);
                        path = finalPath;
                        _logger.LogInfo($"Fragmento recuperado de corte/parada brusca: {finalPath}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"No se pudo renombrar el fragmento recuperado {path}; se encola con su nombre temporal", ex);
                    }
                }

                if (!IsValidMp4(path))
                {
                    _logger.LogError($"Fragmento pendiente INVÁLIDO descartado: {path} (0 bytes o sin cabecera MP4).");
                    TryDelete(path);
                    continue;
                }

                // Subida DIRECTA (mecanismo de la versión que funcionaba), sin cola.
                try { if (await _fileManagementService.TryUploadFileAsync(path, maxRetries: 1)) subidos++; }
                catch (Exception exUp) { _logger.LogWarning($"Drenado directo falló para {path}: {exUp.Message}"); }
            }

            // Latido del drenado: una línea por ciclo para diagnóstico remoto inmediato.
            _logger.LogInfo($"Drenado: huérfanos={orphans.Count} subidos={subidos}");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al procesar la cola de envíos", ex);
        }
        finally
        {
            _orphanUploadInProgress = false;
        }
    }

    /// <summary>Sube los pendientes de la cola SQLite. Reintentos sin tope: MarkFailed solo
    /// incrementa intentos; la siguiente pasada lo vuelve a intentar.</summary>
    private async Task<int> DrainUploadQueueAsync(TimeSpan? timebox = null)
    {
        var deadline = timebox.HasValue ? DateTime.UtcNow + timebox.Value : DateTime.MaxValue;
        _uploadQueue.PruneMissingFiles();
        var pending = _uploadQueue.GetPending(20);
        if (pending.Count == 0) return 0;
        var subidos = 0;

        _logger.LogInfo($"Cola de envíos: {pending.Count} fragmento(s) pendiente(s).");
        foreach (var path in pending)
        {
            if (DateTime.UtcNow > deadline) break;
            if (!File.Exists(path)) { _uploadQueue.MarkSent(path); continue; }
            if (!IsValidMp4(path))
            {
                _logger.LogError($"Pendiente inválido descartado: {path}. Cabecera: {HeaderHex(path)}");
                TryDelete(path);
                _uploadQueue.MarkSent(path);
                continue;
            }

            // Un solo intento por ciclo: el reintento real lo da la cola en el siguiente
            // drenado (cada 2 min). Asi un ciclo nunca se bloquea ~30 s por archivo cuando
            // la NUC esta caida y no se atrasa el resto de la cola.
            var ok = await _fileManagementService.TryUploadFileAsync(path, maxRetries: 1);
            if (ok) { _uploadQueue.MarkSent(path); subidos++; }
            else _uploadQueue.MarkFailed(path);
        }
        return subidos;
    }

    private async Task RecordInFragmentsAsync(RecordingSettings settings, CancellationToken cancellationToken)
    {
        int fragmentNumber = 0;
        int consecutiveFailures = 0;
        const int maxConsecutiveFailures = 5;

        // Resolver (sondear) el pipeline de captura+codificación una sola vez por ciclo de grabación.
        // Se hace aquí, en el hilo del loop, para no bloquear el hilo de la UI.
        _currentPipeline ??= await _pipelineProbe.ResolveAsync(settings);
        LogEffectiveConfiguration(settings, _currentPipeline);

        // Arrancar la captura WASAPI loopback propia (audio del sistema sin depender de
        // "Mezcla estéreo"). Si no hay dispositivo de reproducción, se usa el respaldo dshow.
        if (settings.IncludeSystemAudio && _loopback == null && _loopbackFailures < 2)
        {
            var cap = new WasapiLoopbackCapture();
            if (cap.TryStart(_logger)) _loopback = cap;
            else _logger.LogWarning($"WASAPI loopback no disponible ({cap.LastError}); se usa el respaldo dshow.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var fileName = GenerateFileName(settings, fragmentNumber);
                var finalPath = Path.Combine(_tempDirectory, fileName);
                // El fragmento EN CURSO se escribe como .recording.mp4 y se renombra al .mp4 final
                // cuando FFmpeg cierra: así un fragmento a medio escribir nunca se confunde con uno
                // completo (la recuperación de huérfanos y la NUC solo ven archivos cerrados).
                var recordingPath = Path.Combine(_tempDirectory,
                    Path.GetFileNameWithoutExtension(fileName) + ".recording.mp4");

                _logger.LogInfo($"Iniciando fragmento {fragmentNumber + 1}: {fileName}");

                var pipeline = _currentPipeline ?? throw new InvalidOperationException("Pipeline de captura no resuelto");
                var ffmpegArgs = BuildFFmpegArguments(settings, recordingPath, pipeline);
                _logger.LogInfo($"FFmpeg iniciando con argumentos: {ffmpegArgs}");
                _logger.LogInfo($"Ruta de salida: {recordingPath}");

                _currentRecordingProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        Arguments = ffmpegArgs,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                // Baja prioridad: la grabación no debe competir con el trabajo del usuario (cursor, Excel...).
                _currentRecordingProcess.Start();
                try { _currentRecordingProcess.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Exception ex) { _logger.LogWarning($"No se pudo bajar la prioridad de FFmpeg: {ex.Message}"); }

                // Anti-manipulación: que un usuario estándar no pueda matar FFmpeg (dejaría la
                // grabación muda sin que el watchdog lo note como caída del agente).
                ProcessProtection.DenyTerminateForUsers(_currentRecordingProcess, _logger, "FFmpeg (WmvCoreHost.exe)");

                // Si el audio del sistema va por nuestro loopback WASAPI, conectar la captura
                // al stdin de ESTE proceso FFmpeg (pipe:0) y marcar que stdin no admite 'q'.
                _stdinCarriesAudio = _loopback is { IsRunning: true } && ffmpegArgs.Contains("pipe:0");
                if (_stdinCarriesAudio)
                {
                    _loopback!.Client = _currentRecordingProcess.StandardInput.BaseStream;
                }

                // Captura incremental de stderr: sirve para diagnosticar fallos y para saber cuándo
                // FFmpeg ya mapeó los streams (sustituye la espera fija de 2000 ms por fragmento).
                var stderrLines = new List<string>();
                var stderrLock = new object();
                _currentRecordingProcess.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) { lock (stderrLock) { stderrLines.Add(e.Data); } }
                };
                _currentRecordingProcess.BeginErrorReadLine();
                // Descartar stdout en segundo plano para evitar bloqueo por llenado del buffer.
                _ = Task.Run(() =>
                {
                    try { _currentRecordingProcess.StandardOutput.ReadToEnd(); } catch { /* proceso cerrado */ }
                });

                string GetStderrText()
                {
                    lock (stderrLock) { return string.Join("\n", stderrLines); }
                }

                // Chequeo de salud: esperar a que aparezca "Stream mapping" (FFmpeg ya abrió los
                // dispositivos) o a que el proceso falle. Polling corto en vez de espera fija.
                var streamMapped = false;
                var healthDeadline = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < healthDeadline)
                {
                    if (_currentRecordingProcess.HasExited) break;
                    lock (stderrLock)
                    {
                        streamMapped = stderrLines.Any(l => l.Contains("Stream mapping", StringComparison.Ordinal));
                    }
                    if (streamMapped) break;
                    await Task.Delay(200, cancellationToken);
                }

                if (_currentRecordingProcess.HasExited)
                {
                    var exitCode = _currentRecordingProcess.ExitCode;
                    var errorOutput = GetStderrText();
                    _logger.LogError($"FFmpeg terminó durante el chequeo de salud. Código: {exitCode}. Error: {errorOutput}");
                    throw new InvalidOperationException($"FFmpeg falló al iniciar. Código: {exitCode}. Error: {errorOutput}");
                }

                _logger.LogInfo($"FFmpeg iniciado correctamente (PID: {_currentRecordingProcess.Id})");

                // Verificación REAL de audio: contar los streams de audio que FFmpeg mapeó frente a
                // los inputs de audio configurados (el antiguo heurístico de memoria no detectaba audio mudo).
                try
                {
                    var expectedAudioInputs = 0;
                    if (settings.IncludeSystemAudio) expectedAudioInputs++;
                    if (settings.IncludeMicrophone) expectedAudioInputs++;

                    if (expectedAudioInputs > 0)
                    {
                        // Dar un instante a que aparezcan las líneas "Stream #N: Audio:".
                        await Task.Delay(400, cancellationToken);
                        var text = GetStderrText();
                        var audioStreams = text.Split('\n')
                            .Count(l => l.Contains("Stream #", StringComparison.Ordinal) && l.Contains("Audio:", StringComparison.Ordinal));
                        if (audioStreams < expectedAudioInputs)
                        {
                            _logger.LogError($"FFmpeg arrancó con {audioStreams} stream(s) de audio pero se esperaban {expectedAudioInputs}. " +
                                             "La grabación de este fragmento será parcialmente muda. Revisar dispositivos de audio.");
                        }
                        else
                        {
                            _logger.LogInfo($"Streams de audio confirmados: {audioStreams}/{expectedAudioInputs}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"No se pudo verificar los streams de audio: {ex.Message}");
                }

                // Gracia post-arranque: algunos fallos (p. ej. la conversión de formatos
                // d3d11->qsv) ocurren JUSTO después del mapeo de streams. Si FFmpeg muere dentro
                // de esta ventana, fallar ya para reintentar en vez de dar por iniciado un
                // fragmento condenado.
                var graceDeadline = DateTime.UtcNow.AddSeconds(2.5);
                while (DateTime.UtcNow < graceDeadline && !_currentRecordingProcess.HasExited)
                {
                    await Task.Delay(250, cancellationToken);
                }
                if (_currentRecordingProcess.HasExited)
                {
                    var earlyCode = _currentRecordingProcess.ExitCode;
                    var earlyError = GetStderrText();
                    _logger.LogError($"FFmpeg murió justo después del arranque. Código: {earlyCode}. Error: {earlyError}");
                    throw new InvalidOperationException($"FFmpeg falló tras el arranque. Código: {earlyCode}. Error: {earlyError}");
                }

                // Esperar el tiempo del fragmento o hasta cancelación
                var durationMinutes = Math.Clamp(settings.FragmentDurationMinutes, 1, 120);
                var fragmentDuration = TimeSpan.FromMinutes(durationMinutes);
                var durationTask = Task.Delay(fragmentDuration, cancellationToken);
                var exitTask = Task.Run(() =>
                {
                    try { _currentRecordingProcess?.WaitForExit(); } catch { /* proceso ya terminado */ }
                });
                await Task.WhenAny(durationTask, exitTask);

                // Apagado de Windows confirmado (WM_ENDSESSION recibido): cerrar el fragmento en
                // curso SIN arrancar uno nuevo. Llegamos aquí cuando el fragmento cumplió su
                // duración durante la ventana de apagado o cuando Windows mató a FFmpeg (exitTask).
                // El guard HasShutdownStarted distingue un apagado real de un fallo normal de
                // FFmpeg y, si el usuario canceló el apagado, la grabación continúa sin cortes.
                if (_systemEnding && Environment.HasShutdownStarted)
                {
                    _logger.LogInfo("Apagado en curso: cerrando el fragmento final sin subir (la subida se hará al rearrancar).");
                    await StopCurrentFfmpegProcessAsync(exitTimeoutMs: 1500);
                    await FinalizeAndUploadFragmentAsync(recordingPath, finalPath, "apagado del sistema",
                        waitForUpload: false);
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    // En apagado de Windows el presupuesto es de ~5 s: cerrar FFmpeg ya (timeout
                    // corto) y renombrar el fragmento SIN subir; la subida la hará la recuperación
                    // de huérfanos al rearrancar. En 'echate'/parada normal se espera la subida real.
                    await StopCurrentFfmpegProcessAsync(exitTimeoutMs: _fastShutdown ? 2500 : 15000);
                    await FinalizeAndUploadFragmentAsync(recordingPath, finalPath, "grabación detenida",
                        waitForUpload: !_fastShutdown);
                    break;
                }

                if (_currentRecordingProcess != null && _currentRecordingProcess.HasExited && _currentRecordingProcess.ExitCode != 0)
                {
                    var error = GetStderrText();
                    NoteLoopbackFailure();
                    _logger.LogError($"FFmpeg terminó con error. Código: {_currentRecordingProcess.ExitCode}. Error: {error}");
                    _currentRecordingProcess?.Dispose();
                    _currentRecordingProcess = null;
                    consecutiveFailures++;
                    if (!await HandleFragmentFailureAsync(consecutiveFailures, maxConsecutiveFailures, cancellationToken))
                        break;
                    continue;
                }

                // Finalizar FFmpeg de forma ordenada para que cierre el archivo y libere recursos
                await StopCurrentFfmpegProcessAsync();

                await FinalizeAndUploadFragmentAsync(recordingPath, finalPath, "fragmento completado");

                consecutiveFailures = 0; // fragmento exitoso: reiniciar el contador de fallos
                fragmentNumber++;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("Grabación cancelada");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error durante la grabación de fragmento", ex);
                NoteLoopbackFailure();
                // Asegurar que no quede un proceso de FFmpeg colgado antes de reintentar
                await StopCurrentFfmpegProcessAsync();
                consecutiveFailures++;
                if (!await HandleFragmentFailureAsync(consecutiveFailures, maxConsecutiveFailures, cancellationToken))
                    break;
            }
        }

        IsRecording = false;
        StopLoopback();

        // Detener los reintentos programados de upload
        var timer = _orphanUploadTimer;
        _orphanUploadTimer = null;
        timer?.Dispose();
    }

    /// <summary>
    /// Maneja un fallo de fragmento: registra, espera 5 s y decide si se reintenta (continua)
    /// o se detiene la grabación (false) tras demasiados fallos consecutivos.
    /// Lanza OperationCanceledException si la grabación se canceló durante la espera.
    /// </summary>
    private async Task<bool> HandleFragmentFailureAsync(int consecutiveFailures, int maxConsecutiveFailures, CancellationToken cancellationToken)
    {
        if (consecutiveFailures >= maxConsecutiveFailures)
        {
            _logger.LogError($"Se alcanzaron {maxConsecutiveFailures} fallos consecutivos. Se detiene la grabación.");
            return false;
        }

        _logger.LogInfo($"Reintentando nuevo fragmento en 5 segundos (fallo {consecutiveFailures}/{maxConsecutiveFailures})...");
        await Task.Delay(5000, cancellationToken);
        return true;
    }

    /// <summary>
    /// Valida que un MP4 sea íntegro: existe, tiene un tamaño mínimo y empieza por la caja
    /// 'ftyp'. Así la NUC nunca recibe videos de 0 bytes/corruptos ("a veces no se pueden ver").
    /// </summary>
    private static bool IsValidMp4(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 1024) return false;
            using var fs = File.OpenRead(path);
            var head = new byte[12];
            if (fs.Read(head, 0, 12) < 12) return false;
            return head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Primeros 16 bytes del archivo en hex, para diagnosticar rechazos de validación.</summary>
    private static string HeaderHex(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var b = new byte[16];
            var n = fs.Read(b, 0, 16);
            return BitConverter.ToString(b, 0, n).Replace("-", " ");
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>
    /// Finaliza un fragmento: renombra el .recording.mp4 a su nombre .mp4 final (marca de que el
    /// fragmento está cerrado), notifica y lanza la subida a la ruta remota. Se usa tanto al
    /// completar el fragmento como al detener la grabación, para que el último fragmento también
    /// llegue a la NUC (antes quedaba abandonado en la carpeta temporal).
    /// En apagado de Windows (_fastShutdown) solo renombra: la subida la hará la recuperación
    /// de huérfanos al rearrancar (no cabe la copia SMB en los ~5 s que da Windows).
    /// </summary>
    private async Task FinalizeAndUploadFragmentAsync(string recordingPath, string finalPath, string reason, bool waitForUpload = false)
    {
        // FFmpeg ya cerró su handle del archivo antes de llegar aquí (StopCurrentFfmpegProcessAsync
        // espera a que el proceso termine): no hace falta espera fija.
        var sourcePath = finalPath;
        if (File.Exists(recordingPath))
        {
            try
            {
                File.Move(recordingPath, finalPath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError($"No se pudo renombrar {recordingPath} a su nombre final; se sube con el nombre temporal", ex);
                sourcePath = recordingPath;
            }
        }
        else if (!File.Exists(finalPath))
        {
            _logger.LogWarning($"No se encontró el archivo del fragmento ({reason}): {finalPath}");
            return;
        }

        // No subir ni conservar fragmentos vacíos/corruptos (dejan videos "no reproducibles").
        if (!IsValidMp4(sourcePath))
        {
            _logger.LogError($"Fragmento INVÁLIDO ({reason}) descartado: {sourcePath} (0 bytes o sin cabecera MP4). Cabecera: {HeaderHex(sourcePath)}");
            TryDelete(sourcePath);
            return;
        }

        _logger.LogInfo($"Fragmento finalizado ({reason}): {sourcePath}");
        RecordingCompleted?.Invoke(this, sourcePath);

        if (_fastShutdown)
        {
            _logger.LogInfo("Apagado del sistema: fragmento cerrado localmente y pendiente de subida (se subirá al reanudar).");
            return;
        }

        // Subida DIRECTA (mecanismo de la versión que funcionaba): en segundo plano durante
        // la grabación; esperada al detener. La cola SQLite quedó descartada: añadía fallos
        // silenciosos sin beneficio real. El drenado periódico reintenta lo que falle.
        if (waitForUpload)
        {
            await _fileManagementService.TryUploadFileAsync(sourcePath, maxRetries: 3);
        }
        else
        {
            _ = Task.Run(() => _fileManagementService.TryUploadFileAsync(sourcePath, maxRetries: 3));
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* no crítico */ }
    }

    /// <summary>
    /// Envía 'q' a FFmpeg para finalizar de forma ordenada, espera a que termine y libera el proceso.
    /// Evita que queden procesos ffmpeg.exe huérfanos consumiendo RAM.
    /// </summary>
    private async Task StopCurrentFfmpegProcessAsync(int exitTimeoutMs = 15000)
    {
        var process = _currentRecordingProcess;
        _currentRecordingProcess = null;
        var stdinIsAudio = _stdinCarriesAudio;
        _stdinCarriesAudio = false;
        if (_loopback != null) _loopback.Client = null; // desconectar el PCM antes de cerrar
        if (process == null || process.HasExited)
        {
            process?.Dispose();
            return;
        }
        if (stdinIsAudio)
        {
            // El stdin transporta audio: no se puede enviar 'q'. Matar es seguro: el MP4
            // fragmentado (empty_moov) queda íntegro y reproducible.
            try { process.Kill(); } catch { /* ya terminado */ }
            await Task.Run(() => process.WaitForExit(5000));
            process.Dispose();
            return;
        }
        try
        {
            process.StandardInput.WriteLine("q");
            process.StandardInput.Flush();
            await Task.Run(() => process.WaitForExit(exitTimeoutMs));
            if (!process.HasExited)
            {
                _logger.LogWarning($"FFmpeg no respondió a 'q' en {exitTimeoutMs / 1000} s; forzando cierre.");
                process.Kill();
                await Task.Run(() => process.WaitForExit(3000));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al detener FFmpeg", ex);
            try { if (!process.HasExited) process.Kill(); } catch { /* ignorar */ }
        }
        finally
        {
            process.Dispose();
        }
    }

    // Audio del sistema por WASAPI loopback PROPIO (C# -> named pipe -> FFmpeg). FFmpeg no
    // soporta WASAPI, así que capturamos nosotros y alimentamos un pipe con PCM s16le.
    private WasapiLoopbackCapture? _loopback;
    private int _loopbackFailures;
    // true cuando el stdin de FFmpeg transporta el PCM del loopback (no se puede usar para 'q').
    private bool _stdinCarriesAudio;

    // Detección (una vez por proceso) de si el binario FFmpeg incluye soporte WASAPI.
    // El binario distribuido puede no traerlo compilado (--disable-autodetect): emitir
    // '-f wasapi' en ese caso hace que FFmpeg muera al parsear argumentos y tumba TODA
    // la grabación (lo ocurrido con el build gyan 2026-02).
    private static bool? _wasapiSupported;
    private static readonly object _wasapiLock = new object();

    private bool IsWasapiSupported()
    {
        lock (_wasapiLock)
        {
            if (_wasapiSupported.HasValue) return _wasapiSupported.Value;
            _wasapiSupported = false;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = "-hide_banner -devices",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var stderrTask = p.StandardError.ReadToEndAsync();
                    var stdoutTask = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { /* ya terminó */ } }
                    var text = (stderrTask.Result ?? string.Empty) + (stdoutTask.Result ?? string.Empty);
                    _wasapiSupported = text.Contains("wasapi", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { /* ante la duda: sin wasapi */ }
            _logger.LogInfo(_wasapiSupported.Value
                ? "FFmpeg soporta WASAPI: audio del sistema por loopback (sin depender de 'Mezcla estéreo')."
                : "FFmpeg NO soporta WASAPI: audio del sistema por dshow 'Mezcla estéreo' cuando exista.");
            return _wasapiSupported.Value;
        }
    }

    private string BuildFFmpegArguments(RecordingSettings settings, string outputPath, CapturePipeline pipeline)
    {
        var args = new System.Text.StringBuilder();
        string? gdiScaleFilter = null;

        // Video: según el backend elegido por el sondeo.
        if (pipeline.Backend == CaptureBackend.Dda)
        {
            var dup = pipeline.DupFrames ? "" : ":dup_frames=0";
            // draw_mouse=0: DDA no dibuja el cursor. En GPUs Intel eso ELIMINA el parpadeo del
            // puntero físico y además quita trabajo por frame (se queda en GPU, sin subir CPU).
            // El cursor NO aparece en el video grabado.
            // hwdownload,format=bgra SIEMPRE: sin él los frames quedan en formato hw 'd3d11' y
            // la conversión automática al 'nv12/qsv' que pide h264_qsv FALLA en este build
            // ("Impossible to convert ... src: d3d11, dst: nv12 qsv" -> toda la grabación cae).
            // El escalado sí es opcional: solo cuando la resolución objetivo difiere de la nativa.
            var needsScale = pipeline.Scale.Width != pipeline.Region.Width || pipeline.Scale.Height != pipeline.Region.Height;
            var filter = needsScale
                ? $"ddagrab=output_idx={pipeline.OutputIndex}:framerate={pipeline.Fps}{dup}:draw_mouse=0,hwdownload,format=bgra,scale={pipeline.Scale.Width}:{pipeline.Scale.Height}"
                : $"ddagrab=output_idx={pipeline.OutputIndex}:framerate={pipeline.Fps}{dup}:draw_mouse=0,hwdownload,format=bgra";
            args.Append($"-f lavfi -i \"{filter}\" ");
        }
        else
        {
            args.Append($"-f gdigrab -framerate {pipeline.Fps} ");
            args.Append($"-offset_x {pipeline.Region.X} -offset_y {pipeline.Region.Y} ");
            args.Append($"-video_size {pipeline.Region.Width}x{pipeline.Region.Height} ");
            args.Append("-i desktop ");
            // El scale se añade DESPUÉS de todos los inputs (-vf es opción de SALIDA):
            // si se pone aquí y hay inputs de audio detrás, FFmpeg falla con EINVAL.
            if (pipeline.Scale.Width != pipeline.Region.Width || pipeline.Scale.Height != pipeline.Region.Height)
            {
                gdiScaleFilter = $"scale={pipeline.Scale.Width}:{pipeline.Scale.Height}";
            }
        }

        // Audio del sistema: WASAPI loopback cuando el binario lo soporte (graba la salida por
        // defecto de Windows; NO depende de "Mezcla estéreo"). Respaldo: dshow "Mezcla
        // estéreo" detectada. Solo se emite '-f wasapi' si el binario lo tiene compilado:
        // de lo contrario FFmpeg muere al parsear argumentos y tumba toda la grabación.
        var audioInputs = new List<string>();
        var wasapi = IsWasapiSupported();

        if (settings.IncludeSystemAudio)
        {
            string? systemAudioInput = null;

            if (!settings.AutoDetectAudio && !string.IsNullOrEmpty(settings.SystemAudioDevice))
            {
                // Selección manual heredada: nombre amigable o id @device_cm_... entre comillas.
                systemAudioInput = $"-f dshow -i audio=\"{settings.SystemAudioDevice}\"";
                _logger.LogInfo($"Audio del sistema (manual): {settings.SystemAudioDevice}");
            }
            else if (_loopback is { IsRunning: true })
            {
                // Audio del sistema REAL en cualquier PC: captura WASAPI loopback propia
                // (C#) enviando PCM por named pipe a FFmpeg (que no soporta WASAPI).
                // El PCM va por el STDIN de FFmpeg (pipe:0): sin paths de pipes nombrados,
                // sin escaping, sin ACL. Al no poder usar stdin para 'q', la parada es por Kill
                // (el MP4 fragmentado sobrevive igual).
                systemAudioInput = "-f s16le -ar " + _loopback.SampleRate + " -ac " + _loopback.Channels +
                                   " -i pipe:0";
                _logger.LogInfo($"Audio del sistema: WASAPI loopback propio ({_loopback.SampleRate} Hz, {_loopback.Channels} canales)");
            }
            else if (wasapi)
            {
                systemAudioInput = "-f wasapi -loopback 1 -i default";
                _logger.LogInfo("Audio del sistema: WASAPI loopback (dispositivo de reproducción por defecto)");
            }
            else
            {
                var systemDevice = _audioDetectionService.GetPreferredSystemAudioDevice();
                if (systemDevice != null)
                {
                    systemAudioInput = $"-f dshow -i {systemDevice.DeviceId}";
                    _logger.LogInfo($"Audio del sistema (dshow): {systemDevice.Name} -> {systemDevice.DeviceId}");
                }
                else
                {
                    _logger.LogWarning("FFmpeg sin WASAPI y sin 'Mezcla estéreo' (dshow): el audio del sistema no se grabará. " +
                                       "Para grabarlo instale un build de FFmpeg con WASAPI (ver documentación).");
                }
            }

            if (!string.IsNullOrEmpty(systemAudioInput))
            {
                audioInputs.Add(systemAudioInput);
            }
        }

        if (settings.IncludeMicrophone)
        {
            string? micInput = null;
            if (settings.AutoDetectAudio)
            {
                var micDevice = _audioDetectionService.GetPreferredMicrophoneDevice();
                if (micDevice != null)
                {
                    micInput = $"-f dshow -i {micDevice.DeviceId}";
                    _logger.LogInfo($"Micrófono (detección automática): {micDevice.Name} -> {micDevice.DeviceId}");
                }
                else if (wasapi)
                {
                    micInput = "-f wasapi -i default";
                    _logger.LogInfo("Micrófono: WASAPI (dispositivo de captura por defecto, no hay dshow detectado)");
                }
            }
            if (string.IsNullOrEmpty(micInput) && !string.IsNullOrEmpty(settings.MicrophoneDevice))
            {
                micInput = $"-f dshow -i audio=\"{settings.MicrophoneDevice}\"";
                _logger.LogInfo($"Micrófono (manual/guardado): {settings.MicrophoneDevice}");
            }
            if (!string.IsNullOrEmpty(micInput))
                audioInputs.Add(micInput);
            else
                _logger.LogWarning("No se detectó dispositivo de micrófono ni hay uno guardado.");
        }

        // Último recurso: pista de audio silenciosa para que el MP4 SIEMPRE tenga audio.
        if (audioInputs.Count == 0)
        {
            audioInputs.Add("-f lavfi -i anullsrc=r=48000:cl=stereo");
            _logger.LogError("No se configuró ningún input de audio válido: la grabación llevará una pista de audio SILENCIOSA.");
        }

        _logger.LogInfo($"Total de inputs de audio configurados: {audioInputs.Count}");

        // Inputs numerados: video = 0, audios = 1..
        foreach (var audioInput in audioInputs)
        {
            args.Append($"{audioInput} ");
        }

        // Filtro de escala de GDI (opción de salida: va tras TODOS los inputs).
        if (gdiScaleFilter != null)
        {
            args.Append($"-vf {gdiScaleFilter} ");
        }

        // Mapeos con índices correctos (no se asume que el audio es siempre 1).
        if (audioInputs.Count > 1)
        {
            var filterComplex = new System.Text.StringBuilder();
            filterComplex.Append("-filter_complex \"");
            for (int i = 0; i < audioInputs.Count; i++)
            {
                filterComplex.Append($"[{i + 1}:a]aresample=48000[a{i}];");
            }
            for (int i = 0; i < audioInputs.Count; i++)
            {
                filterComplex.Append($"[a{i}]");
            }
            filterComplex.Append($"amix=inputs={audioInputs.Count}:duration=longest:dropout_transition=3[a]\" ");
            args.Append(filterComplex);
            args.Append("-map 0:v -map \"[a]\" ");
        }
        else if (audioInputs.Count == 1)
        {
            args.Append("-map 0:v -map 1:a ");
            args.Append("-af aresample=48000 ");
        }
        else
        {
            args.Append("-map 0:v ");
        }

        // Duración tope del fragmento: SIN esto, el bucle de lectura de FFmpeg puede
        // quedarse consumiendo un input continuo (p. ej. el audio por pipe:0) y NUNCA leer
        // el video -> fragmento vacío (verificado en prueba real). Con -t ambos inputs
        // fluyen y, además, el fragmento finaliza limpio (flush) sin depender del kill.
        var durationSec = Math.Clamp(settings.FragmentDurationMinutes, 1, 120) * 60;
        args.Append($"-t {durationSec} ");

        // Codec de video según el encoder sondeado.
        if (pipeline.Encoder == EncoderKind.Qsv)
        {
            // QSV codifica directamente a nv12 (no forzar yuv420p: rompería la conversión).
            args.Append("-c:v h264_qsv -preset veryfast -global_quality 28 ");
        }
        else if (pipeline.Encoder == EncoderKind.Nvenc)
        {
            // NVENC (NVIDIA): aceleración por hardware ultra-baja CPU (<1.5%).
            args.Append("-c:v h264_nvenc -preset p1 -cq 28 -pix_fmt yuv420p ");
        }
        else if (pipeline.Encoder == EncoderKind.Amf)
        {
            // AMF (AMD): aceleración por hardware ultra-baja CPU (<1.5%).
            args.Append("-c:v h264_amf -quality speed -rc cqp -qp_i 28 -qp_p 28 -pix_fmt yuv420p ");
        }
        else
        {
            // ultrafast: ~la mitad de CPU que veryfast a cambio de más bitrate (única opción
            // razonable cuando no hay aceleración por GPU en equipos modestos, p. ej. i5 de 4 núcleos).
            args.Append("-c:v libx264 -preset ultrafast -crf 23 -threads 2 -pix_fmt yuv420p ");
        }

        // MP4 fragmentado: reproducible incluso si FFmpeg es terminado abruptamente.
        args.Append($"-c:a aac -b:a 128k ");
        args.Append($"-movflags frag_keyframe+empty_moov ");
        args.Append($"\"{outputPath}\"");

        return args.ToString();
    }

    private string GenerateFileName(RecordingSettings settings, int fragmentNumber)
    {
        var dateTime = DateTime.Now.ToString("dd-MM-yy-HH-mm-ss");
        var fileName = string.Empty;

        if (settings.UseCustomFilename && !string.IsNullOrEmpty(settings.CustomFilenamePrefix))
        {
            fileName = $"{settings.CustomFilenamePrefix}-{dateTime}";
        }
        else if (settings.UseUsernameAndMachineNameInFilename)
        {
            var username = Environment.UserName;
            var machineName = Environment.MachineName;
            fileName = $"{username}-{machineName}-{dateTime}";
        }
        else if (settings.UseUsernameInFilename)
        {
            var username = Environment.UserName;
            fileName = $"{username}-{dateTime}";
        }
        else if (settings.UseMachineNameInFilename)
        {
            var machineName = Environment.MachineName;
            fileName = $"{machineName}-{dateTime}";
        }
        else
        {
            fileName = $"Recording-{dateTime}";
        }

        if (fragmentNumber > 0)
        {
            fileName += $"-Fragment{fragmentNumber}";
        }

        return $"{fileName}.mp4";
    }
}
