using System;
using System.Threading;
using System.Threading.Tasks;
using ScreenRecorder.Agent;
using ScreenRecorder.Core.Ipc;
using ScreenRecorder.Core.Models;
using ScreenRecorder.Core.Services;
using Xunit;

namespace ScreenRecorder.Tests;

public class AgentControlHandlerTests
{
    private class DummyRecordingManager : IRecordingManagerService
    {
        public bool IsRecordingActive { get; set; } = true;
        public int StopCallCount { get; private set; }
        public int StartCallCount { get; private set; }

        public void Start()
        {
            StartCallCount++;
            IsRecordingActive = true;
        }

        public void Stop()
        {
            StopCallCount++;
            IsRecordingActive = false;
        }
    }

    private class DummySettingsService : ISettingsService
    {
        public RecordingSettings LoadSettings() => new();
        public void SaveSettings(RecordingSettings settings) { }
        public bool IsNetworkPathConfigured() => true;
    }

    [Fact]
    public void Handle_NullRequest_ReturnsFailWithoutThrowing()
    {
        var logger = new LoggerService();
        var recMgr = new DummyRecordingManager();
        var settings = new DummySettingsService();
        var handler = new AgentControlHandler(recMgr, settings, logger, exitAction: () => { });

        var response = handler.Handle(null);

        Assert.False(response.Ok);
        Assert.Contains("comando desconocido", response.Error ?? string.Empty);
    }

    [Fact]
    public void Handle_UnknownCommand_ReturnsFail()
    {
        var logger = new LoggerService();
        var recMgr = new DummyRecordingManager();
        var settings = new DummySettingsService();
        var handler = new AgentControlHandler(recMgr, settings, logger, exitAction: () => { });

        var response = handler.Handle(new ControlRequest { Cmd = "comando_inexistente" });

        Assert.False(response.Ok);
        Assert.Contains("comando desconocido", response.Error ?? string.Empty);
    }

    [Fact]
    public void Handle_StopCommand_ActivatesShutdown_AndRejectsSubsequentCommands()
    {
        var logger = new LoggerService();
        var recMgr = new DummyRecordingManager();
        var settings = new DummySettingsService();
        var handler = new AgentControlHandler(recMgr, settings, logger, exitAction: () => { });

        // 1. Comando stop
        var stopResponse = handler.Handle(new ControlRequest { Cmd = "stop" });
        Assert.True(stopResponse.Ok);

        // 2. Cualquier comando entrante posterior (ej. restart o start) debe ser rechazado inmediatamente
        var restartResponse = handler.Handle(new ControlRequest { Cmd = "restart" });
        Assert.False(restartResponse.Ok);
        Assert.Contains("el agente se está cerrando", restartResponse.Error ?? string.Empty);

        var quietResponse = handler.Handle(new ControlRequest { Cmd = "quieto" });
        Assert.False(quietResponse.Ok);
        Assert.Contains("el agente se está cerrando", quietResponse.Error ?? string.Empty);
    }

    [Fact]
    public async Task Handle_ConcurrentStopAndRestart_RejectsSubsequentRequestsUnderStress()
    {
        var logger = new LoggerService();
        var recMgr = new DummyRecordingManager();
        var settings = new DummySettingsService();
        var handler = new AgentControlHandler(recMgr, settings, logger, exitAction: () => { });

        var startBarrier = new ManualResetEventSlim(false);
        var stopDone = false;
        var tasks = new Task[10];

        // 1 tarea envía stop
        tasks[0] = Task.Run(() =>
        {
            startBarrier.Wait();
            var resp = handler.Handle(new ControlRequest { Cmd = "stop" });
            Assert.True(resp.Ok);
            stopDone = true;
        });

        // 9 tareas compiten enviando restart continuamente
        for (int i = 1; i < 10; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                startBarrier.Wait();
                for (int j = 0; j < 50; j++)
                {
                    var resp = handler.Handle(new ControlRequest { Cmd = "restart" });
                    if (stopDone && !resp.Ok)
                    {
                        Assert.Contains("el agente se está cerrando", resp.Error ?? string.Empty);
                    }
                    await Task.Yield();
                }
            });
        }

        startBarrier.Set();
        await Task.WhenAll(tasks);

        // Al finalizar, el estado debe ser de rechazo definitivo
        var finalResp = handler.Handle(new ControlRequest { Cmd = "start" });
        Assert.False(finalResp.Ok);
        Assert.Contains("el agente se está cerrando", finalResp.Error ?? string.Empty);
    }
}
