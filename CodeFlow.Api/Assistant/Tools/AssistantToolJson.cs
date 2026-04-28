using System.Text.Json;

namespace CodeFlow.Api.Assistant.Tools;

internal static class AssistantToolJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Truncate a string to <paramref name="maxChars"/> and append a marker so the LLM knows the
    /// tail was cut. Used for free-form fields (prompt templates, system prompts, JSON blobs as
    /// strings) that can be arbitrarily large.
    /// </summary>
    public static string TruncateText(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        if (value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars] + $"... [truncated, original was {value.Length} chars]";
    }

    /// <summary>
    /// Read an optional string property from a tool argument object. Returns null if the property
    /// is absent, JSON null, or whitespace.
    /// </summary>
    public static string? ReadOptionalString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        if (!args.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind != JsonValueKind.String) return null;
        var raw = prop.GetString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    public static int? ReadOptionalInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        if (!args.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var v) => v,
            _ => null
        };
    }

    public static bool ReadOptionalBool(JsonElement args, string name, bool defaultValue)
    {
        if (args.ValueKind != JsonValueKind.Object) return defaultValue;
        if (!args.TryGetProperty(name, out var prop)) return defaultValue;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue
        };
    }

    public static int ClampLimit(int? requested, int defaultLimit, int maxLimit)
    {
        var value = requested ?? defaultLimit;
        if (value < 1) value = defaultLimit;
        if (value > maxLimit) value = maxLimit;
        return value;
    }
}
