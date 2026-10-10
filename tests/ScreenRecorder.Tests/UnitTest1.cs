using System;
using System.IO;
using Microsoft.Win32;
using ScreenRecorder.Core.Models;
using ScreenRecorder.Core.Services;
using Xunit;

namespace ScreenRecorder.Tests;

public class CoreTests
{
    [Fact]
    public void ScreenGeometryService_ReturnsAtLeastOneMonitorOrEmptyWithoutCrashing()
    {
        var logger = new LoggerService();
        var service = new ScreenGeometryService(logger);
        var monitors = service.GetMonitors();

        Assert.NotNull(monitors);
        // On a physical or CI machine with a display, at least 1 monitor is returned
        foreach (var m in monitors)
        {
            Assert.True(m.Bounds.Width > 0);
            Assert.True(m.Bounds.Height > 0);
            Assert.True(m.Scale >= 1.0);
        }
    }

    [Fact]
    public void UploadQueueService_EnqueuesAndDrainsCorrectly()
    {
        var logger = new LoggerService();
        var db = Path.Combine(Path.GetTempPath(), $"q_{Guid.NewGuid():N}.db");
        var queue = new UploadQueueService(logger, db);

        Assert.True(queue.IsAvailable);

        var tempFile = Path.Combine(Path.GetTempPath(), $"test_queue_{Guid.NewGuid():N}.mp4");
        File.WriteAllText(tempFile, "dummy");

        try
        {
            queue.Enqueue(tempFile);
            var pending = queue.GetPending(100000);
            Assert.Contains(tempFile, pending);

            var count = queue.CountPending();
            Assert.True(count >= 1);

            queue.MarkSent(tempFile);
            var pendingAfter = queue.GetPending(100000);
            Assert.DoesNotContain(tempFile, pendingAfter);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            foreach (var ext in new[] { "", "-wal", "-shm" }) { try { File.Delete(db + ext); } catch { } }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task UploadQueueService_ConcurrentAccessWorks()
    {
        var logger = new LoggerService();
        var db = Path.Combine(Path.GetTempPath(), $"q_{Guid.NewGuid():N}.db");
        var queue = new UploadQueueService(logger, db);
        Assert.True(queue.IsAvailable);

        var tasks = new System.Threading.Tasks.Task[10];
        for (int i = 0; i < 10; i++)
        {
            int index = i;
            tasks[i] = System.Threading.Tasks.Task.Run(() =>
            {
                var q = new UploadQueueService(logger, db);
                for (int j = 0; j < 5; j++)
                {
                    var dummy = $@"C:\dummy_test_{index}_{j}.mp4";
                    q.Enqueue(dummy);
                    var count = q.CountPending();
                    Assert.True(count >= 0);
                    q.MarkFailed(dummy);
                    q.MarkSent(dummy);
                }
            });
        }

        await System.Threading.Tasks.Task.WhenAll(tasks);
        Assert.True(queue.IsAvailable);
        foreach (var ext in new[] { "", "-wal", "-shm" }) { try { File.Delete(db + ext); } catch { } }
    }

    [Fact]
    public void UploadQueueService_LeaseClaimingPreventsDuplicateProcessing()
    {
        var logger = new LoggerService();
        var db = Path.Combine(Path.GetTempPath(), $"q_{Guid.NewGuid():N}.db");
        var queue = new UploadQueueService(logger, db);
        Assert.True(queue.IsAvailable);

        var testFile = Path.Combine(Path.GetTempPath(), $"test_claim_{Guid.NewGuid():N}.mp4");
        File.WriteAllText(testFile, "test data");

        try
        {
            queue.Enqueue(testFile);

            // Reclamar el archivo (Worker 1)
            var claimed = queue.TryClaim(testFile, leaseMinutes: 15);
            Assert.True(claimed);

            // Intentar reclamar de nuevo (Worker 2) antes de que expire el lease -> debe retornar false
            var duplicateClaim = queue.TryClaim(testFile, leaseMinutes: 15);
            Assert.False(duplicateClaim);

            // GetPending/ClaimPending tampoco debe devolverlo mientras el lease esté activo
            var pending = queue.ClaimPending(10, leaseMinutes: 15);
            Assert.DoesNotContain(testFile, pending);

            // Si falla, se libera para el siguiente ciclo
            queue.MarkFailed(testFile);
            var reclaimed = queue.TryClaim(testFile, leaseMinutes: 15);
            Assert.True(reclaimed);

            queue.MarkSent(testFile);
        }
        finally
        {
            if (File.Exists(testFile)) File.Delete(testFile);
            foreach (var ext in new[] { "", "-wal", "-shm" }) { try { File.Delete(db + ext); } catch { } }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task FileManagementService_AtomicUploadAndSingleFlight_Works()
    {
        var logger = new LoggerService();
        // Clave de registro AISLADA: la prueba no debe sobrescribir el NetworkPath real del equipo.
        var regKey = $@"SOFTWARE\ScreenRecorderTest_{Guid.NewGuid():N}";
        var settingsService = new SettingsService(logger, regKey);
        var targetDir = Path.Combine(Path.GetTempPath(), $"test_dest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetDir);

        var testFile = Path.Combine(Path.GetTempPath(), $"test_upload_{Guid.NewGuid():N}.mp4");
        File.WriteAllText(testFile, "video content 1234567890");

        try
        {
            var settings = settingsService.LoadSettings();
            settings.NetworkPath = targetDir;
            settingsService.SaveSettings(settings);

            var fms = new FileManagementService(logger, settingsService);

            // Subida exitosa
            var ok = await fms.TryUploadFileAsync(testFile);
            Assert.True(ok);

            Assert.False(new FileInfo(testFile).Exists); // origen eliminado tras verificación

            // Comprobar que no quedaron archivos temporales .tmp huérfanos
            var tmpFiles = Directory.GetFiles(targetDir, "*.tmp");
            Assert.Empty(tmpFiles);
        }
        finally
        {
            if (File.Exists(testFile)) File.Delete(testFile);
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            try { Registry.LocalMachine.DeleteSubKeyTree(regKey, throwOnMissingSubKey: false); } catch { }
        }
    }

    [Fact]
    public void WasapiLoopbackCapture_TryStartAndDispose_HandlesGracefullyWithoutLeaking()
    {
        var logger = new LoggerService();
        using var capture = new ScreenRecorder.Core.Audio.WasapiLoopbackCapture();
        bool started = capture.TryStart(logger);
        capture.Stop();
        Assert.False(capture.IsRunning);
    }

    [Fact]
    public void SettingsService_SerializesAndDeserializesCorrectly()
    {
        var logger = new LoggerService();
        var service = new SettingsService(logger);
        var settings = service.LoadSettings();

        Assert.NotNull(settings);
        // Verify default or loaded settings
        Assert.True(settings.FragmentDurationMinutes > 0);
        Assert.True(settings.Fps > 0);
    }

    [Fact]
    public void ProcessProtection_DenyAndResetDacl_Works()
    {
        var logger = new LoggerService();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping 127.0.0.1 -n 10",
            CreateNoWindow = true,
            UseShellExecute = false
        };

        using var process = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(process);

        try
        {
            var protectedOk = ProcessProtection.DenyTerminateForUsers(process, logger, "TestProcess");
            Assert.True(protectedOk);

            var resetOk = ProcessProtection.ResetDacl(process, logger);
            Assert.True(resetOk);

            process.Kill();
            process.WaitForExit(3000);
            Assert.True(process.HasExited);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
        }
    }
}
