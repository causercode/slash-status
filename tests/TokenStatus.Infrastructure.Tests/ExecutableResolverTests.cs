using TokenStatus.Infrastructure.Cli;

namespace TokenStatus.Infrastructure.Tests;

public sealed class ExecutableResolverTests
{
    [Fact]
    public void ConfiguredExecutableWinsAndReportsConfiguredProvenance()
    {
        using var directory = new TemporaryDirectory();
        var configured = directory.CreateFile("configured.exe");
        var known = directory.CreateFile("known.exe");
        var path = directory.CreateFile("path.exe");

        var result = ExecutableResolver.ResolveCodexWithProvenance(configured, known, path);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(configured), result.Path);
        Assert.Equal(ExecutableProvenance.Configured, result.Provenance);
    }

    [Fact]
    public void KnownOpenAiInstallationWinsOverPathFallback()
    {
        using var directory = new TemporaryDirectory();
        var known = directory.CreateFile("known.exe");
        var path = directory.CreateFile("path.exe");

        var result = ExecutableResolver.ResolveCodexWithProvenance(null, known, path);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(known), result.Path);
        Assert.Equal(ExecutableProvenance.KnownInstall, result.Provenance);
    }

    [Fact]
    public void PathIsUsedOnlyWhenHigherPriorityCandidatesAreAbsent()
    {
        using var directory = new TemporaryDirectory();
        var expected = directory.CreateFile("codex.exe");

        var result = ExecutableResolver.ResolveCodexWithProvenance(null, directory.Combine("missing.exe"), directory.FullPath);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(expected), result.Path);
        Assert.Equal(ExecutableProvenance.Path, result.Provenance);
    }

    [Fact]
    public void RelativePathsAndShellShimsAreRejected()
    {
        using var directory = new TemporaryDirectory();
        var shim = directory.CreateFile("codex.cmd");

        Assert.Null(ExecutableResolver.ResolveCodexWithProvenance("codex.exe", directory.Combine("missing.exe"), string.Empty));
        Assert.Null(ExecutableResolver.ResolveCodexWithProvenance(shim, directory.Combine("missing.exe"), string.Empty));
    }

    [Fact]
    public void MalformedPathEntriesDoNotPreventLaterValidEntries()
    {
        using var directory = new TemporaryDirectory();
        var valid = directory.CreateFile("codex.exe");
        var path = "\0" + Path.PathSeparator + directory.FullPath;

        var result = ExecutableResolver.ResolveCodexWithProvenance(null, directory.Combine("missing.exe"), path);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(valid), result.Path);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            FullPath = Directory.CreateTempSubdirectory("tokenstatus-resolver-").FullName;
        }

        public string FullPath { get; }

        public string Combine(string name) => Path.Combine(FullPath, name);

        public string CreateFile(string name)
        {
            var path = Combine(name);
            File.WriteAllText(path, string.Empty);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(FullPath, recursive: true);
        }
    }
}
