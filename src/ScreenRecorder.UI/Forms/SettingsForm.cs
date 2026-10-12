using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScreenRecorder.Core.Constants;
using ScreenRecorder.Core.Ipc;
using ScreenRecorder.Core.Models;
using ScreenRecorder.Core.Services;
using ScreenRecorder.UI.UI;

namespace ScreenRecorder.UI.Forms;

/// <summary>
/// Ventana de configuración (WSearchCfg). Rectángulo con pestañas y barra inferior fija.
/// No usa <c>IRecordingManagerService</c>: el control de la grabación (reanudar/pausar) y el
/// estado se consultan al agente en ejecución por el Named Pipe de control.
/// </summary>
public partial class SettingsForm : Form
{
    /// <summary>Se dispara después de guardar la configuración exitosamente.</summary>
    public event EventHandler? SettingsSaved;

    private readonly ISettingsService _settingsService;
    private readonly ILoggerService _logger;
    private readonly IAudioDetectionService _audioDetectionService;

    private RecordingSettings _currentSettings = null!;

    // ---- Video ----
    private ComboBox _resolutionComboBox = null!;
    private Label _screenInfoLabel = null!;
    private NumericUpDown _fpsNumeric = null!;
    private ComboBox _captureTargetComboBox = null!;
    private ComboBox _captureModeComboBox = null!;
    private CheckBox _recordOnlyOnChangesCheckBox = null!;
    private NumericUpDown _fragmentDurationNumeric = null!;
    private Label _fragmentDurationHintLabel = null!;

    // ---- Audio ----
    private CheckBox _includeSystemAudioCheckBox = null!;
    private CheckBox _includeMicrophoneCheckBox = null!;
    private CheckBox _autoDetectAudioCheckBox = null!;
    private ComboBox _systemAudioComboBox = null!;
    private ComboBox _microphoneComboBox = null!;
    private Button _detectDevicesButton = null!;
    private Label _audioResultLabel = null!;

    // ---- Almacenamiento ----
    private TextBox _networkPathTextBox = null!;
    private Button _browseNetworkPathButton = null!;
    private Button _testConnectionButton = null!;
    private Label _networkResultLabel = null!;

    // ---- Nombre de archivo ----
    private RadioButton _rbUserMachine = null!;
    private RadioButton _rbUser = null!;
    private RadioButton _rbMachine = null!;
    private RadioButton _rbCustom = null!;
    private TextBox _customFilenamePrefixTextBox = null!;

    // ---- Footer / control ----
    private Label _statusLabel = null!;
    private Button _resumeButton = null!;
    private Button _pauseButton = null!;
    private Button _saveButton = null!;
    private Button _cancelButton = null!;

    public SettingsForm(ISettingsService settingsService, ILoggerService logger)
    {
        _settingsService = settingsService;
        _logger = logger;
        _audioDetectionService = new AudioDetectionService(logger);

        if (!IsRunningAsAdministrator())
        {
            MessageBox.Show(
                "Se requieren permisos de administrador para acceder a los ajustes.\n\nEste formulario se cerrará.",
                "Permisos requeridos",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            DialogResult = DialogResult.Cancel;
            Load += (s, e) => Close();
            return;
        }

        InitializeComponent();
        LoadSettings();
        UpdateUI();
        Shown += SettingsForm_Shown;
    }

    // ============================================================ UI

    private void SettingsForm_Shown(object? sender, EventArgs e)
    {
        Shown -= SettingsForm_Shown;
        LoadAudioDevices();
        if (_autoDetectAudioCheckBox.Checked)
            SelectPreferredDevicesInCombos();
        UpdateAudioControls();
        RefreshStatus();
    }

    private void InitializeComponent()
    {
        Text = "Screen Recorder · Configuración";
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = Theme.Body;
        BackColor = Theme.Background;
        ClientSize = new Size(900, 588);
        MinimumSize = new Size(900, 588);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Background,
            Margin = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildTabs(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);

        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Header, Margin = new Padding(0) };
        header.Controls.Add(new Label
        {
            Text = "Configuración de Screen Recorder",
            Font = Theme.Title,
            ForeColor = Theme.HeaderText,
            AutoSize = true,
            Location = new Point(18, 10)
        });
        header.Controls.Add(new Label
        {
            Text = $"v{AppConstants.AppVersion} · Grabación de pantalla y audio para auditoría interna",
            Font = Theme.Small,
            ForeColor = Theme.HeaderSubtle,
            AutoSize = true,
            Location = new Point(20, 38)
        });
        return header;
    }

