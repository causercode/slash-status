namespace TokenStatus.App.UI;

internal sealed class QuotaUsageView : TableLayoutPanel
{
    public QuotaUsageView(string title, int remainingPercent, string resetText)
    {
        Width = 382;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1;
        RowCount = 3;
        Margin = new Padding(0, 3, 0, 4);
        Padding = new Padding(0);
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 382));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel
        {
            Width = 382,
            Height = 24,
            ColumnCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        header.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Margin = new Padding(0, 1, 0, 1)
        }, 0, 0);

        header.Controls.Add(new Label
        {
            Text = $"{remainingPercent}% left",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopRight,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Margin = new Padding(0, 1, 0, 1)
        }, 1, 0);

        var bar = new RemainingUsageBar(remainingPercent)
        {
            Width = 382,
            Height = 14,
            Margin = new Padding(0, 0, 0, 2)
        };

        var reset = new Label
        {
            Text = resetText,
            AutoSize = true,
            MaximumSize = new Size(382, 0),
            Font = new Font("Segoe UI", 8, FontStyle.Regular),
            Tag = "muted",
            Margin = new Padding(0, 0, 0, 0)
        };

        Controls.Add(header, 0, 0);
        Controls.Add(bar, 0, 1);
        Controls.Add(reset, 0, 2);
    }

    private sealed class RemainingUsageBar : Control
    {
        private readonly int _remainingPercent;

        public RemainingUsageBar(int remainingPercent)
        {
            _remainingPercent = Math.Clamp(remainingPercent, 0, 100);
            Tag = "transparent";
            AccessibleRole = AccessibleRole.ProgressBar;
            AccessibleName = $"{_remainingPercent}% usage remaining";
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint,
                true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var dark = WindowsTheme.IsDarkModeEnabled();
            var trackColor = dark ? Color.FromArgb(58, 58, 61) : Color.FromArgb(220, 223, 228);
            var fillColor = dark ? Color.FromArgb(210, 210, 213) : Color.FromArgb(74, 88, 112);
            var borderColor = dark ? Color.FromArgb(105, 105, 110) : Color.FromArgb(145, 150, 160);
            var bounds = new Rectangle(0, 1, Math.Max(1, ClientSize.Width - 1), Math.Max(1, ClientSize.Height - 3));

            using var trackBrush = new SolidBrush(trackColor);
            using var fillBrush = new SolidBrush(fillColor);
            using var borderPen = new Pen(borderColor);
            e.Graphics.FillRectangle(trackBrush, bounds);

            var inner = Rectangle.Inflate(bounds, -1, -1);
            var fillWidth = (int)Math.Round(inner.Width * (_remainingPercent / 100d));
            if (fillWidth > 0)
            {
                e.Graphics.FillRectangle(fillBrush, new Rectangle(inner.X, inner.Y, fillWidth, inner.Height));
            }

            e.Graphics.DrawRectangle(borderPen, bounds);
        }
    }
}
