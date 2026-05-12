namespace CrmAgent.Tray;

/// <summary>
/// Central colour palette and styling helpers for a modern dark UI.
/// </summary>
internal static class Theme
{
    // ── Surface colours ──────────────────────────────────────────
    public static readonly Color Background      = Color.FromArgb(24, 24, 37);   // deep navy
    public static readonly Color Surface         = Color.FromArgb(35, 35, 52);   // card / panel
    public static readonly Color SurfaceLight    = Color.FromArgb(48, 48, 68);   // input fields
    public static readonly Color Border          = Color.FromArgb(60, 60, 85);   // subtle borders

    // ── Text colours ─────────────────────────────────────────────
    public static readonly Color TextPrimary     = Color.FromArgb(230, 230, 240);
    public static readonly Color TextSecondary   = Color.FromArgb(150, 150, 175);
    public static readonly Color TextDim         = Color.FromArgb(100, 100, 130);

    // ── Accent colours ───────────────────────────────────────────
    public static readonly Color Accent          = Color.FromArgb(88, 130, 250);  // blue
    public static readonly Color AccentHover     = Color.FromArgb(110, 150, 255);
    public static readonly Color Success         = Color.FromArgb(74, 222, 128);  // green
    public static readonly Color Error           = Color.FromArgb(248, 113, 113); // red
    public static readonly Color Warning         = Color.FromArgb(251, 191, 36);  // amber
    public static readonly Color Info            = Color.FromArgb(100, 200, 255); // cyan

    // ── Log-specific ─────────────────────────────────────────────
    public static readonly Color LogBackground   = Color.FromArgb(18, 18, 28);
    public static readonly Color LogText         = Color.FromArgb(200, 200, 215);

    // ── Fonts ────────────────────────────────────────────────────
    private static readonly string SansFamily =
        FontFamily.Families.Any(f => f.Name == "Segoe UI") ? "Segoe UI" : "Tahoma";

    public static Font Heading   => new(SansFamily, 15f, FontStyle.Bold);
    public static Font SubHead   => new(SansFamily, 10.5f, FontStyle.Bold);
    public static Font Body      => new(SansFamily, 10f, FontStyle.Regular);
    public static Font Small     => new(SansFamily, 9f, FontStyle.Regular);
    public static Font Mono      => new("Consolas", 9.5f, FontStyle.Regular);

    // ── Button styling ───────────────────────────────────────────

    /// <summary>Primary filled button (accent background).</summary>
    public static void StylePrimary(Button btn)
    {
        btn.FlatStyle = FlatStyle.Flat;
        btn.BackColor = Accent;
        btn.ForeColor = Color.White;
        btn.Font = Body;
        btn.Cursor = Cursors.Hand;
        btn.Padding = new Padding(14, 6, 14, 6);
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = AccentHover;
        btn.FlatAppearance.MouseDownBackColor = Accent;
    }

    /// <summary>Secondary ghost button (transparent with border).</summary>
    public static void StyleSecondary(Button btn)
    {
        btn.FlatStyle = FlatStyle.Flat;
        btn.BackColor = Color.Transparent;
        btn.ForeColor = TextSecondary;
        btn.Font = Body;
        btn.Cursor = Cursors.Hand;
        btn.Padding = new Padding(14, 6, 14, 6);
        btn.FlatAppearance.BorderColor = Border;
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.MouseOverBackColor = SurfaceLight;
        btn.FlatAppearance.MouseDownBackColor = Surface;
    }

    /// <summary>Apply the dark theme to a Form.</summary>
    public static void ApplyToForm(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = TextPrimary;
        form.Font = Body;
    }

    /// <summary>Style a text box for the dark theme.</summary>
    public static void StyleTextBox(TextBox tb)
    {
        tb.BackColor = SurfaceLight;
        tb.ForeColor = TextPrimary;
        tb.BorderStyle = BorderStyle.FixedSingle;
        tb.Font = Body;
    }

    /// <summary>Style a label as secondary / caption text.</summary>
    public static void StyleCaption(Label lbl)
    {
        lbl.ForeColor = TextSecondary;
        lbl.Font = Small;
    }
}
