using System.Globalization;
using System.Text.Json;
using TokenStatus.Core.Models;

namespace TokenStatus.Infrastructure.Codex;

public static class CodexResponseParser
{
    public static CodexAccountInfo ParseAccount(JsonElement result)
    {
        var account = result;
        if (TryGetProperty(result, "account", out var nestedAccount))
        {
            account = nestedAccount;
        }

        if (account.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new CodexAccountInfo(false, null, null);
        }

        var authMode = GetString(account, "authMode") ?? GetString(account, "type") ?? GetString(result, "authMode");
        var planType = GetString(account, "planType") ?? GetString(account, "plan") ?? GetString(result, "planType");
        var isAuthenticated = GetNullableBoolean(account, "isAuthenticated")
            ?? GetNullableBoolean(result, "isAuthenticated")
            ?? !string.IsNullOrWhiteSpace(authMode);
        return new CodexAccountInfo(
            isAuthenticated,
            authMode,
            planType);
    }

    public static CodexRateLimits ParseRateLimits(JsonElement result)
    {
        var buckets = new List<RateLimitBucket>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (TryGetProperty(result, "rateLimitsByLimitId", out var byLimitId) &&
            byLimitId.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in byLimitId.EnumerateObject())
            {
                var bucket = ParseBucket(property.Value, property.Name);
                if (seen.Add(bucket.Id))
                {
                    buckets.Add(bucket);
                }
            }
        }

        if (buckets.Count == 0 && TryGetProperty(result, "rateLimits", out var singular) &&
            singular.ValueKind == JsonValueKind.Object)
        {
            var bucket = ParseBucket(singular, null);
            if (seen.Add(bucket.Id))
            {
                buckets.Add(bucket);
            }
        }

        int? resetCreditCount = null;
        decimal? creditBalance = GetNullableDecimal(result, "creditBalance");
        if (TryGetProperty(result, "rateLimitResetCredits", out var resetCredits) &&
            resetCredits.ValueKind == JsonValueKind.Object)
        {
            resetCreditCount = GetNullableInt32(resetCredits, "availableCount");
            creditBalance ??= GetNullableDecimal(resetCredits, "creditBalance") ?? GetNullableDecimal(resetCredits, "balance");
            if (resetCreditCount is null && TryGetProperty(resetCredits, "credits", out var credits) &&
                credits.ValueKind == JsonValueKind.Array)
            {
                resetCreditCount = credits.EnumerateArray().Count();
            }
        }

        return new CodexRateLimits(buckets, resetCreditCount, creditBalance);
    }

    public static CodexTokenUsage ParseTokenUsage(JsonElement result)
    {
        var summary = TryGetProperty(result, "summary", out var summaryElement) &&
                      summaryElement.ValueKind == JsonValueKind.Object
            ? summaryElement
            : default;

        var dailyBuckets = ParseDailyBuckets(result);
        return new CodexTokenUsage(
            GetNullableInt64(summary, "lifetimeTokens"),
            GetNullableInt64(summary, "peakDailyTokens"),
            GetNullableDouble(summary, "longestRunningTurnSec") is { } seconds
                ? TimeSpan.FromSeconds(Math.Max(0, seconds))
                : null,
            GetNullableInt32(summary, "currentStreakDays"),
            GetNullableInt32(summary, "longestStreakDays"),
            dailyBuckets);
    }

    public static CodexRateLimits ParseRateLimitsJson(string json)
    {
        using var document = ParseDocument(json);
        return ParseRateLimits(document.RootElement);
    }

    public static CodexTokenUsage ParseTokenUsageJson(string json)
    {
        using var document = ParseDocument(json);
        return ParseTokenUsage(document.RootElement);
    }

    private static IReadOnlyList<DailyTokenUsage>? ParseDailyBuckets(JsonElement result)
    {
        if (!TryGetProperty(result, "dailyUsageBuckets", out var daily))
        {
            return null;
        }

        if (daily.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (daily.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("dailyUsageBuckets must be an array or null.");
        }

        var parsed = new List<DailyTokenUsage>();
        foreach (var bucket in daily.EnumerateArray())
        {
            var dateText = GetString(bucket, "startDate")
                ?? throw new FormatException("A daily usage bucket is missing startDate.");
            if (!DateOnly.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                throw new FormatException("A daily usage bucket has an invalid startDate.");
            }

            var tokens = GetNullableInt64(bucket, "tokens")
                ?? throw new FormatException("A daily usage bucket is missing tokens.");
            parsed.Add(new DailyTokenUsage(date, tokens));
        }

        return parsed;
    }

    private static RateLimitBucket ParseBucket(JsonElement bucket, string? fallbackId)
    {
        var id = GetString(bucket, "limitId") ?? fallbackId;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new FormatException("A Codex rate-limit bucket is missing limitId.");
        }

        return new RateLimitBucket(
            id,
            GetString(bucket, "limitName"),
            GetString(bucket, "planType"),
            ParseWindow(bucket, "primary"),
            ParseWindow(bucket, "secondary"),
            GetNullableBoolean(bucket, "ordinaryUsageAllowed"),
            GetString(bucket, "rateLimitReachedType"));
    }

    private static RateLimitWindow? ParseWindow(JsonElement bucket, string propertyName)
    {
        if (!TryGetProperty(bucket, propertyName, out var window) || window.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (window.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"Codex {propertyName} rate-limit window must be an object or null.");
        }

        var used = GetNullableDouble(window, "usedPercent")
            ?? throw new FormatException($"Codex {propertyName} rate-limit window is missing usedPercent.");
        TimeSpan? duration = GetNullableDouble(window, "windowDurationMins") is { } minutes
            ? TimeSpan.FromMinutes(Math.Max(0, minutes))
            : null;
        var resetSeconds = GetNullableDouble(window, "resetsAt");
        DateTimeOffset? resetsAt = null;
        if (resetSeconds is { } seconds)
        {
            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(seconds, CultureInfo.InvariantCulture));
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
            {
                resetsAt = null;
            }
        }

        var clampedUsed = Math.Clamp(used, 0, 100);
        return new RateLimitWindow(Convert.ToInt32(Math.Round(clampedUsed)), duration, resetsAt);
    }

    private static JsonDocument ParseDocument(string json)
    {
        if (json.Length > JsonLineRpcConnection.MaximumLineCharacters)
        {
            throw new FormatException("The Codex JSON response is too large.");
        }

        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? GetNullableBoolean(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value) switch
        {
            true when value.ValueKind == JsonValueKind.True => true,
            true when value.ValueKind == JsonValueKind.False => false,
            _ => null
        };
    }

    private static int? GetNullableInt32(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetInt32(out var integer))
        {
            return integer;
        }

        return value.TryGetDouble(out var number) &&
               double.IsFinite(number) &&
               number >= int.MinValue && number <= int.MaxValue
            ? Convert.ToInt32(number, CultureInfo.InvariantCulture)
            : null;
    }

    private static long? GetNullableInt64(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetInt64(out var integer))
        {
            return integer;
        }

        return value.TryGetDouble(out var number) &&
               double.IsFinite(number) &&
               number >= long.MinValue && number <= long.MaxValue
            ? Convert.ToInt64(number, CultureInfo.InvariantCulture)
            : null;
    }

    private static double? GetNullableDouble(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;
    }

    private static decimal? GetNullableDecimal(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDecimal(out var number)
            ? number
            : null;
    }
}
