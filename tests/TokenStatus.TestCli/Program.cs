using System.Text.Json;

namespace TokenStatus.TestCli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return 2;
        }

        return args[0].Equals("codex", StringComparison.OrdinalIgnoreCase) ||
               args[0].Equals("app-server", StringComparison.OrdinalIgnoreCase)
            ? RunCodex()
            : 2;
    }

    private static int RunCodex()
    {
        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
            if (!root.TryGetProperty("id", out var id))
            {
                continue;
            }

            object result = method switch
            {
                "initialize" => new { },
                "account/read" => new { authMode = "chatgpt", planType = "plus", isAuthenticated = true },
                "account/rateLimits/read" => new
                {
                    rateLimits = new
                    {
                        limitId = "codex",
                        limitName = "5-hour",
                        primary = new { usedPercent = 7, windowDurationMins = 300, resetsAt = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() },
                        secondary = new { usedPercent = 1, windowDurationMins = 10080, resetsAt = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds() },
                        ordinaryUsageAllowed = true,
                        rateLimitReachedType = (string?)null
                    },
                    rateLimitsByLimitId = new { },
                    rateLimitResetCredits = new { availableCount = 0 }
                },
                "account/usage/read" => new
                {
                    summary = new { lifetimeTokens = 2400000000L, peakDailyTokens = 5900000L, longestRunningTurnSec = 540, currentStreakDays = 8, longestStreakDays = 14 },
                    dailyUsageBuckets = new[] { new { startDate = DateTime.UtcNow.ToString("yyyy-MM-dd"), tokens = 5900000L } }
                },
                _ => new { }
            };

            Console.WriteLine(JsonSerializer.Serialize(new { id, result }));
            Console.Out.Flush();
        }

        return 0;
    }

}

public static class TestCliMarker
{
}
