using CodeFlow.Runtime;
using System.Text.Json;

namespace CodeFlow.Persistence;

internal static class AgentConfigJson
{
    private const string TypeProperty = "type";
    private const string OutputsProperty = "outputs";

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

    public static IReadOnlyList<AgentOutputDeclaration> ReadOutputs(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return Array.Empty<AgentOutputDeclaration>();
        }

        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<AgentOutputDeclaration>();
            }

            if (!document.RootElement.TryGetProperty(OutputsProperty, out var outputsElement))
            {
                return Array.Empty<AgentOutputDeclaration>();
            }

            if (outputsElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<AgentOutputDeclaration>();
            }

            var result = new List<AgentOutputDeclaration>();
            foreach (var element in outputsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!element.TryGetProperty("kind", out var kindElement)
                    || kindElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var kind = kindElement.GetString();
                if (string.IsNullOrWhiteSpace(kind))
                {
                    continue;
                }

                string? description = null;
                if (element.TryGetProperty("description", out var descElement)
                    && descElement.ValueKind == JsonValueKind.String)
                {
                    description = descElement.GetString();
                }

                JsonElement? payloadExample = null;
                if (element.TryGetProperty("payloadExample", out var exampleElement)
                    && exampleElement.ValueKind != JsonValueKind.Null
                    && exampleElement.ValueKind != JsonValueKind.Undefined)
                {
                    payloadExample = exampleElement.Clone();
                }

                result.Add(new AgentOutputDeclaration(kind.Trim(), description, payloadExample));
            }

            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<AgentOutputDeclaration>();
        }
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
