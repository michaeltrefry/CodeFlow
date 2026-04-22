using System.Collections.Generic;
using System.Text.Json;

namespace CodeFlow.Persistence;

public static class WorkflowSagaJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static IReadOnlyDictionary<string, int> DeserializePinnedVersions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, int>();
        }

        return JsonSerializer.Deserialize<Dictionary<string, int>>(json, Options)
            ?? new Dictionary<string, int>();
    }

    public static string SerializePinnedVersions(IReadOnlyDictionary<string, int> versions)
    {
        return JsonSerializer.Serialize(versions, Options);
    }
}
