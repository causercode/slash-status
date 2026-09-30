using TokenStatus.Core.Models;

namespace TokenStatus.Core.Abstractions;

public interface ICodexUsageClient : IAsyncDisposable
{
    Task<CodexAccountInfo> GetAccountAsync(CancellationToken cancellationToken);
    Task<CodexRateLimits> GetRateLimitsAsync(CancellationToken cancellationToken);
    Task<CodexTokenUsage> GetTokenUsageAsync(CancellationToken cancellationToken);

    // Release the CLI executable between refresh batches so it can be updated.
    ValueTask DisconnectAsync() => ValueTask.CompletedTask;
}
