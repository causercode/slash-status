using System.Runtime.InteropServices;
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

    [Theory]
    [InlineData(Architecture.X64, true)]
    [InlineData(Architecture.X64, false)]
    [InlineData(Architecture.Arm64, true)]
    [InlineData(Architecture.Arm64, false)]
    public void NpmInstallationIsResolvedForSupportedArchitectures(Architecture architecture, bool nested)
    {
        using var directory = new TemporaryDirectory();
        directory.CreateFile("codex.cmd");
        var expected = directory.CreateFile(GetNpmExecutablePath(architecture, nested));

        var result = ExecutableResolver.ResolveCodexWithProvenance(
            null,
            directory.Combine("missing.exe"),
            directory.FullPath,
            architecture);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(expected), result.Path);
        Assert.Equal(ExecutableProvenance.NpmInstall, result.Provenance);
        Assert.Equal("npm installation", result.ProvenanceLabel);
    }

    [Fact]
    public void PowerShellShimCanAnchorNpmDiscovery()
    {
        using var directory = new TemporaryDirectory();
        directory.CreateFile("codex.ps1");
        var expected = directory.CreateFile(GetNpmExecutablePath(Architecture.X64, nested: true));

        var result = ExecutableResolver.ResolveCodexWithProvenance(
            null,
            directory.Combine("missing.exe"),
            directory.FullPath,
            Architecture.X64);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(expected), result.Path);
    }

    [Fact]
    public void NpmShimIsNeverReturnedOrUsedWithoutExpectedNativeExecutable()
    {
        using var directory = new TemporaryDirectory();
        var shim = directory.CreateFile("codex.cmd");
        directory.CreateFile(Path.Combine("node_modules", "@openai", "codex.exe"));

        var result = ExecutableResolver.ResolveCodexWithProvenance(
            null,
            directory.Combine("missing.exe"),
            directory.FullPath,
            Architecture.X64);

        Assert.Null(result);
        Assert.True(File.Exists(shim));
    }

    [Fact]
    public void PathExecutableWinsOverNpmFallback()
    {
        using var directory = new TemporaryDirectory();
        var expected = directory.CreateFile("codex.exe");
        directory.CreateFile("codex.cmd");
        directory.CreateFile(GetNpmExecutablePath(Architecture.X64, nested: true));

        var result = ExecutableResolver.ResolveCodexWithProvenance(
            null,
            directory.Combine("missing.exe"),
            directory.FullPath,
            Architecture.X64);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(expected), result.Path);
        Assert.Equal(ExecutableProvenance.Path, result.Provenance);
    }

    private static string GetNpmExecutablePath(Architecture architecture, bool nested)
    {
        var (package, target) = architecture switch
        {
            Architecture.X64 => ("codex-win32-x64", "x86_64-pc-windows-msvc"),
            Architecture.Arm64 => ("codex-win32-arm64", "aarch64-pc-windows-msvc"),
            _ => throw new ArgumentOutOfRangeException(nameof(architecture))
        };
        var packageRoot = nested
            ? Path.Combine("node_modules", "@openai", "codex", "node_modules", "@openai", package)
            : Path.Combine("node_modules", "@openai", package);
        return Path.Combine(packageRoot, "vendor", target, "bin", "codex.exe");
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
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(FullPath, recursive: true);
        }
    }
}
