using System.Text;
using System.Text.Json;
using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Api.Endpoints;
using CodeFlow.Api.Validation;
using CodeFlow.Contracts;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Authority;
using CodeFlow.Runtime.Authority.Preflight;
using CodeFlow.Runtime.Workspace;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Handlers;

/// <summary>
/// Owns the <c>POST /api/traces</c> operation: workflow lookup, ambiguity preflight,
/// input-script evaluation, artifact write, per-trace workdir + credential file setup,
/// and the saga-launch <see cref="AgentInvokeRequested"/> publish.
///
/// <para>
/// Carved out of <see cref="TracesEndpoints"/> (sc-168 / F-004). The endpoint is now
/// auth + binding + a single <see cref="ExecuteAsync"/> call; the handler holds all
/// orchestration so the same logic is reachable from non-HTTP callers (homepage
/// assistant tools, in-process tests) without an HTTP host.
/// </para>
/// </summary>
public sealed class CreateTraceHandler
{
    private readonly IWorkflowRepository workflowRepository;
    private readonly IAgentConfigRepository agentRepository;
    private readonly IArtifactStore artifactStore;
    private readonly IPublishEndpoint publishEndpoint;
    private readonly CodeFlowDbContext dbContext;
    private readonly ICurrentUser currentUser;
    private readonly LogicNodeScriptHost scriptHost;
    private readonly IOptions<WorkspaceOptions> workspaceOptions;
    private readonly IPerTraceCredentialResolver credentialResolver;
    private readonly IRefusalEventSink refusalSink;
    private readonly IIntentClarityAssessor preflightAssessor;
    private readonly IOptions<PreflightOptions> preflightOptions;

    public CreateTraceHandler(
        IWorkflowRepository workflowRepository,
        IAgentConfigRepository agentRepository,
        IArtifactStore artifactStore,
        IPublishEndpoint publishEndpoint,
        CodeFlowDbContext dbContext,
        ICurrentUser currentUser,
        LogicNodeScriptHost scriptHost,
        IOptions<WorkspaceOptions> workspaceOptions,
        IPerTraceCredentialResolver credentialResolver,
        IRefusalEventSink refusalSink,
        IIntentClarityAssessor preflightAssessor,
        IOptions<PreflightOptions> preflightOptions)
    {
        this.workflowRepository = workflowRepository ?? throw new ArgumentNullException(nameof(workflowRepository));
        this.agentRepository = agentRepository ?? throw new ArgumentNullException(nameof(agentRepository));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        this.currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        this.scriptHost = scriptHost ?? throw new ArgumentNullException(nameof(scriptHost));
        this.workspaceOptions = workspaceOptions ?? throw new ArgumentNullException(nameof(workspaceOptions));
        this.credentialResolver = credentialResolver ?? throw new ArgumentNullException(nameof(credentialResolver));
        this.refusalSink = refusalSink ?? throw new ArgumentNullException(nameof(refusalSink));
        this.preflightAssessor = preflightAssessor ?? throw new ArgumentNullException(nameof(preflightAssessor));
        this.preflightOptions = preflightOptions ?? throw new ArgumentNullException(nameof(preflightOptions));
    }

