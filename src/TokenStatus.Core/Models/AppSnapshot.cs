namespace TokenStatus.Core.Models;

public sealed record AppSnapshot(
    DateTimeOffset CapturedAt,
    ProviderResult<CodexAccountInfo> CodexAccount,
    ProviderResult<CodexRateLimits> CodexRateLimits,
    ProviderResult<CodexTokenUsage> CodexTokenUsage,
    ProviderResult<OpenCodeLocalUsage> OpenCodeUsage,
    AwakeState Awake)
{
    public static AppSnapshot Initial(DateTimeOffset now) => new(
        now,
        ProviderResult<CodexAccountInfo>.Loading(now),
        ProviderResult<CodexRateLimits>.Loading(now),
        ProviderResult<CodexTokenUsage>.Loading(now),
        ProviderResult<OpenCodeLocalUsage>.Loading(now),
        AwakeState.Off);
}
