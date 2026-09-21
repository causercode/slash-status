using System.Drawing.Drawing2D;
using TokenStatus.Core.Models;

namespace TokenStatus.App.UI;

public sealed record RefreshOutcome(bool Succeeded, string Message);

public sealed class StatusPopupForm : Form
{
    private const int BasePopupWidth = 460;
    private const int MinimumPopupHeight = 460;
    private const int ScreenEdgeGap = 4;

    private readonly Func<Task<RefreshOutcome>> _refresh;
    private readonly Action _openSettings;
    private readonly Action _exit;
    private readonly Action<AwakeMode, TimeSpan?> _setAwake;
    private readonly VerticalStackLayout _content;
    private readonly Button _refreshButton;
    private readonly Label _liveStatusLabel;
    private readonly Label _updatedLabel;
    private readonly ProviderHealthIndicator _codexHealthIndicator;
    private readonly VerticalStackLayout _limitsPanel;
    private readonly Label _todayTokensLabel;
    private readonly Label _lifetimeTokensLabel;
    private readonly Label _codexErrorLabel;
    private readonly ProviderHealthIndicator _openCodeHealthIndicator;
    private readonly VerticalStackLayout _openCodeLimitsPanel;
    private readonly Label _openCodeQuotaErrorLabel;
    private readonly Button _openCodeSettingsButton;
    private readonly Label _awakeLabel;
    private readonly GroupBox _awakeDurationGroup;
    private readonly RadioButton _awakeOffRadio;
    private readonly RadioButton _awakeSystemRadio;
    private readonly RadioButton _awakeSystemAndDisplayRadio;
    private readonly RadioButton _awakeThirtyMinutesRadio;
    private readonly RadioButton _awakeOneHourRadio;
    private readonly RadioButton _awakeTwoHoursRadio;
    private readonly RadioButton _awakeFourHoursRadio;
    private readonly RadioButton _awakeUntilOffRadio;
    private readonly Button _applyAwakeButton;
    private readonly System.Windows.Forms.Timer _relativeTimeTimer;
#if DEBUG
    private readonly LayoutInspectorOverlay _layoutInspector;
#endif
    private AppSnapshot _snapshot;
    private bool _hasRenderedSnapshot;
    private bool _refreshInProgress;
    private bool _synchronizingAwakeSelection;
    private bool _awakeSelectionDirty;

    public StatusPopupForm(
        AppSnapshot initialSnapshot,
        Func<Task<RefreshOutcome>> refresh,
        Action openSettings,
        Action<AwakeMode, TimeSpan?> setAwake,
        Action exit)
    {
        _snapshot = initialSnapshot;
        _refresh = refresh;
        _openSettings = openSettings;
        _setAwake = setAwake;
        _exit = exit;

        Text = "/status usage";
        AccessibleName = "/status usage and keep-awake status";
        AccessibleDescription = "View provider quota, refresh status, and keep-awake controls.";
        AccessibleRole = AccessibleRole.Window;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        Tag = "chrome-border";
        Padding = new Padding(1);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        Width = BasePopupWidth;
        Height = 590;

        _content = new VerticalStackLayout
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoScroll = true,
            Padding = new Padding(
                LayoutMetrics.Medium,
                LayoutMetrics.Small,
                LayoutMetrics.Medium,
                LayoutMetrics.Small),
            Tag = "background"
        };
        Controls.Add(_content);

