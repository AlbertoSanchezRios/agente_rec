using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenRecorder.Core.Services;

namespace ScreenRecorder.Core.Audio;

/// <summary>
/// Captura el audio del SISTEMA (loopback del dispositivo de reproducción por defecto) con
/// WASAPI desde C# — FFmpeg NO soporta WASAPI en ningún build, así que lo hacemos nosotros
/// y entregamos PCM s16le a FFmpeg por su STDIN:
///   -f s16le -ar R -ac C -i pipe:0
///
/// El loopback de WASAPI entrega muestras CONTINUAS (silencio cuando no suena nada), así
/// que el stream nunca se seca a mitad de un fragmento. Sin dependencias externas: solo
/// P/Invoke/COM sobre las APIs de audio de Windows.
///
/// La entrega se hace sobre <see cref="Client"/>: RecordingService asigna el StandardInput
/// del proceso FFmpeg de cada fragmento. Sin cliente asignado, el audio se descarta (no se
/// acumula). Así se evita todo el problema de pipes nombrados (escaping, ACL, visibilidad).
/// </summary>
public class WasapiLoopbackCapture : IDisposable
{
    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private Thread? _thread;
    private volatile bool _stop;
    private long _writeErrors;

    /// <summary>Stream de destino del PCM (stdin del FFmpeg actual). Null = descartar.</summary>
    public Stream? Client { get; set; }

    public bool IsRunning { get; private set; }
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public string? LastError { get; private set; }
    public long BytesDelivered { get; private set; }
    public long WriteErrors => _writeErrors;
    private long _lastWriteTicks;
    public long PacketsPolled { get; private set; }
    public uint LastPacketFrames { get; private set; }

    /// <summary>Arranca la captura. Devuelve false si no hay dispositivo de reproducción o
    /// falla la inicialización (el caller hace fallback a dshow).</summary>
    public bool TryStart(ILoggerService logger)
    {
        if (IsRunning) return true;
        IntPtr pFormat = IntPtr.Zero;
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (enumeratorType == null) { LastError = "no se pudo crear MMDeviceEnumerator"; return false; }
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;

            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var device);
            if (hr != 0 || device == null) { LastError = $"sin dispositivo de reproduccion por defecto (hr={hr:X})"; return false; }

            var iidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
            hr = device.Activate(ref iidAudioClient, CLSCTX_ALL, IntPtr.Zero, out var audioClientObj);
            if (hr != 0) { LastError = $"Activate IAudioClient fallo (hr={hr:X})"; return false; }
            _audioClient = (IAudioClient)audioClientObj;

            hr = _audioClient.GetMixFormat(out pFormat);
            if (hr != 0 || pFormat == IntPtr.Zero) { LastError = $"GetMixFormat fallo (hr={hr:X})"; return false; }

            var format = Marshal.PtrToStructure<WAVEFORMATEX>(pFormat);
            // WAVE_FORMAT_EXTENSIBLE (0xFFFE): SubFormat va tras ValidBits(2)+ChannelMask(4).
            bool isFloat = format.wFormatTag == 3 ||
                           (format.wFormatTag == 0xFFFE && Marshal.ReadInt16(pFormat, 24) == 3);

            const int bits = 16;
            if (format.nChannels == 0 || format.nSamplesPerSec == 0 ||
                (!isFloat && format.wBitsPerSample != bits))
            {
                LastError = $"formato del mezclador no soportado (tag={format.wFormatTag}, bits={format.wBitsPerSample})";
                return false;
            }

            SampleRate = (int)format.nSamplesPerSec;
            Channels = format.nChannels;

            const long bufferDuration100ns = 1_000_000; // 100 ms
            hr = _audioClient.Initialize(
                AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK,
                bufferDuration100ns, 0, pFormat, IntPtr.Zero);
            if (hr != 0) { LastError = $"IAudioClient.Initialize fallo (hr={hr:X})"; return false; }

            var iidCapture = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
            hr = _audioClient.GetService(ref iidCapture, out IntPtr pCapture);
            if (hr != 0 || pCapture == IntPtr.Zero) { LastError = $"GetService IAudioCaptureClient fallo (hr={hr:X})"; return false; }
            _captureClient = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(pCapture);
            Marshal.Release(pCapture);

            _stop = false;
            _thread = new Thread(() => CaptureLoop(logger, isFloat)) { IsBackground = true };
            _thread.Start();

