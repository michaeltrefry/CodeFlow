using CodeFlow.Contracts.Notifications;
using CodeFlow.Persistence.Notifications;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Orchestration.Notifications.Providers.Email;

/// <summary>
/// Engine-neutral email provider. Reads <c>FromAddress</c> from the configuration row,
/// validates the rendered message, and delegates the actual send to an
/// <see cref="IEmailDeliveryClient"/> built by <see cref="EmailNotificationProviderFactory"/>
/// (one client per stored config row → one provider instance per row).
/// </summary>
public sealed class EmailNotificationProvider : INotificationProvider
{
    private readonly NotificationProviderConfigWithCredential config;
    private readonly IEmailDeliveryClient deliveryClient;
    private readonly ILogger<EmailNotificationProvider> logger;
    private readonly TimeProvider clock;

    public EmailNotificationProvider(
        NotificationProviderConfigWithCredential config,
        IEmailDeliveryClient deliveryClient,
        ILogger<EmailNotificationProvider> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Config.Channel != NotificationChannel.Email)
        {
            throw new ArgumentException(
                $"EmailNotificationProvider requires an Email channel config; got {config.Config.Channel}.",
                nameof(config));
        }

        this.config = config;
        this.deliveryClient = deliveryClient ?? throw new ArgumentNullException(nameof(deliveryClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.clock = clock ?? TimeProvider.System;
    }

    public string Id => config.Config.Id;

    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<NotificationDeliveryResult> SendAsync(
        NotificationMessage message,
        NotificationRoute route,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(route);

        var attemptedAt = clock.GetUtcNow();
        var recipient = message.Recipients.Count > 0 ? message.Recipients[0] : null;
        var destination = recipient?.Address ?? string.Empty;

        if (string.IsNullOrWhiteSpace(destination))
        {
            return Failed(message, route, attemptedAt, destination,
                "email.missing_recipient",
                "NotificationMessage.Recipients was empty; cannot send email without a To address.");
        }

        if (string.IsNullOrWhiteSpace(config.Config.FromAddress))
        {
            return Failed(message, route, attemptedAt, destination,
                "email.missing_from_address",
                $"Email provider '{Id}' has no FromAddress configured.");
        }

        var request = new EmailRequest(
            FromAddress: config.Config.FromAddress!,
            ToAddress: destination,
            Subject: message.Subject,
            TextBody: message.Body);

        EmailDeliveryOutcome outcome;
        try
        {
            outcome = await deliveryClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Per-engine clients catch their own exceptions; a leak through here means an
            // unexpected bug in the client, not a transport failure. Record as Failed rather
            // than crashing the dispatcher.
            logger.LogError(ex,
                "Email provider '{ProviderId}' delivery client threw unexpectedly for event {EventId}.",
                Id, message.EventId);
            return Failed(message, route, attemptedAt, destination, "email.client_exception", ex.Message);
        }

        if (!outcome.Success)
        {
            return Failed(message, route, attemptedAt, destination,
                outcome.ErrorCode ?? "email.unknown_error",
                outcome.ErrorMessage);
        }

        return new NotificationDeliveryResult(
            EventId: message.EventId,
            RouteId: route.RouteId,
            ProviderId: Id,
            Status: NotificationDeliveryStatus.Sent,
            AttemptedAtUtc: attemptedAt,
            CompletedAtUtc: clock.GetUtcNow(),
            AttemptNumber: 1,
            NormalizedDestination: destination,
            ProviderMessageId: outcome.ProviderMessageId);
    }

    public async Task<ProviderValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        // v1: validate the surface shape only — that the from-address is set and the engine
        // settings parsed cleanly (the factory already exercised that path; if we got here
        // the parse succeeded). A real "test send" path lands in sc-58.
        if (string.IsNullOrWhiteSpace(config.Config.FromAddress))
        {
            return ProviderValidationResult.Invalid("email.missing_from_address",
                $"Email provider '{Id}' has no FromAddress configured.");
        }

        if (deliveryClient is IEmailDeliveryClientValidator validator)
        {
            return await validator.ValidateAsync(cancellationToken);
        }

        return ProviderValidationResult.Valid();
    }

    private NotificationDeliveryResult Failed(
        NotificationMessage message,
        NotificationRoute route,
        DateTimeOffset attemptedAt,
        string destination,
        string errorCode,
        string? errorMessage)
    {
        logger.LogWarning(
            "Email provider '{ProviderId}' delivery failed for event {EventId} → {Destination}: {ErrorCode} {ErrorMessage}",
            Id, message.EventId, destination, errorCode, errorMessage);

        return new NotificationDeliveryResult(
            EventId: message.EventId,
            RouteId: route.RouteId,
            ProviderId: Id,
            Status: NotificationDeliveryStatus.Failed,
            AttemptedAtUtc: attemptedAt,
            CompletedAtUtc: clock.GetUtcNow(),
            AttemptNumber: 1,
            NormalizedDestination: destination,
            ProviderMessageId: null,
            ErrorCode: errorCode,
            ErrorMessage: errorMessage);
    }
}

/// <summary>
/// Optional companion contract a delivery client can implement when it knows how to
/// independently verify its own configuration (e.g. SES `GetAccount` API, SMTP NOOP). The
/// provider's <see cref="EmailNotificationProvider.ValidateAsync"/> delegates here when the
/// client supports it. Engine clients without a meaningful pre-flight just don't implement it.
/// </summary>
public interface IEmailDeliveryClientValidator
{
    Task<ProviderValidationResult> ValidateAsync(CancellationToken cancellationToken = default);
}
