using System.Drawing.Drawing2D;

namespace TokenStatus.App.UI;

internal sealed class SectionCard : VerticalStackLayout
{
    private const int CornerRadius = 10;

    public SectionCard()
    {
        Padding = new Padding(
            LayoutMetrics.Large,
            LayoutMetrics.Small,
            LayoutMetrics.Large,
            LayoutMetrics.Small);
        Margin = new Padding(0, 0, 0, LayoutMetrics.XSmall);
        Tag = "surface";
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? Color.Transparent);
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = CreateRoundedRectangle(bounds, CornerRadius);
        using var brush = new SolidBrush(BackColor);
        using var pen = new Pen(WindowsTheme.GetSurfaceBorderColor());
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
