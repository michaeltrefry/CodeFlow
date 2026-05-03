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
        if (deserialized is null)
        {
            return Array.Empty<string>();
        }

        // Defensive scrub: workflows saved before the package importer's apply path started
        // running WorkflowValidator (closed 2026-04-30 in commit 90e6cde) may have landed in
        // the DB with the implicit `Failed` or synthesized `Exhausted` port redundantly listed
        // in OutputPorts. Both are runtime-synthesized regardless of whether they appear in the
        // persisted list (Workflow.ComputeTerminalPorts skips Failed and unconditionally adds
        // Exhausted for ReviewLoop), so stripping them on read is behavior-neutral at runtime
        // and unblocks editing — the editor's save endpoint runs WorkflowValidator on the
        // posted node list, which carries whatever the editor loaded; without the scrub the
        // user can never save edits to a grandfathered workflow because the same validator
        // hard-rejects the very port the DB persists. New packages that declare these ports are
        // still rejected up-front by WorkflowValidator.CheckDeclaredPortReservations on the
        // assistant's save tool and the import-apply endpoint.
        var filtered = new List<string>(deserialized.Length);
        foreach (var port in deserialized)
        {
            if (string.IsNullOrWhiteSpace(port)) continue;
            if (string.Equals(port, "Failed", StringComparison.Ordinal)) continue;
            if (string.Equals(port, "Exhausted", StringComparison.Ordinal)) continue;
            filtered.Add(port);
        }
        return filtered;
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

    public static string? SerializeStringList(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        return JsonSerializer.Serialize(values, SerializerOptions);
    }

    public static IReadOnlyList<string>? DeserializeStringList(string? json)
    {
        if (json is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        var deserialized = JsonSerializer.Deserialize<string[]>(json, SerializerOptions);
        return deserialized ?? Array.Empty<string>();
    }
}
