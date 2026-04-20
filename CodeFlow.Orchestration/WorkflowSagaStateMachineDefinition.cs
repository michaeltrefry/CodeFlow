using CodeFlow.Persistence;
using MassTransit;

namespace CodeFlow.Orchestration;

public sealed class WorkflowSagaStateMachineDefinition
    : SagaDefinition<WorkflowSagaStateEntity>
{
    public WorkflowSagaStateMachineDefinition()
    {
        EndpointName = "workflow-saga";
    }

    protected override void ConfigureSaga(
        IReceiveEndpointConfigurator endpointConfigurator,
        ISagaConfigurator<WorkflowSagaStateEntity> sagaConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(retry => retry.Intervals(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5)));
    }
}
