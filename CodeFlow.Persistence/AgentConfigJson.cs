using CodeFlow.Runtime;
using System.Text.Json;

namespace CodeFlow.Persistence;

internal static class AgentConfigJson
{
    private const string TypeProperty = "type";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static AgentInvocationConfiguration Deserialize(string configJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configJson);

        try
        {
            var configuration = JsonSerializer.Deserialize<AgentInvocationConfiguration>(
                configJson,
                SerializerOptions);

            return configuration ?? throw new InvalidOperationException(
                "Agent config JSON could not be deserialized into an agent invocation configuration.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Agent config JSON could not be deserialized into an agent invocation configuration.",
                exception);
        }
    }

    public static string Serialize(AgentInvocationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return JsonSerializer.Serialize(configuration, SerializerOptions);
    }

    public static AgentKind ReadKind(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return AgentKind.Agent;
        }

        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return AgentKind.Agent;
            }

            if (!document.RootElement.TryGetProperty(TypeProperty, out var typeElement))
            {
                return AgentKind.Agent;
            }

            if (typeElement.ValueKind != JsonValueKind.String)
            {
                return AgentKind.Agent;
            }

            return Enum.TryParse<AgentKind>(typeElement.GetString(), ignoreCase: true, out var kind)
                ? kind
                : AgentKind.Agent;
        }
        catch (JsonException)
        {
            return AgentKind.Agent;
        }
    }
}
