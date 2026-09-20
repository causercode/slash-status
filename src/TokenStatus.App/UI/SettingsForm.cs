using System.Diagnostics;
using TokenStatus.Core.Models;
using TokenStatus.Infrastructure.Cli;

namespace TokenStatus.App.UI;

public sealed class SettingsForm : Form
{
    private readonly TextBox _codexPath;
    private readonly TextBox _openCodePath;
    private readonly NumericUpDown _codexLimitsSeconds;
    private readonly NumericUpDown _codexUsageSeconds;
    private readonly NumericUpDown _openCodeSeconds;
    private readonly CheckBox _startWithWindows;
    private readonly Func<AppSettings, Task> _save;
    private readonly string _logDirectory;
    private readonly Button _saveButton;

    public SettingsForm(AppSettings settings, Func<AppSettings, Task> save, string logDirectory)
    {
        _save = save;
        _logDirectory = logDirectory;
        Text = "TokenStatus Settings";
        Width = 720;
        Height = 430;
        MinimumSize = new Size(680, 410);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 3,
            RowCount = 8,
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
        _openCodePath = new TextBox { Text = settings.OpenCodeExecutablePath ?? string.Empty, Dock = DockStyle.Fill };
        _codexLimitsSeconds = CreateSecondsControl(settings.CodexRateLimitRefreshSeconds, AppSettings.MinimumProviderRefreshSeconds);
        _codexUsageSeconds = CreateSecondsControl(settings.CodexUsageRefreshSeconds, AppSettings.MinimumTokenUsageRefreshSeconds);
        _openCodeSeconds = CreateSecondsControl(settings.OpenCodeRefreshSeconds, AppSettings.MinimumProviderRefreshSeconds);
        _startWithWindows = new CheckBox { Text = "Start with Windows", Checked = settings.StartWithWindows, AutoSize = true };

        AddPathRow(layout, 0, "Codex executable", _codexPath, () => ExecutableResolver.ResolveCodex(null));
        AddPathRow(layout, 1, "OpenCode executable", _openCodePath, () => ExecutableResolver.ResolveOpenCode(null));
        AddSecondsRow(layout, 2, "Codex quota refresh", _codexLimitsSeconds);
        AddSecondsRow(layout, 3, "Token activity refresh", _codexUsageSeconds);
        AddSecondsRow(layout, 4, "OpenCode refresh", _openCodeSeconds);
        layout.Controls.Add(_startWithWindows, 1, 5);

        var logButton = CreateButton("Open log folder", 150);
        logButton.Click += (_, _) => OpenLogFolder();
        layout.Controls.Add(logButton, 1, 6);

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
        layout.Controls.Add(buttons, 1, 7);
        AcceptButton = _saveButton;
        CancelButton = cancelButton;
        WindowsTheme.Apply(this);
        Shown += (_, _) => WindowsTheme.Apply(this);
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
            "TokenStatus",
            MessageBoxButtons.OK,
            exists ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private async void SaveClicked(object? sender, EventArgs e)
    {
        var codexPath = NormalizePath(_codexPath.Text, "Codex executable");
        var openCodePath = NormalizePath(_openCodePath.Text, "OpenCode executable");
        if (codexPath is null && !string.IsNullOrWhiteSpace(_codexPath.Text) ||
            openCodePath is null && !string.IsNullOrWhiteSpace(_openCodePath.Text))
        {
            return;
        }

        _saveButton.Enabled = false;
        try
        {
            var settings = new AppSettings
            {
                CodexExecutablePath = codexPath,
                OpenCodeExecutablePath = openCodePath,
                CodexRateLimitRefreshSeconds = (int)_codexLimitsSeconds.Value,
                CodexUsageRefreshSeconds = (int)_codexUsageSeconds.Value,
                OpenCodeRefreshSeconds = (int)_openCodeSeconds.Value,
                StartWithWindows = _startWithWindows.Checked
            }.Normalize();
            await _save(settings).ConfigureAwait(true);
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
            MessageBox.Show($"{label} must be an absolute path.", "TokenStatus", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
