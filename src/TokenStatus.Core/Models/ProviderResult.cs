namespace TokenStatus.Core.Models;

public sealed record ProviderResult<T>(
    ProviderHealth Health,
    T? Value,
    DateTimeOffset LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    string? UserFacingError,
    string? DiagnosticCode)
{
    public bool HasValue => Value is not null;

    public static ProviderResult<T> Loading(DateTimeOffset now) =>
        new(ProviderHealth.Loading, default, now, null, null, null);

    public ProviderResult<T> WithFailure(
        ProviderHealth health,
        DateTimeOffset attemptedAt,
        string? userFacingError,
        string? diagnosticCode) =>
        this with
        {
            Health = health,
            LastAttemptAt = attemptedAt,
            UserFacingError = userFacingError,
            DiagnosticCode = diagnosticCode
        };

    public ProviderResult<T> WithSuccess(T value, DateTimeOffset completedAt) =>
        new(ProviderHealth.Healthy, value, completedAt, completedAt, null, null);
}
