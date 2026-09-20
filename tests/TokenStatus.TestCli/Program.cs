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
            : args[0].Equals("--version", StringComparison.OrdinalIgnoreCase) ||
              args[0].Equals("db", StringComparison.OrdinalIgnoreCase)
                ? RunOpenCode(args)
            : args[0].Equals("opencode", StringComparison.OrdinalIgnoreCase)
                ? RunOpenCode(args.Skip(1).ToArray())
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

    private static int RunOpenCode(IReadOnlyList<string> args)
    {
        if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("1.18.31");
            return 0;
        }

        if (args.Contains("--fail", StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("no such table: session");
            return 1;
        }

        Console.WriteLine("[{\"sessions\":8,\"total_cost\":2.91,\"input_tokens\":129000,\"output_tokens\":78000,\"reasoning_tokens\":12000,\"cache_read_tokens\":8900000,\"cache_write_tokens\":11000}]");
        return 0;
    }
}

public static class TestCliMarker
{
}
