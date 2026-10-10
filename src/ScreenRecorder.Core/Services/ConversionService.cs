using System;
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

        await Gate.WaitAsync(cancellationToken);
        try
        {
            var mp4Path = Path.ChangeExtension(mkvPath, ".mp4");
            // El temporal DEBE terminar en .mp4: FFmpeg infiere el muxer por la extensión y, con
            // '.tmp', abortaba con "Unable to choose an output format". Además se pasa -f mp4
            // explícito para no depender de la extensión.
            var tempMp4 = mp4Path + "." + Guid.NewGuid().ToString("N") + ".tmp.mp4";

            var encoderArgs = RemuxOnly ? "-c:v copy" : await GetEncoderArgsAsync(cancellationToken);
            // -err_detect ignore_err + -fflags +genpts: reparan fragmentos truncados (decodifica
            // hasta el primer error y regenera timestamps). -movflags +faststart deja el índice
            // al principio. -f mp4 fuerza el muxer (no depende de la extensión).
            var args = BuildConversionArgs(mkvPath, tempMp4, encoderArgs, RemuxOnly);

            _logger.LogInfo($"Convirtiendo {Path.GetFileName(mkvPath)} a MP4 ({(RemuxOnly ? "remux" : "re-encode")})...");
            var (exit, stderr) = RunProcess(args, TimeSpan.FromMinutes(60));

            if (exit != 0 || !MediaValidator.IsValidMp4(tempMp4))
            {
                _logger.LogError($"Conversión falló (código {exit}): {FirstLine(stderr)}");
                TryDelete(tempMp4);
                return null;
            }

            if (!VerifyPlayable(tempMp4))
            {
                _logger.LogError($"El MP4 convertido NO es reproducible; se descarta: {Path.GetFileName(tempMp4)}");
                TryDelete(tempMp4);
                return null;
            }

            File.Move(tempMp4, mp4Path, overwrite: true);
            _logger.LogInfo($"Conversión verificada OK: {Path.GetFileName(mp4Path)} ({new FileInfo(mp4Path).Length} bytes)");
            return mp4Path;
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
    private bool VerifyPlayable(string path)
    {
        var (exit, _) = RunProcess($"-v error -xerror -i \"{path}\" -f null -", TimeSpan.FromMinutes(30));
        return exit == 0;
    }

    private Task<string> GetEncoderArgsAsync(CancellationToken cancellationToken)
    {
        if (_cachedEncoderArgs != null) return Task.FromResult(_cachedEncoderArgs);
        return Task.Run(() =>
        {
            // `-encoders` escribe la lista en STDOUT (RunProcess captura ambos flujos).
            var (_, text) = RunProcess("-hide_banner -encoders", TimeSpan.FromSeconds(15));
            _cachedEncoderArgs = SelectEncoderArgs(text);
            _logger.LogInfo($"Encoder de conversión elegido: {_cachedEncoderArgs}");
            return _cachedEncoderArgs;
        }, cancellationToken);
    }

    /// <summary>
    /// Elige los argumentos del encoder de conversión a partir de la salida de `ffmpeg -encoders`.
    /// Función pura (testeable): prefiere hardware (QSV &gt; NVENC &gt; AMF) y cae a libx264.
    /// </summary>
    public static string SelectEncoderArgs(string encodersText)
    {
        if (encodersText.Contains("h264_qsv", StringComparison.Ordinal))
            return "-c:v h264_qsv -preset veryfast -global_quality 26";
        if (encodersText.Contains("h264_nvenc", StringComparison.Ordinal))
            return "-c:v h264_nvenc -preset p1 -cq 28 -pix_fmt yuv420p";
        if (encodersText.Contains("h264_amf", StringComparison.Ordinal))
            return "-c:v h264_amf -quality speed -rc cqp -qp_i 28 -qp_p 28 -pix_fmt yuv420p";
        return "-c:v libx264 -preset ultrafast -crf 23 -threads 1 -pix_fmt yuv420p";
    }

    /// <summary>Construye los argumentos de conversión MKV→MP4 (función pura, testeable).</summary>
    public static string BuildConversionArgs(string mkvPath, string tempMp4, string encoderArgs, bool remuxOnly)
    {
        var enc = remuxOnly ? "-c:v copy" : encoderArgs;
        return $"-y -hide_banner -loglevel error -err_detect ignore_err -fflags +genpts " +
               $"-i \"{mkvPath}\" {enc} -c:a aac -b:a 128k -movflags +faststart -f mp4 \"{tempMp4}\"";
    }

    private (int ExitCode, string Output) RunProcess(string args, TimeSpan timeout)
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
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* no crítico */ }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(); process.WaitForExit(3000); } catch { }
                return (-1, "tiempo de conversión agotado");
            }
            // Segunda espera (sin timeout) para vaciar los manejadores asíncronos de salida.
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
