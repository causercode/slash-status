using System.Runtime.InteropServices;

namespace TokenStatus.Infrastructure.Cli;

public enum ExecutableProvenance
{
    Configured,
    KnownInstall,
    Path,
    NpmInstall
}

public sealed record ExecutableResolution(string Path, ExecutableProvenance Provenance)
{
    public string ProvenanceLabel => Provenance switch
    {
        ExecutableProvenance.Configured => "Configured path",
        ExecutableProvenance.KnownInstall => "Known OpenAI installation",
        ExecutableProvenance.Path => "PATH fallback",
        ExecutableProvenance.NpmInstall => "npm installation",
        _ => Provenance.ToString()
    };
}

public static class ExecutableResolver
{
    public static string? ResolveCodex(string? configuredPath)
    {
        return ResolveCodexWithProvenance(configuredPath)?.Path;
    }

    public static ExecutableResolution? ResolveCodexWithProvenance(
        string? configuredPath,
        string? knownInstallPath = null,
        string? pathValue = null,
        Architecture? architecture = null)
    {
        var resolution = ResolveWithProvenance(
            configuredPath,
            "codex.exe",
            knownInstallPath ?? GetKnownCodexInstallPath(),
            pathValue);
        if (resolution is not null)
        {
            return resolution;
        }

        var npmInstall = FindNpmCodexInPath(
            pathValue,
            architecture ?? RuntimeInformation.OSArchitecture);
        return npmInstall is null
            ? null
            : new ExecutableResolution(npmInstall, ExecutableProvenance.NpmInstall);
    }

    private static bool IsShellShim(string path)
    {
        try
        {
            return string.Equals(Path.GetExtension(path), ".cmd", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Path.GetExtension(path), ".ps1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Path.GetExtension(path), ".bat", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (IsPathResolutionException(exception))
        {
            return false;
        }
    }

    public static string? Resolve(string? configuredPath, string pathName, string fallbackPath)
    {
        return ResolveWithProvenance(configuredPath, pathName, fallbackPath)?.Path;
    }

    public static ExecutableResolution? ResolveWithProvenance(
        string? configuredPath,
        string pathName,
        string fallbackPath,
        string? pathValue = null)
    {
        var configured = ResolveConfigured(configuredPath);
        if (configured is not null)
        {
            return new ExecutableResolution(configured, ExecutableProvenance.Configured);
        }

        var knownInstall = ResolveExecutableCandidate(fallbackPath);
        if (knownInstall is not null)
        {
            return new ExecutableResolution(knownInstall, ExecutableProvenance.KnownInstall);
        }

        var path = FindInPath(pathName, pathValue);
        return path is null ? null : new ExecutableResolution(path, ExecutableProvenance.Path);
    }

    public static string? FindInPath(string fileName, string? pathValue = null)
    {
        var path = pathValue ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                var resolved = ResolveExecutableCandidate(candidate);
                if (resolved is not null)
                {
                    return resolved;
                }
            }
            catch (Exception exception) when (IsPathResolutionException(exception))
            {
                // A malformed PATH entry must not prevent other entries from being tried.
            }
        }

        return null;
    }

    private static string? ResolveConfigured(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || IsShellShim(configuredPath))
        {
            return null;
        }

        return ResolveExecutableCandidate(configuredPath);
    }

    private static string? ResolveExecutableCandidate(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !Path.IsPathFullyQualified(path) ||
                !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) && !Directory.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (IsPathResolutionException(exception))
        {
            return null;
        }
    }

    private static string? FindNpmCodexInPath(string? pathValue, Architecture architecture)
    {
        var npmLayout = architecture switch
        {
            Architecture.X64 => (Package: "codex-win32-x64", Target: "x86_64-pc-windows-msvc"),
            Architecture.Arm64 => (Package: "codex-win32-arm64", Target: "aarch64-pc-windows-msvc"),
            _ => ((string Package, string Target)?)null
        };
        if (npmLayout is null)
        {
            return null;
        }

        var path = pathValue ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var shimDirectory = directory.Trim();
                if (!HasNpmCodexShim(shimDirectory))
                {
                    continue;
                }

                var packageDirectory = Path.Combine(
                    shimDirectory,
                    "node_modules",
                    "@openai",
                    npmLayout.Value.Package);
                var nestedPackageDirectory = Path.Combine(
                    shimDirectory,
                    "node_modules",
                    "@openai",
                    "codex",
                    "node_modules",
                    "@openai",
                    npmLayout.Value.Package);

                foreach (var candidatePackageDirectory in new[] { nestedPackageDirectory, packageDirectory })
                {
                    var candidate = Path.Combine(
                        candidatePackageDirectory,
                        "vendor",
                        npmLayout.Value.Target,
                        "bin",
                        "codex.exe");
                    var resolved = ResolveExecutableCandidate(candidate);
                    if (resolved is not null)
                    {
                        return resolved;
                    }
                }
            }
            catch (Exception exception) when (IsPathResolutionException(exception))
            {
                // A malformed PATH entry must not prevent other npm installations from being tried.
            }
        }

        return null;
    }

    private static bool HasNpmCodexShim(string directory)
    {
        foreach (var shimName in new[] { "codex.cmd", "codex.ps1" })
        {
            var shimPath = Path.Combine(directory, shimName);
            if (File.Exists(shimPath) && !Directory.Exists(shimPath))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetKnownCodexInstallPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "OpenAI",
        "Codex",
        "bin",
        "codex.exe");

    private static bool IsPathResolutionException(Exception exception) =>
        exception is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException;
}
