namespace TokenStatus.Core.Models;

public sealed class ProviderFailureException : Exception
{
    public ProviderFailureException(
        ProviderHealth health,
        string userFacingMessage,
        string diagnosticCode,
        Exception? innerException = null)
        : base(userFacingMessage, innerException)
    {
        Health = health;
        UserFacingMessage = userFacingMessage;
        DiagnosticCode = diagnosticCode;
    }

    public ProviderHealth Health { get; }
    public string UserFacingMessage { get; }
    public string DiagnosticCode { get; }
}
