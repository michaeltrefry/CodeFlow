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
