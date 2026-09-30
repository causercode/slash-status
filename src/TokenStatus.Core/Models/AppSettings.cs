namespace TokenStatus.Core.Models;

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public const int MinimumProviderRefreshSeconds = 30;
    public const int MinimumTokenUsageRefreshSeconds = 300;
    public const int MinimumCodexRefreshSeconds = 300;
    public const int MinimumClaudeRefreshSeconds = 300;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string? CodexExecutablePath { get; init; }
    public string? ClaudeExecutablePath { get; init; }
    public bool ClaudeAutomaticRefreshEnabled { get; init; } = true;
    public int ClaudeRefreshSeconds { get; init; } = 300;
    public bool CodexAutomaticRefreshEnabled { get; init; } = true;
    public int CodexRateLimitRefreshSeconds { get; init; } = 300;
    public int CodexUsageRefreshSeconds { get; init; } = 1800;
    public int OpenCodeRefreshSeconds { get; init; } = 120;
    public bool QuotaNotificationsEnabled { get; init; } = true;
    public bool StartWithWindows { get; init; }

    public AppSettings Normalize()
    {
        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            ClaudeRefreshSeconds = Math.Max(MinimumClaudeRefreshSeconds, ClaudeRefreshSeconds),
            CodexRateLimitRefreshSeconds = Math.Max(MinimumCodexRefreshSeconds, CodexRateLimitRefreshSeconds),
            CodexUsageRefreshSeconds = Math.Max(MinimumTokenUsageRefreshSeconds, CodexUsageRefreshSeconds),
            OpenCodeRefreshSeconds = Math.Max(MinimumProviderRefreshSeconds, OpenCodeRefreshSeconds)
        };
    }
}
