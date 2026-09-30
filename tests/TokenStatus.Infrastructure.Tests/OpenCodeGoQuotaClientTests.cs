using System.Net;
using System.Text;
using TokenStatus.Core.Abstractions;
using TokenStatus.Core.Models;
using TokenStatus.Infrastructure.OpenCode;

namespace TokenStatus.Infrastructure.Tests;

public sealed class OpenCodeGoQuotaClientTests
{
    private const string ValidResponse = """
        {
          "usage": {
            "rolling": { "status": "ok", "percent": 23.4, "resetsAt": "2026-09-20T21:30:00.000Z" },
            "weekly": { "status": "ok", "percent": 41.2, "resetsAt": "2026-09-23T00:00:00.000Z" },
            "monthly": { "status": "rate-limited", "percent": 100, "resetsAt": "2026-10-05T00:00:00.000Z" }
          }
        }
        """;

    [Fact]
    public async Task SendsApiKeyOnlyToFixedUsageEndpointAndParsesQuota()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidResponse, Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);
        using var client = new OpenCodeGoQuotaClient(new FakeCredentials("secret-key"), httpClient);

        var quota = await client.GetQuotaAsync(CancellationToken.None);

        Assert.Equal(new Uri(OpenCodeGoQuotaClient.UsageEndpoint), captured!.RequestUri);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("secret-key", captured.Headers.Authorization.Parameter);
        Assert.Equal(23.4m, quota.Rolling.UsedPercent);
        Assert.Equal(77, quota.Rolling.RemainingPercent);
        Assert.True(quota.Monthly.IsRateLimited);
    }

    [Fact]
    public async Task MissingCredentialIsReportedAsNotConfiguredWithoutHttpRequest()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("HTTP should not be called"));
        using var httpClient = new HttpClient(handler);
        using var client = new OpenCodeGoQuotaClient(new FakeCredentials(null), httpClient);

        var exception = await Assert.ThrowsAsync<ProviderFailureException>(
            () => client.GetQuotaAsync(CancellationToken.None));

        Assert.Equal(ProviderHealth.NotConfigured, exception.Health);
        Assert.Equal("opencode_go_not_configured", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderHealth.NotAuthenticated, "opencode_go_unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, ProviderHealth.Unsupported, "opencode_go_subscription_required")]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderHealth.Stale, "opencode_go_request_rate_limited")]
    public async Task MapsKnownHttpFailures(HttpStatusCode status, ProviderHealth expectedHealth, string expectedCode)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));
        using var httpClient = new HttpClient(handler);
        using var client = new OpenCodeGoQuotaClient(new FakeCredentials("secret-key"), httpClient);

        var exception = await Assert.ThrowsAsync<ProviderFailureException>(
            () => client.GetQuotaAsync(CancellationToken.None));

        Assert.Equal(expectedHealth, exception.Health);
        Assert.Equal(expectedCode, exception.DiagnosticCode);
    }

    [Fact]
    public async Task TimeoutCoversResponseBodyAfterHeadersArrive()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new HangingStream())
        });
        using var httpClient = new HttpClient(handler);
        using var client = new OpenCodeGoQuotaClient(new FakeCredentials("secret-key"), httpClient,
            requestTimeout: TimeSpan.FromMilliseconds(50));
        var failure = await Assert.ThrowsAsync<ProviderFailureException>(() =>
            client.GetQuotaAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ProviderHealth.Stale, failure.Health);
        Assert.Equal("opencode_go_timeout", failure.DiagnosticCode);
    }

    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void ParserRejectsMissingWindows()
    {
        var malformed = Encoding.UTF8.GetBytes("{\"usage\":{\"rolling\":{}}}");
        Assert.Throws<FormatException>(() => OpenCodeGoQuotaParser.Parse(malformed));
    }

    private sealed class FakeCredentials(string? apiKey) : IOpenCodeGoCredentialStore
    {
        public bool IsConfigured() => apiKey is not null;
        public string? ReadApiKey() => apiKey;
        public void SaveApiKey(string value) => throw new NotSupportedException();
        public void DeleteApiKey() => throw new NotSupportedException();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
