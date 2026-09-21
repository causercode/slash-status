using System.Diagnostics;
using TokenStatus.Core.Models;
using TokenStatus.Infrastructure.Cli;

namespace TokenStatus.App.UI;

public sealed class SettingsForm : Form
{
    private readonly TextBox _codexPath;
    private readonly TextBox _openCodeGoApiKey;
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
    private readonly Button _testOpenCodeGoApiKeyButton;
    private readonly Button _clearOpenCodeGoApiKeyButton;
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
        Width = 720;
        Height = 480;
        MinimumSize = new Size(680, 460);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 3,
            RowCount = 9,
            AutoSize = false
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        for (var row = 0; row < layout.RowCount; row++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        }
        layout.RowStyles[0].Height = 62;
        Controls.Add(layout);

        _codexPath = new TextBox { Text = settings.CodexExecutablePath ?? string.Empty, Dock = DockStyle.Fill };
        _openCodeGoApiKey = new TextBox
        {
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = true,
            PlaceholderText = openCodeGoApiKeyConfigured
                ? "Configured — leave blank to keep"
                : "Paste OpenCode Go API key"
        };
        _openCodeGoApiKey.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_openCodeGoApiKey.Text))
            {
                _deleteOpenCodeGoApiKey = false;
            }
        };
        _codexLimitsSeconds = CreateSecondsControl(settings.CodexRateLimitRefreshSeconds, AppSettings.MinimumProviderRefreshSeconds);
        _codexUsageSeconds = CreateSecondsControl(settings.CodexUsageRefreshSeconds, AppSettings.MinimumTokenUsageRefreshSeconds);
        _openCodeSeconds = CreateSecondsControl(settings.OpenCodeRefreshSeconds, AppSettings.MinimumProviderRefreshSeconds);
        _quotaNotifications = new CheckBox
        {
            Text = "Notify at 25% and 5% left, and when quota resets",
            Checked = settings.QuotaNotificationsEnabled,
            AutoSize = true
        };
        _startWithWindows = new CheckBox { Text = "Start with Windows", Checked = settings.StartWithWindows, AutoSize = true };

        AddPathRow(layout, 0, "Codex executable", _codexPath, () => ExecutableResolver.ResolveCodex(null));
        layout.Controls.Add(CreateFieldLabel("OpenCode Go API key"), 0, 1);
        layout.Controls.Add(_openCodeGoApiKey, 1, 1);
        var keyButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        _testOpenCodeGoApiKeyButton = CreateButton("Test", 58);
        _testOpenCodeGoApiKeyButton.Click += TestOpenCodeGoApiKeyClicked;
        _clearOpenCodeGoApiKeyButton = CreateButton("Clear", 58);
        _clearOpenCodeGoApiKeyButton.Click += (_, _) => MarkOpenCodeGoApiKeyForDeletion();
        keyButtons.Controls.Add(_testOpenCodeGoApiKeyButton);
        keyButtons.Controls.Add(_clearOpenCodeGoApiKeyButton);
        layout.Controls.Add(keyButtons, 2, 1);
        AddSecondsRow(layout, 2, "Codex quota refresh", _codexLimitsSeconds);
        AddSecondsRow(layout, 3, "Token activity refresh", _codexUsageSeconds);
        AddSecondsRow(layout, 4, "OpenCode quota refresh", _openCodeSeconds);
        layout.Controls.Add(_quotaNotifications, 1, 5);
        layout.Controls.Add(_startWithWindows, 1, 6);

        var logButton = CreateButton("Open log folder", 150);
        logButton.Enabled = !string.IsNullOrWhiteSpace(_logDirectory);
        logButton.AccessibleDescription = logButton.Enabled
            ? _logDirectory
            : "File logging is unavailable.";
        logButton.Click += (_, _) => OpenLogFolder();
        layout.Controls.Add(logButton, 1, 7);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        _cancelButton = CreateButton("Cancel", 95);
        _cancelButton.DialogResult = DialogResult.Cancel;
        _saveButton = CreateButton("Save", 95);
        _saveButton.Click += SaveClicked;
        buttons.Controls.Add(_cancelButton);
        buttons.Controls.Add(_saveButton);
        layout.Controls.Add(buttons, 1, 8);
        AcceptButton = _saveButton;
        CancelButton = _cancelButton;
        WindowsTheme.Apply(this);
        Shown += (_, _) => WindowsTheme.Apply(this);
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
    }

    private static NumericUpDown CreateSecondsControl(int value, int minimum)
    {
        return new NumericUpDown
        {
            Minimum = minimum,
            Maximum = 86400,
            Increment = 30,
            Value = Math.Clamp(value, minimum, 86400),
            Dock = DockStyle.Left,
            Width = 120
        };
    }

    private void AddPathRow(TableLayoutPanel layout, int row, string label, TextBox textBox, Func<string?> resolve)
    {
        layout.Controls.Add(CreateFieldLabel(label), 0, row);
        var pathPanel = new Panel { Dock = DockStyle.Fill };
        var resolutionLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Bottom,
            Height = 18,
            AutoEllipsis = true,
            AccessibleName = "Codex executable resolution"
        };
        void UpdateResolutionLabel()
        {
            var resolution = ExecutableResolver.ResolveCodexWithProvenance(textBox.Text);
            resolutionLabel.Text = resolution is null
                ? "Automatic resolution: executable not found"
                : $"{resolution.ProvenanceLabel}: {resolution.Path}";
            resolutionLabel.AccessibleDescription = resolutionLabel.Text;
        }

        textBox.Dock = DockStyle.Fill;
        textBox.TextChanged += (_, _) => UpdateResolutionLabel();
        pathPanel.Controls.Add(resolutionLabel);
        pathPanel.Controls.Add(textBox);
        layout.Controls.Add(pathPanel, 1, row);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        var auto = CreateButton("Auto", 58);
        auto.Click += (_, _) =>
        {
            textBox.Text = resolve() ?? string.Empty;
            UpdateResolutionLabel();
        };
        var test = CreateButton("Test", 58);
        test.Click += (_, _) => TestPath(textBox.Text, label);
        buttons.Controls.Add(auto);
        buttons.Controls.Add(test);
        layout.Controls.Add(buttons, 2, row);
        UpdateResolutionLabel();
    }

    private static void AddSecondsRow(TableLayoutPanel layout, int row, string label, NumericUpDown control)
    {
        layout.Controls.Add(CreateFieldLabel(label), 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false
        };
    }

    private static Button CreateButton(string text, int width)
    {
        return new Button
        {
            Text = text,
            Width = width,
            Height = Math.Max(34, TextRenderer.MeasureText(text, Control.DefaultFont).Height + 12),
            AutoSize = false,
            UseVisualStyleBackColor = true
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
        _saveButton.Enabled = !inProgress;
    }

    private void SetSaveInProgress(bool inProgress)
    {
        _codexPath.Enabled = !inProgress;
        _openCodeGoApiKey.Enabled = !inProgress;
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
            if (Interlocked.Exchange(ref _lifetimeDisposed, 1) == 0)
            {
                _lifetimeCancellation.Cancel();
                _lifetimeCancellation.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
