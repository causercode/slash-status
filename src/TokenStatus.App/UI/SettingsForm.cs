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
    private bool _deleteOpenCodeGoApiKey;

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
        var clearKeyButton = CreateButton("Clear", 58);
        clearKeyButton.Click += (_, _) => MarkOpenCodeGoApiKeyForDeletion();
        keyButtons.Controls.Add(_testOpenCodeGoApiKeyButton);
        keyButtons.Controls.Add(clearKeyButton);
        layout.Controls.Add(keyButtons, 2, 1);
        AddSecondsRow(layout, 2, "Codex quota refresh", _codexLimitsSeconds);
        AddSecondsRow(layout, 3, "Token activity refresh", _codexUsageSeconds);
        AddSecondsRow(layout, 4, "OpenCode quota refresh", _openCodeSeconds);
        layout.Controls.Add(_quotaNotifications, 1, 5);
        layout.Controls.Add(_startWithWindows, 1, 6);

        var logButton = CreateButton("Open log folder", 150);
        logButton.Click += (_, _) => OpenLogFolder();
        layout.Controls.Add(logButton, 1, 7);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var cancelButton = CreateButton("Cancel", 95);
        cancelButton.DialogResult = DialogResult.Cancel;
        _saveButton = CreateButton("Save", 95);
        _saveButton.Click += SaveClicked;
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(_saveButton);
        layout.Controls.Add(buttons, 1, 8);
        AcceptButton = _saveButton;
        CancelButton = cancelButton;
        WindowsTheme.Apply(this);
        Shown += (_, _) => WindowsTheme.Apply(this);
    }

    private async void TestOpenCodeGoApiKeyClicked(object? sender, EventArgs e)
    {
        var enteredKey = _openCodeGoApiKey.Text.Trim();
        if (enteredKey.Length == 0 && (!_openCodeGoApiKeyConfigured || _deleteOpenCodeGoApiKey))
        {
            MessageBox.Show(
                this,
                "Enter an OpenCode Go API key first.",
                AppBrand.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        _testOpenCodeGoApiKeyButton.Enabled = false;
        try
        {
            var quota = await _testOpenCodeGoApiKey(
                enteredKey.Length == 0 ? null : enteredKey,
                CancellationToken.None).ConfigureAwait(true);
            MessageBox.Show(
                this,
                $"OpenCode Go connected.\n\n5-hour: {quota.Rolling.UsedPercent:0.#}% used\nWeekly: {quota.Weekly.UsedPercent:0.#}% used\nMonthly: {quota.Monthly.UsedPercent:0.#}% used",
                AppBrand.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (ProviderFailureException exception)
        {
            MessageBox.Show(this, exception.UserFacingMessage, "OpenCode Go connection failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception)
        {
            MessageBox.Show(
                this,
                "OpenCode Go could not be reached. Check the connection and try again.",
                "OpenCode Go connection failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _testOpenCodeGoApiKeyButton.Enabled = true;
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
        layout.Controls.Add(textBox, 1, row);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        var auto = CreateButton("Auto", 58);
        auto.Click += (_, _) => textBox.Text = resolve() ?? string.Empty;
        var test = CreateButton("Test", 58);
        test.Click += (_, _) => TestPath(textBox.Text, label);
        buttons.Controls.Add(auto);
        buttons.Controls.Add(test);
        layout.Controls.Add(buttons, 2, row);
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

    private async void SaveClicked(object? sender, EventArgs e)
    {
        var codexPath = NormalizePath(_codexPath.Text, "Codex executable");
        if (codexPath is null && !string.IsNullOrWhiteSpace(_codexPath.Text))
        {
            return;
        }

        _saveButton.Enabled = false;
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
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _saveButton.Enabled = true;
        }
    }

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
}
