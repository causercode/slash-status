using System.Drawing.Drawing2D;
using TokenStatus.Core.Models;

namespace TokenStatus.App.UI;

internal sealed class ProviderHealthIndicator : Control
{
    private readonly ToolTip _toolTip = new();
    private ProviderHealth _health;

    public ProviderHealthIndicator()
    {
        Size = new Size(18, 18);
        AccessibleRole = AccessibleRole.Indicator;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);
        SetHealth(ProviderHealth.Loading);
    }

    public void SetHealth(ProviderHealth health)
    {
        _health = health;
        var description = StatusViewModel.DescribeHealth(health);
        AccessibleName = $"Provider status: {description}";
        _toolTip.SetToolTip(this, description);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var color = _health switch
        {
            ProviderHealth.Healthy => Color.FromArgb(40, 170, 90),
            ProviderHealth.Stale => Color.FromArgb(226, 157, 35),
            ProviderHealth.Loading => Color.FromArgb(130, 136, 145),
            _ => Color.FromArgb(210, 58, 58)
        };
        var circle = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        using var brush = new SolidBrush(color);
        e.Graphics.FillEllipse(brush, circle);

        using var pen = new Pen(Color.White, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        if (_health == ProviderHealth.Healthy)
        {
            e.Graphics.DrawLines(pen,
            [
                new PointF(4.7f, 8.8f),
                new PointF(7.5f, 11.4f),
                new PointF(12.8f, 5.9f)
            ]);
        }
        else if (_health == ProviderHealth.Loading)
        {
            e.Graphics.DrawArc(pen, 4.5f, 4.5f, 8, 8, -65, 235);
        }
        else
        {
            e.Graphics.DrawLine(pen, 8.5f, 4.7f, 8.5f, 9.6f);
            e.Graphics.DrawEllipse(pen, 8.1f, 12, 0.8f, 0.8f);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }
}
