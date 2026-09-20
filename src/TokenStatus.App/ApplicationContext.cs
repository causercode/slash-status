using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;
using TokenStatus.App.UI;
using TokenStatus.Core.Abstractions;
using TokenStatus.Core.Models;
using TokenStatus.Core.Services;
using TokenStatus.Infrastructure.Awake;
using TokenStatus.Infrastructure.Cli;
using TokenStatus.Infrastructure.Codex;
using TokenStatus.Infrastructure.Logging;
using TokenStatus.Infrastructure.OpenCode;
using TokenStatus.Infrastructure.Settings;
using TokenStatus.Infrastructure.Startup;

namespace TokenStatus.App;

public sealed class TokenStatusApplicationContext : ApplicationContext
{
    private const string DashboardUrl = "https://opencode.ai/workspace";
    private readonly Mutex _instanceMutex;
    private readonly SynchronizationContext _uiContext;
    private readonly IRedactedLog _log;
    private readonly JsonSettingsStore _settingsStore;
    private readonly WindowsOpenCodeGoCredentialStore _openCodeGoCredentials;
    private readonly SnapshotStore _snapshots;
    private readonly QuotaNotificationTracker _quotaNotifications = new();
    private readonly WindowsAwakeController _awake;
    private readonly CurrentUserStartupManager _startupManager = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu;
    private readonly ToolStripMenuItem _startWithWindowsMenuItem;
    private StatusPopupForm? _popup;
    private RefreshCoordinator? _coordinator;
    private ICodexUsageClient? _codexClient;
    private IOpenCodeGoQuotaClient? _openCodeGoClient;
    private AppSettings _settings = new();
    private Icon? _trayIcon;
    private int _shuttingDown;

    public TokenStatusApplicationContext(Mutex instanceMutex)
    {
        _instanceMutex = instanceMutex;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _log = new RedactingFileLog();
        _settingsStore = new JsonSettingsStore(_log);
        _openCodeGoCredentials = new WindowsOpenCodeGoCredentialStore();
        _snapshots = new SnapshotStore(AppSnapshot.Initial(DateTimeOffset.UtcNow));
        _awake = new WindowsAwakeController();
        SystemEvents.PowerModeChanged += PowerModeChanged;
        _snapshots.Changed += SnapshotChanged;

        _trayMenu = new ContextMenuStrip();
        _startWithWindowsMenuItem = new ToolStripMenuItem("Start with Windows");
        BuildTrayMenu();

        _trayIcon = TrayIconRenderer.Create(TrayHealth.Gray);
        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            Text = $"{AppBrand.DisplayName} - Loading",
            ContextMenuStrip = _trayMenu
        };
        _tray.MouseUp += TrayMouseUp;
        _tray.BalloonTipClicked += (_, _) => _uiContext.Post(_ => ShowPopup(), null);
        _trayMenu.Opening += (_, _) => WindowsTheme.Apply(_trayMenu);
        SystemEvents.UserPreferenceChanged += UserPreferenceChanged;

        _ = InitializeAsync();
    }

    private void BuildTrayMenu()
    {
        var open = new ToolStripMenuItem($"Open {AppBrand.DisplayName}");
        open.Click += (_, _) => ShowPopup();
        _trayMenu.Items.Add(open);

        var refresh = new ToolStripMenuItem("Refresh now");
        refresh.Click += async (_, _) => await RefreshNowAsync().ConfigureAwait(true);
        _trayMenu.Items.Add(refresh);

        var awake = new ToolStripMenuItem("Keep awake");
        awake.DropDownItems.Add(CreateAwakeMenuItem("Off", AwakeMode.Off, null));
        awake.DropDownItems.Add(CreateAwakeDurationMenu("System", AwakeMode.System));
        awake.DropDownItems.Add(CreateAwakeDurationMenu("System + display", AwakeMode.SystemAndDisplay));
        _trayMenu.Items.Add(awake);

        var dashboard = new ToolStripMenuItem("Open Go Usage Dashboard");
        dashboard.Click += (_, _) => OpenDashboard();
        _trayMenu.Items.Add(dashboard);

        _startWithWindowsMenuItem.CheckOnClick = true;
        _startWithWindowsMenuItem.Click += async (_, _) => await ToggleStartupAsync().ConfigureAwait(true);
        _trayMenu.Items.Add(_startWithWindowsMenuItem);

        var settings = new ToolStripMenuItem("Settings");
        settings.Click += (_, _) => _uiContext.Post(_ => ShowSettings(), null);
        _trayMenu.Items.Add(settings);

#if DEBUG
        var testNotification = new ToolStripMenuItem("Test notification (Debug)");
        testNotification.DropDownItems.Add(CreateTestNotificationMenuItem(
            "25% remaining",
            new QuotaNotification(QuotaNotificationKind.Low, "Codex", "daily", 25)));
        testNotification.DropDownItems.Add(CreateTestNotificationMenuItem(
            "5% remaining",
            new QuotaNotification(QuotaNotificationKind.Critical, "Codex", "daily", 5)));
        testNotification.DropDownItems.Add(CreateTestNotificationMenuItem(
            "Quota reset",
            new QuotaNotification(QuotaNotificationKind.Reset, "Codex", "daily", 100)));
        _trayMenu.Items.Add(testNotification);
#endif

        _trayMenu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitApplication();
        _trayMenu.Items.Add(exit);
        _trayMenu.Opening += (_, _) => UpdateStartupMenuState();
    }

    private ToolStripMenuItem CreateAwakeDurationMenu(string title, AwakeMode mode)
    {
        var menu = new ToolStripMenuItem(title);
        menu.DropDownItems.Add(CreateAwakeMenuItem("30 minutes", mode, TimeSpan.FromMinutes(30)));
        menu.DropDownItems.Add(CreateAwakeMenuItem("1 hour", mode, TimeSpan.FromHours(1)));
        menu.DropDownItems.Add(CreateAwakeMenuItem("2 hours", mode, TimeSpan.FromHours(2)));
        menu.DropDownItems.Add(CreateAwakeMenuItem("4 hours", mode, TimeSpan.FromHours(4)));
        menu.DropDownItems.Add(CreateAwakeMenuItem("Until turned off", mode, null));
        return menu;
    }

    private ToolStripMenuItem CreateAwakeMenuItem(string text, AwakeMode mode, TimeSpan? duration)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => _awake.Start(mode, duration);
        return item;
    }

