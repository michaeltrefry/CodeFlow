using CodeFlow.Contracts.Notifications;

namespace CodeFlow.Persistence.Notifications;

/// <summary>
/// Read-shape for a stored notification provider configuration. <see cref="HasCredential"/>
/// signals whether an encrypted credential exists without exposing the secret itself; callers
/// that actually need the plaintext go through
/// <see cref="INotificationProviderConfigRepository.GetWithDecryptedCredentialAsync"/>.
/// </summary>
public sealed record NotificationProviderConfig(
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

/// <summary>
/// Decrypted view returned only by
/// <see cref="INotificationProviderConfigRepository.GetWithDecryptedCredentialAsync"/>. Never
/// returned from list/get endpoints — keep callers narrow to the dispatcher + provider
/// validation paths.
/// </summary>
public sealed record NotificationProviderConfigWithCredential(
    NotificationProviderConfig Config,
    string? PlaintextCredential);

/// <summary>Upsert payload for a provider configuration. Pair with <see cref="NotificationProviderCredentialUpdate"/>.</summary>
public sealed record NotificationProviderUpsert(
    string Id,
    string DisplayName,
    NotificationChannel Channel,
    string? EndpointUrl,
    string? FromAddress,
    NotificationProviderCredentialUpdate Credential,
    string? AdditionalConfigJson,
    bool Enabled,
    string? UpdatedBy);

/// <summary>Tri-state credential update. Mirrors <c>LlmProviderTokenUpdate</c>'s pattern.</summary>
public sealed record NotificationProviderCredentialUpdate(
    NotificationProviderCredentialAction Action,
    string? Plaintext);

public enum NotificationProviderCredentialAction
{
    Preserve = 0,
    Replace = 1,
    Clear = 2
}
