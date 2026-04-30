namespace CodeFlow.Persistence.Authority;

public interface IAgentInvocationAuthorityRepository
{
    Task<IReadOnlyList<AgentInvocationAuthorityEntity>> ListByTraceAsync(
        Guid traceId,
        CancellationToken cancellationToken = default);

    Task<AgentInvocationAuthorityEntity?> GetByRoundAsync(
        Guid traceId,
        Guid roundId,
        CancellationToken cancellationToken = default);
}
