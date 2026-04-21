using CodeFlow.Contracts;
using CodeFlow.Runtime.Workspace;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Host.Workspace;

public sealed class WorkflowTerminatedConsumer : IConsumer<WorkflowTerminated>
{
    private readonly IWorkspaceService workspaceService;
    private readonly ILogger<WorkflowTerminatedConsumer> logger;

    public WorkflowTerminatedConsumer(
        IWorkspaceService workspaceService,
        ILogger<WorkflowTerminatedConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(workspaceService);
        ArgumentNullException.ThrowIfNull(logger);

        this.workspaceService = workspaceService;
        this.logger = logger;
    }

    public async Task Consume(ConsumeContext<WorkflowTerminated> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;
        try
        {
            await workspaceService.ReleaseAsync(message.TraceId, context.CancellationToken);
            logger.LogInformation(
                "workspace.release {CorrelationId} kind={Kind} workflow={WorkflowKey}",
                message.TraceId,
                message.Kind,
                message.WorkflowKey);
        }
        catch (Exception ex)
        {
            // Never let a cleanup failure rollback the saga's terminal transition —
            // log and swallow so the message is acked.
            logger.LogWarning(
                ex,
                "workspace.release failed {CorrelationId} kind={Kind} workflow={WorkflowKey}",
                message.TraceId,
                message.Kind,
                message.WorkflowKey);
        }
    }
}
