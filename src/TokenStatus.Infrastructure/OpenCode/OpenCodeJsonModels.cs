using System.Globalization;
using System.Text.Json;
using TokenStatus.Core.Models;

namespace TokenStatus.Infrastructure.OpenCode;

public static class OpenCodeUsageParser
{
    public static OpenCodeLocalUsage Parse(string json)
    {
        if (json.Length > 2 * 1024 * 1024)
        {
            throw new FormatException("OpenCode returned too much data.");
        }

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1)
        {
            throw new FormatException("OpenCode database output must contain exactly one row.");
        }

        var row = root[0];
        if (row.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("OpenCode database output row must be an object.");
        }

        return new OpenCodeLocalUsage(
            ReadNonNegativeInt64(row, "sessions"),
            ReadNonNegativeDecimal(row, "total_cost"),
            ReadNonNegativeInt64(row, "input_tokens"),
            ReadNonNegativeInt64(row, "output_tokens"),
            ReadNonNegativeInt64(row, "reasoning_tokens"),
            ReadNonNegativeInt64(row, "cache_read_tokens"),
            ReadNonNegativeInt64(row, "cache_write_tokens"));
    }

    private static long ReadNonNegativeInt64(JsonElement row, string name)
    {
        if (!TryGetProperty(row, name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException($"OpenCode output is missing numeric field {name}.");
        }

        if (!value.TryGetInt64(out var result) || result < 0)
        {
            throw new FormatException($"OpenCode output field {name} is invalid.");
        }

        return result;
    }

    private static decimal ReadNonNegativeDecimal(JsonElement row, string name)
    {
        if (!TryGetProperty(row, name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out var result) || result < 0)
        {
            throw new FormatException($"OpenCode output field {name} is invalid.");
        }

        return decimal.Round(result, 6, MidpointRounding.ToEven);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
