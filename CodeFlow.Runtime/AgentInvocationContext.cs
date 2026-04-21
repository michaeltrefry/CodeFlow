namespace CodeFlow.Runtime;

public sealed record AgentInvocationContext(Guid CorrelationId)
{
    public static AgentInvocationContext ForTests(Guid? correlationId = null)
        => new(correlationId ?? Guid.NewGuid());
}
