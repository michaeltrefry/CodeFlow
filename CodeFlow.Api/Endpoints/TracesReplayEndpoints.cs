using System.Text;
using System.Text.Json.Nodes;
using CodeFlow.Api.Dtos;
using CodeFlow.Orchestration.DryRun;
using CodeFlow.Orchestration.Replay;
using CodeFlow.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Endpoints;

/// <summary>
/// Implementation of <c>POST /api/traces/{id}/replay</c>. Lifts a real saga's recorded decisions
/// into the dry-run executor's mock dictionary, applies author-supplied edits + additional mocks,
/// runs the dry-run, and returns the resulting event timeline alongside a drift report.
/// </summary>
public static class TracesReplayEndpoints
{
    private const int MaxSubflowDepth = CodeFlow.Orchestration.WorkflowSagaStateMachine.MaxSubflowDepth;
    private const string QueueExhaustedPrefix = "No mock response queued for agent '";

    public static async Task<IResult> ReplayTraceAsync(
        Guid id,
        ReplayRequest? body,
        CodeFlowDbContext dbContext,
        IWorkflowRepository workflowRepository,
        IArtifactStore artifactStore,
        DryRunExecutor executor,
        CancellationToken cancellationToken)
    {
        var rootSaga = await dbContext.WorkflowSagas
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TraceId == id, cancellationToken);
        if (rootSaga is null)
        {
            return Results.NotFound();
        }

        var subtreeSagas = await CollectSubtreeSagasAsync(dbContext, rootSaga, cancellationToken);
        var sagaCorrelationIds = subtreeSagas.Select(s => s.CorrelationId).ToArray();

        var decisions = await dbContext.WorkflowSagaDecisions
            .AsNoTracking()
            .Where(d => sagaCorrelationIds.Contains(d.SagaCorrelationId))
            .OrderBy(d => d.SagaCorrelationId)
            .ThenBy(d => d.Ordinal)
            .ToListAsync(cancellationToken);

        var originalWorkflow = await workflowRepository.GetAsync(
            rootSaga.WorkflowKey, rootSaga.WorkflowVersion, cancellationToken);
        if (originalWorkflow is null)
        {
            return Results.Problem(
                title: "Original workflow definition is no longer available.",
                detail: $"Workflow '{rootSaga.WorkflowKey}' v{rootSaga.WorkflowVersion} could not be loaded.",
                statusCode: StatusCodes.Status409Conflict);
        }

