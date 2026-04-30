using System.Text.Json;
using CodeFlow.Contracts.Notifications;
using CodeFlow.Persistence.Notifications;
using CodeFlow.Runtime;
using Scriban.Runtime;

namespace CodeFlow.Orchestration.Notifications;

/// <summary>
/// Default <see cref="INotificationTemplateRenderer"/> backed by the existing Scriban
/// renderer. Loads the template snapshot from <see cref="INotificationTemplateRepository"/>
/// (so the version pinned on the route is rendered, not whatever happens to be the latest at
/// dispatch time), serialises the event to JSON with a snake_case naming policy, and pushes
/// each top-level field into the Scriban scope. This works for every concrete
/// <see cref="INotificationEvent"/> implementer without per-type binding code.
/// </summary>
public sealed class ScribanNotificationTemplateRenderer(
    INotificationTemplateRepository templateRepository,
    IScribanTemplateRenderer scribanRenderer)
    : INotificationTemplateRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public async Task<NotificationMessage> RenderAsync(
        INotificationEvent notificationEvent,
        NotificationRoute route,
        IReadOnlyList<NotificationRecipient> recipients,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notificationEvent);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(recipients);

        var template = await templateRepository.GetAsync(
            route.Template.TemplateId,
            route.Template.Version,
            cancellationToken);

        if (template is null)
        {
            throw new NotificationTemplateNotFoundException(route.Template);
        }

        var scope = BuildScope(notificationEvent);

        var subject = string.IsNullOrEmpty(template.SubjectTemplate)
            ? null
            : scribanRenderer.Render(template.SubjectTemplate, scope, cancellationToken);
        var body = scribanRenderer.Render(template.BodyTemplate, scope, cancellationToken);

        return new NotificationMessage(
            EventId: notificationEvent.EventId,
            EventKind: notificationEvent.Kind,
            Channel: route.Recipients.Count > 0 ? route.Recipients[0].Channel : NotificationChannel.Unspecified,
            Recipients: recipients,
            Body: body,
            ActionUrl: notificationEvent.ActionUrl,
            Severity: notificationEvent.Severity,
            Subject: subject,
            Template: route.Template);
    }

    private static ScriptObject BuildScope(INotificationEvent notificationEvent)
    {
        var element = JsonSerializer.SerializeToElement((object)notificationEvent, JsonOptions);
        var scope = new ScriptObject();
        if (element.ValueKind != JsonValueKind.Object)
        {
            return scope;
        }

        foreach (var property in element.EnumerateObject())
        {
            scope[property.Name] = ConvertJsonValue(property.Value);
        }

        return scope;
    }

    private static object? ConvertJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l)
                ? (object)l
                : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Object => ConvertObject(element),
            JsonValueKind.Array => ConvertArray(element),
            _ => element.ToString(),
        };
    }

    private static ScriptObject ConvertObject(JsonElement element)
    {
        var nested = new ScriptObject();
        foreach (var prop in element.EnumerateObject())
        {
            nested[prop.Name] = ConvertJsonValue(prop.Value);
        }
        return nested;
    }

    private static ScriptArray ConvertArray(JsonElement element)
    {
        var array = new ScriptArray();
        foreach (var item in element.EnumerateArray())
        {
            array.Add(ConvertJsonValue(item));
        }
        return array;
    }
}
