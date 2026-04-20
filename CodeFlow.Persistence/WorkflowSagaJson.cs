using CodeFlow.Runtime;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeFlow.Persistence;

public static class WorkflowSagaJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter<AgentDecisionKind>()
        },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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

    public static IReadOnlyList<DecisionRecord> DeserializeDecisionHistory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<DecisionRecord>>(json, Options) ?? [];
    }

    public static string SerializeDecisionHistory(IReadOnlyList<DecisionRecord> history)
    {
        return JsonSerializer.Serialize(history, Options);
    }
}