        var title = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, LayoutMetrics.XSmall),
            Padding = new Padding(LayoutMetrics.Large, 0, LayoutMetrics.Large, 0),
            AccessibleRole = AccessibleRole.Grouping,
            AccessibleName = "/status controls"
        };
        title.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        title.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        var titleLabel = CreateLabel(
            AppBrand.DisplayName,
            16,
            FontStyle.Bold,
            fontFamily: GetConsoleFontFamily());
        titleLabel.Tag = "accent";
        titleLabel.AccessibleName = AppBrand.DisplayName;
        title.Controls.Add(titleLabel, 0, 0);
        _refreshButton = CreateButton("&Refresh", async (_, _) => await RefreshClickedAsync().ConfigureAwait(true), 112);
        _refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _refreshButton.TabIndex = 0;
        _refreshButton.AccessibleDescription = "Refresh all provider data.";
        title.Controls.Add(_refreshButton, 1, 0);
        _content.AddRow(title);

        _liveStatusLabel = CreateLabel("Ready.", 8, FontStyle.Regular);
        _liveStatusLabel.Tag = "muted";
        _liveStatusLabel.AccessibleRole = AccessibleRole.StatusBar;
        _liveStatusLabel.AccessibleName = "Status updates";
        _liveStatusLabel.AccessibleDescription = "Current status and operation updates.";
        _content.AddRow(_liveStatusLabel);

        _codexErrorLabel = CreateErrorLabel("Codex error details");
        _limitsPanel = CreateLimitsPanel();
        _todayTokensLabel = CreateLabel(string.Empty, 9, FontStyle.Regular);
        _todayTokensLabel.AccessibleName = "Codex tokens today";
        _lifetimeTokensLabel = CreateLabel(string.Empty, 9, FontStyle.Regular);
        _lifetimeTokensLabel.AccessibleName = "Codex lifetime tokens";

        var codexCard = new SectionCard { AccessibleName = "Codex provider" };
        _content.AddRow(codexCard);
        _codexHealthIndicator = AddProviderSectionHeader(codexCard, "Codex", ProviderIconKind.Codex);
        codexCard.AddRow(_limitsPanel);
        codexCard.AddRow(_todayTokensLabel);
        codexCard.AddRow(_lifetimeTokensLabel);
        codexCard.AddRow(_codexErrorLabel);

        _openCodeLimitsPanel = CreateLimitsPanel();
        _openCodeQuotaErrorLabel = CreateErrorLabel("OpenCode Go error details");
        _openCodeSettingsButton = CreateButton("Open &provider settings", (_, _) => OpenSettings(), 168);
        _openCodeSettingsButton.TabIndex = 5;
        _openCodeSettingsButton.Visible = false;

        var openCodeCard = new SectionCard { AccessibleName = "OpenCode Go provider" };
        _content.AddRow(openCodeCard);
        _openCodeHealthIndicator = AddProviderSectionHeader(openCodeCard, "OpenCode Go", ProviderIconKind.OpenCode);
        openCodeCard.AddRow(_openCodeLimitsPanel);
        openCodeCard.AddRow(_openCodeQuotaErrorLabel);
        openCodeCard.AddRow(_openCodeSettingsButton);

        _awakeLabel = CreateLabel(string.Empty, 9, FontStyle.Regular);
        _awakeLabel.AccessibleName = "Keep awake status";
        _awakeLabel.AccessibleDescription = "The currently applied keep-awake mode and remaining time.";
        var awakeCard = new SectionCard { AccessibleName = "Keep awake" };
        _content.AddRow(awakeCard);
        AddSectionHeader(awakeCard, "Keep awake");
        awakeCard.AddRow(_awakeLabel);

        var awakeModeGroup = new GroupBox
        {
            Text = "Mode",
            AccessibleName = "Keep awake mode",
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(LayoutMetrics.Small, LayoutMetrics.Small, LayoutMetrics.Small, 0),
            Margin = new Padding(0, LayoutMetrics.XSmall, 0, 0)
        };
        var modePanel = CreateRadioPanel();
        _awakeOffRadio = CreateRadioButton("&Off", 1);
        _awakeSystemRadio = CreateRadioButton("&System", 2);
        _awakeSystemAndDisplayRadio = CreateRadioButton("System + &display", 3);
        modePanel.Controls.Add(_awakeOffRadio);
        modePanel.Controls.Add(_awakeSystemRadio);
        modePanel.Controls.Add(_awakeSystemAndDisplayRadio);
        awakeModeGroup.Controls.Add(modePanel);
        awakeCard.AddRow(awakeModeGroup);

        _awakeDurationGroup = new GroupBox
        {
            Text = "Duration",
            AccessibleName = "Keep awake duration",
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(LayoutMetrics.Small, LayoutMetrics.Small, LayoutMetrics.Small, 0),
            Margin = new Padding(0, LayoutMetrics.XSmall, 0, 0)
        };
        var durationPanel = CreateRadioPanel();
        _awakeThirtyMinutesRadio = CreateRadioButton("&30 minutes", 4);
        _awakeOneHourRadio = CreateRadioButton("&1 hour", 5);
        _awakeTwoHoursRadio = CreateRadioButton("&2 hours", 6);
        _awakeFourHoursRadio = CreateRadioButton("&4 hours", 7);
        _awakeUntilOffRadio = CreateRadioButton("Until turned o&ff", 8);
        durationPanel.Controls.Add(_awakeThirtyMinutesRadio);
        durationPanel.Controls.Add(_awakeOneHourRadio);
        durationPanel.Controls.Add(_awakeTwoHoursRadio);
        durationPanel.Controls.Add(_awakeFourHoursRadio);
        durationPanel.Controls.Add(_awakeUntilOffRadio);
        _awakeDurationGroup.Controls.Add(durationPanel);
        awakeCard.AddRow(_awakeDurationGroup);

        _applyAwakeButton = CreateButton("&Apply", (_, _) => ApplyAwakeSelection(), 95);
        _applyAwakeButton.TabIndex = 9;
        awakeCard.AddRow(_applyAwakeButton, stretch: false);

        _awakeOffRadio.CheckedChanged += (_, _) => AwakeSelectionChanged();
        _awakeSystemRadio.CheckedChanged += (_, _) => AwakeSelectionChanged();
        _awakeSystemAndDisplayRadio.CheckedChanged += (_, _) => AwakeSelectionChanged();
        _awakeThirtyMinutesRadio.CheckedChanged += (_, _) => AwakeSelectionChanged();
        _awakeOneHourRadio.CheckedChanged += (_, _) => AwakeSelectionChanged();
        _awakeTwoHoursRadio.CheckedChanged += (_, _) => AwakeSelectionChanged();
        _awakeFourHoursRadio.CheckedChanged += (_, _) => AwakeSelectionChanged();
        _awakeUntilOffRadio.CheckedChanged += (_, _) => AwakeSelectionChanged();

        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 3,
            Margin = new Padding(0, LayoutMetrics.XSmall, 0, 0),
            AccessibleRole = AccessibleRole.Grouping,
            AccessibleName = "Popup actions"
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        _updatedLabel = CreateLabel(string.Empty, 8, FontStyle.Regular);
        _updatedLabel.AccessibleName = "Last update time";
        footer.Controls.Add(_updatedLabel, 0, 0);
        var settingsButton = CreateButton("Settin&gs", (_, _) => OpenSettings(), 105);
        settingsButton.TabIndex = 10;
        footer.Controls.Add(settingsButton, 1, 0);
        var exitButton = CreateButton("E&xit", (_, _) => _exit(), 68);
        exitButton.TabIndex = 11;
        footer.Controls.Add(exitButton, 2, 0);
        _content.AddRow(footer);

        _relativeTimeTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _relativeTimeTimer.Tick += (_, _) => UpdateTimeLabels(DateTimeOffset.UtcNow);

