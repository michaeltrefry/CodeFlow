using CodeFlow.Contracts.Notifications;
using CodeFlow.Persistence.Notifications;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Orchestration.Notifications;

/// <summary>
/// Default <see cref="INotificationDispatcher"/>. Pipeline per call:
/// <list type="number">
///   <item>Resolve enabled routes for <see cref="INotificationEvent.Kind"/>.</item>
///   <item>Filter routes whose <see cref="NotificationRoute.MinimumSeverity"/> exceeds the event severity.</item>
///   <item>Resolve the route's provider; missing → record <see cref="NotificationDeliveryStatus.Failed"/>.</item>
///   <item>Per recipient, dedupe via
///     <see cref="INotificationDeliveryAttemptRepository.LatestForDestinationAsync"/>; if a
///     prior <see cref="NotificationDeliveryStatus.Sent"/> exists, record
///     <see cref="NotificationDeliveryStatus.Skipped"/> and move on.</item>
///   <item>Render the route's template once per route; on a render failure, record one Failed
///     row per recipient and skip the route.</item>
///   <item>Send to the provider with a per-recipient try/catch — provider exceptions become
///     Failed audit rows so one bad provider can't break the rest of the fan-out.</item>
/// </list>
/// Failures never propagate to the caller; that lets sc-53 publish HITL-pending events
/// without blocking task creation.
/// </summary>
public sealed class NotificationDispatcher(
    INotificationRouteRepository routeRepository,
    INotificationProviderRegistry providerRegistry,
    INotificationTemplateRenderer templateRenderer,
    INotificationDeliveryAttemptRepository attemptRepository,
    ILogger<NotificationDispatcher> logger,
    TimeProvider? timeProvider = null)
    : INotificationDispatcher
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<NotificationDeliveryResult>> DispatchAsync(
        INotificationEvent notificationEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notificationEvent);

        var routes = await routeRepository.ListByEventKindAsync(notificationEvent.Kind, cancellationToken);
        if (routes.Count == 0)
        {
            return Array.Empty<NotificationDeliveryResult>();
        }

        var results = new List<NotificationDeliveryResult>();
        foreach (var route in routes)
        {
            if (notificationEvent.Severity < route.MinimumSeverity)
            {
                logger.LogDebug(
                    "Skipping route {RouteId} for event {EventId}: event severity {EventSeverity} below route minimum {RouteSeverity}.",
                    route.RouteId, notificationEvent.EventId, notificationEvent.Severity, route.MinimumSeverity);
                continue;
            }

            await DispatchRouteAsync(notificationEvent, route, results, cancellationToken);
        }

        return results;
    }

    private async Task DispatchRouteAsync(
        INotificationEvent notificationEvent,
        NotificationRoute route,
        List<NotificationDeliveryResult> results,
        CancellationToken cancellationToken)
    {
        var provider = providerRegistry.GetById(route.ProviderId);
        if (provider is null)
        {
            await RecordOutcomeAsync(
                notificationEvent,
                route,
                route.Recipients,
                attemptNumber: 1,
                status: NotificationDeliveryStatus.Failed,
                completedAtUtc: clock.GetUtcNow(),
                providerMessageId: null,
                errorCode: "dispatcher.provider_not_registered",
                errorMessage: $"No INotificationProvider with id '{route.ProviderId}' is registered.",
                results: results,
                cancellationToken: cancellationToken);
            return;
        }

        // Render once per route. A failed render fails the whole route (every recipient gets a
        // Failed audit row) — recipient identity does not affect template content.
        NotificationMessage renderedRouteMessage;
        try
        {
            renderedRouteMessage = await templateRenderer.RenderAsync(
                notificationEvent,
                route,
                route.Recipients,
                cancellationToken);
        }
        catch (NotificationTemplateNotFoundException ex)
        {
            logger.LogWarning(ex,
                "Template missing for route {RouteId} (event {EventId}); recording Failed for all recipients.",
                route.RouteId, notificationEvent.EventId);
            await RecordOutcomeAsync(
                notificationEvent,
                route,
                route.Recipients,
                attemptNumber: 1,
                status: NotificationDeliveryStatus.Failed,
                completedAtUtc: clock.GetUtcNow(),
                providerMessageId: null,
                errorCode: "dispatcher.template_not_found",
                errorMessage: ex.Message,
                results: results,
                cancellationToken: cancellationToken);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Template render failed for route {RouteId} (event {EventId}); recording Failed for all recipients.",
                route.RouteId, notificationEvent.EventId);
            await RecordOutcomeAsync(
                notificationEvent,
                route,
                route.Recipients,
                attemptNumber: 1,
                status: NotificationDeliveryStatus.Failed,
                completedAtUtc: clock.GetUtcNow(),
                providerMessageId: null,
                errorCode: "dispatcher.template_render_failed",
                errorMessage: ex.Message,
                results: results,
                cancellationToken: cancellationToken);
            return;
        }

        foreach (var recipient in route.Recipients)
        {
            await DispatchRecipientAsync(
                notificationEvent,
                route,
                provider,
                recipient,
                renderedRouteMessage,
                results,
                cancellationToken);
        }
    }

    private async Task DispatchRecipientAsync(
        INotificationEvent notificationEvent,
        NotificationRoute route,
        INotificationProvider provider,
        NotificationRecipient recipient,
        NotificationMessage renderedRouteMessage,
        List<NotificationDeliveryResult> results,
        CancellationToken cancellationToken)
    {
        var destination = recipient.Address;

        // Idempotency: if an earlier attempt already succeeded for this triple, do not call the
        // provider again — record Skipped so audit reflects the decision and the unique index
        // never conflicts. AttemptNumber here is "next" relative to whatever exists.
        var latest = await attemptRepository.LatestForDestinationAsync(
            notificationEvent.EventId,
            provider.Id,
            destination,
            cancellationToken);

        if (latest is { Status: NotificationDeliveryStatus.Sent })
        {
            await RecordOutcomeAsync(
                notificationEvent,
                route,
                [recipient],
                attemptNumber: latest.AttemptNumber + 1,
                status: NotificationDeliveryStatus.Skipped,
                completedAtUtc: clock.GetUtcNow(),
                providerMessageId: null,
                errorCode: "dispatcher.dedupe_already_sent",
                errorMessage: null,
                results: results,
                cancellationToken: cancellationToken);
            return;
        }

        var nextAttemptNumber = (latest?.AttemptNumber ?? 0) + 1;
        var attemptedAt = clock.GetUtcNow();

        var perRecipientMessage = renderedRouteMessage with { Recipients = new[] { recipient } };

        NotificationDeliveryResult providerResult;
        try
        {
            providerResult = await provider.SendAsync(perRecipientMessage, route, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Provider {ProviderId} threw while sending event {EventId} to {Destination}; recording Failed.",
                provider.Id, notificationEvent.EventId, destination);
            await RecordOutcomeAsync(
                notificationEvent,
                route,
                [recipient],
                attemptNumber: nextAttemptNumber,
                status: NotificationDeliveryStatus.Failed,
                completedAtUtc: clock.GetUtcNow(),
                providerMessageId: null,
                errorCode: "dispatcher.provider_threw",
                errorMessage: ex.Message,
                results: results,
                cancellationToken: cancellationToken,
                attemptedAtOverride: attemptedAt);
            return;
        }

        // Normalise the provider's result into a row using the dispatcher's view of route id +
        // event id + attempt number — providers may not know those. Preserve provider-supplied
        // status, message id, error fields, normalized destination.
        var normalizedDestination = string.IsNullOrEmpty(providerResult.NormalizedDestination)
            ? destination
            : providerResult.NormalizedDestination;

        var result = new NotificationDeliveryResult(
            EventId: notificationEvent.EventId,
            RouteId: route.RouteId,
            ProviderId: provider.Id,
            Status: providerResult.Status == NotificationDeliveryStatus.Unknown
                ? NotificationDeliveryStatus.Failed
                : providerResult.Status,
            AttemptedAtUtc: attemptedAt,
            CompletedAtUtc: providerResult.CompletedAtUtc ?? clock.GetUtcNow(),
            AttemptNumber: nextAttemptNumber,
            NormalizedDestination: normalizedDestination,
            ProviderMessageId: providerResult.ProviderMessageId,
            ErrorCode: providerResult.ErrorCode,
            ErrorMessage: providerResult.ErrorMessage);

        await PersistAsync(notificationEvent.Kind, result, results, cancellationToken);
    }

    private async Task RecordOutcomeAsync(
        INotificationEvent notificationEvent,
        NotificationRoute route,
        IReadOnlyList<NotificationRecipient> recipients,
        int attemptNumber,
        NotificationDeliveryStatus status,
        DateTimeOffset completedAtUtc,
        string? providerMessageId,
        string? errorCode,
        string? errorMessage,
        List<NotificationDeliveryResult> results,
        CancellationToken cancellationToken,
        DateTimeOffset? attemptedAtOverride = null)
    {
        var attemptedAt = attemptedAtOverride ?? completedAtUtc;
        foreach (var recipient in recipients)
        {
            var result = new NotificationDeliveryResult(
                EventId: notificationEvent.EventId,
                RouteId: route.RouteId,
                ProviderId: route.ProviderId,
                Status: status,
                AttemptedAtUtc: attemptedAt,
                CompletedAtUtc: completedAtUtc,
                AttemptNumber: attemptNumber,
                NormalizedDestination: recipient.Address,
                ProviderMessageId: providerMessageId,
                ErrorCode: errorCode,
                ErrorMessage: errorMessage);

            await PersistAsync(notificationEvent.Kind, result, results, cancellationToken);
        }
    }

    private async Task PersistAsync(
        NotificationEventKind eventKind,
        NotificationDeliveryResult result,
        List<NotificationDeliveryResult> results,
        CancellationToken cancellationToken)
    {
        try
        {
            await attemptRepository.RecordAsync(result, eventKind, cancellationToken);
            results.Add(result);
        }
        catch (Exception ex)
        {
            // Record failures must never propagate — losing audit visibility is bad, but
            // crashing a fire-and-forget dispatcher is worse. Log and continue.
            logger.LogError(ex,
                "Failed to persist delivery attempt for event {EventId} route {RouteId} provider {ProviderId} attempt {AttemptNumber}.",
                result.EventId, result.RouteId, result.ProviderId, result.AttemptNumber);
            results.Add(result);
        }
    }
}
