using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TokenStatus.Core.Abstractions;
using TokenStatus.Core.Models;
using TokenStatus.Infrastructure.Logging;

namespace TokenStatus.Infrastructure.OpenCode;

public sealed class OpenCodeGoQuotaClient : IOpenCodeGoQuotaClient, IDisposable
{
    public const string UsageEndpoint = "https://opencode.ai/zen/go/v1/usage";
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly IOpenCodeGoCredentialStore _credentials;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IRedactedLog? _log;
    private int _disposed;

    public OpenCodeGoQuotaClient(
        IOpenCodeGoCredentialStore credentials,
        HttpClient? httpClient = null,
        IRedactedLog? log = null)
    {
        _credentials = credentials;
        _log = log;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? CreateHttpClient();
    }

    public Task<OpenCodeGoQuota> GetQuotaAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var apiKey = _credentials.ReadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ProviderFailureException(
                ProviderHealth.NotConfigured,
                "Add an OpenCode Go API key in Settings to show subscription limits.",
                "opencode_go_not_configured");
        }

        return GetQuotaAsync(apiKey, cancellationToken);
    }

    public async Task<OpenCodeGoQuota> GetQuotaAsync(string apiKey, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var normalizedKey = apiKey.Trim();
        if (normalizedKey.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("The OpenCode Go API key cannot contain whitespace.", nameof(apiKey));
        }

        var started = Stopwatch.GetTimestamp();
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalizedKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ProviderFailureException(
                ProviderHealth.NotAuthenticated,
                "The OpenCode Go API key is invalid or expired.",
                "opencode_go_unauthorized");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ProviderFailureException(
                ProviderHealth.Unsupported,
                "An active OpenCode Go subscription is required.",
                "opencode_go_subscription_required");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new ProviderFailureException(
                ProviderHealth.Stale,
                "OpenCode temporarily rate-limited the quota request.",
                "opencode_go_request_rate_limited");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OpenCode Go usage returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var bytes = await ReadBoundedResponseAsync(response.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            var quota = OpenCodeGoQuotaParser.Parse(bytes);
            _log?.Information("opencode_go", "quota", "success", Stopwatch.GetElapsedTime(started));
            return quota;
        }
        catch (FormatException exception)
        {
            _log?.Error("opencode_go", "quota", "opencode_go_response_invalid", exception);
            throw new ProviderFailureException(
                ProviderHealth.Unsupported,
                "OpenCode returned an unsupported quota response.",
                "opencode_go_response_invalid",
                exception);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private static async Task<byte[]> ReadBoundedResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new FormatException("OpenCode returned too much quota data.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new FormatException("OpenCode returned too much quota data.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}

public static class OpenCodeGoQuotaParser
{
    public static OpenCodeGoQuota Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 8,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var usage = RequiredObject(document.RootElement, "usage");
            return new OpenCodeGoQuota(
                ParseWindow(usage, "rolling"),
                ParseWindow(usage, "weekly"),
                ParseWindow(usage, "monthly"));
        }
        catch (JsonException exception)
        {
            throw new FormatException("OpenCode quota output is not valid JSON.", exception);
        }
    }

    private static OpenCodeQuotaWindow ParseWindow(JsonElement usage, string name)
    {
        var window = RequiredObject(usage, name);
        if (!window.TryGetProperty("percent", out var percentElement) ||
            !percentElement.TryGetDecimal(out var percent) ||
            percent < 0)
        {
            throw new FormatException($"OpenCode quota window {name} has an invalid percent.");
        }

        if (!window.TryGetProperty("resetsAt", out var resetElement) ||
            resetElement.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(resetElement.GetString(), out var resetsAt))
        {
            throw new FormatException($"OpenCode quota window {name} has an invalid reset time.");
        }

        if (!window.TryGetProperty("status", out var statusElement) || statusElement.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"OpenCode quota window {name} has an invalid status.");
        }

        var status = statusElement.GetString();
        if (status is not ("ok" or "rate-limited"))
        {
            throw new FormatException($"OpenCode quota window {name} has an unsupported status.");
        }

        return new OpenCodeQuotaWindow(percent, resetsAt, status == "rate-limited");
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"OpenCode quota output is missing object {name}.");
        }

        return value;
    }
}
