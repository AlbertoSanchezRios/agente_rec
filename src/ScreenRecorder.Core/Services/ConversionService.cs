using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ScreenRecorder.Core.Constants;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Convierte fragmentos MKV (contenedor robusto durante la grabación) a MP4 mediante un
/// subproceso FFmpeg dedicado (WCodecHost.exe), con verificación de que el MP4 resultante sea
/// reproducible. La conversión re-codifica con el encoder de hardware disponible (QSV/NVENC/AMF)
/// a baja prioridad; si no hay hardware, usa libx264 ultrafast. Los fragmentos limpios que el
/// operador prefiera remuxar pueden activarse con <see cref="RemuxOnly"/> (sin pérdida y casi sin
/// CPU), pero el modo por defecto es re-encode para reparar fragmentos truncados.
/// </summary>
public sealed class ConversionService
{
    private readonly ILoggerService _logger;
    private readonly string _codecPath;

    // Serializa las conversiones dentro del proceso (una a la vez) para no competir con la captura.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Encoder detectado una vez por proceso. null = sin detectar todavía.
    private static string? _cachedEncoderArgs;

    /// <summary>Si es true, se hace remux (-c copy) en vez de re-encode. Por defecto false.</summary>
    public bool RemuxOnly { get; set; }

    public ConversionService(ILoggerService logger)
    {
        _logger = logger;
        _codecPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppConstants.CodecExeName);
    }

    public bool IsAvailable => File.Exists(_codecPath);

    /// <summary>
    /// Convierte un MKV a MP4 verificado. Devuelve la ruta del MP4, o null si falla (en cuyo caso
    /// el MKV se conserva para reintentar). No lanza.
    /// </summary>
    public async Task<string?> ConvertMkvToMp4Async(string mkvPath, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            _logger.LogWarning($"No se encontró {AppConstants.CodecExeName}; no se puede convertir {Path.GetFileName(mkvPath)}.");
            return null;
        }
        if (!MediaValidator.IsValidMkv(mkvPath))
        {
            _logger.LogError($"MKV inválido, no se convierte: {mkvPath} (cabecera {MediaValidator.HeaderHex(mkvPath)}).");
            return null;
        }

        try
        {
            await Gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInfo($"Conversión cancelada antes de iniciar: {Path.GetFileName(mkvPath)}");
            return null;
        }

        try
        {
            var mp4Path = Path.ChangeExtension(mkvPath, ".mp4");
            // El temporal DEBE terminar en .mp4: FFmpeg infiere el muxer por la extensión y, con
            // '.tmp', abortaba con "Unable to choose an output format". Además se pasa -f mp4
            // explícito para no depender de la extensión.
            var tempMp4 = mp4Path + "." + Guid.NewGuid().ToString("N") + ".tmp.mp4";

            var encoderArgs = RemuxOnly ? "-c:v copy" : await GetEncoderArgsAsync(mkvPath, cancellationToken);
            // -err_detect ignore_err + -fflags +genpts: reparan fragmentos truncados (decodifica
            // hasta el primer error y regenera timestamps). -movflags +faststart deja el índice
            // al principio. -f mp4 fuerza el muxer (no depende de la extensión).
            var args = BuildConversionArgs(mkvPath, tempMp4, encoderArgs, RemuxOnly);

            _logger.LogInfo($"Convirtiendo {Path.GetFileName(mkvPath)} a MP4 ({(RemuxOnly ? "remux" : "re-encode")})...");
            var (exit, stderr) = await RunProcessAsync(args, TimeSpan.FromMinutes(60), cancellationToken);

            if (exit != 0 || !MediaValidator.IsValidMp4(tempMp4))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInfo($"Conversión cancelada: {Path.GetFileName(mkvPath)}");
                }
                else
                {
                    _logger.LogError($"Conversión falló (código {exit}): {FirstLine(stderr)}");
                }
                TryDelete(tempMp4);
                return null;
            }

            if (!await VerifyPlayableAsync(tempMp4, cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInfo($"Verificación cancelada: {Path.GetFileName(tempMp4)}");
                }
                else
                {
                    _logger.LogError($"El MP4 convertido NO es reproducible; se descarta: {Path.GetFileName(tempMp4)}");
                }
                TryDelete(tempMp4);
                return null;
            }

            File.Move(tempMp4, mp4Path, overwrite: true);
            _logger.LogInfo($"Conversión verificada OK: {Path.GetFileName(mp4Path)} ({new FileInfo(mp4Path).Length} bytes)");
            return mp4Path;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInfo($"Conversión cancelada: {Path.GetFileName(mkvPath)}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error al convertir {Path.GetFileName(mkvPath)}", ex);
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Comprueba que el MP4 se pueda decodificar de principio a fin (pasada de decode).</summary>
    private async Task<bool> VerifyPlayableAsync(string path, CancellationToken cancellationToken)
    {
        var (exit, _) = await RunProcessAsync($"-nostdin -v error -xerror -i \"{path}\" -f null -", TimeSpan.FromMinutes(30), cancellationToken);
        return exit == 0;
    }

    private async Task<string> GetEncoderArgsAsync(string sampleInput, CancellationToken cancellationToken)
    {
        if (_cachedEncoderArgs != null) return _cachedEncoderArgs;
        // `-encoders` escribe la lista en STDOUT (RunProcess captura ambos flujos).
        var (_, text) = await RunProcessAsync("-nostdin -hide_banner -encoders", TimeSpan.FromSeconds(15), cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return LibX264Args;
        }

        foreach (var candidate in CandidateEncoderArgs(text))
        {
            // libx264 (último de la lista) siempre funciona: no se sondea.
            if (candidate.Contains("libx264", StringComparison.Ordinal) || await ProbeEncoderAsync(candidate, sampleInput, cancellationToken))
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _cachedEncoderArgs = candidate;
                }
                break;
            }
            _logger.LogInfo($"Encoder de conversión no operativo en este equipo; probando el siguiente: {candidate}");
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            _cachedEncoderArgs ??= LibX264Args;
            _logger.LogInfo($"Encoder de conversión elegido: {_cachedEncoderArgs}");
            return _cachedEncoderArgs;
        }

        return LibX264Args;
    }

    /// <summary>
    /// Sondea que un encoder funcione REALMENTE en este equipo (los *_qsv/*_nvenc/*_amf
    /// aparecen en `-encoders` aunque no haya hardware compatible). Codifica 1 frame del propio
    /// fragmento de entrada y comprueba el código de salida.
    /// </summary>
    private async Task<bool> ProbeEncoderAsync(string encArgs, string sampleInput, CancellationToken cancellationToken)
    {
        var args = $"-nostdin -hide_banner -loglevel error -i \"{sampleInput}\" -map 0:v -frames:v 1 -an {encArgs} -f null -";
        var (exit, _) = await RunProcessAsync(args, TimeSpan.FromSeconds(30), cancellationToken);
        return exit == 0;
    }

    private const string LibX264Args = "-c:v libx264 -preset ultrafast -crf 23 -threads 1 -pix_fmt yuv420p";

    /// <summary>
    /// Orden de preferencia de encoders de conversión presentes en la salida de `ffmpeg -encoders`
    /// (hardware primero; libx264 siempre al final). Función pura, testeable.
    /// </summary>
    public static IReadOnlyList<string> CandidateEncoderArgs(string encodersText)
    {
        var list = new List<string>();
        if (encodersText.Contains("h264_qsv", StringComparison.Ordinal))
            list.Add("-c:v h264_qsv -preset veryfast -global_quality 26");
        if (encodersText.Contains("h264_nvenc", StringComparison.Ordinal))
            list.Add("-c:v h264_nvenc -preset p1 -cq 28 -pix_fmt yuv420p");
        if (encodersText.Contains("h264_amf", StringComparison.Ordinal))
            list.Add("-c:v h264_amf -quality speed -rc cqp -qp_i 28 -qp_p 28 -pix_fmt yuv420p");
        list.Add(LibX264Args);
        return list;
    }

    /// <summary>Primer encoder de la lista de preferencia (para pruebas/diagnóstico).</summary>
    public static string SelectEncoderArgs(string encodersText) => CandidateEncoderArgs(encodersText)[0];

    /// <summary>Construye los argumentos de conversión MKV→MP4 (función pura, testeable).</summary>
    public static string BuildConversionArgs(string mkvPath, string tempMp4, string encoderArgs, bool remuxOnly)
    {
        var enc = remuxOnly ? "-c:v copy" : encoderArgs;
        return $"-nostdin -y -hide_banner -loglevel error -err_detect ignore_err -fflags +genpts " +
               $"-i \"{mkvPath}\" {enc} -c:a aac -b:a 128k -movflags +faststart -f mp4 \"{tempMp4}\"";
    }

    private async Task<(int ExitCode, string Output)> RunProcessAsync(string args, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _codecPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            // Capturar AMBOS flujos: `-encoders` escribe en stdout y los errores en stderr.
            var output = new StringBuilder();
            var gate = new object();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) { output.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) { output.AppendLine(e.Data); } };
            process.Start();
            try { process.StandardInput.Close(); } catch { }
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* no crítico */ }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await process.WaitForExitAsync(killCts.Token);
                    }
                }
                catch { }

                if (cancellationToken.IsCancellationRequested)
                {
                    return (-1, "conversión cancelada");
                }
                return (-1, "tiempo de conversión agotado");
            }

            // Segunda espera para vaciar los manejadores asíncronos de salida (documentado por Microsoft).
            try { process.WaitForExit(); } catch { }

            int exit;
            try { exit = process.ExitCode; } catch { exit = -1; }
            return (exit, output.ToString());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string FirstLine(string s) =>
        s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* no crítico */ }
    }
}
