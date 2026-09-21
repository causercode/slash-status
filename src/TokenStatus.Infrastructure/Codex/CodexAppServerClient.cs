using System.Diagnostics;
using System.Reflection;
using TokenStatus.Core.Abstractions;
using TokenStatus.Core.Models;
using TokenStatus.Infrastructure.Cli;
using TokenStatus.Infrastructure.Logging;

namespace TokenStatus.Infrastructure.Codex;

public sealed class CodexAppServerClient : ICodexUsageClient
{
    private readonly string? _configuredPath;
    private readonly IRedactedLog? _log;
    private readonly string _clientVersion;
    private readonly object _gate = new();
    private readonly object _restartGate = new();
    private JsonLineRpcConnection? _connection;
    private int _restartFailures;
    private DateTimeOffset _retryAfterUtc;
    private int _disposed;

    public CodexAppServerClient(
        string? configuredPath = null,
        IRedactedLog? log = null,
        string? clientVersion = null)
    {
        _configuredPath = configuredPath;
        _log = log;
        _clientVersion = clientVersion ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";
    }

    public async Task<CodexAccountInfo> GetAccountAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync("account/read", new { refreshToken = false }, "account", cancellationToken).ConfigureAwait(false);
        try
        {
            return CodexResponseParser.ParseAccount(result);
        }
        catch (FormatException exception)
        {
            throw InvalidResponse("account", exception);
        }
    }

    public async Task<CodexRateLimits> GetRateLimitsAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync("account/rateLimits/read", new { }, "rate_limits", cancellationToken).ConfigureAwait(false);
        try
        {
            return CodexResponseParser.ParseRateLimits(result);
        }
        catch (FormatException exception)
        {
            throw InvalidResponse("rate_limits", exception);
        }
    }

    public async Task<CodexTokenUsage> GetTokenUsageAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync("account/usage/read", new { }, "token_usage", cancellationToken).ConfigureAwait(false);
        try
        {
            return CodexResponseParser.ParseTokenUsage(result);
        }
        catch (FormatException exception)
        {
            throw InvalidResponse("token_usage", exception);
        }
    }

    private static ProviderFailureException InvalidResponse(string operation, FormatException exception) =>
        new(
            ProviderHealth.Error,
            "Codex returned data in an unsupported format.",
            "codex_" + operation + "_response_invalid",
            exception);

    private async Task<System.Text.Json.JsonElement> RequestAsync(
        string method,
        object parameters,
        string operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var resolution = ExecutableResolver.ResolveCodexWithProvenance(_configuredPath);
        if (resolution is null)
        {
            throw new ProviderFailureException(
                ProviderHealth.NotInstalled,
                "Codex CLI was not found.",
                "codex_not_installed");
        }
        var executable = resolution.Path;

        await WaitForRestartBackoffAsync(cancellationToken).ConfigureAwait(false);

        JsonLineRpcConnection connection;
        lock (_gate)
        {
            _connection ??= new JsonLineRpcConnection(executable, _clientVersion, _log);
            connection = _connection;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await connection.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            lock (_restartGate)
            {
                _restartFailures = 0;
                _retryAfterUtc = DateTimeOffset.MinValue;
            }
            _log?.Information("codex", operation, "success", Stopwatch.GetElapsedTime(started));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonRpcException exception)
        {
            var failure = CreateProviderFailure(operation, exception);
            RegisterRestartFailure();
            _log?.Error("codex", operation, failure.DiagnosticCode, exception);
            await ResetConnectionAsync(connection).ConfigureAwait(false);
            throw failure;
        }
        catch (TimeoutException exception)
        {
            RegisterRestartFailure();
            _log?.Error("codex", operation, "request_timeout", exception);
            await ResetConnectionAsync(connection).ConfigureAwait(false);
            throw new ProviderFailureException(
                ProviderHealth.Stale,
                "Codex did not respond before the request timed out.",
                "codex_request_timeout",
                exception);
        }
        catch (InvalidDataException exception)
        {
            RegisterRestartFailure();
            _log?.Error("codex", operation, "response_too_large", exception);
            await ResetConnectionAsync(connection).ConfigureAwait(false);
            throw new ProviderFailureException(
                ProviderHealth.Error,
                "Codex returned a response that was too large.",
                "codex_response_too_large",
                exception);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            RegisterRestartFailure();
            _log?.Error("codex", operation, "process_failure", exception);
            await ResetConnectionAsync(connection).ConfigureAwait(false);
            throw new ProviderFailureException(
                ProviderHealth.Error,
                "Codex could not be queried.",
                "codex_process_failure",
                exception);
        }
        catch (FormatException exception)
        {
            _log?.Error("codex", operation, "response_invalid", exception);
            throw new ProviderFailureException(
                ProviderHealth.Error,
                "Codex returned data in an unsupported format.",
                "codex_response_invalid",
                exception);
        }
    }

    private static ProviderFailureException CreateProviderFailure(string operation, JsonRpcException exception)
    {
        var unauthenticated = exception.ErrorCode is 401 or 403 ||
            exception.Message.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("login", StringComparison.OrdinalIgnoreCase);
        return new ProviderFailureException(
            unauthenticated ? ProviderHealth.NotAuthenticated : ProviderHealth.Error,
            unauthenticated
                ? "Codex is not authenticated."
                : $"Codex failed while reading {operation.Replace('_', ' ')}.",
            unauthenticated ? "codex_not_authenticated" : "codex_rpc_error",
            exception);
    }

    private async Task ResetConnectionAsync(JsonLineRpcConnection connection)
    {
        var shouldDispose = false;
        lock (_gate)
        {
            if (ReferenceEquals(_connection, connection))
            {
                _connection = null;
                shouldDispose = true;
            }
        }

        if (shouldDispose)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WaitForRestartBackoffAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset retryAfter;
        lock (_restartGate)
        {
            retryAfter = _retryAfterUtc;
        }

        var remaining = retryAfter - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private void RegisterRestartFailure()
    {
        lock (_restartGate)
        {
            _restartFailures = Math.Min(_restartFailures + 1, 6);
            var seconds = Math.Min(30, Math.Pow(2, _restartFailures - 1));
            var jitter = Random.Shared.NextDouble() * seconds * 0.1;
            _retryAfterUtc = DateTimeOffset.UtcNow.AddSeconds(seconds + jitter);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        JsonLineRpcConnection? connection;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
