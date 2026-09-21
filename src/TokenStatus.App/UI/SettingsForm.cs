using System.Diagnostics;
using Microsoft.Win32;
using TokenStatus.Core.Models;
using TokenStatus.Infrastructure.Cli;

namespace TokenStatus.App.UI;

public sealed class SettingsForm : Form
{
    private readonly TextBox _codexPath;
    private PaddedTextBox _openCodeGoApiKey = null!;
    private CheckBox _showOpenCodeGoApiKey = null!;
    private Label _openCodeGoApiKeyState = null!;
    private readonly NumericUpDown _codexLimitsSeconds;
    private readonly NumericUpDown _codexUsageSeconds;
    private readonly NumericUpDown _openCodeSeconds;
    private readonly CheckBox _quotaNotifications;
    private readonly CheckBox _startWithWindows;
    private readonly Func<AppSettings, string?, bool, Task> _save;
    private readonly Func<string?, CancellationToken, Task<OpenCodeGoQuota>> _testOpenCodeGoApiKey;
    private readonly bool _openCodeGoApiKeyConfigured;
    private readonly string _logDirectory;
    private readonly Button _saveButton;
    private Button _testOpenCodeGoApiKeyButton = null!;
    private Button _clearOpenCodeGoApiKeyButton = null!;
    private readonly Button _cancelButton;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _deleteOpenCodeGoApiKey;
    private int _testInProgress;
    private int _saveInProgress;
    private int _lifetimeDisposed;
    private bool _allowClose;

    public SettingsForm(
        AppSettings settings,
        bool openCodeGoApiKeyConfigured,
        Func<string?, CancellationToken, Task<OpenCodeGoQuota>> testOpenCodeGoApiKey,
        Func<AppSettings, string?, bool, Task> save,
        string logDirectory)
    {
        _save = save;
        _testOpenCodeGoApiKey = testOpenCodeGoApiKey;
        _openCodeGoApiKeyConfigured = openCodeGoApiKeyConfigured;
        _logDirectory = logDirectory;

        Text = $"{AppBrand.DisplayName} settings";
        AccessibleName = $"{AppBrand.DisplayName} settings";
        AccessibleDescription = "Configure providers, refresh intervals, notifications, startup, and diagnostics.";
        AccessibleRole = AccessibleRole.Window;
        AutoScaleMode = AutoScaleMode.Dpi;
        Width = 760;
        Height = 680;
        MinimumSize = new Size(620, 520);
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;

        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(LayoutMetrics.Large),
            AccessibleRole = AccessibleRole.Pane,
            AccessibleName = "Settings content"
        };
        var content = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(content);
        Controls.Add(scroll);

        _codexPath = new PaddedTextBox
        {
            Text = settings.CodexExecutablePath ?? string.Empty,
            Dock = DockStyle.Top,
            AccessibleName = "Codex executable path",
            AccessibleDescription = "Leave blank to detect the Codex executable automatically."
        };
        AddProviderSection(content);

        _codexLimitsSeconds = CreateSecondsControl(
            settings.CodexRateLimitRefreshSeconds,
            AppSettings.MinimumProviderRefreshSeconds,
            "Codex quota refresh interval");
        _codexUsageSeconds = CreateSecondsControl(
            settings.CodexUsageRefreshSeconds,
            AppSettings.MinimumTokenUsageRefreshSeconds,
            "Token activity refresh interval");
        _openCodeSeconds = CreateSecondsControl(
            settings.OpenCodeRefreshSeconds,
            AppSettings.MinimumProviderRefreshSeconds,
            "OpenCode Go quota refresh interval");
        AddRefreshSection(content);