    public async Task<IResult> ExecuteAsync(CreateTraceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.WorkflowKey))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["workflowKey"] = new[] { "workflowKey is required." }
            });
        }

        if (string.IsNullOrWhiteSpace(request.Input))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["input"] = new[] { "input is required." }
            });
        }

        // sc-274 phase 3 — ambiguity preflight runs BEFORE workflow lookup / artifact write /
        // saga publish. Refusing here saves all of that work and gives the launcher a focused
        // clarification list instead of letting the saga fail downstream with a vague Start
        // agent output. Mode is inferred from the de-facto code-aware-workflows convention
        // (presence of a "repositories" key in Inputs); workflows without that key are
        // assumed to be greenfield drafting. Disabled-mode and assessor failures fall through
        // to the normal launch path — preflight is observability for ambiguity, never a hard
        // barrier when the assessor itself misbehaves.
        if (preflightOptions.Value.Enabled)
        {
            var hasRepositoriesInput = request.Inputs is not null
                && request.Inputs.ContainsKey("repositories");
            var preflightMode = hasRepositoriesInput
                ? PreflightMode.BrownfieldChange
                : PreflightMode.GreenfieldDraft;

            IntentClarityAssessment? assessment = null;
            try
            {
                var preflightInput = new WorkflowLaunchPreflightInput(
                    Input: request.Input,
                    HasRepositoriesInput: hasRepositoriesInput,
                    WorkflowKey: request.WorkflowKey);
                assessment = preflightAssessor.Assess(preflightMode, preflightInput);
            }
            catch
            {
                // Preflight failure is observability-only — never block the launch flow because
                // of an assessor bug. The workflow lookup + saga publish still run below.
            }

            if (assessment is { IsClear: false })
            {
                await EmitWorkflowPreflightRefusalAsync(
                    refusalSink, request.WorkflowKey, assessment, cancellationToken);
                return Results.Json(
                    BuildWorkflowPreflightResponse(request.WorkflowKey, assessment),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        }

        Workflow workflow;
        if (request.WorkflowVersion is int version)
        {
            var resolved = await workflowRepository.TryGetAsync(request.WorkflowKey, version, cancellationToken);
            if (resolved is null)
            {
                return ApiResults.NotFound($"Workflow '{request.WorkflowKey}' version {request.WorkflowVersion} not found.");
            }
            workflow = resolved;
        }
        else
        {
            var latest = await workflowRepository.GetLatestAsync(request.WorkflowKey, cancellationToken);
            if (latest is null)
            {
                return ApiResults.NotFound($"Workflow '{request.WorkflowKey}' not found.");
            }
            workflow = latest;
        }

        if (workflow.IsRetired)
        {
            return ApiResults.Conflict($"Workflow '{workflow.Key}' is retired and cannot be used for new traces.");
        }

        var startNode = workflow.StartNode;
        if (string.IsNullOrWhiteSpace(startNode.AgentKey))
        {
            return Results.Problem("Workflow start node has no AgentKey configured.", statusCode: 500);
        }

        var resolvedInputsResult = ResolveContextInputs(workflow, request.Inputs);
        if (resolvedInputsResult.Error is not null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["inputs"] = new[] { resolvedInputsResult.Error }
            }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var startAgentVersion = startNode.AgentVersion
            ?? await agentRepository.GetLatestVersionAsync(startNode.AgentKey, cancellationToken);

        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();

        await using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(request.Input));
        var inputRef = await artifactStore.WriteAsync(
            inputStream,
            new ArtifactMetadata(
                TraceId: traceId,
                RoundId: roundId,
                ArtifactId: Guid.NewGuid(),
                ContentType: "text/plain",
                FileName: request.InputFileName ?? "input.txt"),
            cancellationToken);

        // Seed framework-managed workflow variables before the start-node input script runs so
        // that scripts and the start agent's prompt template can reference them. These keys are
        // listed in `ProtectedVariables.ReservedKeys` and cannot be overwritten by scripts or
        // agents. Top-level traces only — child sagas inherit via the workflow snapshot, which
        // means subflow and ReviewLoop children see the *parent's* traceId (the right answer for
        // branch naming and any other identity-anchored work).
        var seededWorkflowVars = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        seededWorkflowVars["traceId"] = JsonDocument.Parse(
            JsonSerializer.Serialize(traceId.ToString("N"))).RootElement.Clone();

        // The per-trace working-directory root is locked to WorkspaceOptions.WorkingDirectoryRoot
        // (default `/workspace`) — a deployment-level constant matched by a shared host
        // volume on both api and worker. Failure to create the per-trace directory is fatal: it
        // means the operator hasn't mounted the volume on this side, and proceeding would let
        // code-aware agents fail later with the path-rejection symptoms the runtime can't fully
        // diagnose. Override via `Workspace__WorkingDirectoryRoot` for non-container dev.
        var traceWorkDir = Path.Combine(workspaceOptions.Value.WorkingDirectoryRoot, traceId.ToString("N"));
        try
        {
            Directory.CreateDirectory(traceWorkDir);
            // The sandbox controller runs as a different uid (distroless nonroot, 65532) than
            // api/worker (1654). For container.run to mkdir its per-job `.results/{jobId}/`
            // subdir inside this trace dir, the controller needs group-write on the trace dir.
            // The shared GID is wired via `group_add` on the controller service in compose.
            // Group-writable (0775) is sufficient — world-write isn't needed.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    traceWorkDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.Problem(
                detail: $"Failed to create per-trace working directory '{traceWorkDir}': {ex.Message}. "
                    + $"Ensure '{workspaceOptions.Value.WorkingDirectoryRoot}' is mounted as a writable shared volume on the api and worker containers.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
        // sc-604 Phase 3: the legacy `workflow.workDir` alias is gone. The canonical
        // author-facing variable is `workflow.traceWorkDir`; the runtime source of truth is
        // the saga's typed TraceWorkDir field (set on the published AgentInvokeRequested
        // below).
        seededWorkflowVars["traceWorkDir"] = JsonDocument.Parse(JsonSerializer.Serialize(traceWorkDir)).RootElement.Clone();

        // Mid-workflow dispatches run a node's InputScript via the saga's TryEvaluateInputScriptAsync
        // helper, but a top-level Start has no saga yet at this point, so we evaluate the script
        // here. The corresponding LogicEvaluationRecord is intentionally dropped — capturing it
        // would require either changing the AgentInvokeRequested contract or pre-creating saga
        // state from the endpoint. Per-saga hooks still record evaluations for child Start
        // dispatches and every other node.
        var effectiveInputRef = inputRef;
        var effectiveContextInputs = resolvedInputsResult.Values;
        // sc-607: per-trace `repositories` is workflow-context state, not local-context. The
        // workflow-input convention still works at the authoring layer (declare `repositories`
        // as a Json input, validated for shape), but at launch we route the resolved value
        // into seededWorkflowVars so the saga lifts it from `workflow.*` rather than `context.*`.
        // Removing it from effectiveContextInputs avoids a confusing duplicate appearance under
        // both `workflow.repositories` and `context.repositories` in templates and scripts.
        if (effectiveContextInputs.TryGetValue(WorkflowValidator.RepositoriesInputKey, out var resolvedRepositories))
        {
            seededWorkflowVars[WorkflowValidator.RepositoriesInputKey] = resolvedRepositories;
            var trimmed = new Dictionary<string, JsonElement>(effectiveContextInputs, StringComparer.Ordinal);
            trimmed.Remove(WorkflowValidator.RepositoriesInputKey);
            effectiveContextInputs = trimmed;

            // sc-660: write the per-trace git-credential file outside the workspace tree so
            // subsequent run_command git ops in the worker can authenticate against any of the
            // declared repos via the `store --file=...` credential helper. Cred resolution is
            // best-effort: when no git host is configured, ResolveAsync returns an empty list
            // and the file is left absent — git ops that need auth will fail at the helper
            // (clear "credential helper found no entries" failure), better than launching with
            // a false sense of security.
            try
            {
                var repoUrls = ExtractRepositoryUrls(resolvedRepositories);
                var credentials = await credentialResolver.ResolveAsync(repoUrls, cancellationToken);
                await GitCredentialFile.WriteAsync(
                    workspaceOptions.Value.GitCredentialRoot,
                    traceId,
                    credentials,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The trace can still proceed for non-git work; the credential helper just
                // won't have entries to return. Surface to logs so operators see the IO issue.
                // Intentionally not failing the request so a degraded credential-store mount
                // doesn't block all workflow launches.
            }
        }
        if (!string.IsNullOrWhiteSpace(startNode.InputScript))
        {
            var artifactJson = await ReadArtifactAsJsonAsync(artifactStore, inputRef, cancellationToken);
            var eval = scriptHost.Evaluate(
                workflowKey: workflow.Key,
                workflowVersion: workflow.Version,
                nodeId: startNode.Id,
                script: startNode.InputScript!,
                declaredPorts: startNode.OutputPorts,
                input: artifactJson,
                context: resolvedInputsResult.Values,
                cancellationToken: cancellationToken,
                workflow: seededWorkflowVars,
                allowInputOverride: true,
                requireSetNodePath: false);

            if (eval.Failure is not null)
            {
                return Results.Problem(
                    detail: $"Input script for start node {startNode.Id} failed ({eval.Failure}): {eval.FailureMessage}",
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (!string.IsNullOrEmpty(eval.InputOverride))
            {
                await using var overrideStream = new MemoryStream(Encoding.UTF8.GetBytes(eval.InputOverride!));
                effectiveInputRef = await artifactStore.WriteAsync(
                    overrideStream,
                    new ArtifactMetadata(
                        TraceId: traceId,
                        RoundId: roundId,
                        ArtifactId: Guid.NewGuid(),
                        ContentType: "text/plain",
                        FileName: $"{startNode.AgentKey}-scripted-input.txt"),
                    cancellationToken);
            }

            // Apply setContext / setWorkflow writes from the script onto the published message.
            // Without this, scripts can override the input artifact but cannot seed shared state
            // for the start agent — the saga's own input-script handler does the same merge, so
            // doing it here keeps top-level Start parity with mid-workflow dispatches.
            if (eval.ContextUpdates.Count > 0)
            {
                var merged = new Dictionary<string, JsonElement>(effectiveContextInputs, StringComparer.Ordinal);
                foreach (var (key, value) in eval.ContextUpdates)
                {
                    merged[key] = value;
                }
                effectiveContextInputs = merged;
            }

            if (eval.WorkflowUpdates.Count > 0)
            {
                foreach (var (key, value) in eval.WorkflowUpdates)
                {
                    seededWorkflowVars[key] = value;
                }
            }
        }

        IReadOnlyDictionary<string, JsonElement>? effectiveWorkflowContext = seededWorkflowVars.Count > 0
            ? seededWorkflowVars
            : null;

        await publishEndpoint.Publish(
            new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: roundId,
                WorkflowKey: workflow.Key,
                WorkflowVersion: workflow.Version,
                NodeId: startNode.Id,
                AgentKey: startNode.AgentKey,
                AgentVersion: startAgentVersion,
                InputRef: effectiveInputRef,
                ContextInputs: effectiveContextInputs,
                CorrelationHeaders: new Dictionary<string, string>
                {
                    ["x-submitted-by"] = currentUser.Id ?? "unknown"
                },
                WorkflowContext: effectiveWorkflowContext,
                // sc-602: typed per-trace workspace anchor. The saga prefers this over the
                // legacy `workflow.workDir` bag-key fallback in ApplyInitialRequest; once Phase 3
                // drops the bag entry, this becomes the only source.
                TraceWorkDir: traceWorkDir),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/traces/{traceId}", new CreateTraceResponse(traceId));
    }

    private static (IReadOnlyDictionary<string, JsonElement> Values, string? Error) ResolveContextInputs(
        Workflow workflow,
        IReadOnlyDictionary<string, JsonElement>? supplied)
    {
        if (workflow.Inputs.Count == 0 && (supplied is null || supplied.Count == 0))
        {
            return (new Dictionary<string, JsonElement>(StringComparer.Ordinal), null);
        }

        var resolved = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var providedKeys = new HashSet<string>(supplied?.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

        foreach (var definition in workflow.Inputs.OrderBy(input => input.Ordinal))
        {
            if (supplied is not null && supplied.TryGetValue(definition.Key, out var value))
            {
                if (string.Equals(definition.Key, WorkflowValidator.RepositoriesInputKey, StringComparison.Ordinal)
                    && definition.Kind == WorkflowInputKind.Json)
                {
                    var shapeError = WorkflowValidator.ValidateRepositoriesShape(value);
                    if (shapeError is not null)
                    {
                        return (resolved,
                            $"Input 'repositories' has an invalid shape: {shapeError}");
                    }
                }

                resolved[definition.Key] = value.Clone();
                continue;
            }

            if (!string.IsNullOrWhiteSpace(definition.DefaultValueJson))
            {
                using var document = JsonDocument.Parse(definition.DefaultValueJson);
                resolved[definition.Key] = document.RootElement.Clone();
                continue;
            }

            if (definition.Required)
            {
                return (resolved, $"Required input '{definition.Key}' was not supplied and has no default.");
            }
        }

        var undeclared = providedKeys
            .Where(key => !workflow.Inputs.Any(i => string.Equals(i.Key, key, StringComparison.Ordinal)))
            .ToArray();

        if (undeclared.Length > 0)
        {
            return (resolved, $"Unknown input(s) supplied: {string.Join(", ", undeclared)}.");
        }

        return (resolved, null);
    }

    // Mirrors WorkflowSagaStateMachine.ReadArtifactAsJsonAsync — kept local because the saga's
    // copy is private. Wraps non-JSON text as { "text": "…" } so InputScripts can still read it.
    private static async Task<JsonElement> ReadArtifactAsJsonAsync(
        IArtifactStore artifactStore,
        Uri inputRef,
        CancellationToken cancellationToken)
    {
        await using var stream = await artifactStore.ReadAsync(inputRef, cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
        var text = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            var doc = new { text };
            return JsonSerializer.SerializeToElement(doc);
        }
    }

    // sc-660: extracts the `url` field from each entry in the resolved `repositories` JSON
    // array so the credential resolver can derive distinct hosts. Shape is already validated
    // by ValidateRepositoriesShape upstream, so this just walks; defensively returns an
    // empty list when the value is the wrong shape rather than throwing.
    private static IReadOnlyList<string> ExtractRepositoryUrls(JsonElement repositories)
    {
        if (repositories.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var urls = new List<string>();
        foreach (var entry in repositories.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (!entry.TryGetProperty("url", out var urlElement)
                || urlElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var url = urlElement.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                urls.Add(url);
            }
        }
        return urls;
    }

    /// <summary>
    /// sc-274 phase 3 — emit a <see cref="RefusalStages.Preflight"/> refusal when the workflow
    /// launch assessor refuses the request. Workflow launches don't have a TraceId yet (we're
    /// refusing BEFORE generating one) and they're not tied to an assistant conversation, so
    /// both nullable correlation fields are null. The workflow key rides in <c>Path</c> so
    /// governance queries can slice "preflight refusals on workflow X" without parsing detail.
    /// Reuses the shared <see cref="PreflightRefusalDetail"/> shape so phase 1 + 2 + 3 all
    /// write the same DetailJson schema.
    /// </summary>
    private static async Task EmitWorkflowPreflightRefusalAsync(
        IRefusalEventSink sink,
        string workflowKey,
        IntentClarityAssessment assessment,
        CancellationToken cancellationToken)
    {
        try
        {
            await sink.RecordAsync(
                new RefusalEvent(
                    Id: Guid.NewGuid(),
                    TraceId: null,
                    AssistantConversationId: null,
                    Stage: RefusalStages.Preflight,
                    Code: "workflow-preflight-ambiguous",
                    Reason: $"Workflow launch did not meet the {assessment.Mode} clarity threshold "
                            + $"({assessment.OverallScore:0.00} < {assessment.Threshold:0.00}).",
                    Axis: PreflightRefusalDetail.LowestDimensionAxis(assessment),
                    Path: workflowKey,
                    DetailJson: PreflightRefusalDetail.Build(assessment).ToJsonString(),
                    OccurredAt: DateTimeOffset.UtcNow),
                cancellationToken);
        }
        catch
        {
            // Refusal recording is observability — never break the launch flow on sink failure.
            // The HTTP 422 still flows to the caller.
        }
    }

    private static WorkflowPreflightRefusalResponse BuildWorkflowPreflightResponse(
        string workflowKey,
        IntentClarityAssessment assessment) =>
        new(
            WorkflowKey: workflowKey,
            Code: "workflow-preflight-ambiguous",
            Mode: assessment.Mode.ToString(),
            OverallScore: assessment.OverallScore,
            Threshold: assessment.Threshold,
            Dimensions: assessment.Dimensions
                .Select(d => new PreflightDimensionDto(d.Dimension, d.Score, d.Reason))
                .ToArray(),
            MissingFields: assessment.MissingFields,
            ClarificationQuestions: assessment.ClarificationQuestions);
}
