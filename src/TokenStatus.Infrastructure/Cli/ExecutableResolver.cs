namespace TokenStatus.Infrastructure.Cli;

public enum ExecutableProvenance
{
    Configured,
    KnownInstall,
    Path
}

public sealed record ExecutableResolution(string Path, ExecutableProvenance Provenance)
{
    public string ProvenanceLabel => Provenance switch
    {
        ExecutableProvenance.Configured => "Configured path",
        ExecutableProvenance.KnownInstall => "Known OpenAI installation",
        ExecutableProvenance.Path => "PATH fallback",
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
        string? pathValue = null)
    {
        return ResolveWithProvenance(
            configuredPath,
            "codex.exe",
            knownInstallPath ?? GetKnownCodexInstallPath(),
            pathValue);
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
