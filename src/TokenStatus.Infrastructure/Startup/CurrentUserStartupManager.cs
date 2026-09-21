using Microsoft.Win32;
using System.Runtime.Versioning;

namespace TokenStatus.Infrastructure.Startup;

[SupportedOSPlatform("windows")]
public sealed class CurrentUserStartupManager : IStartupManager
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    public const string ValueName = "TokenStatus";

    public bool IsEnabled(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(ValueName) as string;
        return string.Equals(value, QuotePath(executablePath), StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(string executablePath, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("The current-user startup registry key could not be opened.");

        if (enabled)
        {
            key.SetValue(ValueName, QuotePath(Path.GetFullPath(executablePath)), RegistryValueKind.String);
        }
        else
        {
            var existing = key.GetValue(ValueName) as string;
            if (string.Equals(
                existing,
                QuotePath(executablePath),
                StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }

    public static string QuotePath(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath).Replace("\"", "");
        return "\"" + fullPath + "\"";
    }
}
