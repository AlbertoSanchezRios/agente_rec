using System;
using System.Drawing;
using System.Windows.Forms;
using ScreenRecorder.Core.Services;

namespace ScreenRecorder.UI.Forms;

/// <summary>
/// Formulario principal completamente oculto. No muestra ningún indicio visible
/// de que la grabación está en curso (sin icono en el área de notificación).
/// Además hospeda el servidor del pipe de control que ejecuta los comandos
/// perro (mostrar configuración), quieto (reiniciar grabación) y echate (detener todo).
/// El bucle de mensajes es obligatorio: WindowsEventMonitorService usa Microsoft.Win32.SystemEvents.
/// </summary>
public partial class MainForm : Form
{
    private readonly IRecordingManagerService _recordingManager;
    private readonly ISettingsService _settingsService;
    private readonly ILoggerService _logger;
    private readonly bool _showConfigOnStart;

    public MainForm(
        IRecordingManagerService recordingManager,
        ISettingsService settingsService,
        ILoggerService logger,
        bool showConfigOnStart)
    {
        _recordingManager = recordingManager;
        _settingsService = settingsService;
        _logger = logger;
        _showConfigOnStart = showConfigOnStart;

        InitializeComponent();
    }

    private void InitializeComponent()
    {
        WindowState = FormWindowState.Minimized;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        Size = new Size(0, 0);
        // NOTA: este formulario NUNCA se muestra. Program.cs usa ApplicationContext y solo fuerza
        // la creación del handle nativo (oculto) para mantener el bucle de mensajes de WinForms
        // (requerido por SystemEvents) sin dibujar ninguna ventana: cero flash, cero recuadro.
    }

    /// <summary>
    /// Lógica de arranque que antes vivía en Load. Se invoca desde Program DESPUÉS de crear el
    /// handle nativo (oculto) y antes de Application.Run: arranca el servidor del pipe y, solo si
    /// el modo lo pide, abre el diálogo de Ajustes.
    /// </summary>
    public void Start()
    {
        ControlPipe.StartServer(HandlePipeCommand, _logger);

        // Al ejecutar manualmente se muestra la configuración; al cerrarla todo queda oculto
        if (_showConfigOnStart)
        {
            ShowSettingsDialog();
        }
    }

    /// <summary>
    /// Ejecuta un comando recibido por el pipe. Se invoca desde un hilo de segundo plano:
    /// se marshala a la UI para mostrar ventanas.
    /// </summary>
    private void HandlePipeCommand(string command)
    {
        try
        {
            switch (command)
            {
                case ControlPipe.CommandShowConfig:
                    BeginInvoke(new Action(() =>
                    {
                        if (IsRunningAsAdministrator())
                        {
                            ShowSettingsDialog();
                        }
                        else
                        {
                            // La instancia corre sin permisos: abrir el diálogo elevado
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    UseShellExecute = true,
                                    FileName = Application.ExecutablePath,
                                    Arguments = "/settings",
                                    Verb = "runas"
                                });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError("No se pudo abrir la configuración elevada (¿UAC cancelado?)", ex);
                            }
                        }
                    }));
                    break;

                case ControlPipe.CommandRestart:
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            ControlPipe.ClearStopFlag(); // reanudación explícita
                            _logger.LogInfo("Reiniciando ciclo de grabación (comando 'quieto')");
                            _recordingManager.Stop();
                            _recordingManager.Start();
                            _logger.LogInfo("Ciclo de grabación reiniciado");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError("Error al reiniciar la grabación (comando 'quieto')", ex);
                        }
                    }));
                    break;

                case ControlPipe.CommandStopAll:
                    _logger.LogInfo("Deteniendo grabación y cerrando la aplicación (comando 'echate')");
                    BeginInvoke(new Action(() =>
                    {
                        // La bandera ANTES de detener: si el proceso muere durante la parada
                        // ordenada (crashea o lo matan en los hasta 90 s de cierre/subida),
                        // el watchdog ya ve la detención como voluntaria y NO lo revive.
                        ControlPipe.SetStopFlag();
                        try
                        {
                            _recordingManager.Stop();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError("Error al detener la grabación (comando 'echate')", ex);
                        }
                        Application.Exit();
                    }));
                    break;

                default:
                    _logger.LogWarning($"Comando desconocido recibido por pipe: {command}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error al procesar el comando '{command}'", ex);
        }
    }

    private void ShowSettingsDialog()
    {
        using var settingsForm = new SettingsForm(_settingsService, _logger, _recordingManager);
        settingsForm.ShowDialog(this);
    }

    private bool IsRunningAsAdministrator()
    {
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }
}
