namespace TokenStatus.Core.Models;

public sealed record RateLimitBucket(
    string Id,
    string? DisplayName,
    string? PlanType,
    RateLimitWindow? Primary,
    RateLimitWindow? Secondary,
    bool? OrdinaryUsageAllowed,
    string? ReachedType);
