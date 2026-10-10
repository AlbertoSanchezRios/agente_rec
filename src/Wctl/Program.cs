using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using ScreenRecorder.Core.Constants;
using ScreenRecorder.Core.Ipc;
using ScreenRecorder.Core.Services;

namespace ScreenRecorder.Wctl;

/// <summary>
/// Wctl: interfaz de línea de comandos para controlar y diagnosticar Screen Recorder.
/// Envía peticiones al agente por el named pipe (solo-admin) y muestra la respuesta.
/// Si no se ejecuta como administrador, se auto-eleva con UAC.
/// </summary>
internal static class Program
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["perro"] = "config",
        ["quieto"] = "restart",
        ["echate"] = "stop",
        ["-h"] = "help",
        ["-?"] = "help",
        ["--help"] = "help",
        ["-help"] = "help"
    };

    private static int Main(string[] args)
    {
        var parsed = Parse(args);
        var cmd = Normalize(parsed.Command);

        // Modo "salida a archivo": lo usa el proceso padre tras auto-elevar. El hijo elevado
        // escribe aquí y el padre lo imprime, evitando perder la salida en una consola nueva.
        StreamWriter? fileOut = null;
        if (!string.IsNullOrEmpty(parsed.OutPath))
        {
            try
            {
                fileOut = new StreamWriter(parsed.OutPath, append: false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(fileOut);
                Console.SetError(fileOut);
            }
            catch { fileOut = null; }
        }

        try
        {
            if (cmd == null || cmd == "help")
            {
                PrintHelp();
                return 0;
            }

            // El pipe de control es solo-admin: elevarse antes de conectar si hace falta.
            if (!NetworkPathHelper.IsProcessElevated())
            {
                return RelaunchElevated(args);
            }

            try
            {
                var response = ControlClient.Send(cmd, parsed.Options, connectTimeoutMs: 2000, attempts: 3, retryDelayMs: 800);
                if (response == null)
                {
                    Console.Error.WriteLine("No hay una instancia de Screen Recorder en ejecución (o no responde al canal de control).");
                    return 2;
                }

                return Print(cmd, response, parsed.Json);
            }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Acceso denegado al canal de control. Ejecute Wctl como administrador.");
                return 3;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error de control: {ex.Message}");
                return 4;
            }
        }
        finally
        {
            try { fileOut?.Flush(); fileOut?.Dispose(); } catch { /* no crítico */ }
        }
    }

    private static string? Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cmd = raw.Trim();
        if (Aliases.TryGetValue(cmd, out var alias)) return alias;
        return cmd.TrimStart('-').ToLowerInvariant();
    }

    private static int Print(string cmd, ControlResponse response, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            }));
            return response.Ok ? 0 : 1;
        }

        if (!response.Ok)
        {
            Console.Error.WriteLine($"ERROR: {response.Error}");
            return 1;
        }

        if (response.Data is not { } data)
        {
            Console.WriteLine("OK");
            return 0;
        }

        // 'logs': imprimir las líneas limpias, sin envoltorio JSON.
        if (cmd == "logs" && data.TryGetProperty("lineas", out var lineas) && lineas.ValueKind == JsonValueKind.Array)
        {
            if (data.TryGetProperty("archivo", out var archivo) && archivo.ValueKind == JsonValueKind.String)
            {
                Console.WriteLine($"# {archivo.GetString()}");
            }
            foreach (var l in lineas.EnumerateArray())
            {
                Console.WriteLine(l.GetString());
            }
            return 0;
        }

        Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"Wctl {AppConstants.AppVersion} - control de Screen Recorder");
        Console.WriteLine();
        Console.WriteLine("Uso:  Wctl -<comando> [opciones]");
        Console.WriteLine();
        Console.WriteLine("Comandos:");
        Console.WriteLine("  -status            Estado del agente, procesos, ruta de red y cola de envíos.");
        Console.WriteLine("  -config            Abre la ventana de configuración (elevada).");
        Console.WriteLine("  -start / -restart  Reinicia el ciclo de grabación (aplica cambios).");
        Console.WriteLine("  -stop              Detención voluntaria: cierra la grabación y sale.");
        Console.WriteLine("  -pause / -resume   Pausa o reanuda la grabación sin cerrar el agente.");
        Console.WriteLine("  -upload/-orphans   Consulta los envíos pendientes hacia la NUC.");
        Console.WriteLine("  -logs [-lines N]   Muestra las últimas N líneas del log (por defecto 50).");
        Console.WriteLine("  -doctor            Diagnóstico rápido (estado + errores recientes).");
        Console.WriteLine("  -version           Versión del agente.");
        Console.WriteLine("  -help              Esta ayuda.");
        Console.WriteLine();
        Console.WriteLine("Opciones:");
        Console.WriteLine("  --json             Salida JSON (para scripting).");
        Console.WriteLine();
        Console.WriteLine("Alias legacy: -perro = -config, -quieto = -restart, -echate = -stop");
    }

    private static int RelaunchElevated(string[] args)
    {
        // El hijo elevado escribe su salida en un archivo temporal que el padre imprime después,
        // porque con 'runas' (UseShellExecute) no se puede redirigir stdout directamente.
        var temp = Path.Combine(Path.GetTempPath(), "wctl_" + Guid.NewGuid().ToString("N") + ".out");
        try
        {
            var childArgs = string.Join(' ', args.Select(Quote).Concat(new[] { "--out", Quote(temp) }));
            var psi = new ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = Environment.ProcessPath ?? "Wctl.exe",
                Arguments = childArgs,
                Verb = "runas"
            };
            using var child = Process.Start(psi);
            child?.WaitForExit(180000);

            if (File.Exists(temp))
            {
                Console.Write(File.ReadAllText(temp));
            }
            return child?.ExitCode ?? 0;
        }
        catch (Win32Exception)
        {
            // El usuario canceló el UAC
            Console.Error.WriteLine("Se canceló la elevación (UAC). Wctl requiere permisos de administrador.");
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"No se pudo elevar Wctl: {ex.Message}");
            return 5;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* no crítico */ }
        }
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    private sealed class ParsedArgs
    {
        public string Command { get; set; } = string.Empty;
        public bool Json { get; set; }
        public string? OutPath { get; set; }
        public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static ParsedArgs Parse(string[] args)
    {
        var result = new ParsedArgs();
        if (args.Length == 0) return result;

        result.Command = args[0];
        for (int i = 1; i < args.Length; i++)
        {
            var token = args[i];
            if (token is "--json" or "-json")
            {
                result.Json = true;
                continue;
            }

            if (token is "--out" or "-out")
            {
                if (i + 1 < args.Length) result.OutPath = args[++i];
                continue;
            }

            var key = token.TrimStart('-');
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                result.Options[key] = args[++i];
            }
            else
            {
                result.Options[key] = "true";
            }
        }
        return result;
    }
}
