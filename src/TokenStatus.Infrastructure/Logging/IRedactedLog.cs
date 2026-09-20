namespace TokenStatus.Infrastructure.Logging;

public interface IRedactedLog : IDisposable
{
    void Information(string provider, string operation, string outcome, TimeSpan duration, string? diagnosticCode = null);
    void Error(string provider, string operation, string diagnosticCode, Exception? exception = null, string? excerpt = null);
}
