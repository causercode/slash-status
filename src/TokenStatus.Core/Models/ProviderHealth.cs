namespace TokenStatus.Core.Models;

public enum ProviderHealth
{
    Loading,
    Healthy,
    Stale,
    NotInstalled,
    NotAuthenticated,
    Unsupported,
    Error
}
