using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Contracts.Notifications;
using CodeFlow.Orchestration.Notifications;
using CodeFlow.Persistence.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
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
        providers.MapPost("/{id}/validate", ValidateProviderAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.NotificationsWrite);
        providers.MapPost("/{id}/test-send", TestSendAsync)
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

    // --- validate + test send (sc-58) -------------------------------------------------

    private static async Task<IResult> ValidateProviderAsync(
        string id,
        INotificationProviderConfigRepository configRepo,
        INotificationProviderRegistry registry,
        CancellationToken cancellationToken)
    {
        var config = await configRepo.GetAsync(id, cancellationToken);
        if (config is null)
        {
            return Results.NotFound();
        }

        var provider = await registry.GetByIdAsync(id, cancellationToken);
        if (provider is null)
        {
            // The registry returns null when the row is archived/disabled OR when no factory
            // exists for the channel — both are admin-fixable conditions, surface them as a
            // structured validation response rather than a 404 so the UI can render the message
            // alongside the other validation outcomes.
            return Results.Ok(new NotificationProviderValidationResponse(
                IsValid: false,
                ErrorCode: "dispatcher.provider_not_registered",
                ErrorMessage: $"No active provider available for id '{id}' (archived, disabled, or no factory for channel {config.Channel})."));
        }

        var result = await provider.ValidateAsync(cancellationToken);
        return Results.Ok(new NotificationProviderValidationResponse(
            IsValid: result.IsValid,
            ErrorCode: result.ErrorCode,
            ErrorMessage: result.ErrorMessage));
    }

    private static async Task<IResult> TestSendAsync(
        string id,
        NotificationTestSendRequest? request,
        INotificationProviderConfigRepository configRepo,
        INotificationProviderRegistry registry,
        INotificationTemplateRenderer templateRenderer,
        IHitlNotificationActionUrlBuilder actionUrlBuilder,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (request is null || request.Recipient is null)
        {
            return ApiResults.BadRequest("Request body with a recipient is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Recipient.Address))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["recipient.address"] = new[] { "recipient.address is required." },
            });
        }

        var config = await configRepo.GetAsync(id, cancellationToken);
        if (config is null)
        {
            return Results.NotFound();
        }

        if (request.Recipient.Channel != config.Channel)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["recipient.channel"] = new[]
                {
                    $"recipient.channel ({request.Recipient.Channel}) must match provider channel ({config.Channel}).",
                },
            });
        }

        var provider = await registry.GetByIdAsync(id, cancellationToken);
        if (provider is null)
        {
            return Results.Ok(new NotificationTestSendResponse(
                Subject: null,
                Body: string.Empty,
                ActionUrl: string.Empty,
                Delivery: new NotificationTestDeliveryDto(
                    Status: NotificationDeliveryStatus.Failed.ToString(),
                    ProviderMessageId: null,
                    NormalizedDestination: null,
                    ErrorCode: "dispatcher.provider_not_registered",
                    ErrorMessage: $"No active provider available for id '{id}' (archived, disabled, or no factory for channel {config.Channel}).")));
        }

        // Synthetic event used for both the action URL and any template binding. Real saga
        // ids — they're random GUIDs so they can't collide with anything in the audit log;
        // HitlTaskId is 0 to make it obvious in any inadvertent log line that this came from
        // the test path.
        var nowUtc = DateTimeOffset.UtcNow;
        Uri actionUrl;
        try
        {
            actionUrl = actionUrlBuilder.BuildForPendingTask(hitlTaskId: 0, traceId: Guid.NewGuid());
        }
        catch (InvalidOperationException ex)
        {
            // PublicBaseUrl unset — surface the same error the diagnostics endpoint warns about.
            return Results.Ok(new NotificationTestSendResponse(
                Subject: null,
                Body: string.Empty,
                ActionUrl: string.Empty,
                Delivery: new NotificationTestDeliveryDto(
                    Status: NotificationDeliveryStatus.Failed.ToString(),
                    ProviderMessageId: null,
                    NormalizedDestination: null,
                    ErrorCode: "dispatcher.action_url_unconfigured",
                    ErrorMessage: ex.Message)));
        }

        var syntheticEvent = new HitlTaskPendingEvent(
            EventId: Guid.NewGuid(),
            OccurredAtUtc: nowUtc,
            ActionUrl: actionUrl,
            Severity: NotificationSeverity.Normal,
            HitlTaskId: 0,
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            NodeId: Guid.NewGuid(),
            WorkflowKey: "test-send",
            WorkflowVersion: 0,
            AgentKey: "test-send",
            AgentVersion: 0,
            HitlTaskCreatedAtUtc: nowUtc,
            InputPreview: "(test-send synthetic input)",
            InputRef: null,
            SubflowPath: null);

        var recipient = new NotificationRecipient(
            request.Recipient.Channel,
            request.Recipient.Address,
            request.Recipient.DisplayName);

        // The renderer wants a route — synthesise an in-memory one so the renderer can resolve
        // the template ref. Route id is deterministic + obvious so anything that surfaces in
        // logs doesn't look like a real configured route.
        var syntheticRouteId = $"test-send/{id}";
        NotificationTemplateRef templateRef;
        NotificationMessage message;
        try
        {
            if (request.Template is not null
                && !string.IsNullOrWhiteSpace(request.Template.TemplateId)
                && request.Template.Version > 0)
            {
                templateRef = new NotificationTemplateRef(request.Template.TemplateId, request.Template.Version);
                var syntheticRoute = new NotificationRoute(
                    RouteId: syntheticRouteId,
                    EventKind: syntheticEvent.Kind,
                    ProviderId: id,
                    Recipients: new[] { recipient },
                    Template: templateRef,
                    MinimumSeverity: NotificationSeverity.Info,
                    Enabled: true);
                message = await templateRenderer.RenderAsync(syntheticEvent, syntheticRoute, new[] { recipient }, cancellationToken);
            }
            else
            {
                // No template: synthesise a minimal "test notification" body so admins can
                // verify provider + destination + creds before any templates are seeded.
                templateRef = new NotificationTemplateRef("test-send/builtin", 1);
                message = new NotificationMessage(
                    EventId: syntheticEvent.EventId,
                    EventKind: syntheticEvent.Kind,
                    Channel: config.Channel,
                    Recipients: new[] { recipient },
                    Body: $"[CodeFlow] Test notification at {nowUtc:O}. Open: {actionUrl}",
                    ActionUrl: actionUrl,
                    Severity: syntheticEvent.Severity,
                    Subject: $"[CodeFlow] Test notification ({config.Channel})",
                    Template: templateRef);
            }
        }
        catch (NotificationTemplateNotFoundException ex)
        {
            return Results.Ok(new NotificationTestSendResponse(
                Subject: null,
                Body: string.Empty,
                ActionUrl: actionUrl.ToString(),
                Delivery: new NotificationTestDeliveryDto(
                    Status: NotificationDeliveryStatus.Failed.ToString(),
                    ProviderMessageId: null,
                    NormalizedDestination: null,
                    ErrorCode: "dispatcher.template_not_found",
                    ErrorMessage: ex.Message)));
        }
        catch (Exception ex)
        {
            return Results.Ok(new NotificationTestSendResponse(
                Subject: null,
                Body: string.Empty,
                ActionUrl: actionUrl.ToString(),
                Delivery: new NotificationTestDeliveryDto(
                    Status: NotificationDeliveryStatus.Failed.ToString(),
                    ProviderMessageId: null,
                    NormalizedDestination: null,
                    ErrorCode: "dispatcher.template_render_failed",
                    ErrorMessage: ex.Message)));
        }

        // Send via the resolved provider directly. Bypasses the dispatcher so this never
        // writes an audit row; the synthetic event id keeps it isolated from real fan-outs
        // even if it ever did.
        var route = new NotificationRoute(
            RouteId: syntheticRouteId,
            EventKind: syntheticEvent.Kind,
            ProviderId: id,
            Recipients: new[] { recipient },
            Template: templateRef,
            MinimumSeverity: NotificationSeverity.Info,
            Enabled: true);

        NotificationDeliveryResult delivery;
        try
        {
            delivery = await provider.SendAsync(message, route, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Provider impls are supposed to catch transport failures and return Failed; a
            // leak here means a bug or unmocked exception path — surface it as Failed so the
            // admin still sees something actionable.
            loggerFactory.CreateLogger("NotificationsEndpoints.TestSend").LogError(ex,
                "Provider '{ProviderId}' threw during test-send.", id);
            delivery = new NotificationDeliveryResult(
                EventId: syntheticEvent.EventId,
                RouteId: syntheticRouteId,
                ProviderId: id,
                Status: NotificationDeliveryStatus.Failed,
                AttemptedAtUtc: nowUtc,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                AttemptNumber: 1,
                NormalizedDestination: recipient.Address,
                ProviderMessageId: null,
                ErrorCode: "dispatcher.provider_threw",
                ErrorMessage: ex.Message);
        }

        return Results.Ok(new NotificationTestSendResponse(
            Subject: message.Subject,
            Body: message.Body,
            ActionUrl: message.ActionUrl.ToString(),
            Delivery: new NotificationTestDeliveryDto(
                Status: delivery.Status.ToString(),
                ProviderMessageId: delivery.ProviderMessageId,
                NormalizedDestination: delivery.NormalizedDestination,
                ErrorCode: delivery.ErrorCode,
                ErrorMessage: delivery.ErrorMessage)));
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