#if DEBUG
        _layoutInspector = new LayoutInspectorOverlay(this);
        Disposed += (_, _) => _layoutInspector.Dispose();
        VisibleChanged += (_, _) =>
        {
            if (!Visible)
            {
                _layoutInspector.Disable();
            }
        };
#endif

        Resize += (_, _) =>
        {
            UpdateResponsiveLayout();
            UpdateRoundedRegion();
        };
        HandleCreated += (_, _) =>
        {
            UpdateResponsiveLayout();
            UpdateRoundedRegion();
        };
        DpiChanged += (_, _) =>
        {
            UpdateResponsiveLayout();
            UpdateRoundedRegion();
            PositionNearCursor();
        };
        Shown += (_, _) =>
        {
            PositionNearCursor();
            StartRelativeTimeTimer();
            BeginInvoke(FocusFirstControl);
        };
        VisibleChanged += (_, _) =>
        {
            if (Visible)
            {
                StartRelativeTimeTimer();
            }
            else
            {
                StopRelativeTimeTimer();
            }
        };
        KeyDown += (_, args) =>
        {
#if DEBUG
            if (args.Control && args.Shift && args.KeyCode == Keys.I)
            {
                _layoutInspector.Toggle();
                args.Handled = true;
                args.SuppressKeyPress = true;
                return;
            }
#endif
            if (args.KeyCode == Keys.Escape)
            {
                Hide();
            }
        };
        Deactivate += (_, _) =>
        {
#if DEBUG
            if (_layoutInspector.IsInspecting)
            {
                return;
            }
#endif
            if (Form.ActiveForm is { } activeForm && activeForm != this && activeForm.Visible)
            {
                return;
            }

            Hide();
        };

        SyncAwakeControls(initialSnapshot.Awake);
        UpdateSnapshot(initialSnapshot);
        ApplyTheme();
    }

    private static FlowLayoutPanel CreateRadioPanel() => new()
    {
        AutoSize = true,
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        Margin = new Padding(0),
        Padding = new Padding(0)
    };

    private static RadioButton CreateRadioButton(string text, int tabIndex) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(92, 30),
        Margin = new Padding(0, 0, LayoutMetrics.Medium, LayoutMetrics.XSmall),
        TabIndex = tabIndex,
        AccessibleName = text.Replace("&", string.Empty, StringComparison.Ordinal),
        UseVisualStyleBackColor = true
    };

    private async Task RefreshClickedAsync()
    {
        if (_refreshInProgress || IsDisposed)
        {
            return;
        }

        _refreshInProgress = true;
        SetRefreshBusy(true);
        SetLiveStatus("Refreshing provider data.");
        try
        {
            var result = await _refresh().ConfigureAwait(true);
            SetLiveStatus(string.IsNullOrWhiteSpace(result.Message)
                ? result.Succeeded ? "Updated just now." : "Refresh failed."
                : result.Message);
        }
        catch (Exception)
        {
            SetLiveStatus("Refresh failed. Showing the last available values.");
        }
        finally
        {
            _refreshInProgress = false;
            SetRefreshBusy(false);
        }
    }

    private void SetRefreshBusy(bool busy)
    {
        _refreshButton.Enabled = !busy;
        _refreshButton.Text = busy ? "Refreshing…" : "&Refresh";
        _refreshButton.AccessibleName = busy ? "Refreshing provider data" : "Refresh provider data";
        _refreshButton.AccessibleDescription = busy
            ? "Refresh is in progress."
            : "Refresh all provider data.";
    }

    private void SetLiveStatus(string message)
    {
        _liveStatusLabel.Text = message;
        _liveStatusLabel.AccessibleName = $"Status updates: {message}";
        _liveStatusLabel.AccessibleDescription = message;
        WindowsTheme.Apply(_liveStatusLabel);
    }

    private void OpenSettings()
    {
        Hide();
        _openSettings();
    }

    private void ApplyAwakeSelection()
    {
        var mode = _awakeOffRadio.Checked
            ? AwakeMode.Off
            : _awakeSystemAndDisplayRadio.Checked
                ? AwakeMode.SystemAndDisplay
                : AwakeMode.System;
        TimeSpan? duration = mode == AwakeMode.Off
            ? null
            : _awakeThirtyMinutesRadio.Checked
                ? TimeSpan.FromMinutes(30)
                : _awakeOneHourRadio.Checked
                    ? TimeSpan.FromHours(1)
                    : _awakeTwoHoursRadio.Checked
                        ? TimeSpan.FromHours(2)
                        : _awakeFourHoursRadio.Checked
                            ? TimeSpan.FromHours(4)
                            : null;

        try
        {
            _awakeSelectionDirty = false;
            _setAwake(mode, duration);
            SetLiveStatus(mode == AwakeMode.Off
                ? "Keep-awake mode turned off."
                : "Keep-awake mode applied.");
        }
        catch (Exception)
        {
            SyncAwakeControls(AwakeState.Off with
            {
                UserFacingError = "Windows could not enable keep-awake mode."
            });
            SetLiveStatus("Keep-awake mode could not be enabled.");
        }
    }

    private void AwakeSelectionChanged()
    {
        if (_synchronizingAwakeSelection)
        {
            return;
        }

        _awakeSelectionDirty = true;
        SetAwakeDurationEnabled(!_awakeOffRadio.Checked);
    }

    private void SyncAwakeControls(AwakeState state)
    {
        _synchronizingAwakeSelection = true;
        try
        {
            _awakeOffRadio.Checked = state.Mode == AwakeMode.Off;
            _awakeSystemRadio.Checked = state.Mode == AwakeMode.System;
            _awakeSystemAndDisplayRadio.Checked = state.Mode == AwakeMode.SystemAndDisplay;

            var duration = state.ExpiresAt is { } expiresAt && state.StartedAt is { } startedAt
                ? expiresAt - startedAt
                : (TimeSpan?)null;
            _awakeThirtyMinutesRadio.Checked = IsDuration(duration, TimeSpan.FromMinutes(30));
            _awakeOneHourRadio.Checked = IsDuration(duration, TimeSpan.FromHours(1));
            _awakeTwoHoursRadio.Checked = IsDuration(duration, TimeSpan.FromHours(2));
            _awakeFourHoursRadio.Checked = IsDuration(duration, TimeSpan.FromHours(4));
            _awakeUntilOffRadio.Checked = duration is null ||
                (!_awakeThirtyMinutesRadio.Checked &&
                 !_awakeOneHourRadio.Checked &&
                 !_awakeTwoHoursRadio.Checked &&
                 !_awakeFourHoursRadio.Checked);
            SetAwakeDurationEnabled(state.Mode != AwakeMode.Off);
            _awakeSelectionDirty = false;
        }
        finally
        {
            _synchronizingAwakeSelection = false;
        }
    }

    private static bool IsDuration(TimeSpan? actual, TimeSpan expected) =>
        actual is { } value && Math.Abs((value - expected).TotalSeconds) < 1;

    private void SetAwakeDurationEnabled(bool enabled)
    {
        _awakeDurationGroup.Enabled = true;
        _awakeDurationGroup.AccessibleDescription = enabled
            ? "Select how long Windows should stay awake."
            : "Choose System or System and display mode to select a duration.";

        _awakeThirtyMinutesRadio.Enabled = enabled;
        _awakeOneHourRadio.Enabled = enabled;
        _awakeTwoHoursRadio.Enabled = enabled;
        _awakeFourHoursRadio.Enabled = enabled;
        _awakeUntilOffRadio.Enabled = enabled;
        WindowsTheme.Apply(_awakeDurationGroup);
    }

    public void ShowPopup()
    {
        if (IsDisposed)
        {
            return;
        }

        ApplyTheme();
        SyncAwakeControls(_snapshot.Awake);
        _content.AutoScrollPosition = Point.Empty;
        UpdateResponsiveLayout();
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
        StartRelativeTimeTimer();
        BeginInvoke(FocusFirstControl);
    }

    public void ApplyTheme()
    {
        WindowsTheme.Apply(this);
        UpdateRoundedRegion();
        Invalidate(true);
    }

    public void FocusFirstControl()
    {
        if (!IsDisposed && Visible && _refreshButton.CanFocus)
        {
            _refreshButton.Focus();
        }
    }

    public void UpdateSnapshot(AppSnapshot snapshot)
    {
        if (IsDisposed)
        {
            return;
        }

        var previous = _hasRenderedSnapshot ? _snapshot : null;
        _snapshot = snapshot;
        _hasRenderedSnapshot = true;
        var view = new StatusViewModel(snapshot, DateTimeOffset.UtcNow);

        _codexHealthIndicator.SetHealth(snapshot.CodexRateLimits.Health);
        _todayTokensLabel.Text = $"Today  {StatusViewModel.FormatTokens(snapshot.CodexTokenUsage.Value?.GetTokensForDate(DateOnly.FromDateTime(DateTime.Now)))} tokens";
        _todayTokensLabel.AccessibleDescription = _todayTokensLabel.Text;
        _lifetimeTokensLabel.Text = $"Lifetime  {StatusViewModel.FormatTokens(snapshot.CodexTokenUsage.Value?.LifetimeTokens)} tokens";
        _lifetimeTokensLabel.AccessibleDescription = _lifetimeTokensLabel.Text;
        _codexErrorLabel.Text = snapshot.CodexRateLimits.UserFacingError ?? snapshot.CodexAccount.UserFacingError ?? string.Empty;
        _codexErrorLabel.AccessibleName = string.IsNullOrWhiteSpace(_codexErrorLabel.Text)
            ? "Codex error details"
            : $"Codex error: {_codexErrorLabel.Text}";
        _codexErrorLabel.AccessibleDescription = _codexErrorLabel.Text;
        _codexErrorLabel.Visible = !string.IsNullOrWhiteSpace(_codexErrorLabel.Text);
        if (previous is null || !Equals(previous.CodexRateLimits, snapshot.CodexRateLimits))
        {
            UpdateRateLimitRows(snapshot, view.Now);
        }

        _openCodeHealthIndicator.SetHealth(snapshot.OpenCodeGoQuota.Health);
        if (previous is null || !Equals(previous.OpenCodeGoQuota, snapshot.OpenCodeGoQuota))
        {
            UpdateOpenCodeQuotaRows(snapshot, view.Now);
        }

        _openCodeQuotaErrorLabel.Text = snapshot.OpenCodeGoQuota.Health == ProviderHealth.NotConfigured
            ? "OpenCode Go quota requires an API key."
            : snapshot.OpenCodeGoQuota.UserFacingError ?? string.Empty;
        _openCodeQuotaErrorLabel.AccessibleName = string.IsNullOrWhiteSpace(_openCodeQuotaErrorLabel.Text)
            ? "OpenCode Go error details"
            : $"OpenCode Go error: {_openCodeQuotaErrorLabel.Text}";
        _openCodeQuotaErrorLabel.AccessibleDescription = _openCodeQuotaErrorLabel.Text;
        _openCodeQuotaErrorLabel.Tag = snapshot.OpenCodeGoQuota.Health == ProviderHealth.NotConfigured ? "muted" : "error";
        _openCodeQuotaErrorLabel.Visible = !string.IsNullOrWhiteSpace(_openCodeQuotaErrorLabel.Text);
        _openCodeSettingsButton.Visible = snapshot.OpenCodeGoQuota.Health == ProviderHealth.NotConfigured;
        WindowsTheme.Apply(_openCodeQuotaErrorLabel);

        _awakeLabel.Text = view.AwakeSummary;
        _awakeLabel.Tag = snapshot.Awake.UserFacingError is null ? null : "error";
        WindowsTheme.Apply(_awakeLabel);
        if (previous is null || !Equals(previous.Awake, snapshot.Awake))
        {
            if (!_awakeSelectionDirty || snapshot.Awake.UserFacingError is not null)
            {
                SyncAwakeControls(snapshot.Awake);
            }
        }

        if (previous is not null)
        {
            if (previous.CodexRateLimits.Health != snapshot.CodexRateLimits.Health)
            {
                SetLiveStatus($"Codex status: {StatusViewModel.DescribeHealth(snapshot.CodexRateLimits.Health)}.");
            }
            else if (previous.OpenCodeGoQuota.Health != snapshot.OpenCodeGoQuota.Health)
            {
                SetLiveStatus($"OpenCode Go status: {StatusViewModel.DescribeHealth(snapshot.OpenCodeGoQuota.Health)}.");
            }
            else if (snapshot.Awake.UserFacingError is not null &&
                     previous.Awake.UserFacingError != snapshot.Awake.UserFacingError)
            {
                SetLiveStatus(snapshot.Awake.UserFacingError);
            }
        }

        UpdateTimeLabels(view.Now);
        UpdateResponsiveLayout();
    }

    private void UpdateOpenCodeQuotaRows(AppSnapshot snapshot, DateTimeOffset now)
    {
        _openCodeLimitsPanel.SuspendLayout();
        _openCodeLimitsPanel.ClearRows();
        if (snapshot.OpenCodeGoQuota.Value is not { } quota)
        {
            _openCodeLimitsPanel.AddRow(CreateLabel("Go subscription quota: Not provided", 9, FontStyle.Regular));
        }
        else
        {
            AddOpenCodeWindowRow("5-hour limit", quota.Rolling, now);
            AddOpenCodeWindowRow("Weekly limit", quota.Weekly, now);
            AddOpenCodeWindowRow("Monthly limit", quota.Monthly, now);
        }

        _openCodeLimitsPanel.ResumeLayout();
    }

    private void AddOpenCodeWindowRow(string title, OpenCodeQuotaWindow window, DateTimeOffset now)
    {
        var view = new QuotaUsageView(title, window.RemainingPercent, FormatQuotaReset(window.ResetsAt, now));
        _openCodeLimitsPanel.AddRow(view);
        WindowsTheme.Apply(view);
    }

    private void UpdateRateLimitRows(AppSnapshot snapshot, DateTimeOffset now)
    {
        _limitsPanel.SuspendLayout();
        _limitsPanel.ClearRows();
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
            _limitsPanel.AddRow(CreateLabel("Quota windows: Not provided", 9, FontStyle.Regular));
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
            title = $"{bucket.DisplayName} - {title}";
        }

        var view = new QuotaUsageView(
            title,
            Math.Clamp(100 - window.UsedPercent, 0, 100),
            FormatQuotaReset(window.ResetsAt, now));
        _limitsPanel.AddRow(view);
        WindowsTheme.Apply(view);
    }

    private static void AddSectionHeader(SectionCard target, string text)
    {
        var label = CreateLabel(text, 10, FontStyle.Bold, new Padding(0, 0, 0, LayoutMetrics.XSmall));
        label.Tag = "accent";
        label.AccessibleName = text;
        target.AddRow(label);
    }

    private static ProviderHealthIndicator AddProviderSectionHeader(
        SectionCard target,
        string text,
        ProviderIconKind iconKind)
    {
        var label = CreateLabel(text, 10, FontStyle.Bold);
        label.Tag = "accent";
        label.AccessibleName = text;
        label.Margin = new Padding(0, 1, 0, 0);
        var header = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, LayoutMetrics.XSmall),
            Padding = new Padding(0),
            AccessibleRole = AccessibleRole.Grouping,
            AccessibleName = $"{text} provider status"
        };
        var icon = new ProviderIcon(iconKind)
        {
            Margin = new Padding(0, 0, 6, 0)
        };
        var health = new ProviderHealthIndicator(text)
        {
            Margin = new Padding(6, 0, 0, 0)
        };
        header.Controls.Add(icon);
        header.Controls.Add(label);
        header.Controls.Add(health);
        target.AddRow(header);
        return health;
    }

    private static VerticalStackLayout CreateLimitsPanel() => new()
    {
        Margin = new Padding(0, LayoutMetrics.XSmall, 0, LayoutMetrics.XSmall),
        Padding = new Padding(0)
    };

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

    private static string GetConsoleFontFamily()
    {
        var installedFamilies = FontFamily.Families;
        return installedFamilies.Any(family => family.Name.Equals("Cascadia Mono", StringComparison.OrdinalIgnoreCase))
            ? "Cascadia Mono"
            : installedFamilies.Any(family => family.Name.Equals("Consolas", StringComparison.OrdinalIgnoreCase))
                ? "Consolas"
                : FontFamily.GenericMonospace.Name;
    }

    private static Label CreateLabel(
        string text,
        float size,
        FontStyle style,
        Padding? margin = null,
        string fontFamily = "Segoe UI") => new()
        {
            Text = text,
            AutoSize = true,
            Font = new Font(fontFamily, size, style),
            Margin = margin ?? new Padding(0),
            Padding = new Padding(0),
            TabStop = false
        };

    private static Label CreateErrorLabel(string accessibleName)
    {
        var label = CreateLabel(string.Empty, 8, FontStyle.Italic);
        label.Tag = "error";
        label.AccessibleName = accessibleName;
        label.MaximumSize = new Size(0, 0);
        return label;
    }

    private static Button CreateButton(string text, EventHandler click, int minimumWidth = 100)
    {
        var name = text.Replace("&", string.Empty, StringComparison.Ordinal);
        var button = new Button
        {
            Text = text,
            MinimumSize = new Size(minimumWidth, 36),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            Margin = new Padding(0, 0, LayoutMetrics.Small, 0),
            UseVisualStyleBackColor = true,
            AccessibleRole = AccessibleRole.PushButton,
            AccessibleName = name,
            TabStop = true
        };
        button.Click += click;
        return button;
    }

    private void UpdateTimeLabels(DateTimeOffset now)
    {
        var view = new StatusViewModel(_snapshot, now);
        _updatedLabel.Text = $"Updated {FormatAge(_snapshot.CapturedAt, now)}";
        _updatedLabel.AccessibleDescription = _updatedLabel.Text;
        _awakeLabel.Text = view.AwakeSummary;
        _awakeLabel.AccessibleDescription = view.AwakeSummary;
        _awakeLabel.Tag = _snapshot.Awake.UserFacingError is null ? null : "error";
        WindowsTheme.Apply(_updatedLabel);
        WindowsTheme.Apply(_awakeLabel);
    }

    private void StartRelativeTimeTimer()
    {
        if (IsDisposed || !Visible)
        {
            return;
        }

        UpdateTimeLabels(DateTimeOffset.UtcNow);
        _relativeTimeTimer.Start();
    }

    private void StopRelativeTimeTimer() => _relativeTimeTimer.Stop();

    private void UpdateResponsiveLayout()
    {
        if (ClientSize.Width <= 0)
        {
            return;
        }

        var maximumWidth = Math.Max(180, ClientSize.Width - Padding.Horizontal - LayoutMetrics.Large * 2);
        foreach (var control in Descendants(this))
        {
            if (control is Label label &&
                (label.Tag is "error" || label.AccessibleRole == AccessibleRole.StatusBar))
            {
                label.MaximumSize = new Size(maximumWidth, 0);
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private void PositionNearCursor()
    {
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var maximumWidth = Math.Max(200, workingArea.Width - ScreenEdgeGap * 2);
        var targetWidth = Math.Min(Math.Max(280, ScaleDimension(BasePopupWidth)), maximumWidth);
        Width = targetWidth;
        UpdateResponsiveLayout();
        PerformLayout();

        var maximumHeight = Math.Max(200, workingArea.Height - ScreenEdgeGap * 2);
        var measuredContentHeight = _content.Padding.Vertical
            + _content.Controls.Cast<Control>()
                .Where(control => control.Visible)
                .Sum(control => control.PreferredSize.Height + control.Margin.Vertical)
            + 2;
        var contentHeight = Math.Max(measuredContentHeight, _content.PreferredSize.Height + Padding.Vertical);
        _content.AutoScroll = contentHeight > maximumHeight;
        Height = Math.Min(Math.Max(ScaleDimension(MinimumPopupHeight), contentHeight), maximumHeight);
        PerformLayout();

        var left = workingArea.Left + ScreenEdgeGap;
        var right = workingArea.Right - ScreenEdgeGap;
        var top = workingArea.Top + ScreenEdgeGap;
        var bottom = workingArea.Bottom - ScreenEdgeGap;
        var x = Math.Clamp(Cursor.Position.X - Width / 2, left, Math.Max(left, right - Width));
        var y = Math.Clamp(Cursor.Position.Y + 12, top, Math.Max(top, bottom - Height));
        Location = new Point(x, y);
    }

    private int ScaleDimension(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96d));

    private void UpdateRoundedRegion()
    {
        var previousRegion = Region;
        if (WindowsTheme.IsHighContrastEnabled || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            Region = null;
            previousRegion?.Dispose();
            return;
        }

        var radius = Math.Max(10, (int)Math.Round(12 * DeviceDpi / 96d));
        var bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        using var path = CreateRoundedRectangle(bounds, radius);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _relativeTimeTimer.Dispose();
        }

        base.Dispose(disposing);
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
