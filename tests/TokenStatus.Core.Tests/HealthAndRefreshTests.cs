using TokenStatus.Core.Abstractions;
using TokenStatus.Core.Models;
using TokenStatus.Core.Services;

namespace TokenStatus.Core.Tests;

public sealed class HealthAndRefreshTests
{
    [Fact]
    public void QuotaSeverityTakesPrecedenceOverProviderHealth()
    {
        var now = DateTimeOffset.UtcNow;
        var initial = AppSnapshot.Initial(now);
        var limits = new CodexRateLimits(
            [new RateLimitBucket(
                "codex",
                null,
                null,
                new RateLimitWindow(90, TimeSpan.FromHours(5), now.AddHours(1)),
                null,
                true,
                null)],
            null);
        var snapshot = initial with
        {
            CodexRateLimits = new ProviderResult<CodexRateLimits>(ProviderHealth.Stale, limits, now, now, "stale", "stale")
        };

        Assert.Equal(TrayHealth.Red, OverallHealthCalculator.Calculate(snapshot));
    }

    [Fact]
    public void OrdinaryUsageDeniedIsRedEvenWithLowPercentage()
    {
        var now = DateTimeOffset.UtcNow;
        var limits = new CodexRateLimits(
            [new RateLimitBucket(
                "codex",
                null,
                null,
                new RateLimitWindow(4, null, null),
                null,
                false,
                null)],
            null);
        var snapshot = AppSnapshot.Initial(now) with
        {
            CodexRateLimits = new ProviderResult<CodexRateLimits>(ProviderHealth.Healthy, limits, now, now, null, null)
        };

        Assert.Equal(TrayHealth.Red, OverallHealthCalculator.Calculate(snapshot));
    }

    [Fact]
    public void OpenCodeRateLimitIsRed()
    {
        var now = DateTimeOffset.UtcNow;
        var quota = new OpenCodeGoQuota(
            new OpenCodeQuotaWindow(12, now.AddHours(1), false),
            new OpenCodeQuotaWindow(44, now.AddDays(2), false),
            new OpenCodeQuotaWindow(100, now.AddDays(12), true));
        var snapshot = AppSnapshot.Initial(now) with
        {
            OpenCodeGoQuota = new ProviderResult<OpenCodeGoQuota>(
                ProviderHealth.Healthy, quota, now, now, null, null)
        };

        Assert.Equal(TrayHealth.Red, OverallHealthCalculator.Calculate(snapshot));
    }

    [Fact]
    public void UnconfiguredOpenCodeGoDoesNotDegradeAnotherHealthyProvider()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = AppSnapshot.Initial(now) with
        {
            OpenCodeGoQuota = new ProviderResult<OpenCodeGoQuota>(
                ProviderHealth.NotConfigured, null, now, null, "Configure a key.", "not_configured"),
            CodexTokenUsage = new ProviderResult<CodexTokenUsage>(
                ProviderHealth.Healthy, new CodexTokenUsage(10, 5, null, 1, 2, []), now, now, null, null)
        };

