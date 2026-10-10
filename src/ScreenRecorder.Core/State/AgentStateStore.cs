using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenRecorder.Core.Constants;

namespace ScreenRecorder.Core.State;

/// <summary>
/// Estado observable del agente de grabación. Lo publica el agente (RecordingManagerService)
/// y lo consulta el watchdog del servicio para no confundir una pausa legítima (bloqueo de
/// sesión, suspensión, conversión) con una grabación atascada.
/// </summary>
public enum AgentState
{
    /// <summary>Arrancando o sin datos (valor por defecto).</summary>
    Iniciando,
    /// <summary>Grabando con normalidad.</summary>
    Grabando,
    /// <summary>Pausado por bloqueo de sesión (Win+L): no se graba por diseño.</summary>
    PausadoBloqueo,
    /// <summary>Pausado por suspensión del sistema: no se graba mientras duerme.</summary>
    PausadoSuspension,
    /// <summary>Convirtiendo un fragmento MKV a MP4 (la captura puede estar entre fragmentos).</summary>
    Convirtiendo,
    /// <summary>Detenido de forma ordenada (echate/apagado) o tras fallos.</summary>
    Detenido
}

/// <summary>Instantánea del estado del agente, tal como se persiste en agent-state.json.</summary>
public sealed class AgentStateSnapshot
{
    public AgentState Estado { get; set; } = AgentState.Iniciando;
    public DateTime ActualizadoUtc { get; set; } = DateTime.UtcNow;
    public int Pid { get; set; }
    public string? Detalle { get; set; }
}

/// <summary>
/// Persistencia del estado del agente en ProgramData\ScreenRecorder\agent-state.json.
/// Escritura atómica (temp único + Move) y lectura tolerante: nunca lanza, porque el estado
/// es best-effort y no debe romper ni la grabación ni el watchdog.
/// </summary>
public static class AgentStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly object Sync = new();

    /// <summary>Ruta del archivo de estado.</summary>
    public static string StateFilePath =>
        Path.Combine(AppConstants.ProgramDataDirectory, "agent-state.json");

    /// <summary>Publica el estado actual del agente. Best-effort: cualquier fallo se ignora.</summary>
    public static void Set(AgentState state, string? detalle = null)
    {
        try
        {
            var snapshot = new AgentStateSnapshot
            {
                Estado = state,
                ActualizadoUtc = DateTime.UtcNow,
                Pid = Environment.ProcessId,
                Detalle = detalle
            };

            var dir = AppConstants.ProgramDataDirectory;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(snapshot, Options);

            // Nombre temporal único por escritura: evita colisiones si dos procesos (agente y,
            // en el futuro, el conversor) publican estado casi al mismo tiempo.
            var temp = Path.Combine(dir, $"agent-state.{Guid.NewGuid():N}.tmp");
            lock (Sync)
            {
                File.WriteAllText(temp, json);
                File.Move(temp, StateFilePath, overwrite: true);
            }
        }
        catch
        {
            // El estado es diagnóstico: nunca romper por no poder escribirlo.
        }
    }

    /// <summary>
    /// Lee el estado actual. Si el archivo no existe, está corrupto o ilegible, devuelve una
    /// instantánea por defecto con <see cref="AgentState.Iniciando"/>.
    /// </summary>
    public static AgentStateSnapshot Get()
    {
        try
        {
            var path = StateFilePath;
            if (!File.Exists(path)) return new AgentStateSnapshot();

            var json = File.ReadAllText(path);
            var snapshot = JsonSerializer.Deserialize<AgentStateSnapshot>(json, Options);
            return snapshot ?? new AgentStateSnapshot();
        }
        catch
        {
            // Deserialización fallida (archivo a medio escribir, enum desconocido...):
            // tratar como "sin datos" para no condicionar al watchdog.
            return new AgentStateSnapshot();
        }
    }

    /// <summary>Devuelve true si el estado actual es una pausa legítima (no es un atasco).</summary>
    public static bool IsPausedOrBusy(AgentState state) =>
        state is AgentState.PausadoBloqueo
            or AgentState.PausadoSuspension
            or AgentState.Convirtiendo;
}
