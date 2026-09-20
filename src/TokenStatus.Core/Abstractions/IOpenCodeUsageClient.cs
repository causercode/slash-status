using TokenStatus.Core.Models;

namespace TokenStatus.Core.Abstractions;

public interface IOpenCodeUsageClient
{
    Task<OpenCodeLocalUsage> GetSevenDayUsageAsync(CancellationToken cancellationToken);
}
