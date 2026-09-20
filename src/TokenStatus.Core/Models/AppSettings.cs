namespace TokenStatus.Core.Models;

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public const int MinimumProviderRefreshSeconds = 30;
    public const int MinimumTokenUsageRefreshSeconds = 300;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string? CodexExecutablePath { get; init; }
    public string? OpenCodeExecutablePath { get; init; }
    public int CodexRateLimitRefreshSeconds { get; init; } = 120;
    public int CodexUsageRefreshSeconds { get; init; } = 600;
    public int OpenCodeRefreshSeconds { get; init; } = 120;
    public bool StartWithWindows { get; init; }

    public AppSettings Normalize()
    {
        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            CodexRateLimitRefreshSeconds = Math.Max(MinimumProviderRefreshSeconds, CodexRateLimitRefreshSeconds),
            CodexUsageRefreshSeconds = Math.Max(MinimumTokenUsageRefreshSeconds, CodexUsageRefreshSeconds),
            OpenCodeRefreshSeconds = Math.Max(MinimumProviderRefreshSeconds, OpenCodeRefreshSeconds)
        };
    }
}
