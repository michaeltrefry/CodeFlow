using System.Text.Json;

namespace CodeFlow.Api.Endpoints;

/// <summary>
/// Shared empty-collection sentinels used by minimal-API endpoints when an optional
/// <see cref="JsonElement"/>-bearing request field is omitted. Keeping these here
/// avoids per-file <c>EmptyJsonElementDictionary</c> declarations that would otherwise
/// drift when one endpoint adds a comparer or initial capacity that another does not.
/// F-018 in the 2026-04-28 backend review.
/// </summary>
internal static class EndpointDefaults
{
    /// <summary>
    /// Empty JSON object element (<c>{}</c>). Used as the default <c>Input</c> on preview /
    /// validation requests where the caller passed no upstream artifact.
    /// </summary>
    public static readonly JsonElement EmptyJsonObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>
    /// Empty, ordinal-keyed dictionary used as the default <c>Context</c> / <c>Workflow</c> /
    /// form-field map on preview / validation requests.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, JsonElement> EmptyJsonElementMap =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}
