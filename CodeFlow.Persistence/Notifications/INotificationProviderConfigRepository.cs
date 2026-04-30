namespace CodeFlow.Persistence.Notifications;

public interface INotificationProviderConfigRepository
{
    Task<IReadOnlyList<NotificationProviderConfig>> ListAsync(
        bool includeArchived = false,
        CancellationToken cancellationToken = default);

    Task<NotificationProviderConfig?> GetAsync(
        string providerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the provider configuration with its decrypted credential. Reserved for runtime
    /// dispatch + admin "test connection" paths — never returned from public read APIs.
    /// </summary>
    Task<NotificationProviderConfigWithCredential?> GetWithDecryptedCredentialAsync(
        string providerId,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(NotificationProviderUpsert upsert, CancellationToken cancellationToken = default);

    /// <summary>Soft-delete: sets <c>is_archived = true</c> and <c>enabled = false</c>.</summary>
    Task ArchiveAsync(string providerId, string? archivedBy, CancellationToken cancellationToken = default);
}
