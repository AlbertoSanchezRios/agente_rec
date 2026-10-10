using System;
using System.Diagnostics;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Provee utilidades para inspección y consulta de procesos sin fugar handles nativos.
/// Process.GetProcessesByName retorna un arreglo de instancias Process donde cada una
/// retiene un SafeProcessHandle no administrado; deben liberarse explícitamente con Dispose()
/// para evitar agotamiento de handles en servicios de larga duración (24/7).
/// </summary>
public static class ProcessHelper
{
    /// <summary>
    /// Comprueba si existe al menos un proceso con el nombre especificado, liberando
    /// deterministamente todos los objetos Process creados.
    /// </summary>
    public static bool IsProcessRunning(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch
        {
            return false;
        }

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var p in processes)
            {
                try { p.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Cuenta las instancias en ejecución del proceso especificado, liberando los handles.
    /// </summary>
    public static int GetProcessCount(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch
        {
            return 0;
        }

        try
        {
            return processes.Length;
        }
        finally
        {
            foreach (var p in processes)
            {
                try { p.Dispose(); } catch { }
            }
        }
    }
}
