using TokenStatus.Infrastructure.OpenCode;
using TokenStatus.TestCli;

namespace TokenStatus.Infrastructure.Tests;

public sealed class OpenCodeClientTests
{
    [Fact]
    public async Task RunsVersionProbeAndFixedAggregateQueryThroughCli()
    {
        var executablePath = Path.ChangeExtension(typeof(TestCliMarker).Assembly.Location, ".exe");
        Assert.True(File.Exists(executablePath), executablePath);

        using var client = new OpenCodeUsageClient(executablePath);
        var usage = await client.GetSevenDayUsageAsync(CancellationToken.None);

        Assert.Equal(8, usage.Sessions);
        Assert.Equal(2.91m, usage.TotalCost);
        Assert.Equal(8900000, usage.CacheReadTokens);
    }
}
