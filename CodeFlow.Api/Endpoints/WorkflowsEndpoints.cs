using CodeFlow.Api.Assistant;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation;
using CodeFlow.Api.Validation.Pipeline;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Workspace;
using Jint;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeFlow.Api.Endpoints;

public static class WorkflowsEndpoints
{
    private static readonly Regex PackageFileNameUnsafeChars = new("[^A-Za-z0-9_.-]+", RegexOptions.Compiled);

    public static IEndpointRouteBuilder MapWorkflowsEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/workflows");

        group.MapGet("/", ListWorkflowsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        // HAA-14: recently-used workflows for the homepage rail. "Recently used" = workflows
        // ordered by most recent saga activity. We deliberately do NOT introduce a new pin or
        // user-preference table; recency is already implicit in WorkflowSagas.UpdatedAtUtc and
        // covers the v1 spec for the rail.
        group.MapGet("/recent", ListRecentWorkflowsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        group.MapGet("/{key}/versions", ListVersionsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        group.MapGet("/{key}/{version:int}", GetVersionAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        group.MapGet("/{key}/{version:int}/terminal-ports", GetTerminalPortsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        group.MapGet("/{key}/latest/terminal-ports", GetLatestTerminalPortsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        group.MapGet("/{key}/{version:int}/package", ExportPackageAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        group.MapPost("/package/preview", PreviewPackageImportAsync)
            .RequireAuthorization(CodeFlowApiDefaults.PolicyBundles.PackageImportWrite);

        group.MapPost("/package/apply", ApplyPackageImportAsync)
            .RequireAuthorization(CodeFlowApiDefaults.PolicyBundles.PackageImportWrite);

        group.MapPost("/package/apply-from-draft", ApplyPackageImportFromDraftAsync)
            .RequireAuthorization(CodeFlowApiDefaults.PolicyBundles.PackageImportWrite);

        group.MapGet("/{key}", GetLatestAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        group.MapPut("/{key}", CreateVersionAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        group.MapPost("/{key}/retire", RetireWorkflowAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        group.MapPost("/retire", RetireWorkflowsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        group.MapPost("/validate-script", ValidateScript)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        group.MapPost("/validate", ValidateWorkflowAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        group.MapPost("/templates/render-transform-preview", RenderTransformPreviewAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        return routes;
    }

    private static IResult RenderTransformPreviewAsync(
        TransformPreviewRequest request,
        CodeFlow.Runtime.IScribanTemplateRenderer renderer,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrEmpty(request.Template))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["template"] = new[] { "Template must not be empty." }
            });
        }

        var outputType = string.IsNullOrWhiteSpace(request.OutputType)
            ? "string"
            : request.OutputType.Trim();
        if (outputType != "string" && outputType != "json")
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["outputType"] = new[] { "outputType must be 'string' or 'json'." }
            });
        }

        var inputJson = request.Input ?? EndpointDefaults.EmptyJsonObject;
        var contextInputs = request.Context ?? EndpointDefaults.EmptyJsonElementMap;
        var workflowInputs = request.Workflow ?? EndpointDefaults.EmptyJsonElementMap;

        var scope = CodeFlow.Orchestration.TransformNodeContext.Build(
            inputJson,
            contextInputs,
            workflowInputs);

        string rendered;
        try
        {
            rendered = renderer.Render(request.Template, scope, cancellationToken);
        }
        catch (CodeFlow.Runtime.PromptTemplateException ex)
        {
            return Results.UnprocessableEntity(new TransformPreviewErrorResponse(ex.Message));
        }

        if (outputType != "json")
        {
            return Results.Ok(new TransformPreviewResponse(rendered, null, null));
        }

        try
        {
            using var document = JsonDocument.Parse(rendered);
            var parsed = document.RootElement.Clone();
            return Results.Ok(new TransformPreviewResponse(rendered, parsed, null));
        }
        catch (JsonException ex)
        {
            return Results.Ok(new TransformPreviewResponse(rendered, null, ex.Message));
        }
    }

    private static async Task<IResult> ExportPackageAsync(
        string key,
        int version,
        IWorkflowPackageResolver resolver,
        HttpResponse response,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            var package = await resolver.ResolveAsync(key, version, cancellationToken);
            response.Headers.ContentDisposition = $"attachment; filename=\"{PackageFileName(package.EntryPoint)}\"";
            return Results.Json(package, jsonOptions.Value.SerializerOptions, contentType: "application/json");
        }
        catch (WorkflowNotFoundException)
        {
            return Results.NotFound();
        }
        catch (WorkflowPackageResolutionException exception)
        {
            // V8: surface the full structured list of missing references in `extensions` so
            // editors / package-preview UIs can render each one with click-to-jump anchors.
            var extensions = exception.MissingReferences.Count == 0
                ? null
                : new Dictionary<string, object?>
                {
                    ["missingReferences"] = exception.MissingReferences,
                };
            return Results.Problem(
                title: "Workflow package export failed",
                detail: exception.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: extensions);
        }
    }

    private static string PackageFileName(WorkflowPackageReference entryPoint)
    {
        var safeKey = PackageFileNameUnsafeChars.Replace(entryPoint.Key.Trim(), "-").Trim('-');
        if (string.IsNullOrWhiteSpace(safeKey))
        {
            safeKey = "workflow";
        }

        return $"{safeKey}-v{entryPoint.Version}-package.json";
    }

    private static async Task<IResult> PreviewPackageImportAsync(
        WorkflowPackage package,
        IWorkflowPackageImporter importer,
        CancellationToken cancellationToken)
    {
        try
        {
            var preview = await importer.PreviewAsync(package, cancellationToken);
            return Results.Ok(preview);
        }
        catch (WorkflowPackageResolutionException exception)
        {
            return WorkflowPackageImportValidationProblem(exception);
        }
    }

    private static async Task<IResult> ApplyPackageImportAsync(
        WorkflowPackage package,
        IWorkflowPackageImporter importer,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await importer.ApplyAsync(package, cancellationToken);
            return Results.Ok(result);
        }
        catch (WorkflowPackageResolutionException exception)
        {
            return WorkflowPackageImportValidationProblem(exception);
        }
    }

    /// <summary>
    /// Applies the draft package stored in the calling user's assistant conversation workspace.
    /// The chat-panel calls this when the user clicks the Save chip on a `save_workflow_package`
    /// tool call that was invoked WITHOUT a package argument (the draft path). Keeping the
    /// payload server-side means refinement loops never round-trip the package through the LLM
    /// or the browser.
    /// <para/>
    /// The chip carries an immutable <c>snapshotId</c> minted by <c>save_workflow_package</c>
    /// when it validated the draft. The endpoint loads the snapshot file (NOT the live draft)
    /// so a draft mutation between preview and confirmation can't make the user import a
    /// different package than the one they approved.
    /// </summary>
    private static async Task<IResult> ApplyPackageImportFromDraftAsync(
        ApplyPackageImportFromDraftRequest request,
        HttpContext httpContext,
        IAssistantUserResolver userResolver,
        IAssistantConversationRepository conversations,
        IAssistantWorkspaceProvider workspaceProvider,
        IWorkflowPackageImporter importer,
        CancellationToken cancellationToken)
    {
        if (request is null || request.ConversationId == Guid.Empty)
        {
            return ApiResults.BadRequest("conversationId is required.");
        }

        if (request.SnapshotId == Guid.Empty)
        {
            return ApiResults.BadRequest(
                "snapshotId is required. The save chip must carry the snapshot id minted by `save_workflow_package` so the apply matches the bytes the user confirmed.");
        }

        var conversation = await conversations.GetByIdAsync(request.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return Results.NotFound();
        }

        var userId = userResolver.Resolve(httpContext, allowAnonymous: userResolver.IsDemoUser(conversation.UserId));
        if (string.IsNullOrEmpty(userId) || !string.Equals(conversation.UserId, userId, StringComparison.Ordinal))
        {
            // Same shape as the assistant endpoints: don't leak existence to a non-owner.
            return Results.NotFound();
        }

        ToolWorkspaceContext workspace;
        try
        {
            workspace = workspaceProvider.GetOrCreateConversationWorkspace(request.ConversationId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return Results.Problem(
                title: "Assistant workspace unavailable",
                detail: $"Could not access the conversation's workspace: {ex.Message}",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var snapshotPath = WorkflowPackageDraftStore.ResolveSnapshotPath(workspace, request.SnapshotId);
        if (!File.Exists(snapshotPath))
        {
            // Snapshot files only exist between save_workflow_package returning preview_ok and
            // a successful apply (or until the conversation workspace is reset). A miss here
            // means the snapshot was already applied, was created in a different conversation,
            // or never existed — surface as a clean 400 so the chip shows "Save failed" not 500.
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["package"] = new[]
                {
                    "Snapshot not found for this conversation. The Save chip may have already been used, or the assistant has not yet validated a draft. Ask the assistant to save the draft again."
                }
            });
        }

        WorkflowPackage? package;
        try
        {
            await using var stream = File.OpenRead(snapshotPath);
            package = await JsonSerializer.DeserializeAsync<WorkflowPackage>(
                stream,
                AssistantToolJson.SerializerOptions,
                cancellationToken);
        }
        catch (JsonException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["package"] = new[] { $"The draft snapshot on disk could not be parsed: {ex.Message}" }
            });
        }

        if (package is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["package"] = new[] { "The draft snapshot on disk deserialized to null." }
            });
        }

        try
        {
            var result = await importer.ApplyAsync(package, cancellationToken);
            // Snapshot consumed — clean up so the workspace doesn't accumulate them. A failure
            // here is non-fatal (the import already succeeded); silently swallow IO errors so
            // a stale-FS read doesn't undo a successful library write.
            try { WorkflowPackageDraftStore.DeleteSnapshot(workspace, request.SnapshotId); }
            catch (IOException) { /* best-effort cleanup; ignore */ }
            return Results.Ok(result);
        }
        catch (WorkflowPackageResolutionException exception)
        {
            return WorkflowPackageImportValidationProblem(exception);
        }
    }

