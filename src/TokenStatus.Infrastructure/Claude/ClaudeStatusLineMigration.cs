using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenStatus.Infrastructure.Claude;

public static class ClaudeStatusLineMigration
{
    public static void RestorePrevious(string? configDirectory = null, string? dataDirectory = null)
    {
        var configuredDirectory = configDirectory ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var config = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : Path.GetFullPath(configuredDirectory);
        var data = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenStatus", "claude");
        var settingsPath = Path.Combine(config, "settings.json");
        var previousPath = Path.Combine(data, "previous-statusline.json");
        if (!File.Exists(settingsPath) || !File.Exists(previousPath)) return;
        var original = File.ReadAllText(settingsPath);
        var settings = JsonNode.Parse(original, documentOptions: new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        }) as JsonObject ?? throw new FormatException("Claude settings must be a JSON object.");
        var scriptPath = Path.Combine(data, "statusline.ps1");
        var invocation = "& '" + scriptPath.Replace("'", "''") + "'";
        var command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(invocation));
        // Restore only our own bridge, preserving any later user customization.
        if (settings["statusLine"] is not JsonObject status ||
            status["command"] is not JsonValue value ||
            !value.TryGetValue<string>(out var currentCommand) || currentCommand != command) return;
        var previous = JsonNode.Parse(File.ReadAllText(previousPath));
        if (previous is null) settings.Remove("statusLine");
        else settings["statusLine"] = previous;
        var temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, settings.ToJsonString(new() { WriteIndented = true }));
            if (File.ReadAllText(settingsPath) != original)
                throw new IOException("Claude settings changed during bridge removal.");
            File.Move(temporary, settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        foreach (var file in new[] { scriptPath, previousPath, Path.Combine(data, "usage.json") })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
