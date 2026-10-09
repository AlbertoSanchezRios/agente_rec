using System;
using System.Threading.Tasks;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Servicio de grabación de pantalla
/// </summary>
public interface IRecordingService
{
    bool IsRecording { get; }
    Task StartRecordingAsync();
    Task StopRecordingAsync();

    /// <summary>
    /// Cierra FFmpeg de inmediato (SUSPENSIÓN de Windows): envía 'q', renombra el fragmento a su
    /// nombre final y NO intenta la subida SMB (no cabe en los ~5 s que da Windows). El fragmento
    /// queda íntegro en temporal y la subida la hace la recuperación de huérfanos al reanudar.
    /// Para el APAGADO no usar esto: usar NotifySystemEnding() para grabar hasta el final.
    /// </summary>
    void RequestFastShutdown();

    /// <summary>
    /// Aviso de apagado/cierre de sesión de Windows (SessionEnding/SessionEnded). NO detiene la
    /// grabación: sigue hasta que Windows mate a FFmpeg, y el fragmento final se recupera al
    /// rearrancar. Idempotente.
    /// </summary>
    void NotifySystemEnding();

    event EventHandler<string>? RecordingCompleted;
}
