namespace TokenStatus.Core.Models;

public sealed record OpenCodeLocalUsage(
    long Sessions,
    decimal TotalCost,
    long InputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long CacheReadTokens,
    long CacheWriteTokens);
