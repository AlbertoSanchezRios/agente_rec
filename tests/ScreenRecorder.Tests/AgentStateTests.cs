using System;
using ScreenRecorder.Core.State;
using Xunit;

namespace ScreenRecorder.Tests;

public class AgentStateTests
{
    [Theory]
    [InlineData(AgentState.Iniciando)]
    [InlineData(AgentState.Grabando)]
    [InlineData(AgentState.PausadoBloqueo)]
    [InlineData(AgentState.PausadoSuspension)]
    [InlineData(AgentState.Convirtiendo)]
    [InlineData(AgentState.Detenido)]
    public void AgentStateStore_SetThenGet_RoundTripsState(AgentState state)
    {
        AgentStateStore.Set(state, "test");

        var snapshot = AgentStateStore.Get();

        Assert.Equal(state, snapshot.Estado);
        Assert.Equal("test", snapshot.Detalle);
        Assert.True((DateTime.UtcNow - snapshot.ActualizadoUtc).TotalMinutes < 5);
    }

    [Fact]
    public void AgentStateStore_Get_AlwaysReturnsSnapshot()
    {
        // Nunca debe lanzar, exista o no el archivo de estado.
        var snapshot = AgentStateStore.Get();
        Assert.NotNull(snapshot);
    }

    [Theory]
    [InlineData(AgentState.PausadoBloqueo, true)]
    [InlineData(AgentState.PausadoSuspension, true)]
    [InlineData(AgentState.Convirtiendo, true)]
    [InlineData(AgentState.Grabando, false)]
    [InlineData(AgentState.Iniciando, false)]
    [InlineData(AgentState.Detenido, false)]
    public void AgentStateStore_IsPausedOrBusy_ClassifiesStates(AgentState state, bool expected)
    {
        Assert.Equal(expected, AgentStateStore.IsPausedOrBusy(state));
    }

    [Fact]
    public void WatchdogPolicy_ComputeRelaunchBackoff_GrowsAndCaps()
    {
        var baseInterval = TimeSpan.FromSeconds(10);
        var max = TimeSpan.FromMinutes(5);

        // Intento 1: relanzamiento inmediato (primer tick).
        Assert.Equal(TimeSpan.Zero, WatchdogPolicy.ComputeRelaunchBackoff(1, baseInterval, max));
        // A partir del intento 2: 10s, 20s, 40s...
        Assert.Equal(TimeSpan.FromSeconds(10), WatchdogPolicy.ComputeRelaunchBackoff(2, baseInterval, max));
        Assert.Equal(TimeSpan.FromSeconds(20), WatchdogPolicy.ComputeRelaunchBackoff(3, baseInterval, max));
        Assert.Equal(TimeSpan.FromSeconds(40), WatchdogPolicy.ComputeRelaunchBackoff(4, baseInterval, max));
        // Un intento muy alto no supera el tope.
        Assert.Equal(max, WatchdogPolicy.ComputeRelaunchBackoff(50, baseInterval, max));
    }

    [Fact]
    public void WatchdogPolicy_ShouldSendQuieto_RespectsStopFlagAndPauseStates()
    {
        // Grabando/Iniciando sin bandera: se puede recuperar.
        Assert.True(WatchdogPolicy.ShouldSendQuieto(AgentState.Grabando, stopFlag: false));
        Assert.True(WatchdogPolicy.ShouldSendQuieto(AgentState.Iniciando, stopFlag: false));

        // Pausas legítimas y conversión: NUNCA enviar 'quieto'.
        Assert.False(WatchdogPolicy.ShouldSendQuieto(AgentState.PausadoBloqueo, stopFlag: false));
        Assert.False(WatchdogPolicy.ShouldSendQuieto(AgentState.PausadoSuspension, stopFlag: false));
        Assert.False(WatchdogPolicy.ShouldSendQuieto(AgentState.Convirtiendo, stopFlag: false));
        Assert.False(WatchdogPolicy.ShouldSendQuieto(AgentState.Detenido, stopFlag: false));

        // Detención voluntaria: NUNCA.
        Assert.False(WatchdogPolicy.ShouldSendQuieto(AgentState.Grabando, stopFlag: true));
    }
}
