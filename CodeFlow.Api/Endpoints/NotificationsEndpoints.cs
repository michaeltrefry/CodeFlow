using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Contracts.Notifications;
using CodeFlow.Orchestration.Notifications;
using CodeFlow.Persistence.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Endpoints;

/// <summary>
/// Admin API for the HITL notification subsystem (epic 48 / sc-57). Lists, upserts, archives
/// providers; lists, upserts, deletes routes; lists templates (creation/editing is sc-63).
/// All endpoints sit behind <see cref="CodeFlowApiDefaults.Policies.NotificationsRead"/> /
/// <see cref="CodeFlowApiDefaults.Policies.NotificationsWrite"/> — Admin role only, matching
/// the LLM-providers and Git-host admin policies.
/// </summary>
public static class NotificationsEndpoints
{
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var providers = routes.MapGroup("/api/admin/notification-providers");
        providers.MapGet("/", ListProvidersAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.NotificationsRead);
        providers.MapPut("/{id}", PutProviderAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.NotificationsWrite);
        providers.MapDelete("/{id}", ArchiveProviderAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.NotificationsWrite);

        var routesGroup = routes.MapGroup("/api/admin/notification-routes");
        routesGroup.MapGet("/", ListRoutesAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.NotificationsRead);
        routesGroup.MapPut("/{routeId}", PutRouteAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.NotificationsWrite);
        routesGroup.MapDelete("/{routeId}", DeleteRouteAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.NotificationsWrite);

        var templates = routes.MapGroup("/api/admin/notification-templates");
        templates.MapGet("/", ListTemplatesAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.NotificationsRead);

        // Diagnostics — read-only snapshot the admin UI surfaces in a header banner.
        routes.MapGet("/api/admin/notifications/diagnostics", GetDiagnosticsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.NotificationsRead);

