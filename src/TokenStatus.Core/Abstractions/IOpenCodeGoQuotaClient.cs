using TokenStatus.Core.Models;

namespace TokenStatus.Core.Abstractions;

public interface IOpenCodeGoQuotaClient
{
    Task<OpenCodeGoQuota> GetQuotaAsync(CancellationToken cancellationToken);
}
