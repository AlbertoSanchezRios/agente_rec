using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Geometría real de los monitores conectados. Centraliza el acceso a Win32 P/Invoke
/// (EnumDisplayMonitors y GetMonitorInfo) para desacoplar completamente el Core de Windows Forms.
/// </summary>
public interface IScreenGeometryService
{
    /// <summary>Devuelve la región a capturar (offset + tamaño) para un <paramref name="target"/>.</summary>
    CaptureRegion GetCaptureRegion(CaptureTarget target);

    /// <summary>Todos los monitores en el orden que la enumeración expone (para validar output_idx).</summary>
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>Altura/objetivo de escala para un <paramref name="targetHeight"/> en el <paramref name="target"/> elegido.</summary>
    ScaleSize GetScale(CaptureTarget target, int targetHeight);
}

/// <summary>Región del escritorio a capturar: offset en el escritorio virtual + dimensiones.</summary>
public record CaptureRegion(int X, int Y, int Width, int Height)
{
    public string Size => $"{Width}x{Height}";
}

/// <summary>
/// Un monitor tal como lo ve el sistema (orden de enumeración, índice y bounds).
/// Bounds = coordenadas lógicas (escalado del sistema). Scale = factor
/// DPI/96 de ESTE monitor. PhysicalBounds = bounds en píxeles físicos reales (Scale propio).
/// </summary>
public record MonitorInfo(int Index, bool IsPrimary, Rectangle Bounds)
{
    public double Scale { get; init; } = 1.0;
    public Rectangle PhysicalBounds { get; init; } = Bounds;
}

/// <summary>Dimensiones de salida (siempre pares, válidas para H.264).</summary>
public record ScaleSize(int Width, int Height);

public class ScreenGeometryService : IScreenGeometryService
{
    private readonly ILoggerService _logger;

    public ScreenGeometryService(ILoggerService logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        try
        {
            var monitors = new List<MonitorInfo>();
            int index = 0;

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
            {
                var mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf<MONITORINFOEX>();

                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    bool isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                    var bounds = mi.rcMonitor.ToRectangle();
                    var deviceName = mi.szDevice ?? string.Empty;

                    // Físicos primero por EnumDisplaySettings; si falla, por GetDpiForMonitor; y si también, 1.0.
                    var native = GetNativeResolution(deviceName);
                    double scale;
                    Rectangle physical;
                    if (native.HasValue && bounds.Width > 0)
                    {
                        scale = native.Value.Width / (double)bounds.Width;
                        physical = new Rectangle(
                            (int)Math.Round(bounds.X * scale),
                            (int)Math.Round(bounds.Y * scale),
                            native.Value.Width,
                            native.Value.Height);
                    }
                    else
                    {
                        scale = GetMonitorScale(hMonitor);
                        physical = new Rectangle(
                            (int)Math.Round(bounds.X * scale),
                            (int)Math.Round(bounds.Y * scale),
                            (int)Math.Round(bounds.Width * scale),
                            (int)Math.Round(bounds.Height * scale));
                    }

                    monitors.Add(new MonitorInfo(Index: index++, IsPrimary: isPrimary, Bounds: bounds)
                    {
                        Scale = scale,
                        PhysicalBounds = physical
                    });
                }

                return true;
            }, IntPtr.Zero);

