using TokenStatus.Core.Models;

namespace TokenStatus.Core.Abstractions;

public interface IClaudeCodeQuotaClient
{
    Task<ClaudeCodeQuota> GetQuotaAsync(CancellationToken cancellationToken);
}
