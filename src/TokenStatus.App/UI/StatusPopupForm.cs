using TokenStatus.Core.Models;
using System.Drawing.Drawing2D;

namespace TokenStatus.App.UI;

public sealed class StatusPopupForm : Form
{
    private const int PopupWidth = 460;
    private const int MinimumPopupHeight = 460;
    private const int ScreenEdgeGap = 8;

    private readonly Action _refresh;
    private readonly Action _openDashboard;
    private readonly Action _openSettings;
    private readonly Action _exit;
    private readonly Action<AwakeMode, TimeSpan?> _setAwake;
    private readonly FlowLayoutPanel _content;
    private readonly Label _updatedLabel;
    private readonly ProviderHealthIndicator _codexHealthIndicator;
    private readonly FlowLayoutPanel _limitsPanel;
    private readonly Label _todayTokensLabel;
    private readonly Label _lifetimeTokensLabel;
    private readonly Label _codexErrorLabel;
    private readonly ProviderHealthIndicator _openCodeHealthIndicator;
    private readonly Label _openCodeSummaryLabel;
    private readonly Label _openCodeTokensLabel;
    private readonly Label _openCodeCacheLabel;
    private readonly Label _openCodeErrorLabel;
    private readonly Label _awakeLabel;
    private AppSnapshot _snapshot;

    public StatusPopupForm(
        AppSnapshot initialSnapshot,
        Action refresh,
        Action openDashboard,
        Action openSettings,
        Action<AwakeMode, TimeSpan?> setAwake,
        Action exit)
    {
        _snapshot = initialSnapshot;
        _refresh = refresh;
        _openDashboard = openDashboard;
        _openSettings = openSettings;
        _setAwake = setAwake;
        _exit = exit;

        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        Tag = "chrome-border";
        Padding = new Padding(1);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        Width = PopupWidth;
        Height = 590;

        _content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(18, 14, 18, 14),
            Tag = "background"
        };
        Controls.Add(_content);

        var title = new TableLayoutPanel { Width = 402, Height = 40, ColumnCount = 2, Margin = new Padding(0, 0, 0, 4) };
        title.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        title.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        var titleLabel = CreateLabel("TokenStatus", 15, FontStyle.Bold);
        titleLabel.Tag = "accent";
        title.Controls.Add(titleLabel, 0, 0);
        var refreshButton = CreateButton("Refresh", (_, _) => _refresh());
        refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        title.Controls.Add(refreshButton, 1, 0);
        _content.Controls.Add(title);

        _codexErrorLabel = CreateErrorLabel();
        _limitsPanel = CreateLimitsPanel();
        _todayTokensLabel = CreateLabel(string.Empty, 9, FontStyle.Regular);
        _lifetimeTokensLabel = CreateLabel(string.Empty, 9, FontStyle.Regular);

        var codexCard = new SectionCard();
        _content.Controls.Add(codexCard);
        _codexHealthIndicator = AddProviderSectionHeader(codexCard, "Codex", ProviderIconKind.Codex);
        codexCard.Controls.Add(_limitsPanel);
        codexCard.Controls.Add(_todayTokensLabel);
        codexCard.Controls.Add(_lifetimeTokensLabel);
        codexCard.Controls.Add(_codexErrorLabel);

        _openCodeSummaryLabel = CreateLabel(string.Empty, 9, FontStyle.Regular);
        _openCodeTokensLabel = CreateLabel(string.Empty, 9, FontStyle.Regular);
        _openCodeCacheLabel = CreateLabel(string.Empty, 9, FontStyle.Regular);
        _openCodeErrorLabel = CreateErrorLabel();

        var openCodeCard = new SectionCard();
        _content.Controls.Add(openCodeCard);
        _openCodeHealthIndicator = AddProviderSectionHeader(openCodeCard, "OpenCode", ProviderIconKind.OpenCode);
        openCodeCard.Controls.Add(_openCodeSummaryLabel);
        openCodeCard.Controls.Add(_openCodeTokensLabel);
        openCodeCard.Controls.Add(_openCodeCacheLabel);
        openCodeCard.Controls.Add(CreateButton("Open Go Usage Dashboard", (_, _) => _openDashboard(), 250));
        openCodeCard.Controls.Add(_openCodeErrorLabel);

