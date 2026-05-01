namespace CodeFlow.Persistence.Notifications;

public interface INotificationTemplateRepository
{
    Task<IReadOnlyList<NotificationTemplate>> ListVersionsAsync(
        string templateId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the latest version of every distinct <c>template_id</c>. Backs the admin
    /// editor's full-inventory listing (sc-63) — versions are surfaced through
    /// <see cref="ListVersionsAsync"/> when an admin drills into a single template.
    /// </summary>
    Task<IReadOnlyList<NotificationTemplate>> ListLatestPerTemplateAsync(
        CancellationToken cancellationToken = default);

    Task<NotificationTemplate?> GetAsync(
        string templateId,
        int version,
        CancellationToken cancellationToken = default);

    Task<NotificationTemplate?> GetLatestAsync(
        string templateId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new version of <paramref name="upsert"/>.<see cref="NotificationTemplateUpsert.TemplateId"/>.
    /// Templates are immutable per (id, version) — calling this with content that matches the
    /// current latest version is a no-op; otherwise a new version row is created and returned.
    /// </summary>
    Task<NotificationTemplate> PublishAsync(
        NotificationTemplateUpsert upsert,
        CancellationToken cancellationToken = default);
}
