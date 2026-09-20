using TokenStatus.Core.Abstractions;
using TokenStatus.Core.Models;

namespace TokenStatus.Core.Services;

public sealed record RefreshIntervals(
    TimeSpan CodexRateLimits,
    TimeSpan CodexTokenUsage,
    TimeSpan OpenCodeUsage)
{
    public static RefreshIntervals FromSettings(AppSettings settings) => new(
        TimeSpan.FromSeconds(Math.Max(AppSettings.MinimumProviderRefreshSeconds, settings.CodexRateLimitRefreshSeconds)),
        TimeSpan.FromSeconds(Math.Max(AppSettings.MinimumTokenUsageRefreshSeconds, settings.CodexUsageRefreshSeconds)),
        TimeSpan.FromSeconds(Math.Max(AppSettings.MinimumProviderRefreshSeconds, settings.OpenCodeRefreshSeconds)));
}

public sealed class RefreshCoordinator : IAsyncDisposable
{
    private readonly ICodexUsageClient _codex;
    private readonly IOpenCodeUsageClient _openCode;
    private readonly IAwakeController _awake;
    private readonly SnapshotStore _snapshots;
    private readonly IClock _clock;
    private readonly RefreshIntervals _intervals;
    private readonly Action<string, Exception?>? _diagnostic;
    private readonly SemaphoreSlim _accountGate = new(1, 1);
    private readonly SemaphoreSlim _rateLimitsGate = new(1, 1);
    private readonly SemaphoreSlim _tokenUsageGate = new(1, 1);
    private readonly SemaphoreSlim _openCodeGate = new(1, 1);
    private readonly object _coalescingGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _loops = [];
    private readonly TaskSlot _accountRefresh = new();
    private readonly TaskSlot _rateLimitsRefresh = new();
    private readonly TaskSlot _tokenUsageRefresh = new();
    private readonly TaskSlot _openCodeRefresh = new();
    private int _started;
    private int _disposed;

    public RefreshCoordinator(
        ICodexUsageClient codex,
        IOpenCodeUsageClient openCode,
        IAwakeController awake,
        SnapshotStore snapshots,
        IClock clock,
        RefreshIntervals intervals,
        Action<string, Exception?>? diagnostic = null)
    {
        _codex = codex;
        _openCode = openCode;
        _awake = awake;
        _snapshots = snapshots;
        _clock = clock;
        _intervals = intervals;
        _diagnostic = diagnostic;
        _awake.Changed += AwakeChanged;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _loops.Add(Task.Run(() => RunLoopAsync(RefreshAccountAsync, _intervals.CodexRateLimits, MarkAccountStaleAsync)));
        _loops.Add(Task.Run(() => RunLoopAsync(RefreshRateLimitsAsync, _intervals.CodexRateLimits, MarkRateLimitsStaleAsync)));
        _loops.Add(Task.Run(() => RunLoopAsync(RefreshTokenUsageAsync, _intervals.CodexTokenUsage, MarkTokenUsageStaleAsync)));
        _loops.Add(Task.Run(() => RunLoopAsync(RefreshOpenCodeAsync, _intervals.OpenCodeUsage, MarkOpenCodeStaleAsync)));
    }

