using CodeFlow.Runtime;
using System.Text.Json;

namespace CodeFlow.Persistence;

internal static class AgentConfigJson
{
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
}
