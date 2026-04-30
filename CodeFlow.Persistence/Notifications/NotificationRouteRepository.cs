using System.Text.Json;
using CodeFlow.Contracts.Notifications;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence.Notifications;

public sealed class NotificationRouteRepository(CodeFlowDbContext dbContext) : INotificationRouteRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<NotificationRoute>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.NotificationRoutes
            .AsNoTracking()
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task<NotificationRoute?> GetAsync(string routeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);

        var entity = await dbContext.NotificationRoutes
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == routeId, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<NotificationRoute>> ListByEventKindAsync(
        NotificationEventKind eventKind,
        CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.NotificationRoutes
            .AsNoTracking()
            .Where(r => r.EventKind == eventKind && r.Enabled)
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task UpsertAsync(NotificationRoute route, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(route.Recipients);
        ArgumentNullException.ThrowIfNull(route.Template);
        ArgumentException.ThrowIfNullOrWhiteSpace(route.RouteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(route.ProviderId);

        if (route.Recipients.Count == 0)
        {
            throw new ArgumentException("Route must include at least one recipient.", nameof(route));
        }

        var recipientsJson = JsonSerializer.Serialize(route.Recipients, SerializerOptions);
        var now = DateTime.UtcNow;

        var entity = await dbContext.NotificationRoutes
            .SingleOrDefaultAsync(r => r.Id == route.RouteId, cancellationToken);

        if (entity is null)
        {
            dbContext.NotificationRoutes.Add(new NotificationRouteEntity
            {
                Id = route.RouteId,
                EventKind = route.EventKind,
                ProviderId = route.ProviderId,
                TemplateId = route.Template.TemplateId,
                TemplateVersion = route.Template.Version,
                RecipientsJson = recipientsJson,
                MinimumSeverity = route.MinimumSeverity,
                Enabled = route.Enabled,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }
        else
        {
            entity.EventKind = route.EventKind;
            entity.ProviderId = route.ProviderId;
            entity.TemplateId = route.Template.TemplateId;
            entity.TemplateVersion = route.Template.Version;
            entity.RecipientsJson = recipientsJson;
            entity.MinimumSeverity = route.MinimumSeverity;
            entity.Enabled = route.Enabled;
            entity.UpdatedAtUtc = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string routeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);

        var entity = await dbContext.NotificationRoutes
            .SingleOrDefaultAsync(r => r.Id == routeId, cancellationToken);

        if (entity is null)
        {
            return;
        }

        dbContext.NotificationRoutes.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static NotificationRoute Map(NotificationRouteEntity entity)
    {
        var recipients = JsonSerializer.Deserialize<List<NotificationRecipient>>(
            entity.RecipientsJson,
            SerializerOptions) ?? new List<NotificationRecipient>();

        return new NotificationRoute(
            RouteId: entity.Id,
            EventKind: entity.EventKind,
            ProviderId: entity.ProviderId,
            Recipients: recipients,
            Template: new NotificationTemplateRef(entity.TemplateId, entity.TemplateVersion),
            MinimumSeverity: entity.MinimumSeverity,
            Enabled: entity.Enabled);
    }
}
