using CodeFlow.Contracts;
using CodeFlow.Persistence;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CodeFlow.Orchestration;

public sealed class WorkflowSagaStateMachine : MassTransitStateMachine<WorkflowSagaStateEntity>
{
    public const string PendingTransitionCompleted = "Completed";
    public const string PendingTransitionFailed = "Failed";
    public const string PendingTransitionEscalated = "Escalated";

    public State Running { get; } = null!;

    public State Completed { get; } = null!;

    public State Failed { get; } = null!;

    public State Escalated { get; } = null!;

    public Event<AgentInvokeRequested> AgentInvokeRequestedEvent { get; } = null!;

    public Event<AgentInvocationCompleted> AgentInvocationCompletedEvent { get; } = null!;

    public WorkflowSagaStateMachine()
    {
        InstanceState(saga => saga.CurrentState);

        Event(() => AgentInvokeRequestedEvent, config =>
        {
            config.CorrelateById(context => context.Message.TraceId);
        });

        Event(() => AgentInvocationCompletedEvent, config =>
        {
            config.CorrelateById(context => context.Message.TraceId);
        });

        Initially(
            When(AgentInvokeRequestedEvent)
                .Then(context => ApplyInitialRequest(context.Saga, context.Message))
                .TransitionTo(Running));

        DuringAny(Ignore(AgentInvokeRequestedEvent));

        During(Completed, Ignore(AgentInvocationCompletedEvent));
        During(Failed, Ignore(AgentInvocationCompletedEvent));
        During(Escalated, Ignore(AgentInvocationCompletedEvent));

        During(Running,
            When(AgentInvocationCompletedEvent)
                .ThenAsync(context => RouteCompletionAsync(context))
                .IfElse(
                    context => context.Saga.PendingTransition == PendingTransitionCompleted,
                    completeBinder => completeBinder
                        .Then(ClearPendingTransition)
                        .TransitionTo(Completed),
                    continueBinder => continueBinder
                        .IfElse(
                            context => context.Saga.PendingTransition == PendingTransitionFailed,
                            failBinder => failBinder
                                .Then(ClearPendingTransition)
                                .TransitionTo(Failed),
                            continueElseBinder => continueElseBinder
                                .If(
                                    context => context.Saga.PendingTransition == PendingTransitionEscalated,
                                    escalateBinder => escalateBinder
                                        .Then(ClearPendingTransition)
                                        .TransitionTo(Escalated)))));
    }