        return routes;
    }

    // --- providers --------------------------------------------------------------------

    private static async Task<IResult> ListProvidersAsync(
        INotificationProviderConfigRepository repository,
        bool? includeArchived,
        CancellationToken cancellationToken)
    {
        var configs = await repository.ListAsync(includeArchived ?? false, cancellationToken);
        return Results.Ok(configs.Select(MapProvider).ToArray());
    }

    private static async Task<IResult> PutProviderAsync(
        string id,
        NotificationProviderWriteRequest? request,
        INotificationProviderConfigRepository repository,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("Request body is required.");
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["id"] = new[] { "Provider id is required." },
            });
        }

        var errors = ValidateProvider(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var credential = MapCredential(request.Credential);

        try
        {
            await repository.UpsertAsync(new NotificationProviderUpsert(
                Id: id,
                DisplayName: request.DisplayName,
                Channel: request.Channel,
                EndpointUrl: request.EndpointUrl,
                FromAddress: request.FromAddress,
                Credential: credential,
                AdditionalConfigJson: request.AdditionalConfigJson,
                Enabled: request.Enabled,
                UpdatedBy: currentUser.Id), cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["body"] = new[] { ex.Message },
            });
        }

        var saved = await repository.GetAsync(id, cancellationToken);
        return saved is null
            ? Results.NotFound()
            : Results.Ok(MapProvider(saved));
    }

    private static async Task<IResult> ArchiveProviderAsync(
        string id,
        INotificationProviderConfigRepository repository,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var existing = await repository.GetAsync(id, cancellationToken);
        if (existing is null)
        {
            return Results.NotFound();
        }

        await repository.ArchiveAsync(id, currentUser.Id, cancellationToken);
        return Results.NoContent();
    }

    // --- routes -----------------------------------------------------------------------

    private static async Task<IResult> ListRoutesAsync(
        INotificationRouteRepository repository,
        CancellationToken cancellationToken)
    {
        var routes = await repository.ListAsync(cancellationToken);
        return Results.Ok(routes.Select(MapRoute).ToArray());
    }

    private static async Task<IResult> PutRouteAsync(
        string routeId,
        NotificationRouteWriteRequest? request,
        INotificationRouteRepository routeRepo,
        INotificationProviderConfigRepository providerRepo,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("Request body is required.");
        }

        var errors = ValidateRoute(routeId, request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // Provider must exist and channel must match every recipient channel — otherwise the
        // dispatcher would record Failed audit rows on every event for the route.
        var provider = await providerRepo.GetAsync(request.ProviderId, cancellationToken);
        if (provider is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["providerId"] = new[] { $"No provider configured with id '{request.ProviderId}'." },
            });
        }

        if (provider.IsArchived)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["providerId"] = new[] { $"Provider '{request.ProviderId}' is archived." },
            });
        }

        foreach (var recipient in request.Recipients)
        {
            if (recipient.Channel != provider.Channel)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["recipients"] = new[]
                    {
                        $"Recipient channel {recipient.Channel} does not match provider channel {provider.Channel}.",
                    },
                });
            }
        }

        var route = new NotificationRoute(
            RouteId: routeId,
            EventKind: request.EventKind,
            ProviderId: request.ProviderId,
            Recipients: request.Recipients
                .Select(r => new NotificationRecipient(r.Channel, r.Address, r.DisplayName))
                .ToArray(),
            Template: new NotificationTemplateRef(request.Template.TemplateId, request.Template.Version),
            MinimumSeverity: request.MinimumSeverity,
            Enabled: request.Enabled);

        try
        {
            await routeRepo.UpsertAsync(route, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["body"] = new[] { ex.Message },
            });
        }

        var saved = await routeRepo.GetAsync(routeId, cancellationToken);
        return saved is null
            ? Results.NotFound()
            : Results.Ok(MapRoute(saved));
    }

    private static async Task<IResult> DeleteRouteAsync(
        string routeId,
        INotificationRouteRepository repository,
        CancellationToken cancellationToken)
    {
        var existing = await repository.GetAsync(routeId, cancellationToken);
        if (existing is null)
        {
            return Results.NotFound();
        }

        await repository.DeleteAsync(routeId, cancellationToken);
        return Results.NoContent();
    }

    // --- templates --------------------------------------------------------------------

    private static async Task<IResult> ListTemplatesAsync(
        string? templateId,
        INotificationTemplateRepository repository,
        CancellationToken cancellationToken)
    {
        // Without a templateId, return the latest version of every template the admin can
        // currently pick from. With a templateId, return the full version history for that
        // single template (used by the route editor when the admin wants to pin a specific
        // version).
        if (!string.IsNullOrWhiteSpace(templateId))
        {
            var versions = await repository.ListVersionsAsync(templateId, cancellationToken);
            return Results.Ok(versions.Select(MapTemplate).ToArray());
        }

        // Latest-only listing isn't on the repository contract today — sc-63 will likely add
        // it. For now we surface a not-implemented response with a clear error message; the
        // route editor uses templateId-scoped lookups in the meantime.
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["templateId"] = new[] { "templateId query parameter is required (full inventory listing lands in sc-63)." },
        });
    }

    // --- diagnostics ------------------------------------------------------------------

    private static async Task<IResult> GetDiagnosticsAsync(
        IOptions<NotificationOptions> options,
        INotificationProviderConfigRepository providers,
        INotificationRouteRepository routes,
        CancellationToken cancellationToken)
    {
        var publicBaseUrl = options.Value.PublicBaseUrl;
        var providerList = await providers.ListAsync(includeArchived: false, cancellationToken);
        var routeList = await routes.ListAsync(cancellationToken);

        return Results.Ok(new NotificationDiagnosticsResponse(
            PublicBaseUrl: publicBaseUrl,
            ProviderCount: providerList.Count,
            RouteCount: routeList.Count,
            ActionUrlsConfigured: !string.IsNullOrWhiteSpace(publicBaseUrl)));
    }

    // --- mappers ----------------------------------------------------------------------

    private static NotificationProviderResponse MapProvider(NotificationProviderConfig config) => new(
        Id: config.Id,
        DisplayName: config.DisplayName,
        Channel: config.Channel,
        EndpointUrl: config.EndpointUrl,
        FromAddress: config.FromAddress,
        HasCredential: config.HasCredential,
        AdditionalConfigJson: config.AdditionalConfigJson,
        Enabled: config.Enabled,
        IsArchived: config.IsArchived,
        CreatedAtUtc: config.CreatedAtUtc,
        CreatedBy: config.CreatedBy,
        UpdatedAtUtc: config.UpdatedAtUtc,
        UpdatedBy: config.UpdatedBy);

    private static NotificationRouteResponse MapRoute(NotificationRoute route) => new(
        RouteId: route.RouteId,
        EventKind: route.EventKind,
        ProviderId: route.ProviderId,
        Recipients: route.Recipients
            .Select(r => new NotificationRecipientDto(r.Channel, r.Address, r.DisplayName))
            .ToArray(),
        Template: new NotificationTemplateRefDto(route.Template.TemplateId, route.Template.Version),
        MinimumSeverity: route.MinimumSeverity,
        Enabled: route.Enabled);

    private static NotificationTemplateResponse MapTemplate(NotificationTemplate template) => new(
        TemplateId: template.TemplateId,
        Version: template.Version,
        EventKind: template.EventKind,
        Channel: template.Channel,
        SubjectTemplate: template.SubjectTemplate,
        BodyTemplate: template.BodyTemplate,
        CreatedAtUtc: template.CreatedAtUtc,
        CreatedBy: template.CreatedBy,
        UpdatedAtUtc: template.UpdatedAtUtc,
        UpdatedBy: template.UpdatedBy);

    private static NotificationProviderCredentialUpdate MapCredential(
        NotificationProviderCredentialUpdateRequest? request)
    {
        return (request?.Action ?? NotificationProviderCredentialActionRequest.Preserve) switch
        {
            NotificationProviderCredentialActionRequest.Replace =>
                new NotificationProviderCredentialUpdate(
                    NotificationProviderCredentialAction.Replace, request!.Value),
            NotificationProviderCredentialActionRequest.Clear =>
                new NotificationProviderCredentialUpdate(NotificationProviderCredentialAction.Clear, null),
            _ => new NotificationProviderCredentialUpdate(NotificationProviderCredentialAction.Preserve, null),
        };
    }

    // --- validation -------------------------------------------------------------------

    private static Dictionary<string, string[]> ValidateProvider(NotificationProviderWriteRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            errors["displayName"] = new[] { "displayName is required." };
        }

        if (request.Channel == NotificationChannel.Unspecified)
        {
            errors["channel"] = new[] { "channel must be one of Email, Sms, Slack." };
        }

        if (request.Credential is { Action: NotificationProviderCredentialActionRequest.Replace, Value: var v }
            && string.IsNullOrEmpty(v))
        {
            errors["credential.value"] = new[] { "credential.value is required when action is Replace." };
        }

        if (!string.IsNullOrWhiteSpace(request.EndpointUrl)
            && (!Uri.TryCreate(request.EndpointUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            errors["endpointUrl"] = new[] { "endpointUrl must be an absolute http(s) URL." };
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateRoute(string routeId, NotificationRouteWriteRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(routeId))
        {
            errors["routeId"] = new[] { "routeId is required." };
        }

        if (request.EventKind == NotificationEventKind.Unspecified)
        {
            errors["eventKind"] = new[] { "eventKind must be a known notification event kind." };
        }

        if (string.IsNullOrWhiteSpace(request.ProviderId))
        {
            errors["providerId"] = new[] { "providerId is required." };
        }

        if (request.Template is null
            || string.IsNullOrWhiteSpace(request.Template.TemplateId)
            || request.Template.Version <= 0)
        {
            errors["template"] = new[] { "template.templateId and template.version are required." };
        }

        if (request.Recipients is null || request.Recipients.Count == 0)
        {
            errors["recipients"] = new[] { "recipients must include at least one entry." };
        }
        else
        {
            for (var i = 0; i < request.Recipients.Count; i++)
            {
                var recipient = request.Recipients[i];
                if (string.IsNullOrWhiteSpace(recipient.Address))
                {
                    errors[$"recipients[{i}].address"] = new[] { "address is required." };
                }
                if (recipient.Channel == NotificationChannel.Unspecified)
                {
                    errors[$"recipients[{i}].channel"] = new[] { "channel must be one of Email, Sms, Slack." };
                }
            }
        }

        return errors;
    }
}
