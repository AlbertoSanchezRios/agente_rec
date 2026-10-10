using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Forms;
using ScreenRecorder.Core.Ipc;
using ScreenRecorder.Core.Services;
using ScreenRecorder.UI.Forms;

namespace ScreenRecorder.UI;

/// <summary>
/// WSearchCfg.exe: aplicación de configuración (WinForms) que se ejecuta on-demand y elevada.
/// Ya NO es el agente residente: el agente es WSearchAgent.exe (sin WinForms). Al guardar, envía
/// 'quieto' por el canal de control para que el agente aplique la nueva configuración en caliente.
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var logger = new LoggerService();

        if (!NetworkPathHelper.IsProcessElevated())
        {
            if (RelaunchElevated(logger))
            {
                return 0; // la instancia elevada muestra la ventana
            }

            MessageBox.Show(
                "Se requieren permisos de administrador para acceder a los ajustes.",
                "Permisos requeridos",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return 0;
        }

        logger.LogInfo("WSearchCfg iniciado (configuración)");

        var settingsService = new SettingsService(logger);
        using (var settingsForm = new SettingsForm(settingsService, logger))
        {
            if (settingsForm.ShowDialog() == DialogResult.OK)
            {
                TrySendRestart(logger);
            }
        }
        return 0;
    }

    /// <summary>Pide al agente en ejecución que reinicie el ciclo para aplicar la configuración.</summary>
    private static void TrySendRestart(ILoggerService logger)
    {
        try
        {
            var response = ControlClient.Send("restart", connectTimeoutMs: 2000);
            if (response is { Ok: true })
            {
                logger.LogInfo("Comando 'restart' enviado para aplicar la nueva configuración");
            }
            else
            {
                logger.LogInfo("No hay agente en ejecución; la configuración se aplicará en el próximo arranque");
            }
        }
        catch (Exception ex)
        {
            // No hay instancia corriendo: la configuración se aplicará en el próximo inicio
            logger.LogInfo($"No se pudo enviar 'restart' (¿sin instancia en ejecución?): {ex.Message}");
        }
    }

    private static bool RelaunchElevated(ILoggerService logger)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.CurrentDirectory,
                FileName = Environment.ProcessPath ?? "WSearchCfg.exe",
                Arguments = "/settings",
                Verb = "runas"
            };
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)
        {
            logger.LogInfo("El usuario canceló la elevación UAC (/settings)");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError("No se pudo relanzar la configuración con permisos de administrador", ex);
            return false;
        }
    }
}
