using System.Text.Json;

namespace CodeFlow.Orchestration.Notifications.Providers.Slack;

internal static class SlackJsonOptions
{
    /// <summary>
    /// Slack's Web API uses snake_case fields. Centralised options instance keeps allocations
    /// out of the per-send hot path.
    /// </summary>
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