    public sealed record ApplyPackageImportFromDraftRequest(Guid ConversationId, Guid SnapshotId);

    private static IResult WorkflowPackageImportValidationProblem(WorkflowPackageResolutionException exception)
    {
        if (exception.ValidationErrors.Count == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["package"] = new[] { exception.Message }
            });
        }

        var problems = new Dictionary<string, string[]>
        {
            ["package"] = new[]
            {
                "Workflow package import failed validation. Fix the listed workflow errors and retry."
            }
        };

        foreach (var group in exception.ValidationErrors.GroupBy(error => error.WorkflowKey, StringComparer.Ordinal))
        {
            problems[$"workflows.{group.Key}"] = group
                .Select(FormatWorkflowPackageValidationError)
                .ToArray();
        }

        return Results.ValidationProblem(problems);
    }

    private static string FormatWorkflowPackageValidationError(WorkflowPackageValidationError error)
    {
        if (error.RuleIds is not { Count: > 0 })
        {
            return error.Message;
        }

        return $"{error.Message} (rules: {string.Join(", ", error.RuleIds)})";
    }

    private static IResult ValidateScript(
        ValidateScriptRequest request,
        LogicNodeScriptHost scriptHost)
    {
        if (string.IsNullOrWhiteSpace(request.Script))
        {
            return Results.Ok(new ValidateScriptResponse(
                Ok: false,
                Errors: new[] { new ValidateScriptError(0, 0, "Script must not be empty.") }));
        }

        try
        {
            Engine.PrepareScript(request.Script);
        }
        catch (Exception ex)
        {
            return Results.Ok(new ValidateScriptResponse(
                Ok: false,
                Errors: new[] { new ValidateScriptError(0, 0, ex.Message) }));
        }

        // If a direction was specified, run a sandboxed dry-evaluate so wrong-verb usage
        // (setInput in an output script, or setOutput in an input script) is reported by
        // the script host's own gating rather than only at runtime.
        if (request.Direction is ValidateScriptDirection direction)
        {
            var allowInput = direction == ValidateScriptDirection.Input;
            var allowOutput = direction == ValidateScriptDirection.Output;
            var variableName = direction == ValidateScriptDirection.Input ? "input" : "output";

            var emptyJson = JsonDocument.Parse("{}").RootElement;
            var emptyContext = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            // When the caller supplied DeclaredPorts, thread them through so
            // setNodePath/setOutput referencing a port the node doesn't declare surfaces as
            // an UnknownPort error. When omitted, fall back to empty and silently allow port
            // mismatches — useful in the script editor before the script is wired up.
            var declaredPorts = request.DeclaredPorts is { Count: > 0 } ports
                ? ports
                : Array.Empty<string>();
            var portValidationActive = declaredPorts.Count > 0;

            var eval = scriptHost.Evaluate(
                workflowKey: "__validate__",
                workflowVersion: 0,
                nodeId: Guid.NewGuid(),
                script: request.Script!,
                declaredPorts: declaredPorts,
                input: emptyJson,
                context: emptyContext,
                allowOutputOverride: allowOutput,
                allowInputOverride: allowInput,
                inputVariableName: variableName,
                requireSetNodePath: false);

            if (eval.Failure is not null
                && (portValidationActive || eval.Failure != LogicNodeFailureKind.UnknownPort))
            {
                return Results.Ok(new ValidateScriptResponse(
                    Ok: false,
                    Errors: new[] { new ValidateScriptError(0, 0, eval.FailureMessage ?? eval.Failure.ToString()!) }));
            }
        }

        return Results.Ok(new ValidateScriptResponse(Ok: true, Errors: Array.Empty<ValidateScriptError>()));
    }

    private static async Task<IResult> ListWorkflowsAsync(
        IWorkflowRepository repository,
        CancellationToken cancellationToken)
    {
        var workflows = await repository.ListLatestAsync(cancellationToken);
        return Results.Ok(workflows.Select(MapSummary).ToArray());
    }

    /// <summary>
    /// HAA-14 — Returns up to <paramref name="take"/> workflows ordered by their most recent
    /// saga activity (any state, any user). Used by the homepage rail to surface workflows the
    /// caller is likely to want to revisit. Filters out workflows that have never been run; the
    /// "browse all workflows" surface is the existing list endpoint.
    /// </summary>
    private static async Task<IResult> ListRecentWorkflowsAsync(
        CodeFlowDbContext dbContext,
        IWorkflowRepository repository,
        CancellationToken cancellationToken,
        int take = 5)
    {
        var clamped = Math.Clamp(take, 1, 50);

        // Group sagas by workflow key, keep the most recent UpdatedAtUtc per key, then take the
        // top N. We hydrate workflow detail from the Workflows table afterwards so deleted
        // workflows (orphan sagas) silently drop out.
        var recentKeys = await dbContext.WorkflowSagas
            .AsNoTracking()
            .GroupBy(saga => saga.WorkflowKey)
            .Select(group => new
            {
                Key = group.Key,
                LastUsedAtUtc = group.Max(saga => saga.UpdatedAtUtc)
            })
            .OrderByDescending(row => row.LastUsedAtUtc)
            .Take(clamped)
            .ToListAsync(cancellationToken);

        if (recentKeys.Count == 0)
        {
            return Results.Ok(Array.Empty<RecentWorkflowDto>());
        }

        var keyArray = recentKeys.Select(r => r.Key).ToArray();
        var workflows = await repository.ListLatestAsync(cancellationToken);
        var byKey = workflows.ToDictionary(w => w.Key, StringComparer.Ordinal);

        var result = new List<RecentWorkflowDto>(recentKeys.Count);
        foreach (var row in recentKeys)
        {
            if (!byKey.TryGetValue(row.Key, out var workflow))
            {
                continue; // saga's workflow has been deleted
            }

            result.Add(new RecentWorkflowDto(
                Summary: MapSummary(workflow),
                LastUsedAtUtc: DateTime.SpecifyKind(row.LastUsedAtUtc, DateTimeKind.Utc)));
        }

        return Results.Ok(result.ToArray());
    }

    private static async Task<IResult> ListVersionsAsync(
        string key,
        IWorkflowRepository repository,
        CancellationToken cancellationToken)
    {
        var workflows = await repository.ListVersionsAsync(key, cancellationToken);
        if (workflows.Count == 0)
        {
            return Results.NotFound();
        }

        return Results.Ok(workflows.Select(MapDetail).ToArray());
    }

    private static async Task<IResult> GetVersionAsync(
        string key,
        int version,
        IWorkflowRepository repository,
        CancellationToken cancellationToken)
    {
        var workflow = await repository.TryGetAsync(key, version, cancellationToken);
        return workflow is null ? Results.NotFound() : Results.Ok(MapDetail(workflow));
    }

    private static async Task<IResult> GetLatestAsync(
        string key,
        IWorkflowRepository repository,
        CancellationToken cancellationToken)
    {
        var workflow = await repository.GetLatestAsync(key, cancellationToken);
        return workflow is null ? Results.NotFound() : Results.Ok(MapDetail(workflow));
    }

    private static async Task<IResult> GetTerminalPortsAsync(
        string key,
        int version,
        IWorkflowRepository repository,
        CancellationToken cancellationToken)
    {
        var workflow = await repository.TryGetAsync(key, version, cancellationToken);
        return workflow is null ? Results.NotFound() : Results.Ok(workflow.TerminalPorts);
    }

    private static async Task<IResult> GetLatestTerminalPortsAsync(
        string key,
        IWorkflowRepository repository,
        CancellationToken cancellationToken)
    {
        var workflow = await repository.GetLatestAsync(key, cancellationToken);
        return workflow is null
            ? Results.NotFound()
            : Results.Ok(workflow.TerminalPorts);
    }

    private static async Task<IResult> CreateAsync(
        CreateWorkflowRequest request,
        IWorkflowRepository repository,
        IAgentConfigRepository agentRepository,
        IAgentRoleRepository roleRepository,
        CodeFlowDbContext dbContext,
        IAuthoringTelemetry telemetry,
        WorkflowValidationPipeline pipeline,
        CancellationToken cancellationToken)
    {
        var validation = await WorkflowValidator.ValidateAsync(
            request.Key ?? string.Empty,
            request.Name,
            request.MaxRoundsPerRound,
            request.Nodes,
            request.Edges,
            request.Inputs,
            dbContext,
            repository,
            agentRepository,
            cancellationToken);

        if (!validation.IsValid)
        {
            telemetry.ValidatorBlockedSave(
                request.Key ?? string.Empty,
                new[] { "workflow-validator" });
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["workflow"] = new[] { validation.Error! }
            });
        }

        var pipelineBlock = await RunSaveTimePipelineAsync(
            request.Key ?? string.Empty,
            request.Name,
            request.MaxRoundsPerRound,
            request.Nodes,
            request.Edges,
            request.Inputs,
            dbContext,
            repository,
            agentRepository,
            roleRepository,
            pipeline,
            telemetry,
            cancellationToken,
            request.WorkflowVarsReads,
            request.WorkflowVarsWrites);
        if (pipelineBlock is not null)
        {
            return pipelineBlock;
        }

        var normalizedKey = request.Key!.Trim();
        var existingWorkflow = await repository.GetLatestAsync(normalizedKey, cancellationToken);
        if (existingWorkflow is { IsRetired: true })
        {
            return ApiResults.Conflict($"Workflow '{normalizedKey}' is retired. Create a new workflow with a different key.");
        }

        var existing = await dbContext.Workflows
            .AsNoTracking()
            .AnyAsync(workflow => workflow.Key == normalizedKey, cancellationToken);

        if (existing)
        {
            return ApiResults.Conflict($"Workflow '{normalizedKey}' already exists. Use PUT to add a version.");
        }

        var resolvedNodes = await ResolveSubflowLatestVersionsAsync(request.Nodes!, dbContext, cancellationToken);
        var draft = ToDraft(
            normalizedKey,
            request.Name!,
            request.MaxRoundsPerRound,
            request.Category ?? WorkflowCategory.Workflow,
            request.Tags,
            resolvedNodes,
            request.Edges!,
            request.Inputs,
            request.WorkflowVarsReads,
            request.WorkflowVarsWrites);
        var version = await repository.CreateNewVersionAsync(draft, cancellationToken);

        return Results.Created($"/api/workflows/{normalizedKey}/{version}", new { key = normalizedKey, version });
    }

    private static async Task<IResult> CreateVersionAsync(
        string key,
        UpdateWorkflowRequest request,
        IWorkflowRepository repository,
        IAgentConfigRepository agentRepository,
        IAgentRoleRepository roleRepository,
        CodeFlowDbContext dbContext,
        IAuthoringTelemetry telemetry,
        WorkflowValidationPipeline pipeline,
        CancellationToken cancellationToken)
    {
        var validation = await WorkflowValidator.ValidateAsync(
            key,
            request.Name,
            request.MaxRoundsPerRound,
            request.Nodes,
            request.Edges,
            request.Inputs,
            dbContext,
            repository,
            agentRepository,
            cancellationToken);

        if (!validation.IsValid)
        {
            telemetry.ValidatorBlockedSave(key, new[] { "workflow-validator" });
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["workflow"] = new[] { validation.Error! }
            });
        }

        var pipelineBlock = await RunSaveTimePipelineAsync(
            key,
            request.Name,
            request.MaxRoundsPerRound,
            request.Nodes,
            request.Edges,
            request.Inputs,
            dbContext,
            repository,
            agentRepository,
            roleRepository,
            pipeline,
            telemetry,
            cancellationToken,
            request.WorkflowVarsReads,
            request.WorkflowVarsWrites);
        if (pipelineBlock is not null)
        {
            return pipelineBlock;
        }

        var normalizedKey = key.Trim();
        var existingWorkflow = await repository.GetLatestAsync(normalizedKey, cancellationToken);

        if (existingWorkflow is null)
        {
            return ApiResults.NotFound($"Workflow '{normalizedKey}' does not exist. Use POST to create.");
        }
        if (existingWorkflow.IsRetired)
        {
            return ApiResults.Conflict($"Workflow '{normalizedKey}' is retired. Create a new workflow with a different key.");
        }

        var resolvedNodes = await ResolveSubflowLatestVersionsAsync(request.Nodes!, dbContext, cancellationToken);
        var draft = ToDraft(
            normalizedKey,
            request.Name!,
            request.MaxRoundsPerRound,
            request.Category ?? WorkflowCategory.Workflow,
            request.Tags,
            resolvedNodes,
            request.Edges!,
            request.Inputs,
            request.WorkflowVarsReads,
            request.WorkflowVarsWrites);
        var version = await repository.CreateNewVersionAsync(draft, cancellationToken);

        return Results.Ok(new { key = normalizedKey, version });
    }

    /// <summary>
    /// Run the pluggable validation pipeline at workflow save and convert any Error findings into
    /// a ValidationProblem result. Returns null if the pipeline produced no errors (save proceeds).
    /// Warning-only findings do not block save — the editor surfaces them via the
    /// <c>POST /validate</c> endpoint, which is what authoring UIs call interactively.
    /// </summary>
    private static async Task<IResult?> RunSaveTimePipelineAsync(
        string key,
        string? name,
        int? maxRoundsPerRound,
        IReadOnlyList<WorkflowNodeDto>? nodes,
        IReadOnlyList<WorkflowEdgeDto>? edges,
        IReadOnlyList<WorkflowInputDto>? inputs,
        CodeFlowDbContext dbContext,
        IWorkflowRepository repository,
        IAgentConfigRepository agentRepository,
        IAgentRoleRepository roleRepository,
        WorkflowValidationPipeline pipeline,
        IAuthoringTelemetry telemetry,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? workflowVarsReads = null,
        IReadOnlyList<string>? workflowVarsWrites = null)
    {
        var context = new WorkflowValidationContext(
            Key: key ?? string.Empty,
            Name: name,
            MaxRoundsPerRound: maxRoundsPerRound,
            Nodes: nodes ?? Array.Empty<WorkflowNodeDto>(),
            Edges: edges ?? Array.Empty<WorkflowEdgeDto>(),
            Inputs: inputs,
            DbContext: dbContext,
            WorkflowRepository: repository,
            AgentRepository: agentRepository,
            AgentRoleRepository: roleRepository,
            WorkflowVarsReads: workflowVarsReads,
            WorkflowVarsWrites: workflowVarsWrites);

        var report = await pipeline.RunAsync(context, cancellationToken);

        if (!report.HasErrors)
        {
            return null;
        }

        var errorFindings = report.Findings
            .Where(f => f.Severity == WorkflowValidationSeverity.Error)
            .ToArray();
        var blockingRuleIds = errorFindings
            .Select(f => f.RuleId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        telemetry.ValidatorBlockedSave(key ?? string.Empty, blockingRuleIds);

        var problems = errorFindings
            .GroupBy(f => f.RuleId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(f => f.Message).ToArray(),
                StringComparer.Ordinal);

        return Results.ValidationProblem(problems);
    }

    /// <summary>
    /// Run the pluggable validation pipeline against an arbitrary workflow draft and return the
    /// aggregated findings. Authoring DX surface — the editor calls this as the user edits to
    /// drive the results panel without committing the draft. Save endpoints additionally run the
    /// pipeline and convert Error findings into a blocking ValidationProblem (see
    /// <see cref="RunSaveTimePipelineAsync"/>).
    /// </summary>
    private static async Task<IResult> ValidateWorkflowAsync(
        ValidateWorkflowRequest request,
        WorkflowValidationPipeline pipeline,
        IWorkflowRepository repository,
        IAgentConfigRepository agentRepository,
        IAgentRoleRepository roleRepository,
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("Request body is required.");
        }

        var context = new WorkflowValidationContext(
            Key: request.Key ?? string.Empty,
            Name: request.Name,
            MaxRoundsPerRound: request.MaxRoundsPerRound,
            Nodes: request.Nodes ?? Array.Empty<WorkflowNodeDto>(),
            Edges: request.Edges ?? Array.Empty<WorkflowEdgeDto>(),
            Inputs: request.Inputs,
            DbContext: dbContext,
            WorkflowRepository: repository,
            AgentRepository: agentRepository,
            AgentRoleRepository: roleRepository,
            WorkflowVarsReads: request.WorkflowVarsReads,
            WorkflowVarsWrites: request.WorkflowVarsWrites);

        var report = await pipeline.RunAsync(context, cancellationToken);

        var findings = report.Findings
            .Select(f => new WorkflowValidationFindingDto(
                RuleId: f.RuleId,
                Severity: f.Severity.ToString(),
                Message: f.Message,
                Location: f.Location is null
                    ? null
                    : new WorkflowValidationLocationDto(
                        NodeId: f.Location.NodeId,
                        EdgeFrom: f.Location.EdgeFrom,
                        EdgePort: f.Location.EdgePort)))
            .ToArray();

        return Results.Ok(new ValidateWorkflowResponse(
            HasErrors: report.HasErrors,
            HasWarnings: report.HasWarnings,
            Findings: findings));
    }

    /// <summary>
    /// Rewrites every Subflow node with a null <c>SubflowVersion</c> ("latest at save") to the
    /// current latest version of the referenced workflow, so the saved parent row is
    /// reproducible. Validation has already confirmed each referenced key exists.
    /// </summary>
    private static async Task<IReadOnlyList<WorkflowNodeDto>> ResolveSubflowLatestVersionsAsync(
        IReadOnlyList<WorkflowNodeDto> nodes,
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var needsResolution = nodes
            .Where(n => (n.Kind == WorkflowNodeKind.Subflow || n.Kind == WorkflowNodeKind.ReviewLoop)
                && !string.IsNullOrWhiteSpace(n.SubflowKey)
                && n.SubflowVersion is null)
            .Select(n => n.SubflowKey!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (needsResolution.Length == 0)
        {
            return nodes;
        }

        var latestByKey = await dbContext.Workflows
            .AsNoTracking()
            .Where(w => needsResolution.Contains(w.Key))
            .Where(w => !w.IsRetired)
            .GroupBy(w => w.Key)
            .Select(g => new { Key = g.Key, Latest = g.Max(w => w.Version) })
            .ToDictionaryAsync(x => x.Key, x => x.Latest, StringComparer.Ordinal, cancellationToken);

        return nodes
            .Select(n =>
            {
                if ((n.Kind != WorkflowNodeKind.Subflow && n.Kind != WorkflowNodeKind.ReviewLoop)
                    || string.IsNullOrWhiteSpace(n.SubflowKey)
                    || n.SubflowVersion is not null)
                {
                    return n;
                }

                return latestByKey.TryGetValue(n.SubflowKey!.Trim(), out var latest)
                    ? n with { SubflowVersion = latest }
                    : n;
            })
            .ToArray();
    }

    private static WorkflowDraft ToDraft(
        string key,
        string name,
        int? maxRoundsPerRound,
        WorkflowCategory category,
        IReadOnlyList<string>? tags,
        IReadOnlyList<WorkflowNodeDto> nodes,
        IReadOnlyList<WorkflowEdgeDto> edges,
        IReadOnlyList<WorkflowInputDto>? inputs,
        IReadOnlyList<string>? workflowVarsReads = null,
        IReadOnlyList<string>? workflowVarsWrites = null)
    {
        return new WorkflowDraft(
            Key: key,
            Name: name,
            MaxRoundsPerRound: maxRoundsPerRound ?? 3,
            Category: category,
            Tags: tags ?? Array.Empty<string>(),
            WorkflowVarsReads: workflowVarsReads,
            WorkflowVarsWrites: workflowVarsWrites,
            Nodes: nodes
                .Select(node => new WorkflowNodeDraft(
                    Id: node.Id,
                    Kind: node.Kind,
                    AgentKey: node.AgentKey,
                    AgentVersion: node.AgentVersion,
                    OutputScript: node.OutputScript,
                    OutputPorts: node.OutputPorts ?? Array.Empty<string>(),
                    LayoutX: node.LayoutX,
                    LayoutY: node.LayoutY,
                    SubflowKey: node.SubflowKey,
                    SubflowVersion: node.SubflowVersion,
                    ReviewMaxRounds: node.ReviewMaxRounds,
                    LoopDecision: node.LoopDecision,
                    InputScript: node.InputScript,
                    OptOutLastRoundReminder: node.OptOutLastRoundReminder,
                    RejectionHistory: node.RejectionHistory,
                    MirrorOutputToWorkflowVar: node.MirrorOutputToWorkflowVar,
                    OutputPortReplacements: node.OutputPortReplacements,
                    Template: node.Template,
                    OutputType: node.OutputType,
                    SwarmProtocol: node.SwarmProtocol,
                    SwarmN: node.SwarmN,
                    ContributorAgentKey: node.ContributorAgentKey,
                    ContributorAgentVersion: node.ContributorAgentVersion,
                    SynthesizerAgentKey: node.SynthesizerAgentKey,
                    SynthesizerAgentVersion: node.SynthesizerAgentVersion,
                    CoordinatorAgentKey: node.CoordinatorAgentKey,
                    CoordinatorAgentVersion: node.CoordinatorAgentVersion,
                    SwarmTokenBudget: node.SwarmTokenBudget))
                .ToArray(),
            Edges: edges
                .Select((edge, index) => new WorkflowEdgeDraft(
                    FromNodeId: edge.FromNodeId,
                    FromPort: edge.FromPort,
                    ToNodeId: edge.ToNodeId,
                    ToPort: string.IsNullOrWhiteSpace(edge.ToPort) ? WorkflowEdge.DefaultInputPort : edge.ToPort,
                    RotatesRound: edge.RotatesRound,
                    SortOrder: edge.SortOrder == 0 ? index : edge.SortOrder,
                    IntentionalBackedge: edge.IntentionalBackedge))
                .ToArray(),
            Inputs: inputs is null
                ? Array.Empty<WorkflowInputDraft>()
                : inputs
                    .Select((input, index) => new WorkflowInputDraft(
                        Key: input.Key,
                        DisplayName: input.DisplayName,
                        Kind: input.Kind,
                        Required: input.Required,
                        DefaultValueJson: input.DefaultValueJson,
                        Description: input.Description,
                        Ordinal: input.Ordinal == 0 ? index : input.Ordinal))
                    .ToArray());
    }

    private static WorkflowSummaryDto MapSummary(Workflow workflow) => new(
        Key: workflow.Key,
        LatestVersion: workflow.Version,
        Name: workflow.Name,
        Category: workflow.Category,
        Tags: workflow.TagsOrEmpty,
        NodeCount: workflow.Nodes.Count,
        EdgeCount: workflow.Edges.Count,
        InputCount: workflow.Inputs.Count,
        CreatedAtUtc: workflow.CreatedAtUtc,
        IsRetired: workflow.IsRetired);

    private static WorkflowDetailDto MapDetail(Workflow workflow) => new(
        Key: workflow.Key,
        Version: workflow.Version,
        Name: workflow.Name,
        MaxRoundsPerRound: workflow.MaxRoundsPerRound,
        Category: workflow.Category,
        Tags: workflow.TagsOrEmpty,
        CreatedAtUtc: workflow.CreatedAtUtc,
        IsRetired: workflow.IsRetired,
        Nodes: workflow.Nodes
            .Select(node => new WorkflowNodeDto(
                Id: node.Id,
                Kind: node.Kind,
                AgentKey: node.AgentKey,
                AgentVersion: node.AgentVersion,
                OutputScript: node.OutputScript,
                OutputPorts: node.OutputPorts,
                LayoutX: node.LayoutX,
                LayoutY: node.LayoutY,
                SubflowKey: node.SubflowKey,
                SubflowVersion: node.SubflowVersion,
                ReviewMaxRounds: node.ReviewMaxRounds,
                LoopDecision: node.LoopDecision,
                InputScript: node.InputScript,
                OptOutLastRoundReminder: node.OptOutLastRoundReminder,
                RejectionHistory: node.RejectionHistory,
                MirrorOutputToWorkflowVar: node.MirrorOutputToWorkflowVar,
                OutputPortReplacements: node.OutputPortReplacements,
                Template: node.Template,
                OutputType: node.OutputType,
                SwarmProtocol: node.SwarmProtocol,
                SwarmN: node.SwarmN,
                ContributorAgentKey: node.ContributorAgentKey,
                ContributorAgentVersion: node.ContributorAgentVersion,
                SynthesizerAgentKey: node.SynthesizerAgentKey,
                SynthesizerAgentVersion: node.SynthesizerAgentVersion,
                CoordinatorAgentKey: node.CoordinatorAgentKey,
                CoordinatorAgentVersion: node.CoordinatorAgentVersion,
                SwarmTokenBudget: node.SwarmTokenBudget))
            .ToArray(),
        Edges: workflow.Edges
            .Select(edge => new WorkflowEdgeDto(
                FromNodeId: edge.FromNodeId,
                FromPort: edge.FromPort,
                ToNodeId: edge.ToNodeId,
                ToPort: edge.ToPort,
                RotatesRound: edge.RotatesRound,
                SortOrder: edge.SortOrder,
                IntentionalBackedge: edge.IntentionalBackedge))
            .ToArray(),
        Inputs: workflow.Inputs
            .Select(input => new WorkflowInputDto(
                Key: input.Key,
                DisplayName: input.DisplayName,
                Kind: input.Kind,
                Required: input.Required,
                DefaultValueJson: input.DefaultValueJson,
                Description: input.Description,
                Ordinal: input.Ordinal))
            .ToArray(),
        WorkflowVarsReads: workflow.WorkflowVarsReads,
        WorkflowVarsWrites: workflow.WorkflowVarsWrites);

    private static async Task<IResult> RetireWorkflowAsync(
        string key,
        IWorkflowRepository repository,
        CancellationToken cancellationToken)
    {
        var keyValidation = AgentConfigValidator.ValidateKey(key);
        if (!keyValidation.IsValid)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["key"] = new[] { keyValidation.Error! }
            });
        }

        var normalizedKey = key.Trim();
        var found = await repository.RetireAsync(normalizedKey, cancellationToken);
        if (!found)
        {
            return ApiResults.NotFound($"Workflow '{normalizedKey}' does not exist.");
        }

        return Results.Ok(new { key = normalizedKey, isRetired = true });
    }

    private static async Task<IResult> RetireWorkflowsAsync(
        BulkRetireKeysRequest request,
        IWorkflowRepository repository,
        CancellationToken cancellationToken)
    {
        var validation = ValidateBulkKeys(request);
        if (validation.ErrorResult is not null)
        {
            return validation.ErrorResult;
        }

        var retired = await repository.RetireManyAsync(validation.Keys, cancellationToken);
        var retiredSet = retired.ToHashSet(StringComparer.Ordinal);
        var missing = validation.Keys
            .Where(key => !retiredSet.Contains(key))
            .ToArray();

        return Results.Ok(new BulkRetireKeysResponse(retired, missing));
    }

    private static (IReadOnlyList<string> Keys, IResult? ErrorResult) ValidateBulkKeys(BulkRetireKeysRequest request)
    {
        if (request?.Keys is null)
        {
            return (Array.Empty<string>(), Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["keys"] = new[] { "Key list is required." }
            }));
        }

        var keys = request.Keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (keys.Length == 0)
        {
            return (Array.Empty<string>(), Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["keys"] = new[] { "At least one key is required." }
            }));
        }

        var errors = keys
            .Select(key => (Key: key, Validation: AgentConfigValidator.ValidateKey(key)))
            .Where(item => !item.Validation.IsValid)
            .ToArray();
        if (errors.Length > 0)
        {
            return (Array.Empty<string>(), Results.ValidationProblem(errors.ToDictionary(
                item => $"keys.{item.Key}",
                item => new[] { item.Validation.Error! },
                StringComparer.Ordinal)));
        }

        return (keys, null);
    }
}
