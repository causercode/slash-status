namespace TokenStatus.Infrastructure.Logging;

public sealed class NullRedactedLog : IRedactedLog
{
    public void Information(string provider, string operation, string outcome, TimeSpan duration, string? diagnosticCode = null)
    {
    }

    public void Error(string provider, string operation, string diagnosticCode, Exception? exception = null, string? excerpt = null)
    {
    }

    public void Dispose()
    {
    }
}
