using TokenStatus.Infrastructure.Codex;

namespace TokenStatus.Infrastructure.Tests;

public sealed class ParserTests
{
    [Fact]
    public void ParsesSingularRateLimitsAndUnixReset()
    {
        var json = """
            {
              "rateLimits": {
                "limitId": "codex",
                "limitName": "5-hour",
                "primary": { "usedPercent": 25, "windowDurationMins": 300, "resetsAt": 1730947200 },
                "secondary": null,
                "ordinaryUsageAllowed": false,
                "rateLimitReachedType": "primary"
              },
              "rateLimitResetCredits": { "availableCount": 2 }
            }
            """;

        var parsed = CodexResponseParser.ParseRateLimitsJson(json);

        var bucket = Assert.Single(parsed.Buckets);
        Assert.Equal("codex", bucket.Id);
        Assert.Equal(25, bucket.Primary!.UsedPercent);
        Assert.Equal(TimeSpan.FromMinutes(300), bucket.Primary.Duration);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1730947200), bucket.Primary.ResetsAt);
        Assert.False(bucket.OrdinaryUsageAllowed);
        Assert.Equal("primary", bucket.ReachedType);
        Assert.Equal(2, parsed.ResetCreditCount);
    }

    [Fact]
    public void PrefersMultiBucketViewWithoutDuplicatingSingularBucket()
    {
        var json = """
            {
              "rateLimits": { "limitId": "codex", "primary": { "usedPercent": 1 } },
              "rateLimitsByLimitId": {
                "codex": { "limitId": "codex", "primary": { "usedPercent": 7 } },
                "weekly": { "limitId": "weekly", "primary": { "usedPercent": 2 } }
              }
            }
            """;

        var parsed = CodexResponseParser.ParseRateLimitsJson(json);

        Assert.Equal(2, parsed.Buckets.Count);
        Assert.Equal(7, parsed.Buckets.Single(bucket => bucket.Id == "codex").Primary!.UsedPercent);
    }

    [Fact]
    public void ParsesUsageWithNullDailyBucketsAndOptionalSummary()
    {
        var parsed = CodexResponseParser.ParseTokenUsageJson("{\"summary\":{\"lifetimeTokens\":1234567,\"currentStreakDays\":8},\"dailyUsageBuckets\":null}");

        Assert.Equal(1234567, parsed.LifetimeTokens);
        Assert.Equal(8, parsed.CurrentStreakDays);
        Assert.Null(parsed.DailyUsageBuckets);
    }

    [Fact]
    public void EmptyDailyBucketsMeanKnownZeroForDatesNotReturned()
    {
        var parsed = CodexResponseParser.ParseTokenUsageJson("{\"summary\":{},\"dailyUsageBuckets\":[]}");

        Assert.NotNull(parsed.DailyUsageBuckets);
        Assert.Equal(0, parsed.GetTokensForDate(new DateOnly(2026, 9, 19)));
    }

    [Fact]
    public void ClampsOutOfRangeQuotaPercentages()
    {
        var parsed = CodexResponseParser.ParseRateLimitsJson("{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":1000}}}");

        Assert.Equal(100, Assert.Single(parsed.Buckets).Primary!.UsedPercent);
    }

}
