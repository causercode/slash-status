namespace TokenStatus.Core.Models;

public sealed record RateLimitWindow(
    int UsedPercent,
    TimeSpan? Duration,
    DateTimeOffset? ResetsAt)
{
    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}
