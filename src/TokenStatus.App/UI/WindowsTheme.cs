using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TokenStatus.App.UI;

internal static class WindowsTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;

    public static bool IsDarkModeEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    public static Color GetAccentColor() => IsDarkModeEnabled()
        ? ThemePalette.Dark.Accent
        : ThemePalette.Light.Accent;

    public static Color GetSurfaceBorderColor() => IsDarkModeEnabled()
        ? ThemePalette.Dark.SurfaceBorder
        : ThemePalette.Light.SurfaceBorder;

    public static void Apply(Control root)
    {
        var dark = IsDarkModeEnabled();
        var palette = dark ? ThemePalette.Dark : ThemePalette.Light;
        ApplyControl(root, palette, dark);

        if (root is Form form && form.IsHandleCreated)
        {
            var enabled = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
            var cornerPreference = 2;
            _ = DwmSetWindowAttribute(form.Handle, DwmWindowCornerPreference, ref cornerPreference, sizeof(int));
        }
    }

    public static void Apply(ContextMenuStrip menu)
    {
        var dark = IsDarkModeEnabled();
        var palette = dark ? ThemePalette.Dark : ThemePalette.Light;
        menu.BackColor = palette.Background;
        menu.ForeColor = palette.Foreground;
        menu.Renderer = dark
            ? new ToolStripProfessionalRenderer(new DarkColorTable(palette))
            : new ToolStripSystemRenderer();
        ApplyItems(menu.Items, palette);
    }

    private static void ApplyControl(Control control, ThemePalette palette, bool dark)
    {
        var role = control.Tag as string;
        control.ForeColor = role switch
        {
            "error" => palette.Error,
            "muted" => palette.Muted,
            "accent" => palette.Accent,
            _ => palette.Foreground
        };

        switch (control)
        {
            case SectionCard:
                control.BackColor = palette.Surface;
                break;
            case TextBoxBase:
            case NumericUpDown:
                control.BackColor = palette.InputBackground;
                break;
            case Button button:
                button.BackColor = palette.ButtonBackground;
                button.ForeColor = palette.Foreground;
                button.UseVisualStyleBackColor = !dark;
                button.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                button.FlatAppearance.BorderColor = palette.Border;
                break;
            case Label:
            case ProviderIcon:
            case ProviderHealthIndicator:
                control.BackColor = Color.Transparent;
                break;
            case Panel:
                control.BackColor = role == "background" ? palette.Background : Color.Transparent;
                break;
            default:
                control.BackColor = role switch
                {
                    "chrome-border" => palette.Border,
                    "surface" => palette.Surface,
                    "transparent" => Color.Transparent,
                    _ => palette.Background
                };
                break;
        }

        foreach (Control child in control.Controls)
        {
            ApplyControl(child, palette, dark);
        }

        if (control.IsHandleCreated && control is ScrollableControl or TextBoxBase or NumericUpDown)
        {
            _ = SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
        }
    }

    private static void ApplyItems(ToolStripItemCollection items, ThemePalette palette)
    {
        foreach (ToolStripItem item in items)
        {
            item.BackColor = palette.Background;
            item.ForeColor = palette.Foreground;
            if (item is ToolStripMenuItem menuItem)
            {
                ApplyItems(menuItem.DropDownItems, palette);
            }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr window, string? subAppName, string? subIdList);

    private readonly record struct ThemePalette(
        Color Background,
        Color Surface,
        Color SurfaceBorder,
        Color Foreground,
        Color InputBackground,
        Color ButtonBackground,
        Color Border,
        Color Selection,
        Color Muted,
        Color Accent,
        Color Error)
    {
        public static ThemePalette Light { get; } = new(
            Color.FromArgb(248, 249, 251),
            Color.FromArgb(238, 240, 243),
            Color.FromArgb(205, 208, 214),
            Color.FromArgb(31, 35, 41),
            SystemColors.Window,
            SystemColors.Control,
            SystemColors.ControlDark,
            SystemColors.Highlight,
            Color.FromArgb(88, 94, 104),
            Color.FromArgb(122, 78, 47),
            Color.FromArgb(171, 48, 48));

        public static ThemePalette Dark { get; } = new(
            Color.FromArgb(30, 30, 30),
            Color.FromArgb(42, 42, 42),
            Color.FromArgb(61, 61, 64),
            Color.FromArgb(240, 240, 240),
            Color.FromArgb(45, 45, 48),
            Color.FromArgb(50, 50, 53),
            Color.FromArgb(82, 82, 86),
            Color.FromArgb(62, 82, 112),
            Color.FromArgb(174, 174, 179),
            Color.FromArgb(194, 145, 105),
            Color.FromArgb(255, 145, 145));
    }

    private sealed class DarkColorTable(ThemePalette palette) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => palette.Background;
        public override Color MenuBorder => palette.Border;
        public override Color MenuItemBorder => palette.Border;
        public override Color MenuItemSelected => palette.Selection;
        public override Color MenuItemSelectedGradientBegin => palette.Selection;
        public override Color MenuItemSelectedGradientEnd => palette.Selection;
        public override Color MenuItemPressedGradientBegin => palette.Selection;
        public override Color MenuItemPressedGradientMiddle => palette.Selection;
        public override Color MenuItemPressedGradientEnd => palette.Selection;
        public override Color ImageMarginGradientBegin => palette.Background;
        public override Color ImageMarginGradientMiddle => palette.Background;
        public override Color ImageMarginGradientEnd => palette.Background;
        public override Color SeparatorDark => palette.Border;
        public override Color SeparatorLight => palette.Border;
    }
}
