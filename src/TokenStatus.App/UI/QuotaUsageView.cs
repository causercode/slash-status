namespace TokenStatus.App.UI;

internal sealed class QuotaUsageView : TableLayoutPanel
{
    public QuotaUsageView(string title, int remainingPercent, string resetText)
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1;
        RowCount = 3;
        Margin = new Padding(0, LayoutMetrics.XSmall, 0, LayoutMetrics.XSmall);
        Padding = new Padding(0);
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = $"{title} quota";
        TabStop = false;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel
        {
            Height = 24,
            Dock = DockStyle.Fill,
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
            Margin = new Padding(0),
            AccessibleName = title,
            TabStop = false
        }, 0, 0);

        header.Controls.Add(new Label
        {
            Text = $"{remainingPercent}% left",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopRight,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Margin = new Padding(0),
            AccessibleName = $"{title}: {remainingPercent}% remaining",
            TabStop = false
        }, 1, 0);

        var bar = new ProgressBar
        {
            Height = 14,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, LayoutMetrics.XSmall),
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(remainingPercent, 0, 100),
            Style = ProgressBarStyle.Continuous,
            AccessibleRole = AccessibleRole.ProgressBar,
            AccessibleName = $"{title} quota remaining",
            AccessibleDescription = $"{Math.Clamp(remainingPercent, 0, 100)} percent of the {title.ToLowerInvariant()} quota remains.",
            TabStop = false
        };

        var reset = new Label
        {
            Text = resetText,
            AutoSize = true,
            MaximumSize = new Size(0, 0),
            Font = new Font("Segoe UI", 8, FontStyle.Regular),
            Tag = "muted",
            Margin = new Padding(0, 0, 0, 0),
            AccessibleName = resetText,
            TabStop = false
        };

        Controls.Add(header, 0, 0);
        Controls.Add(bar, 0, 1);
        Controls.Add(reset, 0, 2);
    }
}
