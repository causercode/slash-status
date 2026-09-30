using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TokenStatus.Core.Abstractions;
using TokenStatus.Core.Models;
using TokenStatus.Infrastructure.Cli;
using TokenStatus.Infrastructure.Codex;

namespace TokenStatus.Infrastructure.Claude;

public sealed class ClaudeCodeQuotaClient(string? configuredPath = null, TimeSpan? requestTimeout = null)
    : IClaudeCodeQuotaClient
{
    public async Task<ClaudeCodeQuota> GetQuotaAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = ExecutableResolver.Resolve(configuredPath, "claude.exe", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"));
        if (executable is null)
        {
            throw new ProviderFailureException(ProviderHealth.NotInstalled,
                "Install Claude Code, or choose its executable in Settings.", "claude_not_installed");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = CreateStartInfo(executable) };
        using var drainCancellation = new CancellationTokenSource();
        Task stderr = Task.CompletedTask;
        try
        {
            process.Start();
            stderr = DrainStderrAsync(process.StandardError, drainCancellation.Token);
            var lines = new BoundedAsyncLineReader(process.StandardOutput, 4 * 1024 * 1024);
            await RequestAsync(process, lines, "initialize", new { subtype = "initialize" }, timeout.Token)
                .ConfigureAwait(false);
            var response = await RequestAsync(process, lines, "usage",
                new { subtype = "get_usage", skip_behaviors = true }, timeout.Token).ConfigureAwait(false);
            return Parse(response, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new ProviderFailureException(ProviderHealth.Stale,
                "Claude Code usage check timed out. Try Refresh again.", "claude_timeout", exception);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidDataException)
        {
            throw new ProviderFailureException(ProviderHealth.Error,
                "Claude Code returned an unsupported usage format. Check for a CLI update.",
                "claude_usage_invalid", exception);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new ProviderFailureException(ProviderHealth.Error,
                "Claude Code could not report usage. Check its installation and sign in with claude auth login.",
                "claude_query_failed", exception);
        }
        finally
        {
            StopProcess(process);
            drainCancellation.Cancel();
            await stderr.ConfigureAwait(false);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executable)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath()
        };
        // Only control requests are sent: no prompt, tools, hooks, project config, or saved session.
        foreach (var argument in new[]
        {
            "--print", "--input-format", "stream-json", "--output-format", "stream-json",
            "--verbose", "--no-session-persistence", "--setting-sources", "",
            "--settings", "{\"disableAllHooks\":true}", "--tools", "", "--disable-slash-commands",
            "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--no-chrome"
        })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false";
        start.Environment["CLAUDE_CODE_AUTO_CONNECT_IDE"] = "0";
        start.Environment["CLAUDE_CODE_IDE_SKIP_AUTO_INSTALL"] = "1";
        start.Environment["CLAUDE_CODE_ENTRYPOINT"] = "sdk-ts";
        start.Environment.Remove("FORCE_CODE_TERMINAL");
        return start;
    }

    private static async Task<JsonElement> RequestAsync(
        Process process, BoundedAsyncLineReader lines, string id, object request, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(
            JsonSerializer.Serialize(new { type = "control_request", request_id = id, request }).AsMemory(), token)
            .ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
        while (true)
        {
            var line = await lines.ReadBoundedLineAsync(token).ConfigureAwait(false);
            if (line is null) throw new IOException("Claude CLI closed before replying.");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String)
                continue;
            if (type.GetString() == "control_request")
            {
                // Fail closed if a future CLI attempts to request permissions or run a hook.
                throw new ProviderFailureException(ProviderHealth.Unsupported,
                    "This Claude Code version cannot perform a usage-only check.", "claude_control_unsupported");
            }
            if (type.GetString() != "control_response" ||
                !root.TryGetProperty("response", out var response) ||
                response.ValueKind != JsonValueKind.Object ||
                !response.TryGetProperty("request_id", out var requestId) ||
                requestId.ValueKind != JsonValueKind.String || requestId.GetString() != id)
                continue;
            if (!response.TryGetProperty("subtype", out var subtype) ||
                subtype.ValueKind != JsonValueKind.String || subtype.GetString() != "success")
            {
                throw new ProviderFailureException(ProviderHealth.Unsupported,
                    "Claude Code could not query subscription usage. Sign in with claude auth login or update the CLI.",
                    "claude_usage_unsupported");
            }
            if (!response.TryGetProperty("response", out var result))
                throw new FormatException("Claude response payload is missing.");
            return result.Clone();
        }
    }

    public static ClaudeCodeQuota Parse(JsonElement root, DateTimeOffset observedAt)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("rate_limits_available", out var available) ||
            available.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException("Claude usage availability is missing.");
        if (!available.GetBoolean())
            throw new ProviderFailureException(ProviderHealth.NotAuthenticated,
                "Sign in to a Claude subscription with claude auth login. API-key sessions do not provide subscription quota.",
                "claude_subscription_required");
        if (!root.TryGetProperty("rate_limits", out var limits) || limits.ValueKind == JsonValueKind.Null)
            throw new ProviderFailureException(ProviderHealth.Stale,
                "Claude subscription usage is temporarily unavailable. Try Refresh again.", "claude_usage_unavailable");
        if (limits.ValueKind != JsonValueKind.Object) throw new FormatException("Claude rate limits are invalid.");
        var fiveHour = ParseWindow(limits, "five_hour", TimeSpan.FromHours(5));
        var sevenDay = ParseWindow(limits, "seven_day", TimeSpan.FromDays(7));
        if (fiveHour is null && sevenDay is null)
            throw new FormatException("Claude subscription windows are missing.");
        return new ClaudeCodeQuota(observedAt, fiveHour, sevenDay);
    }

    private static RateLimitWindow? ParseWindow(JsonElement limits, string name, TimeSpan duration)
    {
        if (!limits.TryGetProperty(name, out var window) || window.ValueKind == JsonValueKind.Null)
            return null;
        if (window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("utilization", out var used) ||
            used.ValueKind != JsonValueKind.Number || !used.TryGetDecimal(out var percent) || percent < 0)
            throw new FormatException("Claude usage percentage is invalid.");
        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resets_at", out var reset) && reset.ValueKind != JsonValueKind.Null)
        {
            if (reset.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var timestamp))
                throw new FormatException("Claude reset time is invalid.");
            resetsAt = timestamp;
        }
        return new RateLimitWindow((int)Math.Ceiling(Math.Min(100, percent)), duration, resetsAt);
    }

    private static async Task DrainStderrAsync(StreamReader reader, CancellationToken token)
    {
        try
        {
            var buffer = new char[4096];
            while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) != 0) { }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (process.HasExited) return;
            try { process.StandardInput.Close(); }
            catch (IOException) { }
            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            // A process which failed to start or already exited needs no cleanup.
        }
    }
}
