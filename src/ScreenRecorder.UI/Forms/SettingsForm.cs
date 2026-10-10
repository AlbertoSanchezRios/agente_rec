using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScreenRecorder.Core.Constants;
using ScreenRecorder.Core.Ipc;
using ScreenRecorder.Core.Models;
using ScreenRecorder.Core.Services;

namespace ScreenRecorder.UI.Forms;

/// <summary>
/// Formulario de configuración
/// </summary>
public partial class SettingsForm : Form
{
    /// <summary>
    /// Se dispara después de guardar la configuración exitosamente.
    /// </summary>
    public event EventHandler? SettingsSaved;

    private readonly ISettingsService _settingsService;
    private readonly ILoggerService _logger;
    private readonly IAudioDetectionService _audioDetectionService;
    
    private RecordingSettings _currentSettings = null!;
    
    // Controles (inicializados en InitializeComponent)
    private ComboBox _resolutionComboBox = null!;
    private ComboBox _captureModeComboBox = null!;
    private Label _screenInfoLabel = null!;
    private Label? _fragmentDurationHintLabel;
    private NumericUpDown _fpsNumeric = null!;
    private NumericUpDown _fragmentDurationNumeric = null!;
    private ComboBox _captureTargetComboBox = null!;
    private CheckBox _recordOnlyOnChangesCheckBox = null!;
    private CheckBox _autoDetectAudioCheckBox = null!;
    private CheckBox _includeSystemAudioCheckBox = null!;
    private CheckBox _includeMicrophoneCheckBox = null!;
    private CheckBox _manualAudioSelectionCheckBox = null!;
    private ComboBox _systemAudioComboBox = null!;
    private ComboBox _microphoneComboBox = null!;
    private CheckBox _enableNotificationsCheckBox = null!;
    private TextBox _networkPathTextBox = null!;
    private Button _browseNetworkPathButton = null!;
    private Button _testNetworkPathButton = null!;
    private Button _testUploadButton = null!;
    private CheckBox _useUsernameInFilenameCheckBox = null!;
    private CheckBox _useMachineNameInFilenameCheckBox = null!;
    private CheckBox _useUsernameAndMachineNameInFilenameCheckBox = null!;
    private CheckBox _useCustomFilenameCheckBox = null!;
    private TextBox _customFilenamePrefixTextBox = null!;
    private Button _detectDevicesButton = null!;
    private Button _saveButton = null!;
    private Button _cancelButton = null!;
    private Button _startRecordingButton = null!;
    private Button _stopRecordingButton = null!;
    private IRecordingManagerService? _recordingManager;

