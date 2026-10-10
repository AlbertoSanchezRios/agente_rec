using System;
using System.IO;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Validación de integridad de contenedores multimedia por cabecera y tamaño. Evita subir a la
/// NUC archivos de 0 bytes o corruptos ("a veces no se pueden ver").
/// </summary>
public static class MediaValidator
{
    private const int MinSizeBytes = 1024;

    /// <summary>MKV (Matroska): cabecera EBML 0x1A 0x45 0xDF 0xA3.</summary>
    public static bool IsValidMkv(string path)
    {
        return HasHeader(path, h => h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3);
    }

    /// <summary>MP4: caja 'ftyp' en el offset 4.</summary>
    public static bool IsValidMp4(string path)
    {
        return HasHeader(path, h => h[4] == (byte)'f' && h[5] == (byte)'t' && h[6] == (byte)'y' && h[7] == (byte)'p');
    }

    /// <summary>Válido si es un MKV o un MP4 íntegro.</summary>
    public static bool IsValidMedia(string path) => IsValidMkv(path) || IsValidMp4(path);

    private static bool HasHeader(string path, Func<byte[], bool> predicate)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < MinSizeBytes) return false;
            using var fs = File.OpenRead(path);
            var head = new byte[12];
            if (fs.Read(head, 0, 12) < 12) return false;
            return predicate(head);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Primeros 16 bytes del archivo en hex, para diagnosticar rechazos de validación.</summary>
    public static string HeaderHex(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var b = new byte[16];
            var n = fs.Read(b, 0, 16);
            return BitConverter.ToString(b, 0, n).Replace("-", " ");
        }
        catch
        {
            return "?";
        }
    }
}
