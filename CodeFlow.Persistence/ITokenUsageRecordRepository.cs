namespace CodeFlow.Persistence;

public interface ITokenUsageRecordRepository
{
    Task AddAsync(TokenUsageRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TokenUsageRecord>> ListByTraceAsync(Guid traceId, CancellationToken cancellationToken = default);
}
