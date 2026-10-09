using System.Collections.Generic;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Servicio de detección de dispositivos de audio
/// </summary>
public interface IAudioDetectionService
{
    List<AudioDevice> DetectSystemAudioDevices();
    List<AudioDevice> DetectMicrophoneDevices();
    AudioDevice? GetPreferredSystemAudioDevice();
    AudioDevice? GetPreferredMicrophoneDevice();
}