#if DEBUG
    private ToolStripMenuItem CreateTestNotificationMenuItem(string text, QuotaNotification notification)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => ShowQuotaNotifications([notification], ignorePreference: true);
        return item;
    }
#endif

    private async Task InitializeAsync()
    {
        try
        {
            _settings = await _settingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            UpdateStartupMenuState();
            _awake.Start(AwakeMode.Off, null);
            await RecreateProvidersAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _log.Error("app", "startup", "startup_failed", exception);
        }
    }

    private async Task RecreateProvidersAsync()
    {
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync().ConfigureAwait(true);
            _coordinator = null;
        }

        _quotaNotifications.Reset();

        _codexClient = new CodexAppServerClient(_settings.CodexExecutablePath, _log);
        _openCodeGoClient = new OpenCodeGoQuotaClient(_openCodeGoCredentials, log: _log);
        _coordinator = new RefreshCoordinator(
            _codexClient,
            _openCodeGoClient,
            _awake,
            _snapshots,
            new SystemClock(),
            RefreshIntervals.FromSettings(_settings),
            (code, exception) => _log.Error("refresh", "coordinator", code, exception));
        _coordinator.Start();
    }

    private void SnapshotChanged(AppSnapshot snapshot)
    {
        _uiContext.Post(_ => ApplySnapshot(snapshot), null);
    }

    private void PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _awake.Reassert();
        }
    }

    private void ApplySnapshot(AppSnapshot snapshot)
    {
        if (Volatile.Read(ref _shuttingDown) != 0)
        {
            return;
        }

        var health = OverallHealthCalculator.Calculate(snapshot);
        var nextIcon = TrayIconRenderer.Create(health);
        var previousIcon = _tray.Icon;
        _tray.Icon = nextIcon;
        previousIcon?.Dispose();
        _trayIcon = nextIcon;
        _tray.Text = StatusViewModel.BuildTooltip(snapshot);
        _popup?.UpdateSnapshot(snapshot);
        ShowQuotaNotifications(_quotaNotifications.Evaluate(snapshot));
    }

    private void ShowQuotaNotifications(
        IReadOnlyList<QuotaNotification> notifications,
        bool ignorePreference = false)
    {
        if ((!ignorePreference && !_settings.QuotaNotificationsEnabled) || notifications.Count == 0)
        {
            return;
        }

        var lines = notifications.Select(notification =>
            notification.Kind == QuotaNotificationKind.Reset
                ? $"{notification.Provider} {notification.Window}: reset, {notification.RemainingPercent}% available"
                : $"{notification.Provider} {notification.Window}: {notification.RemainingPercent}% left");
        var message = string.Join(Environment.NewLine, lines);
        if (message.Length > 255)
        {
            message = message[..252] + "...";
        }

        var title = notifications.Count == 1
            ? $"{notifications[0].Provider} quota"
            : $"{AppBrand.DisplayName} quota update";
        var icon = notifications.Any(notification => notification.Kind == QuotaNotificationKind.Critical)
            ? ToolTipIcon.Error
            : notifications.Any(notification => notification.Kind == QuotaNotificationKind.Low)
                ? ToolTipIcon.Warning
                : ToolTipIcon.Info;
        _tray.ShowBalloonTip(8000, title, message, icon);
    }

    private void TrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            // Showing from inside the notify-icon mouse message can cause Explorer to
            // immediately take focus back and trigger the popup's Deactivate handler.
            _uiContext.Post(_ => ShowPopup(), null);
        }
    }

    private void UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
        {
            _uiContext.Post(_ =>
            {
                WindowsTheme.Apply(_trayMenu);
                _popup?.ApplyTheme();
            }, null);
        }
    }

    private void ShowPopup()
    {
        if (Volatile.Read(ref _shuttingDown) != 0)
        {
            return;
        }

        _popup ??= new StatusPopupForm(
            _snapshots.Current,
            () => _ = RefreshNowAsync(),
            () => _uiContext.Post(_ => ShowSettings(), null),
            (mode, duration) => _awake.Start(mode, duration),
            ExitApplication);
        _popup.UpdateSnapshot(_snapshots.Current);
        _popup.ShowPopup();
    }

    private async Task RefreshNowAsync()
    {
        if (_coordinator is null)
        {
            return;
        }

        try
        {
            await _coordinator.RefreshNowAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _log.Error("app", "refresh_now", "refresh_now_failed", exception);
        }
    }

    private void OpenDashboard()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DashboardUrl,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _log.Error("app", "dashboard", "dashboard_open_failed", exception);
        }
    }

    private void ShowSettings()
    {
        try
        {
            using var form = new SettingsForm(
                _settings,
                _openCodeGoCredentials.IsConfigured(),
                TestOpenCodeGoApiKeyAsync,
                SaveSettingsAsync,
                _log is RedactingFileLog fileLog ? fileLog.LogDirectory : string.Empty);
            form.ShowDialog();
        }
        catch (Exception exception)
        {
            _log.Error("app", "settings", "settings_open_failed", exception);
            MessageBox.Show(
                exception.Message,
                $"Could not open {AppBrand.DisplayName} settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task<OpenCodeGoQuota> TestOpenCodeGoApiKeyAsync(string? apiKey, CancellationToken cancellationToken)
    {
        using var client = new OpenCodeGoQuotaClient(_openCodeGoCredentials, log: _log);
        return apiKey is null
            ? await client.GetQuotaAsync(cancellationToken).ConfigureAwait(true)
            : await client.GetQuotaAsync(apiKey, cancellationToken).ConfigureAwait(true);
    }

    private async Task SaveSettingsAsync(AppSettings settings, string? openCodeGoApiKey, bool deleteOpenCodeGoApiKey)
    {
        var credentialChanged = deleteOpenCodeGoApiKey || !string.IsNullOrWhiteSpace(openCodeGoApiKey);
        if (deleteOpenCodeGoApiKey)
        {
            _openCodeGoCredentials.DeleteApiKey();
        }
        else if (!string.IsNullOrWhiteSpace(openCodeGoApiKey))
        {
            _openCodeGoCredentials.SaveApiKey(openCodeGoApiKey);
        }

        if (credentialChanged)
        {
            var now = DateTimeOffset.UtcNow;
            _snapshots.Update(snapshot => snapshot with
            {
                CapturedAt = now,
                OpenCodeGoQuota = ProviderResult<OpenCodeGoQuota>.Loading(now)
            });
        }

        await _settingsStore.SaveAsync(settings, CancellationToken.None).ConfigureAwait(true);
        try
        {
            _startupManager.SetEnabled(Application.ExecutablePath, settings.StartWithWindows);
        }
        catch (Exception exception)
        {
            _log.Error("startup", "registry", "startup_registry_failed", exception);
            throw;
        }

        _settings = settings;
        UpdateStartupMenuState();
        await RecreateProvidersAsync().ConfigureAwait(true);
    }

    private async Task ToggleStartupAsync()
    {
        var enabled = _startWithWindowsMenuItem.Checked;
        try
        {
            _startupManager.SetEnabled(Application.ExecutablePath, enabled);
            _settings = _settings with { StartWithWindows = enabled };
            await _settingsStore.SaveAsync(_settings, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _log.Error("startup", "toggle", "startup_toggle_failed", exception);
            _startWithWindowsMenuItem.Checked = !enabled;
        }
    }

    private void UpdateStartupMenuState()
    {
        try
        {
            _startWithWindowsMenuItem.Checked = _startupManager.IsEnabled(Application.ExecutablePath);
        }
        catch (Exception exception)
        {
            _log.Error("startup", "read", "startup_read_failed", exception);
            _startWithWindowsMenuItem.Checked = false;
        }
    }

    private async void ExitApplication()
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0)
        {
            return;
        }

        _tray.Visible = false;
        _popup?.Hide();
        try
        {
            _awake.Stop();
            if (_coordinator is not null)
            {
                await _coordinator.DisposeAsync().ConfigureAwait(true);
            }
            _openCodeGoClient = null;
            _codexClient = null;
        }
        catch (Exception exception)
        {
            _log.Error("app", "shutdown", "shutdown_failed", exception);
        }
        finally
        {
            SystemEvents.PowerModeChanged -= PowerModeChanged;
            SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
            _awake.Dispose();
            _tray.Dispose();
            _trayIcon?.Dispose();
            _trayMenu.Dispose();
            _popup?.Dispose();
            _log.Dispose();
            try
            {
                if (_instanceMutex.WaitOne(0))
                {
                    _instanceMutex.ReleaseMutex();
                }
            }
            catch (ApplicationException)
            {
            }

            ExitThread();
        }
    }
}
