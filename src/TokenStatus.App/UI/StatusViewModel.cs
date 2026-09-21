using System.Globalization;
using TokenStatus.Core.Models;

namespace TokenStatus.App.UI;

public sealed class StatusViewModel
{
    public StatusViewModel(AppSnapshot snapshot, DateTimeOffset now)
    {
        Snapshot = snapshot;
        Now = now;
    }

    public AppSnapshot Snapshot { get; }
    public DateTimeOffset Now { get; }

    public string CodexHealth => DescribeHealth(Snapshot.CodexRateLimits.Health);
    public string CodexPlan => Snapshot.CodexAccount.Value?.PlanType ?? "Plan not provided";

    public string CodexAuthentication => Snapshot.CodexAccount.Value?.IsAuthenticated == true
        ? "Authenticated"
        : Snapshot.CodexAccount.Health == ProviderHealth.Healthy
            ? "Not authenticated"
            : DescribeHealth(Snapshot.CodexAccount.Health);

    public string AwakeSummary => !string.IsNullOrWhiteSpace(Snapshot.Awake.UserFacingError)
        ? Snapshot.Awake.UserFacingError!
        : Snapshot.Awake.Mode switch
        {
            AwakeMode.Off => "Off",
            _ when Snapshot.Awake.ExpiresAt is null => Snapshot.Awake.Mode == AwakeMode.System
                ? "System · until turned off"
                : "System + display · until turned off",
            _ => $"{ModeText(Snapshot.Awake.Mode)} · {FormatDuration(Snapshot.Awake.GetRemaining(Now))} remaining"
        };

    public string Tooltip => BuildTooltip(Snapshot);

    public static string BuildTooltip(AppSnapshot snapshot)
    {
        var bucket = snapshot.CodexRateLimits.Value?.Buckets.FirstOrDefault();
        var codex = bucket?.Primary is { } primary ? $"{primary.RemainingPercent}%" : "--";
        var secondary = bucket?.Secondary is { } secondaryWindow ? $"{secondaryWindow.RemainingPercent}%" : "--";
        var openCode = snapshot.OpenCodeGoQuota.Value is { } quota
            ? $"{quota.Rolling.RemainingPercent}/{quota.Weekly.RemainingPercent}%"
            : "--/--";
        var awake = snapshot.Awake.GetRemaining(DateTimeOffset.UtcNow) is { } remaining
            ? FormatShortDuration(remaining)
            : snapshot.Awake.IsActive ? "on" : "off";
        var text = $"Codex {codex}/{secondary} left | OC {openCode} left | Awake {awake}";
        return text.Length <= 63 ? text : text[..63];
    }

    public static string DescribeHealth(ProviderHealth health) => health switch
    {
        ProviderHealth.Healthy => "Healthy",
        ProviderHealth.Loading => "Loading",
        ProviderHealth.Stale => "Stale",
        ProviderHealth.NotConfigured => "Not configured",
        ProviderHealth.NotInstalled => "Not installed",
        ProviderHealth.NotAuthenticated => "Not authenticated",
        ProviderHealth.Unsupported => "Unsupported",
        ProviderHealth.Error => "Error",
        _ => "Unknown"
    };

    public static string FormatPercent(int? usedPercent) => usedPercent is { } value
        ? $"{Math.Clamp(value, 0, 100)}% used · {100 - Math.Clamp(value, 0, 100)}% remaining"
        : "Not provided";

    public static string FormatTokens(long? tokens)
    {
        if (tokens is null)
        {
            return "Not provided";
        }

        var value = tokens.Value;
        var absolute = Math.Abs((double)value);
        return absolute switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000d:0.0#}B",
            >= 1_000_000 => $"{value / 1_000_000d:0.0#}M",
            >= 1_000 => $"{value / 1_000d:0.0#}K",
            _ => value.ToString("N0", CultureInfo.CurrentCulture)
        };
    }

    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration is null)
        {
            return "until turned off";
        }

        var value = duration.Value;
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}h {value.Minutes}m";
        }

        return $"{Math.Max(0, value.Minutes)}m";
    }

    public static string FormatReset(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null)
        {
            return "resets Not provided";
        }

        var local = reset.Value.ToLocalTime();
        var difference = reset.Value - now;
        var relative = difference <= TimeSpan.Zero
            ? "now"
            : difference.TotalHours >= 24
                ? $"in {(int)difference.TotalDays}d"
                : $"in {Math.Max(1, (int)difference.TotalMinutes)}m";
        return $"resets {local:MMM d, h:mm tt} ({relative})";
    }

    private static string ModeText(AwakeMode mode) => mode == AwakeMode.System ? "System" : "System + display";

    private static string FormatShortDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}h"
        : $"{Math.Max(1, (int)duration.TotalMinutes)}m";
}
