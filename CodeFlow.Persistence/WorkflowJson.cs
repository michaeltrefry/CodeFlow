using System.Text.Json;

namespace CodeFlow.Persistence;

internal static class WorkflowJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

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
}