    private TabControl BuildTabs()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = Theme.TabHeader,
            Padding = new Point(16, 6),
            Margin = new Padding(0)
        };
        tabs.TabPages.Add(BuildVideoTab());
        tabs.TabPages.Add(BuildAudioTab());
        tabs.TabPages.Add(BuildStorageTab());
        tabs.TabPages.Add(BuildNamesTab());
        return tabs;
    }

    private static TabPage NewTab(string title)
    {
        var page = new TabPage(title) { BackColor = Theme.Background, UseVisualStyleBackColor = false };
        return page;
    }

    private static TableLayoutPanel NewGrid()
    {
        var g = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(16, 14, 16, 14),
            BackColor = Theme.Background
        };
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return g;
    }

    private static void Field(TableLayoutPanel g, string label, Control editor)
    {
        var row = g.RowCount++;
        var lbl = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = Theme.Body,
            ForeColor = Theme.TextPrimary,
            Margin = new Padding(3, 8, 3, 4)
        };
        editor.Dock = DockStyle.Fill;
        editor.Margin = new Padding(3, 5, 3, 4);
        g.Controls.Add(lbl, 0, row);
        g.Controls.Add(editor, 1, row);
    }

    private static void Span(TableLayoutPanel g, Control c, int topPad = 4)
    {
        var row = g.RowCount++;
        c.Margin = new Padding(3, topPad, 3, 4);
        g.Controls.Add(c, 0, row);
        g.SetColumnSpan(c, 2);
    }

    private static Label HintLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(830, 0),
        Font = Theme.Small,
        ForeColor = Theme.TextMuted,
        Margin = new Padding(6, 0, 3, 8)
    };

    private static FlowLayoutPanel LeftBar(params Control[] controls)
    {
        var p = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        p.Controls.AddRange(controls);
        return p;
    }

    private static Panel MinutesRow(NumericUpDown numeric)
    {
        var p = new Panel { AutoSize = true, BackColor = Color.Transparent };
        numeric.Width = 80;
        numeric.Location = new Point(0, 0);
        var lbl = new Label
        {
            Text = "minutos",
            AutoSize = true,
            ForeColor = Theme.TextPrimary,
            Location = new Point(88, 3)
        };
        p.Controls.Add(numeric);
        p.Controls.Add(lbl);
        return p;
    }

    private TabPage BuildVideoTab()
    {
        var page = NewTab("Video");
        var g = NewGrid();

        _resolutionComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _resolutionComboBox.Items.AddRange(new object[]
        {
            "Nativa de la pantalla (recomendada)", "480p", "720p", "1080p", "1440p", "2160p (4K)"
        });
        Field(g, "Resolución de grabación:", _resolutionComboBox);

        _screenInfoLabel = HintLabel(string.Empty);
        Span(g, _screenInfoLabel, 0);

        _fpsNumeric = new NumericUpDown { Minimum = 10, Maximum = 30, Increment = 5, Value = 15 };
        Field(g, "FPS de grabación:", _fpsNumeric);

        _captureTargetComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _captureTargetComboBox.Items.AddRange(new object[]
        {
            "Monitor primario", "Monitor secundario", "Ambos monitores (escritorio completo)"
        });
        _captureTargetComboBox.SelectedIndex = 0;
        _captureTargetComboBox.SelectedIndexChanged += (s, e) => UpdateScreenInfoLabel();
        Field(g, "Monitor a grabar:", _captureTargetComboBox);

        _captureModeComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _captureModeComboBox.Items.AddRange(new object[]
        {
            "Automática (recomendada: GPU hardware Intel/NVIDIA/AMD)",
            "GPU (ddagrab: Intel QSV / NVIDIA NVENC / AMD AMF)",
            "GDI (compatibilidad; mayor uso de CPU)"
        });
        Field(g, "Modo de captura:", _captureModeComboBox);

        _recordOnlyOnChangesCheckBox = new CheckBox
        {
            Text = "Grabar sólo cuando la pantalla cambie (ahorra CPU; timestamps variables)",
            AutoSize = true,
            ForeColor = Theme.TextPrimary
        };
        Span(g, _recordOnlyOnChangesCheckBox);

        _fragmentDurationNumeric = new NumericUpDown { Minimum = 1, Maximum = 120, Increment = 5, Value = 30 };
        _fragmentDurationNumeric.ValueChanged += FragmentDurationNumeric_ValueChanged;
        Field(g, "Duración de cada fragmento:", MinutesRow(_fragmentDurationNumeric));

        _fragmentDurationHintLabel = HintLabel(string.Empty);
        Span(g, _fragmentDurationHintLabel, 0);

        page.Controls.Add(g);
        return page;
    }

    private TabPage BuildAudioTab()
    {
        var page = NewTab("Audio");
        var g = NewGrid();

        _includeSystemAudioCheckBox = new CheckBox { Text = "Incluir audio del sistema", AutoSize = true, Checked = true };
        _includeSystemAudioCheckBox.CheckedChanged += (s, e) => UpdateAudioControls();
        Span(g, _includeSystemAudioCheckBox);

        _includeMicrophoneCheckBox = new CheckBox { Text = "Incluir micrófono", AutoSize = true, Checked = true };
        _includeMicrophoneCheckBox.CheckedChanged += (s, e) => UpdateAudioControls();
        Span(g, _includeMicrophoneCheckBox);

        _autoDetectAudioCheckBox = new CheckBox { Text = "Detección automática de dispositivos", AutoSize = true, Checked = true };
        _autoDetectAudioCheckBox.CheckedChanged += AutoDetectAudioCheckBox_CheckedChanged;
        Span(g, _autoDetectAudioCheckBox, 10);

        _systemAudioComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
        Field(g, "Dispositivo del sistema:", _systemAudioComboBox);

        _microphoneComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
        Field(g, "Micrófono:", _microphoneComboBox);

        _detectDevicesButton = Theme.SecondaryButton("Detectar dispositivos", 170);
        _detectDevicesButton.Click += DetectDevicesButton_Click;
        Span(g, LeftBar(_detectDevicesButton), 6);

        _audioResultLabel = HintLabel(string.Empty);
        Span(g, _audioResultLabel, 0);

        Span(g, HintLabel(
            "El audio del sistema se captura por WASAPI (loopback) y no requiere \"Mezcla estéreo\"; " +
            "el respaldo dshow \"Mezcla estéreo\" sólo se usa si WASAPI no está disponible."), 4);

        page.Controls.Add(g);
        return page;
    }

    private TabPage BuildStorageTab()
    {
        var page = NewTab("Almacenamiento");
        var g = NewGrid();

        _networkPathTextBox = new TextBox();
        Field(g, "Ruta de red compartida (NUC):", _networkPathTextBox);

        _browseNetworkPathButton = Theme.SecondaryButton("Examinar…", 110);
        _browseNetworkPathButton.Click += BrowseNetworkPathButton_Click;
        _testConnectionButton = Theme.SecondaryButton("Probar", 110);
        _testConnectionButton.Click += TestConnectionButton_Click;
        Span(g, LeftBar(_browseNetworkPathButton, _testConnectionButton), 6);

        _networkResultLabel = HintLabel(string.Empty);
        Span(g, _networkResultLabel, 0);

        Span(g, HintLabel(
            "Recomendado: ruta UNC (\\\\servidor\\carpeta). Si queda vacía, los videos se guardan " +
            "localmente y se suben solos al configurarla."), 4);

        page.Controls.Add(g);
        return page;
    }

    private TabPage BuildNamesTab()
    {
        var page = NewTab("Nombres");
        var g = NewGrid();

        Span(g, Theme.SectionLabel("Nombre de los archivos"), 8);

        _rbUserMachine = new RadioButton { Text = "Usuario y equipo", AutoSize = true, Checked = true, ForeColor = Theme.TextPrimary };
        _rbUser = new RadioButton { Text = "Sólo usuario", AutoSize = true, ForeColor = Theme.TextPrimary };
        _rbMachine = new RadioButton { Text = "Sólo equipo", AutoSize = true, ForeColor = Theme.TextPrimary };
        _rbCustom = new RadioButton { Text = "Personalizado (prefijo)", AutoSize = true, ForeColor = Theme.TextPrimary };
        _rbCustom.CheckedChanged += (s, e) => _customFilenamePrefixTextBox.Enabled = _rbCustom.Checked;
        Span(g, _rbUserMachine, 2);
        Span(g, _rbUser, 2);
        Span(g, _rbMachine, 2);
        Span(g, _rbCustom, 2);

        _customFilenamePrefixTextBox = new TextBox { Enabled = false };
        Field(g, "Prefijo personalizado:", _customFilenamePrefixTextBox);

        Span(g, HintLabel("A cualquier modo se le agrega automáticamente la fecha y hora."), 4);

        page.Controls.Add(g);
        return page;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(16, 10, 16, 10),
            Margin = new Padding(0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Body,
            ForeColor = Theme.TextMuted,
            Text = "Consultando estado…"
        };

        _resumeButton = Theme.GhostButton("Reanudar", Theme.Success);
        _resumeButton.Click += ResumeButton_Click;
        _pauseButton = Theme.GhostButton("Detener", Theme.Danger);
        _pauseButton.Click += PauseButton_Click;

        _cancelButton = Theme.SecondaryButton("Cancelar", 112);
        _cancelButton.DialogResult = DialogResult.Cancel;
        _saveButton = Theme.PrimaryButton("Guardar");
        _saveButton.Click += SaveButton_Click;

        // RightToLeft: el primero añadido queda más a la derecha.
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        _cancelButton.Margin = new Padding(18, 0, 8, 0);
        _resumeButton.Margin = new Padding(8, 0, 0, 0);
        _pauseButton.Margin = new Padding(8, 0, 0, 0);
        _saveButton.Margin = new Padding(0);
        buttons.Controls.Add(_saveButton);
        buttons.Controls.Add(_cancelButton);
        buttons.Controls.Add(_pauseButton);
        buttons.Controls.Add(_resumeButton);

        footer.Controls.Add(_statusLabel, 0, 0);
        footer.Controls.Add(buttons, 1, 0);
        return footer;
    }

    // ============================================================ Control (IPC)

    /// <summary>Envía un comando al agente en ejecución. Devuelve null si no hay instancia.</summary>
    private ControlResponse? SendControl(string cmd)
    {
        try
        {
            return ControlClient.Send(cmd, connectTimeoutMs: 1500);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Comando de control '{cmd}' falló: {ex.Message}");
            return null;
        }
    }

    /// <summary>Consulta el estado real de la grabación y ajusta el footer.</summary>
    private void RefreshStatus()
    {
        var response = SendControl("status");
        if (response is not { Ok: true, Data: { } data })
        {
            _statusLabel.Text = "Sin agente en ejecución";
            _statusLabel.ForeColor = Theme.TextMuted;
            _resumeButton.Enabled = false;
            _pauseButton.Enabled = false;
            return;
        }

        var recording = data.TryGetProperty("grabando", out var g) && g.ValueKind == JsonValueKind.True;
        var pending = data.TryGetProperty("pendientesEnvio", out var p) && p.TryGetInt32(out var n) ? n : 0;

        _statusLabel.Text = $"{(recording ? "● Grabando" : "○ Detenida")}   ·   Envíos pendientes: {pending}";
        _statusLabel.ForeColor = recording ? Theme.Success : Theme.TextMuted;
        _resumeButton.Enabled = !recording;
        _pauseButton.Enabled = recording;
    }

    private void ResumeButton_Click(object? sender, EventArgs e)
    {
        var r = SendControl("resume");
        if (r is not { Ok: true })
        {
            MessageBox.Show(
                "No hay una instancia de Screen Recorder en ejecución a la que reanudar la grabación.",
                "Reanudar grabación", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshStatus();
            return;
        }
        _logger.LogInfo("Reanudar grabación solicitado desde Ajustes");
        RefreshStatus();
    }

    private void PauseButton_Click(object? sender, EventArgs e)
    {
        var r = SendControl("pause");
        if (r is not { Ok: true })
        {
            MessageBox.Show(
                "No hay una instancia de Screen Recorder en ejecución a la que detener la grabación.",
                "Detener grabación", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshStatus();
            return;
        }
        _logger.LogInfo("Detener (pausa) de grabación solicitado desde Ajustes");
        RefreshStatus();
    }

    // ============================================================ Video

    private void FragmentDurationNumeric_ValueChanged(object? sender, EventArgs e)
    {
        _fragmentDurationHintLabel.Text =
            $"Cada {(int)_fragmentDurationNumeric.Value} min se cierra el video y se sube a la carpeta indicada.";
    }

    private static readonly int[] ResolutionValues = { 0, 480, 720, 1080, 1440, 2160 };

    private void SetResolutionCombo(int value)
    {
        var index = Array.IndexOf(ResolutionValues, value);
        _resolutionComboBox.SelectedIndex = index >= 0 ? index : 0;
    }

    private int ResolutionFromCombo()
    {
        var index = _resolutionComboBox.SelectedIndex;
        return index >= 0 && index < ResolutionValues.Length ? ResolutionValues[index] : 0;
    }

    private void UpdateScreenInfoLabel()
    {
        try
        {
            var geometry = new ScreenGeometryService(_logger);
            var monitors = geometry.GetMonitors();
            var target = (CaptureTarget)Math.Clamp(_captureTargetComboBox.SelectedIndex, 0, 2);

            if (target == CaptureTarget.All)
            {
                var region = geometry.GetCaptureRegion(target);
                _screenInfoLabel.Text = $"Escritorio completo detectado: {region.Width}×{region.Height} píxeles físicos " +
                                        $"({monitors.Count} monitor(es)). Se graba todo junto.";
            }
            else
            {
                var m = target == CaptureTarget.Primary
                    ? monitors.FirstOrDefault(x => x.IsPrimary)
                    : monitors.FirstOrDefault(x => !x.IsPrimary) ?? monitors.FirstOrDefault(x => x.IsPrimary);
                _screenInfoLabel.Text = m != null
                    ? $"Pantalla detectada: {m.PhysicalBounds.Width}×{m.PhysicalBounds.Height} píxeles físicos " +
                      $"(escala {Math.Round(m.Scale * 100)}%). Sin reescalado con 'Nativa'."
                    : "No se detectaron monitores.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"No se pudo calcular la información de pantalla: {ex.Message}");
        }
    }

    // ============================================================ Audio

    private class DeviceComboItem
    {
        public string DisplayText { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string? AlternativeName { get; set; }
    }

    private void LoadAudioDevices()
    {
        _systemAudioComboBox.Items.Clear();
        _microphoneComboBox.Items.Clear();
        try
        {
            foreach (var device in _audioDetectionService.DetectSystemAudioDevices())
            {
                var display = device.Name + (string.IsNullOrEmpty(device.AlternativeName) ? string.Empty : " ✓");
                _systemAudioComboBox.Items.Add(new DeviceComboItem
                {
                    DisplayText = display,
                    DeviceName = device.Name,
                    AlternativeName = device.AlternativeName
                });
            }

            foreach (var device in _audioDetectionService.DetectMicrophoneDevices())
            {
                var display = device.Name + (string.IsNullOrEmpty(device.AlternativeName) ? string.Empty : " ✓");
                _microphoneComboBox.Items.Add(new DeviceComboItem
                {
                    DisplayText = display,
                    DeviceName = device.Name,
                    AlternativeName = device.AlternativeName
                });
            }

            _systemAudioComboBox.DisplayMember = "DisplayText";
            _microphoneComboBox.DisplayMember = "DisplayText";

            if (!string.IsNullOrEmpty(_currentSettings.SystemAudioDevice))
            {
                var item = _systemAudioComboBox.Items.Cast<DeviceComboItem>()
                    .FirstOrDefault(i => i.DeviceName == _currentSettings.SystemAudioDevice);
                if (item != null) _systemAudioComboBox.SelectedItem = item;
            }
            if (!string.IsNullOrEmpty(_currentSettings.MicrophoneDevice))
            {
                var item = _microphoneComboBox.Items.Cast<DeviceComboItem>()
                    .FirstOrDefault(i => i.DeviceName == _currentSettings.MicrophoneDevice);
                if (item != null) _microphoneComboBox.SelectedItem = item;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al cargar dispositivos de audio", ex);
            _audioResultLabel.Text = "No se pudieron detectar dispositivos de audio.";
        }
    }

    private void DetectDevicesButton_Click(object? sender, EventArgs e)
    {
        try
        {
            _detectDevicesButton.Enabled = false;
            _detectDevicesButton.Text = "Detectando…";
            Application.DoEvents();

            LoadAudioDevices();
            SelectPreferredDevicesInCombos();
            UpdateAudioControls();

            var system = _systemAudioComboBox.Items.Count;
            var mic = _microphoneComboBox.Items.Count;
            _audioResultLabel.Text = $"Detección completada: {system} dispositivo(s) de sistema, {mic} micrófono(s).";
            _logger.LogInfo($"Detección manual de audio: {system} sistema, {mic} micrófonos");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error en detección manual de dispositivos", ex);
            _audioResultLabel.Text = $"Error al detectar dispositivos: {ex.Message}";
        }
        finally
        {
            _detectDevicesButton.Enabled = true;
            _detectDevicesButton.Text = "Detectar dispositivos";
        }
    }

    private void SelectPreferredDevicesInCombos()
    {
        try
        {
            var preferredSystem = _audioDetectionService.GetPreferredSystemAudioDevice();
            if (preferredSystem != null)
            {
                var item = _systemAudioComboBox.Items.Cast<DeviceComboItem>()
                    .FirstOrDefault(i => i.DeviceName == preferredSystem.Name);
                if (item != null) _systemAudioComboBox.SelectedItem = item;
            }

            var preferredMic = _audioDetectionService.GetPreferredMicrophoneDevice();
            if (preferredMic != null)
            {
                var item = _microphoneComboBox.Items.Cast<DeviceComboItem>()
                    .FirstOrDefault(i => i.DeviceName == preferredMic.Name);
                if (item != null) _microphoneComboBox.SelectedItem = item;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al seleccionar dispositivos preferidos en combos", ex);
        }
    }

    private void UpdateAudioControls()
    {
        // La selección manual aplica cuando la detección automática está desactivada.
        var manual = !_autoDetectAudioCheckBox.Checked;
        _systemAudioComboBox.Enabled = manual && _includeSystemAudioCheckBox.Checked;
        _microphoneComboBox.Enabled = manual && _includeMicrophoneCheckBox.Checked;
    }

    private void AutoDetectAudioCheckBox_CheckedChanged(object? sender, EventArgs e)
    {
        if (_autoDetectAudioCheckBox.Checked)
        {
            LoadAudioDevices();
            SelectPreferredDevicesInCombos();
        }
        UpdateAudioControls();
    }

    // ============================================================ Almacenamiento

    private void BrowseNetworkPathButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Seleccione la carpeta compartida en red (NUC)",
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() == DialogResult.OK)
            _networkPathTextBox.Text = dialog.SelectedPath;
    }

    /// <summary>
    /// Prueba integral de la ruta NUC: accesibilidad (+resolución a UNC), permiso de
    /// escritura real y subida real por el mismo transporte que usan los videos.
    /// </summary>
    private async void TestConnectionButton_Click(object? sender, EventArgs e)
    {
        var enteredPath = _networkPathTextBox.Text.Trim();
        if (string.IsNullOrEmpty(enteredPath))
        {
            MessageBox.Show("Escriba primero la ruta de red que desea probar.",
                "Probar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var elevated = NetworkPathHelper.IsProcessElevated();
        var path = enteredPath;
        string? resolvedUnc = null;

        var accessible = Directory.Exists(path);
        if (!accessible && NetworkPathHelper.IsDriveLetterPath(path))
        {
            resolvedUnc = NetworkPathHelper.TryResolveToUnc(path);
            if (resolvedUnc != null && Directory.Exists(resolvedUnc))
            {
                accessible = true;
                path = resolvedUnc;
            }
        }

        if (!accessible)
        {
            var diag = NetworkPathHelper.BuildInaccessibleDiagnostics(enteredPath);
            _logger.LogWarning($"Prueba de ruta fallida. {diag}");
            _networkResultLabel.Text = "Ruta no accesible.";
            var message = "No se pudo acceder a la ruta. Diagnóstico:\n\n" + diag + "\n\n";
            message += elevated
                ? "Esta ventana corre COMO ADMINISTRADOR y Windows no le permite ver las unidades de red " +
                  "mapeadas del usuario. Use la ruta UNC directa (\\\\servidor\\carpeta)."
                : "Verifique que la ruta existe y que este equipo tiene acceso de red a ella.";
            MessageBox.Show(message, "Probar", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Permiso de escritura.
        var writeTest = Path.Combine(path, $"__screenrecorder_test_{DateTime.Now:yyyyMMdd_HHmmss}.tmp");
        try
        {
            File.WriteAllText(writeTest, "test");
            File.Delete(writeTest);
        }
        catch (Exception ex)
        {
            _logger.LogError($"La ruta '{path}' se abre pero no permite escritura", ex);
            _networkResultLabel.Text = "La ruta existe pero no permite escritura.";
            MessageBox.Show(
                $"La ruta '{path}' se abre pero NO permite escritura (se requiere para subir los videos).\n\n" +
                $"Error: {ex.Message}",
                "Probar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Subida real (mismo transporte que los videos).
        var testFile = Path.Combine(Path.GetTempPath(), $"__envio_prueba_{Environment.MachineName}.txt");
        try
        {
            _testConnectionButton.Enabled = false;
            File.WriteAllText(testFile,
                $"Prueba de envío de Screen Recorder {Environment.MachineName} {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");

            var fileManagement = new FileManagementService(_logger, _settingsService);
            // Se prueba la ruta RESUMIDA aquí (la escrita/UNC), no la guardada: así Probar
            // funciona antes de pulsar Guardar.
            var ok = await fileManagement.TryUploadFileAsync(testFile, path, maxRetries: 1);

            if (ok)
            {
                var destino = fileManagement.LastUploadDestination
                              ?? Path.Combine(path, Path.GetFileName(testFile));
                _networkResultLabel.Text = "Correcto: ruta accesible, con escritura y subida verificada.";
                var note = resolvedUnc != null ? $"\n\n(La unidad no es visible aquí; se usará {resolvedUnc})" : string.Empty;
                MessageBox.Show(
                    "Prueba correcta.\n\nEl archivo de prueba llegó a:\n" + destino + note +
                    "\n\nLos videos subirán a este mismo camino.\n\n" +
                    "Recuerde pulsar GUARDAR para aplicarlo.",
                    "Probar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                _networkResultLabel.Text = "La ruta permite escritura, pero la subida de prueba falló.";
                MessageBox.Show(
                    "El envío de prueba falló. Revise el log:\nC:\\ProgramData\\ScreenRecorder\\Logs\\\n\n" +
                    "Causas habituales: permisos de escritura en el recurso compartido o credenciales del share.",
                    "Probar", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error inesperado al probar la ruta/ envio", ex);
            _networkResultLabel.Text = $"Error en la prueba: {ex.Message}";
            MessageBox.Show($"Error inesperado al probar:\n\n{ex.Message}",
                "Probar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _testConnectionButton.Enabled = true;
            try { if (File.Exists(testFile)) File.Delete(testFile); } catch { /* no crítico */ }
        }
    }

    // ============================================================ Carga / Guardado

    private void LoadSettings() => _currentSettings = _settingsService.LoadSettings();

    private void UpdateUI()
    {
        SetResolutionCombo(_currentSettings.Resolution);
        _captureModeComboBox.SelectedIndex = Math.Clamp(_currentSettings.CaptureBackendPreference, 0, 2);
        _fpsNumeric.Value = Math.Clamp(_currentSettings.Fps, 10, 30);
        _fragmentDurationNumeric.Value = Math.Clamp(_currentSettings.FragmentDurationMinutes, 1, 120);
        _captureTargetComboBox.SelectedIndex = Math.Clamp((int)_currentSettings.CaptureTarget, 0, 2);
        _recordOnlyOnChangesCheckBox.Checked = _currentSettings.RecordOnlyOnChanges;
        _includeSystemAudioCheckBox.Checked = _currentSettings.IncludeSystemAudio;
        _includeMicrophoneCheckBox.Checked = _currentSettings.IncludeMicrophone;
        _autoDetectAudioCheckBox.Checked = _currentSettings.AutoDetectAudio;
        _networkPathTextBox.Text = _currentSettings.NetworkPath ?? string.Empty;

        // Nombre de archivo: seleccionar el radio correspondiente (por defecto, usuario y equipo).
        if (_currentSettings.UseCustomFilename) _rbCustom.Checked = true;
        else if (_currentSettings.UseUsernameInFilename) _rbUser.Checked = true;
        else if (_currentSettings.UseMachineNameInFilename) _rbMachine.Checked = true;
        else _rbUserMachine.Checked = true;
        _customFilenamePrefixTextBox.Text = _currentSettings.CustomFilenamePrefix ?? string.Empty;
        _customFilenamePrefixTextBox.Enabled = _rbCustom.Checked;

        UpdateScreenInfoLabel();
        FragmentDurationNumeric_ValueChanged(null, EventArgs.Empty);
        LoadAudioDevices();
        UpdateAudioControls();
    }

    private void SaveButton_Click(object? sender, EventArgs e)
    {
        if (!IsRunningAsAdministrator())
        {
            MessageBox.Show("Se requieren permisos de administrador para guardar la configuración.",
                "Permisos requeridos", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            if (_rbCustom.Checked && string.IsNullOrWhiteSpace(_customFilenamePrefixTextBox.Text))
            {
                MessageBox.Show("Debe especificar un prefijo para el nombre personalizado.",
                    "Validación", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var networkPath = string.IsNullOrWhiteSpace(_networkPathTextBox.Text)
                ? null
                : _networkPathTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(networkPath))
            {
                var check = ValidateNetworkPath(networkPath);
                networkPath = check.NormalizedPath;
                _networkPathTextBox.Text = networkPath;
                if (check.WarningMessage != null)
                {
                    MessageBox.Show(check.WarningMessage, "Ruta de red",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            _currentSettings.Resolution = ResolutionFromCombo();
            _currentSettings.CaptureBackendPreference = _captureModeComboBox.SelectedIndex;
            _currentSettings.Fps = (int)_fpsNumeric.Value;
            _currentSettings.FragmentDurationMinutes = (int)_fragmentDurationNumeric.Value;
            _currentSettings.CaptureTarget = (CaptureTarget)_captureTargetComboBox.SelectedIndex;
            _currentSettings.RecordOnlyOnChanges = _recordOnlyOnChangesCheckBox.Checked;
            _currentSettings.AutoDetectAudio = _autoDetectAudioCheckBox.Checked;
            _currentSettings.IncludeSystemAudio = _includeSystemAudioCheckBox.Checked;
            _currentSettings.IncludeMicrophone = _includeMicrophoneCheckBox.Checked;
            _currentSettings.ManualAudioSelection = !_autoDetectAudioCheckBox.Checked;
            _currentSettings.NetworkPath = networkPath;

            _currentSettings.UseUsernameAndMachineNameInFilename = _rbUserMachine.Checked;
            _currentSettings.UseUsernameInFilename = _rbUser.Checked;
            _currentSettings.UseMachineNameInFilename = _rbMachine.Checked;
            _currentSettings.UseCustomFilename = _rbCustom.Checked;
            _currentSettings.CustomFilenamePrefix = _rbCustom.Checked
                ? _customFilenamePrefixTextBox.Text.Trim()
                : null;

            // Los dispositivos manuales sólo se usan cuando la detección automática está desactivada.
            if (!_autoDetectAudioCheckBox.Checked)
            {
                _currentSettings.SystemAudioDevice = (_systemAudioComboBox.SelectedItem as DeviceComboItem) is { } s
                    ? (string.IsNullOrEmpty(s.AlternativeName) ? s.DeviceName : s.AlternativeName)
                    : null;
                _currentSettings.MicrophoneDevice = (_microphoneComboBox.SelectedItem as DeviceComboItem) is { } m
                    ? (string.IsNullOrEmpty(m.AlternativeName) ? m.DeviceName : m.AlternativeName)
                    : null;
            }
            else
            {
                _currentSettings.SystemAudioDevice = null;
                _currentSettings.MicrophoneDevice = null;
            }

            _settingsService.SaveSettings(_currentSettings);
            _logger.LogInfo("Configuración guardada exitosamente");

            SettingsSaved?.Invoke(this, EventArgs.Empty);

            // Reanudación explícita: quita la detención voluntaria ('echate') aunque no haya
            // instancia en ejecución (si no, el watchdog no relanzaría el agente).
            ControlPipe.ClearStopFlag();

            ShowSaveConfirmation();

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("No se tienen permisos de administrador para guardar la configuración.",
                "Error de permisos", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al guardar configuración", ex);
            MessageBox.Show($"Error al guardar la configuración: {ex.Message}",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private (string NormalizedPath, string? WarningMessage) ValidateNetworkPath(string path)
    {
        const string notAccessibleTemplate =
            "Se guardó la configuración, pero ahora mismo no se pudo confirmar la ruta:\n\n{0}\n\n" +
            "Los videos quedarán en la carpeta temporal y se subirán automáticamente cuando la ruta esté disponible.";

        if (NetworkPathHelper.IsDriveLetterPath(path))
        {
            var unc = NetworkPathHelper.TryResolveToUnc(path);
            if (unc != null && Directory.Exists(unc))
                return (unc, null);
            if (Directory.Exists(path))
                return (path,
                    "Se guardó la configuración. La ruta '" + path + "' es accesible, pero su nombre de red " +
                    "no se pudo confirmar desde esta ventana (los procesos administradores no ven las unidades " +
                    "mapeadas). Al grabar, la aplicación intentará también por el nombre de red si es necesario.");
            return (path, string.Format(notAccessibleTemplate, path));
        }

        return Directory.Exists(path)
            ? (path, null)
            : (path, string.Format(notAccessibleTemplate, path));
    }

    private void ShowSaveConfirmation()
    {
        try
        {
            var nucEmpty = string.IsNullOrWhiteSpace(_currentSettings.NetworkPath);
            MessageBox.Show(
                "Configuración guardada. Se aplicará al reiniciar el ciclo de grabación.\n\n" +
                (nucEmpty
                    ? "Aviso: no hay ruta NUC configurada; los videos se guardarán localmente hasta configurarla.\n\n"
                    : string.Empty) +
                "Revise el estado con 'Verificar Autoarranque.bat' o con Wctl -status.",
                "Screen Recorder",
                MessageBoxButtons.OK,
                nucEmpty ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch
        {
            // La confirmación nunca debe impedir cerrar el diálogo.
        }
    }

    private bool IsRunningAsAdministrator() => NetworkPathHelper.IsProcessElevated();
}
