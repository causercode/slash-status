namespace TokenStatus.Core.Models;

public sealed record OpenCodeGoQuota(
    OpenCodeQuotaWindow Rolling,
    OpenCodeQuotaWindow Weekly,
    OpenCodeQuotaWindow Monthly);

public sealed record OpenCodeQuotaWindow(
    decimal UsedPercent,
    DateTimeOffset ResetsAt,
    bool IsRateLimited)
{
    public int RemainingPercent => Math.Clamp(
        100 - (int)Math.Round(Math.Clamp(UsedPercent, 0m, 100m), MidpointRounding.AwayFromZero),
        0,
        100);
}
