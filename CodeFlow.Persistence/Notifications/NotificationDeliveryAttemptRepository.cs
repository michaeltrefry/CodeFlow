using CodeFlow.Contracts.Notifications;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence.Notifications;

public sealed class NotificationDeliveryAttemptRepository(CodeFlowDbContext dbContext)
    : INotificationDeliveryAttemptRepository
{
    public async Task RecordAsync(
        NotificationDeliveryResult result,
        NotificationEventKind eventKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.RouteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.NormalizedDestination);

        var entity = new NotificationDeliveryAttemptEntity
        {
            EventId = result.EventId,
            EventKind = eventKind,
            RouteId = result.RouteId,
            ProviderId = result.ProviderId,
            Status = result.Status,
            AttemptNumber = result.AttemptNumber,
            AttemptedAtUtc = result.AttemptedAtUtc.UtcDateTime,
            CompletedAtUtc = result.CompletedAtUtc?.UtcDateTime,
            NormalizedDestination = result.NormalizedDestination,
            ProviderMessageId = result.ProviderMessageId,
            ErrorCode = result.ErrorCode,
            ErrorMessage = result.ErrorMessage,
            CreatedAtUtc = DateTime.UtcNow,
        };

        dbContext.NotificationDeliveryAttempts.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationDeliveryResult>> ListByEventIdAsync(
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.NotificationDeliveryAttempts
            .AsNoTracking()
            .Where(a => a.EventId == eventId)
            .OrderBy(a => a.AttemptedAtUtc)
            .ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task<NotificationDeliveryResult?> LatestForDestinationAsync(
        Guid eventId,
        string providerId,
        string normalizedDestination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedDestination);

        var entity = await dbContext.NotificationDeliveryAttempts
            .AsNoTracking()
            .Where(a => a.EventId == eventId
                && a.ProviderId == providerId
                && a.NormalizedDestination == normalizedDestination)
            .OrderByDescending(a => a.AttemptNumber)
            .FirstOrDefaultAsync(cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<NotificationDeliveryAttemptRecord>> ListAsync(
        NotificationDeliveryAttemptListFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Clamp limit defensively here too; the API layer also clamps but the repository is the
        // last line of defence against an unbounded scan if a future caller forgets.
        var limit = filter.Limit <= 0 ? 50 : Math.Min(filter.Limit, 200);

        var query = dbContext.NotificationDeliveryAttempts
            .AsNoTracking()
            .AsQueryable();

        if (filter.EventId is { } eventId)
        {
            query = query.Where(a => a.EventId == eventId);
        }

        if (!string.IsNullOrWhiteSpace(filter.ProviderId))
        {
            query = query.Where(a => a.ProviderId == filter.ProviderId);
        }

        if (!string.IsNullOrWhiteSpace(filter.RouteId))
        {
            query = query.Where(a => a.RouteId == filter.RouteId);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(a => a.Status == status);
        }

        if (filter.SinceUtc is { } since)
        {
            var sinceUtc = since.UtcDateTime;
            query = query.Where(a => a.AttemptedAtUtc >= sinceUtc);
        }

        if (filter.BeforeId is { } beforeId)
        {
            query = query.Where(a => a.Id < beforeId);
        }

        var entities = await query
            .OrderByDescending(a => a.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return entities.Select(MapRecord).ToArray();
    }

    private static NotificationDeliveryAttemptRecord MapRecord(NotificationDeliveryAttemptEntity entity) => new(
        Id: entity.Id,
        EventId: entity.EventId,
        EventKind: entity.EventKind,
        RouteId: entity.RouteId,
        ProviderId: entity.ProviderId,
        Status: entity.Status,
        AttemptNumber: entity.AttemptNumber,
        AttemptedAtUtc: new DateTimeOffset(DateTime.SpecifyKind(entity.AttemptedAtUtc, DateTimeKind.Utc)),
        CompletedAtUtc: entity.CompletedAtUtc is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(entity.CompletedAtUtc.Value, DateTimeKind.Utc)),
        NormalizedDestination: entity.NormalizedDestination,
        ProviderMessageId: entity.ProviderMessageId,
        ErrorCode: entity.ErrorCode,
        ErrorMessage: entity.ErrorMessage,
        CreatedAtUtc: new DateTimeOffset(DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc)));

    private static NotificationDeliveryResult Map(NotificationDeliveryAttemptEntity entity) => new(
        EventId: entity.EventId,
        RouteId: entity.RouteId,
        ProviderId: entity.ProviderId,
        Status: entity.Status,
        AttemptedAtUtc: new DateTimeOffset(DateTime.SpecifyKind(entity.AttemptedAtUtc, DateTimeKind.Utc)),
        CompletedAtUtc: entity.CompletedAtUtc is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(entity.CompletedAtUtc.Value, DateTimeKind.Utc)),
        AttemptNumber: entity.AttemptNumber,
        NormalizedDestination: entity.NormalizedDestination,
        ProviderMessageId: entity.ProviderMessageId,
        ErrorCode: entity.ErrorCode,
        ErrorMessage: entity.ErrorMessage);
}
