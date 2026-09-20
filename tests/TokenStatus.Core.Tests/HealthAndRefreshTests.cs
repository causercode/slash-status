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
    public async Task ConcurrentManualRefreshesShareAnActiveRequest()
    {
        var now = DateTimeOffset.UtcNow;
        var releaseRateLimits = new TaskCompletionSource<CodexRateLimits>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codex = new FakeCodex
        {
            RateLimits = releaseRateLimits.Task
        };
        var openCode = new FakeOpenCode();
        using var awake = new FakeAwake();
        var store = new SnapshotStore(AppSnapshot.Initial(now));
        await using var coordinator = new RefreshCoordinator(
            codex,
            openCode,
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
        var openCode = new FakeOpenCode
        {
            Next = new OpenCodeLocalUsage(3, 1.25m, 10, 20, 30, 40, 50)
        };
        var codex = new FakeCodex();
        using var awake = new FakeAwake();
        var store = new SnapshotStore(AppSnapshot.Initial(now));
        await using var coordinator = new RefreshCoordinator(
            codex,
            openCode,
            awake,
            store,
            new FixedClock(now),
            new RefreshIntervals(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2)));

        await coordinator.RefreshNowAsync();
        openCode.Fail = true;
        await coordinator.RefreshNowAsync();

        var result = store.Current.OpenCodeUsage;
        Assert.Equal(ProviderHealth.Stale, result.Health);
        Assert.Equal(new OpenCodeLocalUsage(3, 1.25m, 10, 20, 30, 40, 50), result.Value);
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

    private sealed class FakeOpenCode : IOpenCodeUsageClient
    {
        public OpenCodeLocalUsage Next { get; set; } = new(0, 0, 0, 0, 0, 0, 0);
        public bool Fail { get; set; }

        public Task<OpenCodeLocalUsage> GetSevenDayUsageAsync(CancellationToken cancellationToken)
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