    public SettingsForm(ISettingsService settingsService, ILoggerService logger, IRecordingManagerService? recordingManager = null)
    {
        _settingsService = settingsService;
        _logger = logger;
        _recordingManager = recordingManager;
        _audioDetectionService = new AudioDetectionService(logger);
        
        // Verificar permisos al abrir el formulario ANTES de inicializar
        if (!IsRunningAsAdministrator())
        {
            MessageBox.Show(
                "Se requieren permisos de administrador para acceder a los ajustes.\n\nEste formulario se cerrará.",
                "Permisos requeridos",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            // Cerrar el formulario si no hay permisos
            this.DialogResult = DialogResult.Cancel;
            this.Load += (s, e) => this.Close(); // Cerrar después de cargar
            return;
        }
        
        InitializeComponent();
        LoadSettings();
        UpdateUI();
        // Ejecutar detección al mostrar el formulario por si falló en el constructor (path, timing)
        this.Shown += SettingsForm_Shown;
    }

    private void SettingsForm_Shown(object? sender, EventArgs e)
    {
        this.Shown -= SettingsForm_Shown;
        LoadAudioDevices();
        if (_autoDetectAudioCheckBox.Checked)
            SelectPreferredDevicesInCombos();
        UpdateAudioControls();
        UpdateRecordingControlState();
    }

    private void UpdateRecordingControlState()
    {
        if (_recordingManager == null) return;
        var active = _recordingManager.IsRecordingActive;
        _startRecordingButton.Enabled = !active;
        _stopRecordingButton.Enabled = active;
    }

    private void StartRecordingButton_Click(object? sender, EventArgs e)
    {
        if (_recordingManager == null) return;
        try
        {
            _recordingManager.Start();
            _logger.LogInfo("Grabación reanudada desde Ajustes");
            UpdateRecordingControlState();
            MessageBox.Show(
                "La grabación se ha reanudado.",
                "Grabación reanudada",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al iniciar grabación desde Ajustes", ex);
            MessageBox.Show(
                $"No se pudo iniciar la grabación:\n\n{ex.Message}",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }

    /// <summary>
    /// Prueba el envío REAL por el mismo transporte que usan los videos: crea un archivo
    /// de prueba y lo sube con FileManagementService.TryUploadFileAsync. Muestra la ruta
    /// completa de destino (éxito) o el error exacto (fallo). Así se ve de inmediato si
    /// los videos están llegando y a dónde.
    /// </summary>
    private async void TestUploadButton_Click(object? sender, EventArgs e)
    {
        var settings = _settingsService.LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.NetworkPath))
        {
            MessageBox.Show(
                "No hay ruta NUC configurada. Escriba la ruta de red y pulse Guardar antes de probar el envío.",
                "Probar envío",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var testFile = Path.Combine(Path.GetTempPath(), $"__envio_prueba_{Environment.MachineName}.txt");
        try
        {
            File.WriteAllText(testFile,
                $"Prueba de envío de Screen Recorder {Environment.MachineName} {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");

            var fileManagement = new FileManagementService(_logger, _settingsService);
            var ok = await fileManagement.TryUploadFileAsync(testFile, maxRetries: 1);

            if (ok)
            {
                // Mostrar la ruta destino REAL resuelta (puede diferir de la configurada:
                // letra de unidad -> UNC). El usuario debe ver el archivo EXACTAMENTE ahí.
                var destino = fileManagement.LastUploadDestination
                              ?? Path.Combine(settings.NetworkPath.Trim(), Path.GetFileName(testFile));
                MessageBox.Show(
                    "ENVÍO CORRECTO.\n\nEl archivo de prueba llegó a:\n" + destino + "\n\n" +
                    "ABRA ESA RUTA en el Explorador y compruebe que el archivo está ahí.\n" +
                    "Los videos usan este mismo camino. Si esa ruta NO es el share esperado,\n" +
                    "la ruta configurada apunta a otra parte: corríjala (mejor en formato UNC:\n" +
                    "\\\\servidor\\carpeta) y vuelva a probar.",
                    "Probar envío",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    "EL ENVÍO FALLÓ.\n\nRevise el log para el detalle exacto:\n" +
                    "C:\\ProgramData\\ScreenRecorder\\Logs\\ (busque 'Intento' y 'Ruta de red').\n\n" +
                    "Causas habituales: ruta de red no accesible desde esta cuenta, sin permisos " +
                    "de escritura en el recurso compartido/NTFS, o credenciales del share.",
                    "Probar envío",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Error inesperado al probar el envío:\n\n{ex.Message}",
                "Probar envío",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            try { if (File.Exists(testFile)) File.Delete(testFile); } catch { /* no crítico */ }
        }
    }

    private async void StopRecordingButton_Click(object? sender, EventArgs e)
    {
        if (_recordingManager == null) return;
        if (!_recordingManager.IsRecordingActive)
        {
            MessageBox.Show(
                "No hay ninguna grabación activa en este momento.",
                "Información",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            return;
        }
        var result = MessageBox.Show(
            "¿Está seguro de que desea detener la grabación?",
            "Confirmar detención",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question
        );
        if (result != DialogResult.Yes) return;
        try
        {
            _logger.LogInfo("Deteniendo grabación desde Ajustes");
            _recordingManager.Stop();
            await Task.Delay(500);
            UpdateRecordingControlState();
            MessageBox.Show(
                "La grabación se ha detenido correctamente.",
                "Grabación detenida",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al detener grabación desde Ajustes", ex);
            MessageBox.Show(
                $"Error al detener la grabación:\n\n{ex.Message}",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }

    private void InitializeComponent()
    {
        Text = "Configuración - Screen Recorder";
        Size = new Size(660, 760);
        MinimumSize = new Size(660, 760);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 9);
        BackColor = Color.White;

        var accent = Color.FromArgb(0, 120, 215);

        // ---- Panel principal con scroll ----
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(16),
            BackColor = Color.White
        };

        int yPos = 10;

        // ---- Cabecera ----
        var titleLabel = new Label
        {
            Text = "Configuración de Screen Recorder",
            Location = new Point(16, yPos),
            AutoSize = true,
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            ForeColor = Color.FromArgb(32, 43, 68)
        };
        var subtitleLabel = new Label
        {
            Text = $"v{AppConstants.AppVersion} · Grabación de pantalla para auditoría interna",
            Location = new Point(16, yPos + 28),
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.Gray
        };
        panel.Controls.Add(titleLabel);
        panel.Controls.Add(subtitleLabel);
        yPos += 64;

        // ---- Helpers locales de layout ----
        GroupBox NewGroup(string titulo, int y, int alto)
        {
            var g = new GroupBox
            {
                Text = titulo,
                Location = new Point(16, y),
                Size = new Size(612, alto),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(32, 43, 68)
            };
            panel.Controls.Add(g);
            return g;
        }

        TableLayoutPanel NewGrid(GroupBox grupo)
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 4, 10, 6),
                ColumnCount = 3,
                AutoSize = false
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            grupo.Controls.Add(grid);
            return grid;
        }

        void Row(TableLayoutPanel grid, int fila, string texto, Control editor, Control? extra = null)
        {
            var l = new Label
            {
                Text = texto,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 7, 3, 3)
            };
            grid.Controls.Add(l, 0, fila);
            editor.Margin = new Padding(3, 4, 3, 3);
            grid.Controls.Add(editor, 1, fila);
            if (extra != null)
            {
                extra.Margin = new Padding(3, 4, 3, 3);
                grid.Controls.Add(extra, 2, fila);
            }
        }

        void FullRow(TableLayoutPanel grid, int fila, Control control, int altura = 28)
        {
            control.Margin = new Padding(3, 3, 3, 3);
            grid.Controls.Add(control, 0, fila);
            grid.SetColumnSpan(control, 3);
        }

        void HintRow(TableLayoutPanel grid, int fila, Label hint)
        {
            hint.AutoSize = true;
            hint.ForeColor = Color.Gray;
            hint.Font = new Font("Segoe UI", 8);
            hint.Margin = new Padding(6, 0, 3, 3);
            grid.Controls.Add(hint, 0, fila);
            grid.SetColumnSpan(hint, 3);
        }

        // ===================== GRUPO: VIDEO =====================
        var videoGroup = NewGroup("🎥  Video", yPos, 244);
        var videoGrid = NewGrid(videoGroup);
        videoGrid.RowCount = 8;

        _resolutionComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill
        };
        _resolutionComboBox.Items.AddRange(new object[]
        {
            "Nativa de la pantalla (Recomendada)",
            "480p",
            "720p",
            "1080p",
            "1440p",
            "2160p (4K)"
        });
        Row(videoGrid, 0, "Resolución de grabación:", _resolutionComboBox);

        _screenInfoLabel = new Label { Text = string.Empty };
        HintRow(videoGrid, 1, _screenInfoLabel);

        _fpsNumeric = new NumericUpDown
        {
            Minimum = 10,
            Maximum = 30,
            Increment = 5,
            Value = 15,
            Dock = DockStyle.Fill
        };
        Row(videoGrid, 2, "FPS de grabación:", _fpsNumeric);

        _captureTargetComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill
        };
        _captureTargetComboBox.Items.AddRange(new object[]
        {
            "Monitor primario",
            "Monitor secundario",
            "Ambos monitores (escritorio completo)"
        });
        _captureTargetComboBox.SelectedIndex = 0;
        _captureTargetComboBox.SelectedIndexChanged += (s, e) => UpdateScreenInfoLabel();
        Row(videoGrid, 3, "Monitor a grabar:", _captureTargetComboBox);

        _recordOnlyOnChangesCheckBox = new CheckBox
        {
            Text = "Grabar solo cuando la pantalla cambie (ahorra mucha CPU; timestamps variables)",
            AutoSize = true
        };
        FullRow(videoGrid, 4, _recordOnlyOnChangesCheckBox);

        _fragmentDurationNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 120,
            Increment = 5,
            Value = 30,
            Dock = DockStyle.Fill
        };
        var minLabel = new Label { Text = "minutos", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        Row(videoGrid, 5, "Duración de cada fragmento:", _fragmentDurationNumeric, minLabel);

        _fragmentDurationHintLabel = new Label();
        HintRow(videoGrid, 6, _fragmentDurationHintLabel);
        _fragmentDurationNumeric.ValueChanged += FragmentDurationNumeric_ValueChanged;

        _captureModeComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill
        };
        _captureModeComboBox.Items.AddRange(new object[]
        {
            "Automática (recomendada: GPU hardware Intel/NVIDIA/AMD)",
            "GPU (ddagrab: Intel QSV / NVIDIA NVENC / AMD AMF)",
            "GDI (compatibilidad multi-monitor; mayor CPU)"
        });
        Row(videoGrid, 7, "Modo de captura:", _captureModeComboBox);
        yPos += 244 + 12;

        // ===================== GRUPO: AUDIO =====================
        var audioGroup = NewGroup("🎤  Audio", yPos, 224);
        var audioGrid = NewGrid(audioGroup);
        audioGrid.RowCount = 8;

        _autoDetectAudioCheckBox = new CheckBox
        {
            Text = "Detección automática de dispositivos (dshow)",
            AutoSize = true,
            Checked = true
        };
        _autoDetectAudioCheckBox.CheckedChanged += AutoDetectAudioCheckBox_CheckedChanged;
        FullRow(audioGrid, 0, _autoDetectAudioCheckBox);

        _includeSystemAudioCheckBox = new CheckBox
        {
            Text = "Incluir audio del sistema",
            AutoSize = true,
            Checked = true
        };
        FullRow(audioGrid, 1, _includeSystemAudioCheckBox);

        _includeMicrophoneCheckBox = new CheckBox
        {
            Text = "Incluir micrófono",
            AutoSize = true,
            Checked = true
        };
        FullRow(audioGrid, 2, _includeMicrophoneCheckBox);

        _manualAudioSelectionCheckBox = new CheckBox
        {
            Text = "Selección manual de dispositivos de audio",
            AutoSize = true
        };
        _manualAudioSelectionCheckBox.CheckedChanged += ManualAudioSelectionCheckBox_CheckedChanged;
        FullRow(audioGrid, 3, _manualAudioSelectionCheckBox);

        _systemAudioComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            Enabled = false
        };
        Row(audioGrid, 4, "Dispositivo de audio del sistema:", _systemAudioComboBox);

        _microphoneComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            Enabled = false
        };
        _detectDevicesButton = new Button
        {
            Text = "Detectar",
            Dock = DockStyle.Fill,
            Height = 24
        };
        _detectDevicesButton.Click += DetectDevicesButton_Click;
        Row(audioGrid, 5, "Dispositivo de micrófono:", _microphoneComboBox, _detectDevicesButton);

        _enableNotificationsCheckBox = new CheckBox
        {
            Text = "Activar notificaciones PopUp",
            AutoSize = true,
            Checked = true
        };
        FullRow(audioGrid, 6, _enableNotificationsCheckBox);

        var audioHint = new Label { Text = "Con el binario actual, el audio del sistema requiere 'Mezcla estéreo' habilitada en Windows." };
        HintRow(audioGrid, 7, audioHint);
        yPos += 224 + 12;

        // ===================== GRUPO: ALMACENAMIENTO =====================
        var nucGroup = NewGroup("☁️  Almacenamiento en red (NUC)", yPos, 112);
        var nucGrid = NewGrid(nucGroup);
        nucGrid.RowCount = 3;

        _networkPathTextBox = new TextBox { Dock = DockStyle.Fill };
        Row(nucGrid, 0, "Ruta de red compartida:", _networkPathTextBox);

        var botonesNuc = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        _browseNetworkPathButton = new Button { Text = "Examinar…", Width = 100, Height = 26 };
        _browseNetworkPathButton.Click += BrowseNetworkPathButton_Click;
        _testNetworkPathButton = new Button { Text = "Probar escritura", Width = 130, Height = 26 };
        _testNetworkPathButton.Click += TestNetworkPathButton_Click;
        _testUploadButton = new Button { Text = "Probar envío", Width = 110, Height = 26 };
        _testUploadButton.Click += TestUploadButton_Click;
        botonesNuc.Controls.Add(_browseNetworkPathButton);
        botonesNuc.Controls.Add(_testNetworkPathButton);
        botonesNuc.Controls.Add(_testUploadButton);
        nucGrid.Controls.Add(botonesNuc, 1, 1);
        nucGrid.SetColumnSpan(botonesNuc, 2);

        var nucHint = new Label { Text = "Si queda vacía, los videos se guardan en local y se suben solos al configurarla." };
        HintRow(nucGrid, 2, nucHint);
        yPos += 112 + 12;

        // ===================== GRUPO: NOMBRE DE ARCHIVO =====================
        var nameGroup = NewGroup("📄  Nombre de archivo", yPos, 196);
        var nameGrid = NewGrid(nameGroup);
        nameGrid.RowCount = 6;

        _useUsernameInFilenameCheckBox = new CheckBox { Text = "Nombrar con nombre de usuario", AutoSize = true };
        _useUsernameInFilenameCheckBox.CheckedChanged += FilenameCheckBox_CheckedChanged;
        FullRow(nameGrid, 0, _useUsernameInFilenameCheckBox);

        _useMachineNameInFilenameCheckBox = new CheckBox { Text = "Nombrar con nombre de equipo", AutoSize = true };
        _useMachineNameInFilenameCheckBox.CheckedChanged += FilenameCheckBox_CheckedChanged;
        FullRow(nameGrid, 1, _useMachineNameInFilenameCheckBox);

        _useUsernameAndMachineNameInFilenameCheckBox = new CheckBox { Text = "Nombrar con usuario y equipo", AutoSize = true, Checked = true };
        _useUsernameAndMachineNameInFilenameCheckBox.CheckedChanged += FilenameCheckBox_CheckedChanged;
        FullRow(nameGrid, 2, _useUsernameAndMachineNameInFilenameCheckBox);

        _useCustomFilenameCheckBox = new CheckBox { Text = "Nombre personalizado (prefijo)", AutoSize = true };
        _useCustomFilenameCheckBox.CheckedChanged += CustomFilenameCheckBox_CheckedChanged;
        FullRow(nameGrid, 3, _useCustomFilenameCheckBox);

        _customFilenamePrefixTextBox = new TextBox { Dock = DockStyle.Fill, Enabled = false };
        Row(nameGrid, 4, "Prefijo personalizado:", _customFilenamePrefixTextBox);

        var customFilenameHintLabel = new Label { Text = "(Se agregará fecha y hora automáticamente)" };
        HintRow(nameGrid, 5, customFilenameHintLabel);
        yPos += 196 + 12;

        // ===================== GRUPO: ESTADO DE LA GRABACIÓN =====================
        var recGroup = NewGroup("⚙️  Estado de la grabación", yPos, 64);
        var recGrid = NewGrid(recGroup);
        recGrid.RowCount = 1;

        _startRecordingButton = new Button
        {
            Text = "▶  Reanudar grabación",
            Width = 180,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(230, 244, 234),
            ForeColor = Color.FromArgb(0, 120, 60)
        };
        _startRecordingButton.Click += StartRecordingButton_Click;
        _stopRecordingButton = new Button
        {
            Text = "⏹  Detener grabación",
            Width = 180,
            Height = 30,
            Enabled = false,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(253, 233, 231),
            ForeColor = Color.FromArgb(180, 40, 30)
        };
        _stopRecordingButton.Click += StopRecordingButton_Click;
        var botonesRec = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        botonesRec.Controls.Add(_startRecordingButton);
        botonesRec.Controls.Add(_stopRecordingButton);
        recGrid.Controls.Add(botonesRec, 0, 0);
        recGrid.SetColumnSpan(botonesRec, 3);

        // Si no hay RecordingManager (p. ej. abierto con /settings), deshabilitar botones de grabación
        if (_recordingManager == null)
        {
            _startRecordingButton.Enabled = false;
            _stopRecordingButton.Enabled = false;
        }
        yPos += 64 + 16;

        Controls.Add(panel);

        // ---- Barra inferior fija: Guardar / Cancelar ----
        var bottomBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            Padding = new Padding(12, 8, 16, 10),
            BackColor = Color.FromArgb(245, 246, 248)
        };
        _saveButton = new Button
        {
            Text = "Guardar",
            Size = new Size(110, 34),
            DialogResult = DialogResult.None,
            FlatStyle = FlatStyle.Flat,
            BackColor = accent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        _saveButton.FlatAppearance.BorderSize = 0;
        _saveButton.Click += SaveButton_Click;

        _cancelButton = new Button
        {
            Text = "Cancelar",
            Size = new Size(110, 34),
            DialogResult = DialogResult.Cancel,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };

        // Posición derecha: ancho cliente = 644 - márgenes
        _cancelButton.Location = new Point(bottomBar.Width - 12 - 110, 10);
        _saveButton.Location = new Point(bottomBar.Width - 12 - 110 - 12 - 110, 10);
        bottomBar.Controls.Add(_saveButton);
        bottomBar.Controls.Add(_cancelButton);
        bottomBar.Resize += (s, e) =>
        {
            _cancelButton.Left = bottomBar.Width - 12 - _cancelButton.Width;
            _saveButton.Left = _cancelButton.Left - 12 - _saveButton.Width;
        };
        Controls.Add(bottomBar);
    }

