using System.Drawing.Drawing2D;
using TokenStatus.Core.Services;

namespace TokenStatus.App.UI;

public static class TrayIconRenderer
{
    public static Icon Create(TrayHealth health)
    {
        var color = WindowsTheme.IsHighContrastEnabled
            ? SystemColors.Highlight
            : health switch
            {
                TrayHealth.Green => Color.FromArgb(40, 170, 90),
                TrayHealth.Amber => Color.FromArgb(226, 157, 35),
                TrayHealth.Red => Color.FromArgb(210, 58, 58),
                _ => Color.FromArgb(130, 136, 145)
            };

        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var path = new GraphicsPath())
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            path.AddEllipse(3, 3, 26, 26);
            using var brush = new SolidBrush(color);
            graphics.FillPath(brush, path);
            using var outline = new Pen(
                WindowsTheme.IsHighContrastEnabled ? SystemColors.HighlightText : Color.White,
                2);
            graphics.DrawPath(outline, path);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
