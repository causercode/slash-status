using TokenStatus.Core.Models;
using TokenStatus.Core.Services;

namespace TokenStatus.Core.Tests;

public sealed class QuotaNotificationTrackerTests
{
    [Fact]
    public void InitialReadingDoesNotNotify()
    {
        var tracker = new QuotaNotificationTracker();

        var notifications = tracker.Evaluate(CodexSnapshot(25));

        Assert.Empty(notifications);
    }

    [Fact]
    public void CrossingLowThresholdNotifiesOnlyOnce()
    {
        var tracker = new QuotaNotificationTracker();
        tracker.Evaluate(CodexSnapshot(26));

        var first = Assert.Single(tracker.Evaluate(CodexSnapshot(25)));
        var repeated = tracker.Evaluate(CodexSnapshot(24));

        Assert.Equal(QuotaNotificationKind.Low, first.Kind);
        Assert.Equal("daily", first.Window);
        Assert.Equal(25, first.RemainingPercent);
        Assert.Empty(repeated);
    }

    [Fact]
    public void CrossingBothThresholdsReportsOnlyCriticalAlert()
    {
        var tracker = new QuotaNotificationTracker();
        tracker.Evaluate(CodexSnapshot(30));

        var notification = Assert.Single(tracker.Evaluate(CodexSnapshot(4)));

        Assert.Equal(QuotaNotificationKind.Critical, notification.Kind);
        Assert.Equal(4, notification.RemainingPercent);
    }

    [Fact]
    public void ThresholdDoesNotRepeatAfterQuotaTemporarilyRecovers()
    {
        var tracker = new QuotaNotificationTracker();
        tracker.Evaluate(CodexSnapshot(30));
        Assert.Single(tracker.Evaluate(CodexSnapshot(25)));
        tracker.Evaluate(CodexSnapshot(30));

        var repeated = tracker.Evaluate(CodexSnapshot(25));

        Assert.Empty(repeated);
    }

    [Fact]
    public void ResetRearmsThresholdNotifications()
    {
        var tracker = new QuotaNotificationTracker();
        tracker.Evaluate(CodexSnapshot(30));
        Assert.Single(tracker.Evaluate(CodexSnapshot(25)));
        Assert.Equal(
            QuotaNotificationKind.Reset,
            Assert.Single(tracker.Evaluate(CodexSnapshot(100))).Kind);

        var notification = Assert.Single(tracker.Evaluate(CodexSnapshot(25)));

        Assert.Equal(QuotaNotificationKind.Low, notification.Kind);
    }

    [Fact]
    public void ReachingFullQuotaAfterUsageReportsReset()
    {
        var tracker = new QuotaNotificationTracker();
        tracker.Evaluate(CodexSnapshot(5));

        var notification = Assert.Single(tracker.Evaluate(CodexSnapshot(100)));

        Assert.Equal(QuotaNotificationKind.Reset, notification.Kind);
        Assert.Equal(100, notification.RemainingPercent);
    }

    [Fact]
    public void AdvancedResetWindowIsDetectedEvenIfPollingMissedOneHundredPercent()
    {
        var tracker = new QuotaNotificationTracker();
        var reset = DateTimeOffset.Parse("2026-09-21T00:00:00Z");
        tracker.Evaluate(OpenCodeSnapshot(4, reset));

        var notification = Assert.Single(tracker.Evaluate(OpenCodeSnapshot(98, reset.AddDays(1))));

        Assert.Equal(QuotaNotificationKind.Reset, notification.Kind);
        Assert.Equal("weekly", notification.Window);
        Assert.Equal(98, notification.RemainingPercent);
    }

    private static AppSnapshot CodexSnapshot(int remainingPercent)
    {
        var now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        var limits = new CodexRateLimits(
            [new RateLimitBucket(
                "codex",
                "Codex",
                null,
                new RateLimitWindow(100 - remainingPercent, TimeSpan.FromDays(1), now.AddDays(1)),
                null,
                true,
                null)],
            null);
        return AppSnapshot.Initial(now) with
        {
            CodexRateLimits = new ProviderResult<CodexRateLimits>(
                ProviderHealth.Healthy, limits, now, now, null, null)
        };
    }

    private static AppSnapshot OpenCodeSnapshot(int weeklyRemainingPercent, DateTimeOffset reset)
    {
        var now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        var quota = new OpenCodeGoQuota(
            new OpenCodeQuotaWindow(10, now.AddHours(2), false),
            new OpenCodeQuotaWindow(100 - weeklyRemainingPercent, reset, false),
            new OpenCodeQuotaWindow(10, now.AddDays(20), false));
        return AppSnapshot.Initial(now) with
        {
            OpenCodeGoQuota = new ProviderResult<OpenCodeGoQuota>(
                ProviderHealth.Healthy, quota, now, now, null, null)
        };
    }
}