    private void LoadSettings()
    {
        _currentSettings = _settingsService.LoadSettings();
    }

    private void FragmentDurationNumeric_ValueChanged(object? sender, EventArgs e)
    {
        if (_fragmentDurationHintLabel != null)
        {
            _fragmentDurationHintLabel.Text =
                $"Cada {(int)_fragmentDurationNumeric.Value} min se cierra el video y se sube a la carpeta indicada";
        }
    }

    // Valores de resolución asociados a cada índice del combo (0 = nativa).
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

    /// <summary>Actualiza el label con el tamaño físico real de la pantalla/región detectada.</summary>
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
                                        $"({monitors.Count} monitor(es)). Se grabará todo junto.";
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

    private void UpdateUI()
    {
        SetResolutionCombo(_currentSettings.Resolution);
        _captureModeComboBox.SelectedIndex = Math.Clamp(_currentSettings.CaptureBackendPreference, 0, 2);
        _fpsNumeric.Value = Math.Clamp(_currentSettings.Fps, 10, 30);
        _fragmentDurationNumeric.Value = Math.Clamp(_currentSettings.FragmentDurationMinutes, 1, 120);
        _captureTargetComboBox.SelectedIndex = Math.Clamp((int)_currentSettings.CaptureTarget, 0, 2);
        _recordOnlyOnChangesCheckBox.Checked = _currentSettings.RecordOnlyOnChanges;
        _autoDetectAudioCheckBox.Checked = _currentSettings.AutoDetectAudio;
        _includeSystemAudioCheckBox.Checked = _currentSettings.IncludeSystemAudio;
        _includeMicrophoneCheckBox.Checked = _currentSettings.IncludeMicrophone;
        _manualAudioSelectionCheckBox.Checked = _currentSettings.ManualAudioSelection;
        _enableNotificationsCheckBox.Checked = _currentSettings.EnableNotifications;
        _networkPathTextBox.Text = _currentSettings.NetworkPath ?? string.Empty;
        _useUsernameInFilenameCheckBox.Checked = _currentSettings.UseUsernameInFilename;
        _useMachineNameInFilenameCheckBox.Checked = _currentSettings.UseMachineNameInFilename;
        _useUsernameAndMachineNameInFilenameCheckBox.Checked = _currentSettings.UseUsernameAndMachineNameInFilename;
        _useCustomFilenameCheckBox.Checked = _currentSettings.UseCustomFilename;
        _customFilenamePrefixTextBox.Text = _currentSettings.CustomFilenamePrefix ?? string.Empty;

        UpdateScreenInfoLabel();
        LoadAudioDevices();
        UpdateAudioControls();
    }

