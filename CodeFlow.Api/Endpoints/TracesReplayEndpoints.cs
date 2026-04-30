using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Api.Dtos;
using CodeFlow.Orchestration.DryRun;
using CodeFlow.Orchestration.Replay;
using CodeFlow.Orchestration.Replay.Admission;
using CodeFlow.Persistence;
using CodeFlow.Persistence.Replay;
using CodeFlow.Runtime.Authority;
using CodeFlow.Runtime.Authority.Admission;
using CodeFlow.Runtime.Authority.Preflight;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
        ReplayRequestValidator replayValidator,
        IRefusalEventSink refusalSink,
        IIntentClarityAssessor preflightAssessor,
        IOptions<PreflightOptions> preflightOptions,
        CancellationToken cancellationToken)
    {
        var rootSaga = await dbContext.WorkflowSagas
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TraceId == id, cancellationToken);
        if (rootSaga is null)
        {
            return Results.NotFound();
        }

        // sc-274 phase 1: ambiguity preflight runs BEFORE admission. Refusing here saves the
        // dry-run, the admission validator, and the artifact reads — and gives the author a
        // focused list of clarifying questions instead of a generic "edit didn't move the
        // needle" downstream surprise. Disabled-mode falls through; assessor failure is
        // observability-only and never blocks the primary flow.
        if (preflightOptions.Value.Enabled)
        {
            var preflightInput = new ReplayEditPreflightInput(
                Edits: (body?.Edits ?? Array.Empty<ReplayEditDto>())
                    .Select(e => new ReplayEditPreflightEdit(
                        AgentKey: e.AgentKey,
                        Ordinal: e.Ordinal,
                        Decision: e.Decision,
                        Output: e.Output,
                        HasPayload: e.Payload is not null))
                    .ToArray(),
                HasAdditionalMocks: body?.AdditionalMocks is { Count: > 0 },
                HasWorkflowVersionOverride: body?.WorkflowVersionOverride is not null);

            IntentClarityAssessment? assessment = null;
            try
            {
                assessment = preflightAssessor.Assess(PreflightMode.ReplayEdit, preflightInput);
            }
            catch
            {
                // Preflight failure is observability-only — never block the primary replay flow
                // because of an assessor bug. Admission still runs below.
            }

            if (assessment is { IsClear: false })
            {
                await EmitPreflightRefusalAsync(refusalSink, id, assessment, cancellationToken);
                return Results.Json(BuildPreflightResponse(id, assessment),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
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

        // sc-272 PR3: drift-vs-force admission + edit-shape validation are now consolidated
        // into ReplayRequestValidator. Rejections fire a Stage = Handoff RefusalEvent so
        // governance queries see refused replays as first-class evidence (parallel to the
        // tool-stage refusals that admission already emits via ToolRegistry).
        var admission = replayValidator.Validate(new ReplayAdmissionRequest(
            ParentTraceId: id,
            WorkflowKey: targetWorkflow.Key,
            OriginalWorkflowVersion: rootSaga.WorkflowVersion,
            TargetWorkflowVersion: targetWorkflow.Version,
            TargetWorkflowDisplayLabel: workflowLabel,
            PinnedAgentVersions: rootSaga.GetPinnedAgentVersions(),
            MockBundle: bundle,
            DeclaredPortsByAgent: portIndex,
            Edits: edits,
            AdditionalMocks: additionalMocks,
            Force: force,
            Drift: drift));

        if (admission is Rejected<AdmittedReplayRequest> rejected)
        {
            await EmitHandoffRefusalAsync(refusalSink, id, rejected.Reason, cancellationToken);
            return MapAdmissionRejection(rejected.Reason, id, drift, bundle);
        }

        var admitted = ((Accepted<AdmittedReplayRequest>)admission).Value;

        var startingInput = await ResolveStartingInputAsync(
            artifactStore, decisions, cancellationToken);

        var dryRunRequest = new DryRunRequest(
            WorkflowKey: admitted.WorkflowKey,
            WorkflowVersion: admitted.TargetWorkflowVersion,
            StartingInput: startingInput,
            MockResponses: admitted.Mocks);

        var result = await executor.ExecuteAsync(dryRunRequest, cancellationToken);

        var (failureCode, exhaustedAgent) = ClassifyFailure(result, admitted.Mocks);
        var replayState = exhaustedAgent is null
            ? result.State.ToString()
            : "Failed";

        // sc-275: persist a replay-attempt row + return lineage metadata so authors can
        // see "you've already tried this exact replay" and bundle exports preserve attempt
        // history. Persistence failure is observability-only — never break the primary
        // replay flow because we couldn't write a lineage row.
        var lineageInputs = BuildLineageInputs(body, rootSaga.GetPinnedAgentVersions(), force);
        var contentHash = ReplayLineageHasher.ComputeContentHash(lineageInputs);
        var lineageId = ReplayLineageHasher.ComputeLineageId(id, contentHash);
        var driftLevel = drift.Level.ToString();
        await TryPersistReplayAttemptAsync(
            dbContext, id, lineageId, contentHash, replayState,
            result.TerminalPort, driftLevel, body?.Reason, cancellationToken);

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
            Drift: new ReplayDriftDto(drift.Level.ToString(), drift.Warnings),
            Lineage: new ReplayLineageDto(
                LineageId: lineageId,
                ContentHash: contentHash,
                ParentTraceId: id,
                Generation: 1,
                CreatedAtUtc: DateTime.UtcNow,
                Reason: body?.Reason)));
    }

    private static ReplayLineageInputs BuildLineageInputs(
        ReplayRequest? body,
        IReadOnlyDictionary<string, int> pinnedAgentVersions,
        bool force)
    {
        var edits = (body?.Edits ?? Array.Empty<ReplayEditDto>())
            .Select(e => new ReplayLineageEdit(
                AgentKey: e.AgentKey,
                Ordinal: e.Ordinal,
                Decision: e.Decision,
                Output: e.Output,
                Payload: e.Payload))
            .ToArray();

        IReadOnlyDictionary<string, IReadOnlyList<ReplayLineageMock>> mocks =
            (body?.AdditionalMocks ?? new Dictionary<string, IReadOnlyList<ReplayMockResponseDto>>())
                .ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<ReplayLineageMock>)kv.Value
                        .Where(m => m is not null)
                        .Select(m => new ReplayLineageMock(m.Decision, m.Output, m.Payload))
                        .ToArray(),
                    StringComparer.Ordinal);

        return new ReplayLineageInputs(
            Edits: edits,
            AdditionalMocks: mocks,
            WorkflowVersionOverride: body?.WorkflowVersionOverride,
            PinnedAgentVersions: pinnedAgentVersions,
            Force: force);
    }

    private static async Task TryPersistReplayAttemptAsync(
        CodeFlowDbContext dbContext,
        Guid parentTraceId,
        Guid lineageId,
        string contentHash,
        string replayState,
        string? terminalPort,
        string driftLevel,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            dbContext.ReplayAttempts.Add(new ReplayAttemptEntity
            {
                Id = Guid.NewGuid(),
                ParentTraceId = parentTraceId,
                LineageId = lineageId,
                ContentHash = contentHash,
                Generation = 1,
                ReplayState = replayState,
                TerminalPort = terminalPort,
                DriftLevel = driftLevel,
                Reason = reason,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Persistence is for evidence — never break the primary replay flow on a
            // lineage write failure. The response still flows back to the caller with
            // its computed lineage metadata.
        }
    }

    /// <summary>
    /// sc-274 phase 1 — emit a <see cref="RefusalStages.Preflight"/> refusal when the
    /// ambiguity assessor refuses the replay edits. The refusal carries the per-dimension
    /// scores + clarification questions in <c>DetailJson</c> so governance queries can
    /// reconstruct what the author saw without re-running the assessor. Detail-blob shape
    /// lives in <see cref="PreflightRefusalDetail"/> so phase 2 (assistant chat) writes the
    /// same schema.
    /// </summary>
    private static async Task EmitPreflightRefusalAsync(
        IRefusalEventSink sink,
        Guid traceId,
        IntentClarityAssessment assessment,
        CancellationToken cancellationToken)
    {
        try
        {
            await sink.RecordAsync(
                new RefusalEvent(
                    Id: Guid.NewGuid(),
                    TraceId: traceId,
                    AssistantConversationId: null,
                    Stage: RefusalStages.Preflight,
                    Code: "preflight-ambiguous",
                    Reason: $"Replay edits did not meet the {assessment.Mode} clarity threshold "
                            + $"({assessment.OverallScore:0.00} < {assessment.Threshold:0.00}).",
                    Axis: PreflightRefusalDetail.LowestDimensionAxis(assessment),
                    Path: null,
                    DetailJson: PreflightRefusalDetail.Build(assessment).ToJsonString(),
                    OccurredAt: DateTimeOffset.UtcNow),
                cancellationToken);
        }
        catch
        {
            // Refusal recording is observability — never break the primary replay flow on
            // sink failure. The HTTP rejection still flows to the caller.
        }
    }

    private static PreflightRefusalResponse BuildPreflightResponse(
        Guid traceId,
        IntentClarityAssessment assessment) =>
        new(
            OriginalTraceId: traceId,
            Code: "preflight-ambiguous",
            Mode: assessment.Mode.ToString(),
            OverallScore: assessment.OverallScore,
            Threshold: assessment.Threshold,
            Dimensions: assessment.Dimensions
                .Select(d => new PreflightDimensionDto(d.Dimension, d.Score, d.Reason))
                .ToArray(),
            MissingFields: assessment.MissingFields,
            ClarificationQuestions: assessment.ClarificationQuestions);

    /// <summary>
    /// sc-272 PR3 — emit a <see cref="RefusalStages.Handoff"/> refusal directly via the sink
    /// when an admission boundary refuses outside of a tool-call context. Tool-call boundaries
    /// (workspace patch, vcs.open_pr) ride the existing <see cref="ToolRegistry"/> path at
    /// <see cref="RefusalStages.Tool"/>; the replay endpoint isn't inside a tool call, so it
    /// emits Handoff stage directly so governance queries see the refusal as first-class
    /// evidence rather than just an HTTP error.
    /// </summary>
    private static async Task EmitHandoffRefusalAsync(
        IRefusalEventSink sink,
        Guid traceId,
        Rejection rejection,
        CancellationToken cancellationToken)
    {
        try
        {
            await sink.RecordAsync(
                new RefusalEvent(
                    Id: Guid.NewGuid(),
                    TraceId: traceId,
                    AssistantConversationId: null,
                    Stage: RefusalStages.Handoff,
                    Code: rejection.Code,
                    Reason: rejection.Reason,
                    Axis: rejection.Axis,
                    Path: rejection.Path,
                    DetailJson: rejection.Detail?.ToJsonString(),
                    OccurredAt: DateTimeOffset.UtcNow),
                cancellationToken);
        }
        catch
        {
            // Refusal recording is observability — never break the primary replay flow on
            // sink failure. The HTTP rejection still flows to the caller.
        }
    }

    /// <summary>
    /// Maps a replay <see cref="Rejection"/> back to the response shape the API already
    /// surfaces for that case: <c>replay-drift-hard-refused</c> reconstitutes the
    /// <see cref="ReplayResponse"/> with <c>DriftRefused</c> state; <c>replay-edit-validation</c>
    /// rebuilds a <see cref="ValidationProblem"/> from the indexed errors stored in the
    /// rejection's <c>Detail</c>.
    /// </summary>
    private static IResult MapAdmissionRejection(
        Rejection rejection,
        Guid traceId,
        DriftReport drift,
        ReplayMockBundle bundle)
    {
        if (string.Equals(rejection.Code, "replay-drift-hard-refused", StringComparison.Ordinal))
        {
            return Results.Json(
                new ReplayResponse(
                    OriginalTraceId: traceId,
                    ReplayState: "DriftRefused",
                    ReplayTerminalPort: null,
                    FailureReason: rejection.Reason,
                    FailureCode: "drift_hard_refused",
                    ExhaustedAgent: null,
                    Decisions: bundle.Decisions.Select(MapDecisionRef).ToArray(),
                    ReplayEvents: Array.Empty<DryRunEventDto>(),
                    HitlPayload: null,
                    Drift: new ReplayDriftDto(drift.Level.ToString(), drift.Warnings)),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        if (string.Equals(rejection.Code, "replay-edit-validation", StringComparison.Ordinal))
        {
            var errors = ExtractEditErrors(rejection);
            var grouped = errors
                .Select((message, idx) => new { Key = $"edits[{idx}]", message })
                .GroupBy(x => x.Key, x => x.message)
                .ToDictionary(g => g.Key, g => g.ToArray());
            return Results.ValidationProblem(grouped);
        }

        // Unknown rejection codes fall through to a 400 with the rejection reason — keeps the
        // surface honest if a future validator adds a code without updating this map.
        return Results.Problem(
            title: "Replay request was refused.",
            detail: rejection.Reason,
            statusCode: StatusCodes.Status400BadRequest);
    }

    private static IReadOnlyList<string> ExtractEditErrors(Rejection rejection)
    {
        if (rejection.Detail is null
            || rejection.Detail["errors"] is not JsonArray errorsArray)
        {
            return Array.Empty<string>();
        }

        var errors = new List<string>(errorsArray.Count);
        foreach (var node in errorsArray)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                errors.Add(text);
            }
        }
        return errors;
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
