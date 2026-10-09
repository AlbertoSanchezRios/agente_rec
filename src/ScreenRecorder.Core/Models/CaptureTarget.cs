namespace ScreenRecorder.Core.Models;

/// <summary>
/// Qué parte del escritorio se graba.
/// Los monitores se numeran por su índice DXGI (usado por ddagrab),
/// validado en runtime contra IScreenGeometryService.
/// </summary>
public enum CaptureTarget
{
    /// <summary>Solo el monitor primario (output_idx=0).</summary>
    Primary,
    /// <summary>Solo el monitor secundario (output_idx=1).</summary>
    Secondary,
    /// <summary>El escritorio virtual completo (todos los monitores). Captura por gdigrab.</summary>
    All
}