    public async Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await Task.WhenAll(
            RefreshAccountAsync(cancellationToken),
            RefreshRateLimitsAsync(cancellationToken),
            RefreshTokenUsageAsync(cancellationToken),
            RefreshOpenCodeAsync(cancellationToken)).ConfigureAwait(false);
    }

    private async Task RunLoopAsync(
        Func<CancellationToken, Task> refresh,
        TimeSpan interval,
        Func<Task> markStale,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        var token = linked.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await refresh(token).ConfigureAwait(false);
                await DelayWithJitterAsync(interval, token).ConfigureAwait(false);
                await markStale().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _diagnostic?.Invoke("refresh_loop_failed", exception);
                try
                {
                    await DelayWithJitterAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private static Task DelayWithJitterAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        var jitter = interval.TotalMilliseconds * Random.Shared.NextDouble() * 0.1;
        return Task.Delay(interval + TimeSpan.FromMilliseconds(jitter), cancellationToken);
    }

    private Task RefreshAccountAsync(CancellationToken cancellationToken) => Coalesce(
        _accountRefresh,
        () => RefreshAccountCoreAsync(cancellationToken));

    private async Task RefreshAccountCoreAsync(CancellationToken cancellationToken)
    {
        await RunExclusiveAsync(_accountGate, async () =>
        {
            var attempt = _clock.UtcNow;
            try
            {
                var value = await _codex.GetAccountAsync(cancellationToken).ConfigureAwait(false);
                _snapshots.Update(snapshot => snapshot with
                {
                    CapturedAt = _clock.UtcNow,
                    CodexAccount = new ProviderResult<CodexAccountInfo>(
                        ProviderHealth.Healthy, value, attempt, _clock.UtcNow, null, null)
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (RecordFailure(
                "codex_account", exception, attempt, _snapshots.Current.CodexAccount, result => _snapshots.Update(current => current with
                {
                    CapturedAt = _clock.UtcNow,
                    CodexAccount = result
                })))
            {
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task RefreshRateLimitsAsync(CancellationToken cancellationToken) => Coalesce(
        _rateLimitsRefresh,
        () => RefreshRateLimitsCoreAsync(cancellationToken));

    private async Task RefreshRateLimitsCoreAsync(CancellationToken cancellationToken)
    {
        await RunExclusiveAsync(_rateLimitsGate, async () =>
        {
            var attempt = _clock.UtcNow;
            try
            {
                var value = await _codex.GetRateLimitsAsync(cancellationToken).ConfigureAwait(false);
                _snapshots.Update(snapshot => snapshot with
                {
                    CapturedAt = _clock.UtcNow,
                    CodexRateLimits = new ProviderResult<CodexRateLimits>(
                        ProviderHealth.Healthy, value, attempt, _clock.UtcNow, null, null)
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (RecordFailure(
                "codex_rate_limits", exception, attempt, _snapshots.Current.CodexRateLimits, result => _snapshots.Update(current => current with
                {
                    CapturedAt = _clock.UtcNow,
                    CodexRateLimits = result
                })))
            {
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task RefreshTokenUsageAsync(CancellationToken cancellationToken) => Coalesce(
        _tokenUsageRefresh,
        () => RefreshTokenUsageCoreAsync(cancellationToken));

    private async Task RefreshTokenUsageCoreAsync(CancellationToken cancellationToken)
    {
        await RunExclusiveAsync(_tokenUsageGate, async () =>
        {
            var attempt = _clock.UtcNow;
            try
            {
                var value = await _codex.GetTokenUsageAsync(cancellationToken).ConfigureAwait(false);
                _snapshots.Update(snapshot => snapshot with
                {
                    CapturedAt = _clock.UtcNow,
                    CodexTokenUsage = new ProviderResult<CodexTokenUsage>(
                        ProviderHealth.Healthy, value, attempt, _clock.UtcNow, null, null)
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (RecordFailure(
                "codex_token_usage", exception, attempt, _snapshots.Current.CodexTokenUsage, result => _snapshots.Update(current => current with
                {
                    CapturedAt = _clock.UtcNow,
                    CodexTokenUsage = result
                })))
            {
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task RefreshOpenCodeAsync(CancellationToken cancellationToken) => Coalesce(
        _openCodeRefresh,
        () => RefreshOpenCodeCoreAsync(cancellationToken));

    private async Task RefreshOpenCodeCoreAsync(CancellationToken cancellationToken)
    {
        await RunExclusiveAsync(_openCodeGate, async () =>
        {
            var attempt = _clock.UtcNow;
            try
            {
                var value = await _openCode.GetSevenDayUsageAsync(cancellationToken).ConfigureAwait(false);
                _snapshots.Update(snapshot => snapshot with
                {
                    CapturedAt = _clock.UtcNow,
                    OpenCodeUsage = new ProviderResult<OpenCodeLocalUsage>(
                        ProviderHealth.Healthy, value, attempt, _clock.UtcNow, null, null)
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (RecordFailure(
                "opencode_usage", exception, attempt, _snapshots.Current.OpenCodeUsage, result => _snapshots.Update(current => current with
                {
                    CapturedAt = _clock.UtcNow,
                    OpenCodeUsage = result
                })))
            {
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private bool RecordFailure<T>(
        string operation,
        Exception exception,
        DateTimeOffset attempt,
        ProviderResult<T> prior,
        Action<ProviderResult<T>> update)
    {
        var failure = exception as ProviderFailureException;
        var health = failure?.Health ?? (prior.Value is null ? ProviderHealth.Error : ProviderHealth.Stale);
        var message = failure?.UserFacingMessage ?? "The provider could not be refreshed.";
        var code = failure?.DiagnosticCode ?? "refresh_failed";
        update(prior.WithFailure(health, attempt, message, code));
        _diagnostic?.Invoke(operation + ":" + code, exception);
        return true;
    }

    private Task MarkAccountStaleAsync() => MarkStaleAsync(
        snapshot => snapshot.CodexAccount,
        _intervals.CodexRateLimits,
        (snapshot, result) => snapshot with { CapturedAt = _clock.UtcNow, CodexAccount = result });

    private Task MarkRateLimitsStaleAsync() => MarkStaleAsync(
        snapshot => snapshot.CodexRateLimits,
        _intervals.CodexRateLimits,
        (snapshot, result) => snapshot with { CapturedAt = _clock.UtcNow, CodexRateLimits = result });

    private Task MarkTokenUsageStaleAsync() => MarkStaleAsync(
        snapshot => snapshot.CodexTokenUsage,
        _intervals.CodexTokenUsage,
        (snapshot, result) => snapshot with { CapturedAt = _clock.UtcNow, CodexTokenUsage = result });

    private Task MarkOpenCodeStaleAsync() => MarkStaleAsync(
        snapshot => snapshot.OpenCodeUsage,
        _intervals.OpenCodeUsage,
        (snapshot, result) => snapshot with { CapturedAt = _clock.UtcNow, OpenCodeUsage = result });

    private Task MarkStaleAsync<T>(
        Func<AppSnapshot, ProviderResult<T>> select,
        TimeSpan interval,
        Func<AppSnapshot, ProviderResult<T>, AppSnapshot> replace)
    {
        var now = _clock.UtcNow;
        _snapshots.Update(snapshot =>
        {
            var result = select(snapshot);
            if (result.Health == ProviderHealth.Healthy &&
                result.LastSuccessAt is { } lastSuccess &&
                now - lastSuccess > interval + interval)
            {
                return replace(snapshot, result with
                {
                    Health = ProviderHealth.Stale,
                    UserFacingError = "The last successful value is older than the refresh window.",
                    DiagnosticCode = "stale"
                });
            }

            return snapshot;
        });
        return Task.CompletedTask;
    }

    private static async Task RunExclusiveAsync(
        SemaphoreSlim gate,
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private Task Coalesce(TaskSlot slot, Func<Task> operation)
    {
        lock (_coalescingGate)
        {
            if (slot.Current is { IsCompleted: false })
            {
                return slot.Current;
            }

            var task = operation();
            slot.Current = task;
            _ = task.ContinueWith(
                _ =>
                {
                    lock (_coalescingGate)
                    {
                        if (ReferenceEquals(slot.Current, task))
                        {
                            slot.Current = null;
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    private sealed class TaskSlot
    {
        public Task? Current { get; set; }
    }

    private void AwakeChanged(object? sender, AwakeState state)
    {
        _snapshots.Update(snapshot => snapshot with
        {
            CapturedAt = _clock.UtcNow,
            Awake = state
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _awake.Changed -= AwakeChanged;
        _shutdown.Cancel();

        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _diagnostic?.Invoke("refresh_shutdown_failed", exception);
        }

        _accountGate.Dispose();
        _rateLimitsGate.Dispose();
        _tokenUsageGate.Dispose();
        _openCodeGate.Dispose();
        _shutdown.Dispose();
        await _codex.DisposeAsync().ConfigureAwait(false);
        if (_openCode is IDisposable disposableOpenCode)
        {
            disposableOpenCode.Dispose();
        }
    }
}
