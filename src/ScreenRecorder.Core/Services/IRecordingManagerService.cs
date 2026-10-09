namespace ScreenRecorder.Core.Services;

/// <summary>
/// Servicio principal que coordina la grabación
/// </summary>
public interface IRecordingManagerService
{
    bool IsRecordingActive { get; }
    void Start();
    void Stop();
}
