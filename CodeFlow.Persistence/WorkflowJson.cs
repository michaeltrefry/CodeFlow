using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeFlow.Persistence;

public static class WorkflowJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions ConfigSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string SerializePorts(IReadOnlyList<string> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        return JsonSerializer.Serialize(ports, SerializerOptions);
    }

    public static IReadOnlyList<string> DeserializePorts(string? portsJson)
    {
        if (string.IsNullOrWhiteSpace(portsJson))
        {
            return Array.Empty<string>();
        }

        var deserialized = JsonSerializer.Deserialize<string[]>(portsJson, SerializerOptions);
        return deserialized ?? Array.Empty<string>();
    }

    public static string SerializeTags(IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return JsonSerializer.Serialize(tags, SerializerOptions);
    }

    public static IReadOnlyList<string> DeserializeTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return Array.Empty<string>();
        }

        var deserialized = JsonSerializer.Deserialize<string[]>(tagsJson, SerializerOptions);
        return deserialized ?? Array.Empty<string>();
    }

    public static string? SerializeRejectionHistoryConfig(RejectionHistoryConfig? config)
    {
        return config is null
            ? null
            : JsonSerializer.Serialize(config, ConfigSerializerOptions);
    }

    public static RejectionHistoryConfig? DeserializeRejectionHistoryConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return null;
        }

        return JsonSerializer.Deserialize<RejectionHistoryConfig>(configJson, ConfigSerializerOptions);
    }

    public static string? SerializePortReplacements(IReadOnlyDictionary<string, string>? replacements)
    {
        if (replacements is null || replacements.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(replacements, ConfigSerializerOptions);
    }

    public static IReadOnlyDictionary<string, string>? DeserializePortReplacements(string? replacementsJson)
    {
        if (string.IsNullOrWhiteSpace(replacementsJson))
        {
            return null;
        }

        var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(
            replacementsJson, ConfigSerializerOptions);
        return deserialized is { Count: > 0 } ? deserialized : null;
    }
}
