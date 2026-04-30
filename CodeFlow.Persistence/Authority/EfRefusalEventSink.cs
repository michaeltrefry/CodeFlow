using CodeFlow.Runtime.Authority;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Persistence.Authority;

/// <summary>
/// Append-only EF-backed sink. Each call to <see cref="RecordAsync"/> resolves a fresh
/// <see cref="CodeFlowDbContext"/> from a child scope so refusal recording does not enlist
/// in the caller's tracking context — that keeps refusals durable even if the caller's
/// surrounding unit of work is later rolled back, which matches sc-285's "refusals are
/// evidence, not state" invariant.
/// </summary>
public sealed class EfRefusalEventSink : IRefusalEventSink
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<EfRefusalEventSink> logger;

    public EfRefusalEventSink(
        IServiceScopeFactory scopeFactory,
        ILogger<EfRefusalEventSink> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    public async Task RecordAsync(RefusalEvent refusal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

            db.RefusalEvents.Add(new RefusalEventEntity
            {
                Id = refusal.Id == Guid.Empty ? Guid.NewGuid() : refusal.Id,
                TraceId = refusal.TraceId,
                AssistantConversationId = refusal.AssistantConversationId,
                Stage = refusal.Stage,
                Code = refusal.Code,
                Reason = refusal.Reason,
                Axis = refusal.Axis,
                Path = refusal.Path,
                DetailJson = refusal.DetailJson,
                OccurredAtUtc = refusal.OccurredAt.UtcDateTime
            });

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Refusal recording must never break the calling tool's primary failure flow;
            // the structured payload is already in the ToolResult that reached the LLM.
            logger.LogWarning(
                ex,
                "Failed to persist refusal event {RefusalCode} for trace {TraceId}; original refusal is still surfaced to the caller.",
                refusal.Code,
                refusal.TraceId);
        }
    }
}
