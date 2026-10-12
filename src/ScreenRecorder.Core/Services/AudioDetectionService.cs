using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Implementación del servicio de detección de audio usando ffmpeg/dshow.
/// Parsea nombre amigable y "Alternative name" (@device_cm_...) y usa el alternativo para captura cuando exista.
/// </summary>
public class AudioDetectionService : IAudioDetectionService
{
    private readonly ILoggerService _logger;
    private readonly string _ffmpegPath;

    // Caché en memoria de la lista de dispositivos (GetAllAudioDevicesWithAlternates).
    // Antes se ejecutaba 'ffmpeg -list_devices' 2-3 veces POR FRAGMENTO; ahora una vez
    // cada TTL. La lista de dispositivos no cambia en caliente salvo conexión/desconexión.
    private static readonly TimeSpan DeviceCacheTtl = TimeSpan.FromMinutes(5);
    private List<DeviceEntry>? _deviceCache;
    private DateTime _deviceCacheAt;

    /// <summary>
    /// Nombres que identifican dispositivos de audio del sistema (mezcla estéreo, salida de onda).
    /// Inglés y español. Incluye variantes con problemas de codificación.
    /// "Mezcla" solo captura dispositivos de mezcla (ej. Mezcla estéreo (Realtek)).
    /// </summary>
    private static readonly string[] SystemAudioKeywords =
    {
        "Stereo Mix", "Mezcla estéreo", "Mezcla estereo", "Mezcla Estéreo",
        "Mezcla est├®reo", // Variantes con codificación incorrecta
        "Mezcla ", "Mezcla(", // Cualquier dispositivo que empiece con "Mezcla" (mezcla estéreo)
        "What U Hear", "Wave Out Mix", "Wave out mix",
        "Salida de onda", "Salida de Onda", "Stereomix",
        "Stereo mix", "stereo mix", "Loopback", "loopback",
        "Stereo Mix (", "Mezcla estéreo (", "Mezcla estereo (",
        "Mezcla est├®reo (" // Con codificación incorrecta
    };

