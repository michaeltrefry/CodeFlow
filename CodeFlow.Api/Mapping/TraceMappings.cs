using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;

namespace CodeFlow.Api.Mapping;

internal static class TraceMappings
{
    public static TraceSummaryDto ToSummaryDto(this WorkflowSagaStateEntity saga) => new(
        TraceId: saga.TraceId,
        WorkflowKey: saga.WorkflowKey,
        WorkflowVersion: saga.WorkflowVersion,
        CurrentState: saga.CurrentState,
        CurrentAgentKey: saga.CurrentAgentKey,
        RoundCount: saga.RoundCount,
        CreatedAtUtc: DateTime.SpecifyKind(saga.CreatedAtUtc, DateTimeKind.Utc),
        UpdatedAtUtc: DateTime.SpecifyKind(saga.UpdatedAtUtc, DateTimeKind.Utc),
        ParentTraceId: saga.ParentTraceId,
        ParentNodeId: saga.ParentNodeId,
        ParentReviewRound: saga.ParentReviewRound,
        ParentReviewMaxRounds: saga.ParentReviewMaxRounds);

    public static HitlTaskDto ToHitlDto(this HitlTaskEntity task) =>
        task.ToHitlDto(originTraceId: null, subflowPath: null);

    public static HitlTaskDto ToHitlDto(
        this HitlTaskEntity task,
        Guid? originTraceId,
        IReadOnlyList<string>? subflowPath) => new(
            Id: task.Id,
            TraceId: task.TraceId,
            RoundId: task.RoundId,
            AgentKey: task.AgentKey,
            AgentVersion: task.AgentVersion,
            InputRef: new Uri(task.InputRef),
            InputPreview: task.InputPreview,
            CreatedAtUtc: DateTime.SpecifyKind(task.CreatedAtUtc, DateTimeKind.Utc),
            State: task.State.ToString(),
            Decision: task.Decision,
            DecidedAtUtc: task.DecidedAtUtc is null ? null : DateTime.SpecifyKind(task.DecidedAtUtc.Value, DateTimeKind.Utc),
            DeciderId: task.DeciderId,
            OriginTraceId: originTraceId,
            SubflowPath: subflowPath);
}
