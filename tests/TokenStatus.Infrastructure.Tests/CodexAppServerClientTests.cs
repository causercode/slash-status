using TokenStatus.Infrastructure.Codex;
using TokenStatus.TestCli;

namespace TokenStatus.Infrastructure.Tests;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public async Task DisconnectReleasesExecutableAndNextRefreshCanRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TokenStatus.CodexTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourceDirectory = Path.GetDirectoryName(typeof(TestCliMarker).Assembly.Location)!;
            foreach (var source in Directory.EnumerateFiles(sourceDirectory, "TokenStatus.TestCli.*"))
                File.Copy(source, Path.Combine(directory, Path.GetFileName(source)));
            var executable = Path.Combine(directory, "TokenStatus.TestCli.exe");
            await using var client = new CodexAppServerClient(executable);
            await Task.WhenAll(client.GetAccountAsync(CancellationToken.None),
                client.GetRateLimitsAsync(CancellationToken.None), client.GetTokenUsageAsync(CancellationToken.None));
            await client.DisconnectAsync();

            using (new FileStream(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

            // Windows forbids replacement of a running executable. This proves the
            // app-server exited, rather than merely dropping our Process object.
            var moved = executable + ".released";
            File.Move(executable, moved);
            File.Move(moved, executable);
            var next = await client.GetRateLimitsAsync(CancellationToken.None);
            Assert.NotEmpty(next.Buckets);
            await client.DisconnectAsync();
        }
        finally
        {
            // Both paths above are rooted in this unique test-owned directory.
            Directory.Delete(directory, recursive: true);
        }
    }
}