        Assert.Equal(TrayHealth.Green, OverallHealthCalculator.Calculate(snapshot));
    }

    [Fact]
    public async Task ConcurrentManualRefreshesShareAnActiveRequest()
    {
        var now = DateTimeOffset.UtcNow;
        var releaseRateLimits = new TaskCompletionSource<CodexRateLimits>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codex = new FakeCodex
        {
            RateLimits = releaseRateLimits.Task
        };
        var openCodeGo = new FakeOpenCodeGo();
        using var awake = new FakeAwake();
        var store = new SnapshotStore(AppSnapshot.Initial(now));
        await using var coordinator = new RefreshCoordinator(
            codex,
            openCodeGo,
            awake,
            store,
            new FixedClock(now),
            new RefreshIntervals(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2)));

        var first = coordinator.RefreshNowAsync();
        await WaitUntilAsync(() => codex.RateLimitCalls == 1);
        var second = coordinator.RefreshNowAsync();
        await Task.Delay(25);

        Assert.Equal(1, codex.RateLimitCalls);
        releaseRateLimits.SetResult(new CodexRateLimits([], null));
        await Task.WhenAll(first, second);
        Assert.Equal(1, codex.DisconnectCalls);
    }

    [Fact]
    public async Task PausingAutomaticCodexRefreshStillAllowsManualRefresh()
    {
        var now = DateTimeOffset.UtcNow;
        var codex = new FakeCodex();
        using var awake = new FakeAwake();
        await using var coordinator = new RefreshCoordinator(codex, new FakeOpenCodeGo(), awake,
            new SnapshotStore(AppSnapshot.Initial(now)), new FixedClock(now),
            new RefreshIntervals(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10),
                TimeSpan.FromHours(1), CodexAutomaticRefreshEnabled: false));
        coordinator.Start();
        await Task.Delay(100);
        Assert.Equal(0, codex.RateLimitCalls);
        await coordinator.RefreshNowAsync();
        Assert.Equal(1, codex.RateLimitCalls);
        Assert.Equal(1, codex.DisconnectCalls);
    }

    [Fact]
    public async Task ManualRefreshJoiningScheduledBatchStillRefreshesTokenUsage()
    {
        var now = DateTimeOffset.UtcNow;
        var release = new TaskCompletionSource<CodexRateLimits>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codex = new FakeCodex { RateLimits = release.Task };
        using var awake = new FakeAwake();
        var initial = AppSnapshot.Initial(now) with
        {
            CodexRateLimits = new ProviderResult<CodexRateLimits>(ProviderHealth.Healthy,
                new CodexRateLimits([], null), now.AddMinutes(-6), now.AddMinutes(-6), null, null),
            CodexTokenUsage = new ProviderResult<CodexTokenUsage>(ProviderHealth.Healthy,
                new CodexTokenUsage(10, 5, null, 1, 2, []), now, now, null, null)
        };
        await using var coordinator = new RefreshCoordinator(codex, new FakeOpenCodeGo(), awake,
            new SnapshotStore(initial), new FixedClock(now), RefreshIntervals.FromSettings(new AppSettings()));
        coordinator.Start();
        await WaitUntilAsync(() => codex.RateLimitCalls == 1);
        Assert.Equal(0, codex.TokenUsageCalls);
        var manual = coordinator.RefreshNowAsync();
        release.SetResult(new CodexRateLimits([], null));
        await manual;
        Assert.Equal(1, codex.TokenUsageCalls);
    }

    [Fact]
    public async Task ManualOnlyCodexReadingsStillBecomeStaleWithoutCliRequests()
    {
        var now = DateTimeOffset.UtcNow;
        var initial = AppSnapshot.Initial(now) with
        {
            CodexRateLimits = new ProviderResult<CodexRateLimits>(ProviderHealth.Healthy,
                new CodexRateLimits([], null), now.AddHours(-1), now.AddHours(-1), null, null)
        };
        var store = new SnapshotStore(initial);
        var codex = new FakeCodex();
        using var awake = new FakeAwake();
        await using var coordinator = new RefreshCoordinator(codex, new FakeOpenCodeGo(), awake, store,
            new FixedClock(now), RefreshIntervals.FromSettings(new AppSettings { CodexAutomaticRefreshEnabled = false }));
        coordinator.Start();
        await WaitUntilAsync(() => store.Current.CodexRateLimits.Health == ProviderHealth.Stale);
        Assert.Equal(0, codex.RateLimitCalls);
        Assert.NotNull(store.Current.CodexRateLimits.Value);
    }

    [Fact]
    public async Task FailedCodexRefreshReleasesTheProcess()
    {
        var now = DateTimeOffset.UtcNow;
        var codex = new FakeCodex { RateLimits = Task.FromException<CodexRateLimits>(new IOException("fixture")) };
        using var awake = new FakeAwake();
        var store = new SnapshotStore(AppSnapshot.Initial(now));
        await using var coordinator = new RefreshCoordinator(codex, new FakeOpenCodeGo(), awake, store,
            new FixedClock(now), RefreshIntervals.FromSettings(new AppSettings()));
        await coordinator.RefreshNowAsync();
        Assert.Equal(ProviderHealth.Error, store.Current.CodexRateLimits.Health);
        Assert.Equal(1, codex.DisconnectCalls);
    }

    [Fact]
    public async Task ShutdownDuringManualRefreshCancelsAndDisconnects()
    {
        var now = DateTimeOffset.UtcNow;
        var blocked = new TaskCompletionSource<CodexRateLimits>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codex = new FakeCodex { RateLimits = blocked.Task };
        using var awake = new FakeAwake();
        var coordinator = new RefreshCoordinator(codex, new FakeOpenCodeGo(), awake,
            new SnapshotStore(AppSnapshot.Initial(now)), new FixedClock(now),
            RefreshIntervals.FromSettings(new AppSettings()));
        var refresh = coordinator.RefreshNowAsync();
        await WaitUntilAsync(() => codex.RateLimitCalls == 1);
        await coordinator.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Equal(1, codex.DisconnectCalls);
    }

    [Fact]
    public async Task ClaudeAutomaticRefreshCanBePausedWhileManualRefreshStillWorks()
    {
        var now = DateTimeOffset.UtcNow;
        var quota = new ClaudeCodeQuota(now.AddHours(-1), new RateLimitWindow(25, TimeSpan.FromHours(5), null), null);
        var store = new SnapshotStore(AppSnapshot.Initial(now) with
        {
            ClaudeCodeQuota = new ProviderResult<ClaudeCodeQuota>(ProviderHealth.Healthy,
                quota, quota.ObservedAt, quota.ObservedAt, null, null)
        });
        var claude = new FakeClaude(quota);
        using var awake = new FakeAwake();
        await using var coordinator = new RefreshCoordinator(new FakeCodex(), new FakeOpenCodeGo(), awake, store,
            new FixedClock(now), RefreshIntervals.FromSettings(new AppSettings
            {
                CodexAutomaticRefreshEnabled = false,
                ClaudeAutomaticRefreshEnabled = false
            }), claudeCode: claude);
        coordinator.Start();
        await WaitUntilAsync(() => store.Current.ClaudeCodeQuota.Health == ProviderHealth.Stale);
        Assert.Equal(0, claude.Calls);
        await coordinator.RefreshNowAsync();
        Assert.Equal(1, claude.Calls);
    }

    [Fact]
    public async Task FailedClaudeQueryKeepsLastKnownQuota()
    {
        var now = DateTimeOffset.UtcNow;
        var quota = new ClaudeCodeQuota(now, new RateLimitWindow(25, TimeSpan.FromHours(5), null), null);
        var claude = new FakeClaude(quota);
        using var awake = new FakeAwake();
        var store = new SnapshotStore(AppSnapshot.Initial(now));
        await using var coordinator = new RefreshCoordinator(new FakeCodex(), new FakeOpenCodeGo(), awake, store,
            new FixedClock(now), RefreshIntervals.FromSettings(new AppSettings()), claudeCode: claude);
        await coordinator.RefreshNowAsync();
        claude.Fail = true;
        await coordinator.RefreshNowAsync();
        Assert.Equal(ProviderHealth.Stale, store.Current.ClaudeCodeQuota.Health);
        Assert.Same(quota, store.Current.ClaudeCodeQuota.Value);
        Assert.Equal(now, store.Current.ClaudeCodeQuota.LastSuccessAt);
    }

    [Fact]
    public void ClaudeAutomaticIntervalHasFiveMinuteMinimum()
    {
        var settings = new AppSettings { ClaudeRefreshSeconds = 1 }.Normalize();
        Assert.Equal(300, settings.ClaudeRefreshSeconds);
        Assert.Equal(TimeSpan.FromMinutes(5), RefreshIntervals.FromSettings(settings).ClaudeQuota);
        Assert.Equal(TimeSpan.FromMinutes(5), RefreshIntervals.FromSettings(new AppSettings()).ClaudeQuota);
    }

    [Fact]
    public async Task ClaudeStalenessUsesCheckTimeAndKeepsQuota()
    {
        var now = DateTimeOffset.UtcNow;
        using var awake = new FakeAwake();
        var store = new SnapshotStore(AppSnapshot.Initial(now));
        var claude = new FakeClaude(new ClaudeCodeQuota(now.AddHours(-1),
            new RateLimitWindow(25, TimeSpan.FromHours(5), now.AddHours(1)), null));
        await using var coordinator = new RefreshCoordinator(new FakeCodex(), new FakeOpenCodeGo(), awake, store,
            new FixedClock(now), RefreshIntervals.FromSettings(new AppSettings()), claudeCode: claude);
        await coordinator.RefreshNowAsync();
        Assert.Equal(ProviderHealth.Stale, store.Current.ClaudeCodeQuota.Health);
        Assert.Equal(75, store.Current.ClaudeCodeQuota.Value!.FiveHour!.RemainingPercent);
        Assert.Equal(now.AddHours(-1), store.Current.ClaudeCodeQuota.LastSuccessAt);
    }

    [Fact]
    public void ClaudeQuotaAffectsTrayHealthAndNotifications()
    {
        var now = DateTimeOffset.UtcNow;
        AppSnapshot Create(int used) => AppSnapshot.Initial(now) with
        {
            ClaudeCodeQuota = new ProviderResult<ClaudeCodeQuota>(ProviderHealth.Healthy,
                new ClaudeCodeQuota(now, new RateLimitWindow(used, TimeSpan.FromHours(5), now.AddHours(1)), null),
                now, now, null, null)
        };
        var tracker = new QuotaNotificationTracker();
        tracker.Evaluate(Create(60));
        var notification = Assert.Single(tracker.Evaluate(Create(95)));
        Assert.Equal("Claude Code", notification.Provider);
        Assert.Equal(QuotaNotificationKind.Critical, notification.Kind);
        Assert.Equal(TrayHealth.Red, OverallHealthCalculator.Calculate(Create(95)));
    }

    [Fact]
    public async Task FailurePreservesLastKnownGoodValue()
    {
        var now = DateTimeOffset.UtcNow;
        var codex = new FakeCodex();
        var expected = new OpenCodeGoQuota(
            new OpenCodeQuotaWindow(11, now.AddHours(2), false),
            new OpenCodeQuotaWindow(22, now.AddDays(2), false),
            new OpenCodeQuotaWindow(33, now.AddDays(12), false));
        var openCodeGo = new FakeOpenCodeGo { Next = expected };
        using var awake = new FakeAwake();
        var store = new SnapshotStore(AppSnapshot.Initial(now));
        await using var coordinator = new RefreshCoordinator(
            codex,
            openCodeGo,
            awake,
            store,
            new FixedClock(now),
            new RefreshIntervals(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2)));

        await coordinator.RefreshNowAsync();
        openCodeGo.Fail = true;
        await coordinator.RefreshNowAsync();

        var result = store.Current.OpenCodeGoQuota;
        Assert.Equal(ProviderHealth.Stale, result.Health);
        Assert.Equal(expected, result.Value);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "The expected refresh did not start.");
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeCodex : ICodexUsageClient
    {
        public Task<CodexRateLimits> RateLimits { get; set; } = Task.FromResult(new CodexRateLimits([], null));
        public int RateLimitCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public int TokenUsageCalls { get; private set; }

        public Task<CodexAccountInfo> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CodexAccountInfo(true, "chatgpt", "plus"));

        public Task<CodexRateLimits> GetRateLimitsAsync(CancellationToken cancellationToken)
        {
            RateLimitCalls++;
            return RateLimits.WaitAsync(cancellationToken);
        }

        public Task<CodexTokenUsage> GetTokenUsageAsync(CancellationToken cancellationToken)
        {
            TokenUsageCalls++;
            return Task.FromResult(new CodexTokenUsage(10, 5, null, 1, 2, []));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync()
        {
            DisconnectCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeClaude(ClaudeCodeQuota value) : IClaudeCodeQuotaClient
    {
        public int Calls;
        public bool Fail { get; set; }
        public Task<ClaudeCodeQuota> GetQuotaAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (Fail) throw new IOException("fixture");
            return Task.FromResult(value);
        }
    }

    private sealed class FakeOpenCodeGo : IOpenCodeGoQuotaClient
    {
        public OpenCodeGoQuota Next { get; set; } =
            new OpenCodeGoQuota(
                new OpenCodeQuotaWindow(10, DateTimeOffset.UtcNow.AddHours(1), false),
                new OpenCodeQuotaWindow(20, DateTimeOffset.UtcNow.AddDays(1), false),
                new OpenCodeQuotaWindow(30, DateTimeOffset.UtcNow.AddDays(10), false));
        public bool Fail { get; set; }

        public Task<OpenCodeGoQuota> GetQuotaAsync(CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("simulated");
            }

            return Task.FromResult(Next);
        }
    }

    private sealed class FakeAwake : IAwakeController
    {
        public AwakeState Current => AwakeState.Off;
        public event EventHandler<AwakeState>? Changed;
        public void Start(AwakeMode mode, TimeSpan? duration) => Changed?.Invoke(this, new AwakeState(mode, DateTimeOffset.UtcNow, null));
        public void Stop() => Changed?.Invoke(this, AwakeState.Off);
        public void Dispose() { }
    }
}
