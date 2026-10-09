using System;
using System.Drawing;
using System.Windows.Forms;
using ScreenRecorder.Core.Constants;

namespace ScreenRecorder.UI.Forms;

/// <summary>
/// Formulario Acerca de
/// </summary>
public partial class AboutForm : Form
{
    public AboutForm()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Text = "Acerca de";
        Size = new Size(500, 400);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20)
        };

        var titleLabel = new Label
        {
            Text = AppConstants.AppName,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 20)
        };

        var versionLabel = new Label
        {
            Text = $"Versión: {AppConstants.AppVersion}",
            Font = new Font("Segoe UI", 10),
            AutoSize = true,
            Location = new Point(20, 60)
        };

        var technologiesLabel = new Label
        {
            Text = "Tecnologías implementadas:",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 100)
        };

        var technologiesValueLabel = new Label
        {
            Text = AppConstants.Technologies,
            Font = new Font("Segoe UI", 9),
            AutoSize = true,
            Location = new Point(20, 125)
        };

        var descriptionLabel = new Label
        {
            Text = "Motivo de la implementación:",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 170)
        };

        var descriptionValueLabel = new Label
        {
            Text = AppConstants.AppDescription,
            Font = new Font("Segoe UI", 9),
            AutoSize = false,
            Size = new Size(440, 80),
            Location = new Point(20, 195)
        };

        var companyLabel = new Label
        {
            Text = $"© {DateTime.Now.Year} {AppConstants.CompanyName}",
            Font = new Font("Segoe UI", 8),
            AutoSize = true,
            Location = new Point(20, 290)
        };

        var closeButton = new Button
        {
            Text = "Cerrar",
            Size = new Size(100, 30),
            Location = new Point(360, 320),
            DialogResult = DialogResult.OK
        };

        panel.Controls.Add(titleLabel);
        panel.Controls.Add(versionLabel);
        panel.Controls.Add(technologiesLabel);
        panel.Controls.Add(technologiesValueLabel);
        panel.Controls.Add(descriptionLabel);
        panel.Controls.Add(descriptionValueLabel);
        panel.Controls.Add(companyLabel);
        panel.Controls.Add(closeButton);

        Controls.Add(panel);
    }
}
