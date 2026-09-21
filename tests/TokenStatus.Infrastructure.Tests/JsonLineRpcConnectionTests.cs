using TokenStatus.Infrastructure.Codex;
using TokenStatus.TestCli;

namespace TokenStatus.Infrastructure.Tests;

public sealed class JsonLineRpcConnectionTests
{
    [Fact]
    public async Task CompletesInitializationBeforeRoutingRequests()
    {
        var testCliAssembly = typeof(TestCliMarker).Assembly.Location;
        var executablePath = Path.ChangeExtension(testCliAssembly, ".exe");
        Assert.True(File.Exists(executablePath), executablePath);

        await using var connection = new JsonLineRpcConnection(
            executablePath,
            "test",
            requestTimeout: TimeSpan.FromSeconds(2),
            processArguments: ["codex"]);

        var result = await connection.RequestAsync("account/read", new { refreshToken = false });

        Assert.Equal("chatgpt", result.GetProperty("authMode").GetString());
        Assert.Equal("plus", result.GetProperty("planType").GetString());
    }

    [Fact]
    public async Task OversizedResponseFailsAndStopsTheChildProcess()
    {
        var testCliAssembly = typeof(TestCliMarker).Assembly.Location;
        var executablePath = Path.ChangeExtension(testCliAssembly, ".exe");
        Assert.True(File.Exists(executablePath), executablePath);

        await using var connection = new JsonLineRpcConnection(
            executablePath,
            "test",
            requestTimeout: TimeSpan.FromSeconds(2),
            processArguments: ["codex", "oversized"],
            maximumLineCharacters: 128);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            connection.RequestAsync("account/read", new { refreshToken = false }));
    }
}