        _quotaNotifications = new CheckBox
        {
            Text = "Notify at 25% and 5% remaining, and when quota resets",
            Checked = settings.QuotaNotificationsEnabled,
            AutoSize = true,
            AccessibleName = "Quota notifications"
        };
        _startWithWindows = new CheckBox
        {
            Text = "Start with Windows",
            Checked = settings.StartWithWindows,
            AutoSize = true,
            AccessibleName = "Start with Windows"
        };
        AddNotificationsSection(content);
        AddDiagnosticsSection(content);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, LayoutMetrics.Medium, 0, 0),
            AccessibleRole = AccessibleRole.Grouping,
            AccessibleName = "Settings actions"
        };
        _cancelButton = CreateButton("&Cancel", 95);
        _cancelButton.DialogResult = DialogResult.Cancel;
        _cancelButton.TabIndex = 30;
        _saveButton = CreateButton("&Save", 95);
        _saveButton.TabIndex = 31;
        _saveButton.Click += SaveClicked;
        buttons.Controls.Add(_cancelButton);
        buttons.Controls.Add(_saveButton);
        content.Controls.Add(buttons);

        AcceptButton = _saveButton;
        CancelButton = _cancelButton;
        SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
        Shown += (_, _) =>
        {
            WindowsTheme.Apply(this);
            PositionOnInvokingMonitor();
            _codexPath.Focus();
        };
        DpiChanged += (_, _) =>
        {
            WindowsTheme.Apply(this);
            PositionOnInvokingMonitor();
        };
        WindowsTheme.Apply(this);
    }

    private void AddProviderSection(TableLayoutPanel content)
    {
        var section = CreateSection("&Providers");
        var layout = CreateSectionLayout();
        AddPathRow(layout, 0, "Codex executable path", _codexPath);

        _openCodeGoApiKey = new PaddedTextBox
        {
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = true,
            AccessibleName = "OpenCode Go API key",
            AccessibleDescription = "The API key is stored in Windows Credential Manager and is masked by default.",
            PlaceholderText = _openCodeGoApiKeyConfigured
                ? "Configured - leave blank to keep"
                : "Paste OpenCode Go API key"
        };
        _openCodeGoApiKey.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_openCodeGoApiKey.Text))
            {
                _deleteOpenCodeGoApiKey = false;
            }

            UpdateOpenCodeGoApiKeyState();
        };
        AddFieldLabel(layout, 1, "OpenCode Go API key");
        layout.Controls.Add(_openCodeGoApiKey, 1, 1);
        var keyButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight
        };
        _testOpenCodeGoApiKeyButton = CreateButton("&Test", 64);
        _testOpenCodeGoApiKeyButton.TabIndex = 6;
        _testOpenCodeGoApiKeyButton.Click += TestOpenCodeGoApiKeyClicked;
        _clearOpenCodeGoApiKeyButton = CreateButton("Clear", 64);
        _clearOpenCodeGoApiKeyButton.TabIndex = 7;
        _clearOpenCodeGoApiKeyButton.Click += (_, _) => MarkOpenCodeGoApiKeyForDeletion();
        keyButtons.Controls.Add(_testOpenCodeGoApiKeyButton);
        keyButtons.Controls.Add(_clearOpenCodeGoApiKeyButton);
        layout.Controls.Add(keyButtons, 2, 1);

        _showOpenCodeGoApiKey = new CheckBox
        {
            Text = "Show API key",
            AutoSize = true,
            AccessibleName = "Show OpenCode Go API key",
            AccessibleDescription = "Toggle whether the API key text is visible.",
            TabIndex = 8
        };
        _showOpenCodeGoApiKey.CheckedChanged += (_, _) =>
            _openCodeGoApiKey.UseSystemPasswordChar = !_showOpenCodeGoApiKey.Checked;
        layout.Controls.Add(_showOpenCodeGoApiKey, 1, 2);

        _openCodeGoApiKeyState = new Label
        {
            AutoSize = true,
            Text = string.Empty,
            Tag = "muted",
            AccessibleName = "OpenCode Go API key status",
            TabStop = false,
            Margin = new Padding(0, 0, 0, LayoutMetrics.XSmall)
        };
        layout.Controls.Add(_openCodeGoApiKeyState, 1, 3);

        section.Controls.Add(layout);
        content.Controls.Add(section);
        UpdateOpenCodeGoApiKeyState();
    }

    private void AddRefreshSection(TableLayoutPanel content)
    {
        var section = CreateSection("&Refresh intervals");
        var layout = CreateSectionLayout();
        AddSecondsRow(layout, 0, "Codex quota refresh interval", _codexLimitsSeconds);
        AddSecondsRow(layout, 1, "Token activity refresh interval", _codexUsageSeconds);
        AddSecondsRow(layout, 2, "OpenCode Go quota refresh interval", _openCodeSeconds);
        section.Controls.Add(layout);
        content.Controls.Add(section);
    }

    private void AddNotificationsSection(TableLayoutPanel content)
    {
        var section = CreateSection("&Notifications and startup");
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        panel.Controls.Add(_quotaNotifications);
        panel.Controls.Add(_startWithWindows);
        section.Controls.Add(panel);
        content.Controls.Add(section);
    }

    private void AddDiagnosticsSection(TableLayoutPanel content)
    {
        var section = CreateSection("&Diagnostics");
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        var logButton = CreateButton("&Open log folder", 150);
        logButton.Enabled = !string.IsNullOrWhiteSpace(_logDirectory);
        logButton.AccessibleDescription = logButton.Enabled
            ? _logDirectory
            : "File logging is unavailable.";
        logButton.Click += (_, _) => OpenLogFolder();
        panel.Controls.Add(logButton);
        var help = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(660, 0),
            Text = "Logs contain redacted diagnostics and never contain provider credentials.",
            Tag = "muted",
            AccessibleName = "Diagnostics help"
        };
        panel.Controls.Add(help);
        section.Controls.Add(panel);
        content.Controls.Add(section);
    }

    private static GroupBox CreateSection(string title) => new()
    {
        Text = title,
        AutoSize = true,
        Dock = DockStyle.Top,
        Padding = new Padding(LayoutMetrics.Medium, LayoutMetrics.Large, LayoutMetrics.Medium, LayoutMetrics.Small),
        Margin = new Padding(0, 0, 0, LayoutMetrics.Medium),
        AccessibleName = title.Replace("&", string.Empty, StringComparison.Ordinal)
    };

    private static TableLayoutPanel CreateSectionLayout() => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top,
        ColumnCount = 3,
        GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        Margin = new Padding(0),
        Padding = new Padding(0)
    };

    private static void ConfigureSectionColumns(TableLayoutPanel layout)
    {
        if (layout.ColumnStyles.Count == 0)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        }
    }

    private void TestOpenCodeGoApiKeyClicked(object? sender, EventArgs e)
    {
        _ = TestOpenCodeGoApiKeyWorkflowAsync();
    }

    private async Task TestOpenCodeGoApiKeyWorkflowAsync()
    {
        if (Interlocked.CompareExchange(ref _testInProgress, 1, 0) != 0 ||
            Volatile.Read(ref _saveInProgress) != 0)
        {
            return;
        }

        var enteredKey = _openCodeGoApiKey.Text.Trim();
        if (enteredKey.Length == 0 && (!_openCodeGoApiKeyConfigured || _deleteOpenCodeGoApiKey))
        {
            Interlocked.Exchange(ref _testInProgress, 0);
            MessageBox.Show(
                this,
                "Enter an OpenCode Go API key first.",
                AppBrand.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            _openCodeGoApiKey.Focus();
            return;
        }

        SetTestInProgress(true);
        var lifetimeToken = _lifetimeCancellation.Token;
        try
        {
            var quota = await _testOpenCodeGoApiKey(
                enteredKey.Length == 0 ? null : enteredKey,
                lifetimeToken).ConfigureAwait(true);
            if (!CanUpdateUi)
            {
                return;
            }

            MessageBox.Show(
                this,
                $"OpenCode Go connected.\n\n5-hour: {quota.Rolling.UsedPercent:0.#}% used\nWeekly: {quota.Weekly.UsedPercent:0.#}% used\nMonthly: {quota.Monthly.UsedPercent:0.#}% used",
                AppBrand.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            // Closing the form is the cancellation action for a connection test.
        }
        catch (ProviderFailureException exception)
        {
            if (CanUpdateUi)
            {
                MessageBox.Show(this, exception.UserFacingMessage, "OpenCode Go connection failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception)
        {
            if (CanUpdateUi)
            {
                MessageBox.Show(
                    this,
                    "OpenCode Go could not be reached. Check the connection and try again.",
                    "OpenCode Go connection failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        finally
        {
            if (CanUpdateUi)
            {
                SetTestInProgress(false);
            }

            Interlocked.Exchange(ref _testInProgress, 0);
        }
    }

    private void MarkOpenCodeGoApiKeyForDeletion()
    {
        _openCodeGoApiKey.Clear();
        _deleteOpenCodeGoApiKey = true;
        _openCodeGoApiKey.PlaceholderText = "Will be removed when settings are saved";
        UpdateOpenCodeGoApiKeyState();
    }

    private void UpdateOpenCodeGoApiKeyState()
    {
        if (_openCodeGoApiKeyState is null)
        {
            return;
        }

        _openCodeGoApiKeyState.Text = _deleteOpenCodeGoApiKey
            ? "Will be removed when settings are saved"
            : !string.IsNullOrWhiteSpace(_openCodeGoApiKey.Text) || _openCodeGoApiKeyConfigured
                ? "Configured"
                : "Not configured";
        _openCodeGoApiKeyState.AccessibleDescription = _openCodeGoApiKeyState.Text;
        WindowsTheme.Apply(_openCodeGoApiKeyState);
    }

    private static NumericUpDown CreateSecondsControl(int value, int minimum, string accessibleName)
    {
        return new PaddedNumericUpDown
        {
            Minimum = minimum,
            Maximum = 86400,
            Increment = 30,
            Value = Math.Clamp(value, minimum, 86400),
            Dock = DockStyle.Left,
            Width = 120,
            AccessibleName = accessibleName,
            AccessibleDescription = "Enter a refresh interval in seconds."
        };
    }

    private void AddPathRow(TableLayoutPanel layout, int row, string label, TextBox textBox)
    {
        AddFieldLabel(layout, row, label);
        var pathPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0),
            AccessibleRole = AccessibleRole.Pane,
            AccessibleName = "Codex executable path and resolution"
        };
        pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pathPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var resolutionLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(0, 24),
            AutoEllipsis = true,
            AccessibleName = "Codex executable resolution",
            Tag = "muted",
            Margin = new Padding(0, LayoutMetrics.XSmall, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft
        };
        void UpdateResolutionLabel()
        {
            var resolution = ExecutableResolver.ResolveCodexWithProvenance(textBox.Text);
            resolutionLabel.Text = resolution is null
                ? "Automatic resolution: executable not found. Leave blank to auto-detect."
                : $"{resolution.ProvenanceLabel}: {resolution.Path} (blank uses automatic detection)";
            resolutionLabel.AccessibleDescription = resolutionLabel.Text;
        }

        textBox.Dock = DockStyle.Fill;
        textBox.Margin = new Padding(0);
        textBox.TabIndex = 1;
        textBox.TextChanged += (_, _) => UpdateResolutionLabel();
        pathPanel.Controls.Add(textBox, 0, 0);
        pathPanel.Controls.Add(resolutionLabel, 0, 1);
        layout.Controls.Add(pathPanel, 1, row);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight
        };
        var auto = CreateButton("Auto", 58);
        auto.TabIndex = 2;
        auto.AccessibleDescription = "Clear the path and use automatic Codex detection.";
        auto.Click += (_, _) => textBox.Clear();
        var test = CreateButton("Test", 58);
        test.TabIndex = 3;
        test.Click += (_, _) => TestPath(textBox.Text, label);
        buttons.Controls.Add(auto);
        buttons.Controls.Add(test);
        layout.Controls.Add(buttons, 2, row);
        UpdateResolutionLabel();
    }

    private static void AddSecondsRow(TableLayoutPanel layout, int row, string label, NumericUpDown control)
    {
        AddFieldLabel(layout, row, label);
        var valuePanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight
        };
        valuePanel.Controls.Add(control);
        valuePanel.Controls.Add(new Label
        {
            Text = "seconds",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(LayoutMetrics.Small, 7, 0, 0),
            AccessibleName = "Units: seconds",
            TabStop = false
        });
        layout.Controls.Add(valuePanel, 1, row);
        ConfigureSectionColumns(layout);
    }

    private static void AddFieldLabel(TableLayoutPanel layout, int row, string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = text,
            TabStop = false,
            Margin = new Padding(0, 7, LayoutMetrics.Small, 7)
        };
        layout.Controls.Add(label, 0, row);
        ConfigureSectionColumns(layout);
        while (layout.RowCount <= row)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowCount++;
        }
    }

    private static Button CreateButton(string text, int minimumWidth)
    {
        var name = text.Replace("&", string.Empty, StringComparison.Ordinal);
        return new Button
        {
            Text = text,
            MinimumSize = new Size(minimumWidth, 34),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            UseVisualStyleBackColor = true,
            AccessibleRole = AccessibleRole.PushButton,
            AccessibleName = name,
            TabStop = true,
            Padding = new Padding(LayoutMetrics.Small, 0, LayoutMetrics.Small, 0),
            Margin = new Padding(0, 0, LayoutMetrics.Small, 0)
        };
    }

    private static void TestPath(string path, string label)
    {
        var exists = !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path);
        MessageBox.Show(
            exists ? $"{label} was found." : $"{label} was not found. Leave it blank to auto-detect.",
            AppBrand.DisplayName,
            MessageBoxButtons.OK,
            exists ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void SaveClicked(object? sender, EventArgs e)
    {
        _ = SaveWorkflowAsync();
    }

    private async Task SaveWorkflowAsync()
    {
        if (Interlocked.CompareExchange(ref _saveInProgress, 1, 0) != 0 ||
            Volatile.Read(ref _testInProgress) != 0)
        {
            return;
        }

        var codexPath = NormalizePath(_codexPath.Text, "Codex executable");
        if (codexPath is null && !string.IsNullOrWhiteSpace(_codexPath.Text))
        {
            _codexPath.Focus();
            Interlocked.Exchange(ref _saveInProgress, 0);
            return;
        }

        SetSaveInProgress(true);
        try
        {
            var settings = new AppSettings
            {
                CodexExecutablePath = codexPath,
                CodexRateLimitRefreshSeconds = (int)_codexLimitsSeconds.Value,
                CodexUsageRefreshSeconds = (int)_codexUsageSeconds.Value,
                OpenCodeRefreshSeconds = (int)_openCodeSeconds.Value,
                QuotaNotificationsEnabled = _quotaNotifications.Checked,
                StartWithWindows = _startWithWindows.Checked
            }.Normalize();
            var enteredKey = _openCodeGoApiKey.Text.Trim();
            await _save(
                settings,
                enteredKey.Length == 0 ? null : enteredKey,
                _deleteOpenCodeGoApiKey).ConfigureAwait(true);
            if (!CanUpdateUi)
            {
                return;
            }

            _allowClose = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            if (CanUpdateUi)
            {
                MessageBox.Show(this, exception.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (CanUpdateUi)
            {
                SetSaveInProgress(false);
            }

            Interlocked.Exchange(ref _saveInProgress, 0);
        }
    }

    private void SetTestInProgress(bool inProgress)
    {
        _testOpenCodeGoApiKeyButton.Enabled = !inProgress;
        _clearOpenCodeGoApiKeyButton.Enabled = !inProgress;
        _openCodeGoApiKey.Enabled = !inProgress;
        _showOpenCodeGoApiKey.Enabled = !inProgress;
        _saveButton.Enabled = !inProgress;
    }

    private void SetSaveInProgress(bool inProgress)
    {
        _codexPath.Enabled = !inProgress;
        _openCodeGoApiKey.Enabled = !inProgress;
        _showOpenCodeGoApiKey.Enabled = !inProgress;
        _codexLimitsSeconds.Enabled = !inProgress;
        _codexUsageSeconds.Enabled = !inProgress;
        _openCodeSeconds.Enabled = !inProgress;
        _quotaNotifications.Enabled = !inProgress;
        _startWithWindows.Enabled = !inProgress;
        _testOpenCodeGoApiKeyButton.Enabled = !inProgress;
        _clearOpenCodeGoApiKeyButton.Enabled = !inProgress;
        _cancelButton.Enabled = !inProgress;
        _saveButton.Enabled = !inProgress;
        ControlBox = !inProgress;
    }

    private bool CanUpdateUi => !IsDisposed && !Disposing;

    private static string? NormalizePath(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            MessageBox.Show($"{label} must be an absolute path.", AppBrand.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        return trimmed;
    }

    private void OpenLogFolder()
    {
        if (string.IsNullOrWhiteSpace(_logDirectory))
        {
            MessageBox.Show(
                this,
                "File logging is unavailable on this computer.",
                AppBrand.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(_logDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _logDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not open log folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PositionOnInvokingMonitor()
    {
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = workingArea.Left + Math.Max(0, (workingArea.Width - Width) / 2);
        var y = workingArea.Top + Math.Max(0, (workingArea.Height - Height) / 2);
        Location = new Point(x, y);
    }

    private void UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Color or
            UserPreferenceCategory.General or
            UserPreferenceCategory.VisualStyle or
            UserPreferenceCategory.Accessibility)
        {
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(new Action(() => WindowsTheme.Apply(this)));
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (Volatile.Read(ref _saveInProgress) != 0 && !_allowClose)
        {
            e.Cancel = true;
            return;
        }

        if (!_allowClose)
        {
            _lifetimeCancellation.Cancel();
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
            if (Interlocked.Exchange(ref _lifetimeDisposed, 1) == 0)
            {
                _lifetimeCancellation.Cancel();
                _lifetimeCancellation.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