        Workflow targetWorkflow;
        var versionOverride = body?.WorkflowVersionOverride;
        if (versionOverride is int v && v != rootSaga.WorkflowVersion)
        {
            var overridden = await workflowRepository.GetAsync(rootSaga.WorkflowKey, v, cancellationToken);
            if (overridden is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["workflowVersionOverride"] =
                    [
                        $"Workflow '{rootSaga.WorkflowKey}' v{v} not found.",
                    ],
                });
            }
            targetWorkflow = overridden;
        }
        else
        {
            targetWorkflow = originalWorkflow;
        }

        ReplayMockBundle bundle;
        try
        {
            bundle = await ReplayMockExtractor.ExtractAsync(
                rootSaga, subtreeSagas, decisions, artifactStore, cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            return Results.Problem(
                title: "An artifact referenced by this trace could not be read.",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex) when (ex.ParamName == "uri")
        {
            // FileSystemArtifactStore rejects URIs outside the configured root with
            // ArgumentException — surfaces here when an old trace's recorded OutputRef points at a
            // path the current artifact-store config can no longer resolve (storage migration,
            // retention sweep that moved the root, etc.). Same author-facing meaning as a missing
            // file: the recording can't be replayed.
            return Results.Problem(
                title: "An artifact referenced by this trace is no longer resolvable.",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }

        var drift = ReplayDriftDetector.Detect(
            originalWorkflow,
            rootSaga.GetPinnedAgentVersions(),
            targetWorkflow,
            bundle.Decisions);

        var force = body?.Force ?? false;
        if (drift.Level == DriftLevel.Hard && !force)
        {
            return Results.Json(
                new ReplayResponse(
                    OriginalTraceId: id,
                    ReplayState: "DriftRefused",
                    ReplayTerminalPort: null,
                    FailureReason: "Hard drift detected; pass force=true to opt into a best-effort replay.",
                    FailureCode: "drift_hard_refused",
                    ExhaustedAgent: null,
                    Decisions: bundle.Decisions.Select(MapDecisionRef).ToArray(),
                    ReplayEvents: Array.Empty<DryRunEventDto>(),
                    HitlPayload: null,
                    Drift: new ReplayDriftDto(drift.Level.ToString(), drift.Warnings)),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var edits = body?.Edits?.Select(e => new ReplayEdit(
            AgentKey: e.AgentKey,
            Ordinal: e.Ordinal,
            Decision: e.Decision,
            Output: e.Output,
            Payload: e.Payload)).ToArray();

        IReadOnlyDictionary<string, IReadOnlyList<DryRunMockResponse>>? additionalMocks = null;
        if (body?.AdditionalMocks is { Count: > 0 } additional)
        {
            additionalMocks = additional.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<DryRunMockResponse>)kv.Value
                    .Where(r => r is not null)
                    .Select(r => new DryRunMockResponse(r.Decision, r.Output, r.Payload))
                    .ToArray(),
                StringComparer.Ordinal);
        }

        var portIndex = await ReplayEditsApplicator.BuildPortIndexAsync(
            targetWorkflow, workflowRepository, cancellationToken);
        var workflowLabel = $"'{targetWorkflow.Key}' v{targetWorkflow.Version}";
        var applied = ReplayEditsApplicator.Apply(
            bundle.Mocks, edits, additionalMocks, portIndex, workflowLabel);
        if (applied.ValidationErrors.Count > 0)
        {
            var grouped = applied.ValidationErrors
                .Select((message, idx) => new { Key = $"edits[{idx}]", message })
                .GroupBy(x => x.Key, x => x.message)
                .ToDictionary(g => g.Key, g => g.ToArray());
            return Results.ValidationProblem(grouped);
        }

        var startingInput = await ResolveStartingInputAsync(
            artifactStore, decisions, cancellationToken);

        var dryRunRequest = new DryRunRequest(
            WorkflowKey: targetWorkflow.Key,
            WorkflowVersion: targetWorkflow.Version,
            StartingInput: startingInput,
            MockResponses: applied.Mocks);

        var result = await executor.ExecuteAsync(dryRunRequest, cancellationToken);

        var (failureCode, exhaustedAgent) = ClassifyFailure(result, applied.Mocks);
        var replayState = exhaustedAgent is null
            ? result.State.ToString()
            : "Failed";
        return Results.Ok(new ReplayResponse(
            OriginalTraceId: id,
            ReplayState: replayState,
            ReplayTerminalPort: result.TerminalPort,
            FailureReason: result.FailureReason,
            FailureCode: failureCode,
            ExhaustedAgent: exhaustedAgent,
            Decisions: bundle.Decisions.Select(MapDecisionRef).ToArray(),
            ReplayEvents: result.Events.Select(MapEvent).ToArray(),
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
            Drift: new ReplayDriftDto(drift.Level.ToString(), drift.Warnings)));
    }

    private static (string? FailureCode, ReplayExhaustedAgentDto? Exhausted) ClassifyFailure(
        DryRunResult result,
        IReadOnlyDictionary<string, IReadOnlyList<DryRunMockResponse>> mocks)
    {
        if (result.State != DryRunTerminalState.Failed)
        {
            return (null, null);
        }

        var reason = result.FailureReason;
        if (string.IsNullOrEmpty(reason) || !reason.StartsWith(QueueExhaustedPrefix, StringComparison.Ordinal))
        {
            return (null, null);
        }

        var openQuote = QueueExhaustedPrefix.Length;
        var closeQuote = reason.IndexOf('\'', openQuote);
        if (closeQuote <= openQuote)
        {
            return ("queue_exhausted", null);
        }

        var agentKey = reason.Substring(openQuote, closeQuote - openQuote);
        var recorded = mocks.TryGetValue(agentKey, out var queue) ? queue.Count : 0;
        return ("queue_exhausted", new ReplayExhaustedAgentDto(agentKey, recorded));
    }

    private static async Task<string?> ResolveStartingInputAsync(
        IArtifactStore artifactStore,
        IReadOnlyList<WorkflowSagaDecisionEntity> decisions,
        CancellationToken cancellationToken)
    {
        var firstRealDecision = decisions
            .OrderBy(d => d.SagaCorrelationId)
            .ThenBy(d => d.Ordinal)
            .FirstOrDefault(d => !ReplayMockExtractor.IsSyntheticSubflowAgentKey(d.AgentKey));
        if (firstRealDecision?.InputRef is null
            || !Uri.TryCreate(firstRealDecision.InputRef, UriKind.Absolute, out var uri))
        {
            return null;
        }

        try
        {
            await using var stream = await artifactStore.ReadAsync(uri, cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<WorkflowSagaStateEntity>> CollectSubtreeSagasAsync(
        CodeFlowDbContext dbContext,
        WorkflowSagaStateEntity root,
        CancellationToken cancellationToken)
    {
        var all = new List<WorkflowSagaStateEntity> { root };
        var currentLevel = new List<Guid> { root.TraceId };

        for (var level = 0; level < MaxSubflowDepth; level++)
        {
            if (currentLevel.Count == 0)
            {
                break;
            }

            var parents = currentLevel;
            var children = await dbContext.WorkflowSagas
                .AsNoTracking()
                .Where(s => s.ParentTraceId != null && parents.Contains(s.ParentTraceId!.Value))
                .ToListAsync(cancellationToken);

            if (children.Count == 0)
            {
                break;
            }

            all.AddRange(children);
            currentLevel = children.Select(s => s.TraceId).ToList();
        }

        return all;
    }

    private static RecordedDecisionRefDto MapDecisionRef(RecordedDecisionRef d) => new(
        AgentKey: d.AgentKey,
        OrdinalPerAgent: d.OrdinalPerAgent,
        SagaCorrelationId: d.SagaCorrelationId,
        SagaOrdinal: d.SagaOrdinal,
        NodeId: d.NodeId,
        RoundId: d.RoundId,
        OriginalDecision: d.OriginalDecision);

    private static DryRunEventDto MapEvent(DryRunEvent ev) => new(
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
