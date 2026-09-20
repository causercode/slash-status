using System.Diagnostics;
using TokenStatus.Core.Abstractions;
using TokenStatus.Core.Models;
using TokenStatus.Infrastructure.Cli;
using TokenStatus.Infrastructure.Logging;

namespace TokenStatus.Infrastructure.OpenCode;

public sealed class OpenCodeUsageClient : IOpenCodeUsageClient, IDisposable
{
    public const string GoUsageDashboardUrl = "https://opencode.ai/workspace";
    public const string SevenDayAggregateQuery = """
        SELECT
          COUNT(*) AS sessions,
          ROUND(COALESCE(SUM(cost), 0), 6) AS total_cost,
          COALESCE(SUM(tokens_input), 0) AS input_tokens,
          COALESCE(SUM(tokens_output), 0) AS output_tokens,
          COALESCE(SUM(tokens_reasoning), 0) AS reasoning_tokens,
          COALESCE(SUM(tokens_cache_read), 0) AS cache_read_tokens,
          COALESCE(SUM(tokens_cache_write), 0) AS cache_write_tokens
        FROM session
        WHERE time_created >=
          (CAST(strftime('%s', 'now', '-7 days') AS INTEGER) * 1000)
        """;

    private readonly string? _configuredPath;
    private readonly ProcessRunner _processRunner;
    private readonly IRedactedLog? _log;
    private readonly object _probeGate = new();
    private bool _versionProbed;
    private int _disposed;

    public OpenCodeUsageClient(
        string? configuredPath = null,
        ProcessRunner? processRunner = null,
        IRedactedLog? log = null)
    {
        _configuredPath = configuredPath;
        _processRunner = processRunner ?? new ProcessRunner();
        _log = log;
    }

    public async Task<OpenCodeLocalUsage> GetSevenDayUsageAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var executable = ExecutableResolver.ResolveOpenCode(_configuredPath);
        if (executable is null)
        {
            throw new ProviderFailureException(
                ProviderHealth.NotInstalled,
                "OpenCode CLI was not found.",
                "opencode_not_installed");
        }

        await ProbeVersionAsync(executable, cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        var result = await _processRunner.RunAsync(
            executable,
            ["db", SevenDayAggregateQuery, "--format", "json"],
            TimeSpan.FromSeconds(15),
            cancellationToken,
            ProcessRunner.DefaultMaximumStandardOutputCharacters,
            ProcessRunner.DefaultMaximumStandardErrorCharacters).ConfigureAwait(false);

        if (result.TimedOut)
        {
            _log?.Error("opencode", "usage", "opencode_query_timeout", excerpt: result.StandardError);
            throw new ProviderFailureException(
                ProviderHealth.Stale,
                "OpenCode did not finish the local usage query in time.",
                "opencode_query_timeout");
        }

        if (result.ExitCode != 0)
        {
            var unsupported = result.StandardError.Contains("no such table", StringComparison.OrdinalIgnoreCase) ||
                              result.StandardError.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ||
                              result.StandardError.Contains("not found", StringComparison.OrdinalIgnoreCase);
            var code = unsupported ? "opencode_schema_unsupported" : "opencode_query_failed";
            _log?.Error("opencode", "usage", code, excerpt: result.StandardError);
            throw new ProviderFailureException(
                unsupported ? ProviderHealth.Unsupported : ProviderHealth.Error,
                unsupported
                    ? "OpenCode version not currently supported."
                    : "OpenCode local usage could not be read.",
                code);
        }

        try
        {
            var usage = OpenCodeUsageParser.Parse(result.StandardOutput);
            _log?.Information("opencode", "usage", "success", Stopwatch.GetElapsedTime(started));
            return usage;
        }
        catch (FormatException exception)
        {
            _log?.Error("opencode", "usage", "opencode_response_invalid", exception);
            throw new ProviderFailureException(
                ProviderHealth.Unsupported,
                "OpenCode version not currently supported.",
                "opencode_response_invalid",
                exception);
        }
    }

    private async Task ProbeVersionAsync(string executable, CancellationToken cancellationToken)
    {
        lock (_probeGate)
        {
            if (_versionProbed)
            {
                return;
            }
        }

        var result = await _processRunner.RunAsync(
            executable,
            ["--version"],
            TimeSpan.FromSeconds(5),
            cancellationToken,
            maximumStandardOutputCharacters: 16 * 1024,
            maximumStandardErrorCharacters: ProcessRunner.DefaultMaximumStandardErrorCharacters).ConfigureAwait(false);
        if (result.TimedOut)
        {
            _log?.Error("opencode", "version", "opencode_version_timeout", excerpt: result.StandardError);
            throw new ProviderFailureException(
                ProviderHealth.Unsupported,
                "OpenCode version could not be determined.",
                "opencode_version_timeout");
        }

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            _log?.Error("opencode", "version", "opencode_version_failed", excerpt: result.StandardError);
            throw new ProviderFailureException(
                ProviderHealth.Unsupported,
                "OpenCode version not currently supported.",
                "opencode_version_failed");
        }

        lock (_probeGate)
        {
            _versionProbed = true;
        }

        _log?.Information("opencode", "version", "success", TimeSpan.Zero, "version_probe");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _processRunner.Dispose();
        }
    }
}
