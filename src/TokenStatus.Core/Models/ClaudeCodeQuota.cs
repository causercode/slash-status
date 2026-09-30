namespace TokenStatus.Core.Models;

public sealed record ClaudeCodeQuota(
    DateTimeOffset ObservedAt,
    RateLimitWindow? FiveHour,
    RateLimitWindow? SevenDay);
