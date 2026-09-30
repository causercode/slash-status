using TokenStatus.Core.Abstractions;
using TokenStatus.Core.Models;

namespace TokenStatus.Core.Services;

public sealed record RefreshIntervals(
    TimeSpan CodexRateLimits,
    TimeSpan CodexTokenUsage,
    TimeSpan OpenCodeQuota,
    bool CodexAutomaticRefreshEnabled = true,
    TimeSpan? ClaudeQuota = null,
    bool ClaudeAutomaticRefreshEnabled = true)
{
    public static RefreshIntervals FromSettings(AppSettings settings) => new(
        TimeSpan.FromSeconds(Math.Max(AppSettings.MinimumCodexRefreshSeconds, settings.CodexRateLimitRefreshSeconds)),
        TimeSpan.FromSeconds(Math.Max(AppSettings.MinimumTokenUsageRefreshSeconds, settings.CodexUsageRefreshSeconds)),
        TimeSpan.FromSeconds(Math.Max(AppSettings.MinimumProviderRefreshSeconds, settings.OpenCodeRefreshSeconds)),
        settings.CodexAutomaticRefreshEnabled,
        TimeSpan.FromSeconds(Math.Max(AppSettings.MinimumClaudeRefreshSeconds, settings.ClaudeRefreshSeconds)),
        settings.ClaudeAutomaticRefreshEnabled);
}

public sealed class RefreshCoordinator : IAsyncDisposable
{
    private readonly ICodexUsageClient _codex;
    private readonly IOpenCodeGoQuotaClient _openCodeGo;
    private readonly IClaudeCodeQuotaClient? _claudeCode;
    private readonly IAwakeController _awake;
    private readonly SnapshotStore _snapshots;
    private readonly IClock _clock;
    private readonly RefreshIntervals _intervals;
    private readonly Action<string, Exception?>? _diagnostic;
    private readonly SemaphoreSlim _accountGate = new(1, 1);
    private readonly SemaphoreSlim _rateLimitsGate = new(1, 1);
    private readonly SemaphoreSlim _tokenUsageGate = new(1, 1);
    private readonly SemaphoreSlim _openCodeGoGate = new(1, 1);
    private readonly SemaphoreSlim _claudeCodeGate = new(1, 1);
    private readonly object _coalescingGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _loops = [];
    private readonly TaskSlot _accountRefresh = new();
    private readonly TaskSlot _codexRefresh = new();
    private readonly TaskSlot _rateLimitsRefresh = new();
    private readonly TaskSlot _tokenUsageRefresh = new();
    private readonly TaskSlot _openCodeGoRefresh = new();
    private readonly TaskSlot _claudeCodeRefresh = new();
    private static readonly TimeSpan FreshnessInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ClaudeMaximumAge = TimeSpan.FromMinutes(15);
    private bool _codexRefreshIsForced;
    private int _started;
    private int _disposed;

    public RefreshCoordinator(
        ICodexUsageClient codex,
        IOpenCodeGoQuotaClient openCodeGo,
        IAwakeController awake,
        SnapshotStore snapshots,
        IClock clock,
        RefreshIntervals intervals,
        Action<string, Exception?>? diagnostic = null,
        IClaudeCodeQuotaClient? claudeCode = null)
    {
        _codex = codex;
        _openCodeGo = openCodeGo;
        _claudeCode = claudeCode;
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

        if (_intervals.CodexAutomaticRefreshEnabled)
        {
            var interval = _intervals.CodexRateLimits < _intervals.CodexTokenUsage
                ? _intervals.CodexRateLimits : _intervals.CodexTokenUsage;
            _loops.Add(Task.Run(() => RunLoopAsync(
                token => RefreshCodexAsync(false, token), interval, () => Task.CompletedTask)));
        }
        _loops.Add(Task.Run(() => RunLoopAsync(RefreshOpenCodeGoAsync, _intervals.OpenCodeQuota, MarkOpenCodeGoStaleAsync)));
        if (_claudeCode is not null && _intervals.ClaudeAutomaticRefreshEnabled)
        {
            _loops.Add(Task.Run(() => RunLoopAsync(RefreshClaudeCodeAsync,
                _intervals.ClaudeQuota ?? TimeSpan.FromMinutes(5), MarkClaudeStaleAsync)));
        }
        // Freshness checks remain active in manual-only mode, without starting a CLI.
        _loops.Add(Task.Run(() => RunLoopAsync(
            RefreshLocalStateAsync, FreshnessInterval, () => Task.CompletedTask)));
    }

