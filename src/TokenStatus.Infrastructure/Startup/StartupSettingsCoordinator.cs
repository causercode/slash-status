using TokenStatus.Core.Abstractions;
using TokenStatus.Core.Models;

namespace TokenStatus.Infrastructure.Startup;

/// <summary>
/// Coordinates the two persistent representations of the current-user startup
/// setting. The registry is authoritative; JSON is updated only after the
/// requested registry state has been applied.
/// </summary>
public sealed class StartupSettingsCoordinator
{
    private readonly IStartupManager _startupManager;
    private readonly ISettingsStore _settingsStore;
    private readonly string _executablePath;

    public StartupSettingsCoordinator(
        IStartupManager startupManager,
        ISettingsStore settingsStore,
        string executablePath)
    {
        _startupManager = startupManager;
        _settingsStore = settingsStore;
        _executablePath = executablePath;
    }

    public bool GetEffectiveState() => _startupManager.IsEnabled(_executablePath);

    public AppSettings Reconcile(AppSettings settings) =>
        settings with { StartWithWindows = GetEffectiveState() };

    public async Task CommitAsync(
        AppSettings settings,
        bool previousEffectiveState,
        CancellationToken cancellationToken)
    {
        var startupChanged = settings.StartWithWindows != previousEffectiveState;
        try
        {
            if (startupChanged)
            {
                _startupManager.SetEnabled(_executablePath, settings.StartWithWindows);
            }

            await _settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception saveException)
        {
            if (startupChanged)
            {
                try
                {
                    _startupManager.SetEnabled(_executablePath, previousEffectiveState);
                }
                catch (Exception rollbackException)
                {
                    throw new InvalidOperationException(
                        "Settings could not be saved and the previous Start with Windows state could not be restored.",
                        new AggregateException(saveException, rollbackException));
                }
            }

            throw;
        }
    }

    public async Task RestoreAsync(
        AppSettings previousSettings,
        bool previousEffectiveState,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await _settingsStore.SaveAsync(previousSettings.Normalize(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            _startupManager.SetEnabled(_executablePath, previousEffectiveState);
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is not null)
        {
            throw failure;
        }
    }
}
