using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation;
using CodeFlow.Api.Validation.Pipeline;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
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
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        group.MapPost("/package/apply", ApplyPackageImportAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        group.MapGet("/{key}", GetLatestAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        group.MapPut("/{key}", CreateVersionAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        group.MapPost("/validate-script", ValidateScript)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        group.MapPost("/validate", ValidateWorkflowAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        return routes;
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
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["package"] = new[] { exception.Message }
            });
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
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["package"] = new[] { exception.Message }
            });
        }
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

            var eval = scriptHost.Evaluate(
                workflowKey: "__validate__",
                workflowVersion: 0,
                nodeId: Guid.NewGuid(),
                script: request.Script!,
                declaredPorts: Array.Empty<string>(),
                input: emptyJson,
                context: emptyContext,
                allowOutputOverride: allowOutput,
                allowInputOverride: allowInput,
                inputVariableName: variableName,
                requireSetNodePath: false);

            if (eval.Failure is not null && eval.Failure != LogicNodeFailureKind.UnknownPort)
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
        try
        {
            var workflow = await repository.GetAsync(key, version, cancellationToken);
            return Results.Ok(MapDetail(workflow));
        }
        catch (WorkflowNotFoundException)
        {
            return Results.NotFound();
        }
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
        try
        {
            var ports = await repository.GetTerminalPortsAsync(key, version, cancellationToken);
            return Results.Ok(ports);
        }
        catch (WorkflowNotFoundException)
        {
            return Results.NotFound();
        }
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
        var existing = await dbContext.Workflows
            .AsNoTracking()
            .AnyAsync(workflow => workflow.Key == normalizedKey, cancellationToken);

        if (existing)
        {
            return Results.Conflict(new { error = $"Workflow '{normalizedKey}' already exists. Use PUT to add a version." });
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
        var existing = await dbContext.Workflows
            .AsNoTracking()
            .AnyAsync(workflow => workflow.Key == normalizedKey, cancellationToken);

        if (!existing)
        {
            return Results.NotFound(new { error = $"Workflow '{normalizedKey}' does not exist. Use POST to create." });
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
            return Results.BadRequest(new { error = "Request body is required." });
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
                    OutputType: node.OutputType))
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
        CreatedAtUtc: workflow.CreatedAtUtc);

    private static WorkflowDetailDto MapDetail(Workflow workflow) => new(
        Key: workflow.Key,
        Version: workflow.Version,
        Name: workflow.Name,
        MaxRoundsPerRound: workflow.MaxRoundsPerRound,
        Category: workflow.Category,
        Tags: workflow.TagsOrEmpty,
        CreatedAtUtc: workflow.CreatedAtUtc,
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
                OutputType: node.OutputType))
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
}