    public async Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.WhenAll(
            RefreshCodexAsync(true, _shutdown.Token),
            RefreshOpenCodeGoAsync(_shutdown.Token),
            RefreshClaudeCodeAsync(_shutdown.Token)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshCodexAsync(bool force, CancellationToken cancellationToken)
    {
        Task refresh;
        bool followUp;
        lock (_coalescingGate)
        {
            // A scheduled batch may omit token usage. A manual request must still
            // refresh every Codex value, even when it joins that active batch.
            followUp = force && !_codexRefreshIsForced &&
                _codexRefresh.Current is { IsCompleted: false };
            refresh = Coalesce(_codexRefresh, () =>
            {
                _codexRefreshIsForced = force;
                return RefreshCodexCoreAsync(force, cancellationToken);
            });
        }
        await refresh.ConfigureAwait(false);
        if (followUp)
        {
            await RefreshCodexAsync(true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshCodexCoreAsync(bool force, CancellationToken cancellationToken)
    {
        var snapshot = _snapshots.Current;
        var now = _clock.UtcNow;
        var tasks = new List<Task>();
        try
        {
            if (force || now - snapshot.CodexRateLimits.LastAttemptAt >= _intervals.CodexRateLimits ||
                snapshot.CodexRateLimits.Health == ProviderHealth.Loading)
            {
                tasks.Add(RefreshAccountAsync(cancellationToken));
                tasks.Add(RefreshRateLimitsAsync(cancellationToken));
            }
            if (force || now - snapshot.CodexTokenUsage.LastAttemptAt >= _intervals.CodexTokenUsage ||
                snapshot.CodexTokenUsage.Health == ProviderHealth.Loading)
            {
                tasks.Add(RefreshTokenUsageAsync(cancellationToken));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            await _codex.DisconnectAsync().ConfigureAwait(false);
        }
    }

    private Task MarkCodexStaleAsync() => Task.WhenAll(
        MarkAccountStaleAsync(), MarkRateLimitsStaleAsync(), MarkTokenUsageStaleAsync());

    private Task RefreshLocalStateAsync(CancellationToken cancellationToken) => Task.WhenAll(
        MarkClaudeStaleAsync(), MarkCodexStaleAsync());

    private Task RefreshClaudeCodeAsync(CancellationToken cancellationToken) => _claudeCode is null
        ? Task.CompletedTask
        : Coalesce(_claudeCodeRefresh, () => RefreshClaudeCodeCoreAsync(cancellationToken));

    private async Task RefreshClaudeCodeCoreAsync(CancellationToken cancellationToken)
    {
        await RunExclusiveAsync(_claudeCodeGate, async () =>
        {
            var attempt = _clock.UtcNow;
            try
            {
                var value = await _claudeCode!.GetQuotaAsync(cancellationToken).ConfigureAwait(false);
                var stale = attempt - value.ObservedAt > ClaudeMaximumAge ||
                    value.ObservedAt > attempt.AddMinutes(5);
                _snapshots.Update(snapshot => snapshot with
                {
                    CapturedAt = _clock.UtcNow,
                    ClaudeCodeQuota = new ProviderResult<ClaudeCodeQuota>(
                        stale ? ProviderHealth.Stale : ProviderHealth.Healthy,
                        value, attempt, value.ObservedAt,
                        stale ? "The last Claude usage check is stale. Click Refresh to try again." : null,
                        stale ? "claude_usage_stale" : null)
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (RecordFailure(
                "claude_code", exception, attempt, _snapshots.Current.ClaudeCodeQuota,
                result => _snapshots.Update(snapshot => snapshot with
                {
                    CapturedAt = _clock.UtcNow,
                    ClaudeCodeQuota = result
                })))
            {
            }
        }, cancellationToken).ConfigureAwait(false);
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

    private Task RefreshOpenCodeGoAsync(CancellationToken cancellationToken) => Coalesce(
        _openCodeGoRefresh,
        () => RefreshOpenCodeGoCoreAsync(cancellationToken));

    private async Task RefreshOpenCodeGoCoreAsync(CancellationToken cancellationToken)
    {
        await RunExclusiveAsync(_openCodeGoGate, async () =>
        {
            var attempt = _clock.UtcNow;
            try
            {
                var value = await _openCodeGo.GetQuotaAsync(cancellationToken).ConfigureAwait(false);
                _snapshots.Update(snapshot => snapshot with
                {
                    CapturedAt = _clock.UtcNow,
                    OpenCodeGoQuota = new ProviderResult<OpenCodeGoQuota>(
                        ProviderHealth.Healthy, value, attempt, _clock.UtcNow, null, null)
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (RecordFailure(
                "opencode_go_quota", exception, attempt, _snapshots.Current.OpenCodeGoQuota, result => _snapshots.Update(current => current with
                {
                    CapturedAt = _clock.UtcNow,
                    OpenCodeGoQuota = result
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
        if (health != ProviderHealth.NotConfigured)
        {
            _diagnostic?.Invoke(operation + ":" + code, exception);
        }
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

    private Task MarkOpenCodeGoStaleAsync() => MarkStaleAsync(
        snapshot => snapshot.OpenCodeGoQuota,
        _intervals.OpenCodeQuota,
        (snapshot, result) => snapshot with { CapturedAt = _clock.UtcNow, OpenCodeGoQuota = result });

    private Task MarkClaudeStaleAsync() => MarkStaleAsync(
        snapshot => snapshot.ClaudeCodeQuota,
        _intervals.ClaudeQuota ?? TimeSpan.FromMinutes(5),
        (snapshot, result) => snapshot with { CapturedAt = _clock.UtcNow, ClaudeCodeQuota = result });

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
            Task[] active;
            lock (_coalescingGate)
            {
                active = new[] { _codexRefresh.Current, _openCodeGoRefresh.Current, _claudeCodeRefresh.Current }
                    .OfType<Task>().ToArray();
            }
            await Task.WhenAll(_loops.Concat(active)).ConfigureAwait(false);
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
        _openCodeGoGate.Dispose();
        _claudeCodeGate.Dispose();
        _shutdown.Dispose();
        await _codex.DisposeAsync().ConfigureAwait(false);
        if (_openCodeGo is IDisposable disposableOpenCodeGo)
        {
            disposableOpenCodeGo.Dispose();
        }
    }
}