        _awakeLabel = CreateLabel(string.Empty, 9, FontStyle.Regular);
        var awakeCard = new SectionCard();
        _content.Controls.Add(awakeCard);
        AddSectionHeader(awakeCard, "Keep awake");
        awakeCard.Controls.Add(_awakeLabel);
        var awakeButtons = new FlowLayoutPanel
        {
            Width = 382,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 2)
        };
        awakeButtons.Controls.Add(CreateButton("Off", (_, _) => _setAwake(AwakeMode.Off, null), 55));
        awakeButtons.Controls.Add(CreateButton("System", (_, _) => _setAwake(AwakeMode.System, TimeSpan.FromHours(1)), 90));
        awakeButtons.Controls.Add(CreateButton("System + display", (_, _) => _setAwake(AwakeMode.SystemAndDisplay, TimeSpan.FromHours(1)), 180));
        awakeCard.Controls.Add(awakeButtons);
        var durationButtons = new FlowLayoutPanel
        {
            Width = 382,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 4)
        };
        durationButtons.Controls.Add(CreateButton("30m", (_, _) => _setAwake(AwakeMode.System, TimeSpan.FromMinutes(30)), 55));
        durationButtons.Controls.Add(CreateButton("2h", (_, _) => _setAwake(AwakeMode.System, TimeSpan.FromHours(2)), 55));
        durationButtons.Controls.Add(CreateButton("4h", (_, _) => _setAwake(AwakeMode.System, TimeSpan.FromHours(4)), 55));
        durationButtons.Controls.Add(CreateButton("Until off", (_, _) => _setAwake(AwakeMode.System, null), 100));
        awakeCard.Controls.Add(durationButtons);

        var footer = new TableLayoutPanel { Width = 402, Height = 40, ColumnCount = 3, Margin = new Padding(0, 4, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        _updatedLabel = CreateLabel(string.Empty, 8, FontStyle.Regular);
        footer.Controls.Add(_updatedLabel, 0, 0);
        footer.Controls.Add(CreateButton("Settings", (_, _) => _openSettings(), 105), 1, 0);
        footer.Controls.Add(CreateButton("Exit", (_, _) => _exit(), 60), 2, 0);
        _content.Controls.Add(footer);

        Resize += (_, _) => UpdateRoundedRegion();
        HandleCreated += (_, _) => UpdateRoundedRegion();
        DpiChanged += (_, _) => UpdateRoundedRegion();
        Shown += (_, _) => PositionNearCursor();
        KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Escape)
            {
                Hide();
            }
        };
        Deactivate += (_, _) => Hide();

        UpdateSnapshot(initialSnapshot);
        ApplyTheme();
    }

    public void ShowPopup()
    {
        if (IsDisposed)
        {
            return;
        }

        ApplyTheme();
        _content.AutoScrollPosition = Point.Empty;
        PerformLayout();
        PositionNearCursor();
        if (!Visible)
        {
            Show();
        }

        // Apply once more after handles exist so native controls also pick up
        // the active Windows theme.
        ApplyTheme();
        PositionNearCursor();
        BringToFront();
        Activate();
    }

    public void ApplyTheme()
    {
        WindowsTheme.Apply(this);
        Invalidate(true);
    }

    public void UpdateSnapshot(AppSnapshot snapshot)
    {
        if (IsDisposed)
        {
            return;
        }

        _snapshot = snapshot;
        var view = new StatusViewModel(snapshot, DateTimeOffset.UtcNow);
        _codexHealthIndicator.SetHealth(snapshot.CodexRateLimits.Health);
        _todayTokensLabel.Text = $"Today  {StatusViewModel.FormatTokens(snapshot.CodexTokenUsage.Value?.GetTokensForDate(DateOnly.FromDateTime(DateTime.Now)))} tokens";
        _lifetimeTokensLabel.Text = $"Lifetime  {StatusViewModel.FormatTokens(snapshot.CodexTokenUsage.Value?.LifetimeTokens)} tokens";
        _codexErrorLabel.Text = snapshot.CodexRateLimits.UserFacingError ?? snapshot.CodexAccount.UserFacingError ?? string.Empty;
        _codexErrorLabel.Visible = !string.IsNullOrWhiteSpace(_codexErrorLabel.Text);
        UpdateRateLimitRows(snapshot, view.Now);

        _openCodeHealthIndicator.SetHealth(snapshot.OpenCodeUsage.Health);
        _openCodeSummaryLabel.Text = $"Last 7 days  {view.OpenCodeSummary}";
        _openCodeTokensLabel.Text = view.OpenCodeTokens;
        _openCodeCacheLabel.Text = snapshot.OpenCodeUsage.Value is { } openCode
            ? $"Cache read / write  {StatusViewModel.FormatTokens(openCode.CacheReadTokens)} / {StatusViewModel.FormatTokens(openCode.CacheWriteTokens)}"
            : "Cache read / write  Not provided";
        _openCodeErrorLabel.Text = snapshot.OpenCodeUsage.UserFacingError ?? string.Empty;
        _openCodeErrorLabel.Visible = !string.IsNullOrWhiteSpace(_openCodeErrorLabel.Text);

        _awakeLabel.Text = view.AwakeSummary;
        _updatedLabel.Text = $"Updated {FormatAge(snapshot.CapturedAt, view.Now)}";
    }

    private void UpdateRateLimitRows(AppSnapshot snapshot, DateTimeOffset now)
    {
        _limitsPanel.SuspendLayout();
        _limitsPanel.Controls.Clear();
        var buckets = snapshot.CodexRateLimits.Value?.Buckets ?? [];
        var windows = buckets
            .SelectMany(bucket => new[]
            {
                (Bucket: bucket, Name: "Primary", Window: bucket.Primary),
                (Bucket: bucket, Name: "Secondary", Window: bucket.Secondary)
            })
            .Where(item => item.Window is not null)
            .ToArray();
        if (windows.Length == 0)
        {
            _limitsPanel.Controls.Add(CreateLabel("Quota windows: Not provided", 9, FontStyle.Regular));
        }
        else
        {
            foreach (var item in windows)
            {
                AddWindowRow(item.Bucket, item.Name, item.Window, now);
            }
        }

        _limitsPanel.ResumeLayout();
    }

    private void AddWindowRow(RateLimitBucket bucket, string fallbackName, RateLimitWindow? window, DateTimeOffset now)
    {
        if (window is null)
        {
            return;
        }

        var title = FormatLimitName(window.Duration, fallbackName);
        if (!string.IsNullOrWhiteSpace(bucket.DisplayName) &&
            !string.Equals(bucket.DisplayName, "codex", StringComparison.OrdinalIgnoreCase))
        {
            title = $"{bucket.DisplayName} · {title}";
        }

        var view = new QuotaUsageView(
            title,
            Math.Clamp(100 - window.UsedPercent, 0, 100),
            FormatQuotaReset(window.ResetsAt, now));
        _limitsPanel.Controls.Add(view);
        WindowsTheme.Apply(view);
    }

    private static void AddSectionHeader(FlowLayoutPanel target, string text)
    {
        var label = CreateLabel(text, 10, FontStyle.Bold, new Padding(0, 0, 0, 2));
        label.Tag = "accent";
        target.Controls.Add(label);
    }

    private static ProviderHealthIndicator AddProviderSectionHeader(
        FlowLayoutPanel target,
        string text,
        ProviderIconKind iconKind)
    {
        var label = CreateLabel(text, 10, FontStyle.Bold);
        label.Tag = "accent";
        label.Margin = new Padding(0, 1, 0, 0);
        var headerHeight = Math.Max(24, label.PreferredHeight + 2);
        var header = new FlowLayoutPanel
        {
            Width = 382,
            Height = headerHeight,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 2),
            Padding = new Padding(0)
        };
        var icon = new ProviderIcon(iconKind)
        {
            Margin = new Padding(0, Math.Max(0, (headerHeight - 22) / 2), 6, 0)
        };
        var health = new ProviderHealthIndicator
        {
            Margin = new Padding(6, Math.Max(0, (headerHeight - 18) / 2), 0, 0)
        };
        header.Controls.Add(icon);
        header.Controls.Add(label);
        header.Controls.Add(health);
        target.Controls.Add(header);
        return health;
    }

    private static FlowLayoutPanel CreateLimitsPanel()
    {
        return new FlowLayoutPanel
        {
            Width = 382,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 3, 0, 3),
            Padding = new Padding(0)
        };
    }

    private static string FormatLimitName(TimeSpan? duration, string fallbackName)
    {
        if (duration is null)
        {
            return $"{fallbackName} limit";
        }

        if (duration.Value.TotalDays is >= 6.5 and <= 7.5)
        {
            return "Weekly limit";
        }

        if (duration.Value.TotalHours < 24)
        {
            return $"{duration.Value.TotalHours:0.#}h limit";
        }

        return $"{duration.Value.TotalDays:0.#}d limit";
    }

    private static string FormatQuotaReset(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null)
        {
            return "Reset time not provided";
        }

        var localReset = reset.Value.ToLocalTime();
        var localNow = now.ToLocalTime();
        var difference = reset.Value - now;
        var relative = difference <= TimeSpan.Zero
            ? "now"
            : difference.TotalHours >= 24
                ? $"in {(int)difference.TotalDays}d"
                : $"in {Math.Max(1, (int)difference.TotalMinutes)}m";
        var resetTime = localReset.Date == localNow.Date
            ? localReset.ToString("h:mm tt")
            : localReset.ToString("h:mm tt 'on' MMM d");
        return $"Resets {resetTime} ({relative})";
    }

    private static Label CreateLabel(string text, float size, FontStyle style, Padding? margin = null)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(382, 0),
            Font = new Font("Segoe UI", size, style),
            Margin = margin ?? new Padding(0, 1, 0, 1),
            Padding = new Padding(0)
        };
    }

    private static Label CreateErrorLabel()
    {
        var label = CreateLabel(string.Empty, 8, FontStyle.Italic);
        label.Tag = "error";
        label.ForeColor = Color.FromArgb(171, 48, 48);
        return label;
    }

    private static Button CreateButton(string text, EventHandler click, int width = 100)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = Math.Max(34, TextRenderer.MeasureText(text, Control.DefaultFont).Height + 12),
            AutoSize = false,
            Margin = new Padding(0, 0, 5, 0),
            UseVisualStyleBackColor = true
        };
        button.Click += click;
        return button;
    }

    private void PositionNearCursor()
    {
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var maximumHeight = Math.Max(200, workingArea.Height - (ScreenEdgeGap * 2));
        var contentHeight = _content.Padding.Vertical
            + _content.Controls.Cast<Control>()
                .Where(control => control.Visible)
                .Sum(control => control.Height + control.Margin.Vertical)
            + 2;
        Height = Math.Min(Math.Max(MinimumPopupHeight, contentHeight), maximumHeight);

        var left = workingArea.Left + ScreenEdgeGap;
        var right = workingArea.Right - ScreenEdgeGap;
        var top = workingArea.Top + ScreenEdgeGap;
        var bottom = workingArea.Bottom - ScreenEdgeGap;
        var x = Math.Clamp(Cursor.Position.X - Width / 2, left, Math.Max(left, right - Width));
        var y = Math.Clamp(Cursor.Position.Y + 12, top, Math.Max(top, bottom - Height));
        Location = new Point(x, y);
    }

    private void UpdateRoundedRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        var radius = Math.Max(10, (int)Math.Round(12 * DeviceDpi / 96d));
        var bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        using var path = CreateRoundedRectangle(bounds, radius);
        var previousRegion = Region;
        Region = new Region(path);
        previousRegion?.Dispose();
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

    protected override CreateParams CreateParams
    {
        get
        {
            const int dropShadow = 0x00020000;
            var parameters = base.CreateParams;
            parameters.ClassStyle |= dropShadow;
            return parameters;
        }
    }

    private static string FormatAge(DateTimeOffset capturedAt, DateTimeOffset now)
    {
        var age = now - capturedAt;
        if (age < TimeSpan.FromSeconds(1))
        {
            return "just now";
        }

        return age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes}m ago" : $"{(int)age.TotalSeconds}s ago";
    }
}
