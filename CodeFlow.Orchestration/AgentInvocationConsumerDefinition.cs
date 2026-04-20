using MassTransit;

namespace CodeFlow.Orchestration;

public sealed class AgentInvocationConsumerDefinition : ConsumerDefinition<AgentInvocationConsumer>
{
    public AgentInvocationConsumerDefinition()
    {
        EndpointName = "agent-invocations";
    }
}
