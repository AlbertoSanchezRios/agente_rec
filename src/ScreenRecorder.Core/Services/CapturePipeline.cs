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
    /// <summary>Intel Quick Sync (GPU, casi sin CPU).</summary>
    Qsv,
    /// <summary>NVIDIA NVENC (GPU, casi sin CPU).</summary>
    Nvenc,
    /// <summary>AMD Advanced Media Framework (GPU, casi sin CPU).</summary>
    Amf,
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
        $"enc={EncoderName(Encoder)} " +
        $"{Region.Size}->{Scale.Width}x{Scale.Height}@{Fps}" +
        (DupFrames ? "" : " dup_frames=0");

    private static string EncoderName(EncoderKind enc) => enc switch
    {
        EncoderKind.Qsv => "h264_qsv",
        EncoderKind.Nvenc => "h264_nvenc",
        EncoderKind.Amf => "h264_amf",
        _ => "libx264"
    };
}