            IsRunning = true;
            logger.LogInfo($"WASAPI loopback activo: {SampleRate} Hz, {Channels} canales (PCM por stdin de FFmpeg).");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Cleanup();
            return false;
        }
        finally
        {
            if (pFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(pFormat);
        }
    }

    private void CaptureLoop(ILoggerService logger, bool isFloat)
    {
        try
        {
            int hr = _audioClient!.Start();
            if (hr != 0) { LastError = $"IAudioClient.Start fallo (hr={hr:X})"; return; }

            int bytesPerFrame = Channels * 2; // salida s16le
            long nextDebugAt = Environment.TickCount64 + 10000;
            while (!_stop)
            {
                if (Environment.TickCount64 >= nextDebugAt)
                {
                    nextDebugAt = Environment.TickCount64 + 10000;
                    logger.LogDebug($"loopback WASAPI: paquetes={PacketsPolled} ultFrames={LastPacketFrames} bytes={BytesDelivered} cliente={(Client != null)} running={IsRunning}");
                }
                hr = _captureClient!.GetNextPacketSize(out uint packetFrames);
                if (hr != 0) break;
                PacketsPolled++;
                LastPacketFrames = packetFrames;

                if (packetFrames == 0)
                {
                    Thread.Sleep(5);
                    // Mantener vivo el flujo: algunos drivers SUSPENDEN el endpoint cuando no
                    // suena nada y el loopback no entrega paquetes. Sin datos, FFmpeg se bloquea
                    // leyendo el stdin y el fragmento queda vacío. Se inyecta silencio cuando el
                    // cliente lleva >30 ms sin recibir nada.
                    WriteSilenceIfIdle();
                    continue;
                }

                hr = _captureClient.GetBuffer(out IntPtr pData, out uint numFrames, out uint flags, out _, out _);
                if (hr != 0) break;

                try
                {
                    byte[] pcm = new byte[numFrames * bytesPerFrame];
                    if ((flags & 1 /*AUDCLNT_BUFFERFLAGS_SILENT*/) == 0)
                    {
                        if (isFloat) ConvertFloatToS16(pData, numFrames * (uint)Channels, pcm);
                        else Marshal.Copy(pData, pcm, 0, pcm.Length);
                    }
                    // Si el endpoint entrega paquetes MÁS LENTO que el reloj (algunos drivers
                    // limitan la entrega de silencio), rellenar el hueco con silencio para que
                    // el audio nunca se quede atrás: si el audio se atrasa, FFmpeg espera al
                    // audio y TODO el fragmento se ralentiza (verificado en prueba real).
                    var ahora = DateTime.UtcNow.Ticks;
                    if (_lastWriteTicks != 0)
                    {
                        var transcurridosMs = (ahora - _lastWriteTicks) / TimeSpan.TicksPerMillisecond;
                        var duracionPaqueteMs = numFrames * 1000.0 / SampleRate;
                        if (transcurridosMs > duracionPaqueteMs * 1.8)
                        {
                            var huecoMs = Math.Min(transcurridosMs - duracionPaqueteMs, 250);
                            EscribirSilencio((int)huecoMs);
                        }
                    }
                    Deliver(pcm, logger);
                }
                finally
                {
                    _captureClient.ReleaseBuffer(numFrames);
                }
            }

            try { _audioClient.Stop(); } catch { }
        }
        catch (Exception ex)
        {
            logger.LogError("Error en el hilo de captura WASAPI loopback", ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void Deliver(byte[] pcm, ILoggerService logger)
    {
        var client = Client;
        if (client == null) return; // sin FFmpeg a la escucha: se descarta (no acumular)

        try
        {
            client.Write(pcm, 0, pcm.Length);
            BytesDelivered += pcm.Length;
            _lastWriteTicks = DateTime.UtcNow.Ticks;
        }
        catch (Exception)
        {
            // FFmpeg cerro el stdin (fin del fragmento): el siguiente fragmento reasigna Client.
            var errs = Interlocked.Increment(ref _writeErrors);
            if (errs == 1 || errs % 50 == 0)
            {
                logger.LogDebug($"WASAPI loopback: escritura al stdin falló {errs} veces (fragmento cambiando).");
            }
        }
    }

    // Hasta 250 ms de silencio estéreo s16 (el mayor hueco que se rellena de una vez).
    private static readonly byte[] SilenceBuffer = new byte[48000 * 2 * 2 * 250 / 1000];

    private void WriteSilenceIfIdle()
    {
        var client = Client;
        if (client == null) return;
        var elapsedMs = (DateTime.UtcNow.Ticks - _lastWriteTicks) / TimeSpan.TicksPerMillisecond;
        if (elapsedMs < 25) return;
        // Cantidad proporcional al tiempo transcurrido (no un chunk fijo): así el audio
        // avanza a 1.0x aunque el endpoint esté suspendido y no entregue nada.
        EscribirSilencio((int)Math.Min(elapsedMs, 250));
    }

    private void EscribirSilencio(int ms)
    {
        var client = Client;
        if (client == null) return;
        try
        {
            var bytes = Math.Min(SilenceBuffer.Length, ms * SampleRate * Channels * 2 / 1000);
            if (bytes <= 0) return;
            client.Write(SilenceBuffer, 0, bytes);
            BytesDelivered += bytes;
            _lastWriteTicks = DateTime.UtcNow.Ticks;
        }
        catch { /* cliente cerrado: el siguiente fragmento reasigna Client */ }
    }

    private static void ConvertFloatToS16(IntPtr pFloat, uint sampleCount, byte[] output)
    {
        for (uint i = 0; i < sampleCount; i++)
        {
            float f = Marshal.PtrToStructure<float>(IntPtr.Add(pFloat, (int)(i * 4)));
            if (f > 1f) f = 1f;
            if (f < -1f) f = -1f;
            short s = (short)(f * 32767f);
            output[i * 2] = (byte)(s & 0xFF);
            output[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
    }

    public void Stop()
    {
        _stop = true;
        try { _thread?.Join(2000); } catch { }
        Cleanup();
    }

    private void Cleanup()
    {
        IsRunning = false;
        Client = null;
        try { _audioClient?.Stop(); } catch { }
        try { (_audioClient as IDisposable)?.Dispose(); } catch { }
        try { (_captureClient as IDisposable)?.Dispose(); } catch { }
        _audioClient = null;
        _captureClient = null;
    }

    public void Dispose() => Stop();

    private const uint CLSCTX_ALL = 0x17;
    private const uint AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;

    private enum EDataFlow { eRender = 0, eCapture = 1 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(uint shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        [PreserveSig] int IsFormatSupported(uint shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }
}
