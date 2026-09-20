namespace TokenStatus.Core.Models;

public enum ProviderHealth
{
    Loading,
    Healthy,
    Stale,
    NotConfigured,
    NotInstalled,
    NotAuthenticated,
    Unsupported,
    Error
}
