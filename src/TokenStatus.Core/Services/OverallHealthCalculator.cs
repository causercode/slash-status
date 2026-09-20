using TokenStatus.Core.Models;

namespace TokenStatus.Core.Services;

public enum TrayHealth
{
    Gray,
    Green,
    Amber,
    Red
}

public static class OverallHealthCalculator
{
    public static TrayHealth Calculate(AppSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var codexMaximumQuota = snapshot.CodexRateLimits.Value?.Buckets
            .SelectMany(bucket => new[] { bucket.Primary, bucket.Secondary })
            .Where(window => window is not null)
            .Select(window => window!.UsedPercent)
            .DefaultIfEmpty(-1)
            .Max() ?? -1;
        var openCodeMaximumQuota = snapshot.OpenCodeGoQuota.Value is { } openCodeQuota
            ? new[] { openCodeQuota.Rolling, openCodeQuota.Weekly, openCodeQuota.Monthly }
                .Max(window => (int)Math.Ceiling(Math.Clamp(window.UsedPercent, 0m, 100m)))
            : -1;
        var maximumQuota = Math.Max(codexMaximumQuota, openCodeMaximumQuota);

        var quotaReached = snapshot.CodexRateLimits.Value?.Buckets.Any(bucket =>
            bucket.OrdinaryUsageAllowed == false || !string.IsNullOrWhiteSpace(bucket.ReachedType)) == true;
        quotaReached |= snapshot.OpenCodeGoQuota.Value is { } quota &&
            new[] { quota.Rolling, quota.Weekly, quota.Monthly }.Any(window => window.IsRateLimited);

        if (quotaReached || maximumQuota >= 90)
        {
            return TrayHealth.Red;
        }

        var degraded = new[]
        {
            snapshot.CodexAccount.Health,
            snapshot.CodexRateLimits.Health,
            snapshot.CodexTokenUsage.Health,
            snapshot.OpenCodeGoQuota.Health
        }.Any(IsDegraded);

        var providerResults = new[]
        {
            (Health: snapshot.CodexAccount.Health, HasValue: snapshot.CodexAccount.Value is not null),
            (Health: snapshot.CodexRateLimits.Health, HasValue: snapshot.CodexRateLimits.Value is not null),
            (Health: snapshot.CodexTokenUsage.Health, HasValue: snapshot.CodexTokenUsage.Value is not null),
            (Health: snapshot.OpenCodeGoQuota.Health, HasValue: snapshot.OpenCodeGoQuota.Value is not null)
        };
        if (providerResults.All(provider => provider.Health == ProviderHealth.Loading) ||
            providerResults.All(provider => !provider.HasValue && provider.Health is
                ProviderHealth.NotInstalled or
                ProviderHealth.NotConfigured or
                ProviderHealth.NotAuthenticated or
                ProviderHealth.Unsupported or
                ProviderHealth.Error))
        {
            return TrayHealth.Gray;
        }

        if (maximumQuota >= 75 || degraded)
        {
            return TrayHealth.Amber;
        }

        var hasHealthyProvider = new[]
        {
            snapshot.CodexAccount.Health,
            snapshot.CodexRateLimits.Health,
            snapshot.CodexTokenUsage.Health,
            snapshot.OpenCodeGoQuota.Health
        }.Any(health => health == ProviderHealth.Healthy);

        return hasHealthyProvider ? TrayHealth.Green : TrayHealth.Gray;
    }

    private static bool IsDegraded(ProviderHealth health) => health is
        ProviderHealth.Stale or
        ProviderHealth.NotInstalled or
        ProviderHealth.NotAuthenticated or
        ProviderHealth.Unsupported or
        ProviderHealth.Error;
}
