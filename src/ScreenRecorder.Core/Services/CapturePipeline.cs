using ScreenRecorder.Core.Models;

namespace ScreenRecorder.Core.Services;

/// <summary>Backend de captura de pantalla elegido por el sondeo.</summary>
public enum CaptureBackend
{
    /// <summary>Desktop Duplication API (GPU). Solo un monitor por instancia (output_idx).</summary>
    Dda,
    /// <summary>GDI BitBlt. Captura la región indicada del escritorio virtual (puede cubrir varios monitores).</summary>
    Gdi
}

/// <summary>Codificador de video elegido por el sondeo.</summary>
public enum EncoderKind
{
    /// <summary>Intel Quick Sync (GPU, casi sin CPU). Si falla, libx264.</summary>
    Qsv,
    /// <summary>libx264 por software (CPU).</summary>
    X264
}

/// <summary>
/// Describe cómo se graba en esta máquina: backend, codificador, geometría y fps.
/// Se elige una vez por máquina en <see cref="CapturePipelineProbe"/> y se cachea.
/// </summary>
public record CapturePipeline(
    CaptureBackend Backend,
    int OutputIndex,
    EncoderKind Encoder,
    int Fps,
    CaptureRegion Region,
    ScaleSize Scale,
    bool DupFrames)
{
    /// <summary>Etiqueta corta para el log de arranque.</summary>
    public string Describe() =>
        $"captura={(Backend == CaptureBackend.Dda ? $"ddagrab(idx={OutputIndex})" : "gdigrab")} " +
        $"enc={(Encoder == EncoderKind.Qsv ? "h264_qsv" : "libx264")} " +
        $"{Region.Size}->{Scale.Width}x{Scale.Height}@{Fps}" +
        (DupFrames ? "" : " dup_frames=0");
}
