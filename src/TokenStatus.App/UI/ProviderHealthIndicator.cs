using TokenStatus.Core.Models;
using System.Drawing.Drawing2D;

namespace TokenStatus.App.UI;

internal sealed class ProviderHealthIndicator : Control
{
    private readonly ToolTip _toolTip = new();
    private readonly string _providerName;
    private ProviderHealth _health;

    public ProviderHealthIndicator(string providerName)
    {
        _providerName = providerName;
        AutoSize = true;
        Height = Math.Max(22, Font.Height + 6);
        AccessibleRole = AccessibleRole.StaticText;
        TabStop = false;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);
        BackColor = Color.Transparent;
        SetHealth(ProviderHealth.Loading);
    }

    public void SetHealth(ProviderHealth health)
    {
        _health = health;
        var description = StatusViewModel.DescribeHealth(health);
        AccessibleName = $"{_providerName} status: {description}";
        AccessibleDescription = $"Current {_providerName} provider state is {description}.";
        _toolTip.SetToolTip(this, description);
        UpdateSize();
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateSize();
    }

    private void UpdateSize()
    {
        var text = StatusViewModel.DescribeHealth(_health);
        var textSize = TextRenderer.MeasureText(text, Font);
        Height = Math.Max(22, textSize.Height + 6);
        Width = 22 + textSize.Width + 4;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var color = SystemInformation.HighContrast
            ? _health == ProviderHealth.Loading ? SystemColors.GrayText : SystemColors.Highlight
            : _health switch
            {
                ProviderHealth.Healthy => Color.FromArgb(40, 170, 90),
                ProviderHealth.Stale => Color.FromArgb(226, 157, 35),
                ProviderHealth.Loading => Color.FromArgb(130, 136, 145),
                _ => Color.FromArgb(210, 58, 58)
            };
        var circle = new Rectangle(1, Math.Max(1, (Height - 18) / 2), 16, 16);
        using var brush = new SolidBrush(color);
        e.Graphics.FillEllipse(brush, circle);

        using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        if (_health == ProviderHealth.Healthy)
        {
            var top = circle.Top;
            e.Graphics.DrawLines(pen,
            [
                new PointF(4.7f, top + 7.8f),
                new PointF(7.5f, top + 10.4f),
                new PointF(12.8f, top + 4.9f)
            ]);
        }
        else if (_health == ProviderHealth.Loading)
        {
            e.Graphics.DrawArc(pen, circle.X + 3.5f, circle.Y + 3.5f, 9, 9, -65, 235);
        }
        else
        {
            e.Graphics.DrawLine(pen, circle.X + 7.5f, circle.Y + 3.7f, circle.X + 7.5f, circle.Y + 8.6f);
            e.Graphics.DrawEllipse(pen, circle.X + 7.1f, circle.Y + 11, 0.8f, 0.8f);
        }

        TextRenderer.DrawText(
            e.Graphics,
            StatusViewModel.DescribeHealth(_health),
            Font,
            new Rectangle(22, 0, Math.Max(1, Width - 22), Height),
            SystemInformation.HighContrast ? SystemColors.WindowText : ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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
