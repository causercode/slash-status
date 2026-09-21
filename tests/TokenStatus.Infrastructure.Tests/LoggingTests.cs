using TokenStatus.Infrastructure.Logging;

namespace TokenStatus.Infrastructure.Tests;

public sealed class LoggingTests
{
    [Fact]
    public void FactoryFallsBackWhenTheLogDirectoryCannotBeCreated()
    {
        using var directory = new TemporaryDirectory();
        var blockingFile = directory.Combine("not-a-directory");
        File.WriteAllText(blockingFile, "blocked");

        using var log = RedactedLogFactory.Create(blockingFile);

        Assert.IsType<NullRedactedLog>(log);
        log.Information("provider", "operation", "success", TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void AppendAndRotationFailuresAreBestEffortAndDisableFurtherWrites()
    {
        using var directory = new TemporaryDirectory();
        using var log = new RedactingFileLog(directory.FullPath);
        var filePath = Path.Combine(log.LogDirectory, "token-status.log");
        Directory.CreateDirectory(filePath);

        var exception = Record.Exception(() =>
        {
            log.Information("provider", "operation", "first", TimeSpan.Zero);
            log.Error("provider", "operation", "second");
        });

        Assert.Null(exception);
    }

    [Fact]
    public void HealthyLoggingRedactsSecretsAndRotatesTheActiveFile()
    {
        using var directory = new TemporaryDirectory();
        using var log = new RedactingFileLog(directory.FullPath, retainedFiles: 2);
        var filePath = Path.Combine(log.LogDirectory, "token-status.log");
        log.Error(
            "provider",
            "operation",
            "failed",
            excerpt: "Bearer bearer-value token=token-value api_key=api-value user@example.com org_id=org-value");

        var contents = File.ReadAllText(filePath);
        Assert.DoesNotContain("bearer-value", contents);
        Assert.DoesNotContain("token-value", contents);
        Assert.DoesNotContain("api-value", contents);
        Assert.DoesNotContain("user@example.com", contents);
        Assert.DoesNotContain("org-value", contents);

        File.WriteAllText(filePath, new string('x', 1024 * 1024));
        log.Information("provider", "operation", "rotation", TimeSpan.Zero);

        Assert.True(File.Exists(filePath + ".1"));
        Assert.Contains("rotation", File.ReadAllText(filePath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            FullPath = Directory.CreateTempSubdirectory("tokenstatus-log-").FullName;
        }

        public string FullPath { get; }

        public string Combine(string name) => Path.Combine(FullPath, name);

        public void Dispose()
        {
            Directory.Delete(FullPath, recursive: true);
        }
    }
}
