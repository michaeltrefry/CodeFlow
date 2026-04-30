using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence.Notifications;

public sealed class NotificationTemplateRepository(CodeFlowDbContext dbContext) : INotificationTemplateRepository
{
    public async Task<IReadOnlyList<NotificationTemplate>> ListVersionsAsync(
        string templateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);

        var entities = await dbContext.NotificationTemplates
            .AsNoTracking()
            .Where(t => t.TemplateId == templateId)
            .OrderByDescending(t => t.Version)
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task<NotificationTemplate?> GetAsync(
        string templateId,
        int version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);

        var entity = await dbContext.NotificationTemplates
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.TemplateId == templateId && t.Version == version, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<NotificationTemplate?> GetLatestAsync(
        string templateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);

        var entity = await dbContext.NotificationTemplates
            .AsNoTracking()
            .Where(t => t.TemplateId == templateId)
            .OrderByDescending(t => t.Version)
            .FirstOrDefaultAsync(cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<NotificationTemplate> PublishAsync(
        NotificationTemplateUpsert upsert,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upsert);
        ArgumentException.ThrowIfNullOrWhiteSpace(upsert.TemplateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(upsert.BodyTemplate);

        var latest = await dbContext.NotificationTemplates
            .AsNoTracking()
            .Where(t => t.TemplateId == upsert.TemplateId)
            .OrderByDescending(t => t.Version)
            .FirstOrDefaultAsync(cancellationToken);

        // Skip the insert when the latest version already matches every field — keeps version
        // history meaningful (one new row per real edit) rather than churning on save-no-op.
        if (latest is not null
            && latest.EventKind == upsert.EventKind
            && latest.Channel == upsert.Channel
            && string.Equals(latest.SubjectTemplate, upsert.SubjectTemplate, StringComparison.Ordinal)
            && string.Equals(latest.BodyTemplate, upsert.BodyTemplate, StringComparison.Ordinal))
        {
            return Map(latest);
        }

        var now = DateTime.UtcNow;
        var nextVersion = (latest?.Version ?? 0) + 1;

        var entity = new NotificationTemplateEntity
        {
            TemplateId = upsert.TemplateId,
            Version = nextVersion,
            EventKind = upsert.EventKind,
            Channel = upsert.Channel,
            SubjectTemplate = upsert.SubjectTemplate,
            BodyTemplate = upsert.BodyTemplate,
            CreatedAtUtc = now,
            CreatedBy = upsert.UpdatedBy,
            UpdatedAtUtc = now,
            UpdatedBy = upsert.UpdatedBy,
        };

        dbContext.NotificationTemplates.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Map(entity);
    }

    private static NotificationTemplate Map(NotificationTemplateEntity entity) => new(
        TemplateId: entity.TemplateId,
        Version: entity.Version,
        EventKind: entity.EventKind,
        Channel: entity.Channel,
        SubjectTemplate: entity.SubjectTemplate,
        BodyTemplate: entity.BodyTemplate,
        CreatedAtUtc: DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc),
        CreatedBy: entity.CreatedBy,
        UpdatedAtUtc: DateTime.SpecifyKind(entity.UpdatedAtUtc, DateTimeKind.Utc),
        UpdatedBy: entity.UpdatedBy);
}
