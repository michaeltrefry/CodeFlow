using CodeFlow.Runtime.Authority;

namespace CodeFlow.Persistence.Authority;

/// <summary>
/// Read-side access to the append-only refusal stream. Write side lives behind
/// <see cref="CodeFlow.Runtime.Authority.IRefusalEventSink"/>.
/// </summary>
public interface IRefusalEventRepository
{
    Task<IReadOnlyList<RefusalEvent>> ListByTraceAsync(
        Guid traceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RefusalEvent>> ListByAssistantConversationAsync(
        Guid assistantConversationId,
        CancellationToken cancellationToken = default);
}
