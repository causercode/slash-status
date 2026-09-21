namespace TokenStatus.Core.Models;

public enum AwakeMode
{
    Off,
    System,
    SystemAndDisplay
}

public sealed record AwakeState(
    AwakeMode Mode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ExpiresAt,
    string? UserFacingError = null,
    string? DiagnosticCode = null)
{
    public static AwakeState Off => new(AwakeMode.Off, null, null);

    public bool IsActive => Mode != AwakeMode.Off;

    public TimeSpan? GetRemaining(DateTimeOffset now)
    {
        if (ExpiresAt is null)
        {
            return null;
        }

        var remaining = ExpiresAt.Value - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
