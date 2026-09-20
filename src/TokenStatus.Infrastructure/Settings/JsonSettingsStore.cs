using System.Text.Json;
using System.Text.Json.Serialization;
using TokenStatus.Core.Abstractions;
using TokenStatus.Core.Models;
using TokenStatus.Infrastructure.Logging;

namespace TokenStatus.Infrastructure.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IRedactedLog? _log;

    public JsonSettingsStore(IRedactedLog? log = null, string? localApplicationData = null)
    {
        _log = log;
        var root = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        SettingsPath = Path.Combine(root, "TokenStatus", "settings.json");
    }

    public string SettingsPath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            await using var stream = File.OpenRead(SettingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            return (settings ?? new AppSettings()).Normalize();
        }
        catch (JsonException exception)
        {
            PreserveCorruptSettings();
            _log?.Error("settings", "load", "settings_invalid_json", exception);
            return new AppSettings();
        }
        catch (IOException exception)
        {
            _log?.Error("settings", "load", "settings_io_error", exception);
            return new AppSettings();
        }
        catch (UnauthorizedAccessException exception)
        {
            _log?.Error("settings", "load", "settings_access_denied", exception);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("The settings directory could not be determined.");
        Directory.CreateDirectory(directory);

        var temporaryPath = SettingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void PreserveCorruptSettings()
    {
        if (!File.Exists(SettingsPath))
        {
            return;
        }

        var suffix = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var destination = SettingsPath + ".corrupt-" + suffix;
        try
        {
            File.Move(SettingsPath, destination, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Error("settings", "preserve_corrupt", "settings_preserve_failed", exception);
        }
    }
}
