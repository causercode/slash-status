using TokenStatus.Core.Models;

namespace TokenStatus.Core.Services;

public enum QuotaNotificationKind
{
    Low,
    Critical,
    Reset
}

public sealed record QuotaNotification(
    QuotaNotificationKind Kind,
    string Provider,
    string Window,
    int RemainingPercent);

/// <summary>
/// Detects meaningful quota transitions while keeping notification delivery out
/// of the provider and polling layers.
/// </summary>
public sealed class QuotaNotificationTracker
{
    private const int LowThreshold = 25;
    private const int CriticalThreshold = 5;
    private readonly Dictionary<string, QuotaState> _states = new(StringComparer.Ordinal);

    public IReadOnlyList<QuotaNotification> Evaluate(AppSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var notifications = new List<QuotaNotification>();
        foreach (var reading in ReadQuotas(snapshot))
        {
            if (_states.TryGetValue(reading.Key, out var state))
            {
                var kind = DetectTransition(state, reading);
                if (kind is { } notificationKind)
                {
                    notifications.Add(new QuotaNotification(
                        notificationKind,
                        reading.Provider,
                        reading.Window,
                        reading.RemainingPercent));
                }

                state.Previous = reading;
            }
            else
            {
                // The first reading establishes state without surprising the user
                // with alerts immediately after application startup.
                _states[reading.Key] = new QuotaState(reading)
                {
                    LowNotified = reading.RemainingPercent <= LowThreshold,
                    CriticalNotified = reading.RemainingPercent <= CriticalThreshold
                };
            }
        }

        return notifications;
    }

    public void Reset() => _states.Clear();

    private static QuotaNotificationKind? DetectTransition(QuotaState state, QuotaReading current)
    {
        if (HasReset(state.Previous, current))
        {
            state.LowNotified = false;
            state.CriticalNotified = false;
            return QuotaNotificationKind.Reset;
        }

        // When one refresh crosses both thresholds, report only the most urgent
        // one. Subsequent identical readings do not generate another alert.
        if (!state.CriticalNotified &&
            state.Previous.RemainingPercent > CriticalThreshold &&
            current.RemainingPercent <= CriticalThreshold)
        {
            state.LowNotified = true;
            state.CriticalNotified = true;
            return QuotaNotificationKind.Critical;
        }

        if (!state.LowNotified &&
            state.Previous.RemainingPercent > LowThreshold &&
            current.RemainingPercent <= LowThreshold)
        {
            state.LowNotified = true;
            return QuotaNotificationKind.Low;
        }

        return null;
    }

    private static bool HasReset(QuotaReading previous, QuotaReading current)
    {
        if (current.RemainingPercent <= previous.RemainingPercent)
        {
            return false;
        }

        if (current.RemainingPercent == 100)
        {
            return true;
        }

        // Polling can miss the brief moment at exactly 100%. A later reset time,
        // together with replenished quota, is reliable evidence of a new window.
        return current.HasFixedResetWindow &&
               previous.ResetsAt is { } previousReset &&
               current.ResetsAt is { } currentReset &&
               currentReset > previousReset;
    }

    private static IEnumerable<QuotaReading> ReadQuotas(AppSnapshot snapshot)
    {
        if (snapshot.CodexRateLimits.Health == ProviderHealth.Healthy &&
            snapshot.CodexRateLimits.Value is { } codex)
        {
            foreach (var bucket in codex.Buckets)
            {
                var provider = string.IsNullOrWhiteSpace(bucket.DisplayName)
                    ? "Codex"
                    : bucket.DisplayName.Trim();

                if (bucket.Primary is { } primary)
                {
                    yield return ToReading(
                        $"codex:{bucket.Id}:primary",
                        provider,
                        DescribeWindow(primary.Duration, "primary"),
                        primary,
                        hasFixedResetWindow: true);
                }

                if (bucket.Secondary is { } secondary)
                {
                    yield return ToReading(
                        $"codex:{bucket.Id}:secondary",
                        provider,
                        DescribeWindow(secondary.Duration, "secondary"),
                        secondary,
                        hasFixedResetWindow: true);
                }
            }
        }

        if (snapshot.OpenCodeGoQuota.Health == ProviderHealth.Healthy &&
            snapshot.OpenCodeGoQuota.Value is { } openCode)
        {
            yield return ToReading(
                "opencode:rolling", "OpenCode Go", "5-hour", openCode.Rolling, hasFixedResetWindow: false);
            yield return ToReading(
                "opencode:weekly", "OpenCode Go", "weekly", openCode.Weekly, hasFixedResetWindow: true);
            yield return ToReading(
                "opencode:monthly", "OpenCode Go", "monthly", openCode.Monthly, hasFixedResetWindow: true);
        }
    }

    private static QuotaReading ToReading(
        string key,
        string provider,
        string window,
        RateLimitWindow value,
        bool hasFixedResetWindow) =>
        new(key, provider, window, value.RemainingPercent, value.ResetsAt, hasFixedResetWindow);

    private static QuotaReading ToReading(
        string key,
        string provider,
        string window,
        OpenCodeQuotaWindow value,
        bool hasFixedResetWindow) =>
        new(key, provider, window, value.RemainingPercent, value.ResetsAt, hasFixedResetWindow);

    private static string DescribeWindow(TimeSpan? duration, string fallback)
    {
        if (duration is not { } value)
        {
            return fallback;
        }

        if (IsNear(value, TimeSpan.FromHours(5)))
        {
            return "5-hour";
        }

        if (IsNear(value, TimeSpan.FromDays(1)))
        {
            return "daily";
        }

        if (IsNear(value, TimeSpan.FromDays(7)))
        {
            return "weekly";
        }

        return fallback;
    }

    private static bool IsNear(TimeSpan value, TimeSpan expected) =>
        Math.Abs((value - expected).TotalMinutes) < 1;

    private sealed record QuotaReading(
        string Key,
        string Provider,
        string Window,
        int RemainingPercent,
        DateTimeOffset? ResetsAt,
        bool HasFixedResetWindow);

    private sealed class QuotaState(QuotaReading previous)
    {
        public QuotaReading Previous { get; set; } = previous;
        public bool LowNotified { get; set; }
        public bool CriticalNotified { get; set; }
    }
}
