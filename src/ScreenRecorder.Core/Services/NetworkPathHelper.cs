using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Helper para rutas de red: distingue letra de unidad vs UNC, resuelve unidades mapeadas
/// a su nombre UNC real y genera diagnósticos cuando una ruta no es accesible.
/// Nota: un proceso elevado (UAC) no ve las unidades mapeadas del usuario; por eso la
/// resolución puede fallar aunque la unidad funcione en el Explorador.
/// </summary>
public static class NetworkPathHelper
{
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    /// <summary>
    /// Indica si la ruta usa letra de unidad (p. ej. "Y:\...").
    /// </summary>
    public static bool IsDriveLetterPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var root = Path.GetPathRoot(path);
        return root != null && root.Length >= 2 && char.IsLetter(root[0]) && root[1] == ':';
    }

    /// <summary>
    /// Resuelve la ruta UNC real de una ruta con letra de unidad mapeada
    /// (p. ej. "Y:\SIS" con Y: → "\\EDC-ZP\SIS" devuelve "\\EDC-ZP\SIS\SIS").
    /// Devuelve null si no es unidad de red o no se pudo resolver (p. ej. proceso elevado).
    /// </summary>
    public static string? TryResolveToUnc(string path)
    {
        try
        {
            if (!IsDriveLetterPath(path)) return null;

            var root = Path.GetPathRoot(path)!;
            var sb = new StringBuilder(512);
            var size = sb.Capacity;
            var result = WNetGetConnection(root.TrimEnd('\\'), sb, ref size);
            if (result != 0) return null; // 0 = NO_ERROR

            var subPath = path.Substring(root.Length).TrimStart('\\', '/');
            return string.IsNullOrEmpty(subPath) ? sb.ToString() : Path.Combine(sb.ToString(), subPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Indica si el proceso actual corre con permisos de administrador.
    /// </summary>
    public static bool IsProcessElevated()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Cadena de diagnóstico con todo lo relevante cuando una ruta no es accesible:
    /// si existe, si es letra (y a qué UNC resuelve y si éste existe), si el proceso
    /// está elevado y con qué usuario corre.
    /// </summary>
    public static string BuildInaccessibleDiagnostics(string path)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append($"path='{path}' existe={SafeExists(path)}");

            if (IsDriveLetterPath(path))
            {
                var unc = TryResolveToUnc(path);
                sb.Append($" | UNC_resuelto='{unc ?? "no se pudo resolver (proceso elevado sin visión del mapeo?)"}'");
                if (unc != null)
                    sb.Append($" existe_unc={SafeExists(unc)}");
            }

            sb.Append($" | elevado={IsProcessElevated()} usuario='{Environment.UserName}'");

            if (IsProcessElevated())
            {
                sb.Append(" | NOTA: proceso elevado: Windows no muestra las unidades de red mapeadas del usuario a procesos administrativos. Use una ruta UNC o no ejecute la app como administrador.");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"no se pudo diagnosticar: {ex.Message}";
        }
    }

    private static bool SafeExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }
}