    private void LoadAudioDevices()
    {
        _systemAudioComboBox.Items.Clear();
        _microphoneComboBox.Items.Clear();

        try
        {
            var systemDevices = _audioDetectionService.DetectSystemAudioDevices();
            foreach (var device in systemDevices)
            {
                // Mostrar nombre con indicador de nombre alternativo si está disponible
                var displayName = device.Name;
                if (!string.IsNullOrEmpty(device.AlternativeName))
                {
                    displayName += " ✓";
                }
                _systemAudioComboBox.Items.Add(new DeviceComboItem 
                { 
                    DisplayText = displayName, 
                    DeviceName = device.Name,
                    AlternativeName = device.AlternativeName 
                });
            }

            var micDevices = _audioDetectionService.DetectMicrophoneDevices();
            foreach (var device in micDevices)
            {
                // Mostrar nombre con indicador de nombre alternativo si está disponible
                var displayName = device.Name;
                if (!string.IsNullOrEmpty(device.AlternativeName))
                {
                    displayName += " ✓";
                }
                _microphoneComboBox.Items.Add(new DeviceComboItem 
                { 
                    DisplayText = displayName, 
                    DeviceName = device.Name,
                    AlternativeName = device.AlternativeName 
                });
            }

            // Configurar DisplayMember para mostrar el texto correcto
            _systemAudioComboBox.DisplayMember = "DisplayText";
            _microphoneComboBox.DisplayMember = "DisplayText";

            // Seleccionar dispositivos guardados
            if (!string.IsNullOrEmpty(_currentSettings.SystemAudioDevice))
            {
                var item = _systemAudioComboBox.Items.Cast<DeviceComboItem>()
                    .FirstOrDefault(i => i.DeviceName == _currentSettings.SystemAudioDevice);
                if (item != null)
                {
                    _systemAudioComboBox.SelectedItem = item;
                }
            }

            if (!string.IsNullOrEmpty(_currentSettings.MicrophoneDevice))
            {
                var item = _microphoneComboBox.Items.Cast<DeviceComboItem>()
                    .FirstOrDefault(i => i.DeviceName == _currentSettings.MicrophoneDevice);
                if (item != null)
                {
                    _microphoneComboBox.SelectedItem = item;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al cargar dispositivos de audio", ex);
            MessageBox.Show(
                $"Error al detectar dispositivos de audio:\n\n{ex.Message}",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }

    /// <summary>
    /// Clase auxiliar para mostrar información de dispositivos en los combos
    /// </summary>
    private class DeviceComboItem
    {
        public string DisplayText { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string? AlternativeName { get; set; }
    }

    private void DetectDevicesButton_Click(object? sender, EventArgs e)
    {
        try
        {
            _detectDevicesButton.Enabled = false;
            _detectDevicesButton.Text = "Detectando...";
            Application.DoEvents();

            _logger.LogInfo("Detección manual de dispositivos iniciada");
            
            // Limpiar combos
            _systemAudioComboBox.Items.Clear();
            _microphoneComboBox.Items.Clear();

            // Detectar dispositivos
            var systemDevices = _audioDetectionService.DetectSystemAudioDevices();
            var micDevices = _audioDetectionService.DetectMicrophoneDevices();

            // Agregar dispositivos de sistema
            foreach (var device in systemDevices)
            {
                var displayName = device.Name;
                if (!string.IsNullOrEmpty(device.AlternativeName))
                {
                    displayName += " ✓ (Alt: " + device.AlternativeName.Substring(0, Math.Min(30, device.AlternativeName.Length)) + "...)";
                }
                else
                {
                    displayName += " ⚠ (Sin alt)";
                }
                _systemAudioComboBox.Items.Add(new DeviceComboItem 
                { 
                    DisplayText = displayName, 
                    DeviceName = device.Name,
                    AlternativeName = device.AlternativeName 
                });
            }

            // Agregar micrófonos
            foreach (var device in micDevices)
            {
                var displayName = device.Name;
                if (!string.IsNullOrEmpty(device.AlternativeName))
                {
                    displayName += " ✓ (Alt: " + device.AlternativeName.Substring(0, Math.Min(30, device.AlternativeName.Length)) + "...)";
                }
                else
                {
                    displayName += " ⚠ (Sin alt)";
                }
                _microphoneComboBox.Items.Add(new DeviceComboItem 
                { 
                    DisplayText = displayName, 
                    DeviceName = device.Name,
                    AlternativeName = device.AlternativeName 
                });
            }

            // Configurar DisplayMember
            _systemAudioComboBox.DisplayMember = "DisplayText";
            _microphoneComboBox.DisplayMember = "DisplayText";

            // Habilitar combos si hay dispositivos y selección manual está activada
            UpdateAudioControls();

            // Mostrar resumen
            var message = $"Detección completada:\n\n";
            message += $"Dispositivos de audio del sistema: {systemDevices.Count}\n";
            message += $"Dispositivos de micrófono: {micDevices.Count}\n\n";
            
            if (systemDevices.Count > 0)
            {
                message += "Audio del sistema detectado:\n";
                foreach (var dev in systemDevices)
                {
                    message += $"  - {dev.Name}";
                    if (!string.IsNullOrEmpty(dev.AlternativeName))
                        message += $" ✓";
                    else
                        message += $" ⚠";
                    message += "\n";
                }
            }
            
            if (micDevices.Count > 0)
            {
                message += "\nMicrófonos detectados:\n";
                foreach (var dev in micDevices)
                {
                    message += $"  - {dev.Name}";
                    if (!string.IsNullOrEmpty(dev.AlternativeName))
                        message += $" ✓";
                    else
                        message += $" ⚠";
                    message += "\n";
                }
            }

            MessageBox.Show(
                message,
                "Detección de Dispositivos",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );

            _logger.LogInfo($"Detección manual completada: {systemDevices.Count} sistema, {micDevices.Count} micrófonos");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error en detección manual de dispositivos", ex);
            MessageBox.Show(
                $"Error al detectar dispositivos:\n\n{ex.Message}",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
        finally
        {
            _detectDevicesButton.Enabled = true;
            _detectDevicesButton.Text = "Detectar Dispositivos";
        }
    }

    /// <summary>
    /// Rellena los combos con los dispositivos preferidos por detección automática,
    /// para que el usuario vea qué dispositivo de sistema y micrófono se usará.
    /// </summary>
    private void SelectPreferredDevicesInCombos()
    {
        try
        {
            var preferredSystem = _audioDetectionService.GetPreferredSystemAudioDevice();
            if (preferredSystem != null)
            {
                var item = _systemAudioComboBox.Items.Cast<DeviceComboItem>()
                    .FirstOrDefault(i => i.DeviceName == preferredSystem.Name);
                if (item != null)
                    _systemAudioComboBox.SelectedItem = item;
            }

            var preferredMic = _audioDetectionService.GetPreferredMicrophoneDevice();
            if (preferredMic != null)
            {
                var item = _microphoneComboBox.Items.Cast<DeviceComboItem>()
                    .FirstOrDefault(i => i.DeviceName == preferredMic.Name);
                if (item != null)
                    _microphoneComboBox.SelectedItem = item;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al seleccionar dispositivos preferidos en combos", ex);
        }
    }

    private void UpdateAudioControls()
    {
        var manualMode = _manualAudioSelectionCheckBox.Checked && !_autoDetectAudioCheckBox.Checked;
        _systemAudioComboBox.Enabled = manualMode && _includeSystemAudioCheckBox.Checked;
        _microphoneComboBox.Enabled = manualMode && _includeMicrophoneCheckBox.Checked;
    }

    private void AutoDetectAudioCheckBox_CheckedChanged(object? sender, EventArgs e)
    {
        if (_autoDetectAudioCheckBox.Checked)
        {
            // Al activar detección automática, ejecutar detección y mostrar dispositivos que se usarán
            LoadAudioDevices();
            SelectPreferredDevicesInCombos();
        }
        UpdateAudioControls();
    }

    private void ManualAudioSelectionCheckBox_CheckedChanged(object? sender, EventArgs e)
    {
        UpdateAudioControls();
    }

    private void FilenameCheckBox_CheckedChanged(object? sender, EventArgs e)
    {
        // Asegurar que solo una opción esté seleccionada (excepto personalizado)
        if (sender == _useUsernameInFilenameCheckBox && _useUsernameInFilenameCheckBox.Checked)
        {
            _useMachineNameInFilenameCheckBox.Checked = false;
            _useUsernameAndMachineNameInFilenameCheckBox.Checked = false;
            _useCustomFilenameCheckBox.Checked = false;
        }
        else if (sender == _useMachineNameInFilenameCheckBox && _useMachineNameInFilenameCheckBox.Checked)
        {
            _useUsernameInFilenameCheckBox.Checked = false;
            _useUsernameAndMachineNameInFilenameCheckBox.Checked = false;
            _useCustomFilenameCheckBox.Checked = false;
        }
        else if (sender == _useUsernameAndMachineNameInFilenameCheckBox && _useUsernameAndMachineNameInFilenameCheckBox.Checked)
        {
            _useUsernameInFilenameCheckBox.Checked = false;
            _useMachineNameInFilenameCheckBox.Checked = false;
            _useCustomFilenameCheckBox.Checked = false;
        }
    }

    private void CustomFilenameCheckBox_CheckedChanged(object? sender, EventArgs e)
    {
        _customFilenamePrefixTextBox.Enabled = _useCustomFilenameCheckBox.Checked;
        
        if (_useCustomFilenameCheckBox.Checked)
        {
            _useUsernameInFilenameCheckBox.Checked = false;
            _useMachineNameInFilenameCheckBox.Checked = false;
            _useUsernameAndMachineNameInFilenameCheckBox.Checked = false;
        }
    }

    private void BrowseNetworkPathButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Seleccione la carpeta compartida en red (NUC)",
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _networkPathTextBox.Text = dialog.SelectedPath;
        }
    }

    private void SaveButton_Click(object? sender, EventArgs e)
    {
        // Verificar permisos de administrador antes de guardar
        if (!IsRunningAsAdministrator())
        {
            MessageBox.Show(
                "Se requieren permisos de administrador para guardar la configuración.\n\nPor favor, ejecute la aplicación como administrador.",
                "Permisos requeridos",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            return;
        }

        try
        {
            // Validar nombre personalizado
            if (_useCustomFilenameCheckBox.Checked && string.IsNullOrWhiteSpace(_customFilenamePrefixTextBox.Text))
            {
                MessageBox.Show(
                    "Debe especificar un prefijo para el nombre personalizado.",
                    "Validación",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            // Validar la ruta NUC y normalizar letra de unidad -> UNC cuando sea posible
            var networkPath = string.IsNullOrWhiteSpace(_networkPathTextBox.Text)
                ? null
                : _networkPathTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(networkPath))
            {
                var check = ValidateNetworkPath(networkPath);
                networkPath = check.NormalizedPath;
                _networkPathTextBox.Text = networkPath; // mostrar la ruta normalizada (p. ej. convertida a UNC)
                if (check.WarningMessage != null)
                {
                    MessageBox.Show(
                        check.WarningMessage,
                        "Ruta de red",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                }
            }

            // Guardar configuración
            _currentSettings.Resolution = ResolutionFromCombo();
            _currentSettings.CaptureBackendPreference = _captureModeComboBox.SelectedIndex;
            _currentSettings.Fps = (int)_fpsNumeric.Value;
            _currentSettings.FragmentDurationMinutes = (int)_fragmentDurationNumeric.Value;
            _currentSettings.CaptureTarget = (CaptureTarget)_captureTargetComboBox.SelectedIndex;
            _currentSettings.RecordOnlyOnChanges = _recordOnlyOnChangesCheckBox.Checked;
            _currentSettings.AutoDetectAudio = _autoDetectAudioCheckBox.Checked;
            _currentSettings.IncludeSystemAudio = _includeSystemAudioCheckBox.Checked;
            _currentSettings.IncludeMicrophone = _includeMicrophoneCheckBox.Checked;
            _currentSettings.ManualAudioSelection = _manualAudioSelectionCheckBox.Checked;
            _currentSettings.EnableNotifications = _enableNotificationsCheckBox.Checked;
            _currentSettings.NetworkPath = networkPath;
            _currentSettings.UseUsernameInFilename = _useUsernameInFilenameCheckBox.Checked;
            _currentSettings.UseMachineNameInFilename = _useMachineNameInFilenameCheckBox.Checked;
            _currentSettings.UseUsernameAndMachineNameInFilename = _useUsernameAndMachineNameInFilenameCheckBox.Checked;
            _currentSettings.UseCustomFilename = _useCustomFilenameCheckBox.Checked;
            _currentSettings.CustomFilenamePrefix = _useCustomFilenameCheckBox.Checked 
                ? _customFilenamePrefixTextBox.Text 
                : null;

            // Solo guardar dispositivos manuales cuando la selección manual está activa Y la detección automática desactivada
            // (RecordingService solo usa dispositivos manuales cuando AutoDetectAudio = false)
            if (_manualAudioSelectionCheckBox.Checked && !_autoDetectAudioCheckBox.Checked)
            {
                // Guardar el id @device_cm_... cuando existe (mucho más fiable que el nombre
                // amigable, que falla con acentos/parentesis); si no, el nombre amigable.
                if (_systemAudioComboBox.SelectedItem is DeviceComboItem systemItem)
                {
                    _currentSettings.SystemAudioDevice =
                        !string.IsNullOrEmpty(systemItem.AlternativeName) ? systemItem.AlternativeName : systemItem.DeviceName;
                }
                else
                {
                    _currentSettings.SystemAudioDevice = _systemAudioComboBox.SelectedItem?.ToString();
                }

                if (_microphoneComboBox.SelectedItem is DeviceComboItem micItem)
                {
                    _currentSettings.MicrophoneDevice =
                        !string.IsNullOrEmpty(micItem.AlternativeName) ? micItem.AlternativeName : micItem.DeviceName;
                }
                else
                {
                    _currentSettings.MicrophoneDevice = _microphoneComboBox.SelectedItem?.ToString();
                }
            }
            else
            {
                _currentSettings.SystemAudioDevice = null;
                _currentSettings.MicrophoneDevice = null;
            }

            _settingsService.SaveSettings(_currentSettings);
            _logger.LogInfo("Configuración guardada exitosamente");

            SettingsSaved?.Invoke(this, EventArgs.Empty);

            // Aplicar la configuración de inmediato: reiniciar (o iniciar) la grabación con los nuevos valores
            ApplyConfigurationToRecording();

            // Retroalimentación explícita para TI (este diálogo es solo de admin): sin ella,
            // "Guardar" cerraba en silencio y no había forma de saber si la grabación quedó
            // activa o si los videos tenían a dónde subir.
            ShowSaveConfirmation();

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(
                "No se tienen permisos de administrador para guardar la configuración.",
                "Error de permisos",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al guardar configuración", ex);
            MessageBox.Show(
                $"Error al guardar la configuración: {ex.Message}",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }

    /// <summary>
    /// Verifica la ruta NUC y la normaliza:
    /// - Si es una letra de unidad mapeada (Y:\...), intenta resolverla a su UNC real
    ///   (p. ej. Y:\SIS → \\EDC-ZP\SIS\SIS) para guardar el UNC: la subida no depende
    ///   del mapeo ni del usuario que graba. Nota: un proceso elevado no ve los mapeos del
    ///   usuario; en ese caso no se puede resolver y no se afirma que sea "inaccesible".
    /// - Devuelve el mensaje de advertencia (null si la ruta es accesible o no se pudo verificar
    ///   por ser unidad mapeada en modo administrador).
    /// </summary>
    private (string NormalizedPath, string? WarningMessage) ValidateNetworkPath(string path)
    {
        const string notAccessibleTemplate =
            "Se guardó la configuración, pero ahora mismo no se pudo confirmar la ruta:\n\n{0}\n\n" +
            "Los videos quedarán en la carpeta temporal y se subirán automáticamente cuando la ruta esté disponible.";

        // Letra de unidad mapeada (p. ej. Y:\...)
        if (NetworkPathHelper.IsDriveLetterPath(path))
        {
            var unc = NetworkPathHelper.TryResolveToUnc(path);

            // El UNC resuelve y es accesible: guardar el UNC (no depende del mapeo ni del usuario)
            if (unc != null && Directory.Exists(unc))
                return (unc, null);

            // El UNC no se pudo confirmar pero la letra SÍ es accesible: guardar la letra
            // (comportamiento original; el proceso que graba corre como el usuario dueño del mapeo)
            if (Directory.Exists(path))
            {
                return (path,
                    "Se guardó la configuración. La ruta '" + path + "' es accesible, pero su nombre de red " +
                    "no se pudo confirmar desde esta ventana (los procesos administradores no ven las unidades " +
                    "mapeadas). Al grabar, la aplicación intentará también por el nombre de red si es necesario.");
            }

            return (path, string.Format(notAccessibleTemplate, path));
        }

        // Ruta UNC (\\servidor\carpeta) u otra: verificar directamente
        return Directory.Exists(path)
            ? (path, null)
            : (path, string.Format(notAccessibleTemplate, path));
    }

    /// <summary>
    /// Prueba la ruta NUC escrita: accesibilidad, resolución a UNC (si es letra de unidad no
    /// visible en este proceso) y permiso de escritura real (crea y borra un archivo temporal).
    /// Explica el caso típico de proceso elevado que no ve unidades mapeadas del usuario.
    /// </summary>
    private void TestNetworkPathButton_Click(object? sender, EventArgs e)
    {
        var enteredPath = _networkPathTextBox.Text.Trim();
        if (string.IsNullOrEmpty(enteredPath))
        {
            MessageBox.Show(
                "Escriba primero la ruta de red que desea probar.",
                "Probar ruta",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            return;
        }

        var elevated = NetworkPathHelper.IsProcessElevated();
        var path = enteredPath;
        string? resolvedUnc = null;

        // 1) Accesibilidad de la ruta escrita
        var accessible = Directory.Exists(path);

        // 2) Si es letra de unidad no visible, probar también su UNC (lo mismo que hará la app al grabar)
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

            var message = "No se pudo acceder a la ruta. Diagnóstico:\n\n" + diag + "\n\n";
            if (elevated)
            {
                message +=
                    "Esta ventana corre COMO ADMINISTRADOR y Windows no le permite ver las unidades " +
                    "de red mapeadas del usuario (aunque la carpeta abra en el Explorador).\n\n" +
                    "Opciones:\n" +
                    "1) Use la ruta UNC directa (\\\\servidor\\carpeta).\n" +
                    "2) No ejecute la aplicación como administrador: la grabación no lo necesita.\n" +
                    "3) Active EnableLinkedConnections (ver NOTAS_IMPORTANTES.md).";
            }
            else
            {
                message += "Verifique que la ruta existe y que este equipo tiene acceso de red a ella.";
            }

            MessageBox.Show(message, "Prueba de ruta", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // 3) Permiso de escritura (lo que realmente necesita la app para subir videos)
        var testFile = Path.Combine(path, $"__screenrecorder_test_{DateTime.Now:yyyyMMdd_HHmmss}.tmp");
        try
        {
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);

            _logger.LogInfo($"Prueba de ruta exitosa: '{path}' accesible y con permisos de escritura.");
            var note = resolvedUnc != null
                ? $"\n\n(Nota: la unidad no es visible en este proceso; se usará su nombre de red {resolvedUnc})"
                : string.Empty;
            MessageBox.Show(
                $"La ruta es accesible y tiene permisos de escritura:\n\n{path}{note}",
                "Prueba de ruta",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception ex)
        {
            _logger.LogError($"La ruta '{path}' se abre pero no permite escritura", ex);
            MessageBox.Show(
                $"La ruta '{path}' se abre pero NO permite escritura (se requiere para subir los videos).\n\n" +
                $"Error: {ex.Message}\n\n" +
                $"Conceda permisos de escritura a '{Environment.UserName}' en esa carpeta compartida.",
                "Prueba de ruta",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
    }

    /// <summary>
    /// Mensaje de confirmación tras Guardar: informa si la grabación quedó activa y advierte
    /// cuando no hay ruta NUC (los videos solo se acumularían en local, invisible para TI).
    /// </summary>
    private void ShowSaveConfirmation()
    {
        try
        {
            var nucEmpty = string.IsNullOrWhiteSpace(_currentSettings.NetworkPath);

            if (_recordingManager == null)
            {
                // Modo /settings (diálogo sin instancia): la instancia en ejecución recibe 'quieto'.
                MessageBox.Show(
                    "Configuración guardada. Se aplicará al reiniciar la grabación.\n\n" +
                    (nucEmpty ? "⚠ No hay ruta NUC configurada: los videos se guardarán localmente hasta configurarla.\n\n" : string.Empty) +
                    "Revise el estado con 'Verificar Autoarranque.bat'.",
                    "Screen Recorder",
                    MessageBoxButtons.OK,
                    nucEmpty ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                return;
            }

            if (_recordingManager.IsRecordingActive)
            {
                MessageBox.Show(
                    "Configuración guardada. La grabación está ACTIVA.\n\n" +
                    (nucEmpty
                        ? "⚠ No hay ruta NUC configurada: los videos se guardan localmente en\n" +
                          $"   {Path.Combine(Path.GetTempPath(), "ScreenRecorder")}\n" +
                          "   hasta configurarla (se suben solos en cuanto esté disponible).\n\n"
                        : $"Los videos se suben a: {_currentSettings.NetworkPath}\n\n") +
                    "El primer fragmento aparecerá al completar su duración (30 min por defecto).",
                    "Screen Recorder",
                    MessageBoxButtons.OK,
                    nucEmpty ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    "Configuración guardada, pero la grabación NO está activa.\n\n" +
                    "Revise el log: C:\\ProgramData\\ScreenRecorder\\Logs\\ (busque 'fallos consecutivos' o errores de FFmpeg).",
                    "Screen Recorder",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch
        {
            // La confirmación nunca debe impedir cerrar el diálogo.
        }
    }

    /// <summary>
    /// Aplica la configuración guardada de inmediato: si la grabación está activa la reinicia
    /// (Stop + Start recarga la config del Registry); si no, la inicia.
    /// En modo /settings (sin RecordingManager) la instancia en ejecución recibe el comando
    /// 'quieto' por el pipe (Program.cs) y hace lo mismo.
    /// </summary>
    private void ApplyConfigurationToRecording()
    {
        if (_recordingManager == null) return;

        try
        {
            if (_recordingManager.IsRecordingActive)
            {
                _logger.LogInfo("Aplicando nueva configuración: reiniciando grabación");
                _recordingManager.Stop();
                _recordingManager.Start();
                _logger.LogInfo("Grabación reiniciada con la nueva configuración");
            }
            else
            {
                _logger.LogInfo("Aplicando nueva configuración: iniciando grabación");
                _recordingManager.Start();
                _logger.LogInfo("Grabación iniciada con la nueva configuración");
            }

            // Guardar/Aplicar es una reanudación explícita: quita la detención voluntaria ('echate')
            ControlPipe.ClearStopFlag();
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al aplicar la configuración a la grabación", ex);
            MessageBox.Show(
                $"La configuración se guardó, pero no se pudo aplicar a la grabación:\n\n{ex.Message}",
                "Advertencia",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
    }

    private bool IsRunningAsAdministrator() => ScreenRecorder.Core.Services.NetworkPathHelper.IsProcessElevated();
}
