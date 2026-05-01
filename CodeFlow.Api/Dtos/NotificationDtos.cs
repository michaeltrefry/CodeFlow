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
