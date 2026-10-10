using System;
using ScreenRecorder.Core.State;

namespace ScreenRecorder.Core.State;

/// <summary>
/// Reglas puras (sin efectos secundarios) que gobiernan el watchdog del servicio. Se aíslan
/// aquí para poder probarlas sin arrancar el servicio ni depender de procesos reales.
/// </summary>
public static class WatchdogPolicy
{
    /// <summary>
    /// Backoff exponencial para el relanzamiento del agente: base*2^(intento-1), con tope.
    /// Intento 1 = reintento inmediato (el primer tick ya lanza); intento 2 = base; etc.
    /// Evita el bucle de relanzamiento cada 10 s cuando el agente muere nada más arrancar.
    /// </summary>
    public static TimeSpan ComputeRelaunchBackoff(int attempt, TimeSpan baseInterval, TimeSpan maxBackoff)
    {
        if (attempt <= 1) return TimeSpan.Zero;
        if (baseInterval <= TimeSpan.Zero) return maxBackoff;

        var factor = Math.Pow(2, attempt - 2); // intento 2 -> 1x, 3 -> 2x, 4 -> 4x...
        var seconds = baseInterval.TotalSeconds * factor;
        if (double.IsInfinity(seconds) || seconds >= maxBackoff.TotalSeconds) return maxBackoff;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Indica si el watchdog puede enviar 'quieto' para recuperar una grabación atascada.
    /// NUNCA debe hacerlo si hay detención voluntaria ('stopped') ni si el agente está en una
    /// pausa legítima (bloqueo/suspensión) o convirtiendo: enviar 'quieto' en bloqueo de
    /// sesión provocaba un reinicio fallido de la captura cada ~10 minutos.
    /// </summary>
    public static bool ShouldSendQuieto(AgentState state, bool stopFlag)
    {
        if (stopFlag) return false;
        return state is AgentState.Grabando or AgentState.Iniciando;
    }
}
