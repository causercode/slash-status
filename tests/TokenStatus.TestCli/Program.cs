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

        if (args[0] == "--print") return RunClaude(args);
        return args[0].Equals("codex", StringComparison.OrdinalIgnoreCase) ||
               args[0].Equals("app-server", StringComparison.OrdinalIgnoreCase)
            ? RunCodex(args.Skip(1).ToArray())
            : 2;
    }

    private static int RunClaude(string[] options)
    {
        // A usage probe must disable behaviors and never send a user/model prompt.
        if (!options.Contains("--no-session-persistence") ||
            !options.Contains("{\"disableAllHooks\":true}") ||
            !options.Contains("--strict-mcp-config") ||
            !options.Contains("{\"mcpServers\":{}}") ||
            !options.Contains("--disable-slash-commands") ||
            !options.Contains("--no-chrome") ||
            Array.IndexOf(options, "--tools") is var toolIndex &&
            (toolIndex < 0 || options[toolIndex + 1] != "")) return 3;
        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() != "control_request") return 4;
            var request = root.GetProperty("request");
            var subtype = request.GetProperty("subtype").GetString();
            object result;
            if (subtype == "initialize") result = new { };
            else if (subtype == "get_usage" && request.GetProperty("skip_behaviors").GetBoolean())
            {
                result = new
                {
                    rate_limits_available = true,
                    rate_limits = new
                    {
                        five_hour = new { utilization = 24.2, resets_at = "2026-09-30T04:50:00Z" },
                        seven_day = new { utilization = 76, resets_at = "2026-10-05T15:00:00Z" }
                    }
                };
            }
            else return 5;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                type = "control_response",
                response = new { subtype = "success", request_id = root.GetProperty("request_id"), response = result }
            }));
            Console.Out.Flush();
        }
        return 0;
    }

    private static int RunCodex(string[] options)
    {
        var oversizedResponse = options.Any(option => option.Equals("oversized", StringComparison.OrdinalIgnoreCase));
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

            if (oversizedResponse && method == "initialize")
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    id,
                    result = new { payload = new string('x', 4 * 1024 * 1024 + 1) }
                }));
                Console.Out.Flush();
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
