using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeFlow.Api.Endpoints;

public static class ShortcutWebhookEndpoints
{
    private const string SupportedVersion = "v1";
    private const string StoryEntityType = "story";
    private const string StoryCreateAction = "create";

    public static IEndpointRouteBuilder MapShortcutWebhookEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/api/integrations/shortcut/webhook", HandleAsync);
        return routes;
    }

    private static async Task<IResult> HandleAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string rawBody;
        using (var reader = new StreamReader(request.Body))
        {
            rawBody = await reader.ReadToEndAsync(cancellationToken);
        }

        JsonDocument payload;
        try
        {
            payload = JsonDocument.Parse(rawBody);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ShortcutWebhookResponse("invalid_payload", "Malformed JSON payload."));
        }

        using (payload)
        {
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Results.BadRequest(new ShortcutWebhookResponse("invalid_payload", "Payload must be a JSON object."));
            }

            if (!TryGetRequiredString(root, "version", out var version) || !string.Equals(version, SupportedVersion, StringComparison.Ordinal))
            {
                return Results.BadRequest(new ShortcutWebhookResponse("unsupported_version", "Shortcut webhook version must be 'v1'."));
            }

            if (!root.TryGetProperty("actions", out var actionsElement) || actionsElement.ValueKind != JsonValueKind.Array)
            {
                return Results.BadRequest(new ShortcutWebhookResponse("invalid_payload", "Payload must include an actions array."));
            }

            var actions = actionsElement.EnumerateArray().ToArray();
            if (actions.Any(action => action.ValueKind != JsonValueKind.Object))
            {
                return Results.BadRequest(new ShortcutWebhookResponse("invalid_payload", "Each action must be a JSON object."));
            }

            var accepted = actions.Any(IsStoryCreateAction);
            return Results.Ok(new ShortcutWebhookResponse(accepted ? "accepted" : "ignored", accepted ? "Shortcut story create event accepted." : "Shortcut webhook event ignored."));
        }
    }

    private static bool IsStoryCreateAction(JsonElement action)
    {
        return TryGetRequiredString(action, "action", out var actionType)
            && TryGetRequiredString(action, "entity_type", out var entityType)
            && string.Equals(actionType, StoryCreateAction, StringComparison.Ordinal)
            && string.Equals(entityType, StoryEntityType, StringComparison.Ordinal);
    }

    private static bool TryGetRequiredString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private sealed record ShortcutWebhookResponse(string Status, string Message);
}
