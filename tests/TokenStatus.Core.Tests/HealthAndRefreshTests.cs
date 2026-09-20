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

        public Task<CodexAccountInfo> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CodexAccountInfo(true, "chatgpt", "plus"));

        public Task<CodexRateLimits> GetRateLimitsAsync(CancellationToken cancellationToken)
        {
            RateLimitCalls++;
            return RateLimits;
        }

        public Task<CodexTokenUsage> GetTokenUsageAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CodexTokenUsage(10, 5, null, 1, 2, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
