using System.Drawing;
using System.Windows.Forms;

namespace ScreenRecorder.UI.UI;

/// <summary>
/// Paleta, tipografías y fabricas de controles para mantener un aspecto coherente
/// en la interfaz de configuración (WSearchCfg). Centralizar aquí evita colores y
/// tamaños dispersos por el formulario.
/// </summary>
internal static class Theme
{
    // ---- Colores ----
    public static readonly Color Header = Color.FromArgb(32, 43, 68);        // #202B44
    public static readonly Color HeaderText = Color.White;
    public static readonly Color HeaderSubtle = Color.FromArgb(176, 184, 199);
    public static readonly Color Accent = Color.FromArgb(0, 120, 215);       // #0078D7
    public static readonly Color Background = Color.FromArgb(247, 248, 250); // #F7F8FA
    public static readonly Color Surface = Color.White;
    public static readonly Color Border = Color.FromArgb(225, 228, 232);     // #E1E4E8
    public static readonly Color TextPrimary = Color.FromArgb(32, 43, 68);
    public static readonly Color TextMuted = Color.FromArgb(120, 128, 140);
    public static readonly Color Success = Color.FromArgb(16, 124, 16);       // #107C10
    public static readonly Color Danger = Color.FromArgb(196, 43, 28);        // #C42B1C
    public static readonly Color DangerSoft = Color.FromArgb(253, 233, 231);

    // ---- Tipografías ----
    public static readonly Font Body = new("Segoe UI", 9F);
    public static readonly Font BodyBold = new("Segoe UI", 9F, FontStyle.Bold);
    public static readonly Font Small = new("Segoe UI", 8F);
    public static readonly Font Section = new("Segoe UI", 9.75F, FontStyle.Bold);
    public static readonly Font Title = new("Segoe UI", 13F, FontStyle.Bold);
    public static readonly Font TabHeader = new("Segoe UI", 9.5F, FontStyle.Regular);

    /// <summary>Botón principal (acción afirmativa: Guardar).</summary>
    public static Button PrimaryButton(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Font = BodyBold,
            Size = new Size(112, 34),
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    /// <summary>Botón secundario (borde, fondo claro: Cancelar, Examinar, Probar, Detectar).</summary>
    public static Button SecondaryButton(string text, int width = 112)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = TextPrimary,
            Font = Body,
            Size = new Size(width, 34),
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.BorderSize = 1;
        return b;
    }

    /// <summary>Botón de control de grabación (texto con color, sin borde).</summary>
    public static Button GhostButton(string text, Color fore)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = fore,
            Font = BodyBold,
            AutoSize = true,
            MinimumSize = new Size(128, 34),
            Padding = new Padding(10, 0, 10, 0),
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    /// <summary>Etiqueta de título de sección dentro de una pestaña.</summary>
    public static Label SectionLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Section,
        ForeColor = TextPrimary,
        Margin = new Padding(3, 4, 3, 8)
    };
}
