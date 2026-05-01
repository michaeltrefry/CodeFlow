using CodeFlow.Contracts.Notifications;

namespace CodeFlow.Api.Dtos;

// --- Provider configuration DTOs ----------------------------------------------------------

public sealed record NotificationProviderResponse(
    string Id,
    string DisplayName,
    NotificationChannel Channel,
    string? EndpointUrl,
    string? FromAddress,
    bool HasCredential,
    string? AdditionalConfigJson,
    bool Enabled,
    bool IsArchived,
    DateTime CreatedAtUtc,
    string? CreatedBy,
    DateTime UpdatedAtUtc,
    string? UpdatedBy);

public sealed record NotificationProviderWriteRequest(
    string DisplayName,
    NotificationChannel Channel,
    string? EndpointUrl,
    string? FromAddress,
    string? AdditionalConfigJson,
    bool Enabled,
    NotificationProviderCredentialUpdateRequest? Credential);

/// <summary>
/// Tri-state credential update on a provider upsert. Mirrors
/// <see cref="LlmProviderTokenActionRequest"/> so the admin UI can reuse the
/// Replace/Preserve/Clear UX.
/// </summary>
public enum NotificationProviderCredentialActionRequest
{
    Preserve = 0,
    Replace = 1,
    Clear = 2,
}

public sealed record NotificationProviderCredentialUpdateRequest(
    NotificationProviderCredentialActionRequest Action,
    string? Value);

// --- Route DTOs ---------------------------------------------------------------------------

public sealed record NotificationRouteResponse(
    string RouteId,
    NotificationEventKind EventKind,
    string ProviderId,
    IReadOnlyList<NotificationRecipientDto> Recipients,
    NotificationTemplateRefDto Template,
    NotificationSeverity MinimumSeverity,
    bool Enabled);

public sealed record NotificationRouteWriteRequest(
    NotificationEventKind EventKind,
    string ProviderId,
    IReadOnlyList<NotificationRecipientDto> Recipients,
    NotificationTemplateRefDto Template,
    NotificationSeverity MinimumSeverity,
    bool Enabled);

public sealed record NotificationRecipientDto(
    NotificationChannel Channel,
    string Address,
    string? DisplayName);

public sealed record NotificationTemplateRefDto(string TemplateId, int Version);

// --- Template DTOs ------------------------------------------------------------------------

public sealed record NotificationTemplateResponse(
    string TemplateId,
    int Version,
    NotificationEventKind EventKind,
    NotificationChannel Channel,
    string? SubjectTemplate,
    string BodyTemplate,
    DateTime CreatedAtUtc,
    string? CreatedBy,
    DateTime UpdatedAtUtc,
    string? UpdatedBy);

// --- Test send (sc-58) --------------------------------------------------------------------

/// <summary>
/// Result of the validate-only path. Mirrors <c>ProviderValidationResult</c> from the
/// contracts so the admin UI can render the same shape it already knows from the provider's
/// own validate path.
/// </summary>
public sealed record NotificationProviderValidationResponse(
    bool IsValid,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// Request body for the test-send path. The admin UI populates <see cref="Recipient"/>;
/// <see cref="Template"/> is optional — when null, the endpoint sends a hardcoded "test
/// notification" body so admins can validate provider + creds + destination even before any
/// templates are seeded.
/// </summary>
public sealed record NotificationTestSendRequest(
    NotificationRecipientDto Recipient,
    NotificationTemplateRefDto? Template);

/// <summary>
/// Response from the test-send path. Combines the rendered outbound message preview with the
/// provider's delivery result so the admin UI can show both "this is what was sent" and "the
/// provider said X".
/// </summary>
public sealed record NotificationTestSendResponse(
    string? Subject,
    string Body,
    string ActionUrl,
    NotificationTestDeliveryDto Delivery);

public sealed record NotificationTestDeliveryDto(
    string Status,
    string? ProviderMessageId,
    string? NormalizedDestination,
    string? ErrorCode,
    string? ErrorMessage);

// --- Diagnostics --------------------------------------------------------------------------

/// <summary>
/// Snapshot of the notification subsystem's runtime configuration. The admin UI uses this to
/// surface a banner when <c>PublicBaseUrl</c> is unset (sc-53), since action URLs cannot be
/// generated without it.
/// </summary>
public sealed record NotificationDiagnosticsResponse(
    string? PublicBaseUrl,
    int ProviderCount,
    int RouteCount,
    bool ActionUrlsConfigured);
