using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Sondea una vez por máquina el mejor pipeline de captura+codificación y lo cachea en disco.
/// Las PC corporativas son heterogéneas (Intel Quick Sync, NVIDIA, sin GPU dedicada, escritorio
/// bloqueado o por RDP), por eso se prueban varios candidatos y se elige el primero que funciona.
/// </summary>
public interface ICapturePipelineProbe
{
    /// <summary>Devuelve el pipeline efectivo (cacheado o sondeado ahora).</summary>
    Task<CapturePipeline> ResolveAsync(RecordingSettings settings);

    /// <summary>Invalida la caché (p. ej. tras un cambio de hardware o de ajustes).</summary>
    void InvalidateCache();
}

public class CapturePipelineProbe : ICapturePipelineProbe
{
    private readonly ILoggerService _logger;
    private readonly IScreenGeometryService _geometry;
    private readonly string _ffmpegPath;
    private readonly string _cachePath;
    private CapturePipeline? _cached;

    public CapturePipelineProbe(ILoggerService logger, IScreenGeometryService geometry)
    {
        _logger = logger;
        _geometry = geometry;
        _ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.AppConstants.FFmpegExeName);
        _cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ScreenRecorder", "pipeline.txt");
    }

    public async Task<CapturePipeline> ResolveAsync(RecordingSettings settings)
    {
        var fps = Math.Clamp(settings.Fps, 10, 30);
        var dupFrames = !settings.RecordOnlyOnChanges;
        var region = _geometry.GetCaptureRegion(settings.CaptureTarget);
        var scale = _geometry.GetScale(settings.CaptureTarget, settings.Resolution);

        // La caché se valida SOLO por la región de captura (backend/encoder/índice no dependen
        // de fps/resolución). Si el usuario cambia fps, resolución o dup_frames, se reutiliza el
        // pipeline sondeado actualizando esos campos: evita re-sondear (30-45 s) por cada ajuste.
        // Con modo forzado (GPU/GDI) se ignora la caché: el backend elegido debe respetarse.
        var pref = settings.CaptureBackendPreference;
        var cached = pref == 0 ? TryLoadFromCache() : null;
        if (cached != null
            && cached.Region.X == region.X
            && cached.Region.Y == region.Y
            && cached.Region.Width == region.Width
            && cached.Region.Height == region.Height)
        {
            var adapted = cached with { Fps = fps, DupFrames = dupFrames, Scale = scale };
            _logger.LogInfo($"Pipeline desde caché: {adapted.Describe()}");
            SaveToCache(adapted);
            return adapted;
        }

        var outputIndex = ResolveOutputIndex(settings.CaptureTarget, region);

        // Modo forzado GDI (elige el usuario cuando el cursor parpadea con la captura GPU).
        if (pref == 2)
        {
            _logger.LogInfo("Modo de captura: GDI forzado (sin parpadeo del cursor).");
            var gdiForced = await TryGdiNvencAsync(region, scale, fps)
                ?? await TryGdiQsvAsync(region, scale, fps)
                ?? await TryGdiAmfAsync(region, scale, fps)
                ?? await TryGdiX264Async(settings.CaptureTarget, region, scale, fps);
            var chosenGdi = gdiForced ?? new CapturePipeline(CaptureBackend.Gdi, 0, EncoderKind.X264, fps, region, scale, dupFrames);
            _logger.LogInfo($"Pipeline elegido: {chosenGdi.Describe()}");
            SaveToCache(chosenGdi);
            return chosenGdi;
        }

        // "Ambos monitores" (All) con DDA NO funciona: ddagrab captura UN solo output
        // (output_idx) e ignora la región. Para All se usa GDI, que sí captura la región
        // virtual completa con offsets físicos (varios monitores de verdad).
        if (settings.CaptureTarget == CaptureTarget.All)
        {
            _logger.LogInfo($"Target=All (varios monitores): se usa GDI para capturar toda la región {region.Size}.");
            var gdi = await TryGdiNvencAsync(region, scale, fps)
                ?? await TryGdiQsvAsync(region, scale, fps)
                ?? await TryGdiAmfAsync(region, scale, fps)
                ?? await TryGdiX264Async(settings.CaptureTarget, region, scale, fps);
            var chosenAll = gdi ?? new CapturePipeline(CaptureBackend.Gdi, 0, EncoderKind.X264, fps, region, scale, dupFrames);
            _logger.LogInfo($"Pipeline elegido: {chosenAll.Describe()}");
            SaveToCache(chosenAll);
            return chosenAll;
        }

        _logger.LogInfo($"Sondeando pipelines de captura (region={region.Size}, idx={outputIndex}, fps={fps}, modo={(pref == 1 ? "GPU" : "Auto")})...");

        // Modo GPU forzado: solo sondeos DDA (caer a GDI si la GPU no sirve, mejor que no grabar).
        var chosen = pref == 1
            ? await TryDdaQsvAsync(region, scale, outputIndex, fps, dupFrames)
                ?? await TryDdaNvencAsync(region, scale, outputIndex, fps, dupFrames)
                ?? await TryDdaAmfAsync(region, scale, outputIndex, fps, dupFrames)
                ?? await TryDdaX264Async(region, scale, outputIndex, fps, dupFrames)
                ?? await TryGdiNvencAsync(region, scale, fps)
                ?? await TryGdiQsvAsync(region, scale, fps)
                ?? await TryGdiAmfAsync(region, scale, fps)
                ?? await TryGdiX264Async(settings.CaptureTarget, region, scale, fps)
            : await TryDdaQsvAsync(region, scale, outputIndex, fps, dupFrames)
                ?? await TryDdaNvencAsync(region, scale, outputIndex, fps, dupFrames)
                ?? await TryDdaAmfAsync(region, scale, outputIndex, fps, dupFrames)
                ?? await TryDdaX264Async(region, scale, outputIndex, fps, dupFrames)
                ?? await TryGdiNvencAsync(region, scale, fps)
                ?? await TryGdiQsvAsync(region, scale, fps)
                ?? await TryGdiAmfAsync(region, scale, fps)
                ?? await TryGdiX264Async(settings.CaptureTarget, region, scale, fps);

        if (chosen == null)
        {
            _logger.LogWarning("Ningún pipeline sondeó bien; se usa GDI+libx264 como último recurso");
            chosen = new CapturePipeline(
                CaptureBackend.Gdi, 0, EncoderKind.X264, fps, region, scale, dupFrames);
        }

        _logger.LogInfo($"Pipeline elegido: {chosen.Describe()}");
        SaveToCache(chosen);
        return chosen;
    }

    public void InvalidateCache()
    {
        _cached = null;
        try { if (File.Exists(_cachePath)) File.Delete(_cachePath); } catch { /* nunca romper */ }
    }

    /// <summary>
    /// ddagrab indexa por DXGI output_idx, cuyo orden puede NO coincidir con la enumeración
    /// de Screen.AllScreens en equipos multi-GPU. Validamos en runtime capturando 1 frame de
    /// cada índice candidato y comparando con el tamaño del monitor que queremos.
    /// </summary>
    private int ResolveOutputIndex(CaptureTarget target, CaptureRegion region)
    {
        if (target == CaptureTarget.All)
            return 0; // gdigrab no usa idx

        var monitors = _geometry.GetMonitors();
        if (monitors.Count <= 1)
            return 0;

        var want = target == CaptureTarget.Primary
            ? monitors.First(m => m.IsPrimary)
            : monitors.FirstOrDefault(m => !m.IsPrimary) ?? monitors.First(m => m.IsPrimary);

        // Candidatos: el primario es típicamente idx 0, el siguiente idx 1…
        // ddagrab reporta el tamaño NATIVO (píxeles físicos); se compara contra PhysicalBounds
        // (antes se comparaba con Bounds lógicos: con cualquier escalado DPI nunca coincidía y
        // se caía siempre al índice 0, pudiendo grabar el monitor equivocado).
        for (int idx = 0; idx < Math.Min(monitors.Count, 4); idx++)
        {
            var size = ProbeOutputSize(idx);
            if (size == null) continue;
            if (size.Value.Width == want.PhysicalBounds.Width && size.Value.Height == want.PhysicalBounds.Height)
            {
                if (idx != (want.IsPrimary ? 0 : 1))
                {
                    _logger.LogWarning(
                        $"output_idx={idx} coincide con el monitor pedido pero NO es el índice esperado " +
                        $"({(want.IsPrimary ? 0 : 1)}); esto indica orden DXGI distinto a la enumeración. Se usa idx={idx}.");
                }
                return idx;
            }
        }

        _logger.LogWarning($"No se pudo validar output_idx para {target}; se usa el índice por defecto 0");
        return 0;
    }

    private (int Width, int Height)? ProbeOutputSize(int outputIndex)
    {
        try
        {
            var args = $"-hide_banner -loglevel error -f lavfi -i \"ddagrab=output_idx={outputIndex}:framerate=1\" -frames:v 1 -an -f null -";
            var (exitCode, stderr) = RunFfmpeg(args, TimeSpan.FromSeconds(5));
            if (exitCode != 0) return null;

            // Busca "1920x1080" en la salida de ffmpeg.
            foreach (var line in stderr.Split('\n'))
            {
                var i = line.IndexOf("Stream #0:0", StringComparison.Ordinal);
                if (i < 0) continue;
                var x = line.IndexOf('x', i);
                if (x < 0) continue;
                var wEnd = line.LastIndexOf(' ', x) + 1;
                var wStr = line.Substring(wEnd, x - wEnd);
                var hStart = x + 1;
                var hEnd = hStart;
                while (hEnd < line.Length && char.IsDigit(line[hEnd])) hEnd++;
                if (int.TryParse(wStr, out var w) && int.TryParse(line.Substring(hStart, hEnd - hStart), out var h))
                {
                    return (w, h);
                }
            }
        }
        catch { /* ignorar: seguimos con el siguiente índice */ }
        return null;
    }

    private async Task<CapturePipeline?> TryDdaQsvAsync(CaptureRegion region, ScaleSize scale, int outputIndex, int fps, bool dupFrames)
    {
        var dup = dupFrames ? "" : ":dup_frames=0";
        var filter = $"ddagrab=output_idx={outputIndex}:framerate={fps}{dup}:draw_mouse=0,hwdownload,format=bgra,scale={scale.Width}:{scale.Height}";
        // NO añadir -pix_fmt yuv420p con QSV: fuerza una conversión de pix_fmt que impide
        // abrir el encoder (verificado en sondeo real). QSV codifica a nv12 por defecto.
        var args = $"-hide_banner -loglevel error -f lavfi -i \"{filter}\" -frames:v 12 -an -c:v h264_qsv -preset veryfast -global_quality 28 -f null -";
        var ok = await ProbeAsync(args, "ddagrab+h264_qsv");
        return ok ? new CapturePipeline(CaptureBackend.Dda, outputIndex, EncoderKind.Qsv, fps, region, scale, dupFrames) : null;
    }

    private async Task<CapturePipeline?> TryDdaNvencAsync(CaptureRegion region, ScaleSize scale, int outputIndex, int fps, bool dupFrames)
    {
        var dup = dupFrames ? "" : ":dup_frames=0";
        var filter = $"ddagrab=output_idx={outputIndex}:framerate={fps}{dup}:draw_mouse=0,hwdownload,format=bgra,scale={scale.Width}:{scale.Height}";
        var args = $"-hide_banner -loglevel error -f lavfi -i \"{filter}\" -frames:v 12 -an -c:v h264_nvenc -preset p1 -cq 28 -pix_fmt yuv420p -f null -";
        var ok = await ProbeAsync(args, "ddagrab+h264_nvenc");
        return ok ? new CapturePipeline(CaptureBackend.Dda, outputIndex, EncoderKind.Nvenc, fps, region, scale, dupFrames) : null;
    }

    private async Task<CapturePipeline?> TryDdaAmfAsync(CaptureRegion region, ScaleSize scale, int outputIndex, int fps, bool dupFrames)
    {
        var dup = dupFrames ? "" : ":dup_frames=0";
        var filter = $"ddagrab=output_idx={outputIndex}:framerate={fps}{dup}:draw_mouse=0,hwdownload,format=bgra,scale={scale.Width}:{scale.Height}";
        var args = $"-hide_banner -loglevel error -f lavfi -i \"{filter}\" -frames:v 12 -an -c:v h264_amf -quality speed -rc cqp -qp_i 28 -qp_p 28 -pix_fmt yuv420p -f null -";
        var ok = await ProbeAsync(args, "ddagrab+h264_amf");
        return ok ? new CapturePipeline(CaptureBackend.Dda, outputIndex, EncoderKind.Amf, fps, region, scale, dupFrames) : null;
    }

    private async Task<CapturePipeline?> TryDdaX264Async(CaptureRegion region, ScaleSize scale, int outputIndex, int fps, bool dupFrames)
    {
        var dup = dupFrames ? "" : ":dup_frames=0";
        var filter = $"ddagrab=output_idx={outputIndex}:framerate={fps}{dup}:draw_mouse=0,hwdownload,format=bgra,scale={scale.Width}:{scale.Height}";
        var args = $"-hide_banner -loglevel error -f lavfi -i \"{filter}\" -frames:v 12 -an -c:v libx264 -preset veryfast -crf 23 -threads 2 -pix_fmt yuv420p -f null -";
        var ok = await ProbeAsync(args, "ddagrab+libx264");
        return ok ? new CapturePipeline(CaptureBackend.Dda, outputIndex, EncoderKind.X264, fps, region, scale, dupFrames) : null;
    }

    private async Task<CapturePipeline?> TryGdiNvencAsync(CaptureRegion region, ScaleSize scale, int fps)
    {
        var input = $"-f gdigrab -framerate {fps} -offset_x {region.X} -offset_y {region.Y} -video_size {region.Width}x{region.Height} -i desktop";
        var vf = scale.Width == region.Width && scale.Height == region.Height
            ? ""
            : $" -vf scale={scale.Width}:{scale.Height}";
        var args = $"-hide_banner -loglevel error {input} -frames:v 12 -an{vf} -c:v h264_nvenc -preset p1 -cq 28 -pix_fmt yuv420p -f null -";
        var ok = await ProbeAsync(args, "gdigrab+h264_nvenc");
        return ok ? new CapturePipeline(CaptureBackend.Gdi, 0, EncoderKind.Nvenc, fps, region, scale, DupFrames: true) : null;
    }

    private async Task<CapturePipeline?> TryGdiQsvAsync(CaptureRegion region, ScaleSize scale, int fps)
    {
        var input = $"-f gdigrab -framerate {fps} -offset_x {region.X} -offset_y {region.Y} -video_size {region.Width}x{region.Height} -i desktop";
        var vf = scale.Width == region.Width && scale.Height == region.Height
            ? ""
            : $" -vf scale={scale.Width}:{scale.Height}";
        var args = $"-hide_banner -loglevel error {input} -frames:v 12 -an{vf} -c:v h264_qsv -preset veryfast -global_quality 28 -f null -";
        var ok = await ProbeAsync(args, "gdigrab+h264_qsv");
        return ok ? new CapturePipeline(CaptureBackend.Gdi, 0, EncoderKind.Qsv, fps, region, scale, DupFrames: true) : null;
    }

    private async Task<CapturePipeline?> TryGdiAmfAsync(CaptureRegion region, ScaleSize scale, int fps)
    {
        var input = $"-f gdigrab -framerate {fps} -offset_x {region.X} -offset_y {region.Y} -video_size {region.Width}x{region.Height} -i desktop";
        var vf = scale.Width == region.Width && scale.Height == region.Height
            ? ""
            : $" -vf scale={scale.Width}:{scale.Height}";
        var args = $"-hide_banner -loglevel error {input} -frames:v 12 -an{vf} -c:v h264_amf -quality speed -rc cqp -qp_i 28 -qp_p 28 -pix_fmt yuv420p -f null -";
        var ok = await ProbeAsync(args, "gdigrab+h264_amf");
        return ok ? new CapturePipeline(CaptureBackend.Gdi, 0, EncoderKind.Amf, fps, region, scale, DupFrames: true) : null;
    }

    private async Task<CapturePipeline?> TryGdiX264Async(CaptureTarget target, CaptureRegion region, ScaleSize scale, int fps)
    {
        // gdigrab no tiene dup_frames; la región ya incluye offset/tamaño.
        var input = $"-f gdigrab -framerate {fps} -offset_x {region.X} -offset_y {region.Y} -video_size {region.Width}x{region.Height} -i desktop";
        var vf = scale.Width == region.Width && scale.Height == region.Height
            ? ""
            : $" -vf scale={scale.Width}:{scale.Height}";
        var args = $"-hide_banner -loglevel error {input} -frames:v 12 -an{vf} -c:v libx264 -preset veryfast -crf 23 -threads 2 -pix_fmt yuv420p -f null -";
        var ok = await ProbeAsync(args, "gdigrab+libx264");
        return ok ? new CapturePipeline(CaptureBackend.Gdi, 0, EncoderKind.X264, fps, region, scale, DupFrames: true) : null;
    }

    private async Task<bool> ProbeAsync(string args, string label)
    {
        try
        {
            var (exitCode, stderr) = await Task.Run(() => RunFfmpeg(args, TimeSpan.FromSeconds(5)));
            if (exitCode == 0)
            {
                _logger.LogInfo($"  ✓ {label}");
                return true;
            }
            _logger.LogInfo($"  ✗ {label} (código {exitCode}): {FirstLine(stderr)}");
        }
        catch (Exception ex)
        {
            _logger.LogInfo($"  ✗ {label}: {ex.Message}");
        }
        return false;
    }

    private (int ExitCode, string Stderr) RunFfmpeg(string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stderr = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginErrorReadLine();
        // Consumir stdout para evitar bloqueo por el buffer.
        process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.StandardInput.WriteLine("q"); } catch { }
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); process.WaitForExit(2000); } catch { }
            }
        }
        // Tras Kill() el ExitCode puede no estar disponible; -1 señala "no se pudo determinar".
        int exitCode;
        try { exitCode = process.ExitCode; } catch { exitCode = -1; }
        return (exitCode, stderr.ToString());
    }

    private static string FirstLine(string s) =>
        s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    private CapturePipeline? TryLoadFromCache()
    {
        if (_cached != null) return _cached;
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var parts = File.ReadAllText(_cachePath).Split('|');
            // Formato v2: marca 'v2' final. Las cachés de formatos anteriores (p. ej. sin
            // draw_mouse=0) se ignoran y la pipeline se vuelve a sondear.
            if (parts.Length < 8 || parts[7] != "v2") return null;
            var backend = Enum.Parse<CaptureBackend>(parts[0]);
            var idx = int.Parse(parts[1]);
            var enc = Enum.Parse<EncoderKind>(parts[2]);
            var fps = int.Parse(parts[3]);
            var regionParts = parts[4].Split('x');
            // Formato nuevo: XxYxWxH (offsets incluidos). Formato antiguo: WxH (offsets 0,0).
            var region = regionParts.Length == 4
                ? new CaptureRegion(int.Parse(regionParts[0]), int.Parse(regionParts[1]), int.Parse(regionParts[2]), int.Parse(regionParts[3]))
                : new CaptureRegion(0, 0, int.Parse(regionParts[0]), int.Parse(regionParts[1]));
            var scale = parts[5].Split('x');
            var dup = bool.Parse(parts[6]);
            var scaleSize = new ScaleSize(int.Parse(scale[0]), int.Parse(scale[1]));
            _cached = new CapturePipeline(backend, idx, enc, fps, region, scaleSize, dup);
            return _cached;
        }
        catch
        {
            return null;
        }
    }

    private void SaveToCache(CapturePipeline p)
    {
        try
        {
            _cached = p;
            var dir = Path.GetDirectoryName(_cachePath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(_cachePath,
                $"{p.Backend}|{p.OutputIndex}|{p.Encoder}|{p.Fps}|{p.Region.X}x{p.Region.Y}x{p.Region.Width}x{p.Region.Height}|{p.Scale.Width}x{p.Scale.Height}|{p.DupFrames}|v2");
        }
        catch { /* la caché es opcional */ }
    }
}
