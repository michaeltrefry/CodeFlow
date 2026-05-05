using CodeFlow.Api.Dtos;
using CodeFlow.Orchestration.DryRun;

namespace CodeFlow.Api.Mapping;

internal static class DryRunMappings
{
    public static DryRunResponse ToResponse(this DryRunResult result) => new(
        State: result.State.ToString(),
        TerminalPort: result.TerminalPort,
        FailureReason: result.FailureReason,
        FinalArtifact: result.FinalArtifact,
        HitlPayload: result.HitlPayload is null
            ? null
            : new DryRunHitlPayloadDto(
                result.HitlPayload.NodeId,
                result.HitlPayload.AgentKey,
                result.HitlPayload.Input,
                result.HitlPayload.OutputTemplate,
                result.HitlPayload.DecisionOutputTemplates,
                result.HitlPayload.RenderedFormPreview,
                result.HitlPayload.RenderError),
        WorkflowVariables: result.WorkflowVariables,
        ContextVariables: result.ContextVariables,
        Events: result.Events.Select(e => e.ToDto()).ToArray());

    public static DryRunEventDto ToDto(this DryRunEvent ev) => new(
        ev.Ordinal,
        ev.Kind.ToString(),
        ev.NodeId,
        ev.NodeKind,
        ev.AgentKey,
        ev.PortName,
        ev.Message,
        ev.InputPreview,
        ev.OutputPreview,
        ev.ReviewRound,
        ev.MaxRounds,
        ev.SubflowDepth,
        ev.SubflowKey,
        ev.SubflowVersion,
        ev.Logs,
        ev.DecisionPayload);
}