    public AudioDetectionService(ILoggerService logger)
    {
        _logger = logger;
        _ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.AppConstants.FFmpegExeName);
    }

    /// <summary>
    /// Par de nombre amigable y nombre alternativo (si la siguiente línea es "Alternative name ...").
    /// </summary>
    private sealed class DeviceEntry
    {
        public string FriendlyName { get; set; } = "";
        public string? AlternativeName { get; set; }
    }

    /// <summary>
    /// Obtiene todos los dispositivos de audio con nombre amigable y alternativo.
    /// Formato FFmpeg: "DirectShow audio devices" y luego líneas "  \"Nombre\"" y opcionalmente "     Alternative name \"@device_cm_...\""
    /// </summary>
    private List<DeviceEntry> GetAllAudioDevicesWithAlternates()
    {
        if (_deviceCache != null && DateTime.Now - _deviceCacheAt < DeviceCacheTtl)
        {
            _logger.LogDebug($"Usando caché de dispositivos de audio ({_deviceCache.Count} dispositivos, " +
                             $"en caché desde {_deviceCacheAt:HH:mm:ss})");
            return _deviceCache;
        }

        var list = new List<DeviceEntry>();
        try
        {
            var output = ExecuteFFmpegListDevices();
            _deviceCache = null; // se rellena al final del parseo
            
            // Log de depuración (truncado si es muy largo)
            var debugOutput = output.Length > 1500 ? output.Substring(0, 1500) + "..." : output;
            _logger.LogDebug($"FFmpeg list_devices (primeras 1500 chars): {debugOutput}");

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            _logger.LogDebug($"Total de líneas procesadas: {lines.Length}");

            // Patrón principal: [in#0 @ ...] "Nombre" (audio) o [dshow @ ...] "Nombre" (audio)
            // Formato real: [in#0 @ 0000022282ddc840] "Micrófono..." (audio)
            // Más flexible con espacios variables (usar \s* para espacios opcionales)
            var audioDeviceRegex = new Regex(@"\[(?:in#\d+|dshow)\s+@[^\]]+\]\s+[""']([^""']+)[""']\s*\(audio\)", RegexOptions.Compiled);
            // Patrón alternativo: cualquier línea con [algo] seguido de comillas y (audio)
            var audioDeviceRegexSimple = new Regex(@"\[[^\]]+\]\s+[""']([^""']+)[""']\s*\(audio\)", RegexOptions.Compiled);
            // Patrón de último recurso: cualquier línea que tenga (audio) y comillas (sin importar el formato)
            var audioDeviceRegexFallback = new Regex(@"[""']([^""']+)[""']\s*\(audio\)", RegexOptions.Compiled);
            // Patrón para nombre alternativo
            var altNameRegex = new Regex(@"Alternative\s+name\s+""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                
                // Log las primeras líneas para debugging
                if (i < 20)
                {
                    _logger.LogDebug($"Línea {i}: {line}");
                }

                // Buscar dispositivos de audio directamente por el marcador (audio)
                var audioMatch = audioDeviceRegex.Match(line);
                if (!audioMatch.Success)
                {
                    // Intentar con patrón más simple
                    audioMatch = audioDeviceRegexSimple.Match(line);
                }
                if (!audioMatch.Success)
                {
                    // Último recurso: buscar cualquier línea con (audio) y comillas
                    audioMatch = audioDeviceRegexFallback.Match(line);
                }
                
                if (audioMatch.Success)
                {
                    var name = audioMatch.Groups[1].Value.Trim();
                    if (string.IsNullOrEmpty(name))
                        continue;

                    // Decodificar nombre correctamente (manejar UTF-8 mal codificado)
                    name = FixEncoding(name);
                    
                    var deviceEntry = new DeviceEntry { FriendlyName = name };
                    list.Add(deviceEntry);
                    
                    _logger.LogInfo($"✓ Dispositivo de audio detectado: '{name}'");

                    // La siguiente línea puede ser el Alternative name
                    if (i + 1 < lines.Length)
                    {
                        var nextLine = lines[i + 1];
                        var altMatch = altNameRegex.Match(nextLine);
                        if (altMatch.Success)
                        {
                            var alt = altMatch.Groups[1].Value.Trim();
                            if (!string.IsNullOrEmpty(alt) && alt.StartsWith("@device_", StringComparison.OrdinalIgnoreCase))
                            {
                                deviceEntry.AlternativeName = alt;
                                _logger.LogInfo($"  → Nombre alternativo: {alt}");
                            }
                        }
                    }
                    continue;
                }

                // También buscar nombres alternativos que puedan estar en líneas separadas
                if (line.TrimStart().StartsWith("Alternative name", StringComparison.OrdinalIgnoreCase))
                {
                    var altMatch = altNameRegex.Match(line);
                    if (altMatch.Success && list.Count > 0)
                    {
                        var alt = altMatch.Groups[1].Value.Trim();
                        if (!string.IsNullOrEmpty(alt) && alt.StartsWith("@device_", StringComparison.OrdinalIgnoreCase))
                        {
                            var last = list[list.Count - 1];
                            if (string.IsNullOrEmpty(last.AlternativeName))
                            {
                                last.AlternativeName = alt;
                                _logger.LogInfo($"  → Nombre alternativo encontrado para '{last.FriendlyName}': {alt}");
                            }
                        }
                    }
                }
            }

            _logger.LogInfo($"FFmpeg reportó {list.Count} dispositivos de audio (con/sin nombre alternativo).");
            _deviceCache = list;
            _deviceCacheAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al listar dispositivos de audio con FFmpeg", ex);
            if (_deviceCache != null)
            {
                _logger.LogWarning("Se mantiene la caché de dispositivos anterior tras el error de listado.");
                return _deviceCache;
            }
        }

        return list;
    }

    private static bool IsSystemAudioDevice(string friendlyName)
    {
        return SystemAudioKeywords.Any(k =>
            friendlyName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// Construye DeviceId para ffmpeg: SIEMPRE usa nombre alternativo si existe (más confiable).
    /// Si no hay nombre alternativo, usa el nombre amigable pero registra advertencia.
    /// </summary>
    private string BuildDeviceId(DeviceEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.AlternativeName))
        {
            _logger.LogDebug($"Usando nombre alternativo para '{e.FriendlyName}': {e.AlternativeName}");
            return $"audio=\"{e.AlternativeName}\"";
        }
        
        _logger.LogWarning($"NO hay nombre alternativo para '{e.FriendlyName}'. Usando nombre amigable (puede fallar con caracteres especiales).");
        return $"audio=\"{e.FriendlyName}\"";
    }

    public List<AudioDevice> DetectSystemAudioDevices()
    {
        var all = GetAllAudioDevicesWithAlternates();
        var devices = all
            .Where(e => IsSystemAudioDevice(e.FriendlyName))
            .Select(e => new AudioDevice
            {
                Name = e.FriendlyName,
                AlternativeName = e.AlternativeName,
                DeviceId = BuildDeviceId(e),
                Type = AudioDeviceType.SystemAudio,
                IsUsb = e.FriendlyName.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0
            })
            .ToList();

        _logger.LogInfo($"Se detectaron {devices.Count} dispositivos de audio del sistema por dshow (salida del sistema): [{string.Join(", ", devices.Select(d => d.Name))}]");
        
        // Log detallado de cada dispositivo y qué se usará para FFmpeg
        foreach (var device in devices)
        {
            if (!string.IsNullOrEmpty(device.AlternativeName))
            {
                _logger.LogInfo($"  -> {device.Name} | Usando nombre alternativo: {device.AlternativeName}");
            }
            else
            {
                _logger.LogWarning($"  -> {device.Name} | SIN nombre alternativo, usando nombre amigable (puede fallar)");
            }
        }
        
        return devices;
    }

    public List<AudioDevice> DetectMicrophoneDevices()
    {
        var all = GetAllAudioDevicesWithAlternates();
        var devices = all
            .Where(e => !IsSystemAudioDevice(e.FriendlyName))
            .Select(e => new AudioDevice
            {
                Name = e.FriendlyName,
                AlternativeName = e.AlternativeName,
                DeviceId = BuildDeviceId(e),
                Type = AudioDeviceType.Microphone,
                IsUsb = e.FriendlyName.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         e.FriendlyName.IndexOf("USB Audio", StringComparison.OrdinalIgnoreCase) >= 0
            })
            .ToList();

        _logger.LogInfo($"Se detectaron {devices.Count} dispositivos de micrófono: [{string.Join(", ", devices.Select(d => d.Name))}]");
        
        // Log detallado de cada dispositivo y qué se usará para FFmpeg
        foreach (var device in devices)
        {
            if (!string.IsNullOrEmpty(device.AlternativeName))
            {
                _logger.LogInfo($"  -> {device.Name} | Usando nombre alternativo: {device.AlternativeName}");
            }
            else
            {
                _logger.LogWarning($"  -> {device.Name} | SIN nombre alternativo, usando nombre amigable (puede fallar)");
            }
        }
        
        return devices;
    }

    public AudioDevice? GetPreferredSystemAudioDevice()
    {
        var devices = DetectSystemAudioDevices();
        
        if (devices.Count == 0)
        {
            _logger.LogWarning("No se detectaron dispositivos de audio del sistema por dshow; el audio del sistema se captura por WASAPI loopback (no requiere 'Mezcla estéreo').");
            return null;
        }
        
        // Priorizar dispositivos con nombre alternativo (más confiables)
        var withAltName = devices.Where(d => !string.IsNullOrEmpty(d.AlternativeName)).ToList();
        var preferred = withAltName.FirstOrDefault(d => d.IsUsb) ?? withAltName.FirstOrDefault();
        
        if (preferred == null)
        {
            // Si ninguno tiene nombre alternativo, usar el primero disponible
            preferred = devices.FirstOrDefault(d => d.IsUsb) ?? devices.FirstOrDefault();
            if (preferred != null)
            {
                _logger.LogWarning($"Dispositivo seleccionado SIN nombre alternativo: {preferred.Name}. Puede fallar la grabación de audio.");
            }
        }
        
        if (preferred != null)
        {
            var source = preferred.IsUsb ? "USB" : "primario";
            var altInfo = !string.IsNullOrEmpty(preferred.AlternativeName) ? $" (nombre alternativo: {preferred.AlternativeName})" : " (SIN nombre alternativo)";
            _logger.LogInfo($"Dispositivo de audio del sistema preferido ({source}): {preferred.Name}{altInfo}");
            _logger.LogInfo($"DeviceId para FFmpeg: {preferred.DeviceId}");
        }
        
        return preferred;
    }

    public AudioDevice? GetPreferredMicrophoneDevice()
    {
        var devices = DetectMicrophoneDevices();
        
        if (devices.Count == 0)
        {
            _logger.LogWarning("No se detectaron dispositivos de micrófono");
            return null;
        }
        
        // Priorizar dispositivos USB con nombre alternativo
        var usbWithAlt = devices.Where(d => d.IsUsb && !string.IsNullOrEmpty(d.AlternativeName)).OrderByDescending(d => d.Name).ToList();
        var preferred = usbWithAlt.FirstOrDefault();
        
        if (preferred == null)
        {
            // Luego cualquier dispositivo con nombre alternativo
            var withAltName = devices.Where(d => !string.IsNullOrEmpty(d.AlternativeName)).ToList();
            preferred = withAltName.FirstOrDefault();
        }
        
        if (preferred == null)
        {
            // Finalmente, cualquier dispositivo disponible
            preferred = devices.Where(d => d.IsUsb).OrderByDescending(d => d.Name).FirstOrDefault() ?? devices.FirstOrDefault();
            if (preferred != null)
            {
                _logger.LogWarning($"Micrófono seleccionado SIN nombre alternativo: {preferred.Name}. Puede fallar la grabación de audio.");
            }
        }
        
        if (preferred != null)
        {
            var source = preferred.IsUsb ? "USB" : "primario";
            var altInfo = !string.IsNullOrEmpty(preferred.AlternativeName) ? $" (nombre alternativo: {preferred.AlternativeName})" : " (SIN nombre alternativo)";
            _logger.LogInfo($"Micrófono preferido ({source}): {preferred.Name}{altInfo}");
            _logger.LogInfo($"DeviceId para FFmpeg: {preferred.DeviceId}");
        }
        
        return preferred;
    }

    /// <summary>
    /// Corrige problemas de codificación UTF-8 mal interpretados.
    /// FFmpeg puede devolver nombres con caracteres UTF-8 mal codificados como "├│" en lugar de "ó".
    /// </summary>
    private static string FixEncoding(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Mapeo de caracteres mal codificados comunes a sus equivalentes correctos
        // Usar lista de tuplas para evitar duplicados y permitir múltiples reemplazos
        var replacements = new List<(string old, string @new)>
        {
            ("├│", "ó"),
            ("├®", "é"),
            ("├¡", "á"),  // Para "á" - el contexto determinará si es "á" o "í"
            ("├║", "ú"),
            ("├", "í"),   // Para "í" cuando aparece solo
            ("│", ""),
            ("®", "é")
        };

        var fixedText = text;
        foreach (var (old, @new) in replacements)
        {
            fixedText = fixedText.Replace(old, @new);
        }
        
        // Correcciones específicas basadas en contexto común
        // "Micr├│fono" -> "Micrófono"
        fixedText = fixedText.Replace("Micr├│fono", "Micrófono");
        // "est├®reo" -> "estéreo"
        fixedText = fixedText.Replace("est├®reo", "estéreo");
        // "├│" general -> "ó"
        fixedText = fixedText.Replace("├│", "ó");
        // "├®" general -> "é"
        fixedText = fixedText.Replace("├®", "é");

        // Intentar decodificar como UTF-8 si está mal codificado
        try
        {
            // Si el texto contiene caracteres de reemplazo UTF-8 mal codificados,
            // intentar decodificarlos correctamente desde Windows-1252 a UTF-8
            var windows1252 = Encoding.GetEncoding("Windows-1252");
            var bytes = windows1252.GetBytes(text);
            var decoded = Encoding.UTF8.GetString(bytes);
            
            // Si la decodificación produce caracteres válidos y es diferente, usarla
            if (decoded != text && !decoded.Contains('�') && decoded.Length > 0)
            {
                return decoded;
            }
        }
        catch
        {
            // Si falla, usar el texto con reemplazos manuales
        }

        return fixedText;
    }

    private string ExecuteFFmpegListDevices()
    {
        if (!File.Exists(_ffmpegPath))
            throw new FileNotFoundException($"{Constants.AppConstants.FFmpegExeName} no encontrado en: {_ffmpegPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        
        _logger.LogInfo($"Ejecutando FFmpeg: {_ffmpegPath} {startInfo.Arguments}");

        using var process = Process.Start(startInfo);
        if (process == null)
            throw new InvalidOperationException("No se pudo iniciar el proceso ffmpeg");

        // Leer stderr y stdout con codificación UTF-8 (con timeout REAL: si el proceso
        // se cuelga, se mata; antes el GetResult() bloqueaba indefinidamente).
        var stderrTask = Task.Run(() =>
        {
            try
            {
                return process.StandardError.ReadToEnd();
            }
            catch
            {
                return string.Empty;
            }
        });
        var stdoutTask = Task.Run(() =>
        {
            try
            {
                return process.StandardOutput.ReadToEnd();
            }
            catch
            {
                return string.Empty;
            }
        });

        if (!process.WaitForExit(8000))
        {
            _logger.LogWarning("FFmpeg -list_devices no terminó en 8 s; forzando cierre.");
            try { process.Kill(); } catch { /* ya terminado */ }
        }
        process.WaitForExit(2000);

        var stderrWait = Task.WhenAny(stderrTask, Task.Delay(2000)).GetAwaiter().GetResult();
        var stdoutWait = Task.WhenAny(stdoutTask, Task.Delay(2000)).GetAwaiter().GetResult();
        var stderr = stderrWait == stderrTask ? stderrTask.GetAwaiter().GetResult() : string.Empty;
        var stdout = stdoutWait == stdoutTask ? stdoutTask.GetAwaiter().GetResult() : string.Empty;
        
        _logger.LogDebug($"FFmpeg stderr length: {stderr?.Length ?? 0}, stdout length: {stdout?.Length ?? 0}");
        
        // FFmpeg escribe la lista de dispositivos en stderr
        var output = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        
        // Log completo para depuración
        if (!string.IsNullOrEmpty(output))
        {
            // Log todas las líneas que contengan información relevante
            _logger.LogInfo($"FFmpeg output completo ({output.Length} caracteres, {output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length} líneas):");
            var allLines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int relevantLines = 0;
            foreach (var line in allLines)
            {
                // Log líneas relevantes (audio, Alternative, @device_, o que contengan comillas)
                if (line.Contains("audio", StringComparison.OrdinalIgnoreCase) || 
                    line.Contains("Alternative", StringComparison.OrdinalIgnoreCase) || 
                    line.Contains("@device_", StringComparison.OrdinalIgnoreCase) ||
                    (line.Contains("\"") && line.Contains("(")))
                {
                    _logger.LogInfo($"  [{relevantLines++}] {line}");
                }
            }
            
            if (relevantLines == 0)
            {
                _logger.LogWarning("No se encontraron líneas relevantes en la salida de FFmpeg. Mostrando primeras 20 líneas:");
                foreach (var line in allLines.Take(20))
                {
                    _logger.LogInfo($"  {line}");
                }
            }
        }
        else
        {
            _logger.LogError("FFmpeg no devolvió ninguna salida (stderr y stdout vacíos). Verificar que FFmpeg esté funcionando correctamente.");
        }
        
        return output ?? string.Empty;
    }
}
