namespace TokenStatus.Infrastructure.Cli;

public static class ExecutableResolver
{
    public static string? ResolveCodex(string? configuredPath)
    {
        return Resolve(
            configuredPath,
            "codex.exe",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "OpenAI",
                "Codex",
                "bin",
                "codex.exe"));
    }

    public static string? ResolveOpenCode(string? configuredPath)
    {
        var configured = ResolveConfigured(configuredPath);
        if (configured is not null && !IsShellShim(configured))
        {
            return configured;
        }

        var pathExecutable = FindInPath("opencode.exe");
        if (pathExecutable is not null)
        {
            return pathExecutable;
        }

        var npmRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "node_modules",
            "opencode-ai",
            "bin",
            "opencode.exe");
        if (File.Exists(npmRoot))
        {
            return npmRoot;
        }

        var cmdShim = FindInPath("opencode.cmd");
        if (cmdShim is not null && File.Exists(npmRoot))
        {
            return npmRoot;
        }

        return null;
    }

    private static bool IsShellShim(string path)
    {
        return string.Equals(Path.GetExtension(path), ".cmd", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Path.GetExtension(path), ".ps1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Path.GetExtension(path), ".bat", StringComparison.OrdinalIgnoreCase);
    }

    public static string? Resolve(string? configuredPath, string pathName, string fallbackPath)
    {
        var configured = ResolveConfigured(configuredPath);
        if (configured is not null && !IsShellShim(configured))
        {
            return configured;
        }

        return FindInPath(pathName)
            ?? (File.Exists(fallbackPath) ? fallbackPath : null);
    }

    public static string? FindInPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception) when (directory.Length > 0)
            {
                // A malformed PATH entry must not prevent other entries from being tried.
            }
        }

        return null;
    }

    private static string? ResolveConfigured(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || !Path.IsPathFullyQualified(configuredPath))
        {
            return null;
        }

        try
        {
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        }
        catch (Exception) when (configuredPath.Length > 0)
        {
            return null;
        }
    }
}