    private static WorkflowSagaStateEntity InitializeSagaInstance(AgentInvokeRequested message)
    {
        var instance = new WorkflowSagaStateEntity
        {
            CorrelationId = message.TraceId,
            TraceId = message.TraceId,
            CurrentState = "Initial",
            WorkflowKey = message.WorkflowKey,
            WorkflowVersion = message.WorkflowVersion,
            CurrentAgentKey = message.AgentKey,
            CurrentRoundId = message.RoundId,
            RoundCount = 0,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        instance.PinAgentVersion(message.AgentKey, message.AgentVersion);
        return instance;
    }

    private static void ApplyInitialRequest(WorkflowSagaStateEntity saga, AgentInvokeRequested message)
    {
        saga.TraceId = message.TraceId;
        saga.WorkflowKey = message.WorkflowKey;
        saga.WorkflowVersion = message.WorkflowVersion;
        saga.CurrentAgentKey = message.AgentKey;
        saga.CurrentRoundId = message.RoundId;
        saga.RoundCount = 0;
        saga.PinAgentVersion(message.AgentKey, message.AgentVersion);
        saga.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static void ClearPendingTransition(BehaviorContext<WorkflowSagaStateEntity> context)
    {
        context.Saga.PendingTransition = null;
    }

    private static async Task RouteCompletionAsync(
        BehaviorContext<WorkflowSagaStateEntity, AgentInvocationCompleted> context)
    {
        var saga = context.Saga;
        var message = context.Message;
        var services = context.GetPayload<IServiceProvider>();
        var workflowRepo = services.GetRequiredService<IWorkflowRepository>();
        var agentConfigRepo = services.GetRequiredService<IAgentConfigRepository>();

        var runtimeKind = MapDecisionKind(message.Decision);
        var discriminator = ExtractDiscriminator(message.DecisionPayload);

        saga.AppendDecision(new DecisionRecord(
            AgentKey: message.AgentKey,
            AgentVersion: message.AgentVersion,
            Decision: runtimeKind,
            DecisionPayload: CloneDecisionPayload(message.DecisionPayload),
            RoundId: saga.CurrentRoundId,
            RecordedAtUtc: DateTime.UtcNow));

        var workflow = await workflowRepo.GetAsync(
            saga.WorkflowKey,
            saga.WorkflowVersion,
            context.CancellationToken);

        var edge = workflow.FindNext(message.AgentKey, runtimeKind, discriminator);

        if (edge is null)
        {
            saga.PendingTransition = message.Decision switch
            {
                AgentDecisionKind.Completed => PendingTransitionCompleted,
                AgentDecisionKind.Failed => PendingTransitionFailed,
                _ => PendingTransitionFailed
            };

            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        var targetRoundId = edge.RotatesRound ? Guid.NewGuid() : saga.CurrentRoundId;
        var targetRoundCount = edge.RotatesRound ? 0 : saga.RoundCount + 1;

        if (!edge.RotatesRound && targetRoundCount >= workflow.MaxRoundsPerRound)
        {
            if (!string.IsNullOrWhiteSpace(workflow.EscalationAgentKey))
            {
                await PublishHandoffAsync(
                    context,
                    agentConfigRepo,
                    saga,
                    workflow.EscalationAgentKey,
                    inputRef: message.OutputRef,
                    roundId: saga.CurrentRoundId,
                    roundCount: saga.RoundCount);

                saga.PendingTransition = PendingTransitionEscalated;
            }
            else
            {
                saga.PendingTransition = PendingTransitionFailed;
            }

            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        await PublishHandoffAsync(
            context,
            agentConfigRepo,
            saga,
            edge.ToAgentKey,
            inputRef: message.OutputRef,
            roundId: targetRoundId,
            roundCount: targetRoundCount);

        saga.CurrentAgentKey = edge.ToAgentKey;
        saga.CurrentRoundId = targetRoundId;
        saga.RoundCount = targetRoundCount;
        saga.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static async Task PublishHandoffAsync(
        BehaviorContext<WorkflowSagaStateEntity, AgentInvocationCompleted> context,
        IAgentConfigRepository agentConfigRepo,
        WorkflowSagaStateEntity saga,
        string targetAgentKey,
        Uri inputRef,
        Guid roundId,
        int roundCount)
    {
        var pinnedVersion = saga.GetPinnedVersion(targetAgentKey);

        if (pinnedVersion is null)
        {
            pinnedVersion = await agentConfigRepo.GetLatestVersionAsync(
                targetAgentKey,
                context.CancellationToken);

            saga.PinAgentVersion(targetAgentKey, pinnedVersion.Value);
        }

        await context.Publish(new AgentInvokeRequested(
            TraceId: saga.TraceId,
            RoundId: roundId,
            WorkflowKey: saga.WorkflowKey,
            WorkflowVersion: saga.WorkflowVersion,
            AgentKey: targetAgentKey,
            AgentVersion: pinnedVersion.Value,
            InputRef: inputRef));

        _ = roundCount;
    }

    private static Runtime.AgentDecisionKind MapDecisionKind(AgentDecisionKind kind)
    {
        return kind switch
        {
            AgentDecisionKind.Completed => Runtime.AgentDecisionKind.Completed,
            AgentDecisionKind.Approved => Runtime.AgentDecisionKind.Approved,
            AgentDecisionKind.ApprovedWithActions => Runtime.AgentDecisionKind.ApprovedWithActions,
            AgentDecisionKind.Rejected => Runtime.AgentDecisionKind.Rejected,
            AgentDecisionKind.Failed => Runtime.AgentDecisionKind.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported decision kind.")
        };
    }

    private static JsonElement? ExtractDiscriminator(JsonElement? payload)
    {
        if (payload is null)
        {
            return null;
        }

        if (payload.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (payload.Value.TryGetProperty("payload", out var discriminator))
        {
            return discriminator.Clone();
        }

        return null;
    }

    private static JsonElement? CloneDecisionPayload(JsonElement? payload)
    {
        return payload?.Clone();
    }
}