            return monitors;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al enumerar monitores", ex);
            return Array.Empty<MonitorInfo>();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    private const int CCHDEVICENAME = 32;
    private const int MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public Rectangle ToRectangle() => new Rectangle(Left, Top, Right - Left, Bottom - Top);
    }

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    private const int EnumCurrentSettings = -1;

    /// <summary>
    /// DEVMODE para EnumDisplaySettings. dmPelsWidth/Height del modo ACTUAL devuelven los
    /// píxeles FÍSICOS reales del monitor, independientes de la conciencia DPI del proceso
    /// (clave: cuando el agente es lanzado por el servicio, GetDpiForMonitor puede fallar y
    /// provocaba el bug del "1/4 de pantalla" tras reiniciar).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    /// <summary>Resolución nativa (píxeles físicos) del monitor, o null si no se puede leer.</summary>
    private static (int Width, int Height)? GetNativeResolution(string deviceName)
    {
        try
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
            if (EnumDisplaySettings(deviceName, EnumCurrentSettings, ref dm) && dm.dmPelsWidth > 0)
            {
                return ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight);
            }
        }
        catch { /* resplado por DPI */ }
        return null;
    }

    /// <summary>Factor DPI/96 del monitor (1.0 = 96 DPI / sin escalado). Fallback 1.0 si falla.</summary>
    private static double GetMonitorScale(IntPtr hMonitor)
    {
        try
        {
            if (hMonitor == IntPtr.Zero) return 1.0;
            if (GetDpiForMonitor(hMonitor, 0 /*MDT_EFFECTIVE_DPI*/, out var dpiX, out _) != 0) return 1.0;
            return dpiX / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    public CaptureRegion GetCaptureRegion(CaptureTarget target)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0)
        {
            _logger.LogWarning("No se detectaron monitores; se usa región por defecto 1920x1080");
            return new CaptureRegion(0, 0, 1920, 1080);
        }

        // Toda la zona virtual: unión de todos los monitores.
        var minX = monitors.Min(m => m.Bounds.X);
        var minY = monitors.Min(m => m.Bounds.Y);
        var maxX = monitors.Max(m => m.Bounds.Right);
        var maxY = monitors.Max(m => m.Bounds.Bottom);

        // Conversión a píxeles FÍSICOS: ffmpeg.exe (WmvCoreHost.exe) es un proceso sin
        // manifiesto DPI-aware; Windows lo virtualiza a 96 DPI escalado por el DPI del monitor
        // PRIMARIO. Es decir: el espacio de coordenadas que ve gdigrab = lógico × escalaPrimario.
        // Sin esta conversión, en una pantalla 4K al 200% (lógico 1920x1080, físico 3840x2160)
        // se pasaba -video_size 1920x1080 y gdigrab copiaba solo el cuadrante sup-izq = 1/4 de pantalla.
        // Limitación conocida: monitores con escalados DIFERENTES entre sí solo capturan bien por
        // ddagrab (output_idx), que es el pipeline preferente; gdigrab es el fallback.
        var primaryScale = monitors.FirstOrDefault(m => m.IsPrimary)?.Scale ?? 1.0;
        int Px(double v) => (int)Math.Round(v * primaryScale);

        if (target == CaptureTarget.All)
        {
            return new CaptureRegion(Px(minX), Px(minY), Px(maxX - minX), Px(maxY - minY));
        }

        // Por monitor: primario = el que la enumeración marca primario, secundario = el primer no-primario.
        var chosen = target == CaptureTarget.Primary
            ? monitors.First(m => m.IsPrimary)
            : monitors.FirstOrDefault(m => !m.IsPrimary);

        if (chosen == null)
        {
            _logger.LogWarning("Se pidió el monitor secundario pero solo hay uno; se usa el primario");
            chosen = monitors.First(m => m.IsPrimary);
        }

        return new CaptureRegion(Px(chosen.Bounds.X), Px(chosen.Bounds.Y), Px(chosen.Bounds.Width), Px(chosen.Bounds.Height));
    }

    public ScaleSize GetScale(CaptureTarget target, int targetHeight)
    {
        var region = GetCaptureRegion(target);
        if (targetHeight <= 0 && region.Height > 0)
        {
            // Resolución NATIVA (targetHeight == 0): grabar al tamaño físico real de la
            // región, sin reescalar (aprovecha 4K; no agranda pantallas pequeñas).
            return new ScaleSize(MakeEven(region.Width), MakeEven(region.Height));
        }
        if (targetHeight <= 0 || region.Height <= 0)
        {
            return new ScaleSize(1920, 1080);
        }

        // Ancho derivado de la proporción REAL de la región (no hardcodear 16:9).
        var w = (int)Math.Round(targetHeight * (double)region.Width / region.Height);
        // H.264 exige dimensiones pares.
        w = MakeEven(w);
        var h = MakeEven(targetHeight);
        return new ScaleSize(w, h);
    }

    private static int MakeEven(int v) => v % 2 == 0 ? v : v + 1;
}
