using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TokenStatus.Core.Models;
using TokenStatus.Infrastructure.Claude;
using TokenStatus.TestCli;

namespace TokenStatus.Infrastructure.Tests;

public sealed class ClaudeCodeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TokenStatus.ClaudeTests", Guid.NewGuid().ToString("N"));
    private string ConfigDirectory => Path.Combine(_directory, "config");
    private string DataDirectory => Path.Combine(_directory, "data");

    [Fact]
    public async Task UsageOnlyQueryClosesProcessAndCanRestart()
    {
        var executable = CopyFixture();
        var client = new ClaudeCodeQuotaClient(executable);
        for (var i = 0; i < 2; i++)
        {
            var quota = await client.GetQuotaAsync(CancellationToken.None);
            Assert.Equal(75, quota.FiveHour!.RemainingPercent);
            Assert.Equal(24, quota.SevenDay!.RemainingPercent);
            Assert.Equal(DateTimeOffset.Parse("2026-09-30T04:50:00Z"), quota.FiveHour.ResetsAt);
            // Windows cannot replace a running executable.
            using (new FileStream(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Move(executable, executable + ".released");
            File.Move(executable + ".released", executable);
        }
    }

    [Fact]
    public async Task TimeoutClosesProcessAndReleasesExecutable()
    {
        var executable = CopyFixture();
        var client = new ClaudeCodeQuotaClient(executable, TimeSpan.FromTicks(1));
        var failure = await Assert.ThrowsAsync<ProviderFailureException>(() => client.GetQuotaAsync(CancellationToken.None));
        Assert.Equal("claude_timeout", failure.DiagnosticCode);
        using (new FileStream(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        var executable = CopyFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ClaudeCodeQuotaClient(executable).GetQuotaAsync(cancellation.Token));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(0.4, 99)]
    [InlineData(101, 0)]
    public void UtilizationIsPercentAndOverageIsExhausted(decimal used, int remaining)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            rate_limits_available = true,
            rate_limits = new { five_hour = new { utilization = used } }
        }));
        Assert.Equal(remaining, ClaudeCodeQuotaClient.Parse(document.RootElement, DateTimeOffset.UtcNow)
            .FiveHour!.RemainingPercent);
    }

    [Theory]
    [InlineData("""{"rate_limits_available":true,"rate_limits":{"five_hour":{"utilization":-1}}}""")]
    [InlineData("""{"rate_limits_available":true,"rate_limits":{"five_hour":{"utilization":"0"}}}""")]
    [InlineData("""{"rate_limits_available":true,"rate_limits":{"five_hour":{"utilization":0,"resets_at":123}}}""")]
    [InlineData("""{"rate_limits_available":true,"rate_limits":{}}""")]
    public void InvalidUsageDoesNotBecomeZero(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<FormatException>(() => ClaudeCodeQuotaClient.Parse(document.RootElement, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("""{"rate_limits_available":false}""", ProviderHealth.NotAuthenticated)]
    [InlineData("""{"rate_limits_available":true,"rate_limits":null}""", ProviderHealth.Stale)]
    public void UnavailableUsageHasExplicitState(string json, ProviderHealth expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, Assert.Throws<ProviderFailureException>(() =>
            ClaudeCodeQuotaClient.Parse(document.RootElement, DateTimeOffset.UtcNow)).Health);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MigrationRestoresOriginalStatusLineAndPreservesOtherSettings(bool hadStatusLine)
    {
        var previous = hadStatusLine
            ? new JsonObject { ["type"] = "command", ["command"] = "my-display", ["padding"] = 2 }
            : null;
        PrepareBridge(previous);
        ClaudeStatusLineMigration.RestorePrevious(ConfigDirectory, DataDirectory);
        ClaudeStatusLineMigration.RestorePrevious(ConfigDirectory, DataDirectory);
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(ConfigDirectory, "settings.json")))!;
        Assert.Equal("Read", settings["permissions"]!["allow"]![0]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(previous, settings["statusLine"]));
        Assert.False(File.Exists(Path.Combine(DataDirectory, "statusline.ps1")));
    }

    [Fact]
    public void MigrationLeavesUserReplacementAlone()
    {
        PrepareBridge(null);
        var path = Path.Combine(ConfigDirectory, "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(path))!;
        settings["statusLine"]!["command"] = "new-user-display";
        var original = settings.ToJsonString();
        File.WriteAllText(path, original);
        ClaudeStatusLineMigration.RestorePrevious(ConfigDirectory, DataDirectory);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.True(File.Exists(Path.Combine(DataDirectory, "previous-statusline.json")));
    }

    private void PrepareBridge(JsonNode? previous)
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataDirectory);
        var scriptPath = Path.Combine(DataDirectory, "statusline.ps1");
        File.WriteAllText(scriptPath, "old bridge");
        File.WriteAllText(Path.Combine(DataDirectory, "previous-statusline.json"), previous?.ToJsonString() ?? "null");
        var command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes("& '" + scriptPath.Replace("'", "''") + "'"));
        File.WriteAllText(Path.Combine(ConfigDirectory, "settings.json"), new JsonObject
        {
            ["permissions"] = new JsonObject { ["allow"] = new JsonArray("Read") },
            ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = command }
        }.ToJsonString());
    }

    private string CopyFixture()
    {
        Directory.CreateDirectory(_directory);
        var sourceDirectory = Path.GetDirectoryName(typeof(TestCliMarker).Assembly.Location)!;
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "TokenStatus.TestCli.*"))
            File.Copy(source, Path.Combine(_directory, Path.GetFileName(source)));
        return Path.Combine(_directory, "TokenStatus.TestCli.exe");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
