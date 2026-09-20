namespace TokenStatus.Core.Models;

public sealed record CodexAccountInfo(
    bool IsAuthenticated,
    string? AuthMode,
    string? PlanType);

public sealed record CodexRateLimits(
    IReadOnlyList<RateLimitBucket> Buckets,
    int? ResetCreditCount,
    decimal? CreditBalance = null);

public sealed record DailyTokenUsage(
    DateOnly StartDate,
    long Tokens);

public sealed record CodexTokenUsage(
    long? LifetimeTokens,
    long? PeakDailyTokens,
    TimeSpan? LongestRunningTurn,
    int? CurrentStreakDays,
    int? LongestStreakDays,
    IReadOnlyList<DailyTokenUsage>? DailyUsageBuckets)
{
    public long? GetTokensForDate(DateOnly date)
    {
        if (DailyUsageBuckets is null)
        {
            return null;
        }

        return DailyUsageBuckets
            .Where(bucket => bucket.StartDate == date)
            .Select(bucket => (long?)bucket.Tokens)
            .FirstOrDefault() ?? 0;
    }
}
